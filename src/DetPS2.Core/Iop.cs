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

    public static readonly bool TracePc = Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP") == "1";
    public static readonly ulong TracePcLimit =
        ulong.TryParse(Environment.GetEnvironmentVariable("DETPS2_TRACE_IOP_LIMIT"), out var lim) ? lim : 2000;

    private readonly SystemMemory _memory;
    private uint _branchTarget;
    private bool _pendingVectorJump;
    private uint _vectorTarget;

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
        _pendingVectorJump = false;
        _vectorTarget = 0;
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
            uint opcode = _memory.IopRead32(PC);
            if (TracePc && InstructionsExecuted < TracePcLimit)
                Console.Error.WriteLine($"[IOPTRACE] n={InstructionsExecuted} pc=0x{PC:X8} op=0x{opcode:X8}");
            bool tookBranch = ExecuteInstruction(opcode);
            executed++;
            InstructionsExecuted++;

            if (_pendingVectorJump)
            {
                // Real exception entry (SYSCALL/BREAK) — jumps straight to the vector, no
                // delay-slot fetch of its own (the exception replaces normal sequential
                // control flow at this point; whatever follows the faulting instruction in
                // memory is not executed until/unless the handler itself returns there).
                _pendingVectorJump = false;
                PC = _vectorTarget;
            }
            else if (tookBranch)
            {
                // Delay slot
                uint delay = _memory.IopRead32(PC + 4);
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
            case 0x0C: // SYSCALL — real R3000A exception entry (ExcCode 8), not a halt.
                EnterException(8);
                break;
            case 0x0D: // BREAK (ExcCode 9)
                EnterException(9);
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
            case 0x10: // RFE — real R3000A return-from-exception: shift the KUp/IEp pair
                       // (bits 3:2) back down into KUc/IEc (bits 1:0), restoring the mode/
                       // interrupt-enable state the exception handler was entered under. The
                       // R3000A has no EXL bit (that's a later-MIPS/R5900 feature) — it's a
                       // real 3-deep current/previous/old shift-register stack instead.
                Cop0Status = (Cop0Status & ~0xFu) | ((Cop0Status & 0x3Cu) >> 2);
                break;
        }
        return false;
    }

    /// <summary>
    /// Real R3000A exception entry: EPC = faulting PC, Cause.ExcCode set, the KU/IE stack
    /// shifted left (current pair becomes previous, new current pair forced to kernel-mode/
    /// interrupts-disabled), PC redirected to the BEV-selected vector. This is what lets the
    /// real IOP BIOS ROM's own exception dispatcher (present in the same BIOS dump already
    /// used for the EE side — confirmed 0xBFC00000 maps to real BIOS_BASE=0x1FC00000 data,
    /// not a synthesized stub) actually run for real instead of the interpreter just halting
    /// on the first SYSCALL it hits. Simplification: EPC always = PC (no delay-slot/BD-bit
    /// tracking yet, since Step()'s delay-slot handling doesn't expose "is this instruction
    /// itself in a delay slot" to ExecuteInstruction) -- correct for the overwhelmingly common
    /// case of a syscall not in a delay slot, wrong in the rare case it is.
    /// </summary>
    private void EnterException(uint excCode, uint badVAddr = 0)
    {
        bool bev = (Cop0Status & (1u << 22)) != 0;
        Cop0Epc = PC;
        Cop0Cause = (Cop0Cause & ~0x7Cu) | ((excCode & 0x1Fu) << 2);
        if (excCode == 4 || excCode == 5) Cop0BadVAddr = badVAddr; // AdEL/AdES
        Cop0Status = (Cop0Status & ~0x3Fu) | ((Cop0Status & 0xFu) << 2);
        _vectorTarget = bev ? 0xBFC00180u : 0x80000080u;
        _pendingVectorJump = true;
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
        if (rt != 0) _gprs[rt] = _memory.IopRead32(addr);
        return false;
    }

    private bool StoreWord(uint opcode)
    {
        _memory.IopWrite32(EffectiveAddress(opcode), _gprs[Rt(opcode)]);
        return false;
    }

    private bool LoadStore8(uint opcode, bool store, bool signed)
    {
        uint addr = EffectiveAddress(opcode);
        uint rt = Rt(opcode);
        if (store)
        {
            _memory.IopWrite8(addr, (byte)_gprs[rt]);
        }
        else if (rt != 0)
        {
            byte b = _memory.IopRead8(addr);
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
            _memory.IopWrite8(addr, (byte)_gprs[rt]);
            _memory.IopWrite8(addr + 1, (byte)(_gprs[rt] >> 8));
        }
        else if (rt != 0)
        {
            ushort h = (ushort)(_memory.IopRead8(addr) | (_memory.IopRead8(addr + 1) << 8));
            _gprs[rt] = signed ? (uint)(short)h : h;
        }
        return false;
    }

    public void Stop() => Running = false;

    /// <summary>Assemble helper: write words into IOP-visible memory and set PC.</summary>
    public void LoadProgram(uint address, ReadOnlySpan<uint> words)
    {
        for (int i = 0; i < words.Length; i++)
            _memory.IopWrite32(address + (uint)(i * 4), words[i]);
        PC = address;
        Running = true;
        InstructionsExecuted = 0;
    }
}
