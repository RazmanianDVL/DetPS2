using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Emotion Engine (R5900) CPU core.
/// Phase 1 nearing completion.
/// </summary>
public sealed class EmotionEngine
{
    private readonly SystemMemory _memory;

    public ulong PC { get; set; } = 0xBFC00000;

    private bool _inDelaySlot;
    private ulong _branchTarget;
    private bool _branchTaken;

    [StructLayout(LayoutKind.Sequential)]
    public struct Gpr128
    {
        public ulong Lo;
        public ulong Hi;
        public override string ToString() => $"0x{Hi:X16}_{Lo:X16}";
    }

    private readonly Gpr128[] _gprs = new Gpr128[32];

    public uint COP0_Status { get; set; }
    public uint COP0_Cause { get; set; }
    public ulong COP0_EPC { get; set; }

    public ulong LO { get; set; }
    public ulong HI { get; set; }

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
        _inDelaySlot = false;
        _branchTaken = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Step()
    {
        ulong currentPC = PC;
        uint opcode = _memory.Read32(currentPC);
        ulong nextPC = currentPC + 4;

        uint primary = (opcode >> 26) & 0x3F;
        int cycles = primary switch
        {
            0x00 => ExecuteSpecial(opcode, ref nextPC),
            0x01 => ExecuteRegimm(opcode, ref nextPC),
            0x02 => ExecuteJ(opcode, ref nextPC),
            0x03 => ExecuteJal(opcode, ref nextPC),
            0x04 => ExecuteBeq(opcode, ref nextPC),
            0x05 => ExecuteBne(opcode, ref nextPC),
            0x06 => ExecuteBlez(opcode, ref nextPC),
            0x07 => ExecuteBgtz(opcode, ref nextPC),
            0x08 => ExecuteAddi(opcode),
            0x09 => ExecuteAddiu(opcode),
            0x0C => ExecuteAndi(opcode),
            0x0D => ExecuteOri(opcode),
            0x0E => ExecuteXori(opcode),
            0x0F => ExecuteLui(opcode),
            0x20 => ExecuteLb(opcode),
            0x21 => ExecuteLh(opcode),
            0x23 => ExecuteLw(opcode),
            0x24 => ExecuteLbu(opcode),
            0x25 => ExecuteLhu(opcode),
            0x28 => ExecuteSb(opcode),
            0x29 => ExecuteSh(opcode),
            0x2B => ExecuteSw(opcode),
            _ => HandleUnknown(opcode, currentPC)
        };

        if (_inDelaySlot)
        {
            if (_branchTaken)
            {
                nextPC = _branchTarget;
            }
            _inDelaySlot = false;
            _branchTaken = false;
        }

        PC = nextPC;
        return cycles;
    }

    private int HandleUnknown(uint opcode, ulong addr)
    {
        Console.WriteLine($"[EE] Unknown opcode 0x{opcode:X8} at 0x{addr:X8}");
        return 1;
    }

    // ==================== SPECIAL ====================
    private int ExecuteSpecial(uint opcode, ref ulong nextPC)
    {
        uint function = opcode & 0x3F;
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;
        uint sa = (opcode >> 6) & 0x1F;

        return function switch
        {
            0x00 => ExecuteSll(rd, rt, sa),
            0x02 => ExecuteSrl(rd, rt, sa),
            0x03 => ExecuteSra(rd, rt, sa),
            0x0C => ExecuteSyscall(opcode, ref nextPC),
            0x18 => ExecuteMult(rs, rt),      // MULT
            0x19 => ExecuteMultu(rs, rt),     // MULTU
            0x1A => ExecuteDiv(rs, rt),       // DIV
            0x1B => ExecuteDivu(rs, rt),      // DIVU
            0x20 => ExecuteAdd(rd, rs, rt),
            0x21 => ExecuteAddu(rd, rs, rt),
            0x23 => ExecuteSubu(rd, rs, rt),
            0x24 => ExecuteAnd(rd, rs, rt),
            0x25 => ExecuteOr(rd, rs, rt),
            0x26 => ExecuteXor(rd, rs, rt),
            0x27 => ExecuteNor(rd, rs, rt),
            0x2A => ExecuteSlt(rd, rs, rt),
            0x2B => ExecuteSltu(rd, rs, rt),
            0x12 => ExecuteMflo(rd),          // MFLO
            0x10 => ExecuteMfhi(rd),          // MFHI
            0x13 => ExecuteMtlo(rs),          // MTLO
            0x11 => ExecuteMthi(rs),          // MTHI
            _ => HandleUnknown(opcode, PC)
        };
    }

    private int ExecuteSyscall(uint opcode, ref ulong nextPC)
    {
        COP0_EPC = PC;
        COP0_Cause = (8 << 2);
        nextPC = 0x80000180;
        return 1;
    }

    // ==================== MULTIPLY / DIVIDE ====================
    private int ExecuteMult(int rs, int rt)
    {
        long a = (long)_gprs[rs].Lo;
        long b = (long)_gprs[rt].Lo;
        long result = a * b;
        LO = (ulong)(result & 0xFFFFFFFF);
        HI = (ulong)((result >> 32) & 0xFFFFFFFF);
        return 1;
    }

