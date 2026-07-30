from pathlib import Path
import re

p = Path("src/DetPS2.Core/GameQuirks/Burnout3Assist.cs")
t = p.read_text(encoding="utf-8")

def rep(old, new, label):
    global t
    if old not in t:
        raise SystemExit(f"MISSING: {label}")
    t = t.replace(old, new, 1)
    print("ok", label)

rep(
"""        _postTxdEscapes = 0;
        _lastPostTxdEscapeCyc = 0;
    }

    public void OnDiscMounted(Ps2System sys)""",
"""        _postTxdEscapes = 0;
        _lastPostTxdEscapeCyc = 0;
        _frontendKickPulses = 0;
        _lastFrontendKickCyc = 0;
    }

    public void OnDiscMounted(Ps2System sys)""",
"reset")

rep(
"""                // Prefer natural fall-through (0x2371A0) so s1-indexed store runs;
                // after heavy thrash / when parked at prologue, epilogue return.
                bool heavy = _sleepWakeups >= 8 || _menuKickPulses >= 16 || _vblankExits >= 4
                    || pc is >= 0x00237120 and <= 0x00237170;
                if (heavy)
                {
                    // Clamp s1 into 0..3 so success path writes a valid slot, then epilogue.
                    uint s1 = (uint)(sys.EE.GetGpr(17).Lo & 0xFFUL);
                    if (s1 > 3)
                        sys.EE.SetGpr(17, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
                    sys.EE.PC = 0x002371E0; // ld ra / restore / jr ra
                    _vblankExits++;
                }
                else
                    sys.EE.PC = 0x002371A0; // past beq delay — success body""",
"""                // Prefer natural fall-through (0x2371A0) so s1-indexed store runs.
                // IRX-only: avoid heavy epilogue snap (tip over-exited VBlank, no STG).
                bool irxOnly = sys.Cdvd.SectorsRead < 600;
                bool heavy = !irxOnly && (_sleepWakeups >= 8 || _menuKickPulses >= 16
                    || _vblankExits >= 4 || pc is >= 0x00237120 and <= 0x00237170);
                if (heavy)
                {
                    // Clamp s1 into 0..3 so success path writes a valid slot, then epilogue.
                    uint s1 = (uint)(sys.EE.GetGpr(17).Lo & 0xFFUL);
                    if (s1 > 3)
                        sys.EE.SetGpr(17, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
                    sys.EE.PC = 0x002371E0; // ld ra / restore / jr ra
                    _vblankExits++;
                }
                else
                {
                    sys.EE.PC = 0x002371A0; // past beq delay — success body
                    if (irxOnly) _vblankExits++;
                }""",
"vblank")

rep(
"""        if (_lgDevFullyDone)
        {
            PlantLgDevEntryStub(sys);
            PlantLgDevCallRpcLeafStub(sys);
            sys.Memory.Write32(LgDevPostFlag, 0);""",
"""        if (_lgDevFullyDone)
        {
            // Wave-8: delay stubs until residual n>=24 (early stub dies residual n=2-3).
            if (_lgDevEscapes >= 24)
            {
                PlantLgDevEntryStub(sys);
                PlantLgDevCallRpcLeafStub(sys);
            }
            sys.Memory.Write32(LgDevPostFlag, 0);""",
"fullydone")

rep(
"""            bool forceNow = (pristine && sys.MasterCycles >= 18_000_000)
                            || sys.MasterCycles >= 22_500_000;""",
"""            bool forceNow = (pristine && sys.MasterCycles >= 22_000_000)
                            || sys.MasterCycles >= 23_500_000;""",
"forceNow")

rep(
"""            _lgDevEscapes++;
            _lgDevFullyDone = true;
            PlantLgDevEntryStub(sys);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[B3] force CallRpc→lgDev epilogue pc=0x{pc:X8} sp=0x{sp:X8} s1=0x{s1:X8} " +
                    $"ra*=0x443C44 pristine={pristine} n={_lgDevEscapes} cyc={sys.MasterCycles}");""",
"""            _lgDevEscapes++;
            _lgDevFullyDone = true;
            // Delay entry stub — residual re-entry needed for STG cadence.
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[B3] force CallRpc→lgDev epilogue pc=0x{pc:X8} sp=0x{sp:X8} s1=0x{s1:X8} " +
                    $"ra*=0x443C44 pristine={pristine} n={_lgDevEscapes} cyc={sys.MasterCycles}");""",
"force-plant")

