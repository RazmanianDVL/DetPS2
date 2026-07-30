using System;
using System.IO;
using System.IO.Compression;

namespace DetPS2.Core;

/// <summary>
/// Save state v6: same layout as v5 plus KernelState THREADMAN fields that v5 omitted
/// (EverStarted/SoftSuspended/SuspendCount/WakeupCount/Sema.InitCount). Older v5 files
/// refuse to load once Kernel.ReadState advances (see <see cref="CurrentVersion"/>).
///
/// Save state v5: deflate compression, full-system snapshot.
///
/// v4 and earlier saved a narrow slice of state: EE/IOP GPRs and three COP0 fields each, raw
/// RAM, the GS framebuffer's pixel *bytes* (written but never read back on load — dead code,
/// so a load never actually restored the picture on screen), and VU0's micro memory + PC. Not
/// saved at all: KernelHle thread/semaphore/event-flag state (a load resumed every thread as
/// if freshly booted, even mid-game with a dozen threads genuinely blocked), VU1 (nothing —
/// the unit real 3D games use for actual per-frame vertex work), GS registers/local VRAM/depth
/// buffer (a load lost every uploaded texture and drawing-context register), per-channel DMAC
/// state, SPU2 (silence after every load), CDVD's in-flight read, EE/IOP timers, or
/// SonyKernelHle's game-registered interrupt/DMA handler tables (without which a load makes
/// every future real interrupt dispatch into a game's own handler go nowhere). A save/load
/// mid-boot on a multi-thread commercial title would silently resume with badly wrong
/// scheduler and hardware state instead of failing loudly — worse than not supporting save
/// states at all. Each subsystem now owns its own WriteState/ReadState (see e.g. Gs.cs,
/// KernelHle.cs, VectorUnit.cs) and this file just sequences them.
///
/// v3/v4 files still load (LoadV3OrV4) for backward compatibility, but only ever restored what
/// they saved — loading an old file into a running multi-thread game has the same gaps the old
/// writer did, inherently, since the extra state was never captured.
/// </summary>
public static class SaveState
{
    private const uint Magic = 0x44505332; // 'DPS2'
    private const uint CurrentVersion = 6;

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

            system.EE.WriteState(writer);
            system.Iop.WriteState(writer);

            writer.Write(system.Sif.DmaBusy ? 1u : 0u);
            writer.Write(system.Sif.LastCommand);
            writer.Write(system.Sif.GetStatus());

            writer.Write(system.Pad.Buttons);
            writer.Write(system.Pad.Lx); writer.Write(system.Pad.Ly);
            writer.Write(system.Pad.Rx); writer.Write(system.Pad.Ry);
            writer.Write(system.Pad.AnalogMode);

            system.Gs.WriteState(writer);
            system.Vu0.WriteState(writer);
            system.Vu1.WriteState(writer);

            writer.Write(system.Intc.Stat);
            writer.Write(system.Intc.Mask);

            system.Dmac.WriteState(writer);
            system.Cdvd.WriteState(writer);
            system.Spu2.WriteState(writer);
            system.Timers.WriteState(writer);

            system.Hle.Kernel.WriteState(writer);
            writer.Write(system.Hle.Sony != null);
            system.Hle.Sony?.WriteState(writer);
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

        // v5 and v6 share LoadV5 sequencing; Kernel.ReadState itself is only compatible with
        // the writer that produced the file. CurrentVersion=6 writers emit the extended
        // THREADMAN fields — loading a v5 blob into a v6 Kernel.ReadState would desync the
        // stream, so v5 is no longer accepted once we ship v6.
        bool ok = version >= 6
            ? LoadV5(system, reader)
            : version >= 5
                ? false // v5 kernel blob is not forward-compatible with v6 THREADMAN fields
                : LoadV3OrV4(system, reader, version);
        if (!ok) return false;

        system.Scheduler.SetMasterCycles(savedMasterCycles);
        return true;
    }

    private static bool LoadV5(Ps2System system, BinaryReader reader)
    {
        system.EE.ReadState(reader);
        system.Iop.ReadState(reader);

        reader.ReadUInt32(); // Sif.DmaBusy
        reader.ReadUInt32(); // Sif.LastCommand
        reader.ReadUInt32(); // Sif.GetStatus() — Sif itself has no restore hook; these three
                              // fields settle back out within a tick or two of real traffic,
                              // same as v3/v4 always treated them.

        system.Pad.SetButtons(reader.ReadUInt32());
        byte lx = reader.ReadByte(), ly = reader.ReadByte(), rx = reader.ReadByte(), ry = reader.ReadByte();
        bool analog = reader.ReadBoolean();
        system.Pad.SetLeftStick(lx, ly);
        system.Pad.SetRightStick(rx, ry);
        if (!analog) system.Pad.AnalogMode = false;

        system.Gs.ReadState(reader);
        system.Vu0.ReadState(reader);
        system.Vu1.ReadState(reader);

        uint stat = reader.ReadUInt32();
        uint mask = reader.ReadUInt32();
        system.Intc.RestoreState(stat, mask);

        system.Dmac.ReadState(reader);
        system.Cdvd.ReadState(reader);
        system.Spu2.ReadState(reader);
        system.Timers.ReadState(reader);

        system.Hle.Kernel.ReadState(reader);
        bool hasSony = reader.ReadBoolean();
        if (hasSony)
        {
            if (system.Hle.Sony == null) system.Hle.EnableSonyKernel();
            system.Hle.Sony?.ReadState(reader);
        }
        return true;
    }

    /// <summary>Backward-compat path for files saved by the v3/v4 writer — unchanged from
    /// before v5 existed. Only ever restores what those versions saved (see this class's own
    /// doc comment for the gaps that were always there).</summary>
    private static bool LoadV3OrV4(Ps2System system, BinaryReader reader, uint version)
    {
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
            var fb = new uint[fbLen];
            for (int i = 0; i < fbLen; i++)
                fb[i] = reader.ReadUInt32();
            system.Gs.RestoreFramebuffer(fb);

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

        return true;
    }
}
