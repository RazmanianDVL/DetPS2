using System;

namespace DetPS2.Core;

/// <summary>
/// Timer - Foundational implementation for PS2 timers.
/// 
/// The PS2 has 4 timers (Timer 0-3) used for various timing and interrupt purposes.
/// This class provides a clean foundation for one timer instance.
/// 
/// Design goals:
/// - Simple, deterministic ticking
/// - Clear mode and compare value handling
/// - Easy to connect to Intc for interrupt generation
/// - Foundation for future accurate timer behavior (prescale, gate, etc.)
/// </summary>
public sealed class Timer
{
    public uint Count { get; private set; }
    public uint Compare { get; private set; }
    public uint Mode { get; private set; }

    private readonly Intc _intc;
    private readonly Intc.InterruptSource _interruptSource;

    public Timer(Intc intc, Intc.InterruptSource interruptSource)
    {
        _intc = intc ?? throw new ArgumentNullException(nameof(intc));
        _interruptSource = interruptSource;
        Reset();
    }

    public void Reset()
    {
        Count = 0;
        Compare = 0;
        Mode = 0;
    }

    /// <summary>
    /// Tick the timer by a number of cycles.
    /// In the future this can respect prescaler and gate settings from Mode.
    /// </summary>
    public void Tick(ulong cycles)
    {
        // Simple implementation for foundation
        Count += (uint)cycles;

        if (Compare != 0 && Count >= Compare)
        {
            _intc.Raise(_interruptSource);
            Count = 0; // Reset on compare match (common behavior)
        }
    }

    public void WriteMode(uint value)
    {
        Mode = value;
        // Future: parse mode bits for prescaler, gate, etc.
    }

    public void WriteCompare(uint value)
    {
        Compare = value;
    }
}
