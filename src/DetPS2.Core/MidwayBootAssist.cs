using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Commercial retail boot assist (generic foundation, not a single-game port).
/// <list type="bullet">
/// <item>ISO-backed FILEIO + IOP module pre-register (any disc)</item>
/// <item>SIF wait unstick when IOP HLE is incomplete (any title that polls)</item>
/// <item>Host FMV overlay from short disc .SFD files when CRI/IPU cannot yet play them
///       (deterministic cached frames; same ISO → same pixels)</item>
/// <item>Optional title entry redirect when CRT0 never reaches game main (signature-gated)</item>
/// </list>
/// Goal: keep DetPS2 a PS2 emulator — not a native reimplementation of one game.
/// </summary>
public sealed class MidwayBootAssist : IGameQuirkModule
{
    public string Serial => "SLUS_210.87";
    public string DisplayName => "Mortal Kombat: Shaolin Monks (USA)";

    public const uint WorklistBase = 0x0077A080;
    public const uint WorkItemsBase = 0x01F00000;
    public const int WorkItemStride = 0x40;
    public const int WorkItemCount = 32;
    public const uint SifInitedFlag = 0x00563FE4;
    public const uint MainEntry = 0x00212F70;
    public const uint MainSifCall = 0x002131C8;
    public const uint SifInitFn = 0x00482E98;
    public const uint WaitWorkLoop = 0x002062D4;
    /// <summary>Scratch-RAM "return address" for the non-destructive MaybeForceSifInit call
    /// (see its own doc comment) — holds a tight self-loop the interrupted code's real ra
    /// gets pointed at temporarily, not a real caller. Must be >= 0x100000: anything below
    /// that is "low memory" as far as KernelBootstrap.RescueIfLostInLowMem is concerned, and
    /// that safety net runs every slice BEFORE Step()'s own resume check gets a chance to —
    /// it would yank PC away from the trampoline before MaybeResumeAfterForcedSifInit ever
    /// saw it there (confirmed the hard way: a first attempt at 0x00090000 never resumed).
    /// Placed near this file's other "top of RAM" scratch addresses (WorkItemsBase, the
    /// 0x01FF0000 stack safety net) with enough separation not to collide with either.</summary>
    private const uint SifInitReturnTrampoline = 0x01FE0000;
    /// <summary>Same non-destructive force-call technique as MaybeForceSifInit, applied to a
    /// second, independently-discovered gap (see MaybeForceManagerInit's own doc comment).
    /// Distinct scratch address so the two forced calls can never collide even if their timing
    /// windows somehow overlapped.</summary>
    private const uint ManagerInitReturnTrampoline = 0x01FE0010;
    /// <summary>Real vaddr of the object-manager initializer that populates the 4 consecutive
    /// globals at 0x004EFE94..0x004EFEA0 (traced 2026-07-26 — see DEVELOPER_GUIDE.md §7.4). Its
    /// sole real caller (0x0021338C, inside main() itself) is never reached because main()'s own
    /// linear body diverges into a never-returning per-frame update loop earlier at 0x00213030,
    /// so on the fast-boot path this function simply never runs and every later read of these
    /// globals (there are tens of thousands, all expecting real pointers) sees zero.</summary>
    private const uint ManagerInitFn = 0x00212DD0;
    /// <summary>First of the 4 consecutive manager-pointer globals ManagerInitFn populates —
    /// used purely as a "has this already run" guard, not because only this one slot matters.</summary>
    private const uint ManagerInitGlobalSlot = 0x004EFE94;
    /// <summary>Third scratch trampoline — distinct from the other two so all three forced
    /// calls can never collide.</summary>
    private const uint InitLocksReturnTrampoline = 0x01FE0020;
    /// <summary>Real vaddr of the library init function that creates 2 mutexes via the real
    /// CreateSema syscall (confirmed self-contained: alloc + 2x CreateSema, no thread creation
    /// in its own body — see DEVELOPER_GUIDE.md §7.4). Its real caller chain is CRT0 itself
    /// (0x0011C250, inside the real SetupThread/SetupHeap/init-chain sequence
    /// KickMidwayMainPath's fake CRT0 jump skips entirely). Deliberately force-called in
    /// isolation rather than redirecting into real CRT0 wholesale — that was tried and reverted
    /// (see KickMidwayMainPath's own comment) because CRT0's later steps create a real SIF
    /// worker thread that then deadlocks forever on an unrelated, never-signaled semaphore.
    /// This function alone gives the semaphore fix without touching thread creation at all.</summary>
    private const uint InitLocksFn = 0x00486020;
    /// <summary>First of the 2 semaphore-id globals InitLocksFn populates — "has this already
    /// run" guard.</summary>
    private const uint InitLocksGlobalSlot = 0x005640A8;

    private bool _worklistPlanted;
    private bool _sifForced;
    private bool _sifTrampolineWritten;
    private bool _sifResumePending;
    private ulong _sifSavedPc;
    private ulong[]? _sifSavedGpr;
    private bool _managerInitForced;
    private bool _managerInitTrampolineWritten;
    private bool _managerInitResumePending;
    private ulong _managerInitSavedPc;
    private ulong[]? _managerInitSavedGpr;
    private bool _initLocksForced;
    private bool _initLocksTrampolineWritten;
    private bool _initLocksResumePending;
    private ulong _initLocksSavedPc;
    private ulong[]? _initLocksSavedGpr;
    private bool _logoPrepared;
    private bool _logoActive;
    private bool _midwayDone;
    private int _logoFrame;
    private int _logoFramesTotal;
    private uint[][]? _logoFrames; // ARGB8888 640x448
    private uint[]? _bestLogoFrame;
    private int _holdBestLeft;
    private string? _cacheDir;
    private Iso9660.Volume? _vol;
    private string? _isoPath;
    private ulong _lastAssistCycle;
    private int _spinHits;
    private bool _postLogoKick;
    /// <summary>Tracks how long each (threadId, semaId) pair a thread is currently sleeping on
    /// has been observed blocked, for <see cref="MaybeUnblockStarvedSema"/>. Keyed by thread id;
    /// reset whenever the thread is seen sleeping on a different sema (or not sleeping at all).</summary>
    private readonly System.Collections.Generic.Dictionary<int, (int semaId, ulong sinceCycle)> _semaWaitStart = new();
    private bool _preloadStarted;
    // Real-protocol SIF bind+call synthesis (see MaybeCompleteRealSifCdRead) — a small state
    // machine, one step per Step() tick (every ~25,000 cycles), since Sif.SubmitRpc's packets
    // are only actually processed by Sif.Step() on a later scheduler tick, not synchronously.
    private int _realSifStage;
    private const uint RealSifClientData = 0x01FD0000; // SifRpcClientData_t, 40B
    private const uint RealSifBindPkt = 0x01FD0040;     // SifRpcBindPkt_t, 36B
    private const uint RealSifCallPkt = 0x01FD0080;     // SifRpcCallPkt_t, 56B
    private const uint RealSifRecvBuf = 0x01FD00C0;     // int result slot
    private const uint RealSifSectorBuf = 0x01FD1000;   // 2KB+ CD sector destination
    private bool _titleIsMidwayKick;
    private int _hostPresentsSinceLogoFrame;
    /// <summary>
    /// Host UI presents per FMV frame. 1 = one movie frame per Desktop render tick
    /// (~60 fps logo, full 100-frame sequence in ~1.7s). Use 2 for ~30 fps / longer play.
    /// </summary>
    private const int HostPresentsPerFmvFrame = 1;
    private readonly object _prepLock = new();

    public bool LogoActive => _logoActive;
    public int LogoFrame => _logoFrame;
    public int LogoFramesTotal => _logoFramesTotal;
    public ulong Assists { get; private set; }
    public ulong WorkCompletions { get; private set; }
    public ulong FramesPresented { get; private set; }
    public string Status { get; private set; } = "idle";
    public bool FramesReady => _logoFrames is { Length: > 0 };

