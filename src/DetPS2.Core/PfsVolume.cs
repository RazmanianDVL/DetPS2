using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Read/write driver for a PFS filesystem living inside one APA partition. Formats and
/// navigates the real on-disk layout described in <see cref="Pfs"/> — single main partition,
/// zone_size = 8192 bytes, single-segment inodes (files up to 114 direct zones, ~912KB).
///
/// The exact reserved-zone layout for Format() (superblock zone, bitmap zone-count, journal
/// placement, root inode + root dentry zones) is derived directly from ps2sdk's pfsFormat()/
/// pfsFormatSub() (superWrite.c) — verified by cross-checking two independently-computed
/// reserved-zone counts against each other (the "reserved" bit count passed into the bitmap
/// vs. the actual end-of-root-directory zone) and finding them match exactly; see the format
/// derivation walked through in DEVELOPER_GUIDE.md.
///
/// Directories are tracked throughout this API by their INODE zone (not their data zone) —
/// the inode zone is a directory's stable identity; its data zone (where "." / ".." / children
/// dentries live) is only looked up via <see cref="DirDataZone"/> at the point of actually
/// reading or writing dentries.
///
/// Directory storage in THIS implementation is simplified relative to real PFS: one zone per
/// directory (16 sectors x 512-byte dentry chunks — room for a generous number of save files),
/// rather than Sony's full multi-zone directory growth, and files are single-segment only
/// (no indirect/continuation inodes for very large files). Struct shapes on disk are the real
/// ones; these are scope limits for the "foundation" pass, not fidelity compromises in the
/// parts that were implemented — see DEVELOPER_GUIDE.md for what's deferred.
/// </summary>
public sealed class PfsVolume
{
    private readonly ApaDisk _disk;
    private readonly ApaHeader _partition;
    private PfsSuperBlock _super = new();

    public PfsSuperBlock Super => _super;
    public bool Mounted { get; private set; }

    public PfsVolume(ApaDisk disk, ApaHeader partition)
    {
        if (partition.Type != Apa.TypePfs)
            throw new ArgumentException("partition is not a PFS partition", nameof(partition));
        _disk = disk;
        _partition = partition;
    }

    // ---- low-level sector/zone I/O, relative to this partition's own data area ----

    private void ReadSectors(uint sector, int count, byte[] dest, int destOffset = 0)
    {
        long off = _disk.PartitionDataByteOffset(_partition, sector);
        Array.Copy(_disk.Data, off, dest, destOffset, (long)count * Pfs.SectorSize);
    }

    private void WriteSectors(uint sector, byte[] src, int srcOffset, int count)
    {
        long off = _disk.PartitionDataByteOffset(_partition, sector);
        Array.Copy(src, srcOffset, _disk.Data, off, (long)count * Pfs.SectorSize);
    }

    private byte[] ReadZone(uint zone)
    {
        var buf = new byte[Pfs.ZoneSize];
        ReadSectors(zone * Pfs.SectorsPerZone, Pfs.SectorsPerZone, buf);
        return buf;
    }

    private void WriteZone(uint zone, byte[] zoneBytes)
    {
        if (zoneBytes.Length != Pfs.ZoneSize) throw new ArgumentException("zone buffer must be exactly ZoneSize bytes");
        WriteSectors(zone * Pfs.SectorsPerZone, zoneBytes, 0, Pfs.SectorsPerZone);
    }

    // ---- zone bitmap allocator ----
    // Bitmap starts right after the superblock's own zone (zone SuperZone+1) and spans
    // BitmapZoneCount zones; bit N (chunk = N/8192, word = (N%8192)/32, bit = N%32) marks
    // whether zone N of the partition is allocated. Bitmap chunks are 1024 bytes each
    // (Pfs.MetaSize); ZoneSize/MetaSize (8) chunks pack sequentially into each bitmap zone.

    private const int ChunksPerZone = Pfs.ZoneSize / Pfs.MetaSize;

    private uint _superZone;      // zone containing PFS_SUPER_SECTOR
    private uint _bitmapStartZone;
    private uint _bitmapZoneCount;
    private uint _totalZones;

