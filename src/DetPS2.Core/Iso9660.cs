using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Minimal ISO9660 reader + synthetic builder (Phase 9).
/// Supports Level 1 file lookup by name and SYSTEM.CNF boot flow.
/// </summary>
public static class Iso9660
{
    public const int SectorSize = 2048;

    public sealed class FileEntry
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = ""; // full path e.g. MODULES/FOO.IRX
        public uint ExtentLba { get; init; }
        public uint Size { get; init; }
        public bool IsDirectory { get; init; }
    }

    public sealed class Volume
    {
        public string VolumeId { get; init; } = "";
        public uint RootLba { get; init; }
        public uint RootSize { get; init; }
        public List<FileEntry> Files { get; } = new();
        /// <summary>Small in-memory images only; multi‑GB discs use <see cref="Disc"/>.</summary>
        public byte[]? Image { get; init; }
        public IDiscImage? Disc { get; init; }
        public long ImageLength => Disc?.Length ?? Image?.LongLength ?? 0;
    }

    /// <summary>Parse primary volume + recursive directory listing (Phase 16).</summary>
    public static Volume? Open(byte[] image) => Open(new MemoryDiscImage(image));

    /// <summary>Open ISO from local or UNC path without loading whole file into RAM.</summary>
    public static Volume? OpenFile(string path)
    {
        try
        {
            var disc = new FileDiscImage(path);
            var vol = Open(disc);
            if (vol == null) disc.Dispose();
            return vol;
        }
        catch
        {
            return null;
        }
    }

    public static Volume? Open(IDiscImage disc)
    {
        if (disc.Length < SectorSize * 17) return null;
        Span<byte> pvd = stackalloc byte[SectorSize];
        if (disc.ReadAt(16L * SectorSize, pvd) < SectorSize) return null;
        if (pvd[0] != 1) return null;
        if (Encoding.ASCII.GetString(pvd.Slice(1, 5)) != "CD001") return null;

        string volId = Encoding.ASCII.GetString(pvd.Slice(40, 32)).Trim();
        uint rootLba = BitConverter.ToUInt32(pvd.Slice(158, 4));
        uint rootSize = BitConverter.ToUInt32(pvd.Slice(166, 4));

        var vol = new Volume
        {
            VolumeId = volId,
            RootLba = rootLba,
            RootSize = rootSize,
            Disc = disc,
            Image = disc is MemoryDiscImage ? null : null
        };
        // Keep Image for MemoryDiscImage convenience via disc only
        ParseDirectory(disc, rootLba, rootSize, "", vol.Files, depth: 0);
        return vol;
    }

    private static void ParseDirectory(IDiscImage disc, uint lba, uint size, string prefix, List<FileEntry> files, int depth)
    {
        if (depth > 8) return;
        long offset = (long)lba * SectorSize;
        long end = Math.Min(disc.Length, offset + size);
        // Read directory extent into buffer (dirs are small)
        int dirLen = (int)Math.Min(end - offset, 512 * 1024); // cap 512KB dir
        if (dirLen <= 0) return;
        byte[] image = new byte[dirLen];
        disc.ReadAt(offset, image);

        int pos = 0;
        int localEnd = image.Length;
        while (pos + 33 < localEnd)
        {
            int len = image[pos];
            if (len == 0)
            {
                int next = ((pos / SectorSize) + 1) * SectorSize;
                if (next >= localEnd) break;
                pos = next;
                continue;
            }
            uint extent = BitConverter.ToUInt32(image, pos + 2);
            uint dataLen = BitConverter.ToUInt32(image, pos + 10);
            byte flags = image[pos + 25];
            bool isDir = (flags & 0x02) != 0;
            int nameLen = image[pos + 32];
            if (nameLen > 0 && pos + 33 + nameLen <= image.Length)
            {
                string name = Encoding.ASCII.GetString(image, pos + 33, nameLen);
                int semi = name.IndexOf(';');
                if (semi >= 0) name = name[..semi];
                if (name is not ("\0" or "\x01"))
                {
                    name = name.ToUpperInvariant();
                    string path = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
                    files.Add(new FileEntry
                    {
                        Name = name,
                        Path = path,
                        ExtentLba = extent,
                        Size = dataLen,
                        IsDirectory = isDir
                    });
                    if (isDir)
                        ParseDirectory(disc, extent, dataLen, path, files, depth + 1);
                }
            }
            pos += len;
        }
    }

    /// <summary>Normalize a retail/CRI path to ISO9660 lookup form (upper, no device prefix).</summary>
    public static string NormalizePath(string name)
    {
        name = name.Trim();
        name = name.ToUpperInvariant();
        if (name.StartsWith("CDROM0:\\", StringComparison.Ordinal)) name = name["CDROM0:\\".Length..];
        if (name.StartsWith("CDROM:\\", StringComparison.Ordinal)) name = name["CDROM:\\".Length..];
        if (name.StartsWith("CDV:", StringComparison.Ordinal)) name = name["CDV:".Length..];
        if (name.StartsWith("MFS:", StringComparison.Ordinal)) name = name["MFS:".Length..];
        // CRI paths often start with '\'; strip leading separators.
        name = name.TrimStart('\\', '/');
        name = name.Replace('\\', '/');
        // ISO 9660 version ";1" is stripped at parse time — strip here so FindFile matches.
        int semi = name.IndexOf(';');
        if (semi >= 0) name = name[..semi];
        // S222 dual-ACK diagnostic: US/C5_V1 is the sole 0-byte STREAMED slot (dev/unused).
        // Env-gated path rewrite so size lookups and opens both see C1_V1. Off by default.
        if (Environment.GetEnvironmentVariable("DETPS2_B3_TRACK_REWRITE") == "1"
            && name.Contains("C5_V1", StringComparison.Ordinal))
            name = name.Replace("C5_V1", "C1_V1", StringComparison.Ordinal);
        return name;
    }

    /// <summary>Locate a file entry by name/path without loading contents (multi-GB WADs stay on disc).</summary>
    public static FileEntry? FindFile(Volume vol, string name)
    {
        name = NormalizePath(name);
        if (string.IsNullOrEmpty(name)) return null;

        foreach (var f in vol.Files)
        {
            if (f.IsDirectory) continue;
            if (f.Name == name || f.Path == name || f.Path.EndsWith("/" + name, StringComparison.Ordinal) ||
                f.Name.StartsWith(name + ".", StringComparison.Ordinal) ||
                Path.GetFileName(f.Path) == name)
                return f;
        }
        return null;
    }

    /// <summary>Read a byte range from a file entry (sector-aligned disc path).</summary>
    public static int ReadFileRange(Volume vol, FileEntry file, long fileOffset, Span<byte> dest)
    {
        IDiscImage? disc = vol.Disc;
        if (disc == null && vol.Image != null)
            disc = new MemoryDiscImage(vol.Image);
        if (disc == null || dest.Length == 0) return 0;
        if (fileOffset < 0 || fileOffset >= file.Size) return 0;

        int want = (int)Math.Min(dest.Length, file.Size - fileOffset);
        long discOff = (long)file.ExtentLba * SectorSize + fileOffset;
        if (discOff >= disc.Length) return 0;
        want = (int)Math.Min(want, disc.Length - discOff);
        return disc.ReadAt(discOff, dest[..want]);
    }

    public static byte[]? ReadFile(Volume vol, string name)
    {
        IDiscImage? disc = vol.Disc;
        if (disc == null && vol.Image != null)
            disc = new MemoryDiscImage(vol.Image);
        if (disc == null) return null;

        var f = FindFile(vol, name);
        if (f == null) return null;

        long off = (long)f.ExtentLba * SectorSize;
        // Cap single file load at 512MB (ELF/modules); multi-GB files not loaded whole
        int len = (int)Math.Min(f.Size, (uint)Math.Min(512 * 1024 * 1024, Math.Max(0, disc.Length - off)));
        if (len <= 0) return Array.Empty<byte>();
        byte[] data = new byte[len];
        int got = disc.ReadAt(off, data);
        if (got < len) Array.Resize(ref data, got);
        return data;
    }

    /// <summary>Build ISO with optional subdirectory (e.g. path MODULES/X.IRX).</summary>
    public static byte[] BuildWithDirs(string volumeId, string systemCnf, IReadOnlyDictionary<string, byte[]> files)
    {
        // Flatten: keys may contain '/'
        var flat = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["SYSTEM.CNF"] = Encoding.ASCII.GetBytes(systemCnf.Replace("\r\n", "\n"))
        };
        foreach (var kv in files)
            flat[kv.Key.Replace('\\', '/').ToUpperInvariant()] = kv.Value;

        // Group by directory
        var dirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        foreach (var key in flat.Keys)
        {
            int slash = key.LastIndexOf('/');
            if (slash > 0)
            {
                string d = key[..slash];
                dirs.Add(d);
                // parents
                int p = 0;
                while ((p = d.IndexOf('/', p + 1)) > 0)
                    dirs.Add(d[..p]);
            }
        }

        // Allocate LBAs: 18 root, then each dir sector, then file data
        int nextLba = 19;
        var dirLba = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dirSize = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
        {
            dirLba[d] = nextLba++;
            dirSize[d] = SectorSize; // one sector each for simplicity
        }

        var fileMeta = new List<(string path, string name, string parent, int lba, int size, byte[] data)>();
        foreach (var kv in flat)
        {
            string path = kv.Key;
            int slash = path.LastIndexOf('/');
            string parent = slash > 0 ? path[..slash] : "";
            string name = slash >= 0 ? path[(slash + 1)..] : path;
            int sectors = Math.Max(1, (kv.Value.Length + SectorSize - 1) / SectorSize);
            int lba = nextLba;
            nextLba += sectors;
            fileMeta.Add((path, name, parent, lba, kv.Value.Length, kv.Value));
        }

        byte[] image = new byte[nextLba * SectorSize];
        int pvd = 16 * SectorSize;
        image[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, pvd + 1);
        image[pvd + 6] = 1;
        WriteAString(image, pvd + 40, volumeId, 32);
        WriteBothEndian32(image, pvd + 80, (uint)nextLba);
        WriteDirRecord(image, pvd + 156, dirLba[""], SectorSize, "\0", isDir: true);
        WriteBothEndian32(image, pvd + 132, 10);
        int term = 17 * SectorSize;
        image[term] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, term + 1);

        foreach (var d in dirs)
        {
            int rpos = dirLba[d] * SectorSize;
            int self = dirLba[d];
            int parentLba = d.Contains('/') ? dirLba[d[..d.LastIndexOf('/')]] : dirLba[""];
            if (d == "") parentLba = self;
            rpos += WriteDirRecord(image, rpos, self, SectorSize, "\0", isDir: true);
            rpos += WriteDirRecord(image, rpos, parentLba, SectorSize, "\x01", isDir: true);

            // subdirs
            foreach (var sub in dirs)
            {
                if (sub == d) continue;
                string parent = sub.Contains('/') ? sub[..sub.LastIndexOf('/')] : "";
                if (parent != d) continue;
                string subName = sub.Contains('/') ? sub[(sub.LastIndexOf('/') + 1)..] : sub;
                rpos += WriteDirRecord(image, rpos, dirLba[sub], SectorSize, subName, isDir: true);
            }
            foreach (var f in fileMeta)
            {
                if (f.parent != d) continue;
                rpos += WriteDirRecord(image, rpos, f.lba, f.size, f.name + ";1", isDir: false);
            }
        }

        foreach (var f in fileMeta)
            f.data.CopyTo(image.AsSpan(f.lba * SectorSize));

        return image;
    }

    /// <summary>
    /// Build a minimal single-directory ISO with SYSTEM.CNF and optional extra files.
    /// </summary>
    public static byte[] Build(string volumeId, string systemCnf, IReadOnlyDictionary<string, byte[]> files)
    {
        // Layout:
        // LBA 0-15: zeros
        // LBA 16: PVD
        // LBA 17: volume terminator
        // LBA 18: root directory
        // LBA 19+: file data
        var allFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["SYSTEM.CNF"] = Encoding.ASCII.GetBytes(systemCnf.Replace("\r\n", "\n"))
        };
        foreach (var kv in files)
            allFiles[kv.Key.ToUpperInvariant()] = kv.Value;

        const int rootLba = 18;
        int nextLba = 19;
        var fileMeta = new List<(string name, int lba, int size, byte[] data)>();
        foreach (var kv in allFiles)
        {
            int sectors = (kv.Value.Length + SectorSize - 1) / SectorSize;
            if (sectors == 0) sectors = 1;
            fileMeta.Add((kv.Key, nextLba, kv.Value.Length, kv.Value));
            nextLba += sectors;
        }

        int totalSectors = nextLba;
        byte[] image = new byte[totalSectors * SectorSize];

        // PVD
        int pvd = 16 * SectorSize;
        image[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, pvd + 1);
        image[pvd + 6] = 1;
        WriteAString(image, pvd + 40, volumeId, 32);
        // volume space size (LE + BE)
        WriteBothEndian32(image, pvd + 80, (uint)totalSectors);
        // root dir record at 156
        WriteDirRecord(image, pvd + 156, rootLba, SectorSize, "\0", isDir: true);
        // path table size dummy
        WriteBothEndian32(image, pvd + 132, 10);

        // Terminator at 17
        int term = 17 * SectorSize;
        image[term] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, term + 1);

        // Root directory sector
        int rootOff = rootLba * SectorSize;
        int rpos = rootOff;
        rpos += WriteDirRecord(image, rpos, rootLba, SectorSize, "\0", isDir: true);
        rpos += WriteDirRecord(image, rpos, rootLba, SectorSize, "\x01", isDir: true);
        foreach (var f in fileMeta)
        {
            int rec = WriteDirRecord(image, rpos, f.lba, f.size, f.name + ";1", isDir: false);
            rpos += rec;
        }

        // File data
        foreach (var f in fileMeta)
        {
            int off = f.lba * SectorSize;
            f.data.CopyTo(image.AsSpan(off));
        }

        return image;
    }

    private static void WriteAString(byte[] img, int off, string s, int len)
    {
        for (int i = 0; i < len; i++)
            img[off + i] = (byte)(i < s.Length ? s[i] : ' ');
    }

    private static void WriteBothEndian32(byte[] img, int off, uint v)
    {
        img[off] = (byte)v; img[off + 1] = (byte)(v >> 8); img[off + 2] = (byte)(v >> 16); img[off + 3] = (byte)(v >> 24);
        img[off + 4] = (byte)(v >> 24); img[off + 5] = (byte)(v >> 16); img[off + 6] = (byte)(v >> 8); img[off + 7] = (byte)v;
    }

    private static int WriteDirRecord(byte[] img, int off, int lba, int dataSize, string name, bool isDir)
    {
        int nameLen = Encoding.ASCII.GetByteCount(name);
        if (nameLen == 0) nameLen = 1;
        int len = 33 + nameLen;
        if ((len & 1) != 0) len++;
        if (off + len > img.Length) return len;

        img[off] = (byte)len;
        WriteBothEndian32(img, off + 2, (uint)lba);
        WriteBothEndian32(img, off + 10, (uint)dataSize);
        img[off + 25] = (byte)(isDir ? 2 : 0);
        img[off + 32] = (byte)nameLen;
        if (name == "\0" || name == "\x01")
            img[off + 33] = (byte)name[0];
        else
            Encoding.ASCII.GetBytes(name).CopyTo(img, off + 33);
        return len;
    }
}

