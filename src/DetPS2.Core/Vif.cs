using System;

namespace DetPS2.Core;

/// <summary>
/// VIF (Vector Interface) - Phase 6 progress.
/// Basic data transfer support for VU1.
/// </summary>
public sealed class Vif
{
    private readonly SystemMemory _memory;
    private Vu0 _vu0;
    private Vu1 _vu1;

    public Vif(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public void SetVu0(Vu0 vu0)
    {
        _vu0 = vu0;
    }

    public void SetVu1(Vu1 vu1)
    {
        _vu1 = vu1;
    }

    public void Reset() { }

    public void Step(ulong cycles) { }

    /// <summary>
    /// Send a quadword of data to VU1.
    /// This is a simplified version for early Phase 6.
    /// </summary>
    public void SendQuadwordToVu1(uint address)
    {
        if (_vu1 == null) return;

        // Read 4 words (one quadword) from memory
        uint w0 = _memory.Read32(address);
        uint w1 = _memory.Read32(address + 4);
        uint w2 = _memory.Read32(address + 8);
        uint w3 = _memory.Read32(address + 12);

        // For now we just forward the first word as a signal
        // Real implementation will unpack and feed properly
        _vu1.ReceiveFromVif1(w0);
    }
}