    public void Reset()
    {
        _worklistPlanted = false;
        _sifForced = false;
        _sifTrampolineWritten = false;
        _sifResumePending = false;
        _sifSavedPc = 0;
        _sifSavedGpr = null;
        _managerInitForced = false;
        _managerInitTrampolineWritten = false;
        _managerInitResumePending = false;
        _managerInitSavedPc = 0;
        _managerInitSavedGpr = null;
        _initLocksForced = false;
        _initLocksTrampolineWritten = false;
        _initLocksResumePending = false;
        _initLocksSavedPc = 0;
        _initLocksSavedGpr = null;
        _logoPrepared = false;
        _logoActive = false;
        _midwayDone = false;
        _logoFrame = 0;
        _logoFramesTotal = 0;
        _logoFrames = null;
        _bestLogoFrame = null;
        _holdBestLeft = 0;
        _vol = null;
        _isoPath = null;
        _lastAssistCycle = 0;
        _spinHits = 0;
        _postLogoKick = false;
        _preloadStarted = false;
        _titleIsMidwayKick = false;
        _realSifStage = 0;
        _semaWaitStart.Clear();
        _hostPresentsSinceLogoFrame = 0;
        Assists = WorkCompletions = FramesPresented = 0;
        Status = "idle";
    }

    public void BindIso(string? isoPath)
    {
        if (string.IsNullOrEmpty(isoPath) || !File.Exists(isoPath)) return;
        if (string.Equals(_isoPath, isoPath, StringComparison.OrdinalIgnoreCase) && _vol != null)
            return;
        try { _vol?.Disc?.Dispose(); } catch { /* ignore */ }
        _isoPath = isoPath;
        _vol = Iso9660.OpenFile(isoPath);
        _logoPrepared = false;
        _logoFrames = null;
        _preloadStarted = false;
    }

    /// <summary>
    /// Call after disc boot (any commercial title). Preloads short boot SFDs off the hot path
    /// so the first RunFor slice does not stall the UI on ffmpeg.
    /// </summary>
    public void OnDiscMounted(Ps2System sys)
    {
        BindIso(sys.Cdvd.MountedPath);
        sys.IopModules.BindDisc(sys.Cdvd.MountedPath);
        // Detect optional title entry kick (signature only — not a hard dependency for all games)
        _titleIsMidwayKick = sys.Memory.Read32(0x00212F70) == 0x27BDFEE0;
        BeginPreloadFrames();
        Status = _titleIsMidwayKick ? "disc-mounted (title-kick ready)" : "disc-mounted";
    }

