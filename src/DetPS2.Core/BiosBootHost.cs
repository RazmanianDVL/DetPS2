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
        new() { RomdirName = "DMACMAN", Role = "IOP DMAC", RpcSid = 0, RequiredForCommercialFastPath = true },
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
        new() { RomdirName = "STDIO", Role = "stdio", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "SIFMAN", Role = "SIF DMA transport", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "IGREETING", Role = "IOP greeting", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "SIFCMD", Role = "SIFCMD/RPC: BIND/CALL/RDATA; replies via CID 0x80000008 RPC_END (Ghidra)", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "REBOOT", Role = "IOP reboot helper", RpcSid = 0, RequiredForCommercialFastPath = false },
        new() { RomdirName = "LOADFILE", Role = "sid=0x80000006 LoadModuleByEE (Ghidra registers RPC at init)", RpcSid = RealSifRpc.SidLoadFile, RequiredForCommercialFastPath = true },
        new() { RomdirName = "CDVDMAN", Role = "CDVD manager sid family 0x80000592", RpcSid = RealSifRpc.SidCdBase, RequiredForCommercialFastPath = true },
        new() { RomdirName = "CDVDFSV", Role = "CDVD file service SCMD/NCMD", RpcSid = RealSifRpc.SidCdScmd, RequiredForCommercialFastPath = true },
        new() { RomdirName = "SIFINIT", Role = "SIF init", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "FILEIO", Role = "IOP file I/O RPC sid=0x80000001 (sceOpen family)", RpcSid = RealSifRpc.SidFileIo, RequiredForCommercialFastPath = true },
        // --- Extended / optional ROMDIR siblings ---
        new() { RomdirName = "EESYNC", Role = "EE/IOP sync", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "XLOADFILE", Role = "extended LOADFILE", RpcSid = RealSifRpc.SidLoadFile, RequiredForCommercialFastPath = false },
        new() { RomdirName = "XFILEIO", Role = "extended FILEIO", RpcSid = RealSifRpc.SidFileIo, RequiredForCommercialFastPath = false },
        new() { RomdirName = "NCDVDMAN", Role = "newer CDVDMAN NCMD sid=0x80000595", RpcSid = RealSifRpc.SidCdNcmd, RequiredForCommercialFastPath = false },
        new() { RomdirName = "PADMAN", Role = "pad sid=0x80000100", RpcSid = RealSifRpc.SidPad1, RequiredForCommercialFastPath = true },
        new() { RomdirName = "SIO2MAN", Role = "SIO2 bus", RpcSid = 0, RequiredForCommercialFastPath = true },
        new() { RomdirName = "MCMAN", Role = "memory card", RpcSid = RealSifRpc.SidMcServ, RequiredForCommercialFastPath = false },
        new() { RomdirName = "MCSERV", Role = "MC service", RpcSid = RealSifRpc.SidMcServ, RequiredForCommercialFastPath = false },
        new() { RomdirName = "LIBSD", Role = "sound driver lib", RpcSid = 0, RequiredForCommercialFastPath = false },
    };

    /// <summary>
    /// SIFCMD command IDs from real BIOS SIFCMD.IRX init (Ghidra FUN_000006c0).
    /// IOP handlers for BIND/CALL complete by sending <see cref="SifCmdRpcEnd"/> to the EE.
    /// </summary>
    public const uint SifCmdRpcEnd = RealSifRpc.CidRpcEnd;   // 0x80000008
    public const uint SifCmdRpcBind = RealSifRpc.CidRpcBind; // 0x80000009
    public const uint SifCmdRpcCall = RealSifRpc.CidRpcCall; // 0x8000000A
    public const uint SifCmdRpcRdata = RealSifRpc.CidRpcRdata; // 0x8000000C

    private readonly List<string> _romdirNames = new();
    private readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _biosImage;
    private string? _biosPath;
    private bool _started;

    public bool Started => _started;
    public string? BiosPath => _biosPath;
    public int RomdirModuleCount => _romdirNames.Count;
    public IReadOnlyList<string> RomdirNames => _romdirNames;
    public ulong ServicesInstalled { get; private set; }

    public void Reset()
    {
        _romdirNames.Clear();
        _registered.Clear();
        _biosImage = null;
        _biosPath = null;
        _started = false;
        ServicesInstalled = 0;
    }

    /// <summary>Parse ROMDIR from a BIOS image path (or already-loaded bytes).</summary>
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
    }

    /// <summary>
    /// Install BIOS IOP service destinations into the HLE host. Call after
    /// <see cref="Ps2System.LoadBios"/> / before or with disc boot — not per-thread.
    /// Order: IOPBTCONF (when present in the image) → ROMDIR names → contract table → RPC ELFs.
    /// </summary>
    public void StartCommercialIop(Ps2System sys)
    {
        if (sys == null) return;
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

        // 4) Best-effort: load BIOS IRX ELFs that own RPC sids into IopModuleHost.
        foreach (var c in BootCriticalContracts)
        {
            if (c.RpcSid == 0) continue;
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
                $"iopVblankEf={sys.IopVblank.EventFlagId} path={_biosPath ?? "(bytes)"}");
    }

    /// <summary>
    /// SIFCMD ready flags + IOP VBLANK.IRX event flag — last step of commercial IOP bring-up
    /// (with or without a parsed BIOS image).
    /// </summary>
    private static void FinishIopServices(Ps2System sys)
    {
        // SIFCMD is "initialized": RPC cmd handlers 08/09/0A/0C exist as RealSifRpc HLE.
        // SMFLAG presents CMDINIT|BOOTEND (bios-equivalent handoff).
        sys.Sif.WriteRegister(0x30, sys.Sif.ReadRegister(0x30)
            | Sif.SifStatSifInit | Sif.SifStatCmdInit | Sif.SifStatBootEnd);

        // IOP VBLANK.IRX: empty handler lists + thevent flag ready (dispatch from PCRTC).
        sys.IopVblank.Reset();
        sys.IopVblank.EnsureEventFlag(sys.Hle.Kernel);

        // INTRMAN/TIMEMAN/IOMAN device table + default devices after IOPBTCONF.
        sys.IopSystem.Reset();
        sys.IopSystem.InstallBiosDevices();

        // EE SIF library ready slots: real BIOS EELOAD + IOP SIFCMD handshake would have
        // set these before game main. Fast-boot into commercial ELF still needs them so
        // sceSifInitRpc does not spin on a zero queue-ready word (MK: 0x00778800).
        const uint eeSifReadyBase = 0x00778800;
        for (uint i = 0; i < 8; i++)
            sys.Memory.Write32(eeSifReadyBase + i * 4, 1);
    }

    /// <summary>
    /// Parse IOPBTCONF module name list from BIOS image (ROMDIR entry "IOPBTCONF").
    /// Lines like "SYSMEM" / "@800" — skips directives starting with '@'.
    /// </summary>
    public static List<string> ExtractIopBtConfNames(byte[] bios)
    {
        var names = new List<string>();
        foreach (var e in RomdirExtractor.ParseRomdir(bios))
        {
            if (!string.Equals(e.Name, "IOPBTCONF", StringComparison.OrdinalIgnoreCase))
                continue;
            int start = (int)e.NaiveOffset;
            int len = (int)Math.Min(e.Size, (uint)(bios.Length - start));
            if (start < 0 || len <= 0) break;
            string text = Encoding.ASCII.GetString(bios, start, len);
            foreach (string raw in text.Split(new[] { '\r', '\n', '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '@') continue;
                // printable module names only
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
