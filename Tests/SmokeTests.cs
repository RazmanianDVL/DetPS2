using DetPS2.Core;

namespace DetPS2.Tests;

/// <summary>
/// Basic smoke / determinism tests for Phase 6.2.
/// These tests verify that repeated runs produce identical MasterCycles.
/// </summary>
public static class SmokeTests
{
    public static void RunDeterminismSmokeTest()
    {
        const ulong cyclesToRun = 100_000;

        var system1 = new Ps2System();
        system1.RunFor(cyclesToRun);
        ulong result1 = system1.MasterCycles;

        var system2 = new Ps2System();
        system2.RunFor(cyclesToRun);
        ulong result2 = system2.MasterCycles;

        if (result1 != result2)
        {
            throw new Exception($"Determinism violation! Run1={result1}, Run2={result2}");
        }

        Console.WriteLine($"[SmokeTest] Determinism OK. MasterCycles = {result1} after {cyclesToRun} cycles.");
    }
}
