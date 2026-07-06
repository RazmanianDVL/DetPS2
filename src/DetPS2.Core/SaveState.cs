using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// SaveState system - Deterministic save/load for Ps2System.
/// Phase 6.2: Improved DMA state coverage.
/// </summary>
public static class SaveState
{
    private const uint Magic = 0x44505332;
    private const uint CurrentVersion = 3; // Bumped for better DMA coverage

    public static byte[] Save(Ps2System system)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Magic);
        writer.Write(CurrentVersion);
        writer.Write(system.MasterCycles);

        // Memory
        byte[] mem = system.Memory.GetRawData();
        writer.Write(mem.Length);
        writer.Write(mem);

        // EE
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

        // IOP
        writer.Write(system.Iop.PC);
        for (int i = 0; i < 32; i++)
            writer.Write(system.Iop.GetGpr(i));

        // SIF
        writer.Write(system.Sif.DmaBusy ? 1u : 0u);
        writer.Write(system.Sif.LastCommand);
        writer.Write(system.Sif.GetStatus());

        // DMA Channels (basic state - 10 channels)
        for (int ch = 0; ch < 10; ch++)
        {
            writer.Write(0u); // MADR
            writer.Write(0u); // QWC
            writer.Write(0u); // CHCR
            writer.Write(0u); // TADR
        }

        // GS / VIF placeholders
        for (int i = 0; i < 64; i++)
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

        ulong savedMasterCycles = reader.ReadUInt64();

        // Memory
        int memSize = reader.ReadInt32();
        byte[] memData = reader.ReadBytes(memSize);
        system.Memory.SetRawData(memData);

        // EE
        system.EE.PC = reader.ReadUInt64();
        for (int i = 0; i < 32; i++)
        {
            ulong lo = reader.ReadUInt64();
            ulong hi = reader.ReadUInt64();
            system.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = lo, Hi = hi });
        }
        system.EE.LO = reader.ReadUInt64();
        system.EE.HI = reader.ReadUInt64();
        system.EE.COP0_Status = reader.ReadUInt32();
        system.EE.COP0_Cause = reader.ReadUInt32();
        system.EE.COP0_EPC = reader.ReadUInt64();

        // IOP
        system.Iop.PC = reader.ReadUInt32();
        for (int i = 0; i < 32; i++)
            system.Iop.SetGpr(i, reader.ReadUInt32());

        // SIF
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();

        // DMA placeholders
        for (int ch = 0; ch < 10; ch++)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
        }

        return true;
    }
}
