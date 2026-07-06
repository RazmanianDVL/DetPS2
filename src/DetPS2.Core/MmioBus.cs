using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// MmioBus - Foundational memory-mapped I/O abstraction.
/// 
/// This class provides a clean central place to register and access
/// hardware registers (INTC, DMAC, Timers, GS, etc.).
/// 
/// Design goals:
/// - Single point for all MMIO reads/writes
/// - Easy to debug and log register access
/// - Extensible (components can register their own handlers)
/// - Foundation for accurate hardware behavior and SaveState
/// </summary>
public sealed class MmioBus
{
    private readonly Dictionary<uint, Func<uint>> _readHandlers = new();
    private readonly Dictionary<uint, Action<uint>> _writeHandlers = new();

    /// <summary>
    /// Register a read handler for a specific address.
    /// </summary>
    public void RegisterRead(uint address, Func<uint> readHandler)
    {
        _readHandlers[address] = readHandler;
    }

    /// <summary>
    /// Register a write handler for a specific address.
    /// </summary>
    public void RegisterWrite(uint address, Action<uint> writeHandler)
    {
        _writeHandlers[address] = writeHandler;
    }

    public uint Read32(uint address)
    {
        if (_readHandlers.TryGetValue(address, out var handler))
            return handler();

        // Default: return 0 for unmapped registers
        return 0;
    }

    public void Write32(uint address, uint value)
    {
        if (_writeHandlers.TryGetValue(address, out var handler))
            handler(value);
    }

    public void Reset()
    {
        // Components are responsible for their own reset
    }
}
