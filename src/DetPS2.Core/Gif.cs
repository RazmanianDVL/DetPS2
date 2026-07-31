using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Interface — Phase 7.
/// Paths: Path1 (VU1), Path2 (VIF1), Path3 (DMAC).
/// Tag formats: PACKED (0), REGLIST (1), IMAGE (2), DISABLE (3).
/// </summary>
public sealed class Gif : ISchedulable
{
    private readonly Gs _gs;

    private uint _lastQwcProcessed;
    private ulong _path3Transfers;
    private ulong _path2Transfers;
    private ulong _path1Transfers;
    private ulong _path1Qws;
    private ulong _path3Qws;
    private ulong _path3HeldSubmits; // Path3 submits while M3P|M3R (not yet drained)

    // REGS field from last tag (up to 16 regs × 4 bits)
    private ulong _regs;
    private uint _nreg;

    // Mid-packet sticky state — VIF1 DMA delivers DIRECT one QW at a time
    // (DeliverSegment → SendQuadwordToVu1 → ReceivePath2Data(qwc=1)). Without this,
    // ProcessTransfer consumed the GIFtag and returned with remaining=0, so PACKED
    // A+D data (FRAME/PRIM/XYZ2) never reached Soft-GS. GoW residual gifP2=1082 with
    // FRAME_1=0 / prims=0 was this path. Path3 still delivers full OriginalQWC at once.
    private bool _pktActive;
    private uint _pktFlg;
    private uint _pktNloop;      // PACKED/REGLIST: total loops; IMAGE: total QWs
    private uint _pktLoop;       // PACKED: completed loops; REGLIST: values written
    private uint _pktRegI;       // PACKED: reg index within current loop
    private bool _pktEop;        // stop after this packet completes
    private ulong _pktPartialQws; // telemetry: times a packet spanned Receive* calls
    private ulong _pktCompleted;
    private ulong _pktAborted;
    private ulong _abortNewDirect;
    private ulong _abortDirectTruncate;
    private ulong _abortOther;
    private string _lastAbortReason = "";
    private uint _lastTagFlg;
    private uint _lastTagNloop;
    private uint _lastTagNreg;
    private ulong _lastTagRegs;
    private ulong _tagsSeen;
    private ulong _path2Qws; // total Path2 QWs delivered (vs transfer count)
    private ulong _tagsCompletedPacked;
    private ulong _tagsCompletedReglist;
    private ulong _tagsCompletedImage;
    private ulong _tagsCompletedDisable;

    // GX-003: DETPS2_TRACE_GIF=1 transfer/tag ring (Path1/2/3 + sticky state)
    private const int TraceRingCap = 48;
    private readonly GifTraceSlot[] _traceRing = new GifTraceSlot[TraceRingCap];
    private int _traceRingW;
    private int _traceRingCount;
    private bool? _traceGifCached;

    // GIF I/O (0x10003000)
    private uint _ctrl;
    private uint _mode;
    private readonly uint[] _fifo = new uint[64]; // 16 QW max
    private int _fifoR, _fifoW, _fifoCount;

    /// <summary>
    /// PATH3 temporarily masked by VIF1 <c>MSKPATH3</c> (GIF_STAT bit 1 = M3P).
    /// Distinct from GIF_MODE bit 0 (M3R, permanent mask). Ground-truthed against
    /// ps2tek / PCSX2 GIF_STAT layout; commercial path-sync loops (e.g. Burnout 3
    /// at <c>0x001F19C0</c>) poll M3P and hang forever if it is never raised.
    /// </summary>
    private bool _m3p;

    /// <summary>Active output path while a transfer is in flight (GIF_STAT APATH, bits 10–12).</summary>
    private uint _apath;

