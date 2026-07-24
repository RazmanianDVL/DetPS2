using System;

namespace DetPS2.Core;

/// <summary>
/// Interrupt Controller (Phase 8).
/// STAT / MASK registers with MMIO; notifies EE to sync COP0 Cause.
/// </summary>
public sealed class Intc : ISchedulable
{
    public enum InterruptSource
    {
        GS = 0,
        SbUs = 1,
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
    }

    // EE INTC MMIO
    public const uint AddrStat = 0x1000F000;
    public const uint AddrMask = 0x1000F010;

    public uint Stat { get; private set; }
    public uint Mask { get; private set; }

    private Action? _onChanged;

    public Intc() => Reset();

    public void SetNotify(Action onChanged) => _onChanged = onChanged;

    public void Reset()
    {
        Stat = 0;
        Mask = 0;
    }

    public void Raise(InterruptSource source)
    {
        uint bit = 1u << (int)source;
        if ((Stat & bit) == 0)
        {
            Stat |= bit;
            _onChanged?.Invoke();
        }
        else
        {
            Stat |= bit;
            _onChanged?.Invoke();
        }
    }

    public void Acknowledge(InterruptSource source)
    {
        Stat &= ~(1u << (int)source);
        _onChanged?.Invoke();
    }

    /// <summary>Write-1-to-clear style for STAT.</summary>
    public void WriteStatClear(uint value)
    {
        Stat &= ~value;
        _onChanged?.Invoke();
    }

    public bool IsRaised(InterruptSource source) =>
        (Stat & (1u << (int)source)) != 0;

    public bool IsPending(InterruptSource source) =>
        (Stat & (1u << (int)source)) != 0 &&
        (Mask & (1u << (int)source)) != 0;

    public void SetMask(uint mask)
    {
        Mask = mask;
        _onChanged?.Invoke();
    }

    /// <summary>Restore STAT/MASK from save state.</summary>
    public void RestoreState(uint stat, uint mask)
    {
        Stat = stat;
        Mask = mask;
        _onChanged?.Invoke();
    }

    public uint GetPendingInterrupts() => Stat & Mask;

    public bool AnyPending => GetPendingInterrupts() != 0;

    public uint ReadRegister(uint address)
    {
        return address switch
        {
            AddrStat => Stat,
            AddrMask => Mask,
            _ when (address & ~0xFu) == AddrStat => Stat,
            _ when (address & ~0xFu) == AddrMask => Mask,
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        if (address == AddrStat || (address & ~0xFu) == AddrStat)
            WriteStatClear(value);
        else if (address == AddrMask || (address & ~0xFu) == AddrMask)
            SetMask(value);
    }

    public int Step(ulong maxCycles) => 0;
}
