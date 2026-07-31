using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Memory-card image formats understood by DetPS2 / MCMAN dual-format HLE.
/// libmc <c>sceMcTypePS1</c>=1, <c>sceMcTypePS2</c>=2.
/// </summary>
public enum McCardType : int
{
    None = 0,
    Ps1 = 1,
    Ps2 = 2,
}

/// <summary>
/// On-disk image kind. DetPS2 native remains the default HLE save path;
/// Sony PS2 / PS1 layouts implement dual-format FAT as far as MCSERV residual requires
/// (superblock, IFC/FAT free-space, directory + named file I/O). ECC/wear-leveling residual.
/// </summary>
public enum McImageKind : byte
{
    /// <summary>DetPS2 private layout: magic "DETPS2MC", flat directory after superblock.</summary>
    DetPs2Native = 0,
    /// <summary>Sony PS2 MCFS: magic "Sony PS2 Memory Card Format", IFC+FAT+root dir.</summary>
    SonyPs2 = 1,
    /// <summary>Classic PS1 128KB card: "MC" header + 15 directory frames.</summary>
    SonyPs1 = 2,
}

/// <summary>
/// PS1 / PS2 dual-format memory card image.
///
/// Three layouts share one API used by MCSERV HLE and Desktop memcard UX:
/// <list type="bullet">
/// <item><see cref="McImageKind.DetPs2Native"/> — DetPS2 private format (default), persisted directory table.</item>
/// <item><see cref="McImageKind.SonyPs2"/> — MCMAN superblock ("1.1.0.0"), IFC + FAT cluster chains, root directory (mymc / ps2mcfs).</item>
/// <item><see cref="McImageKind.SonyPs1"/> — Classic PS1 128-byte frames, directory in frames 1–15.</item>
/// </list>
/// Authority: <c>tools/bios-decomp/MCMAN_ALL.txt</c> (dual-type probe FUN_000005ac type 1/2),
/// Ross Ridge PS2 MCFS, libmc type codes. ECC spare-area generation is residual.
/// </summary>
public sealed class MemoryCard
{
    public const int PageSize = 512;
    public const int DefaultPages = 2048; // 1MB — enough for HLE + small Sony geometry
    public const int Ps1CardBytes = 128 * 1024;
    public const int Ps1FrameSize = 128;
    public const int Ps1FrameCount = Ps1CardBytes / Ps1FrameSize; // 1024

    // --- DetPS2 native layout ---
    private const int EntrySize = 64;
    private const int NameFieldLen = 40;
    private const int EntriesPerPage = PageSize / EntrySize; // 8
    private const int DirectoryPages = 32;
    private const int DirectoryCapacity = DirectoryPages * EntriesPerPage; // 256
    private const int SuperblockPage = 0;
    private const int FirstDirectoryPage = SuperblockPage + 1;
    private const int FirstDataPage = FirstDirectoryPage + DirectoryPages;
    private const int SbMagicOff = 0;
    private static readonly byte[] DetMagic = Encoding.ASCII.GetBytes("DETPS2MC"); // 8
    private const int SbVersionOff = 8;
    private const byte DetFormatVersion = 1;
    private const int SbTotalPagesOff = 12;
    private const int SbNextFreePageOff = 16;

    // --- Sony PS2 MCFS superblock (page 0) ---
    // Magic is 28 bytes: "Sony PS2 Memory Card Format " (trailing space per mymc / MCMAN DAT compare 0x1c).
    private static readonly byte[] SonyPs2Magic = Encoding.ASCII.GetBytes("Sony PS2 Memory Card Format ");
    // 12-byte version field; MCMAN format path writes "1.1.0.0" (pad with NULs).
    private static readonly byte[] SonyPs2Version =
    {
        (byte)'1', (byte)'.', (byte)'1', (byte)'.', (byte)'0', (byte)'.', (byte)'0',
        0, 0, 0, 0, 0
    };
    private const int SbPs2PageLen = 0x28;
    private const int SbPs2PagesPerCluster = 0x2A;
    private const int SbPs2PagesPerBlock = 0x2C;
    private const int SbPs2UnknownHalf = 0x2E;
    private const int SbPs2ClustersPerCard = 0x30;
    private const int SbPs2AllocOffset = 0x34;
    private const int SbPs2AllocEnd = 0x38;
    private const int SbPs2RootDirCluster = 0x3C;
    private const int SbPs2BackupBlock1 = 0x40;
    private const int SbPs2BackupBlock2 = 0x44;
    private const int SbPs2IfcList = 0x50; // word[32]
    private const int SbPs2BadBlockList = 0xD0; // word[32]
    private const int SbPs2CardType = 0x150;
    private const int SbPs2CardFlags = 0x151;
    private const byte SonyCardFlags = 0x52; // ECC | bad-block capable (no erase-zeroes)

    // Directory entry (512 bytes max; we use 512-cluster-page split — entries are 512 bytes each on real cards,
    // but mymc documents 512-byte dirents. For compact HLE we use 512-byte dirents in cluster data.)
    private const int SonyDirentSize = 512;
    private const int SonyDirentMode = 0x00;   // u16
    private const int SonyDirentLength = 0x04; // u32 file length
    private const int SonyDirentCluster = 0x10; // u32 first cluster (rel to alloc_offset)
    private const int SonyDirentName = 0x20; // 32 bytes name
    // mode bits (subset used by MCMAN / libmc AttrFile)
    private const ushort SonyModeExists = 0x8000;
    private const ushort SonyModeDir = 0x0020;
    private const ushort SonyModeFile = 0x0010;
    private const ushort SonyModeR = 0x0001;
    private const ushort SonyModeW = 0x0002;
    private const ushort SonyModeFileRwx = SonyModeExists | SonyModeFile | SonyModeR | SonyModeW;
    private const ushort SonyModeDirRwx = SonyModeExists | SonyModeDir | SonyModeR | SonyModeW;
    private const uint FatUsedMask = 0x80000000u;
    private const uint FatEnd = 0xFFFFFFFFu;

