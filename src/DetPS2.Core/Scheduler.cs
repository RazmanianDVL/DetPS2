using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Deterministic PS2 scheduler with optional work-cost aware adaptive slicing.
/// 
/// === Quick Start for other components ===
/// 1. Implement ISchedulable
/// 2. Return meaningful positive work from Step(ulong)
/// 3. Set scheduler.UseReportedWorkCost = true
/// 4. After RunFor(), check LastReportedWork, LastWorkEfficiency, and CurrentEffectiveSliceSize
/// 
/// High work reports → Scheduler grows slices. Low reports → Scheduler shrinks slices.
/// The effect happens live during RunFor().
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

    /// <summary>
    /// The slice size the adaptive system is currently using.
    /// Useful for debugging and observing how reported work is affecting allocation in real time.
    /// </summary>
    public ulong CurrentEffectiveSliceSize => _currentEffectiveSliceSize;

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
                if (sliceReportedWork >= thisSlice * 3 / 4)
                {
                    _currentEffectiveSliceSize = Math.Min(SliceSize * 2, thisSlice * 3 / 2);
                }
                else if (sliceReportedWork < thisSlice / 2 && thisSlice > 4)
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
        foreach (var component in _components)
            component.Reset();
    }
}

public interface ISchedulable
{
    /// <summary>
    /// Return positive work done. This value now directly influences how large
    /// your future slices will be when UseReportedWorkCost is enabled on the Scheduler.
    /// </summary>
    int Step(ulong maxCycles);

    void Reset();
}