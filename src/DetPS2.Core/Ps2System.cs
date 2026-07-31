using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system (Phase 11 tooling / netplay foundation).
/// </summary>
public sealed class Ps2System
{
    public SystemMemory Memory { get; }
    public EmotionEngine EE { get; }
    public Dmac Dmac { get; }
    public Vif Vif { get; }
    public Gif Gif { get; }
    public Gs Gs { get; }
    public Pcrtc Pcrtc { get; }
    public Intc Intc { get; }
    public Iop Iop { get; }
    public Cdvd Cdvd { get; }
    public Sif Sif { get; }
    public Vu0 Vu0 { get; }
    public Vu1 Vu1 { get; }
    public GsPipeline Pipeline { get; }
    public EeTimers Timers { get; }
    public MmioBus Mmio { get; }
    public PadInput Pad { get; }
    public Spu2 Spu2 { get; }
    public Sio2 Sio2 { get; }
    public Multitap Multitap { get; }
    public MemoryCard MemCard { get; }
    /// <summary>Optional virtual HDD — see EmulatorConfig.EnableVirtualHdd's doc comment. Null
    /// unless TryEnableVirtualHdd was called and succeeded; memory cards (MemCard above) are
    /// the always-on primary save path regardless of this.</summary>
    public VirtualHdd? Hdd { get; private set; }
    public BiosHle Hle { get; }
    public BootTrace BootTrace { get; }
    public BusContention Bus { get; }
    public Debugger Debugger { get; }
    public Tracer Tracer { get; }
    public InputRecording InputRecording { get; }
    public PresentPipeline Present { get; }
    public IopModuleHost IopModules { get; }
    public Telemetry Telemetry { get; }
    public Ipu Ipu { get; }
    public EeJit EeJit { get; }
    public SnapshotEngine Snapshots { get; }
    /// <summary>
    /// Shared BIOS/IOP service host — C# reimplementation of ROMDIR module contracts.
    /// Installed once at LoadBios; creates the destinations every commercial title waits on.
    /// See <see cref="BiosBootHost"/> for why this is not per-title PC patching.
    /// </summary>
    public BiosBootHost BiosBoot { get; } = new();

    /// <summary>
    /// BIOS <c>VBLANK.IRX</c> HLE — IOP vblank callback lists and event-flag wakes
    /// (separate from EE INTC cause 2). Driven from PCRTC via <see cref="BiosHle.OnVblank"/>.
    /// </summary>
    public IopVblankHost IopVblank { get; } = new();

    /// <summary>
    /// BIOS INTRMAN / TIMEMAN / IOMAN service contracts (register IRQ, system time, devices).
    /// </summary>
    public IopSystemHost IopSystem { get; } = new();
    public IopEeconfHost IopEeconf { get; } = new();
    public IopSsbuscHost IopSsbusc { get; } = new();
    public IopSysclibHeaplibHost IopSysclibHeaplib { get; } = new();
    public IopDmacManHost IopDmacMan { get; } = new();
    /// <summary>SECRMAN / CLEARSPU / LIBSD / UDNL / X* + THREADMAN thmsgbx/vpl/fpl export HLE.</summary>
    public IopExtendedBiosHost IopExtendedBios { get; } = new();
    /// <summary>LIBSD functional core (sceSdInit / SetParam / key-on) — installed via IopExtendedBios.</summary>
    public IopLibSdHost IopLibSd { get; } = new();

    /// <summary>
    /// BIOS EXCEPMAN.IRX HLE — real per-exception-code, priority-ordered handler registration
    /// (distinct from IopSystem's INTRMAN interrupt registry — see IopExcepManHost's own doc
    /// comment for the real architectural distinction).
    /// </summary>
    public IopExcepManHost IopExcepMan { get; } = new();

    public Scheduler Scheduler { get; }

    /// <summary>
    /// When true (default), commercial Midway path tries real CRT0 after BIOS services start.
    /// The destinations CRT0 waits on are provided by <see cref="BiosBoot"/>, not by poking
    /// individual EE threads. Disable with DETPS2_FAKE_CRT0=1 for the old jump-to-main baseline.
    /// </summary>
    public static bool PreferRealCrt0 =
        Environment.GetEnvironmentVariable("DETPS2_FAKE_CRT0") != "1";

    public ulong MasterCycles => Scheduler.MasterCycles;
    public bool UseJit { get; set; }
    /// <summary>Last EE PC in game/code space — used to recover from low-memory thrash.</summary>
    public ulong LastGoodEePc { get; set; }
    private bool _commercialSifInitKicked;
    /// <summary>
    /// Per-thread first-seen cycle for commercial threads that are Alive but not Started.
    /// One-shot <c>_commercialWorkerKicked</c> left ADX (entry 0x4147F8) and later workers
    /// permanently dormant after only the SIF-RPC thread was kicked.
    /// </summary>
    private readonly Dictionary<int, ulong> _commercialWorkerSeenAt = new();
    /// <summary>Thread ids we already StartThread'd once — never re-kick after ExitThread
    /// leaves Started=false (DORMANT), or we thrash re-entering worker entry points.</summary>
    private readonly HashSet<int> _commercialWorkerKickedIds = new();
    /// <summary>Diagnostic-only escape hatch to test whether the real boot now proceeds
    /// without the Midway forced-jump assists, now that several real EE/SIF-RPC bugs have
    /// been fixed. Opt-in via blocker-trace/etc --no-assist or DETPS2_DISABLE_MIDWAY_ASSIST=1.</summary>
    public static bool DisableMidwayAssist =
        Environment.GetEnvironmentVariable("DETPS2_DISABLE_MIDWAY_ASSIST") == "1";
    /// <summary>Diagnostic-only: disables only the cruder MaybeForceSifInit direct-jump
    /// fallback (which hands 0x482E98 whatever registers happened to be lying around),
    /// while keeping KickMidwayMainPath's real-main() redirect active. Opt-in via
    /// --no-force-sif or DETPS2_DISABLE_FORCE_SIF=1.</summary>
    public static bool DisableForceSifInit =
        Environment.GetEnvironmentVariable("DETPS2_DISABLE_FORCE_SIF") == "1";
    /// <summary>Same diagnostic purpose as DisableForceSifInit, targeting UnstickSifWaits
    /// (which force-injects v0=1 and sometimes redirects PC on hardcoded PC-range polls).
    /// Opt-in via --no-unstick-waits or DETPS2_DISABLE_UNSTICK_WAITS=1.</summary>
    public static bool DisableUnstickSifWaits =
        Environment.GetEnvironmentVariable("DETPS2_DISABLE_UNSTICK_WAITS") == "1";
    /// <summary>Same diagnostic purpose, targeting AutoCompleteWorkItems (force-completes
    /// planted worklist items). Opt-in via --no-auto-complete or
    /// DETPS2_DISABLE_AUTO_COMPLETE=1.</summary>
    public static bool DisableAutoCompleteWorkItems =
        Environment.GetEnvironmentVariable("DETPS2_DISABLE_AUTO_COMPLETE") == "1";
    /// <summary>Quirk module resolved for the currently mounted disc's serial, if any
    /// (see GameQuirkRegistry). Null for the overwhelming majority of titles that need none.</summary>
    public IGameQuirkModule? ActiveQuirk { get; set; }

