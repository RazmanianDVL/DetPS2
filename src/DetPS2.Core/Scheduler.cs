using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Deterministic scheduler for the PS2 system.
/// 
/// Budgeted slice execution with optional work-cost feedback from components.
/// 
/// Core rules (never broken):
/// - MasterCycles is the single source of truth.
/// - All advancement is deterministic and repeatable.
/// - UseReportedWorkCost is completely optional. Default (false) = identical behavior to before.
/// 
/// Work-cost integration:
/// - Components implement int Step(ulong maxCycles) and return meaningful work done.
/// - When UseReportedWorkCost = true, Scheduler accumulates the returned values.
/// - LastReportedWork and WorkEfficiency provide diagnostics.
/// - George’s Gif.Step() implementation (which calls Gs.CalculateWorkCost) now participates automatically.
/// </summary>
public sealed class Scheduler
{
    private readonly List<ISchedulable> _components = new();
    private ulong _masterCycles;
    private ulong _lastRunRequestedCycles;

    public ulong MasterCycles => _masterCycles;

    public ulong SliceSize { get; set; } = 64;

    /// <summary>
    /// Enables accumulation of work cost reported by components via their Step() return value.
    /// When false (default), behavior is identical to previous versions — fully deterministic with no side effects.
    /// Enable for diagnostics, profiling, or future adaptive scheduling experiments.
    /// </summary>
    public bool UseReportedWorkCost { get; set; } = false;

    /// <summary>
    /// Total work reported by all components during the last RunFor() call.
    /// Only updated when UseReportedWorkCost is true.
    /// </summary>
    public int LastReportedWork { get; private set; }

    /// <summary>
    /// Simple efficiency metric: (reported work / requested cycles) * 100.
    /// 100 = components reported exactly as much work as cycles advanced.
    /// Lower values indicate components were not fully utilizing the slice or chose to report conservatively.
    /// Only meaningful when UseReportedWorkCost is true.
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

        _lastRunRequestedCycles = cyclesToRun;
        LastReportedWork = 0;

        ulong target = _masterCycles + cyclesToRun;

        while (_masterCycles < target)
        {
            ulong remaining = target - _masterCycles;
            ulong thisSlice = Math.Min(remaining, SliceSize);

            foreach (var component in _components)
            {
                int cyclesAdvanced = component.Step(thisSlice);

                if (UseReportedWorkCost && cyclesAdvanced > 0)
                {
                    LastReportedWork += cyclesAdvanced;
                }
            }

            _masterCycles += thisSlice;
        }

        if (UseReportedWorkCost && _lastRunRequestedCycles > 0)
        {
            LastWorkEfficiency = (LastReportedWork / (double)_lastRunRequestedCycles) * 100.0;
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
    /// 
    /// Return value rules (adoption guide for components):
    /// - Return a positive integer representing approximate cycles or work units completed.
    /// - Return 0 if idle or choosing not to report this slice.
    /// - Never return a value &gt; maxCycles.
    /// 
    /// Example (from George’s Gif implementation):
    ///   return Math.Min(costFromGsCalculateWorkCost, (int)maxCycles);
    /// 
    /// When Scheduler.UseReportedWorkCost is true, these values are accumulated
    /// into LastReportedWork and used to calculate LastWorkEfficiency.
    /// 
    /// This is the primary lightweight mechanism for components (EE, DMAC, GIF, GS, VIF, etc.)
    /// to provide timing feedback without changing the ISchedulable interface.
    /// </summary>
    int Step(ulong maxCycles);

    void Reset();
}