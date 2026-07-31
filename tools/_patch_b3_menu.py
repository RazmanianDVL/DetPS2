#!/usr/bin/env python3
"""Apply B3 logo-frontend wall fixes: residual→STG gates + Soft-GS PATH3 queue + DISPFB."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def must_replace(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise SystemExit(f"FAIL: {label} not found")
    return text.replace(old, new, 1)


def patch_burnout3():
    p = ROOT / "src/DetPS2.Core/GameQuirks/Burnout3Assist.cs"
    t = p.read_text(encoding="utf-8")

    t = must_replace(
        t,
        """        // Plant STAGEHED only after residual LGDEV CallRpc has stabilized (menu4/final10:
        // residual ~48× at sp@FC10 then STG). Planting mid-residual (escapes≪48) or at
        // force-cycle disturbed frames and left cdvd plant-only (609) without STG bind.
        // Wave-7: also plant once STG+full Global.txd already advanced cdvd (≫2000) even
        // if residual n stayed short (preferIopRp=OFF force@pristine residual n=2–3).
        if (_lgDevFullyDone && sys.MasterCycles >= 30_000_000
            && (_lgDevEscapes >= 48 || sys.Cdvd.SectorsRead >= 2000)
            && sys.Cdvd.SectorsRead >= 400)
            MaybePlantStageAssets(sys);""",
        """        // Plant STAGEHED after residual LGDEV settled. Tip IRX-era residual dies at n=2–3
        // after force@pristine (entry+leaf stubs) — n≥48 left STAGEHED unplanted forever.
        // Wave-7: also plant once game FILEIO already advanced cdvd (≫2000).
        if (_lgDevFullyDone && sys.MasterCycles >= 28_000_000
            && (_lgDevEscapes >= 1 || sys.Cdvd.SectorsRead >= 2000)
            && sys.Cdvd.SectorsRead >= 400)
            MaybePlantStageAssets(sys);""",
        "STAGEHED Step gate",
    )

    t = must_replace(
        t,
        "    public void OnHostPresent(Ps2System sys) => _ = sys;",
        """    public void OnHostPresent(Ps2System sys)
    {
        // Soft-GS: PATH3 may upload logo IMAGE under M3P; DISPFB→FB composite each present.
        sys.Gs.CompositeDispfbToFramebuffer();
    }""",
        "OnHostPresent",
    )

    t = must_replace(
        t,
        """        // Ensure STAGEHED is in EE before we try to plant an iovec (same settle gate).
        if (!_stageAssetsPlanted && sys.MasterCycles >= 28_000_000 && _lgDevEscapes >= 8)
            MaybePlantStageAssets(sys);""",
        """        // Ensure STAGEHED is in EE before we try to plant an iovec (short residual n=2–3 OK).
        if (!_stageAssetsPlanted && sys.MasterCycles >= 28_000_000 && _lgDevEscapes >= 1)
            MaybePlantStageAssets(sys);""",
        "iovec STAGEHED gate",
    )

    t = must_replace(
        t,
        """        // High WaitSema pulse while IRX-only after residual settle, and again after
        // STG/TXD (cdvd>=2000) so presentation workers are not stuck on high ids.
        // Soft-complete post-LGDEV spin WaitSema only while IRX-only (ra@0x2AF8xx).
        bool irxOnly = sys.Cdvd.SectorsRead is >= 400 and < 600;
        bool postTxd = sys.Cdvd.SectorsRead >= 2000;
        if (_lgDevFullyDone && sys.MasterCycles >= 28_000_000 && (irxOnly || postTxd))
        {
            uint pcW = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
            uint raW = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            if (irxOnly && pcW is >= 0x0010BE60 and <= 0x0010BE70
                && raW is >= 0x002AF800 and <= 0x002AF910)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = raW;
                sys.EE.COP0_Status &= ~(1u << 1);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_menuKickPulses % 16) == 0)
                    Console.Error.WriteLine(
                        $"[B3] soft-complete post-LGDEV WaitSema ra=0x{raW:X8} " +
                        $"cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
            }
            if (k != null && (_menuKickPulses % 2) == 0)
            {
                foreach (var t in k.AllThreads)
                {
                    if (!t.Alive || !t.Sleeping) continue;
                    if (t.WaitSemaId >= 32)
                    {
                        try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                    if (t.WaitSemaId == 0 && !t.WaitVblank)
                        k.WakeupThread(t.Id);
                }
            }
        }""",
        """        // Post-LGDEV poll @0x2AF80C: while(*(gp-23104)==0 && s0<600) SleepThread.
        // Success: flag!=0 && s0!=600 → 0x2AF914 v0=1 → epi 0x2AF984.
        // Fail timeout: 0x2AF91C/0x2AF920 — never soft-leave there.
        bool irxOnly = sys.Cdvd.SectorsRead is >= 400 and < 600;
        bool postTxd = sys.Cdvd.SectorsRead >= 2000;
        if (_lgDevFullyDone && sys.MasterCycles >= 22_000_000 && (irxOnly || postTxd))
        {
            uint pcW = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
            uint raW = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            const uint postLgDevSuccess = 0x002AF914u;
            bool raInPostLgDev = raW is >= 0x002AF800 and <= 0x002AF994;
            bool pcInPostLgDev = pcW is >= 0x002AF800 and <= 0x002AF980;
            bool pcInSleep = pcW is >= 0x0010C0A0 and <= 0x0010C0AC;
            bool pcInWaitSema = pcW is >= 0x0010BE60 and <= 0x0010BE70;
            if (irxOnly && (pcInPostLgDev || (pcInSleep && raInPostLgDev)
                || (pcInWaitSema && raInPostLgDev)))
            {
                uint gpW = (uint)(sys.EE.GetGpr(28).Lo & 0x1FFFFFFFUL);
                if (gpW is < 0x00400000 or >= 0x01000000) gpW = 0x004E8670;
                uint f23104 = unchecked((uint)((int)gpW - 23104));
                if (f23104 is >= 0x00400000 and < 0x01000000)
                    sys.Memory.Write32(f23104, 1);
                sys.Memory.Write32(BootWaitFlagDefault, 1);
                uint s0w = (uint)(sys.EE.GetGpr(16).Lo & 0xFFFFFFFFUL);
                if (s0w >= 600)
                    sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 1 });
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                sys.EE.PC = postLgDevSuccess;
                sys.EE.COP0_Status &= ~(1u << 1);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_menuKickPulses % 8) == 0)
                    Console.Error.WriteLine(
                        $"[B3] leave post-LGDEV spin SUCCESS pc=0x{pcW:X8} ra=0x{raW:X8} " +
                        $"-> 0x{postLgDevSuccess:X8} v0=1 cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
            }
            if (irxOnly && k != null && (_menuKickPulses % 2) == 0)
            {
                foreach (var t in k.AllThreads)
                {
                    if (!t.Alive || !t.Sleeping) continue;
                    uint savedRa = (uint)(t.SavedRa & 0x1FFFFFFFUL);
                    if (savedRa == 0 && t.HasFullSave && t.SavedGprFull != null && t.SavedGprFull.Length > 31)
                        savedRa = (uint)(t.SavedGprFull[31] & 0x1FFFFFFFUL);
                    uint savedPc = (uint)(t.SavedPc & 0x1FFFFFFFUL);
                    bool postPark = (savedRa is >= 0x002AF800 and <= 0x002AF994)
                        || (savedPc is >= 0x002AF800 and <= 0x002AF994)
                        || (savedPc is >= 0x0010C0A0 and <= 0x0010C0AC && savedRa is >= 0x002AF800 and <= 0x002AF994)
                        || (savedPc is >= 0x0010BE60 and <= 0x0010BE70 && savedRa is >= 0x002AF800 and <= 0x002AF994)
                        || (t.Id == 1 && t.WaitSemaId >= 0x40)
                        || (t.Id == 1 && t.WaitSemaId == 0 && !t.WaitVblank && _menuKickPulses >= 16);
                    if (t.WaitSemaId >= 32)
                    {
                        try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                    if (postPark && t.Id == 1)
                    {
                        t.SavedPc = postLgDevSuccess;
                        if (t.HasFullSave && t.SavedGprFull != null && t.SavedGprFull.Length > 2)
                        {
                            t.SavedGprFull[2] = 1;
                            if (t.SavedGprFull.Length > 16) t.SavedGprFull[16] = 1;
                        }
                        t.WaitSemaId = 0;
                        t.Sleeping = false;
                        t.WaitVblank = false;
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                            && (_menuKickPulses % 8) == 0)
                            Console.Error.WriteLine(
                                $"[B3] re-home sleeping main post-LGDEV SUCCESS " +
                                $"savedRa=0x{savedRa:X8} -> 0x{postLgDevSuccess:X8} " +
                                $"cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
                    }
                    else if (t.WaitSemaId == 0 && !t.WaitVblank)
                        k.WakeupThread(t.Id);
                }
            }
            else if (k != null && (_menuKickPulses % 2) == 0)
            {
                foreach (var t in k.AllThreads)
                {
                    if (!t.Alive || !t.Sleeping) continue;
                    if (t.WaitSemaId >= 32)
                    {
                        try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                    if (t.WaitSemaId == 0 && !t.WaitVblank)
                        k.WakeupThread(t.Id);
                }
            }
        }""",
        "post-LGDEV soft leave",
    )

    t = must_replace(
        t,
        """        // 0x2AF80C..0x2AF90C: post-LGDEV poll *(gp-23104) before STG bind.
        bool inPostLgDevSpin = pc is >= 0x002AF800 and <= 0x002AF910;
        // Periodic plant only while IRX-only — stop once game FILEIO opens (cdvd≫425).
        bool periodic = (_menuKickPulses % 4) == 0 && sys.Cdvd.SectorsRead is >= 400 and < 600;
        if (!inWait1 && !inWait2 && !inWait3 && !inSleep && !inPostLgDevSpin && !periodic) return;""",
        """        // 0x2AF80C..0x2AF90C: post-LGDEV poll *(gp-23104) before STG bind.
        bool inPostLgDevSpin = pc is >= 0x002AF800 and <= 0x002AF910;
        bool inPostLgDevWaitSema = pc is >= 0x0010BE60 and <= 0x0010BE70
            && ((uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL) is >= 0x002AF800 and <= 0x002AF910);
        // Periodic plant only while IRX-only — stop once game FILEIO opens (cdvd≫425).
        bool periodic = (_menuKickPulses % 4) == 0 && sys.Cdvd.SectorsRead is >= 400 and < 600;
        if (!inWait1 && !inWait2 && !inWait3 && !inSleep && !inPostLgDevSpin
            && !inPostLgDevWaitSema && !periodic) return;""",
        "boot-wait postlgdev detect",
    )

    t = must_replace(
        t,
        """        else if (inWait3 || (inSleep && s0 is >= 0x00100000 and < 0x02000000
                             && sys.Memory.Read32(s0 + 0x13A4) != 0))
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = 0x002B35C0; // past wait-3 → jal 0x2AFDD0
            sys.EE.COP0_Status &= ~0x6u;
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_bootWaitFlagPlants <= 12 || _bootWaitFlagPlants % 32 == 0))
            Console.Error.WriteLine(
                $"[B3] plant boot-wait flags pc=0x{pc:X8} s0=0x{s0:X8} " +
                $"*flag1=1 *s0+13A4=1 n={_bootWaitFlagPlants} cdvd={sys.Cdvd.SectorsRead} " +
                $"cyc={sys.MasterCycles}");
    }
}""",
        """        else if (inWait3 || (inSleep && s0 is >= 0x00100000 and < 0x02000000
                             && sys.Memory.Read32(s0 + 0x13A4) != 0))
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = 0x002B35C0; // past wait-3 → jal 0x2AFDD0
            sys.EE.COP0_Status &= ~0x6u;
        }
        else if (inPostLgDevSpin || inPostLgDevWaitSema)
        {
            // Success leave: flag set + s0!=600 → v0=1 epi (NOT timeout 0x2AF920).
            uint s0w = (uint)(sys.EE.GetGpr(16).Lo & 0xFFFFFFFFUL);
            if (s0w >= 600)
                sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = 0x002AF914;
            sys.EE.COP0_Status &= ~0x6u;
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_bootWaitFlagPlants <= 12 || _bootWaitFlagPlants % 32 == 0))
            Console.Error.WriteLine(
                $"[B3] plant boot-wait flags pc=0x{pc:X8} s0=0x{s0:X8} " +
                $"*flag1=1 *s0+13A4=1 n={_bootWaitFlagPlants} cdvd={sys.Cdvd.SectorsRead} " +
                $"cyc={sys.MasterCycles}");
    }
}""",
        "boot-wait success snap",
    )

    p.write_text(t, encoding="utf-8")
    print("OK Burnout3Assist")


def patch_gif():
    p = ROOT / "src/DetPS2.Core/Gif.cs"
    t = p.read_text(encoding="utf-8")

    t = must_replace(
        t,
        """    // PATH3 held while M3P/M3R masks — real HW fills the GIF FIFO (FQC rises) until unmasked.
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
        _path3Held = false;""",
        """    // PATH3 held while M3P/M3R masks — real HW fills the GIF FIFO (FQC rises) until unmasked.
    // Burnout 3 path-sync @ 0x001F1A28 spins on GIF_STAT.FQC (bits 24–28) after starting a
    // masked PATH3 DMA; instant-drain left FQC=0 forever.
    // Multi-kick under long mask: queue (not last-only) so unmask drains all held transfers.
    private const int HeldPath3QueueCap = 48;
    private readonly uint[] _heldPath3AddrQ = new uint[HeldPath3QueueCap];
    private readonly uint[] _heldPath3QwcQ = new uint[HeldPath3QueueCap];
    private int _heldPath3Count;
    private uint _heldPath3TotalQwc;

    public ulong Path3Transfers => _path3Transfers;
    public ulong Path2Transfers => _path2Transfers;
    public ulong Path1Transfers => _path1Transfers;
    public uint HeldPath3Qwc => _heldPath3TotalQwc;
    public int HeldPath3Entries => _heldPath3Count;

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
        _heldPath3Count = 0;
        _heldPath3TotalQwc = 0;""",
        "Gif hold fields",
    )

    t = must_replace(
        t,
        """    private void DrainHeldPath3()
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
    }""",
        """    private void DrainHeldPath3()
    {
        if (_heldPath3Count == 0) return;
        _fifoR = _fifoW = _fifoCount = 0;
        int n = _heldPath3Count;
        _heldPath3Count = 0;
        _heldPath3TotalQwc = 0;
        _apath = 3;
        for (int i = 0; i < n; i++)
        {
            uint addr = _heldPath3AddrQ[i];
            uint qwc = _heldPath3QwcQ[i];
            if (qwc != 0)
                ProcessTransfer(addr, qwc);
        }
        _apath = 0;
    }

    private void EnqueueHeldPath3(uint address, uint qwc)
    {
        if (qwc == 0) return;
        if (_heldPath3Count >= HeldPath3QueueCap)
        {
            // Process oldest now so multi-kick under long M3P is not discarded.
            uint oldA = _heldPath3AddrQ[0];
            uint oldQ = _heldPath3QwcQ[0];
            Array.Copy(_heldPath3AddrQ, 1, _heldPath3AddrQ, 0, HeldPath3QueueCap - 1);
            Array.Copy(_heldPath3QwcQ, 1, _heldPath3QwcQ, 0, HeldPath3QueueCap - 1);
            _heldPath3Count = HeldPath3QueueCap - 1;
            if (_heldPath3TotalQwc >= oldQ) _heldPath3TotalQwc -= oldQ;
            else _heldPath3TotalQwc = 0;
            if (oldQ != 0)
            {
                _apath = 3;
                ProcessTransfer(oldA, oldQ);
                _apath = 0;
            }
        }
        _heldPath3AddrQ[_heldPath3Count] = address;
        _heldPath3QwcQ[_heldPath3Count] = qwc;
        _heldPath3Count++;
        _heldPath3TotalQwc += qwc;
        int words = (int)Math.Min(_heldPath3TotalQwc, 16u) * 4;
        _fifoCount = Math.Min(words, _fifo.Length);
        _fifoR = 0;
        _fifoW = _fifoCount;
    }""",
        "DrainHeldPath3",
    )

    t = must_replace(
        t,
        """                    _fifoR = _fifoW = _fifoCount = 0;
                    _m3p = false;
                    _apath = 0;
                    _path3Held = false;
                    _heldPath3Addr = _heldPath3Qwc = 0;
                }
                break;""",
        """                    _fifoR = _fifoW = _fifoCount = 0;
                    _m3p = false;
                    _apath = 0;
                    _heldPath3Count = 0;
                    _heldPath3TotalQwc = 0;
                }
                break;""",
        "GIF_CTRL reset held",
    )

    t = must_replace(
        t,
        """        if (Path3Masked)
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
        }""",
        """        if (Path3Masked)
        {
            // Hold in FIFO queue: raise FQC so path-sync loops that poll STAT.FQC can proceed.
            // Queue (not last-only) so multi-kick IMAGE/PACKED under long M3P still reaches GS.
            EnqueueHeldPath3(address, qwc);
            return;
        }""",
        "ReceivePath3Data masked",
    )

    p.write_text(t, encoding="utf-8")
    print("OK Gif")


def patch_gs():
    p = ROOT / "src/DetPS2.Core/Gs.cs"
    t = p.read_text(encoding="utf-8")

    t = must_replace(
        t,
        """    // Stats (tests)
    public long PrimitivesDrawn { get; private set; }
    public long PixelsWritten { get; private set; }
    public long FragmentsTested { get; private set; }
    public long FragmentsRejectedDepth { get; private set; }

    public struct Vertex
    {""",
        """    // Stats (tests)
    public long PrimitivesDrawn { get; private set; }
    public long PixelsWritten { get; private set; }
    public long FragmentsTested { get; private set; }
    public long FragmentsRejectedDepth { get; private set; }
    /// <summary>Bytes accepted by IMAGE / BITBLT host→local (telemetry).</summary>
    public long ImageBytesWritten { get; private set; }
    /// <summary>Pixels last composited from DISPFB/FRAME local VRAM into the software FB.</summary>
    public long DispfbPixelsComposited { get; private set; }
    private bool _localMemHasImage;

    public struct Vertex
    {""",
        "Gs stats fields",
    )

    t = must_replace(
        t,
        """        _trxPending = 0;
        _trxPartial = 0;
        _currentPrim = 0;""",
        """        _trxPending = 0;
        _trxPartial = 0;
        ImageBytesWritten = 0;
        DispfbPixelsComposited = 0;
        _localMemHasImage = false;
        _currentPrim = 0;""",
        "Gs Reset image flags",
    )

    t = must_replace(
        t,
        """        int n = Math.Min(data.Length, _localMem.Length - destByteOffset);
        if (n <= 0) return;
        data.Slice(0, n).CopyTo(_localMem.AsSpan(destByteOffset));
        _useProceduralTexture = false;
    }

    /// <summary>Stream host IMAGE bytes into local mem using BITBLT/TRX cursor.</summary>""",
        """        int n = Math.Min(data.Length, _localMem.Length - destByteOffset);
        if (n <= 0) return;
        data.Slice(0, n).CopyTo(_localMem.AsSpan(destByteOffset));
        _useProceduralTexture = false;
        ImageBytesWritten += n;
        _localMemHasImage = true;
    }

    /// <summary>Stream host IMAGE bytes into local mem using BITBLT/TRX cursor.</summary>""",
        "WriteImageData linear",
    )

    t = must_replace(
        t,
        """        if (bi < 0 || bi >= _localMem.Length) return;
        for (int b = 0; b < bpp && bi + b < _localMem.Length; b++)
            _localMem[bi + b] = (byte)(pixel >> (8 * b));
    }

    private static int Log2(int v)""",
        """        if (bi < 0 || bi >= _localMem.Length) return;
        for (int b = 0; b < bpp && bi + b < _localMem.Length; b++)
            _localMem[bi + b] = (byte)(pixel >> (8 * b));
        ImageBytesWritten += bpp;
        _localMemHasImage = true;
    }

    private static int Log2(int v)""",
        "StoreTrxPixel image flag",
    )

    t = must_replace(
        t,
        """    /// <summary>
    /// What the host should show for Soft-GS truth: the software raster framebuffer only.
    /// Host FMV/boot overlay is retired (IRX era) — never preferred over Soft-GS, even if a
    /// legacy assist still toggled <see cref="HostOverlayActive"/>.
    /// Desktop and PresentPipeline should use this for display / PPM.
    /// </summary>
    public ReadOnlySpan<uint> GetPresentSpan() => _framebuffer;

    public bool HostOverlayActive => _hostOverlayActive;""",
        """    /// <summary>
    /// What the host should show for Soft-GS truth: the software raster framebuffer only.
    /// Host FMV/boot overlay is retired (IRX era) — never preferred over Soft-GS, even if a
    /// legacy assist still toggled <see cref="HostOverlayActive"/>.
    /// Desktop and PresentPipeline should use this for display / PPM.
    /// When prim raster is still empty but IMAGE filled local VRAM and DISPFB/FRAME is set,
    /// composite local→FB (SOFTGS_IRX_ERA residual #1 — commercial logo path).
    /// </summary>
    public ReadOnlySpan<uint> GetPresentSpan()
    {
        if (PixelsWritten == 0 && _localMemHasImage)
            CompositeDispfbToFramebuffer();
        return _framebuffer;
    }

    /// <summary>
    /// Copy DISPFB1/2 (else FRAME_1) local VRAM into the software present FB when raster
    /// <see cref="PixelsWritten"/> is still 0. Returns non-black pixels written.
    /// </summary>
    public long CompositeDispfbToFramebuffer()
    {
        if (PixelsWritten > 0) return 0;
        if (!_localMemHasImage) return 0;

        bool fromDispfb = Registers.DISPFB1 != 0 || Registers.DISPFB2 != 0;
        ulong fb = Registers.DISPFB1 != 0 ? Registers.DISPFB1
            : Registers.DISPFB2 != 0 ? Registers.DISPFB2
            : Registers.FRAME_1;
        if (fb == 0) return 0;

        int fbp;
        int fbw;
        int psm;
        int dbx = 0, dby = 0;
        if (fromDispfb)
        {
            fbp = (int)(fb & 0x1FF);
            fbw = (int)((fb >> 9) & 0x3F) * 64;
            psm = (int)((fb >> 15) & 0x1F);
            dbx = (int)((fb >> 32) & 0x7FF);
            dby = (int)((fb >> 43) & 0x7FF);
        }
        else
        {
            fbp = (int)(fb & 0x1FF);
            fbw = (int)((fb >> 16) & 0x3F) * 64;
            psm = (int)((fb >> 24) & 0x3F);
        }
        if (fbw <= 0) fbw = FB_WIDTH;
        if (fbw > 4096) fbw = 4096;
        uint baseBytes = (uint)fbp * 2048u;
        if (baseBytes >= (uint)_localMem.Length) return 0;
        if (psm is not (0x00 or 0x01 or 0x02 or 0x0A))
            psm = 0x00;

        long written = 0;
        int h = FB_HEIGHT;
        int w = Math.Min(FB_WIDTH, fbw);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sx = dbx + x;
                int sy = dby + y;
                uint pixel;
                if (psm == 0x00)
                {
                    int bi = (int)SwizzleOffset32(baseBytes, sx, sy, fbw);
                    if ((uint)bi + 3u >= (uint)_localMem.Length) continue;
                    pixel = (uint)_localMem[bi]
                            | ((uint)_localMem[bi + 1] << 8)
                            | ((uint)_localMem[bi + 2] << 16)
                            | ((uint)_localMem[bi + 3] << 24);
                }
                else if (psm == 0x01)
                {
                    int bi = (int)SwizzleOffset32(baseBytes, sx, sy, fbw);
                    if ((uint)bi + 2u >= (uint)_localMem.Length) continue;
                    pixel = (uint)_localMem[bi]
                            | ((uint)_localMem[bi + 1] << 8)
                            | ((uint)_localMem[bi + 2] << 16)
                            | 0xFF000000u;
                }
                else
                {
                    int bi = (int)baseBytes + (sy * fbw + sx) * 2;
                    if ((uint)bi + 1u >= (uint)_localMem.Length) continue;
                    ushort p16 = (ushort)(_localMem[bi] | (_localMem[bi + 1] << 8));
                    int r = (p16 & 0x1F) << 3;
                    int g = ((p16 >> 5) & 0x1F) << 3;
                    int b = ((p16 >> 10) & 0x1F) << 3;
                    pixel = (uint)(0xFF000000 | (r << 16) | (g << 8) | b);
                }

                if ((pixel & 0x00FFFFFF) == 0) continue;
                _framebuffer[y * FB_WIDTH + x] = pixel | 0xFF000000;
                written++;
            }
        }

        if (written > 0)
        {
            PixelsWritten += written;
            PrimitivesDrawn++;
            DispfbPixelsComposited = written;
        }
        return written;
    }

    public bool HostOverlayActive => _hostOverlayActive;""",
        "GetPresentSpan + CompositeDispfb",
    )

    p.write_text(t, encoding="utf-8")
    print("OK Gs")


