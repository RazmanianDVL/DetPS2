using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// CDVD (Phases 8/16/24 + WP-18): sector reads, async IRQ, dual-layer stub, stream scheduling,
/// and IOP MMIO window <c>0x1F402000</c> (KSEG1 <c>0xBF402000</c>) that real CDVDMAN pokes.
/// Drive state / error codes ground-truthed against ps2sdk <c>libcdvd-common.h</c>, PCSX2 CDVD
/// register map, and decompiled CDVDFSV.IRX mechacon-status checks
/// (ready when <c>(DAT_bf402005 &amp; 0xc0) == 0x40</c>).
/// </summary>
public sealed class Cdvd : ISchedulable
{
    public const int SectorSize = 2048;

    /// <summary>IOP physical base for CDVD registers (SSBUSC device 5 default).</summary>
    public const uint PhysBase = 0x1F402000;
    /// <summary>Register page size; hardware mirrors across lower 8 bits of the address.</summary>
    public const uint PhysSize = 0x100;

    // Ready register (0x05) sticky bits — PCSX2 always ORs MECHA_INIT|DEV9 into Ready.
    public const byte ReadyBitError = 0x01;
    public const byte ReadyBitDev9 = 0x04;
    public const byte ReadyBitMechaInit = 0x08;
    public const byte ReadyBitReady = 0x40;
    public const byte ReadyBitBusy = 0x80;
    public const byte ReadyStickyBits = ReadyBitDev9 | ReadyBitMechaInit; // 0x0C

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

    // ── IOP MMIO register state (WP-18) ──────────────────────────────────────
    /// <summary>Last N-command written to 0x04.</summary>
    public byte NCommand { get; private set; }
    /// <summary>Interrupt reason register 0x08 (W1C). Bit0 = command complete.</summary>
    public byte IntrStat { get; private set; }
    /// <summary>Sticky status (0x0B) — accumulates Status bits; tray SCMD resets open bit.</summary>
    public byte StatusSticky { get; private set; }
    /// <summary>S-command written to 0x16.</summary>
    public byte SCommand { get; private set; }
    /// <summary>S-data-in ready flag (0x17). Bit6 set = no pending SCMD result bytes.</summary>
    public byte SDataIn { get; private set; } = 0x40;
    /// <summary>Count of unknown MMIO accesses (telemetry / hang diagnosis).</summary>
    public ulong UnknownMmioAccesses { get; private set; }
    /// <summary>Count of MMIO NCMD / SCMD issues (telemetry).</summary>
    public ulong MmioCommands { get; private set; }

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

