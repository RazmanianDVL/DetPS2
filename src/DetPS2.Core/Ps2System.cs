using System;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system.
/// Phase 2: Full DMAC -> GIF -> GS pipeline exercised in the test.
/// Master cycle counter drives everything for determinism.
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
    /// Phase 2 test: Sets up a minimal valid GIFtag in memory and triggers
    /// a real DMAC transfer on the GIF channel (PATH3). This exercises the
    /// full pipeline instead of a direct shortcut.
    /// </summary>
    public void TriggerTestDraw()
    {
        Console.WriteLine("[Ps2System] Triggering real DMAC -> GIF -> GS pipeline...");

        // Simple test GIFtag at a known address in RDRAM (0x100000 for example)
        // GIFtag format (simplified for test):
        //   NLOOP = 1 (one primitive), EOP = 1, Format = PACKED (0)
        //   Then some PRIM + RGBAQ + XYZ2 data (we'll let GS handle drawing for now)
        ulong tagAddr = 0x100000;

        // Write a minimal GIFtag (128-bit / 16 bytes)
        // For this test we use a tag that triggers DrawTestPattern in GS
        // In future this will be real primitive data
        Memory.Write32(tagAddr, 0x00008001);     // NLOOP=1, EOP=1 (rough)
        Memory.Write32(tagAddr + 4, 0x00000000); // PRIM etc.
        Memory.Write32(tagAddr + 8, 0x00000000);
        Memory.Write32(tagAddr + 12, 0x00000000);

        // Configure DMAC GIF channel (channel 2) for a normal transfer
        // In a real implementation we would write CHCR, MADR, QWC via registers.
        // For this early test we directly start a transfer of 1 quadword.
        Dmac.StartTransfer(Dmac.Channel.GIF);  // This sets Active + Mode

        // Manually set the transfer parameters (in real code this comes from register writes)
        // We use reflection here only for the test harness - later we'll add proper register interface
        var chField = typeof(Dmac).GetField("_channels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (chField != null)
        {
            var channels = (Array)chField.GetValue(Dmac);
            var gifCh = channels.GetValue((int)Dmac.Channel.GIF);
            var madrField = gifCh.GetType().GetField("MADR");
            var qwcField = gifCh.GetType().GetField("QWC");
            var activeField = gifCh.GetType().GetField("Active");

            if (madrField != null) madrField.SetValue(gifCh, (uint)tagAddr);
            if (qwcField != null) qwcField.SetValue(gifCh, (uint)1);
            if (activeField != null) activeField.SetValue(gifCh, true);
        }

        // Run a few cycles so DMAC/GIF/GS can process
        RunFor(10);

        // The GIF should have called Gs.ReceiveCommandList which currently draws the test pattern
        Console.WriteLine("[Ps2System] Pipeline step complete. Framebuffer should now contain test pattern.");
    }
}