def patch_program():
    p = ROOT / "src/DetPS2.Core/Program.cs"
    t = p.read_text(encoding="utf-8")

    t = must_replace(
        t,
        """        if (driveHostPresent)
        {
            const ulong slice = 1_000_000;
            while (remaining > 0)
            {
                ulong step = Math.Min(slice, remaining);
                traceSys.RunFor(step);
                traceSys.ActiveQuirk?.OnHostPresent(traceSys);
                remaining -= step;
            }
        }
        else
        {
            traceSys.RunFor(remaining);
        }""",
        """        if (driveHostPresent)
        {
            const ulong slice = 1_000_000;
            while (remaining > 0)
            {
                ulong step = Math.Min(slice, remaining);
                traceSys.RunFor(step);
                traceSys.ActiveQuirk?.OnHostPresent(traceSys);
                // Soft-GS DISPFB residual: IMAGE may fill local VRAM without prim raster.
                traceSys.Gs.CompositeDispfbToFramebuffer();
                remaining -= step;
            }
        }
        else
        {
            traceSys.RunFor(remaining);
            traceSys.Gs.CompositeDispfbToFramebuffer();
        }""",
        "blocker-trace host-present composite",
    )

    # scoreboard-metrics host present
    if "smSys.Gs.CompositeDispfbToFramebuffer();" not in t:
        t = must_replace(
            t,
            """                smSys.ActiveQuirk?.OnHostPresent(smSys);
                remaining -= step;""",
            """                smSys.ActiveQuirk?.OnHostPresent(smSys);
                smSys.Gs.CompositeDispfbToFramebuffer();
                remaining -= step;""",
            "scoreboard-metrics composite",
        )

    p.write_text(t, encoding="utf-8")
    print("OK Program")


def main():
    patch_burnout3()
    patch_gif()
    patch_gs()
    patch_program()
    print("ALL PATCHES APPLIED")


if __name__ == "__main__":
    main()
