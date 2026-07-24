using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system (Phase 11 tooling / netplay foundation).
/// </summary>
public sealed class Ps2System : ISchedulable
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
    public IopJit IopJit { get; }
    public VuAccelerator VuAccel { get; }
    public SnapshotEngine Snapshots { get; }

    public Scheduler Scheduler { get; }

    public ulong MasterCycles => Scheduler.MasterCycles;
    public bool UseJit { get; set; }
    /// <summary>Last EE PC in game/code space — used to recover from low-memory thrash.</summary>
    public ulong LastGoodEePc { get; set; }
    private bool _commercialWorkerKicked;
    private bool _commercialSifInitKicked;
    /// <summary>Shaolin Monks / Midway commercial boot + logo assist.</summary>
    public MidwayBootAssist MidwayAssist { get; } = new();

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
        Sif = new Sif(Memory, Intc);
        IopModules.BindMemCard(MemCard);
        Sif.BindServices(IopModules, Pad, Cdvd);
        Pipeline = new GsPipeline(Gs, Gif, Pcrtc);
        BootTrace = new BootTrace();
        Ipu = new Ipu();
        EeJit = new EeJit(EE, Memory);
        IopJit = new IopJit(Iop, Memory);
        VuAccel = new VuAccelerator();
        Snapshots = new SnapshotEngine();

        Dmac.SetGif(Gif);
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

    public void LoadBios(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("BIOS file not found", path);

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
            const ulong slice = 50_000;
            while (left > 0)
            {
                ulong pcPhys = EE.PC & 0x1FFFFFFFUL;
                // Track only real game RDRAM code (1MB..32MB)
                if (pcPhys >= 0x00100000UL && pcPhys < SystemMemory.RDRAM_SIZE)
                    LastGoodEePc = EE.PC;
                KernelBootstrap.RescueIfLostInLowMem(this, LastGoodEePc);

                // Midway: jump to real main (0x212F70). Early kick (after ~100k) is
                // required — delaying until the idle pump misses the GIF clear path.
                if (!_commercialSifInitKicked && MasterCycles > 100_000)
                    KickMidwayMainPath();

                // SIF worklist auto-complete + real MIDWAY.SFD logo presentation
                MidwayAssist.Step(this);

                ulong n = left > slice ? slice : left;
                Scheduler.RunFor(n);
                left -= n;
            }
        }
        else
        {
            Scheduler.RunFor(cyclesToRun);
        }
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

        // Run CRT0 SetupThread/Heap if we haven't (needed for SP)
        if (pc < 0x0011C250)
        {
            // Minimal: SetupThread-equivalent SP
            EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
            EE.SetGpr(28, new EmotionEngine.Gpr128 { Lo = 0 }); // gp
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

        // Pre-register IOP modules from the mounted disc so sceSifLoadModule
        // checks succeed (MK loads PADMAN/SIO2MAN/CRI_ADXI/etc. before logo).
        PreloadIopModulesFromDisc();
        IopModules.BindDisc(Cdvd.MountedPath);
        MidwayAssist.OnMainKick(this);
    }

    /// <summary>Load IRX modules listed under IOP/ on the mounted ISO into IOP RAM.</summary>
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
                string u = f.Path.ToUpperInvariant();
                if (!u.EndsWith(".IRX", StringComparison.Ordinal) && !u.EndsWith(".IMG", StringComparison.Ordinal))
                    continue;
                // Prefer IOP/ over MODULES/ duplicates
                if (!u.StartsWith("IOP/", StringComparison.Ordinal) && !u.Contains("/IOP/"))
                    continue;
                if (f.Size == 0 || f.Size > 2_000_000) continue;
                byte[]? data = Iso9660.ReadFile(vol, f.Path);
                if (data == null || data.Length < 52) continue;
                // .IMG is IOP reboot image — register as module name only
                if (u.EndsWith(".IMG", StringComparison.Ordinal))
                {
                    IopModules.RegisterModule(Path.GetFileNameWithoutExtension(f.Name));
                    continue;
                }
                try
                {
                    var r = IopModules.LoadIrx(data, Memory, Path.GetFileNameWithoutExtension(f.Name));
                    _ = r;
                }
                catch
                {
                    IopModules.RegisterModule(Path.GetFileNameWithoutExtension(f.Name));
                }
            }
            // Always ensure core names exist
            foreach (var n in new[] { "SIO2MAN", "PADMAN", "MCMAN", "MCSERV", "LIBSD", "CDVDSTM", "CRI_ADXI", "IOPRP300" })
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
    private void KickCommercialWorker()
    {
        _commercialWorkerKicked = true;
        uint ring = Memory.Read32(0x77A080);
        if (ring < 0x100000 || (ring & 0x1FFFFFFFu) >= SystemMemory.RDRAM_SIZE)
            ring = 0x01F80000;
        // Prefer thread id 2 (first CreateThread after main)
        int tid = 2;
        var t = Hle.Kernel.GetThread(tid);
        if (t == null || !t.Alive)
        {
            for (int id = 2; id <= Hle.Kernel.ThreadCount + 2; id++)
            {
                t = Hle.Kernel.GetThread(id);
                if (t != null && t.Alive) { tid = id; break; }
            }
        }
        if (t == null || !t.Alive) return;
        Hle.Kernel.StartAndMaybeSwitch(EE, tid, switchNow: false, arg: ring, fromSyscall: false);
        // Cooperative: yield once so worker can run a quantum
        Hle.Kernel.YieldToWorker(EE);
    }

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
        IopJit.Reset();
        VuAccel.Reset();
        Snapshots.Reset();
        UseJit = false;
        LastGoodEePc = 0;
        _commercialWorkerKicked = false;
        _commercialSifInitKicked = false;
        MidwayAssist.Reset();
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

    int ISchedulable.Step(ulong maxCycles)
    {
        if (UseJit && EeJit.Enabled)
            EeJit.Execute(maxCycles);
        else
            EE.Step(maxCycles);
        Timers.Step(maxCycles);
        Dmac.Step(maxCycles);
        Vif.Step(maxCycles);
        Gif.Step(maxCycles);
        Gs.Step(maxCycles);
        Pcrtc.Step(maxCycles);
        Intc.Step(maxCycles);
        if (UseJit && IopJit.Enabled)
            IopJit.Execute(maxCycles);
        else
            Iop.Step(maxCycles);
        Cdvd.Step(maxCycles);
        Sif.Step(maxCycles);
        Spu2.Step(maxCycles);
        Ipu.Step(maxCycles);
        return 0;
    }

    void ISchedulable.Reset() => Reset();
}
