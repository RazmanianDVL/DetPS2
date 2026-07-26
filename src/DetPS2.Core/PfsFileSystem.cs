using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Real Sony PFS (PlayStation File System) — the filesystem the PS2 HDD Utility Disc and every
/// homebrew HDD tool writes on top of an APA PFS-type partition (<see cref="Apa.TypePfs"/>).
/// Struct layout, field names, checksum algorithm, zone-bitmap sizing formulas and the format
/// (mkfs) sequence are verified against real ps2sdk source (github.com/ps2dev/ps2sdk):
///   iop/hdd/libpfs/include/libpfs.h (pfs_super_block_t/pfs_inode_t/pfs_dentry_t/
///     pfs_blockinfo_t/pfs_datetime_t, PFS_SUPER_MAGIC, PFS_SUPER_SECTOR/BACKUP_SECTOR,
///     PFS_BLOCKSIZE, PFS_INODE_MAX_BLOCKS, PFS_FORMAT_VERSION),
///   iop/hdd/libpfs/src/super.c (pfsGetBitmapSizeSectors/Blocks — the exact, non-obvious
///     sizing formulas used below), iop/hdd/libpfs/src/superWrite.c (pfsFormat/pfsFormatSub —
///     the exact reserved-zone/log/root layout sequence), iop/hdd/libpfs/src/inode.c
///     (pfsInodeCheckSum: same algorithm as APA's checksum, over the inode's 256 u32 words),
///   iop/hdd/libpfs/src/dir.c (pfsInodeFill, pfsFillSelfAndParentDentries, pfsFillDentry),
///   common/include/iox_stat.h (FIO_S_IFDIR=0x1000, FIO_S_IFREG=0x2000, FIO_S_IFMT=0xF000).
///
/// Scope for this pass ("foundation" — see DEVELOPER_GUIDE.md): a single main partition
/// (num_subs=0), fixed zone_size = PFS_BLOCKSIZE (8192 bytes = 16 sectors, Sony's own
/// default), single-segment inodes (files up to PFS_INODE_MAX_BLOCKS=114 direct zones —
/// 114 * 8192 = ~912KB, generous for a save file). Segment-continuation inodes (for files
/// larger than that) and multi-sub-partition volumes are real PFS features not yet
/// implemented here — documented as follow-up work, not silently approximated.
/// </summary>
public static class Pfs
{
    public const int SectorSize = 512;
    public const int ZoneSize = 0x2000;      // PFS_BLOCKSIZE — bytes per zone (default)
    public const int SectorsPerZone = ZoneSize / SectorSize; // 16
    public const int SectorScale = 4;        // log2(SectorsPerZone) — pfsGetScale(ZoneSize,512)
    public const int MetaSize = 1024;        // bitmap chunk / inode / superblock buffer size
    public const int BitsPerBitmapChunk = MetaSize * 8; // 8192 bits per 1024-byte chunk

    public const uint SuperMagic = 0x50465300; // "PFS\0"
    public const uint SegdMagic = 0x53454744;  // "SEGD"
    public const uint SegiMagic = 0x53454749;  // "SEGI"
    public const int FormatVersion = 3;
    public const int MaxSubparts = 64;
    public const int NameLen = 255;
    public const int InodeMaxBlocks = 114;

    public const uint SuperSector = 8192;
    public const uint SuperBackupSector = 8193;

    public const ushort FioSIfmt = 0xF000;
    public const ushort FioSIfreg = 0x2000;
    public const ushort FioSIfdir = 0x1000;
    public const ushort DefaultPerm = 0x1FF; // rwxrwxrwx — matches Sony's own root (0x11FF)

    public const ushort Uid = 0xFFFF;
    public const ushort Gid = 0xFFFF;

    /// <summary>pfsGetBitmapSizeSectors: verified verbatim from super.c — deliberately NOT a
    /// simple ceiling-division; replicated exactly (including its own rounding quirks) for
    /// on-disk compatibility with real PFS.</summary>
    public static uint GetBitmapSizeSectors(int zoneScale, uint partitionSectors)
    {
        uint zones = partitionSectors / (1u << zoneScale);
        uint w = zones & 7;
        zones = zones / 8 + w;
        w = zones & 511;
        return zones / 512 + w;
    }

