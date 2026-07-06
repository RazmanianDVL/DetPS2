using System;
using System.Runtime.CompilerServices;

namespace DetPS2.Core;

/// <summary>
/// Emotion Engine (R5900) - MIPS III based CPU with 128-bit GPRs.
/// 
/// Current Milestone: Expanded interpreter with delay slot support + common instructions.
/// 
/// Key architectural decisions:
/// - 128-bit GPRs stored as Gpr128 (Lo/Hi) to match real hardware and SaveState expectations.
/// - Most integer ops currently operate on .Lo (lower 64 bits). Full 128-bit MMI will come later.
/// - Branch delay slots are handled correctly (delay slot instruction executes before branch target).
/// - Step() executes one instruction (+ delay slot when applicable) and returns cycles consumed.
/// - Designed to be deterministic and easy to extend.
/// </summary>
public sealed class EmotionEngine : ISchedulable
{
    private readonly SystemMemory _memory;

    public ulong PC { get; set; } = 0xBFC00000;

    public struct Gpr128
    {
        public ulong Lo;
        public ulong Hi;

        public override string ToString() => $"0x{Hi:X16}_{Lo:X16}";
    }

    private readonly Gpr128[] _gprs = new Gpr128[32];

    public ulong LO { get; set; }
    public ulong HI { get; set; }

    // COP0 (minimal set for now)
    public uint COP0_Status { get; set; }
    public uint COP0_Cause { get; set; }
    public ulong COP0_EPC { get; set; }

    // Delay slot state
    private bool _inDelaySlot;
    private ulong _delaySlotTarget;

    public EmotionEngine(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void Reset()
    {
        PC = 0xBFC00000;
        Array.Clear(_gprs);
        LO = HI = 0;
        COP0_Status = COP0_Cause = 0;
        COP0_EPC = 0;
        _inDelaySlot = false;
        _delaySlotTarget = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Gpr128 GetGpr(int index) => _gprs[index & 0x1F];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetGpr(int index, Gpr128 value)
    {
        int reg = index & 0x1F;
        if (reg != 0)
            _gprs[reg] = value;
    }

    /// <summary>
    /// Executes one instruction (plus delay slot if applicable).
    /// Returns approximate cycles consumed.
    /// </summary>
    public int Step()
    {
        uint opcode = _memory.Read32(PC);
        bool tookBranch = ExecuteInstruction(opcode);

        int cycles = 1;

        if (tookBranch)
        {
            // Execute delay slot instruction
            uint delayOpcode = _memory.Read32(PC + 4);
            ExecuteInstruction(delayOpcode); // delay slot shouldn't take another branch in most cases

            PC = _delaySlotTarget;
            _inDelaySlot = false;
            cycles += 1; // delay slot cost (can be refined)
        }
        else
        {
            PC += 4;
        }

        return cycles;
    }

    /// <summary>
    /// Returns true if this instruction caused a branch (so caller knows to execute delay slot).
    /// </summary>
    private bool ExecuteInstruction(uint opcode)
    {
        uint primary = (opcode >> 26) & 0x3F;

        switch (primary)
        {
            case 0x00: return ExecuteSpecial(opcode);
            case 0x01: return ExecuteRegimm(opcode);
            case 0x02: return ExecuteJ(opcode);
            case 0x03: return ExecuteJal(opcode);
            case 0x04: return ExecuteBeq(opcode);
            case 0x05: return ExecuteBne(opcode);
            case 0x06: return ExecuteBlez(opcode);
            case 0x07: return ExecuteBgtz(opcode);
            case 0x08: ExecuteAddi(opcode); break;
            case 0x09: ExecuteAddiu(opcode); break;
            case 0x0A: ExecuteSlti(opcode); break;
            case 0x0B: ExecuteSltiu(opcode); break;
            case 0x0C: ExecuteOri(opcode); break;
            case 0x0D: ExecuteXori(opcode); break;
            case 0x0E: ExecuteAndi(opcode); break;
            case 0x0F: ExecuteLui(opcode); break;
            case 0x20: ExecuteLb(opcode); break;
            case 0x21: ExecuteLh(opcode); break;
            case 0x23: ExecuteLw(opcode); break;
            case 0x24: ExecuteLbu(opcode); break;
            case 0x25: ExecuteLhu(opcode); break;
            case 0x28: ExecuteSb(opcode); break;
            case 0x29: ExecuteSh(opcode); break;
            case 0x2B: ExecuteSw(opcode); break;
            case 0x0C: /* SYSCALL */ HandleSyscall(opcode); break;

            default:
                // Unknown opcode - log during development if needed
                // Console.WriteLine($"[EE] Unknown primary opcode 0x{primary:X2} at PC=0x{PC:X}");
                break;
        }

        return false; // no branch taken
    }

    // ==================== SPECIAL ====================
    private bool ExecuteSpecial(uint opcode)
    {
        uint function = opcode & 0x3F;
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;
        uint sa = (opcode >> 6) & 0x1F;

        switch (function)
        {
            case 0x00: // SLL
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo << (int)sa });
                break;

            case 0x02: // SRL
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo >> (int)sa });
                break;

