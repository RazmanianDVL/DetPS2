using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Top-level PS2 system.
/// Now supports loading a BIOS and starting execution.
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
    public Sif Sif { get; }

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
        Iop = new Iop(Intc, Memory);
        Cdvd = new Cdvd();
        Sif = new Sif(Memory);

        Dmac.SetGif(Gif);

        MasterCycles = 0;
    }

    /// <summary>
    /// Loads a PS2 BIOS dump into memory at the correct location (0x1FC00000).
    /// </summary>
    public void LoadBios(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("BIOS file not found", path);

        byte[] biosData = File.ReadAllBytes(path);

        if (biosData.Length != 4 * 1024 * 1024)
            Console.WriteLine("[System] Warning: BIOS size is not 4MB. This may cause issues.");

        // PS2 BIOS is mapped at 0x1FC00000 (KSEG1)
        const uint BIOS_BASE = 0x1FC00000;

        for (int i = 0; i < biosData.Length && i < 4 * 1024 * 1024; i++)
        {
            Memory.Write8(BIOS_BASE + (uint)i, biosData[i]);
        }

        Console.WriteLine($"[System] BIOS loaded from {path} ({biosData.Length} bytes)");

        // Set EE to start executing from BIOS reset vector
        EE.PC = 0xBFC00000; // Reset vector points into BIOS
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
            Sif.Step(1);
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
        // Existing test code...
    }
}
