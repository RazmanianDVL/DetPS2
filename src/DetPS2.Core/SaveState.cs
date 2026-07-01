using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Save state system - Phase 4.
/// Now serializes actual components (starting with Memory).
/// </summary>
public static class SaveState
{
    private const uint Magic = 0x44505332; // "DPS2"
    private const uint Version = 1;

    public static byte[] Save(Ps2System system)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(DateTime.UtcNow.Ticks);

        // Serialize Memory (biggest and most important)
        byte[] memoryData = system.Memory.GetRawData();
        writer.Write(memoryData.Length);
        writer.Write(memoryData);

        // TODO: Serialize EE, IOP, GS, etc.
        writer.Write(0); // Placeholder for other state size

        return ms.ToArray();
    }

    public static bool Load(Ps2System system, byte[] data)
    {
        if (data == null || data.Length < 16) return false;

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        if (reader.ReadUInt32() != Magic) return false;
        if (reader.ReadUInt32() != Version) return false;

        reader.ReadInt64(); // timestamp

        int memSize = reader.ReadInt32();
        if (memSize > 0)
        {
            byte[] memoryData = reader.ReadBytes(memSize);
            system.Memory.SetRawData(memoryData);
        }

        return true;
    }
}