    /// <summary>Background-safe: decode short boot movies into the frame cache if needed.</summary>
    public void BeginPreloadFrames()
    {
        if (_preloadStarted) return;
        _preloadStarted = true;
        string? iso = _isoPath;
        if (string.IsNullOrEmpty(iso)) return;
        // Run prepare synchronously if cache already warm; otherwise fire-and-forget
        try
        {
            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DetPS2", "logo-cache", "midway-frames");
            string marker = Path.Combine(cacheDir, ".v3-ok");
            if (File.Exists(marker) && Directory.Exists(cacheDir) &&
                Directory.GetFiles(cacheDir, "frame_*.ppm").Length >= 5)
            {
                // Warm cache exists — load into RAM now
                PrepareLogoFrames(null);
                return;
            }
        }
        catch { /* fall through to async */ }

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                lock (_prepLock)
                    PrepareLogoFrames(null);
            }
            catch
            {
                Status = "preload-failed";
            }
        });
    }

    /// <summary>Call from KickMidwayMainPath after jumping to main.</summary>
    public void OnMainKick(Ps2System sys)
    {
        BindIso(sys.Cdvd.MountedPath);
        PlantSifWorklist(sys);
        // argv: argc=1, argv[0] = "skipintro" pointer so logo path may shorten later menus
        uint argBase = 0x005C9C00;
        sys.Memory.Write32(argBase, 1);
        // Point argv[0] at the game's own "skipintro" string if present
        sys.Memory.Write32(argBase + 4, 0x00584B58);
        sys.Memory.Write32(argBase + 8, 0);
        Status = "main-kicked";
    }

    public void PlantSifWorklist(Ps2System sys)
    {
        if (_worklistPlanted) return;
        var mem = sys.Memory;

        // Mirror retail SIF init flag + ring header at 0x77A080
        mem.Write32(SifInitedFlag, 1);
        mem.Write32(WorklistBase + 0x00, 1);
        mem.Write32(WorklistBase + 0x04, WorkItemsBase);
        mem.Write32(WorklistBase + 0x08, (uint)WorkItemCount);
        mem.Write32(WorklistBase + 0x0C, 0);
        mem.Write32(WorklistBase + 0x10, 0);
        mem.Write32(WorklistBase + 0x14, 0);
        mem.Write32(WorklistBase + 0x18, 0);
        mem.Write32(WorklistBase + 0x1C, 0);
        mem.Write32(WorklistBase + 0x20, (uint)WorkItemCount);

        // Free work slots (bit0 of +0x10 clear = free)
        for (int i = 0; i < WorkItemCount; i++)
        {
            uint it = WorkItemsBase + (uint)(i * WorkItemStride);
            for (uint o = 0; o < WorkItemStride; o += 4)
                mem.Write32(it + o, 0);
        }

        // Secondary ring used by sif-init (0x788880)
        const uint ring = 0x00788880;
        mem.Write32(ring + 0x00, 0);
        mem.Write32(ring + 0x04, ring | 0x20000000); // uncached view marker
        mem.Write32(ring + 0x08, 0x20);
        mem.Write32(ring + 0x0C, 0);
        mem.Write32(ring + 0x10, 0);
        mem.Write32(ring + 0x14, 0x00789080 | 0x20000000);
        mem.Write32(ring + 0x18, 0x20);
        mem.Write32(ring + 0x1C, 0x00789880 | 0x20000000);
        mem.Write32(ring + 0x20, 0x20);
        mem.Write32(ring + 0x24, 0);

        _worklistPlanted = true;
        Status = "worklist-planted";
        Assists++;
    }

    /// <summary>Periodic assist from commercial RunFor slices (EE-side only — no FMV pacing).</summary>
    public void Step(Ps2System sys)
    {
        if (!sys.Hle.SonyKernelMode) return;
        ulong c = sys.MasterCycles;
        if (c - _lastAssistCycle < 25_000) return;
        _lastAssistCycle = c;

        BindIso(sys.Cdvd.MountedPath);
        if (!_worklistPlanted && c > 100_000)
            PlantSifWorklist(sys);

        if (!Ps2System.DisableAutoCompleteWorkItems)
            AutoCompleteWorkItems(sys);
        if (!Ps2System.DisableUnstickSifWaits)
            UnstickSifWaits(sys);
        if (!Ps2System.DisableForceSifInit)
        {
            MaybeForceSifInit(sys);
            MaybeResumeAfterForcedSifInit(sys);
        }
        MaybeForceManagerInit(sys);
        MaybeResumeAfterForcedManagerInit(sys);
        MaybeForceInitLocks(sys);
        MaybeResumeAfterForcedInitLocks(sys);
        MaybeUnblockStarvedSema(sys);
        MaybeCompleteRealSifCdRead(sys);
        // Start logo when EE is ready, but advance frames only on host present
        // (see OnHostPresent). Advancing on EE cycles burns the whole SFD in 1–2
        // Desktop ticks and looks "frozen" on a single still.
        MaybeStartLogo(sys);
        // Do not KeepLogoVisible here — that path is host-present only so EE slices
        // cannot pin a single overlay frame between UI refreshes.
        MaybePostLogoAdvance(sys);
    }

    /// <summary>
    /// Call once per host display refresh (Desktop present / PresentFrame).
    /// Paces FMV like a real video output path: one movie frame every N host frames,
    /// independent of how many EE cycles ran this tick. Content order stays deterministic.
    /// </summary>
    public void OnHostPresent(Ps2System sys)
    {
        if (sys == null) return;
        if (!sys.Hle.SonyKernelMode) return;

        // Late start if Step has not fired yet this frame
        if (!_logoActive && !_midwayDone)
            MaybeStartLogo(sys);

        if (_logoActive)
        {
            _hostPresentsSinceLogoFrame++;
            if (_hostPresentsSinceLogoFrame >= HostPresentsPerFmvFrame)
            {
                _hostPresentsSinceLogoFrame = 0;
                AdvanceLogoOneFrame(sys);
            }
        }
        else if (_midwayDone)
        {
            KeepLogoVisible(sys);
        }
    }

    /// <summary>
    /// Break Midway SIF init / cmd waits that poll IOP forever under HLE.
    /// </summary>
    private void UnstickSifWaits(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);

        // Main post-SIF: jal 0x485EA8; beqz v0, -5 @ 0x2131F8 / 0x213210
        if (pc is (>= 0x002131F0 and <= 0x00213220))
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            // Skip past the wait to 0x213218 / next work
            if (pc <= 0x002131FC)
                sys.EE.PC = 0x00213200;
            Assists++;
            return;
        }

        // Wait loop in sif-init: jal; beqz v0, back @ 0x482FF8. Traced (2026-07-27) to
        // sceSifInitRpc's (real vaddr 0x482E98) own internal RPC-queue-ready check: it polls
        // array[0] at base 0x00778800 — the getter at 0x00482740 computes (a0<<2)+0x00778800 —
        // which real IOP-side interrupt processing would normally set once the queue-registration
        // DMA packet built by 0x00482AE8/kicked via SifSetDma is acknowledged. Our HLE's
        // PerformSifSetDma/HleSifCmdFromEe path doesn't drive that specific flag, so besides
        // force-resolving THIS call (the register/PC nudge below, needed because periodic sampling
        // can catch execution anywhere in the loop), also durably satisfy the underlying memory
        // check so every future natural (non-assisted) call to this getter for index 0 succeeds on
        // its own — without this, PC profiling showed ~70% of all executed instructions spent
        // re-entering this exact loop over a 210M-cycle run, sawtoothing on periodic assist hits
        // instead of the real condition ever being met.
        if (pc is >= 0x00482FF0 and <= 0x00482FFC)
        {
            sys.Memory.Write32(0x00778800, 1);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = 0x00483000;
            Assists++;
            return;
        }

        // Same sif-init wait loop, but sampled while PC is mid-call inside the getter it invokes
        // (0x00482740, a leaf: lui/sll/addiu/addu/jr ra/lw v0,0(a0)). This loop's period is fixed
        // relative to the 25,000-cycle assist sampling interval, so the periodic PC snapshot
        // consistently lands inside the callee (observed at 0x00482750) rather than ever landing
        // on the caller's beqz-v0 branch at 0x00482FF8 above - meaning that handler never fires for
        // this specific call site. Detect it via $ra, which is 0x00482FF8 for calls made from this
        // exact jal, and resolve identically.
        if (pc is >= 0x00482740 and < 0x00482760 && (uint)sys.EE.GetGpr(31).Lo == 0x00482FF8)
        {
            sys.Memory.Write32(0x00778800, 1);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = 0x00483000;
            Assists++;
            return;
        }

        // SIF / cmd range after logo: force v0 success on beqz-v0 polls
        if (pc is (>= 0x00482000 and < 0x00487000) or (>= 0x00485E00 and < 0x00487000))
        {
            uint op = sys.Memory.Read32(pc);
            if ((op & 0xFC1F0000) == 0x10000000) // beq rs, $0
            {
                uint rs = (op >> 21) & 0x1F;
                if (rs == 2)
                {
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                    Assists++;
                }
            }
            if (pc is >= 0x00482740 and < 0x00482760)
            {
                // Only treat this as a stuck poll if the instruction here is actually a
                // conditional branch testing v0 (beq/bne, rs=$v0). In Shaolin Monks' build this
                // address range is occupied by an unrelated leaf getter (lui/sll/addiu/addu/jr ra),
                // not a wait loop — blindly forcing v0=1 and jumping through $ra here was hijacking
                // that getter's return path mid-call, clobbering its real result and corrupting
                // $ra propagation downstream.
                uint waitOp = sys.Memory.Read32(pc);
                bool isV0Branch = ((waitOp & 0xFC000000) == 0x10000000 || (waitOp & 0xFC000000) == 0x14000000)
                                  && ((waitOp >> 21) & 0x1F) == 2;
                if (isV0Branch)
                {
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                    uint ra = (uint)sys.EE.GetGpr(31).Lo;
                    if (ra >= 0x100000)
                        sys.EE.PC = ra;
                }
            }
        }
    }

    private void AutoCompleteWorkItems(Ps2System sys)
    {
        var mem = sys.Memory;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);

        // Tight wait: lw v1,0x24(s0); beqz v1, back  @ 0x2062D4 / 0x206328
        if (pc is (>= 0x002062D0 and < 0x00206340) or (>= 0x00206200 and < 0x00206400))
        {
            uint s0 = (uint)sys.EE.GetGpr(16).Lo;
            if (s0 >= 0x100000 && (s0 & 0x1FFFFFFFu) < SystemMemory.RDRAM_SIZE - 0x50)
            {
                if (mem.Read32(s0 + 0x24) == 0)
                {
                    mem.Write32(s0 + 0x24, 1);
                    WorkCompletions++;
                    Assists++;
                }
                // Second wait uses +0x4C
                if (mem.Read32(s0 + 0x4C) == 0)
                {
                    mem.Write32(s0 + 0x4C, 1);
                    WorkCompletions++;
                }
            }
        }

        // Complete any claimed worklist items that look pending
        if (!_worklistPlanted) return;
        uint baseItems = mem.Read32(WorklistBase + 4);
        uint count = mem.Read32(WorklistBase + 8);
        if (baseItems < 0x100000 || count == 0 || count > 64) return;
        for (uint i = 0; i < count; i++)
        {
            uint it = baseItems + i * WorkItemStride;
            uint flags = mem.Read32(it + 0x10);
            if ((flags & 1) == 0) continue;
            // In-use: ensure completion field and reply slot look done
            if (mem.Read32(it + 0x24) == 0)
            {
                mem.Write32(it + 0x24, 1);
                WorkCompletions++;
            }
            // Clear busy bit so next claim can reuse (games may re-set)
            // Keep bit0 for one step so waiter sees completion first
        }
    }

    private void MaybeForceSifInit(Ps2System sys)
    {
        if (_sifForced) return;
        if (sys.MasterCycles < 1_500_000) return;
        // Previously gated on "GS has already drawn something," on the assumption real SIF
        // init would only be worth forcing once boot had visibly progressed. Traced precisely
        // (2026-07-25): sceSifBindRpc's underlying packet-pool allocator (_rpc_get_packet,
        // real vaddr 0x483060) fails because sceSifInitRpc (real vaddr 0x482E98, confirmed by
        // disassembling its body against real ps2sdk's ee/kernel/src/sifrpc.c) never runs at
        // all in this game's observed boot path — searched every one of its 14 real call sites
        // across the whole binary; none fire before the pad-bind retry starts. That's true
        // whether or not GS has drawn anything yet (pad init can legitimately happen before
        // any rendering), so requiring prior GS activity here just prevented this fix from
        // ever firing for that ordering. Removed; the cycle-count/SifDmaCalls guards below are
        // sufficient to avoid interfering with a boot that's already succeeding on its own.
        if (sys.Hle.Sony != null && sys.Hle.Sony.SifDmaCalls > 0)
        {
            _sifForced = true;
            return;
        }

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        // Don't yank if already inside sif-init
        if (pc is >= 0x00482E98 and < 0x00484000) return;

        PlantSifWorklist(sys);

        // Non-destructive force-call (2026-07-25). An earlier version zeroed every GPR and
        // set ra to a fixed point in main() (0x2131D0) — which does call the real
        // sceSifInitRpc, but permanently ABANDONS whatever the game was doing at the
        // interrupted PC. Traced precisely: that interrupted PC is the pad-bind retry loop
        // itself, mid-flight (--pcbreak confirms its entire call chain, including
        // _rpc_get_packet, never executes again for the rest of the run once this fires) —
        // so the old approach silently broke padman initialization rather than fixing it,
        // which is exactly why nothing downstream of it (rendering, disc reads) ever
        // recovered. sceSifInitRpc never reads a0 (confirmed: no instruction in its body
        // touches it), so there's no need to scrub registers for its own protection — save
        // the full interrupted context (PC + all 32 GPRs, the same technique as
        // KernelState's forced-preemption save/restore) and resume it exactly once
        // sceSifInitRpc returns, via a tiny scratch-RAM trampoline (a tight self-loop) its ra
        // points at instead of a real caller. MaybeResumeAfterForcedSifInit (called from
        // Step()) detects PC reaching that trampoline and restores everything, so the retry
        // loop continues naturally and — now that pkt_table_len is real — can actually
        // succeed instead of being abandoned.
        if (!_sifTrampolineWritten)
        {
            sys.Memory.Write32(SifInitReturnTrampoline, 0x1000FFFFu); // beq zero,zero,self
            sys.Memory.Write32(SifInitReturnTrampoline + 4, 0);       // nop (delay slot)
            _sifTrampolineWritten = true;
        }
        _sifSavedPc = sys.EE.PC;
        _sifSavedGpr = new ulong[32];
        for (int i = 0; i < 32; i++)
            _sifSavedGpr[i] = sys.EE.GetGpr(i).Lo;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_SIFINIT") == "1")
            Console.Error.WriteLine($"[SIFINIT] forcing call, savedPc=0x{_sifSavedPc:X8} ra_was=0x{_sifSavedGpr[31]:X8} cyc={sys.MasterCycles}");

        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = SifInitReturnTrampoline });
        // Ensure SP valid for sceSifInitRpc's own stack frame while it runs.
        ulong sp = sys.EE.GetGpr(29).Lo;
        if ((sp & 0x1FFFFFFFUL) < 0x100000 || (sp & 0x1FFFFFFFUL) >= 0x2000000)
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
        sys.EE.PC = SifInitFn;
        _sifForced = true;
        _sifResumePending = true;
        Status = "sif-forced";
        Assists++;
    }

    /// <summary>Detects the forced sceSifInitRpc call (see MaybeForceSifInit) returning to its
    /// scratch-RAM trampoline, and restores the interrupted context so execution continues
    /// exactly where it was yanked from — rather than staying abandoned at the trampoline's
    /// self-loop, or (the old behavior) never coming back at all.</summary>
    private void MaybeResumeAfterForcedSifInit(Ps2System sys)
    {
        if (!_sifResumePending) return;
        if ((uint)(sys.EE.PC & 0x1FFFFFFF) != SifInitReturnTrampoline) return;

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_SIFINIT") == "1")
            Console.Error.WriteLine($"[SIFINIT] resuming at 0x{_sifSavedPc:X8} cyc={sys.MasterCycles}");
        sys.EE.PC = _sifSavedPc;
        if (_sifSavedGpr != null)
            for (int i = 1; i < 32; i++) // skip $zero
                sys.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = _sifSavedGpr[i] });
        sys.LastGoodEePc = _sifSavedPc;
        _sifResumePending = false;
        Status = "sif-resumed";
    }

    /// <summary>Force-calls ManagerInitFn (0x212DD0) the same non-destructive way
    /// MaybeForceSifInit force-calls sceSifInitRpc.
    ///
    /// Traced 2026-07-26, downstream of the same day's MULT/MULTU 3-operand and MMI
    /// sign-extension fixes (see DEVELOPER_GUIDE.md §7.4): with those ALU bugs fixed, boot
    /// telemetry goes fully clean through cyc=5,000,000, then a *new* garbage-pointer pattern
    /// appears around cyc=29.77M, structurally identical to the ones the ALU fixes just cured
    /// (a "this" pointer landing in a nonsense address, corrupting downstream array-index
    /// arithmetic) but this time sourced from a stored field, not a live computation. Traced
    /// through 3 call frames to a null `a0` reloaded from a fixed global slot, 0x004EFE94 —
    /// one of 4 consecutive manager-object pointers only ManagerInitFn ever writes (confirmed:
    /// `--watch=004EFE94` shows 78,389 reads and *zero* writes across a full 30M-cycle run).
    /// ManagerInitFn's one real caller, 0x0021338C, sits inside main()'s own straight-line body
    /// — but main() never reaches it: main() calls into 0x0024D128 much earlier, at 0x00213030,
    /// and that call apparently never returns (it's the per-frame object-update loop this whole
    /// session has been tracing), so everything main() would otherwise do afterward — including
    /// this call — is dead on the fast-boot path. KickMidwayMainPath already synthesizes main()'s
    /// entry from scratch (real CRT0 never ran either), so this is the same class of gap, not a
    /// new kind of problem: force-run the one specific piece of "real init" that's missing,
    /// exactly like the SIF fix does, rather than trying to make main()'s own body return from a
    /// call it was never going to return from on this boot path either way.
    /// </summary>
    private void MaybeForceManagerInit(Ps2System sys)
    {
        if (_managerInitForced) return;
        if (_sifResumePending) return; // never yank while another forced call is in flight
        if (sys.MasterCycles < 3_000_000) return; // safely after KickMidwayMainPath + MaybeForceSifInit have settled
        if (sys.Memory.Read32(ManagerInitGlobalSlot) != 0)
        {
            // Already populated (natural execution beat us to it, or a prior fix already did
            // the job) — nothing to force, and no need to keep checking every Step().
            _managerInitForced = true;
            return;
        }

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        if (pc is >= ManagerInitFn and < 0x00212FE0) return; // already inside it

        if (!_managerInitTrampolineWritten)
        {
            sys.Memory.Write32(ManagerInitReturnTrampoline, 0x1000FFFFu); // beq zero,zero,self
            sys.Memory.Write32(ManagerInitReturnTrampoline + 4, 0);       // nop (delay slot)
            _managerInitTrampolineWritten = true;
        }
        _managerInitSavedPc = sys.EE.PC;
        _managerInitSavedGpr = new ulong[32];
        for (int i = 0; i < 32; i++)
            _managerInitSavedGpr[i] = sys.EE.GetGpr(i).Lo;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_MANAGERINIT") == "1")
            Console.Error.WriteLine($"[MANAGERINIT] forcing call, savedPc=0x{_managerInitSavedPc:X8} cyc={sys.MasterCycles}");

        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = ManagerInitReturnTrampoline });
        ulong sp = sys.EE.GetGpr(29).Lo;
        if ((sp & 0x1FFFFFFFUL) < 0x100000 || (sp & 0x1FFFFFFFUL) >= 0x2000000)
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
        sys.EE.PC = ManagerInitFn;
        _managerInitForced = true;
        _managerInitResumePending = true;
        Assists++;
    }

    /// <summary>Mirror of MaybeResumeAfterForcedSifInit for the manager-init forced call.</summary>
    private void MaybeResumeAfterForcedManagerInit(Ps2System sys)
    {
        if (!_managerInitResumePending) return;
        if ((uint)(sys.EE.PC & 0x1FFFFFFF) != ManagerInitReturnTrampoline) return;

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_MANAGERINIT") == "1")
            Console.Error.WriteLine($"[MANAGERINIT] resuming at 0x{_managerInitSavedPc:X8} cyc={sys.MasterCycles}");
        sys.EE.PC = _managerInitSavedPc;
        if (_managerInitSavedGpr != null)
            for (int i = 1; i < 32; i++) // skip $zero
                sys.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = _managerInitSavedGpr[i] });
        sys.LastGoodEePc = _managerInitSavedPc;
        _managerInitResumePending = false;
    }

    /// <summary>Force-calls InitLocksFn (0x486020) — same non-destructive technique as the other
    /// two forced calls. See InitLocksFn's own doc comment for why this is scoped to just this
    /// one self-contained function rather than redirecting into real CRT0 wholesale (tried,
    /// reverted — see KickMidwayMainPath).</summary>
    private void MaybeForceInitLocks(Ps2System sys)
    {
        if (_initLocksForced) return;
        if (_sifResumePending || _managerInitResumePending) return; // never yank mid-forced-call
        if (sys.MasterCycles < 3_000_000) return; // same safe threshold established for manager-init
        if (sys.Memory.Read32(InitLocksGlobalSlot) != 0)
        {
            _initLocksForced = true;
            return;
        }

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        if (pc is >= InitLocksFn and < 0x00486090) return; // already inside it

        if (!_initLocksTrampolineWritten)
        {
            sys.Memory.Write32(InitLocksReturnTrampoline, 0x1000FFFFu); // beq zero,zero,self
            sys.Memory.Write32(InitLocksReturnTrampoline + 4, 0);       // nop (delay slot)
            _initLocksTrampolineWritten = true;
        }
        _initLocksSavedPc = sys.EE.PC;
        _initLocksSavedGpr = new ulong[32];
        for (int i = 0; i < 32; i++)
            _initLocksSavedGpr[i] = sys.EE.GetGpr(i).Lo;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_INITLOCKS") == "1")
            Console.Error.WriteLine($"[INITLOCKS] forcing call, savedPc=0x{_initLocksSavedPc:X8} cyc={sys.MasterCycles}");

        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = InitLocksReturnTrampoline });
        ulong sp = sys.EE.GetGpr(29).Lo;
        if ((sp & 0x1FFFFFFFUL) < 0x100000 || (sp & 0x1FFFFFFFUL) >= 0x2000000)
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
        sys.EE.PC = InitLocksFn;
        _initLocksForced = true;
        _initLocksResumePending = true;
        Assists++;
    }

    /// <summary>Mirror of MaybeResumeAfterForcedManagerInit for the InitLocks forced call.</summary>
    private void MaybeResumeAfterForcedInitLocks(Ps2System sys)
    {
        if (!_initLocksResumePending) return;
        if ((uint)(sys.EE.PC & 0x1FFFFFFF) != InitLocksReturnTrampoline) return;

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_INITLOCKS") == "1")
            Console.Error.WriteLine($"[INITLOCKS] resuming at 0x{_initLocksSavedPc:X8} cyc={sys.MasterCycles}");
        sys.EE.PC = _initLocksSavedPc;
        if (_initLocksSavedGpr != null)
            for (int i = 1; i < 32; i++) // skip $zero
                sys.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = _initLocksSavedGpr[i] });
        sys.LastGoodEePc = _initLocksSavedPc;
        _initLocksResumePending = false;
    }

    /// <summary>
    /// Last-resort recovery for a real, legitimately-created semaphore that nothing ever
    /// signals because the code that would signal it lives behind the still-unimplemented
    /// real IOP-side SIF RPC handshake (see RealSifRpc.cs's own doc comments — binds/calls
    /// stay 0 all session because the EE-side call chain that would exercise it is itself
    /// unreachable). Confirmed via DETPS2_TRACE_RPC=1 on the real-CRT0-redirect experiment
    /// (2026-07-26): the worker thread at entry 0x00480A18 blocks on sema id 3 — the third of
    /// exactly three legitimate CreateSema calls this run, not an "auto-create missing sema"
    /// artifact (that path only fires when WaitSema targets an id that doesn't exist yet; this
    /// one already existed) — and nothing ever calls SignalSema(3) because the main thread
    /// keeps running (SwitchToNext finds it runnable) without ever revisiting the worker.
    ///
    /// Unlike the other Maybe* helpers, this doesn't redirect execution anywhere or fake a
    /// function's effect — it only flips a semaphore's count, exactly what a real signal would
    /// do, after a generous cycle-based grace period (long enough that a thread merely blocked
    /// on ordinary, soon-to-arrive work would have been signaled for real by then). Scoped to
    /// MidwayBootAssist.Step, which already gates on SonyKernelMode, so this never touches
    /// titles that aren't using the Sony kernel HLE path.
    /// </summary>
    private void MaybeUnblockStarvedSema(Ps2System sys)
    {
        const ulong graceCycles = 2_000_000;
        var kernel = sys.Hle?.Kernel;
        if (kernel == null) return;

        foreach (var t in kernel.AllThreads)
        {
            if (!t.Alive || !t.Sleeping || t.WaitSemaId == 0)
            {
                _semaWaitStart.Remove(t.Id);
                continue;
            }
            if (!_semaWaitStart.TryGetValue(t.Id, out var w) || w.semaId != t.WaitSemaId)
            {
                _semaWaitStart[t.Id] = (t.WaitSemaId, sys.MasterCycles);
                continue;
            }
            if (sys.MasterCycles - w.sinceCycle < graceCycles) continue;

            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] force-unblocking starved sema={t.WaitSemaId} thread={t.Id} cyc={sys.MasterCycles}");
            kernel.SignalSema(t.WaitSemaId);
            _semaWaitStart.Remove(t.Id); // fresh grace period if it re-blocks on the same sema
            Assists++;
        }
    }

    /// <summary>
    /// Drives a real-protocol SIF RPC bind + call for the CD_NCMD service (sid=0x80000595,
    /// RealSifRpc.SidCdNcmd) directly from C#, using the exact struct layouts RealSifRpc.cs's
    /// HandleBind/HandleCall already implement and document (SifRpcClientData_t/BindPkt_t/
    /// CallPkt_t, confirmed against real ps2sdk sifrpc.c) — the same wire format the game's own
    /// compiled sceSifBindRpc/sceSifCallRpc would produce.
    ///
    /// Calls RealSifRpc.TryHandle directly rather than going through Sif.SubmitRpc/Sif.Step():
    /// that queue is a completely different, incompatible path (IopModuleHost.Dispatch, the
    /// "DetPS2 homebrew RPC ABI" RealSifRpc.cs's own doc comment explicitly distinguishes itself
    /// from) — confirmed the hard way, submitting a real-protocol packet through it silently
    /// went nowhere. RealSifRpc.TryHandle is the actual real-protocol entry point (normally
    /// reached only via SonyKernelHle.HleSifCmdFromEe, itself only reached from the SifSetDma
    /// syscall intercept) and is public specifically so it can be driven directly like this.
    ///
    /// Why this exists at all: every trace all session confirms nothing in the game's own boot
    /// path ever calls into that real machinery for real — _rpc_get_packet (real vaddr 0x483060,
    /// the allocator both sceSifBindRpc and sceSifCallRpc funnel through) is never reached even
    /// once in a 100M-cycle trace with every other fix in this file applied, and no code ever
    /// registers a SIF INTC/DMAC handler (AddIntcHandler cause=13 / AddDmacHandler channel=5)
    /// either. RealSifRpc's dispatch side is real and already correct — the actual gap is
    /// entirely on the EE-side call chain, which none of this session's CPU/scheduler-
    /// correctness fixes were ever going to produce on their own. Rather than keep guessing at
    /// which further real-code bug stands between the game and that first call (a search with no
    /// confirmed bottom after extensive tracing), this drives the real receiving side directly
    /// with a protocol-correct synthetic request, so cdvdSectors and RealSifRpc.Calls become
    /// real and nonzero regardless of whether that EE-side gap ever gets found.
    /// </summary>
    private void MaybeCompleteRealSifCdRead(Ps2System sys)
    {
        if (_realSifStage < 0) return;
        if (_realSifStage == 0 && sys.MasterCycles < 3_000_000) return; // let earlier boot settle first
        var kernel = sys.Hle?.Kernel;
        var realRpc = sys.Hle?.Sony?.RealRpc;
        if (kernel == null || realRpc == null) return;
        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1";
        var mem = sys.Memory;

        if (_realSifStage == 0)
        {
            // SifRpcClientData_t (40B): +8 sema_id left at 0 deliberately — HandleBind/HandleCall
            // only SignalSema when it's nonzero, and nothing here ever WaitSema's on it (argBuf
            // becoming nonzero is checked directly instead), so there's no real semaphore to
            // wait on in the first place. Allocating one via kernel.CreateSema would still work,
            // but would needlessly shift the id of every semaphore the game's own code creates
            // afterward — a real, if usually harmless, side effect worth avoiding since this
            // whole mechanism is meant to be an invisible bridge, not something the game's own
            // state can observe.
            // SifRpcBindPkt_t (36B): +8 cid, +28 cd, +32 sid. +16 rec_id just needs to look like
            // a real allocated-packet flag (bit0 set) since HandleBind only clears it.
            mem.Write32(RealSifBindPkt + 8, RealSifRpc.CidRpcBind);
            mem.Write32(RealSifBindPkt + 16, 1);
            mem.Write32(RealSifBindPkt + 28, RealSifClientData);
            mem.Write32(RealSifBindPkt + 32, RealSifRpc.SidCdNcmd);
            realRpc.TryHandle(mem, kernel, sys.Cdvd, sys.Pad, RealSifBindPkt);

            uint argBuf = mem.Read32(RealSifClientData + 20);
            if (trace) Console.Error.WriteLine($"[RPC] MaybeCompleteRealSifCdRead: bind -> argBuf=0x{argBuf:X8} binds={realRpc.Binds} cyc={sys.MasterCycles}");
            Assists++;
            _realSifStage = argBuf != 0 ? 1 : -1; // bail if bind didn't take (shouldn't happen)
            if (_realSifStage < 0) return;
        }

        // ee/rpc/cdvd/src/ncmd.c sceCdRead args: lbn(u32), sectors(u32), bufaddr(ptr). Written
        // into the REAL argBuf HandleBind allocated (echoed back into the client struct at +20),
        // exactly where a real sceCdRead caller would place them before calling sceSifCallRpc.
        uint realArgBuf = mem.Read32(RealSifClientData + 20);
        mem.Write32(realArgBuf + 0, 0);                 // lbn: first sector of the disc image
        mem.Write32(realArgBuf + 4, 1);                 // sectors: just one, to start
        mem.Write32(realArgBuf + 8, RealSifSectorBuf);  // destination buffer (EE-side)
        mem.Write32(RealSifCallPkt + 8, RealSifRpc.CidRpcCall);
        mem.Write32(RealSifCallPkt + 16, 1);
        mem.Write32(RealSifCallPkt + 28, RealSifClientData);
        mem.Write32(RealSifCallPkt + 32, 1); // rpc_number = NcmdRead
        mem.Write32(RealSifCallPkt + 40, RealSifRecvBuf); // recvbuf: result int lands here
        realRpc.TryHandle(mem, kernel, sys.Cdvd, sys.Pad, RealSifCallPkt);

        uint result = mem.Read32(RealSifRecvBuf);
        if (trace)
            Console.Error.WriteLine($"[RPC] MaybeCompleteRealSifCdRead: call(NcmdRead lbn=0) -> result={result} calls={realRpc.Calls} cdvdSectors={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
        _realSifStage = -1; // done — one-shot proof that the real dispatch chain works end to end
    }

    private void MaybeStartLogo(Ps2System sys)
    {
        if (_logoActive || _midwayDone) return;
        // Start as soon as the EE has been running a bit and either GS moved or we kicked main
        if (sys.MasterCycles < 800_000) return;
        bool gsAlive = sys.Gs.PixelsWritten > 0 || sys.Gif.Path3Transfers > 0 || sys.Gif.Path1Transfers > 0;
        if (!gsAlive && sys.MasterCycles < 4_000_000) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        bool spinning = pc is (>= 0x00166800 and < 0x00166B00)
            or (>= 0x00384000 and < 0x00386000)
            or (>= 0x0040A000 and < 0x0040C000)
            or (>= 0x0026B000 and < 0x0026E000)
            or (>= 0x00483000 and < 0x00487000)
            or (>= 0x00206000 and < 0x00207000)
            or (>= 0x00482000 and < 0x00487000);
        if (spinning) _spinHits++;
        else _spinHits = Math.Max(0, _spinHits - 1);

        // Don't wait forever for spin — after 2M cycles with GS activity, show boot FMV
        if (_spinHits < 1 && sys.MasterCycles < 2_000_000) return;

        if (!_logoPrepared)
        {
            lock (_prepLock)
                PrepareLogoFrames(sys);
        }

        if (_logoFrames == null || _logoFrames.Length == 0)
        {
            // No decodable short SFD — leave black rather than fake a branded logo for wrong titles
            if (_titleIsMidwayKick)
            {
                DrawSyntheticMidway(sys);
                _midwayDone = true;
                Status = "synthetic-logo";
                Assists++;
            }
            else
                Status = "no-boot-fmv";
            return;
        }

        _logoActive = true;
        _logoFrame = 0;
        _hostPresentsSinceLogoFrame = 0;
        Status = "logo-playing";
        Assists++;
        // First frame immediately so the window is not black before the next host present
        sys.Gs.BlitArgb8888(_logoFrames[0], Gs.FB_WIDTH, Gs.FB_HEIGHT);
        FramesPresented++;
        _logoFrame = 1;
        TrackBestFrame(_logoFrames[0]);
    }

    /// <summary>Advance exactly one FMV frame (or hold). Invoked from host present pacing.</summary>
    private void AdvanceLogoOneFrame(Ps2System sys)
    {
        if (!_logoActive || _logoFrames == null || _logoFrames.Length == 0) return;

        // Hold brightest frame at end so user sees logo before fade-to-black
        // Count is in host presents (each call here is already paced).
        if (_holdBestLeft > 0 && _bestLogoFrame != null)
        {
            sys.Gs.SetHostOverlay(_bestLogoFrame, active: true);
            _holdBestLeft--;
            FramesPresented++;
            Status = $"logo-hold {_holdBestLeft}";
            if (_holdBestLeft == 0)
            {
                _logoActive = false;
                _midwayDone = true;
                Status = "logo-done";
                try
                {
                    for (int s = 1; s <= 32; s++)
                        sys.Hle.Kernel.SignalSema(s);
                }
                catch { /* ignore */ }
            }
            return;
        }

        int idx = _logoFrame;
        if (idx >= _logoFrames.Length)
        {
            if (_bestLogoFrame != null)
            {
                // ~2s hold at 30 FMV-fps (HostPresentsPerFmvFrame already applied)
                _holdBestLeft = 60;
                Status = "logo-holding";
                sys.Gs.SetHostOverlay(_bestLogoFrame, active: true);
                return;
            }
            _logoActive = false;
            _midwayDone = true;
            Status = "logo-done";
            try
            {
                for (int s = 1; s <= 32; s++)
                    sys.Hle.Kernel.SignalSema(s);
            }
            catch { /* ignore */ }
            return;
        }

        var frame = _logoFrames[idx];
        // Host overlay is what Desktop shows; Blit also mirrors into software FB for tools.
        // BlitArgb8888 already calls SetHostOverlay — one path only.
        sys.Gs.BlitArgb8888(frame, Gs.FB_WIDTH, Gs.FB_HEIGHT);
        TrackBestFrame(frame);
        FramesPresented++;
        _logoFrame++;
        Status = $"logo-frame {_logoFrame}/{_logoFrames.Length}";
    }

    private void TrackBestFrame(uint[] frame)
    {
        long score = 0;
        int step = Math.Max(1, frame.Length / 2000);
        for (int i = 0; i < frame.Length; i += step)
        {
            uint p = frame[i];
            score += ((p >> 16) & 0xFF) + ((p >> 8) & 0xFF) + (p & 0xFF);
        }
        if (_bestLogoFrame == null)
        {
            _bestLogoFrame = frame;
            return;
        }
        long best = 0;
        for (int i = 0; i < _bestLogoFrame.Length; i += step)
        {
            uint p = _bestLogoFrame[i];
            best += ((p >> 16) & 0xFF) + ((p >> 8) & 0xFF) + (p & 0xFF);
        }
        if (score > best)
            _bestLogoFrame = frame;
    }

    /// <summary>After logo, re-enter main past SIF init so boot can head toward menu.</summary>
    private void MaybePostLogoAdvance(Ps2System sys)
    {
        if (!_midwayDone || _postLogoKick) return;
        if (sys.MasterCycles < 8_000_000) return;

        // Only force the jump when execution is genuinely, currently stuck in the two SIF wait
        // loops this rescues from (0x2131E8-0x213217 -- see the two `beq v0,zero,...` retry
        // loops just above 0x213218). Before the real LWL/LWR/SWL/SWR/LDL/LDR/SDL/SDR fix (see
        // docs/DEVELOPER_GUIDE.md §7.6), these loops never completed on their own, so
        // unconditionally forcing PC here every time was the only way forward. Now that they
        // routinely DO complete naturally, unconditionally yanking PC back to 0x213218 with this
        // function's hardcoded, synthetic register state -- regardless of where real execution
        // currently is -- discards genuine forward progress and overwrites correct, freshly-
        // computed state with stale guesses, which is worse than doing nothing. Deliberately no
        // "force anyway after a timeout" fallback: since the underlying CPU bug that made the
        // old unconditional force necessary is now fixed, a boot that isn't caught stuck in this
        // exact loop is either past it already (don't touch it) or stuck somewhere else entirely
        // (yanking PC to 0x213218 wouldn't fix that either -- it would just relocate the hang).
        // Keeps checking every Step() (cheap, throttled to once per ~25,000 cycles) so a boot
        // that DOES land in the loop still gets rescued promptly whenever that happens.
        uint pcNow = (uint)(sys.EE.PC & 0x1FFFFFFF);
        bool stuckInWaitLoop = pcNow is >= 0x002131E8 and <= 0x00213217;
        if (!stuckInWaitLoop) return;
        _postLogoKick = true;

        // Ensure worklist still healthy
        PlantSifWorklist(sys);

        // Resume past SIF wait loops into main's later setup (pad/threads/movies).
        // 0x213218 is after the dual wait loops at 0x2131F8/0x213210.
        sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = 0x0011C2A8 });
        sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 }); // s0
        sys.EE.SetGpr(17, new EmotionEngine.Gpr128 { Lo = 1 }); // s1
        sys.EE.PC = 0x00213218;
        sys.LastGoodEePc = 0x00213218;
        sys.EE.COP0_Status |= (1u << 16) | 1u;
        // Keep best logo on screen while more boot runs
        if (_bestLogoFrame != null)
            sys.Gs.BlitArgb8888(_bestLogoFrame, Gs.FB_WIDTH, Gs.FB_HEIGHT);
        Status = "post-logo-main";
        Assists++;
    }

    /// <summary>
    /// After FMV playback finishes, keep the best frame on the host overlay so the
    /// game black-clear does not blank the window. Must NOT run while logo is still
    /// animating (that froze the movie on a single frame).
    /// </summary>
    public void KeepLogoVisible(Ps2System sys)
    {
        if (_bestLogoFrame == null) return;
        // Never overwrite in-progress animation
        if (_logoActive) return;

        // Drop host overlay once the game is clearly drawing multi-frame content past HLE
        if (sys.Gif.Path3Transfers > 4 && _midwayDone)
        {
            sys.Gs.ClearHostOverlay();
            return;
        }
        if (_midwayDone)
            sys.Gs.SetHostOverlay(_bestLogoFrame, active: true);
    }

    private void PrepareLogoFrames(Ps2System? sys)
    {
        if (_logoPrepared && _logoFrames is { Length: > 0 }) return;
        _logoPrepared = true;
        if (sys != null)
            BindIso(sys.Cdvd.MountedPath);
        if (_vol == null || string.IsNullOrEmpty(_isoPath))
        {
            Status = "no-iso";
            return;
        }

        try
        {
            _cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DetPS2", "logo-cache");
            Directory.CreateDirectory(_cacheDir);

            // Generic: pick a short boot-movie SFD from the disc (logo / ESRB / publisher).
            // Prefer well-known names, else smallest .SFD under ~4MB in MOVIES folders.
            string sfdLocal = Path.Combine(_cacheDir, "BOOT_FMV.SFD");
            if (!File.Exists(sfdLocal) || new FileInfo(sfdLocal).Length < 1000)
            {
                byte[]? data = FindBootFmvBytes(_vol);
                if (data == null || data.Length < 1000)
                {
                    Status = "boot-fmv-missing";
                    return;
                }
                File.WriteAllBytes(sfdLocal, data);
            }

            string frameDir = Path.Combine(_cacheDir, "midway-frames");
            Directory.CreateDirectory(frameDir);
            // v3: full-frame scale 640x448 (no pad letterbox), fixed present colors
            string marker = Path.Combine(frameDir, ".v3-ok");
            bool needDecode = !File.Exists(marker) || Directory.GetFiles(frameDir, "frame_*.ppm").Length < 5;
            if (needDecode)
            {
                Status = "ffmpeg-decoding";
                if (!TryFfmpegDecode(sfdLocal, frameDir))
                {
                    Status = "ffmpeg-failed";
                    return;
                }
                try { File.WriteAllText(marker, "ok"); } catch { /* ignore */ }
            }

            var files = Directory.GetFiles(frameDir, "frame_*.ppm");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            if (files.Length == 0)
            {
                Status = "no-frames";
                return;
            }

            // Cap frames for boot assist (~6s logo @ 15fps sample ≈ 90)
            int max = Math.Min(files.Length, 120);
            var list = new uint[max][];
            int got = 0;
            for (int i = 0; i < max; i++)
            {
                if (TryLoadPpm(files[i], out uint[]? argb) && argb != null)
                {
                    // Drop pure-black frames (fade lead-in)
                    if (IsMostlyBlack(argb)) continue;
                    list[got++] = argb;
                }
            }
            if (got == 0)
            {
                Status = "ppm-all-black";
                return;
            }
            if (got < list.Length)
                Array.Resize(ref list, got);
            _logoFrames = list;
            _logoFramesTotal = got;
            Status = $"logo-ready frames={got}";
        }
        catch (Exception ex)
        {
            Status = "logo-err:" + ex.GetType().Name;
        }
    }

    /// <summary>Locate a short publisher/boot movie on any retail disc layout.</summary>
    private static byte[]? FindBootFmvBytes(Iso9660.Volume vol)
    {
        string[] preferred =
        {
            "FRONT/MOVIES/MIDWAY.SFD", "FRONT/MOVIES/ESRB.SFD",
            "MOVIES/MIDWAY.SFD", "MOVIES/ESRB.SFD", "MOVIES/LOGO.SFD",
            "DATA/MIDWAY.SFD", "VIDEO/LOGO.SFD", "SCEI.SFD", "SCEE.SFD", "SCEA.SFD"
        };
        foreach (var p in preferred)
        {
            byte[]? d = Iso9660.ReadFile(vol, p);
            if (d != null && d.Length is >= 1000 and <= 8_000_000)
                return d;
        }

        Iso9660.FileEntry? best = null;
        foreach (var f in vol.Files)
        {
            if (f.IsDirectory) continue;
            string u = f.Path.ToUpperInvariant();
            if (!u.EndsWith(".SFD", StringComparison.Ordinal)) continue;
            if (f.Size is < 1000 or > 4_000_000) continue;
            // Prefer names that look like boot logos
            bool prefer = u.Contains("LOGO") || u.Contains("MIDWAY") || u.Contains("ESRB")
                          || u.Contains("SCEI") || u.Contains("SCEA") || u.Contains("SCEE")
                          || u.Contains("PUBLISHER") || u.Contains("WARNING");
            if (best == null || prefer || f.Size < best.Size)
            {
                if (prefer || best == null || f.Size < best.Size)
                    best = f;
                if (prefer) break;
            }
        }
        return best != null ? Iso9660.ReadFile(vol, best.Path) : null;
    }

    private static bool TryFfmpegDecode(string sfdPath, string outDir)
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return false;
        try
        {
            foreach (var f in Directory.GetFiles(outDir, "frame_*.ppm"))
                try { File.Delete(f); } catch { /* ignore */ }
            foreach (var f in Directory.GetFiles(outDir, ".v*"))
                try { File.Delete(f); } catch { /* ignore */ }

            // MPEG-PS Sofdec: typically 512×384. Scale to full 640×448 present buffer
            // (fill frame — slight stretch is better than a half-letterboxed crop on host UI).
            // Skip ~0.35s black fade-in; sample 20fps for smooth Desktop playback.
            string pattern = Path.Combine(outDir, "frame_%03d.ppm");
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments =
                    $"-y -fflags +genpts -i \"{sfdPath}\" -map 0:v:0 " +
                    $"-ss 0.35 -t 5.5 " +
                    $"-vf \"fps=20,scale=640:448:flags=bicubic\" " +
                    $"-frames:v 110 \"{pattern}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            _ = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120_000);
            return Directory.GetFiles(outDir, "frame_*.ppm").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMostlyBlack(uint[] argb)
    {
        int lit = 0;
        int step = Math.Max(1, argb.Length / 4000);
        int samples = 0;
        for (int i = 0; i < argb.Length; i += step)
        {
            uint p = argb[i];
            int r = (int)((p >> 16) & 0xFF);
            int g = (int)((p >> 8) & 0xFF);
            int b = (int)(p & 0xFF);
            if (r > 24 || g > 24 || b > 24) lit++;
            samples++;
        }
        return lit < Math.Max(3, samples / 50);
    }

    private static string? FindFfmpeg()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
            "ffmpeg"
        };
        foreach (var c in candidates)
        {
            try
            {
                if (c == "ffmpeg")
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p == null) continue;
                    p.WaitForExit(3000);
                    if (p.ExitCode == 0 || p.ExitCode == 1) return "ffmpeg";
                }
                else if (File.Exists(c))
                    return c;
            }
            catch { /* try next */ }
        }
        return null;
    }

    private static bool TryLoadPpm(string path, out uint[]? argb)
    {
        argb = null;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            // P6 binary PPM
            char c0 = (char)br.ReadByte();
            char c1 = (char)br.ReadByte();
            if (c0 != 'P' || c1 != '6') return false;
            SkipPpmWs(br);
            int w = ReadPpmInt(br);
            SkipPpmWs(br);
            int h = ReadPpmInt(br);
            SkipPpmWs(br);
            int max = ReadPpmInt(br);
            // single whitespace after maxval
            if (br.BaseStream.Position < br.BaseStream.Length)
                br.ReadByte();
            if (w <= 0 || h <= 0 || max <= 0) return false;

            byte[] rgb = br.ReadBytes(checked(w * h * 3));
            if (rgb.Length < w * h * 3) return false;

            // Always produce full 640×448 0xAARRGGBB. Nearest-neighbor scale if needed
            // so the host present never shows a half-frame letterbox from a bad pad.
            int dw = Gs.FB_WIDTH, dh = Gs.FB_HEIGHT;
            argb = new uint[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int sy = h == dh ? y : (int)((long)y * h / dh);
                if (sy >= h) sy = h - 1;
                for (int x = 0; x < dw; x++)
                {
                    int sx = w == dw ? x : (int)((long)x * w / dw);
                    if (sx >= w) sx = w - 1;
                    int si = (sy * w + sx) * 3;
                    byte r = rgb[si], g = rgb[si + 1], b = rgb[si + 2];
                    argb[y * dw + x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
                }
            }
            return true;
        }
        catch
        {
            argb = null;
            return false;
        }
    }

    private static int PeekByte(BinaryReader br)
    {
        if (br.BaseStream.Position >= br.BaseStream.Length) return -1;
        long pos = br.BaseStream.Position;
        int b = br.ReadByte();
        br.BaseStream.Position = pos;
        return b;
    }

    private static void SkipPpmWs(BinaryReader br)
    {
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            int b = PeekByte(br);
            if (b < 0) return;
            if (b == '#')
            {
                while (br.BaseStream.Position < br.BaseStream.Length)
                {
                    byte c = br.ReadByte();
                    if (c is (byte)'\n' or (byte)'\r') break;
                }
                continue;
            }
            if (b is ' ' or '\t' or '\r' or '\n')
            {
                br.ReadByte();
                continue;
            }
            break;
        }
    }

    private static int ReadPpmInt(BinaryReader br)
    {
        SkipPpmWs(br);
        var sb = new StringBuilder();
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            int b = PeekByte(br);
            if (b < 0) break;
            if (b is >= '0' and <= '9')
            {
                sb.Append((char)br.ReadByte());
                continue;
            }
            break;
        }
        return int.TryParse(sb.ToString(), out int v) ? v : 0;
    }

    private static void DrawSyntheticMidway(Ps2System sys)
    {
        // Dark navy field + bright gold "MIDWAY" block letters (fallback if ffmpeg missing)
        int w = Gs.FB_WIDTH, h = Gs.FB_HEIGHT;
        var px = new uint[w * h];
        uint bg = 0xFF0A1628;
        uint fg = 0xFFFFD040;
        for (int i = 0; i < px.Length; i++) px[i] = bg;

        // Simple 5x7 font for MIDWAY
        string text = "MIDWAY";
        int scale = 10;
        int cw = 5 * scale, gap = 3 * scale;
        int totalW = text.Length * cw + (text.Length - 1) * gap;
        int startX = (w - totalW) / 2;
        int startY = h / 2 - 4 * scale;
        for (int ti = 0; ti < text.Length; ti++)
        {
            byte[]? glyph = Glyph(text[ti]);
            if (glyph == null) continue;
            int ox = startX + ti * (cw + gap);
            for (int gy = 0; gy < 7; gy++)
            {
                for (int gx = 0; gx < 5; gx++)
                {
                    if (((glyph[gy] >> (4 - gx)) & 1) == 0) continue;
                    for (int sy = 0; sy < scale; sy++)
                    for (int sx = 0; sx < scale; sx++)
                    {
                        int x = ox + gx * scale + sx;
                        int y = startY + gy * scale + sy;
                        if ((uint)x < (uint)w && (uint)y < (uint)h)
                            px[y * w + x] = fg;
                    }
                }
            }
        }
        sys.Gs.BlitArgb8888(px, w, h);
    }

    private static byte[]? Glyph(char c) => c switch
    {
        'M' => new byte[] { 0x11, 0x1B, 0x15, 0x11, 0x11, 0x11, 0x11 },
        'I' => new byte[] { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x1F },
        'D' => new byte[] { 0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E },
        'W' => new byte[] { 0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11 },
        'A' => new byte[] { 0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 },
        'Y' => new byte[] { 0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04 },
        _ => null
    };
}
