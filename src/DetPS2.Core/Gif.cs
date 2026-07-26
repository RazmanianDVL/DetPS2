using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Interface — Phase 7.
/// Paths: Path1 (VU1), Path2 (VIF1), Path3 (DMAC).
/// Tag formats: PACKED (0), REGLIST (1), IMAGE (2), DISABLE (3).
/// </summary>
public sealed class Gif : ISchedulable
{
    private readonly Gs _gs;

    private uint _lastQwcProcessed;
    private ulong _path3Transfers;
    private ulong _path2Transfers;
    private ulong _path1Transfers;

    // REGS field from last tag (up to 16 regs × 4 bits)
    private ulong _regs;
    private uint _nreg;

    // GIF I/O (0x10003000)
    private uint _ctrl;
    private uint _mode;
    private readonly uint[] _fifo = new uint[64]; // 16 QW max
    private int _fifoR, _fifoW, _fifoCount;

    public ulong Path3Transfers => _path3Transfers;
    public ulong Path2Transfers => _path2Transfers;
    public ulong Path1Transfers => _path1Transfers;

    public Gif(Gs gs)
    {
        _gs = gs ?? throw new ArgumentNullException(nameof(gs));
    }

    public void Reset()
    {
        _lastQwcProcessed = 0;
        _path1Transfers = _path2Transfers = _path3Transfers = 0;
        _regs = 0;
        _nreg = 0;
        _ctrl = _mode = 0;
        _fifoR = _fifoW = _fifoCount = 0;
    }