rep(
"""        _lgDevEscapes++;
        _lgDevFullyDone = true;
        PlantLgDevEntryStub(sys);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[B3] force lgDeviceInit complete ({why}) pc=0x{fromPc:X8} " +
                $"-> 0x{LgDevSuccessReturn:X8} sp=0x{(uint)sys.EE.GetGpr(29).Lo:X8} n={_lgDevEscapes} cyc={sys.MasterCycles}");""",
"""        _lgDevEscapes++;
        _lgDevFullyDone = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[B3] force lgDeviceInit complete ({why}) pc=0x{fromPc:X8} " +
                $"-> 0x{LgDevSuccessReturn:X8} sp=0x{(uint)sys.EE.GetGpr(29).Lo:X8} n={_lgDevEscapes} cyc={sys.MasterCycles}");""",
"ForceLgDevSuccess")

rep(
"""        // Sticky re-plant entry stub after LGDEV so boot cannot re-enter wheel init.
        if (_lgDevFullyDone)
            PlantLgDevEntryStub(sys);""",
"""        // Sticky re-plant only after residual window (n>=24).
        if (_lgDevFullyDone && _lgDevEscapes >= 24)
            PlantLgDevEntryStub(sys);""",
"menu-kick-plant")

old_mmio = """    private int _deadEpiLeaves;
    private int _postTxdEscapes;
    private ulong _lastPostTxdEscapeCyc;

    /// <summary>
    /// After STG + full Global.txd (cdvd≥2000), live wave-7 parks in the SIF transfer
    /// byte-copy at <c>0x10FB80..0x10FBCC</c> (disasm: <c>lbu/sb</c> loop with
    /// <c>*(a3+4)</c> size and <c>*(a3+12)|0x20000000</c> dest) when size/dest are
    /// garbage → UnknownMmioRead flood. Exit the real loop epilogue at <c>0x10FD9C</c>
    /// so CallRpc/DBC peers can resume and open FRONTEND via SHARED GTFS.
    /// Never rewrites residual LGDEV force cadence.
    /// </summary>
    private void MaybeEscapePostTxdHang(Ps2System sys)
    {
        if (_postTxdEscapes >= 256) return;
        if (sys.MasterCycles - _lastPostTxdEscapeCyc < 40_000) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);

        // Live deliver: post full-TXD UnknownMmioRead flood @ 0x21A5xx / park 0x1F308C (px=0).
        bool mmioProbe = (pc is >= 0x0021A540 and <= 0x0021A580
                          || pc is >= 0x00218740 and <= 0x00218770
                          || pc is >= 0x001F3080 and <= 0x001F30A0)
                         && sys.Cdvd.SectorsRead >= 2000;
        if (mmioProbe)
        {
            _lastPostTxdEscapeCyc = sys.MasterCycles;
            _postTxdEscapes++;
            uint resume = 0x001F2520; // past flip-wait
            if (ra is >= 0x00100000 and < 0x00400000 && sys.Memory.IsLikelyEeCode(ra)
                && ra is not (>= 0x0021A500 and <= 0x0021A600)
                && ra is not (>= 0x001F3080 and <= 0x001F30C0)
                && ra is not (>= 0x001F24E0 and <= 0x001F2520))
                resume = ra;
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            ArmFlipConsumer(sys);
            var kk = sys.Hle?.Kernel;
            if (kk != null)
            {
                foreach (var th in kk.AllThreads)
                {
                    if (!th.Alive || !th.Sleeping) continue;
                    if (th.WaitSemaId >= 32) { try { kk.SignalSema(th.WaitSemaId); } catch { } }
                    if (th.WaitSemaId == 0 && !th.WaitVblank) kk.WakeupThread(th.Id);
                }
            }
            try
            {
                sys.Pad.SetButtons((_postTxdEscapes % 4) < 2
                    ? (uint)PadInput.Button.Start : (uint)PadInput.Button.Cross);
            }
            catch { }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_postTxdEscapes <= 16 || _postTxdEscapes % 16 == 0))
                Console.Error.WriteLine(
                    $"[B3] post-TXD MMIO probe leave pc=0x{pc:X8} ra=0x{ra:X8} -> 0x{resume:X8} " +
                    $"n={_postTxdEscapes} cdvd={sys.Cdvd.SectorsRead} gifP3={sys.Gif.Path3Transfers} " +
                    $"cyc={sys.MasterCycles}");
            return;
        }"""

