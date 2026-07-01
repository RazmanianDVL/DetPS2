using System;

namespace DetPS2.Core;

/// <summary>
/// Input/Output Processor (IOP) - Phase 3
/// Expanded with SIF communication and structure for future IOP CPU core.
/// </summary>
public sealed class Iop
{
    public Intc Intc { get; }

    public uint SifMbxFromEE { get; private set; }
    public uint SifMbxToEE { get; private set; }

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

    public void WriteSifMailboxFromEE(uint value)
    {
        SifMbxFromEE = value;
        Console.WriteLine($"[IOP] SIF write from EE: 0x{value:X8}");

        // Simple response for testing
        SifMbxToEE = ~value;
    }

    public uint ReadSifMailboxToEE() => SifMbxToEE;

    public void Step(ulong cycles)
    {
        if (!Running) return;
        // TODO: IOP R3000A CPU core execution
    }

    public void Stop() => Running = false;
}
