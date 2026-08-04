using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// C# HLE for SCPH70008 ROMDIR modules that are outside the original IOPBTCONF @800
/// commercial-fast-path set but still ship in the real BIOS and are loaded via
/// <c>rom0:</c> / UDNL / LOADFILE during retail bring-up.
///
/// <para><b>Authority:</b> live ROMDIR parse of SCPH70008 (101 entries), Ghidra 12.1.2
/// headless of extracted IRX (<c>SECRMAN</c>, <c>UDNL</c>, <c>CLEARSPU</c>, <c>LIBSD</c>,
/// <c>ADDDRV</c>, <c>XMTAPMAN</c>), retail IOPRP*.IMG ROMDIR-in-IMG layout (RESET@0),
/// and <c>docs/BIOS_DISSECTION.md</c>.</para>
///
/// <para><b>What this is:</b> functional service contracts so name probes, export linking,
/// CLEARSPU soft-reset, UDNL IOPRP/DNAS image apply (ROMDIR + IOPBTCONF + optional LoadIrx),
/// and SECRMAN plain-ELF passthrough succeed without title-local plants.
/// Under <c>DETPS2_LITERAL_IRX=1</c>, commercial UDNL handoff LoadIrx’s extractable ELFs and
/// records Entry/LoadBase for IOP exec (see <c>docs/irx/UDNL_IOPRP.md</c>).
/// <b>Not</b> MagicGate crypto, full mechacon auth, or cycle-accurate R3000 IRX execution.</para>
/// </summary>
public sealed class IopExtendedBiosHost
{
    public const string SecrmanLibName = "secrman";
    public const string LibsdLibName = "libsd";
    public const string ThmsgbxLibName = "thmsgbx";
    public const string ThvpoolLibName = "thvpool";
    public const string ThfpoolLibName = "thfpool";

    // Plant region: after SYSCLIB/HEAPLIB stubs (0x4000..0x6000).
    public const uint StubRegionPhys = 0x00006000;
    public const uint StubRegionSize = 0x00002800; // 10 KiB

    /// <summary>SECRMAN Secr*BootFile success (plain / already-decrypted ELF).</summary>
    public const int SecrOk = 0;
    /// <summary>
    /// SECRMAN cannot decrypt — MagicGate secrets absent / payload not plain ELF.
    /// Surfaces as a clear failure for MG LOADFILE (no fake success).
    /// </summary>
    public const int SecrErrCannotDecrypt = -1;
    /// <summary>Null/empty path or missing file bytes for Secr*BootFile.</summary>
    public const int SecrErrNoFile = -2;

    /// <summary>
    /// One extractable ELF successfully <see cref="IopModuleHost.LoadIrx"/>'d from an IOPRP/DNAS
    /// image. <see cref="Entry"/> / <see cref="LoadBase"/> are ready for IOP R3000 exec (WP-25+).
    /// </summary>
    public readonly struct IopRpLoadedEntry
    {
        public string Name { get; init; }
        public uint Entry { get; init; }
        public uint LoadBase { get; init; }
        public uint Size { get; init; }
        public int ModuleId { get; init; }
    }

    private bool _installed;
    private ulong _clearSpuRuns;
    private ulong _udnlApplies;
    private ulong _secrBootPassthroughs;
    private ulong _secrEncryptedRejects;
    private ulong _iopRpImagesApplied;
    private int _lastIopRpModulesRegistered;
    private int _lastIopRpElfsLoaded;
    /// <summary>
    /// When true, next ApplyIopRpImageCore prefers name-only RegisterModule (legacy HLE commercial
    /// handoff). Overridden when <c>DETPS2_LITERAL_IRX=1</c> so retail images still LoadIrx.
    /// </summary>
    private bool _iopRpNameOnlyApply;
    private string _lastUdnlArg = "";
    private string _lastUdnlVersion = "";
    private string _lastIopRpSource = "";
    private readonly List<string> _lastIopRpModuleNames = new();
    private readonly List<IopRpLoadedEntry> _lastIopRpLoadedEntries = new();

    public bool Installed => _installed;
    public ulong ClearSpuRuns => _clearSpuRuns;
    public ulong UdnlApplies => _udnlApplies;
    public ulong SecrBootPassthroughs => _secrBootPassthroughs;
    public ulong SecrEncryptedRejects => _secrEncryptedRejects;
    public ulong IopRpImagesApplied => _iopRpImagesApplied;
    public int LastIopRpModulesRegistered => _lastIopRpModulesRegistered;
    public int LastIopRpElfsLoaded => _lastIopRpElfsLoaded;
    public string LastUdnlArg => _lastUdnlArg;
    public string LastUdnlVersion => _lastUdnlVersion;
    public string LastIopRpSource => _lastIopRpSource;
    public IReadOnlyList<string> LastIopRpModuleNames => _lastIopRpModuleNames;
    /// <summary>ELFs LoadIrx'd on the last image apply (entry/loadBase for WP-25 exec).</summary>
    public IReadOnlyList<IopRpLoadedEntry> LastIopRpLoadedEntries => _lastIopRpLoadedEntries;

    public void Reset()
    {
        _installed = false;
        _clearSpuRuns = 0;
        _udnlApplies = 0;
        _secrBootPassthroughs = 0;
        _secrEncryptedRejects = 0;
        _iopRpImagesApplied = 0;
        _lastIopRpModulesRegistered = 0;
        _lastIopRpElfsLoaded = 0;
        _iopRpNameOnlyApply = false;
        _lastUdnlArg = "";
        _lastUdnlVersion = "";
        _lastIopRpSource = "";
        _lastIopRpModuleNames.Clear();
        _lastIopRpLoadedEntries.Clear();
    }

    /// <summary>
    /// Always load+prepare real disc/BIOS IRX unless emergency HLE bisect
    /// (<c>DETPS2_FORCE_HLE_IOP=1</c> / legacy <c>DETPS2_LITERAL_IRX=0</c>).
    /// Delegates to <see cref="IopModuleHost.IsLiteralIrxEnabled"/>.
    /// </summary>
    public static bool IsLiteralIrxEnabled() => IopModuleHost.IsLiteralIrxEnabled;