    /// <summary>pfsGetBitmapSizeBlocks: ceil(bitmapSectors / sectorsPerZone).</summary>
    public static uint GetBitmapSizeBlocks(int scale, uint partitionSectors)
    {
        uint sectors = GetBitmapSizeSectors(scale, partitionSectors);
        return sectors / (1u << scale) + (sectors % (1u << scale) > 0 ? 1u : 0u);
    }
}

/// <summary>pfs_datetime_t — 8 bytes, identical layout to <see cref="ApaTime"/>.</summary>
public struct PfsDateTime
{
    public byte Unused, Sec, Min, Hour, Day, Month;
    public ushort Year;

    public void WriteTo(byte[] buf, int off)
    {
        buf[off + 0] = Unused; buf[off + 1] = Sec; buf[off + 2] = Min; buf[off + 3] = Hour;
        buf[off + 4] = Day; buf[off + 5] = Month;
        BitConverter.GetBytes(Year).CopyTo(buf, off + 6);
    }

    public static PfsDateTime ReadFrom(byte[] buf, int off) => new PfsDateTime
    {
        Unused = buf[off + 0], Sec = buf[off + 1], Min = buf[off + 2], Hour = buf[off + 3],
        Day = buf[off + 4], Month = buf[off + 5], Year = BitConverter.ToUInt16(buf, off + 6),
    };

    public static PfsDateTime FromUtcNow(DateTime utc) => new PfsDateTime
    {
        Sec = (byte)utc.Second, Min = (byte)utc.Minute, Hour = (byte)utc.Hour,
        Day = (byte)utc.Day, Month = (byte)utc.Month, Year = (ushort)utc.Year,
    };
}

/// <summary>pfs_blockinfo_t — 8 bytes: a (subpart, zone-number, zone-count) run.</summary>
public struct PfsBlockInfo
{
    public uint Number;
    public ushort Subpart;
    public ushort Count;

    public void WriteTo(byte[] buf, int off)
    {
        BitConverter.GetBytes(Number).CopyTo(buf, off);
        BitConverter.GetBytes(Subpart).CopyTo(buf, off + 4);
        BitConverter.GetBytes(Count).CopyTo(buf, off + 6);
    }

    public static PfsBlockInfo ReadFrom(byte[] buf, int off) => new PfsBlockInfo
    {
        Number = BitConverter.ToUInt32(buf, off),
        Subpart = BitConverter.ToUInt16(buf, off + 4),
        Count = BitConverter.ToUInt16(buf, off + 6),
    };
}

/// <summary>pfs_super_block_t — logical size 40 bytes, stored in its own 512-byte sector.</summary>
public sealed class PfsSuperBlock
{
    public uint Magic = Pfs.SuperMagic;
    public uint Version = Pfs.FormatVersion;
    public uint ModVer;
    public uint FsckStat;
    public uint ZoneSize = Pfs.ZoneSize;
    public uint NumSubs;
    public PfsBlockInfo Log;
    public PfsBlockInfo Root;

    public byte[] ToBytes()
    {
        var buf = new byte[Pfs.SectorSize];
        BitConverter.GetBytes(Magic).CopyTo(buf, 0);
        BitConverter.GetBytes(Version).CopyTo(buf, 4);
        BitConverter.GetBytes(ModVer).CopyTo(buf, 8);
        BitConverter.GetBytes(FsckStat).CopyTo(buf, 12);
        BitConverter.GetBytes(ZoneSize).CopyTo(buf, 16);
        BitConverter.GetBytes(NumSubs).CopyTo(buf, 20);
        Log.WriteTo(buf, 24);
        Root.WriteTo(buf, 32);
        return buf;
    }

    public static PfsSuperBlock FromBytes(byte[] buf) => new PfsSuperBlock
    {
        Magic = BitConverter.ToUInt32(buf, 0),
        Version = BitConverter.ToUInt32(buf, 4),
        ModVer = BitConverter.ToUInt32(buf, 8),
        FsckStat = BitConverter.ToUInt32(buf, 12),
        ZoneSize = BitConverter.ToUInt32(buf, 16),
        NumSubs = BitConverter.ToUInt32(buf, 20),
        Log = PfsBlockInfo.ReadFrom(buf, 24),
        Root = PfsBlockInfo.ReadFrom(buf, 32),
    };
}

