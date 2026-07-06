using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// VU1 - Vector Unit 1.
/// Phase 6 solid state.
/// Receives quadword data from VIF1 and executes instructions.
/// </summary>
public sealed class Vu1 : VectorUnit
{
    private readonly Queue<uint> _incomingData = new Queue<uint>();
    private uint _currentQuadwordWordCount = 0;

    public Vu1(SystemMemory memory) : base(memory) { }

    public override void Reset()
    {
        base.Reset();
        _incomingData.Clear();
        _currentQuadwordWordCount = 0;
    }

    public override void Step(ulong cycles)
    {
        // Process any pending data from VIF1
        while (_incomingData.Count > 0)
        {
            uint data = _incomingData.Dequeue();
            ExecuteInstruction(data);
            _currentQuadwordWordCount++;

            if (_currentQuadwordWordCount >= 4)
                _currentQuadwordWordCount = 0;
        }

        base.Step(cycles);
    }

    public void ReceiveFromVif1(uint data) => _incomingData.Enqueue(data);

    public uint GetNextIncomingData()
        => _incomingData.Count > 0 ? _incomingData.Dequeue() : 0u;

    protected override void ExecuteInstruction(uint opcode)
    {
        base.ExecuteInstruction(opcode);
    }
}