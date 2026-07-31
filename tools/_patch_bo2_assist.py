from pathlib import Path

p = Path(__file__).resolve().parents[1] / "src/DetPS2.Core/GameQuirks/BloodOmen2SnAssist.cs"
c = p.read_text(encoding="utf-8")
if "MaybeEscapeGoeFileTokenThrash" in c:
    print("already patched")
    raise SystemExit(0)

# Reset
old = """        _snPrintfStubbed = false;
        _vtCallStubbed = false;
    }"""
new = """        _snPrintfStubbed = false;
        _vtCallStubbed = false;
        _goeTokenEscapes = 0;
    }"""
if old not in c:
    raise SystemExit("Reset block not found")
c = c.replace(old, new, 1)

# PulseWaiters — match on unique SoftStubBadVtCall + following After GOE comment start
idx = c.find("SoftStubBadVtCall(sys);")
if idx < 0:
    raise SystemExit("SoftStubBadVtCall not found")
# Find the WAVE-3 SoftStubBadVtCall (first one in PulseWaiters after "WAVE 3")
wave = c.find("// WAVE 3")
if wave < 0:
    raise SystemExit("WAVE 3 not found")
idx = c.find("SoftStubBadVtCall(sys);", wave)
if idx < 0:
    raise SystemExit("SoftStubBadVtCall after WAVE 3 not found")
# Insert after that line
line_end = c.find("\n", idx)
insert = """
        // Post-KAIN: soft-stub SN printf so Dest Database storm cannot monopolize 100M.
        // Gate at cdvd>=500 so Manager State still opens KAIN.IMP first.
        if (sys.Cdvd.SectorsRead >= 500 && sys.Gs.PixelsWritten < 100_000)
            SoftStubSnPrintf(sys);

        // Post-KAIN goefile token thrash @0x4830xx - unwind frame toward CODE/MAINMENU.
        if (sys.Cdvd.SectorsRead >= 500 && sys.Gs.PixelsWritten < 50_000)
            MaybeEscapeGoeFileTokenThrash(sys, c);
"""
c = c[: line_end + 1] + insert + c[line_end + 1 :]

method = r'''
    private int _goeTokenEscapes;

    /// <summary>
    /// Unstick post-KAIN goefile token thrash at <c>0x483040..0x483090</c>.
    /// After pack KAIN.IMP (PRECODE) full-read @ <c>0xA242A0</c>, EE sticks scanning for
    /// token <c>0x25</c> in a frame ending at epilogue <c>0x48444C</c>
    /// (<c>ld ra,704(sp); addiu sp,720</c>). Soft-stub sibling <c>0x482E30</c>; unwind
    /// the 0x2D0 frame so boot can continue toward CODE/MAINMENU Open.
    /// </summary>
    private void MaybeEscapeGoeFileTokenThrash(Ps2System sys, ulong c)
    {
        uint head = sys.Memory.Read32(0x00482E30);
        if (head != 0 && head != 0x03E00008u)
        {
            sys.Memory.Write32(0x00482E30, 0x03E00008u); // jr ra
            sys.Memory.Write32(0x00482E34, 0x0000102Du); // daddu v0, zero, zero
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && _goeTokenEscapes == 0)
                Console.Error.WriteLine("[BO2] soft-stub goefile process @ 0x482E30 (jr ra; v0=0)");
        }

        if (_goeTokenEscapes >= 16) return;
        if (c - _lastTitleSmCyc < 120_000) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);

        bool midFrame = pc is >= 0x00483018 and <= 0x00484474;
        bool dataThrash = pc is < 0x00120000
            || (pc is >= 0x00500000 and < 0x02000000);
        if (!midFrame && !dataThrash) return;

        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        uint resume = 0;
        if (midFrame && sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 0x2D0u)
        {
            uint raSlot = sys.Memory.Read32(sp + 0x2C0) & 0x1FFFFFFFu;
            if (raSlot is >= 0x00200000 and < 0x004A0000
                && (raSlot < 0x00483018u || raSlot >= 0x00484480u)
                && IsSafeCodeTarget(sys, raSlot))
            {
                resume = raSlot;
                sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = sp + 0x2D0 });
            }
        }
        if (resume == 0)
        {
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            if (IsColdSafeResume(sys, ra) && ra != pc
                && (ra < 0x00483018u || ra >= 0x00484480u))
                resume = ra;
        }
        if (resume == 0)
            resume = PickSafeResume(sys, pc);
        if (resume is >= 0x00483018 and <= 0x00484474)
            resume = 0;
        if (resume == 0 || resume == pc || !IsSafeCodeTarget(sys, resume))
            resume = 0x0048A980;

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        try
        {
            foreach (var t in sys.Hle.Kernel.AllThreads)
            {
                if (t.Alive && t.Id == 1 && !t.Started)
                    sys.Hle.Kernel.StartAndMaybeSwitch(sys.EE, 1, switchNow: true, arg: 0, fromSyscall: false);
            }
        }
        catch { /* ignore */ }
        ArmGifPath3(sys);
        _goeTokenEscapes++;
        _titleSmEscapes++;
        _lastTitleSmCyc = c;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_goeTokenEscapes <= 12 || _goeTokenEscapes % 8 == 0))
            Console.Error.WriteLine(
                $"[BO2] unwind goefile frame 0x{pc:X8} -> 0x{resume:X8} " +
                $"n={_goeTokenEscapes} cdvd={sys.Cdvd.SectorsRead} cyc={c}");
    }

'''

old_fields = """    private bool _snPrintfStubbed;
    private bool _vtCallStubbed;

    /// <summary>
    /// SN ProDG printf channel entry at <c>0x46FAF8</c> (sp-=160, CallRpc sid=0x534E03)."""
new_fields = """    private bool _snPrintfStubbed;
    private bool _vtCallStubbed;
""" + method + """    /// <summary>
    /// SN ProDG printf channel entry at <c>0x46FAF8</c> (sp-=160, CallRpc sid=0x534E03)."""
if old_fields not in c:
    raise SystemExit("fields block not found")
c = c.replace(old_fields, new_fields, 1)

old_cold = """        if (addr is >= 0x002F1700 and <= 0x002F1780) return false;
        return true;
    }

    private static uint PickSafeResume"""
new_cold = """        if (addr is >= 0x002F1700 and <= 0x002F1780) return false;
        // Post-KAIN goefile token-scan frame - cold re-entry re-thrash.
        if (addr is >= 0x00483018 and <= 0x00484474) return false;
        if (addr is >= 0x00482E30 and <= 0x00482E40) return false;
        return true;
    }

    private static uint PickSafeResume"""
if old_cold not in c:
    raise SystemExit("cold resume block not found")
c = c.replace(old_cold, new_cold, 1)

p.write_text(c, encoding="utf-8")
print("patched", p)
print("pulse", "MaybeEscapeGoeFileTokenThrash(sys" in c)
print("method", "private void MaybeEscapeGoeFileTokenThrash" in c)
print("cold", "0x00483018 and <= 0x00484474) return false" in c)
print("len", len(c))
