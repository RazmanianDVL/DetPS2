using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Real Sony APA (Aligned Partition Allocation) partition table — the format the PS2's HDD
/// Utility Disc and every homebrew HDD tool (uLaunchELF, OPL, PFS Shell, hdl_dump) writes to
/// a PlayStation 2 hard drive. Struct layout, field names, checksum algorithm and constants
/// verified against real ps2sdk source (github.com/ps2dev/ps2sdk):
///   iop/hdd/libapa/include/libapa.h (apa_header_t/apa_sub_t/apa_ps2time_t, APA_MAGIC),
///   common/include/hdd-ioctl.h (APA_IDMAX/APA_MAXSUB/APA_PASSMAX/APA_TYPE_*),
///   iop/hdd/libapa/src/apa.c (apaCheckSum: sum of the header's 256 u32 words, skipping the
///   checksum field itself at word 0).
///
/// One apa_header_t occupies exactly two 512-byte sectors (1024 bytes = 256 x 4-byte words).
/// Every partition on the disk (including the disk's own MBR-equivalent "self" header at
/// sector 0) is one of these headers, chained via next/prev sector offsets — a real, on-disk
/// doubly-linked list, exactly like a real PS2 HDD.
/// </summary>
public static class Apa
{
    public const int SectorSize = 512;
    public const int HeaderSectors = 2;
    public const int HeaderBytes = HeaderSectors * SectorSize; // 1024

    public const uint Magic = 0x00415041; // 'APA\0'
    public const int IdMax = 32;
    public const int PassMax = 8;
    public const int MaxSub = 64;
    public const uint MbrVersion = 2;

    // Partition types (hdd-ioctl.h) — the "mode" field games/tools see via getstat/dread.
    public const ushort TypeFree = 0x0000;
    public const ushort TypeMbr = 0x0001;
    public const ushort TypeExt2Swap = 0x0082;
    public const ushort TypeExt2 = 0x0083;
    public const ushort TypeReiser = 0x0088;
    public const ushort TypePfs = 0x0100;
    public const ushort TypeCfs = 0x0101;
    public const ushort TypeHdl = 0x1337;

    public const ushort FlagSub = 0x0001;

    // Sector 0 always holds the disk's own "self" header (id "PlayStation2", type MBR) —
    // the real PS2 BIOS/HDD Utility Disc convention.
    public const uint SectorMbr = 0;
    public const string MbrId = "PlayStation2";
}

/// <summary>APA on-disk timestamp (apa_ps2time_t) — 8 bytes, BCD-free plain fields.</summary>
public struct ApaTime
{
    public byte Unused, Sec, Min, Hour, Day, Month;
    public ushort Year;

    public void WriteTo(byte[] buf, int off)
    {
        buf[off + 0] = Unused;
        buf[off + 1] = Sec;
        buf[off + 2] = Min;
        buf[off + 3] = Hour;
        buf[off + 4] = Day;
        buf[off + 5] = Month;
        BitConverter.GetBytes(Year).CopyTo(buf, off + 6);
    }

    public static ApaTime ReadFrom(byte[] buf, int off) => new ApaTime
    {
        Unused = buf[off + 0],
        Sec = buf[off + 1],
        Min = buf[off + 2],
        Hour = buf[off + 3],
        Day = buf[off + 4],
        Month = buf[off + 5],
        Year = BitConverter.ToUInt16(buf, off + 6),
    };

    public static ApaTime FromUtcNow(DateTime utc) => new ApaTime
    {
        Sec = (byte)utc.Second,
        Min = (byte)utc.Minute,
        Hour = (byte)utc.Hour,
        Day = (byte)utc.Day,
        Month = (byte)utc.Month,
        Year = (ushort)utc.Year,
    };
}

/// <summary>Sub-partition block range (apa_sub_t) — 8 bytes.</summary>
public struct ApaSub
{
    public uint Start;  // sector address
    public uint Length; // sector count

    public void WriteTo(byte[] buf, int off)
    {
        BitConverter.GetBytes(Start).CopyTo(buf, off);
        BitConverter.GetBytes(Length).CopyTo(buf, off + 4);
    }

    public static ApaSub ReadFrom(byte[] buf, int off) => new ApaSub
    {
        Start = BitConverter.ToUInt32(buf, off),
        Length = BitConverter.ToUInt32(buf, off + 4),
    };
}

