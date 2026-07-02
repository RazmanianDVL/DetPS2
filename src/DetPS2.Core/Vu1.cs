using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// VU1 - Vector Unit 1.
/// Has basic data reception and buffering from Vif1.
/// </summary>
public sealed class Vu1 : VectorUnit
{
    private readonly Queue<uint> _incomingData = new Queue<uint>();

    public Vu1(SystemMemory memory) : base(memory)
    {
    }

    public override void Reset()
    {
        base.Reset();
        _incomingData.Clear();
    }

    public override void Step(ulong cycles)
    {
        base.Step(cycles);
    }

    public void ReceiveFromVif1(uint data)
    {
        // Store incoming data in a buffer for later processing
        _incomingData.Enqueue(data);

        // For now, also try to execute it as an instruction
        ExecuteInstruction(data);
    }

    /// <summary>
    /// Get the next piece of data received from Vif1.
    /// </summary>
    public uint GetNextIncomingData()
    {
        return _incomingData.Count > 0 ? _incomingData.Dequeue() : 0;
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        base.ExecuteInstruction(opcode);
    }
}
