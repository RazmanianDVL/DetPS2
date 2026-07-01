using System;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system coordinator.
/// 
/// This class owns the master cycle counter and all major components.
/// Everything is driven from here for maximum determinism.
/// </summary>
public sealed class Ps2System
{
    public SystemMemory Memory { get; }
    public EmotionEngine EE { get; }

    /// <summary>
    /// Master cycle counter. This is the single source of truth for time in the entire emulator.
    /// All input events, DMA transfers, interrupts, VBlank, etc. will be scheduled against this.
    /// </summary>
    public ulong MasterCycles { get; private set; }

    public Ps2System()
    {
        Memory = new SystemMemory();
        EE = new EmotionEngine(Memory);
        MasterCycles = 0;
    }

    /// <summary>
    /// Run the emulator for a target number of master cycles.
    /// This is the main entry point for deterministic execution.
    /// </summary>
    public void RunFor(ulong cyclesToRun)
    {
        ulong target = MasterCycles + cyclesToRun;

        while (MasterCycles < target)
        {
            // For now the EE is the only thing that advances time.
            // Later we will interleave IOP, VUs, DMAC, GS, timers, etc. here
            // using proper cycle budgeting or an event queue.
            int cyclesTaken = EE.Step();
            MasterCycles += (ulong)cyclesTaken;
        }
    }

    /// <summary>
    /// Convenience: run until we hit (or pass) a specific master cycle.
    /// </summary>
    public void RunUntil(ulong targetCycle)
    {
        while (MasterCycles < targetCycle)
        {
            int cyclesTaken = EE.Step();
            MasterCycles += (ulong)cyclesTaken;
        }
    }

    public void Reset()
    {
        MasterCycles = 0;
        EE.Reset();
        // TODO: Reset other components when we add them
    }
}
