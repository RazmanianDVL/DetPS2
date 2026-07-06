using System;

namespace DetPS2.Core;

/// <summary>
/// VifUnpacker - Foundational structure for unpacking VIF data.
/// 
/// VIF data often comes in packed formats that need to be unpacked
/// before being sent to the VU.
/// 
/// This class provides the architectural foundation only.
/// Actual unpacking algorithms and VifCode handling should be
/// implemented on top of this structure.
/// </summary>
public sealed class VifUnpacker
{
    private readonly SystemMemory _memory;

    public VifUnpacker(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public void Reset() { }

    /// <summary>
    /// Unpack data from memory starting at the given address.
    /// Foundation method - real unpacking logic goes here later.
    /// </summary>
    public void Unpack(uint address, uint qwc, Action<uint> onData)
    {
        for (uint i = 0; i < qwc; i++)
        {
            uint data = _memory.Read32(address + (i * 16));
            onData?.Invoke(data);
        }
    }
}