    private void ComputeLayout()
    {
        _totalZones = _partition.Length / Pfs.SectorsPerZone;
        _superZone = Pfs.SuperSector / Pfs.SectorsPerZone; // 8192/16 = 512
        _bitmapStartZone = _superZone + 1;
        _bitmapZoneCount = Pfs.GetBitmapSizeBlocks(Pfs.SectorScale, _partition.Length);
    }

    private static bool GetBit(byte[] bitmapChunk, int bitIndex) =>
        (BitConverter.ToUInt32(bitmapChunk, (bitIndex / 32) * 4) & (1u << (bitIndex % 32))) != 0;

    private static void SetBit(byte[] bitmapChunk, int bitIndex, bool value)
    {
        int wordOff = (bitIndex / 32) * 4;
        uint word = BitConverter.ToUInt32(bitmapChunk, wordOff);
        word = value ? (word | (1u << (bitIndex % 32))) : (word & ~(1u << (bitIndex % 32)));
        BitConverter.GetBytes(word).CopyTo(bitmapChunk, wordOff);
    }

    private bool IsZoneAllocated(uint zoneNumber)
    {
        int chunkIndex = (int)(zoneNumber / Pfs.BitsPerBitmapChunk);
        int bitInChunk = (int)(zoneNumber % Pfs.BitsPerBitmapChunk);
        uint zone = _bitmapStartZone + (uint)(chunkIndex / ChunksPerZone);
        int chunkInZone = chunkIndex % ChunksPerZone;
        var zoneBuf = ReadZone(zone);
        var chunkBuf = new byte[Pfs.MetaSize];
        Array.Copy(zoneBuf, chunkInZone * Pfs.MetaSize, chunkBuf, 0, Pfs.MetaSize);
        return GetBit(chunkBuf, bitInChunk);
    }

    private void SetZoneAllocated(uint zoneNumber, bool allocated)
    {
        int chunkIndex = (int)(zoneNumber / Pfs.BitsPerBitmapChunk);
        int bitInChunk = (int)(zoneNumber % Pfs.BitsPerBitmapChunk);
        uint zone = _bitmapStartZone + (uint)(chunkIndex / ChunksPerZone);
        int chunkInZone = chunkIndex % ChunksPerZone;
        var zoneBuf = ReadZone(zone);
        var chunkBuf = new byte[Pfs.MetaSize];
        Array.Copy(zoneBuf, chunkInZone * Pfs.MetaSize, chunkBuf, 0, Pfs.MetaSize);
        SetBit(chunkBuf, bitInChunk, allocated);
        Array.Copy(chunkBuf, 0, zoneBuf, chunkInZone * Pfs.MetaSize, Pfs.MetaSize);
        WriteZone(zone, zoneBuf);
    }

    private uint AllocateZone()
    {
        for (uint z = 0; z < _totalZones; z++)
        {
            if (!IsZoneAllocated(z))
            {
                SetZoneAllocated(z, true);
                return z;
            }
        }
        throw new InvalidOperationException("PFS volume is full — no free zones");
    }

    private void FreeZone(uint zone) => SetZoneAllocated(zone, false);

    // ---- format ----

