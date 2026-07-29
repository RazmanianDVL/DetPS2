using System;
using System.Collections.Generic;
using System.IO;
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

public sealed class LoadedIrx
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public uint Entry { get; init; }
    public uint LoadBase { get; init; }
    public int Segments { get; init; }
}

/// <summary>
/// IOP module registry + RPC + IRX load (Phases 13/22).
/// </summary>
public sealed class IopModuleHost
{
    private readonly Dictionary<string, int> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, LoadedIrx> _irxById = new();
    private readonly Dictionary<int, string> _openFiles = new();
    private readonly Dictionary<int, OpenHostFile> _hostFiles = new();
    private readonly Dictionary<int, OpenDir> _openDirs = new();
    private int _nextModuleId = 1;
    private int _nextFd = 3;
    private int _nextDirFd = 1000;
    private uint _nextIopBase = IrxLoader.DefaultLoadBase;
    private MemoryCard _memcard = new();
    private Iso9660.Volume? _discVolume;
    private string? _discPath;

    // fio / iox_stat mode bits (ps2sdk iox_stat.h)
    public const uint FioSIfDir = 0x1000;
    public const uint FioSIfReg = 0x2000;
    public const uint FioSIfmt = 0xF000;
    public const uint FioSIrusr = 0x0100;
    public const uint FioSIwusr = 0x0080;
    public const uint FioSIxusr = 0x0040;

    private sealed class OpenHostFile
    {
        public string Path = "";
        public byte[]? Data;
        public int Position;
        public uint Lba;
        public uint Size;
    }

    private sealed class OpenDir
    {
        public string Path = "";
        public List<Iso9660.FileEntry> Entries = new();
        public int Index;
    }

    public ulong RpcHandled { get; private set; }
    public int ModuleCount => _modules.Count;
    public int IrxLoadedCount => _irxById.Count;
    public MemoryCard MemCard => _memcard;
    public ulong IrxLoads { get; private set; }
    public ulong DiscBytesRead { get; private set; }

    /// <summary>Share system memory card instance (Phase 31).</summary>
    public void BindMemCard(MemoryCard card) => _memcard = card ?? new MemoryCard();

    public void Reset()
    {
        _modules.Clear();
        _irxById.Clear();
        _openFiles.Clear();
        _hostFiles.Clear();
        _openDirs.Clear();
        _nextModuleId = 1;
        _nextFd = 3;
        _nextDirFd = 1000;
        _nextIopBase = IrxLoader.DefaultLoadBase;
        RpcHandled = 0;
        IrxLoads = 0;
        DiscBytesRead = 0;
        // keep disc volume + bound card
        _memcard.Format();
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
        RegisterModule("FILEIO");
        RegisterModule("PADMAN");
        RegisterModule("CDVDMAN");
        RegisterModule("SIO2MAN");
        RegisterModule("MCMAN");
        RegisterModule("MCSERV");
        RegisterModule("LIBSD");
    }

    public int RegisterModule(string name)
    {
        name = NormalizeName(name);
        if (_modules.TryGetValue(name, out int id))
            return id;
        id = _nextModuleId++;
        _modules[name] = id;
        return id;
    }

    public bool TryGetModule(string name, out int id)
    {
        name = NormalizeName(name);
        return _modules.TryGetValue(name, out id);
    }

    public bool IsModuleLoaded(string name) => TryGetModule(name, out _);

    public bool TryGetIrx(int id, out LoadedIrx irx) => _irxById.TryGetValue(id, out irx!);

