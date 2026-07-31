using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// DetPS2 SIF RPC ABI (Phases 13/22).
/// Packet in EE RDRAM (16 bytes): cmd, eeBuffer, size, result.
/// </summary>
public static class SifRpcCmd
{
    public const uint Open = 1;
    public const uint Close = 2;
    public const uint Read = 3;
    public const uint Write = 4;
    public const uint Seek = 5;
    public const uint PadState = 6;
    public const uint CdvdRead = 7;
    public const uint LoadModule = 8;
    public const uint GetModule = 9;
    public const uint LoadIrx = 10;
    public const uint MemCard = 11;
}

public readonly struct SifRpcPacket
{
    public uint Cmd { get; init; }
    public uint EeBuffer { get; init; }
    public uint Size { get; init; }
    public uint Result { get; init; }

    public static SifRpcPacket Read(SystemMemory mem, uint addr) => new()
    {
        Cmd = mem.Read32(addr),
        EeBuffer = mem.Read32(addr + 4),
        Size = mem.Read32(addr + 8),
        Result = mem.Read32(addr + 12)
    };

    public void Write(SystemMemory mem, uint addr)
    {
        mem.Write32(addr, Cmd);
        mem.Write32(addr + 4, EeBuffer);
        mem.Write32(addr + 8, Size);
        mem.Write32(addr + 12, Result);
    }

    public SifRpcPacket WithResult(uint result) => new()
    {
        Cmd = Cmd,
        EeBuffer = EeBuffer,
        Size = Size,
        Result = result
    };
}

/// <summary>MODLOAD/LOADCORE module lifecycle. Real ModuleInfo_t lives on LOADCORE's image_info
/// list; DetPS2 tracks the same states. HLE <see cref="IopModuleHost.StartModule"/> marks
/// Started without R3000; <see cref="IopModuleHost.StartLoadedModule"/> runs real entry code.</summary>
public enum IopModuleState
{
    /// <summary>Name registered, no image (HLE stub / pending).</summary>
    Registered = 0,
    /// <summary>IRX image relocated into IOP RAM; <c>_start</c> not yet run on R3000.</summary>
    Loaded = 1,
    /// <summary><c>_start</c> completed (HLE soft-start and/or real R3000 entry returned).</summary>
    Started = 2,
    /// <summary>Stopped after a prior start; eligible for unload when non-resident.</summary>
    Stopped = 3,
}

/// <summary>
/// One IOP module table entry — HLE mirror of LOADCORE <c>ModuleInfo_t</c> fields games care about
/// (id / name / entry / gp / text_start / size / start order) plus MODLOAD start/stop state and
/// runnable context for literal IRX execution (WP-07).
/// </summary>
public sealed class LoadedIrx
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>EE-mapped entry (0x1Cxxxxxx). Convert via <see cref="IopModuleHost.ToIopPhys"/> for <see cref="Iop.PC"/>.</summary>
    public uint Entry { get; set; }
    /// <summary>GP value from .iopmod (0 if unknown / PT_LOAD fixture).</summary>
    public uint Gp { get; set; }
    /// <summary>EE-mapped load base (0x1Cxxxxxx).</summary>
    public uint LoadBase { get; set; }
    public int Segments { get; set; }
    /// <summary>Real loaded extent (0 if name-only HLE registration).</summary>
    public uint Size { get; set; }
    public IopModuleState State { get; set; }
    /// <summary>Monotonic order assigned on each successful Start (1-based). 0 = never started.</summary>
    public int StartOrder { get; set; }
    /// <summary>Last <c>_start</c> / stop return (MODULE_RESIDENT_END=0, NO_RESIDENT=1, REMOVABLE=2).</summary>
    public int LastModRes { get; set; }
    /// <summary>True when real IRX bytes were placed in IOP RAM via <see cref="IopModuleHost.LoadIrx"/>.</summary>
    public bool HasImage { get; set; }
    /// <summary>Boot/default HLE modules that must not be unloaded (InitDefaults / system).</summary>
    public bool SystemResident { get; set; }
    /// <summary>True after at least one successful <see cref="IopModuleHost.StartLoadedModule"/> run returned.</summary>
    public bool EntryExecuted { get; set; }
    /// <summary>IOP instructions retired during the last <see cref="IopModuleHost.StartLoadedModule"/> call.</summary>
    public ulong LastEntryInstructions { get; set; }
}

/// <summary>Result of running a loaded module's entry on the R3000 IOP core.</summary>
public sealed class ModuleRunResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int ModuleId { get; init; }
    public string Name { get; init; } = "";
    /// <summary>IOP-physical PC set at entry (not EE-mapped).</summary>
    public uint EntryPc { get; init; }
    public uint FinalPc { get; init; }
    public ulong InstructionsExecuted { get; init; }
    /// <summary>v0 after return (MODULE_*_END when _start returns conventionally).</summary>
    public int ModRes { get; init; }
    public bool ReturnedToSentinel { get; init; }
    public bool HitInstructionBudget { get; init; }
}

/// <summary>
/// IOP module registry + RPC + IRX load (Phases 13/22) + MODLOAD contract HLE.
/// Module table / load / start / stop / unload / search-by-name|address are ground-truthed against
/// BIOS MODLOAD.IRX (tools/bios-decomp/MODLOAD_ALL.txt) and ps2sdk <c>modload.h</c> / LOADFILE RPC.
/// Cross-module export linking remains LOADCORE's domain via <see cref="IrxLoader"/>.
/// </summary>
public sealed class IopModuleHost
{
    private readonly Dictionary<string, int> _modules = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Full module table keyed by MODLOAD id (also backs legacy <see cref="TryGetIrx"/>).</summary>
    private readonly Dictionary<int, LoadedIrx> _irxById = new();
    private readonly Dictionary<int, string> _openFiles = new();
    private readonly Dictionary<int, OpenHostFile> _hostFiles = new();
    private readonly Dictionary<int, OpenDir> _openDirs = new();
    private int _nextModuleId = 1;
    private int _nextStartOrder = 1;
    /// <summary>Legacy synthetic SifRpcCmd.Open path only (unbounded). Real FILEIO/IOMAN
    /// allocation uses <see cref="AllocIoManFd"/> over slots 0..15.</summary>
    private int _nextFd = 3;
    private uint _nextIopBase = IrxLoader.DefaultLoadBase;
    private MemoryCard _memcard = new();
    private Iso9660.Volume? _discVolume;
    private string? _discPath;
    /// <summary>Bound BIOS image for ROMDRV <c>rom0:</c> content (null = synthetic empty stubs).</summary>
    private byte[]? _romBios;
    private List<RomdirExtractor.RomdirEntry>? _romdirCache;
    /// <summary>Optional IOMAN/STDIO host for AddDrv/DelDrv + tty write routing.</summary>
    private IopSystemHost? _ioSystem;

    // --- MODLOAD / loadcore result codes (ps2sdk loadcore.h + MODLOAD decomp) ---
    /// <summary>Module _start returned "stay resident" (cannot unload).</summary>
    public const int ModuleResidentEnd = 0;
    /// <summary>Module _start returned non-resident (unloadable after stop).</summary>
    public const int ModuleNoResidentEnd = 1;
    /// <summary>Module _start returned removable (modload &gt; v1.2).</summary>
    public const int ModuleRemovableEnd = 2;
    /// <summary>StartModule: id not on image_info list (decomp FUN_000005a0 → 0xFFFFFF36).</summary>
    public const int ModloadErrNotFound = unchecked((int)0xFFFFFF36); // -202
    /// <summary>Illegal boot device / cannot unload resident (decomp / LOADFILE 0xFFFFFF37).</summary>
    public const int ModloadErrIllegal = unchecked((int)0xFFFFFF37); // -201

    // fio / iox_stat mode bits (ps2sdk iox_stat.h / io_common.h)
    public const uint FioSIfDir = 0x1000;
    public const uint FioSIfReg = 0x2000;
    public const uint FioSIfmt = 0xF000;
    public const uint FioSIrusr = 0x0100;
    public const uint FioSIwusr = 0x0080;
    public const uint FioSIxusr = 0x0040;

    // Real BIOS IOMAN.IRX file-descriptor table, ground-truthed via Ghidra decompile
    // (tools/bios-decomp/IOMAN_ALL.txt): FUN_00000b98 scans a fixed 16-slot table; FUN_00000c3c
    // validates with `0xf < fd`. Exhaustion returns real errno -24 (EMFILE, module string
    // "out of file descriptors"). sceOpen and sceDopen share the same allocator; successful
    // open returns the slot index 0..15 (not an unbounded counter).
    private const int IoManMaxDescriptors = 16;
    public const int IoManErrnoOutOfDescriptors = -24; // EMFILE
    public const int IoManErrnoBadFile = -9;           // EBADF
    public const int IoManErrnoNoDevice = -19;         // ENODEV (unknown device)
    public const int IoManErrnoInvalid = -22;          // EINVAL (bad lseek whence)
    public const int IoManErrnoNoEntry = -2;           // ENOENT (missing path on mounted disc)

    private sealed class OpenHostFile
    {
        public string Path = "";
        public byte[]? Data;
        public int Position;
        public uint Lba;
        public uint Size;
        /// <summary>Extra byte offset within the first sector for virtual sub-streams (RKV entries).</summary>
        public int BaseOffset;
    }

    private sealed class OpenDir
    {
        public string Path = "";
        public List<Iso9660.FileEntry> Entries = new();
        public int Index;
    }

