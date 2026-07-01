using System;

namespace DetPS2.Core;

/// <summary>
/// Interrupt Controller (INTC) - Phase 3
/// Expanded with more realistic timer and interrupt behavior.
/// </summary>
public sealed class Intc
{
    public uint Stat { get; private set; }
    public uint Mask { get; private set; }

    public uint Timer0 { get; private set; }
    public uint Timer1 { get; private set; }
    public uint Timer2 { get; private set; }

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
        Timer2 = 0;
    }

    public void RaiseInterrupt(uint irq)
    {
        Stat |= (1u << (int)irq);
    }

    public void Acknowledge(uint irq)
    {
        Stat &= ~(1u << (int)irq);
    }

    public bool IsPending(uint irq)
    {
        return (Stat & (1u << (int)irq)) != 0 && (Mask & (1u << (int)irq)) != 0;
    }

    public void SetMask(uint mask)
    {
        Mask = mask;
    }

    public void Step(ulong cycles)
    {
        Timer0 += (uint)cycles;
        Timer1 += (uint)(cycles / 2);
        Timer2 += (uint)(cycles / 3);

        // Example: raise a timer interrupt every ~1000 cycles (very rough)
        if (Timer0 % 1000 == 0)
        {
            RaiseInterrupt(0); // Timer 0 interrupt example
        }
    }

    public void WriteRegister(uint address, uint value)
    {
        // Placeholder for future expansion
    }

    public uint ReadRegister(uint address)
    {
        return 0;
    }
}