            case 0x03: // SRA
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = (ulong)((long)GetGpr(rt).Lo >> (int)sa) });
                break;

            case 0x08: // JR
                _delaySlotTarget = GetGpr(rs).Lo;
                return true;

            case 0x09: // JALR
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = PC + 8 });
                _delaySlotTarget = GetGpr(rs).Lo;
                return true;

            case 0x0C: // SYSCALL
                HandleSyscall(opcode);
                break;

            case 0x0D: // BREAK
                // Can be used for debugging
                break;

            case 0x10: // MFHI
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = HI });
                break;

            case 0x11: // MTHI
                HI = GetGpr(rs).Lo;
                break;

            case 0x12: // MFLO
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO });
                break;

            case 0x13: // MTLO
                LO = GetGpr(rs).Lo;
                break;

            case 0x18: // MULT
            case 0x19: // MULTU
                {
                    long a = (long)GetGpr(rs).Lo;
                    long b = (long)GetGpr(rt).Lo;
                    long result = a * b;
                    LO = (ulong)(result & 0xFFFFFFFF);
                    HI = (ulong)((result >> 32) & 0xFFFFFFFF);
                }
                break;

            case 0x1A: // DIV
            case 0x1B: // DIVU
                {
                    long a = (long)GetGpr(rs).Lo;
                    long b = (long)GetGpr(rt).Lo;
                    if (b != 0)
                    {
                        LO = (ulong)(a / b);
                        HI = (ulong)(a % b);
                    }
                }
                break;

            case 0x20: case 0x21: // ADD / ADDU
                SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo + GetGpr(rt).Lo });
                break;

            case 0x23: // SUBU
                SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo - GetGpr(rt).Lo });
                break;

            case 0x24: // AND
                SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo & GetGpr(rt).Lo });
                break;

            case 0x25: // OR
                SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo | GetGpr(rt).Lo });
                break;

            case 0x26: // XOR
                SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo ^ GetGpr(rt).Lo });
                break;

            case 0x2A: // SLT
                SetGpr(rd, new Gpr128 { Lo = ((long)GetGpr(rs).Lo < (long)GetGpr(rt).Lo) ? 1UL : 0UL });
                break;

            case 0x2B: // SLTU
                SetGpr(rd, new Gpr128 { Lo = (GetGpr(rs).Lo < GetGpr(rt).Lo) ? 1UL : 0UL });
                break;
        }

        return false;
    }

    // ==================== REGIMM ====================
    private bool ExecuteRegimm(uint opcode)
    {
        uint rt = (opcode >> 16) & 0x1F;
        uint rs = (opcode >> 21) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong target = PC + 4 + (ulong)((int)offset << 2);

        switch (rt)
        {
            case 0: // BLTZ
                if ((long)GetGpr(rs).Lo < 0) { _delaySlotTarget = target; return true; }
                break;
            case 1: // BGEZ
                if ((long)GetGpr(rs).Lo >= 0) { _delaySlotTarget = target; return true; }
                break;
        }
        return false;
    }

    // ==================== Jumps & Branches ====================
    private bool ExecuteJ(uint opcode)
    {
        uint target = opcode & 0x03FFFFFF;
        _delaySlotTarget = (PC & 0xF0000000UL) | (target << 2);
        return true;
    }

    private bool ExecuteJal(uint opcode)
    {
        SetGpr(31, new Gpr128 { Lo = PC + 8 });
        uint target = opcode & 0x03FFFFFF;
        _delaySlotTarget = (PC & 0xF0000000UL) | (target << 2);
        return true;
    }

    private bool ExecuteBeq(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);

        if (GetGpr(rs).Lo == GetGpr(rt).Lo)
        {
            _delaySlotTarget = PC + 4 + (ulong)((int)offset << 2);
            return true;
        }
        return false;
    }

    private bool ExecuteBne(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);

        if (GetGpr(rs).Lo != GetGpr(rt).Lo)
        {
            _delaySlotTarget = PC + 4 + (ulong)((int)offset << 2);
            return true;
        }
        return false;
    }

    private bool ExecuteBlez(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        if ((long)GetGpr(rs).Lo <= 0)
        {
            _delaySlotTarget = PC + 4 + (ulong)((int)offset << 2);
            return true;
        }
        return false;
    }

    private bool ExecuteBgtz(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        if ((long)GetGpr(rs).Lo > 0)
        {
            _delaySlotTarget = PC + 4 + (ulong)((int)offset << 2);
            return true;
        }
        return false;
    }

    // ==================== Immediate ops ====================
    private void ExecuteAddi(uint opcode) { /* can add overflow check later */ ExecuteAddiu(opcode); }
    private void ExecuteAddiu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = GetGpr(rs).Lo + (ulong)imm });
    }

    private void ExecuteSlti(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = ((long)GetGpr(rs).Lo < imm) ? 1UL : 0UL });
    }

    private void ExecuteSltiu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (GetGpr(rs).Lo < (ulong)imm) ? 1UL : 0UL });
    }

    private void ExecuteOri(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = GetGpr(rs).Lo | imm });
    }

    private void ExecuteXori(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = GetGpr(rs).Lo ^ imm });
    }

    private void ExecuteAndi(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = GetGpr(rs).Lo & imm });
    }

    private void ExecuteLui(uint opcode)
    {
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (ulong)imm << 16 });
    }

    // ==================== Load/Store ====================
    private void ExecuteLb(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        sbyte value = (sbyte)_memory.Read8(addr);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (ulong)value });
    }

    private void ExecuteLbu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        byte value = _memory.Read8(addr);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = value });
    }

    private void ExecuteLh(uint opcode) { /* similar to Lw but 16-bit */ }
    private void ExecuteLhu(uint opcode) { /* similar */ }

    private void ExecuteLw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        uint value = _memory.Read32(addr);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = value });
    }

    private void ExecuteSb(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        _memory.Write8(addr, (byte)GetGpr(rt).Lo);
    }

    private void ExecuteSh(uint opcode) { /* similar */ }

    private void ExecuteSw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        _memory.Write32(addr, (uint)GetGpr(rt).Lo);
    }

    // ==================== System Calls ====================
    private void HandleSyscall(uint opcode)
    {
        // TODO: Implement minimal HLE syscalls here later (e.g. for homebrew)
        // For now we just continue execution.
        // You can add logging or a breakpoint hook here during development.
    }
}