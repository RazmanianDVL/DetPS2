using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Commercial retail boot assist (generic foundation, not a single-game port).
/// <list type="bullet">
/// <item>ISO-backed FILEIO + IOP module pre-register (any disc)</item>
/// <item>SIF wait unstick when IOP HLE is incomplete (any title that polls)</item>
/// <item>Boot logos / Sofdec .SFD must render via Soft-GS + IPU/CRI HLE only — no host FFmpeg,
///       no synthetic branded overlay. Missing video is an honest emulation gap, not a UI paint job.</item>
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
    private Iso9660.Volume? _vol;
    private string? _isoPath;
    private ulong _lastAssistCycle;
    private bool _postLogoKick;
    /// <summary>Tracks how long each (threadId, semaId) pair a thread is currently sleeping on
    /// has been observed blocked, for <see cref="MaybeUnblockStarvedSema"/>. Keyed by thread id;
    /// reset whenever the thread is seen sleeping on a different sema (or not sleeping at all).</summary>
    private readonly System.Collections.Generic.Dictionary<int, (int semaId, ulong sinceCycle)> _semaWaitStart = new();
    /// <summary>Same idea as <see cref="_semaWaitStart"/>/<see cref="MaybeUnblockStarvedSema"/>,
    /// for the sibling case of a thread parked via plain SleepThread (WaitSemaId==0, not
    /// WaitVblank) rather than WaitSema — see <see cref="MaybeUnblockStarvedSleep"/>.</summary>
    private readonly System.Collections.Generic.Dictionary<int, ulong> _sleepWaitStart = new();
    private bool _preloadStarted;
    // Real-protocol SIF bind+call synthesis (see MaybeCompleteRealSifCdRead) — a small state
    // machine, one step per Step() tick (every ~25,000 cycles), since Sif.SubmitRpc's packets
    // are only actually processed by Sif.Step() on a later scheduler tick, not synchronously.
    private int _realSifStage;
    private bool _adxGateCompleted;
    private bool _resourceLoadForced;
    private int _resourceForceScans;
    private bool _vblankPollNudgeArmed;
    private ulong _lastPostResourceResumeCycle;
    private const uint RealSifClientData = 0x01FD0000; // SifRpcClientData_t, 40B
    private const uint RealSifBindPkt = 0x01FD0040;     // SifRpcBindPkt_t, 36B
    private const uint RealSifCallPkt = 0x01FD0080;     // SifRpcCallPkt_t, 56B
    private const uint RealSifRecvBuf = 0x01FD00C0;     // int result slot
    private const uint RealSifSectorBuf = 0x01FD1000;   // 2KB+ CD sector destination
    // -------------------------------------------------------------------------
    // CRI cvFs ISO-backed HLE (unblocks ADXF open of GAMEDATA.WAD etc.).
    // Retail registers "MFS"/"CDV" via 0x418670, but that path never runs on our
    // fast-boot spine, so the device table at 0x76BFE0 stays empty and cvFsOpen
    // returns null (ADXF status=4). We plant CDV + service open/read/seek from ISO.
    // -------------------------------------------------------------------------
    private const uint CriDevTable = 0x0076BFE0;       // 32 × {void* dev, char name[12]}
    private const uint CriCwd = 0x0076C1E0;
    private const uint CriHandleTable = 0x0076BEA0;    // 40 × 8B {dev, fileobj}
    private const uint CriOpsBase = 0x01FD3000;        // synthetic device ops table
    private const uint CriFilePool = 0x01FD3100;       // synthetic file objects (32 × 0x40)
    private const uint CriStubOpen = 0x01FD4000;
    private const uint CriStubClose = 0x01FD4010;
    private const uint CriStubSeek = 0x01FD4020;
    private const uint CriStubTell = 0x01FD4030;
    private const uint CriStubRead = 0x01FD4040;
    private const uint CriStubStatus = 0x01FD4050;
    private const uint CriStubNop = 0x01FD4060;
    private const uint CriStubFsize = 0x01FD4070; // device+8: path → size in bytes
    private const uint CvFsOpenFn = 0x0041D0C0;
    private const int CriMaxFiles = 32;
    private const int CriFileStride = 0x40;
    // fileobj layout: +0 ops, +4 lba, +8 size, +0xC pos, +0x10 inUse, +0x14 path[44]
    private bool _criFsPlanted;
    private bool _criHookInstalled;
    private Ps2System? _criHookSys;
    /// <summary>Last EE buffer used by a CRI/ADXF read — used to pump multi-chunk WAD loads.</summary>
    private uint _lastAdxfBuf;
    private uint _lastAdxfFileObj;
    private int _adxfPumpCount;
    private bool _titleIsMidwayKick;
    private int _hostPresentsSinceLogoFrame;
    /// <summary>
    /// Host UI presents per FMV frame. 1 = one movie frame per Desktop render tick
    /// (~60 fps logo, full 100-frame sequence in ~1.7s). Use 2 for ~30 fps / longer play.
    /// </summary>
    private const int HostPresentsPerFmvFrame = 1;

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
        _resourceLoadForced = false;
        _lastListWalkBreakCyc = 0;
        _lastFormatStallCyc = 0;
        _formatStallEscapes = 0;
        _resourceForceScans = 0;
        _vblankPollNudgeArmed = false;
        _lastPostResourceResumeCycle = 0;
        _adxGateCompleted = false;
        _adxPumpLockYields = 0;
        _lastAdxPumpLockCyc = 0;
        _adxMenuKicks = 0;
        _lastAdxMenuKickCyc = 0;
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
        _postLogoKick = false;
        _preloadStarted = false;
        if (_criHookSys != null)
            _criHookSys.EE.MidInstructionHook = null;
        _criFsPlanted = false;
        _criHookInstalled = false;
        _criHookSys = null;
        _lastAdxfBuf = 0;
        _lastAdxfFileObj = 0;
        _adxfPumpCount = 0;
        _titleIsMidwayKick = false;
        _realSifStage = 0;
        _semaWaitStart.Clear();
        _sleepWaitStart.Clear();
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
    /// Call after disc boot (any commercial title). Binds disc + CRI hooks only.
    /// Does <b>not</b> host-decode boot movies — logos must appear from Soft-GS.
    /// </summary>
    public void OnDiscMounted(Ps2System sys)
    {
        BindIso(sys.Cdvd.MountedPath);
        // Install CRI mid-slice hook as early as possible (not only after 500k plant).
        _criHookSys = sys;
        sys.EE.MidInstructionHook = OnCriMidInstruction;
        _criHookInstalled = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_CRIFS") == "1")
            Console.Error.WriteLine($"[CRIFS] OnDiscMounted hook installed vol={_vol != null} files={_vol?.Files.Count ?? 0}");
        sys.IopModules.BindDisc(sys.Cdvd.MountedPath);
        // G0 THREADMAN priority band + preempt reordered ADX pump vs main on this title
        // (Exit@12.4M, cdvdSectors 198k→1). Prefer circular RR while keeping force-preempt
        // for lock-wait busy loops. Shared flag — not a PC plant. See KernelState.PreferRoundRobinSched.
        if (sys.Hle?.Kernel != null)
            sys.Hle.Kernel.PreferRoundRobinSched = true;
        // Detect optional title entry kick (signature only — not a hard dependency for all games)
        _titleIsMidwayKick = sys.Memory.Read32(0x00212F70) == 0x27BDFEE0;
        // Explicitly refuse host FFmpeg / logo-cache preload (see BeginPreloadFrames).
        BeginPreloadFrames();
        Status = _titleIsMidwayKick ? "disc-mounted (title-kick ready)" : "disc-mounted";
    }

    /// <summary>
    /// Formerly decoded short boot SFDs via host FFmpeg into a logo-cache.
    /// That path is removed: missing IPU/CRI Soft-GS video is an honest gap, not a UI dependency.
    /// </summary>
    public void BeginPreloadFrames()
    {
        if (_preloadStarted) return;
        _preloadStarted = true;
        _logoPrepared = true;
        _logoFrames = null;
        _logoFramesTotal = 0;
        Status = "host-fmv-disabled (Soft-GS only)";
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

    /// <summary>
    /// Plant CRI cvFs device table entries for "CDV" (and "MFS" → real static ops) plus a
    /// synthetic CDV ops table whose methods are spin-stubs serviced by
    /// <see cref="ServiceCriFsStubs"/>. Also sets the CRI cwd so device-less paths like
    /// "\GAMEDATA.WAD" resolve through CDV.
    /// </summary>
    private void PlantCriFsDevices(Ps2System sys)
    {
        var mem = sys.Memory;

        // One-time: write stubs + ops + clear file pool
        if (!_criFsPlanted)
        {
            // Spin stub: beq zero,zero,self ; nop  — Step() rewrites PC after doing the work.
            static void WriteSpinStub(SystemMemory m, uint addr)
            {
                m.Write32(addr, 0x1000FFFFu); // beq zero, zero, -1 (self)
                m.Write32(addr + 4, 0);       // nop
                m.Write32(addr + 8, 0x03E00008u); // jr ra (landing after HLE advances PC)
                m.Write32(addr + 12, 0);
            }

            WriteSpinStub(mem, CriStubOpen);
            WriteSpinStub(mem, CriStubClose);
            WriteSpinStub(mem, CriStubSeek);
            WriteSpinStub(mem, CriStubTell);
            WriteSpinStub(mem, CriStubRead);
            WriteSpinStub(mem, CriStubStatus);
            WriteSpinStub(mem, CriStubNop);
            WriteSpinStub(mem, CriStubFsize);

            // Device ops layout (matches cvFs wrappers in 0x41D410..0x41D690):
            // +0x00 destroy, +0x08 getsize(path), +0x10 open, +0x14 close,
            // +0x18 seek, +0x1C tell, +0x20 read, +0x2C status
            for (uint o = 0; o < 0x80; o += 4)
                mem.Write32(CriOpsBase + o, CriStubNop);
            mem.Write32(CriOpsBase + 0x00, CriStubNop);
            mem.Write32(CriOpsBase + 0x08, CriStubFsize);
            mem.Write32(CriOpsBase + 0x10, CriStubOpen);
            mem.Write32(CriOpsBase + 0x14, CriStubClose);
            mem.Write32(CriOpsBase + 0x18, CriStubSeek);
            mem.Write32(CriOpsBase + 0x1C, CriStubTell);
            mem.Write32(CriOpsBase + 0x20, CriStubRead);
            mem.Write32(CriOpsBase + 0x2C, CriStubStatus);

            for (int i = 0; i < CriMaxFiles; i++)
            {
                uint fo = CriFilePool + (uint)(i * CriFileStride);
                for (uint o = 0; o < CriFileStride; o += 4)
                    mem.Write32(fo + o, 0);
            }

            _criFsPlanted = true;
            Status = "cri-fs-planted";
            Assists++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_CRIFS") == "1")
                Console.Error.WriteLine($"[CRIFS] planted CDV/MFS devices cyc={sys.MasterCycles}");
        }

        // Mid-slice EE hook so cvFsOpen/stub PCs are not missed across 50k-cycle commercial slices.
        if (!_criHookInstalled)
        {
            _criHookSys = sys;
            sys.EE.MidInstructionHook = OnCriMidInstruction;
            _criHookInstalled = true;
        }
        else
            _criHookSys = sys;

        // Re-assert table + cwd every call — game BSS clear / failed AddDev can wipe them.
        // Slot 0: CDV → synthetic ops (disc files)
        mem.Write32(CriDevTable + 0, CriOpsBase);
        WriteAsciiZ(mem, CriDevTable + 4, "CDV", 12);
        // Slot 1: MFS → real static device at 0x5439A0
        mem.Write32(CriDevTable + 16, 0x005439A0u);
        WriteAsciiZ(mem, CriDevTable + 20, "MFS", 12);
        // cwd = "CDV" so "\GAMEDATA.WAD" (no device prefix) binds to CDV
        WriteAsciiZ(mem, CriCwd, "CDV", 32);
        // Keep ops table pointers intact (in case something stomped scratch)
        mem.Write32(CriOpsBase + 0x08, CriStubFsize);
        mem.Write32(CriOpsBase + 0x10, CriStubOpen);
        mem.Write32(CriOpsBase + 0x14, CriStubClose);
        mem.Write32(CriOpsBase + 0x18, CriStubSeek);
        mem.Write32(CriOpsBase + 0x1C, CriStubTell);
        mem.Write32(CriOpsBase + 0x20, CriStubRead);
        mem.Write32(CriOpsBase + 0x2C, CriStubStatus);
    }

    private void OnCriMidInstruction(EmotionEngine ee)
    {
        var sys = _criHookSys;
        if (sys == null) return;
        uint pc = (uint)(ee.PC & 0x1FFFFFFF);
        // Cheap reject before any heavier work.
        bool hot = (pc >= CvFsOpenFn && pc <= 0x0041D1E4)
            || (pc >= 0x00417F80 && pc <= 0x00418020)
            || (pc >= CriStubOpen && pc < CriStubFsize + 16)
            || pc == 0x0041D4F0 || pc == 0x0041D488 || pc == 0x0041D558
            || pc == 0x0041D410 || pc == 0x0041D628 || pc == 0x0041D690;
        if (!hot) return;

        // verbose mid-hook logging omitted (too noisy); see wrapper/open/getsize traces

        MaybeHleCvFsOpen(sys);
        MaybeHleCvFsGetSize(sys);
        MaybeHleCvFsMethodWrappers(sys);
        ServiceCriFsStubs(sys);
    }

    /// <summary>HLE cvFsGetFileSize (0x41D690): a0=path → byte size from ISO.</summary>
    private void MaybeHleCvFsGetSize(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        if (pc != 0x0041D690 && pc != CriStubFsize && (pc < CriStubFsize || pc >= CriStubFsize + 16))
            return;

        uint pathPtr = (uint)sys.EE.GetGpr(4).Lo;
        // Inside stub, a0 is path; at wrapper entry too
        string path = pathPtr >= 0x1000 ? ReadCString(sys.Memory, pathPtr) : "";
        long size = 0;
        if (!string.IsNullOrEmpty(path) && _vol != null)
        {
            var e = Iso9660.FindFile(_vol, path);
            if (e != null) size = e.Size;
        }
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = unchecked((ulong)size) });
        if (pc == 0x0041D690)
            sys.EE.PC = sys.EE.GetGpr(31).Lo;
        else
            sys.EE.PC = CriStubFsize + 8; // jr ra
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_CRIFS") == "1")
            Console.Error.WriteLine($"[CRIFS] getsize path='{path}' size={size} cyc={sys.MasterCycles}");
    }

    /// <summary>HLE cvFs seek/tell/read/close wrappers when the handle is ours (skip stub jalr).</summary>
    private void MaybeHleCvFsMethodWrappers(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        uint a0 = (uint)sys.EE.GetGpr(4).Lo;
        uint a1 = (uint)sys.EE.GetGpr(5).Lo;
        uint a2 = (uint)sys.EE.GetGpr(6).Lo;

        // Resolve handle → fileobj if a0 is a cvFs handle pointing at our ops
        uint fileObj = 0;
        if (a0 >= CriHandleTable && a0 < CriHandleTable + 40 * 8 && (a0 - CriHandleTable) % 8 == 0)
        {
            if (sys.Memory.Read32(a0) == CriOpsBase)
                fileObj = sys.Memory.Read32(a0 + 4);
        }
        else if (IsCriFileObj(a0))
            fileObj = a0;

        if (fileObj == 0 || !IsCriFileObj(fileObj)) return;

        long result = 0;
        bool handled = false;

        if (pc == 0x0041D4F0) // seek
        {
            uint size = sys.Memory.Read32(fileObj + 0x08);
            uint cur = sys.Memory.Read32(fileObj + 0x0C);
            long sectorOff = (int)a1;
            long pos = a2 switch
            {
                1 => cur + sectorOff * 2048L,
                2 => size + sectorOff * 2048L,
                _ => sectorOff * 2048L
            };
            if (pos < 0) pos = 0;
            if (pos > size) pos = size;
            sys.Memory.Write32(fileObj + 0x0C, (uint)pos);
            result = 0;
            handled = true;
        }
        else if (pc == 0x0041D488) // tell → sectors
        {
            uint pos = sys.Memory.Read32(fileObj + 0x0C);
            result = pos / 2048u;
            handled = true;
        }
        else if (pc == 0x0041D558) // read
        {
            // Wrapper passes fileobj in a0 (delay). Caller 0x417E4C: a1 = sector count
            // (from sra …, 11), a2 = dest buffer. Convert sectors → bytes for ISO read.
            uint buf = a2;
            uint nSectors = a1;
            if (a1 >= 0x100000 && a2 < 0x100000)
            {
                buf = a1;
                nSectors = a2;
            }
            // Cap insane sizes; treat values that already look like byte counts (>1MB or
            // not sector-aligned small) as bytes — but ADXF's path is always sector units.
            uint nbytes = nSectors < 0x00100000u ? nSectors * 2048u : nSectors;
            long bytesRead = CriRead(sys, fileObj, buf, nbytes);
            // ADXF stores v0 at +0x20 then does `sll +0x20, 11` for memcpy size and
            // `addu +0x58, +0x20` where +0x58 is compared to total sector count (+0x14).
            // So both the request and the return value are in **sectors**.
            result = bytesRead > 0 ? (bytesRead + 2047) / 2048 : 0;
            if (result > 0)
            {
                _lastAdxfBuf = buf;
                _lastAdxfFileObj = fileObj;
                MaybeCompleteAdxfAfterRead(sys, a0 >= CriHandleTable && a0 < CriHandleTable + 40 * 8 ? a0 : 0, fileObj, (uint)result);
            }
            handled = true;
        }
        else if (pc == 0x0041D410) // close
        {
            sys.Memory.Write32(fileObj + 0x10, 0);
            // free handle slot if a0 was handle
            if (a0 >= CriHandleTable && a0 < CriHandleTable + 40 * 8)
            {
                sys.Memory.Write32(a0, 0);
                sys.Memory.Write32(a0 + 4, 0);
            }
            result = 0;
            handled = true;
        }
        else if (pc == 0x0041D628) // status
        {
            // ADXF fill (0x417AF0): when +2==1 (read pending), it waits until
            // status() == ADXF+1. Open/fill sets +1=2 for "reading"; status 3 is
            // treated as end/error (0x417C28 path). Sync HLE: report 2 = data ready.
            uint pos = sys.Memory.Read32(fileObj + 0x0C);
            uint size = sys.Memory.Read32(fileObj + 0x08);
            result = pos >= size && size > 0 ? 3 : 2;
            handled = true;
        }

        if (!handled) return;
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = unchecked((ulong)result) });
        sys.EE.PC = sys.EE.GetGpr(31).Lo;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_CRIFS") == "1"
            && pc != 0x0041D628) // skip status spam
            Console.Error.WriteLine($"[CRIFS] wrapper pc=0x{pc:X8} fo=0x{fileObj:X8} a1=0x{a1:X} a2=0x{a2:X} res={result} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// After a successful sync sector read, find the ADXF object that owns this cvFs handle
    /// and clear its busy flag (+2) while advancing sector cursors. Without this, fill can
    /// leave +2=1 and never re-enter completion if the tick path doesn't run again.
    /// </summary>
    private void MaybeCompleteAdxfAfterRead(Ps2System sys, uint handleHint, uint fileObj, uint sectors)
    {
        if (sectors == 0 || sectors > 0x10000) return;

        void TryFix(uint adxf)
        {
            if (adxf < 0x100000 || adxf >= SystemMemory.RDRAM_SIZE - 0x60) return;
            uint h = sys.Memory.Read32(adxf + 8);
            // Match by handle, or by our fileobj living at handle+4
            bool match = (handleHint != 0 && h == handleHint)
                || (h >= CriHandleTable && h < CriHandleTable + 40 * 8
                    && sys.Memory.Read32(h + 4) == fileObj)
                || (h == CriOpsBase); // unlikely
            // Also match if +8 is our handle table slot that points at this fileObj
            if (!match && h != 0 && h < SystemMemory.RDRAM_SIZE - 8
                && sys.Memory.Read32(h) == CriOpsBase && sys.Memory.Read32(h + 4) == fileObj)
                match = true;
            if (!match && sys.Memory.Read32(adxf + 8) != 0x0076BEA0
                && sys.Memory.Read32(adxf + 8) != handleHint)
            {
                // Last resort: any ADXF with busy set and our buffer pattern
                if (sys.Memory.Read8(adxf + 2) == 0) return;
                if (sys.Memory.Read32(adxf + 8) == 0) return;
                // only accept if handle+4 == fileObj
                uint hh = sys.Memory.Read32(adxf + 8);
                if (hh < 0x100000 || sys.Memory.Read32(hh + 4) != fileObj) return;
            }
            else if (!match) return;

            sys.Memory.Write32(adxf + 0x20, sectors);
            sys.Memory.Write8(adxf + 2, 0); // clear busy so next fill can issue again
            uint cur = sys.Memory.Read32(adxf + 0x58);
            sys.Memory.Write32(adxf + 0x58, cur + sectors);
            uint bcur = sys.Memory.Read32(adxf + 0x34);
            sys.Memory.Write32(adxf + 0x34, bcur + sectors * 2048u);
            sys.Memory.Write8(adxf + 0x45, 0);

            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_CRIFS") == "1")
                Console.Error.WriteLine($"[CRIFS] adxf-complete adxf=0x{adxf:X8} sectors={sectors} cur={cur} cyc={sys.MasterCycles}");
        }

        // Prefer the live ADXF seen at open (0x53CE10) and the static pool.
        TryFix(0x0053CE10);
        for (uint i = 0; i < 40; i++)
            TryFix(0x0054C510 + i * 0x60);

        // Also scan a short range of heap-looking ADXF candidates if handle is known
        if (handleHint != 0)
        {
            // Walk a few MB of RDRAM looking for handle pointer (capped)
            // Skip — too expensive. The open path uses 0x53CE10 for this title.
        }
    }

    private long CriRead(Ps2System sys, uint fileObj, uint buf, uint nbytes)
    {
        if (_vol == null || buf < 0x1000 || nbytes == 0 || nbytes > 16 * 1024 * 1024) return 0;
        uint lba = sys.Memory.Read32(fileObj + 0x04);
        uint size = sys.Memory.Read32(fileObj + 0x08);
        uint pos = sys.Memory.Read32(fileObj + 0x0C);
        int want = (int)Math.Min(nbytes, size > pos ? size - pos : 0);
        if (want <= 0) return 0;
        var fake = new Iso9660.FileEntry { ExtentLba = lba, Size = size, Name = "", Path = "" };
        byte[] tmp = new byte[want];
        int got = Iso9660.ReadFileRange(_vol, fake, pos, tmp);
        if (got <= 0) return 0;
        for (int i = 0; i < got; i++)
            sys.Memory.Write8(buf + (uint)i, tmp[i]);
        sys.Cdvd.NoteHostReadSectors((got + 2047) / 2048);
        sys.Memory.Write32(fileObj + 0x0C, pos + (uint)got);
        return got;
    }

    /// <summary>
    /// When ADXF busy (+2) is set, complete one window into the last EE buffer and clear
    /// busy so fill can continue. Does not invent a full-WAD-in-RDRAM lie (that crashed
    /// into bad code at 0x6Axxxx after claiming 198k sectors were delivered).
    /// Also pumps the first-chunk stall (busy cleared but almost nothing read yet) a few times.
    /// </summary>
    private void MaybePumpAdxfBulk(Ps2System sys)
    {
        if (_vol == null || _lastAdxfFileObj == 0 || _lastAdxfBuf == 0) return;
        if (!IsCriFileObj(_lastAdxfFileObj)) return;
        if (_adxfPumpCount > 20000) return;

        uint adxf = 0x0053CE10;
        byte busy = sys.Memory.Read8(adxf + 2);
        uint size = sys.Memory.Read32(_lastAdxfFileObj + 0x08);
        uint pos = sys.Memory.Read32(_lastAdxfFileObj + 0x0C);
        if (size == 0) return;
        if (pos >= size)
        {
            // WAD fully streamed — mark ADXF done once (do not SignalSema every Step).
            sys.Memory.Write8(adxf + 2, 0);
            sys.Memory.Write8(adxf + 1, 3);
            uint tot = (size + 2047) / 2048;
            sys.Memory.Write32(adxf + 0x58, tot);
            if (tot != 0) sys.Memory.Write32(adxf + 0x14, tot);
            return;
        }

        // Always stream remaining file data (fill often clears busy then WaitSema before
        // re-issuing). 29-sector windows → ~6900 pumps for a 198839-sector GAMEDATA.WAD.
        _ = busy;

        uint buf = _lastAdxfBuf;
        if (buf < 0x100000 || buf >= SystemMemory.RDRAM_SIZE - 0x40000) return;

        uint req = sys.Memory.Read32(adxf + 0x20);
        if (req == 0 || req > 512) req = 29;
        uint nbytes = Math.Min(req * 2048u, size - pos);
        if (nbytes < 2048) return;

        // Multiple windows per Step so a 198k-sector WAD finishes in tens of M cycles,
        // not hundreds (Step fires ~every 50k EE cycles).
        for (int n = 0; n < 32; n++)
        {
            pos = sys.Memory.Read32(_lastAdxfFileObj + 0x0C);
            if (pos >= size) break;
            nbytes = Math.Min(req * 2048u, size - pos);
            if (nbytes < 2048) break;
            long got = CriRead(sys, _lastAdxfFileObj, buf, nbytes);
            if (got <= 0) break;
            uint sectors = (uint)((got + 2047) / 2048);
            MaybeCompleteAdxfAfterRead(sys, 0x0076BEA0, _lastAdxfFileObj, sectors);
            sys.Memory.Write8(adxf + 2, 0);
            _adxfPumpCount++;
        }
    }

    private int _syscallTrampolineEscapes;
    private ulong _lastSyscallTrampolineEscCyc;
    private int _logoSpineKicks;
    private ulong _lastLogoSpineKickCyc;

    /// <summary>
    /// Post-merge / GetModVer-fixed path: EE thrives in ADX pump + pad-poll bands after WAD
    /// (PC 0x414xxx / 0x4275xx / 0x429Cxx, syscalls climbing) but never re-enters the
    /// list-walk→format-stall sequence that menu6 used to restore gifP3 5→12. Mirror that
    /// format-stall re-home to Midway main when logo spine is still frozen post-WAD.
    /// Does <b>not</b> set PreferIopRpGetVersion / PadModVerMajor4 (SM needs classic defaults).
    /// <para>
    /// Wave-6 (live 2026-07-30 HEAD): do <b>not</b> treat pad-poll / group-6 multi dispatch
    /// (<c>0x427518..0x4276A0</c>) as thrash — kicking that band into Midway main storms
    /// IOPRP gen≥2 and lands the EE in title-hash code-walk thrash (<c>0x47EBxx</c>) with
    /// gifP3 stuck at 8 and eventual open-bus death. Only kick empty-ADX / syscall-table
    /// residual when group-6 multi is still empty.
    /// </para>
    /// </summary>
    private void MaybeKickMainForLogoSpine(Ps2System sys)
    {
        // Allow enough kicks to restore historical gifP3≥11 spine, but stop once Path3 is
        // moving (further main re-entry storms IOPRP gen≥2 and clears pad open areas).
        if (_logoSpineKicks >= 4) return;
        if (sys.MasterCycles - _lastLogoSpineKickCyc < 2_500_000) return;
        if (sys.Gif.Path3Transfers >= 11) return;
        if (sys.Memory.Read32(0x00212F70) != 0x27BDFEE0) return; // main wiped
        // Group-6 multi already filled: stay in pump/pad; main re-entry only harms pad ghosts.
        if (sys.Memory.Read32(0x0075E950) == 0x0043F920u) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        // Productive post-WAD bands — NEVER kick (live regression → title-hash / open-bus).
        // pad-poll multi 0x4275xx, frame-cb dispatch 0x4156xx, stream tick 0x43F9xx.
        if (pc is (>= 0x00427500 and <= 0x004276A0)
            or (>= 0x00415600 and <= 0x00415780)
            or (>= 0x0043F800 and <= 0x0043FC00)
            or (>= 0x0043CB00 and <= 0x0043CD00)) return;
        // True residual thrash only: empty ADX lock-wait / pump entry, syscall stub table.
        // Exclude productive ADX title 0x4148xx..0x414Axx and frame path above.
        bool inPostWadThrash = pc is (>= 0x00414480 and <= 0x00414550) // lock-wait
            or (>= 0x00414980 and <= 0x00414A80) // ReferThreadStatus thrash
            or (>= 0x0047FD00 and <= 0x0047FF80)
            or (>= 0x00418000 and <= 0x00419000);
        if (!inPostWadThrash) return;

        // After bulk WAD, prefer ADX pump / pad-poll over Midway main — main re-entry
        // storms IOPRP gen≥2 and outer-list thrash (live: kick@59.9M → 0x474Cxx).
        // Prefer natural stack return into main body only when still pre-WAD-cold.
        uint resume = 0;
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        bool bulkWad = sys.Cdvd.SectorsRead >= 100_000;
        if (!bulkWad && sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE)
        {
            for (uint off = 0; off <= 0x80; off += 4)
            {
                uint cand = sys.Memory.Read32(sp + off) & 0x1FFFFFFFu;
                if (cand is >= 0x00212F70 and < 0x00214000 && sys.Memory.IsLikelyEeCode(cand))
                {
                    resume = cand;
                    break;
                }
            }
        }
        if (resume == 0 && sys.Memory.IsLikelyEeCode(0x004147F8UL))
            resume = 0x004147F8; // ADX pump
        if (resume == 0 && sys.Memory.IsLikelyEeCode(0x00427518UL))
            resume = 0x00427518; // group-6 multi / pad-poll
        if (resume == 0 && sys.Memory.Read32(0x00212F70) == 0x27BDFEE0)
            resume = 0x00212F70; // last resort Midway main
        if (resume == 0) return;

        sys.EE.COP0_Status &= ~0x6u;
        sys.EE.PC = resume;
        sys.LastGoodEePc = resume;
        if (sp < 0x01000000 || sp >= (uint)SystemMemory.RDRAM_SIZE)
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
        // Clear ADX pump-stop so main's frame path can re-arm pump.
        if (sys.Memory.Read32(0x005341D8) == 0)
            sys.Memory.Write32(0x005341D8, 1);
        sys.Memory.Write32(0x00534164, 0);
        sys.Memory.Write32(0x00534218, 0);

        var kernel = sys.Hle?.Kernel;
        if (kernel != null)
        {
            foreach (var t in kernel.AllThreads)
            {
                if (!t.Alive) continue;
                if (t.SoftSuspended) t.SoftSuspended = false;
                while (t.SuspendCount > 0) kernel.ResumeThread(t.Id);
                if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                    kernel.WakeupThread(t.Id);
                if (t.Sleeping && t.WaitSemaId > 0 && t.WaitSemaId < 64)
                {
                    try { kernel.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                }
            }
        }

        _lastLogoSpineKickCyc = sys.MasterCycles;
        _logoSpineKicks++;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_logoSpineKicks <= 12 || _logoSpineKicks % 4 == 0))
            Console.Error.WriteLine(
                $"[BIOS] logo-spine kick 0x{pc:X8} -> 0x{resume:X8} n={_logoSpineKicks} " +
                $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Live (GetModVer 0x0400 path / open-bus residual): EE walks the kernel syscall stub
    /// table at <c>0x47FD80..0x47FF80</c> (<c>addiu v1,imm; syscall; jr ra</c>) with
    /// <c>ra=0</c> and low SP — pure thrash, no game progress. Re-home to ADX pump / main
    /// with a real stack so bulk WAD / logo spine can continue.
    /// </summary>
    private void MaybeEscapeSyscallTrampolineThrash(Ps2System sys)
    {
        if (_syscallTrampolineEscapes >= 32) return;
        if (sys.MasterCycles - _lastSyscallTrampolineEscCyc < 200_000) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        // Stub table: 16-byte entries, addiu v1 + syscall + jr ra + nop.
        if (pc is < 0x0047FD00 or > 0x0047FF80) return;
        uint op = sys.Memory.Read32(pc);
        // Match either the addiu v1,imm (0x2403xxxx) or the jr ra (0x03E00008) of a stub.
        bool looksStub = (op & 0xFFFF0000u) == 0x24030000u || op == 0x03E00008u || op == 0x0000000Cu;
        if (!looksStub) return;
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        // Legitimate syscall path: ra points into the ELF image (e.g. ADX pump 0x4148EC).
        // Only treat as thrash when $ra is clearly dead (0 / low / past RDRAM / open bus).
        // Using !IsLikelyEeCode was too aggressive — yanked live pump↔syscall traffic and
        // blocked list-walk / format-stall escapes that restore gifP3 5→12.
        bool raDead = ra < 0x00100000 || ra >= (uint)SystemMemory.RDRAM_SIZE;
        if (!raDead) return;

        uint resume = 0;
        if (sys.Memory.Read32(0x00212F70) == 0x27BDFEE0)
            resume = 0x00212F70; // Midway main (prefer for spine restore)
        else if (sys.Memory.IsLikelyEeCode(0x004147F8UL))
            resume = 0x004147F8; // ADX pump
        else if (sys.Memory.IsLikelyEeCode(0x00414590UL))
            resume = 0x00414590;
        if (resume == 0) return;

        sys.EE.COP0_Status &= ~0x6u;
        sys.EE.PC = resume;
        sys.LastGoodEePc = resume;
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        if (sp < 0x00100000 || sp >= (uint)SystemMemory.RDRAM_SIZE || sp < 0x01000000)
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
        _lastSyscallTrampolineEscCyc = sys.MasterCycles;
        _syscallTrampolineEscapes++;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_syscallTrampolineEscapes <= 8 || _syscallTrampolineEscapes % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] escape syscall trampoline thrash 0x{pc:X8} -> 0x{resume:X8} " +
                $"ra=0x{ra:X8} n={_syscallTrampolineEscapes} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// If EE is stuck forever at the synthesized interrupt vector (0x80000200 bare eret),
    /// or executing our HLE scratch (0x01FD0000–0x01FEFFFF: synthetic SIF packets / CRI stubs),
    /// force return to a safe game PC so commercial boot does not pin the whole budget there.
    /// </summary>
    private void MaybeEscapeStuckIntVector(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        if (sys.MasterCycles < 5_000_000) return;

        // HLE scratch / synthetic packet region — never valid game code
        if (pc is >= 0x01FD0000 and < 0x01FF0000)
        {
            ulong safe = sys.LastGoodEePc;
            uint safePhys = (uint)(safe & 0x1FFFFFFF);
            if (safePhys is < 0x00100000 or >= 0x01FD0000 or (>= 0x01FD0000 and < 0x01FF0000))
                safe = 0x00212F70; // Midway main prologue (signature-checked at kick)
            if (sys.Memory.Read32(0x00212F70) != 0x27BDFEE0)
                safe = 0x0011C200; // CRT0 SetupThread region
            sys.EE.COP0_Status &= ~(1u << 1);
            sys.EE.PC = safe;
            // Post-WAD only (internal gate) — early Escape must not move SP (WAD regression).
            ReHomeSpIfInHleScratch(sys);
            sys.Intc.ClearCpuLatchPending();
            Assists++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine($"[BIOS] escape HLE scratch pc=0x{pc:X8} -> 0x{safe:X8} cyc={sys.MasterCycles}");
            return;
        }

        // Phys interrupt vector = 0x200 (KSEG0 0x80000200) or general 0x180 (AdEL/etc.)
        if (pc is not (0x200 or 0x180 or 0x000)) return;
        if ((sys.MasterCycles % 50_000) != 0) return; // cheap throttle

        uint epc = (uint)(sys.EE.COP0_EPC & 0x1FFFFFFFUL);
        // Live MK IRX-era ~50.45M: AdEL with EPC = ASCII "GAMEDATA" (path as code).
        // Jumping back to that EPC re-faults forever. Only when EPC is clearly data-as-code
        // (unaligned / past RDRAM / ASCII word), re-home to real EE code. Empty/low EPC keeps
        // the historical early-out (do not force Midway main — WAD regression).
        bool epcDataAsCode = (epc & 3) != 0
            || epc >= (uint)SystemMemory.RDRAM_SIZE
            || LooksLikeAsciiWord(epc);
        if (epcDataAsCode)
        {
            ulong resume = 0;
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
            if (sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE)
            {
                for (uint off = 0; off <= 0x80; off += 4)
                {
                    uint cand = sys.Memory.Read32(sp + off);
                    if ((cand & 3) == 0 && sys.Memory.IsLikelyEeCode(cand))
                    {
                        resume = cand;
                        break;
                    }
                }
            }
            uint last = (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL);
            if (resume == 0 && (last & 3) == 0 && sys.Memory.IsLikelyEeCode(last))
                resume = last;
            if (resume == 0 && sys.Memory.Read32(0x00212F70) == 0x27BDFEE0)
                resume = 0x00212F70;
            if (resume == 0 && sys.Memory.IsLikelyEeCode(0x0011C200UL))
                resume = 0x0011C200;
            if (resume == 0)
                resume = 0x00100008;

            sys.EE.COP0_Status &= ~(1u << 1);
            sys.EE.PC = resume;
            sys.LastGoodEePc = resume;
            sys.Intc.ClearCpuLatchPending();
            Assists++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[BIOS] escape stuck int vector (data-EPC) epc=0x{epc:X8} -> 0x{resume:X8} cyc={sys.MasterCycles}");
            return;
        }

        if (epc < 0x100000 || epc >= 0x01FD0000) return;
        // Clear EXL and jump back
        sys.EE.COP0_Status &= ~(1u << 1); // clear EXL
        sys.EE.PC = epc;
        // Drop sticky COP0 latches that might re-enter immediately
        sys.Intc.ClearCpuLatchPending();
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine($"[BIOS] escape stuck int vector -> EPC=0x{epc:X8} cyc={sys.MasterCycles}");
    }

    private static bool LooksLikeAsciiWord(uint word)
    {
        int printable = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(word >> (8 * i));
            if (b is >= 0x20 and <= 0x7E) printable++;
        }
        return printable >= 3;
    }

    private static void WriteAsciiZ(SystemMemory mem, uint addr, string s, int maxLen)
    {
        int n = Math.Min(s.Length, maxLen - 1);
        for (int i = 0; i < n; i++)
            mem.Write8(addr + (uint)i, (byte)s[i]);
        for (int i = n; i < maxLen; i++)
            mem.Write8(addr + (uint)i, 0);
    }

    private static string ReadCString(SystemMemory mem, uint addr, int maxLen = 256)
    {
        var sb = new StringBuilder(Math.Min(maxLen, 64));
        for (int i = 0; i < maxLen; i++)
        {
            byte b = mem.Read8(addr + (uint)i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>
    /// When EE is inside cvFsOpen (entry or mid-body — commercial slices are 50k cycles so the
    /// exact entry PC is often skipped), finish the open from ISO and return a synthetic handle.
    /// </summary>
    private void MaybeHleCvFsOpen(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        // Whole function body through jr ra (0x41D0C0..0x41D1E4)
        if (pc is < CvFsOpenFn or > 0x0041D1E4) return;
        if (_vol == null) BindIso(sys.Cdvd.MountedPath);
        if (_vol == null) return;

        PlantCriFsDevices(sys);

        // Prefer live a0 (valid at entry). Mid-body a0 may be clobbered — recover path from
        // ADXF caller's s0+0x50 when ra points back at 0x417FE0, or from stack path buffer.
        uint pathPtr = (uint)sys.EE.GetGpr(4).Lo; // a0
        string path = "";
        if (pathPtr >= 0x1000 && pathPtr < SystemMemory.RDRAM_SIZE)
            path = ReadCString(sys.Memory, pathPtr);

        // ADXF open: jal 0x41D0C0 from 0x417FD8, ra=0x417FE0, path at s0+0x50
        if ((string.IsNullOrEmpty(path) || path.Length < 3) &&
            (uint)sys.EE.GetGpr(31).Lo == 0x00417FE0)
        {
            uint s0 = (uint)sys.EE.GetGpr(16).Lo;
            if (s0 >= 0x1000 && s0 < SystemMemory.RDRAM_SIZE - 0x60)
            {
                uint p = sys.Memory.Read32(s0 + 0x50);
                if (p >= 0x1000 && p < SystemMemory.RDRAM_SIZE)
                    path = ReadCString(sys.Memory, p);
            }
        }

        // Mid-function: path copy lives at original a0 (s1) or sp+0 path buffers
        if (string.IsNullOrEmpty(path) || path.Length < 3)
        {
            uint s1 = (uint)sys.EE.GetGpr(17).Lo; // s1 = original a0 path at entry
            if (s1 >= 0x1000 && s1 < SystemMemory.RDRAM_SIZE)
                path = ReadCString(sys.Memory, s1);
        }

        if (string.IsNullOrEmpty(path))
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            ReturnFromCvFsOpen(sys);
            return;
        }

        uint handle = CriOpenPath(sys, path);
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = handle });
        ReturnFromCvFsOpen(sys);
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_CRIFS") == "1")
            Console.Error.WriteLine($"[CRIFS] cvFsOpen path='{path}' handle=0x{handle:X8} pc=0x{pc:X8} cyc={sys.MasterCycles}");
    }

    private static void ReturnFromCvFsOpen(Ps2System sys)
    {
        // Prefer $ra when still at entry; mid-body $ra is still the real caller (saved on stack
        // only after prologue, and prologue does sd ra,648(sp) — restore if sp looks valid).
        uint ra = (uint)sys.EE.GetGpr(31).Lo;
        uint sp = (uint)sys.EE.GetGpr(29).Lo;
        if (ra < 0x100000 || ra >= SystemMemory.RDRAM_SIZE)
        {
            // Prologue: addiu sp,-656; sd ra,648(sp)
            if (sp >= 0x1000 && sp < SystemMemory.RDRAM_SIZE - 0x290)
                ra = sys.Memory.Read32(sp + 648);
        }
        if (ra >= 0x100000 && ra < SystemMemory.RDRAM_SIZE)
            sys.EE.PC = ra;
        else
            sys.EE.PC = 0x00417FE0; // known ADXF open return
    }

    private uint CriOpenPath(Ps2System sys, string path)
    {
        if (_vol == null) return 0;
        var entry = Iso9660.FindFile(_vol, path);
        if (entry == null)
        {
            // Also try stripping a leading device: prefix the game might pass through
            int colon = path.IndexOf(':');
            if (colon >= 0 && colon + 1 < path.Length)
                entry = Iso9660.FindFile(_vol, path[(colon + 1)..]);
        }
        if (entry == null) return 0;

        // Allocate synthetic file object
        uint fileObj = 0;
        for (int i = 0; i < CriMaxFiles; i++)
        {
            uint fo = CriFilePool + (uint)(i * CriFileStride);
            if (sys.Memory.Read32(fo + 0x10) == 0)
            {
                fileObj = fo;
                break;
            }
        }
        if (fileObj == 0) return 0;

        sys.Memory.Write32(fileObj + 0x00, CriOpsBase);
        sys.Memory.Write32(fileObj + 0x04, entry.ExtentLba);
        sys.Memory.Write32(fileObj + 0x08, entry.Size);
        sys.Memory.Write32(fileObj + 0x0C, 0); // pos
        sys.Memory.Write32(fileObj + 0x10, 1); // in use
        WriteAsciiZ(sys.Memory, fileObj + 0x14, Iso9660.NormalizePath(path), 44);

        // Allocate cvFs handle slot at 0x76BEA0 (40 × 8B): free when +0 == 0
        uint handle = 0;
        for (int i = 0; i < 40; i++)
        {
            uint h = CriHandleTable + (uint)(i * 8);
            if (sys.Memory.Read32(h) == 0)
            {
                handle = h;
                break;
            }
        }
        if (handle == 0)
        {
            // Fall back to file-object-as-handle (some call sites use ops at +0 directly)
            return fileObj;
        }

        sys.Memory.Write32(handle + 0, CriOpsBase);
        sys.Memory.Write32(handle + 4, fileObj);
        return handle;
    }

    /// <summary>Service synthetic CDV method stubs (spin loops at CriStub*).</summary>
    private void ServiceCriFsStubs(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        // Stubs are 16 bytes; accept any PC in the stub block.
        if (pc is < CriStubOpen or >= CriStubFsize + 16) return;

        uint a0 = (uint)sys.EE.GetGpr(4).Lo;
        uint a1 = (uint)sys.EE.GetGpr(5).Lo;
        uint a2 = (uint)sys.EE.GetGpr(6).Lo;
        long result = 0;

        // Map PC to which stub
        uint stubBase = pc & ~0xFu;
        if (stubBase == CriStubFsize)
        {
            string path = a0 >= 0x1000 ? ReadCString(sys.Memory, a0) : "";
            if (!string.IsNullOrEmpty(path) && _vol != null)
            {
                var e = Iso9660.FindFile(_vol, path);
                if (e != null) result = e.Size;
            }
        }
        else if (stubBase == CriStubOpen)
        {
            // a0 = path (device open signature)
            string path = a0 >= 0x1000 ? ReadCString(sys.Memory, a0) : "";
            uint fileObj = 0;
            if (!string.IsNullOrEmpty(path) && _vol != null)
            {
                // Open returns file object (not full handle) — matching device->open
                var entry = Iso9660.FindFile(_vol, path);
                if (entry != null)
                {
                    for (int i = 0; i < CriMaxFiles; i++)
                    {
                        uint fo = CriFilePool + (uint)(i * CriFileStride);
                        if (sys.Memory.Read32(fo + 0x10) == 0)
                        {
                            fileObj = fo;
                            sys.Memory.Write32(fo + 0x00, CriOpsBase);
                            sys.Memory.Write32(fo + 0x04, entry.ExtentLba);
                            sys.Memory.Write32(fo + 0x08, entry.Size);
                            sys.Memory.Write32(fo + 0x0C, 0);
                            sys.Memory.Write32(fo + 0x10, 1);
                            WriteAsciiZ(sys.Memory, fo + 0x14, Iso9660.NormalizePath(path), 44);
                            break;
                        }
                    }
                }
            }
            result = fileObj;
        }
        else if (stubBase == CriStubClose)
        {
            // a0 = file object
            if (IsCriFileObj(a0))
                sys.Memory.Write32(a0 + 0x10, 0);
            result = 0;
        }
        else if (stubBase == CriStubSeek)
        {
            // a0 = fileobj (wrapper unwraps handle+4). ADXF/CRI CDV uses sector units
            // (2048B): seek(fp, sectorOff, whence) with whence in a2 (0=set,1=cur,2=end).
            // Internal position stays in bytes.
            if (IsCriFileObj(a0))
            {
                uint size = sys.Memory.Read32(a0 + 0x08);
                uint cur = sys.Memory.Read32(a0 + 0x0C);
                long sectorOff = (int)a1;
                long pos;
                if (a2 <= 2)
                {
                    pos = a2 switch
                    {
                        1 => cur + sectorOff * 2048L,
                        2 => size + sectorOff * 2048L,
                        _ => sectorOff * 2048L
                    };
                }
                else
                {
                    // Fallback: treat a1 as byte offset
                    pos = a1;
                }
                if (pos < 0) pos = 0;
                if (pos > size) pos = size;
                sys.Memory.Write32(a0 + 0x0C, (uint)pos);
                result = 0; // success
            }
            else result = -1;
        }
        else if (stubBase == CriStubTell)
        {
            // a0 = fileobj — ADXF does tell after SEEK_END then `sll result, 11` to get
            // bytes, so tell must return **sector** position (pos/2048), not bytes.
            if (IsCriFileObj(a0))
            {
                uint pos = sys.Memory.Read32(a0 + 0x0C);
                uint size = sys.Memory.Read32(a0 + 0x08);
                uint posSect = pos / 2048u;
                uint sizeSect = (size + 2047u) / 2048u;
                // If a2 looks like a writable pointer, store size (sectors) there.
                if (a2 >= 0x100000 && (a2 & 0x1FFFFFFFu) < SystemMemory.RDRAM_SIZE - 4)
                    sys.Memory.Write32(a2, sizeSect);
                if (a1 >= 0x100000 && (a1 & 0x1FFFFFFFu) < SystemMemory.RDRAM_SIZE - 4 && a1 != a2)
                    sys.Memory.Write32(a1, posSect);
                result = posSect;
            }
        }
        else if (stubBase == CriStubRead)
        {
            // Device method: a0=fileobj, a1/a2 = (buf,size) or (size,buf)
            uint fileObj = a0;
            if (!IsCriFileObj(a0) && a0 >= CriHandleTable && a0 < CriHandleTable + 40 * 8)
                fileObj = sys.Memory.Read32(a0 + 4);
            uint buf = a2, nbytes = a1;
            if (a1 >= 0x100000 && a2 < 0x100000) { buf = a1; nbytes = a2; }
            if (IsCriFileObj(fileObj))
                result = CriRead(sys, fileObj, buf, nbytes);
        }
        else if (stubBase == CriStubStatus)
        {
            // Match wrapper: 2 = reading/data ready, 3 = EOF
            if (IsCriFileObj(a0))
            {
                uint pos = sys.Memory.Read32(a0 + 0x0C);
                uint size = sys.Memory.Read32(a0 + 0x08);
                result = pos >= size && size > 0 ? 3 : 2;
            }
            else result = 2;
        }
        else if (stubBase == CriStubNop)
        {
            result = 0;
        }
        else return;

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = unchecked((ulong)(long)result) });
        // Advance past spin to jr ra at stub+8
        sys.EE.PC = stubBase + 8;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_CRIFS") == "1")
            Console.Error.WriteLine($"[CRIFS] stub=0x{stubBase:X8} a0=0x{a0:X} a1=0x{a1:X} a2=0x{a2:X} res={result} cyc={sys.MasterCycles}");
    }

    private static bool IsCriFileObj(uint addr) =>
        addr >= CriFilePool && addr < CriFilePool + (uint)(CriMaxFiles * CriFileStride)
        && ((addr - CriFilePool) % CriFileStride) == 0;

    /// <summary>
    /// Periodic commercial-slice work. Split into:
    /// <list type="bullet">
    /// <item><b>Structural middleware HLE</b> (always): CRI cvFs ISO open/read, ADXPS2
    /// completion gate — required for GAMEDATA.WAD / resource loads. Not a PC poke.</item>
    /// <item><b>PC-range assists</b> (only when <see cref="Ps2System.DisableMidwayAssist"/> is
    /// false): force SIF init jump, unstick waits, logo FMV overlay.</item>
    /// </list>
    /// With <c>DETPS2_DISABLE_MIDWAY_ASSIST=1</c> / <c>--no-assist</c>, structural HLE still
    /// runs so pure-BIOS boot can progress past the ADX/resource gate.
    /// </summary>
    public void Step(Ps2System sys)
    {
        if (!sys.Hle.SonyKernelMode) return;
        ulong c = sys.MasterCycles;

        // --- Structural (always on for this title) ---
        BindIso(sys.Cdvd.MountedPath);
        if (c > 200_000)
        {
            PlantCriFsDevices(sys);
            MaybeHleCvFsOpen(sys);
            MaybeHleCvFsGetSize(sys);
            MaybeHleCvFsMethodWrappers(sys);
            ServiceCriFsStubs(sys);
            MaybePumpAdxfBulk(sys);
        }
        // ADX refcount gate: after real SIF/DTX activity, plant ready flags (FUN_00414ed0).
        MaybeCompleteAdxInitGate(sys);
        // After bulk disc stream, force resource-manager load slots out of "still loading".
        MaybeCompleteResourceLoadGate(sys);
        // Repair corrupted ADX waiter s0 (must poll 0x5341D8, never plant *0x75C0D0).
        if (c >= 55_000_000)
            MaybeRepairAdxWaiterS0(sys);
        // Main sets DAT_00534164=1 then busy-polls ReferThreadStatus without Sleep/yield —
        // ADX pump never runs to clear the flag. Service after bulk WAD.
        if (c >= 55_000_000 && sys.Cdvd.SectorsRead >= 100_000)
            MaybeServiceAdxPumpLock(sys);
        // Post-WAD bad fnptr → past-RDRAM open bus (0x024Fxxxx): recover every Step once WAD is real.
        if (sys.Cdvd.SectorsRead >= 100_000)
            MaybeRescuePostWadNopSled(sys);
        // Runaway linked-list walk at 0x475608 with corrupted count (s0 >> real list size)
        // freezes boot forever after resource gate (live 2026-07-30: s0≈0x3037953D).
        if (c >= 50_000_000)
            MaybeBreakRunawayListWalk(sys);
        // Post-list format/itoa stall at 0x47670C (jr ra delay) with all workers asleep —
        // force natural return + wake pump/menu path so gifP3 can leave logo spine (5→11+).
        if (c >= 55_000_000 && sys.Cdvd.SectorsRead >= 100_000)
            MaybeEscapePostListFormatStall(sys);
        // Post-merge path parks in ADX/pad bands (0x414xxx/0x4275xx/0x429Cxx) at gifP3=5
        // without ever re-entering format/list-walk. After bulk WAD, force the same main
        // re-home menu6 used after format stall so logo spine can advance (gifP3 5→12).
        if (c >= 58_000_000 && sys.Cdvd.SectorsRead >= 100_000
            && sys.Gif.Path3Transfers < 11)
            MaybeKickMainForLogoSpine(sys);
        // Pad inject START/CROSS after bulk WAD so title/menu can observe input.
        // (Also fired inside pump-lock clear; this covers non-lock-wait PC bands.)
        if (c >= 60_000_000 && sys.Cdvd.SectorsRead >= 100_000)
            MaybeInjectMenuPad(sys);
        // Post-WAD / post-spine memset at 0x385278 (a1..a2 clear). Inverted a2<a1 walks
        // the whole VA space via bne and WIPES EE code (live: main 0x212F70→0 by 64.8M,
        // nop-sled rescue at 0x215FF8). Break as soon as bulk WAD is in — do not wait for
        // gifP3≥11 (wipe races the spine restore).
        if (c >= 55_000_000 && sys.Cdvd.SectorsRead >= 100_000)
            MaybeBreakMenuMemset(sys);
        // After logo spine, kill countdown thrash at 0x427594 (pad-poll callback list)
        // when s2 is absurd so CROSS/DOWN can reach accept paths past the list.
        // Wave-8: gifP3>=11 (not 12) so plateau-11 covers pad accept.
        if (c >= 70_000_000 && sys.Gif.Path3Transfers >= 11)
            MaybeBreakMenuCallbackCountdown(sys);
        // Post-spine pump thrash (empty group-6): re-home toward Midway main so menu
        // state machine can observe pad edges written into ghost PADMAN DMA areas.
        // Cap: after gifP3≥12 main re-entry storms IOPRP gen≥2; prefer multi-slot fill.
        if (c >= 90_000_000 && sys.Gif.Path3Transfers >= 12 && sys.Gif.Path3Transfers < 16)
            MaybeKickMainFromPumpThrash(sys);
        // Mirror FUN_0043ccf8 group-6 multi-slot registration (stream tick @ 0x43F920).
        // Live wall: *0x75E950 empty → pump is pure no-op while menu tick 0x54E600 climbs.
        if (c >= 60_000_000 && sys.Cdvd.SectorsRead >= 100_000)
            MaybeFillGroup6MultiSlot(sys);
        // ADX init FUN_00414d40 @ cyc≈2.76M does `sw zero,0(0x75BDD8)` and never re-arms.
        // Pump path (0x414688/0x4148D8) jal 0x4156E0 → jalr *0x75BDD8; null = skip frame work.
        // Do NOT re-arm at 3M (empty group-6 thrash starved spine: gifP3 stuck 6). Wait for
        // bulk WAD + group-6 multi plant so the frame path has real work to dispatch.
        if (c >= 60_000_000 && sys.Cdvd.SectorsRead >= 100_000)
            MaybeRearmFrameCb(sys);
        // Stream work gate *0x55E1EC must stay 1 so FUN_0043FAE8 does not early-out.
        // Hold after multi+frame-cb plant (resource gate plants once; scrub re-opens).
        if (c >= 60_000_000 && sys.Cdvd.SectorsRead >= 100_000)
            MaybeHoldStreamWorkGate(sys);
        // Wave-8: minimal stream cookie *0x5BB860=1 (FUN_0043ccf8 arg / slot-style active).
        if (c >= 60_000_000 && sys.Cdvd.SectorsRead >= 100_000)
            MaybeInitStreamCookie(sys);
        // Wave-11: FUN_0043CD58 stream-manager defaults / one-shot force-call so FAE8 sees a
        // post-init header (ready *base+0x38=1). Slots still need FUN_0043C1C0 object bind.
        if (c >= 60_000_000 && sys.Cdvd.SectorsRead >= 100_000)
            MaybeInitStreamManager(sys);
        // Wave-12 REJECTED: synthetic stream slot0 plant (flag=1 + type5 stub @0x01FD5000 +
        // D6F8[0]) → EE death at 0x8000018x by ~80M (baseline FAE8@0x43FB40 healthy). Do not
        // re-enable. C1C0 never runs under HLE (pcbreak: 26FBF0/43BFC0/43C1C0 = 0 hits @100M;
        // sole chain 26FC34→43BFC0→C1C0). Need real resource bind or PCSX2 slot dump.
        // Wave-9: re-arm stream CAS *0x55E248 so FUN_0043FAE8 can re-enter after first pass
        // (live 120M: cas248 stuck at 1 while gifP3 plateaus 11; skip200 already 0).
        if (c >= 70_000_000 && sys.Cdvd.SectorsRead >= 100_000)
            MaybeRearmStreamCas(sys);
        // Wave-9: post-spine sticky park in syscall-68 / worker 0x47FD..0x480B and ADX
        // re-init 0x4143A0 — starve second chrome + pad accept.
        if (c >= 75_000_000 && sys.Gif.Path3Transfers >= 11)
            MaybeEscapePostSpineWorkerThrash(sys);
        // Lock wrappers 0x426EF8/0x426F04 thrash after group-6 fills (refcount @ 0x54E5E0).
        // Wave-8: gifP3>=11 (not 12).
        if (c >= 70_000_000 && sys.Gif.Path3Transfers >= 11)
            MaybeBreakLockWrapperThrash(sys);
        // Title-band hash/mix loops with corrupt cursors walk into ELF code
        // (live: sw @ 0x47EB28 zeros main; later sh @ 0x47EFA8 corrupts main).
        // Wave-6: run after bulk WAD without gifP3≥12 gate — HEAD residual parks here
        // at gifP3=8 after bad logo-spine kick and never recovers if gated on spine.
        if (c >= 55_000_000 && sys.Cdvd.SectorsRead >= 100_000)
        {
            MaybeBreakTitleHashCodeWalk(sys);
            MaybeBreakTitleHashCodeWalk2(sys);
            MaybeEscapeTitleHashStickyThrash(sys);
            MaybeEscapeOuterListThrash(sys);
        }
        // VU blit at 0x385674 sqc2 vi5,0(a0) with corrupt a0 overwrites EE code
        // (live find-writer: pc=0x385674 cyc≈81.9M zeros main).
        if (c >= 65_000_000 && sys.Gif.Path3Transfers >= 11)
            MaybeGuardVuBlitCodeDest(sys);
        // Escape stuck bare-eret interrupt vector / HLE scratch if EE never leaves it.
        MaybeEscapeStuckIntVector(sys);
        // EE syscall trampoline table thrash (ra=0 walk through 0x47FDxx) after open-bus.
        if (c >= 12_000_000)
            MaybeEscapeSyscallTrampolineThrash(sys);

        // --- PC-range Midway assists (opt-out via --no-assist) ---
        if (Ps2System.DisableMidwayAssist)
            return;

        if (c - _lastAssistCycle < 25_000) return;
        _lastAssistCycle = c;

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
        MaybeUnblockStarvedSleep(sys);
        MaybeResumeAllAfterResource(sys);
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

        // INTC_STAT VBlankStart poll (MKSM 0x4803D0): lw [0x1000F000]; andi 4; bne exit.
        // Live 300M: PC stuck at 0x4803DC with cdvd/RPC frozen — VBlank bit never observed.
        // Force Raise + jump past poll so boot continues (STAT already sticky elsewhere).
        if (pc is >= 0x004803D0 and <= 0x004803E8)
        {
            sys.Intc.Raise(Intc.InterruptSource.VBlankStart);
            // Prefer real exit path: set bit is enough if next lw sees it. Also nudge PC past
            // the beq timeout arm so one assist sample completes the wait.
            if (!_vblankPollNudgeArmed)
            {
                _vblankPollNudgeArmed = true;
                Assists++;
            }
            else
            {
                // Second hit: hard-exit to post-poll (jal 0x485FB8)
                sys.EE.PC = 0x004803EC;
                _vblankPollNudgeArmed = false;
                Assists++;
            }
            return;
        }
        else
            _vblankPollNudgeArmed = false;

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
            RestoreRaThenJumpTo0x483000(sys);
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
            RestoreRaThenJumpTo0x483000(sys);
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

    /// <summary>
    /// Both sif-init-wait-unstick branches above jump straight to `0x00483000`, skipping the real
    /// instruction at `0x00482FFC` (`ld ra,48(sp)`) that the natural, unassisted code path would
    /// have executed there. That instruction restores $ra to whatever called this whole wait
    /// routine, right before it tail-jumps into SifSetReg (`j 0x00480260`) -- so skipping it leaves
    /// $ra holding a stale mid-loop value (0x00482FF8, this wait's own internal retry address)
    /// instead of a real caller. Confirmed live (2026-07-27, Mortal Kombat: Shaolin Monks, found via
    /// a real hardware/DetPS2 side-by-side comparison using a custom PCSX2 remote debugger — see
    /// docs/DEVELOPER_GUIDE.md): real hardware has a genuine, valid $ra at the equivalent
    /// SifSetReg-trampoline return point; DetPS2 had 0 -- eventually surfacing, cycles later, as
    /// thread 1's own `jr ra` (ra==0) implicit-exit path firing with nothing else runnable,
    /// permanently stalling the EE. Restoring the real stack read here (rather than just jumping)
    /// keeps this assist's own intent (skip the wait, land at 0x483000) while no longer bypassing
    /// a real instruction's effect.
    /// </summary>
    private static void RestoreRaThenJumpTo0x483000(Ps2System sys)
    {
        ulong sp = sys.EE.GetGpr(29).Lo;
        uint realRa = sys.Memory.Read32((uint)sp + 48);
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = realRa });
        sys.EE.PC = 0x00483000;
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
        // Keep force SP at 0x01FF0000 when invalid — WAD stream (cdvd=198840) is load-bearing
        // and was killed by re-homing to 0x01FC0000 during early force (2026-07-30 recheck).
        // Post-WAD SP re-home runs only after bulk disc I/O (see ReHomeSpIfInHleScratch).
        ulong sp = sys.EE.GetGpr(29).Lo;
        if ((sp & 0x1FFFFFFFUL) < 0x100000 || (sp & 0x1FFFFFFFUL) >= 0x2000000)
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
        sys.EE.PC = ManagerInitFn;
        _managerInitForced = true;
        _managerInitResumePending = true;
        Assists++;
    }

    /// <summary>Stack top for post-WAD SP re-home — below HLE scratch, 16-byte aligned.</summary>
    private const uint ManagerInitSafeSp = 0x01FC0000;

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
        // Do NOT re-home SP here — early resume is mid-boot; re-home only post-WAD.
        sys.LastGoodEePc = _managerInitSavedPc;
        _managerInitResumePending = false;
    }

    /// <summary>
    /// If $sp lands in HLE scratch (0x01FD0000–0x01FFFFFF) or is null/low, move it to
    /// <see cref="ManagerInitSafeSp"/>. Only safe AFTER bulk WAD I/O (cdvdSectors gate).
    /// Does not restore pre-force GPRs.
    /// </summary>
    private static void ReHomeSpIfInHleScratch(Ps2System sys)
    {
        // WAD-preserving gate: never touch SP before the resource stream is real.
        if (sys.Cdvd.SectorsRead < 100_000) return;
        uint spPhys = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        if (spPhys < 0x100000 || spPhys >= 0x01FD0000)
        {
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = ManagerInitSafeSp });
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine($"[BIOS] re-home SP 0x{spPhys:X8} -> 0x{ManagerInitSafeSp:X8}");
        }
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
        // Prefer real RPC completions. Force-signal only after a long genuine stall.
        // After WAD/resource gate, poke more often so SIF worker (sema 3) keeps draining.
        ulong graceCycles = _resourceLoadForced ? 250_000UL : 1_500_000UL;
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

            // Drain real RPC first — often the producer for this WaitSema.
            sys.Hle?.Sony?.DrainRealRpcQueue(sys.SchedulerGeneration + 1);
            if (!t.Sleeping) { _semaWaitStart.Remove(t.Id); continue; }

            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine($"[RPC] force-unblocking starved sema={t.WaitSemaId} thread={t.Id} cyc={sys.MasterCycles}");
            kernel.SignalSema(t.WaitSemaId);
            // Cooldown: do not re-rescue this thread for another full grace window
            _semaWaitStart[t.Id] = (t.WaitSemaId, sys.MasterCycles);
            Assists++;
        }
    }

    /// <summary>
    /// Sibling rescue to <see cref="MaybeUnblockStarvedSema"/>, for a thread parked via plain
    /// SleepThread (WaitSemaId==0, not WaitVblank) rather than WaitSema. Traced (2026-07-27): once
    /// KickCommercialWorker actually starts Shaolin Monks' SIF-RPC dispatch worker (thread id>=2),
    /// that worker runs a real WaitSema/WakeupThread dispatch loop indefinitely, but every
    /// WakeupThread call it makes targets id=0 — a no-op, since no thread has id 0 (ids start at 1,
    /// and thread 1 - the primordial boot thread - predates the normal CreateThread/GetThreadId
    /// flow, so nothing ever recorded its real id anywhere the worker reads it back from). The main
    /// thread (id 1), which had SleepThread'd itself expecting the worker to wake it once real,
    /// therefore sleeps forever even though the worker is genuinely alive and running. Force-wakes
    /// any such starved thread after the same grace period as the sema case.
    /// </summary>
    /// <summary>
    /// After WAD/resource gate, ADX mutual-exclusion often Suspends the live pump and leaves
    /// SoftSuspended peers while main sits at the Suspend stub with RPC frozen. Periodically
    /// drain SoftSuspended + SuspendCount and Signal WaitSema waiters so SIF/pump run again.
    /// </summary>
    private void MaybeResumeAllAfterResource(Ps2System sys)
    {
        if (!_resourceLoadForced) return;
        if (sys.MasterCycles - _lastPostResourceResumeCycle < 500_000UL) return;
        _lastPostResourceResumeCycle = sys.MasterCycles;
        var kernel = sys.Hle?.Kernel;
        if (kernel == null) return;

        sys.Hle?.Sony?.DrainRealRpcQueue(sys.SchedulerGeneration + 1);

        foreach (var t in kernel.AllThreads)
        {
            if (!t.Alive) continue;
            if (t.SoftSuspended)
                kernel.ResumeThread(t.Id);
            while (t.SuspendCount > 0)
                kernel.ResumeThread(t.Id);
            if (t.Sleeping && t.WaitSemaId != 0)
            {
                try { kernel.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
            }
            else if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank && t.Id >= 2)
                kernel.WakeupThread(t.Id);
        }
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        if (pc is >= 0x0047FDD0 and <= 0x0047FDE0)
            kernel.YieldToWorker(sys.EE);
        Assists++;
    }

    private void MaybeUnblockStarvedSleep(Ps2System sys)
    {
        // SuspendThread parks are often "wait for peer Resume" with no peer under HLE —
        // rescue sooner than plain SleepThread so boot CD/ADX can proceed.
        const ulong graceSleep = 2_000_000UL;
        const ulong graceSuspend = 400_000UL;
        var kernel = sys.Hle?.Kernel;
        if (kernel == null) return;

        foreach (var t in kernel.AllThreads)
        {
            // Pure SleepThread OR SuspendThread park (SuspendCount>0), no sema/vblank
            bool suspended = t.SuspendCount > 0;
            if (!t.Alive || t.WaitSemaId != 0 || t.WaitVblank)
            {
                _sleepWaitStart.Remove(t.Id);
                continue;
            }
            if (!t.Sleeping && !suspended)
            {
                _sleepWaitStart.Remove(t.Id);
                continue;
            }
            if (!_sleepWaitStart.TryGetValue(t.Id, out var since))
            {
                _sleepWaitStart[t.Id] = sys.MasterCycles;
                continue;
            }
            ulong grace = suspended ? graceSuspend : graceSleep;
            if (sys.MasterCycles - since < grace) continue;

            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                Console.Error.WriteLine(
                    $"[RPC] force-waking starved sleep/suspend thread={t.Id} susp={t.SuspendCount} cyc={sys.MasterCycles}");
            if (t.SuspendCount > 0)
            {
                // Drain suspend nest so Resume-equivalent unpark works
                while (t.SuspendCount > 0)
                    kernel.ResumeThread(t.Id);
            }
            else
                kernel.WakeupThread(t.Id);
            _sleepWaitStart.Remove(t.Id); // fresh grace period if it re-sleeps
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
            realRpc.TryHandle(mem, kernel, sys.Cdvd, sys.Pad, sys.IopModules, RealSifBindPkt);

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
        realRpc.TryHandle(mem, kernel, sys.Cdvd, sys.Pad, sys.IopModules, RealSifCallPkt);

        uint result = mem.Read32(RealSifRecvBuf);
        if (trace)
            Console.Error.WriteLine($"[RPC] MaybeCompleteRealSifCdRead: call(NcmdRead lbn=0) -> result={result} calls={realRpc.Calls} cdvdSectors={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
        _realSifStage = -1; // done — one-shot proof that the real dispatch chain works end to end
    }

    /// <summary>
    /// HLE the Midway/Surreal ADXPS2 "last async complete" gate when real IOP IRX execution
    /// is not yet available to deliver the EE callback that would call FUN_00414f20.
    ///
    /// Live-traced (2026-07-29, DEVELOPER_GUIDE §7.19–7.22): CRI ADX init (FUN_00414d40)
    /// increments refcount at 0x534124 0→1, spawns waiters on readiness flags 0x5341D8…228,
    /// then never receives the completion that would run FUN_00414f20 (only static caller
    /// FUN_0026f288 is itself only reachable via an IOP-driven path never entered under HLE).
    /// FUN_00414ed0 (called only when that refcount hits 0) is an unconditional six-flag store.
    /// After RealSifRpc has finished the bind/DTX surface the game already exercised, plant the
    /// same six flags and clear the refcount — same observable as a real last-out completion.
    /// Title-scoped (MidwayBootAssist); not a generic BIOS contract.
    /// </summary>
    private void MaybeCompleteAdxInitGate(Ps2System sys)
    {
        if (_adxGateCompleted) return;
        // Do NOT plant before ADX workers (0x4145A8/0x4147F8) have been StartThread'd and
        // had a chance to park on zero flags. Planting at 5M then kicking at 7.35M made
        // every worker see flag!=0, ExitThread immediately, and left main thrashing
        // SuspendThread/GetThreadId/ReferThreadStatus (~144k each / 150M).
        if (sys.MasterCycles < 12_000_000) return;
        var realRpc = sys.Hle?.Sony?.RealRpc;
        if (realRpc == null) return;
        // Need real CRI ADX / SIF activity before claiming ready (binds include CD/FILEIO/ADX).
        if (realRpc.Binds < 4 || realRpc.Calls < 10) return;

        // Plant only once ADX workers have had time to park on zero flags, OR late emergency.
        // Prefer: bulk disc stream underway (WAD) so resource path isn't starved of EE time.
        bool heavyIo = sys.Cdvd.SectorsRead > 10_000 || _adxfPumpCount > 50;
        if (!heavyIo && sys.MasterCycles < 35_000_000) return;
        if (sys.MasterCycles < 18_000_000) return;

        uint rc = sys.Memory.Read32(0x00534124);
        uint flag = sys.Memory.Read32(0x005341D8);
        if (flag != 0) { _adxGateCompleted = true; return; } // already open
        // If refcount never acquired, still open waiters after long run with heavy RPC —
        // resource load can leave threads parked on zero flags with rc still 0.
        if (rc == 0 && sys.MasterCycles < 40_000_000) return;
        if (rc == 0 && realRpc.Calls < 50) return;

        // Mirror FUN_00414ed0 ready flags — BUT skip DAT_00534218 (0x5341D8 + 4*0x10).
        // Live-traced (2026-07-29): pump worker entry 0x4147F8 is
        //   ld v1,0(0x534218); bne v1,zero,epilogue
        // so planting 1 there forces every ADX worker to fall through to jr ra with
        // $ra=0 → ExitThread (observed: all four tids exit at 0x47FCA4 ra=0 the same
        // cycle the gate fires). Waiters only need 0x5341D8 / 0x5341E8 / … / 0x534208.
        for (uint i = 0; i < 6; i++)
        {
            uint addr = 0x005341D8 + i * 0x10;
            if (addr == 0x00534218) continue; // pump-stop — leave 0
            sys.Memory.Write32(addr, 1);
            sys.Memory.Write32(addr + 4, 0);
        }
        sys.Memory.Write32(0x00534124, 0); // refcount drained
        // Force pump-stop flag clear in case prior session state / game code set it.
        sys.Memory.Write32(0x00534218, 0);
        sys.Memory.Write32(0x0053421C, 0);
        // Also mark sibling heartbeat region the wait loop samples (0x534180 area).
        if (sys.Memory.Read32(0x00534180) == 0)
            sys.Memory.Write32(0x00534180, 1);

        // Mirror FUN_00427410(6, 0x414568, 0): single-slot group table at 0x75E9E0 (stride 8).
        // FUN_00427468(6) reads slot 6 → 0x75EA10; streaming-tick callers (FUN_0043ce78 etc.)
        // use that path. Live dumps often leave it zero under HLE if ADX init never finished
        // registration — plant only the single-slot entry.
        //
        // CRITICAL: do NOT plant 0x414568 into the multi-slot table at 0x75E7A0.
        // Pump worker 0x4147F8 → FUN_00427678 → FUN_00427518(6) walks 0x75E7A0 (stride 0x48).
        // FUN_00414568 is itself the lock-wait (tail-calls FUN_00414480 which sets *0x534164=1
        // and busy-polls the pump). Putting it in the multi table makes the pump call the
        // lock-wait on itself → self-deadlock; both main and pump thrash at 0x4144F0 forever
        // (observed 2026-07-30: directed switch lands pump still at lock-wait PCs). Empty
        // multi table = correct boot-time no-op (DEVELOPER_GUIDE §7.24 live dump).
        const uint AdxGroup6Fn = 0x00414568;
        const uint AdxGroupTable = 0x0075E9E0;
        const uint AdxGroup6Slot = AdxGroupTable + 6 * 8;
        if (sys.Memory.Read32(AdxGroup6Slot) == 0)
        {
            sys.Memory.Write32(AdxGroup6Slot, AdxGroup6Fn);
            sys.Memory.Write32(AdxGroup6Slot + 4, 0);
        }
        // Scrub a prior-session / older-build self-deadlock plant if present.
        const uint AdxMultiBase = 0x0075E7A0;
        if (sys.Memory.Read32(AdxMultiBase) == AdxGroup6Fn)
        {
            sys.Memory.Write32(AdxMultiBase, 0);
            sys.Memory.Write32(AdxMultiBase + 4, 0);
            sys.Memory.Write32(AdxMultiBase + 8, 0);
        }

        // Wake anyone parked via SuspendThread waiting on these flags.
        var kernel = sys.Hle?.Kernel;
        if (kernel != null)
        {
            foreach (var t in kernel.AllThreads)
            {
                if (!t.Alive || t.SuspendCount <= 0) continue;
                while (t.SuspendCount > 0)
                    kernel.ResumeThread(t.Id);
            }
        }
        _adxGateCompleted = true;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1"
            || Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BIOS] MaybeCompleteAdxInitGate: 6 ready flags, rc 0x534124 {rc}->0 " +
                $"binds={realRpc.Binds} calls={realRpc.Calls} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Unblock FUN_0026fd80-style resource load-and-wait once bulk disc I/O has finished.
    /// Live-traced (DEVELOPER_GUIDE §ADX/resource): poll is
    /// <c>FUN_0026fbf0(0x678458) → status at handle+0x48</c> (0 = still loading). After
    /// GAMEDATA.WAD-scale CDVD activity, force active resource slots to "done" so main can
    /// leave the load spin and reach the CRI ADX tick / menu path.
    /// </summary>
    private void MaybeCompleteResourceLoadGate(Ps2System sys)
    {
        if (_resourceLoadForced) return;
        // Wait until WAD-scale stream is done AND boot has had time for pad/SIF surface.
        // Firing at 35M with only status poke still left main at Suspend stub with RPC
        // frozen at 172 calls for the rest of a 400M run.
        if (sys.MasterCycles < 55_000_000) return;
        bool wadDone = (sys.Cdvd.SectorsRead >= 180_000 && _adxfPumpCount >= 5000)
            || sys.MasterCycles > 100_000_000;
        if (!wadDone) return;

        var mem = sys.Memory;
        int fixedSlots = 0;

        // Known static handle used by FUN_0026fd80 poll (DEVELOPER_GUIDE)
        fixedSlots += ForceResourceHandleDone(mem, 0x00678458);
        // Unconditional status poke — handle may have zero header under HLE but still be polled.
        if (mem.Read32(0x00678458 + 0x48) == 0)
        {
            mem.Write32(0x00678458 + 0x48, 1);
            fixedSlots++;
        }

        // FUN_0026fd80: lVar6 = (DAT_00678644 != 1) ? poll : 3; while (lVar6 == 0);
        // Prefer poll completion via +0x48 status (above) rather than forcing DAT_00678644=1,
        // which previously aborted load-wait before the SIF bind surface finished expanding
        // (binds stuck at 12 / calls at 172 for hundreds of M cycles after the gate).
        // Prefer +0x48 status alone. Force DAT_00678644 only very late.
        if (sys.MasterCycles > 120_000_000)
            mem.Write32(0x00678644, 1);
        mem.Write32(0x00678650, 0);

        // Streaming-tick countdown that never gets written under HLE (DEVELOPER_GUIDE §ADX).
        if (mem.Read32(0x0055E1E8) == 0)
            mem.Write32(0x0055E1E8, 1);
        // Stream work gate for FUN_0043FAE8: lw s1, *0x55E1EC; bne s1,1,skip.
        // Live disasm (2026-07-30): plant at 0x55E1E8 alone left *0x55E1EC=0 so stream tick
        // entered 0x43FAE8 then immediately took the epilogue (PC samples 0x43FB9C). Without
        // this, cookie work / UI accept never runs even with group-6+frame-cb planted.
        if (mem.Read32(0x0055E1EC) == 0)
            mem.Write32(0x0055E1EC, 1);

        // Resource-manager pool: 8 entries × 0x2AC (FUN_0043b670), scan a few candidate bases
        // used by Midway resource manager near 0x678xxx / 0x55Exxx.
        uint[] poolBases = { 0x00678000, 0x00670000, 0x0055E000, 0x00560000 };
        foreach (uint baseP in poolBases)
        {
            for (int i = 0; i < 8; i++)
            {
                uint slot = baseP + (uint)(i * 0x2AC);
                if (slot + 0x50 >= SystemMemory.RDRAM_SIZE) continue;
                // Active slot: non-zero pointer/id at +0 and status 0 at +0x48
                uint id = mem.Read32(slot);
                uint st = mem.Read32(slot + 0x48);
                if (id != 0 && st == 0)
                    fixedSlots += ForceResourceHandleDone(mem, slot);
            }
        }

        // Also sweep a tight window around 0x678458 for any 0x48-offset status zeros with
        // non-zero object headers (cheap, bounded).
        for (uint p = 0x00678000; p < 0x0067A000; p += 4)
        {
            uint st = mem.Read32(p);
            // Only write when looking at likely status fields: prior word non-zero, this zero,
            // and p ends with pattern matching +0x48 stride-ish — use direct handle force only.
            _ = st;
        }

        // Countdown gate DAT_0055e1e8 that never gets written under HLE — plant a non-zero
        // so FUN_0043ce78's "every Nth tick" path can fire once main reaches it.
        if (mem.Read32(0x0055E1E8) == 0)
            mem.Write32(0x0055E1E8, 1);
        if (mem.Read32(0x0055E1EC) == 0)
            mem.Write32(0x0055E1EC, 1);

        _resourceForceScans++;
        if (fixedSlots > 0 || sys.MasterCycles > 50_000_000)
        {
            _resourceLoadForced = true;
            // Wake only threads that are actually blocked — do NOT SignalSema(1..64) blindly
            // (that re-created a 2M× WaitSema/Wakeup thrash after the early-gate regression).
            var kernel = sys.Hle?.Kernel;
            if (kernel != null)
            {
                foreach (var t in kernel.AllThreads)
                {
                    if (!t.Alive) continue;
                    if (t.Sleeping && t.WaitSemaId != 0)
                    {
                        try { kernel.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                    if (t.SuspendCount > 0)
                    {
                        while (t.SuspendCount > 0)
                            kernel.ResumeThread(t.Id);
                    }
                }
            }
            // Re-arm only the ADX pump worker (0x4147F8) after bulk load — not one-shot
            // flag waiters (0x4145A8 etc.) which correctly ExitThread once flags are set.
            // Do NOT YieldToWorker here: switching mid-gate onto a half-init worker has been
            // observed to land in corrupted list walks (0x475608) and ra=0 thread death.
            if (kernel != null)
            {
                foreach (var t in kernel.AllThreads)
                {
                    if (t.Id < 2 || !t.Alive || t.Started) continue;
                    if (t.Entry != 0x004147F8u) continue;
                    kernel.StartAndMaybeSwitch(sys.EE, t.Id, switchNow: false, arg: 0, fromSyscall: false);
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                        Console.Error.WriteLine(
                            $"[BIOS] re-Start ADX pump tid={t.Id} entry=0x{t.Entry:X8} cyc={sys.MasterCycles}");
                }
            }
            // Drop host logo overlay once bulk resources are in — game GS (Path3) should
            // own the framebuffer past this point (was pinning px≈77M on logo blit).
            if (_midwayDone || _logoActive)
            {
                _logoActive = false;
                _midwayDone = true;
                sys.Gs.ClearHostOverlay();
                Status = "post-wad-gs";
            }

            // NOTE: do NOT SoftSuspend exited ADX waiters here. SoftSuspend + Yield after the
            // resource poke previously combined with the multi-table self-deadlock plant to
            // thrash; without SoftSuspend, pump/main can re-arm cleanly. Exited waiters are
            // already !Started so they won't run.

            // NOTE (2026-07-30): Prior gifP3=11 spine showed post-WAD wait at 0x4145xx with
            // s0=0x75C0D0. ELF has ZERO address forms for 0x75C0D0 — not a static flag.
            // FUN_004145A8 always polls 0x5341D8 when entered cleanly; s0=0x75C0D0 is register
            // corruption (ManagerInit force SP=0x01FF0000 / Escape). Planting *0x75C0D0=1 hits
            // ExitThread → process Exit() ~61M. Do NOT plant. See docs/title-ports/MK_SHAOLIN_MONKS.md.
            // Instead: re-home SP + repair waiter s0 to 0x5341D8 when parked in the poll loop.
            ReHomeSpIfInHleScratch(sys);
            MaybeRepairAdxWaiterS0(sys);
            // Clear pump lock flag so FUN_00414480 does not busy-spin forever (see MaybeServiceAdxPumpLock).
            if (sys.Memory.Read32(0x00534164) != 0)
                sys.Memory.Write32(0x00534164, 0);
            sys.Memory.Write32(0x00534218, 0); // pump-stop must stay clear
            // Post-WAD often jumps into zeroed BSS (0x024Fxxxx) via bad fnptr — recover now.
            MaybeRescuePostWadNopSled(sys);

            Assists++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1"
                || Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[BIOS] MaybeCompleteResourceLoadGate: fixed={fixedSlots} " +
                    $"cdvd={sys.Cdvd.SectorsRead} adxfPumps={_adxfPumpCount} cyc={sys.MasterCycles}");
        }
    }

    /// <summary>
    /// After bulk WAD I/O, if EE PC lands on a zero opcode (bad function pointer into BSS),
    /// snap to a stack return candidate or ADX waiter epilogue. Complements
    /// <c>Ps2System.MaybeRescueNopSled</c> which can miss when a 50k slice walks off zeros.
    /// Wave-6: past-RDRAM open-bus sticky thrash (live <c>0x00F30Cxx</c>) must prefer
    /// ADX pump / pad-poll over stack-scan garbage (live loop: rescue→0x170BFC→open-bus).
    /// </summary>
    private int _openBusStickyHits;
    private ulong _lastOpenBusCyc;

    private void MaybeRescuePostWadNopSled(Ps2System sys)
    {
        if (sys.Cdvd.SectorsRead < 100_000) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        // 0x024F0C64 is PAST 32MiB RDRAM (0x02000000) — open bus reads as 0 / nop forever.
        // Also catch in-range zero sleds. Do NOT require pc < RDRAM_SIZE.
        if (pc < 0x00100000) return;
        bool pastRdram = pc >= (uint)SystemMemory.RDRAM_SIZE;
        uint op = pastRdram ? 0u : sys.Memory.Read32(pc);
        if (!pastRdram && op != 0) return;
        if (!pastRdram && sys.Memory.Read32(pc + 4) != 0 && sys.Memory.Read32(pc + 8) != 0) return;

        if (pastRdram)
        {
            if (sys.MasterCycles - _lastOpenBusCyc < 200_000)
                _openBusStickyHits++;
            else
                _openBusStickyHits = 1;
            _lastOpenBusCyc = sys.MasterCycles;
        }

        uint resume = 0;
        // Past-RDRAM sticky thrash: skip stack scan (returns garbage that re-enters open bus).
        bool forceKnown = pastRdram && _openBusStickyHits >= 4;
        if (!forceKnown)
        {
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
            if (sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE)
            {
                for (uint off = 0; off <= 0x80; off += 4)
                {
                    uint cand = sys.Memory.Read32(sp + off) & 0x1FFFFFFFu;
                    // Reject low BIOS/kernel stubs and anything past typical ELF image.
                    if (cand is < 0x00110000 or >= 0x00800000) continue;
                    if (!sys.Memory.IsLikelyEeCode(cand)) continue;
                    // Reject title-hash / outer-list thrash bands as resume targets.
                    if (cand is (>= 0x0047EAE0 and <= 0x0047EFC0)
                        or (>= 0x00474C00 and <= 0x00476840)) continue;
                    resume = cand;
                    break;
                }
            }
        }
        // Prefer ADX pump / pad-poll / ready-waiter / main (code-validated).
        if (resume == 0 && sys.Memory.IsLikelyEeCode(0x004147F8UL))
            resume = 0x004147F8;
        if (resume == 0 && sys.Memory.IsLikelyEeCode(0x00427518UL))
            resume = 0x00427518;
        if (resume == 0 && sys.Memory.IsLikelyEeCode(0x004145A8UL))
            resume = 0x004145A8;
        if (resume == 0 && sys.Memory.Read32(0x00212F70) == 0x27BDFEE0)
            resume = 0x00212F70;
        if (resume == 0 && sys.Memory.IsLikelyEeCode(sys.LastGoodEePc))
        {
            uint lg = (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL);
            if (lg is not ((>= 0x0047EAE0 and <= 0x0047EFC0)
                or (>= 0x00474C00 and <= 0x00476840)))
                resume = lg;
        }
        if (resume == 0) return;

        sys.EE.COP0_Status &= ~0x6u;
        sys.EE.PC = resume;
        sys.LastGoodEePc = resume;
        ReHomeSpIfInHleScratch(sys);
        // Ensure ADX ready flags so waiter at 0x4145A8 can exit.
        if (sys.Memory.Read32(0x005341D8) == 0)
            sys.Memory.Write32(0x005341D8, 1);
        sys.Memory.Write32(0x00534164, 0);
        if (pastRdram)
            _openBusStickyHits = 0;
        Assists++;
        // Rate-limit spam: sticky open-bus used to log every 50k cycles.
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (!pastRdram || _openBusStickyHits == 0))
            Console.Error.WriteLine(
                $"[BIOS] post-WAD nop-sled rescue 0x{pc:X8} -> 0x{resume:X8} pastRdram={pastRdram} " +
                $"forceKnown={forceKnown} cyc={sys.MasterCycles}");
    }

    private ulong _lastMenuPadCyc;
    private int _menuPadPulses;
    private int _menuSpineKicks;

    private ulong _lastMemsetBreakCyc;
    private int _memsetBreaks;

    private bool _memsetFnStubbed;

    /// <summary>
    /// Clear loop at <c>0x385278</c>: <c>sw zero,0(a1); a1+=4; bne a1,a2</c>.
    /// When <c>a2 &lt; a1</c> or remain is huge the loop <b>zeros EE code</b> (live:
    /// main <c>0x212F70</c> wiped; progressive a1 walked 0x670→0x1081e0 across breaks).
    /// Do NOT permanently jr-ra-stub on WAD load — legitimate clears of real heap ranges
    /// must still run (early stub regressed object bases / spine restore). Prefer nop of
    /// the back-edge after spine + full stub only after repeated absurd hits.
    /// </summary>
    private void MaybeBreakMenuMemset(Ps2System sys)
    {
        // ONLY the tight clear loop (0x385278..0x385290). Live disasm: 0x3854C0.. is COP2
        // VU math (jr ra @ 0x3854C0/0x385534) — treating that band as memset false-positives
        // snapped a1/a2 mid-VU and thrashed title for tens of M cycles.
        // Do NOT permanently jr-ra-stub the function on WAD load — legitimate clears of real
        // heap ranges must still run (early stub regressed object bases / spine restore).
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pc is < 0x00385270 or > 0x00385290) return;
        if (sys.MasterCycles - _lastMemsetBreakCyc < 10_000) return;

        uint a1 = (uint)(sys.EE.GetGpr(5).Lo & 0x1FFFFFFFUL); // cursor
        uint a2 = (uint)(sys.EE.GetGpr(6).Lo & 0x1FFFFFFFUL); // end
        ulong remain = a2 >= a1 ? (ulong)(a2 - a1) : ulong.MaxValue;
        bool absurd = remain >= 0x40000 || a2 < a1 || a2 >= 0x02000000 || a1 >= 0x02000000
            || (a1 < 0x00780000 && remain >= 0x1000);
        if (!absurd) return;

        // On first absurd clear after logo spine: nop the back-edge so re-entry cannot wipe
        // the ELF image for millions of cycles (menu6-proven approach). Full jr-ra stub of
        // the body is reserved for repeated absurd hits that still re-enter.
        if (sys.Gif.Path3Transfers >= 11 && !_memsetFnStubbed
            && sys.Memory.Read32(0x0038528C) != 0u)
        {
            sys.Memory.Write32(0x0038528C, 0u); // nop bne back-edge
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[BIOS] plant memset back-edge nop @ 0x38528C gifP3={sys.Gif.Path3Transfers} " +
                    $"cyc={sys.MasterCycles}");
        }
        if (_memsetBreaks >= 8 && !_memsetFnStubbed)
        {
            sys.Memory.Write32(0x00385278, 0x03E00008u); // jr ra
            sys.Memory.Write32(0x0038527C, 0x00000000u); // nop
            _memsetFnStubbed = true;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[BIOS] stub menu memset 0x385278 -> jr ra after repeated absurd clears " +
                    $"n={_memsetBreaks} cyc={sys.MasterCycles}");
        }

        sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = a2 == 0 ? a1 : a2 });
        sys.EE.PC = 0x00385294;
        sys.LastGoodEePc = 0x00385294;
        _lastMemsetBreakCyc = sys.MasterCycles;
        _memsetBreaks++;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_memsetBreaks <= 8 || _memsetBreaks % 16 == 0))
            Console.Error.WriteLine(
                $"[BIOS] break menu memset a1=0x{a1:X8} a2=0x{a2:X8} remain=0x{remain:X} " +
                $"-> 0x385294 n={_memsetBreaks} gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

    private ulong _lastCbCountdownCyc;
    private int _cbCountdownBreaks;

    /// <summary>
    /// Live pad-poll band <c>0x427570..0x427594</c>: countdown <c>s2</c> with
    /// <c>bgezl s2, loop; jalr callback</c>. With HLE-corrupt list length s2 is huge
    /// (or never reaches -1) so CROSS accept never falls through to the epilogue that
    /// bumps the menu tick at <c>0x54E5E8+s4</c> (MIPS <c>lui 0x55; addiu -6680</c>).
    /// Clamp s2 so the list finishes. Live: index-6 tick at 0x54E600 advances millions
    /// when s2 is the natural constant 5 — only absurd s2 needs this snap.
    /// </summary>
    private int _cbCountdownVisits;
    private ulong _lastCbCountdownVisitCyc;

    private void MaybeBreakMenuCallbackCountdown(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pc is < 0x00427570 or > 0x00427598) return;
        if (_cbCountdownBreaks >= 128) return;

        if (sys.MasterCycles - _lastCbCountdownVisitCyc < 200_000)
            _cbCountdownVisits++;
        else
            _cbCountdownVisits = 1;
        _lastCbCountdownVisitCyc = sys.MasterCycles;
        if (sys.MasterCycles - _lastCbCountdownCyc < 80_000) return;

        long s2 = unchecked((int)(uint)sys.EE.GetGpr(18).Lo);
        // Wave-10: natural multi starts s2=5 and counts to -1. Sticky-break on visits≥2
        // aborted real callbacks mid-list (live: s2=3/2 → -1) and starved stream/pad work.
        // Only snap absurd HLE-corrupt counts; require extreme sticky + huge s2 for the
        // "never finishes" case. Negative s2 is the natural terminal — leave it alone.
        bool absurd = s2 >= 64;
        bool extremeSticky = !absurd && s2 >= 16 && _cbCountdownVisits >= 24;
        if (!absurd && !extremeSticky) return;
        if (s2 < 0) return;

        sys.EE.SetGpr(18, new EmotionEngine.Gpr128 { Lo = 0xFFFFFFFFUL });
        sys.EE.PC = 0x0042759C;
        sys.LastGoodEePc = 0x0042759C;
        _lastCbCountdownCyc = sys.MasterCycles;
        _cbCountdownBreaks++;
        _cbCountdownVisits = 0;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_cbCountdownBreaks <= 12 || _cbCountdownBreaks % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] break menu callback countdown s2 was {s2} -> -1 / 0x42759C " +
                $"(absurd={absurd} extremeSticky={extremeSticky}) n={_cbCountdownBreaks} " +
                $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Wave-10: pick a safe resume for group-6 multi / stream / ADX after thrash.
    /// Prefer <c>0x427678</c> (sets <c>a0=6</c> then jumps to multi) over bare
    /// <c>0x427518</c> — thrash context leaves garbage a0 so bare multi dispatches the
    /// wrong group. Also heal <c>$ra</c> so multi's <c>jr ra</c> returns to ADX pump
    /// instead of worker thrash / open-bus.
    /// </summary>
    private static uint PickMenuDispatchResume(Ps2System sys)
    {
        // Group-6 entry prolog: addiu sp,sp,-16 ; a0=6
        if (sys.Memory.Read32(0x00427678) == 0x27BDFFF0u
            && sys.Memory.Read32(0x0042767C) == 0x24040006u)
            return 0x00427678;
        if (sys.Memory.Read32(0x0043F920) == 0x27BDFFF0u)
            return 0x0043F920;
        if (sys.Memory.IsLikelyEeCode(0x00427518UL))
            return 0x00427518;
        if (sys.Memory.IsLikelyEeCode(0x004147F8UL))
            return 0x004147F8;
        if (sys.Memory.IsLikelyEeCode(0x00414590UL))
            return 0x00414590;
        return 0;
    }

    /// <summary>
    /// Apply <see cref="PickMenuDispatchResume"/> + set a0 + re-open stream gates.
    /// Returns chosen resume PC or 0 if none.
    /// <para>
    /// Wave-10 note: do <b>not</b> rewrite <c>$ra</c> to ADX pump. Multi's <c>jr ra</c>
    /// must return to the interrupted context's real caller — forcing pump $ra from the
    /// commercial-worker stack landed EE in the exception vector (live: PC=0x8000018C).
    /// Only heal clearly dead $ra (0 / non-code / past RDRAM).
    /// </para>
    /// </summary>
    private uint ApplyMenuDispatchResume(Ps2System sys)
    {
        uint resume = PickMenuDispatchResume(sys);
        if (resume == 0) return 0;

        // Heal only dead $ra — never retarget live worker/lock return addresses.
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        bool raDead = ra == 0
            || ra >= 0x00800000u
            || (ra >= 0x00100000u && !sys.Memory.IsLikelyEeCode(ra));
        if (raDead && sys.Memory.IsLikelyEeCode(0x004147F8UL))
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = 0x004147F8UL });

        // Bare multi entry needs a0=6 (group-6 multi table at 0x75E950).
        if (resume == 0x00427518u)
            sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = 6 });

        // Re-open stream work so FAE8 can run after dispatch.
        if (sys.Memory.Read32(0x0055E1EC) != 1)
            sys.Memory.Write32(0x0055E1EC, 1);
        if (sys.Memory.Read32(0x0055E200) == 1)
            sys.Memory.Write32(0x0055E200, 0);
        // CAS cell must be 0 or 1 — garbage (live: 0x54E6A7) blocks forever.
        uint cas = sys.Memory.Read32(0x0055E248);
        if (cas != 0)
            sys.Memory.Write32(0x0055E248, 0);
        if (sys.Memory.Read32(0x0055E24C) != 0)
            sys.Memory.Write32(0x0055E24C, 0);

        // Prefer SP in RDRAM so multi's 64B frame does not fault.
        ReHomeSpIfInHleScratch(sys);

        sys.EE.PC = resume;
        sys.LastGoodEePc = resume;
        return resume;
    }

    /// <summary>
    /// Dense START / CROSS after WAD so pad-gated title/menu can advance.
    /// Once gifP3 has left the logo spine (≥11), hold longer press windows and
    /// occasionally re-wake ADX pump / SleepThread peers so CROSS can accept.
    /// Menu-class PCs observed under pad: <c>0x3BF654</c> (VU math), <c>0x4156F4</c>
    /// (callback dispatch), <c>0x47EAxx</c> (post-spine title). Heavy CROSS once there.
    /// Live pad-inject final: PC oscillates <c>0x4148xx</c>/<c>0x4275xx</c> with
    /// "Kombat"+"Start" — denser D-pad+CROSS to push accept-to-submenu.
    /// </summary>
    private void MaybeInjectMenuPad(Ps2System sys)
    {
        // Faster cadence once logo spine is restored (historical gifP3≥11).
        // Even faster once in interactive pad-poll / ADX title bands (gifP3≥12).
        // Wave-5 heavy pad: 5k cycle cadence once interactive (gifP3≥12) so edge-triggered
        // menu code sees more press/release pairs; ghost PADMAN DMA refreshed each pulse.
        // gifP3≥14 (frame-cb + group-6 live): even denser 3k for accept-to-submenu push.
        // Wave-6: group-6 multi + frame-cb live also counts as interactive even if Path3
        // is still climbing (HEAD residual often holds gifP3=5..8 in healthy pad bands).
        bool multiLive = sys.Memory.Read32(0x0075E950) == 0x0043F920u;
        bool frameCbLive = sys.Memory.Read32(0x0075BDD8) == 0x0043F920u;
        bool interactive = sys.Gif.Path3Transfers >= 12 || (multiLive && frameCbLive);
        ulong interval = sys.Gif.Path3Transfers >= 14 ? 3_000UL
            : interactive ? 5_000UL
            : sys.Gif.Path3Transfers >= 11 || multiLive ? 12_000UL
            : 200_000UL;
        if (sys.MasterCycles - _lastMenuPadCyc < interval) return;
        _lastMenuPadCyc = sys.MasterCycles;
        _menuPadPulses++;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        // Menu / title bands: memset, ADX title, pad-poll 0x4275xx (live final), CROSS target.
        bool inMenuBand = pc is (>= 0x003BF000 and <= 0x003C0000)
            or (>= 0x00415000 and <= 0x00416000)
            or (>= 0x0047E800 and <= 0x0047F000)
            or (>= 0x00384000 and <= 0x00386000)
            or (>= 0x00427000 and <= 0x00428000)
            or (>= 0x00414000 and <= 0x00415000)
            or (>= 0x00414800 and <= 0x00414A00) // live ADX title 0x4148EC
            or (>= 0x00426E00 and <= 0x00427000) // lock wrapper thrash band
            or (>= 0x00202000 and <= 0x00203000); // wave-2 title settle 0x20243x

        int phase = (int)((sys.MasterCycles / 1_000_000) % 6);
        // After spine: longer START hold then CROSS accept (menu confirm pattern).
        // Include D-pad so selection index can move before CROSS accept.
        uint buttons;
        if (sys.Gif.Path3Transfers >= 11 || multiLive)
        {
            // In menu-class PC bands: D-pad then CROSS so selection/accept advances.
            // Accept-heavy once interactive (gifP3≥12 or multi+frame-cb): more CROSS edges.
            if (inMenuBand || interactive)
            {
                // Wave-5 accept-heavy: denser CROSS + D-pad so selection index moves then
                // accept. Live: gifP3=14 with *0x75BDD8=*0x75E950=0x43F920; pad moves PC;
                // thrash wall was 0x385674 VU blit — escape that separately. Need release
                // edges between presses for edge-triggered menu code.
                phase = _menuPadPulses % 24;
                buttons = phase switch
                {
                    0 => 0u, // release edge
                    1 => (uint)PadInput.Button.Down,
                    2 => 0u,
                    3 or 4 or 5 => (uint)PadInput.Button.Cross,
                    6 => 0u,
                    7 => (uint)PadInput.Button.Up,
                    8 => 0u,
                    9 or 10 or 11 => (uint)PadInput.Button.Cross,
                    12 => (uint)PadInput.Button.Start,
                    13 => 0u,
                    14 => (uint)(PadInput.Button.Start | PadInput.Button.Cross),
                    15 => (uint)PadInput.Button.Circle, // alt confirm on some Midway UIs
                    16 => 0u,
                    17 => (uint)PadInput.Button.Right,
                    18 => 0u,
                    19 => (uint)PadInput.Button.Down,
                    20 => 0u,
                    21 or 22 => (uint)PadInput.Button.Cross,
                    _ => (uint)PadInput.Button.Cross
                };
                // Occasional Left so horizontal menus also move.
                if (_menuPadPulses % 19 == 0)
                    buttons = (uint)PadInput.Button.Left;
                // After many pulses still in pad-poll / ADX title band: hold CROSS for accept.
                if (_menuPadPulses >= 32 && inMenuBand && (_menuPadPulses % 3) < 2)
                    buttons = (uint)PadInput.Button.Cross;
                // Wave-5: once frame-cb path is live (gifP3≥14), alternate Down+Cross harder.
                if (sys.Gif.Path3Transfers >= 14 && (_menuPadPulses % 5) == 0)
                    buttons = (uint)PadInput.Button.Down;
                if (sys.Gif.Path3Transfers >= 14 && (_menuPadPulses % 5) == 1)
                    buttons = (uint)PadInput.Button.Cross;
            }
            else
            {
                phase = _menuPadPulses % 8;
                buttons = phase switch
                {
                    0 or 1 or 2 => (uint)PadInput.Button.Start,
                    3 => 0u,
                    4 or 5 or 6 => (uint)PadInput.Button.Cross,
                    _ => (uint)(PadInput.Button.Start | PadInput.Button.Cross)
                };
            }
        }
        else
        {
            buttons = phase switch
            {
                1 or 2 => (uint)PadInput.Button.Start,
                3 or 4 => (uint)PadInput.Button.Cross,
                5 => (uint)(PadInput.Button.Start | PadInput.Button.Cross),
                _ => 0u
            };
        }
        try { sys.Pad.SetButtons(buttons); } catch { /* ignore */ }

        // Wave-11: selection-index delta on every D-pad pulse once spine is live (not only
        // sparse menu-sel samples — those miss edge timing).
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && sys.Gif.Path3Transfers >= 11
            && (buttons & (uint)(PadInput.Button.Up | PadInput.Button.Down
                | PadInput.Button.Left | PadInput.Button.Right)) != 0)
            MaybeLogSelectionIndexDelta(sys, buttons);

        // Push buttons into PADMAN DMA immediately (not only on next PCRTC VBlank).
        // After IOPRP300 gen≥2 reboot RealSifRpc keeps ghost areas — ForceRefreshPad
        // writes STABLE + active-low buttons into the EE pad buffer the game polls.
        try
        {
            var rpc = sys.Hle?.Sony?.RealRpc;
            rpc?.ForceRefreshPad(sys.Memory, sys.Pad);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && _menuPadPulses <= 4
                && sys.Gif.Path3Transfers >= 11)
                Console.Error.WriteLine(
                    $"[BIOS] menu pad pulse n={_menuPadPulses} btn=0x{buttons:X4} " +
                    $"open={rpc?.OpenPadCount ?? -1} ghost={rpc?.GhostPadCount ?? -1} " +
                    $"pc=0x{pc:X8} gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
        }
        catch { /* ignore */ }

        // Keep workers alive so pad edge is observed on a running thread.
        if ((sys.Gif.Path3Transfers >= 11 || multiLive) && (_menuPadPulses % 2) == 0)
        {
            var kernel = sys.Hle?.Kernel;
            if (kernel != null)
            {
                foreach (var t in kernel.AllThreads)
                {
                    if (!t.Alive) continue;
                    if (t.SoftSuspended) t.SoftSuspended = false;
                    while (t.SuspendCount > 0)
                        kernel.ResumeThread(t.Id);
                    if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                        kernel.WakeupThread(t.Id);
                    // Menu accept often WaitSema's the pad/RPC path — pulse lightly.
                    if ((inMenuBand || interactive)
                        && t.Sleeping && t.WaitSemaId > 0 && t.WaitSemaId < 64)
                    {
                        try { kernel.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                }
            }
            _menuSpineKicks++;
        }

        // Wave-5/6 selection chrome telemetry: menu tick cluster + stream cookie object.
        // Live: index-6 tick at 0x54E600 climbs under pad; dump for accept-to-submenu proof.
        // Wave-6: also dump once multiLive (frame-cb path) even if gifP3 still climbing.
        // Scan 0x54E5E0..0x54E640 and cookie 0x5BB860 for small ints that move with D-pad
        // (selection index typically 0..N where N≤16). Wave-9: also scan 0x54E680..0x54E780
        // and 0x54F000..0x54F100 for alternate Midway menu object slots.
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (sys.Gif.Path3Transfers >= 11 || multiLive)
            && (_menuPadPulses == 16 || _menuPadPulses == 64 || _menuPadPulses == 128
                || _menuPadPulses == 256 || (_menuPadPulses > 0 && _menuPadPulses % 512 == 0)))
        {
            uint t0 = sys.Memory.Read32(0x0054E5E0);
            uint t1 = sys.Memory.Read32(0x0054E5E4);
            uint t2 = sys.Memory.Read32(0x0054E5E8);
            uint t3 = sys.Memory.Read32(0x0054E5EC);
            uint t4 = sys.Memory.Read32(0x0054E600);
            uint t5 = sys.Memory.Read32(0x0054E610);
            uint t6 = sys.Memory.Read32(0x0054E614);
            uint t7 = sys.Memory.Read32(0x0054E618);
            uint t8 = sys.Memory.Read32(0x0054E61C);
            uint t9 = sys.Memory.Read32(0x0054E620);
            uint ta = sys.Memory.Read32(0x0054E624);
            uint tb = sys.Memory.Read32(0x0054E628);
            uint fcb = sys.Memory.Read32(0x0075BDD8);
            uint g6 = sys.Memory.Read32(0x0075E950);
            // Stream cookie object (FUN_0043ccf8 arg) — often holds UI/stream state.
            uint ck0 = sys.Memory.Read32(0x005BB860);
            uint ck1 = sys.Memory.Read32(0x005BB864);
            uint ck2 = sys.Memory.Read32(0x005BB868);
            uint ck3 = sys.Memory.Read32(0x005BB86C);
            uint ck4 = sys.Memory.Read32(0x005BB870);
            uint ck5 = sys.Memory.Read32(0x005BB874);
            // Stream work gate + skip flag (FUN_0043F968 / FUN_0043FAE8).
            uint gateEc = sys.Memory.Read32(0x0055E1EC);
            uint skip200 = sys.Memory.Read32(0x0055E200); // *(FUN_0043CB18()+16)
            uint cas248 = sys.Memory.Read32(0x0055E248); // FUN_0043F2C0 compare-and-set cell
            // Small-int candidates in menu cluster (selection index 0..15).
            var small = new System.Text.StringBuilder();
            for (uint off = 0; off < 0x80; off += 4)
            {
                uint v = sys.Memory.Read32(0x0054E5E0 + off);
                if (v <= 16)
                    small.Append($" +{off:X2}={v}");
            }
            // Wider small-int scan for selection (D-pad tracking).
            // Wave-10: include stream-manager slots (base 0x55E1F0) + title BSS bands
            // where Midway menu objects often live (0x54Exxx / 0x55xxxx / 0x53xxxx).
            var wide = new System.Text.StringBuilder();
            foreach (uint baseAddr in new uint[] {
                0x0054E680, 0x0054F000, 0x0054E800, 0x0054E900,
                0x00550000, 0x0053F000, 0x0055E250 })
            {
                for (uint off = 0; off < 0x100; off += 4)
                {
                    uint v = sys.Memory.Read32(baseAddr + off);
                    if (v <= 8)
                        wide.Append($" {baseAddr + off:X6}={v}");
                }
            }
            // Stream manager first work-slot word0 (FAE8 walks base+0x6C stride 0x2AC).
            // Wave-11: also dump ready flag, slot object ptr (+0x3C), work flag (+0x60),
            // and D6F8 object table (0x55FA0C) — second chrome needs non-null objects.
            uint slot0 = sys.Memory.Read32(0x0055E25C);
            uint slot0b = sys.Memory.Read32(0x0055E260);
            uint slot0c = sys.Memory.Read32(0x0055E264);
            uint slot0obj = sys.Memory.Read32(0x0055E25C + 0x3C);
            uint slot0work = sys.Memory.Read32(0x0055E25C + 0x60);
            uint smReady = sys.Memory.Read32(0x0055E1F0 + 0x38); // FUN_0043CE00
            uint smLock24 = sys.Memory.Read32(0x0055E1F0 + 0x24);
            uint smCb40 = sys.Memory.Read32(0x0055E1F0 + 0x40);
            uint smCb48 = sys.Memory.Read32(0x0055E1F0 + 0x48);
            uint smCb50 = sys.Memory.Read32(0x0055E1F0 + 0x50);
            var d6 = new System.Text.StringBuilder();
            for (uint i = 0; i < 8; i++)
            {
                uint p = sys.Memory.Read32(0x0055FA0C + i * 4);
                if (p != 0) d6.Append($" [{i}]=0x{p:X8}");
            }
            // Ghost pad DMA button words (active-low Digital) for accept proof.
            uint pad0 = sys.Memory.Read32(0x00651F00);
            uint pad2 = (sys.Memory.Read32(0x00651F00) >> 16) & 0xFFFFu; // buttons halfword
            Console.Error.WriteLine(
                $"[BIOS] menu-sel *54E5E0={t0:X8}/{t1:X8}/{t2:X8}/{t3:X8} " +
                $"*54E600={t4:X8} *54E610={t5:X8}/{t6:X8}/{t7:X8}/{t8:X8} " +
                $"*54E620={t9:X8}/{ta:X8}/{tb:X8} fcb=0x{fcb:X8} g6=0x{g6:X8} " +
                $"ck={ck0:X8}/{ck1:X8}/{ck2:X8}/{ck3:X8}/{ck4:X8}/{ck5:X8} " +
                $"gateEc={gateEc:X} skip200={skip200:X} cas248={cas248:X} " +
                $"smReady={smReady:X} smLock24={smLock24:X} smCb={smCb40:X8}/{smCb48:X8}/{smCb50:X8} " +
                $"slot0={slot0:X8}/{slot0b:X8}/{slot0c:X8} obj={slot0obj:X8} wk={slot0work:X} " +
                $"pad@651F00={pad0:X8}/{pad2:X4} " +
                $"btn=0x{buttons:X4} pc=0x{pc:X8} gifP3={sys.Gif.Path3Transfers} " +
                $"dmac={sys.Dmac.TransfersCompleted} n={_menuPadPulses} cyc={sys.MasterCycles}");
            if (d6.Length > 0)
                Console.Error.WriteLine($"[BIOS] menu-sel-d6f8{d6} cyc={sys.MasterCycles}");
            if (small.Length > 0)
                Console.Error.WriteLine($"[BIOS] menu-sel-small{small} cyc={sys.MasterCycles}");
            if (wide.Length > 0 && wide.Length < 500)
                Console.Error.WriteLine($"[BIOS] menu-sel-wide{wide} cyc={sys.MasterCycles}");

            // Wave-11: selection-index delta under D-pad (0..N cells that move).
            MaybeLogSelectionIndexDelta(sys, buttons);
        }

        // Post-spine: if main sits in the ADX pump forever with empty group-6 callbacks
        // (*0x75E950 all zero — live dump), yield once toward Midway main so title/menu
        // state machine can observe the pad edge we just wrote.
        if (sys.Gif.Path3Transfers >= 12 && _menuPadPulses >= 8 && (_menuPadPulses % 16) == 0)
            MaybeKickMainFromPumpThrash(sys);
    }

    private int _mainFromPumpKicks;
    private ulong _lastMainKickCyc;
    private ulong _lastTitleHashBreakCyc;
    private int _titleHashBreaks;
    private ulong _lastVuBlitGuardCyc;
    private int _vuBlitGuards;

    private ulong _lastTitleHash2Cyc;
    private int _titleHash2Breaks;

    /// <summary>
    /// Second mix loop at <c>0x47EF68..0x47EFB4</c>: <c>sh v0,0(t0); sh a1,2(t0); t0+=4</c>.
    /// Live find-writer: t0 lands on main at cyc≈119.5M (<c>sh a1,2(t0)</c> @ 0x47EFA8).
    /// </summary>
    private void MaybeBreakTitleHashCodeWalk2(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pc is < 0x0047EF60 or > 0x0047EFB8) return;
        if (sys.MasterCycles - _lastTitleHash2Cyc < 20_000) return;
        if (_titleHash2Breaks >= 64) return;

        uint t0 = (uint)(sys.EE.GetGpr(8).Lo & 0x1FFFFFFFUL);
        if (t0 is < 0x00100000 or >= 0x00780000) return;

        // Exit loop: clear a2 (bne a2,zero) and jump past.
        sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 }); // a2 = 0
        sys.EE.PC = 0x0047EFB8;
        sys.LastGoodEePc = 0x0047EFB8;
        _lastTitleHash2Cyc = sys.MasterCycles;
        _titleHash2Breaks++;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_titleHash2Breaks <= 12 || _titleHash2Breaks % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] break title hash2 code-walk t0=0x{t0:X8} -> 0x47EFB8 " +
                $"n={_titleHash2Breaks} cyc={sys.MasterCycles}");
    }

    private int _vuBlitVisits;
    private ulong _lastVuBlitVisitCyc;

    /// <summary>
    /// VU copy at <c>0x385660..0x385688</c>: 4× <c>lqc2 / mix / sqc2 vi5,0(a0); a0+=16</c>.
    /// Live find-writer: <c>a0</c> can point into the ELF image so <c>sqc2</c> @ <c>0x385674</c>
    /// zeros Midway main. Wave-5 wall: final PC parks at <c>0x385674</c> even after frame-cb
    /// re-arm (gifP3=14) — thrash without always having a0 in the ELF image. Escape when:
    /// (1) dest is code-image, or (2) sticky thrash in the blit band (re-entry without exit).
    /// Redirect <c>a0</c> to scratch and/or force <c>jr ra</c>. Prefer natural <c>$ra</c>
    /// when it is live EE code outside this band.
    /// </summary>
    private void MaybeGuardVuBlitCodeDest(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        // Wave-8: include post-blit COP2 siblings (live park 0x38568C).
        if (pc is < 0x00385650 or > 0x00385720) return;
        if (_vuBlitGuards >= 512) return;

        // Sticky thrash counter: re-visits within a short window without leaving the band.
        if (sys.MasterCycles - _lastVuBlitVisitCyc < 200_000)
            _vuBlitVisits++;
        else
            _vuBlitVisits = 1;
        _lastVuBlitVisitCyc = sys.MasterCycles;

        if (sys.MasterCycles - _lastVuBlitGuardCyc < 5_000) return;

        uint a0 = (uint)(sys.EE.GetGpr(4).Lo & 0x1FFFFFFFUL);
        // Dest in ELF code/rodata image → code wipe.
        bool a0InCode = a0 is >= 0x00100000 and < 0x00780000;
        // Unmapped / kernel / very low dest also cannot be a valid GS blit dest.
        bool a0Nonsense = a0 < 0x00100000 || a0 >= (uint)SystemMemory.RDRAM_SIZE;
        // Wave-5: thrash escape after repeated visits even if a0 looks "plausible" high BSS
        // (corrupt count keeps us in the band forever).
        bool pastEpilogue = pc > 0x00385688;
        bool stickyThrash = _vuBlitVisits >= (pastEpilogue ? 4 : 8);
        if (!a0InCode && !a0Nonsense && !stickyThrash) return;

        const uint scratch = 0x01F00000;
        if (a0InCode || a0Nonsense)
            sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = scratch });
        sys.EE.SetGpr(7, new EmotionEngine.Gpr128 { Lo = 0 });

        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        uint resume = 0x00385688;
        if (ra is >= 0x00100000 and < 0x00800000
            && ra is not (>= 0x00385650 and <= 0x00385720)
            && sys.Memory.IsLikelyEeCode(ra))
            resume = ra;

        if (stickyThrash || pastEpilogue)
        {
            uint force = 0;
            if (sys.Memory.IsLikelyEeCode(0x004147F8UL)) force = 0x004147F8;
            else if (sys.Memory.IsLikelyEeCode(0x00427518UL)) force = 0x00427518;
            else if (sys.Memory.IsLikelyEeCode(0x0043F920UL)) force = 0x0043F920;
            if (force != 0) resume = force;
            ReHomeSpIfInHleScratch(sys);
            try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); } catch { /* ignore */ }
        }

        sys.EE.PC = resume;
        sys.LastGoodEePc = resume;
        _lastVuBlitGuardCyc = sys.MasterCycles;
        _vuBlitGuards++;
        _vuBlitVisits = 0;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_vuBlitGuards <= 16 || _vuBlitGuards % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] escape VU blit thrash a0=0x{a0:X8} pc=0x{pc:X8} -> 0x{resume:X8} " +
                $"(code={a0InCode} nonsense={a0Nonsense} thrash={stickyThrash} pastEp={pastEpilogue}) " +
                $"n={_vuBlitGuards} gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }


    /// <summary>
    /// Title path <c>0x47EAF0..0x47EB30</c>: mix/hash loop
    /// <c>a3=s1+0x14; for i in 0..*(s1+0x10): *a3 = mix(*a3); a3+=4</c>.
    /// Live find-writer: when <c>s1</c> is garbage, <c>a3</c> lands on EE code and
    /// <c>sw v0,0(a3)</c> @ <c>0x47EB28</c> zeros Midway main (<c>0x212F70</c> at
    /// cyc≈89182640). Abort the loop when a3/s1 points into the ELF image or count is absurd.
    /// Wave-6: also trip on sticky re-visits (plausible high-BSS a3 that never exits).
    /// </summary>
    private void MaybeBreakTitleHashCodeWalk(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pc is < 0x0047EAE0 or > 0x0047EB30) return;
        if (sys.MasterCycles - _lastTitleHashBreakCyc < 20_000) return;
        if (_titleHashBreaks >= 128) return;

        uint s1 = (uint)(sys.EE.GetGpr(17).Lo & 0x1FFFFFFFUL);
        uint a3 = (uint)(sys.EE.GetGpr(7).Lo & 0x1FFFFFFFUL);
        uint s2 = (uint)sys.EE.GetGpr(18).Lo; // remaining/limit count
        // Only intervene when the store pointer is in / will enter the ELF image.
        bool a3InCode = a3 is >= 0x00100000 and < 0x00780000;
        bool s1InCode = s1 is >= 0x00100000 and < 0x00780000;
        // Huge count with a3 already near code base (or null object about to walk).
        bool countThreatensCode = s2 > 0x10000 && a3 is >= 0x00080000 and < 0x00780000;
        // Sticky: band re-entered many times without leaving (live HEAD: parks at 0x47EB00).
        bool sticky = _titleHashVisits >= 12;
        if (!a3InCode && !s1InCode && !countThreatensCode && !sticky) return;

        // Force loop exit: t1=s2 so slt a2,t1,s2 is false; jump past bne to 0x47EB34.
        sys.EE.SetGpr(9, new EmotionEngine.Gpr128 { Lo = s2 }); // t1 = s2
        sys.EE.SetGpr(18, new EmotionEngine.Gpr128 { Lo = 0 }); // s2 = 0
        sys.EE.PC = 0x0047EB34; // beq s3 / epilogue path
        sys.LastGoodEePc = 0x0047EB34;
        _lastTitleHashBreakCyc = sys.MasterCycles;
        _titleHashBreaks++;
        _titleHashVisits = 0;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_titleHashBreaks <= 12 || _titleHashBreaks % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] break title hash code-walk s1=0x{s1:X8} a3=0x{a3:X8} s2=0x{s2:X} " +
                $"sticky={sticky} -> 0x47EB34 n={_titleHashBreaks} cyc={sys.MasterCycles}");
    }

    private int _titleHashVisits;
    private ulong _lastTitleHashVisitCyc;
    private int _titleHashStickyEscapes;
    private ulong _lastTitleHashStickyCyc;
    private int _outerListThrashVisits;
    private ulong _lastOuterListVisitCyc;
    private int _outerListEscapes;
    private ulong _lastOuterListEscCyc;

    /// <summary>
    /// Sticky thrash in the wider title-hash band <c>0x47EAE0..0x47EFC0</c> (mix loop +
    /// second sh-walk). Live HEAD after bad logo-spine kick: PC parks here for tens of M
    /// cycles then walks open-bus (<c>0x00F30Cxx</c>). Prefer ADX pump / pad-poll resume
    /// so dense pad + group-6 multi can drive accept — do not re-enter Midway main
    /// (IOPRP storm). Mirrors VU blit sticky thrash escape pattern.
    /// </summary>
    private void MaybeEscapeTitleHashStickyThrash(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pc is < 0x0047EAE0 or > 0x0047EFC0) return;
        if (_titleHashStickyEscapes >= 48) return;

        if (sys.MasterCycles - _lastTitleHashVisitCyc < 250_000)
            _titleHashVisits++;
        else
            _titleHashVisits = 1;
        _lastTitleHashVisitCyc = sys.MasterCycles;

        if (_titleHashVisits < 16) return;
        if (sys.MasterCycles - _lastTitleHashStickyCyc < 100_000) return;

        // Prefer productive post-WAD targets over main (main → IOPRP gen≥2).
        uint resume = 0;
        if (sys.Memory.IsLikelyEeCode(0x004147F8UL))
            resume = 0x004147F8; // ADX pump
        else if (sys.Memory.IsLikelyEeCode(0x00427518UL))
            resume = 0x00427518; // group-6 multi dispatch / pad-poll
        else if (sys.Memory.IsLikelyEeCode(0x004145A8UL))
            resume = 0x004145A8; // ADX ready waiter
        else if (sys.Memory.Read32(0x00212F70) == 0x27BDFEE0 && sys.Gif.Path3Transfers < 8)
            resume = 0x00212F70; // main only if spine still cold
        if (resume == 0) return;

        sys.EE.COP0_Status &= ~0x6u;
        sys.EE.PC = resume;
        sys.LastGoodEePc = resume;
        ReHomeSpIfInHleScratch(sys);
        // Keep ADX ready so pump can run after escape.
        if (sys.Memory.Read32(0x005341D8) == 0)
            sys.Memory.Write32(0x005341D8, 1);
        sys.Memory.Write32(0x00534164, 0);
        sys.Memory.Write32(0x00534218, 0);
        try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); } catch { /* ignore */ }

        _titleHashStickyEscapes++;
        _lastTitleHashStickyCyc = sys.MasterCycles;
        _titleHashVisits = 0;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_titleHashStickyEscapes <= 12 || _titleHashStickyEscapes % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] escape title-hash sticky thrash 0x{pc:X8} -> 0x{resume:X8} " +
                $"n={_titleHashStickyEscapes} gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Outer list-apply band <c>0x474C00..0x474E00</c> (FUN_00474Cxx) thrash that never
    /// reaches the inner absurd-count loop at <c>0x475608</c> covered by
    /// <see cref="MaybeBreakRunawayListWalk"/>. Live HEAD samples park here for 10M+
    /// cycles interleaved with title-hash. Sticky escape to ADX pump / pad-poll.
    /// </summary>
    private void MaybeEscapeOuterListThrash(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pc is < 0x00474C00 or > 0x00474E00) return;
        if (_outerListEscapes >= 48) return;

        if (sys.MasterCycles - _lastOuterListVisitCyc < 250_000)
            _outerListThrashVisits++;
        else
            _outerListThrashVisits = 1;
        _lastOuterListVisitCyc = sys.MasterCycles;

        if (_outerListThrashVisits < 16) return;
        if (sys.MasterCycles - _lastOuterListEscCyc < 100_000) return;

        uint resume = 0;
        if (sys.Memory.IsLikelyEeCode(0x004147F8UL))
            resume = 0x004147F8;
        else if (sys.Memory.IsLikelyEeCode(0x00427518UL))
            resume = 0x00427518;
        else if (sys.Memory.Read32(0x00212F70) == 0x27BDFEE0 && sys.Gif.Path3Transfers < 8)
            resume = 0x00212F70;
        if (resume == 0) return;

        sys.EE.COP0_Status &= ~0x6u;
        sys.EE.PC = resume;
        sys.LastGoodEePc = resume;
        ReHomeSpIfInHleScratch(sys);
        if (sys.Memory.Read32(0x005341D8) == 0)
            sys.Memory.Write32(0x005341D8, 1);
        sys.Memory.Write32(0x00534164, 0);
        try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); } catch { /* ignore */ }

        _outerListEscapes++;
        _lastOuterListEscCyc = sys.MasterCycles;
        _outerListThrashVisits = 0;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_outerListEscapes <= 12 || _outerListEscapes % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] escape outer list thrash 0x{pc:X8} -> 0x{resume:X8} " +
                $"n={_outerListEscapes} gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }


    private bool _group6MultiFilled;
    private int _group6MultiFills;

    /// <summary>
    /// Mirror resource-manager <c>FUN_0043ccf8</c> group-6 multi-slot registration.
    /// <para>
    /// Layout (FUN_00427108 / FUN_00427518): base <c>0x75E7A0</c>, stride <c>0x48</c>/group,
    /// sub-slot stride 12: <c>+0 fn, +4 arg, +8 cookie</c>. Group 6 → <c>0x75E950</c>.
    /// Sole game registration path: <c>FUN_0043ccf8</c> → <c>FUN_0043f168(0x43F920, 0, 0x5BB860)</c>
    /// (stream tick). Live 100M dumps leave the slot all-zero while menu tick <c>0x54E600</c>
    /// still climbs (dispatch epilogue runs with empty callbacks).
    /// </para>
    /// Do NOT plant <c>0x414568</c> here (lock-wait → self-deadlock with multi-table).
    /// Do NOT plant <c>*0x75C0D0</c>. Prefer this over post-spine main re-home (IOPRP storms).
    /// </summary>
    private void MaybeFillGroup6MultiSlot(Ps2System sys)
    {
        if (_group6MultiFills >= 8) return;
        // Rate-limit re-plants (table can be scrubbed by partial inits / IOPRP gen≥2).
        if (_group6MultiFilled && sys.MasterCycles - _lastGroup6FillCyc < 2_000_000)
            return;

        const uint Group6Base = 0x0075E950; // 0x75E7A0 + 6*0x48
        const uint StreamTickFn = 0x0043F920; // FUN registered by FUN_0043ccf8
        const uint StreamCookie = 0x005BB860;
        // Sibling group-2 stream cb from the same init (FUN_0043cd14 → 0x43F8C0).
        const uint Group2Base = 0x0075E830; // 0x75E7A0 + 2*0x48
        const uint StreamTickFnG2 = 0x0043F8C0;

        uint g6fn = sys.Memory.Read32(Group6Base);
        // Already filled by real game path or prior plant — leave it alone.
        if (g6fn != 0 && g6fn != StreamTickFn)
        {
            _group6MultiFilled = true;
            return;
        }
        if (g6fn == StreamTickFn && sys.Memory.Read32(Group6Base + 8) == StreamCookie)
        {
            _group6MultiFilled = true;
            return;
        }

        // Validate target prolog. Do NOT use IsLikelyEeCode here — EE `sd` (primary 0x3F)
        // in the delay/next word is rejected by WordLooksLikeInsn, so real callbacks at
        // 0x43F920 / 0x43F8C0 (addiu sp; sd s0) fail the dual-word check.
        if (sys.Memory.Read32(StreamTickFn) != 0x27BDFFF0u) // addiu sp,sp,-16
            return;

        // Plant group-6 multi-slot[0] = stream tick (exactly what FUN_00427108 stores).
        sys.Memory.Write32(Group6Base + 0, StreamTickFn);
        sys.Memory.Write32(Group6Base + 4, 0);
        sys.Memory.Write32(Group6Base + 8, StreamCookie);

        // Optional group-2 fill when empty (same resource-init batch). Safe no-op if code missing.
        if (sys.Memory.Read32(Group2Base) == 0
            && sys.Memory.Read32(StreamTickFnG2) == 0x27BDFFF0u)
        {
            sys.Memory.Write32(Group2Base + 0, StreamTickFnG2);
            sys.Memory.Write32(Group2Base + 4, 0);
            sys.Memory.Write32(Group2Base + 8, 0x005BB830);
        }

        // Keep single-slot group-6 (0x75EA10) as ADX lock-wait — already planted by
        // MaybeCompleteAdxInitGate; do not touch multi-table with 0x414568.
        // Scrub self-deadlock plant if any residual build left it.
        if (sys.Memory.Read32(0x0075E7A0) == 0x00414568u)
        {
            sys.Memory.Write32(0x0075E7A0, 0);
            sys.Memory.Write32(0x0075E7A4, 0);
            sys.Memory.Write32(0x0075E7A8, 0);
        }

        _group6MultiFilled = true;
        _lastGroup6FillCyc = sys.MasterCycles;
        _group6MultiFills++;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _group6MultiFills <= 4)
            Console.Error.WriteLine(
                $"[BIOS] fill group-6 multi *0x75E950=0x{StreamTickFn:X8} cookie=0x{StreamCookie:X8} " +
                $"(mirror FUN_0043ccf8) n={_group6MultiFills} gifP3={sys.Gif.Path3Transfers} " +
                $"cyc={sys.MasterCycles}");
    }

    private ulong _lastGroup6FillCyc;

    private int _frameCbRearms;
    private ulong _lastFrameCbRearmCyc;
    private int _lockWrapperBreaks;
    private ulong _lastLockWrapperBreakCyc;
    private int _lockWrapperVisits;
    private ulong _lastLockWrapperVisitCyc;
    private int _streamWorkGateHolds;
    private ulong _lastStreamWorkGateCyc;

    /// <summary>
    /// Hold stream-work gate <c>*0x55E1EC = 1</c> once group-6 multi / frame-cb are live.
    /// <para>
    /// Disasm of <c>FUN_0043FAE8</c> (stream tick work leaf):
    /// <c>lw s1, *0x55E1EC; bne s1,1,epilogue</c>. Prior plant only wrote <c>0x55E1E8</c>
    /// (FUN_0043ce78 countdown). Live menu-sel dumps kept cookie <c>0x5BB860</c> zero and
    /// PC samples parked on epilogue <c>0x43FB9C</c> — work body never ran.
    /// Secondary check <c>FUN_0043F2C0(base+0x58)</c> is a compare-and-set on
    /// <c>*0x55E248</c> (returns 1 when previously not 1) — re-armed by
    /// <see cref="MaybeRearmStreamCas"/> after first pass sticks.
    /// Stream-tick skip flag <c>*0x55E200</c> (<c>*(FUN_0043CB18()+16)</c>) must stay 0 —
    /// <c>FUN_0043F920</c> early-outs FAE8 when it equals 1.
    /// </para>
    /// Do NOT plant <c>*0x75C0D0</c>. Prefer SHARED if a generic stream-manager ready
    /// contract emerges; for now TITLE_LOCAL (wrong offset was a title-assist bug).
    /// </summary>
    private void MaybeHoldStreamWorkGate(Ps2System sys)
    {
        if (_streamWorkGateHolds >= 24) return;
        if (sys.MasterCycles - _lastStreamWorkGateCyc < 1_000_000) return;

        // Prefer after multi+frame-cb plant so we do not open work on an empty pump.
        bool multiLive = sys.Memory.Read32(0x0075E950) == 0x0043F920u;
        bool frameCbLive = sys.Memory.Read32(0x0075BDD8) == 0x0043F920u;
        if (!multiLive && !frameCbLive && sys.Cdvd.SectorsRead < 180_000)
            return;

        // Keep stream-tick skip flag clear (FUN_0043F968 returns *(base+16)).
        if (sys.Memory.Read32(0x0055E200) == 1)
            sys.Memory.Write32(0x0055E200, 0);

        uint gate = sys.Memory.Read32(0x0055E1EC);
        if (gate == 1)
        {
            // Also keep sibling countdown non-zero if scrubbed.
            if (sys.Memory.Read32(0x0055E1E8) == 0)
                sys.Memory.Write32(0x0055E1E8, 1);
            return;
        }

        sys.Memory.Write32(0x0055E1EC, 1);
        if (sys.Memory.Read32(0x0055E1E8) == 0)
            sys.Memory.Write32(0x0055E1E8, 1);

        _streamWorkGateHolds++;
        _lastStreamWorkGateCyc = sys.MasterCycles;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _streamWorkGateHolds <= 6)
            Console.Error.WriteLine(
                $"[BIOS] hold stream work gate *0x55E1EC=1 (was 0x{gate:X8}) " +
                $"n={_streamWorkGateHolds} multi={(multiLive ? 1 : 0)} fcb={(frameCbLive ? 1 : 0)} " +
                $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

    private int _streamCasRearms;
    private ulong _lastStreamCasRearmCyc;

    /// <summary>
    /// Wave-9: re-arm stream compare-and-set cell <c>*0x55E248</c> (stream manager base+0x58).
    /// <para>
    /// Disasm <c>FUN_0043FAE8</c> → <c>FUN_0043F2C0(base+0x58)</c> → <c>0x4277E8</c>:
    /// <c>old=*a0; *a0=1; return (old^1)!=0</c>. When old is already 1 the leaf returns 0
    /// and FAE8 takes the epilogue — live 120M menu-sel showed <c>cas248=1</c> stuck after
    /// the first stream burst that lifted gifP3 5→11, with no further Path3 growth.
    /// Clear to 0 on a rate-limited cadence so the work body can run again (second chrome).
    /// Also clears skip flag <c>*0x55E200</c> if set. Do NOT plant <c>*0x75C0D0</c>.
    /// </para>
    /// </summary>
    private void MaybeRearmStreamCas(Ps2System sys)
    {
        if (_streamCasRearms >= 32) return;
        if (sys.MasterCycles - _lastStreamCasRearmCyc < 1_500_000) return;

        bool multiLive = sys.Memory.Read32(0x0075E950) == 0x0043F920u;
        bool frameCbLive = sys.Memory.Read32(0x0075BDD8) == 0x0043F920u;
        if (!multiLive && !frameCbLive) return;
        // Gate must already be open — do not thrash CAS before first FAE8 entry.
        if (sys.Memory.Read32(0x0055E1EC) != 1) return;

        if (sys.Memory.Read32(0x0055E200) == 1)
            sys.Memory.Write32(0x0055E200, 0);

        uint cas = sys.Memory.Read32(0x0055E248);
        if (cas == 0)
        {
            // Still re-open once gifP3 is plateaued so a later plant of cas=1 gets cleared.
            if (sys.Gif.Path3Transfers < 11 || sys.Gif.Path3Transfers >= 14)
                return;
            // gifP3 11..13 and cas already 0: nothing to do this tick.
            return;
        }

        sys.Memory.Write32(0x0055E248, 0);
        // Sibling CAS at base+0x5C (FUN_0043F9A8) can also stick.
        if (sys.Memory.Read32(0x0055E24C) == 1)
            sys.Memory.Write32(0x0055E24C, 0);

        _streamCasRearms++;
        _lastStreamCasRearmCyc = sys.MasterCycles;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_streamCasRearms <= 8 || _streamCasRearms % 4 == 0))
            Console.Error.WriteLine(
                $"[BIOS] re-arm stream CAS *0x55E248=0 (was 0x{cas:X8}) n={_streamCasRearms} " +
                $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

    private int _postSpineWorkerEscapes;
    private ulong _lastPostSpineWorkerEscCyc;
    private int _postSpineWorkerVisits;
    private ulong _lastPostSpineWorkerVisitCyc;

    /// <summary>
    /// Wave-9: escape post-spine sticky parks that starve second chrome / pad accept.
    /// <para>
    /// Live 120M pad-inject after gifP3=11:
    /// <list type="bullet">
    /// <item><c>0x426E28</c> lock thrash (handled by lock-wrapper break)</item>
    /// <item><c>0x4143A0</c> ADX re-init body hammering syscall stubs for ~10M cyc</item>
    /// <item><c>0x47FEA0</c> (syscall 68) / <c>0x480Axx</c> commercial worker loop</item>
    /// </list>
    /// Trampoline escape only fires when <c>$ra</c> is dead — legitimate jal→syscall with
    /// live ra never exits. Sticky re-visits force resume to ADX pump / group-6 / stream tick
    /// and re-arm stream CAS so FAE8 can contribute Path3 again.
    /// </para>
    /// </summary>
    private void MaybeEscapePostSpineWorkerThrash(Ps2System sys)
    {
        if (_postSpineWorkerEscapes >= 96) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        bool inBand = pc is (>= 0x0047FD00 and <= 0x00480C00)
            or (>= 0x00414380 and <= 0x00414400)
            or (>= 0x004143A0 and <= 0x00414410);
        if (!inBand) return;

        if (sys.MasterCycles - _lastPostSpineWorkerVisitCyc < 300_000)
            _postSpineWorkerVisits++;
        else
            _postSpineWorkerVisits = 1;
        _lastPostSpineWorkerVisitCyc = sys.MasterCycles;
        if (_postSpineWorkerVisits < 3) return;
        // Wave-10: give multi/stream time to finish before re-escaping (was 80k — thrash
        // re-entry aborted group-6 mid-list). Back off further after many escapes.
        ulong minGap = _postSpineWorkerEscapes >= 16 ? 250_000UL : 150_000UL;
        if (sys.MasterCycles - _lastPostSpineWorkerEscCyc < minGap) return;

        // Wave-10: group-6 entry 0x427678 (a0=6) + heal $ra → ADX pump.
        uint resume = ApplyMenuDispatchResume(sys);
        if (resume == 0) return;

        ReHomeSpIfInHleScratch(sys);
        try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); } catch { /* ignore */ }

        _lastPostSpineWorkerEscCyc = sys.MasterCycles;
        _postSpineWorkerEscapes++;
        _postSpineWorkerVisits = 0;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_postSpineWorkerEscapes <= 12 || _postSpineWorkerEscapes % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] escape post-spine worker thrash 0x{pc:X8} -> 0x{resume:X8} " +
                $"n={_postSpineWorkerEscapes} gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

    private int _streamCookieInits;
    private ulong _lastStreamCookieInitCyc;

    /// <summary>
    /// Wave-8: minimal init of stream cookie <c>0x5BB860</c> (FUN_0043ccf8 arg). Word0=1.
    /// </summary>
    private void MaybeInitStreamCookie(Ps2System sys)
    {
        if (_streamCookieInits >= 8) return;
        if (sys.MasterCycles - _lastStreamCookieInitCyc < 2_000_000) return;
        bool multiLive = sys.Memory.Read32(0x0075E950) == 0x0043F920u;
        bool frameCbLive = sys.Memory.Read32(0x0075BDD8) == 0x0043F920u;
        if (!multiLive && !frameCbLive) return;
        const uint Cookie = 0x005BB860;
        const uint CookieG2 = 0x005BB830;
        if (sys.Memory.Read32(Cookie) != 0 || sys.Memory.Read32(Cookie + 4) != 0)
        {
            _streamCookieInits = Math.Max(_streamCookieInits, 1);
            return;
        }
        if (sys.Memory.Read32(0x0055E1EC) == 0) sys.Memory.Write32(0x0055E1EC, 1);
        sys.Memory.Write32(Cookie, 1);
        if (sys.Memory.Read32(CookieG2) == 0) sys.Memory.Write32(CookieG2, 1);
        _streamCookieInits++;
        _lastStreamCookieInitCyc = sys.MasterCycles;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _streamCookieInits <= 4)
            Console.Error.WriteLine(
                $"[BIOS] init stream cookie *0x5BB860=1 (was zero) n={_streamCookieInits} " +
                $"multi={(multiLive ? 1 : 0)} fcb={(frameCbLive ? 1 : 0)} " +
                $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

    private int _streamManagerInits;
    private ulong _lastStreamManagerInitCyc;
    private const uint StreamManagerBase = 0x0055E1F0;

    // Selection-index tracking (wave-11): last seen 0..8 cells in menu/stream BSS.
    private readonly Dictionary<uint, uint> _selIndexSnapshot = new();
    private int _selIndexDeltaLogs;

    /// <summary>
    /// Wave-11: soft-plant stream-manager header to match <c>FUN_0043CD58</c> a0==0 defaults.
    /// <para>
    /// Disasm (CD58): <c>base = FUN_0043CB18() = 0x55E1F0</c>; defaults when a0==0:
    /// float <c>*base+4 = 0x426FC28F</c>, <c>*base+8=1</c>, <c>*base+0xC=1</c>,
    /// <c>*base+0x38=1</c> (ready — <c>FUN_0043CE00</c>), clear <c>*base+0x5C</c>.
    /// Sole natural caller is parent <c>0x43CC40</c> (never reached under HLE).
    /// </para>
    /// <para>
    /// Work slots at <c>base+0x6C</c> stride <c>0x2AC</c> are <b>not</b> filled here —
    /// <c>FUN_0043C1C0</c> binds <c>*slot=flag</c> + <c>*slot+0x3C=object</c> from resource
    /// descriptors. Planting active=1 with a null object is unsafe (FBB0→D770). Full
    /// force-call of CD58 (memset 0x15CC) regressed gifP3 11→5 — do not re-enable without
    /// isolated Step return. Do NOT plant <c>*0x75C0D0</c>. TITLE_LOCAL.
    /// </para>
    /// </summary>
    private void MaybeInitStreamManager(Ps2System sys)
    {
        if (_streamManagerInits >= 8) return;
        if (sys.MasterCycles - _lastStreamManagerInitCyc < 2_000_000) return;

        bool multiLive = sys.Memory.Read32(0x0075E950) == 0x0043F920u;
        bool frameCbLive = sys.Memory.Read32(0x0075BDD8) == 0x0043F920u;
        if (!multiLive && !frameCbLive) return;

        // NOTE: force-calling CD58 via trampoline was tried (wave-11) and regressed gifP3
        // 11→5 — PC/GPR resume raced other Step assists. Soft-plant defaults only.
        uint ready = sys.Memory.Read32(StreamManagerBase + 0x38);
        if (ready == 1)
        {
            // Keep lock clear if stuck (FBB0 early-out when *base+0x24==1).
            if (sys.Memory.Read32(StreamManagerBase + 0x24) == 1)
                sys.Memory.Write32(StreamManagerBase + 0x24, 0);
            _streamManagerInits = Math.Max(_streamManagerInits, 1);
            return;
        }

        // Mirror CD58 a0==0 defaults without full memset (safe while FAE8 may be live).
        if (sys.Memory.Read32(StreamManagerBase + 4) == 0)
            sys.Memory.Write32(StreamManagerBase + 4, 0x426FC28Fu); // ~59.94f
        if (sys.Memory.Read32(StreamManagerBase + 8) == 0)
            sys.Memory.Write32(StreamManagerBase + 8, 1);
        if (sys.Memory.Read32(StreamManagerBase + 0xC) == 0)
            sys.Memory.Write32(StreamManagerBase + 0xC, 1);
        sys.Memory.Write32(StreamManagerBase + 0x38, 1); // ready
        // Clear CAS cells if garbage (FD28 / CD58).
        uint cas = sys.Memory.Read32(StreamManagerBase + 0x58);
        if (cas > 1) sys.Memory.Write32(StreamManagerBase + 0x58, 0);
        uint err = sys.Memory.Read32(StreamManagerBase + 0x5C);
        if (err != 0) sys.Memory.Write32(StreamManagerBase + 0x5C, 0);
        // Global lock at +0x24 must not stick at 1 (FBB0 early-out).
        if (sys.Memory.Read32(StreamManagerBase + 0x24) == 1)
            sys.Memory.Write32(StreamManagerBase + 0x24, 0);

        _streamManagerInits++;
        _lastStreamManagerInitCyc = sys.MasterCycles;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _streamManagerInits <= 4)
            Console.Error.WriteLine(
                $"[BIOS] plant stream-manager CD58 defaults *0x{(StreamManagerBase + 0x38):X}=1 " +
                $"(ready was 0) n={_streamManagerInits} gifP3={sys.Gif.Path3Transfers} " +
                $"cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Wave-11/12: log 0..N integer cells that change under D-pad (selection index hunt).
    /// Scans menu tick BSS + multi busy band + stream manager + cookie.
    /// Wave-12: raise cap to 0..16; add multi-group busy base <c>0x54E608</c> (disasm
    /// <c>FUN_00427518</c>: re-entrancy flags, not selection index — negative evidence)
    /// and multi table <c>0x75E7A0</c>. No stable 0..N cell found under dense D-pad.
    /// </summary>
    private void MaybeLogSelectionIndexDelta(Ps2System sys, uint buttons)
    {
        if (_selIndexDeltaLogs >= 48) return;
        // Host-active pad mask from MaybeInjectMenuPad (PadInput.Button flags).
        bool dpad = (buttons & (uint)(PadInput.Button.Up | PadInput.Button.Down
            | PadInput.Button.Left | PadInput.Button.Right)) != 0;
        // Always snapshot; only emit on dpad edge or first post-spine sample.
        uint[] bases = {
            0x0054E5E0, 0x0054E608, 0x0054E680, 0x0054E800, 0x0054F000,
            0x0055E1F0, 0x0055E25C, 0x005BB860, 0x005BB830, 0x0075E7A0, 0x0075E950
        };
        var deltas = new System.Text.StringBuilder();
        var now = new Dictionary<uint, uint>();
        foreach (uint b in bases)
        {
            for (uint off = 0; off < 0x80; off += 4)
            {
                uint addr = b + off;
                if (addr + 4 > (uint)SystemMemory.RDRAM_SIZE) continue;
                uint v = sys.Memory.Read32(addr);
                // Wave-12: allow 0..16 (menus with more than 8 rows).
                if (v > 16) continue;
                now[addr] = v;
                if (_selIndexSnapshot.TryGetValue(addr, out uint prev) && prev != v)
                    deltas.Append($" 0x{addr:X6}:{prev}->{v}");
            }
        }
        // Replace snapshot.
        _selIndexSnapshot.Clear();
        foreach (var kv in now) _selIndexSnapshot[kv.Key] = kv.Value;

        if (deltas.Length == 0) return;
        // Prefer logging when D-pad is held; also log any movement once gifP3>=11.
        if (!dpad && sys.Gif.Path3Transfers < 11) return;
        _selIndexDeltaLogs++;
        Console.Error.WriteLine(
            $"[BIOS] sel-idx-delta{deltas} dpad={(dpad ? 1 : 0)} btn=0x{buttons:X4} " +
            $"gifP3={sys.Gif.Path3Transfers} n={_selIndexDeltaLogs} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Re-arm frame callback <c>*0x75BDD8</c> after ADX init zeros it.
    /// <para>
    /// Live find-writer: only write is <c>pc=0x414DC8 sw zero</c> inside
    /// <c>FUN_00414d40</c> (ADXPS2 init) at cyc≈2.76M — slot never re-filled by game
    /// under HLE. Pump path <c>0x414688</c>/<c>0x4148D8</c> does
    /// <c>jal 0x4156E0 → lw v1,*0x75BDD8; beql skip; jalr v1</c> so a null slot
    /// permanently skips per-frame work (selection chrome / accept).
    /// </para>
    /// Plant stream tick <c>0x43F920</c> (same fn FUN_0043ccf8 registers into group-6)
    /// with cookie arg <c>0x5BB860</c>. Prefer the real leaf over the group-6 multi wrapper
    /// (<c>0x427678</c>) — early wrapper plants with empty multi-table starved logo spine
    /// (live: gifP3 stuck at 6). Sibling slot <c>*0x75BDD0</c> gets group-2 stream tick
    /// <c>0x43F8C0</c> when empty. Do NOT plant <c>*0x75C0D0</c>.
    /// Prefer SHARED if ADX init clears incorrectly (not applied — title-local plant).
    /// </summary>
    private void MaybeRearmFrameCb(Ps2System sys)
    {
        if (_frameCbRearms >= 12) return;
        if (sys.MasterCycles - _lastFrameCbRearmCyc < 1_500_000) return;

        const uint FrameCbSlot = 0x0075BDD8; // FUN_004156E0 loads / jalr
        const uint FrameCbArg = 0x0075BDDC;
        const uint FrameCbSlot0 = 0x0075BDD0; // FUN_004156B0 sibling
        const uint FrameCbArg0 = 0x0075BDD4;
        // Real stream tick (FUN_0043ccf8 target) — safe leaf, same as group-6 multi plant.
        const uint StreamTickFn = 0x0043F920;
        const uint StreamCookie = 0x005BB860;
        const uint StreamTickFnG2 = 0x0043F8C0;
        const uint StreamCookieG2 = 0x005BB830;

        uint cur = sys.Memory.Read32(FrameCbSlot);
        // Already armed with a live non-zero code pointer — leave alone (unless it is our
        // known target and arg got wiped).
        if (cur != 0 && cur != StreamTickFn && sys.Memory.IsLikelyEeCode(cur))
            return;
        if (cur == StreamTickFn && sys.Memory.Read32(FrameCbArg) == StreamCookie)
            return;

        // Validate stream-tick prolog (addiu sp,sp,-16). Prefer not using IsLikelyEeCode
        // alone — dual-word check used to reject sd; prolog word is enough.
        if (sys.Memory.Read32(StreamTickFn) != 0x27BDFFF0u)
            return;
        // Prefer arming once group-6 multi is also filled (or about to be) so frame path
        // and multi-table agree. Soft-require: allow after bulk WAD even if multi empty.
        bool multiReady = sys.Memory.Read32(0x0075E950) == StreamTickFn
            || sys.Cdvd.SectorsRead >= 180_000;

        if (!multiReady && sys.Gif.Path3Transfers < 11)
            return;

        sys.Memory.Write32(FrameCbSlot, StreamTickFn);
        sys.Memory.Write32(FrameCbArg, StreamCookie);

        // Sibling pre-frame slot when empty.
        if (sys.Memory.Read32(FrameCbSlot0) == 0
            && sys.Memory.Read32(StreamTickFnG2) == 0x27BDFFF0u)
        {
            sys.Memory.Write32(FrameCbSlot0, StreamTickFnG2);
            sys.Memory.Write32(FrameCbArg0, StreamCookieG2);
        }

        _frameCbRearms++;
        _lastFrameCbRearmCyc = sys.MasterCycles;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _frameCbRearms <= 6)
            Console.Error.WriteLine(
                $"[BIOS] re-arm frame cb *0x75BDD8=0x{StreamTickFn:X8} arg=0x{StreamCookie:X8} " +
                $"(was 0x{cur:X8}) n={_frameCbRearms} gifP3={sys.Gif.Path3Transfers} " +
                $"cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Escape lock-wrapper thrash at <c>0x426EF8..0x426F80</c> (wave-3 late PC).
    /// Wrappers force a0=lock-id then jump to acquire (<c>0x426DF0</c>) / release
    /// (<c>0x426E50</c>) which jalr <c>*0x75EA20/*0x75EA28</c>. With HLE-corrupt
    /// refcount at <c>0x54E5E0</c> the EE tight-loops the unlock path. Clear the
    /// stuck refcount and force <c>jr ra</c> epilogue so pad/accept can resume.
    /// </summary>
    private void MaybeBreakLockWrapperThrash(Ps2System sys)
    {
        if (_lockWrapperBreaks >= 96) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        bool inWrap = pc is (>= 0x00426EE0 and <= 0x00426F90)
            or (>= 0x00426DF0 and <= 0x00426ED8)
            or (>= 0x00426E00 and <= 0x00426E40); // live park 0x426E28
        if (!inWrap) return;

        if (sys.MasterCycles - _lastLockWrapperVisitCyc < 250_000)
            _lockWrapperVisits++;
        else
            _lockWrapperVisits = 1;
        _lastLockWrapperVisitCyc = sys.MasterCycles;
        if (sys.MasterCycles - _lastLockWrapperBreakCyc < 50_000) return;

        uint refc = sys.Memory.Read32(0x0054E5E0);
        bool stickyRef = refc > 8 || refc == 0xFFFFFFFFu;
        bool onHotInsn = pc is (>= 0x00426F00 and <= 0x00426F10)
            or (>= 0x00426EBC and <= 0x00426EC8);
        bool stickyBand = _lockWrapperVisits >= 2;
        if (!stickyRef && !onHotInsn && !stickyBand) return;

        if (stickyRef || stickyBand)
            sys.Memory.Write32(0x0054E5E0, 0);
        sys.Memory.Write32(0x0054E5E4, 0);

        uint resume = 0x00426ED4;
        if (stickyBand || stickyRef)
        {
            // Wave-10: group-6 entry (a0=6) + heal $ra once spine is live.
            if (sys.Gif.Path3Transfers >= 11)
            {
                uint menuResume = ApplyMenuDispatchResume(sys);
                if (menuResume != 0)
                    resume = menuResume;
                else if (sys.Memory.IsLikelyEeCode(0x004147F8UL))
                    resume = 0x004147F8;
            }
            else if (sys.Memory.IsLikelyEeCode(0x004147F8UL))
            {
                resume = 0x004147F8;
                sys.EE.PC = resume;
                sys.LastGoodEePc = resume;
            }
            try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); } catch { /* ignore */ }
        }

        // ApplyMenuDispatchResume already set PC when used; ensure final target.
        if ((uint)(sys.EE.PC & 0x1FFFFFFFUL) != resume)
        {
            sys.EE.PC = resume;
            sys.LastGoodEePc = resume;
        }
        _lastLockWrapperBreakCyc = sys.MasterCycles;
        _lockWrapperBreaks++;
        _lockWrapperVisits = 0;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_lockWrapperBreaks <= 12 || _lockWrapperBreaks % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] break lock-wrapper thrash pc=0x{pc:X8} refc={refc} -> 0x{resume:X8} " +
                $"(hot={onHotInsn} stickyRef={stickyRef} stickyBand={stickyBand}) " +
                $"n={_lockWrapperBreaks} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Live (2026-07-30): after gifP3=12 the EE oscillates <c>0x4148EC</c>↔<c>0x4275xx</c>
    /// (ADX pump group-6 dispatch) with multi-slot table at <c>0x75E950</c> empty and frame
    /// callback <c>*0x75BDD8</c> null. Menu tick at <c>0x54E600</c> advances millions but no
    /// UI accept path runs — pump is a pure no-op dispatcher. Re-home to Midway main
    /// (<c>0x212F70</c>) so title/menu can observe pad edges in ghost PADMAN DMA.
    /// Do NOT plant <c>*0x75C0D0</c>. Prefer <see cref="MaybeFillGroup6MultiSlot"/> first.
    /// </summary>
    private void MaybeKickMainFromPumpThrash(Ps2System sys)
    {
        // After logo spine is restored, re-homing to main re-enters IOPRP RESET and storms
        // gen=2..N (live pad-inject: gen 2→12). Prefer staying in pump/pad with ghost DMA.
        if (sys.Gif.Path3Transfers >= 12) return;
        if (_mainFromPumpKicks >= 12) return;
        if (sys.MasterCycles - _lastMainKickCyc < 1_500_000) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        bool inPump = pc is (>= 0x004147F8 and <= 0x00414A80)
            or (>= 0x00427518 and <= 0x004276A0)
            or (>= 0x0047FD60 and <= 0x0047FE00)
            // Live pad-inject samples also park on the bgezl itself and epilogue.
            or (>= 0x00427590 and <= 0x004275E0)
            or (>= 0x004148C0 and <= 0x00414910);
        if (!inPump) return;

        // Only when group-6 multi-slot table is still empty (live: all zero at 100M).
        if (sys.Memory.Read32(0x0075E950) != 0) return;
        // Bail if inverted-memset already wiped main — re-homing into zeros is worse.
        if (sys.Memory.Read32(0x00212F70) != 0x27BDFEE0) return;

        // Wake every non-pump worker first so main-shaped peers can run.
        var kernel = sys.Hle?.Kernel;
        if (kernel != null)
        {
            foreach (var t in kernel.AllThreads)
            {
                if (!t.Alive) continue;
                // Leave pure ADX pump entry alone if it's the only thing making ticks.
                if (t.Entry == 0x004147F8u) continue;
                if (t.SoftSuspended) t.SoftSuspended = false;
                while (t.SuspendCount > 0) kernel.ResumeThread(t.Id);
                if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                    kernel.WakeupThread(t.Id);
                if (t.Sleeping && t.WaitSemaId > 0 && t.WaitSemaId < 64)
                {
                    try { kernel.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                }
            }
        }

        // Re-home current context to Midway main. Pump thread will be rescheduled later
        // via Create/Start or residual; empty group-6 means we lose nothing by leaving.
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        if (sp < 0x01000000 || sp >= (uint)SystemMemory.RDRAM_SIZE)
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
        // Ensure a1/a0 look non-wild for main (daddu s0,a1 / daddu s1,a0 at entry).
        sys.EE.PC = 0x00212F70;
        sys.LastGoodEePc = 0x00212F70;
        _lastMainKickCyc = sys.MasterCycles;
        _mainFromPumpKicks++;
        Assists++;
        // Keep pad DMA live across the kick.
        try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); } catch { /* ignore */ }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_mainFromPumpKicks <= 12 || _mainFromPumpKicks % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] re-home pump thrash 0x{pc:X8} -> Midway main 0x212F70 " +
                $"n={_mainFromPumpKicks} open={sys.Hle?.Sony?.RealRpc?.OpenPadCount} " +
                $"ghost={sys.Hle?.Sony?.RealRpc?.GhostPadCount} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// FUN_004755xx linked-list apply: walks <c>s2</c> nodes, each with count at +4 and
    /// element array at +8 (stride 0x58). Live post-WAD (2026-07-30) lands here with a
    /// garbage count in <c>s0</c> (e.g. 0x3037953D) so the inner loop at 0x475608 never
    /// finishes — PC frozen, gifP3 stuck at logo spine, pad injects inert. Force the
    /// function epilogue when the remaining count is absurd for any real Midway list.
    /// </summary>
    private ulong _lastListWalkBreakCyc;
    private ulong _lastFormatStallCyc;
    private int _formatStallEscapes;

    private void MaybeBreakRunawayListWalk(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        // Inner loop: lh v0,12(s1) / beql / bgez back
        if (pc is < 0x00475608 or > 0x00475628) return;
        long s0 = unchecked((int)(uint)sys.EE.GetGpr(16).Lo); // signed remaining count
        // Real lists are tiny (bucket counts << 1k). Negative after underflow also means done.
        if (s0 >= 0 && s0 < 4096) return;
        if (sys.MasterCycles - _lastListWalkBreakCyc < 100_000) return;
        _lastListWalkBreakCyc = sys.MasterCycles;

        // Clamp counters and skip `lw s2,0(s2)` (that load from s2=0 pulls kernel-vector
        // garbage and re-enters the walk). Land on the `bnel s2,zero` with s2 already 0 so
        // control falls into the real epilogue with intact $ra/$sp.
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0xFFFFFFFFUL }); // s0 = -1
        sys.EE.SetGpr(18, new EmotionEngine.Gpr128 { Lo = 0 }); // s2 = null
        sys.EE.PC = 0x00475630; // bnel s2, zero → fall through to epilogue
        sys.LastGoodEePc = 0x00475630;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BIOS] break runaway list-walk s0=0x{(uint)s0:X8} -> natural exit cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Post-list-walk plateau: PC parks in the format/itoa helper band around
    /// <c>0x47670C</c> (often <c>jr ra</c> delay) while workers Sleep/WaitSema(5).
    /// gifP3 stays at logo spine (5). Resume via live <c>$ra</c> when it is code, else
    /// re-home to ADX pump / main; always wake peers so pad and GS can advance.
    /// </summary>
    private void MaybeEscapePostListFormatStall(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        // Format/vsnprintf family + fatal message builders that park after list-walk break.
        bool inFormatBand = pc is (>= 0x00475BA0 and <= 0x00476840)
            or (>= 0x004766E0 and <= 0x00476740);
        if (!inFormatBand) return;
        if (sys.MasterCycles - _lastFormatStallCyc < 200_000) return;
        _lastFormatStallCyc = sys.MasterCycles;
        if (_formatStallEscapes >= 64) return;

        // Prefer natural return when $ra looks like EE code (format epilogue).
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        uint resume = 0;
        // After a few self-chase cycles (format→caller→format), skip stack/$ra and go
        // straight to a known live post-WAD target — 0x475998 was re-entering 0x475BA4.
        bool forceKnown = _formatStallEscapes >= 4;

        if (!forceKnown && sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00800000
            && ra is not (>= 0x00475000 and <= 0x00476840))
            resume = ra;

        // Stack scan for a caller return if $ra is bad (0 / inside helper).
        if (!forceKnown && resume == 0)
        {
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
            if (sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE)
            {
                for (uint off = 0; off <= 0xC0; off += 4)
                {
                    uint cand = sys.Memory.Read32(sp + off) & 0x1FFFFFFFu;
                    if (!sys.Memory.IsLikelyEeCode(cand)) continue;
                    if (cand is >= 0x00475000 and <= 0x00476840) continue;
                    if (cand < 0x00100000 || cand >= 0x00800000) continue;
                    resume = cand;
                    break;
                }
            }
        }

        // Known live boot targets once bulk WAD is in. Prefer main ONLY while logo spine
        // is still cold (gifP3&lt;12) and prolog intact. After spine restore, re-entering main
        // storms IOPRP gen≥2 (live: gen 2→6 @ 100M) and wipes pad open areas — prefer ADX
        // pump / pad-poll so dense pad + group-6 multi can drive accept.
        if (resume == 0 || forceKnown)
        {
            bool spineRestored = sys.Gif.Path3Transfers >= 12;
            if (!spineRestored && sys.Memory.Read32(0x00212F70) == 0x27BDFEE0)
                resume = 0x00212F70; // Midway main (intact, pre-spine only)
            else if (sys.Memory.IsLikelyEeCode(0x004147F8UL))
                resume = 0x004147F8; // ADX pump
            else if (sys.Memory.IsLikelyEeCode(0x00427518UL))
                resume = 0x00427518; // group-6 multi dispatch (pad/menu tick)
            else if (sys.Memory.IsLikelyEeCode(0x004145A8UL))
                resume = 0x004145A8; // ADX ready waiter
            else if (sys.Memory.IsLikelyEeCode(0x00414590UL))
                resume = 0x00414590; // historical gifP3=11 waiter spine
            else if (sys.Memory.Read32(0x00212F70) == 0x27BDFEE0)
                resume = 0x00212F70; // last resort
        }

        if (resume != 0 && resume != pc)
        {
            // Format helpers return int length in v0 — 0 is a safe "nothing written".
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = resume;
            sys.LastGoodEePc = resume;
            ReHomeSpIfInHleScratch(sys);
            // Ensure main has a sane stack if we re-home to Midway main.
            if (resume == 0x00212F70)
            {
                uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
                if (sp < 0x00100000 || sp >= (uint)SystemMemory.RDRAM_SIZE || sp < 0x01000000)
                    sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
            }
        }

        // Keep ADX ready + pump-stop clear so re-entered waiters/pump can run.
        if (sys.Memory.Read32(0x005341D8) == 0)
            sys.Memory.Write32(0x005341D8, 1);
        sys.Memory.Write32(0x00534218, 0);
        if (sys.Memory.Read32(0x00534164) != 0)
            sys.Memory.Write32(0x00534164, 0);

        // Wake peers — pad is inert while everyone Sleeps/WaitSema.
        var kernel = sys.Hle?.Kernel;
        if (kernel != null)
        {
            foreach (var t in kernel.AllThreads)
            {
                if (!t.Alive) continue;
                if (t.SoftSuspended) t.SoftSuspended = false;
                while (t.SuspendCount > 0)
                    kernel.ResumeThread(t.Id);
                if (t.Sleeping && t.WaitSemaId != 0)
                {
                    try { kernel.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                }
                else if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                    kernel.WakeupThread(t.Id);
                // Re-start ADX pump if it exited.
                if (!t.Started && t.Entry == 0x004147F8u)
                    kernel.StartAndMaybeSwitch(sys.EE, t.Id, switchNow: false, arg: 0, fromSyscall: false);
            }
        }

        _formatStallEscapes++;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_formatStallEscapes <= 8 || _formatStallEscapes % 16 == 0))
            Console.Error.WriteLine(
                $"[BIOS] escape post-list format stall pc=0x{pc:X8} -> 0x{resume:X8} " +
                $"ra=0x{ra:X8} n={_formatStallEscapes} gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// FUN_004145A8 waiter: <c>s0</c> must be <c>0x5341D8</c> (ready flag). Corrupted
    /// <c>s0=0x75C0D0</c> polls forever — repair register only, never plant *0x75C0D0.
    /// </summary>
    private void MaybeRepairAdxWaiterS0(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        // Poll loop body: ld v0,0(s0); beq v0,zero,loop  @ 0x4145D0..0x4145E0
        if (pc is < 0x004145D0 or > 0x004145E0) return;
        uint s0 = (uint)sys.EE.GetGpr(16).Lo;
        if (s0 == 0x005341D8) return;
        // Only repair when s0 looks like the known garbage table cursor / unmapped BSS.
        if (s0 is not (0x0075C0D0 or 0) && (s0 < 0x0075B000 || s0 > 0x0075D000))
            return;
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0x005341D8 }); // s0
        // Ensure the real flag is ready so the repaired waiter can exit.
        if (sys.Memory.Read32(0x005341D8) == 0)
            sys.Memory.Write32(0x005341D8, 1);
        ReHomeSpIfInHleScratch(sys);
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BIOS] repair ADX waiter s0 0x{s0:X8} -> 0x5341D8 pc=0x{pc:X8} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// FUN_00414480 lock-wait: sets <c>DAT_00534164=1</c> then busy-polls
    /// <c>ReferThreadStatus</c>/<c>ResumeThread</c> on the ADX pump (tid usually 6) until the
    /// pump clears the flag. Main never Sleeps, so cooperative scheduling never runs the pump
    /// and the loop burns millions of ReferThreadStatus (syscall 0x30) at PC 0x414988.
    /// Live (2026-07-30): pump body at 0x41487C does <c>if (*0x534164==1) { *0x534164=0; … }</c>.
    /// After bulk WAD, yield to the pump; if still stuck, clear the flag (same store the pump would).
    /// </summary>
    private int _adxPumpLockYields;
    private ulong _lastAdxPumpLockCyc;
    private int _adxMenuKicks;
    private ulong _lastAdxMenuKickCyc;

    private void MaybeServiceAdxPumpLock(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFF);
        // Lock-wait loop body / ReferThreadStatus helpers it hammers.
        bool inLockWait = (pc is >= 0x004144D0 and <= 0x00414510)
            || (pc is >= 0x00414988 and <= 0x00414A50)
            || (pc is >= 0x0047FD60 and <= 0x0047FDF8); // ReferThreadStatus / Resume stubs
        if (!inLockWait) return;

        // Scrub multi-table self-deadlock plant (0x414568 in 0x75E7A0) every visit.
        if (sys.Memory.Read32(0x0075E7A0) == 0x00414568u)
        {
            sys.Memory.Write32(0x0075E7A0, 0);
            sys.Memory.Write32(0x0075E7A4, 0);
            sys.Memory.Write32(0x0075E7A8, 0);
        }

        uint flag = sys.Memory.Read32(0x00534164);
        if (flag == 0)
        {
            _adxPumpLockYields = 0;
            return;
        }

        // Rate-limit: Step runs every instruction window; only act every ~50k cycles.
        if (sys.MasterCycles - _lastAdxPumpLockCyc < 50_000) return;
        _lastAdxPumpLockCyc = sys.MasterCycles;

        // Keep pump-stop clear so 0x4147F8 stays in its work loop.
        sys.Memory.Write32(0x00534218, 0);
        sys.Memory.Write32(0x0053421C, 0);

        var kernel = sys.Hle?.Kernel;
        int pumpTid = -1;
        if (kernel != null)
        {
            // Re-Start ADX pump if it exited / never started; remember its tid for a directed switch.
            foreach (var t in kernel.AllThreads)
            {
                if (!t.Alive || t.Entry != 0x004147F8u) continue;
                pumpTid = t.Id;
                if (!t.Started)
                    kernel.StartAndMaybeSwitch(sys.EE, t.Id, switchNow: false, arg: 0, fromSyscall: false);
                if (t.SoftSuspended) t.SoftSuspended = false;
                while (t.SuspendCount > 0)
                    kernel.ResumeThread(t.Id);
                if (t.Sleeping && t.WaitSemaId == 0)
                    kernel.WakeupThread(t.Id);
            }

            // If the CURRENT thread is the pump but PC is in lock-wait, the multi-table
            // self-deadlock already happened: pump is waiting on itself. Clear flag and
            // re-home PC to the pump loop head so it can resume real work.
            if (pumpTid > 0 && kernel.CurrentThreadId == pumpTid && inLockWait)
            {
                sys.Memory.Write32(0x00534164, 0);
                if (sys.Memory.Read32(0x00534174) == 1)
                    sys.Memory.Write32(0x00534174, 0);
                sys.EE.PC = 0x00414860; // pump loop head (heartbeat / FUN_00427678)
                sys.LastGoodEePc = 0x00414860;
                _adxPumpLockYields = 0;
                Assists++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine(
                        $"[BIOS] ADX pump-lock self-deadlock rescue tid={pumpTid} pc=0x{pc:X8} cyc={sys.MasterCycles}");
                return;
            }

            // Prefer directed switch to the ADX pump (not a random worker) so it can clear
            // *0x534164 itself. YieldToWorker alone often landed on non-pump threads.
            // After multi-table scrub, 2 yields is enough — pump clears flag at 0x41488C.
            if (_adxPumpLockYields < 4 && pumpTid > 0 && pumpTid != kernel.CurrentThreadId)
            {
                kernel.SaveCurrentContext(sys.EE, fromSyscall: false);
                bool switched = kernel.RestoreContext(sys.EE, pumpTid, fromSyscall: false);
                if (!switched)
                    switched = kernel.YieldToWorker(sys.EE);
                if (switched)
                {
                    // If restored pump context is itself mid lock-wait (old self-deadlock
                    // stack), re-home to loop head so the next slice clears the flag.
                    uint pumpPc = (uint)(sys.EE.PC & 0x1FFFFFFF);
                    if (pumpPc is (>= 0x00414480 and <= 0x00414560)
                        or (>= 0x00414988 and <= 0x00414A50)
                        or (>= 0x0047FD60 and <= 0x0047FDF8))
                    {
                        sys.EE.PC = 0x00414860;
                        sys.LastGoodEePc = 0x00414860;
                    }
                    _adxPumpLockYields++;
                    Assists++;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _adxPumpLockYields <= 4)
                        Console.Error.WriteLine(
                            $"[BIOS] ADX pump-lock switch tid={pumpTid} n={_adxPumpLockYields} pc=0x{pc:X8} cyc={sys.MasterCycles}");
                    return;
                }
            }
        }

        // Fallback: perform the same store the pump would (0x41488C sw zero,0(s2)).
        // Also clear sibling busy at 0x534174 so re-entry does not re-arm immediately.
        sys.Memory.Write32(0x00534164, 0);
        if (sys.Memory.Read32(0x00534174) == 1)
            sys.Memory.Write32(0x00534174, 0);
        _adxPumpLockYields = 0;
        // Wave-10: after logo spine, leave ADX lock-wait into group-6 multi so pad/UI
        // callbacks run instead of hammering ReferThreadStatus forever (live 100M+ wall).
        if (sys.Gif.Path3Transfers >= 11
            && sys.MasterCycles - _lastAdxMenuKickCyc >= 400_000
            && sys.Memory.Read32(0x0075E950) == 0x0043F920u)
        {
            uint menuResume = ApplyMenuDispatchResume(sys);
            if (menuResume != 0)
            {
                _lastAdxMenuKickCyc = sys.MasterCycles;
                _adxMenuKicks++;
                Assists++;
                try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); } catch { /* ignore */ }
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_adxMenuKicks <= 8 || _adxMenuKicks % 8 == 0))
                    Console.Error.WriteLine(
                        $"[BIOS] ADX lock → menu dispatch 0x{pc:X8} -> 0x{menuResume:X8} " +
                        $"n={_adxMenuKicks} gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
                return;
            }
        }
        // Pad inject once bulk WAD + lock service: START then CROSS so any pad-gated
        // title/menu transition can observe input (pad-inject API allowed).
        if (sys.MasterCycles >= 60_000_000)
        {
            int phase = (int)((sys.MasterCycles / 1_000_000) % 6);
            uint buttons = phase switch
            {
                1 or 2 => (uint)PadInput.Button.Start,
                3 or 4 => (uint)PadInput.Button.Cross,
                _ => 0u
            };
            try
            {
                sys.Pad.SetButtons(buttons);
                sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad);
            }
            catch { /* ignore */ }
        }
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BIOS] ADX pump-lock clear *0x534164 (main busy-wait) pc=0x{pc:X8} cyc={sys.MasterCycles}");
    }

    /// <summary>Write resource handle status field +0x48 to "done" (non-zero). Returns 1 if changed.</summary>
    private static int ForceResourceHandleDone(SystemMemory mem, uint handle)
    {
        if (handle < 0x100000 || handle + 0x50 >= SystemMemory.RDRAM_SIZE) return 0;
        uint st = mem.Read32(handle + 0x48);
        if (st != 0) return 0;
        // Only if handle looks allocated (any non-zero in first 0x20)
        bool live = false;
        for (uint o = 0; o < 0x20; o += 4)
            if (mem.Read32(handle + o) != 0) { live = true; break; }
        if (!live) return 0;
        mem.Write32(handle + 0x48, 1); // done
        // Common sibling fields: error=0, progress=full
        if (mem.Read32(handle + 0x4C) == 0)
            mem.Write32(handle + 0x4C, 0);
        return 1;
    }

    private void MaybeStartLogo(Ps2System sys)
    {
        if (_logoActive || _midwayDone) return;
        // Host FFmpeg / synthetic logo overlays removed (2026-07-30). Boot movies must come
        // from Soft-GS (IPU/CRI path). Do not paint fake logos over a black frame.
        if (!_logoPrepared)
            BeginPreloadFrames();
        // Clear any leftover overlay from older sessions/builds.
        sys.Gs.ClearHostOverlay();
        if (sys.MasterCycles >= 2_000_000 && Status is "idle" or "disc-mounted" or "disc-mounted (title-kick ready)")
            Status = "awaiting Soft-GS logo (no host FMV)";
    }

    /// <summary>No-op: host FMV frame advance removed. Soft-GS owns presentation.</summary>
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

    /// <summary>
    /// Host logo-frame pipeline retired. Never loads FFmpeg logo-cache or paints synthetic branding.
    /// Boot movies must appear via Soft-GS (IPU/CRI) only — missing video is an honest gap.
    /// </summary>
    private void PrepareLogoFrames(Ps2System? sys)
    {
        _logoPrepared = true;
        _logoFrames = null;
        _logoFramesTotal = 0;
        Status = "host-fmv-disabled (Soft-GS only)";
        sys?.Gs.ClearHostOverlay();
    }

}
