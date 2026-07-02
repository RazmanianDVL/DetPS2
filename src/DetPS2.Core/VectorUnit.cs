using System;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Base class for VU0 and VU1.
/// Designed with determinism as the primary constraint.
/// All execution must be reproducible for future netplay support.
/// </summary>
public abstract class VectorUnit
{
    protected readonly SystemMemory _memory;

    // VU has 32 x 128-bit registers (VF00-VF31)
    // VF00 is hardwired to (0.0f, 0.0f, 0.0f, 1.0f) in many operations
    [StructLayout(LayoutKind.Sequential)]
    public struct VuReg128
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public override string ToString() => $"({X}, {Y}, {Z}, {W})";
    }

    protected readonly VuReg128[] _vf = new VuReg128[32];

    // Accumulator (ACC) - used by many vector instructions
    public VuReg128 ACC;

    // Control registers
    public uint Status;     // Status flags
    public uint MAC;        // MAC flags (from last operation)
    public uint Clipping;   // Clipping flags
    public uint R;          // Random number register (deterministic implementation needed)
    public uint I;          // Interrupt control
    public uint Q;          // Q register (used by some instructions)
    public uint P;          // P register (used by some instructions)

    // Program counter for microprogram execution
    public uint PC;

    // Cycle counter local to this VU (for timing)
    public ulong LocalCycles;

    protected VectorUnit(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public virtual void Reset()
    {
        Array.Clear(_vf);
        ACC = default;
        Status = 0;
        MAC = 0;
        Clipping = 0;
        R = 0;
        I = 0;
        Q = 0;
        P = 0;
        PC = 0;
        LocalCycles = 0;

        // VF00 is often treated as (0,0,0,1) for many operations
        _vf[0] = new VuReg128 { X = 0f, Y = 0f, Z = 0f, W = 1f };
    }

    /// <summary>
    /// Execute a number of VU cycles.
    /// Must remain fully deterministic.
    /// </summary>
    public abstract void Step(ulong cycles);

    /// <summary>
    /// Execute a single VU instruction.
    /// This is where determinism must be strictly maintained.
    /// </summary>
    protected abstract void ExecuteInstruction(uint opcode);

    // TODO in future steps:
    // - Implement full instruction decoder
    // - Add proper floating-point handling with controlled determinism
    // - Implement memory access through Vif when integrated
    // - Add SaveState support for VU state
}
