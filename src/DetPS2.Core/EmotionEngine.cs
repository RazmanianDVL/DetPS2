/// <summary>
/// Emotion Engine (R5900) - Polished scalar interpreter.
/// 
/// Features:
/// - Full common MIPS III + R5900 scalar instruction set
/// - Correct delay slot handling for all branches/jumps
/// - Basic COP1 FPU (arithmetic + LWC1/SWC1)
/// - COP2 routing to VU0
/// - ERET support for BIOS/exception return
/// - Deterministic: integer-only core, explicit state, master cycle driven
/// 
/// Architecture:
/// Step() pre-advances PC, then dispatches. Branches set pending target.
/// Delay slot is executed before branch is taken.
/// </summary>