using System;

namespace DetPS2.Core;

/// <summary>
/// SIF - Improved with better DMA status tracking.
/// </summary>
public sealed class Sif
{
    private readonly SystemMemory _memory;
    public bool DmaBusy { get; private set; } = false;

    public Sif(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public void Reset()
    {
        DmaBusy = false;
    }

    public void DoDmaTransfer(uint eeAddr, uint iopAddr, uint size)
    {
        DmaBusy = true;
        Console.WriteLine($"[SIF] Starting DMA EE:0x{eeAddr:X} -> IOP:0x{iopAddr:X} ({size} bytes)");

        for (uint i = 0; i < size; i += 4)
        {
            uint value = _memory.Read32(eeAddr + i);
            _memory.Write32(iopAddr + i, value);
        }

        DmaBusy = false;
        Console.WriteLine("[SIF] DMA transfer completed");
    }

    public void Step(ulong cycles) { }
}
