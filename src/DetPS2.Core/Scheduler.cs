using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Work-cost aware deterministic scheduler.
/// 
/// When UseReportedWorkCost = true, the int returned from Step(ulong) directly affects how cycles are allocated:
/// - High reported work → grows the current slice (up to 2x)
/// - Low reported work → shrinks the current slice (down to 4)
/// 
/// Thresholds are tunable via HighUtilizationThreshold and LowUtilizationThreshold.
/// </summary>
public sealed class Scheduler
{
    private readonly List<ISchedulable> _components = new();
    private ulong _masterCycles;
    private ulong _currentEffectiveSliceSize;

    public ulong MasterCycles => _masterCycles;

    public ulong SliceSize { get; set; } = 64;

    public bool UseReportedWorkCost { get; set; } = false;

    public int LastReportedWork { get; private set; }
    public double LastWorkEfficiency { get; private set; }
    public ulong CurrentEffectiveSliceSize => _currentEffectiveSliceSize;

    public double HighUtilizationThreshold { get; set; } = 0.75;
    public double LowUtilizationThreshold { get; set; } = 0.5;

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
        _currentEffectiveSliceSize = SliceSize;

        ulong target = _masterCycles + cyclesToRun;

        while (_masterCycles < target)
        {
            ulong remaining = target - _masterCycles;
            ulong thisSlice = Math.Min(remaining, _currentEffectiveSliceSize);

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
                AdjustSliceBasedOnWork(thisSlice, sliceReportedWork);
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

    private void AdjustSliceBasedOnWork(ulong thisSlice, int reportedWork)
    {
        double utilization = thisSlice > 0 ? (double)reportedWork / thisSlice : 0.0;

        if (utilization >= HighUtilizationThreshold)
        {
            _currentEffectiveSliceSize = Math.Min(SliceSize * 2, (ulong)(thisSlice * 1.5));
        }
        else if (utilization < LowUtilizationThreshold && thisSlice > 4)
        {
            _currentEffectiveSliceSize = Math.Max(4UL, thisSlice / 2);
        }
        else
        {
            _currentEffectiveSliceSize = SliceSize;
        }
    }

    public void Reset()
    {
        _masterCycles = 0;
        LastReportedWork = 0;
        LastWorkEfficiency = 0;
        _currentEffectiveSliceSize = SliceSize;
        foreach (var component in _components)
            component.Reset();
    }
}

public interface ISchedulable
{
    /// <summary>
    /// Return work done. Higher values cause the Scheduler to allocate larger slices
    /// when UseReportedWorkCost is enabled.
    /// </summary>
    int Step(ulong maxCycles);

    void Reset();
}