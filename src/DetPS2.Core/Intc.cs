using System;

namespace DetPS2.Core;

/// <summary>
/// Interrupt Controller (INTC) - Foundational implementation.
/// 
/// This class provides the core interrupt system for the PS2.
/// It manages interrupt status, masking, and pending checks.
/// 
/// Design goals:
/// - Clean, extensible structure
/// - Clear separation between status and mask
/// - Easy to connect to Emotion Engine (COP0) later
/// - Foundation for realistic timer and DMA interrupt sources
/// </summary>
public sealed class Intc
{
    /// <summary>
    /// PS2 Interrupt sources (standard INTC bits).
/// </summary>
    public enum InterruptSource
    {
        GS = 0,
        VBlankStart = 2,
        VBlankEnd = 3,
        Vif0 = 4,
        Vif1 = 5,
        Vu0 = 6,
        Vu1 = 7,
        Ipu = 8,
        Timer0 = 9,
        Timer1 = 10,
        Timer2 = 11,
        Timer3 = 12,
        Sif = 13,
        DmaController = 14,
        // Add more as needed
    }

    public uint Stat { get; private set; }
    public uint Mask { get; private set; }

    public Intc()
    {
        Reset();
    }

    public void Reset()
    {
        Stat = 0;
        Mask = 0;
    }

    /// <summary>
    /// Raise an interrupt (set the bit in Stat).
    /// </summary>
    public void Raise(InterruptSource source)
    {
        Stat |= (1u << (int)source);
    }

    /// <summary>
    /// Acknowledge (clear) an interrupt.
    /// </summary>
    public void Acknowledge(InterruptSource source)
    {
        Stat &= ~(1u << (int)source);
    }

    /// <summary>
    /// Check if a specific interrupt is pending (Stat & Mask).
    /// </summary>
    public bool IsPending(InterruptSource source)
    {
        return (Stat & (1u << (int)source)) != 0 &&
               (Mask & (1u << (int)source)) != 0;
    }

    /// <summary>
    /// Set the interrupt mask.
    /// </summary>
    public void SetMask(uint mask)
    {
        Mask = mask;
    }

    /// <summary>
    /// Get the current pending interrupts (Stat & Mask).
    /// Useful for COP0 Cause register updates.
    /// </summary>
    public uint GetPendingInterrupts()
    {
        return Stat & Mask;
    }

    // Placeholder for future register read/write if needed
    public void WriteRegister(uint address, uint value) { }
    public uint ReadRegister(uint address) => 0;
}
