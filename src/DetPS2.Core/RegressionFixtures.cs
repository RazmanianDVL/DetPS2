using System;
using System.Diagnostics;

namespace DetPS2.Core;

/// <summary>
/// Golden MasterCycles + framebuffer hash fixtures (Phase 10 regression pack).
/// </summary>
public static class RegressionFixtures
{
    /// <summary>FNV-1a over ARGB framebuffer (deterministic).</summary>
    public static ulong HashFramebuffer(ReadOnlySpan<uint> fb)
    {
        ulong hash = 2166136261UL;
        for (int i = 0; i < fb.Length; i++)
        {
            uint p = fb[i];
            hash ^= p & 0xFF; hash *= 16777619;
            hash ^= (p >> 8) & 0xFF; hash *= 16777619;
            hash ^= (p >> 16) & 0xFF; hash *= 16777619;
            hash ^= (p >> 24) & 0xFF; hash *= 16777619;
        }
        return hash;
    }

    public static ulong HashFramebuffer(Gs gs) => HashFramebuffer(gs.GetFramebufferSpan());

    public static (ulong cycles, ulong fbHash) CaptureTestScene(ulong runCycles = 10_000)
    {
        var sys = new Ps2System();
        sys.RunFor(runCycles);
        sys.Gs.RenderTestScene();
        return (sys.MasterCycles, HashFramebuffer(sys.Gs));
    }

    public const ulong GoldenTestSceneCycles_10k = 10_000;

    /// <summary>
    /// Wall-clock measure of RunFor — harness only, not used inside core.
    /// Returns (elapsedMs, masterCycles).
    /// </summary>
    public static (double ms, ulong cycles) MeasureRunFor(ulong cycles)
    {
        var sys = new Ps2System();
        var sw = Stopwatch.StartNew();
        sys.RunFor(cycles);
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, sys.MasterCycles);
    }
}
