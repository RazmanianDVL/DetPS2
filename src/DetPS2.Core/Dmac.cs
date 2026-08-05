using System;

namespace DetPS2.Core;

/// <summary>
/// DMA Controller (Phase 8).
/// 10 channels, normal + chain, stall bit, IRQ on complete → INTC.
/// GIF Path3 uses start MADR; SIF0/SIF1 hook optional Sif unit.
/// </summary>
public sealed class Dmac : ISchedulable
{
    private readonly SystemMemory _memory;
    private Gif? _gif;
    private Sif? _sif;
    private Intc? _intc;
    private Vif? _vif;
    private BusContention? _bus;
    private Ipu? _ipu;

    public uint DStat { get; private set; } // channel IRQ status
    public uint DMask { get; private set; }
    public uint DCtrl { get; private set; }
    public uint DPcr { get; private set; }
    /// <summary>D_ENABLER / D_ENABLEW — real HW gates DMA; default enabled for commercial boot.</summary>
    public uint DEnabler { get; private set; } = 0x1201; // common "all good" pattern after BIOS
    /// <summary>MFIFO ring base / size (Phase 27 stub — games that poll these get stable values).</summary>
    public uint MfifoBase { get; private set; }
    public uint MfifoEnd { get; private set; }
    public uint MfifoWptr { get; private set; }
    public uint MfifoRptr { get; private set; }

    public ulong TransfersCompleted { get; private set; }
    public ulong ChainTagsProcessed { get; private set; }
    public int ActiveChannelCount { get; private set; }
    public uint DrainCyclesPerQw { get; set; } = 1; // Det cost model

    /// <summary>Cap on the CHCR-write force-pump loop (see its call site) — bounds how much
    /// DMA progress a single CHCR register write can manufacture synchronously.
    /// History: pre-A3 bare 512 (≤131072 cycles/write); A3 default 16 (GIF_STAT-class cut);
    /// M1 residual Opt A default <b>1</b> (mirror GIF_STAT single-round: one Step(256) on STR).
    /// Each Step is cycle-costed via A1 DrainCyclesPerQw. Does NOT touch path3Hold/daDisplayVif
    /// gates — only how many force-steps run once that gate has already decided to fire.</summary>
    private const int MaxChcrForceSteps = 1;

    /// <summary>M1 residual bisect: restore A3 product bound of 16 force-steps
    /// (<c>DETPS2_CHCR_FORCE_LEGACY=1</c>). Ignored when pre-A3 kill-switch is set.</summary>
    private static readonly bool ChcrForceLegacy16 =
        Environment.GetEnvironmentVariable("DETPS2_CHCR_FORCE_LEGACY") == "1";

    /// <summary>A3/pre-A3 root-cause only: restores the pre-A3 512-iteration CHCR force-pump
    /// bound (<c>DETPS2_DISABLE_A3_CHCR_CAP=1</c>). Force loop still only fires under the same
    /// path3Hold/daDisplayVif gate. Never set in normal use. Wins over legacy-16.</summary>
    private static readonly bool DisableA3ChcrCap =
        Environment.GetEnvironmentVariable("DETPS2_DISABLE_A3_CHCR_CAP") == "1";

    /// <summary>M5-a S1 / Phase 0: <c>DETPS2_TRACE_DMAC=1</c> enables stderr dumps.
    /// Counters always accumulate (cheap); print only when this flag is set. Zero DMA behavior change.</summary>
    public static readonly bool TraceDmac =
        Environment.GetEnvironmentVariable("DETPS2_TRACE_DMAC") == "1";

    /// <summary>M5-a S6: <c>DETPS2_DMAC_LEVEL_CATCHUP=1</c> enables opt-in level-sensitive
    /// re-Raise while owed/CIS remain after a handler take (no invent credits).
    /// Hard kill: <c>DETPS2_DISABLE_M5A_DMAC=1</c> forces off. Default off = pre-S6 behavior.</summary>
    public static readonly bool LevelCatchup =
        Environment.GetEnvironmentVariable("DETPS2_DMAC_LEVEL_CATCHUP") == "1"
        && Environment.GetEnvironmentVariable("DETPS2_DISABLE_M5A_DMAC") != "1";

    // --- M5-a Phase 0 per-channel completion/credit telemetry (always accumulate) ---
    // finish: FinishChannel entries
    // owedInc: FinishChannel owed++ when CIM live
    // owedPeak: max depth of _owedHandlerCalls[ch]
    // preEnableInc: FinishChannel while CIM off
    // preEnablePromote: units promoted on EnableChannelIrq (after cap)
    // creditAssist: units added via CreditOwedHandlerCall (assist / public API)
    // w1cWhileOwed: software D_STAT W1C of CIS while owed>0 (race before take)
    // tryTakeCis / tryTakeOwed: AddDmacHandler takes (CIS path vs owed-only fallback)
    // raiseIrq: RaiseDmacIrq attributed to this channel
    // catchupRaise: S6 level re-Raise (no invent) after take/ack path
    private readonly ulong[] _telemFinish = new ulong[10];
    private readonly ulong[] _telemOwedInc = new ulong[10];
    private readonly int[] _telemOwedPeak = new int[10];
    private readonly ulong[] _telemPreEnableInc = new ulong[10];
    private readonly ulong[] _telemPreEnablePromote = new ulong[10];
    private readonly ulong[] _telemCreditAssist = new ulong[10];
    private readonly ulong[] _telemW1cWhileOwed = new ulong[10];
    private readonly ulong[] _telemTryTakeCis = new ulong[10];
    private readonly ulong[] _telemTryTakeOwed = new ulong[10];
    private readonly ulong[] _telemRaiseIrq = new ulong[10];
    private ulong _telemRaiseIrqTotal;
    private ulong _telemFinishTotal;
    private ulong _telemLastDumpFinishTotal;
    private ulong _telemCatchupRaise;

