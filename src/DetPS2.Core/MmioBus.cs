using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// Central MMIO decode (Phase 8–9).
/// Timers, INTC, DMAC, SIF, Pad, SPU2 alias.
/// </summary>
public sealed class MmioBus
{
    private readonly Dictionary<uint, Func<uint>> _readExact = new();
    private readonly Dictionary<uint, Action<uint>> _writeExact = new();

    private EeTimers? _timers;
    private Intc? _intc;
    private Dmac? _dmac;
    private Sif? _sif;
    private PadInput? _pad;
    private Spu2? _spu2;
    private Sio2? _sio2;
    private Ipu? _ipu;
    private Gif? _gif;
    private Gs? _gs;
    private Vif? _vif;
    private Telemetry? _telemetry;
    private Func<(ulong cycle, ulong pc)>? _context;

    // MCH RDRAM init stubs (ps2tek) — games/BIOS poll these during early bring-up
    private uint _mchRicm;
    private uint _mchDrd;
    private int _rdramSdevId;

    // Genuinely unmapped corners of the 0x10000000-0x1F000000 I/O window get real
    // write-then-read memory semantics (like a mature reference emulator's generic
    // fallback handler) instead of "always return 0" — code that uses an unmapped
    // address as scratch memory (a real pattern seen on retail boots) should see what
    // it wrote, not silently-wrong zeros. Telemetry still records the access either way.
    private readonly Dictionary<uint, uint> _unmappedFallback = new();

    public void Attach(EeTimers timers, Intc intc, Dmac dmac, Sif sif, PadInput? pad = null, Spu2? spu2 = null, Sio2? sio2 = null, Ipu? ipu = null)
    {
        _timers = timers;
        _intc = intc;
        _dmac = dmac;
        _sif = sif;
        _pad = pad;
        _spu2 = spu2;
        _sio2 = sio2;
        _ipu = ipu;
    }

    public void AttachGraphics(Gif gif, Gs gs, Vif? vif = null)
    {
        _gif = gif;
        _gs = gs;
        _vif = vif;
    }

    public void SetTelemetry(Telemetry? telemetry, Func<(ulong cycle, ulong pc)>? context = null)
    {
        _telemetry = telemetry;
        _context = context;
    }

    public void RegisterRead(uint address, Func<uint> readHandler) =>
        _readExact[address] = readHandler;

    public void RegisterWrite(uint address, Action<uint> writeHandler) =>
        _writeExact[address] = writeHandler;

    public uint Read32(uint address)
    {
        if (_readExact.TryGetValue(address, out var h))
            return h();

        // IPU control 0x10002000–0x10002FFF; in/out FIFO 0x10007000–0x1000701F (ps2tek)
        if (address >= Ipu.MmioBase && address < Ipu.MmioBase + 0x1000 && _ipu != null)
            return _ipu.ReadRegister(address);
        if (address >= 0x10007000 && address < 0x10007020 && _ipu != null)
            return _ipu.ReadFifoWord(address);

        if (address >= 0x10000000 && address < 0x10002000 && _timers != null)
            return _timers.ReadRegister(address);

        // GIF control + FIFO window
        if (address >= 0x10003000 && address < 0x10003100 && _gif != null)
        {
            // GIF_STAT (…3020) poll: path-sync loops (Burnout 3 @ 0x001F1A28) spin on FQC
            // immediately after kicking a masked PATH3 DMA. EE.Step quanta can run thousands
            // of those poll instructions before the scheduler turns hit DMAC, so FQC never
            // rises inside the spin. Pump a few GIF/VIF-relevant DMAC steps on STAT read —
            // not a blanket force-finish of every channel (MK WAD path is sensitive to that).
            if ((address & 0xFF) == 0x20 && _dmac != null)
            {
                for (int i = 0; i < 16; i++)
                {
                    if (_dmac.Step(128) == 0) break;
                    // FQC already non-zero — poller will observe it; stop early.
                    if ((_gif.ReadStat() & 0x1F00_0000u) != 0) break;
                }
            }
            return _gif.ReadRegister(address);
        }
        if (address >= 0x10006000 && address < 0x10006010 && _gif != null)
            return 0; // FIFO write-only

        // VIF0 / VIF1 status stubs — return idle, FQC=0 so games don't spin forever
        if (address >= 0x10003800 && address < 0x10003C00)
            return ReadVifStub(address, vif1: false);
        if (address >= 0x10003C00 && address < 0x10004000)
            return ReadVifStub(address, vif1: true);

        if ((address == Intc.AddrStat || address == Intc.AddrMask ||
             (address & 0xFFFFFF00) == 0x1000F000) && _intc != null)
            return _intc.ReadRegister(address);

        if (address >= 0x10008000 && address < 0x1000F000 && _dmac != null)
            return _dmac.ReadRegister(address);

        // D_ENABLER / D_ENABLEW live above the channel window
        if ((address == 0x1000F520 || address == 0x1000F590) && _dmac != null)
            return _dmac.ReadRegister(address);

        if (address >= 0x1000F200 && address < 0x1000F300 && _sif != null)
            return _sif.ReadRegister(address);

        // SSBUS / SBUS window (0x1000F100–0x1000F1FF) — games poll these during
        // IOP bring-up. Return "ready" patterns so commercial boot doesn't spin.
        if (address >= 0x1000F100 && address < 0x1000F200)
        {
            // 0x1000F130 is a common status poll; bit patterns match "idle/ready"
            if (address == 0x1000F130) return 0x00000000; // not busy
            if (address == 0x1000F140) return 0x00000001;
            return 0;
        }

        // MCH RDRAM init (ps2tek)
        if (address == 0x1000F430) return 0;
        if (address == 0x1000F440) return ReadMchDrd();

        if (address >= PadInput.MmioBase && address < PadInput.MmioBase + 0x20 && _pad != null)
            return _pad.ReadRegister(address);

        if (address >= Spu2.MmioAlias && address < Spu2.MmioAlias + 0x400 && _spu2 != null)
            return _spu2.ReadRegister(address);

        if (address >= Sio2.MmioBase && address < Sio2.MmioBase + 0x100 && _sio2 != null)
            return _sio2.ReadRegister(address);

        // Privileged GS registers (0x12000000)
        if (address >= 0x12000000 && address < 0x12002000 && _gs != null)
            return _gs.ReadPrivileged32(address);

        // Unhandled MMIO read (Phase 21 telemetry)
        if (address >= 0x10000000 && address < 0x1F000000)
        {
            if (_telemetry != null)
            {
                var (cyc, pc) = _context?.Invoke() ?? (0UL, 0UL);
                _telemetry.UnknownMmioRead(cyc, pc, address);
            }
            return _unmappedFallback.TryGetValue(address, out uint v) ? v : 0;
        }
        return 0;
    }