    // --- PS1 ---
    private static readonly byte[] Ps1Magic = Encoding.ASCII.GetBytes("MC");
    private const byte Ps1BlockFree = 0xA0;
    private const byte Ps1BlockFirst = 0x51;
    private const byte Ps1BlockMid = 0x52;
    private const byte Ps1BlockEnd = 0x53;
    private const int Ps1DirStart = 1;
    private const int Ps1DirCount = 15;
    private const int Ps1DataStart = 16;

    private byte[] _data;
    private McImageKind _kind;
    private readonly Dictionary<string, int> _slotByName = new(StringComparer.OrdinalIgnoreCase);

    // Cached Sony geometry (valid when Kind == SonyPs2).
    private int _pageLen = PageSize;
    private int _pagesPerCluster = 2;
    private int _pagesPerBlock = 16;
    private int _clustersPerCard;
    private int _allocOffset;
    private int _allocEnd;
    private int _rootDirCluster;
    private int _ifcCluster;

    public int SizeBytes => _data.Length;
    public bool Formatted { get; private set; }
    public McImageKind Kind => _kind;
    public McCardType CardType => _kind switch
    {
        McImageKind.SonyPs1 => McCardType.Ps1,
        McImageKind.SonyPs2 => McCardType.Ps2,
        McImageKind.DetPs2Native => McCardType.Ps2, // HLE presents as PS2 to libmc
        _ => McCardType.None,
    };
    public ulong Writes { get; private set; }
    public ulong Reads { get; private set; }

    /// <summary>Free allocation units reported to MCSERV GET_INFO (clusters for PS2/native, blocks for PS1).</summary>
    public int FreeUnits
    {
        get
        {
            if (!Formatted) return 0;
            return _kind switch
            {
                McImageKind.SonyPs2 => CountFreeFatEntries(),
                McImageKind.SonyPs1 => CountFreePs1Blocks(),
                _ => Math.Max(1, TotalPages - NextFreePage),
            };
        }
    }

    public MemoryCard(int pages = DefaultPages)
    {
        if (pages < FirstDataPage + 1) pages = FirstDataPage + 1;
        _data = new byte[pages * PageSize];
        _kind = McImageKind.DetPs2Native;
        Format();
    }

    /// <summary>Construct from a raw page dump and auto-detect DetPS2 / Sony PS2 / PS1 magic.</summary>
    public MemoryCard(byte[] rawData)
    {
        if (rawData == null || rawData.Length == 0)
        {
            _data = new byte[DefaultPages * PageSize];
            _kind = McImageKind.DetPs2Native;
            Format();
            return;
        }

        McImageKind detected = DetectKind(rawData);
        if (detected == McImageKind.SonyPs1)
        {
            _data = new byte[Ps1CardBytes];
            Buffer.BlockCopy(rawData, 0, _data, 0, Math.Min(rawData.Length, _data.Length));
            _kind = McImageKind.SonyPs1;
            Formatted = HasPs1Magic();
            if (!Formatted) FormatSonyPs1();
            else RebuildPs1Cache();
            return;
        }

        int pages = Math.Max(FirstDataPage + 1, rawData.Length / PageSize);
        if (pages * PageSize < rawData.Length)
            pages = (rawData.Length + PageSize - 1) / PageSize;
        _data = new byte[pages * PageSize];
        Buffer.BlockCopy(rawData, 0, _data, 0, Math.Min(rawData.Length, _data.Length));

        if (detected == McImageKind.SonyPs2 && TryLoadSonyGeometry())
        {
            _kind = McImageKind.SonyPs2;
            Formatted = true;
            RebuildSonyCache();
            return;
        }

        if (HasDetMagic())
        {
            _kind = McImageKind.DetPs2Native;
            Formatted = true;
            RebuildDetCacheFromDisk();
            return;
        }

        // Unknown / corrupt → fresh DetPS2 native of same size.
        _kind = McImageKind.DetPs2Native;
        Format();
    }

    /// <summary>Create a card pre-formatted in the requested dual-format layout.</summary>
    public static MemoryCard Create(McImageKind kind, int pages = DefaultPages)
    {
        if (kind == McImageKind.SonyPs1)
        {
            var c = new MemoryCard(1); // temporary
            c.FormatSonyPs1();
            return c;
        }
        var card = new MemoryCard(pages);
        if (kind == McImageKind.SonyPs2)
            card.FormatSonyPs2();
        return card;
    }

    public static McImageKind DetectKind(ReadOnlySpan<byte> raw)
    {
        if (raw.Length >= SonyPs2Magic.Length && raw[..SonyPs2Magic.Length].SequenceEqual(SonyPs2Magic))
            return McImageKind.SonyPs2;
        if (raw.Length >= DetMagic.Length && raw[..DetMagic.Length].SequenceEqual(DetMagic))
            return McImageKind.DetPs2Native;
        if (raw.Length >= 2 && raw[0] == (byte)'M' && raw[1] == (byte)'C')
            return McImageKind.SonyPs1;
        return McImageKind.DetPs2Native; // default guess for empty
    }

    // ───────────────────── public file API ─────────────────────

    public void Format() => FormatDetPs2();

    public void Reset() => Format();

