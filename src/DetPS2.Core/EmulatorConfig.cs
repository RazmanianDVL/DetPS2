using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DetPS2.Core;

/// <summary>
/// Per-game and global settings (Phase 37). Host serializes to JSON; core only reads values.
/// </summary>
public sealed class GameSettings
{
    public string GameId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string Mode { get; set; } = "Perf"; // Det | Perf
    public double Upscale { get; set; } = 1.0;
    public int Deadzone { get; set; } = 12;
    public bool FrameLimit { get; set; } = true;
    public int TargetFps { get; set; } = 60;
    public int RunAheadFrames { get; set; } = 0; // solo Perf only
    public bool WidescreenPatch { get; set; }
    public string WidescreenPatchPath { get; set; } = "";
    public string Notes { get; set; } = "";

    public static GameSettings DefaultFor(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        return new GameSettings
        {
            GameId = TargetCatalog.SanitizeId(name),
            DisplayName = name,
            Path = path
        };
    }
}

public sealed class EmulatorConfig
{
    public string Version { get; set; } = VersionInfo.Version;
    public string BiosPath { get; set; } = "";
    /// <summary>Central media root (ISOs/ELFs). Persisted in AppData config.</summary>
    public string GamesFolder { get; set; } = "";
    /// <summary>Memory card path; prefer under GamesFolder\memcards\ (browser later).</summary>
    public string MemCardPath { get; set; } = "";
    /// <summary>Memory cards are the primary, always-on save path (Sio2 attaches a MemoryCard
    /// unconditionally in Ps2System — see its constructor). The virtual HDD (ApaDisk/PfsVolume,
    /// VirtualHdd.cs) is a real, working alternative with far larger capacity, but is opt-in:
    /// off by default, and only created/mounted when both EnableVirtualHdd is true AND
    /// VirtualHddPath is set (see Ps2System.TryEnableVirtualHdd). Games only ever see it if a
    /// title's save-path code explicitly checks Ps2System.Hdd, which stays null otherwise.</summary>
    public bool EnableVirtualHdd { get; set; }
    /// <summary>Host file backing the virtual HDD image. Created fresh (VirtualHddSizeMb) if it
    /// doesn't exist yet when EnableVirtualHdd is turned on.</summary>
    public string VirtualHddPath { get; set; } = "";
    public int VirtualHddSizeMb { get; set; } = 8192; // 8GB — real "fat" PS2 HDDs ranged 40-160GB, this is a practical default
    public string LastGameId { get; set; } = "";
    public bool DefaultFrameLimit { get; set; } = true;
    public int DefaultTargetFps { get; set; } = 60;
    public string PresentMode { get; set; } = "Software";
    public bool EnableJit { get; set; }
    public bool AutoRunAfterBoot { get; set; } = true;
    /// <summary>Legacy XInput index (-1 = keyboard). Migrated to Player1DeviceId.</summary>
    public int Player1Gamepad { get; set; } = -1;
    public int Player2Gamepad { get; set; } = -1;
    /// <summary>Device id: kb | xi:N | hid:VID:PID:n</summary>
    public string Player1DeviceId { get; set; } = "kb";
    public string Player2DeviceId { get; set; } = "kb";
    /// <summary>Standard or GuitarHero mapping profile.</summary>
    public string Player1Profile { get; set; } = "Standard";
    public string Player2Profile { get; set; } = "Standard";
    public bool VerifyMediaOnBoot { get; set; } = true;

    public void MigrateGamepadIds()
    {
        if (string.IsNullOrEmpty(Player1DeviceId) || Player1DeviceId == "kb")
        {
            if (Player1Gamepad >= 0) Player1DeviceId = "xi:" + Player1Gamepad;
            else Player1DeviceId = "kb";
        }
        if (string.IsNullOrEmpty(Player2DeviceId) || Player2DeviceId == "kb")
        {
            if (Player2Gamepad >= 0) Player2DeviceId = "xi:" + Player2Gamepad;
            else Player2DeviceId = "kb";
        }
        if (string.IsNullOrEmpty(Player1Profile)) Player1Profile = "Standard";
        if (string.IsNullOrEmpty(Player2Profile)) Player2Profile = "Standard";
    }

