using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Memory card file manager (Phase 37): load/save host images for Desktop UX.
/// </summary>
public static class MemCardManager
{
    public const int DefaultSize = MemoryCard.DefaultPages * MemoryCard.PageSize;

    public static void SaveToFile(MemoryCard card, string path) =>
        File.WriteAllBytes(path, card.ToRawBytes());

    /// <summary>Loads a card image previously written by SaveToFile. The directory
    /// table lives inside the image itself (see MemoryCard.cs's own doc comment), so
    /// this recovers every named file exactly as it was saved — no raw-blob fallback
    /// needed for DetPS2's own images. A file with the wrong magic (not one of ours)
    /// comes back as a freshly formatted, empty card rather than misread garbage.</summary>
    public static MemoryCard LoadFromFile(string path)
    {
        if (!File.Exists(path))
            return new MemoryCard();
        return new MemoryCard(File.ReadAllBytes(path));
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
