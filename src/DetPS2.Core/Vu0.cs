using System;

namespace DetPS2.Core;

/// <summary>
/// VU0 - Vector Unit 0.
/// Accepts real operands from COP2.
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
        // Now receives full operands from COP2
        // Build a more complete opcode and execute
        uint opcode = (function & 0x3F);

        // For now we still use simplified execution
        // Real per-instruction operand usage will be expanded
        ExecuteInstruction(opcode);
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        base.ExecuteInstruction(opcode);
    }
}
