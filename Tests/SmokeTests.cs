using DetPS2.Core;

namespace DetPS2.Tests;

/// <summary>
/// Basic smoke / determinism tests for Phase 6.2.
/// </summary>
public static class SmokeTests
{
    /// <summary>
    /// Verifies that two identical runs produce the same MasterCycles.
    /// </summary>
    public static void Determinism_MasterCycles()
    {
        const ulong cycles = 100_000;

        var sys1 = new Ps2System();
        sys1.RunFor(cycles);
        ulong r1 = sys1.MasterCycles;

        var sys2 = new Ps2System();
        sys2.RunFor(cycles);
        ulong r2 = sys2.MasterCycles;

        if (r1 != r2)
            throw new Exception($"Determinism violation: {r1} != {r2}");

        Console.WriteLine($"[Smoke] Determinism_MasterCycles OK ({r1})");
    }

    /// <summary>
    /// Verifies that SaveState + LoadState round-trip preserves MasterCycles.
    /// </summary>
    public static void SaveState_MasterCyclesRoundTrip()
    {
        var sys = new Ps2System();
        sys.RunFor(50_000);
        ulong before = sys.MasterCycles;

        byte[] state = sys.SaveState();

        var sys2 = new Ps2System();
        sys2.LoadState(state);
        ulong after = sys2.MasterCycles;

        if (before != after)
            throw new Exception($"SaveState MasterCycles mismatch: {before} != {after}");

        Console.WriteLine($"[Smoke] SaveState_MasterCyclesRoundTrip OK ({after})");
    }

    /// <summary>
    /// Verifies that Reset() brings MasterCycles back to 0.
    /// </summary>
    public static void Reset_MasterCycles()
    {
        var sys = new Ps2System();
        sys.RunFor(12345);

        sys.Reset();

        if (sys.MasterCycles != 0)
            throw new Exception($"Reset did not clear MasterCycles (got {sys.MasterCycles})");

        Console.WriteLine("[Smoke] Reset_MasterCycles OK");
    }
}
