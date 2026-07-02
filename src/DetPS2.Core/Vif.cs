using System;

namespace DetPS2.Core;

/// <summary>
/// VIF (Vector Interface) - Updated for Phase 6.
/// Handles data transfer to VU0 and VU1.
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
    /// Send data to VU1 (from Vif1).
    /// </summary>
    public void SendToVu1(uint data)
    {
        if (_vu1 != null)
        {
            _vu1.ReceiveFromVif1(data);
        }
    }
}