/// <summary>
/// A single apa_header_t (1024 bytes = 2 sectors) — one partition table entry. Field layout
/// matches real ps2sdk's apa_header_t exactly (see class-level doc on <see cref="Apa"/>).
/// </summary>
public sealed class ApaHeader
{
    public uint Checksum;
    public uint Magic = Apa.Magic;
    public uint Next;   // sector of next partition in the chain (0 = none)
    public uint Prev;   // sector of previous partition in the chain (0 = none)
    public string Id = "";
    public string Rpwd = "";
    public string Fpwd = "";
    public uint Start;  // this partition's own starting sector
    public uint Length; // sector count
    public ushort Type;
    public ushort Flags;
    public uint Nsub;   // number of sub-partitions attached (main partitions only)
    public ApaTime Created;
    public uint Main;   // for sub-partitions: sector of the owning main partition
    public uint Number; // partition index
    public uint ModVer;
    public string MbrMagic = "";      // only meaningful for the sector-0 "self" header
    public uint MbrVersion2 = Apa.MbrVersion;
    public uint MbrNSector;
    public ApaTime MbrCreated;
    public uint OsdStart;
    public uint OsdSize;
    public readonly ApaSub[] Subs = new ApaSub[Apa.MaxSub];

    // Byte offsets within the 1024-byte header (verified against apa_header_t field order).
    private const int OffChecksum = 0;
    private const int OffMagic = 4;
    private const int OffNext = 8;
    private const int OffPrev = 12;
    private const int OffId = 16;                         // [32]
    private const int OffRpwd = OffId + Apa.IdMax;         // [8]
    private const int OffFpwd = OffRpwd + Apa.PassMax;     // [8]
    private const int OffStart = OffFpwd + Apa.PassMax;
    private const int OffLength = OffStart + 4;
    private const int OffType = OffLength + 4;
    private const int OffFlags = OffType + 2;
    private const int OffNsub = OffFlags + 2;
    private const int OffCreated = OffNsub + 4;            // ApaTime, 8 bytes
    private const int OffMain = OffCreated + 8;
    private const int OffNumber = OffMain + 4;
    private const int OffModVer = OffNumber + 4;
    private const int OffPad1 = OffModVer + 4;             // u32[7] = 28 bytes
    private const int OffPad2 = OffPad1 + 28;              // char[128]
    private const int OffMbr = OffPad2 + 128;               // mbr sub-struct starts here
    private const int OffMbrMagic = OffMbr;                // char[32]
    private const int OffMbrVersion = OffMbrMagic + 32;
    private const int OffMbrNSector = OffMbrVersion + 4;
    private const int OffMbrCreated = OffMbrNSector + 4;   // ApaTime, 8 bytes
    private const int OffOsdStart = OffMbrCreated + 8;
    private const int OffOsdSize = OffOsdStart + 4;
    private const int OffMbrPad = OffOsdSize + 4;           // char[200] (non-GPT layout)
    private const int OffSubs = OffMbr + 256;               // mbr sub-struct is 256 bytes total
    // 16 (fixed) + 32+8+8 (id/rpwd/fpwd) + 4+4+2+2+4+8+4+4+4 (start..modver) + 28+128 (pad1/pad2)
    // + 256 (mbr) = 512 bytes of fixed fields; subs[64] * 8 bytes = 512 bytes -> 1024 total.

    public static ApaHeader CreateEmpty(string id, uint start, uint length, ushort type, uint number)
    {
        return new ApaHeader
        {
            Id = id,
            Start = start,
            Length = length,
            Type = type,
            Number = number,
            Created = ApaTime.FromUtcNow(DateTime.UtcNow),
        };
    }

    public byte[] ToBytes()
    {
        var buf = new byte[Apa.HeaderBytes];
        WriteFixed(Id, Apa.IdMax, buf, OffId);
        WriteFixed(Rpwd, Apa.PassMax, buf, OffRpwd);
        WriteFixed(Fpwd, Apa.PassMax, buf, OffFpwd);
        BitConverter.GetBytes(Start).CopyTo(buf, OffStart);
        BitConverter.GetBytes(Length).CopyTo(buf, OffLength);
        BitConverter.GetBytes(Type).CopyTo(buf, OffType);
        BitConverter.GetBytes(Flags).CopyTo(buf, OffFlags);
        BitConverter.GetBytes(Nsub).CopyTo(buf, OffNsub);
        Created.WriteTo(buf, OffCreated);
        BitConverter.GetBytes(Main).CopyTo(buf, OffMain);
        BitConverter.GetBytes(Number).CopyTo(buf, OffNumber);
        BitConverter.GetBytes(ModVer).CopyTo(buf, OffModVer);
        WriteFixed(MbrMagic, 32, buf, OffMbrMagic);
        BitConverter.GetBytes(MbrVersion2).CopyTo(buf, OffMbrVersion);
        BitConverter.GetBytes(MbrNSector).CopyTo(buf, OffMbrNSector);
        MbrCreated.WriteTo(buf, OffMbrCreated);
        BitConverter.GetBytes(OsdStart).CopyTo(buf, OffOsdStart);
        BitConverter.GetBytes(OsdSize).CopyTo(buf, OffOsdSize);
        for (int i = 0; i < Apa.MaxSub; i++)
            Subs[i].WriteTo(buf, OffSubs + i * 8);
        BitConverter.GetBytes(Magic).CopyTo(buf, OffMagic);
        BitConverter.GetBytes(Next).CopyTo(buf, OffNext);
        BitConverter.GetBytes(Prev).CopyTo(buf, OffPrev);
        Checksum = ComputeChecksum(buf);
        BitConverter.GetBytes(Checksum).CopyTo(buf, OffChecksum);
        return buf;
    }

