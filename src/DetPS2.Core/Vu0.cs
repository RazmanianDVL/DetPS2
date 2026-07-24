using System;

namespace DetPS2.Core;

/// <summary>
/// VU0 — Phase 10: COP2 interlock + microprogram entry.
/// </summary>
public sealed class Vu0 : VectorUnit, ISchedulable
{
    public Vu0(SystemMemory memory) : base(memory) { }

    public override void Reset() => base.Reset();

    public override int Step(ulong maxCycles) => base.Step(maxCycles);

    /// <summary>
    /// COP2 special function path. Returns stall cost for EE interlock.
    /// </summary>
    public int ExecuteVuInstruction(uint function, uint rs, uint rt, uint rd, uint sa)
    {
        uint opcode = function & 0x3F;
        DecodeAndExecute(opcode);

        int cost = 2;
        if (IsEfuBusy)
            cost += _efuStallRemaining;
        LocalCycles += (ulong)cost;
        AddCop2Interlock(cost);
        return cost;
    }

    /// <summary>VCall-style: start micro at entry (word index).</summary>
    public void VCall(uint entryWord)
    {
        StartMicro(entryWord * 4);
        AddCop2Interlock(4);
    }
}
