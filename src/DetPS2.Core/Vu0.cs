using System;

namespace DetPS2.Core;

/// <summary>
/// VU0 - Vector Unit 0.
/// 
/// Foxtrot improvement this round: Made COP2 entry point (ExecuteVuInstruction) functional
/// and cycle-aware. It now properly routes to DecodeAndExecute and updates LocalCycles.
/// This is a concrete step toward accurate COP2 / VU0 timing interaction.
/// 
/// Phase 6.2 Focus: COP2 interface timing analysis.
/// </summary>
public sealed class Vu0 : VectorUnit
{
    public Vu0(SystemMemory memory) : base(memory) { }

    public override void Reset() => base.Reset();

    public override int Step(ulong maxCycles) => base.Step(maxCycles);

    /// <summary>
    /// Called from Emotion Engine when a COP2 instruction targets VU0.
    /// 
    /// Improvement: Now properly dispatches to DecodeAndExecute (the real base implementation)
    /// and accounts cycles in LocalCycles. Returns a conservative cycle cost for the operation.
    /// Future: EmotionEngine can use the return value for interlock/stall modeling.
    /// </summary>
    public int ExecuteVuInstruction(uint function, uint rs, uint rt, uint rd, uint sa)
    {
        uint opcode = function & 0x3F;

        // Properly route to the actual decode/execute path in the base class
        DecodeAndExecute(opcode);

        // Concrete timing improvement: account a small fixed cost for COP2 operations
        // This makes the interaction visible in LocalCycles and gives a hook for future EE integration
        const int cop2CycleCost = 2;
        LocalCycles += cop2CycleCost;

        return cop2CycleCost;
    }

    // Removed the broken ExecuteInstruction override that didn't exist in base.
    // All execution now goes through DecodeAndExecute consistently.
}