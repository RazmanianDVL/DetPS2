using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Emotion Engine (R5900) CPU core.
/// 
/// This is the beginning of a clean, deterministic interpreter.
/// We use value types everywhere possible for predictability and to avoid GC pressure.
/// 128-bit GPRs are modeled as a simple struct (Lo + Hi) — easy to reason about and serialize.
/// 
/// Determinism notes:
/// - No floating point in instruction execution or timing yet.
/// - Every instruction will eventually cost a precise number of cycles.
/// - Self-modifying code and cache behavior will be handled explicitly later.
/// </summary>
public sealed class EmotionEngine
{
    private readonly SystemMemory _memory;

    // Program Counter
    public ulong PC { get; set; } = 0xBFC00000; // Typical PS2 reset vector (BIOS)

    // 32 general purpose 128-bit registers
    // Using a struct of two ulongs keeps everything value-type and easy to serialize for save states.
    [StructLayout(LayoutKind.Sequential)]
    public struct Gpr128
    {
        public ulong Lo;
        public ulong Hi;

        public override string ToString() => $"0x{Hi:X16}_{Lo:X16}";
    }

    private readonly Gpr128[] _gprs = new Gpr128[32];

    // COP0 registers (very simplified for now — Status, Cause, EPC, etc. come later)
    public uint COP0_Status { get; set; };
    public uint COP0_Cause { get; set; };
    public ulong COP0_EPC { get; set; };

    // LO/HI for multiply/divide (MIPS tradition, though R5900 MMI changes some behavior)
    public ulong LO { get; set; };
    public ulong HI { get; set; };

    public EmotionEngine(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void Reset()
    {
        PC = 0xBFC00000;
        Array.Clear(_gprs);
        COP0_Status = 0;
        COP0_Cause = 0;
        COP0_EPC = 0;
        LO = 0;
        HI = 0;
    }

    /// <summary>
    /// Execute a single instruction and return the number of cycles it took.
    /// This is the heart of the deterministic core.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Step()
    {
        ulong opcodeAddr = PC;
        uint opcode = _memory.Read32(opcodeAddr);
        PC += 4; // Most instructions are 4 bytes. Branch delay slots handled later.

        // Decode primary opcode (bits 31-26)
        uint primary = (opcode >> 26) & 0x3F;

        switch (primary)
        {
            case 0x00: // SPECIAL
                return ExecuteSpecial(opcode);

            case 0x0D: // ORI
                return ExecuteOri(opcode);

            case 0x0F: // LUI
                return ExecuteLui(opcode);

            case 0x23: // LW
                return ExecuteLw(opcode);

            case 0x2B: // SW
                return ExecuteSw(opcode);

            // TODO: Add all the rest — ADDI, BEQ, JAL, MMI instructions (0x1C), COP0, etc.
            // For now we implement just enough to run very simple test code.

            default:
                // Unknown opcode — for early development we just log and continue.
                // In a real deterministic emulator we might trap or handle reserved instruction exception.
                Console.WriteLine($"[EE] Unknown primary opcode 0x{primary:X2} at 0x{opcodeAddr:X8}");
                return 1; // Still consume a cycle so we don't infinite loop on bad code
        }
    }

    private int ExecuteSpecial(uint opcode)
    {
        uint function = opcode & 0x3F;
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;

        switch (function)
        {
            case 0x00: // SLL (shift left logical)
                _gprs[rd].Lo = _gprs[rt].Lo << (int)((opcode >> 6) & 0x1F);
                _gprs[rd].Hi = 0; // For now we keep it simple
                return 1;

            case 0x25: // OR
                _gprs[rd].Lo = _gprs[rs].Lo | _gprs[rt].Lo;
                _gprs[rd].Hi = _gprs[rs].Hi | _gprs[rt].Hi;
                return 1;

            case 0x2D: // DADDU (64-bit add unsigned) — common in 64-bit MIPS code
                _gprs[rd].Lo = _gprs[rs].Lo + _gprs[rt].Lo;
                // Very naive carry into Hi for now
                _gprs[rd].Hi = _gprs[rs].Hi + _gprs[rt].Hi;
                return 1;

            default:
                Console.WriteLine($"[EE] Unknown SPECIAL function 0x{function:X2}");
                return 1;
        }
    }

    private int ExecuteOri(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);

        _gprs[rt].Lo = _gprs[rs].Lo | imm;
        // Hi stays the same for ORI (it only affects the lower 16 bits of Lo in 64-bit view)
        return 1;
    }

    private int ExecuteLui(uint opcode)
    {
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);

        _gprs[rt].Lo = (ulong)imm << 16;
        _gprs[rt].Hi = 0;
        return 1;
    }

    private int ExecuteLw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);

        ulong addr = _gprs[rs].Lo + (ulong)offset;
        uint value = _memory.Read32(addr);
        _gprs[rt].Lo = value;
        _gprs[rt].Hi = 0;
        return 1; // Approximate — real timing depends on cache hits, etc.
    }

    private int ExecuteSw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);

        ulong addr = _gprs[rs].Lo + (ulong)offset;
        uint value = (uint)_gprs[rt].Lo;
        _memory.Write32(addr, value);
        return 1;
    }

    /// <summary>
    /// Get a copy of a GPR for debugging / save states.
    /// </summary>
    public Gpr128 GetGpr(int index) => _gprs[index & 0x1F];

    public void SetGpr(int index, Gpr128 value)
    {
        if (index != 0) // $zero is hardwired to 0
            _gprs[index & 0x1F] = value;
    }

    /// <summary>
    /// Dump some registers for debugging.
    /// </summary>
    public void DumpRegisters()
    {
        Console.WriteLine($"PC = 0x{PC:X8}");
        for (int i = 0; i < 8; i++)
        {
            Console.WriteLine($"${i:D2} = {_gprs[i]}   ${i+8:D2} = {_gprs[i+8]}   ${i+16:D2} = {_gprs[i+16]}   ${i+24:D2} = {_gprs[i+24]}");
        }
    }
}
