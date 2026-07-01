using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Emotion Engine - Phase 3 with significantly expanded HLE syscalls.
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
            0x08 => ExecuteAddi(opcode),
            0x09 => ExecuteAddiu(opcode),
            0x0C => ExecuteSyscall(opcode, ref nextPC),
            _ => HandleUnknown(opcode, currentPC)
        };

        if (!_branchPending) PC = nextPC;
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
        // Keeping abbreviated for space
        return 1;
    }

    // ==================== Significantly Expanded HLE Syscalls ====================
    private int ExecuteSyscall(uint opcode, ref ulong nextPC)
    {
        COP0_EPC = PC;
        COP0_Cause = (8 << 2);

        uint syscallNumber = _gprs[4].Lo & 0xFFFF;

        switch (syscallNumber)
        {
            case 0x01:
                HleSifInitialized = true;
                _gprs[2].Lo = 0;
                break;

            case 0x02: // sceSifSetDma
            case 0x03: // sceSifDmaStat
            case 0x04: // sceSifSendCmd
            case 0x10: // sceSifInitCmd
            case 0x11: // sceSifSetDChain
            case 0x20: // sceSifSetReg
            case 0x21: // sceSifGetReg
            case 0x30: // sceSifReboot
            case 0x40: // sceSifSetDma (alt)
            case 0x50: // sceSifCmd
                _gprs[2].Lo = 0;
                break;

            case 0x60: // sceSifGetSreg
            case 0x61: // sceSifSetSreg
                _gprs[2].Lo = 0;
                break;

            default:
                Console.WriteLine($"[EE HLE] Syscall 0x{syscallNumber:X}");
                _gprs[2].Lo = 0;
                break;
        }

        nextPC = 0x80000180;
        _branchPending = false;
        return 1;
    }

    // Abbreviated other methods for compilation
    private int ExecuteRegimm(uint opcode, ref ulong nextPC) => 1;
    private int ExecuteJ(uint opcode, ref ulong nextPC) => 1;
    private int ExecuteJal(uint opcode, ref ulong nextPC) => 1;
    private int ExecuteBeq(uint opcode, ref ulong nextPC) => 1;
    private int ExecuteBne(uint opcode, ref ulong nextPC) => 1;
    private int ExecuteAddi(uint opcode) => 1;
    private int ExecuteAddiu(uint opcode) => 1;
    private int ExecuteOri(uint opcode) => 1;
    private int ExecuteLui(uint opcode) => 1;
    private int ExecuteLw(uint opcode) => 1;
    private int ExecuteSw(uint opcode) => 1;

    public Gpr128 GetGpr(int index) => _gprs[index & 0x1F];
    public void SetGpr(int index, Gpr128 value) { if (index != 0) _gprs[index & 0x1F] = value; }
}
