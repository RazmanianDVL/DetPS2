using System;
using System.IO;
using System.IO.Compression;

namespace DetPS2.Core;

/// <summary>
/// Save state v4 (Phase 11): deflate compression, GS FB + VU micro snapshot.
/// No host clocks. Magic DPS2.
/// </summary>
public static class SaveState
{
    private const uint Magic = 0x44505332; // 'DPS2'
    private const uint CurrentVersion = 4;

    public static byte[] Save(Ps2System system) => Save(system, compress: true);

    public static byte[] Save(Ps2System system, bool compress)
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(CurrentVersion);
            writer.Write(system.MasterCycles);

            byte[] mem = system.Memory.GetRawData();
            writer.Write(mem.Length);
            writer.Write(mem);

            byte[] iop = system.Memory.GetIopRamCopy();
            writer.Write(iop.Length);
            writer.Write(iop);
            byte[] spr = system.Memory.GetScratchpadCopy();
            writer.Write(spr.Length);
            writer.Write(spr);

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

            writer.Write(system.Iop.PC);
            for (int i = 0; i < 32; i++)
                writer.Write(system.Iop.GetGpr(i));

            writer.Write(system.Sif.DmaBusy ? 1u : 0u);
            writer.Write(system.Sif.LastCommand);
            writer.Write(system.Sif.GetStatus());

            writer.Write(system.Pad.Buttons);

            var fb = system.Gs.GetFramebufferSpan();
            writer.Write(fb.Length);
            for (int i = 0; i < fb.Length; i++)
                writer.Write(fb[i]);

            writer.Write(system.Vu0.PC);
            writer.Write(system.Vu0.RunningMicro ? 1u : 0u);
            for (uint i = 0; i < 256; i++)
                writer.Write(system.Vu0.ReadMicroWord(i));

            writer.Write(system.Intc.Stat);
            writer.Write(system.Intc.Mask);
        }

        byte[] payload = raw.ToArray();
        if (!compress)
            return payload;

        using var outMs = new MemoryStream();
        using (var bw = new BinaryWriter(outMs, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(Magic);
            bw.Write(CurrentVersion | 0x80000000u);
            bw.Write(payload.Length);
        }
        using (var def = new DeflateStream(outMs, CompressionLevel.Optimal, leaveOpen: true))
            def.Write(payload, 0, payload.Length);
        return outMs.ToArray();
    }

    public static bool Load(Ps2System system, byte[] data)
    {
        if (data == null || data.Length < 16) return false;

        using var outer = new MemoryStream(data);
        using var or = new BinaryReader(outer);
        if (or.ReadUInt32() != Magic) return false;
        uint ver = or.ReadUInt32();

        if ((ver & 0x80000000u) != 0)
        {
            int rawLen = or.ReadInt32();
            using var def = new DeflateStream(outer, CompressionMode.Decompress);
            byte[] payload = new byte[rawLen];
            int read = 0;
            while (read < rawLen)
            {
                int n = def.Read(payload, read, rawLen - read);
                if (n <= 0) break;
                read += n;
            }
            if (read != rawLen) return false;
            return LoadUncompressed(system, payload);
        }

        outer.Position = 0;
        return LoadUncompressed(system, data);
    }

    private static bool LoadUncompressed(Ps2System system, byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        if (reader.ReadUInt32() != Magic) return false;
        uint version = reader.ReadUInt32() & 0x7FFFFFFFu;
        if (version < 3 || version > CurrentVersion) return false;

        ulong savedMasterCycles = reader.ReadUInt64();

        int memSize = reader.ReadInt32();
        system.Memory.SetRawData(reader.ReadBytes(memSize));

        if (version >= 4)
        {
            int iopLen = reader.ReadInt32();
            byte[] iop = reader.ReadBytes(iopLen);
            for (int i = 0; i < iop.Length; i++)
                system.Memory.Write8(SystemMemory.IOP_RAM_BASE + (uint)i, iop[i]);

            int sprLen = reader.ReadInt32();
            byte[] spr = reader.ReadBytes(sprLen);
            for (int i = 0; i < spr.Length; i++)
                system.Memory.Write8(SystemMemory.SPR_BASE + (uint)i, spr[i]);
        }

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

        system.Iop.PC = reader.ReadUInt32();
        for (int i = 0; i < 32; i++)
            system.Iop.SetGpr(i, reader.ReadUInt32());

        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();

        if (version >= 4)
        {
            system.Pad.SetButtons(reader.ReadUInt32());

            int fbLen = reader.ReadInt32();
            for (int i = 0; i < fbLen; i++)
                reader.ReadUInt32();

            uint vuPc = reader.ReadUInt32();
            bool runMicro = reader.ReadUInt32() != 0;
            for (uint i = 0; i < 256; i++)
                system.Vu0.WriteMicroWord(i, reader.ReadUInt32());
            system.Vu0.PC = vuPc;
            if (runMicro) system.Vu0.StartMicro(vuPc);
            else system.Vu0.StopMicro();

            uint stat = reader.ReadUInt32();
            uint mask = reader.ReadUInt32();
            system.Intc.RestoreState(stat, mask);
        }
        else
        {
            for (int ch = 0; ch < 10; ch++)
            {
                reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();
            }
            for (int i = 0; i < 64; i++)
                reader.ReadUInt32();
        }

        system.Scheduler.SetMasterCycles(savedMasterCycles);
        return true;
    }
}
