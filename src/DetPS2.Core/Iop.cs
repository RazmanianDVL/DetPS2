using System;

namespace DetPS2.Core;

/// <summary>
/// Input/Output Processor (IOP) - Early Phase 3 skeleton.
/// Provides basic SIF mailbox communication with EE.
/// Very minimal for now.
/// </summary>
public sealed class Iop
{
    public Intc Intc { get; }

    // Very simple SIF mailbox (EE <-> IOP communication)
    public uint SifMbxFromEE { get; private set; }
    public uint SifMbxToEE { get; private set; }

    public Iop(Intc intc)
    {
        Intc = intc ?? throw new ArgumentNullException(nameof(intc));
        Reset();
    }

    public void Reset()
    {
        SifMbxFromEE = 0;
        SifMbxToEE = 0;
    }

    public void WriteSifMailbox(uint value)
    {
        SifMbxFromEE = value;
        Console.WriteLine($"[IOP] SIF mailbox write from EE: 0x{value:X8}");
    }

    public uint ReadSifMailbox()
    {
        return SifMbxToEE;
    }

    public void Step(ulong cycles)
    {
        // Future: IOP CPU core, DMA, CDVD, etc.
    }
}
