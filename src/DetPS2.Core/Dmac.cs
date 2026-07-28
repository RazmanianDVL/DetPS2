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

    public void Reset()
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i] = new ChannelState();
        DStat = DMask = DCtrl = DPcr = 0;
        DEnabler = 0x1201;
        MfifoBase = MfifoEnd = MfifoWptr = MfifoRptr = 0;
        TransfersCompleted = 0;
        ChainTagsProcessed = 0;
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
            w.Write(ch.MADR); w.Write(ch.QWC); w.Write(ch.CHCR); w.Write(ch.TADR);
            w.Write(ch.Active); w.Write(ch.Mode); w.Write(ch.OriginalQWC); w.Write(ch.StartMADR);
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
            ch.Active = r.ReadBoolean(); ch.Mode = r.ReadInt32(); ch.OriginalQWC = r.ReadUInt32(); ch.StartMADR = r.ReadUInt32();
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
        public bool Active;
        public int Mode;
        public uint OriginalQWC;
        public uint StartMADR;
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
        ch.Stalled = (ch.CHCR & 0x80) != 0 && (DCtrl & 1) != 0; // simplified stall ctrl
        ch.Mode = (int)((ch.CHCR >> 2) & 0x3);
        ch.OriginalQWC = ch.QWC;
        ch.StartMADR = ch.MADR;
        if (TransferLog.Enabled)
            TransferLog.Log("DMA:" + channel, ch.MADR, ch.TADR, ch.QWC * 16,
                $"chcr=0x{ch.CHCR:X8} mode={ch.Mode} stalled={ch.Stalled}");
        if (ch.Stalled)
            ch.Active = false; // wait for stall release
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

            DoNormalTransfer(channel, ch);

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
                for (uint i = 0; i < ch.OriginalQWC; i++)
                    _vif.SendQuadwordToVu1(ch.StartMADR + i * 16);
                break;
            case Channel.IPU_TO when _ipu != null:
                _ipu.DmaIn(_memory, ch.StartMADR, ch.OriginalQWC);
                break;
            case Channel.IPU_FROM when _ipu != null:
                _ipu.DmaOut(_memory, ch.StartMADR, ch.OriginalQWC);
                break;
        }
    }

    private void FinishChannel(Channel channel, ChannelState ch)
    {
        // If a segment was in progress and never drained to QWC==0 deliver path, flush now
        // (normal path already delivered; this covers QWC-started-at-0 edge cases)
        ch.Active = false;
        ch.CHCR &= ~0x100u;
        TransfersCompleted++;

        // Channel complete bit in D_STAT (low 10 bits)
        DStat |= 1u << (int)channel;
        // Mask lives in high half of D_STAT on real HW; we also keep DMask mirror
        uint maskBit = 1u << (16 + (int)channel);
        if ((DStat & maskBit) != 0 || (DMask & (1u << (int)channel)) != 0)
            _intc?.Raise(Intc.InterruptSource.DmaController);
    }

    private void DoNormalTransfer(Channel channel, ChannelState ch)
    {
        // Drain limited QWs per step (priority via DPcr: higher nibble = more budget)
        uint priority = (DPcr >> ((int)channel * 2)) & 0x3;
        uint budget = 4u + priority * 4u;
        // For video path progress: allow larger bursts so GIF packets complete quickly
        if (channel is Channel.GIF or Channel.VIF1 or Channel.VIF0)
            budget = Math.Max(budget, 64u);
        uint qwToTransfer = Math.Min(ch.QWC, budget);
        ch.MADR += qwToTransfer * 16;
        ch.QWC -= qwToTransfer;
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
        ChainTagsProcessed++;

        ch.QWC = tagLow & 0xFFFF;
        // ADDR field (lower 31 bits; bit31 = SPR select — ignore for now)
        ch.MADR = tagHigh & 0x7FFFFFFF;
        ch.StartMADR = ch.MADR;
        ch.OriginalQWC = ch.QWC;

        uint tagId = (tagLow >> 28) & 0x7;
        bool tagIrq = ((tagLow >> 31) & 1) != 0;

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
                ch.TADR = 0;
                break;
            default:
                ch.TADR += 16;
                break;
        }

        if (tagIrq)
        {
            DStat |= 1u << (int)channel;
            if ((DMask & (1u << (int)channel)) != 0)
                _intc?.Raise(Intc.InterruptSource.DmaController);
        }
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
                ch.CHCR = value;
                if ((value & 0x100) != 0)
                    StartTransfer((Channel)channel);
                break;
            case 0x1: ch.MADR = value; break;
            case 0x2: ch.QWC = value; break;
            case 0x3: ch.TADR = value; break;
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
}
