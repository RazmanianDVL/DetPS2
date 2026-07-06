using System;

namespace DetPS2.Core;

/// <summary>
/// SIF - More realistic implementation for Phase 3.
/// 
/// Updated to comply with ISchedulable contract.
/// </summary>
public sealed class Sif : ISchedulable
{
    private readonly SystemMemory _memory;
    public bool DmaBusy { get; private set; } = false;
    public uint LastCommand { get; private set; } = 0;
    public uint Status { get; private set; } = 0;

    public Sif(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public void Reset()
    {
        DmaBusy = false;
        LastCommand = 0;
        Status = 0;
    }

    public void DoDmaTransfer(uint eeAddr, uint iopAddr, uint size)
    {
        DmaBusy = true;
        Status |= 0x1;

        for (uint i = 0; i < size; i += 4)
        {
            uint value = _memory.Read32(eeAddr + i);
            _memory.Write32(iopAddr + i, value);
        }

        DmaBusy = false;
        Status &= ~0x1u;
    }

    public void SendCommand(uint command)
    {
        LastCommand = command;
        Status |= 0x2;
    }

    public uint GetStatus() => Status;

    /// <summary>
    /// ISchedulable contract implementation.
    /// Currently does minimal work; real DMA timing can be added later.
    /// </summary>
    public int Step(ulong maxCycles)
    {
        // For now, SIF DMA is instantaneous in DoDmaTransfer.
        // Future: model actual DMA transfer cycles here.
        return 0;
    }
}