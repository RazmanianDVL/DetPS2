using System;

namespace DetPS2.Core;

/// <summary>
/// VU0 - Vector Unit 0.
/// Phase 5 complete.
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

    public void ExecuteVuInstruction(uint function, uint rs, uint rt, uint rd, uint sa)
    {
        // Receives full operands from COP2
        uint opcode = (function & 0x3F);
        ExecuteInstruction(opcode);
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        base.ExecuteInstruction(opcode);
    }
}
