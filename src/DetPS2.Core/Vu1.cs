using System;

namespace DetPS2.Core;

/// <summary>
/// VU1 - Vector Unit 1.
/// Works with Vif1 and feeds into the Gif/GS pipeline.
/// Must remain fully deterministic for netplay compatibility.
/// </summary>
public sealed class Vu1 : VectorUnit
{
    public Vu1(SystemMemory memory) : base(memory)
    {
    }

    public override void Reset()
    {
        base.Reset();
        // VU1-specific reset behavior can be added here
    }

    public override void Step(ulong cycles)
    {
        // TODO: Implement VU1 microprogram execution + Vif1 integration
        // Must stay deterministic. Timing with Vif1 and Gif is critical.
        LocalCycles += cycles;
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        // TODO: Implement VU1 instruction decoding and execution
        // Start with integer operations for maximum determinism.
    }

    // TODO:
    // - Add Vif1 integration
    // - Add proper SaveState support
    // - Handle VU1-specific interrupts and Gif path
}
