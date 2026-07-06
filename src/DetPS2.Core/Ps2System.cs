using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system.
/// Execution now flows exclusively through Scheduler.RunFor().
/// Ps2System no longer registers itself to avoid duplicate stepping.
/// All hardware blocks are stepped directly by the Scheduler in registration order.
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
        // NOTE: Do NOT register 'this' (Ps2System). Scheduler now drives leaf components directly.
        // This eliminates duplicate execution and restores clean deterministic scheduling.
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

    /// <summary>
    /// The single public entry point for running the system.
    /// All execution must go through the Scheduler.
    /// </summary>
    public void RunFor(ulong cyclesToRun)
    {
        Scheduler.RunFor(cyclesToRun);
    }

    public void Reset()
    {
        Scheduler.Reset();
    }

    // ISchedulable implementation (kept for facade compatibility if needed externally)
    int ISchedulable.Step(ulong maxCycles)
    {
        // With leaf components now registered directly, this combined step is no longer used by Scheduler.
        // Return 0 to indicate no additional cycles consumed at this level.
        return 0;
    }

    void ISchedulable.Reset() => Reset();
}