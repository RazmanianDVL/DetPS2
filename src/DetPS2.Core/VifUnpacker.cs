using System;

namespace DetPS2.Core;

/// <summary>
/// VIF unpack helper — reads packed memory and feeds a production <see cref="Vif"/> UNPACK stream.
/// Phase 50: real V4_32 path via <see cref="Vif.FeedData"/> (not a foundation stub).
/// </summary>
public sealed class VifUnpacker
{
    private readonly SystemMemory _memory;
    private readonly Vif? _vif;

    public ulong WordsUnpacked { get; private set; }

    public VifUnpacker(SystemMemory memory, Vif? vif = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _vif = vif;
    }

    public void Reset() => WordsUnpacked = 0;

    /// <summary>Raw callback unpack (legacy tests).</summary>
    public void Unpack(uint address, uint qwc, Action<uint> onData)
    {
        for (uint i = 0; i < qwc; i++)
        {
            for (int w = 0; w < 4; w++)
            {
                uint data = _memory.Read32(address + i * 16 + (uint)(w * 4));
                onData?.Invoke(data);
                WordsUnpacked++;
            }
        }
    }

    /// <summary>Issue V4_32 UNPACK then feed qwc units from memory into VIF.</summary>
    public void UnpackV4_32ToVif(uint address, uint numUnits, uint dest = 0)
    {
        if (_vif == null) throw new InvalidOperationException("Vif backend required");
        uint code = (0x6Cu << 24) | ((numUnits & 0xFF) << 16) | (dest & 0xFFFF);
        _vif.ProcessVifCode(code);
        uint words = numUnits * 4;
        for (uint i = 0; i < words; i++)
        {
            _vif.FeedData(_memory.Read32(address + i * 4));
            WordsUnpacked++;
        }
    }
}
