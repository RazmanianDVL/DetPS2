using System;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system.
/// Uses clean DMAC register interface (no more reflection).
/// </summary>
public sealed class Ps2System
{
    public SystemMemory Memory { get; }
    public EmotionEngine EE { get; }
    public Dmac Dmac { get; }
    public Gif Gif { get; }
    public Gs Gs { get; }

    public ulong MasterCycles { get; private set; }

    public Ps2System()
    {
        Memory = new SystemMemory();
        EE = new EmotionEngine(Memory);

        Dmac = new Dmac(Memory);
        Gs = new Gs(Memory);
        Gif = new Gif(Gs);

        Dmac.SetGif(Gif);

        MasterCycles = 0;
    }

    public void RunFor(ulong cyclesToRun)
    {
        ulong target = MasterCycles + cyclesToRun;

        while (MasterCycles < target)
        {
            int cyclesTaken = EE.Step();
            MasterCycles += (ulong)cyclesTaken;

            Dmac.Step(1);
            Gif.Step(1);
            Gs.Step(1);
        }
    }

    public void RunUntil(ulong targetCycle)
    {
        while (MasterCycles < targetCycle)
        {
            int cyclesTaken = EE.Step();
            MasterCycles += (ulong)cyclesTaken;

            Dmac.Step(1);
            Gif.Step(1);
            Gs.Step(1);
        }
    }

    public void Reset()
    {
        MasterCycles = 0;
        EE.Reset();
        Dmac.Reset();
        Gif.Reset();
        Gs.Reset();
    }

    /// <summary>
    /// Demonstrates the full pipeline using the new clean DMAC register interface.
    /// </summary>
    public void TriggerTestDraw()
    {
        Console.WriteLine("[Ps2System] Sending drawing commands via clean DMAC register writes...");

        ulong baseAddr = 0x100000;

        // GIFtag + data (same as before)
        Memory.Write32(baseAddr + 0,  0x00008005);
        Memory.Write32(baseAddr + 4,  0);
        Memory.Write32(baseAddr + 8,  0);
        Memory.Write32(baseAddr + 12, 0);

        Memory.Write32(baseAddr + 16,  0x00000001); // PRIM
        Memory.Write32(baseAddr + 32,  0xFF00FFFF); // RGBAQ
        Memory.Write32(baseAddr + 48,  0x0000C800); // V1
        Memory.Write32(baseAddr + 64,  0x0001B800); // V2
        Memory.Write32(baseAddr + 80,  0x00014000); // V3

        // Use the new clean register interface instead of reflection
        uint gifChBase = 0x10008000; // approximate GIF channel base

        Dmac.WriteRegister(gifChBase + 0x00, (uint)baseAddr); // MADR
        Dmac.WriteRegister(gifChBase + 0x10, 6);              // QWC
        Dmac.WriteRegister(gifChBase + 0x20, 0x101);          // CHCR with start bit

        RunFor(25);

        Console.WriteLine("[Ps2System] Pipeline complete using real register interface.");
    }
}
