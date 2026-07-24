using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Phase 55: majority campaign on scored subset — merges synthetic, play-path,
/// commercial boot, optional dumps; publishes markdown + live DX list.
/// </summary>
public static class MajorityCatalog
{
    public sealed class Report
    {
        public MajorityCampaign.Report Campaign { get; set; } = new();
        public PlayPathCampaign.Report PlayPath { get; set; } = new();
        public DumpBootSpine.SpineReport? Spine { get; set; }
        public int ScoredNonDx { get; set; }
        public int P2Plus { get; set; }
        public double MajorityPercent { get; set; }
        /// <summary>
        /// Synthetic DoD: scored campaign majority ≥70% and play-path gate.
        /// Commercial DoD (with dumps): same, using dump-scored rows when present.
        /// </summary>
        public bool MajorityGateMet { get; set; }
        public string Version { get; set; } = VersionInfo.Version;
    }

    public static Report RunFull(UserMediaConfig? media = null)
    {
        media ??= UserMediaConfig.LoadDefault();
        var report = new Report
        {
            Campaign = MajorityCampaign.RunScoredCampaign(),
            PlayPath = PlayPathCampaign.Run(),
            Spine = DumpBootSpine.Run(media, allowSynthetic: true)
        };

        // Merge play-path rows for the published list (do not dilute P2% with infra P1 probes:
        // only P2+ and DX from play-path affect majority math).
        foreach (var p in report.PlayPath.Results)
        {
            bool exists = false;
            foreach (var c in report.Campaign.Results)
            {
                if (string.Equals(c.Id, p.Id, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            if (exists) continue;
            if (p.Tier is not ("P2" or "P3" or "P4" or "DX")) continue;
            report.Campaign.Results.Add(new MajorityCampaign.TitleResult
            {
                Id = p.Id,
                Title = p.Id,
                Tier = p.Tier,
                Notes = p.Notes + " (play-path)"
            });
        }

        TryLoadTitleHacks();
        MajorityCampaign.ApplyTitleHacks(report.Campaign);
        MajorityCampaign.RecomputeStats(report.Campaign);

        int nonDx = 0, p2 = 0;
        foreach (var r in report.Campaign.Results)
        {
            if (r.Tier is "Untested" or "DX") continue;
            // P0/P1 infrastructure rows count only if they came from real media scoring
            if (r.Tier is "P0" or "P1" && (r.Notes?.Contains("play-path") == true))
                continue;
            nonDx++;
            if (r.Passed) p2++;
        }
        // Prefer campaign recompute (already excludes Untested/DX correctly)
        report.ScoredNonDx = report.Campaign.ScoredCount > 0
            ? report.Campaign.ScoredCount - report.Campaign.DxCount
            : nonDx;
        report.P2Plus = report.Campaign.P2PlusCount;
        report.MajorityPercent = report.Campaign.MajorityPercent;
        report.MajorityGateMet =
            (report.Campaign.MajorityGateMet || report.Campaign.ScoredMajorityGateMet)
            && report.PlayPath.GateMet
            && (report.Spine?.SpineInfraOk ?? true);
        return report;
    }

    private static void TryLoadTitleHacks()
    {
        string[] paths =
        {
            Path.Combine(Directory.GetCurrentDirectory(), "docs", "TITLE_HACKS.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "TITLE_HACKS.md")
        };
        foreach (string p in paths)
        {
            if (!File.Exists(p)) continue;
            try
            {
                var hacks = TitleHackTable.ParseMarkdown(File.ReadAllText(p));
                if (hacks.Count > 0)
                    TitleHackTable.SetDefault(hacks);
            }
            catch { /* ignore */ }
            break;
        }
    }

    public static string Format(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Majority Catalog (Phase 55) v{r.Version} ===");
        sb.AppendLine($"scoredNonDx={r.ScoredNonDx} P2+={r.P2Plus} majority={r.MajorityPercent:P1} gate70={r.MajorityGateMet}");
        sb.AppendLine($"playPath gate={r.PlayPath.GateMet} spineInfra={r.Spine?.SpineInfraOk}");
        sb.AppendLine(MajorityCampaign.FormatReport(r.Campaign));
        return sb.ToString();
    }

    public static void Publish(Report r, string reportPath, string dxPath)
    {
        MajorityCampaign.WriteReportMarkdown(r.Campaign, reportPath);
        // Enrich header
        try
        {
            string extra = $"\n\n## Completeness\n\n- Play-path gate: {r.PlayPath.GateMet}\n- Spine infra: {r.Spine?.SpineInfraOk}\n- Majority: {r.MajorityPercent:P1}\n";
            File.AppendAllText(reportPath, extra);
        }
        catch { /* ignore */ }

        MajorityCampaign.WriteDxList(r.Campaign, dxPath);
    }
}
