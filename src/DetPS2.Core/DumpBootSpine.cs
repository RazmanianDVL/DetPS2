using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Phase 53: dump-driven boot spine — media discovery, readiness check, ranked boot pass.
/// Without dumps: full readiness report + synthetic spine still exercises the pipeline.
/// </summary>
public static class DumpBootSpine
{
    public sealed class Readiness
    {
        public bool HasUserMediaConfig { get; set; }
        public bool HasBios { get; set; }
        public int TitlePathsConfigured { get; set; }
        public int TitlePathsExisting { get; set; }
        public List<string> DiscoveredCandidates { get; } = new();
        public List<string> Hints { get; } = new();
        public bool ReadyForCommercialP0 => HasBios && TitlePathsExisting >= 1;
        public bool ReadyForMajoritySample => HasBios && TitlePathsExisting >= 3;
    }

    public sealed class SpineReport
    {
        public Readiness Readiness { get; set; } = new();
        public CommercialBootRunner.RunReport Boot { get; set; } = new();
        public string BlockerRankText { get; set; } = "";
        public int SyntheticP0Plus { get; set; }
        public int CommercialP0Plus { get; set; }
        public bool SpineInfraOk { get; set; }
    }

    /// <summary>Scan common folders for BIOS/ISO candidates (paths only; never copy).</summary>
    public static List<string> DiscoverMediaCandidates(string? root = null)
    {
        var found = new List<string>();
        string baseDir = root ?? Directory.GetCurrentDirectory();
        string[] names =
        {
            "BIOS", "bios", "roms", "ROMs", "iso", "ISO", "games", "games",
            "ps2", "PS2", "media", "dumps"
        };
        string[] exts = { ".bin", ".rom", ".iso", ".nrg", ".chd", ".elf" };

        void Scan(string dir, int depth)
        {
            if (depth > 2 || !Directory.Exists(dir)) return;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    string e = Path.GetExtension(f);
                    foreach (string ext in exts)
                    {
                        if (e.Equals(ext, StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(f);
                            break;
                        }
                    }
                    if (found.Count >= 32) return;
                }
                if (depth < 2)
                {
                    foreach (string sub in Directory.EnumerateDirectories(dir))
                    {
                        string leaf = Path.GetFileName(sub);
                        foreach (string n in names)
                        {
                            if (leaf.Equals(n, StringComparison.OrdinalIgnoreCase))
                            {
                                Scan(sub, depth + 1);
                                break;
                            }
                        }
                        if (found.Count >= 32) return;
                    }
                }
            }
            catch
            {
                // permission — ignore
            }
        }

        Scan(baseDir, 0);
        // also one level up common
        var parent = Directory.GetParent(baseDir);
        if (parent != null)
        {
            foreach (string n in names)
            {
                string p = Path.Combine(parent.FullName, n);
                if (Directory.Exists(p))
                    Scan(p, 0);
            }
        }
        return found;
    }

    public static Readiness CheckReadiness(UserMediaConfig? cfg = null, string? searchRoot = null)
    {
        cfg ??= UserMediaConfig.LoadDefault(searchRoot);
        var r = new Readiness
        {
            HasUserMediaConfig = cfg.HasBios || cfg.Titles.Count > 0,
            HasBios = cfg.HasBios,
            TitlePathsConfigured = cfg.Titles.Count,
            TitlePathsExisting = cfg.ExistingTitleCount
        };
        r.DiscoveredCandidates.AddRange(DiscoverMediaCandidates(searchRoot));

        if (!r.HasBios)
            r.Hints.Add("Set BiosPath in user-media.json to your PS2 BIOS dump.");
        if (r.TitlePathsExisting == 0)
            r.Hints.Add("Add at least one ISO/ELF under Titles[].Path (gitignored).");
        if (r.DiscoveredCandidates.Count > 0 && r.TitlePathsExisting == 0)
            r.Hints.Add($"Found {r.DiscoveredCandidates.Count} media-like file(s) nearby — wire them into user-media.json.");
        if (r.ReadyForCommercialP0)
            r.Hints.Add("Ready: run commercial-boot / dump-spine with your media.");
        else
            r.Hints.Add("Synthetic spine still runs in CI without dumps.");
        return r;
    }

    /// <summary>Full spine: readiness + boot run + blocker rank.</summary>
    public static SpineReport Run(UserMediaConfig? cfg = null, bool allowSynthetic = true)
    {
        cfg ??= UserMediaConfig.LoadDefault();
        var report = new SpineReport
        {
            Readiness = CheckReadiness(cfg)
        };
        report.Boot = CommercialBootRunner.Run(cfg, allowSyntheticFallback: allowSynthetic);

        var ranker = new BlockerRanker();
        ranker.IngestReport(report.Boot);
        report.BlockerRankText = ranker.FormatReport(16);

        foreach (var r in report.Boot.Results)
        {
            if (r.Tier is not ("P0" or "P1" or "P2" or "P3" or "P4")) continue;
            if (r.Synthetic) report.SyntheticP0Plus++;
            else report.CommercialP0Plus++;
        }

        // Infra OK when synthetic gate holds OR commercial P0 exists
        report.SpineInfraOk = report.Boot.P0Plus >= 10
                              || report.CommercialP0Plus >= 1
                              || report.SyntheticP0Plus >= 10;
        return report;
    }

    public static string Format(SpineReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Dump Boot Spine (Phase 53) ===");
        sb.AppendLine($"readyCommercialP0={r.Readiness.ReadyForCommercialP0} majoritySample={r.Readiness.ReadyForMajoritySample}");
        sb.AppendLine($"bios={r.Readiness.HasBios} titlesExisting={r.Readiness.TitlePathsExisting}/{r.Readiness.TitlePathsConfigured}");
        sb.AppendLine($"discovered={r.Readiness.DiscoveredCandidates.Count} syntheticP0+={r.SyntheticP0Plus} commercialP0+={r.CommercialP0Plus}");
        sb.AppendLine($"spineInfraOk={r.SpineInfraOk}");
        foreach (string h in r.Readiness.Hints)
            sb.AppendLine($"  hint: {h}");
        sb.AppendLine(r.Boot.Summary);
        if (!string.IsNullOrEmpty(r.BlockerRankText))
            sb.AppendLine(r.BlockerRankText);
        return sb.ToString();
    }

    public static void WriteBlockerMarkdown(SpineReport r, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Boot Spine Blockers");
        sb.AppendLine();
        sb.AppendLine($"Generated by DumpBootSpine. commercialP0+={r.CommercialP0Plus} syntheticP0+={r.SyntheticP0Plus}");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(r.BlockerRankText);
        sb.AppendLine("```");
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString());
    }
}