    /// <summary>GIF_CTRL / MODE / STAT / FIFO at 0x10003000–0x10006000.</summary>
    public uint ReadRegister(uint address)
    {
        uint off = address & 0xFFFF;
        return off switch
        {
            0x3000 => 0, // GIF_CTRL write-only
            0x3010 => _mode,
            // GIF_STAT: idle, empty FIFO after instant drain
            0x3020 => (_mode & 1) | ((_fifoCount > 0 ? 1u : 0u) << 9) | ((uint)Math.Min(_fifoCount / 4, 16) << 24),
            0x3040 or 0x3050 or 0x3060 or 0x3070 => 0,
            0x3080 or 0x3090 or 0x30A0 => 0,
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        uint off = address & 0xFFFF;
        switch (off)
        {
            case 0x3000: // GIF_CTRL
                _ctrl = value;
                if ((value & 1) != 0) // reset
                {
                    _fifoR = _fifoW = _fifoCount = 0;
                }
                break;
            case 0x3010: // GIF_MODE
                _mode = value;
                break;
        }
    }

    /// <summary>EE writes to GIF FIFO (0x10006000) — one word at a time; assemble QWs and process.</summary>
    public void WriteFifo(uint value)
    {
        if (_fifoCount >= _fifo.Length) return;
        _fifo[_fifoW] = value;
        _fifoW = (_fifoW + 1) % _fifo.Length;
        _fifoCount++;
        // When we have a full QW (4 words), try to process if we can buffer a packet in GS via path3-like path
        // For simplicity: every 4 words form a QW pushed into a mini buffer; when EOP tags complete we ProcessTransfer isn't memory-based.
        // Path: collect into EE scratch via ProcessFifoQuad when 4 words ready — store to a ring in GS local and process if tag complete.
        if (_fifoCount >= 4 && (_fifoCount % 4) == 0)
            DrainFifoQuadwords();
    }

    private void DrainFifoQuadwords()
    {
        // Instant-process: GIF FIFO PATH3-style streaming is rare vs DMA; when games poke FIFO we
        // accumulate words into a temporary buffer on the GS local mem high area and run ProcessTransfer.
        // Minimal: count transfers for telemetry; full FIFO-to-GS PATH3 stream needs a proper state machine.
        _path3Transfers++;
        // Drop words (games often uses DMA not FIFO). Keep FIFO empty so STAT never stalls.
        _fifoR = _fifoW = _fifoCount = 0;
    }

    /// <summary>Path3 — DMAC GIF channel.</summary>
    public void ReceivePath3Data(uint address, uint qwc)
    {
        _path3Transfers++;
        ProcessTransfer(address, qwc);
    }

    /// <summary>Path2 — from VIF1 DIRECT/HL.</summary>
    public void ReceivePath2Data(uint address, uint qwc)
    {
        _path2Transfers++;
        ProcessTransfer(address, qwc);
    }

    /// <summary>Path1 — VU1 XGKICK style: process tags from memory.</summary>
    public void ReceivePath1Data(uint address, uint qwc)
    {
        _path1Transfers++;
        ProcessTransfer(address, qwc);
    }

    /// <summary>Process an in-memory GIF packet (tests can call this directly).</summary>
    public void ProcessTransfer(uint address, uint qwc)
    {
        if (qwc == 0) return;
        _lastQwcProcessed = qwc;
        // Phase 10: batch accounting — single cost report for whole transfer

        uint currentAddr = address;
        uint remaining = qwc;

        while (remaining > 0)
        {
            // GIFTag is 128-bit
            uint w0 = Read32(currentAddr);
            uint w1 = Read32(currentAddr + 4);
            uint w2 = Read32(currentAddr + 8);
            uint w3 = Read32(currentAddr + 12);

            uint nloop = w0 & 0x7FFF;
            bool eop = (w0 & (1u << 15)) != 0;
            bool pre = (w0 & (1u << 14)) != 0; // actually bit 46 in full — see layout
            // Correct GIFtag layout (from GS docs):
            // bits 0-14 NLOOP, 15 EOP, 46 PRE, 47-57 PRIM, 58-59 FLG, 60-63 NREG
            // 64-127 REGS
            // With little-endian QW as 4×u32: w0 has NLOOP/EOP low; PRE/PRIM/FLG/NREG span w1
            pre = ((w1 >> 14) & 1) != 0; // approximate when packed in high of first 64
            // More portable parse of first 64 bits as ulong
            ulong tagLo = w0 | ((ulong)w1 << 32);
            nloop = (uint)(tagLo & 0x7FFF);
            eop = (tagLo & (1UL << 15)) != 0;
            pre = (tagLo & (1UL << 46)) != 0;
            uint prim = (uint)((tagLo >> 47) & 0x7FF);
            uint flg = (uint)((tagLo >> 58) & 0x3);
            uint nreg = (uint)((tagLo >> 60) & 0xF);
            if (nreg == 0) nreg = 16;
            _nreg = nreg;
            _regs = w2 | ((ulong)w3 << 32);

            currentAddr += 16;
            remaining--;

            if (pre)
                _gs.WriteGsRegister(0x00, prim);

            switch (flg)
            {
                case 0: // PACKED
                    remaining = ProcessPacked(ref currentAddr, remaining, nloop, nreg);
                    break;
                case 1: // REGLIST
                    remaining = ProcessReglist(ref currentAddr, remaining, nloop, nreg);
                    break;
                case 2: // IMAGE / HWREG
                    remaining = ProcessImage(ref currentAddr, remaining, nloop);
                    break;
                default:
                    // DISABLE — skip nloop * nreg qwords best-effort
                    uint skip = Math.Min(nloop, remaining);
                    currentAddr += skip * 16;
                    remaining -= skip;
                    break;
            }

            if (eop) break;
        }
    }

    private uint ProcessPacked(ref uint addr, uint remaining, uint nloop, uint nreg)
    {
        // Each "loop" writes nreg registers; each register is one QW
        for (uint loop = 0; loop < nloop; loop++)
        {
            for (uint r = 0; r < nreg; r++)
            {
                if (remaining == 0) return 0;
                uint lo = Read32(addr);
                uint hi = Read32(addr + 4);
                uint mid = Read32(addr + 8);
                uint top = Read32(addr + 12);
                uint regId = RegAt(r);
                // PACKED data is 64-bit in low QW half typically; full QW for ST/RGBAQ etc.
                ulong data = lo | ((ulong)hi << 32);
                // Some packed formats put data in full 128 — use low 64
                if (regId == 0x0E) // A+D
                {
                    // data = value, reg = low 8 of upper 64
                    uint adReg = mid & 0x7F;
                    _gs.WriteGsRegister(adReg, data);
                }
                else
                {
                    _gs.WriteGsRegister(regId, data);
                }
                addr += 16;
                remaining--;
            }
        }
        return remaining;
    }

    private uint ProcessReglist(ref uint addr, uint remaining, uint nloop, uint nreg)
    {
        // REGLIST: data is tightly packed 64-bit values, 2 per QW
        uint total = nloop * nreg;
        uint i = 0;
        while (i < total && remaining > 0)
        {
            uint lo = Read32(addr);
            uint hi = Read32(addr + 4);
            uint lo2 = Read32(addr + 8);
            uint hi2 = Read32(addr + 12);
            ulong d0 = lo | ((ulong)hi << 32);
            ulong d1 = lo2 | ((ulong)hi2 << 32);

            uint reg0 = RegAt(i % nreg);
            _gs.WriteGsRegister(reg0, d0);
            i++;
            if (i < total)
            {
                uint reg1 = RegAt(i % nreg);
                _gs.WriteGsRegister(reg1, d1);
                i++;
            }
            addr += 16;
            remaining--;
        }
        return remaining;
    }

    private uint ProcessImage(ref uint addr, uint remaining, uint nloop)
    {
        // IMAGE: nloop QWs of raw data into local GS mem (linear upload for Phase 7)
        int dest = 0;
        uint count = Math.Min(nloop, remaining);
        Span<byte> qw = stackalloc byte[16];
        for (uint i = 0; i < count; i++)
        {
            for (int b = 0; b < 16; b++)
                qw[b] = Memory.Read8(addr + (uint)b);
            _gs.WriteImageData(qw, dest);
            dest += 16;
            addr += 16;
            remaining--;
        }
        return remaining;
    }

    private uint RegAt(uint index)
    {
        // REGS: 4 bits per register, index 0..15
        int shift = (int)((index % 16) * 4);
        return (uint)((_regs >> shift) & 0xF);
    }

    private SystemMemory Memory => _gs.Memory;

    private uint Read32(uint addr) => Memory.Read32(addr);

    public int Step(ulong maxCycles)
    {
        if (_lastQwcProcessed == 0)
            return 1;
        uint nreg = _nreg == 0 ? 1u : _nreg;
        int cost = _gs.CalculateWorkCost(_lastQwcProcessed, nreg);
        _lastQwcProcessed = 0;
        return Math.Min(cost, (int)Math.Max(1L, (long)maxCycles));
    }
}
