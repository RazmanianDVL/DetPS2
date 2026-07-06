using System;

namespace DetPS2.Core;

/// <summary>
/// VU0 - Vector Unit 0.
/// Phase 6 - Solid state. Receives instructions via COP2 from the Emotion Engine.
/// </summary>
public sealed class Vu0 : VectorUnit
{
    public Vu0(SystemMemory memory) : base(memory) { }

    public override void Reset() => base.Reset();
    public override void Step(ulong cycles) => base.Step(cycles);

    /// <summary>
    /// Called from Emotion Engine when a COP2 instruction targets VU0.
    /// </summary>
    public void ExecuteVuInstruction(uint function, uint rs, uint rt, uint rd, uint sa)
    {
        uint opcode = function & 0x3F;
        ExecuteInstruction(opcode);
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        base.ExecuteInstruction(opcode);
    }
}