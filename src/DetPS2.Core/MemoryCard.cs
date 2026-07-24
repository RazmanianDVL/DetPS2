using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// PS2 memory card image stub (Phase 22/31 prep): page store + format.
/// 8MB standard image model simplified to 1MB for tests.
/// </summary>
public sealed class MemoryCard
{
    public const int PageSize = 512;
    public const int DefaultPages = 2048; // 1MB

    private readonly byte[] _data;
    private readonly Dictionary<string, (int page, int length)> _files = new(StringComparer.OrdinalIgnoreCase);

    public int SizeBytes => _data.Length;
    public bool Formatted { get; private set; }
    public ulong Writes { get; private set; }
    public ulong Reads { get; private set; }

    public MemoryCard(int pages = DefaultPages)
    {
        _data = new byte[pages * PageSize];
        Format();
    }

    public void Format()
    {
        Array.Clear(_data);
        _files.Clear();
        // Superblock marker
        _data[0] = (byte)'S';
        _data[1] = (byte)'o';
        _data[2] = (byte)'n';
        _data[3] = (byte)'y';
        Formatted = true;
        Writes++;
    }

    public void Reset() => Format();

    public bool WriteFile(string name, ReadOnlySpan<byte> data)
    {
        if (string.IsNullOrEmpty(name) || data.Length > _data.Length - PageSize)
            return false;
        int page = 1 + _files.Count * ((data.Length + PageSize - 1) / PageSize + 1);
        int off = page * PageSize;
        if (off + data.Length > _data.Length) return false;
        data.CopyTo(_data.AsSpan(off));
        _files[name] = (page, data.Length);
        Writes++;
        return true;
    }

    public byte[]? ReadFile(string name)
    {
        if (!_files.TryGetValue(name, out var meta)) return null;
        int off = meta.page * PageSize;
        byte[] buf = new byte[meta.length];
        Buffer.BlockCopy(_data, off, buf, 0, meta.length);
        Reads++;
        return buf;
    }

    public bool HasFile(string name) => _files.ContainsKey(name);
    public int FileCount => _files.Count;

    public void ReadPage(int page, Span<byte> dest)
    {
        int off = page * PageSize;
        if (off < 0 || off + PageSize > _data.Length) { dest.Clear(); return; }
        _data.AsSpan(off, Math.Min(PageSize, dest.Length)).CopyTo(dest);
        Reads++;
    }
}
