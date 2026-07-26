using System;

namespace DetPS2.Core;

/// <summary>
/// IOP R3000A interpreter (Phase 8).
/// Delay slots, LO/HI, expanded loads/stores, minimal COP0, deterministic stepping.
/// </summary>
public sealed class Iop : ISchedulable
{
    public Intc Intc { get; }

    public uint PC { get; set; } = 0xBFC00000;
    private readonly uint[] _gprs = new uint[32];

    public uint LO { get; private set; }
    public uint HI { get; private set; }

    public uint Cop0Status { get; set; }
    public uint Cop0Cause { get; set; }
    public uint Cop0Epc { get; set; }
    public uint Cop0BadVAddr { get; set; }

    public uint SifMbxFromEE { get; private set; }
    public uint SifMbxToEE { get; private set; }

    public bool Running { get; private set; } = true;
    public ulong InstructionsExecuted { get; private set; }

    private readonly SystemMemory _memory;
    private uint _branchTarget;

    public Iop(Intc intc, SystemMemory memory)
    {
        Intc = intc ?? throw new ArgumentNullException(nameof(intc));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void Reset()
    {
        PC = 0xBFC00000;
        Array.Clear(_gprs);
        LO = HI = 0;
        Cop0Status = 0;
        Cop0Cause = 0;
        Cop0Epc = 0;
        Cop0BadVAddr = 0;
        SifMbxFromEE = 0;
        SifMbxToEE = 0;
        Running = true;
        InstructionsExecuted = 0;
        _branchTarget = 0;
    }

    public uint GetGpr(int index) => _gprs[index & 0x1F];
    public void SetGpr(int index, uint value)
    {
        if ((index & 0x1F) != 0) _gprs[index & 0x1F] = value;
    }

    public void WriteSifMailboxFromEE(uint value)
    {
        SifMbxFromEE = value;
        SifMbxToEE = ~value;
        Intc.Raise(Intc.InterruptSource.Sif);
    }

    public uint ReadSifMailboxToEE() => SifMbxToEE;

    public int Step(ulong maxCycles)
    {
        if (!Running || maxCycles == 0) return 0;

        int executed = 0;
        while ((ulong)executed < maxCycles && Running)
        {
            uint opcode = _memory.Read32(PC);
            bool tookBranch = ExecuteInstruction(opcode);
            executed++;
            InstructionsExecuted++;

            if (tookBranch)
            {
                // Delay slot
                uint delay = _memory.Read32(PC + 4);
                ExecuteInstruction(delay);
                executed++;
                InstructionsExecuted++;
                PC = _branchTarget;
            }
            else
            {
                PC += 4;
            }
        }
        return executed;
    }

    private bool ExecuteInstruction(uint opcode)
    {
        uint primary = (opcode >> 26) & 0x3F;

        return primary switch
        {
            0x00 => ExecuteSpecial(opcode),
            0x01 => ExecuteRegimm(opcode),
            0x02 => BranchTo(((PC + 4) & 0xF0000000) | ((opcode & 0x03FFFFFF) << 2)),
            0x03 => Jal(opcode),
            0x04 => BranchIf(_gprs[Rs(opcode)] == _gprs[Rt(opcode)], opcode),
            0x05 => BranchIf(_gprs[Rs(opcode)] != _gprs[Rt(opcode)], opcode),
            0x06 => BranchIf((int)_gprs[Rs(opcode)] <= 0, opcode), // BLEZ
            0x07 => BranchIf((int)_gprs[Rs(opcode)] > 0, opcode),  // BGTZ
            0x08 => ImmArith(opcode, (a, i) => a + (uint)i),       // ADDI
            0x09 => ImmArith(opcode, (a, i) => a + (uint)i),       // ADDIU
            0x0A => ImmArith(opcode, (a, i) => (uint)((int)a < i ? 1 : 0)), // SLTI
            0x0B => ImmArith(opcode, (a, i) => a < (uint)i ? 1u : 0u),      // SLTIU
            0x0C => ImmLogic(opcode, (a, i) => a & i),             // ANDI
            0x0D => ImmLogic(opcode, (a, i) => a | i),             // ORI
            0x0E => ImmLogic(opcode, (a, i) => a ^ i),             // XORI
            0x0F => Lui(opcode),
            0x10 => ExecuteCop0(opcode),
            0x20 => LoadStore8(opcode, store: false, signed: true),   // LB
            0x21 => LoadStore16(opcode, store: false, signed: true),  // LH
            0x23 => LoadWord(opcode),                                 // LW
            0x24 => LoadStore8(opcode, store: false, signed: false),  // LBU
            0x25 => LoadStore16(opcode, store: false, signed: false), // LHU
            0x28 => LoadStore8(opcode, store: true, signed: false),   // SB
            0x29 => LoadStore16(opcode, store: true, signed: false),  // SH
            0x2B => StoreWord(opcode),                                // SW
            _ => false
        };
    }

    private static uint Rs(uint op) => (op >> 21) & 0x1F;
    private static uint Rt(uint op) => (op >> 16) & 0x1F;
    private static uint Rd(uint op) => (op >> 11) & 0x1F;
    private static uint Sa(uint op) => (op >> 6) & 0x1F;
    private static short Imm16(uint op) => (short)(op & 0xFFFF);
    private static ushort ImmU16(uint op) => (ushort)(op & 0xFFFF);

    private bool BranchTo(uint target)
    {
        _branchTarget = target;
        return true;
    }

    private bool BranchIf(bool cond, uint opcode)
    {
        if (!cond) return false;
        int off = Imm16(opcode) << 2;
        return BranchTo((uint)(PC + 4 + off));
    }

    private bool Jal(uint opcode)
    {
        _gprs[31] = PC + 8;
        return BranchTo(((PC + 4) & 0xF0000000) | ((opcode & 0x03FFFFFF) << 2));
    }

    private bool ImmArith(uint opcode, Func<uint, int, uint> fn)
    {
        uint rt = Rt(opcode);
        if (rt != 0) _gprs[rt] = fn(_gprs[Rs(opcode)], Imm16(opcode));
        return false;
    }

    private bool ImmLogic(uint opcode, Func<uint, uint, uint> fn)
    {
        uint rt = Rt(opcode);
        if (rt != 0) _gprs[rt] = fn(_gprs[Rs(opcode)], ImmU16(opcode));
        return false;
    }

    private bool Lui(uint opcode)
    {
        uint rt = Rt(opcode);
        if (rt != 0) _gprs[rt] = (uint)ImmU16(opcode) << 16;
        return false;
    }

    private bool ExecuteSpecial(uint opcode)
    {
        uint fn = opcode & 0x3F;
        uint rs = Rs(opcode), rt = Rt(opcode), rd = Rd(opcode);
        int sa = (int)Sa(opcode);

        switch (fn)
        {
            case 0x00: if (rd != 0) _gprs[rd] = _gprs[rt] << sa; break; // SLL
            case 0x02: if (rd != 0) _gprs[rd] = _gprs[rt] >> sa; break; // SRL
            case 0x03: if (rd != 0) _gprs[rd] = (uint)((int)_gprs[rt] >> sa); break; // SRA
            case 0x04: if (rd != 0) _gprs[rd] = _gprs[rt] << (int)(_gprs[rs] & 0x1F); break; // SLLV
            case 0x06: if (rd != 0) _gprs[rd] = _gprs[rt] >> (int)(_gprs[rs] & 0x1F); break; // SRLV
            case 0x07: if (rd != 0) _gprs[rd] = (uint)((int)_gprs[rt] >> (int)(_gprs[rs] & 0x1F)); break; // SRAV
            case 0x08: return BranchTo(_gprs[rs]); // JR
            case 0x09: // JALR
                uint ret = PC + 8;
                uint target = _gprs[rs];
                if (rd != 0) _gprs[rd] = ret;
                return BranchTo(target);
            case 0x0C: // SYSCALL — halt for tests / HLE hook
                Cop0Cause |= 1u << 8;
                Running = false;
                break;
            case 0x0D: // BREAK
                Running = false;
                break;
            case 0x10: if (rd != 0) _gprs[rd] = HI; break; // MFHI
            case 0x11: HI = _gprs[rs]; break; // MTHI
            case 0x12: if (rd != 0) _gprs[rd] = LO; break; // MFLO
            case 0x13: LO = _gprs[rs]; break; // MTLO
            case 0x18: // MULT
            {
                long r = (long)(int)_gprs[rs] * (int)_gprs[rt];
                LO = (uint)r;
                HI = (uint)(r >> 32);
                break;
            }
            case 0x19: // MULTU
            {
                ulong r = (ulong)_gprs[rs] * _gprs[rt];
                LO = (uint)r;
                HI = (uint)(r >> 32);
                break;
            }
            case 0x1A: // DIV
                if (_gprs[rt] != 0)
                {
                    LO = (uint)((int)_gprs[rs] / (int)_gprs[rt]);
                    HI = (uint)((int)_gprs[rs] % (int)_gprs[rt]);
                }
                break;
            case 0x1B: // DIVU
                if (_gprs[rt] != 0)
                {
                    LO = _gprs[rs] / _gprs[rt];
                    HI = _gprs[rs] % _gprs[rt];
                }
                break;
            case 0x20:
            case 0x21: if (rd != 0) _gprs[rd] = _gprs[rs] + _gprs[rt]; break;
            case 0x22:
            case 0x23: if (rd != 0) _gprs[rd] = _gprs[rs] - _gprs[rt]; break;
            case 0x24: if (rd != 0) _gprs[rd] = _gprs[rs] & _gprs[rt]; break;
            case 0x25: if (rd != 0) _gprs[rd] = _gprs[rs] | _gprs[rt]; break;
            case 0x26: if (rd != 0) _gprs[rd] = _gprs[rs] ^ _gprs[rt]; break;
            case 0x27: if (rd != 0) _gprs[rd] = ~(_gprs[rs] | _gprs[rt]); break; // NOR
            case 0x2A: if (rd != 0) _gprs[rd] = (int)_gprs[rs] < (int)_gprs[rt] ? 1u : 0u; break;
            case 0x2B: if (rd != 0) _gprs[rd] = _gprs[rs] < _gprs[rt] ? 1u : 0u; break;
        }
        return false;
    }

    private bool ExecuteRegimm(uint opcode)
    {
        uint rt = Rt(opcode);
        int val = (int)_gprs[Rs(opcode)];
        bool take = rt switch
        {
            0x00 => val < 0,  // BLTZ
            0x01 => val >= 0, // BGEZ
            0x10 => val < 0,  // BLTZAL
            0x11 => val >= 0, // BGEZAL
            _ => false
        };
        if ((rt == 0x10 || rt == 0x11) && take)
            _gprs[31] = PC + 8;
        return take && BranchIf(true, opcode);
    }

    private bool ExecuteCop0(uint opcode)
    {
        uint rs = Rs(opcode);
        uint rt = Rt(opcode);
        uint rd = Rd(opcode);
        switch (rs)
        {
            case 0x00: // MFC0
                if (rt != 0) _gprs[rt] = ReadCop0(rd);
                break;
            case 0x04: // MTC0
                WriteCop0(rd, _gprs[rt]);
                break;
            case 0x10: // RFE-ish — clear EXL in status
                Cop0Status &= ~0x2u;
                break;
        }
        return false;
    }

    private uint ReadCop0(uint reg) => reg switch
    {
        8 => Cop0BadVAddr,
        12 => Cop0Status,
        13 => Cop0Cause,
        14 => Cop0Epc,
        _ => 0
    };

    private void WriteCop0(uint reg, uint value)
    {
        switch (reg)
        {
            case 12: Cop0Status = value; break;
            case 13: Cop0Cause = value; break;
            case 14: Cop0Epc = value; break;
        }
    }

    private uint EffectiveAddress(uint opcode) => _gprs[Rs(opcode)] + (uint)Imm16(opcode);

    private bool LoadWord(uint opcode)
    {
        uint rt = Rt(opcode);
        uint addr = EffectiveAddress(opcode);
        if (rt != 0) _gprs[rt] = _memory.Read32(addr);
        return false;
    }

    private bool StoreWord(uint opcode)
    {
        _memory.Write32(EffectiveAddress(opcode), _gprs[Rt(opcode)]);
        return false;
    }

    private bool LoadStore8(uint opcode, bool store, bool signed)
    {
        uint addr = EffectiveAddress(opcode);
        uint rt = Rt(opcode);
        if (store)
        {
            _memory.Write8(addr, (byte)_gprs[rt]);
        }
        else if (rt != 0)
        {
            byte b = _memory.Read8(addr);
            _gprs[rt] = signed ? (uint)(sbyte)b : b;
        }
        return false;
    }

    private bool LoadStore16(uint opcode, bool store, bool signed)
    {
        uint addr = EffectiveAddress(opcode);
        uint rt = Rt(opcode);
        if (store)
        {
            _memory.Write8(addr, (byte)_gprs[rt]);
            _memory.Write8(addr + 1, (byte)(_gprs[rt] >> 8));
        }
        else if (rt != 0)
        {
            ushort h = (ushort)(_memory.Read8(addr) | (_memory.Read8(addr + 1) << 8));
            _gprs[rt] = signed ? (uint)(short)h : h;
        }
        return false;
    }

    public void Stop() => Running = false;

    /// <summary>Assemble helper: write words into IOP-visible memory and set PC.</summary>
    public void LoadProgram(uint address, ReadOnlySpan<uint> words)
    {
        for (int i = 0; i < words.Length; i++)
            _memory.Write32(address + (uint)(i * 4), words[i]);
        PC = address;
        Running = true;
        InstructionsExecuted = 0;
    }
}
