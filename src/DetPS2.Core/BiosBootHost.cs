using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Structural C# reimplementation of the PS2 BIOS/IOP <b>service surface</b> that commercial
/// titles assume exists before <c>main()</c> runs.
///
/// <para><b>Why this exists (root cause, not another thread poke):</b>
/// DetPS2 loads the BIOS ROM image as data and jumps into the game ELF. It does <b>not</b>
/// execute RESET → IOPBOOT → THREADMAN/SIF*/LOADFILE/CDVD → EELOAD. Threads then
/// <c>WaitSema</c> / bind RPC / open CRI devices for destinations that only those BIOS modules
/// create. Patching one waiter PC after another cannot scale — every game uses the same BIOS.
/// This host re-creates the <b>contracts</b> of those modules so destinations exist for all titles.</para>
///
/// <para><b>Source of truth:</b> ROMDIR table inside the real SCPH BIOS (see
/// <see cref="RomdirExtractor"/>). Module names and boot order match the ROM, not one game's
/// binary. RPC SIDs match ps2sdk / retail modules already wired in <see cref="RealSifRpc"/>.</para>
///
/// <para><b>What this is not:</b> cycle-accurate R3000 execution of every IRX. It is intentional
/// HLE of the same service map the BIOS ships — the 20-year-old ABI, not per-title PCs.</para>
/// </summary>
public sealed class BiosBootHost
{
    /// <summary>ROMDIR module → role in commercial boot (documentation + registration).</summary>
    public readonly struct ModuleContract
    {
        public string RomdirName { get; init; }
        public string Role { get; init; }
        /// <summary>Known SIF RPC service id if this module registers one (0 = none / EE-only).</summary>
        public uint RpcSid { get; init; }
        public bool RequiredForCommercialFastPath { get; init; }
    }

