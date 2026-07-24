using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// CDVD (Phases 8/16/24): sector reads, async IRQ, dual-layer stub, stream scheduling.
/// </summary>
public sealed class Cdvd : ISchedulable
{
    public const int SectorSize = 2048;

    public bool DiscPresent { get; private set; } = true;
    public string DiscId { get; private set; } = "PS2DEMO";
    public bool TrayOpen { get; private set; }
    public uint DiscType { get; private set; } = 0x14; // PS2 DVD
    public uint LastSector { get; private set; }
    public ulong SectorsRead { get; private set; }
    public bool ReadPending { get; private set; }
    public ulong Completions { get; private set; }
    public uint LayerBreakLba { get; private set; } // dual-layer break (0 = single)
    public uint StreamCursor { get; private set; }
    public ulong StreamBytes { get; private set; }
    public uint SectorLatencyCycles { get; set; } = 1000; // Det-stable timing
    public uint MechaconStatus { get; private set; } = 0x40; // ready-ish

    private IDiscImage? _disc;
    private readonly byte[] _sectorBuffer = new byte[SectorSize];
    private Intc? _intc;
    private uint _pendingLba;
    private ulong _readCyclesLeft;
    private uint _pendingCount = 1;
    private uint _streamRemaining;

    public uint TocTracks { get; private set; } = 1;
    public uint TocLeadOutSector { get; private set; } = 100_000;
    public string? MountedPath => _disc?.SourcePath;
    public long ImageLength => _disc?.Length ?? 0;

    public Cdvd() => Reset();

    public void SetIntc(Intc intc) => _intc = intc;

    public void Reset()
    {
        DiscPresent = true;
        TrayOpen = false;
        DiscType = 0x14;
        LastSector = 0;
        SectorsRead = 0;
        ReadPending = false;
        Completions = 0;
        _pendingLba = 0;
        _readCyclesLeft = 0;
        _pendingCount = 1;
        StreamCursor = 0;
        StreamBytes = 0;
        _streamRemaining = 0;
        LayerBreakLba = 0;
        MechaconStatus = 0x40;
        Array.Clear(_sectorBuffer);
        // Do not dispose disc on soft reset mid-boot; use Unmount for full clear
    }

    public void Unmount()
    {
        try { _disc?.Dispose(); } catch { /* ignore */ }
        _disc = null;
        DiscId = "PS2DEMO";
        TocLeadOutSector = 100_000;
        LayerBreakLba = 0;
    }

    /// <summary>Mount ISO/BIN from path (local or UNC). Does not load whole file into RAM.</summary>
    public bool MountIso(string? path)
    {
        Unmount();
        if (string.IsNullOrEmpty(path))
        {
            DiscId = "PS2DEMO";
            return true;
        }
        try
        {
            path = FileDiscImage.NormalizePath(path);
            if (!File.Exists(path)) return false;
            _disc = new FileDiscImage(path);
            DiscId = Path.GetFileNameWithoutExtension(path);
            DiscPresent = true;
            TrayOpen = false;
            TocLeadOutSector = (uint)Math.Max(1, _disc.Length / SectorSize);
            DetectDualLayer();
            return true;
        }
        catch
        {
            Unmount();
            return false;
        }
    }

    public void MountImage(ReadOnlySpan<byte> image, string discId = "MEMDISC")
    {
        Unmount();
        _disc = new MemoryDiscImage(image.ToArray());
        DiscId = discId;
        DiscPresent = true;
        TocLeadOutSector = (uint)Math.Max(1, _disc.Length / SectorSize);
        DetectDualLayer();
    }

    public void MountDisc(IDiscImage disc, string? discId = null)
    {
        if (disc == null) throw new ArgumentNullException(nameof(disc));
        // Keep same instance if re-mounting; only dispose a different previous image
        if (!ReferenceEquals(_disc, disc))
        {
            try { _disc?.Dispose(); } catch { /* ignore */ }
            _disc = disc;
        }
        DiscId = discId ?? Path.GetFileNameWithoutExtension(disc.SourcePath ?? "DISC");
        DiscPresent = true;
        TrayOpen = false;
        TocLeadOutSector = (uint)Math.Max(1, _disc.Length / SectorSize);
        DetectDualLayer();
    }

    private void DetectDualLayer()
    {
        if (_disc != null && _disc.Length > 2_500_000_000L)
            LayerBreakLba = (uint)(_disc.Length / SectorSize / 2);
        else if (LayerBreakLba == 0)
            LayerBreakLba = 0;
    }

    public void SetDualLayerBreak(uint lba) => LayerBreakLba = lba;

