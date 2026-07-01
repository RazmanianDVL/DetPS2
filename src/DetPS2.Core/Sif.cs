using System;

namespace DetPS2.Core;

/// <summary>
/// SIF (Sub-system Interface) - Phase 3
/// Improved with actual memory transfer support.
/// </summary>
public sealed class Sif
{
    private readonly SystemMemory _eeMemory;
    private readonly SystemMemory _iopMemory; // For future separate IOP memory

    public Sif(SystemMemory eeMemory)
    {
        _eeMemory = eeMemory ?? throw new ArgumentNullException(nameof(eeMemory));
        _iopMemory = eeMemory; // Currently sharing until we have separate IOP memory
    }

    public void Reset() { }

    /// <summary>
    /// Performs an actual memory copy from EE to IOP address space.
    /// </summary>
    public void DoDmaTransfer(uint eeAddr, uint iopAddr, uint size)
    {
        Console.WriteLine($"[SIF] DMA EE:0x{eeAddr:X} -> IOP:0x{iopAddr:X} size=0x{size:X}");

        for (uint i = 0; i < size; i += 4)
        {
            uint value = _eeMemory.Read32(eeAddr + i);
            _iopMemory.Write32(iopAddr + i, value);
        }

        Console.WriteLine("[SIF] DMA transfer complete");
    }

    public void Step(ulong cycles) { }
}