    /// <summary>
    /// Boot-critical contracts derived from SCPH70008 <b>ROMDIR + IOPBTCONF</b> and Ghidra
    /// decompilation of the real IRX blobs (see docs/BIOS_DISSECTION.md).
    /// Order follows IOPBTCONF load sequence, not arbitrary listing order.
    /// </summary>
    public static readonly ModuleContract[] BootCriticalContracts =
    {
        // --- IOPBTCONF @800 sequence (verbatim from BIOS) ---
        new() { RomdirName = "SYSMEM", Role = "IOP heap; EE RPC sid=0x80000003 (Ghidra LOADFILE/SIFCMD deps)", RpcSid = RealSifRpc.SidSysmem, RequiredForCommercialFastPath = true },
        new() { RomdirName = "LOADCORE", Role = "IRX load core", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "EXCEPMAN", Role = "IOP exception manager", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "INTRMANP", Role = "IOP interrupt manager (primary)", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "INTRMANI", Role = "IOP interrupt manager (secondary)", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "SSBUSC", Role = "SSBUS controller", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "DMACMAN", Role = "IOP DMAC manager (dmacman exports; IopDmacManHost HLE)", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "TIMEMANP", Role = "IOP timer manager", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "TIMEMANI", Role = "IOP timer manager (i)", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "SYSCLIB", Role = "IOP C library", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "HEAPLIB", Role = "IOP heap helpers", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "EECONF", Role = "EE config (IOPBTCONF path)", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "THREADMAN", Role = "WaitSema/SignalSema/threads (Ghidra: count vs waiter queue)", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "VBLANK", Role = "IOP Vblank_service: Register/dispatch callback lists (not EE INTC)", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "IOMAN", Role = "device manager", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "MODLOAD", Role = "module loader", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "ROMDRV", Role = "rom0: driver", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "STDIO", Role = "stdio printf/puts → tty log sink (non-fatal)", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "SIFMAN", Role = "SIF DMA transport", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "IGREETING", Role = "IOP greeting init stub (early boot banner)", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "SIFCMD", Role = "SIFCMD/RPC: BIND/CALL/RDATA; replies via CID 0x80000008 RPC_END (Ghidra)", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "REBOOT", Role = "IOP reboot helper (RESET_CMD arg + IOPBTCONF re-handoff)", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "LOADFILE", Role = "sid=0x80000006 LoadModuleByEE (Ghidra registers RPC at init)", RpcSid = RealSifRpc.SidLoadFile, RequiredForCommercialFastPath = true },
        new() { RomdirName = "CDVDMAN", Role = "CDVD manager (mechacon imports for CDVDFSV)", RpcSid = RealSifRpc.SidCdBase, RequiredForCommercialFastPath = true },
        new() { RomdirName = "CDVDFSV", Role = "CDVD RPC: 0x592 init / 0x593 SCMD / 0x595 NCMD / 0x597 SearchFile / 0x59a+0x59c DiskReady", RpcSid = RealSifRpc.SidCdScmd, RequiredForCommercialFastPath = true },
        new() { RomdirName = "SIFINIT", Role = "SIF init (idempotent SIFMAN bring-up; 'Skip SIF init' if already up)", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "FILEIO", Role = "IOP file I/O RPC sid=0x80000001 (sceOpen family)", RpcSid = RealSifRpc.SidFileIo, RequiredForCommercialFastPath = true },
        // --- Extended / optional ROMDIR siblings ---
        // EESYNC is not always listed in IOPBTCONF @800 text but is a ROMDIR sibling; SyncEE posts BOOTEND.
        new() { RomdirName = "EESYNC", Role = "EE/IOP sync — export SyncEE posts SIF_STAT_BOOTEND (0x40000)", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "XLOADFILE", Role = "extended LOADFILE", RpcSid = RealSifRpc.SidLoadFile, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XFILEIO", Role = "extended FILEIO", RpcSid = RealSifRpc.SidFileIo, RequiredForCommercialFastPath = false },
        new() { RomdirName = "NCDVDMAN", Role = "newer CDVDMAN NCMD sid=0x80000595", RpcSid = RealSifRpc.SidCdNcmd, RequiredForCommercialFastPath = false },
        new() { RomdirName = "PADMAN", Role = "pad sid=0x8000010f (rom0 OLD) / 0x80000100 (NEW)", RpcSid = RealSifRpc.SidPadOld1, RequiredForCommercialFastPath = true },
        new() { RomdirName = "SIO2MAN", Role = "SIO2 bus transfer/ctrl (no EE RPC; PADMAN/MCSERV import)", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "MCMAN", Role = "memory card", RpcSid = RealSifRpc.SidMcServ, RequiredForCommercialFastPath = false },
        new() { RomdirName = "MCSERV", Role = "MC service", RpcSid = RealSifRpc.SidMcServ, RequiredForCommercialFastPath = false },
        new() { RomdirName = "LIBSD", Role = "sound driver lib (IopExtendedBiosHost export stubs)", RpcSid = 0, RequiredForCommercialFastPath = false },
        // --- Extended ROMDIR services (SCPH70008 full table; HLE via IopExtendedBiosHost) ---
        new() { RomdirName = "ADDDRV", Role = "rom0: add driver helper", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "SECRMAN", Role = "MagicGate SECRMAN — Secr*BootFile passthrough (no MG crypto)", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "CLEARSPU", Role = "SPU2 soft clear on cold/post-UDNL (IopExtendedBiosHost)", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "UDNL", Role = "IOPRP/DNAS image handoff after SifIopReset (version + module register)", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "RMRESET", Role = "remote/reset helper", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XMTAPMAN", Role = "multitap manager (X path)", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XPADMAN", Role = "PADMAN X alias", RpcSid = RealSifRpc.SidPadOld1, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XSIO2MAN", Role = "SIO2MAN X alias", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XMCMAN", Role = "MCMAN X alias", RpcSid = RealSifRpc.SidMcServ, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XMCSERV", Role = "MCSERV X alias", RpcSid = RealSifRpc.SidMcServ, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XSIFCMD", Role = "SIFCMD X alias", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XCDVDMAN", Role = "CDVDMAN X alias", RpcSid = RealSifRpc.SidCdBase, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XCDVDFSV", Role = "CDVDFSV X alias", RpcSid = RealSifRpc.SidCdScmd, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XDEV9", Role = "DEV9 / HDD path (optional)", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XDEV9SERV", Role = "DEV9 service (optional)", RpcSid = 0, RequiredForCommercialFastPath = false },
    };

    /// <summary>
    /// SIFCMD command IDs from real BIOS SIFCMD.IRX init (Ghidra FUN_000006c0).
    /// IOP handlers for BIND/CALL complete by sending <see cref="SifCmdRpcEnd"/> to the EE.
    /// </summary>
    public const uint SifCmdRpcEnd = RealSifRpc.CidRpcEnd;   // 0x80000008
    public const uint SifCmdRpcBind = RealSifRpc.CidRpcBind; // 0x80000009
    public const uint SifCmdRpcCall = RealSifRpc.CidRpcCall; // 0x8000000A
    public const uint SifCmdRpcRdata = RealSifRpc.CidRpcRdata; // 0x8000000C

    /// <summary>
    /// Canonical SCPH70008 <c>IOPBTCONF</c> <c>@800</c> module order (docs/BIOS_DISSECTION.md §2).
    /// Used by unit tests and as the no-image fallback sequence for commercial fast-path names
    /// that appear in the retail text blob (not the full extended contract table).
    /// </summary>
    public static readonly string[] Scph70008IopBtConfOrder =
    {
        "SYSMEM", "LOADCORE", "EXCEPMAN", "INTRMANP", "INTRMANI", "SSBUSC", "DMACMAN",
        "TIMEMANP", "TIMEMANI", "SYSCLIB", "HEAPLIB", "EECONF", "THREADMAN", "VBLANK",
        "IOMAN", "MODLOAD", "ROMDRV", "STDIO", "SIFMAN", "IGREETING", "SIFCMD", "REBOOT",
        "LOADFILE", "CDVDMAN", "CDVDFSV", "SIFINIT", "FILEIO",
    };

    private readonly List<string> _romdirNames = new();
    private readonly List<string> _iopBtConfNames = new();
    private readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _biosImage;
    private string? _biosPath;
    private bool _started;
    private bool _igreetingDone;
    private bool _stdioReady;
    private ulong _iopRebootHandoffs;
    private LiteralIopBtConfBootResult? _lastLiteralBoot;

    public bool Started => _started;
    public string? BiosPath => _biosPath;
    public int RomdirModuleCount => _romdirNames.Count;
    public IReadOnlyList<string> RomdirNames => _romdirNames;
    /// <summary>Ordered module names from bound BIOS <c>IOPBTCONF</c> (empty if unbound / missing).</summary>
    public IReadOnlyList<string> IopBtConfNames => _iopBtConfNames;
    public ulong ServicesInstalled { get; private set; }
    /// <summary>Result of the last <see cref="BootIopBtConfLiteral"/> call (null if never run).</summary>
    public LiteralIopBtConfBootResult? LastLiteralBoot => _lastLiteralBoot;

    /// <summary>IGREETING.IRX init stub ran (early IOP banner). Idempotent once per bring-up.</summary>
    public bool IgreetingDone => _igreetingDone;

    /// <summary>STDIO.IRX ready — printf/puts sink attached to tty/stderr.</summary>
    public bool StdioReady => _stdioReady;

    /// <summary>Times post-RESET_CMD handoff re-applied REBOOT/STDIO/IGREETING/IOMAN contracts.</summary>
    public ulong IopRebootHandoffs => _iopRebootHandoffs;

    public void Reset()
    {
        _romdirNames.Clear();
        _iopBtConfNames.Clear();
        _registered.Clear();
        _biosImage = null;
        _biosPath = null;
        _started = false;
        ServicesInstalled = 0;
        _igreetingDone = false;
        _stdioReady = false;
        _iopRebootHandoffs = 0;
        _lastLiteralBoot = null;
    }

    /// <summary>Parse ROMDIR + IOPBTCONF from a BIOS image path (or already-loaded bytes).</summary>
    public void BindBios(string? path, byte[]? image = null)
    {
        Reset();
        if (image != null && image.Length > 0)
            _biosImage = image;
        else if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            _biosPath = path;
            _biosImage = File.ReadAllBytes(path);
        }
        else return;

        foreach (var e in RomdirExtractor.ParseRomdir(_biosImage))
        {
            if (!string.IsNullOrWhiteSpace(e.Name) && e.Name != "-")
                _romdirNames.Add(e.Name);
        }

        // Ordered IOPBTCONF list for WP-15 / literal boot (unit-testable via ParseIopBtConfText).
        _iopBtConfNames.AddRange(ExtractIopBtConfNames(_biosImage));
    }