    // PATH3 held while M3P/M3R masks — real HW fills the GIF FIFO (FQC rises) until unmasked.
    // Burnout 3 path-sync @ 0x001F1A28 spins on GIF_STAT.FQC (bits 24–28) after starting a
    // masked PATH3 DMA; instant-drain left FQC=0 forever.
    // Multi-kick under long mask: queue (not last-only) so unmask drains all held transfers.
    private const int HeldPath3QueueCap = 48;
    private readonly uint[] _heldPath3AddrQ = new uint[HeldPath3QueueCap];
    private readonly uint[] _heldPath3QwcQ = new uint[HeldPath3QueueCap];
    private int _heldPath3Count;
    private uint _heldPath3TotalQwc;

    public ulong Path3Transfers => _path3Transfers;
    public ulong Path2Transfers => _path2Transfers;
    public ulong Path1Transfers => _path1Transfers;
    public ulong Path1Qws => _path1Qws;
    public ulong Path3Qws => _path3Qws;
    /// <summary>Path3 submits that were held under M3P|M3R (incremented on enqueue).</summary>
    public ulong Path3HeldSubmits => _path3HeldSubmits;
    public uint HeldPath3Qwc => _heldPath3TotalQwc;
    public int HeldPath3Entries => _heldPath3Count;
    /// <summary>Completed GIFtag packets (PACKED/REGLIST/IMAGE/DISABLE) that fully drained.</summary>
    public ulong PacketsCompleted => _pktCompleted;
    /// <summary>Times a GIFtag needed more QWs than the current Receive* call (sticky reassembly).</summary>
    public ulong PacketsSpannedCalls => _pktPartialQws;
    /// <summary>True while a multi-QW GIFtag is waiting for more Path2/3 data.</summary>
    public bool PacketInFlight => _pktActive;
    public uint LastTagFlg => _lastTagFlg;
    public uint LastTagNloop => _lastTagNloop;
    public uint LastTagNreg => _lastTagNreg;
    public ulong LastTagRegs => _lastTagRegs;
    public ulong TagsSeen => _tagsSeen;
    /// <summary>Total Path2 QWs fed (batch-aware; better than Path2Transfers when DIRECT is coalesced).</summary>
    public ulong Path2Qws => _path2Qws;
    /// <summary>In-flight packet progress: loops/values/QWs consumed so far.</summary>
    public uint PacketProgress => _pktLoop;
    public uint PacketNloop => _pktActive ? _pktNloop : 0;
    public uint PacketFlg => _pktActive ? _pktFlg : 0;
    public ulong PacketsAborted => _pktAborted;
    public ulong AbortNewDirect => _abortNewDirect;
    public ulong AbortDirectTruncate => _abortDirectTruncate;
    public ulong AbortOther => _abortOther;
    public string LastAbortReason => _lastAbortReason;
    public ulong TagsCompletedPacked => _tagsCompletedPacked;
    public ulong TagsCompletedReglist => _tagsCompletedReglist;
    public ulong TagsCompletedImage => _tagsCompletedImage;
    public ulong TagsCompletedDisable => _tagsCompletedDisable;
    public int TraceRingCount => _traceRingCount;

    /// <summary>GIF_STAT M3P — PATH3 masked by VIF1 MSKPATH3.</summary>
    public bool Path3MaskedByVif => _m3p;

    /// <summary>One DETPS2_TRACE_GIF ring slot (Path1/2/3 xfer or tag/abort).</summary>
    public readonly struct GifTraceSlot
    {
        public readonly byte Kind;  // 0=xfer 1=tag 2=complete 3=abort
        public readonly byte Path;  // 1/2/3 (APATH)
        public readonly byte Flg;   // GIFtag FLG or 0xFF
        public readonly byte Flags; // bit0=held bit1=inFlightAfter
        public readonly uint Addr;
        public readonly uint QwcOrNloop;
        public readonly ulong Completed;
        public readonly ulong Aborted;

        public GifTraceSlot(byte kind, byte path, byte flg, byte flags, uint addr, uint qwcOrNloop, ulong completed, ulong aborted)
        {
            Kind = kind; Path = path; Flg = flg; Flags = flags;
            Addr = addr; QwcOrNloop = qwcOrNloop; Completed = completed; Aborted = aborted;
        }
    }

