using System;

namespace DetPS2.Core;

/// <summary>
/// VU0 - Vector Unit 0.
/// Acts as a coprocessor (COP2) to the Emotion Engine.
/// Must remain fully deterministic for netplay compatibility.
/// </summary>
public sealed class Vu0 : VectorUnit
{
    public Vu0(SystemMemory memory) : base(memory)
    {
    }

    public override void Reset()
    {
        base.Reset();
        // VU0-specific reset behavior can be added here
    }

    public override void Step(ulong cycles)
    {
        // TODO: Implement VU0 microprogram execution + COP2 instruction handling
        // Must stay deterministic. No host-side floating-point variance allowed.
        LocalCycles += cycles;
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        // TODO: Implement VU0 instruction decoding and execution
        // Start with integer operations for maximum determinism.
    }

    // TODO:
    // - Implement COP2 interface methods for EmotionEngine
    // - Add proper SaveState support
    // - Handle VU0-specific interrupts
}
