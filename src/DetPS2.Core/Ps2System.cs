using System;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system.
/// Phase 2 now includes VIF (PATH3) and PCRTC for complete graphics pipeline.
/// </summary>
public sealed class Ps2System
{
    public SystemMemory Memory { get; }
    public EmotionEngine EE { get; }
    public Dmac Dmac { get; }
    public Vif Vif { get; }
    public Gif Gif { get; }
    public Gs Gs { get; }
    public Pcrtc Pcrtc { get; }
    public Intc Intc { get; }
    public Iop Iop { get; }

    public ulong MasterCycles { get; private set; }

    public Ps2System()
    {
        Memory = new SystemMemory();
        EE = new EmotionEngine(Memory);

        Dmac = new Dmac(Memory);
        Gs = new Gs(Memory);
        Gif = new Gif(Gs);
        Vif = new Vif(Gs, Gif);
        Pcrtc = new Pcrtc(Gs);
        Intc = new Intc();
        Iop = new Iop(Intc);

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
            Vif.Step(1);
            Gif.Step(1);
            Gs.Step(1);
            Pcrtc.Step(1);
            Intc.Step(1);
            Iop.Step(1);
        }
    }

    public void RunUntil(ulong targetCycle)
    {
        while (MasterCycles < targetCycle)
        {
            int cyclesTaken = EE.Step();
            MasterCycles += (ulong)cyclesTaken;

            Dmac.Step(1);
            Vif.Step(1);
            Gif.Step(1);
            Gs.Step(1);
            Pcrtc.Step(1);
            Intc.Step(1);
            Iop.Step(1);
        }
    }

    public void Reset()
    {
        MasterCycles = 0;
        EE.Reset();
        Dmac.Reset();
        Vif.Reset();
        Gif.Reset();
        Gs.Reset();
        Pcrtc.Reset();
        Intc.Reset();
        Iop.Reset();
    }

    public void TriggerTestDraw()
    {
        Console.WriteLine("[Ps2System] Full Phase 2 pipeline test (VIF + GIF + GS + PCRTC)...");

        ulong baseAddr = 0x100000;

        Memory.Write32(baseAddr + 0,  0x00008005);
        Memory.Write32(baseAddr + 4,  0);
        Memory.Write32(baseAddr + 8,  0);
        Memory.Write32(baseAddr + 12, 0);

        Memory.Write32(baseAddr + 16,  0x00000005); // PRIM = Quad
        Memory.Write32(baseAddr + 32,  0xFF00FFFF);
        Memory.Write32(baseAddr + 48,  0x0000C800);
        Memory.Write32(baseAddr + 64,  0x0001B800);
        Memory.Write32(baseAddr + 80,  0x00014000);

        uint gifChBase = 0x10008000;

        Dmac.WriteRegister(gifChBase + 0x00, (uint)baseAddr);
        Dmac.WriteRegister(gifChBase + 0x10, 6);
        Dmac.WriteRegister(gifChBase + 0x20, 0x101);

        RunFor(30);

        // Use PCRTC to present the final frame
        Pcrtc.Present("detps2_phase2_final.ppm");

        Console.WriteLine("[Ps2System] Phase 2 pipeline complete.");
    }
}