    public Gif(Gs gs)
    {
        _gs = gs ?? throw new ArgumentNullException(nameof(gs));
    }

    public void Reset()
    {
        _lastQwcProcessed = 0;
        _path1Transfers = _path2Transfers = _path3Transfers = 0;
        _path1Qws = _path3Qws = 0;
        _path3HeldSubmits = 0;
        _regs = 0;
        _nreg = 0;
        ClearPacketState();
        _pktPartialQws = 0;
        _pktCompleted = 0;
        _pktAborted = 0;
        _abortNewDirect = _abortDirectTruncate = _abortOther = 0;
        _lastAbortReason = "";
        _lastTagFlg = _lastTagNloop = _lastTagNreg = 0;
        _lastTagRegs = 0;
        _tagsSeen = 0;
        _path2Qws = 0;
        _tagsCompletedPacked = _tagsCompletedReglist = _tagsCompletedImage = _tagsCompletedDisable = 0;
        _traceRingW = 0;
        _traceRingCount = 0;
        _traceGifCached = null;
        _ctrl = _mode = 0;
        _fifoR = _fifoW = _fifoCount = 0;
        _m3p = false;
        _apath = 0;
        _heldPath3Count = 0;
        _heldPath3TotalQwc = 0;
    }

    private void ClearPacketState()
    {
        _pktActive = false;
        _pktFlg = 0;
        _pktNloop = 0;
        _pktLoop = 0;
        _pktRegI = 0;
        _pktEop = false;
    }

    private bool TraceGifEnabled
    {
        get
        {
            if (_traceGifCached is bool b) return b;
            b = Environment.GetEnvironmentVariable("DETPS2_TRACE_GIF") == "1";
            _traceGifCached = b;
            return b;
        }
    }

    private void RingPush(byte kind, byte path, byte flg, byte flags, uint addr, uint qwcOrNloop)
    {
        if (!TraceGifEnabled) return;
        _traceRing[_traceRingW] = new GifTraceSlot(
            kind, path, flg, flags, addr, qwcOrNloop, _pktCompleted, _pktAborted);
        _traceRingW = (_traceRingW + 1) % TraceRingCap;
        if (_traceRingCount < TraceRingCap) _traceRingCount++;
    }

    /// <summary>Copy recent DETPS2_TRACE_GIF ring entries (oldest→newest) into dest; returns count.</summary>
    public int CopyTraceRing(Span<GifTraceSlot> dest)
    {
        int n = Math.Min(dest.Length, _traceRingCount);
        if (n == 0) return 0;
        int start = (_traceRingW - _traceRingCount + TraceRingCap) % TraceRingCap;
        for (int i = 0; i < n; i++)
            dest[i] = _traceRing[(start + i) % TraceRingCap];
        return n;
    }

    /// <summary>
    /// Drop an in-flight GIFtag so the next Path2 QW is parsed as a fresh tag.
    /// Used when VIF1 issues a new DIRECT while a prior DIRECT left a truncated / garbage
    /// packet mid-stream (GoW: first DIRECT IMM=0xBF0 at 0x46BE90 was not GIF — REGLIST
    /// nloop=12301 sticky-swallowed later real PACKED A+D at 0x3969xx).
    /// Each commercial DIRECT is typically a self-contained Path2 unit (EOP packets sized
    /// to IMM); multi-DIRECT continuous IMAGE is Path3's job.
    /// </summary>
    public void AbortIncompletePacket(string reason = "")
    {
        if (!_pktActive) return;
        _pktAborted++;
        _lastAbortReason = reason ?? "";
        if (reason == "new-DIRECT") _abortNewDirect++;
        else if (reason == "DIRECT-end-truncated") _abortDirectTruncate++;
        else _abortOther++;
        byte path = (byte)(_apath != 0 ? _apath : 2);
        RingPush(3, path, (byte)_pktFlg, 0, 0, _pktNloop);
        if (TraceGifEnabled)
        {
            Console.Error.WriteLine(
                $"[GIF] abort in-flight flg={_pktFlg} progress={_pktLoop}/{_pktNloop} " +
                $"reason={reason} n={_pktAborted} completed={_pktCompleted}");
        }
        ClearPacketState();
    }

