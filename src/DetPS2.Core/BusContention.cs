using System;

namespace DetPS2.Core;

/// <summary>
/// Memory bus contention (Phases 10/27).
/// When DMA is active, EE cycle budget for a slice is reduced deterministically.
/// Integer-only; no host timing.
/// </summary>
public sealed class BusContention
{
    /// <summary>0 = no contention, 100 = EE gets ~0% of slice (clamped).</summary>
    public int ContentionPercent { get; private set; }

    public int ActiveDmaChannels { get; private set; }
    public ulong Samples { get; private set; }
    /// <summary>Percent added per active DMA channel (Det-stable knobs).</summary>
    public int PercentPerChannel { get; set; } = 12;
    public int MaxContentionPercent { get; set; } = 75;
    public int MinEeKeepPercent { get; set; } = 5;
    public ulong TotalCyclesScaled { get; private set; }

    /// <summary>How many EE cycles to grant from a nominal slice.</summary>
    public ulong ScaleEeBudget(ulong nominalSlice)
    {
        Samples++;
        if (ContentionPercent <= 0 || nominalSlice == 0)
            return nominalSlice;
        int keep = Math.Clamp(100 - ContentionPercent, MinEeKeepPercent, 100);
        ulong scaled = Math.Max(1UL, (nominalSlice * (ulong)keep) / 100UL);
        TotalCyclesScaled += scaled;
        return scaled;
    }

    public void NotifyDmaActivity(int activeChannels)
    {
        ActiveDmaChannels = Math.Max(0, activeChannels);
        ContentionPercent = Math.Min(MaxContentionPercent, ActiveDmaChannels * PercentPerChannel);
    }

    public void Reset()
    {
        ContentionPercent = 0;
        ActiveDmaChannels = 0;
        Samples = 0;
        TotalCyclesScaled = 0;
    }
}