    public static ApaHeader FromBytes(byte[] buf)
    {
        if (buf.Length != Apa.HeaderBytes)
            throw new ArgumentException($"APA header must be {Apa.HeaderBytes} bytes", nameof(buf));
        var h = new ApaHeader
        {
            Checksum = BitConverter.ToUInt32(buf, OffChecksum),
            Magic = BitConverter.ToUInt32(buf, OffMagic),
            Next = BitConverter.ToUInt32(buf, OffNext),
            Prev = BitConverter.ToUInt32(buf, OffPrev),
            Id = ReadFixed(buf, OffId, Apa.IdMax),
            Rpwd = ReadFixed(buf, OffRpwd, Apa.PassMax),
            Fpwd = ReadFixed(buf, OffFpwd, Apa.PassMax),
            Start = BitConverter.ToUInt32(buf, OffStart),
            Length = BitConverter.ToUInt32(buf, OffLength),
            Type = BitConverter.ToUInt16(buf, OffType),
            Flags = BitConverter.ToUInt16(buf, OffFlags),
            Nsub = BitConverter.ToUInt32(buf, OffNsub),
            Created = ApaTime.ReadFrom(buf, OffCreated),
            Main = BitConverter.ToUInt32(buf, OffMain),
            Number = BitConverter.ToUInt32(buf, OffNumber),
            ModVer = BitConverter.ToUInt32(buf, OffModVer),
            MbrMagic = ReadFixed(buf, OffMbrMagic, 32),
            MbrVersion2 = BitConverter.ToUInt32(buf, OffMbrVersion),
            MbrNSector = BitConverter.ToUInt32(buf, OffMbrNSector),
            MbrCreated = ApaTime.ReadFrom(buf, OffMbrCreated),
            OsdStart = BitConverter.ToUInt32(buf, OffOsdStart),
            OsdSize = BitConverter.ToUInt32(buf, OffOsdSize),
        };
        for (int i = 0; i < Apa.MaxSub; i++)
            h.Subs[i] = ApaSub.ReadFrom(buf, OffSubs + i * 8);
        return h;
    }

    /// <summary>apaCheckSum: sum of all 256 u32 words in the header except the checksum
    /// field itself (word index 0) — verified against apa.c's apaCheckSum().</summary>
    public static uint ComputeChecksum(byte[] headerBytes)
    {
        uint sum = 0;
        for (int i = 1; i < 256; i++)
            sum += BitConverter.ToUInt32(headerBytes, i * 4);
        return sum;
    }

    public bool VerifyChecksum(byte[] headerBytes) => ComputeChecksum(headerBytes) == BitConverter.ToUInt32(headerBytes, 0);

    private static void WriteFixed(string s, int len, byte[] buf, int off)
    {
        var bytes = Encoding.ASCII.GetBytes(s ?? "");
        int n = Math.Min(bytes.Length, len - 1); // leave room for implicit NUL
        Array.Copy(bytes, 0, buf, off, n);
    }

    private static string ReadFixed(byte[] buf, int off, int len)
    {
        int n = 0;
        while (n < len && buf[off + n] != 0) n++;
        return Encoding.ASCII.GetString(buf, off, n);
    }
}

/// <summary>
/// A raw APA-formatted disk image, backed by a plain byte array (host-side; the caller decides
/// whether/how to persist it to a file). Models the real on-disk structure: sector 0 holds the
/// disk's own "self" header (id "PlayStation2", type MBR), and every partition is chained off
/// it via Next/Prev sector links, exactly like a real Sony-formatted or OPL/uLaunchELF-formatted
/// PS2 HDD — a tool that understands real APA (PFS Shell, hdl_dump, WinHIIP) should be able to
/// read a disk built by this class.
/// </summary>
public sealed class ApaDisk
{
    public byte[] Data { get; }
    public uint TotalSectors { get; }

