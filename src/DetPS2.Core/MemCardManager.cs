using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Memory card file manager: load/save host images for Desktop UX.
/// Supports DetPS2 native, Sony PS2 MCFS, and classic PS1 dual-format images
/// via <see cref="MemoryCard"/> auto-detect.
/// </summary>
public static class MemCardManager
{
    public const int DefaultSize = MemoryCard.DefaultPages * MemoryCard.PageSize;

    public static void SaveToFile(MemoryCard card, string path) =>
        File.WriteAllBytes(path, card.ToRawBytes());

    /// <summary>
    /// Loads a card image. Magic auto-detect covers DetPS2 ("DETPS2MC"), Sony PS2
    /// ("Sony PS2 Memory Card Format "), and PS1 ("MC"). Unknown/corrupt images
    /// fall back to a freshly formatted DetPS2 card of the same size.
    /// </summary>
    public static MemoryCard LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return new MemoryCard();
        return new MemoryCard(File.ReadAllBytes(path));
    }

    /// <summary>Create a blank card of the requested dual-format kind and save it.</summary>
    public static MemoryCard CreateAndSave(string path, McImageKind kind, int pages = MemoryCard.DefaultPages)
    {
        var card = MemoryCard.Create(kind, pages);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        SaveToFile(card, path);
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