    public ulong RpcHandled { get; private set; }
    /// <summary>FILEIO open/read/stat hits that resolved real ROMDIR bytes via ROMDRV HLE.</summary>
    public ulong Rom0BytesServed { get; private set; }
    /// <summary>True when a BIOS image is bound for <c>rom0:</c> content serving.</summary>
    public bool RomBiosBound => _romBios != null && _romBios.Length > 0;
    /// <summary>ROMDIR entry count when a BIOS image is bound; 0 otherwise.</summary>
    public int RomdirEntryCount => _romdirCache?.Count ?? 0;
    public int ModuleCount => _modules.Count;
    /// <summary>Modules that currently have a real IRX image in IOP RAM.</summary>
    public int IrxLoadedCount
    {
        get
        {
            int n = 0;
            foreach (var m in _irxById.Values)
                if (m.HasImage) n++;
            return n;
        }
    }
    public MemoryCard MemCard => _memcard;
    public ulong IrxLoads { get; private set; }
    public ulong DiscBytesRead { get; private set; }
    public ulong ModuleStarts { get; private set; }
    public ulong ModuleStops { get; private set; }
    public ulong ModuleUnloads { get; private set; }
    /// <summary>IOP instructions retired across all <see cref="StartLoadedModule"/> calls.</summary>
    public ulong ModuleEntryInstructions { get; private set; }
    /// <summary>Successful R3000 module entry runs (returned to sentinel or exhausted budget with progress).</summary>
    public ulong ModuleEntryRuns { get; private set; }

