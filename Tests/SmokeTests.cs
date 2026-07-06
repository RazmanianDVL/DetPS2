using DetPS2.Core;

namespace DetPS2.Tests;

/// <summary>
/// Basic smoke / determinism tests for Phase 6.2.
/// </summary>
public static class SmokeTests
{
    public static void Determinism_MasterCycles()
    {
        const ulong cycles = 100_000;
        var sys1 = new Ps2System(); sys1.RunFor(cycles);
        var sys2 = new Ps2System(); sys2.RunFor(cycles);
        if (sys1.MasterCycles != sys2.MasterCycles)
            throw new Exception("Determinism violation on MasterCycles");
        Console.WriteLine("[Smoke] Determinism_MasterCycles OK");
    }

    public static void SaveState_MasterCyclesRoundTrip()
    {
        var sys = new Ps2System(); sys.RunFor(50_000);
        ulong before = sys.MasterCycles;
        byte[] state = sys.SaveState();
        var sys2 = new Ps2System(); sys2.LoadState(state);
        if (before != sys2.MasterCycles)
            throw new Exception("SaveState MasterCycles mismatch");
        Console.WriteLine("[Smoke] SaveState_MasterCyclesRoundTrip OK");
    }

    public static void Reset_MasterCycles()
    {
        var sys = new Ps2System(); sys.RunFor(12345); sys.Reset();
        if (sys.MasterCycles != 0) throw new Exception("Reset failed to clear MasterCycles");
        Console.WriteLine("[Smoke] Reset_MasterCycles OK");
    }

    /// <summary>
    /// Additional test: Multiple short runs should be deterministic.
    /// </summary>
    public static void MultipleShortRuns()
    {
        var sys1 = new Ps2System();
        for (int i = 0; i < 10; i++) sys1.RunFor(1000);

        var sys2 = new Ps2System();
        for (int i = 0; i < 10; i++) sys2.RunFor(1000);

        if (sys1.MasterCycles != sys2.MasterCycles)
            throw new Exception("Multiple short runs determinism violation");
        Console.WriteLine("[Smoke] MultipleShortRuns OK");
    }
}
