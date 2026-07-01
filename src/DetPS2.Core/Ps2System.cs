using System;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system.
/// Phase 2: Full DMAC -> GIF -> GS pipeline with real primitive drawing.
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
    /// Phase 2 test: Writes a more complete GIFtag (with PRIM, RGBAQ, XYZ)
    /// and triggers real DMAC transfer. The new Gif/GS code will draw an actual triangle.
    /// </summary>
    public void TriggerTestDraw()
    {
        Console.WriteLine("[Ps2System] Triggering real DMAC -> GIF -> GS with primitive data...");

        ulong tagAddr = 0x100000;

        // GIFtag (NLOOP=3 so we can send PRIM + RGBAQ + XYZ + XYZ + XYZ roughly)
        Memory.Write32(tagAddr, 0x00008003);     // NLOOP=3, EOP=1 (simplified)
        Memory.Write32(tagAddr + 4, 0x00000000);
        Memory.Write32(tagAddr + 8, 0x00000000);
        Memory.Write32(tagAddr + 12, 0x00000000);

        // Data following the tag (very rough layout for Phase 2 test)
        // Word 0: PRIM (low byte)
        Memory.Write32(tagAddr + 16, 0x00000001); // PRIM marker
        // Word 1: RGBAQ (magenta-ish)
        Memory.Write32(tagAddr + 32, 0xFF00FFFF);
        // Vertices (XYZ rough)
        Memory.Write32(tagAddr + 48, 0x0000C800); // vertex 1
        Memory.Write32(tagAddr + 64, 0x0001B800); // vertex 2
        Memory.Write32(tagAddr + 80, 0x00014000); // vertex 3 (rough scaling)

        // Trigger DMAC GIF channel
        Dmac.StartTransfer(Dmac.Channel.GIF);

        // Set transfer params via reflection (temporary until we add real register interface)
        var chField = typeof(Dmac).GetField("_channels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (chField != null)
        {
            var channels = (Array)chField.GetValue(Dmac);
            var gifCh = channels.GetValue((int)Dmac.Channel.GIF);
            var madrField = gifCh.GetType().GetField("MADR");
            var qwcField = gifCh.GetType().GetField("QWC");
            var activeField = gifCh.GetType().GetField("Active");

            if (madrField != null) madrField.SetValue(gifCh, (uint)tagAddr);
            if (qwcField != null) qwcField.SetValue(gifCh, (uint)6); // tag + data
            if (activeField != null) activeField.SetValue(gifCh, true);
        }

        // Run enough cycles for the pipeline to process
        RunFor(20);

        Console.WriteLine("[Ps2System] Pipeline complete. Real primitive should be in framebuffer.");
    }
}