    /// <summary>MCSERV 0x77 FORMAT: produce a Sony PS2 dual-format card (MCMAN superblock path).</summary>
    public void FormatSonyPs2()
    {
        int pages = Math.Max(_data.Length / PageSize, 128);
        if (_data.Length != pages * PageSize)
            _data = new byte[pages * PageSize];
        Array.Clear(_data);
        _slotByName.Clear();
        _kind = McImageKind.SonyPs2;
        _pageLen = PageSize;
        _pagesPerCluster = 2;
        _pagesPerBlock = 16;
        _clustersPerCard = pages / _pagesPerCluster;
        if (_clustersPerCard < 64)
        {
            // Grow to a minimum workable geometry.
            pages = 128;
            _data = new byte[pages * PageSize];
            _clustersPerCard = pages / _pagesPerCluster;
        }

        // Geometry: reserve cluster 0..7 unused/super, cluster 8 = IFC, then FAT clusters, then alloc.
        _ifcCluster = 8;
        int fatEntriesNeeded = _clustersPerCard; // index by absolute cluster for simplicity in free count; alloc uses relative
        int entriesPerFatCluster = (_pageLen * _pagesPerCluster) / 4; // 256 for 1024B cluster
        int fatClusters = Math.Max(1, (fatEntriesNeeded + entriesPerFatCluster - 1) / entriesPerFatCluster);
        // Cap fatClusters so we leave room for data.
        int maxFat = Math.Max(1, _clustersPerCard / 8);
        if (fatClusters > maxFat) fatClusters = maxFat;

        _allocOffset = _ifcCluster + 1 + fatClusters;
        int backupBlocks = 2;
        int pagesBackup = backupBlocks * _pagesPerBlock;
        int clustersBackup = (pagesBackup + _pagesPerCluster - 1) / _pagesPerCluster;
        _allocEnd = Math.Max(1, _clustersPerCard - _allocOffset - clustersBackup);
        _rootDirCluster = 0;

        // Superblock
        Buffer.BlockCopy(SonyPs2Magic, 0, _data, 0, SonyPs2Magic.Length);
        Buffer.BlockCopy(SonyPs2Version, 0, _data, 0x1C, 12);
        WriteU16(SbPs2PageLen, (ushort)_pageLen);
        WriteU16(SbPs2PagesPerCluster, (ushort)_pagesPerCluster);
        WriteU16(SbPs2PagesPerBlock, (ushort)_pagesPerBlock);
        WriteU16(SbPs2UnknownHalf, 0xFF00);
        WriteU32(SbPs2ClustersPerCard, (uint)_clustersPerCard);
        WriteU32(SbPs2AllocOffset, (uint)_allocOffset);
        WriteU32(SbPs2AllocEnd, (uint)_allocEnd);
        WriteU32(SbPs2RootDirCluster, (uint)_rootDirCluster);
        int lastBlock = pages / _pagesPerBlock - 1;
        WriteU32(SbPs2BackupBlock1, (uint)Math.Max(0, lastBlock));
        WriteU32(SbPs2BackupBlock2, (uint)Math.Max(0, lastBlock - 1));
        WriteU32(SbPs2IfcList, (uint)_ifcCluster);
        for (int i = 1; i < 32; i++)
            WriteU32(SbPs2IfcList + i * 4, 0xFFFFFFFFu);
        for (int i = 0; i < 32; i++)
            WriteU32(SbPs2BadBlockList + i * 4, 0xFFFFFFFFu);
        _data[SbPs2CardType] = 2;
        _data[SbPs2CardFlags] = SonyCardFlags;

        // IFC cluster: point at FAT clusters starting at ifc+1
        ClearCluster(_ifcCluster, 0xFF);
        for (int i = 0; i < fatClusters; i++)
            WriteClusterU32(_ifcCluster, i, (uint)(_ifcCluster + 1 + i));
        for (int i = fatClusters; i < entriesPerFatCluster; i++)
            WriteClusterU32(_ifcCluster, i, 0xFFFFFFFFu);

        // FAT: all free (MSB clear). Root dir will claim entry 0.
        for (int fc = 0; fc < fatClusters; fc++)
        {
            int abs = _ifcCluster + 1 + fc;
            ClearCluster(abs, 0x00);
        }

        // Root directory cluster (relative 0 → absolute alloc_offset)
        int rootAbs = _allocOffset + _rootDirCluster;
        ClearCluster(rootAbs, 0x00);
        // "." and ".." dirents
        WriteSonyDirent(rootAbs, 0, SonyModeDirRwx, 0, 0, ".");
        WriteSonyDirent(rootAbs, 1, SonyModeDirRwx, 0, 0, "..");
        SetFatEntry(_rootDirCluster, FatEnd); // used, end of chain

        Formatted = true;
        Writes++;
        RebuildSonyCache();
    }

    /// <summary>Format as classic PS1 128KB dual-format card.</summary>
    public void FormatSonyPs1()
    {
        _data = new byte[Ps1CardBytes];
        _kind = McImageKind.SonyPs1;
        _slotByName.Clear();
        // Header frame
        _data[0] = (byte)'M';
        _data[1] = (byte)'C';
        // Rest of header zeros; PS1 often puts 0x0E at offset 0x7F as xor checksum of first 127 bytes.
        byte xor = 0;
        for (int i = 0; i < 127; i++) xor ^= _data[i];
        _data[127] = xor;
        // Directory frames free
        for (int f = Ps1DirStart; f < Ps1DirStart + Ps1DirCount; f++)
        {
            int off = f * Ps1FrameSize;
            _data[off] = Ps1BlockFree;
            // trailing checksum
            byte x = 0;
            for (int i = 0; i < 127; i++) x ^= _data[off + i];
            _data[off + 127] = x;
        }
        Formatted = true;
        Writes++;
    }

    public void FormatDetPs2()
    {
        if (_data.Length < (FirstDataPage + 1) * PageSize)
            _data = new byte[DefaultPages * PageSize];
        Array.Clear(_data);
        _slotByName.Clear();
        _kind = McImageKind.DetPs2Native;
        Buffer.BlockCopy(DetMagic, 0, _data, SbMagicOff, DetMagic.Length);
        _data[SbVersionOff] = DetFormatVersion;
        WriteInt32(SbTotalPagesOff, TotalPages);
        NextFreePage = FirstDataPage;
        Formatted = true;
        Writes++;
    }

