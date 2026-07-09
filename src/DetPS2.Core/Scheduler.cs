using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Deterministic PS2 scheduler with optional work-cost aware adaptive slicing.
/// 
/// === How components participate (very easy) ===
/// 1. Implement ISchedulable (most already do).
/// 2. In your Step(ulong maxCycles) method, return a positive int representing work done.
/// 3. High return value = Scheduler will give you larger slices next time.
/// 4. Low return value = Scheduler will give you smaller slices.
/// 
/// Enable with: scheduler.UseReportedWorkCost = true;
/// Then inspect scheduler.LastReportedWork and scheduler.LastWorkEfficiency after RunFor().
/// 
/// The adaptive behavior happens automatically during RunFor() when the flag is enabled.
/// </summary>
public sealed class Scheduler
{
    private readonly List<ISchedulable> _components = new();
    private ulong _masterCycles;

    public ulong MasterCycles => _masterCycles;

    /// <summary>
/// Base slice size used when starting a RunFor() call.
/// The adaptive system may temporarily grow or shrink from this value.
/// </summary>
    public ulong SliceSize { get; set; } = 64;

    /// <summary>
    /// When true, the Scheduler uses the int returned from every component's Step() call
    /// to dynamically adjust future slice sizes during RunFor().
    /// High reported work → larger slices. Low reported work → smaller slices.
    /// Default is false for full backward compatibility and maximum determinism.
    /// </summary>
    public bool UseReportedWorkCost { get; set; } = false;

    /// <summary>
    /// Total work reported by all components during the most recent RunFor() call.
    /// Only updated when UseReportedWorkCost is true.
    /// </summary>
    public int LastReportedWork { get; private set; }

    /// <summary>
    /// Efficiency of the last run as a percentage (reported work / requested cycles * 100).
    /// Useful diagnostic when UseReportedWorkCost is enabled.
    /// </summary>
    public double LastWorkEfficiency { get; private set; }

    public void Register(ISchedulable component)
    {
        if (component == null) throw new ArgumentNullException(nameof(component));
        if (!_components.Contains(component))
            _components.Add(component);
    }

    public void Unregister(ISchedulable component)
    {
        _components.Remove(component);
    }

    public void RunFor(ulong cyclesToRun)
    {
        if (cyclesToRun == 0) return;

        LastReportedWork = 0;
        ulong target = _masterCycles + cyclesToRun;
        ulong currentSlice = SliceSize;

        while (_masterCycles < target)
        {
            ulong remaining = target - _masterCycles;
            ulong thisSlice = Math.Min(remaining, currentSlice);

            int sliceReportedWork = 0;

            foreach (var component in _components)
            {
                int cyclesAdvanced = component.Step(thisSlice);

                if (UseReportedWorkCost && cyclesAdvanced > 0)
                {
                    sliceReportedWork += cyclesAdvanced;
                    LastReportedWork += cyclesAdvanced;
                }
            }

            _masterCycles += thisSlice;

            if (UseReportedWorkCost)
            {
                // Bidirectional adaptive slicing based on reported load
                if (sliceReportedWork >= thisSlice * 3 / 4)
                {
                    // High load → grow slice (reward productive components)
                    currentSlice = Math.Min(SliceSize * 2, thisSlice * 3 / 2);
                }
                else if (sliceReportedWork < thisSlice / 2 && thisSlice > 4)
                {
                    // Low load → shrink slice
                    currentSlice = Math.Max(4UL, thisSlice / 2);
                }
                else
                {
                    currentSlice = SliceSize;
                }
            }
        }

        if (UseReportedWorkCost && cyclesToRun > 0)
        {
            LastWorkEfficiency = (LastReportedWork / (double)cyclesToRun) * 100.0;
        }
        else
        {
            LastWorkEfficiency = 0;
        }
    }

    public void Reset()
    {
        _masterCycles = 0;
        LastReportedWork = 0;
        LastWorkEfficiency = 0;
        foreach (var component in _components)
            component.Reset();
    }
}

public interface ISchedulable
{
    /// <summary>
    /// Perform up to maxCycles of work.
    /// 
    /// Return value directly influences future slice allocation when the Scheduler's
    /// UseReportedWorkCost flag is enabled:
    ///   - High positive value → Scheduler tends to give this component larger slices going forward.
    ///   - Low / zero value → Scheduler tends to give this component smaller slices.
    /// 
    /// This is the primary (and very simple) way for EE, DMAC, GIF, GS, VIF, VU, etc.
    /// to participate in smarter scheduling.
    /// </summary>
    int Step(ulong maxCycles);

    void Reset();
}