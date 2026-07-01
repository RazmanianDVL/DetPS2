using System;

namespace DetPS2.Core;

/// <summary>
/// CDVD - Basic stub for Phase 3.
/// Handles simple CDVD commands.
/// </summary>
public sealed class Cdvd
{
    public bool DiscPresent { get; private set; } = true;
    public string DiscId { get; private set; } = "TEST_DISC";

    public Cdvd()
    {
        Reset();
    }

    public void Reset()
    {
        DiscPresent = true;
    }

    public uint SendCommand(uint command, uint param)
    {
        Console.WriteLine($"[CDVD] Command 0x{command:X} param=0x{param:X}");

        switch (command)
        {
            case 0x01: // Nop
                return 0;
            case 0x03: // ReadDiscType
                return 0x14; // DVD
            default:
                return 0;
        }
    }

    public void Step(ulong cycles) { }
}