    /// <summary>
    /// IRX execution is the <b>only</b> normal boot path: load real modules and run them on IOP.
    /// Opt-out only for emergency bisect: <c>DETPS2_FORCE_HLE_IOP=1</c> (or legacy
    /// <c>DETPS2_LITERAL_IRX=0</c>). There is no separate "IRX mode" — IRX is the emulator.
    /// </summary>
    public static bool IsLiteralIrxEnabled
    {
        get
        {
            string? forceHle = Environment.GetEnvironmentVariable("DETPS2_FORCE_HLE_IOP");
            if (string.Equals(forceHle, "1", StringComparison.Ordinal) ||
                string.Equals(forceHle, "true", StringComparison.OrdinalIgnoreCase))
                return false;
            // Legacy opt-out only (was opt-in =1; now default on unless explicitly 0).
            string? legacy = Environment.GetEnvironmentVariable("DETPS2_LITERAL_IRX");
            if (string.Equals(legacy, "0", StringComparison.Ordinal) ||
                string.Equals(legacy, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(legacy, "off", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
    }

    /// <summary>Return address planted in <c>$ra</c> so <c>jr ra</c> from module entry is detectable.</summary>
    public const uint ModuleReturnSentinel = 0x0000BEE0u;

    /// <summary>Default IOP stack top for module entry (below 2 MiB IOP RAM end).</summary>
    public const uint DefaultModuleStack = 0x001FF000u;

    private int _pendingLiteralId = -1;
    private uint _pendingLiteralEntryPhys;
    private uint _pendingLiteralGp;
    private string _pendingLiteralName = "";

    /// <summary>True when a LoadIrx under LITERAL_IRX left an entry ready to arm on the IOP.</summary>
    public bool HasPendingLiteralEntry => _pendingLiteralId >= 0;

    /// <summary>Module id of the pending literal entry, or -1.</summary>
    public int PendingLiteralModuleId => _pendingLiteralId;

    /// <summary>Share system memory card instance (Phase 31).</summary>
    public void BindMemCard(MemoryCard card) => _memcard = card ?? new MemoryCard();

    public void Reset()
    {
        _modules.Clear();
        _irxById.Clear();
        _openFiles.Clear();
        _hostFiles.Clear();
        _openDirs.Clear();
        _hostWriteOverlay.Clear();
        _nextModuleId = 1;
        _nextStartOrder = 1;
        _nextFd = 3;
        _nextIopBase = IrxLoader.DefaultLoadBase;
        RpcHandled = 0;
        IrxLoads = 0;
        DiscBytesRead = 0;
        Rom0BytesServed = 0;
        ModuleStarts = 0;
        ModuleStops = 0;
        ModuleUnloads = 0;
        ModuleEntryInstructions = 0;
        ModuleEntryRuns = 0;
        ImportsResolved = 0;
        ImportsUnresolved = 0;
        _exportRegistry.Clear();
        ClearPendingLiteralEntry();
        // keep disc volume + ROM bios binding + bound card
        _memcard.Format();
    }

    /// <summary>Clear the optional post-LoadIrx literal entry arming state.</summary>
    public void ClearPendingLiteralEntry()
    {
        _pendingLiteralId = -1;
        _pendingLiteralEntryPhys = 0;
        _pendingLiteralGp = 0;
        _pendingLiteralName = "";
    }

    /// <summary>
    /// Convert EE-mapped IOP RAM (0x1Cxxxxxx) or already-physical IOP address to IOP-bus physical
    /// for <see cref="Iop.PC"/> / GPR setup. <see cref="SystemMemory.IopRead32"/> only sees the
    /// low 2 MiB window — feeding EE-mapped addresses fetches zeros.
    /// </summary>
    public static uint ToIopPhys(uint eeOrPhys)
    {
        if (eeOrPhys >= SystemMemory.IOP_RAM_BASE)
            return eeOrPhys - SystemMemory.IOP_RAM_BASE;
        return eeOrPhys & 0x1FFFFFu;
    }

    /// <summary>
    /// Bind a BIOS ROM image so FILEIO/IOMAN <c>rom0:</c> paths serve real ROMDIR content
    /// (ROMDRV contract). Pass null/empty to clear and fall back to synthetic empty stubs.
    /// </summary>
    public void BindRomBios(byte[]? biosImage)
    {
        if (biosImage == null || biosImage.Length == 0)
        {
            _romBios = null;
            _romdirCache = null;
            return;
        }
        _romBios = biosImage;
        _romdirCache = RomdirExtractor.ParseRomdir(biosImage);
    }

    /// <summary>IOMAN-shaped first free slot in 0..15 across file + directory opens.
    /// Returns -1 if the table is full (caller maps to EMFILE).</summary>
    private int AllocIoManFd()
    {
        for (int fd = 0; fd < IoManMaxDescriptors; fd++)
        {
            if (!_hostFiles.ContainsKey(fd) && !_openDirs.ContainsKey(fd))
                return fd;
        }
        return -1;
    }

    /// <summary>Non-cdrom device prefixes that must not be rejected just because a disc is
    /// mounted (boot probes open host:/mc0:/rom0: without ISO entries).</summary>
    private static bool IsNonDiscDevicePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        // Require colon form ("host:foo") so bare names still resolve against the ISO.
        int colon = path.IndexOf(':');
        if (colon <= 0) return false;
        string dev = path[..colon].ToLowerInvariant();
        // Strip trailing unit digit: mc0, host0, rom0, tty00 → mc/host/rom/tty
        while (dev.Length > 0 && char.IsDigit(dev[^1]))
            dev = dev[..^1];
        return dev is "host" or "mc" or "rom" or "tty" or "dev" or "hdd" or "pfs" or "mass";
    }

    /// <summary>Mounted ISO volume (null if none). Used by LOADFILE disc path loads.</summary>
    public Iso9660.Volume? DiscVolume => _discVolume;

    /// <summary>Bind mounted ISO so FILEIO open/read return real disc bytes.</summary>
    public void BindDisc(string? isoPath)
    {
        if (string.IsNullOrEmpty(isoPath) || !File.Exists(isoPath)) return;
        if (string.Equals(_discPath, isoPath, StringComparison.OrdinalIgnoreCase) && _discVolume != null)
            return;
        try { _discVolume?.Disc?.Dispose(); } catch { /* ignore */ }
        _discPath = isoPath;
        _discVolume = Iso9660.OpenFile(isoPath);
    }

    public void InitDefaults()
    {
        // System-resident HLE destinations (commercial boot assumes these already Started).
        RegisterModule("FILEIO", systemResident: true);
        RegisterModule("PADMAN", systemResident: true);
        RegisterModule("CDVDMAN", systemResident: true);
        RegisterModule("SIO2MAN", systemResident: true);
        RegisterModule("MCMAN", systemResident: true);
        RegisterModule("MCSERV", systemResident: true);
        RegisterModule("LIBSD", systemResident: true);
    }

    
    /// <summary>
    /// Resolve a single import library against the current registry (test / diagnostics helper).
    /// Returns the table if present with matching major version, else null.
    /// </summary>
    public IrxLoader.ExportTable? LookupExportLibrary(string name, byte versionMajor = 1)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!_exportRegistry.TryGetValue(name, out var lib)) return null;
        if (lib.VersionMajor != versionMajor) return null;
        return lib;
    }

    
    /// <summary>
    /// Register (or replace) an export library by name without loading a real IRX image.
    /// Used by BIOS HLE hosts (SYSCLIB/HEAPLIB, etc.) so <see cref="IrxLoader.LinkImports"/>
    /// resolves stubs to non-null function pointers when the real module is not executed on
    /// R3000. Same last-wins semantics as scanning a freshly loaded IRX export table.
    /// </summary>
    public void RegisterExportLibrary(IrxLoader.ExportTable table)
    {
        if (table == null || string.IsNullOrEmpty(table.Name)) return;
        if (table.Exports == null || table.Exports.Length == 0) return;
        // Reject all-null export arrays — LinkImports would J to 0.
        bool any = false;
        for (int i = 0; i < table.Exports.Length; i++)
            if (table.Exports[i] != 0) { any = true; break; }
        if (!any) return;
        _exportRegistry[table.Name] = table;
    }

    /// <summary>
    /// Register a module name in the MODLOAD/LOADCORE table. Existing name → same id (search/load
    /// idempotent). New entries are marked <see cref="IopModuleState.Started"/> so BIOS/boot HLE
    /// and LOADFILE path loads present as already running (real LoadStartModule path).
    /// </summary>
    /// <param name="systemResident">If true, UnloadModule refuses (InitDefaults / BIOS stack).</param>
    public int RegisterModule(string name, bool systemResident = false)
    {
        name = NormalizeName(name);
        if (string.IsNullOrEmpty(name))
            return ModloadErrIllegal;
        if (_modules.TryGetValue(name, out int id))
        {
            if (systemResident && _irxById.TryGetValue(id, out var existing))
                existing.SystemResident = true;
            return id;
        }
        id = _nextModuleId++;
        _modules[name] = id;
        _irxById[id] = new LoadedIrx
        {
            Id = id,
            Name = name,
            State = IopModuleState.Started,
            StartOrder = _nextStartOrder++,
            LastModRes = ModuleResidentEnd,
            HasImage = false,
            SystemResident = systemResident,
        };
        ModuleStarts++;
        return id;
    }

    public bool TryGetModule(string name, out int id)
    {
        name = NormalizeName(name);
        return _modules.TryGetValue(name, out id);
    }

    public bool IsModuleLoaded(string name) => TryGetModule(name, out _);

    public bool TryGetIrx(int id, out LoadedIrx irx) => _irxById.TryGetValue(id, out irx!);

    /// <summary>Snapshot of the module table in ascending id order (LOADCORE image_info walk).</summary>
    public IReadOnlyList<LoadedIrx> GetModuleTable()
    {
        var list = new List<LoadedIrx>(_irxById.Count);
        foreach (var kv in _irxById.OrderBy(k => k.Key))
            list.Add(kv.Value);
        return list;
    }

    /// <summary>MODLOAD GetModuleIdList — positive ids currently in the table, ascending.</summary>
    public int GetModuleIdList(Span<int> dest)
    {
        int n = 0;
        foreach (var kv in _irxById.OrderBy(k => k.Key))
        {
            if (n >= dest.Length) break;
            dest[n++] = kv.Key;
        }
        return n;
    }

    /// <summary>MODLOAD SearchModuleByName / LOADFILE LF_F_SEARCH_MOD_BY_NAME. Returns id or -1.</summary>
    public int SearchModuleByName(string name)
        => TryGetModule(name, out int id) ? id : -1;

    /// <summary>
    /// MODLOAD SearchModuleByAddress / LOADFILE LF_F_SEARCH_MOD_BY_ADDRESS.
    /// Accepts IOP physical or EE-mapped (0x1Cxxxxxx) addresses; name-only stubs never match.
    /// Module <see cref="LoadedIrx.LoadBase"/> is stored EE-mapped (IOP_RAM_BASE + local).
    /// </summary>
    public int SearchModuleByAddress(uint addr)
    {
        static uint ToPhys(uint a) =>
            a >= SystemMemory.IOP_RAM_BASE ? a - SystemMemory.IOP_RAM_BASE : a;
        uint physAddr = ToPhys(addr);
        foreach (var m in _irxById.Values)
        {
            if (!m.HasImage) continue;
            uint size = m.Size == 0 ? 0x1000u : m.Size;
            uint physBase = ToPhys(m.LoadBase);
            if (physAddr >= physBase && physAddr < physBase + size)
                return m.Id;
        }
        return -1;
    }

    /// <summary>
    /// Modules with a real IRX image and non-zero entry — candidates for
    /// <see cref="StartLoadedModule"/> / literal R3000 start (WP-07).
    /// </summary>
    public IReadOnlyList<LoadedIrx> GetRunnableModules()
    {
        var list = new List<LoadedIrx>();
        foreach (var kv in _irxById.OrderBy(k => k.Key))
        {
            var m = kv.Value;
            if (m.HasImage && m.Entry != 0)
                list.Add(m);
        }
        return list;
    }

    /// <summary>
    /// MODLOAD StartModule(id) — decomp FUN_00000358 / FUN_000005a0 case 2.
    /// Returns module id on success, <see cref="ModloadErrNotFound"/> if id unknown.
    /// <paramref name="modres"/> is the HLE _start return (MODULE_*_END).
    /// Does <b>not</b> run R3000 code — use <see cref="StartLoadedModule"/> for literal entry.
    /// </summary>
    public int StartModule(int id, out int modres)
    {
        modres = 0;
        if (!_irxById.TryGetValue(id, out var m))
            return ModloadErrNotFound;
        if (m.State == IopModuleState.Started)
        {
            modres = m.LastModRes;
            return id;
        }
        // HLE: no real R3000 entry — treat success as resident stay (matches most BIOS IRX).
        m.LastModRes = ModuleResidentEnd;
        m.State = IopModuleState.Started;
        m.StartOrder = _nextStartOrder++;
        modres = m.LastModRes;
        ModuleStarts++;
        return id;
    }

    /// <summary>
    /// Arm IOP GPRs/PC for a loaded module's <c>_start</c> without stepping.
    /// Sets PC = entry (IOP phys), <c>$gp</c>, <c>$ra</c> = <see cref="ModuleReturnSentinel"/>,
    /// <c>$sp</c> = <see cref="DefaultModuleStack"/>, a0/a1 = 0 (argc/argv).
    /// Hook for T0: call from Ps2System when scheduling IOP quanta under LITERAL_IRX.
    /// </summary>
    public bool PrepareModuleEntry(Iop iop, int id)
    {
        if (iop == null) return false;
        if (!_irxById.TryGetValue(id, out var m) || !m.HasImage || m.Entry == 0)
            return false;
        uint entryPhys = ToIopPhys(m.Entry);
        iop.PC = entryPhys;
        if (m.Gp != 0)
            iop.SetGpr(28, m.Gp); // $gp
        iop.SetGpr(29, DefaultModuleStack); // $sp
        iop.SetGpr(31, ModuleReturnSentinel); // $ra
        iop.SetGpr(4, 0); // a0 argc
        iop.SetGpr(5, 0); // a1 argv
        iop.SetGpr(2, 0); // v0 clear before start
        return true;
    }

    /// <summary>
    /// If <see cref="LoadIrx"/> recorded a pending literal entry (<c>DETPS2_LITERAL_IRX=1</c>),
    /// set <see cref="Iop.PC"/> (and GP) so the next IOP Step quantum executes module text.
    /// Does not clear the pending record (idempotent re-arm). Returns false if none pending.
    /// <b>T0 handoff:</b> wire from <c>Ps2System.RunFor</c> when LITERAL_IRX=1 (WP-11).
    /// </summary>
    public bool TryArmPendingLiteralEntry(Iop iop)
    {
        if (iop == null || _pendingLiteralId < 0) return false;
        if (!PrepareModuleEntry(iop, _pendingLiteralId))
            return false;
        return true;
    }

    /// <summary>
    /// Run a loaded module's entry on the R3000 until <c>jr ra</c> hits
    /// <see cref="ModuleReturnSentinel"/> or <paramref name="maxInstructions"/> is reached.
    /// Records <see cref="LoadedIrx.LastModRes"/> from v0 on return. Always available (not gated
    /// on env); preferred smoke/exec path for WP-08.
    /// </summary>
    public ModuleRunResult StartLoadedModule(Ps2System system, int id, ulong maxInstructions = 100_000)
    {
        if (system == null)
            return new ModuleRunResult { Success = false, Message = "system is null", ModuleId = id };
        if (!_irxById.TryGetValue(id, out var m) || !m.HasImage || m.Entry == 0)
            return new ModuleRunResult
            {
                Success = false,
                Message = "module not runnable (missing image or entry)",
                ModuleId = id,
                Name = m?.Name ?? ""
            };

        var iop = system.Iop;
        if (!PrepareModuleEntry(iop, id))
            return new ModuleRunResult { Success = false, Message = "PrepareModuleEntry failed", ModuleId = id, Name = m.Name };

        uint entryPhys = ToIopPhys(m.Entry);
        ulong before = iop.InstructionsExecuted;
        bool returned = false;
        bool budget = false;

        // Step one outer Iop.Step(1) at a time so we stop as soon as PC lands on the
        // return sentinel. (A large Step(N) would keep fetching past $ra after jr ra.)
        // Note: Iop.Step(1) may retire 2 insns when the instruction is a branch (delay slot).
        while (true)
        {
            ulong done = iop.InstructionsExecuted - before;
            if (done >= maxInstructions)
            {
                budget = true;
                break;
            }
            if (iop.PC == ModuleReturnSentinel)
            {
                returned = true;
                break;
            }
            if (!iop.Running)
                break;

            iop.Step(1);

            if (iop.PC == ModuleReturnSentinel)
            {
                returned = true;
                break;
            }
        }

        ulong insns = iop.InstructionsExecuted - before;
        int modres = (int)iop.GetGpr(2);
        m.LastModRes = modres;
        m.LastEntryInstructions = insns;
        m.EntryExecuted = insns > 0;
        if (m.State != IopModuleState.Started)
        {
            m.State = IopModuleState.Started;
            m.StartOrder = _nextStartOrder++;
            ModuleStarts++;
        }
        ModuleEntryInstructions += insns;
        if (insns > 0)
            ModuleEntryRuns++;

        if (_pendingLiteralId == id)
            ClearPendingLiteralEntry();

        bool ok = insns > 0 && (returned || budget);
        return new ModuleRunResult
        {
            Success = ok,
            Message = returned
                ? $"returned to sentinel after {insns} insn"
                : budget
                    ? $"hit budget {maxInstructions} after {insns} insn"
                    : insns == 0 ? "no instructions executed" : $"stopped pc=0x{iop.PC:X8} after {insns} insn",
            ModuleId = id,
            Name = m.Name,
            EntryPc = entryPhys,
            FinalPc = iop.PC,
            InstructionsExecuted = insns,
            ModRes = modres,
            ReturnedToSentinel = returned,
            HitInstructionBudget = budget && !returned,
        };
    }

    /// <summary>
    /// MODLOAD StopModule / LOADFILE LF_F_MOD_STOP. Returns id on success, error if unknown.
    /// </summary>
    public int StopModule(int id, out int modres)
    {
        modres = 0;
        if (!_irxById.TryGetValue(id, out var m))
            return ModloadErrNotFound;
        if (m.State == IopModuleState.Stopped || m.State == IopModuleState.Registered
            || m.State == IopModuleState.Loaded)
        {
            // Already not running — success with zero modres (idempotent stop).
            m.State = IopModuleState.Stopped;
            modres = 0;
            return id;
        }
        m.State = IopModuleState.Stopped;
        modres = 0;
        ModuleStops++;
        return id;
    }

    /// <summary>
    /// MODLOAD UnloadModule / LOADFILE LF_F_MOD_UNLOAD. Removes table entry when allowed.
    /// System-resident (InitDefaults) modules refuse with <see cref="ModloadErrIllegal"/>.
    /// Auto-stops if still Started (client may omit an explicit stop).
    /// </summary>
    
    /// <summary>
    /// LOADFILE <c>LF_F_SEARCH_MOD_BY_ADDRESS</c>: find a relocated IRX whose load range
    /// contains <paramref name="addr"/> (EE-mapped 0x1Cxxxxxx or IOP physical).
    /// Name-only registrations (no real load base) are not searchable by address.
    /// </summary>
    public bool TryFindModuleByAddress(uint addr, out int id)
    {
        id = -1;
        static uint ToIopPhys(uint a)
        {
            if (a >= SystemMemory.IOP_RAM_BASE)
                a -= SystemMemory.IOP_RAM_BASE;
            return a & 0x1FFFFFu; // 2 MiB IOP window
        }
        uint phys = ToIopPhys(addr);
        // Prefer the tightest (highest LoadBase) match — modules are placed contiguously and a
        // fixed 64 KiB guess window can overlap the next module's base. Real LOADCORE uses exact
        // extents; HLE LoadedIrx only has Segments, so this is the least-surprising approximation.
        uint bestStart = 0;
        bool found = false;
        foreach (var kv in _irxById)
        {
            var irx = kv.Value;
            // LoadedIrx.LoadBase is EE-mapped (0x1Cxxxxxx) from IrxLoader; normalize both sides.
            uint start = ToIopPhys(irx.LoadBase);
            uint end = start + 0x10000u;
            if (phys >= start && phys < end && (!found || start >= bestStart))
            {
                bestStart = start;
                id = irx.Id;
                found = true;
            }
        }
        return found;
    }

    public int UnloadModule(int id)
    {
        if (!_irxById.TryGetValue(id, out var m))
            return ModloadErrNotFound;
        if (m.SystemResident)
            return ModloadErrIllegal;
        if (m.State == IopModuleState.Started)
            StopModule(id, out _);
        // Real resident modules (LastModRes==MODULE_RESIDENT_END with image) refuse unload.
        // HLE LoadIrx always records modres=0 (resident) because _start is not executed.
        // Name-only RegisterModule stubs remain unloadable so LF_F_MOD_UNLOAD is still useful.
        if (m.HasImage && m.LastModRes == ModuleResidentEnd)
            return ModloadErrIllegal;
        _irxById.Remove(id);
        _modules.Remove(m.Name);
        ModuleUnloads++;
        return id;
    }

    /// <summary>
    /// MODLOAD IsIllegalBootDevice (FUN_00000bb8): rejects mc*/hd*/net*/dev* device prefixes.
    /// LOADFILE refuses LoadModule on these with 0xFFFFFF37.
    /// </summary>
    public static bool IsIllegalBootDevice(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        // Strip whitespace like the real skip loop.
        int i = 0;
        while (i < path.Length && (path[i] == ' ' || path[i] == '\t')) i++;
        if (i >= path.Length) return false;
        // Device name before ':' if present, else whole string prefix check.
        int colon = path.IndexOf(':', i);
        string head = (colon > i ? path[i..colon] : path[i..]).ToLowerInvariant();
        while (head.Length > 0 && char.IsDigit(head[^1]))
            head = head[..^1];
        return head is "mc" or "hd" or "hdd" or "net" or "dev";
    }

    // Real cross-module import/export linking registry (IrxLoader.ScanExports/LinkImports,
    // ground-truthed against the real BIOS LOADCORE module — see IrxLoader.cs's own doc comment
    // on the export/import table format). Keyed by library name; a module registered later with
    // the same name replaces the earlier entry, matching real LOADCORE's own "last one wins for
    // a given name" linked-list-prepend behavior (DAT_00001c70 in the real decompile).
    private readonly Dictionary<string, IrxLoader.ExportTable> _exportRegistry = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IrxLoader.ExportTable> ExportRegistry => _exportRegistry;
    public ulong ImportsResolved { get; private set; }
    public ulong ImportsUnresolved { get; private set; }

    /// <summary>Load IRX ELF bytes into IOP RAM, register module name, and perform real
    /// cross-module linking: register any libraries this module exports, then resolve this
    /// module's own unresolved imports against every library registered so far (real boot order
    /// matters exactly as on real hardware — a module can only call into libraries loaded before
    /// it). Matches MODLOAD LoadStartModule: image load then implicit start (modres resident).</summary>
    public IrxLoader.LoadResult LoadIrx(byte[] elf, SystemMemory mem, string? nameOverride = null)
    {
        uint baseLocal = _nextIopBase;
        var result = IrxLoader.Load(elf, mem, baseLocal);
        if (!result.Success)
            return result;

        string name = !string.IsNullOrEmpty(nameOverride)
            ? nameOverride!
            : (string.IsNullOrEmpty(result.ModuleName) ? $"IRX{_nextModuleId}" : result.ModuleName);
        name = NormalizeName(name);

        // Real cross-module linking: scan this module's own real loaded extent (result.Size,
        // not a fixed guess) for both directions (it may both export libraries and import from
        // earlier ones). Real BIOS modules vary a lot in size — e.g. THREADMAN's real loaded
        // size is 0x6C94 (~27KB), confirmed live via `load-irx --scan-exports` against the real
        // extracted module, well past a fixed 16KB window.
        uint moduleSize = Math.Max(result.Size, 0x1000u); // sane floor for the legacy no-Size path

        int id;
        if (_modules.TryGetValue(name, out id) && _irxById.TryGetValue(id, out var prior))
        {
            // Upgrade existing name registration with a real image (keep id).
            prior.Entry = result.Entry;
            prior.Gp = result.Gp;
            prior.LoadBase = result.LoadBase;
            prior.Segments = result.Segments;
            prior.Size = moduleSize;
            prior.HasImage = true;
            prior.State = IopModuleState.Loaded;
            prior.EntryExecuted = false;
            prior.LastEntryInstructions = 0;
        }
        else
        {
            id = _nextModuleId++;
            _modules[name] = id;
            _irxById[id] = new LoadedIrx
            {
                Id = id,
                Name = name,
                Entry = result.Entry,
                Gp = result.Gp,
                LoadBase = result.LoadBase,
                Segments = result.Segments,
                Size = moduleSize,
                HasImage = true,
                State = IopModuleState.Loaded,
                SystemResident = false,
            };
        }

        uint scanStart = result.LoadBase;
        uint scanEnd = result.LoadBase + moduleSize;
        foreach (var lib in IrxLoader.ScanExports(mem, scanStart, scanEnd))
            _exportRegistry[lib.Name] = lib;
        var (resolved, unresolved) = IrxLoader.LinkImports(mem, scanStart, scanEnd, _exportRegistry);
        ImportsResolved += (ulong)resolved;
        ImportsUnresolved += (ulong)unresolved;

        // MODLOAD LoadStartModule: after load, real hardware runs _start. DetPS2 still soft-marks
        // Started for registry/LOADFILE compatibility (games probes must see the module as up).
        // R3000 entry is armed for scheduler quanta (always unless DETPS2_FORCE_HLE_IOP=1).
        StartModule(id, out _);

        if (IsLiteralIrxEnabled && result.Entry != 0)
        {
            _pendingLiteralId = id;
            _pendingLiteralEntryPhys = ToIopPhys(result.Entry);
            _pendingLiteralGp = result.Gp;
            _pendingLiteralName = name;
        }

        // Advance base for next load, rounded up to a 16KB boundary past this module's real
        // extent — previously a fixed +0x4000 regardless of real size, which silently let a
        // module bigger than 16KB (confirmed real: THREADMAN) have its own tail overwritten by
        // whatever loaded right after it.
        uint afterModule = baseLocal + moduleSize;
        _nextIopBase = (afterModule + 0x3FFFu) & ~0x3FFFu;
        if (_nextIopBase <= baseLocal) _nextIopBase = baseLocal + 0x4000; // overflow guard
        if (_nextIopBase > 0x00180000) _nextIopBase = IrxLoader.DefaultLoadBase;
        IrxLoads++;
        return new IrxLoader.LoadResult
        {
            Success = true,
            Message = result.Message,
            Entry = result.Entry,
            Gp = result.Gp,
            LoadBase = result.LoadBase,
            Segments = result.Segments,
            Size = moduleSize,
            ModuleName = name
        };
    }

    private static string NormalizeName(string name)
    {
        name = name.Trim();
        int slash = name.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) name = name[(slash + 1)..];
        int colon = name.IndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        int semi = name.IndexOf(';');
        if (semi >= 0) name = name[..semi];
        int dot = name.IndexOf('.');
        if (dot > 0) name = name[..dot];
        return name.ToUpperInvariant();
    }

