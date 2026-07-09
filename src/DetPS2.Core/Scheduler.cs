using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Deterministic scheduler with optional work-cost aware adaptive slicing.
/// 
/// When UseReportedWorkCost is false (default):
///   - Behavior is identical to all previous versions.
///   - Fully deterministic, fixed slices.
/// 
/// When UseReportedWorkCost is true:
///   - Components report work via int returned from Step(ulong).
///   - If a slice's total reported work is significantly lower than the budget given,
///     the Scheduler reduces the size of the *next* slice (light adaptive behavior).
///   - This makes reported work actually change how cycles are allocated.
///   - Still 100% deterministic because decisions are based only on component reports.
/// </summary>
public sealed class Scheduler
{
    private readonly List<ISchedulable> _components = new();
    private ulong _masterCycles;

    public ulong MasterCycles => _masterCycles;

    public ulong SliceSize { get; set; } = 64;

    public bool UseReportedWorkCost { get; set; } = false;

    public int LastReportedWork { get; private set; }
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

            // === Light adaptive slicing (only when feedback is enabled) ===
            if (UseReportedWorkCost)
            {
                // If components reported significantly less work than the budget we gave them,
                // shrink the next slice so we don't over-allocate to underutilized components.
                if (sliceReportedWork < thisSlice / 2 && thisSlice > 4)
                {
                    currentSlice = Math.Max(4UL, thisSlice / 2);
                }
                else
                {
                    currentSlice = SliceSize; // reset to normal
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
    /// Execute up to maxCycles of work.
    /// Return a positive value representing actual work/cycles completed.
    /// Return 0 if idle.
    /// 
    /// When Scheduler.UseReportedWorkCost is enabled, low reported work
    /// will cause the Scheduler to automatically reduce future slice sizes.
    /// This is how your return value now directly affects cycle allocation.
    /// </summary>
    int Step(ulong maxCycles);

    void Reset();
}</summary>