    /// <summary>Load IRX ELF bytes into IOP RAM and register module name.</summary>
    public IrxLoader.LoadResult LoadIrx(byte[] elf, SystemMemory mem, string? nameOverride = null)
    {
        uint baseLocal = _nextIopBase;
        var result = IrxLoader.Load(elf, mem, baseLocal);
        if (!result.Success)
            return result;

        string name = !string.IsNullOrEmpty(nameOverride)
            ? nameOverride!
            : (string.IsNullOrEmpty(result.ModuleName) ? $"IRX{_nextModuleId}" : result.ModuleName);
        int id = RegisterModule(name);
        _irxById[id] = new LoadedIrx
        {
            Id = id,
            Name = NormalizeName(name),
            Entry = result.Entry,
            LoadBase = result.LoadBase,
            Segments = result.Segments
        };
        // Advance base for next load (16KB align)
        _nextIopBase = baseLocal + 0x4000;
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
            ModuleName = NormalizeName(name)
        };
    }

    private static string NormalizeName(string name)
    {
        name = name.Trim();
        int slash = name.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) name = name[(slash + 1)..];
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
            long off = (long)hf.Lba * Iso9660.SectorSize + hf.Position;
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

    /// <summary>fio open by path string; returns fd or -1.</summary>
    public int FileOpen(string path, int mode = 0)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        int fd = _nextFd++;
        _openFiles[fd] = path;
        var hf = new OpenHostFile { Path = path, Position = 0 };
        if (_discVolume != null)
        {
            string norm = NormalizeDiscPath(path);
            var entry = FindDiscEntry(norm) ?? FindDiscEntryAny(norm);
            if (entry != null && !entry.IsDirectory && entry.Size > 0)
            {
                if (entry.Size <= 16 * 1024 * 1024)
                    hf.Data = Iso9660.ReadFile(_discVolume, entry.Path);
                hf.Lba = entry.ExtentLba;
                hf.Size = entry.Size;
            }
            else if (entry == null && (mode & 0x200) != 0)
            {
                // O_CREAT-ish: allow empty host file for write probes
                hf.Data = Array.Empty<byte>();
            }
            else if (entry == null && !_discVolume.Files.Exists(f => !f.IsDirectory))
            {
                // no disc files — still return fd for boot probes
            }
            else if (entry == null)
            {
                // Missing path on real disc — fail open (commercial games check this)
                _openFiles.Remove(fd);
                return -1;
            }
        }
        _hostFiles[fd] = hf;
        _ = mode;
        return fd;
    }

    public int FileClose(int fd)
    {
        _openFiles.Remove(fd);
        _hostFiles.Remove(fd);
        return 0;
    }

    public int FileRead(SystemMemory mem, int fd, uint buf, uint size)
    {
        var pkt = new SifRpcPacket
        {
            Cmd = SifRpcCmd.Read,
            EeBuffer = buf,
            Size = size,
            Result = unchecked((uint)fd)
        };
        return unchecked((int)DoRead(pkt, mem));
    }

    public int FileWrite(SystemMemory mem, int fd, uint buf, uint size)
    {
        var pkt = new SifRpcPacket
        {
            Cmd = SifRpcCmd.Write,
            EeBuffer = buf,
            Size = size,
            Result = unchecked((uint)fd)
        };
        return unchecked((int)DoWrite(pkt, mem));
    }

    public int FileSeek(int fd, int offset, int whence)
    {
        if (!_hostFiles.TryGetValue(fd, out var hf)) return -1;
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
    /// fio getstat / chstat. Writes io_stat_t-shaped fields into <paramref name="statAddr"/>
    /// when non-zero: +0 mode, +4 attr, +8 size (u32), +0x28 hisize.
    /// </summary>
    public int FileGetStat(SystemMemory mem, string path, uint statAddr)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        string norm = NormalizeDiscPath(path);
        var entry = FindDiscEntryAny(norm);
        uint mode = FioSIrusr | FioSIwusr | FioSIxusr;
        uint size = 0;
        if (entry != null)
        {
            mode |= entry.IsDirectory ? FioSIfDir : FioSIfReg;
            size = entry.Size;
        }
        else if (_discVolume == null)
        {
            // No disc: claim regular empty file so probes succeed
            mode |= FioSIfReg;
        }
        else
            return -1; // missing on mounted disc

        if (statAddr != 0)
        {
            mem.Write32(statAddr + 0, mode);
            mem.Write32(statAddr + 4, 0); // attr
            mem.Write32(statAddr + 8, size);
            // ctime/atime/mtime 8 bytes each at +0xC,+0x14,+0x1C — leave zero
            mem.Write32(statAddr + 0x28, 0); // hisize
        }
        return 0;
    }

    public int DirOpen(string path)
    {
        string norm = NormalizeDiscPath(path ?? "");
        if (norm.Length == 0) norm = "";
        var list = new List<Iso9660.FileEntry>();
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
        int dfd = _nextDirFd++;
        _openDirs[dfd] = new OpenDir { Path = norm, Entries = list, Index = 0 };
        return dfd;
    }

    public int DirClose(int dfd)
    {
        return _openDirs.Remove(dfd) ? 0 : -1;
    }

    /// <summary>fio dread: write io_dirent_t name + stat into <paramref name="direntAddr"/>.</summary>
    public int DirRead(SystemMemory mem, int dfd, uint direntAddr)
    {
        if (!_openDirs.TryGetValue(dfd, out var dir)) return -1;
        if (dir.Index >= dir.Entries.Count) return -1; // end
        var e = dir.Entries[dir.Index++];
        if (direntAddr != 0)
        {
            // io_dirent_t: io_stat_t stat; char name[256];
            uint mode = FioSIrusr | FioSIwusr | FioSIxusr | (e.IsDirectory ? FioSIfDir : FioSIfReg);
            mem.Write32(direntAddr + 0, mode);
            mem.Write32(direntAddr + 4, 0);
            mem.Write32(direntAddr + 8, e.Size);
            uint nameAddr = direntAddr + 0x40; // common padding; also try +0x30
            WriteCString(mem, nameAddr, e.Name, 255);
            // Alternate layout used by some SDK builds: name at +0x20 after shorter stat
            WriteCString(mem, direntAddr + 0x20, e.Name, 255);
        }
        return dir.Index; // positive remaining-ish / success
    }

    public int FileRemove(string path)
    {
        // Read-only ISO: pretend success for temp paths, fail for disc files
        if (_discVolume != null && FindDiscEntryAny(NormalizeDiscPath(path)) != null)
            return -1;
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
        foreach (var f in _discVolume.Files)
        {
            if (f.IsDirectory) continue;
            string p = f.Path.Replace('\\', '/').ToUpperInvariant();
            string n = f.Name.ToUpperInvariant();
            if (p == normPath || n == normPath || p.EndsWith("/" + normPath, StringComparison.Ordinal) ||
                p.EndsWith(normPath, StringComparison.Ordinal))
                return f;
        }
        // basename match
        string baseName = Path.GetFileName(normPath);
        foreach (var f in _discVolume.Files)
        {
            if (f.IsDirectory) continue;
            if (string.Equals(f.Name, baseName, StringComparison.OrdinalIgnoreCase))
                return f;
        }
        return null;
    }

    private static string NormalizeDiscPath(string path)
    {
        path = path.Trim();
        if (path.StartsWith("cdrom0:", StringComparison.OrdinalIgnoreCase))
            path = path["cdrom0:".Length..];
        if (path.StartsWith("cdrom:", StringComparison.OrdinalIgnoreCase))
            path = path["cdrom:".Length..];
        path = path.TrimStart('\\', '/');
        int semi = path.IndexOf(';');
        if (semi >= 0) path = path[..semi];
        return path.Replace('\\', '/').ToUpperInvariant();
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
