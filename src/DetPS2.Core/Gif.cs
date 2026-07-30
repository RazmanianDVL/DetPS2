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

    /// <summary>
    /// PATH3 temporarily masked by VIF1 <c>MSKPATH3</c> (GIF_STAT bit 1 = M3P).
    /// Distinct from GIF_MODE bit 0 (M3R, permanent mask). Ground-truthed against
    /// ps2tek / PCSX2 GIF_STAT layout; commercial path-sync loops (e.g. Burnout 3
    /// at <c>0x001F19C0</c>) poll M3P and hang forever if it is never raised.
    /// </summary>
    private bool _m3p;

    /// <summary>Active output path while a transfer is in flight (GIF_STAT APATH, bits 10–12).</summary>
    private uint _apath;

    // PATH3 held while M3P/M3R masks — real HW fills the GIF FIFO (FQC rises) until unmasked.
    // Burnout 3 path-sync @ 0x001F1A28 spins on GIF_STAT.FQC (bits 24–28) after starting a
    // masked PATH3 DMA; instant-drain left FQC=0 forever.
    private uint _heldPath3Addr;
    private uint _heldPath3Qwc;
    private bool _path3Held;

    public ulong Path3Transfers => _path3Transfers;
    public ulong Path2Transfers => _path2Transfers;
    public ulong Path1Transfers => _path1Transfers;

    /// <summary>GIF_STAT M3P — PATH3 masked by VIF1 MSKPATH3.</summary>
    public bool Path3MaskedByVif => _m3p;

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
        _m3p = false;
        _apath = 0;
        _heldPath3Addr = _heldPath3Qwc = 0;
        _path3Held = false;
    }

    /// <summary>
    /// VIF1 MSKPATH3 effect: IMM bit 15 (0x8000) = 1 masks PATH3 (M3P=1), 0 unmasks.
    /// (ps2tek: "Sets the VIF-side PATH3 mask to bit 15 of IMMEDIATE.")
    /// Real hardware latches this until the opposite MSKPATH3; GIF_STAT.M3P mirrors it.
    /// Unmask drains any held PATH3 FIFO data into the GS.
    /// </summary>
    public void SetMskPath3(bool masked)
    {
        _m3p = masked;
        if (!masked)
            DrainHeldPath3();
    }

    private bool Path3Masked => _m3p || (_mode & 1) != 0;

    private void DrainHeldPath3()
    {
        if (!_path3Held) return;
        _path3Held = false;
        _fifoR = _fifoW = _fifoCount = 0;
        uint addr = _heldPath3Addr;
        uint qwc = _heldPath3Qwc;
        _heldPath3Addr = _heldPath3Qwc = 0;
        if (qwc == 0) return;
        _apath = 3;
        ProcessTransfer(addr, qwc);
        _apath = 0;
    }

    /// <summary>
    /// Compose GIF_STAT (0x10003020). Bits (ps2tek / PCSX2):
    /// 0 M3R, 1 M3P, 2 IMT, 3 PSE, 5 IP3, 6 P3Q, 7 P2Q, 8 P1Q, 9 OPH,
    /// 10–12 APATH, 13 DIR, 24–31 FQC.
    /// </summary>
    public uint ReadStat()
    {
        uint fqc = (uint)Math.Min(Math.Max(_fifoCount / 4, 0), 16);
        // Path-sync (Burnout 3 @ 0x001F1A28): after MSKPATH3 the EE spins on FQC!=0
        // before kicking the next VIF1/GIF chain. Real HW has the just-started PATH3
        // DMA already filling the FIFO under the mask. Our HLE can race such that the
        // fill DMA completed unmasked (FQC drained) or never delivered, leaving
        // M3P=1 with FQC=0 forever and the busy-flag in the flip-queue consumer stuck.
        // While PATH3 is masked, report at least 1 QW so the poller proceeds; the next
        // chain re-validates against real DMA state. Unmasked path is unchanged.
        if (Path3Masked && fqc == 0)
            fqc = 1;
        uint oph = (_fifoCount > 0 || _apath != 0 || (Path3Masked && fqc > 0)) ? 1u : 0u;
        uint imt = (_mode & 2) != 0 ? 1u : 0u; // GIF_MODE.IMT → STAT.IMT
        return (_mode & 1)                      // M3R
               | (_m3p ? 2u : 0u)               // M3P
               | (imt << 2)                     // IMT
               | (oph << 9)                     // OPH
               | ((_apath & 7) << 10)           // APATH
               | (fqc << 24);                   // FQC
    }

    /// <summary>GIF_CTRL / MODE / STAT / FIFO at 0x10003000–0x10006000.</summary>
    public uint ReadRegister(uint address)
    {
        uint off = address & 0xFFFF;
        return off switch
        {
            0x3000 => 0, // GIF_CTRL write-only
            0x3010 => _mode,
            0x3020 => ReadStat(),
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
                    _m3p = false;
                    _apath = 0;
                    _path3Held = false;
                    _heldPath3Addr = _heldPath3Qwc = 0;
                }
                break;
            case 0x3010: // GIF_MODE
            {
                uint prev = _mode;
                _mode = value;
                // Clearing M3R unmasks PATH3 — drain held FIFO like MSKPATH3 unmask.
                if ((prev & 1) != 0 && (value & 1) == 0)
                    DrainHeldPath3();
                break;
            }
        }
    }

    /// <summary>EE writes to GIF FIFO (0x10006000) — one word at a time; assemble QWs and process.</summary>
    public void WriteFifo(uint value)
    {
        if (_fifoCount >= _fifo.Length) return;
        _fifo[_fifoW] = value;
        _fifoW = (_fifoW + 1) % _fifo.Length;
        _fifoCount++;
        // When PATH3 is masked (M3P/M3R), FIFO data must stay and raise FQC — Burnout 3
        // path-sync @ 0x001F1A28 polls FQC after MSKPATH3 + FIFO/DMA fill. Instant-drain
        // left FQC=0 and the spin never exited.
        if (Path3Masked)
            return;
        // Unmasked: every full QW is fair game to consume (DMA is the usual path; FIFO
        // pokes are rare and treated as fire-and-forget for telemetry).
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
        if (qwc == 0) return;
        _path3Transfers++;
        if (TransferLog.Enabled) TransferLog.Log("GIF:Path3->GS", address, 0, qwc * 16);

        if (Path3Masked)
        {
            // Hold in FIFO: raise FQC so path-sync loops that poll STAT.FQC can proceed.
            // ps2tek: masked PATH3 data resides in the FIFO until the mask is lifted.
            _heldPath3Addr = address;
            _heldPath3Qwc = qwc;
            _path3Held = true;
            // FQC is words/4 capped at 16; report min(qwc,16) QWs pending.
            int words = (int)Math.Min(qwc, 16u) * 4;
            _fifoCount = Math.Min(words, _fifo.Length);
            _fifoR = 0;
            _fifoW = _fifoCount;
            // P3Q / OPH: path queued while masked (bit 6 of STAT via oph when fifo non-empty)
            return;
        }

        // Unmasked: process immediately (instant HLE).
        _apath = 3;
        ProcessTransfer(address, qwc);
        _apath = 0;
    }

    /// <summary>Path2 — from VIF1 DIRECT/HL.</summary>
    public void ReceivePath2Data(uint address, uint qwc)
    {
        _path2Transfers++;
        if (TransferLog.Enabled) TransferLog.Log("GIF:Path2->GS", address, 0, qwc * 16);
        _apath = 2;
        ProcessTransfer(address, qwc);
        _apath = 0;
    }

    /// <summary>Path1 — VU1 XGKICK style: process tags from memory.</summary>
    public void ReceivePath1Data(uint address, uint qwc)
    {
        _path1Transfers++;
        if (TransferLog.Enabled) TransferLog.Log("GIF:Path1->GS", address, 0, qwc * 16);
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