    /// <summary>
    /// <c>DETPS2_IOPRP_NAME_ONLY=1</c> forces name-only RegisterModule for all ApplyIopRp* paths
    /// (emergency bisect; overrides literal LoadIrx).
    /// </summary>
    public static bool IsIopRpNameOnlyForced() =>
        string.Equals(Environment.GetEnvironmentVariable("DETPS2_IOPRP_NAME_ONLY"), "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Install export tables + soft-register every extended ROMDIR service name.
    /// Called from <see cref="BiosBootHost"/> after SYSCLIB/HEAPLIB.
    /// </summary>
    public void Install(Ps2System sys)
    {
        if (sys == null) return;
        var mem = sys.Memory;
        var modules = sys.IopModules;

        uint cursor = StubRegionPhys;
        uint stubJrRa = cursor;
        mem.Write32(cursor, 0x03E00008u); // jr ra
        mem.Write32(cursor + 4, 0x00000000u); // nop
        cursor += 8;

        // SECRMAN export table (Ghidra: SecrAuthCard / SecrCardBootFile / SecrDiskBootFile strings).
        // Ordinals are host-side placeholders; bodies are jr ra until IOP IRX exec.
        var secrExports = MakeStubExports(mem, ref cursor, stubJrRa, 24);
        cursor = PlantExportTable(mem, cursor, SecrmanLibName, 1, 1, secrExports);
        modules.RegisterExportLibrary(new IrxLoader.ExportTable
        {
            Name = SecrmanLibName,
            VersionMajor = 1,
            VersionMinor = 1,
            Exports = secrExports
        });

        // LIBSD — functional core (export ordinals + sceSdInit/SetParam/key-on) on IopLibSdHost.
        // Replaces jr-ra-only plant so retail LinkImports resolve libsd 1.4 and host API works.
        sys.IopLibSd.Install(sys);

        // THREADMAN extras — thmsgbx / thvpool / thfpool (decomp WARNING strings in THREADMAN.IRX).
        // Functional C# pools/mailboxes live on KernelState; export tables satisfy LOADCORE link.
        PlantThreadmanExtraLibs(mem, modules, ref cursor, stubJrRa);

        // Soft-register every extended ROMDIR service (name probes + LOADFILE search).
        foreach (string n in ExtendedRomdirServiceNames)
            modules.RegisterModule(n, systemResident: true);

        // X* retail aliases share primary HLE modules.
        foreach (var (alias, primary) in XModuleAliases)
        {
            modules.RegisterModule(alias, systemResident: true);
            modules.RegisterModule("rom0:" + alias, systemResident: true);
            modules.RegisterModule(primary, systemResident: true);
        }

        _installed = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BIOS] IopExtendedBiosHost installed cursor=0x{cursor:X} " +
                $"(secrman/libsd/th* + {ExtendedRomdirServiceNames.Length} names)");
    }

    /// <summary>
    /// CLEARSPU.IRX contract: soft-reset SPU2 voices / mix state so cold boot and post-reboot
    /// audio init do not inherit stale voice PCM. Ghidra string: <c>clearspu: completed</c>.
    /// </summary>
    public void ApplyClearSpu(Ps2System sys)
    {
        if (sys == null) return;
        sys.Spu2.Reset();
        _clearSpuRuns++;
        sys.IopModules.RegisterModule("CLEARSPU", systemResident: true);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine($"[BIOS] CLEARSPU soft-reset runs={_clearSpuRuns}");
    }

    /// <summary>
    /// UDNL.IRX handoff after <c>SifIopReset("rom0:UDNL …IOPRPxxx.IMG")</c>.
    /// Real UDNL opens the image, walks IOPBTCONF inside it (or ROMDIR module list), and
    /// loads listed IRX. HLE: parse version tag, try resolve/parse IOPRP/DNAS image bytes
    /// from disc when available, re-register common modules, CLEARSPU, SECRMAN present.
    /// With <c>DETPS2_LITERAL_IRX=1</c>, extractable image ELFs are LoadIrx’d and entries
    /// recorded (<see cref="LastIopRpLoadedEntries"/>); otherwise commercial apply is name-only.
    /// LOADFILE GetVersion ASCII is set via <see cref="RealSifRpc.OnIopReboot"/>.
    /// </summary>
    public void ApplyUdnlHandoff(Ps2System sys, string? rebootArg)
    {
        if (sys == null) return;

        // Real IOP reset (2026-08-03): a genuine hardware reset wipes the module/heap area, so
        // SYSMEM's real (persistent, never-reloading) heap bookkeeping and every resident
        // module's own post-boot state come back fresh. We don't reset -- so real pool
        // allocations from modules that DO reload each handoff (LOADCORE/THREADMAN/SIFCMD/...)
        // never got freed and kept eating the same real heap, until a late-loading module's own
        // real _start (confirmed live: Whiplash's IOPFILE.IRX calling CreateSema) got real
        // KE_NO_MEMORY back and bailed out before ever creating the worker threads that would
        // register real SIF RPC services. See SystemMemory.RestoreIopHeapRegion's own doc
        // comment for the full chain. Restore bytes first, then let placement start from the
        // bottom of the now-genuinely-free area again (IopModuleHost.FindFreeIopBase still
        // correctly skips anything still really resident).
        if (sys.Memory.RestoreIopHeapRegion())
            sys.IopModules.ResetModulePlacementForIopReset();

        // Real IOP reset also resets the INTC and the R3000's own interrupt-enable state (see
        // both methods' doc comments) -- otherwise a latched-but-unacked interrupt from before
        // the handoff fires on the very first instruction of the freshly-reloaded module chain.
        sys.Memory.ResetIopInterruptControllerForIopReset();
        sys.Iop.ResetInterruptStateForIopReset();

        _lastUdnlArg = rebootArg ?? "";
        string ver = RealSifRpc.ExtractIopRpVersionAscii(_lastUdnlArg);
        if (!string.IsNullOrEmpty(ver))
            _lastUdnlVersion = ver;

        // Always present UDNL itself + common post-image modules (fallback when no image bytes).
        sys.IopModules.RegisterModule("UDNL", systemResident: true);
        foreach (string n in UdnlImageModuleNames)
            sys.IopModules.RegisterModule(n, systemResident: true);

        // Diagnostic: skip image apply entirely (name list above still registers).
        // Live SM A/B: full ApplyIopRpImage (even name-only) correlated with Exit@13M —
        // leave this opt-out while diagnosing; default remains apply-on.
        if (string.Equals(Environment.GetEnvironmentVariable("DETPS2_UDNL_SKIP_IMAGE"), "1",
                StringComparison.Ordinal))
        {
            ApplyClearSpu(sys);
            sys.IopModules.RegisterModule("SECRMAN", systemResident: true);
            _udnlApplies++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[BIOS] UDNL handoff SKIP image applies={_udnlApplies} ver=\"{_lastUdnlVersion}\" " +
                    $"arg=\"{_lastUdnlArg}\"");
            return;
        }

        // Version-specific IOPRP token (e.g. IOPRP300) already handled by BiosBootHost;
        // re-assert here so a direct call still works.
        if (!string.IsNullOrEmpty(_lastUdnlVersion) && _lastUdnlVersion.Length >= 3)
        {
            // "3000" → IOPRP300 ; "2340" → IOPRP234
            string digits = _lastUdnlVersion.TrimEnd('0');
            if (digits.Length >= 3)
                sys.IopModules.RegisterModule("IOPRP" + digits[..Math.Min(3, digits.Length)], systemResident: true);
            sys.IopModules.RegisterModule("IOPRP" + _lastUdnlVersion.Substring(0, Math.Min(3, _lastUdnlVersion.Length)), systemResident: true);
        }

        // Prefer real IOPRP/DNAS container when path is resolvable via FILEIO/ISO.
        // Always LoadIrx extractable ELFs (IRX is the product). Name-only only if
        // DETPS2_IOPRP_NAME_ONLY=1 or emergency FORCE_HLE_IOP / LITERAL_IRX=0.
        byte[]? image = TryResolveUdnlImageBytes(sys, _lastUdnlArg);
        if (image != null && image.Length >= 32)
        {
            string src = ExtractUdnlImagePath(_lastUdnlArg) ?? "udnl-image";
            _iopRpNameOnlyApply = !IsLiteralIrxEnabled();
            try { ApplyIopRpImage(sys, image, src); }
            finally { _iopRpNameOnlyApply = false; }
        }

        // CLEARSPU is commonly re-run after UDNL image apply.
        ApplyClearSpu(sys);

        // SECRMAN present for encrypted module path probes (LOADFILE MG_* uses plain path load).
        sys.IopModules.RegisterModule("SECRMAN", systemResident: true);

        _udnlApplies++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" ||
            Environment.GetEnvironmentVariable("DETPS2_TRACE_REBOOT") == "1")
            Console.Error.WriteLine(
                $"[BIOS] UDNL handoff applies={_udnlApplies} ver=\"{_lastUdnlVersion}\" " +
                $"img=\"{_lastIopRpSource}\" reg={_lastIopRpModulesRegistered} elfs={_lastIopRpElfsLoaded} " +
                $"arg=\"{_lastUdnlArg}\"");
    }

    /// <summary>
    /// Parse a retail IOPRP/DNAS ROMDIR-in-IMG container and register module names.
    /// Common layout: <c>RESET</c> entry at offset 0, cumulative naive payloads, optional
    /// <c>IOPBTCONF</c> text listing load order (else all non-meta ROMDIR names).
    /// <para>
    /// Load policy (see <c>docs/irx/UDNL_IOPRP.md</c>):
    /// <list type="bullet">
    /// <item>Direct callers / smokes: <see cref="IopModuleHost.LoadIrx"/> extractable ELFs.</item>
    /// <item>UDNL commercial handoff: name-only unless <c>DETPS2_LITERAL_IRX=1</c>.</item>
    /// <item><c>DETPS2_IOPRP_NAME_ONLY=1</c>: force name-only for all callers (bisect).</item>
    /// </list>
    /// When LoadIrx succeeds, entries are recorded on <see cref="LastIopRpLoadedEntries"/>
    /// (Entry/LoadBase for future IOP R3000 exec).
    /// </para>
    /// </summary>
    /// <returns>Number of module names registered from the image.</returns>
    public int ApplyIopRpImage(Ps2System sys, byte[] image, string? sourceName = null)
    {
        if (sys == null || image == null || image.Length < 32)
            return 0;
        int reg = ApplyIopRpImageCore(sys.IopModules, sys.Memory, image, sourceName, updateHost: true);
        // WP-25: StartLoadedModule for non-HLE-owned extractable ELFs only.
        // HLE stack (FILEIO/LOADFILE/CDVD/SIF*) stays LoadIrx-only — incomplete re-exec
        // regressed SotC FILEIO-2200 while RealSifRpc still answers.
        if (IsLiteralIrxEnabled() && _lastIopRpLoadedEntries.Count > 0)
            StartLoadedIopRpModules(sys);
        return reg;
    }

    /// <summary>
    /// Static entry for LOADFILE / callers without <see cref="Ps2System"/>: parse image and
    /// register modules (LoadIrx when ELF extractable and not name-only). Does not update
    /// host counters. Does <b>not</b> run <c>_start</c> — use <see cref="ApplyIopRpImage"/>.
    /// </summary>
    public static int ApplyIopRpImageBytes(IopModuleHost modules, SystemMemory mem, byte[] image,
        string? sourceName, out int elfsLoaded)
    {
        elfsLoaded = 0;
        if (modules == null || mem == null || image == null || image.Length < 32)
            return 0;
        var tmp = new IopExtendedBiosHost();
        int reg = tmp.ApplyIopRpImageCore(modules, mem, image, sourceName, updateHost: false);
        elfsLoaded = tmp._lastIopRpElfsLoaded;
        return reg;
    }

    /// <summary>
    /// IOP stack modules still answered by C# RealSifRpc. Opt-in full re-exec:
    /// DETPS2_IOPRP_START_HLE_OWNED=1 once live SIF answers RPC (WP-22+).
    /// </summary>
    private static readonly HashSet<string> HleOwnedIopRpSkipStart = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYSMEM", "LOADCORE", "HEAPLIB", "EXCEPMAN", "INTRMAN", "INTRMANP", "INTRMANS",
        "TIMEMAN", "TIMEMANI", "TIMEMANS", "SSBUSC", "EECONF",
        "THREADMAN", "VBLANK", "VBLANK_A", "VBLANK_B",
        "IOMAN", "MODLOAD", "ROMDRV", "STDIO", "SYSCLIB", "IGREETING",
        "SIFMAN", "SIFCMD", "SIFINIT", "EESYNC", "REBOOT",
        "FILEIO", "LOADFILE", "CDVDMAN", "CDVDFSV",
        "MCMAN", "MCSERV", "PADMAN", "SIO2MAN",
    };

    /// <summary>
    /// WP-25: after IOPRP/DNAS LoadIrx, run R3000 _start on non-HLE-owned modules.
    /// Shared B3 DNAS280 / SotC·GoW IOPRP300 / Haven SYS250.
    /// </summary>
    public int StartLoadedIopRpModules(Ps2System sys, ulong maxInsnPerModule = 50_000)
    {
        if (sys == null || _lastIopRpLoadedEntries.Count == 0)
            return 0;
        if (!IsLiteralIrxEnabled())
            return 0;

        bool startHleOwned = string.Equals(
            Environment.GetEnvironmentVariable("DETPS2_IOPRP_START_HLE_OWNED"), "1",
            StringComparison.Ordinal);
        var budgetOverride = Environment.GetEnvironmentVariable("DETPS2_IOPRP_MAX_INSN_PER_MODULE");
        if (!string.IsNullOrEmpty(budgetOverride) && ulong.TryParse(budgetOverride, out var parsedBudget))
            maxInsnPerModule = parsedBudget;

        if (startHleOwned)
            RegisterModulesWithLoadCore(sys);

        int started = 0;
        int skippedHle = 0;
        ulong totalInsns = 0;
        bool stubReady = false;

        foreach (var le in _lastIopRpLoadedEntries)
        {
            int mid = le.ModuleId;
            if (mid < 0 && !string.IsNullOrEmpty(le.Name))
                mid = sys.IopModules.SearchModuleByName(le.Name);
            if (mid < 0) continue;
            if (!sys.IopModules.TryGetIrx(mid, out var irx) || !irx.HasImage || irx.Entry == 0)
                continue;
            if (irx.EntryExecuted && irx.LastEntryInstructions > 0)
                continue;

            if (!startHleOwned && HleOwnedIopRpSkipStart.Contains(irx.Name))
            {
                skippedHle++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    || Environment.GetEnvironmentVariable("DETPS2_TRACE_LITERAL_IRX") == "1")
                    Console.Error.WriteLine(
                        $"[BIOS] IOPRP StartLoadedModule SKIP hle-owned name={irx.Name} id={mid}");
                continue;
            }

            if (!stubReady)
            {
                // Real INTRMANI/INTRMANP never reload (not part of the game's IOPRP/UDNL image --
                // they stayed resident, real dispatcher and all, from the original BIOS boot walk).
                // Unconditionally clobbering VectorGeneral here with the synthetic skip-and-rfe
                // stub throws away that real, already-installed handler -- and unlike a real
                // handler, the stub never acks I_STAT, so any hardware interrupt taken through it
                // refires itself forever (confirmed live: THREADMAN's real _start parks frozen at
                // pc=0x80000080 the instant a real interrupt lands mid-init). Only install the
                // fallback stub when no real INTRMAN dispatcher is actually resident.
                bool hasRealIntrman =
                    sys.IopModules.SearchModuleByName("INTRMANI") is int im and >= 0 &&
                        sys.IopModules.TryGetIrx(im, out var intrmanIrx) && intrmanIrx.HasImage ||
                    sys.IopModules.SearchModuleByName("INTRMANP") is int ip and >= 0 &&
                        sys.IopModules.TryGetIrx(ip, out var intrmanpIrx) && intrmanpIrx.HasImage;
                if (!hasRealIntrman)
                    sys.Iop.InstallMinimalExceptionStub();
                stubReady = true;
            }

            if (string.Equals(irx.Name, "INTRMANP", StringComparison.OrdinalIgnoreCase))
                sys.Memory.IopWrite32(0xBF801450, SystemMemory.IopIoIntrmanConfigDefault);
            else if (string.Equals(irx.Name, "SIFMAN", StringComparison.OrdinalIgnoreCase))
                sys.Memory.IopWrite32(0xBF801450, 0);

            ulong irqBefore = sys.Iop.InterruptExceptionCount;
            ulong excBefore = sys.Iop.ExceptionCount;
            var run = sys.IopModules.StartLoadedModule(sys, mid, maxInsnPerModule);
            ulong irqDuring = sys.Iop.InterruptExceptionCount - irqBefore;
            ulong excDuring = sys.Iop.ExceptionCount - excBefore;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_SCHED_HOOKS") == "1" &&
                string.Equals(irx.Name, "THREADMAN", StringComparison.OrdinalIgnoreCase))
            {
                int intrmanId = sys.IopModules.SearchModuleByName("INTRMANI");
                if (intrmanId < 0) intrmanId = sys.IopModules.SearchModuleByName("INTRMANP");
                if (intrmanId >= 0 && sys.IopModules.TryGetIrx(intrmanId, out var intrman))
                {
                    uint ibase = IopModuleHost.ToIopPhys(intrman.LoadBase);
                    uint hook1 = sys.Memory.IopRead32(SystemMemory.IOP_RAM_BASE + ibase + 0x15a4);
                    uint hook2 = sys.Memory.IopRead32(SystemMemory.IOP_RAM_BASE + ibase + 0x15a0);
                    uint scratch1de0 = sys.Memory.IopRead32(SystemMemory.IOP_RAM_BASE + ibase + 0x1de0);
                    uint k400 = sys.Memory.IopRead32(SystemMemory.IOP_RAM_BASE + 0x400);
                    uint k404 = sys.Memory.IopRead32(SystemMemory.IOP_RAM_BASE + 0x404);
                    uint k408 = sys.Memory.IopRead32(SystemMemory.IOP_RAM_BASE + 0x408);
                    uint k40c = sys.Memory.IopRead32(SystemMemory.IOP_RAM_BASE + 0x40c);
                    Console.Error.WriteLine(
                        $"[SCHED-HOOKS] intrmanName={intrman.Name} intrmanBase=0x{ibase:X8} " +
                        $"hook@+15a4=0x{hook1:X8} hook@+15a0=0x{hook2:X8} scratch@+1de0=0x{scratch1de0:X8} " +
                        $"k0x400=0x{k400:X8} k0x404=0x{k404:X8} k0x408=0x{k408:X8} k0x40c=0x{k40c:X8}");
                }
                else
                {
                    Console.Error.WriteLine("[SCHED-HOOKS] INTRMANI/INTRMANP not found in module table");
                }
                // THREADMAN's own "current thread" / "next thread" globals (file-relative 0x67d0/
                // 0x67d4 in the real module image) -- FUN_00000010 (_start) should set these to a
                // real, RUNNING-marked boot-thread TCB before ever installing the 0x940/0xbc4
                // scheduler hooks into INTRMANI. If still zero here, FUN_000006bc's ready-pick
                // dereferences address 0xc directly -- garbage, not a real TCB status byte.
                uint tbase = IopModuleHost.ToIopPhys(irx.LoadBase);
                // Build-specific: this game-disc THREADMAN's own .bss starts at file-relative
                // 0x7090 (verified via DETPS2_TRACE_SECTIONS against ITS OWN section table --
                // the BIOS-bundled build has a different layout, .bss at 0x67d0; reusing that
                // offset across builds was the earlier session's actual bug, not this field).
                uint curThread = sys.Memory.IopRead32(SystemMemory.IOP_RAM_BASE + tbase + 0x7090);
                uint nextThread = sys.Memory.IopRead32(SystemMemory.IOP_RAM_BASE + tbase + 0x7094);
                byte curStatus = curThread != 0
                    ? sys.Memory.IopRead8(SystemMemory.IOP_RAM_BASE + curThread + 0xc)
                    : (byte)0xFF;
                Console.Error.WriteLine(
                    $"[SCHED-CUR] threadmanBase=0x{tbase:X8} bss0x7090(cur)=0x{curThread:X8} " +
                    $"bss0x7094(next)=0x{nextThread:X8} curStatus=0x{curStatus:X2}");
            }
            if (run.Success && !run.ReturnedToSentinel &&
                sys.IopModules.TryGetIrx(mid, out var irxAfter))
                irxAfter.LastModRes = IopModuleHost.ModuleResidentEnd;
            if (run.Success && run.InstructionsExecuted > 0)
            {
                started++;
                totalInsns += run.InstructionsExecuted;
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                || Environment.GetEnvironmentVariable("DETPS2_TRACE_LITERAL_IRX") == "1")
            {
                int showMod = run.ReturnedToSentinel ? run.ModRes : IopModuleHost.ModuleResidentEnd;
                Console.Error.WriteLine(
                    $"[BIOS] IOPRP StartLoadedModule name={irx.Name} id={mid} " +
                    $"ok={run.Success} insns={run.InstructionsExecuted} modres={showMod} " +
                    $"(v0={run.ModRes} ret={run.ReturnedToSentinel}) finalPc=0x{run.FinalPc:X8} irq={irqDuring} exc={excDuring} msg={run.Message}");
            }
        }

        if ((started > 0 || skippedHle > 0) &&
            (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
             || Environment.GetEnvironmentVariable("DETPS2_TRACE_LITERAL_IRX") == "1"))
        {
            Console.Error.WriteLine(
                $"[BIOS] IOPRP StartLoadedIopRpModules started={started} skipHle={skippedHle}/" +
                $"{_lastIopRpLoadedEntries.Count} r3000insns={totalInsns} src=\"{_lastIopRpSource}\"");
        }

        return started;
    }

    /// <summary>
    /// Ground-truthed 2026-08-03 via byte-for-byte cross-build comparison (Whiplash SLUS_206.84 vs
    /// Blood Omen 2 LOADCORE.IRX): every real IOP module's own <c>_start</c> calls LOADCORE's real
    /// <c>RegisterLibraryEntries</c> as its very first action, passing a pointer to its own embedded
    /// export table (magic 0x41C00000 -- the exact same magic <see cref="IrxLoader.ExportTableMagic"/>
    /// already keys off for cross-module import/export linking). The real function validates that
    /// magic, ORs a "registered" flag bit into the table's own version halfword, and prepends the
    /// table into LOADCORE's real global registry (a singly-linked list rooted at a fixed absolute
    /// global -- the table's own first word doubles as the "next" pointer once the magic check has
    /// already happened, since nothing needs to re-verify it after that point).
    ///
    /// An EARLIER version of this fix hand-synthesized a *different* list's node structs directly in
    /// C# to satisfy one already-traced consumer (a THREADMAN self-check) -- that was diagnosed as
    /// exactly the wrong shape of fix: it fabricated an end state instead of finding why the real
    /// producer never ran. This version does not synthesize anything -- it locates real
    /// RegisterLibraryEntries (via cross-build instruction fingerprint, since LOADCORE.IRX is
    /// relinked per title just like THREADMAN, so its absolute layout is build-specific even though
    /// its logic is not) and EXECUTES it on the real R3000 core, once per already-loaded module's real
    /// export table, via the same ModuleReturnSentinel call convention <see cref="StartLoadedModule"/>
    /// already uses for module _start -- exactly what real hardware's own module-loading sequence
    /// does, using data (export table addresses) DetPS2 already correctly locates via
    /// <see cref="IrxLoader.ScanExports"/>. This does not by itself resolve every real-kernel-code
    /// dependency on LOADCORE bookkeeping (a *separate* module-descriptor list, rooted at a distinct
    /// global 16 bytes away, is populated by LOADCORE's own internal loading loop rather than by any
    /// module's _start, and remains a distinct, not-yet-found gap) -- it closes the specific,
    /// confirmed gap of every module's own first real init call currently doing nothing.
    /// </summary>
    private static void RegisterModulesWithLoadCore(Ps2System sys)
    {
        int lcId = sys.IopModules.SearchModuleByName("LOADCORE");
        if (lcId < 0 || !sys.IopModules.TryGetIrx(lcId, out var lc) || !lc.HasImage || lc.Size == 0)
            return;
        uint lcBase = IopModuleHost.ToIopPhys(lc.LoadBase);
        uint scanEnd = lcBase + lc.Size;

        // RegisterLibraryEntries body, anchored at "lw v1,0(a0)" (function entry is 12 bytes
        // earlier: addiu sp,sp,-24 / beq a0,zero,err / sw ra,16(sp) / <anchor>). Offsets +16
        // (error-path jump target), +32 (list-head lui/addiu immediate) and +56 (icache-flush jal
        // target) are build-specific and intentionally skipped -- every other word confirmed
        // byte-identical across both cross-referenced builds (PC-relative branch encoding makes the
        // whole sequence position-independent).
        var fp = new (int Offset, uint Word)[]
        {
            (0, 0x8C830000), (4, 0x3C0241C0), (8, 0x10620003), (12, 0x00000000),
            (20, 0x2402FF2A), (24, 0x9482000A), (28, 0x3C030001),
            (36, 0x34420001), (40, 0xA482000A), (44, 0x8C620000), (48, 0x00000000),
            (52, 0xAC820000), (60, 0xAC640000), (64, 0x00001021),
        };
        uint? anchor = null;
        for (uint a = lcBase; a + 68 <= scanEnd; a += 4)
        {
            bool ok = true;
            foreach (var (off, word) in fp)
            {
                if (sys.Memory.IopRead32(SystemMemory.IOP_RAM_BASE + a + (uint)off) != word)
                { ok = false; break; }
            }
            if (ok) { anchor = a; break; }
        }
        if (anchor == null)
            return; // fingerprint absent in this build's LOADCORE -- nothing to call, no worse than before this fix
        uint entryPc = anchor.Value - 12;

        var iop = sys.Iop;
        const uint scratchSp = 0x001C0000u; // dedicated scratch stack for these synthetic calls only
        int registered = 0;
        foreach (var m in sys.IopModules.GetModuleTable())
        {
            if (!m.HasImage || m.Size == 0) continue;
            uint modBase = IopModuleHost.ToIopPhys(m.LoadBase);
            uint exportAddr = 0;
            for (uint a = modBase; a + 0x14 <= modBase + m.Size; a += 4)
            {
                if (sys.Memory.IopRead32(SystemMemory.IOP_RAM_BASE + a) == IrxLoader.ExportTableMagic)
                { exportAddr = a; break; }
            }
            if (exportAddr == 0) continue;

            iop.PC = entryPc;
            iop.SetGpr(4, exportAddr); // a0
            iop.SetGpr(29, scratchSp); // sp
            iop.SetGpr(31, IopModuleHost.ModuleReturnSentinel); // ra
            ulong before = iop.InstructionsExecuted;
            const ulong callBudget = 500;
            while (iop.InstructionsExecuted - before < callBudget && iop.Running)
            {
                if (iop.PC == IopModuleHost.ModuleReturnSentinel) break;
                iop.Step(1);
                if (iop.PC == IopModuleHost.ModuleReturnSentinel) break;
            }
            registered++;
        }

        if (registered > 0 && (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            || Environment.GetEnvironmentVariable("DETPS2_TRACE_LITERAL_IRX") == "1"))
            Console.Error.WriteLine(
                $"[BIOS] RegisterLibraryEntries@0x{entryPc:X8} executed for {registered} module(s)");
    }

    private int ApplyIopRpImageCore(IopModuleHost modules, SystemMemory mem, byte[] image,
        string? sourceName, bool updateHost)
    {
        if (!TryParseIopRpContainer(image, out var entries) || entries.Count == 0)
            return 0;

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_ROMDIR") == "1")
            Console.Error.WriteLine("[ROMDIR] entries: " + string.Join(", ", entries.Select(e => e.Name)));

        var btconf = ExtractIopBtConfNamesFromImage(image, entries);
        var loadList = new List<string>();
        if (btconf.Count > 0)
        {
            loadList.AddRange(btconf);
        }
        else
        {
            foreach (var e in entries)
            {
                if (IsRomdirMetaName(e.Name)) continue;
                loadList.Add(e.Name);
            }
        }

        int registered = 0;
        int elfs = 0;
        var names = new List<string>();
        var loaded = new List<IopRpLoadedEntry>();
        // Load policy: always LoadIrx extractable ELFs unless name-only forced or HLE bisect.
        bool loadElfs = !IsIopRpNameOnlyForced()
            && (IsLiteralIrxEnabled() || !_iopRpNameOnlyApply);
        foreach (string modName in loadList)
        {
            if (string.IsNullOrWhiteSpace(modName)) continue;
            string key = modName.Trim();
            // Soft-register every listed name (name probes / LOADFILE search).
            modules.RegisterModule(key, systemResident: true);
            registered++;
            names.Add(key);

            if (!loadElfs) continue;

            // Load ELF into IOP RAM when extractable from the image; record entry for exec.
            byte[]? elf = ExtractEntryElf(image, entries, key);
            if (elf == null || elf.Length < 52) continue;
            if (!LooksLikePlainElf(elf)) continue;
            try
            {
                var lr = modules.LoadIrx(elf, mem, key);
                if (!lr.Success) continue;
                elfs++;
                int mid = modules.SearchModuleByName(lr.ModuleName ?? key);
                loaded.Add(new IopRpLoadedEntry
                {
                    Name = lr.ModuleName ?? key,
                    Entry = lr.Entry,
                    LoadBase = lr.LoadBase,
                    Size = lr.Size,
                    ModuleId = mid,
                });
            }
            catch
            {
                // Keep name registration; corrupt payload is not fatal to handoff.
            }
        }

        // Image tag itself (IOPRP234 / DNAS280) for probes.
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            string tag = BasenameToken(sourceName);
            if (tag.Length > 0)
            {
                modules.RegisterModule(tag, systemResident: true);
                if (!names.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    names.Add(tag);
            }
        }

        if (updateHost)
        {
            _lastIopRpModulesRegistered = registered;
            _lastIopRpElfsLoaded = elfs;
            _lastIopRpSource = sourceName ?? "";
            _lastIopRpModuleNames.Clear();
            _lastIopRpModuleNames.AddRange(names);
            _lastIopRpLoadedEntries.Clear();
            _lastIopRpLoadedEntries.AddRange(loaded);
            _iopRpImagesApplied++;
        }
        else
        {
            _lastIopRpModulesRegistered = registered;
            _lastIopRpElfsLoaded = elfs;
            _lastIopRpLoadedEntries.Clear();
            _lastIopRpLoadedEntries.AddRange(loaded);
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
        {
            Console.Error.WriteLine(
                $"[BIOS] IOPRP apply src=\"{sourceName}\" entries={entries.Count} " +
                $"btconf={btconf.Count} reg={registered} elfs={elfs}" +
                (loadElfs ? (IsLiteralIrxEnabled() ? " (literal)" : "") : " (name-only)"));
            foreach (var le in loaded)
                Console.Error.WriteLine(
                    $"[BIOS] IOPRP elf name={le.Name} id={le.ModuleId} " +
                    $"entry=0x{le.Entry:X8} base=0x{le.LoadBase:X8} size=0x{le.Size:X}");
        }

        return registered;
    }

    /// <summary>
    /// True when <paramref name="image"/> starts with a ROMDIR table (<c>RESET\0</c> entry).
    /// Retail IOPRP*.IMG / DNAS*.IMG and BIOS ROMDIR share this layout.
    /// </summary>
    public static bool TryParseIopRpContainer(byte[] image, out List<RomdirExtractor.RomdirEntry> entries)
    {
        entries = new List<RomdirExtractor.RomdirEntry>();
        if (image == null || image.Length < 32) return false;
        // IOPRP images place RESET at offset 0; BIOS may place ROMDIR deeper — reuse extractor.
        if (image[0] != (byte)'R' || image[1] != (byte)'E' || image[2] != (byte)'S' ||
            image[3] != (byte)'E' || image[4] != (byte)'T' || image[5] != 0)
        {
            // Fallback: full scan via RomdirExtractor (BIOS-style).
            entries = RomdirExtractor.ParseRomdir(image);
            return entries.Count > 0;
        }

        entries = RomdirExtractor.ParseRomdir(image);
        return entries.Count > 0;
    }

    /// <summary>Parse IOPBTCONF text payload from a ROMDIR-in-IMG container.</summary>
    public static List<string> ExtractIopBtConfNamesFromImage(byte[] image,
        List<RomdirExtractor.RomdirEntry>? entries = null)
    {
        var names = new List<string>();
        entries ??= RomdirExtractor.ParseRomdir(image);
        foreach (var e in entries)
        {
            if (!string.Equals(e.Name, "IOPBTCONF", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(e.Name, "IOPBTCON2", StringComparison.OrdinalIgnoreCase))
                continue;
            byte[]? content = ExtractEntryRaw(image, e);
            if (content == null || content.Length == 0) continue;
            string text = Encoding.ASCII.GetString(content);
            foreach (string raw in text.Split(new[] { '\r', '\n', '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '@') continue;
                bool ok = true;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (c < 0x20 || c >= 0x7F) { ok = false; break; }
                }
                if (ok && line.Length <= 16)
                    names.Add(line);
            }
            break;
        }
        return names;
    }

    /// <summary>
    /// SECRMAN SecrDiskBootFile / SecrCardBootFile for unsigned or already-plain IRX:
    /// success without MagicGate. Encrypted / non-ELF payloads return
    /// <see cref="SecrErrCannotDecrypt"/> (no secrets → honest fail).
    /// </summary>
    public int SecrDiskBootFile(byte[]? fileBytes)
    {
        int rc = ClassifySecrBoot(fileBytes);
        if (rc == SecrOk) _secrBootPassthroughs++;
        else if (rc == SecrErrCannotDecrypt) _secrEncryptedRejects++;
        return rc;
    }

    /// <summary>SECRMAN SecrCardBootFile — same plain/encrypted classification as disk.</summary>
    public int SecrCardBootFile(byte[]? fileBytes) => SecrDiskBootFile(fileBytes);

    /// <summary>
    /// Back-compat: plain-boot success counter (no payload). Prefer
    /// <see cref="SecrDiskBootFile"/> when bytes are available.
    /// </summary>
    public int SecrDiskBootFilePassthrough()
    {
        _secrBootPassthroughs++;
        return SecrOk;
    }

    /// <summary>
    /// Static SECRMAN classification for LOADFILE MG_* (no host counters).
    /// Plain ELF → <see cref="SecrOk"/>; missing → <see cref="SecrErrNoFile"/>;
    /// non-ELF (encrypted/unknown) → <see cref="SecrErrCannotDecrypt"/>.
    /// </summary>
    public static int ClassifySecrBoot(byte[]? fileBytes)
    {
        if (fileBytes == null || fileBytes.Length == 0)
            return SecrErrNoFile;
        if (LooksLikePlainElf(fileBytes))
            return SecrOk;
        // Non-ELF payload on MG path: treat as encrypted / undecryptable without secrets.
        return SecrErrCannotDecrypt;
    }

    /// <summary>True when bytes start with ELF magic (plain IRX/ELF).</summary>
    public static bool LooksLikePlainElf(byte[] data) =>
        data != null && data.Length >= 4 &&
        data[0] == 0x7F && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F';

    /// <summary>
    /// True when payload is present but not plain ELF — MG-encrypted IRX class without secrets.
    /// </summary>
    public static bool LooksLikeEncryptedMg(byte[]? data) =>
        data != null && data.Length >= 16 && !LooksLikePlainElf(data);

    /// <summary>
    /// Build a minimal synthetic IOPRP-like ROMDIR container for smokes:
    /// RESET + ROMDIR + EXTINFO + IOPBTCONF + optional IRX ELF modules.
    /// </summary>
    public static byte[] BuildSyntheticIopRpImage(
        IReadOnlyList<string>? btconfModules = null,
        IReadOnlyDictionary<string, byte[]>? elfModules = null)
    {
        // Entry list: meta + IOPBTCONF + each ELF module name.
        var entryNames = new List<string> { "RESET", "ROMDIR", "EXTINFO", "IOPBTCONF" };
        var elfOrder = new List<string>();
        if (elfModules != null)
        {
            foreach (var kv in elfModules)
            {
                entryNames.Add(kv.Key);
                elfOrder.Add(kv.Key);
            }
        }

        // IOPBTCONF text body.
        var confLines = new List<string> { "@800" };
        if (btconfModules != null)
        {
            foreach (string m in btconfModules)
                if (!string.IsNullOrWhiteSpace(m))
                    confLines.Add(m.Trim());
        }
        else
        {
            foreach (string m in elfOrder)
                confLines.Add(m);
        }
        // Ensure listed modules that lack ELF still appear as names-only entries? Only in conf.
        byte[] confBytes = Encoding.ASCII.GetBytes(string.Join("\n", confLines) + "\n");

        // Payloads after the ROMDIR table: EXTINFO, IOPBTCONF, ELFs (RESET size=0).
        byte[] extInfo = new byte[16]; // dummy
        var payloads = new List<(string Name, byte[] Data)>
        {
            ("EXTINFO", extInfo),
            ("IOPBTCONF", confBytes),
        };
        if (elfModules != null)
        {
            foreach (string name in elfOrder)
                payloads.Add((name, elfModules[name]));
        }

        int nEntries = entryNames.Count;
        int romdirSize = nEntries * 16;
        // Cumulative sizes: RESET=0, ROMDIR=romdirSize, then each payload.
        var sizes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["RESET"] = 0,
            ["ROMDIR"] = (uint)romdirSize,
        };
        foreach (var (name, data) in payloads)
            sizes[name] = (uint)data.Length;

        // Total image = ROMDIR table + sum of non-ROMDIR/RESET payloads.
        // Naive offsets: RESET=0, ROMDIR=0, EXTINFO=romdirSize, ...
        long total = romdirSize;
        foreach (var (_, data) in payloads)
            total += data.Length;

        byte[] image = new byte[total];
        // Write ROMDIR table at 0.
        int off = 0;
        long naive = 0;
        foreach (string name in entryNames)
        {
            uint size = sizes[name];
            WriteRomdirEntry(image, off, name, extInfoSize: 0, size);
            off += 16;
            // Advance naive for next entry's NaiveOffset bookkeeping.
            // (Payload write uses running naive from sizes in order.)
            naive += size;
        }

        // Write payloads at cumulative naive offsets (RESET+ROMDIR contribute first).
        long cursor = sizes["RESET"] + sizes["ROMDIR"];
        foreach (var (name, data) in payloads)
        {
            if (cursor + data.Length > image.Length)
                break;
            Array.Copy(data, 0, image, (int)cursor, data.Length);
            cursor += data.Length;
        }

        return image;
    }

    // --- tables ----------------------------------------------------------------

    /// <summary>ROMDIR service modules beyond IOPBTCONF @800 required set.</summary>
    public static readonly string[] ExtendedRomdirServiceNames =
    {
        "ADDDRV", "SECRMAN", "RMRESET", "CLEARSPU", "UDNL",
        "TSIO2MAN", "TPADMAN", "XDEV9SERV", "XDEV9",
        "XSIFCMD", "XCDVDMAN", "XCDVDFSV", "XFILEIO", "XSIO2MAN",
        "XMTAPMAN", "XMCMAN", "XMCSERV", "XPADMAN", "XRMMAN2",
        "XLOADFILE", "NCDVDMAN", "EECONF", "LIBSD",
    };

    public static readonly (string Alias, string Primary)[] XModuleAliases =
    {
        ("XLOADFILE", "LOADFILE"),
        ("XFILEIO", "FILEIO"),
        ("XCDVDMAN", "CDVDMAN"),
        ("XCDVDFSV", "CDVDFSV"),
        ("XSIO2MAN", "SIO2MAN"),
        ("XPADMAN", "PADMAN"),
        ("XMCMAN", "MCMAN"),
        ("XMCSERV", "MCSERV"),
        ("XSIFCMD", "SIFCMD"),
        ("TSIO2MAN", "SIO2MAN"),
        ("TPADMAN", "PADMAN"),
        ("NCDVDMAN", "CDVDMAN"),
    };

    /// <summary>Modules typically present after a retail IOPRP/DNAS image apply.</summary>
    public static readonly string[] UdnlImageModuleNames =
    {
        "SIO2MAN", "PADMAN", "MCMAN", "MCSERV", "LIBSD", "SDRDRV",
        "CDVDMAN", "CDVDFSV", "FILEIO", "LOADFILE", "MODLOAD",
        "SECRMAN", "CLEARSPU", "IOPFILE", "IOPMEM", "IOPSND",
    };

    private static readonly HashSet<string> RomdirMetaNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "RESET", "ROMDIR", "EXTINFO", "IOPBTCONF", "IOPBTCON2", "ROMVER", "VERSTR",
    };

    private static bool IsRomdirMetaName(string name) =>
        RomdirMetaNames.Contains(name);

    /// <summary>
    /// From <c>rom0:UDNL cdrom0:\PATH\IOPRP234.IMG;1</c> extract the image path token.
    /// </summary>
    public static string? ExtractUdnlImagePath(string? rebootArg)
    {
        if (string.IsNullOrWhiteSpace(rebootArg)) return null;
        // Split on whitespace; find token after UDNL or any path containing IOPRP/DNAS/.IMG
        string[] parts = rebootArg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p.Contains("IOPRP", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("DNAS", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".IMG", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".IMG;1", StringComparison.OrdinalIgnoreCase))
                return p;
            if (p.Contains("UDNL", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
            {
                string next = parts[i + 1];
                if (next.Contains(':') || next.Contains('.') || next.Contains('/') || next.Contains('\\'))
                    return next;
            }
        }
        return null;
    }

    private static byte[]? TryResolveUdnlImageBytes(Ps2System sys, string rebootArg)
    {
        string? path = ExtractUdnlImagePath(rebootArg);
        if (path == null) return null;

        // Disc / ISO via IopModuleHost FILEIO path resolver.
        byte[]? bytes = sys.IopModules.ReadDiscFileBytes(path);
        if (bytes != null) return bytes;

        // Basename fallback (cdrom0:\FOO\IOPRP234.IMG;1 → IOPRP234.IMG).
        string baseName = path.Replace('\\', '/');
        int slash = baseName.LastIndexOf('/');
        if (slash >= 0) baseName = baseName[(slash + 1)..];
        int semi = baseName.IndexOf(';');
        if (semi >= 0) baseName = baseName[..semi];
        if (baseName.Length > 0)
        {
            bytes = sys.IopModules.ReadDiscFileBytes(baseName)
                    ?? sys.IopModules.ReadDiscFileBytes("cdrom0:" + baseName)
                    ?? sys.IopModules.ReadDiscFileBytes("cdrom0:\\" + baseName);
            if (bytes != null) return bytes;
        }
        return null;
    }

    private static byte[]? ExtractEntryElf(byte[] image, List<RomdirExtractor.RomdirEntry> entries, string name)
    {
        foreach (var e in entries)
        {
            if (!string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            long realOff = RomdirExtractor.FindRealOffset(image, e);
            if (realOff < 0) return null;
            if (!LooksLikePlainElfAt(image, realOff)) return null;

            // Prefer size from ELF start so pre-ELF padding is not counted twice.
            long pad = realOff - e.NaiveOffset;
            long avail = e.Size;
            if (pad > 0 && pad < avail)
                avail -= pad;
            if (realOff + avail > image.Length)
                avail = image.Length - realOff;
            if (avail < 52) return null;

            var buf = new byte[avail];
            Array.Copy(image, realOff, buf, 0, (int)avail);
            return buf;
        }
        return null;
    }

    private static byte[]? ExtractEntryRaw(byte[] image, RomdirExtractor.RomdirEntry e)
    {
        if (e.Size == 0) return Array.Empty<byte>();
        long off = RomdirExtractor.ResolveContentOffset(image, e);
        if (off < 0) off = e.NaiveOffset;
        if (off < 0 || off + e.Size > image.Length) return null;
        var buf = new byte[e.Size];
        Array.Copy(image, off, buf, 0, (int)e.Size);
        return buf;
    }

    private static bool LooksLikePlainElfAt(byte[] data, long off) =>
        off >= 0 && off + 4 <= data.Length &&
        data[off] == 0x7F && data[off + 1] == (byte)'E' &&
        data[off + 2] == (byte)'L' && data[off + 3] == (byte)'F';

    private static string BasenameToken(string path)
    {
        string s = path.Replace('\\', '/');
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        int semi = s.IndexOf(';');
        if (semi >= 0) s = s[..semi];
        int dot = s.LastIndexOf('.');
        if (dot > 0) s = s[..dot];
        return s.Trim();
    }

    private static void WriteRomdirEntry(byte[] dest, int off, string name, ushort extInfoSize, uint size)
    {
        for (int i = 0; i < 10; i++)
            dest[off + i] = i < name.Length ? (byte)name[i] : (byte)0;
        dest[off + 10] = (byte)(extInfoSize & 0xFF);
        dest[off + 11] = (byte)((extInfoSize >> 8) & 0xFF);
        dest[off + 12] = (byte)(size & 0xFF);
        dest[off + 13] = (byte)((size >> 8) & 0xFF);
        dest[off + 14] = (byte)((size >> 16) & 0xFF);
        dest[off + 15] = (byte)((size >> 24) & 0xFF);
    }

    private static void PlantThreadmanExtraLibs(SystemMemory mem, IopModuleHost modules, ref uint cursor, uint stubJrRa)
    {
        foreach (var (name, count) in new[]
                 {
                     (ThmsgbxLibName, 16),
                     (ThvpoolLibName, 16),
                     (ThfpoolLibName, 16),
                 })
        {
            var exports = MakeStubExports(mem, ref cursor, stubJrRa, count);
            cursor = PlantExportTable(mem, cursor, name, 1, 1, exports);
            modules.RegisterExportLibrary(new IrxLoader.ExportTable
            {
                Name = name,
                VersionMajor = 1,
                VersionMinor = 1,
                Exports = exports
            });
        }
    }

    private static uint[] MakeStubExports(SystemMemory mem, ref uint cursor, uint stubJrRa, int count)
    {
        _ = mem;
        _ = cursor;
        var exports = new uint[count];
        for (int i = 0; i < count; i++)
            exports[i] = stubJrRa; // share single jr ra body
        return exports;
    }

    /// <summary>Match <see cref="IopSysclibHeaplibHost"/> layout (magic + ver + 8-byte name + ptrs + 0).</summary>
    private static uint PlantExportTable(SystemMemory mem, uint at, string name,
        byte verMaj, byte verMin, uint[] exports)
    {
        mem.Write32(at + 0x00, IrxLoader.ExportTableMagic);
        mem.Write32(at + 0x04, 0);
        mem.Write8(at + 0x08, verMin);
        mem.Write8(at + 0x09, verMaj);
        mem.Write8(at + 0x0A, 0);
        mem.Write8(at + 0x0B, 0);
        for (int i = 0; i < 8; i++)
            mem.Write8(at + 0x0C + (uint)i, i < name.Length ? (byte)name[i] : (byte)0);
        uint p = at + 0x14;
        for (int i = 0; i < exports.Length; i++, p += 4)
            mem.Write32(p, exports[i]);
        mem.Write32(p, 0); // terminator
        return p + 4;
    }
}
