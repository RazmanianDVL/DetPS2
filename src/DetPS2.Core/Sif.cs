using System;

namespace DetPS2.Core;

/// <summary>
/// SIF - Subsystem Interface.
/// Minimal SIF interrupt generation added (Phase 6.2).
/// </summary>
public sealed class Sif : ISchedulable
{
    private readonly SystemMemory _memory;
    private readonly Intc? _intc;

    public bool DmaBusy { get; private set; } = false;
    public uint LastCommand { get; private set; } = 0;
    public uint Status { get; private set; } = 0;

    public Sif(SystemMemory memory, Intc? intc = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _intc = intc;
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

        // Minimal SIF interrupt generation
        _intc?.Raise(Intc.InterruptSource.Sif);
    }

    public uint GetStatus() => Status;

    public int Step(ulong maxCycles) => 0;
}
