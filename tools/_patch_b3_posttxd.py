from pathlib import Path

p = Path("src/DetPS2.Core/GameQuirks/Burnout3Assist.cs")
t = p.read_text(encoding="utf-8")
t2 = t.replace(
    "if (_lgDevFullyDone && sys.Cdvd.SectorsRead >= 2000 && sys.MasterCycles >= 72_000_000)",
    "if (_lgDevFullyDone && sys.Cdvd.SectorsRead >= 2000 && sys.MasterCycles >= 55_000_000)",
)
old = """        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        // SIF DMA copy body (0x10FB30 first path + 0x10FB80 second path).
        bool sifCopy = pc is (>= 0x0010FB30 and <= 0x0010FB7C)
            or (>= 0x0010FB80 and <= 0x0010FBD0);
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        bool waitOnWorker = pc is >= 0x0010BE60 and <= 0x0010BE70
                            && ra is >= 0x00242A40 and <= 0x00242B80;

        if (!sifCopy && !waitOnWorker) return;"""
new = """        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
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
        }

        // SIF DMA copy body (0x10FB30 first path + 0x10FB80 second path).
        bool sifCopy = pc is (>= 0x0010FB30 and <= 0x0010FB7C)
            or (>= 0x0010FB80 and <= 0x0010FBD0);
        bool waitOnWorker = pc is >= 0x0010BE60 and <= 0x0010BE70
                            && ra is >= 0x00242A40 and <= 0x00242B80;

        if (!sifCopy && !waitOnWorker) return;"""
if old not in t2:
    print("OLD NOT FOUND")
    i = t2.find("MaybeEscapePostTxdHang")
    print(repr(t2[i : i + 900]))
    raise SystemExit(1)
t2 = t2.replace(old, new, 1)
p.write_text(t2, encoding="utf-8")
print("OK", t2.count("post-TXD MMIO probe"), t2.count("55_000_000"))
