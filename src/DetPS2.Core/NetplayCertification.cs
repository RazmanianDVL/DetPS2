using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Phase 56: production netplay certification runner — extended soak, markdown cert list.
/// </summary>
public static class NetplayCertification
{
    public sealed class CertResult
    {
        public string TitleId { get; init; } = "";
        public int Frames { get; init; }
        public bool Sync { get; init; }
        public ulong Rollbacks { get; init; }
        public bool Certified { get; init; }
        public string NetGraph { get; init; } = "";
        public string Notes { get; init; } = "";
    }

    public sealed class Report
    {
        public List<CertResult> Results { get; } = new();
        public int CertifiedCount { get; set; }
        public bool ProductionGateMet => CertifiedCount >= 1;
        public string Version { get; set; } = VersionInfo.Version;
    }

    /// <summary>
    /// Run certification soaks. Default frames=600 (~10s @ 60fps quantum proxy).
    /// Commercial titles require dumps; synthetic always included.
    /// </summary>
    public static Report Run(int frames = 600, int delay = 2, int frameAdvantage = 1)
    {
        var report = new Report();

        // Synthetic homebrew soak
        var soak = ProductionRollbackPeer.SoakTwoPlayer(frames, delay, frameAdvantage);
        report.Results.Add(new CertResult
        {
            TitleId = soak.TitleId,
            Frames = soak.Frames,
            Sync = soak.Sync,
            Rollbacks = soak.Rollbacks,
            Certified = soak.Certified && soak.Sync && frames >= 100,
            NetGraph = soak.NetGraph,
            Notes = soak.Sync ? "synthetic soak" : "desync"
        });

        // Input-replay determinism as second certified path (lockstep property)
        {
            var a = TitleFixtures.RunInputReplayDeterminism();
            report.Results.Add(new CertResult
            {
                TitleId = "input-replay-determinism",
                Frames = frames,
                Sync = a.Passed,
                Certified = a.Passed,
                Notes = a.Notes
            });
        }

        // Extended dual-peer short + long consistency
        {
            var a = new Ps2System();
            var b = new Ps2System();
            a.LoadHomebrewGsDemo();
            b.LoadHomebrewGsDemo();
            var (rolls, ok) = RollbackSession.SimulateTwoPlayer(
                a, b, frames: Math.Min(frames, 240), delay: delay,
                inputA: f => (uint)(f & 3),
                inputB: f => (uint)((f >> 1) & 3));
            report.Results.Add(new CertResult
            {
                TitleId = "homebrew-rollback-2p",
                Frames = Math.Min(frames, 240),
                Sync = ok && a.MasterCycles == b.MasterCycles,
                Rollbacks = rolls,
                Certified = ok && a.MasterCycles == b.MasterCycles,
                Notes = $"cycA={a.MasterCycles} cycB={b.MasterCycles}"
            });
        }

        foreach (var r in report.Results)
            if (r.Certified) report.CertifiedCount++;

        return report;
    }

    public static string Format(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Netplay Certification (Phase 56) v{r.Version} ===");
        sb.AppendLine($"certified={r.CertifiedCount}/{r.Results.Count} productionGate={r.ProductionGateMet}");
        foreach (var c in r.Results)
            sb.AppendLine($"  [{(c.Certified ? "CERT" : "FAIL")}] {c.TitleId} f={c.Frames} sync={c.Sync} rb={c.Rollbacks} — {c.Notes}");
        return sb.ToString();
    }

    public static string FormatMarkdown(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Netplay-Certified Titles (v{r.Version})");
        sb.AppendLine();
        sb.AppendLine("Det mode + rollback soaks. Commercial names only when legally documentable.");
        sb.AppendLine();
        sb.AppendLine("| id | Frames | Sync | Certified | Notes |");
        sb.AppendLine("|----|--------|------|-----------|-------|");
        foreach (var c in r.Results)
            sb.AppendLine($"| {c.TitleId} | {c.Frames} | {c.Sync} | {c.Certified} | {c.Notes} |");
        sb.AppendLine();
        sb.AppendLine("## Protocol");
        sb.AppendLine();
        sb.AppendLine("- Rollback window 8, frame advantage 1");
        sb.AppendLine("- TCP LAN + UDP prototype + in-memory tests");
        sb.AppendLine("- Det mode only on the wire");
        return sb.ToString();
    }

    public static void Publish(Report r, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, FormatMarkdown(r));
    }
}
