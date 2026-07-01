using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Emotion Engine (R5900) - Phase 3 with expanded HLE syscalls.
/// </summary>
public sealed class EmotionEngine
{
    private readonly SystemMemory _memory;

    public ulong PC { get; set; } = 0xBFC00000;
    private bool _branchPending;
    private ulong _pendingBranchTarget;

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

    public bool HleSifInitialized { get; private set; } = false;

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
        _branchPending = false;
        HleSifInitialized = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Step()
    {
        if (_branchPending)
        {
            PC = _pendingBranchTarget;
            _branchPending = false;
        }

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
            0x0A => ExecuteSlti(opcode),
            0x0B => ExecuteSltiu(opcode),
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

        if (!_branchPending)
            PC = nextPC;

        return cycles;
    }

    private int HandleUnknown(uint opcode, ulong addr)
    {
        Console.WriteLine($"[EE] Unknown opcode 0x{opcode:X8} at 0x{addr:X8}");
        return 1;
    }

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
            0x08 => ExecuteJr(rs, ref nextPC),
            0x09 => ExecuteJalr(rd, rs, ref nextPC),
            0x0C => ExecuteSyscall(opcode, ref nextPC),
            0x18 => ExecuteMult(rs, rt),
            0x19 => ExecuteMultu(rs, rt),
            0x1A => ExecuteDiv(rs, rt),
            0x1B => ExecuteDivu(rs, rt),
            0x10 => ExecuteMfhi(rd),
            0x12 => ExecuteMflo(rd),
            0x11 => ExecuteMthi(rs),
            0x13 => ExecuteMtlo(rs),
            0x20 => ExecuteAddu(rd, rs, rt),
            0x21 => ExecuteAddu(rd, rs, rt),
            0x23 => ExecuteSubu(rd, rs, rt),
            0x24 => ExecuteAnd(rd, rs, rt),
            0x25 => ExecuteOr(rd, rs, rt),
            0x26 => ExecuteXor(rd, rs, rt),
            0x27 => ExecuteNor(rd, rs, rt),
            0x2A => ExecuteSlt(rd, rs, rt),
            0x2B => ExecuteSltu(rd, rs, rt),
            _ => HandleUnknown(opcode, PC)
        };
    }

    // ==================== Expanded HLE Syscalls (Phase 3) ====================
    private int ExecuteSyscall(uint opcode, ref ulong nextPC)
    {
        COP0_EPC = PC;
        COP0_Cause = (8 << 2);

        uint syscallNumber = _gprs[4].Lo & 0xFFFF;

        switch (syscallNumber)
        {
            case 0x01:
                HleSifInitialized = true;
                Console.WriteLine("[EE HLE] sceSifInit called");
                _gprs[2].Lo = 0;
                break;

            case 0x02: // sceSifSetDma (simplified)
                Console.WriteLine("[EE HLE] sceSifSetDma called");
                _gprs[2].Lo = 1; // Success
                break;

            case 0x03: // sceSifDmaStat
                _gprs[2].Lo = 0; // Done
                break;

            case 0x04: // sceSifSendCmd
                Console.WriteLine("[EE HLE] sceSifSendCmd called");
                _gprs[2].Lo = 0;
                break;

            default:
                Console.WriteLine($"[EE HLE] Unknown syscall 0x{syscallNumber:X}");
                _gprs[2].Lo = -1; // Error
                break;
        }

        nextPC = 0x80000180;
        _branchPending = false;
        return 1;
    }

    // ... (rest of the file remains the same as previous version)
    private int ExecuteMult(int rs, int rt) { /* ... */ return 1; }
    private int ExecuteMultu(int rs, int rt) { /* ... */ return 1; }
    private int ExecuteDiv(int rs, int rt) { /* ... */ return 1; }
    private int ExecuteDivu(int rs, int rt) { /* ... */ return 1; }
    private int ExecuteMfhi(int rd) { if (rd != 0) _gprs[rd].Lo = HI; return 1; }
    private int ExecuteMflo(int rd) { if (rd != 0) _gprs[rd].Lo = LO; return 1; }
    private int ExecuteMthi(int rs) { HI = _gprs[rs].Lo; return 1; }
    private int ExecuteMtlo(int rs) { LO = _gprs[rs].Lo; return 1; }
    private int ExecuteSll(int rd, int rt, int sa) { if (rd != 0) { _gprs[rd].Lo = _gprs[rt].Lo << sa; _gprs[rd].Hi = 0; } return 1; }
    private int ExecuteSrl(int rd, int rt, int sa) { if (rd != 0) { _gprs[rd].Lo = _gprs[rt].Lo >> sa; _gprs[rd].Hi = 0; } return 1; }
    private int ExecuteSra(int rd, int rt, int sa) { if (rd != 0) { _gprs[rd].Lo = (ulong)((long)_gprs[rt].Lo >> sa); _gprs[rd].Hi = 0; } return 1; }
    private int ExecuteJr(int rs, ref ulong nextPC) { _pendingBranchTarget = _gprs[rs].Lo; _branchPending = true; return 1; }
    private int ExecuteJalr(int rd, int rs, ref ulong nextPC) { if (rd != 0) _gprs[rd].Lo = PC + 8; _pendingBranchTarget = _gprs[rs].Lo; _branchPending = true; return 1; }
    private int ExecuteAddu(int rd, int rs, int rt) { if (rd != 0) _gprs[rd].Lo = _gprs[rs].Lo + _gprs[rt].Lo; return 1; }
    private int ExecuteSubu(int rd, int rs, int rt) { if (rd != 0) _gprs[rd].Lo = _gprs[rs].Lo - _gprs[rt].Lo; return 1; }
    private int ExecuteAnd(int rd, int rs, int rt) { if (rd != 0) { _gprs[rd].Lo = _gprs[rs].Lo & _gprs[rt].Lo; _gprs[rd].Hi = _gprs[rs].Hi & _gprs[rt].Hi; } return 1; }
    private int ExecuteOr(int rd, int rs, int rt) { if (rd != 0) { _gprs[rd].Lo = _gprs[rs].Lo | _gprs[rt].Lo; _gprs[rd].Hi = _gprs[rs].Hi | _gprs[rt].Hi; } return 1; }
    private int ExecuteXor(int rd, int rs, int rt) { if (rd != 0) { _gprs[rd].Lo = _gprs[rs].Lo ^ _gprs[rt].Lo; _gprs[rd].Hi = _gprs[rs].Hi ^ _gprs[rt].Hi; } return 1; }
    private int ExecuteNor(int rd, int rs, int rt) { if (rd != 0) { _gprs[rd].Lo = ~(_gprs[rs].Lo | _gprs[rt].Lo); _gprs[rd].Hi = ~(_gprs[rs].Hi | _gprs[rt].Hi); } return 1; }
    private int ExecuteSlt(int rd, int rs, int rt) { if (rd != 0) _gprs[rd].Lo = ((long)_gprs[rs].Lo < (long)_gprs[rt].Lo) ? 1UL : 0; return 1; }
    private int ExecuteSltu(int rd, int rs, int rt) { if (rd != 0) _gprs[rd].Lo = (_gprs[rs].Lo < _gprs[rt].Lo) ? 1UL : 0; return 1; }
    private int ExecuteRegimm(uint opcode, ref ulong nextPC) { /* ... */ return 1; }
    private int ExecuteJ(uint opcode, ref ulong nextPC) { /* ... */ return 1; }
    private int ExecuteJal(uint opcode, ref ulong nextPC) { /* ... */ return 1; }
    private int ExecuteBeq(uint opcode, ref ulong nextPC) => ScheduleBranchIf(opcode, _gprs[(opcode >> 21) & 0x1F].Lo == _gprs[(opcode >> 16) & 0x1F].Lo, ref nextPC);
    private int ExecuteBne(uint opcode, ref ulong nextPC) => ScheduleBranchIf(opcode, _gprs[(opcode >> 21) & 0x1F].Lo != _gprs[(opcode >> 16) & 0x1F].Lo, ref nextPC);
    private int ExecuteBlez(uint opcode, ref ulong nextPC) => ScheduleBranchIf(opcode, (long)_gprs[(opcode >> 21) & 0x1F].Lo <= 0, ref nextPC);
    private int ExecuteBgtz(uint opcode, ref ulong nextPC) => ScheduleBranchIf(opcode, (long)_gprs[(opcode >> 21) & 0x1F].Lo > 0, ref nextPC);
    private int ScheduleBranchIf(uint opcode, bool condition, ref ulong nextPC) { /* ... */ return 1; }
    private int ExecuteAddi(uint opcode) { /* ... */ return 1; }
    private int ExecuteAddiu(uint opcode) { /* ... */ return 1; }
    private int ExecuteSlti(uint opcode) { /* ... */ return 1; }
    private int ExecuteSltiu(uint opcode) { /* ... */ return 1; }
    private int ExecuteAndi(uint opcode) { /* ... */ return 1; }
    private int ExecuteOri(uint opcode) { /* ... */ return 1; }
    private int ExecuteXori(uint opcode) { /* ... */ return 1; }
    private int ExecuteLui(uint opcode) { /* ... */ return 1; }
    private int ExecuteLb(uint opcode) { /* ... */ return 1; }
    private int ExecuteLbu(uint opcode) { /* ... */ return 1; }
    private int ExecuteLh(uint opcode) { /* ... */ return 1; }
    private int ExecuteLhu(uint opcode) { /* ... */ return 1; }
    private int ExecuteLw(uint opcode) { /* ... */ return 1; }
    private int ExecuteSb(uint opcode) { /* ... */ return 1; }
    private int ExecuteSh(uint opcode) { /* ... */ return 1; }
    private int ExecuteSw(uint opcode) { /* ... */ return 1; }

    public Gpr128 GetGpr(int index) => _gprs[index & 0x1F];
    public void SetGpr(int index, Gpr128 value) { if (index != 0) _gprs[index & 0x1F] = value; }
    public void DumpRegisters() { /* ... */ }
}