new_mmio = r"""    private int _deadEpiLeaves;
    private int _postTxdEscapes;
    private ulong _lastPostTxdEscapeCyc;
    private int _frontendKickPulses;
    private ulong _lastFrontendKickCyc;

    public const uint GifPathFlushEntry = 0x0021A4F0;
    public const uint GifPathFlushEpilogue = 0x0021A5D8;
    public const uint GifSubmitEntry = 0x001F3080;
    public const uint GifSubmitCallerReturn = 0x00218774;

    private static void GetGifRingCells(Ps2System sys, out uint startCell, out uint endCell, out uint dstCell)
    {
        uint gp = (uint)(sys.EE.GetGpr(28).Lo & 0x1FFFFFFFUL);
        if (gp is < 0x00400000 or >= 0x01000000)
            gp = 0x004E8670;
        startCell = gp - 27936u;
        endCell = gp - 23960u;
        dstCell = gp - 24240u;
    }

    private static bool IsAbsurdGifRing(Ps2System sys)
    {
        GetGifRingCells(sys, out uint startCell, out uint endCell, out uint dstCell);
        uint startPhys = sys.Memory.Read32(startCell) & 0x1FFFFFFFu;
        uint endPhys = sys.Memory.Read32(endCell) & 0x1FFFFFFFu;
        uint dstPhys = sys.Memory.Read32(dstCell) & 0x1FFFFFFFu;
        return startPhys >= 0x10000000u || endPhys >= 0x10000000u
               || dstPhys >= 0x10000000u
               || (endPhys > startPhys && (endPhys - startPhys) > 0x00080000u)
               || endPhys < startPhys
               || startPhys < 0x00100000u
               || startPhys >= (uint)SystemMemory.RDRAM_SIZE;
    }

    private static void CollapseAbsurdGifRing(Ps2System sys)
    {
        if (!IsAbsurdGifRing(sys)) return;
        GetGifRingCells(sys, out uint startCell, out uint endCell, out uint dstCell);
        uint start = sys.Memory.Read32(startCell);
        uint startPhys = start & 0x1FFFFFFFu;
        uint safe = startPhys is >= 0x00100000 and < 0x01E00000u ? start : 0x00700000u;
        sys.Memory.Write32(startCell, safe);
        sys.Memory.Write32(endCell, safe);
        sys.Memory.Write32(dstCell, safe);
    }

    private void KickPostTxdPresentation(Ps2System sys)
    {
        _lastFrontendKickCyc = sys.MasterCycles;
        if (_frontendKickPulses >= 4096) return;
        _frontendKickPulses++;
        ArmFlipConsumer(sys);
        uint pending = sys.Memory.Read32(PendingCountAddr) & 0xFF;
        if (pending > 0)
        {
            sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.VIF1, (int)Math.Min(pending + 1, 4u));
            sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.GIF, (int)Math.Min(pending + 1, 4u));
            if ((_frontendKickPulses % 4) == 0)
                sys.Memory.Write8(PendingCountAddr, 0);
        }
        PlantWakeFlags(sys, VblankWakeFlagBase);
        var k = sys.Hle?.Kernel;
        if (k != null)
        {
            foreach (var t in k.AllThreads)
            {
                if (!t.Alive || !t.Sleeping) continue;
                if (t.WaitSemaId >= 32) { try { k.SignalSema(t.WaitSemaId); } catch { } }
                if (t.WaitSemaId == 0 && !t.WaitVblank) k.WakeupThread(t.Id);
            }
        }
        int phase = _frontendKickPulses % 6;
        uint buttons = phase switch
        {
            0 or 1 => (uint)PadInput.Button.Start,
            3 or 4 => (uint)PadInput.Button.Cross,
            _ => 0u
        };
        try { sys.Pad.SetButtons(buttons); } catch { }
        uint pcNow = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pcNow is >= 0x001F24E0 and <= 0x001F251C && _postTxdEscapes > 0)
        {
            sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = 0x001F2520;
            sys.EE.COP0_Status &= ~0x6u;
        }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_frontendKickPulses <= 8 || _frontendKickPulses % 32 == 0))
            Console.Error.WriteLine(
                $"[B3] post-TXD presentation kick n={_frontendKickPulses} " +
                $"cdvd={sys.Cdvd.SectorsRead} gifP3={sys.Gif.Path3Transfers} " +
                $"px={sys.Gs.PixelsWritten} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Post full-TXD: absurd GIF path-flush (0x21A4F0 bulk lq/sq with MMIO src) / submit
    /// (0x1F308C) / SIF copy. Collapse ring + leave epilogue when absurd only.
    /// Never rewrites residual LGDEV force cadence.
    /// </summary>
    private void MaybeEscapePostTxdHang(Ps2System sys)
    {
        if (_postTxdEscapes >= 1024) return;
        if (_postTxdEscapes > 0 && sys.Cdvd.SectorsRead >= 2000
            && sys.MasterCycles - _lastFrontendKickCyc >= 200_000)
            KickPostTxdPresentation(sys);
        if (sys.MasterCycles - _lastPostTxdEscapeCyc < 4_000) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);

        bool inGifFlush = pc is >= 0x0021A4F0 and <= 0x0021A5E4;
        bool inGifSubmit = pc is >= 0x001F3080 and <= 0x001F3500;
        bool inFlushCaller = pc is >= 0x00218700 and <= 0x00218790;
        bool mmioProbe = (inGifFlush || inGifSubmit || inFlushCaller)
                         && sys.Cdvd.SectorsRead >= 2000;
        if (mmioProbe)
        {
            bool absurd = IsAbsurdGifRing(sys);
            if (!absurd && inGifFlush)
            {
                uint t7 = (uint)(sys.EE.GetGpr(15).Lo & 0xFFFFFFFFUL);
                if ((t7 & 0x1FFFFFFFu) >= 0x10000000u || ((t7 & 0x1FFFFFFFu) is > 0 and < 0x00100000u))
                    absurd = true;
            }
            if (!absurd && inGifSubmit)
            {
                uint a1 = (uint)(sys.EE.GetGpr(5).Lo & 0xFFFFFFFFUL);
                uint s0pkt = (uint)(sys.EE.GetGpr(16).Lo & 0xFFFFFFFFUL);
                if (a1 > 0x4000 || s0pkt > 0x4000) absurd = true;
            }
            if (!absurd) return;

            _lastPostTxdEscapeCyc = sys.MasterCycles;
            _postTxdEscapes++;
            CollapseAbsurdGifRing(sys);

            uint resume;
            if (inGifFlush)
                resume = GifPathFlushEpilogue;
            else if (inGifSubmit)
            {
                resume = (ra is >= 0x0021A520 and <= 0x0021A530)
                    ? GifPathFlushEpilogue
                    : (ra is >= 0x00100000 and < 0x00400000 && sys.Memory.IsLikelyEeCode(ra)
                       && ra is not (>= 0x001F3080 and <= 0x001F3500)
                        ? ra
                        : GifSubmitCallerReturn);
            }
            else if (ra is >= 0x00100000 and < 0x00400000 && sys.Memory.IsLikelyEeCode(ra)
                     && ra is not (>= 0x0021A4F0 and <= 0x0021A5E8)
                     && ra is not (>= 0x001F3080 and <= 0x001F3500)
                     && ra is not (>= 0x001F24E0 and <= 0x001F2520))
                resume = ra;
            else
                resume = GifSubmitCallerReturn;

            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            ArmFlipConsumer(sys);
            KickPostTxdPresentation(sys);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_postTxdEscapes <= 16 || _postTxdEscapes % 16 == 0))
                Console.Error.WriteLine(
                    $"[B3] post-TXD GIF-flush leave pc=0x{pc:X8} ra=0x{ra:X8} -> 0x{resume:X8} " +
                    $"n={_postTxdEscapes} cdvd={sys.Cdvd.SectorsRead} gifP3={sys.Gif.Path3Transfers} " +
                    $"px={sys.Gs.PixelsWritten} cyc={sys.MasterCycles}");
            return;
        }"""