    private MidwayBootAssist? _fallbackMidwayAssist;
    /// <summary>Shaolin Monks / Midway commercial boot + logo assist — now a real
    /// IGameQuirkModule (see GameQuirks/), registered for serial SLUS_210.87.
    /// Kept as a typed property (rather than requiring every existing caller to cast
    /// ActiveQuirk) since Program.cs's probe-frame and several Desktop UI status displays
    /// read its diagnostic fields (Status/LogoFrame/FramesPresented/...) directly. Resolves
    /// to the live ActiveQuirk instance when the mounted disc is SLUS_210.87 (so there is
    /// exactly one instance, correctly serial-gated); otherwise returns an idle fallback
    /// instance that is never stepped, so other titles' boots never touch its hardcoded
    /// MK-specific addresses.</summary>
    public MidwayBootAssist MidwayAssist =>
        (ActiveQuirk as MidwayBootAssist) ?? (_fallbackMidwayAssist ??= new MidwayBootAssist());

    public Ps2System()
    {
        Memory = new SystemMemory();
        Mmio = new MmioBus();
        Memory.AttachMmio(Mmio);
        Bus = new BusContention();
        Debugger = new Debugger();
        Tracer = new Tracer();
        InputRecording = new InputRecording();
        Present = new PresentPipeline();
        IopModules = new IopModuleHost();
        IopModules.InitDefaults();
        IopModules.BindIopSystem(IopSystem);
        Telemetry = new Telemetry();

        EE = new EmotionEngine(Memory);
        Intc = new Intc();
        Timers = new EeTimers(Intc);
        Pad = new PadInput();
        Spu2 = new Spu2();
        Memory.AttachSpu2(Spu2);
        Multitap = new Multitap();
        MemCard = new MemoryCard();
        Sio2 = new Sio2();
        // Port 0 = main pad; ports 1–3 are separate pads for P2+
        Multitap.Ports[0] = Pad; // ports 1–3 already allocated for P2+
        Sio2.Attach(Pad, MemCard);
        Sio2.AttachMultitap(Multitap.Ports);

        Dmac = new Dmac(Memory);
        Gs = new Gs(Memory);
        Gif = new Gif(Gs);
        Vif = new Vif(Memory);
        Vu0 = new Vu0(Memory);
        Vu1 = new Vu1(Memory);
        Pcrtc = new Pcrtc(Gs);
        Iop = new Iop(Intc, Memory);
        Cdvd = new Cdvd();
        Memory.AttachCdvd(Cdvd);
        Sif = new Sif(Memory, Intc);
        Memory.AttachSif(Sif);
        IopModules.BindMemCard(MemCard);
        Sif.BindServices(IopModules, Pad, Cdvd);
        Pipeline = new GsPipeline(Gs, Gif, Pcrtc);
        BootTrace = new BootTrace();
        Ipu = new Ipu();
        EeJit = new EeJit(EE, Memory);
        Snapshots = new SnapshotEngine();

        Dmac.SetGif(Gif);
        Vif.SetGif(Gif); // MSKPATH3 -> GIF_STAT.M3P
        Dmac.SetSif(Sif);
        Dmac.SetIntc(Intc);
        Dmac.SetVif(Vif);
        Dmac.SetBus(Bus);
        Dmac.SetIpu(Ipu);
        Vif.SetVu0(Vu0);
        Vif.SetVu1(Vu1);
        Vu1.SetGif(Gif);
        EE.SetVu0(Vu0);
        EE.SetIntc(Intc);
        EE.SetDebugger(Debugger);
        EE.SetTracer(Tracer);
        EE.SetCycleSource(() => MasterCycles);
        EE.SetTelemetry(Telemetry);
        Pcrtc.SetIntc(Intc);
        Pcrtc.SetVblankCallback(() => Hle?.OnVblank());
        Cdvd.SetIntc(Intc);
        Spu2.SetIntc(Intc);
        Ipu.SetIntc(Intc);
        Mmio.Attach(Timers, Intc, Dmac, Sif, Pad, Spu2, Sio2, Ipu);
        Mmio.AttachGraphics(Gif, Gs, Vif);
        Mmio.SetTelemetry(Telemetry, () => (MasterCycles, EE.PC));

        Hle = new BiosHle(this);
        EE.SetHle(Hle);
        // Fix callback now that Hle exists
        Pcrtc.SetVblankCallback(() => Hle.OnVblank());

        Scheduler = new Scheduler
        {
            Bus = Bus,
            BudgetScaledComponent = EE
        };
        RegisterComponents();
    }

    private void RegisterComponents()
    {
        Scheduler.Register(EE);
        Scheduler.Register(Vu0);
        Scheduler.Register(Timers);
        Scheduler.Register(Dmac);
        Scheduler.Register(Vif);
        Scheduler.Register(Gif);
        Scheduler.Register(Gs);
        Scheduler.Register(Pcrtc);
        Scheduler.Register(Intc);
        Scheduler.Register(Iop);
        Scheduler.Register(Cdvd);
        Scheduler.Register(Sif);
        Scheduler.Register(Spu2);
        Scheduler.Register(Ipu);
    }

    public void SetEventQueueMode(bool enabled) =>
        Scheduler.SchedulingMode = enabled ? Scheduler.Mode.EventQueue : Scheduler.Mode.FixedSlice;

