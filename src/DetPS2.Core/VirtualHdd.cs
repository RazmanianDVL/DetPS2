using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// A virtual PS2 hard drive — a real APA-partitioned, PFS-formatted disk image, persisted as a
/// single file on the host filesystem. This is the "give the PS2 a native HDD" foundation:
/// unlike memory cards (2 slots, 8MB each, no switching), a virtual HDD has effectively
/// unbounded capacity and a real filesystem, exactly like the Expansion Bay + Network Adaptor +
/// HDD setup on real "fat" PS2 hardware — see <see cref="Apa"/>/<see cref="Pfs"/> for the
/// verified on-disk format details.
///
/// This class is intentionally NOT wired into game-facing I/O yet (no SIF RPC/IOP device
/// hookup) — see DEVELOPER_GUIDE.md for why: the on-disk format needed to be built and tested
/// as a standalone, verified library first. Memory card support is untouched and still
/// available; this is an addition, not a replacement.
/// </summary>
public sealed class VirtualHdd
{
    public const string MainPartitionId = "__common"; // conventional PFS main-partition name

    public ApaDisk Disk { get; }
    public PfsVolume Main { get; }

    private VirtualHdd(ApaDisk disk, PfsVolume main)
    {
        Disk = disk;
        Main = main;
    }

    /// <summary>Creates a brand-new virtual HDD of the given size, already APA-partitioned and
    /// PFS-formatted with one main partition spanning almost the whole disk.</summary>
    public static VirtualHdd CreateNew(long sizeBytes)
    {
        uint totalSectors = (uint)(sizeBytes / Apa.SectorSize);
        var disk = new ApaDisk(totalSectors);
        disk.FormatDisk();

        // Leave a small amount of headroom after the self header before the main partition
        // starts, matching how real tools lay out the first partition; use (almost) all
        // remaining space for the main partition.
        uint available = totalSectors - Apa.HeaderSectors;
        uint mainSectors = available - (available % Pfs.SectorsPerZone); // whole zones only
        uint partitionSector = disk.CreatePartition(MainPartitionId, mainSectors, Apa.TypePfs);
        var partitionHeader = disk.FindPartition(MainPartitionId)
            ?? throw new InvalidOperationException("failed to create main partition");
        _ = partitionSector;

        var volume = new PfsVolume(disk, partitionHeader);
        volume.Format();

        return new VirtualHdd(disk, volume);
    }

    /// <summary>Opens an existing virtual HDD image already on disk.</summary>
    public static VirtualHdd Open(byte[] existingImage)
    {
        var disk = new ApaDisk(existingImage);
        var partitionHeader = disk.FindPartition(MainPartitionId)
            ?? throw new InvalidOperationException($"no '{MainPartitionId}' partition found on this image");
        var volume = new PfsVolume(disk, partitionHeader);
        volume.Mount();
        return new VirtualHdd(disk, volume);
    }

    public static VirtualHdd CreateNewFile(string path, long sizeBytes)
    {
        var hdd = CreateNew(sizeBytes);
        hdd.SaveToFile(path);
        return hdd;
    }

    public static VirtualHdd OpenFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Open(bytes);
    }

    public void SaveToFile(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, Disk.Data);
    }

    // ---- convenience save-data API (per-game directories under the main partition) ----

    private static string GameDir(string gameId) => $"/SAVES/{gameId}";

    public void WriteSaveFile(string gameId, string fileName, ReadOnlySpan<byte> data)
    {
        EnsureDirectoryPath(GameDir(gameId));
        Main.WriteFile($"{GameDir(gameId)}/{fileName}", data);
    }

    public byte[] ReadSaveFile(string gameId, string fileName) =>
        Main.ReadFile($"{GameDir(gameId)}/{fileName}");

    public bool SaveFileExists(string gameId, string fileName) =>
        Main.FileExists($"{GameDir(gameId)}/{fileName}");

    public System.Collections.Generic.IReadOnlyList<(string Name, bool IsDirectory, ulong Size)> ListSaveFiles(string gameId)
    {
        try { return Main.ListDirectory(GameDir(gameId)); }
        catch (DirectoryNotFoundException) { return Array.Empty<(string, bool, ulong)>(); }
    }

    private void EnsureDirectoryPath(string path)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = "";
        foreach (var part in parts)
        {
            string next = current + "/" + part;
            bool exists;
            try { Main.ListDirectory(current == "" ? "/" : current); exists = true; }
            catch (DirectoryNotFoundException) { exists = false; }
            if (exists)
            {
                bool found = false;
                foreach (var e in Main.ListDirectory(current == "" ? "/" : current))
                    if (e.Name == part && e.IsDirectory) { found = true; break; }
                if (!found) Main.CreateDirectory(next);
            }
            current = next;
        }
    }
}
