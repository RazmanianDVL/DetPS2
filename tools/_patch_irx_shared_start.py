#!/usr/bin/env python3
"""Apply shared WP-25 LOADFILE/IOPRP StartLoadedModule patches (idempotent)."""
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[1]


def must_replace(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        if new[:80] in text or (label in text and "already" not in label):
            # Allow idempotent re-run when marker present
            return text
        raise SystemExit(f"anchor missing: {label}")
    return text.replace(old, new, 1)


def patch_real_sif_rpc() -> None:
    p = root / "src/DetPS2.Core/RealSifRpc.cs"
    t = p.read_text(encoding="utf-8")
    if "void BindHost" not in t:
        t = must_replace(
            t,
            """    public const uint CidSifInit = 0x80000000;
    public const uint CidSifSetSreg = 0x80000001;

    // Known real service ids""",
            """    public const uint CidSifInit = 0x80000000;
    public const uint CidSifSetSreg = 0x80000001;

    /// <summary>Host for LOADFILE MOD_LOAD StartLoadedModule (WP-25/31). Bound from SonyKernelHle.</summary>
    private Ps2System? _host;
    /// <summary>Wire host so disc MOD_LOAD can run real IRX _start after LoadIrx.</summary>
    public void BindHost(Ps2System system) => _host = system;

    // Known real service ids""",
            "BindHost",
        )

    t = must_replace(
        t,
        """        if (iopModules.TryGetModule(name, out int existingId) ||
            iopModules.TryGetModule(baseName, out existingId) ||
            iopModules.TryGetModule(modKey, out existingId))
        {
            modres = 0;
            return existingId;
        }""",
        """        if (iopModules.TryGetModule(name, out int existingId) ||
            iopModules.TryGetModule(baseName, out existingId) ||
            iopModules.TryGetModule(modKey, out existingId))
        {
            // Proprietary disc IRX: try _start if image present (HLE-owned skipped in helper).
            modres = TryStartLoadedModule(iopModules, existingId);
            return existingId;
        }""",
        "existing-module",
    )

    t = must_replace(
        t,
        """                if (lr.Success && iopModules.TryGetModule(lr.ModuleName, out int mid))
                {
                    // HLE does not run module _start; real modres would be start()'s return.
                    modres = 0;
                    return mid;
                }
                // Also try by requested key (LoadIrx nameOverride may uppercase).
                if (lr.Success && iopModules.TryGetModule(modKey, out mid))
                {
                    modres = 0;
                    return mid;
                }""",
        """                if (lr.Success && iopModules.TryGetModule(lr.ModuleName, out int mid))
                {
                    // WP-25/31: real R3000 _start for proprietary disc IRX (shared).
                    modres = TryStartLoadedModule(iopModules, mid);
                    return mid;
                }
                // Also try by requested key (LoadIrx nameOverride may uppercase).
                if (lr.Success && iopModules.TryGetModule(modKey, out mid))
                {
                    modres = TryStartLoadedModule(iopModules, mid);
                    return mid;
                }""",
        "load-irx-start",
    )

    if "LoadFileHleOwnedSkipStart" not in t:
        helper = r'''
    /// <summary>
    /// Stack/SCE modules still answered by C# HLE — incomplete R3000 _start clobbers coexistence.
    /// Disc proprietary IRX (GTFSCDVD, LGDEVW, PL2303, 989nomid, B3ROUTE, …) still run.
    /// Force all: DETPS2_LOADFILE_START_ALL=1. Disable all: DETPS2_LOADFILE_START_IRX=0.
    /// </summary>
    private static readonly HashSet<string> LoadFileHleOwnedSkipStart = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYSMEM", "LOADCORE", "HEAPLIB", "EXCEPMAN", "INTRMAN", "INTRMANP", "INTRMANS",
        "TIMEMAN", "TIMEMANI", "TIMEMANS", "SSBUSC", "EECONF",
        "THREADMAN", "VBLANK", "VBLANK_A", "VBLANK_B",
        "IOMAN", "MODLOAD", "ROMDRV", "STDIO", "SYSCLIB", "IGREETING",
        "SIFMAN", "SIFCMD", "SIFINIT", "EESYNC", "REBOOT",
        "FILEIO", "LOADFILE", "CDVDMAN", "CDVDFSV",
        "MCMAN", "MCSERV", "PADMAN", "SIO2MAN", "LIBSD",
    };

    /// <summary>Run R3000 _start for proprietary disc IRX; return LOADFILE modres (WP-25/31).</summary>
    private int TryStartLoadedModule(IopModuleHost iopModules, int mid)
    {
        if (mid < 0) return 0;
        if (!iopModules.TryGetIrx(mid, out var irx) || !irx.HasImage || irx.Entry == 0)
            return irx?.LastModRes ?? 0;
        if (irx.EntryExecuted && irx.LastEntryInstructions > 0)
            return irx.LastModRes;
        if (_host == null || !IopModuleHost.IsLiteralIrxEnabled)
            return irx.LastModRes;
        if (string.Equals(Environment.GetEnvironmentVariable("DETPS2_LOADFILE_START_IRX"), "0", StringComparison.Ordinal))
            return irx.LastModRes;
        bool startAll = string.Equals(Environment.GetEnvironmentVariable("DETPS2_LOADFILE_START_ALL"), "1", StringComparison.Ordinal);
        if (!startAll && LoadFileHleOwnedSkipStart.Contains(irx.Name))
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1"
                || Environment.GetEnvironmentVariable("DETPS2_TRACE_LITERAL_IRX") == "1")
                Console.Error.WriteLine($"[LOADFILE] StartLoadedModule SKIP hle-owned name={irx.Name} id={mid}");
            return irx.LastModRes;
        }
        const ulong maxInsn = 50_000;
        if (string.Equals(irx.Name, "INTRMANP", StringComparison.OrdinalIgnoreCase))
            _host.Memory.IopWrite32(0xBF801450, SystemMemory.IopIoIntrmanConfigDefault);
        else if (string.Equals(irx.Name, "SIFMAN", StringComparison.OrdinalIgnoreCase))
            _host.Memory.IopWrite32(0xBF801450, 0);
        var run = iopModules.StartLoadedModule(_host, mid, maxInsn);
        int replyModres;
        if (run.ReturnedToSentinel)
            replyModres = run.ModRes;
        else if (run.Success)
        {
            replyModres = IopModuleHost.ModuleResidentEnd;
            irx.LastModRes = replyModres;
        }
        else
            replyModres = irx.LastModRes;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1"
            || Environment.GetEnvironmentVariable("DETPS2_TRACE_LITERAL_IRX") == "1")
            Console.Error.WriteLine(
                $"[LOADFILE] StartLoadedModule name={irx.Name} id={mid} ok={run.Success} " +
                $"insns={run.InstructionsExecuted} modres={replyModres} (v0={run.ModRes} ret={run.ReturnedToSentinel}) msg={run.Message}");
        return replyModres;
    }

'''
        anchor = "        return iopModules.RegisterModule(modKey.Length > 0 ? modKey : name);\n    }\n\n    /// <summary>LF_F_ELF_LOAD"
        if anchor not in t:
            raise SystemExit("RealSifRpc helper anchor missing")
        t = t.replace(
            anchor,
            "        return iopModules.RegisterModule(modKey.Length > 0 ? modKey : name);\n    }\n"
            + helper
            + "\n    /// <summary>LF_F_ELF_LOAD",
            1,
        )

    p.write_text(t, encoding="utf-8")
    print("RealSifRpc OK")


def patch_sony_kernel() -> None:
    p = root / "src/DetPS2.Core/SonyKernelHle.cs"
    t = p.read_text(encoding="utf-8")
    if "BindHost(system)" in t:
        print("SonyKernelHle already patched")
        return
    t = must_replace(
        t,
        """    public SonyKernelHle(Ps2System system, KernelState kernel)
    {
        _system = system;
        _kernel = kernel;
    }""",
        """    public SonyKernelHle(Ps2System system, KernelState kernel)
    {
        _system = system;
        _kernel = kernel;
        // WP-25: LOADFILE MOD_LOAD needs host for StartLoadedModule after disc LoadIrx.
        _realRpc.BindHost(system);
    }""",
        "SonyKernelHle ctor",
    )
    p.write_text(t, encoding="utf-8")
    print("SonyKernelHle OK")


def patch_iop_extended() -> None:
    p = root / "src/DetPS2.Core/IopExtendedBiosHost.cs"
    t = p.read_text(encoding="utf-8")
    if "StartLoadedIopRpModules" in t:
        print("IopExtendedBiosHost already patched")
        return
    old = """    public int ApplyIopRpImage(Ps2System sys, byte[] image, string? sourceName = null)
    {
        if (sys == null || image == null || image.Length < 32)
            return 0;
        return ApplyIopRpImageCore(sys.IopModules, sys.Memory, image, sourceName, updateHost: true);
    }

    /// <summary>
    /// Static entry for LOADFILE / callers without <see cref="Ps2System"/>: parse image and
    /// register modules (LoadIrx when ELF extractable and not name-only). Does not update
    /// host counters on a live instance.
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

    private int ApplyIopRpImageCore"""
    new = r'''    public int ApplyIopRpImage(Ps2System sys, byte[] image, string? sourceName = null)
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
                sys.Iop.InstallMinimalExceptionStub();
                stubReady = true;
            }

            if (string.Equals(irx.Name, "INTRMANP", StringComparison.OrdinalIgnoreCase))
                sys.Memory.IopWrite32(0xBF801450, SystemMemory.IopIoIntrmanConfigDefault);
            else if (string.Equals(irx.Name, "SIFMAN", StringComparison.OrdinalIgnoreCase))
                sys.Memory.IopWrite32(0xBF801450, 0);

            var run = sys.IopModules.StartLoadedModule(sys, mid, maxInsnPerModule);
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
                    $"(v0={run.ModRes} ret={run.ReturnedToSentinel}) msg={run.Message}");
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

    private int ApplyIopRpImageCore'''
    if old not in t:
        raise SystemExit("IopExtendedBiosHost ApplyIopRpImage anchor missing")
    t = t.replace(old, new, 1)
    p.write_text(t, encoding="utf-8")
    print("IopExtendedBiosHost OK")


def patch_tools() -> None:
    p = root / "tools/scoreboard-fleet.json"
    t = p.read_text(encoding="utf-8")
    t2 = t.replace(
        '''      "id": "haven",
      "name": "Haven: Call of the King",
      "serial": "",''',
        '''      "id": "haven",
      "name": "Haven: Call of the King",
      "serial": "SLUS_205.17",''',
        1,
    )
    if t2 == t and "SLUS_205.17" not in t:
        raise SystemExit("fleet haven serial anchor missing")
    p.write_text(t2, encoding="utf-8")
    print("fleet OK")

    p = root / "tools/scoreboard.ps1"
    t = p.read_text(encoding="utf-8")
    if "elseif ($m.serial)" in t:
        print("scoreboard.ps1 already patched")
        return
    old = """            $results += [pscustomobject]@{
                id = $t.id; name = $t.name; serial = $t.serial; menuKind = $t.menuKind
                status = "RAN"; menuHeuristic = (Get-MenuHeuristic $pxN $gifN)"""
    new = """            # Prefer live metrics serial when fleet entry is empty (Haven historically blank).
            $serial = if ($t.serial) { $t.serial } elseif ($m.serial) { $m.serial } else { "" }
            $results += [pscustomobject]@{
                id = $t.id; name = $t.name; serial = $serial; menuKind = $t.menuKind
                status = "RAN"; menuHeuristic = (Get-MenuHeuristic $pxN $gifN)"""
    if old not in t:
        raise SystemExit("scoreboard.ps1 serial anchor missing")
    p.write_text(t.replace(old, new, 1), encoding="utf-8")
    print("scoreboard.ps1 OK")


def main() -> int:
    patch_real_sif_rpc()
    patch_sony_kernel()
    patch_iop_extended()
    patch_tools()
    print("ALL PATCHES APPLIED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