    // MMIO param / result FIFOs (hardware NDATAIN / SDATAIN / SDATAOUT)
    private readonly byte[] _ncmdParams = new byte[16];
    private int _ncmdParamPos;
    private int _ncmdParamCnt;
    private readonly byte[] _scmdParams = new byte[16];
    private int _scmdParamPos;
    private int _scmdParamCnt;
    private readonly byte[] _scmdResult = new byte[16];
    private int _scmdResultPos;
    private int _scmdResultCnt;
    private byte _mmioError; // latched error cleared on reg 0x06 read
    private bool _abortRequested;

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
        NCommand = 0;
        IntrStat = 0;
        StatusSticky = 0;
        SCommand = 0;
        SDataIn = 0x40;
        UnknownMmioAccesses = 0;
        MmioCommands = 0;
        _ncmdParamPos = _ncmdParamCnt = 0;
        _scmdParamPos = _scmdParamCnt = 0;
        _scmdResultPos = _scmdResultCnt = 0;
        _mmioError = 0;
        _abortRequested = false;
        Array.Clear(_sectorBuffer);
        Array.Clear(_ncmdParams);
        Array.Clear(_scmdParams);
        Array.Clear(_scmdResult);
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
        w.Write(LastError);
        w.Write(DriveState);
        w.Write(StreamActive);
        w.Write(StreamBanks);
        w.Write(StreamBankSectors);
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
        LastError = r.ReadInt32();
        DriveState = r.ReadInt32();
        StreamActive = r.ReadBoolean();
        StreamBanks = r.ReadUInt32();
        StreamBankSectors = r.ReadUInt32();
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
            SetMountedReady();
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
        TrayOpen = false;
        TocLeadOutSector = (uint)Math.Max(1, _disc.Length / SectorSize);
        DetectDualLayer();
        SetMountedReady();
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
        SetMountedReady();
    }

    /// <summary>
    /// After a successful mount: clear tray-open / read errors and put the mechacon stand-in
    /// into SCECdComplete-ready spin (matches retail CDVDMAN post-insert settle).
    /// </summary>
    private void SetMountedReady()
    {
        CancelAsyncInternal(keepError: false);
        LastError = ErNO;
        DriveState = StatSpin;
        MechaconStatus = 0x40;
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
        MechaconStatus = ReadyBitReady;
        NoteStatusSticky();
    }

    private void NoteStatusSticky()
    {
        StatusSticky = (byte)(StatusSticky | (byte)DriveState);
    }

    /// <summary>
    /// IOP Ready register (0x05 / <c>DAT_bf402005</c>) value exposed to CDVDMAN.
    /// Ready when <c>(ComposeReady() &amp; 0xc0) == 0x40</c>. Always includes MECHA_INIT|DEV9
    /// sticky bits (PCSX2 <c>cdvdUpdateReady</c> / Cold Fear).
    /// </summary>
    public byte ComposeReady()
    {
        byte core = (byte)(MechaconStatus & (ReadyBitReady | ReadyBitBusy | ReadyBitError));
        if (ReadPending)
            core = (byte)((core & ~ReadyBitReady) | ReadyBitBusy);
        else if (TrayOpen)
            core = (byte)(core & ~(ReadyBitReady | ReadyBitBusy));
        else if ((core & ReadyBitBusy) == 0)
            core = (byte)((core & ~ReadyBitBusy) | ReadyBitReady);
        return (byte)(core | ReadyStickyBits);
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

    // ═══════════════════════════════════════════════════════════════════════════
    // IOP MMIO window 0x1F4020xx (WP-18) — surface real CDVDMAN expects
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>True if physical IOP address hits the CDVD register window.</summary>
    public static bool IsMmioAddress(uint paddr)
    {
        paddr &= 0x1FFFFFFFu;
        return paddr >= PhysBase && paddr < PhysBase + PhysSize;
    }

    /// <summary>8-bit read of CDVD MMIO (lower 8 bits of address select the register).</summary>
    public byte ReadMmio8(uint address)
    {
        byte reg = (byte)(address & 0xFF);
        switch (reg)
        {
            case 0x04: // NCOMMAND
                return NCommand;
            case 0x05: // N-READY / Ready (DAT_bf402005) — DiskReady polls this
                return ComposeReady();
            case 0x06: // ERROR — clear-on-read (hardware)
            {
                byte e = _mmioError;
                _mmioError = 0;
                return e;
            }
            case 0x07: // BREAK (read returns 0)
                return 0;
            case 0x08: // INTR_STAT
                return IntrStat;
            case 0x0A: // STATUS (SCECdStat* / DriveState)
                return (byte)DriveState;
            case 0x0B: // STATUS STICKY
                return StatusSticky;
            case 0x0C: // CRT MINUTE (BCD MSF of LastSector — CD mode)
                return Ittob((byte)(LastSector / (60u * 75u)));
            case 0x0D: // CRT SECOND
                return Ittob((byte)((LastSector / 75u) % 60u + 2u));
            case 0x0E: // CRT FRAME
                return Ittob((byte)(LastSector % 75u));
            case 0x0F: // TYPE (DAT_bf40200f) — disc type
                return TrayOpen ? (byte)0 : (byte)DiscType;
            case 0x13: // SPEED — report a stable DVD x4 class value when spinning
                if (TrayOpen || DriveState == StatStop) return 0;
                return DiscType is 0x14 or 0xFE ? (byte)(3 + 0xF) : (byte)4;
            case 0x15: // RSV
                return 0;
            case 0x16: // SCOMMAND
                return SCommand;
            case 0x17: // SREADY / sDataIn
                return SDataIn;
            case 0x18: // SDATAOUT
                return ReadScmdDataOut();
            case 0x20: case 0x21: case 0x22: case 0x23: case 0x24:
            case 0x28: case 0x29: case 0x2A: case 0x2B: case 0x2C:
            case 0x30: case 0x31: case 0x32: case 0x33: case 0x34:
            case 0x38: case 0x39: case 0x3A:
                // Key / decrypt registers — return 0 (no MG payload yet)
                return 0;
            default:
                LogUnknownMmio(reg, isWrite: false, value: 0);
                // PCSX2 returns 0xFF on unknown CDVD regs — not silent 0.
                return 0xFF;
        }
    }

    /// <summary>8-bit write of CDVD MMIO.</summary>
    public void WriteMmio8(uint address, byte value)
    {
        byte reg = (byte)(address & 0xFF);
        switch (reg)
        {
            case 0x04: // NCOMMAND — issue NCMD after params written to 0x05
                IssueNCommand(value);
                break;
            case 0x05: // NDATAIN — NCMD parameter FIFO
                if (_ncmdParamPos >= _ncmdParams.Length)
                {
                    _ncmdParamPos = 0;
                    _ncmdParamCnt = 0;
                }
                _ncmdParams[_ncmdParamPos++] = value;
                _ncmdParamCnt++;
                break;
            case 0x06: // HOWTO (write ignored / stored nothing useful for IRX boot)
                break;
            case 0x07: // BREAK
                if ((ComposeReady() & ReadyBitBusy) != 0)
                {
                    _abortRequested = true;
                    CancelAsync();
                    SetReadySpin();
                    RaiseCommandCompleteIrq();
                }
                break;
            case 0x08: // INTR_STAT — W1C
                IntrStat = (byte)(IntrStat & ~value);
                break;
            case 0x09: // where_select (MSF mode) — ignore non-zero with log
                if (value != 0)
                    LogUnknownMmio(reg, isWrite: true, value);
                break;
            case 0x0A: // STATUS write — NOP on HW
            case 0x0F: // TYPE write — logged on HW, ignore
                break;
            case 0x16: // SCOMMAND
                IssueSCommand(value);
                break;
            case 0x17: // SDATAIN — SCMD parameter FIFO
                if (_scmdParamPos >= _scmdParams.Length)
                {
                    _scmdParamPos = 0;
                    _scmdParamCnt = 0;
                }
                _scmdParams[_scmdParamPos++] = value;
                _scmdParamCnt++;
                break;
            case 0x18: // SDATAOUT write — no-op
            case 0x3A: // DEC_SET — accept, no decrypt yet
                break;
            default:
                LogUnknownMmio(reg, isWrite: true, value);
                break;
        }
    }

    private void IssueNCommand(byte cmd)
    {
        MmioCommands++;
        NCommand = cmd;
        _abortRequested = false;

        // Drive must be ready (not busy) to accept NCMD (except break path).
        if ((ComposeReady() & ReadyBitBusy) != 0)
        {
            _mmioError = (byte)ErNORDY;
            LastError = ErNORDY;
            MechaconStatus = (uint)(ReadyBitReady | ReadyBitError);
            RaiseCommandCompleteIrq();
            ClearNcmdParams();
            return;
        }

        switch (cmd)
        {
            case 0x00: // CdNop
                SetReadySpin();
                RaiseCommandCompleteIrq();
                break;
            case 0x01: // CdReset
                CancelAsyncInternal(keepError: false);
                DriveState = StatStop;
                MechaconStatus = ReadyBitReady;
                NoteStatusSticky();
                SDataIn = 0x40;
                _scmdResultCnt = _scmdResultPos = 0;
                RaiseCommandCompleteIrq();
                break;
            case 0x02: // CdStandby
                Standby();
                RaiseCommandCompleteIrq();
                break;
            case 0x03: // CdStop
                Stop();
                RaiseCommandCompleteIrq();
                break;
            case 0x04: // CdPause
                Pause();
                RaiseCommandCompleteIrq();
                break;
            case 0x05: // CdSeek — LSN little-endian in params[0..3]
            {
                uint lsn = ReadParamU32(0);
                SeekTo(lsn);
                RaiseCommandCompleteIrq();
                break;
            }
            case 0x06: // CdRead
            case 0x07: // CdReadCDDA
            case 0x08: // DvdRead
            {
                // Params: lsn[0..3], nsectors[4..7], retry, spindle, mode…
                // Without IOP DMA3 we cannot push sectors into a caller buffer.
                // Start an internal async read so Ready goes busy→ready and IRQ fires;
                // log the gap so IRX hang diagnosis is not silent.
                uint lsn = ReadParamU32(0);
                uint nsec = ReadParamU32(4);
                if (nsec == 0) nsec = 1;
                if (BeginAsyncReadN(lsn, Math.Min(nsec, 64u)) == 0)
                {
                    _mmioError = (byte)LastError;
                    MechaconStatus = (uint)(ReadyBitReady | ReadyBitError);
                    RaiseCommandCompleteIrq();
                }
                // else: Step() will complete and call SetReadySpin; raise IRQ on complete
                // (see Step).
                break;
            }
            case 0x09: // CdGetToc — accept, complete (TOC DMA not modeled on IOP path)
                SetReadySpin();
                RaiseCommandCompleteIrq();
                break;
            case 0x0C: // CdReadKey
                SetReadySpin();
                RaiseCommandCompleteIrq();
                break;
            case 0x0F: // CdChgSpdlCtrl
                SetReadySpin();
                RaiseCommandCompleteIrq();
                break;
            default:
                Console.Error.WriteLine(
                    $"[Cdvd.MMIO] unknown NCMD 0x{cmd:X2} (params={_ncmdParamCnt}) — accepting as NOP");
                UnknownMmioAccesses++;
                SetReadySpin();
                RaiseCommandCompleteIrq();
                break;
        }

        ClearNcmdParams();
    }

    private void IssueSCommand(byte cmd)
    {
        MmioCommands++;
        SCommand = cmd;

        // Minimal SCMD result shapes so CDVDMAN can poll SREADY / drain SDATAOUT.
        switch (cmd)
        {
            case 0x01: // GetDiscType (ps2tek / PCSX2 name table)
                SetScmdResult(new byte[] { (byte)(TrayOpen ? 0 : DiscType) });
                break;
            case 0x02: // CdReadSubQ
                SetScmdResult(new byte[11]); // zeros + ok shape
                break;
            case 0x03: // Mecacon subcommands — return mecha version for sub=0
                if (_scmdParamCnt > 0 && _scmdParams[0] == 0)
                    SetScmdResult(new byte[] { 0x00, 0x01, 0x01, 0x02, 0x00 }); // result + ver LE
                else
                    SetScmdResult(new byte[] { 0x00 });
                break;
            case 0x05: // CdTrayReqState — sticky open bit
                StatusSticky = (byte)(DriveState & StatShellOpen);
                SetScmdResult(new byte[] { 0x00 });
                break;
            case 0x06: // CdTrayCtrl
            {
                int mode = _scmdParamCnt > 0 ? _scmdParams[0] : 0;
                TrayRequest(mode == 0 ? TrayReqOpen : TrayReqClose);
                SetScmdResult(new byte[] { 0x00 });
                break;
            }
            case 0x08: // CdReadRTC
                SetScmdResult(new byte[] { 0x00, 0x00, 0x00, 0x12, 0x00, 0x01, 0x01, 0x24 });
                break;
            case 0x15: // ForbidDVDP
                SetScmdResult(new byte[] { 0x05 });
                break;
            case 0x16: // AutoAdjust
                SetScmdResult(new byte[] { 0x00 });
                break;
            case 0x1A: // BootCertify
                SetScmdResult(new byte[] { 0x01 });
                break;
            default:
                // Unknown SCMD: return success 0 so init loops do not hard-fail.
                // Log once-style via counter; console for first diagnosis.
                Console.Error.WriteLine(
                    $"[Cdvd.MMIO] SCMD 0x{cmd:X2} stubbed (params={_scmdParamCnt})");
                UnknownMmioAccesses++;
                SetScmdResult(new byte[] { 0x00 });
                break;
        }

        _scmdParamPos = _scmdParamCnt = 0;
    }

    private void SetScmdResult(byte[] result)
    {
        int n = Math.Min(result.Length, _scmdResult.Length);
        Array.Clear(_scmdResult);
        Buffer.BlockCopy(result, 0, _scmdResult, 0, n);
        _scmdResultCnt = n;
        _scmdResultPos = 0;
        // Bit6 clear while result bytes remain (PCSX2 SetSCMDResultSize).
        if (n > 0)
            SDataIn = (byte)(SDataIn & ~0x40);
        else
            SDataIn = (byte)(SDataIn | 0x40);
    }

    private byte ReadScmdDataOut()
    {
        if ((SDataIn & 0x40) != 0 || _scmdResultPos >= _scmdResultCnt)
            return 0;
        byte b = _scmdResult[_scmdResultPos++];
        if (_scmdResultPos >= _scmdResultCnt)
            SDataIn = (byte)(SDataIn | 0x40);
        return b;
    }

    private void RaiseCommandCompleteIrq()
    {
        IntrStat = (byte)(IntrStat | 0x01); // Irq_CommandComplete
        // IOP INTC cause 2 is CDVD on real HW; raise SIF as shared notify for now
        // (same stand-in used by ReadSector completion). Full IOP INTC is a T1 gap.
        _intc?.Raise(Intc.InterruptSource.Sif);
    }

    private void ClearNcmdParams()
    {
        _ncmdParamPos = 0;
        _ncmdParamCnt = 0;
    }

    private uint ReadParamU32(int off)
    {
        if (off + 3 >= _ncmdParams.Length) return 0;
        return (uint)(_ncmdParams[off]
            | (_ncmdParams[off + 1] << 8)
            | (_ncmdParams[off + 2] << 16)
            | (_ncmdParams[off + 3] << 24));
    }

    private void LogUnknownMmio(byte reg, bool isWrite, byte value)
    {
        UnknownMmioAccesses++;
        if (isWrite)
            Console.Error.WriteLine($"[Cdvd.MMIO] unknown write 0x1F4020{reg:X2} = 0x{value:X2}");
        else
            Console.Error.WriteLine($"[Cdvd.MMIO] unknown read  0x1F4020{reg:X2} → 0xFF");
    }

    private static byte Ittob(byte i) => (byte)(((i / 10) << 4) + (i % 10));

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

        if (_abortRequested)
        {
            _abortRequested = false;
            LastError = ErABRT;
            _mmioError = (byte)ErABRT;
            SetReadySpin();
            MechaconStatus = ReadyBitReady | ReadyBitError;
            if (NCommand is 0x06 or 0x07 or 0x08)
                RaiseCommandCompleteIrq();
            _pendingDest = 0;
            _memForAsync = null;
            _kernelForComplete = null;
            _completionEfId = 0;
            return (int)used;
        }

        // Complete all pending sectors; DMA out when dest was set by BeginAsyncReadTo.
        for (uint i = 0; i < _pendingCount; i++)
        {
            if (!ReadSector(_pendingLba + i)) break;
            if (_pendingDest != 0 && _memForAsync != null)
                CopySectorToMemory(_memForAsync, _pendingDest + i * (uint)SectorSize);
        }
        SetReadySpin();
        // MMIO NCMD read path: signal command-complete so CDVDMAN leaves its poll.
        if (NCommand is 0x06 or 0x07 or 0x08)
            RaiseCommandCompleteIrq();
        if (_completionEfId != 0 && _kernelForComplete != null)
            _kernelForComplete.SetEventFlag(_completionEfId, 1u);
        _pendingDest = 0;
        _memForAsync = null;
        _kernelForComplete = null;
        _completionEfId = 0;
        return (int)used;
    }
}
