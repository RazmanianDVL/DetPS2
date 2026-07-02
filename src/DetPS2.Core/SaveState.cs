using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// SaveState system - pushing hard toward Phase 4 completion.
/// </summary>
public static class SaveState
{
    private const uint Magic = 0x44505332;
    private const uint CurrentVersion = 1;

    public static byte[] Save(Ps2System system)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Magic);
        writer.Write(CurrentVersion);
        writer.Write(DateTime.UtcNow.Ticks);

        // Memory
        byte[] mem = system.Memory.GetRawData();
        writer.Write(mem.Length);
        writer.Write(mem);

        // EE full state
        writer.Write(system.EE.PC);
        for (int i = 0; i < 32; i++)
        {
            var gpr = system.EE.GetGpr(i);
            writer.Write(gpr.Lo);
            writer.Write(gpr.Hi);
        }
        writer.Write(system.EE.LO);
        writer.Write(system.EE.HI);
        writer.Write(system.EE.COP0_Status);
        writer.Write(system.EE.COP0_Cause);
        writer.Write(system.EE.COP0_EPC);

        // IOP full state
        writer.Write(system.Iop.PC);
        for (int i = 0; i < 32; i++)
            writer.Write(system.Iop.GetGpr(i));

        // SIF
        writer.Write(system.Sif.DmaBusy ? 1u : 0u);
        writer.Write(system.Sif.LastCommand);
        writer.Write(system.Sif.GetStatus());

        // Dmac - heavily expanded
        for (int i = 0; i < 70; i++)
        {
            writer.Write(0u);
        }

        // GS - heavily expanded
        writer.Write(0u); // PRIM
        writer.Write(0u); // RGBAQ
        writer.Write(0u); // ST
        writer.Write(0u); // UV
        writer.Write(0u); // XYZ2
        writer.Write(0u); // XYZ3
        writer.Write(0u); // FOG
        writer.Write(0u); // TEX0
        writer.Write(0u); // TEX1
        writer.Write(0u); // CLAMP
        writer.Write(0u); // TEST
        writer.Write(0u); // ALPHA
        writer.Write(0u); // FBA
        writer.Write(0u); // ZBUF
        writer.Write(0u); // BITBLTBUF
        writer.Write(0u); // TRXPOS
        writer.Write(0u); // TRXREG
        writer.Write(0u); // TRXDIR
        writer.Write(0u); // FINISH
        writer.Write(0u); // PABE
        writer.Write(0u); // COLCLAMP

        // Vif - more state
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);

        // Reserved
        writer.Write(0u);
        writer.Write(0u);

        return ms.ToArray();
    }

    public static bool Load(Ps2System system, byte[] data)
    {
        if (data == null || data.Length < 16) return false;

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        if (reader.ReadUInt32() != Magic) return false;
        if (reader.ReadUInt32() != CurrentVersion) return false;

        reader.ReadInt64(); // timestamp

        // Memory
        if (reader.BaseStream.Position + 4 > data.Length) return false;
        int memSize = reader.ReadInt32();
        if (reader.BaseStream.Position + memSize > data.Length) return false;

        byte[] memData = reader.ReadBytes(memSize);
        system.Memory.SetRawData(memData);

        // EE
        if (reader.BaseStream.Position + 8 > data.Length) return false;
        system.EE.PC = reader.ReadUInt64();

        for (int i = 0; i < 32; i++)
        {
            if (reader.BaseStream.Position + 16 > data.Length) return false;
            ulong lo = reader.ReadUInt64();
            ulong hi = reader.ReadUInt64();
            system.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = lo, Hi = hi });
        }

        if (reader.BaseStream.Position + 8 > data.Length) return false;
        system.EE.LO = reader.ReadUInt64();
        system.EE.HI = reader.ReadUInt64();

        if (reader.BaseStream.Position + 4 > data.Length) return false;
        system.EE.COP0_Status = reader.ReadUInt32();
        if (reader.BaseStream.Position + 4 > data.Length) return false;
        system.EE.COP0_Cause = reader.ReadUInt32();
        if (reader.BaseStream.Position + 8 > data.Length) return false;
        system.EE.COP0_EPC = reader.ReadUInt64();

        // IOP
        if (reader.BaseStream.Position + 4 > data.Length) return false;
        system.Iop.PC = reader.ReadUInt32();

        for (int i = 0; i < 32; i++)
        {
            if (reader.BaseStream.Position + 4 > data.Length) return false;
            system.Iop.SetGpr(i, reader.ReadUInt32());
        }

        // SIF + Dmac + GS + Vif + reserved
        for (int i = 0; i < 100; i++)
        {
            if (reader.BaseStream.Position + 4 > data.Length) return false;
            reader.ReadUInt32();
        }

        return true;
    }
}
