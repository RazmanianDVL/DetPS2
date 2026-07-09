using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Work-cost aware deterministic scheduler.
/// 
/// Reported work from components directly influences live slice allocation during RunFor():
/// - High utilization → grows slice size
/// - Low utilization → shrinks slice size
/// 
/// Tunable via HighUtilizationThreshold / LowUtilizationThreshold.
/// Observe via CurrentEffectiveSliceSize and LastSliceUtilization.
/// </summary>
public sealed class Scheduler
{
    private readonly List<ISchedulable> _components = new();
    private ulong _masterCycles;
    private ulong _currentEffectiveSliceSize;
    private double _lastSliceUtilization;

    public ulong MasterCycles => _masterCycles;

    public ulong SliceSize { get; set; } = 64;

    public bool UseReportedWorkCost { get; set; } = false;

    public int LastReportedWork { get; private set; }
    public double LastWorkEfficiency { get; private set; }
    public ulong CurrentEffectiveSliceSize => _currentEffectiveSliceSize;
    public double LastSliceUtilization => _lastSliceUtilization;

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
        _lastSliceUtilization = 0;

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
                _lastSliceUtilization = thisSlice > 0 ? (double)sliceReportedWork / thisSlice : 0.0;

                if (_lastSliceUtilization >= HighUtilizationThreshold)
                {
                    _currentEffectiveSliceSize = Math.Min(SliceSize * 2, (ulong)(thisSlice * 1.5));
                }
                else if (_lastSliceUtilization < LowUtilizationThreshold && thisSlice > 4)
                {
                    _currentEffectiveSliceSize = Math.Max(4UL, thisSlice / 2);
                }
                else
                {
                    _currentEffectiveSliceSize = SliceSize;
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
        _currentEffectiveSliceSize = SliceSize;
        _lastSliceUtilization = 0;
        foreach (var component in _components)
            component.Reset();
    }
}

public interface ISchedulable
{
    /// <summary>
    /// Return work done this slice. This directly influences future slice sizes
    /// when UseReportedWorkCost is enabled.
    /// </summary>
    int Step(ulong maxCycles);

    void Reset();
}