    public ApaDisk(uint totalSectors)
    {
        if (totalSectors < Apa.HeaderSectors + 1)
            throw new ArgumentException("disk too small", nameof(totalSectors));
        TotalSectors = totalSectors;
        Data = new byte[(long)totalSectors * Apa.SectorSize];
    }

    public ApaDisk(byte[] existingImage)
    {
        if (existingImage.Length % Apa.SectorSize != 0)
            throw new ArgumentException("image size must be a multiple of the sector size", nameof(existingImage));
        Data = existingImage;
        TotalSectors = (uint)(existingImage.Length / Apa.SectorSize);
    }

    private void WriteHeader(uint sector, ApaHeader header)
    {
        var bytes = header.ToBytes();
        Array.Copy(bytes, 0, Data, (long)sector * Apa.SectorSize, Apa.HeaderBytes);
    }

    private ApaHeader ReadHeader(uint sector)
    {
        var bytes = new byte[Apa.HeaderBytes];
        Array.Copy(Data, (long)sector * Apa.SectorSize, bytes, 0, Apa.HeaderBytes);
        return ApaHeader.FromBytes(bytes);
    }

    /// <summary>Initializes an empty disk: writes the sector-0 "self" header with no
    /// partitions chained yet. Must be called before <see cref="CreatePartition"/>.</summary>
    public void FormatDisk()
    {
        var self = ApaHeader.CreateEmpty(Apa.MbrId, Apa.SectorMbr, Apa.HeaderSectors, Apa.TypeMbr, 0);
        self.MbrMagic = Apa.MbrId;
        self.MbrNSector = TotalSectors;
        self.MbrCreated = self.Created;
        self.Next = 0;
        self.Prev = 0;
        WriteHeader(Apa.SectorMbr, self);
    }

    /// <summary>
    /// Creates a new top-level (main) partition, appending it to the end of the existing
    /// chain (first-fit from the end of the last partition, matching how real HDD tools lay
    /// consecutive partitions out end-to-end). Returns the sector the new partition's header
    /// was written at.
    /// </summary>
    public uint CreatePartition(string id, uint lengthSectors, ushort type)
    {
        if (FindPartitionSector(id) != 0)
            throw new InvalidOperationException($"partition '{id}' already exists");

        var self = ReadHeader(Apa.SectorMbr);
        uint lastSector = Apa.SectorMbr;
        var last = self;
        uint number = 1;
        while (last.Next != 0)
        {
            lastSector = last.Next;
            last = ReadHeader(lastSector);
            number++;
        }

        uint newStart = lastSector == Apa.SectorMbr
            ? Apa.SectorMbr + Apa.HeaderSectors
            : last.Start + last.Length;
        if ((ulong)newStart + lengthSectors > TotalSectors)
            throw new InvalidOperationException("not enough space on disk for this partition");

        var header = ApaHeader.CreateEmpty(id, newStart, lengthSectors, type, number);
        header.Prev = lastSector;
        header.Next = 0;
        WriteHeader(newStart, header);

        last.Next = newStart;
        if (lastSector == Apa.SectorMbr)
            WriteHeader(Apa.SectorMbr, last);
        else
            WriteHeader(lastSector, last);

        return newStart;
    }

    /// <summary>Returns the sector a partition's header lives at, or 0 if not found
    /// (0 is never a valid partition sector — it's always the disk's own self header).</summary>
    public uint FindPartitionSector(string id)
    {
        var self = ReadHeader(Apa.SectorMbr);
        uint sector = self.Next;
        while (sector != 0)
        {
            var h = ReadHeader(sector);
            if (h.Id == id) return sector;
            sector = h.Next;
        }
        return 0;
    }

    public ApaHeader? FindPartition(string id)
    {
        uint sector = FindPartitionSector(id);
        return sector == 0 ? null : ReadHeader(sector);
    }

    public IEnumerable<ApaHeader> ListPartitions()
    {
        var self = ReadHeader(Apa.SectorMbr);
        uint sector = self.Next;
        while (sector != 0)
        {
            var h = ReadHeader(sector);
            yield return h;
            sector = h.Next;
        }
    }

    /// <summary>Absolute byte offset into <see cref="Data"/> for sector-relative reads/writes
    /// within a partition's own data area (i.e. sectors after its own 2-sector header).</summary>
    public long PartitionDataByteOffset(ApaHeader partition, uint sectorWithinPartition) =>
        (long)(partition.Start + Apa.HeaderSectors + sectorWithinPartition) * Apa.SectorSize;
}