/// <summary>SYSTEM.CNF parser for BOOT2 / VER / VMODE.</summary>
public sealed class SystemCnf
{
    public string? Boot2 { get; init; }
    public string? Ver { get; init; }
    public string? Vmode { get; init; }
    public string Raw { get; init; } = "";

    public static SystemCnf Parse(string text)
    {
        string? boot2 = null, ver = null, vmode = null;
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = line.Trim();
            if (t.StartsWith("#") || !t.Contains('=')) continue;
            int eq = t.IndexOf('=');
            string key = t[..eq].Trim().ToUpperInvariant();
            string val = t[(eq + 1)..].Trim();
            switch (key)
            {
                case "BOOT2":
                case "BOOT":
                    boot2 = val;
                    break;
                case "VER":
                    ver = val;
                    break;
                case "VMODE":
                    vmode = val;
                    break;
            }
        }
        return new SystemCnf { Boot2 = boot2, Ver = ver, Vmode = vmode, Raw = text };
    }

    /// <summary>Extract ELF filename from BOOT2 path like cdrom0:\FOO.ELF;1</summary>
    public string? BootFileName
    {
        get
        {
            if (string.IsNullOrEmpty(Boot2)) return null;
            string s = Boot2;
            int colon = s.IndexOf(':');
            if (colon >= 0) s = s[(colon + 1)..];
            s = s.TrimStart('\\', '/');
            int semi = s.IndexOf(';');
            if (semi >= 0) s = s[..semi];
            return s.ToUpperInvariant();
        }
    }
}

