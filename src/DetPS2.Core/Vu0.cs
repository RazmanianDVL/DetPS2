using System;

namespace DetPS2.Core;

/// <summary>
/// VU0 - Vector Unit 0 (COP2 to Emotion Engine).
/// Now receives real instruction calls from COP2.
/// </summary>
public sealed class Vu0 : VectorUnit
{
    public Vu0(SystemMemory memory) : base(memory)
    {
    }

    public override void Reset()
    {
        base.Reset();
    }

    public override void Step(ulong cycles)
    {
        base.Step(cycles);
    }

    /// <summary>
    /// Called by EmotionEngine COP2 when a VU0 instruction should be executed.
    /// </summary>
    public void ExecuteVuInstruction(uint function, uint rt, uint rd)
    {
        // For now we just execute a generic step.
        // Real per-instruction decoding will be added here.
        Step(1);
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        base.ExecuteInstruction(opcode);
    }
}
