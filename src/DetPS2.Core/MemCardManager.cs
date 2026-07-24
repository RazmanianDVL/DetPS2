using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Memory card file manager (Phase 37): load/save host images for Desktop UX.
/// </summary>
public static class MemCardManager
{
    public const int DefaultSize = MemoryCard.DefaultPages * MemoryCard.PageSize;

    public static void SaveToFile(MemoryCard card, string path)
    {
        // Export via page dump
        using var fs = File.Create(path);
        var page = new byte[MemoryCard.PageSize];
        int pages = card.SizeBytes / MemoryCard.PageSize;
        for (int i = 0; i < pages; i++)
        {
            card.ReadPage(i, page);
            fs.Write(page, 0, page.Length);
        }
    }

    public static MemoryCard LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return new MemoryCard();
        byte[] data = File.ReadAllBytes(path);
        int pages = Math.Max(1, data.Length / MemoryCard.PageSize);
        var card = new MemoryCard(pages);
        // Reconstruct: write raw into card by formatting then file entries unknown —
        // store as single blob file "__RAW__"
        card.Format();
        card.WriteFile("__RAW__", data.AsSpan(0, Math.Min(data.Length, card.SizeBytes - MemoryCard.PageSize)));
        return card;
    }

    public static bool TryImportFile(MemoryCard card, string name, string hostFile)
    {
        if (!File.Exists(hostFile)) return false;
        byte[] data = File.ReadAllBytes(hostFile);
        return card.WriteFile(name, data);
    }

    public static bool TryExportFile(MemoryCard card, string name, string hostFile)
    {
        byte[]? data = card.ReadFile(name);
        if (data == null) return false;
        File.WriteAllBytes(hostFile, data);
        return true;
    }
}
