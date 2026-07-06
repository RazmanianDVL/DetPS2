using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system.
/// Now uses the foundational Scheduler for deterministic stepping.
/// </summary>
public sealed class Ps2System : ISchedulable
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
    public Sif Sif { get; }

    public Scheduler Scheduler { get; }

    public ulong MasterCycles => Scheduler.MasterCycles;

    public Ps2System()
    {
        Memory = new SystemMemory();
        EE = new EmotionEngine(Memory);

        Dmac = new Dmac(Memory);
        Gs = new Gs(Memory);
        Gif = new Gif(Gs);
        Vif = new Vif(Memory);
        Pcrtc = new Pcrtc(Gs);
        Intc = new Intc();
        Iop = new Iop(Intc, Memory);
        Cdvd = new Cdvd();
        Sif = new Sif(Memory);

        Dmac.SetGif(Gif);

        Scheduler = new Scheduler();
        RegisterComponents();
    }

    private void RegisterComponents()
    {
        Scheduler.Register(this);           // Ps2System itself
        Scheduler.Register(EE);
        Scheduler.Register(Dmac);
        Scheduler.Register(Vif);
        Scheduler.Register(Gif);
        Scheduler.Register(Gs);
        Scheduler.Register(Pcrtc);
        Scheduler.Register(Intc);
        Scheduler.Register(Iop);
        Scheduler.Register(Cdvd);
        Scheduler.Register(Sif);
    }

    public byte[] SaveState() => SaveState.Save(this);
    public bool LoadState(byte[] data) => SaveState.Load(this, data);

    public void LoadBios(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("BIOS file not found", path);

        byte[] biosData = File.ReadAllBytes(path);
        const uint BIOS_BASE = 0x1FC00000;

        for (int i = 0; i < biosData.Length && i < 4 * 1024 * 1024; i++)
        {
            Memory.Write8(BIOS_BASE + (uint)i, biosData[i]);
        }

        EE.PC = 0xBFC00000;
    }

    public void RunFor(ulong cyclesToRun)
    {
        Scheduler.RunFor(cyclesToRun);
    }

    public void Reset()
    {
        Scheduler.Reset();
    }

    // ISchedulable implementation
    public int Step()
    {
        // For now, do a simple combined step.
        // Future versions can do more sophisticated per-component cycle accounting.
        int cycles = EE.Step();
        Dmac.Step(1);
        Vif.Step(1);
        Gif.Step(1);
        Gs.Step(1);
        Pcrtc.Step(1);
        Intc.Step(1);
        Iop.Step(1);
        Cdvd.Step(1);
        Sif.Step(1);

        return cycles;
    }
}
