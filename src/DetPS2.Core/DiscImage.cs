using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Disc image abstraction — supports multi‑GB ISOs without loading the whole file into a byte[].
/// </summary>
public interface IDiscImage : IDisposable
{
    long Length { get; }
    string? SourcePath { get; }
    /// <summary>Read up to buffer.Length bytes at absolute file offset. Returns bytes read.</summary>
    int ReadAt(long offset, Span<byte> buffer);
}

/// <summary>In-memory image (small synthetics / tests).</summary>
public sealed class MemoryDiscImage : IDiscImage
{
    private readonly byte[] _data;
    public long Length => _data.Length;
    public string? SourcePath => null;

    public MemoryDiscImage(byte[] data) => _data = data ?? throw new ArgumentNullException(nameof(data));

    public int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= _data.Length) return 0;
        int n = (int)Math.Min(buffer.Length, _data.Length - offset);
        _data.AsSpan((int)offset, n).CopyTo(buffer);
        return n;
    }

    public void Dispose() { }
}

/// <summary>
/// File-backed image with random access. Works with local and UNC paths
/// (e.g. \\server\share\game.iso). Handles files larger than 2 GB.
/// </summary>
public sealed class FileDiscImage : IDiscImage
{
    private readonly FileStream _fs;
    private readonly object _lock = new();

    public long Length { get; }
    public string? SourcePath { get; }

    public FileDiscImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path required", nameof(path));
        // Normalize UNC and long paths
        path = NormalizePath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Disc image not found", path);

        SourcePath = path;
        _fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.RandomAccess);
        Length = _fs.Length;
    }

    public static string NormalizePath(string path)
    {
        path = path.Trim().Trim('"');
        // Allow \\server\share and \\?\UNC\server\share
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return path;
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return path;
        // Prefer extended UNC for long network paths
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path.TrimStart('\\');
        // Long local paths
        if (path.Length >= 240 && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && Path.IsPathRooted(path))
            return @"\\?\" + path;
        return path;
    }

    public int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length || buffer.Length == 0) return 0;
        lock (_lock)
        {
            _fs.Seek(offset, SeekOrigin.Begin);
            return _fs.Read(buffer);
        }
    }

    public void Dispose() => _fs.Dispose();
}