/// <summary>pfs_inode_t — 1024 bytes (256 u32 words), checksummed the same way as
/// <see cref="ApaHeader"/> (sum of words[1..255]).</summary>
public sealed class PfsInode
{
    public uint Checksum;
    public uint Magic = Pfs.SegdMagic;
    public PfsBlockInfo InodeBlock;
    public PfsBlockInfo NextSegment;
    public PfsBlockInfo LastSegment;
    public PfsBlockInfo Unused;
    public readonly PfsBlockInfo[] Data = new PfsBlockInfo[Pfs.InodeMaxBlocks];
    public ushort Mode;
    public ushort Attr;
    public ushort Uid = Pfs.Uid;
    public ushort Gid = Pfs.Gid;
    public PfsDateTime Atime, Ctime, Mtime;
    public ulong Size;
    public uint NumberBlocks;
    public uint NumberData;
    public uint NumberSegdesc;
    public uint Subpart;

    private const int OffChecksum = 0;
    private const int OffMagic = 4;
    private const int OffInodeBlock = 8;
    private const int OffNextSegment = 16;
    private const int OffLastSegment = 24;
    private const int OffUnused = 32;
    private const int OffData = 40;                      // [114] * 8 = 912, ends 952
    private const int OffMode = OffData + Pfs.InodeMaxBlocks * 8;
    private const int OffAttr = OffMode + 2;
    private const int OffUid = OffAttr + 2;
    private const int OffGid = OffUid + 2;
    private const int OffAtime = OffGid + 2;
    private const int OffCtime = OffAtime + 8;
    private const int OffMtime = OffCtime + 8;
    private const int OffSize = OffMtime + 8;
    private const int OffNumberBlocks = OffSize + 8;
    private const int OffNumberData = OffNumberBlocks + 4;
    private const int OffNumberSegdesc = OffNumberData + 4;
    private const int OffSubpart = OffNumberSegdesc + 4;
    // + reserved[4] (16 bytes) -> total 1024

    public byte[] ToBytes()
    {
        var buf = new byte[Pfs.MetaSize];
        BitConverter.GetBytes(Magic).CopyTo(buf, OffMagic);
        InodeBlock.WriteTo(buf, OffInodeBlock);
        NextSegment.WriteTo(buf, OffNextSegment);
        LastSegment.WriteTo(buf, OffLastSegment);
        Unused.WriteTo(buf, OffUnused);
        for (int i = 0; i < Pfs.InodeMaxBlocks; i++)
            Data[i].WriteTo(buf, OffData + i * 8);
        BitConverter.GetBytes(Mode).CopyTo(buf, OffMode);
        BitConverter.GetBytes(Attr).CopyTo(buf, OffAttr);
        BitConverter.GetBytes(Uid).CopyTo(buf, OffUid);
        BitConverter.GetBytes(Gid).CopyTo(buf, OffGid);
        Atime.WriteTo(buf, OffAtime);
        Ctime.WriteTo(buf, OffCtime);
        Mtime.WriteTo(buf, OffMtime);
        BitConverter.GetBytes(Size).CopyTo(buf, OffSize);
        BitConverter.GetBytes(NumberBlocks).CopyTo(buf, OffNumberBlocks);
        BitConverter.GetBytes(NumberData).CopyTo(buf, OffNumberData);
        BitConverter.GetBytes(NumberSegdesc).CopyTo(buf, OffNumberSegdesc);
        BitConverter.GetBytes(Subpart).CopyTo(buf, OffSubpart);
        Checksum = ComputeChecksum(buf);
        BitConverter.GetBytes(Checksum).CopyTo(buf, OffChecksum);
        return buf;
    }