rep(old_mmio, new_mmio, "mmio")

p.write_text(t, encoding="utf-8")
print("Burnout3Assist written", len(t))

p2 = Path("src/DetPS2.Core/Ps2System.cs")
t2 = p2.read_text(encoding="utf-8")
m = re.search(
    r'(or \(>= 0x80000180UL and <= 0x80000200UL\);)\s*\n(\s*)ulong slice = \(criHot \|\| gowHot\) \? sliceCri : sliceDefault;',
    t2,
)
if not m:
    raise SystemExit("Ps2System pattern missing")
ind = m.group(2)
repl = (
    m.group(1)
    + f"\n{ind}// Burnout 3 post-TXD GIF flush thrash — tight slices."
    + f"\n{ind}bool b3Hot = ActiveQuirk is Burnout3Assist && pcPhys is"
    + f"\n{ind}    (>= 0x0021A4F0UL and <= 0x0021A5E8UL)"
    + f"\n{ind}    or (>= 0x001F3080UL and <= 0x001F3500UL)"
    + f"\n{ind}    or (>= 0x00218700UL and <= 0x00218790UL);"
    + f"\n{ind}ulong slice = (criHot || gowHot || b3Hot) ? sliceCri : sliceDefault;"
)
t2 = t2[: m.start()] + repl + t2[m.end() :]
p2.write_text(t2, encoding="utf-8")
print("Ps2System b3Hot OK")
