using DetPS2.Core;

namespace DetPS2.Tests;

/// <summary>
/// Smoke / determinism tests for Phase 6.2.
/// Includes SIF interrupt and work-cost reporting tests.
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
    /// Verifies that the new work-cost reporting mechanism in Scheduler works when enabled.
    /// </summary>
    public static void Scheduler_WorkCostReporting()
    {
        var sys = new Ps2System();
        sys.Scheduler.UseReportedWorkCost = true;

        sys.RunFor(10_000);

        if (sys.Scheduler.LastReportedWork < 0)
            throw new Exception("LastReportedWork should not be negative");

        Console.WriteLine($"[Smoke] Scheduler_WorkCostReporting OK (LastReportedWork = {sys.Scheduler.LastReportedWork})");
    }
}