    public byte[] SaveState() => DetPS2.Core.SaveState.Save(this);
    public byte[] SaveState(bool compress) => DetPS2.Core.SaveState.Save(this, compress);
    public bool LoadState(byte[] data) => DetPS2.Core.SaveState.Load(this, data);

    /// <summary>Opt-in virtual HDD setup — call only when EmulatorConfig.EnableVirtualHdd is
    /// true. Opens the image at path if it already exists; otherwise creates a fresh one at
    /// sizeMb. Memory cards remain the primary save path regardless (MemCard above is always
    /// created, unconditionally, in the constructor) — this only ever adds Hdd as an
    /// additional, optional option a title's own save-path code would need to explicitly use.
    /// Returns false (and leaves Hdd null) on any failure — a bad/missing HDD path should never
    /// prevent the rest of the system from booting normally on memory cards alone.</summary>
    public bool TryEnableVirtualHdd(string path, long sizeBytes)
    {
        try
        {
            Hdd = File.Exists(path) ? VirtualHdd.OpenFile(path) : VirtualHdd.CreateNewFile(path, sizeBytes);
            return true;
        }
        catch
        {
            Hdd = null;
            return false;
        }
    }

    public void DisableVirtualHdd() => Hdd = null;

    /// <summary>
    /// Bring up the commercial EE/IOP kernel service surface. A real Sony BIOS file is optional,
    /// not required — see <see cref="LoadBiosNative"/>'s own doc comment for why: when
    /// <paramref name="path"/> is null/blank or doesn't exist, this falls back to the native,
    /// file-free bring-up automatically rather than throwing, so every existing caller (CLI
    /// commands, the Desktop app, tests) gets "no BIOS needed" without individually changing.
    /// Confirmed byte-identical behavior between the two paths across a 9-title cross-check and
    /// a 400M-cycle single-title deep trace before this fallback was wired in.
    /// </summary>
    public void LoadBios(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            LoadBiosNative();
            return;
        }

        byte[] biosData = File.ReadAllBytes(path);
        Memory.LoadBiosRom(biosData);
        const uint BIOS_BASE = 0x1FC00000;
        for (int i = 0; i < biosData.Length && i < 4 * 1024 * 1024; i++)
            Memory.Write8(BIOS_BASE + (uint)i, biosData[i]);

        EE.PC = 0xBFC00000;
        Iop.PC = 0xBFC00000;
        Hle.Reset();
        // Commercial titles use the Sony EE syscall table, not Det homebrew ABI
        Hle.EnableSonyKernel();
        // Kernel-friendly COP0: IE + EIE, user can run; leave BEV clear for RAM vectors later
        EE.COP0_Status = (1u << 16) | 1u; // EIE | IE
        KernelBootstrap.InstallCommercialRuntime(this);

