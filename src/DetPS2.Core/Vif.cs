using System;

namespace DetPS2.Core;

/// <summary>
/// VIF (Vector Interface) - Phase 6.
/// Provides data transfer between main memory and the Vector Units (especially VU1).
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

    public void SetVu0(Vu0 vu0) => _vu0 = vu0;
    public void SetVu1(Vu1 vu1) => _vu1 = vu1;

    public void Reset() { }
    public void Step(ulong cycles) { }

    /// <summary>
    /// Send one quadword (16 bytes) from memory to VU1.
    /// Called by DMAC when a VIF1 transfer completes.
    /// </summary>
    public void SendQuadwordToVu1(uint address)
    {
        if (_vu1 == null) return;

        uint w0 = _memory.Read32(address);
        uint w1 = _memory.Read32(address + 4);
        uint w2 = _memory.Read32(address + 8);
        uint w3 = _memory.Read32(address + 12);

        _vu1.ReceiveFromVif1(w0);
        _vu1.ReceiveFromVif1(w1);
        _vu1.ReceiveFromVif1(w2);
        _vu1.ReceiveFromVif1(w3);
    }
}