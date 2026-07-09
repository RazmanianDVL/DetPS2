using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Deterministic scheduler for the PS2 system.
/// 
/// v2 design: Budgeted slicing
/// - Components receive a cycle budget instead of being stepped 1 cycle at a time.
/// - Dramatically reduces overhead while staying fully deterministic.
/// - Master cycle counter remains the single source of truth.
/// - Tunable slice size for performance vs granularity.
/// 
/// Execution order: Components are stepped in the order they were registered via Register().
/// This order is stable and deterministic.
/// 
/// Work-cost / timing feedback (formalized in this change):
/// - ISchedulable.Step(ulong) returns int.
/// - The returned value is the component's self-reported work/cycles consumed.
/// - By default (UseReportedWorkCost = false) the value is captured but ignored for advancement (Phase 6.1 safety).
/// - When UseReportedWorkCost = true, the Scheduler accumulates reported work for diagnostics and future back-pressure logic.
/// - This mechanism is fully optional and non-breaking.
/// </summary>
public sealed class Scheduler
{
    private readonly List<ISchedulable> _components = new();
    private ulong _masterCycles;

    public ulong MasterCycles => _masterCycles;

    public ulong SliceSize { get; set; } = 64;

    /// <summary>
    /// When true, the Scheduler will accumulate the int returned from each component's Step() call.
    /// Default is false to preserve exact current behavior and determinism guarantees.
    /// Enable this for diagnostics or when components start providing accurate work-cost numbers.
    /// </summary>
    public bool UseReportedWorkCost { get; set; } = false;

    /// <summary>
    /// Sum of all non-negative values returned by components during the most recent RunFor() call.
    /// Only meaningful when UseReportedWorkCost is true.
    /// </summary>
    public int LastReportedWork { get; private set; }

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
    }

    public void Reset()
    {
        _masterCycles = 0;
        LastReportedWork = 0;
        foreach (var component in _components)
            component.Reset();
    }
}

public interface ISchedulable
{
    /// <summary>
    /// Advances the component by up to maxCycles.
    /// 
    /// Return value contract (formalized):
    ///   &gt; 0  = Approximate cycles or work units actually consumed by this component in the slice.
    ///   == 0  = No work performed or component chose not to report.
    ///   &lt; 0  = Reserved, do not use.
    /// 
    /// Components should return a value between 0 and maxCycles inclusive.
    /// The Scheduler may use this value when UseReportedWorkCost is enabled.
    /// Returning values outside this range is undefined.
    /// </summary>
    int Step(ulong maxCycles);

    void Reset();
}