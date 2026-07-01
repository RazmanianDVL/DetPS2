using System;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system.
/// Now includes DMAC, GIF, and GS for Phase 2 graphics pipeline.
/// </summary>
public sealed class Ps2System
{
    public SystemMemory Memory { get; }
    public EmotionEngine EE { get; }
    public Dmac Dmac { get; }
    public Gif Gif { get; }
    public Gs Gs { get; }

    public ulong MasterCycles { get; private set; }

    public Ps2System()
    {
        Memory = new SystemMemory();
        EE = new EmotionEngine(Memory);

        // Create graphics components
        Dmac = new Dmac(Memory);
        Gs = new Gs(Memory);
        Gif = new Gif(Gs);

        // Wire DMAC to GIF for PATH3
        Dmac.SetGif(Gif);

        MasterCycles = 0;
    }

    public void RunFor(ulong cyclesToRun)
    {
        ulong target = MasterCycles + cyclesToRun;

        while (MasterCycles < target)
        {
            int cyclesTaken = EE.Step();
            MasterCycles += (ulong)cyclesTaken;

            // Step graphics components
            Dmac.Step(1);
            Gif.Step(1);
            Gs.Step(1);
        }
    }

    public void RunUntil(ulong targetCycle)
    {
        while (MasterCycles < targetCycle)
        {
            int cyclesTaken = EE.Step();
            MasterCycles += (ulong)cyclesTaken;

            Dmac.Step(1);
            Gif.Step(1);
            Gs.Step(1);
        }
    }

    public void Reset()
    {
        MasterCycles = 0;
        EE.Reset();
        Dmac.Reset();
        Gif.Reset();
        Gs.Reset();
    }

    /// <summary>
    /// Helper to trigger a test draw through the full pipeline.
    /// </summary>
    public void TriggerTestDraw()
    {
        Console.WriteLine("[Ps2System] Triggering test draw through DMAC -> GIF -> GS...");

        // Simulate DMAC transferring data to GIF channel
        var gifChannel = Dmac.GetType().GetField("_channels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        // For simplicity in early testing, just call GS directly
        Gs.DrawTestPattern();
    }
}
