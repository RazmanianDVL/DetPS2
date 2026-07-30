using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Parses the real PS2 BIOS ROMDIR table and extracts individual module blobs
/// (SYSMEM, LOADCORE, THREADMAN, etc.) from a BIOS ROM image (IRX Phase 2).
///
/// ROMDIR is a flat array of 16-byte entries (10-byte NUL-padded ASCII name,
/// uint16 extinfo_size, uint32 size) starting at the "RESET\0" entry. Entry
/// data is NOT packed at the naive cumulative-size offset -- real BIOS images
/// insert variable alignment padding between entries (empirically observed,
/// not a fixed stride) -- so extraction locates each module's real ELF magic
/// by searching a window around the naive cumulative offset rather than
/// trusting a closed-form packing formula.
/// </summary>
public static class RomdirExtractor
{
    public readonly struct RomdirEntry
    {
        public string Name { get; init; }
        public ushort ExtInfoSize { get; init; }
        public uint Size { get; init; }
        public long NaiveOffset { get; init; }
    }

    private static readonly byte[] ElfMagic = { 0x7F, (byte)'E', (byte)'L', (byte)'F' };

    /// <summary>Locate the ROMDIR table and parse all entries in cumulative-offset order.</summary>
    public static List<RomdirEntry> ParseRomdir(byte[] bios)
    {
        var entries = new List<RomdirEntry>();
        int start = IndexOf(bios, Encoding.ASCII.GetBytes("RESET\0"), 0);
        if (start < 0) return entries;

        long cum = 0;
        int off = start;
        for (int i = 0; i < 512 && off + 16 <= bios.Length; i++)
        {
            var nameBytes = new ReadOnlySpan<byte>(bios, off, 10);
            int nameLen = nameBytes.IndexOf((byte)0);
            if (nameLen < 0) nameLen = 10;
            if (nameLen == 0) break;

            bool printable = true;
            for (int j = 0; j < nameLen; j++)
            {
                byte b = nameBytes[j];
                if (b < 0x20 || b >= 0x7F) { printable = false; break; }
            }
            if (!printable) break;

            string name = Encoding.ASCII.GetString(bios, off, nameLen);
            ushort extInfoSize = BitConverter.ToUInt16(bios, off + 10);
            uint size = BitConverter.ToUInt32(bios, off + 12);

            entries.Add(new RomdirEntry { Name = name, ExtInfoSize = extInfoSize, Size = size, NaiveOffset = cum });
            cum += size;
            off += 16;
        }
        return entries;
    }

    /// <summary>
    /// Find a named entry's real file offset by searching for ELF magic bytes
    /// forward from its naive cumulative offset, widening the search window until
    /// found or the max window is exhausted. Forward-only: alignment padding pushes
    /// an entry's real data later than its naive offset, never earlier (observed
    /// across all real kernel modules: deltas of +35..+396 bytes, always positive) --
    /// searching backward risks matching a smaller preceding entry's own ELF magic
    /// instead of the target's. Returns -1 if not found.
    /// </summary>
    public static long FindRealOffset(byte[] bios, RomdirEntry entry, int maxWindow = 4096)
    {
        for (int w = 256; w <= maxWindow; w *= 2)
        {
            long lo = entry.NaiveOffset;
            long hi = Math.Min(bios.Length, entry.NaiveOffset + w);
            int idx = IndexOf(bios, ElfMagic, (int)lo, (int)hi);
            if (idx >= 0) return idx;
        }
        return -1;
    }

    /// <summary>Extract a named module's raw ELF bytes from a BIOS image. Returns null if not found
    /// or the entry is not ELF-relocatable (use <see cref="ExtractModuleContent"/> for text/raw).</summary>
    public static byte[]? ExtractModule(byte[] bios, string moduleName)
    {
        if (!TryFindEntry(bios, moduleName, out var e)) return null;
        long realOff = FindRealOffset(bios, e);
        if (realOff < 0) return null;
        if (realOff + e.Size > bios.Length) return null;
        var buf = new byte[e.Size];
        Array.Copy(bios, realOff, buf, 0, (int)e.Size);
        return buf;
    }