    public SifRpcPacket Dispatch(SifRpcPacket pkt, SystemMemory mem, PadInput pad, Cdvd cdvd)
    {
        RpcHandled++;
        uint result = pkt.Cmd switch
        {
            SifRpcCmd.Open => DoOpen(pkt, mem),
            SifRpcCmd.Close => DoClose(pkt),
            SifRpcCmd.Read => DoRead(pkt, mem),
            SifRpcCmd.Write => DoWrite(pkt, mem),
            SifRpcCmd.Seek => DoSeek(pkt),
            SifRpcCmd.PadState => DoPad(pkt, mem, pad),
            SifRpcCmd.CdvdRead => DoCdvd(pkt, mem, cdvd),
            SifRpcCmd.LoadModule => DoLoadModule(pkt, mem),
            SifRpcCmd.GetModule => DoGetModule(pkt, mem),
            SifRpcCmd.LoadIrx => DoLoadIrx(pkt, mem),
            SifRpcCmd.MemCard => DoMemCard(pkt, mem),
            _ => unchecked((uint)-1)
        };
        return pkt.WithResult(result);
    }

    private uint DoOpen(SifRpcPacket pkt, SystemMemory mem)
    {
        string path = ReadCString(mem, pkt.EeBuffer, 256);
        if (string.IsNullOrEmpty(path))
            return unchecked((uint)-1);
        int fd = _nextFd++;
        _openFiles[fd] = path;

        var hf = new OpenHostFile { Path = path, Position = 0 };
        // Resolve against mounted ISO when possible
        if (_discVolume != null)
        {
            string norm = NormalizeDiscPath(path);
            byte[]? data = null;
            // Small files: load whole; large SFD: still load if under 16MB for logo/ESRB
            var entry = FindDiscEntry(norm);
            if (entry != null && !entry.IsDirectory && entry.Size > 0)
            {
                if (entry.Size <= 16 * 1024 * 1024)
                    data = Iso9660.ReadFile(_discVolume, entry.Path);
                hf.Lba = entry.ExtentLba;
                hf.Size = entry.Size;
            }
            hf.Data = data;
        }
        _hostFiles[fd] = hf;
        return (uint)fd;
    }

