using System;

namespace DetPS2.Core;

/// <summary>
/// VU1 - Vector Unit 1.
/// Now has basic data reception from Vif1.
/// </summary>
public sealed class Vu1 : VectorUnit
{
    public Vu1(SystemMemory memory) : base(memory)
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

    public void ReceiveFromVif1(uint data)
    {
        // Basic handling: for now we just store the data in a simple way
        // Real implementation will unpack quadwords into VU registers or microprogram memory
        Console.WriteLine($"[VU1] Received data from Vif1: 0x{data:X8}");

        // Example: treat incoming data as an instruction to execute
        ExecuteInstruction(data);
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        base.ExecuteInstruction(opcode);
    }
}
