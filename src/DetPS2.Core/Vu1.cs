using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// VU1 - Vector Unit 1.
/// Has data buffering and basic processing from Vif1.
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
        // Process any buffered data from Vif1
        while (_incomingData.Count > 0)
        {
            uint data = _incomingData.Dequeue();
            ExecuteInstruction(data);
        }

        base.Step(cycles);
    }

    public void ReceiveFromVif1(uint data)
    {
        _incomingData.Enqueue(data);
    }

    public uint GetNextIncomingData()
    {
        return _incomingData.Count > 0 ? _incomingData.Dequeue() : 0;
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        base.ExecuteInstruction(opcode);
    }
}