    /// <summary>Formats this partition with a fresh, empty PFS filesystem: superblock (+
    /// backup), zone bitmap (all reserved zones pre-marked used), and a root directory
    /// containing only "." and "..". Matches ps2sdk's pfsFormat() layout for a single main
    /// partition with zero sub-partitions.</summary>
    public void Format()
    {
        ComputeLayout();

        uint logCount = (uint)((0x20000 / Pfs.ZoneSize) > 0 ? 0x20000 / Pfs.ZoneSize : 1);
        uint logNumber = _bitmapZoneCount + _bitmapStartZone; // bitmapBlocks + (0x2000>>scale) + 1
        uint rootNumber = logNumber + logCount;
        uint rootDentryZone = rootNumber + 1;

        _super = new PfsSuperBlock
        {
            Magic = Pfs.SuperMagic,
            Version = Pfs.FormatVersion,
            ModVer = 0,
            FsckStat = 0,
            ZoneSize = Pfs.ZoneSize,
            NumSubs = 0,
            Log = new PfsBlockInfo { Number = logNumber, Subpart = 0, Count = (ushort)logCount },
            Root = new PfsBlockInfo { Number = rootNumber, Subpart = 0, Count = 1 },
        };

        // Mark every zone from 0 up to (and including) the root's dentry zone as allocated —
        // this covers the pre-superblock gap, the superblock's own zone, the bitmap area, the
        // journal/log area, and the root inode + root dentry zones. Verified equal to Sony's
        // own `reserved = (0x2000>>scale) + log.count + 3 + bitmapBlocks` (see class doc).
        uint reservedZones = rootDentryZone + 1;
        for (uint z = 0; z < reservedZones; z++)
            SetZoneAllocated(z, true);

        // Root directory: "." and ".." only, matching pfsFillSelfAndParentDentries exactly.
        var rootBlock = _super.Root;
        var dentryZoneBuf = new byte[Pfs.ZoneSize];
        new PfsDentry { Inode = rootBlock.Number, Sub = 0, PathLen = 1, ALen = (ushort)(12 | Pfs.FioSIfdir), Path = "." }.WriteTo(dentryZoneBuf, 0);
        new PfsDentry { Inode = rootBlock.Number, Sub = 0, PathLen = 2, ALen = (ushort)(500 | Pfs.FioSIfdir), Path = ".." }.WriteTo(dentryZoneBuf, 12);
        InitializeFreeDentrySectors(dentryZoneBuf, firstFreeSector: 1);
        WriteZone(rootDentryZone, dentryZoneBuf);

        var now = PfsDateTime.FromUtcNow(DateTime.UtcNow);
        var rootInode = new PfsInode
        {
            Magic = Pfs.SegdMagic,
            InodeBlock = rootBlock,
            LastSegment = rootBlock,
            Mode = (ushort)(Pfs.FioSIfdir | Pfs.DefaultPerm),
            Attr = 0xA0,
            Uid = Pfs.Uid,
            Gid = Pfs.Gid,
            Size = PfsDentry.Size,
            NumberBlocks = 2,
            NumberData = 2,
            Atime = now,
            Ctime = now,
            Mtime = now,
        };
        rootInode.Data[1] = new PfsBlockInfo { Number = rootDentryZone, Subpart = 0, Count = 1 };
        var rootInodeZoneBuf = new byte[Pfs.ZoneSize];
        rootInode.ToBytes().CopyTo(rootInodeZoneBuf, 0);
        WriteZone(rootNumber, rootInodeZoneBuf);

        WriteSuperBlockSectors();
        Mounted = true;
    }

    private void WriteSuperBlockSectors()
    {
        var bytes = _super.ToBytes();
        WriteSectors(Pfs.SuperSector, bytes, 0, 1);
        WriteSectors(Pfs.SuperBackupSector, bytes, 0, 1);
    }

    /// <summary>Mounts an already-formatted PFS volume (reads and validates the superblock).</summary>
    public void Mount()
    {
        ComputeLayout();
        var buf = new byte[Pfs.SectorSize];
        ReadSectors(Pfs.SuperSector, 1, buf);
        var sb = PfsSuperBlock.FromBytes(buf);
        if (sb.Magic != Pfs.SuperMagic)
            throw new InvalidOperationException("not a PFS volume (bad superblock magic)");
        if (sb.Version > Pfs.FormatVersion)
            throw new InvalidOperationException($"PFS version {sb.Version} newer than supported ({Pfs.FormatVersion})");
        _super = sb;
        Mounted = true;
    }

    // ---- inode / dentry helpers (directories are identified by INODE zone throughout) ----

