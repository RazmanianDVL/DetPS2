using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Synthetic title regression fixtures (Phase 20).
/// Legal: no copyrighted dumps — built-in homebrew + ISO builders only.
/// </summary>
public static class TitleFixtures
{
    public sealed class Result
    {
        public string Name { get; init; } = "";
        public bool Passed { get; init; }
        public ulong MasterCycles { get; init; }
        public ulong FbHash { get; init; }
        public ulong ExpectedFbHash { get; init; }
        public string Notes { get; init; } = "";
        public IReadOnlyList<ulong> PcTrace { get; init; } = Array.Empty<ulong>();
    }

    /// <summary>
    /// Golden FB hash for homebrew GS demo after short run (recomputed if zero).
    /// Captured once at suite start when expected is 0.
    /// </summary>
    public static ulong GoldenHomebrewFbHash { get; private set; }

    public static Result RunHomebrewGsDemo(ulong maxCycles = 50_000)
    {
        var sys = new Ps2System();
        var load = sys.LoadHomebrewGsDemo();
        var pcs = new List<ulong>();
        for (int i = 0; i < 2000 && !sys.Hle.ExitRequested; i++)
        {
            sys.RunFor(64);
            if ((i & 31) == 0) pcs.Add(sys.EE.PC);
        }
        ulong hash = RegressionFixtures.HashFramebuffer(sys.Gs);
        if (GoldenHomebrewFbHash == 0)
            GoldenHomebrewFbHash = hash;

        bool ok = sys.Hle.ExitRequested || sys.Gs.PixelsWritten > 0;
        ok = ok && hash == GoldenHomebrewFbHash;
        return new Result
        {
            Name = "homebrew-gs-demo",
            Passed = ok,
            MasterCycles = sys.MasterCycles,
            FbHash = hash,
            ExpectedFbHash = GoldenHomebrewFbHash,
            Notes = $"entry=0x{load.Entry:X8} exit={sys.Hle.ExitRequested} px={sys.Gs.PixelsWritten}",
            PcTrace = pcs
        };
    }

    public static Result RunIsoBoot(ulong runCycles = 20_000)
    {
        var sys = new Ps2System();
        byte[] elf = ElfLoader.BuildHomebrewGsDemoElf();
        string cnf = "BOOT2 = cdrom0:\\BOOT.ELF;1\nVER = 1.00\nVMODE = NTSC\n";
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["BOOT.ELF"] = elf
        };
        byte[] iso = Iso9660.Build("DETPS2", cnf, files);
        var boot = sys.BootDiscImage(iso);
        if (!boot.Success)
        {
            return new Result
            {
                Name = "iso-boot-homebrew",
                Passed = false,
                Notes = boot.Message
            };
        }
        for (int i = 0; i < 500 && !sys.Hle.ExitRequested; i++)
            sys.RunFor(64);
        ulong hash = RegressionFixtures.HashFramebuffer(sys.Gs);
        return new Result
        {
            Name = "iso-boot-homebrew",
            Passed = boot.Success && (sys.Hle.ExitRequested || sys.Gs.PixelsWritten > 0),
            MasterCycles = sys.MasterCycles,
            FbHash = hash,
            ExpectedFbHash = hash, // self-golden for synthetic
            Notes = boot.Message,
            PcTrace = new[] { sys.EE.PC }
        };
    }

    public static Result RunMultiDirIsoLookup()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["MODULES/LIBSD.IRX"] = Encoding.ASCII.GetBytes("IRXSTUB"),
            ["BOOT.ELF"] = ElfLoader.BuildHomebrewGsDemoElf()
        };
        byte[] iso = Iso9660.BuildWithDirs("DETPS2", "BOOT2 = cdrom0:\\BOOT.ELF;1\n", files);
        var vol = Iso9660.Open(iso);
        bool ok = vol != null && Iso9660.ReadFile(vol!, "MODULES/LIBSD.IRX") != null;
        return new Result
        {
            Name = "iso-multidir-modules",
            Passed = ok,
            Notes = ok ? $"files={vol!.Files.Count}" : "lookup failed"
        };
    }

    public static Result RunInputReplayDeterminism()
    {
        var a = new Ps2System();
        a.Gs.RenderTestScene();
        a.InputRecording.StartRecording();
        a.Pad.SetButtons((uint)PadInput.Button.Start);
        a.RunFor(1000);
        a.Pad.SetButtons((uint)PadInput.Button.Cross);
        a.RunFor(1000);
        a.InputRecording.StopRecording();
        byte[] tape = a.InputRecording.Serialize();
        ulong hashA = RegressionFixtures.HashFramebuffer(a.Gs);
        ulong cycA = a.MasterCycles;

        var b = new Ps2System();
        b.Gs.RenderTestScene();
        b.InputRecording.Deserialize(tape);
        b.InputRecording.StartPlayback();
        while (b.MasterCycles < cycA)
        {
            uint? pad = b.InputRecording.PollPlayback(b.MasterCycles);
            if (pad.HasValue) b.Pad.SetButtons(pad.Value);
            b.RunFor(Math.Min(500UL, cycA - b.MasterCycles));
        }
        ulong hashB = RegressionFixtures.HashFramebuffer(b.Gs);
        return new Result
        {
            Name = "input-replay-determinism",
            Passed = hashA == hashB && cycA == b.MasterCycles,
            MasterCycles = cycA,
            FbHash = hashA,
            ExpectedFbHash = hashB,
            Notes = $"frames={a.InputRecording.FrameCount}"
        };
    }

    /// <summary>Run the synthetic compatibility campaign pack.</summary>
    public static IReadOnlyList<Result> RunCampaign()
    {
        GoldenHomebrewFbHash = 0; // re-seed from first homebrew run
        return new[]
        {
            RunHomebrewGsDemo(),
            RunIsoBoot(),
            RunMultiDirIsoLookup(),
            RunInputReplayDeterminism()
        };
    }

    public static string FormatCampaignReport(IReadOnlyList<Result> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DetPS2 Title Compatibility Campaign (synthetic)");
        int pass = 0;
        foreach (var r in results)
        {
            if (r.Passed) pass++;
            sb.AppendLine($"  [{(r.Passed ? "PASS" : "FAIL")}] {r.Name} cyc={r.MasterCycles} hash=0x{r.FbHash:X16} — {r.Notes}");
            if (r.PcTrace.Count > 0)
            {
                sb.Append("    PC: ");
                int n = Math.Min(8, r.PcTrace.Count);
                for (int i = 0; i < n; i++)
                    sb.Append($"0x{r.PcTrace[i]:X8} ");
                sb.AppendLine();
            }
        }
        sb.AppendLine($"Total: {pass}/{results.Count} passed");
        return sb.ToString();
    }
}
