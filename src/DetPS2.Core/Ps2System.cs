using System;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system with expanded Phase 3 components.
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
    public Cdvd Cdvd { get; }

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
        Iop = new Iop(Intc, Memory); // Now shares memory
        Cdvd = new Cdvd();

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
            Cdvd.Step(1);
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
        Cdvd.Reset();
    }

    public void TriggerTestDraw(bool useVif = false)
    {
        // ... (same as before)
    }
}
