using System;
using System.Runtime.CompilerServices;

namespace DetPS2.Core;

/// <summary>
/// Emotion Engine (R5900) - MIPS III based CPU with 128-bit GPRs.
/// 
/// Milestone 2: More complete instruction set + fixed delay slot + load/store improvements.
/// 
/// Key decisions:
/// - Still focusing on .Lo for most ops (full 128-bit later).
/// - Delay slots now correctly execute the following instruction before branching.
/// - Added many commonly used instructions for homebrew compatibility.
/// - Clean switch structure with no duplicate cases.
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

    public uint COP0_Status { get; set; }
    public uint COP0_Cause { get; set; }
    public ulong COP0_EPC { get; set; }

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

    public int Step()
    {
        uint opcode = _memory.Read32(PC);
        bool tookBranch = ExecuteInstruction(opcode);

        int cycles = 1;

        if (tookBranch)
        {
            uint delayOpcode = _memory.Read32(PC + 4);
            ExecuteInstruction(delayOpcode);
            PC = _delaySlotTarget;
            _inDelaySlot = false;
            cycles += 1;
        }
        else
        {
            PC += 4;
        }

        return cycles;
    }

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

            default:
                break;
        }

        return false;
    }

    // ==================== SPECIAL (0x00) ====================
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

            case 0x04: // SLLV
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo << (int)(GetGpr(rs).Lo & 0x1F) });
                break;

            case 0x06: // SRLV
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo >> (int)(GetGpr(rs).Lo & 0x1F) });
                break;

            case 0x07: // SRAV
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = (ulong)((long)GetGpr(rt).Lo >> (int)(GetGpr(rs).Lo & 0x1F)) });
                break;

            case 0x08: // JR
                _delaySlotTarget = GetGpr(rs).Lo;
                return true;

            case 0x09: // JALR
                if (rd != 0) SetGpr(rd, new Gpr128 { Lo = PC + 8 });
                _delaySlotTarget = GetGpr(rs).Lo;
                return true;

            case 0x0A: // MOVZ
                if (GetGpr(rt).Lo == 0 && rd != 0)
                    SetGpr(rd, GetGpr(rs));
                break;

            case 0x0B: // MOVN
                if (GetGpr(rt).Lo != 0 && rd != 0)
                    SetGpr(rd, GetGpr(rs));
                break;

            case 0x0C: // SYSCALL
                HandleSyscall(opcode);
                break;

            case 0x0D: // BREAK
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

            case 0x18: case 0x19: // MULT / MULTU
                {
                    long a = (long)GetGpr(rs).Lo;
                    long b = (long)GetGpr(rt).Lo;
                    long result = a * b;
                    LO = (ulong)(result & 0xFFFFFFFF);
                    HI = (ulong)((result >> 32) & 0xFFFFFFFF);
                }
                break;

            case 0x1A: case 0x1B: // DIV / DIVU
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

    // ==================== REGIMM (0x01) ====================
    private bool ExecuteRegimm(uint opcode)
    {
        uint rt = (opcode >> 16) & 0x1F;
        uint rs = (opcode >> 21) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong target = PC + 4 + (ulong)((int)offset << 2);

        switch (rt)
        {
            case 0: if ((long)GetGpr(rs).Lo < 0) { _delaySlotTarget = target; return true; } break; // BLTZ
            case 1: if ((long)GetGpr(rs).Lo >= 0) { _delaySlotTarget = target; return true; } break; // BGEZ
        }
        return false;
    }

    // ==================== J / JAL ====================
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

    // ==================== Branches ====================
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
        if ((long)GetGpr(rs).Lo <= 0) { _delaySlotTarget = PC + 4 + (ulong)((int)offset << 2); return true; }
        return false;
    }

    private bool ExecuteBgtz(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        if ((long)GetGpr(rs).Lo > 0) { _delaySlotTarget = PC + 4 + (ulong)((int)offset << 2); return true; }
        return false;
    }

    // ==================== Immediate Arithmetic ====================
    private void ExecuteAddi(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = GetGpr(rs).Lo + (ulong)imm });
    }

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

    // ==================== Load / Store ====================
    private void ExecuteLb(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        sbyte val = (sbyte)_memory.Read8(addr);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (ulong)val });
    }

    private void ExecuteLbu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        byte val = _memory.Read8(addr);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = val });
    }

    private void ExecuteLh(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        short val = (short)(_memory.Read8(addr) | (_memory.Read8(addr + 1) << 8));
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (ulong)val });
    }

    private void ExecuteLhu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        ushort val = (ushort)(_memory.Read8(addr) | (_memory.Read8(addr + 1) << 8));
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = val });
    }

    private void ExecuteLw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        uint val = _memory.Read32(addr);
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = val });
    }

    private void ExecuteSb(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        _memory.Write8(addr, (byte)GetGpr(rt).Lo);
    }

    private void ExecuteSh(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        ushort val = (ushort)GetGpr(rt).Lo;
        _memory.Write8(addr, (byte)val);
        _memory.Write8(addr + 1, (byte)(val >> 8));
    }

    private void ExecuteSw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)offset;
        _memory.Write32(addr, (uint)GetGpr(rt).Lo);
    }

    private void HandleSyscall(uint opcode)
    {
        // Future HLE syscall handling goes here
    }
}