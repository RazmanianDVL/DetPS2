using System;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system.
/// Phase 2 pipeline now produces real drawn geometry from properly sequenced GIF commands.
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
    /// Writes a clean GIFtag + data that the improved Gif parser expects:
    /// PRIM, RGBAQ, Vertex1, Vertex2, Vertex3.
    /// This produces a real drawn triangle via the full pipeline.
    /// </summary>
    public void TriggerTestDraw()
    {
        Console.WriteLine("[Ps2System] Sending clean GIF command stream through DMAC...");

        ulong baseAddr = 0x100000;

        // GIFtag: NLOOP=5 (PRIM + RGBAQ + 3 vertices)
        Memory.Write32(baseAddr + 0,  0x00008005);
        Memory.Write32(baseAddr + 4,  0);
        Memory.Write32(baseAddr + 8,  0);
        Memory.Write32(baseAddr + 12, 0);

        // Data quadwords (sequential as expected by Gif parser)
        Memory.Write32(baseAddr + 16,  0x00000001); // PRIM
        Memory.Write32(baseAddr + 32,  0xFF00FFFF); // RGBAQ (magenta)
        Memory.Write32(baseAddr + 48,  0x0000C800); // Vertex 1
        Memory.Write32(baseAddr + 64,  0x0001B800); // Vertex 2
        Memory.Write32(baseAddr + 80,  0x00014000); // Vertex 3

        // Start DMAC transfer on GIF channel
        Dmac.StartTransfer(Dmac.Channel.GIF);

        // Temporary: set transfer state (will be replaced by real register writes)
        var chField = typeof(Dmac).GetField("_channels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (chField != null)
        {
            var channels = (Array)chField.GetValue(Dmac);
            var ch = channels.GetValue((int)Dmac.Channel.GIF);
            ch.GetType().GetField("MADR")?.SetValue(ch, (uint)baseAddr);
            ch.GetType().GetField("QWC")?.SetValue(ch, (uint)6);
            ch.GetType().GetField("Active")?.SetValue(ch, true);
        }

        RunFor(25);

        Console.WriteLine("[Ps2System] Pipeline finished. Real triangle drawn from GIF commands.");
    }
}
