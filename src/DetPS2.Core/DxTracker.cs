using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Deferred-title (DX) tracker (Phase 39+). Promote/demote tiers without blocking v2.0.
/// </summary>
public sealed class DxTracker
{
    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Tier { get; set; } = "DX";
        public string Tags { get; set; } = "";
        public string Notes { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    private readonly Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _map.Count;
    public IEnumerable<Entry> Entries => _map.Values;

    public void Upsert(string id, string title, string tier, string tags, string notes)
    {
        _map[id] = new Entry
        {
            Id = id,
            Title = title,
            Tier = tier,
            Tags = tags,
            Notes = notes,
            UpdatedUtc = DateTime.UtcNow.ToString("O")
        };
    }

    public bool TryGet(string id, out Entry entry) => _map.TryGetValue(id, out entry!);

    public bool Promote(string id, string newTier, string? note = null)
    {
        if (!_map.TryGetValue(id, out var e)) return false;
        if (!CompatEntry.IsValidTier(newTier)) return false;
        e.Tier = newTier;
        if (!string.IsNullOrEmpty(note))
            e.Notes = string.IsNullOrEmpty(e.Notes) ? note! : e.Notes + "; " + note;
        e.UpdatedUtc = DateTime.UtcNow.ToString("O");
        return true;
    }

    public void LoadMarkdown(string path)
    {
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (!line.StartsWith('|') || line.Contains("---")) continue;
            if (line.Contains("Title", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Tags", StringComparison.OrdinalIgnoreCase))
                continue;
            var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cells.Length < 2) continue;
            string id = cells[0];
            if (id.Equals("id", StringComparison.OrdinalIgnoreCase)) continue;
            Upsert(id,
                cells.Length > 1 ? cells[1] : id,
                "DX",
                cells.Length > 2 ? cells[2] : "",
                cells.Length > 3 ? cells[3] : "");
        }
    }

    public void SaveMarkdown(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Deferred Titles (DX)");
        sb.AppendLine();
        sb.AppendLine("| id | Title | Tags | Notes | Updated |");
        sb.AppendLine("|----|-------|------|-------|---------|");
        foreach (var e in _map.Values)
        {
            if (e.Tier != "DX") continue;
            sb.AppendLine($"| {e.Id} | {e.Title} | {e.Tags} | {e.Notes} | {e.UpdatedUtc} |");
        }
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString());
    }

    public static DxTracker FromCampaign(MajorityCampaign.Report report)
    {
        var t = new DxTracker();
        foreach (var r in report.Results)
        {
            if (r.Tier != "DX") continue;
            t.Upsert(r.Id, r.Title, "DX", r.BlockerTags, r.Notes);
        }
        return t;
    }
}

/// <summary>Netplay-certified title list (Phases 38/46/49).</summary>
public static class NetplayCertified
{
    public static readonly string[] SyntheticCertified =
    {
        "homebrew-gs-demo",
        "iso-boot-homebrew",
        "input-replay-determinism",
        "stub-bios-harness",
        "iso-multidir-modules"
    };

    /// <summary>Phase 46: titles that passed ProductionRollbackPeer soak (≥100 frames sync).</summary>
    public static readonly string[] SoakCertified =
    {
        "homebrew-gs-demo"
    };

    public static string FormatMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Netplay-Certified Titles (v{VersionInfo.Version})");
        sb.AppendLine();
        sb.AppendLine("Det mode + rollback session tests pass for these **synthetic** fixtures.");
        sb.AppendLine("Commercial certification requires user dumps and a longer 2P soak.");
        sb.AppendLine();
        sb.AppendLine("| id | Status |");
        sb.AppendLine("|----|--------|");
        foreach (string id in SyntheticCertified)
        {
            string soak = IsSoakCertified(id) ? " + soak" : "";
            sb.AppendLine($"| {id} | Certified (synthetic{soak}) |");
        }
        sb.AppendLine();
        sb.AppendLine("## Protocol");
        sb.AppendLine();
        sb.AppendLine("- Rollback window default 8");
        sb.AppendLine("- Frame advantage default 1 (Phase 46)");
        sb.AppendLine("- Transports: TCP LAN (N3), UDP prototype (N4), in-memory tests");
        sb.AppendLine("- Det mode only on the wire");
        return sb.ToString();
    }

    public static bool IsCertified(string id)
    {
        foreach (string s in SyntheticCertified)
            if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static bool IsSoakCertified(string id)
    {
        foreach (string s in SoakCertified)
            if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