    private int ExecuteMultu(int rs, int rt)
    {
        ulong a = _gprs[rs].Lo;
        ulong b = _gprs[rt].Lo;
        ulong result = a * b;  // This will overflow naturally for 64-bit result
        LO = result & 0xFFFFFFFF;
        HI = (result >> 32) & 0xFFFFFFFF;
        return 1;
    }

    private int ExecuteDiv(int rs, int rt)
    {
        long a = (long)_gprs[rs].Lo;
        long b = (long)_gprs[rt].Lo;
        if (b != 0)
        {
            LO = (ulong)(a / b);
            HI = (ulong)(a % b);
        }
        else
        {
            // Division by zero behavior (undefined on real hardware, simplified here)
            LO = 0;
            HI = 0;
        }
        return 1;
    }

    private int ExecuteDivu(int rs, int rt)
    {
        ulong a = _gprs[rs].Lo;
        ulong b = _gprs[rt].Lo;
        if (b != 0)
        {
            LO = a / b;
            HI = a % b;
        }
        else
        {
            LO = 0;
            HI = 0;
        }
        return 1;
    }

    private int ExecuteMfhi(int rd)
    {
        if (rd != 0) _gprs[rd].Lo = HI;
        return 1;
    }

    private int ExecuteMflo(int rd)
    {
        if (rd != 0) _gprs[rd].Lo = LO;
        return 1;
    }

    private int ExecuteMthi(int rs)
    {
        HI = _gprs[rs].Lo;
        return 1;
    }

    private int ExecuteMtlo(int rs)
    {
        LO = _gprs[rs].Lo;
        return 1;
    }

    // ==================== SHIFTS ====================
    private int ExecuteSll(int rd, int rt, int sa) { if (rd != 0) { _gprs[rd].Lo = _gprs[rt].Lo << sa; _gprs[rd].Hi = 0; } return 1; }
    private int ExecuteSrl(int rd, int rt, int sa) { if (rd != 0) { _gprs[rd].Lo = _gprs[rt].Lo >> sa; _gprs[rd].Hi = 0; } return 1; }
    private int ExecuteSra(int rd, int rt, int sa) { if (rd != 0) { _gprs[rd].Lo = (ulong)((long)_gprs[rt].Lo >> sa); _gprs[rd].Hi = 0; } return 1; }

    // ==================== ARITHMETIC ====================
    private int ExecuteAddu(int rd, int rs, int rt) { if (rd != 0) _gprs[rd].Lo = _gprs[rs].Lo + _gprs[rt].Lo; return 1; }
    private int ExecuteSubu(int rd, int rs, int rt) { if (rd != 0) _gprs[rd].Lo = _gprs[rs].Lo - _gprs[rt].Lo; return 1; }
    private int ExecuteAnd(int rd, int rs, int rt) { if (rd != 0) { _gprs[rd].Lo = _gprs[rs].Lo & _gprs[rt].Lo; _gprs[rd].Hi = _gprs[rs].Hi & _gprs[rt].Hi; } return 1; }
    private int ExecuteOr(int rd, int rs, int rt)  { if (rd != 0) { _gprs[rd].Lo = _gprs[rs].Lo | _gprs[rt].Lo; _gprs[rd].Hi = _gprs[rs].Hi | _gprs[rt].Hi; } return 1; }
    private int ExecuteXor(int rd, int rs, int rt) { if (rd != 0) { _gprs[rd].Lo = _gprs[rs].Lo ^ _gprs[rt].Lo; _gprs[rd].Hi = _gprs[rs].Hi ^ _gprs[rt].Hi; } return 1; }
    private int ExecuteNor(int rd, int rs, int rt) { if (rd != 0) { _gprs[rd].Lo = ~(_gprs[rs].Lo | _gprs[rt].Lo); _gprs[rd].Hi = ~(_gprs[rs].Hi | _gprs[rt].Hi); } return 1; }
    private int ExecuteSlt(int rd, int rs, int rt) { if (rd != 0) _gprs[rd].Lo = ((long)_gprs[rs].Lo < (long)_gprs[rt].Lo) ? 1UL : 0; return 1; }
    private int ExecuteSltu(int rd, int rs, int rt) { if (rd != 0) _gprs[rd].Lo = (_gprs[rs].Lo < _gprs[rt].Lo) ? 1UL : 0; return 1; }

    // ==================== REGIMM ====================
    private int ExecuteRegimm(uint opcode, ref ulong nextPC)
    {
        uint rt = (opcode >> 16) & 0x1F;
        long value = (long)_gprs[(opcode >> 21) & 0x1F].Lo;
        return rt switch
        {
            0x00 => TakeBranchIf(opcode, value < 0, ref nextPC),
            0x01 => TakeBranchIf(opcode, value >= 0, ref nextPC),
            _ => HandleUnknown(opcode, PC)
        };
    }