    // Optional TRACE ring: last N (ch, reason, seq). reason: 0=finish 1=credit 2=enable 3=take
    private const int TelemRingCap = 32;
    private readonly byte[] _telemRingCh = new byte[TelemRingCap];
    private readonly byte[] _telemRingReason = new byte[TelemRingCap];
    private readonly ulong[] _telemRingSeq = new ulong[TelemRingCap];
    private int _telemRingWrite;
    private int _telemRingCount;
    private ulong _telemEventSeq;

    public Dmac(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void SetGif(Gif gif) => _gif = gif ?? throw new ArgumentNullException(nameof(gif));
    public void SetSif(Sif sif) => _sif = sif;
    public void SetIntc(Intc intc) => _intc = intc;
    public void SetVif(Vif vif) => _vif = vif;
    public void SetBus(BusContention bus) => _bus = bus;
    public void SetIpu(Ipu ipu) => _ipu = ipu;

    /// <summary>
    /// Raise INTC DmaController, ensuring MASK bit 14 is set. Burnout 3 EnableDmac arms
    /// the bit then a later SetMask/DisableIntc path can drop it while D_STAT channel
    /// masks stay live — AddDmacHandler then never runs and flip pending-count wedges.
    /// </summary>
    private void RaiseDmacIrq(int channel = -1)
    {
        _telemRaiseIrqTotal++;
        if ((uint)channel < 10)
            _telemRaiseIrq[channel]++;
        if (_intc == null) return;
        uint bit = 1u << (int)Intc.InterruptSource.DmaController;
        if ((_intc.Mask & bit) == 0)
            _intc.SetMask(_intc.Mask | bit);
        _intc.Raise(Intc.InterruptSource.DmaController);
    }

    public void Reset()
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i] = new ChannelState();
        DStat = DMask = DCtrl = DPcr = 0;
        DEnabler = 0x1201;
        MfifoBase = MfifoEnd = MfifoWptr = MfifoRptr = 0;
        TransfersCompleted = 0;
        ChainTagsProcessed = 0;
        Array.Clear(_owedHandlerCalls);
        Array.Clear(_preEnableCompletions);
        ClearTelemetry();
    }

    private void ClearTelemetry()
    {
        Array.Clear(_telemFinish);
        Array.Clear(_telemOwedInc);
        Array.Clear(_telemOwedPeak);
        Array.Clear(_telemPreEnableInc);
        Array.Clear(_telemPreEnablePromote);
        Array.Clear(_telemCreditAssist);
        Array.Clear(_telemW1cWhileOwed);
        Array.Clear(_telemTryTakeCis);
        Array.Clear(_telemTryTakeOwed);
        Array.Clear(_telemRaiseIrq);
        _telemRaiseIrqTotal = 0;
        _telemFinishTotal = 0;
        _telemCatchupRaise = 0;
        _telemLastDumpFinishTotal = 0;
        _telemRingWrite = 0;
        _telemRingCount = 0;
        _telemEventSeq = 0;
    }

    /// <summary>Per-channel DMA state for SaveState.cs — previously not saved at all, so a
    /// load mid-transfer (extremely common; a real game is mid-DMA constantly) resumed with
    /// every channel back at Active=false/QWC=0, silently dropping whatever transfer was in
    /// flight instead of continuing it.</summary>
    public void WriteState(System.IO.BinaryWriter w)
    {
        w.Write(DStat); w.Write(DMask); w.Write(DCtrl); w.Write(DPcr); w.Write(DEnabler);
        w.Write(MfifoBase); w.Write(MfifoEnd); w.Write(MfifoWptr); w.Write(MfifoRptr);
        w.Write(TransfersCompleted); w.Write(ChainTagsProcessed);
        w.Write(ActiveChannelCount);
        w.Write(DrainCyclesPerQw);
        w.Write(_channels.Length);
        foreach (var ch in _channels)
        {
            w.Write(ch.MADR); w.Write(ch.QWC); w.Write(ch.CHCR); w.Write(ch.TADR); w.Write(ch.SADR);
            w.Write(ch.Active); w.Write(ch.Mode); w.Write(ch.OriginalQWC); w.Write(ch.StartMADR); w.Write(ch.StartSADR);
            w.Write(ch.Stalled);
        }
    }

    public void ReadState(System.IO.BinaryReader r)
    {
        DStat = r.ReadUInt32(); DMask = r.ReadUInt32(); DCtrl = r.ReadUInt32(); DPcr = r.ReadUInt32(); DEnabler = r.ReadUInt32();
        MfifoBase = r.ReadUInt32(); MfifoEnd = r.ReadUInt32(); MfifoWptr = r.ReadUInt32(); MfifoRptr = r.ReadUInt32();
        TransfersCompleted = r.ReadUInt64(); ChainTagsProcessed = r.ReadUInt64();
        ActiveChannelCount = r.ReadInt32();
        DrainCyclesPerQw = r.ReadUInt32();
        int n = r.ReadInt32();
        for (int i = 0; i < n && i < _channels.Length; i++)
        {
            var ch = _channels[i];
            ch.MADR = r.ReadUInt32(); ch.QWC = r.ReadUInt32(); ch.CHCR = r.ReadUInt32(); ch.TADR = r.ReadUInt32();
            ch.SADR = r.ReadUInt32();
            ch.Active = r.ReadBoolean(); ch.Mode = r.ReadInt32(); ch.OriginalQWC = r.ReadUInt32(); ch.StartMADR = r.ReadUInt32();
            ch.StartSADR = r.ReadUInt32();
            ch.Stalled = r.ReadBoolean();
        }
    }

    public enum Channel
    {
        VIF0 = 0, VIF1 = 1, GIF = 2,
        IPU_FROM = 3, IPU_TO = 4,
        SIF0 = 5, SIF1 = 6, SIF2 = 7,
        SPR_FROM = 8, SPR_TO = 9
    }

    private sealed class ChannelState
    {
        public uint MADR;
        public uint QWC;
        public uint CHCR;
        public uint TADR;
        /// <summary>Scratchpad address for SPR_FROM/SPR_TO (Dn_SADR @ +0x80). 14-bit, QW aligned.</summary>
        public uint SADR;
        public bool Active;
        public int Mode;
        public uint OriginalQWC;
        public uint StartMADR;
        public uint StartSADR;
        public bool Stalled;
    }

    private readonly ChannelState[] _channels = new ChannelState[10];

    public bool IsActive(Channel ch) => _channels[(int)ch].Active;
    public bool IsStalled(Channel ch) => _channels[(int)ch].Stalled;

    public void StartTransfer(Channel channel)
    {
        var ch = _channels[(int)channel];
        if ((ch.CHCR & 0x100) == 0) return;
        ch.Active = true;
        // CHCR bit7 is TIE (tag IRQ enable), NOT a stall request. Real stall control is
        // D_CTRL source/drain + D_STADR — do not freeze transfers that set TIE (common on
        // VIF1 chain/path-sync packets; previous misparse left STR set but Active=false forever).
        ch.Stalled = false;
        ch.Mode = (int)((ch.CHCR >> 2) & 0x3);
        ch.OriginalQWC = ch.QWC;
        ch.StartMADR = ch.MADR;
        ch.StartSADR = ch.SADR;
        if (TransferLog.Enabled)
            TransferLog.Log("DMA:" + channel, ch.MADR, ch.TADR, ch.QWC * 16,
                $"chcr=0x{ch.CHCR:X8} mode={ch.Mode} sadr=0x{ch.SADR:X} stalled={ch.Stalled}");
    }

    public void ReleaseStall(Channel channel)
    {
        var ch = _channels[(int)channel];
        ch.Stalled = false;
        if ((ch.CHCR & 0x100) != 0 && ch.QWC > 0)
            ch.Active = true;
    }

    public void Start(Channel channel, uint madr, uint qwc, int mode = 0)
    {
        var ch = _channels[(int)channel];
        ch.MADR = madr;
        ch.QWC = qwc;
        ch.CHCR = 0x100u | ((uint)mode << 2);
        StartTransfer(channel);
    }

    public int Step(ulong maxCycles)
    {
        if (maxCycles == 0) return 0;
        int workDone = 0;
        int active = 0;

        for (int i = 0; i < _channels.Length; i++)
        {
            var ch = _channels[i];
            if (!ch.Active || ch.Stalled) continue;
            active++;
            var channel = (Channel)i;

            // Chain mode with empty QWC: fetch next DMA tag
            if (ch.Mode == 1 && ch.QWC == 0)
            {
                if (ch.TADR == 0)
                {
                    FinishChannel(channel, ch);
                    workDone++;
                    continue;
                }
                DoChainTransfer(channel, ch);
                if (ch.QWC == 0 && ch.TADR == 0)
                {
                    FinishChannel(channel, ch);
                    workDone++;
                    continue;
                }
            }

            if (ch.QWC == 0)
            {
                if (ch.Mode != 1)
                    FinishChannel(channel, ch);
                continue;
            }

            DoNormalTransfer(channel, ch, maxCycles);

            if (ch.QWC == 0)
            {
                // Deliver this segment's payload to the peripheral now
                DeliverSegment(channel, ch);

                if (ch.Mode == 1)
                {
                    if (ch.TADR == 0)
                        FinishChannel(channel, ch);
                    // else next Step iteration fetches the following tag
                }
                else
                {
                    FinishChannel(channel, ch);
                }
            }

            workDone++;
        }

        ActiveChannelCount = active;
        _bus?.NotifyDmaActivity(active);
        return workDone > 0 ? 1 : 0;
    }

    /// <summary>Push one completed DMA segment (StartMADR, OriginalQWC) into the sink device.</summary>
    private void DeliverSegment(Channel channel, ChannelState ch)
    {
        if (ch.OriginalQWC == 0) return;
        switch (channel)
        {
            case Channel.GIF when _gif != null:
                _gif.ReceivePath3Data(ch.StartMADR, ch.OriginalQWC);
                break;
            case Channel.SIF0 when _sif != null:
                _sif.Sif0IopToEe(ch.TADR != 0 ? ch.TADR : 0, ch.StartMADR, ch.OriginalQWC * 16);
                break;
            case Channel.SIF1 when _sif != null:
                _sif.Sif1EeToIop(ch.StartMADR, ch.TADR != 0 ? ch.TADR : 0, ch.OriginalQWC * 16);
                break;
            case Channel.VIF0 when _vif != null:
            case Channel.VIF1 when _vif != null:
                // Batch the whole segment as one VIF stream. DIRECT Path2 then reaches GIF
                // as a contiguous QW run (ProcessStream coalesces _directRemaining into one
                // ReceivePath2Data). QW-by-QW SendQuadwordToVu1 still works via Gif sticky
                // reassembly, but batching matches Path3 full-segment delivery and avoids
                // per-QW Path2 transfer counter inflation.
                _vif.ProcessStream(ch.StartMADR, ch.OriginalQWC * 4);
                break;
            case Channel.IPU_TO when _ipu != null:
                _ipu.DmaIn(_memory, ch.StartMADR, ch.OriginalQWC);
                break;
            case Channel.IPU_FROM when _ipu != null:
                _ipu.DmaOut(_memory, ch.StartMADR, ch.OriginalQWC);
                break;
            case Channel.SPR_FROM:
                // Scratchpad → main memory (Burnout 3 path-sync builds GIF tags in SPR then SPR_FROM)
                CopySprSegment(ch, toMemory: true);
                break;
            case Channel.SPR_TO:
                // Main memory → scratchpad
                CopySprSegment(ch, toMemory: false);
                break;
        }
    }

    /// <summary>
    /// SPR_FROM (toMemory=true): SPR[SADR..] → MADR. SPR_TO: MADR → SPR[SADR..].
    /// SADR is a 14-bit offset into the 16 KB scratchpad; MADR is EE main memory.
    /// </summary>
    private void CopySprSegment(ChannelState ch, bool toMemory)
    {
        uint bytes = ch.OriginalQWC * 16;
        if (bytes == 0) return;
        uint sadr = ch.StartSADR & (SystemMemory.SPR_SIZE - 1u);
        // Keep QW alignment; wrap within SPR
        sadr &= ~0xFu;
        uint madr = ch.StartMADR;
        if (TransferLog.Enabled)
            TransferLog.Log(toMemory ? "DMA:SPR_FROM->MEM" : "DMA:MEM->SPR_TO",
                toMemory ? SystemMemory.SPR_BASE + sadr : madr,
                toMemory ? madr : SystemMemory.SPR_BASE + sadr,
                bytes);

        for (uint i = 0; i < bytes; i += 4)
        {
            uint sprOff = (sadr + i) & (SystemMemory.SPR_SIZE - 1u);
            uint sprAddr = SystemMemory.SPR_BASE + sprOff;
            uint memAddr = madr + i;
            if (toMemory)
                _memory.Write32(memAddr, _memory.Read32(sprAddr));
            else
                _memory.Write32(sprAddr, _memory.Read32(memAddr));
        }
    }

    /// <summary>
    /// Per-channel "owed" AddDmacHandler invocations that survive D_STAT W1C.
    /// FinishChannel increments when IRQ is enabled; TryTakePendingDmacHandler drains.
    /// Prevents path-sync force-step completions from being lost when the game W1C's
    /// D_STAT before EE can dispatch (Burnout 3 flip pending-count stuck at 2).
    /// </summary>
    private readonly int[] _owedHandlerCalls = new int[10];
    /// <summary>Completions that finished while the channel IRQ was still masked.
    /// Promoted to owed handler calls on EnableChannelIrq (level-sensitive catch-up).</summary>
    private readonly int[] _preEnableCompletions = new int[10];

    /// <summary>True when any channel still owes an AddDmacHandler call (queue or D_STAT).</summary>
    public bool HasOwedHandlerCall(int channel) =>
        (uint)channel < 10 && _owedHandlerCalls[channel] > 0;

    /// <summary>True when any IRQ-enabled channel still has sticky CIS or owed handler credits.</summary>
    public bool HasLevelSensitiveDmacWork()
    {
        for (int ch = 0; ch < 10; ch++)
        {
            if (!IsChannelIrqEnabled(ch)) continue;
            if ((DStat & (1u << ch)) != 0 || _owedHandlerCalls[ch] > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// M5-a S6: re-Raise DmaController when level-sensitive work remains, without inventing
    /// owed credits. Call after a viaDmacFallback take + INTC Acknowledge so edge-clear does
    /// not drop remaining owed/CIS work. No-op when <see cref="LevelCatchup"/> is off.
    /// </summary>
    public bool MaybeLevelCatchupRaise()
    {
        if (!LevelCatchup) return false;
        if (!HasLevelSensitiveDmacWork()) return false;
        RaiseDmacIrq(-1);
        _telemCatchupRaise++;
        return true;
    }

    /// <summary>Consume one owed handler call for <paramref name="channel"/>. Returns false if none.</summary>
    public bool TryConsumeOwedHandlerCall(int channel)
    {
        if ((uint)channel >= 10 || _owedHandlerCalls[channel] <= 0) return false;
        _owedHandlerCalls[channel]--;
        return true;
    }

    /// <summary>
    /// Credit owed AddDmacHandler invocations for a channel and raise DmaController.
    /// Used when a title's flip/path-sync consumer (Burnout 3 @ 0x1F1778) must run to
    /// decrement pending and drain out→in — without force-finishing active DMA transfers
    /// and without poking queue pointers (out←in is unsafe).
    /// </summary>
    public void CreditOwedHandlerCall(int channel, int count = 1)
    {
        if ((uint)channel >= 10 || count <= 0) return;
        int add = Math.Min(count, 8);
        _owedHandlerCalls[channel] = Math.Min(64, _owedHandlerCalls[channel] + add);
        _telemCreditAssist[channel] += (ulong)add;
        NoteOwedPeak(channel);
        TelemRingPush(channel, reason: 1);
        // Sticky CIS so TryTakePendingDmacHandler prefers the D_STAT path when possible.
        DStat |= 1u << channel;
        if (IsChannelIrqEnabled(channel))
            RaiseDmacIrq(channel);
    }

    private void FinishChannel(Channel channel, ChannelState ch)
    {
        // If a segment was in progress and never drained to QWC==0 deliver path, flush now
        // (normal path already delivered; this covers QWC-started-at-0 edge cases)
        ch.Active = false;
        ch.CHCR &= ~0x100u;
        TransfersCompleted++;

        int chNum = (int)channel;
        _telemFinish[chNum]++;
        _telemFinishTotal++;
        TelemRingPush(chNum, reason: 0);
        // Channel complete bit in D_STAT (low 10 bits)
        DStat |= 1u << chNum;
        // Mask lives in high half of D_STAT on real HW; we also keep DMask mirror
        if (IsChannelIrqEnabled(chNum))
        {
            // Queue a handler call that survives a racey D_STAT W1C before EE dispatch.
            if (_owedHandlerCalls[chNum] < 64)
            {
                _owedHandlerCalls[chNum]++;
                _telemOwedInc[chNum]++;
                NoteOwedPeak(chNum);
            }
            RaiseDmacIrq(chNum);
        }
        else
        {
            // Remember for EnableChannelIrq catch-up (Burnout registers handlers then
            // EnableDmac after some path-sync DMA has already finished).
            if (_preEnableCompletions[chNum] < 64)
            {
                _preEnableCompletions[chNum]++;
                _telemPreEnableInc[chNum]++;
            }
        }
        MaybeIntervalDump();
    }

    /// <summary>Whether channel <paramref name="channel"/>'s completion IRQ is unmasked
    /// (D_STAT bit 16+ch and/or <see cref="DMask"/>).</summary>
    public bool IsChannelIrqEnabled(int channel)
    {
        if ((uint)channel >= 10) return false;
        uint bit = 1u << channel;
        return (DStat & (bit << 16)) != 0 || (DMask & bit) != 0;
    }

    /// <summary>
    /// EnableDmac(channel) — arm per-channel completion IRQ (D_STAT mask bit 16+ch + DMask).
    /// Was a pure no-op; Burnout 3 registers AddDmacHandler(VIF1/GIF) then EnableDmac, and the
    /// GIF path-sync consumer at 0x001F1778 only drains the flip-queue on the IRQ path (a0=ch).
    /// Without the mask, FinishChannel never raised DmaController and the queue never drained.
    /// </summary>
    /// <remarks>
    /// If a completion status bit is already sticky when the mask is first armed, raise
    /// DmaController immediately — real DMAC is level-sensitive on (CIS &amp; CIM). Without this,
    /// path-sync DMA that finished before EnableDmac permanently loses its AddDmacHandler call
    /// and Burnout's pending-count byte never decrements (soft-poll a0=-1 early-outs while
    /// pending≠0).
    /// </remarks>
    public void EnableChannelIrq(int channel)
    {
        if ((uint)channel >= 10) return;
        uint bit = 1u << channel;
        DMask |= bit;
        DStat |= bit << 16;
        // Promote completions that finished while masked into owed handler calls.
        if (_preEnableCompletions[channel] > 0)
        {
            int n = _preEnableCompletions[channel];
            _preEnableCompletions[channel] = 0;
            // Cap: game pending-count is a byte; don't flood dozens of stale IRQs.
            if (n > 4) n = 4;
            int before = _owedHandlerCalls[channel];
            _owedHandlerCalls[channel] = Math.Min(64, _owedHandlerCalls[channel] + n);
            int promoted = _owedHandlerCalls[channel] - before;
            if (promoted > 0)
            {
                _telemPreEnablePromote[channel] += (ulong)promoted;
                NoteOwedPeak(channel);
            }
            TelemRingPush(channel, reason: 2);
            // Ensure CIS sticky so D_STAT path also sees work.
            DStat |= bit;
        }
        // Level-sensitive: already-complete + newly unmasked → IRQ now.
        if ((DStat & bit) != 0 || _owedHandlerCalls[channel] > 0)
            RaiseDmacIrq(channel);
    }

    /// <summary>DisableDmac(channel) — mask off per-channel completion IRQ.</summary>
    public void DisableChannelIrq(int channel)
    {
        if ((uint)channel >= 10) return;
        uint bit = 1u << channel;
        DMask &= ~bit;
        DStat &= ~(bit << 16);
    }

    /// <summary>Write-1-clear a channel's D_STAT completion bit (low 10). Used by the HLE
    /// DmaController dispatcher after handing the channel to its AddDmacHandler callback.</summary>
    public void ClearChannelStatus(int channel)
    {
        if ((uint)channel >= 10) return;
        DStat &= ~(1u << channel);
    }

    /// <summary>True when any channel has a sticky completion bit and is IRQ-enabled.</summary>
    public bool HasPendingChannelIrq()
    {
        for (int ch = 0; ch < 10; ch++)
        {
            if ((DStat & (1u << ch)) != 0 && IsChannelIrqEnabled(ch))
                return true;
        }
        return false;
    }

    private void DoNormalTransfer(Channel channel, ChannelState ch, ulong cycleBudget)
    {
        // Drain limited QWs per step (priority via DPcr: higher nibble = more budget)
        uint priority = (DPcr >> ((int)channel * 2)) & 0x3;
        uint budget = 4u + priority * 4u;
        // For video path progress: allow larger bursts so GIF packets complete quickly
        if (channel is Channel.GIF or Channel.VIF1 or Channel.VIF0)
            budget = Math.Max(budget, 64u);
        // A1 (dual-orchestrator timing-realism milestone): DrainCyclesPerQw used to be dead
        // scaffolding -- serialized in save-states, documented "Det cost model", never actually
        // read. Channel completion was gated only by the fixed priority-derived per-Step() QW
        // cap above, independent of Step's own maxCycles argument -- so however many times a
        // caller chose to invoke Step() in a row, each call's progress was "free" with no real
        // elapsed-cycle cost. Cap the per-call QW budget by the cycles this Step() call was
        // actually granted (maxCycles / DrainCyclesPerQw) so a channel can only finish as fast
        // as real elapsed scheduler time allows -- a big transfer now genuinely spans multiple
        // scheduler rounds instead of being bounded only by the priority cap. DrainCyclesPerQw
        // stays save-state compatible (same field, same wire format); default of 1 keeps normal
        // per-round throughput close to the old fixed caps under the scheduler's regular slice
        // cadence, and only matters once a caller can no longer manufacture artificial extra
        // Step() calls to bypass it (see MmioBus.cs GIF_STAT poll-pump fix, same milestone).
        uint cyclesPerQw = Math.Max(1u, DrainCyclesPerQw);
        ulong maxQwFromBudget = Math.Max(1UL, cycleBudget / cyclesPerQw);
        if (maxQwFromBudget < budget)
            budget = (uint)Math.Min(maxQwFromBudget, uint.MaxValue);
        uint qwToTransfer = Math.Min(ch.QWC, budget);
        ch.MADR += qwToTransfer * 16;
        ch.QWC -= qwToTransfer;
        // SPR channels advance SADR in lockstep with MADR
        if (channel is Channel.SPR_FROM or Channel.SPR_TO)
            ch.SADR = (ch.SADR + qwToTransfer * 16) & (SystemMemory.SPR_SIZE - 1u);
        // MFIFO drain: advance read pointer when enabled
        if ((DCtrl & 0x4) != 0 && MfifoEnd > MfifoBase)
        {
            MfifoRptr += qwToTransfer * 16;
            if (MfifoRptr >= MfifoEnd) MfifoRptr = MfifoBase;
        }
    }

    private void DoChainTransfer(Channel channel, ChannelState ch)
    {
        if (ch.QWC != 0 || ch.TADR == 0) return;

        uint tagLow = _memory.Read32(ch.TADR);
        uint tagHigh = _memory.Read32(ch.TADR + 4);
        uint tagW2 = _memory.Read32(ch.TADR + 8);
        uint tagW3 = _memory.Read32(ch.TADR + 12);
        ChainTagsProcessed++;

        // TTE (CHCR bit 6): transfer upper 64 bits of DMAtag to the VIF/GIF as a QW.
        // VIF1 path-sync chains (CHCR=0x145) put MSKPATH3/DIRECT/… in this half.
        if ((ch.CHCR & 0x40) != 0 && channel is Channel.VIF0 or Channel.VIF1 or Channel.GIF)
        {
            if (channel is Channel.VIF0 or Channel.VIF1 && _vif != null)
            {
                // Process the two upper words as a mini stream (pad to QW with zeros for ALIGN)
                // Real HW feeds tag[64:127] as one QW; we push words via ProcessStream scratch.
                // Use a transient in-place feed: ProcessVifCode/FeedData on w2/w3.
                _vif.ProcessVifCode(tagW2);
                // If DIRECT was just latched, remaining data follows in subsequent segments
                // not in the tag half — only command words live here typically.
                if (tagW3 != 0)
                    _vif.ProcessVifCode(tagW3);
            }
            // GIF TTE is rare; Path3 data still comes from ADDR/QWC
        }

        ch.QWC = tagLow & 0xFFFF;
        // ADDR field (lower 31 bits; bit31 = SPR select — ignore for now)
        ch.MADR = tagHigh & 0x7FFFFFFF;
        ch.StartMADR = ch.MADR;
        ch.OriginalQWC = ch.QWC;

        uint tagId = (tagLow >> 28) & 0x7;
        bool tagIrq = ((tagLow >> 31) & 1) != 0;

        // Play! / ps2tek: CHCR bits 16–31 latch the high 16 bits of the DMAtag word0
        // (nTAG = tagLow >> 16). DA IRQ @0x1B261C checks CHCR&0xF0000000 ∈ {0x8,0xF}
        // for REFE/END+IRQ (0x8xxx / 0xFxxx). Preserve DIR/MOD/ASP/TTE/TIE; STR cleared
        // on FinishChannel. Do NOT invent TAG — only honest bits from the live tag.
        ch.CHCR = (ch.CHCR & 0x0000FFFFu) | ((tagLow >> 16) << 16);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && channel == Channel.VIF1 && tagIrq && (tagId == 0 || tagId == 7)
            && (ch.TADR >= 0x01F00000u && ch.TADR < 0x02000000u))
        {
            // Live DA display END 0xF000000B @0x01FB2A80: TTE w3=DIRECT IMM=QWC, ADDR=0,
            // payload inline after tag (case END). WAVE-4: nTAG latched so handler can succeed.
            uint addr = tagHigh & 0x7FFFFFFFu;
            uint qwc = tagLow & 0xFFFF;
            uint dataAddr = addr != 0 ? addr : (ch.TADR + 16);
            Console.Error.WriteLine(
                $"[DMAC] VIF1 END/REFE+IRQ tag=0x{tagLow:X8} chcr=0x{ch.CHCR:X8} tadr=0x{ch.TADR:X8} " +
                $"data=0x{dataAddr:X8} qwc={qwc} w2=0x{tagW2:X8} w3=0x{tagW3:X8} " +
                $"(CHCR.nTAG latched)");
        }

        // Source-chain tag IDs (ps2tek / PCSX2)
        switch (tagId)
        {
            case 0: // REFE — data at ADDR, next tag ends chain
                ch.TADR = 0;
                break;
            case 1: // CNT — data follows tag in memory; next tag after data
                ch.MADR = ch.TADR + 16;
                ch.StartMADR = ch.MADR;
                ch.TADR = ch.MADR + ch.QWC * 16;
                break;
            case 2: // NEXT — data at ADDR, next tag at ADDR of tag's next field
                // ADDR = data; next TADR is in bits of tag word1 high / second dword often same
                // Simplified: next tag pointer is upper of next QW half — use tagHigh as data, next from TADR+8
                {
                    uint next = _memory.Read32(ch.TADR + 8) & 0x7FFFFFFF;
                    ch.TADR = next;
                }
                break;
            case 3: // REF — data at ADDR, next tag at TADR+16
            case 4: // REFS
                ch.TADR += 16;
                break;
            case 5: // CALL
                ch.TADR += 16;
                break;
            case 6: // RET
                ch.TADR += 16;
                break;
            case 7: // END — data at ADDR (if QWC), stop after
                // DA display chain: END+IRQ 0xF000000B @0x01FB2A80 with ADDR=0, QWC=11,
                // TTE DIRECT IMM=0xB. Real payload is the 11 QWs *following the tag*
                // (CNT-style), not physical address 0. When ADDR==0 and QWC>0, treat as
                // inline data after the DMAtag so DIRECT Path2 can reach Soft-GS.
                //
                // Inline END payload after tag when ADDR=0 (TADR+16 CNT-style):
                // - DA display chains: high TADR [0x01F00000,0x02000000) — Midway PATH2
                // - GIF channel: always (Whiplash title FRAME/XYOFFSET @~0x417960 qwc=12;
                //   tip B3 high-TADR-only gate left these unmapped → px 640→3, FRAME=0)
                // Do NOT ungate VIF*/others: B3 residual ENDs with legitimate ADDR=0
                // outside DA band remapped to TADR+16 garbage (cdvd stuck 609).
                // Bisect: 45d8c3c alone OK; merge + Path3Masked gate → tip px=0/cdvd=609.
                bool daHigh = ch.TADR >= 0x01F00000u && ch.TADR < 0x02000000u;
                if (ch.QWC > 0 && ch.MADR == 0 && (daHigh || channel == Channel.GIF))
                {
                    ch.MADR = ch.TADR + 16;
                    ch.StartMADR = ch.MADR;
                }
                ch.TADR = 0;
                break;
            default:
                ch.TADR += 16;
                break;
        }

        // CIS is raised in FinishChannel/ClearSTR (Play!), not at tag-fetch. Early CIS
        // before data delivery made DA IRQ handlers observe incomplete STR/TAG state.
        // TIE+IRQ early-stop for non-terminal tags is handled after segment when TADR!=0.
        _ = tagIrq;
    }

    public uint ReadRegister(uint address)
    {
        // Global regs (ps2tek)
        if (address == 0x1000E000) return DCtrl;
        if (address == 0x1000E010) return DStat;
        if (address == 0x1000E020) return DPcr;
        if (address == 0x1000E030) return 0; // D_SQWC
        if (address == 0x1000E040) return MfifoBase; // D_RBSR
        if (address == 0x1000E050) return MfifoEnd;  // D_RBOR
        if (address == 0x1000E060) return MfifoWptr; // D_STADR (stall) — reuse
        if (address == 0x1000E070) return MfifoRptr;
        if (address == 0x1000F520) return DEnabler; // D_ENABLER
        if (address == 0x1000F590) return DEnabler; // D_ENABLEW mirror

        int channel = GetChannelFromAddress(address);
        if (channel < 0) return 0;
        var ch = _channels[channel];
        // Dn_CHCR=0x00, MADR=0x10, QWC=0x20, TADR=0x30, ASR0=0x40, ASR1=0x50, SADR=0x80
        uint reg = (address & 0xFF) >> 4;
        return reg switch
        {
            0x0 => ch.CHCR,
            0x1 => ch.MADR,
            0x2 => ch.QWC,
            0x3 => ch.TADR,
            0x8 => ch.SADR, // Dn_SADR (SPR channels)
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        if (address == 0x1000E000) { DCtrl = value; return; }
        if (address == 0x1000E010)
        {
            // D_STAT: low bits w1c status; high bits XOR mask
            uint clear = value & 0x3FF;
            // M5-a S1: software W1C of CIS while owed credits remain (race before take).
            if (clear != 0)
            {
                for (int b = 0; b < 10; b++)
                {
                    uint bbit = 1u << b;
                    if ((clear & bbit) == 0) continue;
                    if ((DStat & bbit) == 0) continue; // was not sticky
                    if (_owedHandlerCalls[b] > 0)
                        _telemW1cWhileOwed[b]++;
                }
            }
            DStat &= ~clear;
            uint maskXor = (value >> 16) & 0x3FF;
            // Mirror mask into DMask and high half of DStat
            for (int b = 0; b < 10; b++)
            {
                if (((maskXor >> b) & 1) != 0)
                {
                    DMask ^= 1u << b;
                    DStat ^= 1u << (16 + b);
                }
            }
            return;
        }
        if (address == 0x1000E020) { DPcr = value; return; }
        if (address == 0x1000E030) { return; } // D_SQWC
        if (address == 0x1000E040) { MfifoBase = value; return; }
        if (address == 0x1000E050) { MfifoEnd = value; return; }
        if (address == 0x1000E060) { MfifoWptr = value; return; }
        if (address == 0x1000E070) { MfifoRptr = value; return; }
        if (address == 0x1000F520 || address == 0x1000F590)
        {
            // Writes enable DMA engine; keep sticky enabled bit
            DEnabler = value | 0x10000u;
            // Also ensure master DMAE in D_CTRL so subsequent CHCR.STR works
            if ((value & 1) != 0 || value != 0)
                DCtrl |= 1;
            return;
        }

        int channel = GetChannelFromAddress(address);
        if (channel < 0) return;
        var ch = _channels[channel];
        uint reg = (address & 0xFF) >> 4;
        switch (reg)
        {
            case 0x0: // CHCR
                // Play!: while STR is set, only STR may change (suspend/clear); nTAG and
                // control bits stay so IRQ handlers still see the last DMAtag high half.
                if ((ch.CHCR & 0x100u) != 0)
                {
                    if ((value & 0x100u) == 0)
                    {
                        ch.CHCR &= ~0x100u;
                        ch.Active = false;
                    }
                    // else: write with STR still 1 while running — ignore (Play!)
                    break;
                }
                ch.CHCR = value;
                if ((value & 0x100) != 0)
                {
                    StartTransfer((Channel)channel);
                    // Path-sync (B3 @ 0x001F1A4C) drains while PATH3 is masked.
                    // DA display type-1 kicks VIF1 TTE chain (CHCR=0x145) with TADR in high
                    // RDRAM (0x01FBxxxx) without M3P — must drain or STR sticks + queue lock.
                    // Unconditional VIF/GIF drain starves GoW early boot (agent/menu-gow-w3).
                    bool path3Hold = _gif != null && _gif.Path3MaskedByVif;
                    bool daDisplayVif =
                        (channel == (int)Channel.VIF1 || channel == (int)Channel.VIF0)
                        && (value & 0x40u) != 0 // TTE
                        && ((ch.TADR >= 0x01F00000u && ch.TADR < 0x02000000u)
                            || (ch.TADR >= 0x001F0000u && ch.TADR < 0x00200000u));
                    if ((path3Hold || daDisplayVif) &&
                        (channel == (int)Channel.VIF1 || channel == (int)Channel.GIF ||
                         channel == (int)Channel.VIF0))
                    {
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                            Console.Error.WriteLine(
                                $"[DMAC] CHCR force-pump fired ch={(Channel)channel} path3Hold={path3Hold} daDisplayVif={daDisplayVif}");
                        // M1 residual Opt A: default one Step(256). Kill-switches: pre-A3 512,
                        // then A3 legacy 16, else product MaxChcrForceSteps (1).
                        int maxSteps = DisableA3ChcrCap ? 512
                            : ChcrForceLegacy16 ? 16
                            : MaxChcrForceSteps;
                        for (int i = 0; i < maxSteps && _channels[channel].Active; i++)
                            Step(256);
                    }
                }
                break;
            case 0x1: ch.MADR = value; break;
            case 0x2: ch.QWC = value; break;
            case 0x3: ch.TADR = value; break;
            case 0x8: ch.SADR = value & 0x3FF0u; break; // Dn_SADR — 14-bit, QW aligned
        }
    }

    /// <summary>
    /// Real EE DMAC channel bases (not uniform 0x400 stride).
    /// VIF0=8000, VIF1=9000, GIF=A000, IPU_FROM=B000, IPU_TO=B400,
    /// SIF0=C000, SIF1=C400, SIF2=C800, SPR_FROM=D000, SPR_TO=D400.
    /// </summary>
    private static int GetChannelFromAddress(uint address)
    {
        if (address >= 0x10008000 && address < 0x10009000) return (int)Channel.VIF0;
        if (address >= 0x10009000 && address < 0x1000A000) return (int)Channel.VIF1;
        if (address >= 0x1000A000 && address < 0x1000B000) return (int)Channel.GIF;
        if (address >= 0x1000B000 && address < 0x1000B400) return (int)Channel.IPU_FROM;
        if (address >= 0x1000B400 && address < 0x1000C000) return (int)Channel.IPU_TO;
        if (address >= 0x1000C000 && address < 0x1000C400) return (int)Channel.SIF0;
        if (address >= 0x1000C400 && address < 0x1000C800) return (int)Channel.SIF1;
        if (address >= 0x1000C800 && address < 0x1000D000) return (int)Channel.SIF2;
        if (address >= 0x1000D000 && address < 0x1000D400) return (int)Channel.SPR_FROM;
        if (address >= 0x1000D400 && address < 0x1000E000) return (int)Channel.SPR_TO;
        return -1;
    }

    // --- M5-a Phase 0 telemetry helpers (no DMA behavior) ---

    private void NoteOwedPeak(int channel)
    {
        int depth = _owedHandlerCalls[channel];
        if (depth > _telemOwedPeak[channel])
            _telemOwedPeak[channel] = depth;
    }

    private void TelemRingPush(int channel, byte reason)
    {
        if (!TraceDmac) return;
        _telemEventSeq++;
        int i = _telemRingWrite;
        _telemRingCh[i] = (byte)channel;
        _telemRingReason[i] = reason;
        _telemRingSeq[i] = _telemEventSeq;
        _telemRingWrite = (i + 1) % TelemRingCap;
        if (_telemRingCount < TelemRingCap) _telemRingCount++;
    }

    /// <summary>Record one AddDmacHandler take (CIS sticky path vs owed-only fallback).
    /// Called from <c>SonyKernelHle.TryTakePendingDmacHandler</c> — counters only.</summary>
    public void NoteHandlerTake(int channel, bool viaCis)
    {
        if ((uint)channel >= 10) return;
        if (viaCis) _telemTryTakeCis[channel]++;
        else _telemTryTakeOwed[channel]++;
        TelemRingPush(channel, reason: 3);
    }

    /// <summary>Per-channel finish count (M5-a S1 telemetry).</summary>
    public ulong GetTelemFinish(int channel) => (uint)channel < 10 ? _telemFinish[channel] : 0;
    /// <summary>Per-channel CreditOwedHandlerCall units (assist residual probe).</summary>
    public ulong GetTelemCreditAssist(int channel) => (uint)channel < 10 ? _telemCreditAssist[channel] : 0;
    /// <summary>Per-channel CIS-path handler takes.</summary>
    public ulong GetTelemTryTakeCis(int channel) => (uint)channel < 10 ? _telemTryTakeCis[channel] : 0;
    /// <summary>Per-channel owed-only handler takes.</summary>
    public ulong GetTelemTryTakeOwed(int channel) => (uint)channel < 10 ? _telemTryTakeOwed[channel] : 0;

    private void MaybeIntervalDump()
    {
        if (!TraceDmac) return;
        // Dump every 4096 finishes so long canaries stay readable without flooding.
        if (_telemFinishTotal - _telemLastDumpFinishTotal < 4096) return;
        _telemLastDumpFinishTotal = _telemFinishTotal;
        DumpTraceSummary(prefix: "[DMAC-TRACE] interval");
    }

    /// <summary>
    /// Print M5-a Phase 0 DMAC completion telemetry to stderr.
    /// Safe to call anytime; intended under <c>DETPS2_TRACE_DMAC=1</c>.
    /// </summary>
    public void DumpTraceSummary(string prefix = "[DMAC-TRACE]")
    {
        var w = Console.Error;
        w.WriteLine(
            $"{prefix} total finish={_telemFinishTotal} raise={_telemRaiseIrqTotal} " +
            $"catchupRaise={_telemCatchupRaise} levelCatchup={(LevelCatchup ? 1 : 0)} " +
            $"transfersCompleted={TransfersCompleted} active={ActiveChannelCount}");
        for (int ch = 0; ch < 10; ch++)
        {
            ulong fin = _telemFinish[ch];
            ulong oinc = _telemOwedInc[ch];
            ulong pre = _telemPreEnableInc[ch];
            ulong prom = _telemPreEnablePromote[ch];
            ulong cred = _telemCreditAssist[ch];
            ulong w1c = _telemW1cWhileOwed[ch];
            ulong tCis = _telemTryTakeCis[ch];
            ulong tOwed = _telemTryTakeOwed[ch];
            ulong raise = _telemRaiseIrq[ch];
            if (fin == 0 && oinc == 0 && pre == 0 && prom == 0 && cred == 0 &&
                w1c == 0 && tCis == 0 && tOwed == 0 && raise == 0)
                continue;
            w.WriteLine(
                $"{prefix} ch={(Channel)ch}({ch}) finish={fin} owedInc={oinc} owedPeak={_telemOwedPeak[ch]} " +
                $"preEnableInc={pre} preEnablePromote={prom} creditAssist={cred} " +
                $"w1cWhileOwed={w1c} tryTakeCis={tCis} tryTakeOwed={tOwed} raise={raise} " +
                $"owedNow={_owedHandlerCalls[ch]} preNow={_preEnableCompletions[ch]}");
        }
        if (TraceDmac && _telemRingCount > 0)
        {
            w.WriteLine($"{prefix} ring (newest last, reason 0=finish 1=credit 2=enable 3=take):");
            int start = (_telemRingWrite - _telemRingCount + TelemRingCap) % TelemRingCap;
            for (int n = 0; n < _telemRingCount; n++)
            {
                int i = (start + n) % TelemRingCap;
                string rn = _telemRingReason[i] switch
                {
                    0 => "finish",
                    1 => "credit",
                    2 => "enable",
                    3 => "take",
                    _ => "?"
                };
                w.WriteLine(
                    $"{prefix}   seq={_telemRingSeq[i]} ch={(Channel)_telemRingCh[i]}({_telemRingCh[i]}) {rn}");
            }
        }
    }
}
