using System;

namespace DetPS2.Core;

/// <summary>
/// SIF (Sub-system Interface) - Phase 3
/// Basic DMA support between EE and IOP.
/// </summary>
public sealed class Sif
{
    private readonly SystemMemory _memory;
    private readonly Iop _iop;

    public Sif(SystemMemory memory, Iop iop)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _iop = iop ?? throw new ArgumentNullException(nameof(iop));
    }

    public void Reset() { }

    /// <summary>
    /// Very simplified SIF DMA transfer from EE to IOP memory space.
    /// In reality this is much more complex with tags, etc.
    /// </summary>
    public void DoDmaTransfer(uint eeAddr, uint iopAddr, uint size)
    {
        Console.WriteLine($"[SIF] DMA transfer EE:0x{eeAddr:X} -> IOP:0x{iopAddr:X} size=0x{size:X}");

        for (uint i = 0; i < size; i += 4)
        {
            uint value = _memory.Read32(eeAddr + i);
            // For now we just log - real implementation would write to IOP memory space
            // _iop.WriteMemory(iopAddr + i, value);
        }

        Console.WriteLine("[SIF] DMA transfer complete (simplified)");
    }

    public void Step(ulong cycles) { }
}