    private uint DoClose(SifRpcPacket pkt)
    {
        int fd = (int)pkt.Size;
        _openFiles.Remove(fd);
        _hostFiles.Remove(fd);
        return 0;
    }

    private uint DoRead(SifRpcPacket pkt, SystemMemory mem)
    {
        // Convention: Size = byte count, Result field often holds fd in some ABIs;
        // Det ABI uses Size as length and we look up last open — prefer fd in Result.
        int fd = (int)pkt.Result;
        if (!_hostFiles.TryGetValue(fd, out var hf))
        {
            // Fallback: Size low 16 bits as fd when Result empty (test compat)
            if (!_hostFiles.TryGetValue((int)(pkt.Size >> 16), out hf))
            {
                // Zero-fill legacy behavior for unknown fd
                uint n0 = Math.Min(pkt.Size, 0x100000);
                for (uint i = 0; i < n0; i++)
                    mem.Write8(pkt.EeBuffer + i, 0);
                return n0;
            }
        }

        uint want = Math.Min(pkt.Size & 0xFFFFFFu, 0x200000u);
        if (want == 0) want = Math.Min(pkt.Size, 0x200000u);

        if (hf.Data != null)
        {
            int avail = Math.Max(0, hf.Data.Length - hf.Position);
            int n = (int)Math.Min(want, (uint)avail);
            for (int i = 0; i < n; i++)
                mem.Write8(pkt.EeBuffer + (uint)i, hf.Data[hf.Position + i]);
            hf.Position += n;
            DiscBytesRead += (uint)n;
            return (uint)n;
        }

        // Stream from disc by LBA for large files
        if (_discVolume?.Disc != null && hf.Size > 0 && hf.Lba != 0)
        {
            long off = (long)hf.Lba * Iso9660.SectorSize + hf.BaseOffset + hf.Position;
            int n = (int)Math.Min(want, (uint)Math.Max(0, (int)hf.Size - hf.Position));
            if (n <= 0) return 0;
            byte[] buf = new byte[n];
            int got = _discVolume.Disc.ReadAt(off, buf);
            for (int i = 0; i < got; i++)
                mem.Write8(pkt.EeBuffer + (uint)i, buf[i]);
            hf.Position += got;
            DiscBytesRead += (uint)got;
            return (uint)got;
        }

        // No data — return zeros but full length so callers don't infinite-retry short reads
        for (uint i = 0; i < want; i++)
            mem.Write8(pkt.EeBuffer + i, 0);
        return want;
    }

    private uint DoWrite(SifRpcPacket pkt, SystemMemory mem) => Math.Min(pkt.Size, 0x100000);

    private uint DoSeek(SifRpcPacket pkt)
    {
        int fd = (int)pkt.Result;
        if (_hostFiles.TryGetValue(fd, out var hf))
        {
            // Size encodes offset; high path: absolute seek within file
            int max = (int)(hf.Size != 0 ? hf.Size : (uint)(hf.Data?.Length ?? 0));
            hf.Position = Math.Clamp((int)pkt.Size, 0, Math.Max(0, max));
            return (uint)hf.Position;
        }
        return pkt.Size;
    }

    // ---- Public FILEIO ops used by RealSifRpc sid=0x80000001 ----

    /// <summary>fio open by path string; returns IOMAN slot 0..15, or a real negative errno
    /// (-24 EMFILE, -2 ENOENT, -9 EBADF, -19 ENODEV on later ops).</summary>
    /// <summary>Size of an open FILEIO fd (host Data length or ISO Size); false if bad fd.</summary>
    public bool TryGetOpenFileSize(int fd, out uint size)
    {
        size = 0;
        if (!_hostFiles.TryGetValue(fd, out var hf)) return false;
        size = hf.Size != 0 ? hf.Size : (uint)(hf.Data?.Length ?? 0);
        return true;
    }

    /// <summary>ISO extent LBA for a streamed open file; false if not disc-backed.</summary>
    public bool TryGetOpenFileLba(int fd, out uint lba)
    {
        lba = 0;
        if (!_hostFiles.TryGetValue(fd, out var hf)) return false;
        if (hf.Lba == 0) return false;
        lba = hf.Lba;
        return true;
    }

    /// <summary>
    /// Host-side byte read from an open fd (for TOC parse etc.). Does not advance the fd
    /// position permanently — saves/restores <see cref="OpenHostFile.Position"/>.
    /// </summary>
    public bool TryReadOpenFileBytes(int fd, int offset, int count, out byte[]? data)
    {
        data = null;
        if (!_hostFiles.TryGetValue(fd, out var hf) || count <= 0) return false;
        count = Math.Min(count, 2 * 1024 * 1024);
        var buf = new byte[count];
        int saved = hf.Position;
        try
        {
            if (hf.Data != null)
            {
                if (offset < 0 || offset >= hf.Data.Length) return false;
                int n = Math.Min(count, hf.Data.Length - offset);
                Buffer.BlockCopy(hf.Data, offset, buf, 0, n);
                if (n < count) Array.Resize(ref buf, n);
                data = buf;
                return n > 0;
            }
            if (_discVolume?.Disc != null && hf.Size > 0 && hf.Lba != 0)
            {
                long off = (long)hf.Lba * Iso9660.SectorSize + hf.BaseOffset + offset;
                int n = (int)Math.Min(count, Math.Max(0, (int)hf.Size - offset));
                if (n <= 0) return false;
                if (n != buf.Length) buf = new byte[n];
                int got = _discVolume.Disc.ReadAt(off, buf);
                if (got < n) Array.Resize(ref buf, got);
                data = buf;
                return got > 0;
            }
            return false;
        }
        finally
        {
            hf.Position = saved;
        }
    }

    /// <summary>True when open fd serves from LBA streaming (no in-memory Data preload).</summary>
    public bool OpenFileIsStreamed(int fd) =>
        _hostFiles.TryGetValue(fd, out var hf) && hf.Data == null && hf.Lba != 0 && hf.Size > 0;

    /// <summary>
    /// Open a virtual stream backed by an absolute byte offset within the mounted disc image
    /// (e.g. PS2.RKV entry: base LBA of archive + byte offset). Used by GOE/RKV TOC HLE.
    /// </summary>
    public int FileOpenVirtualStream(string path, uint discByteOffset, uint size)
    {
        if (_discVolume?.Disc == null || size == 0)
            return IoManErrnoNoEntry;
        int fd = AllocIoManFd();
        if (fd < 0) return IoManErrnoOutOfDescriptors;
        // Lba = sector of archive byte; BaseOffset = remainder so seek(0) still hits entry start.
        uint lba = discByteOffset / Iso9660.SectorSize;
        int baseOff = (int)(discByteOffset % Iso9660.SectorSize);
        var hf = new OpenHostFile
        {
            Path = path,
            Data = null,
            Lba = lba,
            BaseOffset = baseOff,
            Size = size,
            Position = 0,
        };
        _openFiles[fd] = path;
        _hostFiles[fd] = hf;
        return fd;
    }