    /// <summary>
    /// VIF1 MSKPATH3 effect: IMM bit 15 (0x8000) = 1 masks PATH3 (M3P=1), 0 unmasks.
    /// (ps2tek: "Sets the VIF-side PATH3 mask to bit 15 of IMMEDIATE.")
    /// Real hardware latches this until the opposite MSKPATH3; GIF_STAT.M3P mirrors it.
    /// Unmask drains any held PATH3 FIFO data into the GS.
    /// </summary>
    public void SetMskPath3(bool masked)
    {
        _m3p = masked;
        if (!masked)
            DrainHeldPath3();
    }

    private bool Path3Masked => _m3p || (_mode & 1) != 0;

    private void DrainHeldPath3()
    {
        if (_heldPath3Count == 0) return;
        _fifoR = _fifoW = _fifoCount = 0;
        int n = _heldPath3Count;
        _heldPath3Count = 0;
        _heldPath3TotalQwc = 0;
        _apath = 3;
        for (int i = 0; i < n; i++)
        {
            uint addr = _heldPath3AddrQ[i];
            uint qwc = _heldPath3QwcQ[i];
            if (qwc != 0)
                ProcessTransfer(addr, qwc);
        }
        _apath = 0;
    }

    private void EnqueueHeldPath3(uint address, uint qwc)
    {
        if (qwc == 0) return;
        if (_heldPath3Count >= HeldPath3QueueCap)
        {
            // Process oldest now so multi-kick under long M3P is not discarded.
            uint oldA = _heldPath3AddrQ[0];
            uint oldQ = _heldPath3QwcQ[0];
            Array.Copy(_heldPath3AddrQ, 1, _heldPath3AddrQ, 0, HeldPath3QueueCap - 1);
            Array.Copy(_heldPath3QwcQ, 1, _heldPath3QwcQ, 0, HeldPath3QueueCap - 1);
            _heldPath3Count = HeldPath3QueueCap - 1;
            if (_heldPath3TotalQwc >= oldQ) _heldPath3TotalQwc -= oldQ;
            else _heldPath3TotalQwc = 0;
            if (oldQ != 0)
            {
                _apath = 3;
                ProcessTransfer(oldA, oldQ);
                _apath = 0;
            }
        }
        _heldPath3AddrQ[_heldPath3Count] = address;
        _heldPath3QwcQ[_heldPath3Count] = qwc;
        _heldPath3Count++;
        _heldPath3TotalQwc += qwc;
        int words = (int)Math.Min(_heldPath3TotalQwc, 16u) * 4;
        _fifoCount = Math.Min(words, _fifo.Length);
        _fifoR = 0;
        _fifoW = _fifoCount;
    }

    /// <summary>
    /// Compose GIF_STAT (0x10003020). Bits (ps2tek / PCSX2):
    /// 0 M3R, 1 M3P, 2 IMT, 3 PSE, 5 IP3, 6 P3Q, 7 P2Q, 8 P1Q, 9 OPH,
    /// 10–12 APATH, 13 DIR, 24–31 FQC.
    /// </summary>
    public uint ReadStat()
    {
        uint fqc = (uint)Math.Min(Math.Max(_fifoCount / 4, 0), 16);
        // Path-sync (Burnout 3 @ 0x001F1A28): after MSKPATH3 the EE spins on FQC!=0
        // before kicking the next VIF1/GIF chain. Real HW has the just-started PATH3
        // DMA already filling the FIFO under the mask. Our HLE can race such that the
        // fill DMA completed unmasked (FQC drained) or never delivered, leaving
        // M3P=1 with FQC=0 forever and the busy-flag in the flip-queue consumer stuck.
        // While PATH3 is masked, report at least 1 QW so the poller proceeds; the next
        // chain re-validates against real DMA state. Unmasked path is unchanged.
        if (Path3Masked && fqc == 0)
            fqc = 1;
        uint oph = (_fifoCount > 0 || _apath != 0 || (Path3Masked && fqc > 0)) ? 1u : 0u;
        uint imt = (_mode & 2) != 0 ? 1u : 0u; // GIF_MODE.IMT → STAT.IMT
        return (_mode & 1)                      // M3R
               | (_m3p ? 2u : 0u)               // M3P
               | (imt << 2)                     // IMT
               | (oph << 9)                     // OPH
               | ((_apath & 7) << 10)           // APATH
               | (fqc << 24);                   // FQC
    }

