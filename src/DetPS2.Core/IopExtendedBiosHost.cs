using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// C# HLE for SCPH70008 ROMDIR modules that are outside the original IOPBTCONF @800
/// commercial-fast-path set but still ship in the real BIOS and are loaded via
/// <c>rom0:</c> / UDNL / LOADFILE during retail bring-up.
///
/// <para><b>Authority:</b> live ROMDIR parse of SCPH70008 (101 entries), Ghidra 12.1.2
/// headless of extracted IRX (<c>SECRMAN</c>, <c>UDNL</c>, <c>CLEARSPU</c>, <c>LIBSD</c>,
/// <c>ADDDRV</c>, <c>XMTAPMAN</c>), and <c>docs/BIOS_DISSECTION.md</c>.</para>
///
/// <para><b>What this is:</b> functional service contracts so name probes, export linking,
/// CLEARSPU soft-reset, UDNL image handoff registration, and SECRMAN/LIBSD load succeed
/// without requiring title-local plants. <b>Not</b> MagicGate crypto, full mechacon auth,
/// or cycle-accurate R3000 IRX execution.</para>
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

    private bool _installed;
    private ulong _clearSpuRuns;
    private ulong _udnlApplies;
    private ulong _secrBootPassthroughs;
    private string _lastUdnlArg = "";
    private string _lastUdnlVersion = "";

    public bool Installed => _installed;
    public ulong ClearSpuRuns => _clearSpuRuns;
    public ulong UdnlApplies => _udnlApplies;
    public ulong SecrBootPassthroughs => _secrBootPassthroughs;
    public string LastUdnlArg => _lastUdnlArg;
    public string LastUdnlVersion => _lastUdnlVersion;

    public void Reset()
    {
        _installed = false;
        _clearSpuRuns = 0;
        _udnlApplies = 0;
        _secrBootPassthroughs = 0;
        _lastUdnlArg = "";
        _lastUdnlVersion = "";
    }

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

        // LIBSD — Sound Device Library (Ghidra module string). Export stubs for link-imports.
        var libsdExports = MakeStubExports(mem, ref cursor, stubJrRa, 48);
        cursor = PlantExportTable(mem, cursor, LibsdLibName, 1, 4, libsdExports);
        modules.RegisterExportLibrary(new IrxLoader.ExportTable
        {
            Name = LibsdLibName,
            VersionMajor = 1,
            VersionMinor = 4,
            Exports = libsdExports
        });

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
    /// Real UDNL opens the image, walks IOPBTCONF inside it, and loads listed IRX.
    /// HLE: parse version tag, re-register common IOPRP module names, re-seed IOMAN/SIF,
    /// and mark LOADFILE GetVersion ASCII (via <see cref="RealSifRpc.OnIopReboot"/>).
    /// </summary>
    public void ApplyUdnlHandoff(Ps2System sys, string? rebootArg)
    {
        if (sys == null) return;
        _lastUdnlArg = rebootArg ?? "";
        string ver = RealSifRpc.ExtractIopRpVersionAscii(_lastUdnlArg);
        if (!string.IsNullOrEmpty(ver))
            _lastUdnlVersion = ver;

        // Always present UDNL itself + common post-image modules.
        sys.IopModules.RegisterModule("UDNL", systemResident: true);
        foreach (string n in UdnlImageModuleNames)
            sys.IopModules.RegisterModule(n, systemResident: true);

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

        // CLEARSPU is commonly re-run after UDNL image apply.
        ApplyClearSpu(sys);

        // SECRMAN present for encrypted module path probes (LOADFILE MG_* already path-loads).
        sys.IopModules.RegisterModule("SECRMAN", systemResident: true);
        _secrBootPassthroughs++; // MgModLoad uses plain path load — counted as passthrough contract

        _udnlApplies++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" ||
            Environment.GetEnvironmentVariable("DETPS2_TRACE_REBOOT") == "1")
            Console.Error.WriteLine(
                $"[BIOS] UDNL handoff applies={_udnlApplies} ver=\"{_lastUdnlVersion}\" " +
                $"arg=\"{_lastUdnlArg}\"");
    }

    /// <summary>
    /// SECRMAN SecrDiskBootFile / SecrCardBootFile contract for unsigned or already-plain
    /// IRX: return success without MagicGate. Encrypted MG payloads still fail open if the
    /// path is missing; LOADFILE MG_* uses the same path loader as plain MOD_LOAD.
    /// </summary>
    public int SecrDiskBootFilePassthrough()
    {
        _secrBootPassthroughs++;
        return 0; // success
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
