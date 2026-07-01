using System;

namespace DetPS2.Core;

/// <summary>
/// Input/Output Processor (IOP) - Phase 3
/// Further expanded R3000A interpreter.
/// </summary>
public sealed class Iop
{
    public Intc Intc { get; }

    public uint PC { get; set; } = 0xBFC00000;
    private readonly uint[] _gprs = new uint[32];

    public uint SifMbxFromEE { get; private set; }
    public uint SifMbxToEE { get; private set; }

    public bool Running { get; private set; } = true;

    private readonly SystemMemory _memory;

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
        SifMbxFromEE = 0;
        SifMbxToEE = 0;
        Running = true;
    }

    public void WriteSifMailboxFromEE(uint value)
    {
        SifMbxFromEE = value;
        SifMbxToEE = ~value;
        Console.WriteLine($"[IOP] SIF from EE: 0x{value:X8}");
    }

    public uint ReadSifMailboxToEE() => SifMbxToEE;

    public void Step(ulong cycles)
    {
        if (!Running) return;

        for (int i = 0; i < 16 && Running; i++) // Increased execution rate
        {
            uint opcode = _memory.Read32(PC);
            ExecuteInstruction(opcode);
            PC += 4;
        }
    }

    private void ExecuteInstruction(uint opcode)
    {
        uint primary = (opcode >> 26) & 0x3F;

        switch (primary)
        {
            case 0x00: ExecuteSpecial(opcode); break;
            case 0x01: ExecuteRegimm(opcode); break;
            case 0x02: ExecuteJ(opcode); break;
            case 0x03: ExecuteJal(opcode); break;
            case 0x04: ExecuteBeq(opcode); break;
            case 0x05: ExecuteBne(opcode); break;
            case 0x08: ExecuteAddi(opcode); break;
            case 0x09: ExecuteAddiu(opcode); break;
            case 0x0D: ExecuteOri(opcode); break;
            case 0x0F: ExecuteLui(opcode); break;
            case 0x23: ExecuteLw(opcode); break;
            case 0x2B: ExecuteSw(opcode); break;
            default: break;
        }
    }

    private void ExecuteSpecial(uint opcode)
    {
        uint function = opcode & 0x3F;
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;
        uint sa = (opcode >> 6) & 0x1F;

        switch (function)
        {
            case 0x00: if (rd != 0) _gprs[rd] = _gprs[rt] << sa; break;           // SLL
            case 0x02: if (rd != 0) _gprs[rd] = _gprs[rt] >> sa; break;           // SRL
            case 0x03: if (rd != 0) _gprs[rd] = (uint)((int)_gprs[rt] >> sa); break; // SRA
            case 0x08: PC = _gprs[rs] - 4; break;                                 // JR
            case 0x18: /* MULT - simplified */ break;
            case 0x20:
            case 0x21: if (rd != 0) _gprs[rd] = _gprs[rs] + _gprs[rt]; break; // ADD/ADDU
            case 0x23: if (rd != 0) _gprs[rd] = _gprs[rs] - _gprs[rt]; break;      // SUBU
            case 0x24: if (rd != 0) _gprs[rd] = _gprs[rs] & _gprs[rt]; break;      // AND
            case 0x25: if (rd != 0) _gprs[rd] = _gprs[rs] | _gprs[rt]; break;      // OR
            case 0x26: if (rd != 0) _gprs[rd] = _gprs[rs] ^ _gprs[rt]; break;      // XOR
            case 0x2A: if (rd != 0) _gprs[rd] = ((int)_gprs[rs] < (int)_gprs[rt]) ? 1u : 0; break; // SLT
            case 0x2B: if (rd != 0) _gprs[rd] = (_gprs[rs] < _gprs[rt]) ? 1u : 0; break; // SLTU
        }
    }

    private void ExecuteRegimm(uint opcode)
    {
        uint rt = (opcode >> 16) & 0x1F;
        int val = (int)_gprs[(opcode >> 21) & 0x1F];
        short offset = (short)(opcode & 0xFFFF);

        bool take = false;
        if (rt == 0x00) take = val < 0;   // BLTZ
        if (rt == 0x01) take = val >= 0;  // BGEZ

        if (take)
        {
            PC += (uint)((int)offset << 2);
            PC -= 4;
        }
    }

    private void ExecuteJ(uint opcode)
    {
        uint target = opcode & 0x03FFFFFF;
        PC = (PC & 0xF0000000) | (target << 2);
        PC -= 4;
    }

    private void ExecuteJal(uint opcode)
    {
        _gprs[31] = PC + 8;
        uint target = opcode & 0x03FFFFFF;
        PC = (PC & 0xF0000000) | (target << 2);
        PC -= 4;
    }

    private void ExecuteBeq(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);

        if (_gprs[rs] == _gprs[rt])
        {
            PC += (uint)((int)offset << 2);
            PC -= 4;
        }
    }

    private void ExecuteBne(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);

        if (_gprs[rs] != _gprs[rt])
        {
            PC += (uint)((int)offset << 2);
            PC -= 4;
        }
    }

    private void ExecuteAddi(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) _gprs[rt] = _gprs[rs] + (uint)imm;
    }

    private void ExecuteAddiu(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short imm = (short)(opcode & 0xFFFF);
        if (rt != 0) _gprs[rt] = _gprs[rs] + (uint)imm;
    }

    private void ExecuteOri(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) _gprs[rt] = _gprs[rs] | imm;
    }

    private void ExecuteLui(uint opcode)
    {
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) _gprs[rt] = (uint)imm << 16;
    }

    private void ExecuteLw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        uint addr = _gprs[rs] + (uint)offset;
        if (rt != 0) _gprs[rt] = _memory.Read32(addr);
    }

    private void ExecuteSw(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        short offset = (short)(opcode & 0xFFFF);
        uint addr = _gprs[rs] + (uint)offset;
        _memory.Write32(addr, _gprs[rt]);
    }

    public void Stop() => Running = false;
}
