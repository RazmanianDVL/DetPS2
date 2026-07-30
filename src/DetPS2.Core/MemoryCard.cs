using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// PS2 memory card image: page store + a real, persisted directory table.
/// 8MB standard image model simplified to 1MB for tests (<see cref="DefaultPages"/>).
///
/// This is DetPS2's own format, not a byte-exact reproduction of Sony's real on-disk
/// FAT/indirect-cluster memory card layout (that format's exact cluster-chain byte
/// offsets aren't something this project has verified against a primary source the way
/// ApaPartitionTable.cs/PfsFileSystem.cs verified the real HDD APA/PFS formats against
/// ps2sdk). What matters for correctness here — this is the emulator's PRIMARY save
/// path — is that the directory (file names, locations, lengths) is stored ON the card
/// image itself, not in a side dictionary that only exists in memory. The earlier
/// version kept the whole file table in a private Dictionary that was never written to
/// _data at all: SizeBytes/ReadPage only ever exposed the raw superblock+file bytes, so
/// dumping _data to disk and reloading it into a fresh MemoryCard produced a byte-exact
/// copy of the DATA with no way to know which bytes belonged to which file — exactly
/// the round-trip bug MemCardManager.cs worked around with an opaque "__RAW__" blob.
/// Fixed by giving the directory a real, fixed home in the image (right after the
/// superblock) and rebuilding the in-memory lookup cache by scanning it, every time a
/// card is constructed from raw bytes — the cache is a read cache, never the source of
/// truth.
/// </summary>
public sealed class MemoryCard
{
    public const int PageSize = 512;
    public const int DefaultPages = 2048; // 1MB

    /// <summary>Directory entry: 64 bytes, 8 per 512-byte page.
    /// [0..39] name (ASCII, null-padded) [40..43] startPage (i32 LE)
    /// [44..47] length in bytes (i32 LE) [48] used flag [49..63] reserved.</summary>
    private const int EntrySize = 64;
    private const int NameFieldLen = 40;
    private const int EntriesPerPage = PageSize / EntrySize; // 8

    /// <summary>Reserved directory pages right after the superblock — fixed regardless
    /// of card size, room for 256 files (256 * 64B = 16,384B = 32 pages).</summary>
    private const int DirectoryPages = 32;
    private const int DirectoryCapacity = DirectoryPages * EntriesPerPage; // 256

    private const int SuperblockPage = 0;
    private const int FirstDirectoryPage = SuperblockPage + 1;
    private const int FirstDataPage = FirstDirectoryPage + DirectoryPages;

    // Superblock (page 0) field offsets.
    private const int SbMagicOff = 0;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DETPS2MC"); // 8 bytes
    private const int SbVersionOff = 8; // 1 byte
    private const byte FormatVersion = 1;
    private const int SbTotalPagesOff = 12; // i32
    private const int SbNextFreePageOff = 16; // i32 — bump allocator cursor

    private readonly byte[] _data;

    /// <summary>Read cache only: name -> directory slot index. Rebuilt from _data on
    /// every Format()/construction-from-raw-bytes, kept in sync by WriteFile/DeleteFile.
    /// Never the source of truth — _data's directory table is.</summary>
    private readonly Dictionary<string, int> _slotByName = new(StringComparer.OrdinalIgnoreCase);

    public int SizeBytes => _data.Length;
    public bool Formatted { get; private set; }
    public ulong Writes { get; private set; }
    public ulong Reads { get; private set; }

    public MemoryCard(int pages = DefaultPages)
    {
        if (pages < FirstDataPage + 1) pages = FirstDataPage + 1;
        _data = new byte[pages * PageSize];
        Format();
    }

    /// <summary>Construct from a raw page dump (e.g. loaded from disk) and rebuild the
    /// directory read-cache by scanning it. Falls back to a freshly formatted card of
    /// the same size if the magic doesn't match (corrupt or foreign file) rather than
    /// silently misinterpreting unrelated bytes as a directory table.</summary>
    public MemoryCard(byte[] rawData)
    {
        int pages = Math.Max(FirstDataPage + 1, rawData.Length / PageSize);
        _data = new byte[pages * PageSize];
        Buffer.BlockCopy(rawData, 0, _data, 0, Math.Min(rawData.Length, _data.Length));

        if (!HasValidMagic())
        {
            Format();
            return;
        }
        Formatted = true;
        RebuildCacheFromDisk();
    }

    private bool HasValidMagic()
    {
        for (int i = 0; i < Magic.Length; i++)
            if (_data[SbMagicOff + i] != Magic[i]) return false;
        return _data[SbVersionOff] == FormatVersion;
    }

    private int ReadInt32(int off) =>
        _data[off] | (_data[off + 1] << 8) | (_data[off + 2] << 16) | (_data[off + 3] << 24);

