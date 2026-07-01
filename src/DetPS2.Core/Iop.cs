using System;

namespace DetPS2.Core;

/// <summary>
/// Input/Output Processor (IOP) - Phase 3
/// Expanded skeleton with improved SIF mailbox and basic structure for future IOP CPU core.
/// </summary>
public sealed class Iop
{
    public Intc Intc { get; }

    // SIF Mailbox (EE <-> IOP communication)
    public uint SifMbxFromEE { get; private set; }
    public uint SifMbxToEE { get; private set; }

    // Simple status
    public bool Running { get; private set; } = true;

    public Iop(Intc intc)
    {
        Intc = intc ?? throw new ArgumentNullException(nameof(intc));
        Reset();
    }

    public void Reset()
    {
        SifMbxFromEE = 0;
        SifMbxToEE = 0;
        Running = true;
    }

    /// <summary>
    /// EE writes to IOP mailbox.
    /// </summary>
    public void WriteSifMailboxFromEE(uint value)
    {
        SifMbxFromEE = value;
        Console.WriteLine($"[IOP] Received from EE via SIF: 0x{value:X8}");

        // Simple echo for testing
        SifMbxToEE = value ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// EE reads from IOP mailbox.
    /// </summary>
    public uint ReadSifMailboxToEE()
    {
        return SifMbxToEE;
    }

    public void Step(ulong cycles)
    {
        // Future: IOP R3000A CPU core, DMA, CDVD, etc.
        if (!Running) return;
    }

    public void Stop()
    {
        Running = false;
    }
}
