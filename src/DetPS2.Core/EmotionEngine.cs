using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Emotion Engine (R5900) CPU core.
/// 
/// This is a clean, deterministic interpreter for the PS2's main CPU.
/// We use value types everywhere possible.
/// </summary>
public sealed class EmotionEngine
{
    private readonly SystemMemory _memory;

    public ulong PC { get; set; } = 0xBFC00000;

    [StructLayout(LayoutKind.Sequential)]
    public struct Gpr128
    {
        public ulong Lo;
        public ulong Hi;

        public override string ToString() => $"0x{Hi:X16}_{Lo:X16}";
    }

    private readonly Gpr128[] _gprs = new Gpr128[32];

    // COP0 (simplified for now)
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Step()
    {
        ulong opcodeAddr = PC;
        uint opcode = _memory.Read32(opcodeAddr);
        PC += 4;

        uint primary = (opcode >> 26) & 0x3F;

        return primary switch
        {
            0x00 => ExecuteSpecial(opcode),
            0x01 => ExecuteRegimm(opcode),
            0x02 => ExecuteJ(opcode),
            0x03 => ExecuteJal(opcode),
            0x04 => ExecuteBeq(opcode),
            0x05 => ExecuteBne(opcode),
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
            _ => HandleUnknown(opcode, opcodeAddr)
        };
    }

    private int HandleUnknown(uint opcode, ulong addr)
    {
        Console.WriteLine($"[EE] Unknown opcode 0x{opcode:X8} at 0x{addr:X8}");
        return 1;
    }

