using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Deterministic scheduler (Phase 10).
/// Modes:
/// - FixedSlice: classic round-robin fixed (or work-cost adaptive) slices.
/// - EventQueue: advance to next scheduled event boundary while still meeting exact MasterCycles budget.
/// </summary>
public sealed class Scheduler
{
    public enum Mode
    {
        FixedSlice = 0,
        EventQueue = 1
    }

    private readonly List<ISchedulable> _components = new();
    private readonly List<ScheduledEvent> _events = new();
    private ulong _masterCycles;
    private ulong _currentEffectiveSliceSize;
    private double _lastSliceUtilization;
    private ulong _eventSerial;

    public ulong MasterCycles => _masterCycles;
    public Mode SchedulingMode { get; set; } = Mode.FixedSlice;
    public ulong SliceSize { get; set; } = 64;
    public bool UseReportedWorkCost { get; set; } = false;
    public int LastReportedWork { get; private set; }
    public double LastWorkEfficiency { get; private set; }
    public ulong CurrentEffectiveSliceSize => _currentEffectiveSliceSize;
    public double LastSliceUtilization => _lastSliceUtilization;
    public double HighUtilizationThreshold { get; set; } = 0.75;
    public double LowUtilizationThreshold { get; set; } = 0.5;
    public int EventQueueDepth => _events.Count;
    public ulong EventsFired { get; private set; }

    /// <summary>Optional bus contention (scales EE-like first component budget).</summary>
    public BusContention? Bus { get; set; }

    /// <summary>When set, first registered component gets scaled budget (typically EE).</summary>
    public ISchedulable? BudgetScaledComponent { get; set; }

    private struct ScheduledEvent
    {
        public ulong DueCycle;
        public ulong Serial;
        public Action? Callback;
        public string Name;
    }

    public void Register(ISchedulable component)
    {
        if (component == null) throw new ArgumentNullException(nameof(component));
        if (!_components.Contains(component))
            _components.Add(component);
    }

    public void Unregister(ISchedulable component) => _components.Remove(component);

    /// <summary>Schedule a deterministic callback at absolute master cycle (or relative if absolute=false).</summary>
    public void ScheduleEvent(ulong cycles, Action callback, string name = "", bool absolute = false)
    {
        ulong due = absolute ? cycles : _masterCycles + cycles;
        _events.Add(new ScheduledEvent
        {
            DueCycle = due,
            Serial = ++_eventSerial,
            Callback = callback,
            Name = name ?? ""
        });
        // Keep sorted by due, then serial (stable)
        _events.Sort(static (a, b) =>
        {
            int c = a.DueCycle.CompareTo(b.DueCycle);
            return c != 0 ? c : a.Serial.CompareTo(b.Serial);
        });
    }

    public void ClearEvents() => _events.Clear();

    public void RunFor(ulong cyclesToRun)
    {
        if (cyclesToRun == 0) return;

        if (SchedulingMode == Mode.EventQueue)
            RunForEventQueue(cyclesToRun);
        else
            RunForFixedSlice(cyclesToRun);
    }

    private void RunForFixedSlice(ulong cyclesToRun)
    {
        LastReportedWork = 0;
        _currentEffectiveSliceSize = SliceSize;
        _lastSliceUtilization = 0;
        ulong target = _masterCycles + cyclesToRun;

        while (_masterCycles < target)
        {
            FireDueEvents(_masterCycles);

            ulong remaining = target - _masterCycles;
            ulong thisSlice = Math.Min(remaining, _currentEffectiveSliceSize);
            // Clamp slice to next event so FixedSlice still respects event times when any exist
            if (_events.Count > 0 && _events[0].DueCycle > _masterCycles)
            {
                ulong untilEvent = _events[0].DueCycle - _masterCycles;
                if (untilEvent < thisSlice)
                    thisSlice = Math.Max(1UL, untilEvent);
            }

            StepComponents(thisSlice);
            _masterCycles += thisSlice;
            FireDueEvents(_masterCycles);
        }

        FinalizeWorkStats(cyclesToRun);
    }