    /// <summary>Open an in-memory stub (empty or caller-provided) so ENOENT does not abort boot.</summary>
    public int FileOpenMemoryStub(string path, byte[] data)
    {
        int fd = AllocIoManFd();
        if (fd < 0) return IoManErrnoOutOfDescriptors;
        var hf = new OpenHostFile
        {
            Path = path,
            Data = data ?? Array.Empty<byte>(),
            Size = (uint)(data?.Length ?? 0),
            Position = 0,
            Lba = 0,
        };
        _openFiles[fd] = path;
        _hostFiles[fd] = hf;
        return fd;
    }

    public int FileOpen(string path, int mode = 0)
    {
        if (string.IsNullOrEmpty(path)) return IoManErrnoNoEntry;
        // IOMAN FUN_00000d28: colon path with unknown device → ENODEV (-19).
        // Relative (no colon) paths stay allowed for disc-relative probes.
        // Only enforce when the registry has been seeded (DeviceCount > 0).
        if (_ioSystem != null && _ioSystem.DeviceCount > 0
            && path.IndexOf(':') >= 0 && !_ioSystem.IsKnownDevicePath(path))
            return IoManErrnoNoDevice;
        int fd = AllocIoManFd();
        if (fd < 0) return IoManErrnoOutOfDescriptors;

        var hf = new OpenHostFile { Path = path, Position = 0 };
        bool nonDisc = IsNonDiscDevicePath(path);

        // Retail leftovers often keep host0:~/bin/… / host:… paths from SN ProView builds
        // (Whiplash SLUS_206.84, others). When a mounted ISO has a matching basename/path,
        // serve real disc bytes instead of an empty host stub so boot can proceed without
        // a title-only path rewrite. Prefer exact disc open for cdrom* as before.
        if (_discVolume != null && nonDisc && TryMapHostPathToDisc(path, out var hostMapped))
        {
            nonDisc = false;
            path = hostMapped;
            hf.Path = path;
        }

        // ROMDRV: rom0:/rom: file content from bound BIOS ROMDIR (or synthetic empty stubs).
        if (TryResolveRom0Path(path, out string? romName))
        {
            byte[]? romData = ResolveRom0Content(romName!);
            if (romData != null)
            {
                hf.Data = romData;
                hf.Size = (uint)romData.Length;
                Rom0BytesServed += (uint)romData.Length;
            }
            else if (RomBiosBound)
            {
                // Real ROM image present but name not in ROMDIR — commercial probes branch on ENOENT.
                return IoManErrnoNoEntry;
            }
            else
            {
                // No BIOS bound: empty stub so host/boot probes that open rom0:FOO still succeed.
                hf.Data = Array.Empty<byte>();
                hf.Size = 0;
            }
        }
        else if (_discVolume != null && !nonDisc)
        {
            string norm = NormalizeDiscPath(path);
            var entry = FindDiscEntry(norm) ?? FindDiscEntryAny(norm);
            if (entry != null && !entry.IsDirectory)
            {
                if (entry.Size > 0 && entry.Size <= 16 * 1024 * 1024)
                    hf.Data = Iso9660.ReadFile(_discVolume, entry.Path);
                else if (entry.Size == 0)
                    hf.Data = Array.Empty<byte>();
                hf.Lba = entry.ExtentLba;
                hf.Size = entry.Size;
            }
            else if (entry != null && entry.IsDirectory)
            {
                // Opening a directory via sceOpen is rejected by most cdvd drivers.
                return IoManErrnoNoEntry;
            }
            else if ((mode & 0x200) != 0) // FIO_O_CREAT
            {
                hf.Data = Array.Empty<byte>();
            }
            else if (!_discVolume.Files.Exists(f => !f.IsDirectory))
            {
                // Empty volume — allow open for boot probes (no real content).
            }
            else
            {
                // Missing path on a real mounted disc — commercial titles branch on this.
                return IoManErrnoNoEntry;
            }
        }
        else if (nonDisc || _discVolume == null)
        {
            // host:/mc0: probes, or no disc: empty file so open succeeds.
            hf.Data ??= Array.Empty<byte>();
        }

        // Prefer prior FILEIO write overlay (BO2 GAME.ERG RDWR config) over stock ISO bytes.
        if (_hostWriteOverlay.TryGetValue(NormalizeOverlayKey(path), out byte[]? overlay)
            && overlay is { Length: > 0 })
        {
            hf.Data = (byte[])overlay.Clone();
            hf.Size = (uint)overlay.Length;
            hf.Position = 0;
        }

        _openFiles[fd] = path;
        _hostFiles[fd] = hf;
        _ = mode;
        return fd;
    }

    /// <summary>IOMAN sceClose: free slot; EBADF (-9) if invalid (FUN_000003a4 / FUN_00000c3c).</summary>
    public int FileClose(int fd)
    {
        if (fd < 0 || fd >= IoManMaxDescriptors)
            return IoManErrnoBadFile;
        bool hadFile = _hostFiles.Remove(fd);
        bool hadDir = _openDirs.Remove(fd);
        _openFiles.Remove(fd);
        if (!hadFile && !hadDir)
            return IoManErrnoBadFile;
        return 0;
    }

    /// <summary>fio read into EE buffer. Invalid fd → EBADF (does not zero-fill — that legacy
    /// path remains only on the synthetic SifRpcCmd.Read dispatcher).</summary>
    public int FileRead(SystemMemory mem, int fd, uint buf, uint size)
    {
        if (!_hostFiles.TryGetValue(fd, out var hf))
            return IoManErrnoBadFile;
        if (buf == 0) return 0;
        uint want = Math.Min(size, 0x200000u);
        if (want == 0) return 0;

        if (hf.Data != null)
        {
            int avail = Math.Max(0, hf.Data.Length - hf.Position);
            int n = (int)Math.Min(want, (uint)avail);
            for (int i = 0; i < n; i++)
                mem.Write8(buf + (uint)i, hf.Data[hf.Position + i]);
            hf.Position += n;
            DiscBytesRead += (uint)n;
            return n;
        }

        if (_discVolume?.Disc != null && hf.Size > 0 && hf.Lba != 0)
        {
            long off = (long)hf.Lba * Iso9660.SectorSize + hf.BaseOffset + hf.Position;
            int n = (int)Math.Min(want, (uint)Math.Max(0, (int)hf.Size - hf.Position));
            if (n <= 0) return 0;
            byte[] tmp = new byte[n];
            int got = _discVolume.Disc.ReadAt(off, tmp);
            for (int i = 0; i < got; i++)
                mem.Write8(buf + (uint)i, tmp[i]);
            hf.Position += got;
            DiscBytesRead += (uint)got;
            return got;
        }

        // Empty host file / no backing — EOF
        return 0;
    }

    /// <summary>
    /// Host-side overlay for files written via FILEIO (e.g. BO2 GAME.ERG config lines).
    /// Survives close/re-open of the same path so RDWR config updates are visible on re-read.
    /// </summary>
    private readonly Dictionary<string, byte[]> _hostWriteOverlay =
        new(StringComparer.OrdinalIgnoreCase);

    public int FileWrite(SystemMemory mem, int fd, uint buf, uint size)
    {
        if (!_hostFiles.TryGetValue(fd, out var hf))
            return IoManErrnoBadFile;
        // STDIO tty/stderr: route bytes to the optional IOMAN/STDIO sink (non-fatal log).
        if (IsStdioPath(hf.Path) && mem != null && buf != 0 && size > 0)
        {
            int n = (int)Math.Min(size, 0x1000u);
            if (_ioSystem != null)
                return _ioSystem.StdioWriteBytes(mem, buf, (uint)n);
            // Fallback: swallow as successful write (no sink bound).
            return n;
        }
        // Persist writes into the open-file host buffer + path overlay.
        // Blood Omen 2 / SN FILEIO opens GAME.ERG RDWR and writes short config lines
        // (usebigfile / path keys); a pure success-size stub left re-reads seeing stock
        // ISO bytes and blocked the PRECODE/CODE bigfile path after GOE bind.
        int nWrite = (int)Math.Min(size, 0x100000u);
        if (nWrite <= 0 || mem == null || buf == 0)
            return Math.Max(0, nWrite);

        int need = hf.Position + nWrite;
        if (need > 4 * 1024 * 1024)
            nWrite = Math.Max(0, 4 * 1024 * 1024 - hf.Position);
        if (nWrite <= 0)
            return 0;

        if (hf.Data == null)
            hf.Data = new byte[Math.Max(need, 256)];
        else if (hf.Data.Length < need)
        {
            int grow = Math.Max(need, hf.Data.Length * 2);
            if (grow > 4 * 1024 * 1024) grow = 4 * 1024 * 1024;
            Array.Resize(ref hf.Data, grow);
        }
        for (int i = 0; i < nWrite; i++)
            hf.Data[hf.Position + i] = mem.Read8(buf + (uint)i);
        hf.Position += nWrite;
        if (hf.Position > (int)hf.Size)
            hf.Size = (uint)hf.Position;

        // Snapshot overlay for close/re-open of the same path (normalize device prefix).
        if (!string.IsNullOrEmpty(hf.Path) && hf.Data != null && hf.Size > 0)
        {
            int copyLen = (int)Math.Min(hf.Size, (uint)hf.Data.Length);
            var snap = new byte[copyLen];
            Buffer.BlockCopy(hf.Data, 0, snap, 0, copyLen);
            _hostWriteOverlay[NormalizeOverlayKey(hf.Path)] = snap;
        }
        return nWrite;
    }

    private static string NormalizeOverlayKey(string path)
    {
        string p = path.Replace('/', '\\').Trim();
        int semi = p.IndexOf(';');
        if (semi > 0) p = p[..semi];
        return p;
    }

