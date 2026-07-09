using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Work-cost aware deterministic scheduler.
/// 
/// The reported int from ISchedulable.Step() now directly influences how cycles are allocated:
/// - High reported work → Scheduler grows the next slice (rewards productive components)
/// - Low reported work → Scheduler shrinks the next slice (avoids wasting cycles on idle components)
/// 
/// This is fully optional via UseReportedWorkCost and remains 100% deterministic.
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

            if (UseReportedWorkCost)
            {
                // Bidirectional adaptive logic
                if (sliceReportedWork >= thisSlice * 3 / 4)
                {
                    // High utilization → grow slice (up to 2x base)
                    currentSlice = Math.Min(SliceSize * 2, thisSlice * 3 / 2);
                }
                else if (sliceReportedWork < thisSlice / 2 && thisSlice > 4)
                {
                    // Low utilization → shrink slice
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
    /// Return work done this slice. High values encourage the Scheduler to give you larger slices next time.
    /// Low values cause the Scheduler to give smaller slices.
    /// This is how your component now influences real scheduling decisions.
    /// </summary>
    int Step(ulong maxCycles);

    void Reset();
}