    // ==================== JUMPS ====================
    private int ExecuteJ(uint opcode, ref ulong nextPC)
    {
        uint target = opcode & 0x03FFFFFF;
        _branchTarget = (PC & 0xF0000000) | (target << 2);
        _branchTaken = true;
        _inDelaySlot = true;
        return 1;
    }

    private int ExecuteJal(uint opcode, ref ulong nextPC)
    {
        _gprs[31].Lo = PC + 8;
        uint target = opcode & 0x03FFFFFF;
        _branchTarget = (PC & 0xF0000000) | (target << 2);
        _branchTaken = true;
        _inDelaySlot = true;
        return 1;
    }

    // ==================== BRANCHES ====================
    private int ExecuteBeq(uint opcode, ref ulong nextPC) => TakeBranchIf(opcode, _gprs[(opcode >> 21) & 0x1F].Lo == _gprs[(opcode >> 16) & 0x1F].Lo, ref nextPC);
    private int ExecuteBne(uint opcode, ref ulong nextPC) => TakeBranchIf(opcode, _gprs[(opcode >> 21) & 0x1F].Lo != _gprs[(opcode >> 16) & 0x1F].Lo, ref nextPC);
    private int ExecuteBlez(uint opcode, ref ulong nextPC) => TakeBranchIf(opcode, (long)_gprs[(opcode >> 21) & 0x1F].Lo <= 0, ref nextPC);
    private int ExecuteBgtz(uint opcode, ref ulong nextPC) => TakeBranchIf(opcode, (long)_gprs[(opcode >> 21) & 0x1F].Lo > 0, ref nextPC);

    private int TakeBranchIf(uint opcode, bool condition, ref ulong nextPC)
    {
        if (condition)
        {
            short offset = (short)(opcode & 0xFFFF);
            _branchTarget = PC + (ulong)((long)offset << 2) + 4;
            _branchTaken = true;
        }
        _inDelaySlot = true;
        return 1;
    }

    // ==================== IMMEDIATE ====================
    private int ExecuteAddi(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) _gprs[rt].Lo = _gprs[rs].Lo + (ulong)imm;
        return 1;
    }

    private int ExecuteAddiu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) _gprs[rt].Lo = _gprs[rs].Lo + (ulong)imm;
        return 1;
    }

    private int ExecuteAndi(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) { _gprs[rt].Lo = _gprs[rs].Lo & imm; _gprs[rt].Hi = 0; }
        return 1;
    }

    private int ExecuteOri(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) _gprs[rt].Lo = _gprs[rs].Lo | imm;
        return 1;
    }

    private int ExecuteXori(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) _gprs[rt].Lo = _gprs[rs].Lo ^ imm;
        return 1;
    }

    private int ExecuteLui(uint opcode)
    {
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) { _gprs[rt].Lo = (ulong)imm << 16; _gprs[rt].Hi = 0; }
        return 1;
    }

    // ==================== LOADS ====================
    private int ExecuteLb(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        byte val = _memory.Read8(addr);
        if (rt != 0) { _gprs[rt].Lo = (ulong)(sbyte)val; _gprs[rt].Hi = ((val & 0x80) != 0) ? ~0UL : 0; }
        return 1;
    }

    private int ExecuteLbu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        if (rt != 0) { _gprs[rt].Lo = _memory.Read8(addr); _gprs[rt].Hi = 0; }
        return 1;
    }

    private int ExecuteLh(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        ushort val = (ushort)(_memory.Read8(addr) | (_memory.Read8(addr + 1) << 8));
        if (rt != 0) { _gprs[rt].Lo = (ulong)(short)val; _gprs[rt].Hi = ((val & 0x8000) != 0) ? ~0UL : 0; }
        return 1;
    }

    private int ExecuteLhu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        if (rt != 0) { _gprs[rt].Lo = (ushort)(_memory.Read8(addr) | (_memory.Read8(addr + 1) << 8)); _gprs[rt].Hi = 0; }
        return 1;
    }

    private int ExecuteLw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        if (rt != 0) _gprs[rt].Lo = _memory.Read32(addr);
        return 1;
    }

    // ==================== STORES ====================
    private int ExecuteSb(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        _memory.Write8(addr, (byte)_gprs[rt].Lo);
        return 1;
    }

    private int ExecuteSh(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        _memory.Write8(addr, (byte)_gprs[rt].Lo);
        _memory.Write8(addr + 1, (byte)(_gprs[rt].Lo >> 8));
        return 1;
    }

    private int ExecuteSw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        _memory.Write32(addr, (uint)_gprs[rt].Lo);
        return 1;
    }

    public Gpr128 GetGpr(int index) => _gprs[index & 0x1F];

    public void SetGpr(int index, Gpr128 value)
    {
        if (index != 0) _gprs[index & 0x1F] = value;
    }

    public void DumpRegisters()
    {
        Console.WriteLine($"PC = 0x{PC:X8}");
        for (int i = 0; i < 8; i++)
        {
            Console.WriteLine($"${i:D2} = {_gprs[i]}   ${i + 8:D2} = {_gprs[i + 8]}   ${i + 16:D2} = {_gprs[i + 16]}   ${i + 24:D2} = {_gprs[i + 24]}");
        }
    }
}