    public static PfsInode FromBytes(byte[] buf)
    {
        var n = new PfsInode
        {
            Checksum = BitConverter.ToUInt32(buf, OffChecksum),
            Magic = BitConverter.ToUInt32(buf, OffMagic),
            InodeBlock = PfsBlockInfo.ReadFrom(buf, OffInodeBlock),
            NextSegment = PfsBlockInfo.ReadFrom(buf, OffNextSegment),
            LastSegment = PfsBlockInfo.ReadFrom(buf, OffLastSegment),
            Unused = PfsBlockInfo.ReadFrom(buf, OffUnused),
            Mode = BitConverter.ToUInt16(buf, OffMode),
            Attr = BitConverter.ToUInt16(buf, OffAttr),
            Uid = BitConverter.ToUInt16(buf, OffUid),
            Gid = BitConverter.ToUInt16(buf, OffGid),
            Atime = PfsDateTime.ReadFrom(buf, OffAtime),
            Ctime = PfsDateTime.ReadFrom(buf, OffCtime),
            Mtime = PfsDateTime.ReadFrom(buf, OffMtime),
            Size = BitConverter.ToUInt64(buf, OffSize),
            NumberBlocks = BitConverter.ToUInt32(buf, OffNumberBlocks),
            NumberData = BitConverter.ToUInt32(buf, OffNumberData),
            NumberSegdesc = BitConverter.ToUInt32(buf, OffNumberSegdesc),
            Subpart = BitConverter.ToUInt32(buf, OffSubpart),
        };
        for (int i = 0; i < Pfs.InodeMaxBlocks; i++)
            n.Data[i] = PfsBlockInfo.ReadFrom(buf, OffData + i * 8);
        return n;
    }

    /// <summary>pfsInodeCheckSum: sum of the inode's 256 u32 words, skipping word 0
    /// (the checksum field itself) — same algorithm as ApaHeader's checksum.</summary>
    public static uint ComputeChecksum(byte[] inodeBytes)
    {
        uint sum = 0;
        for (int i = 1; i < 256; i++)
            sum += BitConverter.ToUInt32(inodeBytes, i * 4);
        return sum;
    }
}

/// <summary>pfs_dentry_t — 512 bytes. `aLen` doubles as both the entry's allocated stride
/// (low 12 bits, `aLen & 0xFFF`) to the next entry in the same 512-byte chunk, and the
/// entry's file-type bits (`aLen & FIO_S_IFMT`) — verified against dir.c's pfsFillDentry.</summary>
public sealed class PfsDentry
{
    public uint Inode;
    public byte Sub;
    public byte PathLen;
    public ushort ALen;
    public string Path = "";

    public const int Size = 512;
    private const int OffInode = 0;
    private const int OffSub = 4;
    private const int OffPLen = 5;
    private const int OffALen = 6;
    private const int OffPath = 8;

    public void WriteTo(byte[] buf, int off)
    {
        BitConverter.GetBytes(Inode).CopyTo(buf, off + OffInode);
        buf[off + OffSub] = Sub;
        buf[off + OffPLen] = PathLen;
        BitConverter.GetBytes(ALen).CopyTo(buf, off + OffALen);
        var pathBytes = Encoding.ASCII.GetBytes(Path);
        int n = Math.Min(pathBytes.Length, Size - OffPath - 1);
        Array.Copy(pathBytes, 0, buf, off + OffPath, n);
    }

    public static PfsDentry ReadFrom(byte[] buf, int off)
    {
        byte pLen = buf[off + OffPLen];
        int n = 0;
        while (n < pLen && n < Size - OffPath - 1 && buf[off + OffPath + n] != 0) n++;
        return new PfsDentry
        {
            Inode = BitConverter.ToUInt32(buf, off + OffInode),
            Sub = buf[off + OffSub],
            PathLen = pLen,
            ALen = BitConverter.ToUInt16(buf, off + OffALen),
            Path = Encoding.ASCII.GetString(buf, off + OffPath, n),
        };
    }

    public static PfsDentry MakeFilled(string name, uint inodeNumber, byte subpart, ushort strideAndType)
    {
        var d = new PfsDentry { Inode = inodeNumber, Sub = subpart, PathLen = (byte)name.Length, ALen = strideAndType, Path = name };
        return d;
    }
}
