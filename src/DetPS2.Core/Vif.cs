using System;

namespace DetPS2.Core;

/// <summary>
/// VIF (Vector Interface) - Phase 6 substantial update.
/// Improved data transfer support for VU1.
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
    /// Send a full quadword (4 words) from memory to VU1.
    /// This is a more realistic Vif1 transfer.
    /// </summary>
    public void SendQuadwordToVu1(uint address)
    {
        if (_vu1 == null) return;

        // Read 16 bytes (one quadword)
        uint w0 = _memory.Read32(address);
        uint w1 = _memory.Read32(address + 4);
        uint w2 = _memory.Read32(address + 8);
        uint w3 = _memory.Read32(address + 12);

        // Forward all 4 words to VU1
        _vu1.ReceiveFromVif1(w0);
        _vu1.ReceiveFromVif1(w1);
        _vu1.ReceiveFromVif1(w2);
        _vu1.ReceiveFromVif1(w3);
    }
}