/// <summary>Disc boot orchestration (Phase 9).</summary>
public sealed class DiscBoot
{
    public sealed class Result
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public SystemCnf? Cnf { get; init; }
        public ElfLoader.LoadResult? Elf { get; init; }
        public string? BootPath { get; init; }
    }

    public static Result BootFromImage(Ps2System system, byte[] isoImage)
    {
        var disc = new MemoryDiscImage(isoImage);
        return BootFromDisc(system, disc, takeOwnership: true);
    }

    /// <summary>Boot ISO/BIN from path (supports multi‑GB and UNC paths).</summary>
    public static Result BootFromFile(Ps2System system, string path)
    {
        try
        {
            path = FileDiscImage.NormalizePath(path);
            if (!File.Exists(path))
                return new Result { Success = false, Message = "File not found: " + path };
            var disc = new FileDiscImage(path);
            return BootFromDisc(system, disc, takeOwnership: true);
        }
        catch (Exception ex)
        {
            return new Result { Success = false, Message = ex.Message };
        }
    }

    public static Result BootFromDisc(Ps2System system, IDiscImage disc, bool takeOwnership)
    {
        var vol = Iso9660.Open(disc);
        if (vol == null)
        {
            if (takeOwnership) disc.Dispose();
            return new Result { Success = false, Message = "Invalid ISO9660 image" };
        }

        // Mount same disc on CDVD for runtime sector I/O (Cdvd takes ownership if we pass it)
        system.Cdvd.MountDisc(disc, vol.VolumeId);

        byte[]? cnfBytes = Iso9660.ReadFile(vol, "SYSTEM.CNF");
        if (cnfBytes == null)
            return new Result { Success = false, Message = "SYSTEM.CNF not found" };

        string cnfText = Encoding.ASCII.GetString(cnfBytes);
        var cnf = SystemCnf.Parse(cnfText);
        string? bootName = cnf.BootFileName;
        if (bootName == null)
            return new Result { Success = false, Message = "BOOT2 missing", Cnf = cnf };

        byte[]? elf = Iso9660.ReadFile(vol, bootName);
        if (elf == null)
            return new Result { Success = false, Message = $"Boot ELF '{bootName}' not found", Cnf = cnf };

        var load = ElfLoader.LoadIntoEe(elf, system);
        // After ELF load, keep Sony kernel HLE if BIOS was installed (commercial path)
        if (system.Hle.SonyKernelMode == false && system.Memory.Read32(0x1FC00000) != 0)
            system.Hle.EnableSonyKernel();
        if (system.Hle.SonyKernelMode)
            KernelBootstrap.InstallCommercialRuntime(system);

        system.IopModules.BindDisc(system.Cdvd.MountedPath);

        // GameQuirks SDK: resolve any registered module for this disc's serial (additive —
        // most titles resolve to null and nothing else changes). See IGameQuirkModule.
        // MidwayBootAssist is now itself an IGameQuirkModule (serial SLUS_210.87) — this is
        // the only OnDiscMounted call site, so it's correctly serial-gated rather than firing
        // for every commercial title regardless of which disc is mounted.
        // BIOS service map must be live before any title code runs (shared by all discs).
        if (!system.BiosBoot.Started)
            system.BiosBoot.StartCommercialIop(system);

        string? serial = MediaVerify.ExtractSerial(cnfText, bootName);
        system.ActiveQuirk = GameQuirkRegistry.Resolve(serial);
        system.ActiveQuirk?.OnDiscMounted(system);

        long mb = disc.Length / (1024 * 1024);
        return new Result
        {
            Success = true,
            Message = $"Booted {bootName} entry=0x{load.Entry:X8} size={mb}MB",
            Cnf = cnf,
            Elf = load,
            BootPath = bootName
        };
    }

    public static Result BootSynthetic(Ps2System system, string systemCnf, byte[] elf, string elfName = "BOOT.ELF")
    {
        byte[] iso = Iso9660.Build("DETPS2", systemCnf, new Dictionary<string, byte[]>
        {
            [elfName.ToUpperInvariant()] = elf
        });
        return BootFromImage(system, iso);
    }

    public static Result BootSyntheticWithDirs(Ps2System system, string systemCnf, IReadOnlyDictionary<string, byte[]> files)
    {
        byte[] iso = Iso9660.BuildWithDirs("DETPS2", systemCnf, files);
        return BootFromImage(system, iso);
    }
}