    public uint SendCommand(uint command, uint param)
    {
        switch (command)
        {
            case 0x01: return 0;
            case 0x03:
            case 0x0A: return DiscType;
            case 0x05: return TrayOpen ? 1u : 0u;
            case 0x06: return ToggleTray();
            case 0x08: return (uint)DiscId.Length;
            case 0x09: return ReadToc(param);
            case 0x12: return ReadSector(param) ? 1u : 0u;
            case 0x13: return BeginAsyncRead(param);
            case 0x14: return BeginAsyncReadN(param, 1);
            case 0x15: return DiscPresent ? TocLeadOutSector : 0;
            case 0x16: return LayerBreakLba;
            case 0x17: return MechaconStatus;
            case 0x18: return BeginStream(param);
            case 0x19: return StreamCursor;
            case 0x1A:
                SectorLatencyCycles = Math.Max(100u, param);
                return SectorLatencyCycles;
            default: return 0;
        }
    }

    private uint ToggleTray()
    {
        TrayOpen = !TrayOpen;
        DiscPresent = !TrayOpen;
        MechaconStatus = TrayOpen ? 0x01u : 0x40u;
        return 0;
    }

    public uint ReadToc(uint field) => field switch
    {
        0 => TocTracks,
        1 => TocLeadOutSector,
        2 => DiscType,
        3 => LayerBreakLba,
        _ => 0
    };

    public uint BeginAsyncRead(uint lba) => BeginAsyncReadN(lba, 1);

    public uint BeginAsyncReadN(uint lba, uint count)
    {
        if (!DiscPresent || TrayOpen) return 0;
        _pendingLba = lba;
        _pendingCount = Math.Max(1u, count);
        _readCyclesLeft = SectorLatencyCycles * _pendingCount;
        ReadPending = true;
        MechaconStatus = 0x80; // busy
        return 1;
    }

    /// <summary>Start sequential stream from LBA for `count` sectors (Step delivers).</summary>
    public uint BeginStream(uint lba)
    {
        if (!DiscPresent || TrayOpen) return 0;
        StreamCursor = lba;
        _streamRemaining = 0xFFFF; // open-ended until cancelled; tests use ReadSector
        return 1;
    }

    public bool ReadSector(uint lba)
    {
        if (!DiscPresent || TrayOpen)
        {
            Array.Clear(_sectorBuffer);
            return false;
        }

        // Dual-layer: LBA past break maps linearly still (image is flat); report layer via status
        LastSector = lba;
        Array.Clear(_sectorBuffer);

        if (_disc != null)
        {
            long offset = (long)lba * SectorSize;
            if (offset < _disc.Length)
                _disc.ReadAt(offset, _sectorBuffer.AsSpan(0, SectorSize));
        }
        else
        {
            WriteU32(_sectorBuffer, 0, 0x44455643);
            WriteU32(_sectorBuffer, 4, lba);
            WriteU32(_sectorBuffer, 8, 0xDEADBEEF);
            if (LayerBreakLba != 0 && lba >= LayerBreakLba)
                WriteU32(_sectorBuffer, 12, 1); // layer 1 marker
        }

        SectorsRead++;
        Completions++;
        StreamCursor = lba + 1;
        StreamBytes += SectorSize;
        // CDVD completion signals via SBUS/SIF on real HW; raise SIF so EE-side
        // waiters (and our HLE) observe activity. Also IPU was historically used
        // as a stand-in — keep SIF as the primary notify.
        _intc?.Raise(Intc.InterruptSource.Sif);
        MechaconStatus = 0x40;
        return true;
    }

    public ReadOnlySpan<byte> GetSectorBuffer() => _sectorBuffer;

    public void CopySectorToMemory(SystemMemory memory, uint destAddr)
    {
        for (int i = 0; i < SectorSize; i++)
            memory.Write8(destAddr + (uint)i, _sectorBuffer[i]);
    }

    private static void WriteU32(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)v;
        buf[off + 1] = (byte)(v >> 8);
        buf[off + 2] = (byte)(v >> 16);
        buf[off + 3] = (byte)(v >> 24);
    }

    public int Step(ulong maxCycles)
    {
        if (!ReadPending || maxCycles == 0) return 0;
        if (_readCyclesLeft > maxCycles)
        {
            _readCyclesLeft -= maxCycles;
            return (int)maxCycles;
        }
        ulong used = _readCyclesLeft;
        _readCyclesLeft = 0;
        ReadPending = false;
        // Complete all pending sectors (last buffer holds final LBA)
        for (uint i = 0; i < _pendingCount; i++)
            ReadSector(_pendingLba + i);
        return (int)used;
    }
}
