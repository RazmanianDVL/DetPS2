using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Foundational deterministic scheduler for the PS2 system.
/// This class is responsible for driving all components in a cycle-aware, deterministic order.
/// 
/// Design goals:
/// - Clear separation between components and timing logic
/// - Easy to extend with cycle-accurate stepping later
/// - Supports multiple clock domains (EE vs IOP)
/// - Foundation for future interrupt and timing-sensitive work
/// </summary>
public sealed class Scheduler
{
    private readonly List<ISchedulable> _components = new();
    private ulong _masterCycles;

    /// <summary>
    /// Current master cycle count (shared timeline).
    /// </summary>
    public ulong MasterCycles => _masterCycles;

    /// <summary>
    /// Register a component that can be stepped by the scheduler.
    /// </summary>
    public void Register(ISchedulable component)
    {
        if (component == null) throw new ArgumentNullException(nameof(component));
        if (!_components.Contains(component))
            _components.Add(component);
    }

    /// <summary>
    /// Remove a component from scheduling.
    /// </summary>
    public void Unregister(ISchedulable component)
    {
        _components.Remove(component);
    }

    /// <summary>
    /// Advance the system by a number of master cycles.
    /// This is the main entry point for running the emulator.
    /// </summary>
    public void RunFor(ulong cycles)
    {
        ulong target = _masterCycles + cycles;

        while (_masterCycles < target)
        {
            // Step all registered components
            // In the future this can become more sophisticated (cycle-accurate per component, etc.)
            foreach (var component in _components)
            {
                int cyclesTaken = component.Step();
                // For now we use a simple model. Future versions can track per-component cycles.
            }

            _masterCycles++;
        }
    }

    public void Reset()
    {
        _masterCycles = 0;
        foreach (var component in _components)
            component.Reset();
    }
}

/// <summary>
/// Interface for any component that can be driven by the Scheduler.
/// </summary>
public interface ISchedulable
{
    int Step();
    void Reset();
}