    /// <summary>
    /// ROMDRV / <c>rom0:</c> content serving: extract named ROMDIR entry bytes.
    /// Prefers ELF-magic-verified offset when the entry is (or is padded before) an ELF;
    /// uses the naive cumulative offset for non-ELF payloads (IOPBTCONF text, RESET blobs)
    /// so a later entry's ELF magic is not stolen. Returns null if the name is absent.
    /// </summary>
    public static byte[]? ExtractModuleContent(byte[] bios, string moduleName)
    {
        if (bios == null || bios.Length == 0 || string.IsNullOrWhiteSpace(moduleName))
            return null;
        if (!TryFindEntry(bios, moduleName, out var e)) return null;
        if (e.Size == 0) return Array.Empty<byte>();
        if (e.NaiveOffset < 0 || e.NaiveOffset + e.Size > bios.Length) return null;

        long realOff = ResolveContentOffset(bios, e);
        if (realOff < 0 || realOff + e.Size > bios.Length) return null;

        var buf = new byte[e.Size];
        Array.Copy(bios, realOff, buf, 0, (int)e.Size);
        return buf;
    }

    /// <summary>
    /// Choose file offset for a ROMDIR entry's payload. ELF modules may sit after alignment
    /// padding past <see cref="RomdirEntry.NaiveOffset"/>; non-ELF payloads live at the naive
    /// offset and must not be replaced by a subsequent entry's ELF magic.
    /// </summary>
    public static long ResolveContentOffset(byte[] bios, RomdirEntry entry)
    {
        long naive = entry.NaiveOffset;
        if (naive < 0 || naive >= bios.Length) return -1;

        long elfOff = FindRealOffset(bios, entry);
        if (elfOff >= 0 && IsElfMagic(bios, elfOff) && elfOff >= naive)
        {
            // Accept the ELF offset when naive is already ELF, or the span [naive, elfOff)
            // is only alignment padding (zeros). Non-zero text (IOPBTCONF) keeps naive so a
            // later entry's ELF is not stolen.
            int pad = (int)(elfOff - naive);
            if (IsElfMagic(bios, naive) || (pad >= 0 && pad < 4096 && IsZeroRegion(bios, naive, pad)))
                return elfOff;
        }

        return naive;
    }

    private static bool IsElfMagic(byte[] bios, long off) =>
        off >= 0 && off + 4 <= bios.Length &&
        bios[off] == 0x7F && bios[off + 1] == (byte)'E' &&
        bios[off + 2] == (byte)'L' && bios[off + 3] == (byte)'F';

    private static bool IsZeroRegion(byte[] bios, long off, int n)
    {
        if (n <= 0) return true;
        long end = Math.Min(bios.Length, off + n);
        for (long i = off; i < end; i++)
            if (bios[i] != 0) return false;
        return true;
    }

    /// <summary>Lookup a single ROMDIR entry by name (case-insensitive). Returns false if absent.</summary>
    public static bool TryFindEntry(byte[] bios, string moduleName, out RomdirEntry entry)
    {
        entry = default;
        if (bios == null || string.IsNullOrWhiteSpace(moduleName)) return false;
        foreach (var e in ParseRomdir(bios))
        {
            if (!string.Equals(e.Name, moduleName, StringComparison.OrdinalIgnoreCase)) continue;
            entry = e;
            return true;
        }
        return false;
    }

    /// <summary>True if <paramref name="moduleName"/> is present in the ROMDIR table.</summary>
    public static bool HasModule(byte[] bios, string moduleName) =>
        TryFindEntry(bios, moduleName, out _);

    private static int IndexOf(byte[] haystack, byte[] needle, int start, int? end = null)
    {
        int limit = (end ?? haystack.Length) - needle.Length;
        for (int i = Math.Max(0, start); i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
