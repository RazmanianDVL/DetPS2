using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// CDVD (Phases 8/16/24): sector reads, async IRQ, dual-layer stub, stream scheduling.
/// Drive state / error codes ground-truthed against ps2sdk <c>libcdvd-common.h</c> and the
/// decompiled CDVDFSV.IRX mechacon-status checks (ready when <c>(status &amp; 0xc0) == 0x40</c>).
/// </summary>
public sealed class Cdvd : ISchedulable
{
    public const int SectorSize = 2048;

    // SCECdvdErrorCode (ps2sdk libcdvd-common.h)
    public const int ErNO = 0x00;
    public const int ErABRT = 0x01;
    public const int ErOPENS = 0x11;
    public const int ErNODISC = 0x12;
    public const int ErNORDY = 0x13;
    public const int ErREAD = 0x30;
    public const int ErTRMOPN = 0x31;
    public const int ErEOM = 0x32;

    // SCECdvdDriveState
    public const int StatStop = 0x00;
    public const int StatShellOpen = 0x01;
    public const int StatSpin = 0x02;
    public const int StatRead = 0x06;
    public const int StatPause = 0x0A;
    public const int StatSeek = 0x12;
    public const int StatEmg = 0x20;

    // SCECdvdInterruptCode / DiskReady replies
    public const int ReadyComplete = 0x02; // SCECdComplete
    public const int ReadyNotReady = 0x06; // SCECdNotReady

    // SCECdvdTrayReqMode
    public const int TrayReqOpen = 0;
    public const int TrayReqClose = 1;
    public const int TrayReqCheck = 2;

    // Stream subcommands (ps2sdk CdvdStCmd_t / NCMD STREAM arg[3])
    public const int StCmdStart = 1;
    public const int StCmdRead = 2;
    public const int StCmdStop = 3;
    public const int StCmdSeek = 4;
    public const int StCmdInit = 5;
    public const int StCmdStat = 6;
    public const int StCmdPause = 7;
    public const int StCmdResume = 8;
    public const int StCmdSeekF = 9;

    public bool DiscPresent { get; private set; } = true;
    public string DiscId { get; private set; } = "PS2DEMO";
    public bool TrayOpen { get; private set; }
    public uint DiscType { get; private set; } = 0x14; // SCECdPS2DVD
    public uint LastSector { get; private set; }
    public ulong SectorsRead { get; private set; }
    public bool ReadPending { get; private set; }
    public ulong Completions { get; private set; }
    public uint LayerBreakLba { get; private set; } // dual-layer break (0 = single)
    public uint StreamCursor { get; private set; }
    public ulong StreamBytes { get; private set; }
    public uint SectorLatencyCycles { get; set; } = 1000; // Det-stable timing
    /// <summary>Raw mechacon ready bits used by DiskReady: ready when <c>(MechaconStatus &amp; 0xc0) == 0x40</c>
    /// (decomp FUN_00003ee0 / FUN_000032d8).</summary>
    public uint MechaconStatus { get; private set; } = 0x40;
    /// <summary>Last SCECd error (SCMD GetError / FUN_00003e60 → FUN_00004810).</summary>
    public int LastError { get; private set; }
    /// <summary>sceCdStatus drive state (SCECdStat*).</summary>
    public int DriveState { get; private set; } = StatSpin;
    public bool StreamActive { get; private set; }
    public uint StreamBankSectors { get; private set; }
    public uint StreamBanks { get; private set; }

