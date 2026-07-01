using System;

namespace DetPS2.Core;

/// <summary>
/// Interrupt Controller (INTC) - Early Phase 3 skeleton.
/// Provides basic interrupt pending/acknowledge and a couple of timers.
/// This will eventually be used by IOP, EE exceptions, and device emulation.
/// </summary>
public sealed class Intc
{
    public uint Stat { get; private set; }   // Interrupt status
    public uint Mask { get; private set; }   // Interrupt mask

    // Simple timers (very rough for early Phase 3)
    public uint Timer0 { get; private set; }
    public uint Timer1 { get; private set; }

    public Intc()
    {
        Reset();
    }

    public void Reset()
    {
        Stat = 0;
        Mask = 0;
        Timer0 = 0;
        Timer1 = 0;
    }

    public void RaiseInterrupt(uint irq)
    {
        Stat |= (1u << (int)irq);
        Console.WriteLine($"[INTC] Interrupt raised: {irq}");
    }

    public void Acknowledge(uint irq)
    {
        Stat &= ~(1u << (int)irq);
    }

    public bool IsPending(uint irq)
    {
        return (Stat & (1u << (int)irq)) != 0 && (Mask & (1u << (int)irq)) != 0;
    }

    public void Step(ulong cycles)
    {
        // Very basic timer tick (real hardware is more complex)
        Timer0 += (uint)cycles;
        Timer1 += (uint)(cycles / 2);
    }

    public void WriteRegister(uint address, uint value)
    {
        // Placeholder for future register mapping (STAT, MASK, etc.)
    }

    public uint ReadRegister(uint address)
    {
        return 0;
    }
}