    private static bool IsStdioPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        int colon = path.IndexOf(':');
        string dev = colon > 0 ? path[..colon] : path;
        while (dev.Length > 0 && char.IsDigit(dev[^1]))
            dev = dev[..^1];
        return dev.Equals("tty", StringComparison.OrdinalIgnoreCase)
            || dev.Equals("stderr", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Optional bind to <see cref="IopSystemHost"/> for IOMAN AddDrv/DelDrv + STDIO write
    /// routing. Called from <see cref="Ps2System"/> construction.
    /// </summary>
    public void BindIopSystem(IopSystemHost? io) => _ioSystem = io;

    /// <summary>IOMAN AddDrv via bound system host (or local name-only fallback).</summary>
    public int AddDrv(string name)
    {
        if (_ioSystem != null)
            return _ioSystem.AddDrv(name);
        // Fallback without IopSystem: accept name so FILEIO fno 15 does not fail hard.
        if (string.IsNullOrWhiteSpace(name)) return -1;
        return 0;
    }

    /// <summary>IOMAN DelDrv via bound system host.</summary>
    public int DelDrv(string name)
    {
        if (_ioSystem != null)
            return _ioSystem.DelDrv(name);
        if (string.IsNullOrWhiteSpace(name)) return -1;
        return 0;
    }

    /// <summary>fio lseek. Invalid fd → EBADF; whence ∉ {0,1,2} → EINVAL (IOMAN FUN_000001bc).</summary>
    public int FileSeek(int fd, int offset, int whence)
    {
        if (!_hostFiles.TryGetValue(fd, out var hf))
            return IoManErrnoBadFile;
        if (whence < 0 || whence > 2)
            return IoManErrnoInvalid;
        int max = (int)(hf.Size != 0 ? hf.Size : (uint)(hf.Data?.Length ?? 0));
        int pos = whence switch
        {
            1 => hf.Position + offset, // SEEK_CUR
            2 => max + offset,         // SEEK_END
            _ => offset               // SEEK_SET
        };
        hf.Position = Math.Clamp(pos, 0, Math.Max(0, max));
        return hf.Position;
    }

    /// <summary>
    /// fio getstat. Writes <c>io_stat_t</c> (ps2sdk io_common.h) into <paramref name="statAddr"/>
    /// when non-zero: +0 mode, +4 attr, +8 size, +0x0C..+0x23 times, +0x24 hisize.
    /// </summary>
    public int FileGetStat(SystemMemory mem, string path, uint statAddr)
    {
        if (string.IsNullOrEmpty(path)) return IoManErrnoNoEntry;
        uint mode = FioSIrusr | FioSIwusr | FioSIxusr;
        uint size = 0;

        if (TryResolveRom0Path(path, out string? romName))
        {
            if (RomBiosBound)
            {
                if (_romBios != null && RomdirExtractor.TryFindEntry(_romBios, romName!, out var re))
                {
                    mode |= FioSIfReg;
                    size = re.Size;
                }
                else
                    return IoManErrnoNoEntry;
            }
            else
            {
                // No BIOS: synthetic empty regular file for probes.
                mode |= FioSIfReg;
                size = 0;
            }
        }
        else
        {
            bool nonDisc = IsNonDiscDevicePath(path);
            string norm = NormalizeDiscPath(path);
            var entry = nonDisc ? null : FindDiscEntryAny(norm);
            if (entry != null)
            {
                mode |= entry.IsDirectory ? FioSIfDir : FioSIfReg;
                size = entry.Size;
            }
            else if (_discVolume == null || nonDisc)
            {
                // No disc or host/mc probe: claim regular empty file so probes succeed.
                mode |= FioSIfReg;
            }
            else
                return IoManErrnoNoEntry;
        }

        if (statAddr != 0)
        {
            mem.Write32(statAddr + 0, mode);
            mem.Write32(statAddr + 4, 0); // attr
            mem.Write32(statAddr + 8, size);
            // ctime/atime/mtime 8 bytes each at +0x0C,+0x14,+0x1C — leave zero
            mem.Write32(statAddr + 0x24, 0); // hisize (io_stat_t ends at 0x28)
        }
        return 0;
    }

    /// <summary>IOMAN sceDopen — same 16-slot allocator as sceOpen (FUN_000004c0 / FUN_00000b98).</summary>
    public int DirOpen(string path)
    {
        int dfd = AllocIoManFd();
        if (dfd < 0) return IoManErrnoOutOfDescriptors;
        string raw = path ?? "";
        var list = new List<Iso9660.FileEntry>();

        // ROMDRV: dopen("rom0:") / dopen("rom0:\\") lists ROMDIR entry names when BIOS is bound.
        if (TryResolveRom0Path(raw, out string? romRest) &&
            (string.IsNullOrEmpty(romRest) || romRest is "/" or "\\" or "."))
        {
            if (_romdirCache != null)
            {
                foreach (var e in _romdirCache)
                {
                    if (string.IsNullOrEmpty(e.Name) || e.Name == "-") continue;
                    list.Add(new Iso9660.FileEntry
                    {
                        Name = e.Name,
                        Path = e.Name,
                        Size = e.Size,
                        IsDirectory = false
                    });
                }
            }
            _openDirs[dfd] = new OpenDir { Path = "rom0:", Entries = list, Index = 0 };
            return dfd;
        }

        string norm = NormalizeDiscPath(raw);
        if (norm.Length == 0) norm = "";
        if (_discVolume != null)
        {
            string prefix = norm.TrimEnd('/');
            foreach (var f in _discVolume.Files)
            {
                string p = f.Path.Replace('\\', '/').ToUpperInvariant();
                if (prefix.Length == 0)
                {
                    // Root: only top-level names (no slash, or first segment)
                    int slash = p.IndexOf('/');
                    if (slash < 0) list.Add(f);
                    else
                    {
                        string top = p[..slash];
                        if (!list.Exists(e => string.Equals(e.Name, top, StringComparison.OrdinalIgnoreCase)))
                            list.Add(new Iso9660.FileEntry { Name = top, Path = top, IsDirectory = true });
                    }
                }
                else if (p == prefix || p.StartsWith(prefix + "/", StringComparison.Ordinal))
                {
                    string rest = p == prefix ? f.Name : p[(prefix.Length + 1)..];
                    int slash = rest.IndexOf('/');
                    if (slash < 0)
                        list.Add(f);
                    else
                    {
                        string child = rest[..slash];
                        if (!list.Exists(e => string.Equals(e.Name, child, StringComparison.OrdinalIgnoreCase)))
                            list.Add(new Iso9660.FileEntry { Name = child, Path = prefix + "/" + child, IsDirectory = true });
                    }
                }
            }
        }
        _openDirs[dfd] = new OpenDir { Path = norm, Entries = list, Index = 0 };
        return dfd;
    }

    public int DirClose(int dfd)
    {
        if (dfd < 0 || dfd >= IoManMaxDescriptors)
            return IoManErrnoBadFile;
        return _openDirs.Remove(dfd) ? 0 : IoManErrnoBadFile;
    }

    /// <summary>fio dread: write <c>io_dirent_t</c> (stat @0, name @+0x28) into
    /// <paramref name="direntAddr"/>. Returns 1 on entry, -1 at end / EBADF.</summary>
    public int DirRead(SystemMemory mem, int dfd, uint direntAddr)
    {
        if (!_openDirs.TryGetValue(dfd, out var dir)) return IoManErrnoBadFile;
        if (dir.Index >= dir.Entries.Count) return -1; // end of directory
        var e = dir.Entries[dir.Index++];
        if (direntAddr != 0)
        {
            // io_dirent_t (ps2sdk io_common.h): io_stat_t (0x28) + char name[256] + privdata*
            uint mode = FioSIrusr | FioSIwusr | FioSIxusr | (e.IsDirectory ? FioSIfDir : FioSIfReg);
            mem.Write32(direntAddr + 0, mode);
            mem.Write32(direntAddr + 4, 0);
            mem.Write32(direntAddr + 8, e.Size);
            mem.Write32(direntAddr + 0x24, 0); // hisize
            WriteCString(mem, direntAddr + 0x28, e.Name, 255);
        }
        return 1;
    }

    public int FileRemove(string path)
    {
        // Read-only ISO: pretend success for temp/host paths, fail for disc files
        if (_discVolume != null && !IsNonDiscDevicePath(path ?? "") &&
            FindDiscEntryAny(NormalizeDiscPath(path ?? "")) != null)
            return IoManErrnoNoEntry;
        return 0;
    }

    /// <summary>Load IRX bytes from mounted disc by path (LOADFILE path loads).</summary>
    public byte[]? ReadDiscFileBytes(string path, int maxBytes = 0x100000)
    {
        if (_discVolume == null || string.IsNullOrEmpty(path)) return null;
        string norm = NormalizeDiscPath(path);
        var entry = FindDiscEntryAny(norm);
        if (entry == null || entry.IsDirectory || entry.Size == 0) return null;
        try
        {
            byte[]? full = Iso9660.ReadFile(_discVolume, entry.Path);
            if (full == null) return null;
            if (full.Length <= maxBytes) return full;
            var cut = new byte[maxBytes];
            Buffer.BlockCopy(full, 0, cut, 0, maxBytes);
            return cut;
        }
        catch { return null; }
    }

    private Iso9660.FileEntry? FindDiscEntryAny(string normPath)
    {
        var file = FindDiscEntry(normPath);
        if (file != null) return file;
        if (_discVolume == null) return null;
        foreach (var f in _discVolume.Files)
        {
            string p = f.Path.Replace('\\', '/').ToUpperInvariant();
            string n = f.Name.ToUpperInvariant();
            if (p == normPath || n == normPath || p.EndsWith("/" + normPath, StringComparison.Ordinal))
                return f;
        }
        return null;
    }

    private static void WriteCString(SystemMemory mem, uint addr, string s, int max)
    {
        int n = Math.Min(s.Length, max);
        for (int i = 0; i < n; i++)
            mem.Write8(addr + (uint)i, (byte)s[i]);
        mem.Write8(addr + (uint)n, 0);
    }

    private Iso9660.FileEntry? FindDiscEntry(string normPath)
    {
        if (_discVolume == null) return null;
        string want = normPath.Replace('\\', '/').ToUpperInvariant();
        foreach (var f in _discVolume.Files)
        {
            if (f.IsDirectory) continue;
            string p = f.Path.Replace('\\', '/').ToUpperInvariant();
            string n = f.Name.ToUpperInvariant();
            if (p == want || n == want || p.EndsWith("/" + want, StringComparison.Ordinal) ||
                p.EndsWith(want, StringComparison.Ordinal))
                return f;
        }
        // basename match
        string baseName = Path.GetFileName(want);
        foreach (var f in _discVolume.Files)
        {
            if (f.IsDirectory) continue;
            if (string.Equals(f.Name, baseName, StringComparison.OrdinalIgnoreCase))
                return f;
        }
        // ISO 9660 Level-1 short-name segment match (RESOURCES↔RESOUR~1, …).
        // Blood Omen 2 retail has no Joliet; long paths must alias onto 8.3 names.
        if (want.IndexOf('/') >= 0 || want.IndexOf('~') < 0)
        {
            string[] wantSegs = want.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var f in _discVolume.Files)
            {
                if (f.IsDirectory) continue;
                string p = f.Path.Replace('\\', '/').ToUpperInvariant();
                string[] haveSegs = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (haveSegs.Length != wantSegs.Length) continue;
                bool ok = true;
                for (int i = 0; i < wantSegs.Length; i++)
                {
                    if (!IsoSegmentMatch(wantSegs[i], haveSegs[i]))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) return f;
            }
        }
        return null;
    }

