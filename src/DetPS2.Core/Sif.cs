using System;

namespace DetPS2.Core;

/// <summary>
/// SIF - Continued improvement with command support.
/// </summary>
public sealed class Sif
{
    private readonly SystemMemory _memory;
    public bool DmaBusy { get; private set; } = false;
    public uint LastCommand { get; private set; } = 0;

    public Sif(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public void Reset()
    {
        DmaBusy = false;
        LastCommand = 0;
    }

    public void DoDmaTransfer(uint eeAddr, uint iopAddr, uint size)
    {
        DmaBusy = true;
        for (uint i = 0; i < size; i += 4)
        {
            uint value = _memory.Read32(eeAddr + i);
            _memory.Write32(iopAddr + i, value);
        }
        DmaBusy = false;
    }

    public void SendCommand(uint command)
    {
        LastCommand = command;
        Console.WriteLine($"[SIF] Command sent: 0x{command:X}");
    }

    public void Step(ulong cycles) { }
}
