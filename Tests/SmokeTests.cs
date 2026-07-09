using DetPS2.Core;

namespace DetPS2.Tests;

/// <summary>
/// Smoke / determinism tests for Phase 6.2.
/// Includes SIF interrupt and work-cost / dynamic scheduling tests.
/// </summary>
public static class SmokeTests
{
    public static void Determinism_MasterCycles()
    {
        const ulong cycles = 100_000;
        var sys1 = new Ps2System(); sys1.RunFor(cycles);
        var sys2 = new Ps2System(); sys2.RunFor(cycles);
        if (sys1.MasterCycles != sys2.MasterCycles) throw new Exception("Determinism violation");
        Console.WriteLine("[Smoke] Determinism_MasterCycles OK");
    }

    public static void SaveState_MasterCyclesRoundTrip()
    {
        var sys = new Ps2System(); sys.RunFor(50_000);
        ulong before = sys.MasterCycles;
        byte[] state = sys.SaveState();
        var sys2 = new Ps2System(); sys2.LoadState(state);
        if (before != sys2.MasterCycles) throw new Exception("SaveState mismatch");
        Console.WriteLine("[Smoke] SaveState_MasterCyclesRoundTrip OK");
    }

    public static void Reset_MasterCycles()
    {
        var sys = new Ps2System(); sys.RunFor(12345); sys.Reset();
        if (sys.MasterCycles != 0) throw new Exception("Reset failed");
        Console.WriteLine("[Smoke] Reset_MasterCycles OK");
    }

    public static void MultipleShortRuns()
    {
        var sys1 = new Ps2System(); for (int i = 0; i < 10; i++) sys1.RunFor(1000);
        var sys2 = new Ps2System(); for (int i = 0; i < 10; i++) sys2.RunFor(1000);
        if (sys1.MasterCycles != sys2.MasterCycles) throw new Exception("Multiple short runs violation");
        Console.WriteLine("[Smoke] MultipleShortRuns OK");
    }

    public static void Sif_InterruptRaisedOnSendCommand()
    {
        var sys = new Ps2System();
        bool before = sys.Intc.IsPending(Intc.InterruptSource.Sif);
        sys.Sif.SendCommand(0x12345678);
        bool after = sys.Intc.IsPending(Intc.InterruptSource.Sif);
        if (!after) throw new Exception("SIF interrupt was not raised");
        Console.WriteLine("[Smoke] Sif_InterruptRaisedOnSendCommand OK");
    }

    /// <summary>
    /// Verifies that the work-cost reporting system works correctly when enabled vs disabled.
    /// </summary>
    public static void Scheduler_WorkCostReporting()
    {
        var sys = new Ps2System();

        sys.Scheduler.UseReportedWorkCost = false;
        sys.RunFor(5000);
        if (sys.Scheduler.LastReportedWork != 0)
            throw new Exception("LastReportedWork should be 0 when disabled");

        sys.Scheduler.UseReportedWorkCost = true;
        sys.RunFor(5000);

        if (sys.Scheduler.LastReportedWork <= 0)
            throw new Exception("LastReportedWork should increase when enabled");

        Console.WriteLine($"[Smoke] Scheduler_WorkCostReporting OK (LastReportedWork = {sys.Scheduler.LastReportedWork})");
    }

    /// <summary>
    /// Verifies that LastReportedWork resets properly when Scheduler.Reset() is called.
    /// </summary>
    public static void Scheduler_WorkCostResetsOnReset()
    {
        var sys = new Ps2System();
        sys.Scheduler.UseReportedWorkCost = true;

        sys.RunFor(5000);
        if (sys.Scheduler.LastReportedWork <= 0)
            throw new Exception("Expected work to be reported before reset");

        sys.Reset();

        if (sys.Scheduler.LastReportedWork != 0)
            throw new Exception("LastReportedWork should be 0 after Reset()");

        Console.WriteLine("[Smoke] Scheduler_WorkCostResetsOnReset OK");
    }

    /// <summary>
    /// Verifies that LastReportedWork accumulates across multiple RunFor calls.
    /// </summary>
    public static void Scheduler_WorkCostAccumulates()
    {
        var sys = new Ps2System();
        sys.Scheduler.UseReportedWorkCost = true;

        sys.RunFor(2000);
        int first = sys.Scheduler.LastReportedWork;

        sys.RunFor(2000);
        int second = sys.Scheduler.LastReportedWork;

        if (second <= first)
            throw new Exception("LastReportedWork should accumulate across multiple RunFor calls");

        Console.WriteLine($"[Smoke] Scheduler_WorkCostAccumulates OK (first={first}, second={second})");
    }

    /// <summary>
    /// Basic smoke test for dynamic scheduling behavior (work-cost feedback path is exercised).
    /// </summary>
    public static void Scheduler_DynamicSchedulingSmoke()
    {
        var sys = new Ps2System();

        sys.Scheduler.UseReportedWorkCost = false;
        sys.RunFor(10000);
        ulong cyclesDisabled = sys.MasterCycles;

        var sys2 = new Ps2System();
        sys2.Scheduler.UseReportedWorkCost = true;
        sys2.RunFor(10000);
        ulong cyclesEnabled = sys2.MasterCycles;

        if (cyclesDisabled == 0 || cyclesEnabled == 0)
            throw new Exception("MasterCycles did not advance");

        Console.WriteLine($"[Smoke] Scheduler_DynamicSchedulingSmoke OK (disabled={cyclesDisabled}, enabled={cyclesEnabled})");
    }
}