    private void RunForEventQueue(ulong cyclesToRun)
    {
        LastReportedWork = 0;
        _currentEffectiveSliceSize = SliceSize;
        _lastSliceUtilization = 0;
        ulong target = _masterCycles + cyclesToRun;

        while (_masterCycles < target)
        {
            FireDueEvents(_masterCycles);

            ulong remaining = target - _masterCycles;
            ulong thisSlice;

            if (_events.Count > 0 && _events[0].DueCycle <= target)
            {
                // Advance exactly to next event (or remaining if sooner)
                ulong next = _events[0].DueCycle;
                if (next <= _masterCycles)
                {
                    FireDueEvents(_masterCycles + 1); // progress if event stuck at now
                    thisSlice = Math.Min(remaining, Math.Max(1UL, SliceSize));
                }
                else
                {
                    thisSlice = Math.Min(remaining, next - _masterCycles);
                }
            }
            else
            {
                // No events before target — use slice size (optionally work-cost adaptive)
                thisSlice = Math.Min(remaining, _currentEffectiveSliceSize);
            }

            if (thisSlice == 0) thisSlice = 1;
            StepComponents(thisSlice);
            _masterCycles += thisSlice;
            FireDueEvents(_masterCycles);

            if (UseReportedWorkCost)
                AdjustSliceFromWork(_lastSliceUtilization, thisSlice);
        }

        FinalizeWorkStats(cyclesToRun);
    }

    private void StepComponents(ulong thisSlice)
    {
        int sliceReportedWork = 0;
        for (int i = 0; i < _components.Count; i++)
        {
            var component = _components[i];
            ulong budget = thisSlice;
            if (Bus != null && BudgetScaledComponent != null && ReferenceEquals(component, BudgetScaledComponent))
                budget = Bus.ScaleEeBudget(thisSlice);

            int cyclesAdvanced = component.Step(budget);
            if (UseReportedWorkCost && cyclesAdvanced > 0)
            {
                sliceReportedWork += cyclesAdvanced;
                LastReportedWork += cyclesAdvanced;
            }
        }

        if (UseReportedWorkCost)
        {
            _lastSliceUtilization = thisSlice > 0 ? (double)sliceReportedWork / thisSlice : 0.0;
            if (SchedulingMode == Mode.FixedSlice)
                AdjustSliceFromWork(_lastSliceUtilization, thisSlice);
        }
    }

    private void AdjustSliceFromWork(double util, ulong thisSlice)
    {
        if (util >= HighUtilizationThreshold)
            _currentEffectiveSliceSize = Math.Min(SliceSize * 2, (ulong)(thisSlice * 1.5));
        else if (util < LowUtilizationThreshold && thisSlice > 4)
            _currentEffectiveSliceSize = Math.Max(4UL, thisSlice / 2);
        else
            _currentEffectiveSliceSize = SliceSize;
    }

    private void FireDueEvents(ulong now)
    {
        while (_events.Count > 0 && _events[0].DueCycle <= now)
        {
            var ev = _events[0];
            _events.RemoveAt(0);
            EventsFired++;
            ev.Callback?.Invoke();
        }
    }

    private void FinalizeWorkStats(ulong cyclesToRun)
    {
        if (UseReportedWorkCost && cyclesToRun > 0)
            LastWorkEfficiency = (LastReportedWork / (double)cyclesToRun) * 100.0;
        else
            LastWorkEfficiency = 0;
    }

    public void Reset()
    {
        _masterCycles = 0;
        LastReportedWork = 0;
        LastWorkEfficiency = 0;
        _currentEffectiveSliceSize = SliceSize;
        _lastSliceUtilization = 0;
        EventsFired = 0;
        _eventSerial = 0;
        _events.Clear();
        Bus?.Reset();
        foreach (var component in _components)
            component.Reset();
    }

    public void SetMasterCycles(ulong cycles) => _masterCycles = cycles;
}

public interface ISchedulable
{
    int Step(ulong maxCycles);
    void Reset();
}
