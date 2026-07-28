namespace DetPS2.Core;

/// <summary>
/// Product version. Versioning policy (adopted 2026-07-27, replacing an earlier scheme that
/// tied the product version to internal engineering-phase completion and reached "3.1.0"
/// while zero commercial titles could be played at all): pre-1.0 versions track only real,
/// user-visible commercial playability milestones. <c>1.0.0</c> is reserved for at least 10%
/// of <c>docs/TARGET_CATALOG.md</c>'s titles being fully playable start-to-finish with no
/// errors — not for any amount of internal/synthetic engineering completeness. Bump
/// <see cref="Version"/> only when a real playability milestone is met (see
/// <see cref="TitlesFullyPlayable"/> / <see cref="TitlesReachMainMenu"/>), never for
/// finishing an engineering phase.
/// </summary>
public static class VersionInfo
{
    public const string Version = "0.1.0";
    public const string Codename = "Foundation";
    public const string ReleaseDate = "2026-07-27";
    /// <summary>
    /// Highest completed internal engineering phase (synthetic gates: JIT, netplay
    /// infrastructure, save states, tooling, etc.). Deliberately NOT coupled to
    /// <see cref="Version"/> — see the versioning policy above.
    /// </summary>
    public const int ParityPhaseComplete = 56;
    public const int CommercialPhaseComplete = 56;
    /// <summary>Commercial titles (real user-supplied dumps) that have reached their main menu with functional input.</summary>
    public const int TitlesReachMainMenu = 0;
    /// <summary>Commercial titles fully playable start-to-finish with no errors.</summary>
    public const int TitlesFullyPlayable = 0;

    public static string Banner =>
        $"DetPS2Sharp v{Version} ({Codename}) — engineering phases 0–{CommercialPhaseComplete} (synthetic) — {TitlesFullyPlayable} commercial titles fully playable — {ReleaseDate}";
}