    /// <summary>GIF_CTRL / MODE / STAT / FIFO at 0x10003000–0x10006000.</summary>
    public uint ReadRegister(uint address)
    {
        uint off = address & 0xFFFF;
        return off switch
        {
            0x3000 => 0, // GIF_CTRL write-only
            0x3010 => _mode,
            0x3020 => ReadStat(),
            0x3040 or 0x3050 or 0x3060 or 0x3070 => 0,
            0x3080 or 0x3090 or 0x30A0 => 0,
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        uint off = address & 0xFFFF;
        switch (off)
        {
            case 0x3000: // GIF_CTRL
                _ctrl = value;
                if ((value & 1) != 0) // reset
                {
                    _fifoR = _fifoW = _fifoCount = 0;
                    _m3p = false;
                    _apath = 0;
                    _heldPath3Count = 0;
                    _heldPath3TotalQwc = 0;
                }
                break;
            case 0x3010: // GIF_MODE
            {
                uint prev = _mode;
                _mode = value;
                // Clearing M3R unmasks PATH3 — drain held FIFO like MSKPATH3 unmask.
                if ((prev & 1) != 0 && (value & 1) == 0)
                    DrainHeldPath3();
                break;
            }
        }
    }

    /// <summary>EE writes to GIF FIFO (0x10006000) — one word at a time; assemble QWs and process.</summary>
    public void WriteFifo(uint value)
    {
        if (_fifoCount >= _fifo.Length) return;
        _fifo[_fifoW] = value;
        _fifoW = (_fifoW + 1) % _fifo.Length;
        _fifoCount++;
        // When PATH3 is masked (M3P/M3R), FIFO data must stay and raise FQC — Burnout 3
        // path-sync @ 0x001F1A28 polls FQC after MSKPATH3 + FIFO/DMA fill. Instant-drain
        // left FQC=0 and the spin never exited.
        if (Path3Masked)
            return;
        // Unmasked: every full QW is fair game to consume (DMA is the usual path; FIFO
        // pokes are rare and treated as fire-and-forget for telemetry).
        if (_fifoCount >= 4 && (_fifoCount % 4) == 0)
            DrainFifoQuadwords();
    }

    private void DrainFifoQuadwords()
    {
        // Instant-process: GIF FIFO PATH3-style streaming is rare vs DMA; when games poke FIFO we
        // accumulate words into a temporary buffer on the GS local mem high area and run ProcessTransfer.
        // Minimal: count transfers for telemetry; full FIFO-to-GS PATH3 stream needs a proper state machine.
        _path3Transfers++;
        // Drop words (games often uses DMA not FIFO). Keep FIFO empty so STAT never stalls.
        _fifoR = _fifoW = _fifoCount = 0;
    }

    /// <summary>Path3 — DMAC GIF channel.</summary>
    public void ReceivePath3Data(uint address, uint qwc)
    {
        if (qwc == 0) return;
        _path3Transfers++;
        _path3Qws += qwc;
        if (TransferLog.Enabled) TransferLog.Log("GIF:Path3->GS", address, 0, qwc * 16);

        if (Path3Masked)
        {
            // Hold in FIFO queue: raise FQC so path-sync loops that poll STAT.FQC can proceed.
            // Queue (not last-only) so multi-kick IMAGE/PACKED under long M3P still reaches GS.
            _path3HeldSubmits++;
            EnqueueHeldPath3(address, qwc);
            RingPush(0, 3, 0xFF, 0x01, address, qwc); // held
            if (TraceGifEnabled)
            {
                Console.Error.WriteLine(
                    $"[GIF] Path3 HOLD addr=0x{address:X8} qwc={qwc} heldN={_heldPath3Count} " +
                    $"heldQwc={_heldPath3TotalQwc} m3p={_m3p} completed={_pktCompleted} aborted={_pktAborted}");
            }
            return;
        }

        // Unmasked: process immediately (instant HLE).
        _apath = 3;
        RingPush(0, 3, 0xFF, (byte)(_pktActive ? 2 : 0), address, qwc);
        if (TraceGifEnabled)
        {
            Console.Error.WriteLine(
                $"[GIF] Path3 xfer addr=0x{address:X8} qwc={qwc} n={_path3Transfers} " +
                $"inFlight={_pktActive} completed={_pktCompleted} aborted={_pktAborted}");
        }
        ProcessTransfer(address, qwc);
        _apath = 0;
    }

    /// <summary>Path2 — from VIF1 DIRECT/HL. Sticky mid-packet across QW-sliced DMA.</summary>
    public void ReceivePath2Data(uint address, uint qwc)
    {
        if (qwc == 0) return; // match Path3: do not inflate transfer counts on empty feeds
        _path2Transfers++;
        _path2Qws += qwc;
        if (TransferLog.Enabled) TransferLog.Log("GIF:Path2->GS", address, 0, qwc * 16);
        _apath = 2;
        byte flags = (byte)(_pktActive ? 2 : 0);
        RingPush(0, 2, 0xFF, flags, address, qwc);
        if (TraceGifEnabled)
        {
            Console.Error.WriteLine(
                $"[GIF] Path2 xfer addr=0x{address:X8} qwc={qwc} n={_path2Transfers} p2qws={_path2Qws} " +
                $"inFlight={_pktActive} completed={_pktCompleted} aborted={_pktAborted}");
        }
        ProcessTransfer(address, qwc);
        _apath = 0;
    }

    /// <summary>Path1 — VU1 XGKICK style: process tags from memory.</summary>
    public void ReceivePath1Data(uint address, uint qwc)
    {
        if (qwc == 0) return;
        _path1Transfers++;
        _path1Qws += qwc;
        if (TransferLog.Enabled) TransferLog.Log("GIF:Path1->GS", address, 0, qwc * 16);
        _apath = 1;
        RingPush(0, 1, 0xFF, (byte)(_pktActive ? 2 : 0), address, qwc);
        if (TraceGifEnabled)
        {
            Console.Error.WriteLine(
                $"[GIF] Path1 xfer addr=0x{address:X8} qwc={qwc} n={_path1Transfers} " +
                $"inFlight={_pktActive} completed={_pktCompleted} aborted={_pktAborted}");
        }
        ProcessTransfer(address, qwc);
        _apath = 0;
    }

    /// <summary>
    /// Process an in-memory GIF stream. Sticky: if a prior call left a mid-packet
    /// (common when VIF1 feeds Path2 one QW at a time), continue that packet before
    /// parsing a new GIFtag. EOP still ends the current logical stream once the
    /// in-flight packet fully drains.
    /// </summary>
    public void ProcessTransfer(uint address, uint qwc)
    {
        if (qwc == 0) return;
        _lastQwcProcessed = qwc;

        uint currentAddr = address;
        uint remaining = qwc;

        while (remaining > 0)
        {
            if (!_pktActive)
            {
                // GIFtag is 128-bit
                uint w0 = Read32(currentAddr);
                uint w1 = Read32(currentAddr + 4);
                uint w2 = Read32(currentAddr + 8);
                uint w3 = Read32(currentAddr + 12);

                // bits 0-14 NLOOP, 15 EOP, 46 PRE, 47-57 PRIM, 58-59 FLG, 60-63 NREG
                // 64-127 REGS
                ulong tagLo = w0 | ((ulong)w1 << 32);
                uint nloop = (uint)(tagLo & 0x7FFF);
                bool eop = (tagLo & (1UL << 15)) != 0;
                bool pre = (tagLo & (1UL << 46)) != 0;
                uint prim = (uint)((tagLo >> 47) & 0x7FF);
                uint flg = (uint)((tagLo >> 58) & 0x3);
                uint nreg = (uint)((tagLo >> 60) & 0xF);
                if (nreg == 0) nreg = 16;
                _nreg = nreg;
                _regs = w2 | ((ulong)w3 << 32);

                currentAddr += 16;
                remaining--;

                if (pre)
                    _gs.WriteGsRegister(0x00, prim);

                _tagsSeen++;
                _lastTagFlg = flg;
                _lastTagNloop = nloop;
                _lastTagNreg = nreg;
                _lastTagRegs = _regs;
                byte path = (byte)(_apath != 0 ? _apath : 0);
                RingPush(1, path, (byte)flg, 0, currentAddr - 16, nloop);
                // GX-010 inventory: Path2 non-PACKED with huge nloop often means garbage DIRECT
                // (GoW IMM=0xBF0 REGLIST nloop=12301). Telemetry only — do not invent abort here;
                // new-DIRECT / DIRECT-end-truncated already drop sticky so real PACKED A+D lands.
                bool path2Huge =
                    path == 2 && flg is 1 or 2 && nloop > 4096;
                if (TraceGifEnabled && (_tagsSeen <= 48 || path2Huge))
                {
                    Console.Error.WriteLine(
                        $"[GIF] tag#{_tagsSeen} flg={flg} nloop={nloop} nreg={nreg} eop={eop} " +
                        $"pre={pre} regs=0x{_regs:X16} apath={_apath} addr=0x{currentAddr - 16:X8}" +
                        (path2Huge ? " WARN=path2-huge-nloop" : ""));
                }

                // Empty NLOOP or DISABLE with nothing to skip: packet complete immediately.
                if (flg == 3)
                {
                    // DISABLE — skip nloop QWs (best-effort; same as prior HLE)
                    uint skip = Math.Min(nloop, remaining);
                    currentAddr += skip * 16;
                    remaining -= skip;
                    if (skip < nloop)
                    {
                        _pktActive = true;
                        _pktFlg = 3;
                        _pktNloop = nloop - skip;
                        _pktEop = eop;
                    }
                    else
                    {
                        NotePacketCompleted(3);
                        if (eop) break;
                    }
                    continue;
                }

                if (nloop == 0)
                {
                    NotePacketCompleted(flg);
                    if (eop) break;
                    continue;
                }

                _pktActive = true;
                _pktFlg = flg;
                _pktNloop = nloop;
                _pktLoop = 0;
                _pktRegI = 0;
                _pktEop = eop;
            }

            // Drain active packet body with available QWs.
            uint flgBefore = _pktFlg;
            remaining = _pktFlg switch
            {
                0 => DrainPacked(ref currentAddr, remaining),
                1 => DrainReglist(ref currentAddr, remaining),
                2 => DrainImage(ref currentAddr, remaining),
                3 => DrainDisable(ref currentAddr, remaining),
                _ => DrainDisable(ref currentAddr, remaining)
            };

            if (_pktActive)
            {
                // Still need more data from a future Receive* call (VIF1 QW-slice).
                if (remaining == 0)
                    _pktPartialQws++;
                break;
            }

            NotePacketCompleted(flgBefore);
            if (_pktEop) break;
        }
    }

    private void NotePacketCompleted(uint flg)
    {
        _pktCompleted++;
        switch (flg)
        {
            case 0: _tagsCompletedPacked++; break;
            case 1: _tagsCompletedReglist++; break;
            case 2: _tagsCompletedImage++; break;
            default: _tagsCompletedDisable++; break;
        }
        byte path = (byte)(_apath != 0 ? _apath : 0);
        RingPush(2, path, (byte)flg, 0, 0, 0);
    }

    /// <summary>PACKED: each loop writes nreg registers; each register is one QW.</summary>
    private uint DrainPacked(ref uint addr, uint remaining)
    {
        uint nreg = _nreg == 0 ? 1u : _nreg;
        while (remaining > 0 && _pktLoop < _pktNloop)
        {
            while (remaining > 0 && _pktRegI < nreg)
            {
                uint lo = Read32(addr);
                uint hi = Read32(addr + 4);
                uint mid = Read32(addr + 8);
                uint regId = RegAt(_pktRegI);
                ulong data = lo | ((ulong)hi << 32);
                if (regId == 0x0E) // A+D
                {
                    uint adReg = mid & 0x7F;
                    _gs.WriteGsRegister(adReg, data);
                }
                else
                {
                    _gs.WriteGsRegister(regId, data);
                }
                addr += 16;
                remaining--;
                _pktRegI++;
            }
            if (_pktRegI >= nreg)
            {
                _pktRegI = 0;
                _pktLoop++;
            }
        }
        if (_pktLoop >= _pktNloop)
            _pktActive = false;
        return remaining;
    }

    /// <summary>REGLIST: 64-bit values tightly packed, 2 per QW. _pktLoop = values written.</summary>
    private uint DrainReglist(ref uint addr, uint remaining)
    {
        uint nreg = _nreg == 0 ? 1u : _nreg;
        uint total = _pktNloop * nreg;
        while (remaining > 0 && _pktLoop < total)
        {
            uint lo = Read32(addr);
            uint hi = Read32(addr + 4);
            uint lo2 = Read32(addr + 8);
            uint hi2 = Read32(addr + 12);
            ulong d0 = lo | ((ulong)hi << 32);
            ulong d1 = lo2 | ((ulong)hi2 << 32);

            uint reg0 = RegAt(_pktLoop % nreg);
            _gs.WriteGsRegister(reg0, d0);
            _pktLoop++;
            if (_pktLoop < total)
            {
                uint reg1 = RegAt(_pktLoop % nreg);
                _gs.WriteGsRegister(reg1, d1);
                _pktLoop++;
            }
            addr += 16;
            remaining--;
        }
        if (_pktLoop >= total)
            _pktActive = false;
        return remaining;
    }

    /// <summary>IMAGE: nloop QWs of raw host→local data. _pktLoop = QWs written.</summary>
    private uint DrainImage(ref uint addr, uint remaining)
    {
        Span<byte> qw = stackalloc byte[16];
        while (remaining > 0 && _pktLoop < _pktNloop)
        {
            for (int b = 0; b < 16; b++)
                qw[b] = Memory.Read8(addr + (uint)b);
            // dest offset for legacy fallback; TRX cursor owns commercial path
            _gs.WriteImageData(qw, (int)(_pktLoop * 16));
            addr += 16;
            remaining--;
            _pktLoop++;
        }
        if (_pktLoop >= _pktNloop)
            _pktActive = false;
        return remaining;
    }

    private uint DrainDisable(ref uint addr, uint remaining)
    {
        uint skip = Math.Min(_pktNloop, remaining);
        addr += skip * 16;
        remaining -= skip;
        _pktNloop -= skip;
        if (_pktNloop == 0)
            _pktActive = false;
        return remaining;
    }

    private uint RegAt(uint index)
    {
        // REGS: 4 bits per register, index 0..15
        int shift = (int)((index % 16) * 4);
        return (uint)((_regs >> shift) & 0xF);
    }

    private SystemMemory Memory => _gs.Memory;

    private uint Read32(uint addr) => Memory.Read32(addr);

    public int Step(ulong maxCycles)
    {
        if (_lastQwcProcessed == 0)
            return 1;
        uint nreg = _nreg == 0 ? 1u : _nreg;
        int cost = _gs.CalculateWorkCost(_lastQwcProcessed, nreg);
        _lastQwcProcessed = 0;
        return Math.Min(cost, (int)Math.Max(1L, (long)maxCycles));
    }
}
