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

    // M1-b (playability-roadmap.json): large single-call transfers are budgeted instead of
    // instant-completing in one ProcessTransfer call -- same "manufactured instant progress"
    // bug class A1 fixed for Dmac.cs's GIF_STAT poll-pump. Dmac.DeliverSegment hands the WHOLE
    // original DMA segment (ch.OriginalQWC, can be many thousands of QW for a large IMAGE) to
    // Receive*Data in one call regardless of A1's own per-Step cycle budgeting inside the DMAC
    // itself -- that budgets how fast the channel drains, not how much GIF renders in one call
    // once it does. Residual re-enters through the SAME public Receive*Data entry point on a
    // later Step() tick (not a raw ProcessTransfer call), so existing Path2/Path3 sticky-hold
    // semantics apply to the deferred part exactly as they would to a fresh, unrelated transfer.
    private const uint MaxQwPerReceiveCall = 256;
    private static readonly bool DisableM1bBudgetedProcess =
        Environment.GetEnvironmentVariable("DETPS2_DISABLE_M1B_BUDGETED_PROCESS") == "1";
    private byte _pendingBudgetPath; // 0 = none, else 1/2/3 matching APATH
    // M1-b: true only while Step() is re-entering Receive*Data to drain a deferred
    // residual. Path*Transfers is a load-bearing counter -- MidwayBootAssist.cs gates
    // real boot-progression heuristics on Path3Transfers thresholds (<=5, >=8, >=11),
    // tuned against "one count per real external segment". Budgeted continuation calls
    // must NOT inflate that count, or those thresholds fire early/wrong for titles that
    // happen to submit one large Path3 segment split across ticks.
    private bool _isBudgetContinuation;
    private uint _pendingBudgetAddr;
    private uint _pendingBudgetQwc;

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
    private bool _pktEop;        // EOP on current tag (path free after packet drains)
    private uint _pktPath;       // APATH that owns sticky mid-packet (1/2/3); GX-010
    private ulong _pktPartialQws; // telemetry: times a packet spanned Receive* calls
    private ulong _pktCompleted;
    private ulong _pktAborted;
    private ulong _abortNewDirect;
    private ulong _abortDirectTruncate;
    private ulong _abortOther;
    private ulong _path2StalledByPath3; // Path2 xfer while Path3 owned sticky
    private ulong _path3StalledByPath2; // Path3 xfer while Path2 owned sticky
    private ulong _path2HeldSubmits;    // Path2 enqueued under Path3 sticky (G2)
    private ulong _path2HoldDrops;      // Path2 hold overflow drops
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

    // M7-c Slice 2a: Path3 IMAGE delivery bisect (counters always accumulate; print gated).
    // path3Kicks reuses _path3Transfers. Dmac already has GetTelemFinish(GIF) for finish-side
    // (no new Dmac kick counter). TRACE_GIF ring is 48-slot + gated — full-run buckets need counters.
    private ulong _path3ImageTags;
    private ulong _path3ImageCompleted;
    private ulong _path3ImageStalled;
    private string _lastImageStallReason = "";

    // GX-003: DETPS2_TRACE_GIF=1 transfer/tag ring (Path1/2/3 + sticky state)
    private const int TraceRingCap = 48;
    private readonly GifTraceSlot[] _traceRing = new GifTraceSlot[TraceRingCap];
    private int _traceRingW;
    private int _traceRingCount;
    private bool? _traceGifCached;

    // GX-011: private 1-QW buffer for FIFO DIRECT assembly (no EE poke).
    private readonly uint[] _inlineQw = new uint[4];
    private bool _inlineActive;

    // GIF I/O (0x10003000)
    private uint _ctrl;
    private uint _mode;
    private readonly uint[] _fifo = new uint[64]; // 16 QW max
    private int _fifoR, _fifoW, _fifoCount;

    // M1-c: dedicated staging for real EE FIFO pokes (WriteFifo), independent of
    // _fifo/_fifoR/_fifoW/_fifoCount above. Those four fields are ALSO reused by
    // EnqueueHeldPath3 purely as a synthetic FQC/telemetry counter (it resets
    // _fifoR/_fifoW/_fifoCount to represent held-Path3 QWC, without touching _fifo's
    // contents) -- trusting them for real word data here would risk feeding stale or
    // mismatched bytes into the GIFtag parser as if they were genuine FIFO words.
    // _fifoStage only ever holds words this call actually wrote via WriteFifo while
    // unmasked, so draining it is safe regardless of what the shared counters say.
    private static readonly bool DisableM1cBudgetedFifo =
        Environment.GetEnvironmentVariable("DETPS2_DISABLE_M1C_BUDGETED_FIFO") == "1";
    private readonly uint[] _fifoStage = new uint[4];
    private int _fifoStageCount;

    // M1-a: ReadStat's FQC fabrication (below) used to fire unconditionally whenever
    // Path3Masked && fqc==0, forever, even when PATH3 had never actually transferred
    // anything -- an outright invention, not just an early/optimistic report. The real
    // race it exists for (Burnout 3 @ 0x001F1A28) only happens right after an UNMASKED
    // PATH3 delivery instant-completes just before the game's own mask+poll instructions
    // run; ReceivePath3Data's unmasked branch sets this evidence counter at that moment.
    // Fabrication now requires that evidence and is capped by a generous poll budget as
    // a pure safety backstop -- NOT tightened enough to risk resurrecting the documented
    // hang, since the honest fix here is "don't lie about PATH3 that never ran," not
    // "shrink the window for PATH3 that genuinely did."
    private static readonly bool DisableM1aHonestFqc =
        Environment.GetEnvironmentVariable("DETPS2_DISABLE_M1A_HONEST_FQC") == "1";
    private const int M1aRaceEvidencePollBudget = 65536;
    private int _path3RaceEvidencePolls;

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

    // G2: Path2 held while Path3 owns sticky (esp. multi-DMA Host→Local IMAGE).
    // Prior HLE dropped Path2 QWs → VIF already debited DIRECT → desync → abort storms
    // on Midway/DA Path2 paint after Path3 IMAGE setup. Hold + drain when Path3 frees.
    private const int HeldPath2QueueCap = 32;
    private readonly uint[] _heldPath2AddrQ = new uint[HeldPath2QueueCap];
    private readonly uint[] _heldPath2QwcQ = new uint[HeldPath2QueueCap];
    private int _heldPath2Count;
    private uint _heldPath2TotalQwc;
    // Inline Path2 QWs (FIFO DIRECT) held under Path3 sticky — small ring of 4-word slots.
    private const int HeldPath2InlineCap = 16;
    private readonly uint[] _heldPath2Inline = new uint[HeldPath2InlineCap * 4];
    private int _heldPath2InlineCount;

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
    /// <summary>M7-c Slice 2a: IMAGE GIFtags parsed on Path3 (flg=2).</summary>
    public ulong Path3ImageTags => _path3ImageTags;
    /// <summary>M7-c Slice 2a: IMAGE packets fully drained on Path3.</summary>
    public ulong Path3ImageCompleted => _path3ImageCompleted;
    /// <summary>M7-c Slice 2a: times Path3 IMAGE left sticky mid-packet after a Receive* chunk.</summary>
    public ulong Path3ImageStalled => _path3ImageStalled;
    /// <summary>M7-c Slice 2a: last Path3 IMAGE partial reason (telemetry only).</summary>
    public string LastImageStallReason => _lastImageStallReason;
    public int TraceRingCount => _traceRingCount;
    /// <summary>Path (1/2/3) that owns the in-flight sticky packet; 0 if idle.</summary>
    public uint PacketPath => _pktActive ? _pktPath : 0;
    /// <summary>Path2 Receive* held/stalled because Path3 owned sticky (Play!-style arbitration).</summary>
    public ulong Path2StalledByPath3 => _path2StalledByPath3;
    /// <summary>Path3 Receive* held/stalled because Path2 owned sticky.</summary>
    public ulong Path3StalledByPath2 => _path3StalledByPath2;
    /// <summary>Path2 submits enqueued under Path3 sticky (G2 hold; not dropped).</summary>
    public ulong Path2HeldSubmits => _path2HeldSubmits;
    /// <summary>Path2 hold overflow drops (should stay 0 on commercial MENU claims).</summary>
    public ulong Path2HoldDrops => _path2HoldDrops;
    public int HeldPath2Entries => _heldPath2Count + _heldPath2InlineCount;
    public uint HeldPath2Qwc => _heldPath2TotalQwc + (uint)_heldPath2InlineCount;

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
        _path2StalledByPath3 = _path3StalledByPath2 = 0;
        _path2HeldSubmits = _path2HoldDrops = 0;
        _lastAbortReason = "";
        _lastTagFlg = _lastTagNloop = _lastTagNreg = 0;
        _lastTagRegs = 0;
        _tagsSeen = 0;
        _path2Qws = 0;
        _tagsCompletedPacked = _tagsCompletedReglist = _tagsCompletedImage = _tagsCompletedDisable = 0;
        _path3ImageTags = _path3ImageCompleted = _path3ImageStalled = 0;
        _lastImageStallReason = "";
        _traceRingW = 0;
        _traceRingCount = 0;
        _traceGifCached = null;
        _ctrl = _mode = 0;
        _fifoR = _fifoW = _fifoCount = 0;
        _m3p = false;
        _apath = 0;
        _heldPath3Count = 0;
        _heldPath3TotalQwc = 0;
        _heldPath2Count = 0;
        _heldPath2TotalQwc = 0;
        _heldPath2InlineCount = 0;
    }

    private void ClearPacketState()
    {
        _pktActive = false;
        _pktFlg = 0;
        _pktNloop = 0;
        _pktLoop = 0;
        _pktRegI = 0;
        _pktEop = false;
        _pktPath = 0;
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

    /// <summary>M7-c Slice 2a: <c>DETPS2_TRACE_GIF_BISECT=1</c> prints Path3 IMAGE bisect line.
    /// Counters always accumulate (cheap); print only when set. Zero transfer behavior change.</summary>
    public static bool TraceGifBisect =>
        Environment.GetEnvironmentVariable("DETPS2_TRACE_GIF_BISECT") == "1";

    /// <summary>
    /// Print M7-c Slice 2a Path3 IMAGE bisect telemetry.
    /// <c>path3Kicks</c> reuses <see cref="Path3Transfers"/> (GIF-side submits).
    /// DMAC finish-side for GIF channel: <c>Dmac.GetTelemFinish((int)Dmac.Channel.GIF)</c> — no new Dmac counter.
    /// </summary>
    public void DumpBisectSummary(string prefix = "[GIF-BISECT]")
    {
        string reason = string.IsNullOrEmpty(_lastImageStallReason) ? "-" : _lastImageStallReason;
        Console.Error.WriteLine(
            $"{prefix} path3Kicks={_path3Transfers} path3ImageTags={_path3ImageTags} " +
            $"path3ImageCompleted={_path3ImageCompleted} path3ImageStalled={_path3ImageStalled} " +
            $"lastStallReason={reason}");
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
    /// GX-010: VIF DIRECT boundaries must not abort Path3-owned sticky (Play! path arb).
    /// G2: new-DIRECT still drops Path2 hold (cancelled DIRECT payload must not drain later).
    /// </summary>
    public void AbortIncompletePacket(string reason = "")
    {
        // G2: superseding DIRECT cancels any Path2 QWs held under Path3 sticky from the
        // prior DIRECT (VIF already zeroed _directRemaining). DIRECT-end-truncate keeps
        // holds — those QWs were legitimately paid for by the finished IMM.
        if (reason == "new-DIRECT")
            ClearHeldPath2();

        if (!_pktActive) return;
        // Reduce harmful aborts: VIF DIRECT supersede/truncate is Path2-scoped.
        // Do not clear Path3-owned sticky (Play! path arbitration).
        if (_pktPath == 3 &&
            (reason == "new-DIRECT" || reason == "DIRECT-end-truncate"))
        {
            if (TraceGifEnabled)
            {
                Console.Error.WriteLine(
                    $"[GIF] skip Path2-boundary abort of Path3 sticky flg={_pktFlg} " +
                    $"progress={_pktLoop}/{_pktNloop} reason={reason}");
            }
            return;
        }
        _pktAborted++;
        _lastAbortReason = reason ?? "";
        if (reason == "new-DIRECT") _abortNewDirect++;
        else if (reason == "DIRECT-end-truncate") _abortDirectTruncate++;
        else _abortOther++;
        byte path = (byte)(_pktPath != 0 ? _pktPath : (_apath != 0 ? _apath : 2));
        uint abortedPath = _pktPath;
        RingPush(3, path, (byte)_pktFlg, 0, 0, _pktNloop);
        if (TraceGifEnabled)
        {
            Console.Error.WriteLine(
                $"[GIF] abort in-flight flg={_pktFlg} path={_pktPath} progress={_pktLoop}/{_pktNloop} " +
                $"reason={reason} n={_pktAborted} completed={_pktCompleted}");
        }
        ClearPacketState();
        // G2: Path3 IMAGE sticky abort (assist / RST / other) must release held Path2.
        if (abortedPath == 3 && (_heldPath2Count > 0 || _heldPath2InlineCount > 0))
            DrainHeldPath2();
        // Path2 abort may free the path for held Path3 (M3P still gates).
        if (abortedPath == 2 && _heldPath3Count > 0 && !Path3Masked)
            DrainHeldPath3();
    }

    private void ClearHeldPath2()
    {
        _heldPath2Count = 0;
        _heldPath2TotalQwc = 0;
        _heldPath2InlineCount = 0;
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
        // G2: never clobber Path2-owned sticky with held Path3 body (Play! path arb).
        // Path2 completion path will re-call DrainHeldPath3 when sticky clears.
        if (_pktActive && _pktPath == 2)
            return;
        _fifoR = _fifoW = _fifoCount = 0;
        // Snapshot queue so re-enqueue mid-drain cannot alias the live arrays.
        int n = _heldPath3Count;
        Span<uint> addrs = stackalloc uint[n];
        Span<uint> qwcs = stackalloc uint[n];
        for (int i = 0; i < n; i++)
        {
            addrs[i] = _heldPath3AddrQ[i];
            qwcs[i] = _heldPath3QwcQ[i];
        }
        _heldPath3Count = 0;
        _heldPath3TotalQwc = 0;
        _apath = 3;
        for (int i = 0; i < n; i++)
        {
            // Mid-loop Path2 sticky (nested DIRECT) — re-enqueue rest and stop.
            if (_pktActive && _pktPath == 2)
            {
                for (int j = i; j < n; j++)
                    EnqueueHeldPath3(addrs[j], qwcs[j]);
                break;
            }
            uint addr = addrs[i];
            uint qwc = qwcs[i];
            if (qwc != 0)
                ProcessTransfer(addr, qwc);
        }
        _apath = 0;
        // Path3 IMAGE may have completed; release any Path2 held under Path3 sticky.
        if (!_pktActive && (_heldPath2Count > 0 || _heldPath2InlineCount > 0))
            DrainHeldPath2();
    }

    private void EnqueueHeldPath3(uint address, uint qwc)
    {
        if (qwc == 0) return;
        if (_heldPath3Count >= HeldPath3QueueCap)
        {
            // Process oldest now so multi-kick under long M3P is not discarded —
            // but never while Path2 owns sticky (would feed Path3 QWs as Path2 body).
            uint oldA = _heldPath3AddrQ[0];
            uint oldQ = _heldPath3QwcQ[0];
            Array.Copy(_heldPath3AddrQ, 1, _heldPath3AddrQ, 0, HeldPath3QueueCap - 1);
            Array.Copy(_heldPath3QwcQ, 1, _heldPath3QwcQ, 0, HeldPath3QueueCap - 1);
            _heldPath3Count = HeldPath3QueueCap - 1;
            if (_heldPath3TotalQwc >= oldQ) _heldPath3TotalQwc -= oldQ;
            else _heldPath3TotalQwc = 0;
            if (oldQ != 0 && !(_pktActive && _pktPath == 2))
            {
                _apath = 3;
                ProcessTransfer(oldA, oldQ);
                _apath = 0;
            }
            // else: drop oldest under Path2 sticky (prefer not corrupting DIRECT reassembly)
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
    /// G2: drain Path2 held while Path3 owned sticky (multi-DMA IMAGE → Path2 paint).
    /// Only when Path3 sticky is idle; M3P does not gate Path2.
    /// </summary>
    private void DrainHeldPath2()
    {
        if (_heldPath2Count == 0 && _heldPath2InlineCount == 0) return;
        if (_pktActive && _pktPath == 3)
            return; // still owned by Path3 IMAGE/PACKED sticky

        // Snapshot so re-hold cannot alias live queues.
        int n = _heldPath2Count;
        uint[]? addrSnap = null;
        uint[]? qwcSnap = null;
        if (n > 0)
        {
            addrSnap = new uint[n];
            qwcSnap = new uint[n];
            Array.Copy(_heldPath2AddrQ, addrSnap, n);
            Array.Copy(_heldPath2QwcQ, qwcSnap, n);
        }
        _heldPath2Count = 0;
        _heldPath2TotalQwc = 0;

        int ni = _heldPath2InlineCount;
        uint[]? inlineSnap = null;
        if (ni > 0)
        {
            inlineSnap = new uint[ni * 4];
            Array.Copy(_heldPath2Inline, inlineSnap, ni * 4);
        }
        _heldPath2InlineCount = 0;

        for (int i = 0; i < n; i++)
        {
            if (_pktActive && _pktPath == 3)
            {
                for (int j = i; j < n; j++)
                    EnqueueHeldPath2(addrSnap![j], qwcSnap![j]);
                if (inlineSnap != null)
                {
                    for (int j = 0; j < ni; j++)
                    {
                        int b = j * 4;
                        EnqueueHeldPath2Inline(
                            inlineSnap[b], inlineSnap[b + 1], inlineSnap[b + 2], inlineSnap[b + 3]);
                    }
                }
                return;
            }
            uint addr = addrSnap![i];
            uint qwc = qwcSnap![i];
            if (qwc != 0)
            {
                _path2Transfers++;
                _path2Qws += qwc;
                _apath = 2;
                ProcessTransfer(addr, qwc);
                _apath = 0;
            }
        }

        for (int i = 0; i < ni; i++)
        {
            if (_pktActive && _pktPath == 3)
            {
                for (int j = i; j < ni; j++)
                {
                    int b = j * 4;
                    EnqueueHeldPath2Inline(
                        inlineSnap![b], inlineSnap[b + 1], inlineSnap[b + 2], inlineSnap[b + 3]);
                }
                return;
            }
            int basei = i * 4;
            _path2Transfers++;
            _path2Qws += 1;
            _apath = 2;
            _inlineQw[0] = inlineSnap![basei];
            _inlineQw[1] = inlineSnap[basei + 1];
            _inlineQw[2] = inlineSnap[basei + 2];
            _inlineQw[3] = inlineSnap[basei + 3];
            _inlineActive = true;
            try { ProcessTransfer(0, 1); }
            finally { _inlineActive = false; _apath = 0; }
        }

        // Path2 finished — Path3 held under path2-stall can drain (if unmasked).
        if (!_pktActive && _heldPath3Count > 0 && !Path3Masked)
            DrainHeldPath3();
    }

    private void EnqueueHeldPath2(uint address, uint qwc)
    {
        if (qwc == 0) return;
        if (_heldPath2Count >= HeldPath2QueueCap)
        {
            _path2HoldDrops++;
            // Drop newest under overflow (keep older Path2 order intact).
            if (TraceGifEnabled)
            {
                Console.Error.WriteLine(
                    $"[GIF] Path2 HOLD overflow drop addr=0x{address:X8} qwc={qwc} " +
                    $"heldN={_heldPath2Count} drops={_path2HoldDrops}");
            }
            return;
        }
        _heldPath2AddrQ[_heldPath2Count] = address;
        _heldPath2QwcQ[_heldPath2Count] = qwc;
        _heldPath2Count++;
        _heldPath2TotalQwc += qwc;
        _path2HeldSubmits++;
    }

    private void EnqueueHeldPath2Inline(uint w0, uint w1, uint w2, uint w3)
    {
        if (_heldPath2InlineCount >= HeldPath2InlineCap)
        {
            _path2HoldDrops++;
            return;
        }
        int b = _heldPath2InlineCount * 4;
        _heldPath2Inline[b] = w0;
        _heldPath2Inline[b + 1] = w1;
        _heldPath2Inline[b + 2] = w2;
        _heldPath2Inline[b + 3] = w3;
        _heldPath2InlineCount++;
        _path2HeldSubmits++;
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
        // M1-a: only when there's real evidence a PATH3 delivery just raced the mask
        // (see _path3RaceEvidencePolls) -- not unconditionally, which would report FQC=1
        // even for titles/masks where PATH3 never ran at all. Bounded, not permanent.
        if (Path3Masked && fqc == 0)
        {
            if (DisableM1aHonestFqc)
            {
                fqc = 1;
            }
            else if (_path3RaceEvidencePolls > 0)
            {
                fqc = 1;
                _path3RaceEvidencePolls--;
            }
        }
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
                    _heldPath2Count = 0;
                    _heldPath2TotalQwc = 0;
                    _heldPath2InlineCount = 0;
                    _path3RaceEvidencePolls = 0;
                    // GX-010: RST must drop sticky mid-packet so next path is not
                    // swallowed as body data (Play! / PCSX2 clear path state on RST).
                    ClearPacketState();
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
        // pokes are rare and treated as fire-and-forget for telemetry). Stage separately
        // from _fifoCount (see M1-c comment above) so a concurrent held-Path3 FQC reset
        // of the shared counters can never desync this word's position in its QW.
        _fifoStage[_fifoStageCount++] = value;
        if (_fifoStageCount >= 4)
        {
            _fifoStageCount = 0;
            DrainFifoQuadwords(_fifoStage[0], _fifoStage[1], _fifoStage[2], _fifoStage[3]);
            _fifoR = _fifoW = _fifoCount = 0;
        }
    }

    /// <summary>
    /// M1-c: honestly process one QW poked directly into the GIF FIFO (0x10006000) through
    /// the same GIFtag/packet state machine as Path1-3, instead of silently dropping it.
    /// FIFO pokes have no EE/SPR address, so the QW is fed via the inline-QW mechanism
    /// (the same one <see cref="ReceivePath2Quadword"/> uses). Kill-switch restores the old
    /// drop/reset stub for A/B testing.
    /// </summary>
    private void DrainFifoQuadwords(uint w0, uint w1, uint w2, uint w3)
    {
        if (DisableM1cBudgetedFifo)
        {
            _path3Transfers++;
            return;
        }
        // GX-010 parity: Path2-owned sticky must not be clobbered by FIFO/Path3-style data.
        // FIFO pokes are rare/telemetry-grade (per existing WriteFifo comment); dropping this
        // one QW under an active Path2 DIRECT is safer than corrupting the reassembly.
        if (_pktActive && _pktPath == 2)
        {
            _path3StalledByPath2++;
            return;
        }
        _path3Transfers++;
        uint prevApath = _apath;
        _apath = 3;
        _inlineQw[0] = w0;
        _inlineQw[1] = w1;
        _inlineQw[2] = w2;
        _inlineQw[3] = w3;
        _inlineActive = true;
        try
        {
            ProcessTransfer(0, 1);
        }
        finally
        {
            _inlineActive = false;
            _apath = prevApath;
        }
        if (!_pktActive && _heldPath3Count > 0 && !Path3Masked)
            DrainHeldPath3();
    }

    /// <summary>Path3 — DMAC GIF channel.</summary>
    public void ReceivePath3Data(uint address, uint qwc)
    {
        if (qwc == 0) return;
        // GX-010: Path2-owned sticky — do not clobber mid-DIRECT as Path3 body (Play! stalls).
        // Hold like M3P so real PATH3 is not invented and Path2 reassembly stays intact.
        if (_pktActive && _pktPath == 2)
        {
            _path3StalledByPath2++;
            _path3HeldSubmits++;
            EnqueueHeldPath3(address, qwc);
            RingPush(0, 3, 0xFF, 0x01, address, qwc);
            if (TraceGifEnabled)
            {
                Console.Error.WriteLine(
                    $"[GIF] Path3 STALL(path2-sticky) addr=0x{address:X8} qwc={qwc} " +
                    $"heldN={_heldPath3Count} stalled={_path3StalledByPath2}");
            }
            return;
        }
        // M1-b: don't recount a budgeted residual continuation as a new external segment
        // (see _isBudgetContinuation doc) -- MidwayBootAssist.cs gates real thresholds on
        // Path3Transfers. QW accumulation (_path3Qws) still reflects genuine data moved.
        if (!_isBudgetContinuation)
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
        ProcessTransferBudgeted(address, qwc);
        _apath = 0;
        // M1-a: this unmasked delivery is the exact evidence ReadStat's FQC fabrication
        // requires — if the game masks PATH3 and polls STAT right after this, honor the
        // documented race for a bounded number of polls instead of lying unconditionally.
        _path3RaceEvidencePolls = M1aRaceEvidencePollBudget;
        // G2: Path3 multi-DMA IMAGE just completed — drain Path2 held under Path3 sticky.
        if (!_pktActive && (_heldPath2Count > 0 || _heldPath2InlineCount > 0))
            DrainHeldPath2();
        // Path2 sticky finished during this call — drain any Path3 held for path2-stall.
        if (!_pktActive && _heldPath3Count > 0 && !Path3Masked)
            DrainHeldPath3();
    }

    /// <summary>Path2 — from VIF1 DIRECT/HL. Sticky mid-packet across QW-sliced DMA.</summary>
    public void ReceivePath2Data(uint address, uint qwc)
    {
        if (qwc == 0) return; // match Path3: do not inflate transfer counts on empty feeds
        // GX-010/G2: Path3-owned sticky — hold Path2 (do not drop). Multi-DMA Host→Local
        // IMAGE leaves sticky between GIF DMA segments; dropping Path2 desynced VIF DIRECT
        // debit vs GIF → Midway/DA abort storms on subsequent paint.
        if (_pktActive && _pktPath == 3)
        {
            _path2StalledByPath3++;
            EnqueueHeldPath2(address, qwc);
            RingPush(0, 2, 0xFF, 0x01, address, qwc);
            if (TraceGifEnabled)
            {
                Console.Error.WriteLine(
                    $"[GIF] Path2 HOLD(path3-sticky) addr=0x{address:X8} qwc={qwc} " +
                    $"heldN={_heldPath2Count} stalled={_path2StalledByPath3} " +
                    $"progress={_pktLoop}/{_pktNloop} flg={_pktFlg}");
            }
            return;
        }
        // M1-b: see _isBudgetContinuation doc — don't recount a residual continuation.
        if (!_isBudgetContinuation)
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
        ProcessTransferBudgeted(address, qwc);
        _apath = 0;
        // If Path2 sticky just finished, drain Path3 held during path2-stall.
        if (!_pktActive && _heldPath3Count > 0 && !Path3Masked)
            DrainHeldPath3();
    }
    /// <summary>
    /// GX-011: feed one assembled Path2 QW from VIF FIFO / partial DIRECT buffer
    /// without requiring a contiguous EE address (Play! m_directQwordBuffer path).
    /// Words are processed via a private inline buffer — no EE/SPR poke.
    /// </summary>
    public void ReceivePath2Quadword(uint w0, uint w1, uint w2, uint w3)
    {
        if (_pktActive && _pktPath == 3)
        {
            _path2StalledByPath3++;
            EnqueueHeldPath2Inline(w0, w1, w2, w3);
            return;
        }
        _path2Transfers++;
        _path2Qws += 1;
        _apath = 2;
        RingPush(0, 2, 0xFF, (byte)(_pktActive ? 2 : 0), 0, 1);
        _inlineQw[0] = w0;
        _inlineQw[1] = w1;
        _inlineQw[2] = w2;
        _inlineQw[3] = w3;
        _inlineActive = true;
        try
        {
            ProcessTransfer(0, 1);
        }
        finally
        {
            _inlineActive = false;
            _apath = 0;
        }
        if (!_pktActive && _heldPath3Count > 0 && !Path3Masked)
            DrainHeldPath3();
    }

    /// <summary>Path1 — VU1 XGKICK style: process tags from memory.</summary>
    public void ReceivePath1Data(uint address, uint qwc)
    {
        if (qwc == 0) return;
        // M1-b: see _isBudgetContinuation doc — don't recount a residual continuation.
        if (!_isBudgetContinuation)
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
        ProcessTransferBudgeted(address, qwc);
        _apath = 0;
    }

    /// <summary>
    /// M1-b: bounded front door for Path1/2/3's "process now" call sites. Callers must set
    /// <c>_apath</c> to the correct path (1/2/3) before calling, same as they already do for
    /// the direct <see cref="ProcessTransfer"/> call this replaces. When <paramref name="qwc"/>
    /// exceeds <see cref="MaxQwPerReceiveCall"/> and no residual is already outstanding, only
    /// the budgeted portion is processed now; the remainder is deferred to <see cref="Step"/>,
    /// which re-submits it through the SAME public Receive*Data entry point (not a raw
    /// ProcessTransfer call) so Path2/Path3 sticky-hold semantics apply to the deferred part
    /// exactly as they would to any other transfer. If a residual is already outstanding when
    /// a new large transfer arrives (rare double-large-transfer collision), this falls back to
    /// processing the new one unbounded rather than dropping data or silently reordering two
    /// pending residuals -- correctness over perfect budgeting for that edge case.
    /// </summary>
    private void ProcessTransferBudgeted(uint address, uint qwc)
    {
        uint thisCall = qwc;
        if (!DisableM1bBudgetedProcess && qwc > MaxQwPerReceiveCall && _pendingBudgetPath == 0)
            thisCall = MaxQwPerReceiveCall;
        ProcessTransfer(address, thisCall);
        if (thisCall < qwc && _pendingBudgetPath == 0)
        {
            _pendingBudgetPath = (byte)_apath;
            _pendingBudgetAddr = address + thisCall * 16;
            _pendingBudgetQwc = qwc - thisCall;
        }
    }

    /// <summary>
    /// Process an in-memory GIF stream. Sticky: if a prior call left a mid-packet
    /// (common when VIF1 feeds Path2 one QW at a time), continue that packet before
    /// parsing a new GIFtag. GX-010: EOP frees the path after the packet drains, but
    /// remaining QWs in the same transfer may start a new tag (Play! ProcessMultiplePackets).
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
                // M7-c Slice 2a: Path3 IMAGE tag seen (telemetry only).
                if (flg == 2 && path == 3)
                    _path3ImageTags++;
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
                        _pktPath = _apath != 0 ? _apath : _pktPath;
                        _pktLoop = 0;
                        _pktRegI = 0;
                    }
                    else
                    {
                        NotePacketCompleted(3);
                        // EOP frees path; more tags may follow in this transfer (Play!).
                    }
                    continue;
                }

                if (nloop == 0)
                {
                    NotePacketCompleted(flg);
                    // EOP frees path; continue if remaining QWs hold another tag.
                    continue;
                }

                _pktActive = true;
                _pktFlg = flg;
                _pktNloop = nloop;
                _pktLoop = 0;
                _pktRegI = 0;
                _pktEop = eop;
                _pktPath = _apath != 0 ? _apath : 2; // default Path2 when apath unset
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
                // M7-c Slice 2a: Path3 IMAGE sticky across chunk (multi-DMA or budgeted residual).
                // Telemetry only — does not alter drain/hold behavior.
                if (_pktFlg == 2 && _pktPath == 3)
                {
                    _path3ImageStalled++;
                    _lastImageStallReason = $"image-partial progress={_pktLoop}/{_pktNloop}";
                }
                break;
            }

            NotePacketCompleted(flgBefore);
            // GX-010: do not break on EOP while remaining > 0 — next tag may follow
            // in the same DIRECT/DMA buffer (Play! ProcessMultiplePackets loop).
        }
    }

    private void NotePacketCompleted(uint flg)
    {
        _pktCompleted++;
        switch (flg)
        {
            case 0: _tagsCompletedPacked++; break;
            case 1: _tagsCompletedReglist++; break;
            case 2:
                _tagsCompletedImage++;
                // M7-c Slice 2a: Path3-specific IMAGE complete (split of global counter).
                // _apath is set for the duration of ReceivePath3Data / held Path3 drain.
                if (_apath == 3)
                    _path3ImageCompleted++;
                break;
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
            if (_inlineActive)
            {
                // Little-endian pack of the private Path2 QW (GX-011 FIFO DIRECT).
                for (int i = 0; i < 4; i++)
                {
                    uint w = _inlineQw[i];
                    qw[i * 4 + 0] = (byte)w;
                    qw[i * 4 + 1] = (byte)(w >> 8);
                    qw[i * 4 + 2] = (byte)(w >> 16);
                    qw[i * 4 + 3] = (byte)(w >> 24);
                }
            }
            else
            {
                for (int b = 0; b < 16; b++)
                    qw[b] = Memory.Read8(addr + (uint)b);
            }
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

    private uint Read32(uint addr)
    {
        if (_inlineActive)
            return _inlineQw[(addr >> 2) & 3];
        return Memory.Read32(addr);
    }

    public int Step(ulong maxCycles)
    {
        // M1-b: drain one budgeted chunk of a deferred large transfer, if any, before
        // reporting cost. Clear the pending slot BEFORE re-entering Receive*Data so a
        // still-oversized residual correctly re-arms a fresh pending slot for the NEXT
        // Step() tick (chains across as many ticks as the transfer needs) instead of the
        // re-entrant call seeing its own not-yet-cleared residual and taking the "already
        // outstanding" fallback path in ProcessTransferBudgeted.
        if (_pendingBudgetPath != 0)
        {
            byte path = _pendingBudgetPath;
            uint addr = _pendingBudgetAddr;
            uint qwc = _pendingBudgetQwc;
            _pendingBudgetPath = 0;
            _pendingBudgetAddr = 0;
            _pendingBudgetQwc = 0;
            _isBudgetContinuation = true;
            try
            {
                switch (path)
                {
                    case 1: ReceivePath1Data(addr, qwc); break;
                    case 2: ReceivePath2Data(addr, qwc); break;
                    case 3: ReceivePath3Data(addr, qwc); break;
                }
            }
            finally
            {
                _isBudgetContinuation = false;
            }
        }
        if (_lastQwcProcessed == 0)
            return 1;
        uint nreg = _nreg == 0 ? 1u : _nreg;
        int cost = _gs.CalculateWorkCost(_lastQwcProcessed, nreg);
        _lastQwcProcessed = 0;
        return Math.Min(cost, (int)Math.Max(1L, (long)maxCycles));
    }
}