    public static ControllerProfile ParseProfile(string? s) =>
        string.Equals(s, "GuitarHero", StringComparison.OrdinalIgnoreCase)
            ? ControllerProfile.GuitarHero
            : ControllerProfile.Standard;
    public List<GameSettings> Games { get; set; } = new();

    public bool HasBiosFile => !string.IsNullOrWhiteSpace(BiosPath) && File.Exists(BiosPath);
    public bool HasGamesFolder => !string.IsNullOrWhiteSpace(GamesFolder) && Directory.Exists(GamesFolder);

    public static EmulatorConfig Load(string path)
    {
        if (!File.Exists(path))
            return new EmulatorConfig();
        string json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<EmulatorConfig>(json) ?? new EmulatorConfig();
        cfg.EnsureMemCardPathDefault();
        cfg.MigrateGamepadIds();
        return cfg;
    }

    public void Save(string path)
    {
        Version = VersionInfo.Version;
        EnsureMemCardPathDefault();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
    }

    public void EnsureMemCardPathDefault()
    {
        if (!string.IsNullOrWhiteSpace(MemCardPath)) return;
        if (HasGamesFolder)
            MemCardPath = Path.Combine(GamesFolder, "memcards", "slot1.ps2");
        else
            MemCardPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DetPS2", "memcards", "slot1.ps2");
    }

    public void ApplyScan(string folder, IEnumerable<GameSettings> games)
    {
        GamesFolder = folder;
        Games.Clear();
        Games.AddRange(games);
        EnsureMemCardPathDefault();
    }

    public GameSettings GetOrAddGame(string gamePath)
    {
        foreach (var g in Games)
        {
            if (string.Equals(g.Path, gamePath, StringComparison.OrdinalIgnoreCase))
                return g;
        }
        var created = GameSettings.DefaultFor(gamePath);
        Games.Add(created);
        return created;
    }

    public byte[] ToBytes()
    {
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static EmulatorConfig FromBytes(byte[] data)
    {
        var cfg = JsonSerializer.Deserialize<EmulatorConfig>(Encoding.UTF8.GetString(data)) ?? new EmulatorConfig();
        cfg.EnsureMemCardPathDefault();
        return cfg;
    }
}

/// <summary>Scan a folder for bootable media (ISO/ELF/BIN) — Phase 37 / MP1 library.</summary>
public static class GameLibrary
{
    private static readonly string[] Ext =
    {
        ".iso", ".ISO", ".elf", ".ELF", ".bin", ".BIN", ".cso", ".CSO"
    };

    public static bool IsCompatibleExtension(string path)
    {
        string ext = Path.GetExtension(path);
        foreach (string e in Ext)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string MediaKind(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".iso" => "ISO",
            ".elf" => "ELF",
            ".bin" => "BIN",
            ".cso" => "CSO",
            _ => ext.TrimStart('.').ToUpperInvariant()
        };
    }

    public static bool IsBootableNow(string path)
    {
        // CSO listed but not decodable yet
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".iso" or ".elf" or ".bin";
    }

    public static List<GameSettings> ScanFolder(string folder, int max = 500)
    {
        var list = new List<GameSettings>();
        folder = NormalizeLibraryPath(folder);
        if (string.IsNullOrWhiteSpace(folder))
            return list;
        try
        {
            if (!Directory.Exists(folder))
                return list;
        }
        catch
        {
            return list;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories);
        }
        catch
        {
            // Some UNC shares reject AllDirectories — fall back to top level
            try { files = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly); }
            catch { return list; }
        }

        foreach (string file in files)
        {
            if (!IsCompatibleExtension(file)) continue;
            list.Add(GameSettings.DefaultFor(file));
            if (list.Count >= max) break;
        }
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Normalize local or UNC library roots (\\server\share, mapped drives).</summary>
    public static string NormalizeLibraryPath(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";
        folder = folder.Trim().Trim('"');
        // Keep UNC as-is for Directory APIs (extended \\?\UNC\ is optional)
        if (folder.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return folder;
        if (folder.StartsWith(@"\\", StringComparison.Ordinal))
            return folder.TrimEnd('\\');
        try { return Path.GetFullPath(folder); }
        catch { return folder; }
    }
}