    /// <summary>Marks every 512-byte dentry chunk from <paramref name="firstFreeSector"/>
    /// onward as one large free entry (Inode=0, ALen=512). Without this, an untouched
    /// (all-zero) sector reads back as ALen=0 — indistinguishable from "end of chain" — so
    /// AddDentry would never recognize it as allocatable space.</summary>
    private static void InitializeFreeDentrySectors(byte[] zoneBuf, int firstFreeSector)
    {
        for (int sector = firstFreeSector; sector < Pfs.SectorsPerZone; sector++)
        {
            var free = new PfsDentry { Inode = 0, PathLen = 0, ALen = Pfs.SectorSize };
            free.WriteTo(zoneBuf, sector * Pfs.SectorSize);
        }
    }

    private PfsInode ReadInode(uint zone)
    {
        var meta = new byte[Pfs.MetaSize];
        Array.Copy(ReadZone(zone), 0, meta, 0, Pfs.MetaSize);
        return PfsInode.FromBytes(meta);
    }

    private void WriteInode(uint zone, PfsInode inode)
    {
        var zoneBuf = ReadZone(zone);
        inode.ToBytes().CopyTo(zoneBuf, 0);
        WriteZone(zone, zoneBuf);
    }

    /// <summary>A directory's dentry listing lives at data[1] for the root (matching Sony's
    /// format-time bootstrap exactly) or data[0] for any directory created via
    /// <see cref="CreateDirectory"/> (the normal, post-format allocation path).</summary>
    private uint DirDataZone(uint dirInodeZone)
    {
        var inode = ReadInode(dirInodeZone);
        if (inode.Data[1].Count > 0) return inode.Data[1].Number;
        if (inode.Data[0].Count > 0) return inode.Data[0].Number;
        throw new InvalidOperationException("directory inode has no data zone");
    }

    private IEnumerable<PfsDentry> EnumerateDentries(uint dirInodeZone)
    {
        var zoneBuf = ReadZone(DirDataZone(dirInodeZone));
        for (int sector = 0; sector < Pfs.SectorsPerZone; sector++)
        {
            int baseOff = sector * Pfs.SectorSize;
            int pos = 0;
            while (pos < Pfs.SectorSize)
            {
                var d = PfsDentry.ReadFrom(zoneBuf, baseOff + pos);
                int stride = d.ALen & 0xFFF;
                if (stride == 0) break;
                if (d.Inode != 0)
                    yield return d;
                pos += stride;
            }
        }
    }

    /// <summary>Finds a direct child entry (not a full path) by name within one directory.</summary>
    private bool TryFindChild(uint dirInodeZone, string name, out PfsDentry found)
    {
        foreach (var entry in EnumerateDentries(dirInodeZone))
        {
            if (entry.Path == name) { found = entry; return true; }
        }
        found = new PfsDentry();
        return false;
    }

