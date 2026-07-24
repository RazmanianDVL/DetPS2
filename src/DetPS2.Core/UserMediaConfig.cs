using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DetPS2.Core;

/// <summary>
/// User-supplied BIOS/ISO/ELF paths (Phase 40). Never commit real files — only this config shape.
/// Load from gitignored <c>user-media.json</c>.
/// </summary>
public sealed class UserMediaConfig
{
    public string BiosPath { get; set; } = "";
    public List<UserTitleEntry> Titles { get; set; } = new();
    public ulong BootCycles { get; set; } = 5_000_000;
    public ulong SampleEvery { get; set; } = 100_000;

    public bool HasBios => !string.IsNullOrWhiteSpace(BiosPath) && File.Exists(BiosPath);
    public int ExistingTitleCount
    {
        get
        {
            int n = 0;
            foreach (var t in Titles)
                if (t.Exists) n++;
            return n;
        }
    }

    public static UserMediaConfig Load(string path)
    {
        if (!File.Exists(path))
            return new UserMediaConfig();
        string json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<UserMediaConfig>(json, JsonOpts());
        return cfg ?? new UserMediaConfig();
    }

    public static UserMediaConfig LoadDefault(string? searchFromDir = null)
    {
        string[] names = { "user-media.json", "user-media.local.json" };
        string dir = searchFromDir ?? Directory.GetCurrentDirectory();
        for (int up = 0; up < 6; up++)
        {
            foreach (string name in names)
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p))
                    return Load(p);
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return new UserMediaConfig();
    }

    public void Save(string path)
    {
        string? d = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(d))
            Directory.CreateDirectory(d);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts()));
    }

    private static JsonSerializerOptions JsonOpts() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}

public sealed class UserTitleEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>iso | elf | bin</summary>
    public string Kind { get; set; } = "iso";

    public bool Exists => !string.IsNullOrWhiteSpace(Path) && File.Exists(Path);
}
