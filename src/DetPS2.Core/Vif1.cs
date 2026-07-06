using System;

namespace DetPS2.Core;

/// <summary>
/// VIF1 - Foundational implementation for VIF1 (Vector Interface 1).
/// 
/// VIF1 is responsible for transferring data from main memory to VU1,
/// unpacking data, and loading microcode into VU1.
/// 
/// This class provides the foundational structure only.
/// Heavy implementation (full VifCode parsing, microcode handling, etc.)
/// should be done on top of this foundation.
/// </summary>
public sealed class Vif1
{
    private readonly SystemMemory _memory;
    private readonly Vu1 _vu1;

    public Vif1(SystemMemory memory, Vu1 vu1)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _vu1 = vu1 ?? throw new ArgumentNullException(nameof(vu1));
    }

    public void Reset() { }

    /// <summary>
    /// Process data from a specific memory address into VU1.
    /// This is a high-level entry point.
    /// </summary>
    public void ProcessData(uint address, uint qwc)
    {
        // Foundation only - actual unpacking logic goes here in the future
        for (uint i = 0; i < qwc; i++)
        {
            uint data = _memory.Read32(address + (i * 16));
            _vu1.ReceiveFromVif1(data);
        }
    }

    /// <summary>
    /// Send a VifCode command.
    /// Foundation for future command parsing (MSCAL, MPG, etc.).
    /// </summary>
    public void SendVifCode(uint vifCode)
    {
        // TODO: Parse VifCode and act accordingly
        // Examples: MSCAL (run microcode), MPG (load microprogram), etc.
    }

    public void Step(ulong cycles) { }
}