    /// <summary>Match a long path segment to an ISO 8.3 name (e.g. RESOURCES vs RESOUR~1).</summary>
    private static bool IsoSegmentMatch(string want, string have)
    {
        if (want == have) return true;
        // Strip version ";1" if present on either side.
        int sw = want.IndexOf(';'); if (sw >= 0) want = want[..sw];
        int sh = have.IndexOf(';'); if (sh >= 0) have = have[..sh];
        if (want == have) return true;
        // 8.3 with tilde: PREFIX~N[.EXT]
        int tilde = have.IndexOf('~');
        if (tilde > 0)
        {
            string prefix = have[..tilde];
            // Extension on have
            int dotH = have.LastIndexOf('.');
            int dotW = want.LastIndexOf('.');
            if (dotH > tilde && dotW > 0)
            {
                string extH = have[(dotH + 1)..];
                string extW = want[(dotW + 1)..];
                string nameW = want[..dotW];
                return nameW.StartsWith(prefix, StringComparison.Ordinal) && extH == extW;
            }
            return want.StartsWith(prefix, StringComparison.Ordinal);
        }
        return false;
    }

    private static string NormalizeDiscPath(string path)
    {
        path = path.Trim();
        if (path.StartsWith("cdrom0:", StringComparison.OrdinalIgnoreCase))
            path = path["cdrom0:".Length..];
        if (path.StartsWith("cdrom:", StringComparison.OrdinalIgnoreCase))
            path = path["cdrom:".Length..];
        // Strip host0:~/… / host:… so FindDiscEntryAny can basename-match disc IRX/IMG.
        if (path.StartsWith("host0:", StringComparison.OrdinalIgnoreCase))
            path = path["host0:".Length..];
        else if (path.StartsWith("host:", StringComparison.OrdinalIgnoreCase))
            path = path["host:".Length..];
        // Drop home-prefix leftovers from SN ProView paths (host0:~/bin/FOO.IRX).
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = path[2..];
        path = path.TrimStart('\\', '/');
        int semi = path.IndexOf(';');
        if (semi >= 0) path = path[..semi];
        return path.Replace('\\', '/').ToUpperInvariant();
    }

    /// <summary>
    /// Map SN ProView-style <c>host0:~/bin/FOO</c> / <c>host:…</c> paths onto a mounted ISO
    /// entry when one exists. Returns a <c>cdrom0:</c>-shaped path for the normal disc open
    /// path. No-op when the disc has no matching file (caller keeps empty host stub).
    /// </summary>
    private bool TryMapHostPathToDisc(string path, out string mapped)
    {
        mapped = path;
        if (_discVolume == null || string.IsNullOrEmpty(path)) return false;
        if (!IsNonDiscDevicePath(path)) return false;
        // Only remap host* — never mc/rom/hdd (those are real non-disc devices).
        int colon = path.IndexOf(':');
        if (colon <= 0) return false;
        string dev = path[..colon].ToLowerInvariant();
        while (dev.Length > 0 && char.IsDigit(dev[^1]))
            dev = dev[..^1];
        if (dev != "host") return false;

        string norm = NormalizeDiscPath(path);
        var entry = FindDiscEntry(norm) ?? FindDiscEntryAny(norm);
        if (entry == null || entry.IsDirectory) return false;
        mapped = "cdrom0:" + entry.Path.Replace('/', '\\');
        return true;
    }

    /// <summary>
    /// Parse <c>rom0:NAME</c> / <c>rom:NAME</c> / <c>rom0:\NAME;1</c> into a bare ROMDIR module name.
    /// Returns true when the path targets the rom device (including bare <c>rom0:</c> for dopen).
    /// </summary>
    public static bool TryResolveRom0Path(string path, out string? moduleName)
    {
        moduleName = null;
        if (string.IsNullOrEmpty(path)) return false;
        string p = path.Trim();
        string rest;
        if (p.StartsWith("rom0:", StringComparison.OrdinalIgnoreCase))
            rest = p[5..];
        else if (p.StartsWith("rom:", StringComparison.OrdinalIgnoreCase))
            rest = p[4..];
        else if (p.StartsWith("rom1:", StringComparison.OrdinalIgnoreCase))
            rest = p[5..];
        else
            return false;

        rest = rest.TrimStart('\\', '/');
        int semi = rest.IndexOf(';');
        if (semi >= 0) rest = rest[..semi];
        // Strip extension sometimes used in probes (rom0:PADMAN.IRX → PADMAN)
        int dot = rest.LastIndexOf('.');
        if (dot > 0)
        {
            string ext = rest[(dot + 1)..];
            if (ext.Equals("IRX", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals("IMG", StringComparison.OrdinalIgnoreCase))
                rest = rest[..dot];
        }
        moduleName = rest.Trim();
        return true;
    }

    /// <summary>Resolve ROMDIR bytes for a bare module name. Null if unbound or missing.</summary>
    public byte[]? ResolveRom0Content(string moduleName)
    {
        if (_romBios == null || _romBios.Length == 0 || string.IsNullOrWhiteSpace(moduleName))
            return null;
        return RomdirExtractor.ExtractModuleContent(_romBios, moduleName);
    }

    private static uint DoPad(SifRpcPacket pkt, SystemMemory mem, PadInput pad)
    {
        uint buttons = pad.Buttons;
        if (pkt.EeBuffer != 0)
        {
            if (pkt.Size >= 8)
                pad.WriteStatusBuffer(mem, pkt.EeBuffer);
            else
                mem.Write32(pkt.EeBuffer, buttons);
        }
        return buttons;
    }

    private static uint DoCdvd(SifRpcPacket pkt, SystemMemory mem, Cdvd cdvd)
    {
        uint lba = pkt.Size;
        if (!cdvd.ReadSector(lba))
            return 0;
        if (pkt.EeBuffer != 0)
            cdvd.CopySectorToMemory(mem, pkt.EeBuffer);
        return 1;
    }

    private uint DoLoadModule(SifRpcPacket pkt, SystemMemory mem)
    {
        string name = ReadCString(mem, pkt.EeBuffer, 64);
        if (string.IsNullOrEmpty(name))
            return unchecked((uint)-1);
        return (uint)RegisterModule(name);
    }

    private uint DoGetModule(SifRpcPacket pkt, SystemMemory mem)
    {
        string name = ReadCString(mem, pkt.EeBuffer, 64);
        return TryGetModule(name, out int id) ? (uint)id : unchecked((uint)-1);
    }

    private uint DoLoadIrx(SifRpcPacket pkt, SystemMemory mem)
    {
        // EeBuffer = ELF image addr, Size = byte length, result = module id or -1
        if (pkt.Size == 0 || pkt.Size > 0x200000 || pkt.EeBuffer == 0)
            return unchecked((uint)-1);
        byte[] elf = new byte[pkt.Size];
        for (uint i = 0; i < pkt.Size; i++)
            elf[i] = mem.Read8(pkt.EeBuffer + i);
        var r = LoadIrx(elf, mem);
        if (!r.Success) return unchecked((uint)-1);
        return TryGetModule(r.ModuleName, out int id) ? (uint)id : unchecked((uint)-1);
    }

    private uint DoMemCard(SifRpcPacket pkt, SystemMemory mem)
    {
        // Size: 0=status, 1=format, 2=file count
        switch (pkt.Size)
        {
            case 0: return _memcard.Formatted ? 1u : 0u;
            case 1: _memcard.Format(); return 1;
            case 2: return (uint)_memcard.FileCount;
            default: return 0;
        }
    }

    private static string ReadCString(SystemMemory mem, uint addr, int max)
    {
        if (addr == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < max; i++)
        {
            byte b = mem.Read8(addr + (uint)i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}