    /// <summary>
    /// Install BIOS IOP service destinations into the HLE host. Call after
    /// <see cref="Ps2System.LoadBios"/> / before or with disc boot — not per-thread.
    /// Order: IOPBTCONF (when present in the image) → ROMDIR names → contract table → RPC ELFs.
    /// </summary>
    public void StartCommercialIop(Ps2System sys)
    {
        if (sys == null) return;

        // ROMDRV: bind BIOS image into FILEIO/IOMAN so rom0: open/read/getstat serve ROMDIR bytes
        // (or clear binding for the no-image path — synthetic empty stubs remain).
        sys.IopModules.BindRomBios(_biosImage);

        if (_biosImage == null || _biosImage.Length == 0)
        {
            // Still install the fixed contract table so destinations exist even without a parse.
            InstallIopBtConfOrder(sys, null);
            InstallContractModules(sys);
            FinishIopServices(sys);
            _started = true;
            return;
        }

        // 1) IOPBTCONF boot order from the real BIOS text blob (docs/BIOS_DISSECTION.md §2).
        var btconf = ExtractIopBtConfNames(_biosImage);
        InstallIopBtConfOrder(sys, btconf);

        // 2) Every other ROMDIR name so sceSifLoadModule / search-by-name succeeds.
        foreach (var name in _romdirNames)
            Register(sys, name);

        // 3) Contract table (RPC SIDs + aliases) — forces REQ modules even if ROMDIR parse missed.
        InstallContractModules(sys);

        // 4) Load real BIOS IRX ELFs into IOP RAM (always — IRX is the product path).
        // Emergency HLE bisect: DETPS2_FORCE_HLE_IOP=1 / DETPS2_LITERAL_IRX=0 still loads
        // RPC-critical ELFs but BootIopBtConfLiteral is the full IOPBTCONF chain.
        if (IopModuleHost.IsLiteralIrxEnabled)
        {
            var lit = BootIopBtConfLiteral(sys);
            ServicesInstalled += (ulong)lit.ElfsLoaded;
        }
        // Always also load BootCritical RPC modules (PADMAN, FILEIO, …) if extractable.
        foreach (var c in BootCriticalContracts)
        {
            if (c.RpcSid == 0) continue;
            if (sys.IopModules.TryGetModule(c.RomdirName, out int existingId) &&
                sys.IopModules.TryGetIrx(existingId, out var existing) &&
                existing.HasImage)
                continue;
            try
            {
                byte[]? mod = RomdirExtractor.ExtractModule(_biosImage, c.RomdirName);
                if (mod == null || mod.Length < 52) continue;
                if (mod[0] != 0x7F || mod[1] != (byte)'E') continue;
                sys.IopModules.LoadIrx(mod, sys.Memory, c.RomdirName);
                ServicesInstalled++;
            }
            catch
            {
                Register(sys, c.RomdirName);
            }
        }

        FinishIopServices(sys);

        _started = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BIOS] StartCommercialIop romdir={_romdirNames.Count} btconf={btconf.Count} " +
                $"registered={_registered.Count} elfLoads={ServicesInstalled} " +
                $"romdrv={sys.IopModules.RomBiosBound} romdirEntries={sys.IopModules.RomdirEntryCount} " +
                $"iopVblankEf={sys.IopVblank.EventFlagId} path={_biosPath ?? "(bytes)"}");
    }

    /// <summary>
    /// SIFINIT + EESYNC + SIFCMD ready flags + IOP VBLANK.IRX event flag + STDIO/IGREETING —
    /// last step of commercial IOP bring-up (with or without a parsed BIOS image).
    /// Contracts: docs/bios-ports/SIFINIT_EESYNC.md, docs/bios-ports/REBOOT_STDIO_IOMAN.md,
    /// docs/BIOS_DISSECTION.md §2–3.
    /// </summary>
    private void FinishIopServices(Ps2System sys)
    {
        // IOPBTCONF SIF stack as already up:
        //   SIFMAN  → SIF_STAT_SIFINIT
        //   SIFCMD  → SIF_STAT_CMDINIT (+ RPC handlers via RealSifRpc)
        //   SIFINIT → idempotent SIFMAN ensure (decomp "Skip SIF init" if set)
        //   EESYNC  → SyncEE posts SIF_STAT_BOOTEND
        sys.Sif.ApplySifInit();
        sys.Sif.ApplyCmdInit();
        sys.Sif.PostBootEnd();
        sys.Sif.PresentIopBootReady();

        // SUBADDR / SYSREG_RPCINIT / EE ready-slot table (sceSifInitCmd / sceSifInitRpc).
        sys.Hle.Sony?.PlantSifInitSyncContracts();

        // IOP VBLANK.IRX: empty handler lists + thevent flag ready (dispatch from PCRTC).
        // Real VBLANK._start also RegisterIntrHandler(IRQ 0/11) + EnableIntr — plant those
        // so IopSystemHost.OnVblankIrqPulse observes the same contract (callbacks stay HLE).
        sys.IopVblank.Reset();
        sys.IopVblank.EnsureEventFlag(sys.Hle.Kernel);

        // INTRMAN/TIMEMAN/IOMAN device table + default devices after IOPBTCONF.
        sys.IopSystem.Reset();
        // Commercial PS2 path loads TIMEMANI (6 RTC slots); TIMEMANP is the 3-slot PS1-compat.
        sys.IopSystem.ConfigureTimeMan(useMani: true);
        sys.IopSystem.InstallBiosDevices();
        sys.IopDmacMan.Start();
        sys.IopSysclibHeaplib.Install(sys.Memory, sys.IopModules);
        sys.IopSsbusc.ApplyBiosDefaults();
        sys.IopEeconf.ApplyBiosInit();

        // Extended ROMDIR services: SECRMAN/CLEARSPU/LIBSD/UDNL/ADDDRV/X* + THREADMAN thmsgbx/vpl/fpl exports.
        // Ground-truthed against SCPH70008 ROMDIR (101 entries) + Ghidra IRX decomp.
        sys.IopExtendedBios.Install(sys);
        sys.IopExtendedBios.ApplyClearSpu(sys);

        // Opaque non-zero "handler" cookies — not R3000-executed; mark IRQ 0/11 owned by VBLANK.
        sys.IopSystem.RegisterIntrHandler(IopSystemHost.IrqVblank, 1, 0x56424C4Bu /* "VBLK" */, 0);
        sys.IopSystem.RegisterIntrHandler(IopSystemHost.IrqEvblank, 1, 0x5642454Eu /* "VBEN" */, 0);
        sys.IopSystem.EnableIntr(IopSystemHost.IrqVblank);
        sys.IopSystem.EnableIntr(IopSystemHost.IrqEvblank);

        // IOPBTCONF mid-stack: STDIO (after ROMDRV) then IGREETING (after SIFMAN).
        // REBOOT is name-registered so rom0:REBOOT / module probes succeed; the RESET_CMD
        // contract lives on Sif + SonyKernelHle (not R3000 IRX).
        ApplyStdioContract(sys);
        ApplyIgreetingContract(sys);
        Register(sys, "REBOOT");
    }

    /// <summary>
    /// REBOOT.IRX completion / EESYNC re-post after <c>SifIopReset</c>: re-apply the
    /// post-IOPBTCONF service contracts that a real IOP reload would re-register
    /// (devices, STDIO, IGREETING, SIF ready). Called from SonyKernelHle on deferred
    /// reboot complete — generic, no title PCs.
    /// </summary>
    public static void ApplyPostIopRebootContracts(Ps2System sys)
    {
        if (sys == null) return;
        // IOMAN device registry re-seed (AddDrv table wiped on real reboot).
        sys.IopSystem.InstallBiosDevices();
        sys.IopSystem.EnsureStdioDevices();

        // SIFINIT idempotent ensure + EESYNC already applied by Sif.TryCompletePendingIopReboot.
        sys.Sif.ApplySifInit();
        sys.Sif.ApplyCmdInit();
        sys.Sif.PostBootEnd();

        var host = sys.BiosBoot;
        host.ApplyStdioContract(sys);
        host.ApplyIgreetingContract(sys);
        // Re-present LOADFILE/FILEIO-visible module names after IOPRP-style reboot.
        // Real UDNL would re-load the image; HLE keeps soft registrations live.
        ReRegisterPostRebootModules(sys);
        // Functional UDNL handoff: version tag + CLEARSPU + common IOPRP module names.
        // RealRpc.OnIopReboot still runs from SonyKernelHle.OnIopRebootCompleted (after this).
        // Titles still plant EE RAM version cells when they bypass LOADFILE GetVersion.
        // DETPS2_UDNL_SKIP_HANDOFF=1: leave pre-G0 reboot surface (name re-reg only) — A/B for
        // commercial titles that Exit early when full UDNL image path runs.
        if (!string.Equals(Environment.GetEnvironmentVariable("DETPS2_UDNL_SKIP_HANDOFF"), "1",
                StringComparison.Ordinal))
            sys.IopExtendedBios.ApplyUdnlHandoff(sys, sys.Sif.LastIopRebootArg);
        host._iopRebootHandoffs++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_REBOOT") == "1" ||
            Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[REBOOT] handoff gen={sys.Sif.IopRebootGeneration} " +
                $"arg=\"{sys.Sif.LastIopRebootArg}\" devices={sys.IopSystem.DeviceCount} " +
                $"stdio={host._stdioReady} igreeting={host._igreetingDone} " +
                $"udnlVer=\"{sys.IopExtendedBios.LastUdnlVersion}\"");
    }

    /// <summary>
    /// After <c>SifIopReset</c>/<c>RESET_CMD</c>, re-register modules titles probe via
    /// LOADFILE (rom0 + common disc IRX names). Parses <c>IOPRPxxx</c> from the reboot
    /// arg when present so name probes succeed.
    /// </summary>
    private static void ReRegisterPostRebootModules(Ps2System sys)
    {
        foreach (var n in new[]
                 {
                     "LOADFILE", "FILEIO", "MODLOAD", "SIO2MAN", "PADMAN", "MCMAN", "MCSERV",
                     "LIBSD", "SDRDRV", "IOPFILE", "IOPMEM", "IOPSND", "CDVDMAN", "CDVDFSV",
                     "SECRMAN", "CLEARSPU", "UDNL", "ADDDRV", "XMTAPMAN", "XPADMAN", "XSIO2MAN",
                     // Common retail IOPRP image tags (also re-registered from RESET_CMD arg).
                     "IOPRP214", "IOPRP234", "IOPRP243", "IOPRP250", "IOPRP252", "IOPRP255",
                     "IOPRP280", "IOPRP300", "DNAS280"
                 })
            sys.IopModules.RegisterModule(n);

        string arg = sys.Sif.LastIopRebootArg ?? "";
        // rom0:UDNL cdrom0:\IOPRP234.IMG;1  → register IOPRP234
        int idx = arg.IndexOf("IOPRP", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            int end = idx;
            while (end < arg.Length)
            {
                char c = arg[end];
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
                    break;
                end++;
            }
            string token = arg[idx..end];
            int dot = token.IndexOf('.');
            if (dot > 0) token = token[..dot];
            if (token.Length > 0)
                sys.IopModules.RegisterModule(token);
        }
    }

    /// <summary>
    /// STDIO.IRX contract: tty/stderr devices present; printf/puts sink ready (non-fatal log).
    /// </summary>
    public void ApplyStdioContract(Ps2System sys)
    {
        sys.IopSystem.EnsureStdioDevices();
        sys.IopSystem.StdioReady = true;
        _stdioReady = true;
        // Keep module registered for rom0:STDIO load probes.
        Register(sys, "STDIO");
    }

    /// <summary>
    /// IGREETING.IRX init stub: early IOP greeting (banner to STDIO). Idempotent.
    /// Real module is a tiny resident that prints once during IOPBTCONF after SIFMAN.
    /// </summary>
    public void ApplyIgreetingContract(Ps2System sys)
    {
        Register(sys, "IGREETING");
        if (_igreetingDone) return;
        // Non-fatal banner via STDIO sink (matches "greeting" role — never aborts boot).
        sys.IopSystem.Printf("IOP: IGREETING ready (DetPS2 HLE)\n");
        _igreetingDone = true;
    }

    /// <summary>
    /// IRX path is always on unless emergency HLE bisect
    /// (<c>DETPS2_FORCE_HLE_IOP=1</c> / legacy <c>DETPS2_LITERAL_IRX=0</c>).
    /// Delegates to <see cref="IopModuleHost.IsLiteralIrxEnabled"/>.
    /// </summary>
    public static bool IsLiteralIrxEnabled() => IopModuleHost.IsLiteralIrxEnabled;

    /// <summary>
    /// Pure IOPBTCONF text parser — unit-testable without a BIOS image.
    /// Lines like "SYSMEM" / "@800": skips empty lines and directives starting with '@'.
    /// Module names are at most 16 printable ASCII characters (ROMDIR name width).
    /// </summary>
    public static List<string> ParseIopBtConfText(string text)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(text)) return names;
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
        return names;
    }

    /// <summary>
    /// Parse IOPBTCONF module name list from a BIOS image (ROMDIR entry "IOPBTCONF").
    /// Uses <see cref="RomdirExtractor.ExtractModuleContent"/> so text payloads stay at the
    /// naive offset (not stolen by a later entry's ELF magic).
    /// </summary>
    public static List<string> ExtractIopBtConfNames(byte[] bios)
    {
        if (bios == null || bios.Length == 0) return new List<string>();
        byte[]? content = RomdirExtractor.ExtractModuleContent(bios, "IOPBTCONF");
        if (content == null || content.Length == 0) return new List<string>();
        return ParseIopBtConfText(Encoding.ASCII.GetString(content));
    }

    /// <summary>
    /// Ordered IOPBTCONF names from the bound BIOS image, or the empty list if unbound.
    /// Prefer this over re-parsing when <see cref="BindBios"/> already ran.
    /// </summary>
    public IReadOnlyList<string> GetBoundIopBtConfNames() => _iopBtConfNames;

    /// <summary>
    /// For each name in <paramref name="btconfOrder"/>, report whether a real ELF can be
    /// extracted from <paramref name="bios"/> via <see cref="RomdirExtractor.ExtractModule"/>.
    /// Does not load into IOP RAM — inventory helper for WP-15/WP-16.
    /// </summary>
    public static List<(string Name, bool ElfExtractable, int ByteLength)> InventoryIopBtConfElfs(
        byte[] bios, IReadOnlyList<string> btconfOrder)
    {
        var rows = new List<(string Name, bool ElfExtractable, int ByteLength)>(btconfOrder.Count);
        foreach (string name in btconfOrder)
        {
            byte[]? mod = RomdirExtractor.ExtractModule(bios, name);
            bool elf = mod != null && mod.Length >= 52 &&
                       mod[0] == 0x7F && mod[1] == (byte)'E' &&
                       mod[2] == (byte)'L' && mod[3] == (byte)'F';
            rows.Add((name, elf, mod?.Length ?? 0));
        }
        return rows;
    }

    /// <summary>
    /// Walk IOPBTCONF: LoadIrx every extractable ELF, then <b>run R3000 <c>_start</c></b> via
    /// <see cref="IopModuleHost.StartLoadedModule"/> (WP-14/16). Name-only when no ELF.
    /// Optional <paramref name="maxModulesToExec"/> limits sequential exec (0 = all loaded).
    /// <paramref name="maxInsnPerModule"/> budget per <c>_start</c> (default 50k).
    /// </summary>
    public LiteralIopBtConfBootResult BootIopBtConfLiteral(
        Ps2System sys,
        int maxModulesToExec = 0,
        ulong maxInsnPerModule = 50_000)
    {
        var result = new LiteralIopBtConfBootResult();
        if (sys == null)
        {
            _lastLiteralBoot = result;
            return result;
        }

        List<string> order;
        if (_iopBtConfNames.Count > 0)
            order = new List<string>(_iopBtConfNames);
        else if (_biosImage != null && _biosImage.Length > 0)
            order = ExtractIopBtConfNames(_biosImage);
        else
            order = new List<string>(Scph70008IopBtConfOrder);

        result.Order.AddRange(order);
        result.LiteralIrxEnv = IsLiteralIrxEnabled();
        sys.IopModules.BindRomBios(_biosImage);

        // Minimal IOP exception trampoline so SYSCALL/BREAK in early IRX do not halt forever.
        sys.Iop.Cop0Status = 0; // BEV=0 → RAM vector
        sys.Iop.InstallMinimalExceptionStub();

        int execBudgetLeft = maxModulesToExec <= 0 ? int.MaxValue : maxModulesToExec;

        foreach (string name in order)
        {
            result.ModulesSeen++;
            if (_biosImage == null || _biosImage.Length == 0)
            {
                Register(sys, name);
                result.NameOnlyRegistered++;
                result.Steps.Add(new LiteralIopBtConfStep
                {
                    Name = name, Action = "register-no-bios", Loaded = false, Started = false
                });
                continue;
            }

            byte[]? mod = null;
            try { mod = RomdirExtractor.ExtractModule(_biosImage, name); }
            catch { mod = null; }

            bool isElf = mod != null && mod.Length >= 52 &&
                         mod[0] == 0x7F && mod[1] == (byte)'E' &&
                         mod[2] == (byte)'L' && mod[3] == (byte)'F';

            if (!isElf)
            {
                Register(sys, name);
                result.NameOnlyRegistered++;
                result.Steps.Add(new LiteralIopBtConfStep
                {
                    Name = name, Action = "register-name-only", Loaded = false, Started = false,
                    Bytes = mod?.Length ?? 0
                });
                continue;
            }

            result.ElfsExtractable++;
            try
            {
                var lr = sys.IopModules.LoadIrx(mod!, sys.Memory, name);
                if (!lr.Success)
                {
                    Register(sys, name);
                    result.LoadFailures++;
                    result.NameOnlyRegistered++;
                    result.Steps.Add(new LiteralIopBtConfStep
                    {
                        Name = name, Action = "load-failed:" + lr.Message,
                        Loaded = false, Started = false, Bytes = mod!.Length
                    });
                    continue;
                }

                result.ElfsLoaded++;
                ServicesInstalled++;
                bool started = false;
                string startNote = "load-only";
                ulong execInsns = 0;

                if (result.LiteralIrxEnv &&
                    execBudgetLeft > 0 &&
                    sys.IopModules.TryGetModule(name, out int mid) &&
                    sys.IopModules.TryGetIrx(mid, out var irx) &&
                    irx.HasImage && irx.Entry != 0)
                {
                    // INTRMANP (PRId≥16) needs 0x1F801450 bit3 set; SIFMAN needs it clear.
                    // Retail ROM/SSBUS leaves bit3 set through interrupt bring-up then SIF
                    // sees the post-intrman clear. Mirror that between these two modules.
                    if (string.Equals(name, "INTRMANP", StringComparison.OrdinalIgnoreCase))
                        sys.Memory.IopWrite32(0xBF801450, SystemMemory.IopIoIntrmanConfigDefault);
                    else if (string.Equals(name, "SIFMAN", StringComparison.OrdinalIgnoreCase))
                        sys.Memory.IopWrite32(0xBF801450, 0);

                    var run = sys.IopModules.StartLoadedModule(sys, mid, maxInsnPerModule);
                    execInsns = run.InstructionsExecuted;
                    started = run.Success && run.InstructionsExecuted > 0;
                    if (started)
                    {
                        result.ModulesStarted++;
                        result.ModulesExecutedR3000++;
                        result.TotalR3000Instructions += run.InstructionsExecuted;
                        execBudgetLeft--;
                    }
                    startNote = started
                        ? $"r3000-exec insns={run.InstructionsExecuted} {run.Message}"
                        : $"r3000-fail {run.Message}";
                }
                else if (!result.LiteralIrxEnv)
                {
                    startNote = "load-ok (HLE bisect; no R3000 start)";
                }
                else if (execBudgetLeft <= 0)
                {
                    startNote = "load-ok (exec budget exhausted; later modules load-only)";
                }

                result.Steps.Add(new LiteralIopBtConfStep
                {
                    Name = name,
                    Action = startNote,
                    Loaded = true,
                    Started = started,
                    Bytes = mod!.Length,
                    Entry = lr.Entry,
                    LoadBase = lr.LoadBase,
                    ExecInstructions = execInsns,
                });
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BTCONF_STEP") == "1")
                    Console.Error.WriteLine(
                        $"[BTCONF-STEP] name={name} action=\"{startNote}\" entry=0x{lr.Entry:X8} " +
                        $"loadBase=0x{lr.LoadBase:X8} execInsns={execInsns}");
            }
            catch (Exception ex)
            {
                Register(sys, name);
                result.LoadFailures++;
                result.NameOnlyRegistered++;
                result.Steps.Add(new LiteralIopBtConfStep
                {
                    Name = name, Action = "load-exception:" + ex.GetType().Name,
                    Loaded = false, Started = false, Bytes = mod?.Length ?? 0
                });
            }
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BTCONF_STEP") == "1")
        {
            uint w0 = sys.Memory.IopRead32(0x80);
            uint w1 = sys.Memory.IopRead32(0x84);
            uint w2 = sys.Memory.IopRead32(0x88);
            uint w3 = sys.Memory.IopRead32(0x8C);
            Console.Error.WriteLine(
                $"[BTCONF-VEC80] after IOPBTCONF walk: 0x80={w0:X8} 0x84={w1:X8} 0x88={w2:X8} 0x8C={w3:X8}");
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" ||
            Environment.GetEnvironmentVariable("DETPS2_TRACE_LITERAL_IRX") == "1")
            Console.Error.WriteLine(
                $"[BIOS] BootIopBtConfLiteral order={result.Order.Count} " +
                $"elfExtractable={result.ElfsExtractable} loaded={result.ElfsLoaded} " +
                $"r3000exec={result.ModulesExecutedR3000} r3000insns={result.TotalR3000Instructions} " +
                $"started={result.ModulesStarted} nameOnly={result.NameOnlyRegistered} " +
                $"fail={result.LoadFailures} literalEnv={result.LiteralIrxEnv}");

        _lastLiteralBoot = result;
        return result;
    }

    /// <summary>Per-module step recorded by <see cref="BootIopBtConfLiteral"/>.</summary>
    public sealed class LiteralIopBtConfStep
    {
        public string Name { get; init; } = "";
        public string Action { get; init; } = "";
        public bool Loaded { get; init; }
        public bool Started { get; init; }
        public int Bytes { get; init; }
        public uint Entry { get; init; }
        public uint LoadBase { get; init; }
        /// <summary>R3000 instructions retired during <see cref="IopModuleHost.StartLoadedModule"/>.</summary>
        public ulong ExecInstructions { get; init; }
    }

    /// <summary>Aggregate result of <see cref="BootIopBtConfLiteral"/> (load + R3000 exec).</summary>
    public sealed class LiteralIopBtConfBootResult
    {
        public List<string> Order { get; } = new();
        public List<LiteralIopBtConfStep> Steps { get; } = new();
        public int ModulesSeen { get; set; }
        public int ElfsExtractable { get; set; }
        public int ElfsLoaded { get; set; }
        public int ModulesStarted { get; set; }
        /// <summary>Modules that retired ≥1 R3000 insn in <c>_start</c>.</summary>
        public int ModulesExecutedR3000 { get; set; }
        public ulong TotalR3000Instructions { get; set; }
        public int NameOnlyRegistered { get; set; }
        public int LoadFailures { get; set; }
        public bool LiteralIrxEnv { get; set; }

        public override string ToString() =>
            $"order={Order.Count} extractable={ElfsExtractable} loaded={ElfsLoaded} " +
            $"r3000exec={ModulesExecutedR3000} r3000insns={TotalR3000Instructions} " +
            $"started={ModulesStarted} nameOnly={NameOnlyRegistered} fail={LoadFailures} " +
            $"literal={LiteralIrxEnv}";
    }

    private void InstallIopBtConfOrder(Ps2System sys, List<string>? names)
    {
        IEnumerable<string> order = names != null && names.Count > 0
            ? names
            : BootCriticalContracts.Where(c => c.RequiredForCommercialFastPath).Select(c => c.RomdirName);
        foreach (var name in order)
            Register(sys, name);
    }

    private void InstallContractModules(Ps2System sys)
    {
        foreach (var c in BootCriticalContracts)
        {
            Register(sys, c.RomdirName);
            // Aliases games use after sceSifLoadModule("rom0:FOO")
            Register(sys, "rom0:" + c.RomdirName);
            Register(sys, "ROM0:" + c.RomdirName);
        }

        // Disc-side names that depend on BIOS CD/SIF already being "up"
        foreach (var n in new[]
                 {
                     "CDVDSTM", "IOPRP300", "CRI_ADXI", "SNDFI", "SDRDRV",
                     "SIO2MAN", "PADMAN", "MCMAN", "MCSERV", "LIBSD", "FILEIO"
                 })
            Register(sys, n);
    }

    private void Register(Ps2System sys, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string key = name.ToUpperInvariant();
        if (!_registered.Add(key)) return;
        sys.IopModules.RegisterModule(name);
        ServicesInstalled++;
    }

    /// <summary>
    /// Dump a human-readable map (ROMDIR ∩ contracts) for debugging / docs.
    /// </summary>
    public string FormatServiceMap()
    {
        var sb = new StringBuilder();
        sb.AppendLine("BIOS/IOP service map (C# HLE destinations, shared by all titles)");
        sb.AppendLine($"  ROMDIR modules: {_romdirNames.Count}  started={_started}");
        sb.AppendLine("  Boot-critical contracts:");
        foreach (var c in BootCriticalContracts)
        {
            bool inRom = _romdirNames.Exists(n =>
                string.Equals(n, c.RomdirName, StringComparison.OrdinalIgnoreCase));
            sb.Append("    ");
            sb.Append(c.RomdirName.PadRight(12));
            sb.Append(c.RequiredForCommercialFastPath ? " [REQ] " : " [opt] ");
            sb.Append(inRom ? "in-ROM  " : "missing ");
            if (c.RpcSid != 0) sb.Append($"sid=0x{c.RpcSid:X8}  ");
            sb.Append(c.Role);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
