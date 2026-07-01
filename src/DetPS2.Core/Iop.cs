using System;

namespace DetPS2.Core;

/// <summary>
/// Input/Output Processor (IOP) - Phase 3
/// Now includes a basic R3000A-style CPU interpreter skeleton.
/// </summary>
public sealed class Iop
{
    public Intc Intc { get; }

    // CPU State
    public uint PC { get; set; } = 0xBFC00000;
    private readonly uint[] _gprs = new uint[32];

    // SIF
    public uint SifMbxFromEE { get; private set; }
    public uint SifMbxToEE { get; private set; }

    public bool Running { get; private set; } = true;

    private readonly SystemMemory _memory;

    public Iop(Intc intc)
    {
        Intc = intc ?? throw new ArgumentNullException(nameof(intc));
        _memory = new SystemMemory();
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

        for (int i = 0; i < 4 && Running; i++)
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
            case 0x00:
                ExecuteSpecial(opcode);
                break;
            case 0x0D:
                ExecuteOri(opcode);
                break;
            case 0x23:
                ExecuteLw(opcode);
                break;
            case 0x2B:
                ExecuteSw(opcode);
                break;
            default:
                break;
        }
    }

    private void ExecuteSpecial(uint opcode)
    {
        uint function = opcode & 0x3F;
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        uint rd = (opcode >> 11) & 0x1F;

        switch (function)
        {
            case 0x20:
            case 0x21:
                if (rd != 0) _gprs[rd] = _gprs[rs] + _gprs[rt];
                break;
            case 0x08:
                PC = _gprs[rs] - 4;
                break;
        }
    }

    private void ExecuteOri(uint opcode)
    {
        uint rs = (opcode >> 21) & 0x1F;
        uint rt = (opcode >> 16) & 0x1F;
        ushort imm = (ushort)(opcode & 0xFFFF);
        if (rt != 0) _gprs[rt] = _gprs[rs] | imm;
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
