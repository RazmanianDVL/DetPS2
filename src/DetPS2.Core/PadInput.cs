using System;

namespace DetPS2.Core;

/// <summary>
/// Digital + analog pad (Phase 9/16). MMIO 0x1000F400.
/// </summary>
public sealed class PadInput
{
    public const uint MmioBase = 0x1000F400;

    [Flags]
    public enum Button : uint
    {
        None = 0,
        Select = 1u << 0,
        L3 = 1u << 1,
        R3 = 1u << 2,
        Start = 1u << 3,
        Up = 1u << 4,
        Right = 1u << 5,
        Down = 1u << 6,
        Left = 1u << 7,
        L2 = 1u << 8,
        R2 = 1u << 9,
        L1 = 1u << 10,
        R1 = 1u << 11,
        Triangle = 1u << 12,
        Circle = 1u << 13,
        Cross = 1u << 14,
        Square = 1u << 15,
    }

    public uint Buttons { get; private set; }
    public uint ButtonsPrevious { get; private set; }

    // Analog sticks: 0x80 center, 0x00..0xFF
    public byte Lx { get; private set; } = 0x80;
    public byte Ly { get; private set; } = 0x80;
    public byte Rx { get; private set; } = 0x80;
    public byte Ry { get; private set; } = 0x80;
    public bool AnalogMode { get; set; }

    public void Reset()
    {
        Buttons = 0;
        ButtonsPrevious = 0;
        Lx = Ly = Rx = Ry = 0x80;
        AnalogMode = false;
    }

    public void SetButtons(uint buttons)
    {
        ButtonsPrevious = Buttons;
        Buttons = buttons;
    }

    public void Press(Button b) => SetButtons(Buttons | (uint)b);
    public void Release(Button b) => SetButtons(Buttons & ~(uint)b);
    public void Set(Button b, bool down)
    {
        if (down) Press(b); else Release(b);
    }

    public void SetLeftStick(byte x, byte y) { Lx = x; Ly = y; AnalogMode = true; }
    public void SetRightStick(byte x, byte y) { Rx = x; Ry = y; AnalogMode = true; }

    public bool IsDown(Button b) => (Buttons & (uint)b) != 0;
    public bool IsPressed(Button b) => (Buttons & (uint)b) != 0 && (ButtonsPrevious & (uint)b) == 0;

    /// <summary>Pack digital + analog into 8 bytes for status buffer.</summary>
    public void WriteStatusBuffer(SystemMemory mem, uint addr)
    {
        mem.Write8(addr, 0); // port
        mem.Write8(addr + 1, AnalogMode ? (byte)0x79 : (byte)0x41);
        mem.Write8(addr + 2, (byte)(Buttons & 0xFF));
        mem.Write8(addr + 3, (byte)((Buttons >> 8) & 0xFF));
        mem.Write8(addr + 4, Rx);
        mem.Write8(addr + 5, Ry);
        mem.Write8(addr + 6, Lx);
        mem.Write8(addr + 7, Ly);
    }

    public uint ReadRegister(uint address)
    {
        return (address - MmioBase) switch
        {
            0 => Buttons,
            4 => ButtonsPrevious,
            8 => Buttons ^ ButtonsPrevious,
            0x10 => (uint)(Lx | (Ly << 8) | (Rx << 16) | (Ry << 24)),
            0x14 => AnalogMode ? 1u : 0u,
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        uint off = address - MmioBase;
        if (off == 0) SetButtons(value);
        else if (off == 0x10)
        {
            Lx = (byte)value;
            Ly = (byte)(value >> 8);
            Rx = (byte)(value >> 16);
            Ry = (byte)(value >> 24);
            AnalogMode = true;
        }
        else if (off == 0x14)
            AnalogMode = value != 0;
    }
}
