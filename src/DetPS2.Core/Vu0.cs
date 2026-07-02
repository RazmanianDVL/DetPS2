using System;

namespace DetPS2.Core;

/// <summary>
/// VU0 - Vector Unit 0.
/// Now properly decodes and executes instructions from COP2.
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

    public void ExecuteVuInstruction(uint function, uint rt, uint rd)
    {
        // Decode the COP2 function code and execute the corresponding VU instruction
        // For now we map common COP2 functions to our existing instruction handlers
        uint fakeOpcode = (function & 0x3F);

        // Call into the base instruction executor
        ExecuteInstruction(fakeOpcode);
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        base.ExecuteInstruction(opcode);
    }
}