/// <summary>
/// Phase 49: commercial smoke checklist (synthetic always; dumps optional).
/// </summary>
public static class CommercialSmokeChecklist
{
    public sealed class Item
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public bool Passed { get; set; }
        public string Notes { get; set; } = "";
    }

    public sealed class Result
    {
        public string Version { get; init; } = VersionInfo.Version;
        public System.Collections.Generic.List<Item> Items { get; } = new();
        public int Passed => Items.FindAll(i => i.Passed).Count;
        public int Total => Items.Count;
        public bool AllRequiredPassed { get; set; }
    }

    public static Result Run()
    {
        var r = new Result();

        // Determinism
        {
            var a = new Ps2System();
            var b = new Ps2System();
            a.RunFor(10_000);
            b.RunFor(10_000);
            r.Items.Add(new Item
            {
                Id = "det-cycles",
                Name = "MasterCycles determinism",
                Passed = a.MasterCycles == b.MasterCycles,
                Notes = $"a={a.MasterCycles} b={b.MasterCycles}"
            });
        }

        // Homebrew P2
        {
            var sys = new Ps2System();
            sys.LoadHomebrewGsDemo();
            for (int i = 0; i < 500 && !sys.Hle.ExitRequested; i++)
                sys.RunFor(64);
            r.Items.Add(new Item
            {
                Id = "homebrew-p2",
                Name = "Homebrew GS demo play path",
                Passed = sys.Hle.ExitRequested || sys.Gs.PixelsWritten > 1000,
                Notes = $"px={sys.Gs.PixelsWritten}"
            });
        }

        // Commercial boot synthetic
        {
            var boot = CommercialBootRunner.Run(new UserMediaConfig(), allowSyntheticFallback: true);
            r.Items.Add(new Item
            {
                Id = "boot-p0",
                Name = "Synthetic commercial boot ≥10 P0",
                Passed = boot.P0Plus >= 10,
                Notes = $"P0+={boot.P0Plus}"
            });
        }

        // Majority gate
        {
            var maj = MajorityCampaign.RunScoredCampaign();
            r.Items.Add(new Item
            {
                Id = "majority",
                Name = "Majority ≥70% scored",
                Passed = maj.MajorityGateMet || maj.ScoredMajorityGateMet,
                Notes = $"maj={maj.MajorityPercent:P0} scored={maj.ScoredMajorityPercent:P0}"
            });
        }

        // Rollback soak
        {
            var soak = ProductionRollbackPeer.SoakTwoPlayer(120, delay: 2, frameAdvantage: 1);
            r.Items.Add(new Item
            {
                Id = "netplay-soak",
                Name = "Rollback 2P soak (synthetic)",
                Passed = soak.Certified && soak.Sync,
                Notes = soak.NetGraph
            });
        }

        // IPU not mass-DX
        {
            var maj = MajorityCampaign.RunSynthetic();
            maj.Results.Add(new MajorityCampaign.TitleResult
            {
                Id = "fmv-sample",
                Title = "FMV sample",
                Tier = "DX",
                BlockerTags = "IPU,FMV",
                Notes = "fixture"
            });
            int promoted = IpuFmvPolicy.RescoreIpuBlocked(maj, skipFmvEnabled: true);
            var (top, ipu, dx) = IpuFmvPolicy.RankIpuDx(maj);
            r.Items.Add(new Item
            {
                Id = "ipu-not-top-dx",
                Name = "IPU not top DX tag after SkipFMV",
                Passed = !top || ipu == 0,
                Notes = $"promoted={promoted} ipuDx={ipu} totalDx={dx}"
            });
        }

        // JIT parity
        {
            var a = new Ps2System();
            var b = new Ps2System();
            for (int i = 0; i < 4; i++)
            {
                uint op = (0x09u << 26) | (8u << 21) | (8u << 16) | 1;
                a.Memory.Write32(0x00100000 + (uint)(i * 4), op);
                b.Memory.Write32(0x00100000 + (uint)(i * 4), op);
            }
            a.Memory.Write32(0x00100010, 0x1000FFFF);
            b.Memory.Write32(0x00100010, 0x1000FFFF);
            a.EE.PC = b.EE.PC = 0x00100000;
            a.EE.Step(200);
            b.EeJit.Enabled = true;
            b.RunEeJit(200);
            r.Items.Add(new Item
            {
                Id = "jit-parity",
                Name = "EE JIT parity",
                Passed = a.EE.GetGpr(8).Lo == b.EE.GetGpr(8).Lo,
                Notes = $"t0 a={a.EE.GetGpr(8).Lo} b={b.EE.GetGpr(8).Lo}"
            });
        }

        // Phase 53 spine infra
        {
            var spine = DumpBootSpine.Run(new UserMediaConfig(), allowSynthetic: true);
            r.Items.Add(new Item
            {
                Id = "dump-spine",
                Name = "Dump boot spine infrastructure",
                Passed = spine.SpineInfraOk,
                Notes = $"synthP0={spine.SyntheticP0Plus} commP0={spine.CommercialP0Plus}"
            });
        }

        // Phase 54 play path
        {
            var play = PlayPathCampaign.Run();
            r.Items.Add(new Item
            {
                Id = "play-path",
                Name = "Play-path campaign gate",
                Passed = play.GateMet,
                Notes = $"P1+={play.P1Plus} P2+={play.P2Plus}"
            });
        }

        // Phase 55 majority catalog
        {
            var maj = MajorityCatalog.RunFull(new UserMediaConfig());
            r.Items.Add(new Item
            {
                Id = "majority-catalog",
                Name = "Majority catalog ≥70%",
                Passed = maj.MajorityGateMet || maj.Campaign.MajorityGateMet,
                Notes = $"maj={maj.MajorityPercent:P0} scored={maj.ScoredNonDx}"
            });
        }

        // Phase 56 netplay cert
        {
            var cert = NetplayCertification.Run(frames: 200);
            r.Items.Add(new Item
            {
                Id = "netplay-cert",
                Name = "Netplay rollback soak (synthetic/homebrew only)",
                Passed = cert.ProductionGateMet,
                Notes = $"certified={cert.CertifiedCount}"
            });
        }

        r.AllRequiredPassed = r.Items.TrueForAll(i => i.Passed);
        return r;
    }

    public static string Format(Result r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Commercial Smoke Checklist v{r.Version} ===");
        sb.AppendLine($"Passed {r.Passed}/{r.Total}  allRequired={r.AllRequiredPassed}");
        foreach (var i in r.Items)
            sb.AppendLine($"  [{(i.Passed ? "PASS" : "FAIL")}] {i.Id}: {i.Name} — {i.Notes}");
        return sb.ToString();
    }
}