    /// <summary>Resolves a '/'-separated path (relative to root) to the INODE zone of its
    /// containing directory, plus the final component name. Intermediate components must
    /// already exist and be directories.</summary>
    private (uint parentInodeZone, string name) ResolveParent(string path)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("empty path", nameof(path));
        uint dir = _super.Root.Number;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!TryFindChild(dir, parts[i], out var child))
                throw new System.IO.DirectoryNotFoundException($"'{parts[i]}' not found");
            dir = child.Inode;
        }
        return (dir, parts[^1]);
    }

    /// <summary>Resolves a full directory path (relative to root) to its INODE zone.</summary>
    private uint ResolveDirectory(string path)
    {
        uint dir = _super.Root.Number;
        if (string.IsNullOrEmpty(path) || path == "/") return dir;
        foreach (var part in path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryFindChild(dir, part, out var child))
                throw new System.IO.DirectoryNotFoundException(path);
            dir = child.Inode;
        }
        return dir;
    }

    private void AddDentry(uint dirInodeZone, string name, uint childInodeZone, ushort typeBits)
    {
        uint dataZone = DirDataZone(dirInodeZone);
        var zoneBuf = ReadZone(dataZone);
        // header(8) + path bytes + NUL, rounded up to a 4-byte stride, matching the on-disk
        // (pLen+11)&~3-style alignment real PFS uses for dentry allocation.
        int entryBytes = Math.Max(16, (8 + name.Length + 1 + 3) & ~3);
        for (int sector = 0; sector < Pfs.SectorsPerZone; sector++)
        {
            int baseOff = sector * Pfs.SectorSize;
            int pos = 0;
            while (pos < Pfs.SectorSize)
            {
                var d = PfsDentry.ReadFrom(zoneBuf, baseOff + pos);
                int stride = d.ALen & 0xFFF;
                if (stride == 0) break;
                if (d.Inode == 0 && stride >= entryBytes)
                {
                    var newEntry = new PfsDentry { Inode = childInodeZone, Sub = 0, PathLen = (byte)name.Length, ALen = (ushort)(entryBytes | typeBits), Path = name };
                    newEntry.WriteTo(zoneBuf, baseOff + pos);
                    if (stride > entryBytes)
                    {
                        var remainder = new PfsDentry { Inode = 0, PathLen = 0, ALen = (ushort)(stride - entryBytes) };
                        remainder.WriteTo(zoneBuf, baseOff + pos + entryBytes);
                    }
                    WriteZone(dataZone, zoneBuf);
                    return;
                }
                pos += stride;
            }
        }
        throw new InvalidOperationException("directory is full (single-zone directory limit reached in this implementation)");
    }

    private void RemoveDentry(uint dirInodeZone, string name)
    {
        uint dataZone = DirDataZone(dirInodeZone);
        var zoneBuf = ReadZone(dataZone);
        for (int sector = 0; sector < Pfs.SectorsPerZone; sector++)
        {
            int baseOff = sector * Pfs.SectorSize;
            int pos = 0;
            while (pos < Pfs.SectorSize)
            {
                var d = PfsDentry.ReadFrom(zoneBuf, baseOff + pos);
                int stride = d.ALen & 0xFFF;
                if (stride == 0) break;
                if (d.Inode != 0 && d.Path == name)
                {
                    var freed = new PfsDentry { Inode = 0, PathLen = 0, ALen = (ushort)stride };
                    freed.WriteTo(zoneBuf, baseOff + pos);
                    WriteZone(dataZone, zoneBuf);
                    return;
                }
                pos += stride;
            }
        }
    }

    // ---- public file/directory API ----

    public void CreateDirectory(string path)
    {
        var (parentInodeZone, name) = ResolveParent(path);
        if (TryFindChild(parentInodeZone, name, out _))
            throw new InvalidOperationException($"'{name}' already exists");

        uint inodeZone = AllocateZone();
        uint dataZone = AllocateZone();

        var self = new PfsBlockInfo { Number = inodeZone, Subpart = 0, Count = 1 };
        var dentryBuf = new byte[Pfs.ZoneSize];
        new PfsDentry { Inode = self.Number, Sub = 0, PathLen = 1, ALen = (ushort)(12 | Pfs.FioSIfdir), Path = "." }.WriteTo(dentryBuf, 0);
        new PfsDentry { Inode = parentInodeZone, Sub = 0, PathLen = 2, ALen = (ushort)(500 | Pfs.FioSIfdir), Path = ".." }.WriteTo(dentryBuf, 12);
        InitializeFreeDentrySectors(dentryBuf, firstFreeSector: 1);
        WriteZone(dataZone, dentryBuf);

        var now = PfsDateTime.FromUtcNow(DateTime.UtcNow);
        var inode = new PfsInode
        {
            InodeBlock = self,
            LastSegment = self,
            Mode = (ushort)(Pfs.FioSIfdir | Pfs.DefaultPerm),
            Attr = 0xA0,
            Size = PfsDentry.Size,
            NumberBlocks = 2,
            NumberData = 2,
            Atime = now,
            Ctime = now,
            Mtime = now,
        };
        inode.Data[0] = new PfsBlockInfo { Number = dataZone, Subpart = 0, Count = 1 };
        WriteInode(inodeZone, inode);

        AddDentry(parentInodeZone, name, inodeZone, Pfs.FioSIfdir);
    }

    public void WriteFile(string path, ReadOnlySpan<byte> data)
    {
        var (parentInodeZone, name) = ResolveParent(path);
        if (TryFindChild(parentInodeZone, name, out var existing))
        {
            DeleteInodeAndData(existing.Inode);
            RemoveDentry(parentInodeZone, name);
        }

        int zonesNeeded = (data.Length + Pfs.ZoneSize - 1) / Pfs.ZoneSize;
        if (zonesNeeded > Pfs.InodeMaxBlocks)
            throw new NotSupportedException($"file too large for a single-segment inode ({zonesNeeded} zones > {Pfs.InodeMaxBlocks} max) — segment continuation isn't implemented yet");

        uint inodeZone = AllocateZone();
        var dataZones = new uint[Math.Max(zonesNeeded, 1)];
        for (int i = 0; i < zonesNeeded; i++)
        {
            uint z = AllocateZone();
            dataZones[i] = z;
            var zoneBuf = new byte[Pfs.ZoneSize];
            int srcOff = i * Pfs.ZoneSize;
            int n = Math.Min(Pfs.ZoneSize, data.Length - srcOff);
            data.Slice(srcOff, n).CopyTo(zoneBuf);
            WriteZone(z, zoneBuf);
        }

        var now = PfsDateTime.FromUtcNow(DateTime.UtcNow);
        var self = new PfsBlockInfo { Number = inodeZone, Subpart = 0, Count = 1 };
        var inode = new PfsInode
        {
            InodeBlock = self,
            LastSegment = self,
            Mode = (ushort)(Pfs.FioSIfreg | Pfs.DefaultPerm),
            Attr = 0x80,
            Size = (ulong)data.Length,
            NumberBlocks = (uint)zonesNeeded,
            NumberData = (uint)zonesNeeded,
            Atime = now,
            Ctime = now,
            Mtime = now,
        };
        for (int i = 0; i < zonesNeeded; i++)
            inode.Data[i] = new PfsBlockInfo { Number = dataZones[i], Subpart = 0, Count = 1 };
        WriteInode(inodeZone, inode);

        AddDentry(parentInodeZone, name, inodeZone, Pfs.FioSIfreg);
    }

    public byte[] ReadFile(string path)
    {
        var (parentInodeZone, name) = ResolveParent(path);
        if (!TryFindChild(parentInodeZone, name, out var entry))
            throw new System.IO.FileNotFoundException(path);
        var inode = ReadInode(entry.Inode);
        var result = new byte[inode.Size];
        int written = 0;
        for (int i = 0; i < inode.NumberData && written < result.Length; i++)
        {
            var zoneBuf = ReadZone(inode.Data[i].Number);
            int n = Math.Min(Pfs.ZoneSize, result.Length - written);
            Array.Copy(zoneBuf, 0, result, written, n);
            written += n;
        }
        return result;
    }

    public bool FileExists(string path)
    {
        try
        {
            var (parentInodeZone, name) = ResolveParent(path);
            return TryFindChild(parentInodeZone, name, out _);
        }
        catch (System.IO.DirectoryNotFoundException) { return false; }
    }

    public IReadOnlyList<(string Name, bool IsDirectory, ulong Size)> ListDirectory(string path)
    {
        uint dirInodeZone = ResolveDirectory(path);
        var results = new List<(string, bool, ulong)>();
        foreach (var entry in EnumerateDentries(dirInodeZone))
        {
            if (entry.Path == "." || entry.Path == "..") continue;
            bool isDir = (entry.ALen & Pfs.FioSIfmt) == Pfs.FioSIfdir;
            ulong size = isDir ? 0 : ReadInode(entry.Inode).Size;
            results.Add((entry.Path, isDir, size));
        }
        return results;
    }

    public void DeleteFile(string path)
    {
        var (parentInodeZone, name) = ResolveParent(path);
        if (!TryFindChild(parentInodeZone, name, out var entry))
            throw new System.IO.FileNotFoundException(path);
        DeleteInodeAndData(entry.Inode);
        RemoveDentry(parentInodeZone, name);
    }

    private void DeleteInodeAndData(uint inodeZone)
    {
        var inode = ReadInode(inodeZone);
        for (int i = 0; i < inode.NumberData; i++)
            FreeZone(inode.Data[i].Number);
        FreeZone(inodeZone);
    }
}