    // ==================== SPECIAL ====================
    private int ExecuteSpecial(uint opcode)
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
            0x20 => ExecuteAdd(rd, rs, rt),      // ADD
            0x21 => ExecuteAddu(rd, rs, rt),
            0x23 => ExecuteSubu(rd, rs, rt),
            0x24 => ExecuteAnd(rd, rs, rt),
            0x25 => ExecuteOr(rd, rs, rt),
            0x26 => ExecuteXor(rd, rs, rt),
            0x27 => ExecuteNor(rd, rs, rt),
            0x2A => ExecuteSlt(rd, rs, rt),
            0x2B => ExecuteSltu(rd, rs, rt),
            _ => HandleUnknown(opcode, PC - 4)
        };
    }

    private int ExecuteSll(int rd, int rt, int sa) { _gprs[rd].Lo = _gprs[rt].Lo << sa; _gprs[rd].Hi = 0; return 1; }
    private int ExecuteSrl(int rd, int rt, int sa) { _gprs[rd].Lo = _gprs[rt].Lo >> sa; _gprs[rd].Hi = 0; return 1; }
    private int ExecuteSra(int rd, int rt, int sa) { _gprs[rd].Lo = (ulong)((long)_gprs[rt].Lo >> sa); _gprs[rd].Hi = 0; return 1; }

    private int ExecuteAddu(int rd, int rs, int rt) { _gprs[rd].Lo = _gprs[rs].Lo + _gprs[rt].Lo; return 1; }
    private int ExecuteSubu(int rd, int rs, int rt) { _gprs[rd].Lo = _gprs[rs].Lo - _gprs[rt].Lo; return 1; }
    private int ExecuteAnd(int rd, int rs, int rt) { _gprs[rd].Lo = _gprs[rs].Lo & _gprs[rt].Lo; _gprs[rd].Hi = _gprs[rs].Hi & _gprs[rt].Hi; return 1; }
    private int ExecuteOr(int rd, int rs, int rt)  { _gprs[rd].Lo = _gprs[rs].Lo | _gprs[rt].Lo; _gprs[rd].Hi = _gprs[rs].Hi | _gprs[rt].Hi; return 1; }
    private int ExecuteXor(int rd, int rs, int rt) { _gprs[rd].Lo = _gprs[rs].Lo ^ _gprs[rt].Lo; _gprs[rd].Hi = _gprs[rs].Hi ^ _gprs[rt].Hi; return 1; }
    private int ExecuteNor(int rd, int rs, int rt) { _gprs[rd].Lo = ~(_gprs[rs].Lo | _gprs[rt].Lo); _gprs[rd].Hi = ~(_gprs[rs].Hi | _gprs[rt].Hi); return 1; }
    private int ExecuteSlt(int rd, int rs, int rt) { _gprs[rd].Lo = ((long)_gprs[rs].Lo < (long)_gprs[rt].Lo) ? 1UL : 0; return 1; }
    private int ExecuteSltu(int rd, int rs, int rt) { _gprs[rd].Lo = (_gprs[rs].Lo < _gprs[rt].Lo) ? 1UL : 0; return 1; }

    // ==================== REGIMM ====================
    private int ExecuteRegimm(uint opcode)
    {
        uint rt = (opcode >> 16) & 0x1F;
        return rt switch
        {
            0x00 => ExecuteBltz(opcode),
            0x01 => ExecuteBgez(opcode),
            _ => HandleUnknown(opcode, PC - 4)
        };
    }

    private int ExecuteBltz(uint opcode) { /* TODO: proper delay slot */ return 1; }
    private int ExecuteBgez(uint opcode) { /* TODO: proper delay slot */ return 1; }

    // ==================== JUMPS ====================
    private int ExecuteJ(uint opcode)
    {
        uint target = opcode & 0x03FFFFFF;
        PC = (PC & 0xF0000000) | (target << 2);
        return 1;
    }

    private int ExecuteJal(uint opcode)
    {
        _gprs[31].Lo = PC; // return address (simplified, no delay slot yet)
        uint target = opcode & 0x03FFFFFF;
        PC = (PC & 0xF0000000) | (target << 2);
        return 1;
    }

    // ==================== BRANCHES ====================
    private int ExecuteBeq(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);

        if (_gprs[rs].Lo == _gprs[rt].Lo)
        {
            PC += (ulong)((long)offset << 2) - 4; // -4 because we already added 4
        }
        return 1;
    }

    private int ExecuteBne(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);

        if (_gprs[rs].Lo != _gprs[rt].Lo)
        {
            PC += (ulong)((long)offset << 2) - 4;
        }
        return 1;
    }

    // ==================== IMMEDIATE ARITHMETIC ====================
    private int ExecuteAddi(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        _gprs[rt].Lo = _gprs[rs].Lo + (ulong)imm;
        return 1;
    }

    private int ExecuteAddiu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        _gprs[rt].Lo = _gprs[rs].Lo + (ulong)imm;
        return 1;
    }

    private int ExecuteAndi(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        _gprs[rt].Lo = _gprs[rs].Lo & imm;
        _gprs[rt].Hi = 0;
        return 1;
    }

    private int ExecuteOri(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        _gprs[rt].Lo = _gprs[rs].Lo | imm;
        return 1;
    }

    private int ExecuteXori(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        _gprs[rt].Lo = _gprs[rs].Lo ^ imm;
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

    // ==================== LOADS ====================
    private int ExecuteLb(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        byte val = _memory.Read8(addr);
        _gprs[rt].Lo = (ulong)(sbyte)val;
        _gprs[rt].Hi = (val & 0x80) != 0 ? ~0UL : 0;
        return 1;
    }

    private int ExecuteLbu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        _gprs[rt].Lo = _memory.Read8(addr);
        _gprs[rt].Hi = 0;
        return 1;
    }

    private int ExecuteLh(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        ushort val = (ushort)(_memory.Read8(addr) | (_memory.Read8(addr + 1) << 8));
        _gprs[rt].Lo = (ulong)(short)val;
        _gprs[rt].Hi = (val & 0x8000) != 0 ? ~0UL : 0;
        return 1;
    }

    private int ExecuteLhu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        _gprs[rt].Lo = (ushort)(_memory.Read8(addr) | (_memory.Read8(addr + 1) << 8));
        _gprs[rt].Hi = 0;
        return 1;
    }

    private int ExecuteLw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        ulong addr = _gprs[rs].Lo + (ulong)offset;
        _gprs[rt].Lo = _memory.Read32(addr);
        _gprs[rt].Hi = 0;
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
        if (index != 0)
            _gprs[index & 0x1F] = value;
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
