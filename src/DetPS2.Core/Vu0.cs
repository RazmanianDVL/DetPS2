using System;

namespace DetPS2.Core;

/// <summary>
/// VU0 - Vector Unit 0.
/// 
/// Phase 6.2 Focus: COP2 interface timing analysis.
/// 
/// Current State:
/// - ExecuteVuInstruction is called from EmotionEngine when a COP2 move or
///   VU macro instruction targets VU0.
/// - Currently executes the instruction immediately with no cycle cost modeling.
/// 
/// Timing Observations:
/// - Many COP2 instructions have specific interlock requirements with the EE pipeline.
/// - Some VU0 operations can cause the EE to stall until the operation completes.
/// - The current implementation does not model any delay or stall back to the EE.
/// 
/// Low-Risk Improvement Opportunity:
/// - We could return a cycle cost from ExecuteVuInstruction so the Emotion Engine
///   can account for it when advancing its own cycles. This would be a small, contained change.
/// 
/// TODO (Phase 6.2+): Investigate returning timing information from this method
/// so COP2 operations can contribute to accurate EE/VU0 interleaving.
/// </summary>
public sealed class Vu0 : VectorUnit
{
    public Vu0(SystemMemory memory) : base(memory) { }

    public override void Reset() => base.Reset();

    public override int Step(ulong maxCycles) => base.Step(maxCycles);

    /// <summary>
    /// Called from Emotion Engine when a COP2 instruction targets VU0.
    /// Currently executes immediately with no timing feedback to the caller.
    /// </summary>
    public void ExecuteVuInstruction(uint function, uint rs, uint rt, uint rd, uint sa)
    {
        uint opcode = function & 0x3F;
        ExecuteInstruction(opcode);
    }

    protected override void ExecuteInstruction(uint opcode)
    {
        base.ExecuteInstruction(opcode);
    }
}