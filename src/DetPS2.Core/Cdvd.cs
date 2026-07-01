using System;

namespace DetPS2.Core;

/// <summary>
/// CDVD - Improved stub for Phase 3.
/// </summary>
public sealed class Cdvd
{
    public bool DiscPresent { get; private set; } = true;

    public Cdvd() => Reset();

    public void Reset() => DiscPresent = true;

    public uint SendCommand(uint command, uint param)
    {
        Console.WriteLine($"[CDVD] Command 0x{command:X2}");

        return command switch
        {
            0x01 => 0,           // Nop
            0x03 => 0x14,        // ReadDiscType (DVD)
            0x05 => 0x02,        // Tray status
            _ => 0
        };
    }

    public void Step(ulong cycles) { }
}