    private IDiscImage? _disc;
    private readonly byte[] _sectorBuffer = new byte[SectorSize];
    private Intc? _intc;
    private SystemMemory? _memForAsync;
    private uint _pendingLba;
    private ulong _readCyclesLeft;
    private uint _pendingCount = 1;
    /// <summary>EE/IOP dest for async multi-sector fill (0 = buffer-only, no DMA out).</summary>
    private uint _pendingDest;
    /// <summary>Completion event-flag id (THREADMAN) optional wake for sceCdSync waiters.</summary>
    private int _completionEfId;
    private KernelState? _kernelForComplete;
    private uint _streamBufMax;
    private uint _streamIopBuf;

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
        _pendingDest = 0;
        _memForAsync = null;
        _completionEfId = 0;
        _kernelForComplete = null;
        StreamCursor = 0;
        StreamBytes = 0;
        StreamActive = false;
        StreamBankSectors = 0;
        StreamBanks = 0;
        _streamBufMax = 0;
        _streamIopBuf = 0;
        LayerBreakLba = 0;
        MechaconStatus = 0x40;
        LastError = ErNO;
        DriveState = StatSpin;
        Array.Clear(_sectorBuffer);
        // Do not dispose disc on soft reset mid-boot; use Unmount for full clear
    }

    /// <summary>CDVD controller state for SaveState.cs. Deliberately does NOT save/restore
    /// the mounted disc itself (_disc) — that's boot media, set up from the user's media
    /// config when a title is loaded, not runtime state; a save file isn't expected to carry
    /// disc bytes. What matters at runtime is where the drive currently is (LastSector, an
    /// in-flight async read's countdown/target, tray state) so a load mid-read resumes the
    /// same read instead of silently dropping it and leaving the game waiting forever for a
    /// completion that will never come.</summary>
    public void WriteState(System.IO.BinaryWriter w)
    {
        w.Write(DiscPresent);
        w.Write(DiscId);
        w.Write(TrayOpen);
        w.Write(DiscType);
        w.Write(LastSector);
        w.Write(SectorsRead);
        w.Write(ReadPending);
        w.Write(Completions);
        w.Write(LayerBreakLba);
        w.Write(StreamCursor);
        w.Write(StreamBytes);
        w.Write(SectorLatencyCycles);
        w.Write(MechaconStatus);
        w.Write(_pendingLba);
        w.Write(_readCyclesLeft);
        w.Write(_pendingCount);
        w.Write(TocTracks);
        w.Write(TocLeadOutSector);
        w.Write(_sectorBuffer.Length);
        w.Write(_sectorBuffer);
    }

    public void ReadState(System.IO.BinaryReader r)
    {
        DiscPresent = r.ReadBoolean();
        DiscId = r.ReadString();
        TrayOpen = r.ReadBoolean();
        DiscType = r.ReadUInt32();
        LastSector = r.ReadUInt32();
        SectorsRead = r.ReadUInt64();
        ReadPending = r.ReadBoolean();
        Completions = r.ReadUInt64();
        LayerBreakLba = r.ReadUInt32();
        StreamCursor = r.ReadUInt32();
        StreamBytes = r.ReadUInt64();
        SectorLatencyCycles = r.ReadUInt32();
        MechaconStatus = r.ReadUInt32();
        _pendingLba = r.ReadUInt32();
        _readCyclesLeft = r.ReadUInt64();
        _pendingCount = r.ReadUInt32();
        TocTracks = r.ReadUInt32();
        TocLeadOutSector = r.ReadUInt32();
        int bufLen = r.ReadInt32();
        byte[] buf = r.ReadBytes(bufLen);
        Buffer.BlockCopy(buf, 0, _sectorBuffer, 0, Math.Min(bufLen, _sectorBuffer.Length));
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
        return TrayRequest(TrayOpen ? TrayReqClose : TrayReqOpen);
    }

    /// <summary>
    /// sceCdTrayReq (SCMD case 5 / FUN_00003e88). Modes from SCECdvdTrayReqMode.
    /// Returns 1 on success; tray-change flag is available via <see cref="TrayOpen"/>.
    /// </summary>
    public uint TrayRequest(int mode)
    {
        switch (mode)
        {
            case TrayReqOpen:
                TrayOpen = true;
                DiscPresent = false;
                CancelAsyncInternal(keepError: false);
                LastError = ErOPENS;
                DriveState = StatShellOpen;
                MechaconStatus = 0x01;
                return 1;
            case TrayReqClose:
                TrayOpen = false;
                DiscPresent = true;
                LastError = ErNO;
                DriveState = StatSpin;
                MechaconStatus = 0x40;
                return 1;
            case TrayReqCheck:
                // Report current tray state only; no transition.
                return 1;
            default:
                return 1;
        }
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
        if (!CanAccessDisc(out _)) return 0;
        _pendingLba = lba;
        _pendingCount = Math.Max(1u, count);
        _pendingDest = 0;
        _memForAsync = null;
        _readCyclesLeft = SectorLatencyCycles * _pendingCount;
        ReadPending = true;
        DriveState = StatRead;
        MechaconStatus = 0x80; // busy (bits 7:6 != 01)
        return 1;
    }

    /// <summary>
    /// Async multi-sector read that DMA-fills <paramref name="destAddr"/> on completion
    /// (BIOS CDVDFSV NCMD path). Optional event-flag bit for WaitEventFlag-style sceCdSync.
    /// </summary>
    public uint BeginAsyncReadTo(SystemMemory mem, uint lba, uint count, uint destAddr,
        KernelState? kernel = null, int completionEfId = 0)
    {
        if (!CanAccessDisc(out _)) return 0;
        _pendingLba = lba;
        _pendingCount = Math.Max(1u, Math.Min(count, 512u));
        _pendingDest = destAddr;
        _memForAsync = mem;
        _kernelForComplete = kernel;
        _completionEfId = completionEfId;
        // Short but non-zero latency so RPC_END can land before busy clears when polled same-slice.
        _readCyclesLeft = Math.Max(200u, SectorLatencyCycles) * Math.Min(_pendingCount, 8u);
        ReadPending = true;
        DriveState = StatRead;
        MechaconStatus = 0x80;
        return 1;
    }

    /// <summary>Synchronous multi-sector fill used when NCMD must complete inside RPC_END.</summary>
    public uint ReadSectorsTo(SystemMemory mem, uint lba, uint count, uint destAddr)
    {
        if (!CanAccessDisc(out _)) return 0;
        count = Math.Min(count, 512u);
        uint ok = 0;
        DriveState = StatRead;
        MechaconStatus = 0x80;
        for (uint i = 0; i < count; i++)
        {
            if (!ReadSector(lba + i)) break;
            if (destAddr != 0) CopySectorToMemory(mem, destAddr + i * (uint)SectorSize);
            ok++;
        }
        if (ok == count)
            LastError = ErNO;
        else if (LastError == ErNO)
            LastError = ErREAD;
        SetReadySpin();
        return ok;
    }

    /// <summary>NCMD SEEK (case 5): update head position; return 1 on accept.</summary>
    public int SeekTo(uint lsn)
    {
        if (!CanAccessDisc(out _)) return 0;
        LastSector = lsn;
        StreamCursor = lsn;
        DriveState = StatSeek;
        // Seek completes inside RPC (decomp does FUN_00004828(2) after seek).
        SetReadySpin();
        LastError = ErNO;
        return 1;
    }

    /// <summary>NCMD STANDBY (case 6).</summary>
    public int Standby()
    {
        if (TrayOpen) { LastError = ErOPENS; return 0; }
        CancelAsyncInternal(keepError: false);
        DiscPresent = true;
        DriveState = StatSpin;
        MechaconStatus = 0x40;
        LastError = ErNO;
        return 1;
    }

    /// <summary>NCMD STOP (case 7). Command completes inside RPC (decomp + sceCdSync(2));
    /// drive is stopped but not mid-command-busy, so DiskReady stays Complete.</summary>
    public int Stop()
    {
        CancelAsyncInternal(keepError: false);
        StreamActive = false;
        DriveState = StatStop;
        // Mechacon command-ready bit stays 0x40 after stop completes (not mid-NCMD busy).
        MechaconStatus = 0x40;
        LastError = ErNO;
        return 1;
    }

    /// <summary>NCMD PAUSE (case 8).</summary>
    public int Pause()
    {
        CancelAsyncInternal(keepError: false);
        DriveState = StatPause;
        MechaconStatus = 0x40; // not mid-command; paused drive is "ready" for DiskReady
        LastError = ErNO;
        return 1;
    }

    /// <summary>
    /// NCMD DISK READY (case 0xe / FUN_00003ee0) and SID 0x8000059a (FUN_000032d8):
    /// SCECdComplete (2) when ready, SCECdNotReady (6) when busy/not ready.
    /// Decomp: ready iff <c>(DAT_bf402005 &amp; 0xc0) == 0x40</c>.
    /// </summary>
    public int DiskReady()
    {
        // In-flight async NCMD or tray open → not ready.
        if (ReadPending || TrayOpen)
            return ReadyNotReady;
        if ((MechaconStatus & 0xc0) != 0x40)
            return ReadyNotReady;
        return ReadyComplete;
    }

    /// <summary>sceCdSync-style: 0=complete/ready, 1=busy.</summary>
    public int SyncStatus => ReadPending ? 1 : 0;

    /// <summary>Cancel in-flight async read (sceCdBreak / SCMD 0x16).</summary>
    public void CancelAsync()
    {
        if (ReadPending)
            LastError = ErABRT;
        CancelAsyncInternal(keepError: true);
    }

    private void CancelAsyncInternal(bool keepError)
    {
        ReadPending = false;
        _readCyclesLeft = 0;
        _pendingDest = 0;
        _memForAsync = null;
        _kernelForComplete = null;
        _completionEfId = 0;
        if (!TrayOpen)
        {
            // Preserve Stop/Pause visual state; clear mid-command busy bit so DiskReady completes.
            if (DriveState != StatStop && DriveState != StatPause)
                DriveState = StatSpin;
            MechaconStatus = 0x40;
        }
        if (!keepError && LastError == ErABRT)
            LastError = ErNO;
    }

    /// <summary>Start sequential stream from LBA (legacy single-arg path / ST_CMD_START).</summary>
    public uint BeginStream(uint lba)
    {
        if (!CanAccessDisc(out _)) return 0;
        StreamCursor = lba;
        StreamActive = true;
        DriveState = StatSpin;
        MechaconStatus = 0x40;
        return 1;
    }

    /// <summary>
    /// NCMD STREAM (case 9 / FUN_00001d5c): subcommand in <paramref name="cmd"/> (CdvdStCmd_t).
    /// Args match ps2sdk <c>sceCdStream</c>: lbn, nsectors, buf, cmd.
    /// </summary>
    public int StreamCommand(uint lbn, uint nsectors, uint buf, int cmd, SystemMemory? mem = null)
    {
        switch (cmd)
        {
            case StCmdInit:
                // sceCdStInit(bufmax, bankmax, buf): lbn=bufmax sectors, nsectors=banks, buf=IOP buffer
                _streamBufMax = lbn;
                StreamBanks = Math.Max(1u, nsectors);
                StreamBankSectors = StreamBanks == 0 ? 0 : _streamBufMax / StreamBanks;
                _streamIopBuf = buf;
                StreamActive = false;
                StreamCursor = 0;
                return 1;
            case StCmdStart:
                return (int)BeginStream(lbn);
            case StCmdStop:
                StreamActive = false;
                return 1;
            case StCmdSeek:
            case StCmdSeekF:
                StreamCursor = lbn;
                return 1;
            case StCmdPause:
                StreamActive = false;
                DriveState = StatPause;
                return 1;
            case StCmdResume:
                StreamActive = true;
                DriveState = StatSpin;
                MechaconStatus = 0x40;
                return 1;
            case StCmdStat:
                // Sectors available in stream buffer — report full bank when active.
                return StreamActive ? (int)Math.Max(1u, StreamBankSectors) : 0;
            case StCmdRead:
            {
                if (!StreamActive && StreamCursor == 0 && lbn == 0)
                    return 0;
                uint toRead = Math.Max(1u, Math.Min(nsectors == 0 ? 1u : nsectors, 64u));
                if (mem != null && buf != 0)
                {
                    uint got = ReadSectorsTo(mem, StreamCursor, toRead, buf);
                    return (int)got;
                }
                // No EE dest: advance cursor and count as success for IOP-side buffer paths.
                for (uint i = 0; i < toRead; i++)
                {
                    if (!ReadSector(StreamCursor + i))
                        return (int)i;
                }
                return (int)toRead;
            }
            default:
                return 1;
        }
    }

    public bool ReadSector(uint lba)
    {
        if (TrayOpen)
        {
            Array.Clear(_sectorBuffer);
            LastError = ErOPENS;
            DriveState = StatShellOpen;
            return false;
        }
        if (!DiscPresent)
        {
            Array.Clear(_sectorBuffer);
            LastError = ErNODISC;
            return false;
        }

        // Dual-layer: LBA past break maps linearly still (image is flat); report layer via status
        LastSector = lba;
        Array.Clear(_sectorBuffer);

        if (_disc != null)
        {
            long offset = (long)lba * SectorSize;
            if (offset >= _disc.Length)
            {
                LastError = ErEOM;
                return false;
            }
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
        LastError = ErNO;
        if (!ReadPending)
            SetReadySpin();
        return true;
    }

    private bool CanAccessDisc(out int error)
    {
        if (TrayOpen) { error = ErOPENS; LastError = ErOPENS; DriveState = StatShellOpen; return false; }
        if (!DiscPresent) { error = ErNODISC; LastError = ErNODISC; return false; }
        error = ErNO;
        return true;
    }

    private void SetReadySpin()
    {
        DriveState = StatSpin;
        MechaconStatus = 0x40;
    }

    /// <summary>Count host-side ISO reads (CRI HLE etc.) toward <see cref="SectorsRead"/> telemetry.</summary>
    public void NoteHostReadSectors(int sectors)
    {
        if (sectors <= 0) return;
        SectorsRead += (ulong)sectors;
        StreamBytes += (ulong)sectors * SectorSize;
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
        // Complete all pending sectors; DMA out when dest was set by BeginAsyncReadTo.
        for (uint i = 0; i < _pendingCount; i++)
        {
            if (!ReadSector(_pendingLba + i)) break;
            if (_pendingDest != 0 && _memForAsync != null)
                CopySectorToMemory(_memForAsync, _pendingDest + i * (uint)SectorSize);
        }
        SetReadySpin();
        if (_completionEfId != 0 && _kernelForComplete != null)
            _kernelForComplete.SetEventFlag(_completionEfId, 1u);
        _pendingDest = 0;
        _memForAsync = null;
        _kernelForComplete = null;
        _completionEfId = 0;
        return (int)used;
    }
}
