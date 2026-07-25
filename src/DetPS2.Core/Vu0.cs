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
    /// COP2 special function path (VU0 "macro mode" — the EE issues VU ops synchronously as
    /// COP2 instructions using the same rs/rt/rd/sa fields as any MIPS COP2 instruction). Real
    /// VU0 macro mode reuses the VU upper-instruction opcode table, with rs/rt/rd mapping to
    /// Fs/Ft/Fd and sa carrying the destination write-mask — build a real upper-instruction
    /// word from those fields rather than a bare function code, since the field positions
    /// ExecuteUpper reads from are specific bit ranges, not implicit parameters.
    /// Returns stall cost for EE interlock.
    /// </summary>
    public int ExecuteVuInstruction(uint function, uint rs, uint rt, uint rd, uint sa)
    {
        uint upperWord = (function & 0x3F) | ((rd & 0x1F) << 6) | ((rs & 0x1F) << 11) | ((rt & 0x1F) << 16) | ((sa & 0xF) << 21);
        DecodeAndExecute(0, upperWord);

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