    private void WriteInt32(int off, int value)
    {
        _data[off] = (byte)value;
        _data[off + 1] = (byte)(value >> 8);
        _data[off + 2] = (byte)(value >> 16);
        _data[off + 3] = (byte)(value >> 24);
    }

    private int NextFreePage
    {
        get => ReadInt32(SbNextFreePageOff);
        set => WriteInt32(SbNextFreePageOff, value);
    }

    private int TotalPages => _data.Length / PageSize;

    private int SlotOffset(int slot) => FirstDirectoryPage * PageSize + slot * EntrySize;

    private void RebuildCacheFromDisk()
    {
        _slotByName.Clear();
        for (int slot = 0; slot < DirectoryCapacity; slot++)
        {
            int off = SlotOffset(slot);
            if (_data[off + NameFieldLen + 8] == 0) continue; // used flag
            string name = ReadName(off);
            if (name.Length > 0) _slotByName[name] = slot;
        }
    }

    private string ReadName(int entryOff)
    {
        int len = 0;
        while (len < NameFieldLen && _data[entryOff + len] != 0) len++;
        return Encoding.ASCII.GetString(_data, entryOff, len);
    }

    public void Format()
    {
        Array.Clear(_data);
        _slotByName.Clear();
        Buffer.BlockCopy(Magic, 0, _data, SbMagicOff, Magic.Length);
        _data[SbVersionOff] = FormatVersion;
        WriteInt32(SbTotalPagesOff, TotalPages);
        NextFreePage = FirstDataPage;
        Formatted = true;
        Writes++;
    }

    public void Reset() => Format();

    /// <summary>Writes (or overwrites) a named file. Overwrite reuses the existing
    /// data pages in place when the new content is no larger; a larger overwrite (or a
    /// new file) bump-allocates fresh pages instead. Bump-only: DeleteFile and shrink-
    /// on-overwrite do not reclaim data-page space (no free list) — acceptable for a
    /// save-game card's write pattern (occasional saves, not a general filesystem), but
    /// worth knowing if this card ends up handling thousands of writes.</summary>
    public bool WriteFile(string name, ReadOnlySpan<byte> data)
    {
        if (string.IsNullOrEmpty(name) || Encoding.ASCII.GetByteCount(name) >= NameFieldLen)
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
            if (slot < 0) return false; // directory full
            startPage = AllocatePages(pagesNeeded);
        }
        if (startPage < 0) return false; // out of space

        int dataOff = startPage * PageSize;
        if (dataOff + data.Length > _data.Length) return false;
        data.CopyTo(_data.AsSpan(dataOff));

        int entryOff = SlotOffset(slot);
        Array.Clear(_data, entryOff, EntrySize);
        Encoding.ASCII.GetBytes(name).CopyTo(_data, entryOff);
        WriteInt32(entryOff + NameFieldLen, startPage);
        WriteInt32(entryOff + NameFieldLen + 4, data.Length);
        _data[entryOff + NameFieldLen + 8] = 1; // used

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

    public byte[]? ReadFile(string name)
    {
        if (!_slotByName.TryGetValue(name, out int slot)) return null;
        int off = SlotOffset(slot);
        int startPage = ReadInt32(off + NameFieldLen);
        int length = ReadInt32(off + NameFieldLen + 4);
        byte[] buf = new byte[length];
        Buffer.BlockCopy(_data, startPage * PageSize, buf, 0, length);
        Reads++;
        return buf;
    }

    public bool DeleteFile(string name)
    {
        if (!_slotByName.TryGetValue(name, out int slot)) return false;
        Array.Clear(_data, SlotOffset(slot), EntrySize);
        _slotByName.Remove(name);
        Writes++;
        return true;
    }

    public bool HasFile(string name) => _slotByName.ContainsKey(name);
    public int FileCount => _slotByName.Count;
    public IEnumerable<string> FileNames => _slotByName.Keys;

    public void ReadPage(int page, Span<byte> dest)
    {
        int off = page * PageSize;
        if (off < 0 || off + PageSize > _data.Length) { dest.Clear(); return; }
        _data.AsSpan(off, Math.Min(PageSize, dest.Length)).CopyTo(dest);
        Reads++;
    }

    /// <summary>Raw page write used by MCSERV 0x7F (mcWritePage) HLE. Out-of-range is a no-op.</summary>
    public void WritePage(int page, ReadOnlySpan<byte> src)
    {
        int off = page * PageSize;
        if (off < 0 || off + PageSize > _data.Length) return;
        int n = Math.Min(PageSize, src.Length);
        src[..n].CopyTo(_data.AsSpan(off, n));
        Writes++;
    }

    /// <summary>Raw page dump for host persistence (MemCardManager.SaveToFile) — the
    /// directory table is part of this, so reloading it (via the byte[] constructor)
    /// recovers every named file, not just the data bytes.</summary>
    public byte[] ToRawBytes() => (byte[])_data.Clone();
}