    public void Write32(uint address, uint value)
    {
        if (_writeExact.TryGetValue(address, out var h))
        {
            h(value);
            return;
        }

        if (address >= Ipu.MmioBase && address < Ipu.MmioBase + 0x1000 && _ipu != null)
        {
            _ipu.WriteRegister(address, value);
            return;
        }
        if (address >= 0x10007000 && address < 0x10007020 && _ipu != null)
        {
            _ipu.WriteFifoWord(address, value);
            return;
        }

        if (address >= 0x10000000 && address < 0x10002000 && _timers != null)
        {
            _timers.WriteRegister(address, value);
            return;
        }

        if (address >= 0x10003000 && address < 0x10003100 && _gif != null)
        {
            _gif.WriteRegister(address, value);
            return;
        }
        if (address >= 0x10006000 && address < 0x10006010 && _gif != null)
        {
            _gif.WriteFifo(value);
            return;
        }

        // VIF0 FIFO @ 0x10004000, VIF1 FIFO @ 0x10005000 (ps2tek)
        if (address >= 0x10004000 && address < 0x10005000 && _vif != null)
        {
            _vif.FeedData(value);
            return;
        }
        if (address >= 0x10005000 && address < 0x10006000 && _vif != null)
        {
            _vif.FeedData(value);
            return;
        }

        // VIF0/VIF1 control — accept writes (NOP FBRST etc.)
        if (address >= 0x10003800 && address < 0x10004000)
            return;

        if ((address == Intc.AddrStat || address == Intc.AddrMask ||
             (address & 0xFFFFFF00) == 0x1000F000) && _intc != null)
        {
            _intc.WriteRegister(address, value);
            return;
        }

        if (address >= 0x10008000 && address < 0x1000F000 && _dmac != null)
        {
            _dmac.WriteRegister(address, value);
            return;
        }

        if ((address == 0x1000F520 || address == 0x1000F590) && _dmac != null)
        {
            _dmac.WriteRegister(address, value);
            return;
        }

        if (address >= 0x1000F200 && address < 0x1000F300 && _sif != null)
        {
            _sif.WriteRegister(address, value);
            return;
        }

        if (address == 0x1000F430)
        {
            uint sa = (value >> 16) & 0xFFF;
            uint sbc = (value >> 6) & 0xF;
            if (sa == 0x21 && sbc == 0x1 && ((_mchDrd >> 7) & 1) == 0)
                _rdramSdevId = 0;
            _mchRicm = value & ~0x80000000u;
            return;
        }
        if (address == 0x1000F440)
        {
            _mchDrd = value;
            return;
        }

        if (address >= PadInput.MmioBase && address < PadInput.MmioBase + 0x20 && _pad != null)
        {
            _pad.WriteRegister(address, value);
            return;
        }

        if (address >= Spu2.MmioAlias && address < Spu2.MmioAlias + 0x400 && _spu2 != null)
        {
            _spu2.WriteRegister(address, value);
            return;
        }

        if (address >= Sio2.MmioBase && address < Sio2.MmioBase + 0x100 && _sio2 != null)
        {
            _sio2.WriteRegister(address, value);
            return;
        }

        if (address >= 0x12000000 && address < 0x12002000 && _gs != null)
        {
            _gs.WritePrivileged32(address, value);
            return;
        }

        // Unhandled MMIO write (Phase 21 telemetry)
        if (address >= 0x10000000 && address < 0x1F000000)
        {
            if (_telemetry != null)
            {
                var (cyc, pc) = _context?.Invoke() ?? (0UL, 0UL);
                _telemetry.UnknownMmioWrite(cyc, pc, address);
            }
            _unmappedFallback[address] = value;
        }
    }

    private static uint ReadVifStub(uint address, bool vif1)
    {
        uint off = address & 0x3FF;
        // STAT (0x00): idle, FQC empty
        if (off == 0x00) return 0;
        // FBRST / ERR / MARK — 0
        return 0;
    }

    private uint ReadMchDrd()
    {
        // ps2tek MCH_DRD probe sequence for RDRAM init
        uint sop = (_mchRicm >> 6) & 0xF;
        uint sa = (_mchRicm >> 16) & 0xFFF;
        if (sop != 0) return 0;
        switch (sa)
        {
            case 0x21:
                if (_rdramSdevId < 2)
                {
                    _rdramSdevId++;
                    return 0x1F;
                }
                return 0;
            case 0x23: return 0x0D0D;
            case 0x24: return 0x0090;
            case 0x40: return _mchRicm & 0x1F;
            default: return 0;
        }
    }

    public void Reset() => _unmappedFallback.Clear();
}
