using System;

namespace DetPS2.Core;

/// <summary>
/// VU1 - Vector Unit 1.
/// Works with Vif1 and feeds the Gif/GS pipeline.
/// Determinism is a core requirement for future netplay.
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

    /// <summary>
    /// Entry point for Vif1 to send data or microcode to VU1.
    /// </summary>
    public void ReceiveFromVif1(uint data)
    {
        // TODO: Implement proper Vif1 data handling and microprogram loading
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        base.ExecuteInstruction(opcode);
    }
}
