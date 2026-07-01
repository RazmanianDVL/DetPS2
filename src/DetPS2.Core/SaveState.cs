using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// SaveState - Phase 4. Now captures Memory + basic EE/IOP registers.
/// </summary>
public static class SaveState
{
    private const uint Magic = 0x44505332;
    private const uint Version = 1;

    public static byte[] Save(Ps2System system)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(DateTime.UtcNow.Ticks);

        // Memory
        byte[] mem = system.Memory.GetRawData();
        writer.Write(mem.Length);
        writer.Write(mem);

        // Basic EE registers (PC + first 8 GPRs for now)
        writer.Write(system.EE.PC);
        for (int i = 0; i < 8; i++)
        {
            var gpr = system.EE.GetGpr(i);
            writer.Write(gpr.Lo);
            writer.Write(gpr.Hi);
        }

        // Basic IOP registers
        writer.Write(system.Iop.PC);
        for (int i = 0; i < 8; i++)
            writer.Write(system.Iop.GetGpr(i)); // We'll add GetGpr to Iop

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

        // Memory
        int memSize = reader.ReadInt32();
        byte[] memData = reader.ReadBytes(memSize);
        system.Memory.SetRawData(memData);

        // EE
        system.EE.PC = reader.ReadUInt64();
        for (int i = 0; i < 8; i++)
        {
            ulong lo = reader.ReadUInt64();
            ulong hi = reader.ReadUInt64();
            system.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = lo, Hi = hi });
        }

        // IOP
        system.Iop.PC = reader.ReadUInt32();
        for (int i = 0; i < 8; i++)
            system.Iop.SetGpr(i, reader.ReadUInt32());

        return true;
    }
}
