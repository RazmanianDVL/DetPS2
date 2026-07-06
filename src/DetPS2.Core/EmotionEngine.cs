using System;
using System.Runtime.CompilerServices;

namespace DetPS2.Core;

/// <summary>
/// Emotion Engine (R5900) - MIPS III + PS2 extensions.
/// 
/// Milestone 4: Basic COP2 integration with VU0.
/// 
/// This is currently the highest priority task.
/// We now support the most common COP2 data movement instructions so the EE can
/// communicate with VU0. Full VU macro instruction execution comes later.
/// </summary>
public sealed class EmotionEngine : ISchedulable
{
    private readonly SystemMemory _memory;
    private Vu0? _vu0;

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

    /// <summary>
    /// Connects this Emotion Engine to VU0 so COP2 instructions can access it.
    /// Call this from Ps2System after construction.
    /// </summary>
    public void SetVu0(Vu0 vu0)
    {
        _vu0 = vu0 ?? throw new ArgumentNullException(nameof(vu0));
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

            case 0x12: return ExecuteCop2(opcode);

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
            case 0x00: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo << (int)sa }); break;
            case 0x02: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo >> (int)sa }); break;
            case 0x03: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = (ulong)((long)GetGpr(rt).Lo >> (int)sa) }); break;

            case 0x04: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo << (int)(GetGpr(rs).Lo & 0x1F) }); break; // SLLV
            case 0x06: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = GetGpr(rt).Lo >> (int)(GetGpr(rs).Lo & 0x1F) }); break; // SRLV
            case 0x07: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = (ulong)((long)GetGpr(rt).Lo >> (int)(GetGpr(rs).Lo & 0x1F)) }); break; // SRAV

            case 0x08: _delaySlotTarget = GetGpr(rs).Lo; return true; // JR
            case 0x09: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = PC + 8 }); _delaySlotTarget = GetGpr(rs).Lo; return true; // JALR

            case 0x0A: if (GetGpr(rt).Lo == 0 && rd != 0) SetGpr(rd, GetGpr(rs)); break; // MOVZ
            case 0x0B: if (GetGpr(rt).Lo != 0 && rd != 0) SetGpr(rd, GetGpr(rs)); break; // MOVN

            case 0x0C: HandleSyscall(opcode); break;
            case 0x0D: break;

            case 0x10: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = HI }); break; // MFHI
            case 0x11: HI = GetGpr(rs).Lo; break; // MTHI
            case 0x12: if (rd != 0) SetGpr(rd, new Gpr128 { Lo = LO }); break; // MFLO
            case 0x13: LO = GetGpr(rs).Lo; break; // MTLO

            case 0x18: case 0x19:
                {
                    long a = (long)GetGpr(rs).Lo;
                    long b = (long)GetGpr(rt).Lo;
                    long res = a * b;
                    LO = (ulong)(res & 0xFFFFFFFF);
                    HI = (ulong)((res >> 32) & 0xFFFFFFFF);
                }
                break;

            case 0x1A: case 0x1B:
                {
                    long a = (long)GetGpr(rs).Lo;
                    long b = (long)GetGpr(rt).Lo;
                    if (b != 0) { LO = (ulong)(a / b); HI = (ulong)(a % b); }
                }
                break;

            case 0x20: case 0x21: SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo + GetGpr(rt).Lo }); break;
            case 0x23: SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo - GetGpr(rt).Lo }); break;
            case 0x24: SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo & GetGpr(rt).Lo }); break;
            case 0x25: SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo | GetGpr(rt).Lo }); break;
            case 0x26: SetGpr(rd, new Gpr128 { Lo = GetGpr(rs).Lo ^ GetGpr(rt).Lo }); break;
            case 0x2A: SetGpr(rd, new Gpr128 { Lo = ((long)GetGpr(rs).Lo < (long)GetGpr(rt).Lo) ? 1UL : 0UL }); break;
            case 0x2B: SetGpr(rd, new Gpr128 { Lo = (GetGpr(rs).Lo < GetGpr(rt).Lo) ? 1UL : 0UL }); break;
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

        if (rt == 0 && (long)GetGpr(rs).Lo < 0) { _delaySlotTarget = target; return true; }
        if (rt == 1 && (long)GetGpr(rs).Lo >= 0) { _delaySlotTarget = target; return true; }
        return false;
    }

    // ==================== Jumps ====================
    private bool ExecuteJ(uint opcode) { uint t = opcode & 0x03FFFFFF; _delaySlotTarget = (PC & 0xF0000000UL) | (t << 2); return true; }
    private bool ExecuteJal(uint opcode) { SetGpr(31, new Gpr128 { Lo = PC + 8 }); uint t = opcode & 0x03FFFFFF; _delaySlotTarget = (PC & 0xF0000000UL) | (t << 2); return true; }

    // ==================== Branches ====================
    private bool ExecuteBeq(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        if (GetGpr(rs).Lo == GetGpr(rt).Lo) { _delaySlotTarget = PC + 4 + (ulong)((int)off << 2); return true; }
        return false;
    }

    private bool ExecuteBne(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        if (GetGpr(rs).Lo != GetGpr(rt).Lo) { _delaySlotTarget = PC + 4 + (ulong)((int)off << 2); return true; }
        return false;
    }

    private bool ExecuteBlez(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        if ((long)GetGpr(rs).Lo <= 0) { _delaySlotTarget = PC + 4 + (ulong)((int)off << 2); return true; }
        return false;
    }

    private bool ExecuteBgtz(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        if ((long)GetGpr(rs).Lo > 0) { _delaySlotTarget = PC + 4 + (ulong)((int)off << 2); return true; }
        return false;
    }

    // ==================== Immediate ====================
    private void ExecuteAddi(uint opcode) => ExecuteAddiu(opcode);

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
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (ulong)(sbyte)_memory.Read8(addr) });
    }

    private void ExecuteLbu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = _memory.Read8(addr) });
    }

    private void ExecuteLh(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        short val = (short)(_memory.Read8(addr) | (_memory.Read8(addr + 1) << 8));
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = (ulong)val });
    }

    private void ExecuteLhu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        ushort val = (ushort)(_memory.Read8(addr) | (_memory.Read8(addr + 1) << 8));
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = val });
    }

    private void ExecuteLw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        if (rt != 0) SetGpr(rt, new Gpr128 { Lo = _memory.Read32(addr) });
    }

    private void ExecuteSb(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        _memory.Write8(GetGpr(rs).Lo + (ulong)off, (byte)GetGpr(rt).Lo);
    }

    private void ExecuteSh(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        ulong addr = GetGpr(rs).Lo + (ulong)off;
        ushort v = (ushort)GetGpr(rt).Lo;
        _memory.Write8(addr, (byte)v);
        _memory.Write8(addr + 1, (byte)(v >> 8));
    }

    private void ExecuteSw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short off = (short)(opcode & 0xFFFF);
        _memory.Write32(GetGpr(rs).Lo + (ulong)off, (uint)GetGpr(rt).Lo);
    }

    // ==================== COP2 (VU0 communication) - Milestone 4 ====================
    private bool ExecuteCop2(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;   // often used as sub-opcode
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;

        // Common COP2 move instructions
        switch (rs)
        {
            case 0x00: // CFC2 - Move from COP2 control register
                // For now we treat it as a no-op or simple move from a placeholder
                if (rt != 0) SetGpr(rt, new Gpr128 { Lo = 0 });
                break;

            case 0x02: // CTC2 - Move to COP2 control register
                // Placeholder - real implementation will update VU0 control state
                break;

            case 0x04: // QMFC2 - Quadword Move From COP2 (VU0 register -> EE GPR)
                if (_vu0 != null && rt < 32)
                {
                    // In real hardware this moves a full 128-bit VU register.
                    // For this milestone we move the .Lo part and leave .Hi as 0.
                    var vuReg = _vu0.GetVfRegister(rd); // We will add this helper to Vu0
                    SetGpr(rt, new Gpr128 { Lo = (ulong)BitConverter.SingleToInt32Bits(vuReg.X) });
                }
                break;

            case 0x06: // QMTC2 - Quadword Move To COP2 (EE GPR -> VU0 register)
                if (_vu0 != null && rd < 32)
                {
                    Gpr128 gpr = GetGpr(rt);
                    float val = BitConverter.Int32BitsToSingle((int)gpr.Lo);
                    _vu0.SetVfRegister(rd, new VectorUnit.VuReg128 { X = val, Y = 0, Z = 0, W = 1 });
                }
                break;

            default:
                // Many other COP2 instructions are VU macro ops (VADD, VMUL, etc.)
                // These will be expanded when we have full VU execution integration.
                break;
        }

        return false;
    }

    private void HandleSyscall(uint opcode)
    {
        // TODO: Add minimal HLE syscall handling here
    }
}