    /// <summary>Writes (or overwrites) a named file on the active image format.</summary>
    public bool WriteFile(string name, ReadOnlySpan<byte> data)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return _kind switch
        {
            McImageKind.SonyPs2 => WriteFileSony(name, data),
            McImageKind.SonyPs1 => WriteFilePs1(name, data),
            _ => WriteFileDet(name, data),
        };
    }

    public byte[]? ReadFile(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return _kind switch
        {
            McImageKind.SonyPs2 => ReadFileSony(name),
            McImageKind.SonyPs1 => ReadFilePs1(name),
            _ => ReadFileDet(name),
        };
    }

    public bool DeleteFile(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return _kind switch
        {
            McImageKind.SonyPs2 => DeleteFileSony(name),
            McImageKind.SonyPs1 => DeleteFilePs1(name),
            _ => DeleteFileDet(name),
        };
    }

    public bool HasFile(string name) => _slotByName.ContainsKey(name);
    public int FileCount => _slotByName.Count;
    public IEnumerable<string> FileNames => _slotByName.Keys;

    public void ReadPage(int page, Span<byte> dest)
    {
        if (_kind == McImageKind.SonyPs1)
        {
            // PS1 has no 512B pages; map page → 4 frames.
            int off = page * PageSize;
            if (off < 0 || off >= _data.Length) { dest.Clear(); return; }
            int n = Math.Min(Math.Min(PageSize, dest.Length), _data.Length - off);
            _data.AsSpan(off, n).CopyTo(dest);
            if (n < dest.Length) dest[n..].Clear();
            Reads++;
            return;
        }
        int poff = page * PageSize;
        if (poff < 0 || poff + PageSize > _data.Length) { dest.Clear(); return; }
        _data.AsSpan(poff, Math.Min(PageSize, dest.Length)).CopyTo(dest);
        Reads++;
    }

    /// <summary>Raw page write used by MCSERV 0x7F (mcWritePage) HLE.</summary>
    public void WritePage(int page, ReadOnlySpan<byte> src)
    {
        int off = page * PageSize;
        if (off < 0 || off >= _data.Length) return;
        int n = Math.Min(Math.Min(PageSize, src.Length), _data.Length - off);
        src[..n].CopyTo(_data.AsSpan(off, n));
        Writes++;
        // After raw page poke, rebuild caches if superblock/dir may have changed.
        if (page == 0 || _kind == McImageKind.SonyPs2)
            TryResyncAfterRawWrite();
    }

    /// <summary>MCSERV 0x7D erase-block: zero a 16-page erase block (PS2) or no-op success path.</summary>
    public bool EraseBlock(int blockIndex)
    {
        if (_kind == McImageKind.SonyPs1)
        {
            // PS1: treat as single 128B frame clear when block maps to frame.
            if (blockIndex < 0 || blockIndex >= Ps1FrameCount) return false;
            Array.Clear(_data, blockIndex * Ps1FrameSize, Ps1FrameSize);
            Writes++;
            return true;
        }
        int ppb = _kind == McImageKind.SonyPs2 ? _pagesPerBlock : 16;
        int startPage = blockIndex * ppb;
        int off = startPage * PageSize;
        int bytes = ppb * PageSize;
        if (off < 0 || off + bytes > _data.Length) return false;
        Array.Clear(_data, off, bytes);
        Writes++;
        return true;
    }

    public byte[] ToRawBytes() => (byte[])_data.Clone();

    // ───────────────────── DetPS2 native ─────────────────────

    private bool HasDetMagic()
    {
        for (int i = 0; i < DetMagic.Length; i++)
            if (_data[SbMagicOff + i] != DetMagic[i]) return false;
        return _data[SbVersionOff] == DetFormatVersion;
    }

    private int TotalPages => _data.Length / PageSize;

    private int NextFreePage
    {
        get => ReadInt32(SbNextFreePageOff);
        set => WriteInt32(SbNextFreePageOff, value);
    }

    private int SlotOffset(int slot) => FirstDirectoryPage * PageSize + slot * EntrySize;

    private void RebuildDetCacheFromDisk()
    {
        _slotByName.Clear();
        for (int slot = 0; slot < DirectoryCapacity; slot++)
        {
            int off = SlotOffset(slot);
            if (_data[off + NameFieldLen + 8] == 0) continue;
            string name = ReadName(off, NameFieldLen);
            if (name.Length > 0) _slotByName[name] = slot;
        }
    }

    private static string ReadName(byte[] data, int entryOff, int maxLen)
    {
        int len = 0;
        while (len < maxLen && data[entryOff + len] != 0) len++;
        return Encoding.ASCII.GetString(data, entryOff, len);
    }

    private string ReadName(int entryOff, int maxLen) => ReadName(_data, entryOff, maxLen);

    private bool WriteFileDet(string name, ReadOnlySpan<byte> data)
    {
        if (Encoding.ASCII.GetByteCount(name) >= NameFieldLen)
            return false;

        int pagesNeeded = (data.Length + PageSize - 1) / PageSize;
        if (pagesNeeded == 0) pagesNeeded = 1;

        int slot;
        int startPage;
        if (_slotByName.TryGetValue(name, out slot))
        {
            int existingOff = SlotOffset(slot);
            int existingLen = ReadInt32(existingOff + NameFieldLen + 4);
            int existingPages = (existingLen + PageSize - 1) / PageSize;
            if (existingPages == 0) existingPages = 1;
            startPage = pagesNeeded <= existingPages
                ? ReadInt32(existingOff + NameFieldLen)
                : AllocatePages(pagesNeeded);
        }
        else
        {
            slot = FindFreeSlot();
            if (slot < 0) return false;
            startPage = AllocatePages(pagesNeeded);
        }
        if (startPage < 0) return false;

        int dataOff = startPage * PageSize;
        if (dataOff + data.Length > _data.Length) return false;
        data.CopyTo(_data.AsSpan(dataOff));

        int entryOff = SlotOffset(slot);
        Array.Clear(_data, entryOff, EntrySize);
        Encoding.ASCII.GetBytes(name).CopyTo(_data, entryOff);
        WriteInt32(entryOff + NameFieldLen, startPage);
        WriteInt32(entryOff + NameFieldLen + 4, data.Length);
        _data[entryOff + NameFieldLen + 8] = 1;

        _slotByName[name] = slot;
        Writes++;
        return true;
    }

    private int FindFreeSlot()
    {
        for (int slot = 0; slot < DirectoryCapacity; slot++)
            if (_data[SlotOffset(slot) + NameFieldLen + 8] == 0) return slot;
        return -1;
    }

    private int AllocatePages(int count)
    {
        int start = NextFreePage;
        if (start + count > TotalPages) return -1;
        NextFreePage = start + count;
        return start;
    }

    private byte[]? ReadFileDet(string name)
    {
        if (!_slotByName.TryGetValue(name, out int slot)) return null;
        int off = SlotOffset(slot);
        int startPage = ReadInt32(off + NameFieldLen);
        int length = ReadInt32(off + NameFieldLen + 4);
        if (length < 0 || startPage < 0) return null;
        byte[] buf = new byte[length];
        int avail = Math.Min(length, _data.Length - startPage * PageSize);
        if (avail > 0)
            Buffer.BlockCopy(_data, startPage * PageSize, buf, 0, avail);
        Reads++;
        return buf;
    }

    private bool DeleteFileDet(string name)
    {
        if (!_slotByName.TryGetValue(name, out int slot)) return false;
        Array.Clear(_data, SlotOffset(slot), EntrySize);
        _slotByName.Remove(name);
        Writes++;
        return true;
    }

    // ───────────────────── Sony PS2 FAT ─────────────────────

    private bool TryLoadSonyGeometry()
    {
        if (_data.Length < PageSize) return false;
        for (int i = 0; i < SonyPs2Magic.Length; i++)
            if (_data[i] != SonyPs2Magic[i]) return false;

        _pageLen = ReadU16(SbPs2PageLen);
        _pagesPerCluster = ReadU16(SbPs2PagesPerCluster);
        _pagesPerBlock = ReadU16(SbPs2PagesPerBlock);
        _clustersPerCard = (int)ReadU32(SbPs2ClustersPerCard);
        _allocOffset = (int)ReadU32(SbPs2AllocOffset);
        _allocEnd = (int)ReadU32(SbPs2AllocEnd);
        _rootDirCluster = (int)ReadU32(SbPs2RootDirCluster);
        _ifcCluster = (int)ReadU32(SbPs2IfcList);

        if (_pageLen != 512 && _pageLen != 1024) return false;
        if (_pagesPerCluster < 1 || _pagesPerCluster > 2) return false;
        if (_clustersPerCard < 8 || _allocOffset < 1) return false;
        if (_ifcCluster < 0 || _ifcCluster >= _clustersPerCard) return false;
        return true;
    }

    private int ClusterSizeBytes => _pageLen * _pagesPerCluster;

    private int ClusterByteOffset(int absCluster) => absCluster * ClusterSizeBytes;

    private void ClearCluster(int absCluster, byte fill)
    {
        int off = ClusterByteOffset(absCluster);
        int n = ClusterSizeBytes;
        if (off < 0 || off + n > _data.Length) return;
        if (fill == 0) Array.Clear(_data, off, n);
        else
            for (int i = 0; i < n; i++) _data[off + i] = fill;
    }

    private void WriteClusterU32(int absCluster, int index, uint value)
    {
        int off = ClusterByteOffset(absCluster) + index * 4;
        if (off < 0 || off + 4 > _data.Length) return;
        WriteU32(off, value);
    }

    private uint ReadClusterU32(int absCluster, int index)
    {
        int off = ClusterByteOffset(absCluster) + index * 4;
        if (off < 0 || off + 4 > _data.Length) return 0;
        return ReadU32(off);
    }

    /// <summary>FAT entry for allocatable cluster index (relative to alloc_offset).</summary>
    private uint GetFatEntry(int relCluster)
    {
        if (relCluster < 0) return 0;
        int entriesPerFatCluster = ClusterSizeBytes / 4;
        int fatIndex = relCluster;
        int fatClusterIndex = fatIndex / entriesPerFatCluster;
        int fatOffset = fatIndex % entriesPerFatCluster;
        // IFC[0] → first FAT cluster number (absolute)
        int fatAbs = (int)ReadClusterU32(_ifcCluster, fatClusterIndex);
        if (fatAbs < 0 || fatAbs >= _clustersPerCard) return 0;
        return ReadClusterU32(fatAbs, fatOffset);
    }

    private void SetFatEntry(int relCluster, uint value)
    {
        if (relCluster < 0) return;
        int entriesPerFatCluster = ClusterSizeBytes / 4;
        int fatIndex = relCluster;
        int fatClusterIndex = fatIndex / entriesPerFatCluster;
        int fatOffset = fatIndex % entriesPerFatCluster;
        int fatAbs = (int)ReadClusterU32(_ifcCluster, fatClusterIndex);
        if (fatAbs < 0 || fatAbs >= _clustersPerCard) return;
        WriteClusterU32(fatAbs, fatOffset, value);
    }

    private int CountFreeFatEntries()
    {
        int free = 0;
        int limit = Math.Min(_allocEnd, _clustersPerCard - _allocOffset);
        for (int i = 0; i < limit; i++)
        {
            uint e = GetFatEntry(i);
            if ((e & FatUsedMask) == 0) free++;
        }
        return free;
    }

    private int AllocFatClusters(int count)
    {
        if (count <= 0) return -1;
        int limit = Math.Min(_allocEnd, _clustersPerCard - _allocOffset);
        int first = -1;
        int prev = -1;
        int got = 0;
        for (int i = 0; i < limit && got < count; i++)
        {
            uint e = GetFatEntry(i);
            if ((e & FatUsedMask) != 0) continue;
            // Claim as end for now; link later.
            SetFatEntry(i, FatEnd);
            if (first < 0) first = i;
            if (prev >= 0)
                SetFatEntry(prev, FatUsedMask | (uint)i);
            prev = i;
            got++;
        }
        if (got < count)
        {
            // Roll back partial allocation.
            if (first >= 0) FreeFatChain(first);
            return -1;
        }
        return first;
    }

    private void FreeFatChain(int relStart)
    {
        int rel = relStart;
        int guard = 0;
        while (rel >= 0 && guard++ < _allocEnd + 8)
        {
            uint e = GetFatEntry(rel);
            SetFatEntry(rel, 0);
            if (e == FatEnd || (e & FatUsedMask) == 0) break;
            rel = (int)(e & ~FatUsedMask);
            if (rel == 0x7FFFFFFF) break;
        }
    }

    private void WriteSonyDirent(int absCluster, int index, ushort mode, int length, int firstRelCluster, string name)
    {
        int entsPerCluster = ClusterSizeBytes / SonyDirentSize;
        if (entsPerCluster < 1) entsPerCluster = 1;
        // For 1024B clusters only 2 dirents fit if size=512; that's fine for root "."/"..".
        // If cluster is 1024 and we need more files, chain more dir clusters — handled in WriteFileSony.
        int off = ClusterByteOffset(absCluster) + index * SonyDirentSize;
        if (off < 0 || off + SonyDirentSize > _data.Length) return;
        Array.Clear(_data, off, SonyDirentSize);
        _data[off + SonyDirentMode] = (byte)mode;
        _data[off + SonyDirentMode + 1] = (byte)(mode >> 8);
        WriteU32(off + SonyDirentLength, (uint)length);
        WriteU32(off + SonyDirentCluster, (uint)firstRelCluster);
        byte[] nb = Encoding.ASCII.GetBytes(name);
        int n = Math.Min(31, nb.Length);
        Buffer.BlockCopy(nb, 0, _data, off + SonyDirentName, n);
    }

    private bool TryReadSonyDirent(int absCluster, int index, out ushort mode, out int length, out int firstRel, out string name)
    {
        mode = 0; length = 0; firstRel = 0; name = "";
        int off = ClusterByteOffset(absCluster) + index * SonyDirentSize;
        if (off < 0 || off + SonyDirentSize > _data.Length) return false;
        mode = (ushort)(_data[off] | (_data[off + 1] << 8));
        if ((mode & SonyModeExists) == 0) return false;
        length = (int)ReadU32(off + SonyDirentLength);
        firstRel = (int)ReadU32(off + SonyDirentCluster);
        name = ReadName(_data, off + SonyDirentName, 32);
        return name.Length > 0;
    }

    private void RebuildSonyCache()
    {
        _slotByName.Clear();
        if (!Formatted) return;
        int rootAbs = _allocOffset + _rootDirCluster;
        int ents = Math.Max(1, ClusterSizeBytes / SonyDirentSize);
        // Walk root dir chain
        int rel = _rootDirCluster;
        int guard = 0;
        while (rel >= 0 && guard++ < 256)
        {
            int abs = _allocOffset + rel;
            for (int i = 0; i < ents; i++)
            {
                if (!TryReadSonyDirent(abs, i, out ushort mode, out _, out _, out string name))
                    continue;
                if ((mode & SonyModeDir) != 0) continue; // skip . .. and subdirs
                if (name is "." or "..") continue;
                _slotByName[name] = PackSonySlot(rel, i);
            }
            uint e = GetFatEntry(rel);
            if (e == FatEnd || (e & FatUsedMask) == 0) break;
            rel = (int)(e & ~FatUsedMask);
        }
    }

    private static int PackSonySlot(int dirRel, int index) => (dirRel << 16) | (index & 0xFFFF);
    private static void UnpackSonySlot(int slot, out int dirRel, out int index)
    {
        dirRel = slot >> 16;
        index = slot & 0xFFFF;
    }

    private bool WriteFileSony(string name, ReadOnlySpan<byte> data)
    {
        if (Encoding.ASCII.GetByteCount(name) >= 32) return false;
        int clusterBytes = ClusterSizeBytes;
        int clustersNeeded = Math.Max(1, (data.Length + clusterBytes - 1) / clusterBytes);

        // Free old chain if overwrite.
        if (_slotByName.TryGetValue(name, out int oldSlot))
        {
            UnpackSonySlot(oldSlot, out int oldDirRel, out int oldIdx);
            int oldAbs = _allocOffset + oldDirRel;
            if (TryReadSonyDirent(oldAbs, oldIdx, out _, out _, out int oldFirst, out _))
                FreeFatChain(oldFirst);
            // Reuse dirent slot
            int first = AllocFatClusters(clustersNeeded);
            if (first < 0) return false;
            WriteClusterChain(first, data);
            WriteSonyDirent(oldAbs, oldIdx, SonyModeFileRwx, data.Length, first, name);
            Writes++;
            return true;
        }

        if (!FindFreeSonyDirent(out int dirRel, out int dirIdx, out int dirAbs))
            return false;

        int firstNew = AllocFatClusters(clustersNeeded);
        if (firstNew < 0) return false;
        WriteClusterChain(firstNew, data);
        WriteSonyDirent(dirAbs, dirIdx, SonyModeFileRwx, data.Length, firstNew, name);
        _slotByName[name] = PackSonySlot(dirRel, dirIdx);
        Writes++;
        return true;
    }

    private bool FindFreeSonyDirent(out int dirRel, out int dirIdx, out int dirAbs)
    {
        dirRel = _rootDirCluster;
        dirIdx = 0;
        dirAbs = _allocOffset + dirRel;
        int ents = Math.Max(1, ClusterSizeBytes / SonyDirentSize);
        int rel = _rootDirCluster;
        int guard = 0;
        int lastRel = rel;
        while (rel >= 0 && guard++ < 256)
        {
            int abs = _allocOffset + rel;
            for (int i = 0; i < ents; i++)
            {
                int off = ClusterByteOffset(abs) + i * SonyDirentSize;
                if (off + 2 > _data.Length) continue;
                ushort mode = (ushort)(_data[off] | (_data[off + 1] << 8));
                if ((mode & SonyModeExists) == 0)
                {
                    dirRel = rel;
                    dirIdx = i;
                    dirAbs = abs;
                    return true;
                }
            }
            lastRel = rel;
            uint e = GetFatEntry(rel);
            if (e == FatEnd || (e & FatUsedMask) == 0) break;
            rel = (int)(e & ~FatUsedMask);
        }

        // Extend directory chain by one cluster.
        int extra = AllocFatClusters(1);
        if (extra < 0) return false;
        SetFatEntry(lastRel, FatUsedMask | (uint)extra);
        SetFatEntry(extra, FatEnd);
        ClearCluster(_allocOffset + extra, 0x00);
        dirRel = extra;
        dirIdx = 0;
        dirAbs = _allocOffset + extra;
        return true;
    }

    private void WriteClusterChain(int relStart, ReadOnlySpan<byte> data)
    {
        int rel = relStart;
        int offset = 0;
        int guard = 0;
        while (rel >= 0 && guard++ < _allocEnd + 8)
        {
            int abs = _allocOffset + rel;
            int off = ClusterByteOffset(abs);
            int n = Math.Min(ClusterSizeBytes, data.Length - offset);
            if (n > 0 && off >= 0 && off + n <= _data.Length)
                data.Slice(offset, n).CopyTo(_data.AsSpan(off, n));
            // zero remainder of cluster
            if (n < ClusterSizeBytes && off + n < _data.Length)
            {
                int clear = Math.Min(ClusterSizeBytes - n, _data.Length - (off + n));
                if (clear > 0) Array.Clear(_data, off + n, clear);
            }
            offset += n;
            uint e = GetFatEntry(rel);
            if (e == FatEnd || (e & FatUsedMask) == 0) break;
            rel = (int)(e & ~FatUsedMask);
        }
    }

    private byte[]? ReadFileSony(string name)
    {
        if (!_slotByName.TryGetValue(name, out int slot)) return null;
        UnpackSonySlot(slot, out int dirRel, out int dirIdx);
        if (!TryReadSonyDirent(_allocOffset + dirRel, dirIdx, out _, out int length, out int firstRel, out _))
            return null;
        if (length < 0) return null;
        byte[] buf = new byte[length];
        int rel = firstRel;
        int offset = 0;
        int guard = 0;
        while (rel >= 0 && offset < length && guard++ < _allocEnd + 8)
        {
            int abs = _allocOffset + rel;
            int off = ClusterByteOffset(abs);
            int n = Math.Min(ClusterSizeBytes, length - offset);
            if (off >= 0 && off + n <= _data.Length)
                Buffer.BlockCopy(_data, off, buf, offset, n);
            offset += n;
            uint e = GetFatEntry(rel);
            if (e == FatEnd || (e & FatUsedMask) == 0) break;
            rel = (int)(e & ~FatUsedMask);
        }
        Reads++;
        return buf;
    }

    private bool DeleteFileSony(string name)
    {
        if (!_slotByName.TryGetValue(name, out int slot)) return false;
        UnpackSonySlot(slot, out int dirRel, out int dirIdx);
        int abs = _allocOffset + dirRel;
        if (TryReadSonyDirent(abs, dirIdx, out _, out _, out int firstRel, out _))
            FreeFatChain(firstRel);
        int off = ClusterByteOffset(abs) + dirIdx * SonyDirentSize;
        if (off >= 0 && off + SonyDirentSize <= _data.Length)
            Array.Clear(_data, off, SonyDirentSize);
        _slotByName.Remove(name);
        Writes++;
        return true;
    }

    // ───────────────────── Sony PS1 ─────────────────────

    private bool HasPs1Magic() => _data.Length >= 2 && _data[0] == (byte)'M' && _data[1] == (byte)'C';

    private void RebuildPs1Cache()
    {
        _slotByName.Clear();
        for (int f = Ps1DirStart; f < Ps1DirStart + Ps1DirCount; f++)
        {
            int off = f * Ps1FrameSize;
            byte usage = _data[off];
            if (usage != Ps1BlockFirst && usage != 0x51) continue;
            // name at +0x0A, 20 bytes
            string name = ReadName(_data, off + 0x0A, 20);
            if (name.Length > 0) _slotByName[name] = f;
        }
    }

    private int CountFreePs1Blocks()
    {
        int free = 0;
        for (int f = Ps1DataStart; f < Ps1FrameCount; f++)
        {
            // A frame is free if no directory chain claims it — approximate via free dir slots * leftover.
            // Simpler: count free directory slots' available capacity.
            _ = f;
        }
        for (int f = Ps1DirStart; f < Ps1DirStart + Ps1DirCount; f++)
        {
            if (_data[f * Ps1FrameSize] == Ps1BlockFree) free += 1; // one slot ≈ one block group
        }
        // Also rough free data frames
        int usedData = 0;
        foreach (var kv in _slotByName)
        {
            int off = kv.Value * Ps1FrameSize;
            int size = ReadInt32(off + 4);
            usedData += Math.Max(1, (size + Ps1FrameSize - 1) / Ps1FrameSize);
        }
        int dataFrames = Ps1FrameCount - Ps1DataStart;
        return Math.Max(free, dataFrames - usedData);
    }

    private bool WriteFilePs1(string name, ReadOnlySpan<byte> data)
    {
        if (Encoding.ASCII.GetByteCount(name) > 20) return false;
        int framesNeeded = Math.Max(1, (data.Length + Ps1FrameSize - 1) / Ps1FrameSize);
        if (framesNeeded > Ps1FrameCount - Ps1DataStart) return false;

        // Delete existing
        if (_slotByName.ContainsKey(name))
            DeleteFilePs1(name);

        // Find free directory slot
        int dirFrame = -1;
        for (int f = Ps1DirStart; f < Ps1DirStart + Ps1DirCount; f++)
        {
            if (_data[f * Ps1FrameSize] == Ps1BlockFree)
            {
                dirFrame = f;
                break;
            }
        }
        if (dirFrame < 0) return false;

        // Find contiguous free data frames (simple bump from Ps1DataStart)
        int startData = FindFreePs1DataRun(framesNeeded);
        if (startData < 0) return false;

        // Write data
        for (int i = 0; i < framesNeeded; i++)
        {
            int frame = startData + i;
            int off = frame * Ps1FrameSize;
            int srcOff = i * Ps1FrameSize;
            int n = Math.Min(Ps1FrameSize, data.Length - srcOff);
            Array.Clear(_data, off, Ps1FrameSize);
            if (n > 0) data.Slice(srcOff, n).CopyTo(_data.AsSpan(off, n));
        }

        // Directory entry
        int dOff = dirFrame * Ps1FrameSize;
        Array.Clear(_data, dOff, Ps1FrameSize);
        _data[dOff] = framesNeeded == 1 ? Ps1BlockEnd : Ps1BlockFirst;
        // Some cards use 0x51 only for first; link next block number at +0x08
        WriteInt32(dOff + 4, data.Length);
        // next-block pointer (frame number of next data) — store start data frame
        _data[dOff + 8] = (byte)(startData & 0xFF);
        _data[dOff + 9] = (byte)((startData >> 8) & 0xFF);
        Encoding.ASCII.GetBytes(name).AsSpan(0, Math.Min(20, name.Length)).CopyTo(_data.AsSpan(dOff + 0x0A));
        // If multi-frame, mark intermediate (simplified: only first dirent; data is contiguous)
        if (framesNeeded > 1)
            _data[dOff] = Ps1BlockFirst;
        byte xor = 0;
        for (int i = 0; i < 127; i++) xor ^= _data[dOff + i];
        _data[dOff + 127] = xor;

        _slotByName[name] = dirFrame;
        Writes++;
        return true;
    }

    private int FindFreePs1DataRun(int framesNeeded)
    {
        // Build set of used data frames from directory.
        var used = new bool[Ps1FrameCount];
        for (int f = Ps1DirStart; f < Ps1DirStart + Ps1DirCount; f++)
        {
            int off = f * Ps1FrameSize;
            byte u = _data[off];
            if (u is not (Ps1BlockFirst or Ps1BlockMid or Ps1BlockEnd or 0x51 or 0x52 or 0x53))
                continue;
            int size = ReadInt32(off + 4);
            int start = _data[off + 8] | (_data[off + 9] << 8);
            int n = Math.Max(1, (size + Ps1FrameSize - 1) / Ps1FrameSize);
            for (int i = 0; i < n && start + i < Ps1FrameCount; i++)
                used[start + i] = true;
        }
        for (int s = Ps1DataStart; s + framesNeeded <= Ps1FrameCount; s++)
        {
            bool ok = true;
            for (int i = 0; i < framesNeeded; i++)
                if (used[s + i]) { ok = false; break; }
            if (ok) return s;
        }
        return -1;
    }

    private byte[]? ReadFilePs1(string name)
    {
        if (!_slotByName.TryGetValue(name, out int dirFrame)) return null;
        int dOff = dirFrame * Ps1FrameSize;
        int length = ReadInt32(dOff + 4);
        int start = _data[dOff + 8] | (_data[dOff + 9] << 8);
        if (length < 0 || start < 0 || start >= Ps1FrameCount) return null;
        byte[] buf = new byte[length];
        int frames = Math.Max(1, (length + Ps1FrameSize - 1) / Ps1FrameSize);
        for (int i = 0; i < frames; i++)
        {
            int off = (start + i) * Ps1FrameSize;
            int dst = i * Ps1FrameSize;
            int n = Math.Min(Ps1FrameSize, length - dst);
            if (n > 0 && off + n <= _data.Length)
                Buffer.BlockCopy(_data, off, buf, dst, n);
        }
        Reads++;
        return buf;
    }

    private bool DeleteFilePs1(string name)
    {
        if (!_slotByName.TryGetValue(name, out int dirFrame)) return false;
        int dOff = dirFrame * Ps1FrameSize;
        int length = ReadInt32(dOff + 4);
        int start = _data[dOff + 8] | (_data[dOff + 9] << 8);
        int frames = Math.Max(1, (length + Ps1FrameSize - 1) / Ps1FrameSize);
        for (int i = 0; i < frames && start + i < Ps1FrameCount; i++)
            Array.Clear(_data, (start + i) * Ps1FrameSize, Ps1FrameSize);
        Array.Clear(_data, dOff, Ps1FrameSize);
        _data[dOff] = Ps1BlockFree;
        byte xor = 0;
        for (int i = 0; i < 127; i++) xor ^= _data[dOff + i];
        _data[dOff + 127] = xor;
        _slotByName.Remove(name);
        Writes++;
        return true;
    }

    private void TryResyncAfterRawWrite()
    {
        if (HasDetMagic())
        {
            _kind = McImageKind.DetPs2Native;
            Formatted = true;
            RebuildDetCacheFromDisk();
            return;
        }
        if (TryLoadSonyGeometry())
        {
            _kind = McImageKind.SonyPs2;
            Formatted = true;
            RebuildSonyCache();
            return;
        }
        if (HasPs1Magic() && _data.Length >= Ps1CardBytes)
        {
            _kind = McImageKind.SonyPs1;
            Formatted = true;
            RebuildPs1Cache();
        }
    }

    // ───────────────────── binary helpers ─────────────────────

    private int ReadInt32(int off) =>
        _data[off] | (_data[off + 1] << 8) | (_data[off + 2] << 16) | (_data[off + 3] << 24);

    private void WriteInt32(int off, int value)
    {
        _data[off] = (byte)value;
        _data[off + 1] = (byte)(value >> 8);
        _data[off + 2] = (byte)(value >> 16);
        _data[off + 3] = (byte)(value >> 24);
    }

    private uint ReadU32(int off) => unchecked((uint)ReadInt32(off));

    private void WriteU32(int off, uint value) => WriteInt32(off, unchecked((int)value));

    private ushort ReadU16(int off) => (ushort)(_data[off] | (_data[off + 1] << 8));

    private void WriteU16(int off, ushort value)
    {
        _data[off] = (byte)value;
        _data[off + 1] = (byte)(value >> 8);
    }
}