        // Structural substrate: parse ROMDIR and install IOP service destinations *before*
        // any game ELF runs. This is the shared BIOS map — not a per-thread assist.
        BiosBoot.BindBios(path, biosData);
        BiosBoot.StartCommercialIop(this);
    }

    /// <summary>
    /// Bring up the same commercial EE/IOP kernel service surface as <see cref="LoadBios"/>
    /// WITHOUT reading any real Sony firmware file — the "no BIOS should even be necessary"
    /// path. Real BIOS bytes were never actually executed by the standard LoadBios→BootDiscFile
    /// flow to begin with: <see cref="ElfLoader.LoadIntoEe"/> sets <c>EE.PC</c> to the game's own
    /// ELF entry unconditionally the moment a disc boots, overwriting whatever
    /// <c>EE.PC = 0xBFC00000</c> reset-vector value was set beforehand — confirmed by grepping
    /// every real read of BIOS ROM content in this codebase (<c>0x1FC00000</c>/<c>LoadBiosRom</c>):
    /// none of them are on the actual per-cycle execution path. What real commercial titles do
    /// need is the *service surface* those BIOS-resident IOP modules provide (SIFCMD BIND/CALL/
    /// RPC_END, THREADMAN sema/thread semantics, IOMAN fd table, LOADFILE/CDVDFSV/PADMAN/MCSERV
    /// RPC services, VBLANK/EXCEPMAN registries, LOADCORE import/export linking) — all of which
    /// are already real, ground-truthed C# HLE (<see cref="RealSifRpc"/>, <see cref="IopVblankHost"/>,
    /// <see cref="IopExcepManHost"/>, <see cref="IopSystemHost"/>, <see cref="IrxLoader"/>) reachable
    /// without a byte of real firmware, via <see cref="BiosBootHost"/>'s own no-image fallback
    /// (<see cref="BiosBootHost.BootCriticalContracts"/> — the fixed, ROMDIR-derived module/role/sid
    /// table already used whenever no real image was bound).
    /// </summary>
    public void LoadBiosNative()
    {
        EE.PC = 0xBFC00000;
        Iop.PC = 0xBFC00000;
        Hle.Reset();
        Hle.EnableSonyKernel();
        EE.COP0_Status = (1u << 16) | 1u; // EIE | IE
        KernelBootstrap.InstallCommercialRuntime(this);

        BiosBoot.BindBios(null, null);
        BiosBoot.StartCommercialIop(this);
    }

    public void InstallStubBios(ulong jumpTarget = 0x00100000)
    {
        uint target = (uint)jumpTarget;
        uint hi = target >> 16;
        uint lo = target & 0xFFFF;
        uint lui = (0x0Fu << 26) | (8u << 16) | hi;
        uint ori = (0x0Du << 26) | (8u << 21) | (8u << 16) | lo;
        uint jr = (8u << 21) | 0x08;
        uint nop = 0;

        Memory.WriteBios32(0, lui);
        Memory.WriteBios32(4, ori);
        Memory.WriteBios32(8, jr);
        Memory.WriteBios32(12, nop);
        EE.PC = 0xBFC00000;
    }

    public ElfLoader.LoadResult LoadElf(byte[] elfData)
    {
        Hle.Reset();
        return ElfLoader.LoadIntoEe(elfData, this);
    }

    public ElfLoader.LoadResult LoadHomebrewGsDemo()
    {
        byte[] elf = ElfLoader.BuildHomebrewGsDemoElf();
        return LoadElf(elf);
    }

    public DiscBoot.Result BootDiscImage(byte[] iso) => DiscBoot.BootFromImage(this, iso);

    /// <summary>Boot multi‑GB ISO/BIN from local or UNC path (streamed, no full RAM load).</summary>
    public DiscBoot.Result BootDiscFile(string path) => DiscBoot.BootFromFile(this, path);

    public void RunFor(ulong cyclesToRun)
    {
        // Input playback
        uint? pb = InputRecording.PollPlayback(MasterCycles);
        if (pb.HasValue)
            Pad.SetButtons(pb.Value);

        // Input record
        InputRecording.Record(MasterCycles, Pad.Buttons);

        if (Debugger.Halted)
            return;

        if (UseJit && EeJit.Enabled)
        {
            // EE-heavy path: JIT block cache for EE; rest of system via scheduler still
            // For Det parity, scheduler still owns MasterCycles — disable UseJit for full RunFor
            // and use RunForJit for microbenches instead.
        }

        // Commercial: slice the run so we can recover if the EE falls into low memory
        if (Hle.SonyKernelMode)
        {
            ulong left = cyclesToRun;
            // Default 50k; tighten while EE is inside CRI cvFs / our HLE stubs so MidwayBootAssist
            // can finish ISO open/read without missing the PC window across a long slice.
            const ulong sliceDefault = 50_000;
            const ulong sliceCri = 2_000;
            // Always arm IOP at last LoadIrx entry so scheduler quanta execute module text
            // (IRX is the product path; DETPS2_FORCE_HLE_IOP=1 skips arming via IsLiteralIrxEnabled).
            if (IopModuleHost.IsLiteralIrxEnabled && IopModules.HasPendingLiteralEntry)
                IopModules.TryArmPendingLiteralEntry(Iop);
            while (left > 0)
            {
                ulong pcPhys = EE.PC & 0x1FFFFFFFUL;
                // Track only real EE *code* (IsLikelyEeCode rejects zero sleds AND string/data
                // mis-exec like 0x00520040). Poisoned LastGood re-homes open-bus thrash forever.
                if (Memory.IsLikelyEeCode(pcPhys))
                    LastGoodEePc = EE.PC;
                KernelBootstrap.RescueIfLostInLowMem(this, LastGoodEePc);
                // Mid-RDRAM nop-sled rescue (PC is "in range" so low-mem rescue skips it).
                MaybeRescueNopSled(LastGoodEePc);

                // Midway: jump to real main (0x212F70). Early kick (after ~100k) is
                // required — delaying until the idle pump misses the GIF clear path.
                // Gated by ActiveQuirk (i.e. the mounted disc's serial actually resolved to
                // MidwayBootAssist) — previously this ran unconditionally for ANY commercial
                // boot, poking MK-specific addresses regardless of which title was mounted.
                if (!DisableMidwayAssist && ActiveQuirk is MidwayBootAssist &&
                    !_commercialSifInitKicked && MasterCycles > 100_000)
                    KickMidwayMainPath();

                // GameQuirks SDK: always step the matched module. MidwayBootAssist.Step itself
                // keeps CRI cvFs + ADX gate HLE running even when DisableMidwayAssist is set
                // (those are middleware contracts, not PC-range pokes); only force-jumps/logo
                // assists are suppressed inside Step when --no-assist is on.
                ActiveQuirk?.Step(this);

                bool criHot = pcPhys is (>= 0x0041D0C0UL and <= 0x0041D1E4UL)
                    or (>= 0x00417F80UL and <= 0x00418020UL)
                    or (>= 0x01FD4000UL and < 0x01FD4080UL);
                // God of War: list/flag/object-init/exception thrash bands need tight slices
                // so GodOfWarAssist soft escapes fire before 50k-cycle windows burn out.
                bool gowHot = ActiveQuirk is GodOfWarAssist && pcPhys is
                    (>= 0x0015F2C0UL and <= 0x0015FA80UL)
                    or (>= 0x001312C0UL and <= 0x001312F0UL)  // link-search thrash
                    or (>= 0x00293C00UL and <= 0x00293C80UL)  // WaitSema empty SIF poll
                    or (>= 0x00294800UL and <= 0x002948A0UL)  // SIF-cmd poll caller (loops WaitSema)
                    or (>= 0x0027CC00UL and <= 0x0027CE90UL)  // worker entry/dispatch (WaitSema 0x20)
                    or (>= 0x0027DF00UL and <= 0x00282000UL)  // worker cmd handlers (type=2 → 0x2803C0)
                    or (>= 0x0026B9B0UL and <= 0x0026C200UL)  // post-type-2 jalr thrash + 989snd (wave-5)
                    or (>= 0x00239300UL and <= 0x00239810UL)  // secondary freelist thrash
                    or (>= 0x0023A900UL and <= 0x0023AA30UL)  // null freelist thrash
                    or (>= 0x002C0000UL and <= 0x02000000UL)  // data/heap as PC
                    or (>= 0x00100000UL and <= 0x00100200UL)  // CRT0 re-entry after AdEL (wave-2)
                    or (>= 0x002943C0UL and <= 0x00294590UL)  // cache-wb residual (0x2943D8/420)
                    or (>= 0x00183880UL and <= 0x001838D0UL)
                    or (>= 0x0017A1D0UL and <= 0x0017A298UL)  // soft-tick wait leaf (*0x29C7D4)
                    or (>= 0x0017A320UL and <= 0x0017A37CUL)  // flag spin + jal tick-wait
                    or (>= 0x00233AD0UL and <= 0x00233B44UL)
                    or (>= 0x00284600UL and <= 0x00284B00UL)  // soft-float + wrappers (0x2849C4 heat)
                    or (>= 0x00155AB0UL and <= 0x00156400UL)  // table-index + post-table residual (0x156324)
                    or (>= 0x001390F0UL and <= 0x00139114UL)  // huge byte-sum
                    or (>= 0x0023E7C0UL and <= 0x0023E7F0UL)  // align-zero poison a0
                    or (>= 0x0021FF00UL and <= 0x00220600UL)
                    or (>= 0x0013DED0UL and <= 0x0013DEF8UL)
                    or (>= 0x0013E1C0UL and <= 0x0013E1F4UL)  // global free-search circular
                    or (>= 0x80000180UL and <= 0x80020000UL);
                // Burnout 3: post-TXD GIF flush thrash + residual-STG WaitSema/SIF bands.
                bool b3Hot = ActiveQuirk is Burnout3Assist && pcPhys is
                    (>= 0x0021A4F0UL and <= 0x0021A5E8UL)
                    or (>= 0x001F3080UL and <= 0x001F3500UL)
                    or (>= 0x00218700UL and <= 0x00218790UL)
                    or (>= 0x00293A00UL and <= 0x00294200UL)
                    or (>= 0x00123E00UL and <= 0x00124080UL)
                    or (>= 0x002AF800UL and <= 0x002AF994UL)
                    or (>= 0x002B34C0UL and <= 0x002B35D0UL);
                // Dec post-MSL factory/sys-init fail gates (one-instruction v0 checks) —
                // MidwayFamilyAssist soft-success needs tight slices or the window is missed.
                bool mkFamHot = ActiveQuirk is MidwayFamilyAssist
                    && MidwayFamilyAssist.IsDecSysInitHotPc(pcPhys);
                ulong slice = (criHot || gowHot || b3Hot || mkFamHot) ? sliceCri : sliceDefault;

                // Kick commercial workers that CreateThread left DORMANT (StartThread never
                // reached). One-shot kick of only thread 2 left ADX (entry 0x4147F8) and every
                // later worker permanently unstarted — traced 2026-07-29 at 120M cycles:
                // threads 3–6 Alive/!Started while main spun SetVSyncFlag at 0x463960.
                // Re-arm per thread so each new CreateThread gets its own grace then Start.
                // MidwayFamilyAssist (DA/Dec/Arm): same CreateThread→DORMANT pattern for
                // MWFILE reverse-RPC / post-MSL workers while main thrash-sleeps.
                if (!DisableMidwayAssist
                    && ActiveQuirk is MidwayBootAssist or MidwayFamilyAssist)
                    KickAllDormantCommercialWorkers();

                ulong n = left > slice ? slice : left;
                Scheduler.RunFor(n);
                left -= n;

                // See SchedulerGeneration's own doc comment: this used to live inside an
                // ISchedulable.Step() override that nothing ever called, because Ps2System
                // itself was never Scheduler.Register()'d (only its individual components
                // were) — SchedulerGeneration was permanently stuck at 0 as a result, which
                // meant Sif.cs's real-RPC queue (TryDequeueRealRpc's "peekGen < currentGen"
                // check) could NEVER successfully drain anything: every submission and every
                // drain attempt saw the same gen=0, so "strictly older" was never true even
                // once. The one real bind/call that ever appeared to work (Shaolin Monks'
                // opening CDVD sector read) went through MidwayBootAssist's own separate,
                // hardcoded direct-TryHandle bypass, not this queue at all — confirmed live
                // (2026-07-28) via a diagnostic trace showing every queue submission and
                // refusal stuck at gen=0 for the entire run. Fixed by advancing generation and
                // draining once per real slice here, where it's actually reached.
                SchedulerGeneration++;
                Hle.Sony?.DrainRealRpcQueue(SchedulerGeneration);
            }
        }
        else
        {
            Scheduler.RunFor(cyclesToRun);
            SchedulerGeneration++;
            Hle.Sony?.DrainRealRpcQueue(SchedulerGeneration);
        }
    }

    /// <summary>
    /// <summary>
    /// Hits of PC at a zero opcode inside RDRAM (not low-mem). Used to detect sustained
    /// nop-sleds like MK post-WAD <c>0x024F0C64</c> without treating delay-slot nops as fatal.
    /// </summary>
    private int _nopSledHits;

    /// <summary>
    /// If EE is executing a sustained nop-sled in mid-RDRAM (zeroed BSS / bad fnptr target),
    /// snap back to <paramref name="lastGoodPc"/> or a stack return candidate. Generic —
    /// does not force out←in or plant title flags.
    /// </summary>
    private void MaybeRescueNopSled(ulong lastGoodPc)
    {
        // KSEG0 exception vectors are legitimate.
        if (EE.PC >= 0x80000000UL && EE.PC < 0x80001000UL)
        {
            _nopSledHits = 0;
            return;
        }
        uint pcPhys = (uint)(EE.PC & 0x1FFFFFFFUL);
        if (pcPhys < 0x00100000u)
        {
            _nopSledHits = 0;
            return;
        }
        // MK parks at 0x024F0C64 — past 32MiB RDRAM; open-bus fetches are 0 (nop forever).
        bool pastRdram = pcPhys >= (uint)SystemMemory.RDRAM_SIZE;
        uint op = pastRdram ? 0u : Memory.Read32(pcPhys);
        if (!pastRdram && op != 0)
        {
            _nopSledHits = 0;
            return;
        }
        // Require a run of zeros ahead so a single delay-slot nop never trips this.
        if (!pastRdram && (Memory.Read32(pcPhys + 4) != 0 || Memory.Read32(pcPhys + 8) != 0))
        {
            _nopSledHits = 0;
            return;
        }
        _nopSledHits++;
        // Immediate for past-RDRAM; 2 slices for in-range zero sleds.
        if (!pastRdram && _nopSledHits < 2) return;

        ulong resume = lastGoodPc;

        // Prefer a live stack return address (MK parked with sp→0x414448 code).
        // Wave-3: reject heap-alloc mid-body (0x13DCxx) — stack scan can pick 0x13D9C8
        // after ungated VIF/GIF drain and kill GoW MOD_LOAD (binds=0).
        static bool IsBadNopSledResume(ulong cand)
        {
            uint p = (uint)(cand & 0x1FFFFFFFUL);
            if (p == 0x00100008u) return true;
            if (p is >= 0x0013DC00u and <= 0x0013E200u) return true;
            if (p is >= 0x80000180u and <= 0x80000200u) return true;
            if (p < 0x00100000u) return true;
            return false;
        }

        uint sp = (uint)(EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        if (sp is >= 0x00100000 and < 0x02000000)
        {
            for (uint off = 0; off <= 0x40; off += 4)
            {
                uint cand = Memory.Read32(sp + off);
                if (Memory.IsLikelyEeCode(cand) && !IsBadNopSledResume(cand))
                {
                    resume = cand;
                    break;
                }
            }
        }
        if (!Memory.IsLikelyEeCode(resume) || IsBadNopSledResume(resume))
        {
            ulong ra = EE.GetGpr(31).Lo & 0x1FFFFFFFUL;
            if (Memory.IsLikelyEeCode(ra) && !IsBadNopSledResume(ra))
                resume = ra;
            else if (Memory.IsLikelyEeCode(EE.COP0_EPC) && !IsBadNopSledResume(EE.COP0_EPC))
                resume = EE.COP0_EPC;
            else if (lastGoodPc is >= 0x00100000 and < 0x01000000
                     && Memory.IsLikelyEeCode(lastGoodPc)
                     && !IsBadNopSledResume(lastGoodPc))
                resume = lastGoodPc;
            else if (Memory.IsLikelyEeCode(0x004147F8UL))
                resume = 0x004147F8UL; // ADX pump (MK)
            else if (Memory.Read32(0x00212F70) == 0x27BDFEE0)
                resume = 0x00212F70UL; // Midway main
            else if (Memory.IsLikelyEeCode(0x00170BFCUL))
                resume = 0x00170BFCUL; // GoW tag-list empty epilogue (never CRT0)
            else if (Memory.IsLikelyEeCode(0x00185FACUL))
                resume = 0x00185FACUL; // GoW post-FreezeCache
            else if (pcPhys is >= 0x0021FF00u and <= 0x00220600u)
                resume = pcPhys + 0x0Cu; // skip zero sled in-place (healthy GoW 0x2200F0→FC)
            // Avoid re-CRT0 (0x100008): restarts boot and storms UnknownOpcode.
            else if (Memory.IsLikelyEeCode(0x00100008UL))
                resume = lastGoodPc is >= 0x00100000 && !IsBadNopSledResume(lastGoodPc)
                    ? lastGoodPc : 0x00100008UL;
        }
        if (IsBadNopSledResume(resume))
        {
            if (pcPhys is >= 0x0021FF00u and <= 0x00220600u)
                resume = pcPhys + 0x0Cu;
            else
            {
                _nopSledHits = 0;
                return;
            }
        }

        EE.COP0_Status &= ~0x6u; // clear EXL|ERL
        EE.PC = resume;
        LastGoodEePc = resume;
        _nopSledHits = 0;
        // Nop-sled rescue can re-home mid-function into an INTC_STAT busy-poll
        // (`lw v0,0(v1)` / `andi v0,4` / `beq` spin) whose `lui/ori v1,0x1000F000` setup
        // was skipped — live SotC after a LoadModule-error hang: rescue re-home left
        // v1=sema residue so the poll never saw sticky STAT bit2 even when set.
        TryRepairIntcStatPollBase();
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BIOS] rescue nop-sled 0x{pcPhys:X8} -> 0x{(uint)(resume & 0x1FFFFFFF):X8} cyc={MasterCycles}");
    }

    /// <summary>
    /// If EE.PC is at <c>lw rt, 0(rs)</c> followed by <c>andi rt, rt, 4</c> (VBlankStart bit)
    /// and the base register is not already <c>INTC_STAT</c> (<c>0x1000F000</c>), load it.
    /// Shared assist for mid-function nop-sled re-homes; does not invent poll success.
    /// </summary>
    private void TryRepairIntcStatPollBase()
    {
        uint pc = (uint)(EE.PC & 0x1FFFFFFFu);
        if (pc < 0x00100000u || pc + 8u >= (uint)SystemMemory.RDRAM_SIZE) return;
        uint opLw = Memory.Read32(pc);
        // lw rt, off(rs): primary 0x23, signed imm == 0
        if ((opLw >> 26) != 0x23 || (short)(opLw & 0xFFFF) != 0) return;
        uint rs = (opLw >> 21) & 0x1F;
        uint rt = (opLw >> 16) & 0x1F;
        if (rs == 0 || rt == 0) return;
        uint opAndi = Memory.Read32(pc + 4);
        // andi rt, rt, 4 — primary 0x0C, same rt, imm=4 (VBlankStart)
        if ((opAndi >> 26) != 0x0C) return;
        if (((opAndi >> 21) & 0x1F) != rt || ((opAndi >> 16) & 0x1F) != rt) return;
        if ((opAndi & 0xFFFF) != 4) return;
        if ((EE.GetGpr((int)rs).Lo & 0xFFFFFFFFu) == Intc.AddrStat) return;
        EE.SetGpr((int)rs, new EmotionEngine.Gpr128 { Lo = Intc.AddrStat });
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BIOS] repair INTC_STAT poll base r{rs}=0x{Intc.AddrStat:X8} at pc=0x{pc:X8} cyc={MasterCycles}");
    }

    /// <summary>
    /// Force-call Midway SIF/cmd init (0x482E98). Observed effect: fills 0x77A080
    /// worklist and issues SifGetReg/SifSetDma that fast-boot otherwise never reaches.
    /// </summary>
    /// <summary>
    /// Redirect CRT0 into Midway's real main at 0x212F70 (which calls SIF init at
    /// 0x482E98). Observed: fast-boot never hits 0x212F70 and idles without GIF.
    /// </summary>
    private void KickMidwayMainPath()
    {
        // Signature: main prologue at 0x212F70
        if (Memory.Read32(0x00212F70) != 0x27BDFEE0)
        {
            _commercialSifInitKicked = true; // not this title
            return;
        }

        ulong pc = EE.PC & 0x1FFFFFFFUL;
        // Already in/near main — done
        if (pc is >= 0x00212F00 and < 0x00215000)
        {
            _commercialSifInitKicked = true;
            return;
        }

        _commercialSifInitKicked = true;
        Dmac.WriteRegister(0x1000E000, 1);
        Dmac.WriteRegister(0x1000F520, 0x1201);

        // Pre-register disc IRX + ensure BIOS service map is live *before* any CRT0/main.
        // Destinations (LOADFILE, CDVD, SIF stack names, etc.) come from BiosBootHost + disc
        // preload — not from Midway PC-range assists.
        if (!BiosBoot.Started)
            BiosBoot.StartCommercialIop(this);
        PreloadIopModulesFromDisc();
        IopModules.BindDisc(Cdvd.MountedPath);

        // Prefer real CRT0 when BIOS services are installed. Historical failures (worker stuck
        // on an unsignaled SIF sema) were from missing IOP *destinations*; with BiosBootHost
        // those destinations exist as HLE contracts. Fake jump-to-main remains via DETPS2_FAKE_CRT0=1.
        bool useRealCrt0 = PreferRealCrt0 && BiosBoot.Started
                           && Memory.Read32(0x0011C200) != 0; // CRT0 still present in ELF image
        if (useRealCrt0)
        {
            // Enter just before SetupThread in real CRT0 (same address prior experiments used).
            EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
            EE.SetGpr(28, new EmotionEngine.Gpr128 { Lo = 0 });
            EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = 0x0011C2A8 });
            EE.PC = 0x0011C200;
            LastGoodEePc = 0x0011C200;
            EE.COP0_Status |= (1u << 16) | 1u;
            MidwayAssist.OnMainKick(this); // ISO bind / worklist plant still useful under CRT0
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine($"[BIOS] KickMidwayMainPath → real CRT0 @ 0x0011C200 (bios services up)");
            return;
        }

        // Fallback: synthetic main() entry (old baseline).
        if (pc < 0x0011C250)
        {
            EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
            EE.SetGpr(28, new EmotionEngine.Gpr128 { Lo = 0 });
        }

        uint argBase = 0x005C9C00;
        uint a0 = Memory.Read32(argBase);
        if (a0 == 0 || unchecked((int)a0) < 0)
            a0 = 1;
        EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = a0 });
        EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = argBase + 4 });
        EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = 0x0011C2A8 });
        EE.PC = 0x00212F70;
        LastGoodEePc = 0x00212F70;
        EE.COP0_Status |= (1u << 16) | 1u; // EIE | IE

        MidwayAssist.OnMainKick(this);
    }

    /// <summary>
    /// Load IRX modules from the mounted ISO into IOP RAM / name table.
    /// Accepts <c>IOP/</c>, <c>MODULES/</c>, and disc-root IRX (Blood Omen 2 ships
    /// SIO2MAN/PADMAN/… at the ISO root next to IOPRP234.IMG).
    /// </summary>
    private void PreloadIopModulesFromDisc()
    {
        string? path = Cdvd.MountedPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;
        try
        {
            var vol = Iso9660.OpenFile(path);
            if (vol == null) return;
            foreach (var f in vol.Files)
            {
                if (f.IsDirectory) continue;
                string u = f.Path.Replace('\\', '/').ToUpperInvariant();
                string nameU = f.Name.ToUpperInvariant();
                bool isIrx = nameU.EndsWith(".IRX", StringComparison.Ordinal);
                bool isImg = nameU.EndsWith(".IMG", StringComparison.Ordinal)
                             && nameU.StartsWith("IOPRP", StringComparison.Ordinal);
                if (!isIrx && !isImg)
                    continue;
                // Accept IOP/, MODULES/, or root-level IRX/IOPRP images (no nested junk).
                int slash = u.LastIndexOf('/');
                bool rootLevel = slash < 0;
                bool inIop = u.StartsWith("IOP/", StringComparison.Ordinal) || u.Contains("/IOP/", StringComparison.Ordinal);
                bool inModules = u.StartsWith("MODULES/", StringComparison.Ordinal) || u.Contains("/MODULES/", StringComparison.Ordinal);
                if (!rootLevel && !inIop && !inModules)
                    continue;
                if (f.Size == 0 || f.Size > 2_000_000) continue;
                string modName = Path.GetFileNameWithoutExtension(f.Name);
                // .IMG is IOP reboot image — register name only (no ELF load)
                if (isImg)
                {
                    IopModules.RegisterModule(modName);
                    continue;
                }
                byte[]? data = Iso9660.ReadFile(vol, f.Path);
                if (data == null || data.Length < 52)
                {
                    IopModules.RegisterModule(modName);
                    continue;
                }
                try
                {
                    var r = IopModules.LoadIrx(data, Memory, modName);
                    _ = r;
                }
                catch
                {
                    IopModules.RegisterModule(modName);
                }
            }
            // Always ensure core names exist
            foreach (var n in new[] { "SIO2MAN", "PADMAN", "MCMAN", "MCSERV", "LIBSD", "CDVDSTM", "CRI_ADXI", "IOPRP300", "IOPRP234", "IOPRP214", "IOPFILE", "IOPMEM", "IOPSND", "SDRDRV" })
                IopModules.RegisterModule(n);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Start Midway worker with the real message ring base if SIF init created one,
    /// else a scratch ring.
    /// </summary>
    /// <summary>
    /// Start every Alive-but-not-Started commercial thread after a short grace, with an
    /// entry-appropriate StartThread arg. SIF-RPC dispatch (~0x480A18) gets the packet ring;
    /// CRI ADX worker (0x4147F8) and other workers get arg 0.
    /// </summary>
    private void KickAllDormantCommercialWorkers()
    {
        const ulong grace = 200_000UL;
        bool startedAny = false;
        foreach (var t in Hle.Kernel.AllThreads)
        {
            if (t.Id < 2 || !t.Alive || t.Started || _commercialWorkerKickedIds.Contains(t.Id))
            {
                _commercialWorkerSeenAt.Remove(t.Id);
                continue;
            }
            if (!_commercialWorkerSeenAt.TryGetValue(t.Id, out var seenAt))
            {
                _commercialWorkerSeenAt[t.Id] = MasterCycles;
                continue;
            }
            if (MasterCycles - seenAt < grace) continue;

            uint entry = t.Entry;
            ulong arg = 0;
            // SIF-RPC library worker: needs the packet-ring base as $a0
            if (entry is >= 0x00480000u and < 0x00487000u)
            {
                uint ring = Memory.Read32(0x77A080);
                if (ring < 0x100000 || (ring & 0x1FFFFFFFu) >= SystemMemory.RDRAM_SIZE)
                    ring = 0x01F80000;
                arg = ring;
            }
            // CRI ADX workers (0x414xxx): the game's own StartThread (syscall 0x22 ×5)
            // already starts these. Re-kicking after ExitThread left them DORMANT with
            // ADX flags already planted → instant re-exit + main SuspendThread thrash
            // (650k× GetThreadId/Refer/ChangePrio per 150M). Never auto-start ADX range.
            else if (entry is >= 0x00414000u and < 0x00416000u)
            {
                _commercialWorkerKickedIds.Add(t.Id); // don't keep retrying
                _commercialWorkerSeenAt.Remove(t.Id);
                continue;
            }

            Hle.Kernel.StartAndMaybeSwitch(EE, t.Id, switchNow: false, arg: arg, fromSyscall: false);
            _commercialWorkerKickedIds.Add(t.Id);
            _commercialWorkerSeenAt.Remove(t.Id);
            startedAny = true;
            // Always log once — this is the multi-worker fix path
            Console.Error.WriteLine(
                $"[RPC] KickCommercialWorker tid={t.Id} entry=0x{entry:X8} arg=0x{arg:X} cyc={MasterCycles}");
        }
        if (startedAny)
            Hle.Kernel.YieldToWorker(EE);
    }

    /// <summary>Legacy single-thread entry — routes to multi-worker kick.</summary>
    private void KickCommercialWorker() => KickAllDormantCommercialWorkers();

    /// <summary>Phase 32: EE JIT microbench path (bit-identical to Step when Det).</summary>
    public int RunEeJit(ulong cycles)
    {
        EeJit.Enabled = true;
        return EeJit.Execute(cycles);
    }

    /// <summary>Phase 33: save frame snapshot (delta).</summary>
    public void SaveFrameSnapshot() => Snapshots.SaveDelta(this);

    /// <summary>Phase 33: load frame snapshot.</summary>
    public bool LoadFrameSnapshot(ulong frame) => Snapshots.LoadFrame(this, frame);

    /// <summary>Debug single-step: run until one instruction executes or breakpoint.</summary>
    public void DebugStepInstruction()
    {
        Debugger.Enabled = true;
        Debugger.Continue();
        Debugger.RequestStep();
        // Step EE only a small budget so one instruction hits
        EE.Step(4);
        if (!Debugger.Halted)
            Debugger.RequestStep();
    }

    public void PresentFrame()
    {
        // FMV host pacing is done by the Desktop present path (GameDisplayWindow)
        // or explicit MidwayAssist.OnHostPresent — not here, to avoid double-step
        // when both PresentFrame and the window present in the same UI tick.
        Present.PresentFromGs(Gs);
    }

    public void SetAudioSink(IAudioSink? sink) => Spu2.SetSink(sink);

    /// <summary>BIOS boot harness: run up to maxCycles sampling PC; returns report string.</summary>
    public string RunBiosHarness(ulong maxCycles = 5_000_000, ulong sampleEvery = 100_000)
    {
        BootTrace.RunWithTrace(this, maxCycles, sampleEvery);
        return BootTrace.FormatReport();
    }

    public void Reset()
    {
        Scheduler.Reset();
        Hle.Reset();
        Pad.Reset();
        Spu2.Reset();
        Sio2.Reset();
        Multitap.Reset();
        MemCard.Reset();
        BootTrace.Reset();
        Debugger.Reset();
        Tracer.Clear();
        InputRecording.Reset();
        Present.Reset();
        IopModules.Reset();
        IopModules.BindMemCard(MemCard);
        IopModules.InitDefaults();
        Sio2.Attach(Pad, MemCard);
        Sio2.AttachMultitap(Multitap.Ports);
        Telemetry.Reset();
        Ipu.Reset();
        EeJit.Reset();
        Snapshots.Reset();
        UseJit = false;
        LastGoodEePc = 0;
        _commercialSifInitKicked = false;
        _commercialWorkerSeenAt.Clear();
        _commercialWorkerKickedIds.Clear();
        // The fallback MidwayAssist instance (used when no quirk is active) is never
        // stepped/touched, so it never accumulates real state and needs no reset here.
        ActiveQuirk?.Reset();
        ActiveQuirk = null;
        SoftFloatBridge.Reset();
        BiosBoot.Reset();
        IopVblank.Reset();
        IopSystem.Reset();
        IopEeconf.Reset();
        IopSsbusc.Reset();
        IopSysclibHeaplib.Reset();
        IopDmacMan.Reset();
        IopExtendedBios.Reset();
        IopLibSd.Reset();
        IopExcepMan.Reset();
        // Re-bind after IopSystem.Reset so FILEIO ENODEV/AddDrv still route to the live host.
        IopModules.BindIopSystem(IopSystem);
    }

    /// <summary>Phase 21: boot harness JSON including telemetry blockers.</summary>
    public string DumpBootReportJson(ulong maxCycles = 5_000_000, ulong sampleEvery = 100_000)
    {
        BootTrace.RunWithTrace(this, maxCycles, sampleEvery);
        return BootTrace.ToJson(this);
    }

    /// <summary>Phase 22: load IRX into IOP RAM and register module.</summary>
    public IrxLoader.LoadResult LoadIrx(byte[] elf, string? name = null) =>
        IopModules.LoadIrx(elf, Memory, name);

    /// <summary>Phase 23: Prefer architectural SYSCALL vs HLE.</summary>
    public void SetPreferHleSyscalls(bool prefer)
    {
        EE.PreferHleSyscalls = prefer;
        Hle.Level = prefer ? HleLevel.Standard : HleLevel.Minimal;
    }

    /// <summary>Helper: write RPC packet and process via SIF (tests / tools).</summary>
    public uint CallRpc(uint cmd, uint eeBuffer, uint size)
    {
        const uint pkt = 0x0000F000;
        new SifRpcPacket { Cmd = cmd, EeBuffer = eeBuffer, Size = size, Result = 0 }.Write(Memory, pkt);
        Sif.SubmitRpc(pkt);
        Sif.Step(16);
        return Memory.Read32(pkt + 12);
    }

    /// <summary>Increments once per real-RunFor-slice (see RunFor's own call sites — NOT tied
    /// to MasterCycles, which only advances once per whole Scheduler.RunFor slice and so can't
    /// distinguish "this tick" from "an earlier tick"). Used to tag real SIF RPC queue entries
    /// (Sif.cs's _realRpcQueue) so they're never drained within the same tick they were
    /// submitted in, while still being drainable on any later tick.
    ///
    /// This used to live inside an `ISchedulable.Step()` override, on the mistaken assumption
    /// that Ps2System itself was registered with its own Scheduler the way EE/Iop/Sif/etc. are
    /// (Ps2System.cs's constructor: Scheduler.Register(EE), .Register(Iop), ...). It never was
    /// — only its individual components are — so that Step() override was dead code nothing
    /// ever called, and it also would have double-stepped every one of those components had it
    /// somehow been registered (it re-called EE.Step/Iop.Step/etc. itself). Removed; the
    /// increment+drain now happens directly in RunFor, which is really called every slice.</summary>
    public ulong SchedulerGeneration { get; private set; }
}
