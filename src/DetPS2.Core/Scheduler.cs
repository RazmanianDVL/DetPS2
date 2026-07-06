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
/// Step return value: Currently ignored by the scheduler. It is reserved for future
/// back-pressure / variable cycle consumption tracking once more components report
/// accurate cycle counts.
/// </summary>
public sealed class Scheduler
{
    private readonly List<ISchedulable> _components = new();
    private ulong _masterCycles;

    public ulong MasterCycles => _masterCycles;

    public ulong SliceSize { get; set; } = 64;

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

        ulong target = _masterCycles + cyclesToRun;

        while (_masterCycles < target)
        {
            ulong remaining = target - _masterCycles;
            ulong thisSlice = Math.Min(remaining, SliceSize);

            foreach (var component in _components)
            {
                // Note: return value is currently ignored (see class docs)
                component.Step(thisSlice);
            }

            _masterCycles += thisSlice;
        }
    }

    public void Reset()
    {
        _masterCycles = 0;
        foreach (var component in _components)
            component.Reset();
    }
}

public interface ISchedulable
{
    int Step(ulong maxCycles);
    void Reset();
}