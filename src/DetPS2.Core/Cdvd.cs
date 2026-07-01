using System;

namespace DetPS2.Core;

/// <summary>
/// CDVD - Completed stub for Phase 3.
/// Handles a wide range of CDVD commands with simulated state.
/// </summary>
public sealed class Cdvd
{
    public bool DiscPresent { get; private set; } = true;
    public string DiscId { get; private set; } = "PS2DEMO";
    public bool TrayOpen { get; private set; } = false;
    public uint DiscType { get; private set; } = 0x14; // DVD

    public Cdvd()
    {
        Reset();
    }

    public void Reset()
    {
        DiscPresent = true;
        TrayOpen = false;
        DiscType = 0x14;
    }

    public uint SendCommand(uint command, uint param)
    {
        Console.WriteLine($"[CDVD] Command 0x{command:X2} param=0x{param:X}");

        switch (command)
        {
            case 0x01: // Nop
                return 0;

            case 0x03: // ReadDiscType
                return DiscType;

            case 0x05: // Tray status / GetTrayState
                return TrayOpen ? 0x01u : 0x00u;

            case 0x06: // Tray open/close
                TrayOpen = !TrayOpen;
                return 0;

            case 0x08: // ReadDiscID (simplified)
                return 0;

            case 0x0A: // GetDiscType
                return DiscType;

            case 0x12: // ReadSector (simulated)
                return 0;

            case 0x15: // GetDiscInfo
                return 0;

            default:
                return 0;
        }
    }

    public void Step(ulong cycles) { }
}
