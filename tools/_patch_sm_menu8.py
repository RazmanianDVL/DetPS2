#!/usr/bin/env python3
"""Wave-8 MidwayBootAssist MENU patches. Handles LF or CRLF."""
from pathlib import Path
import re

p = Path("src/DetPS2.Core/MidwayBootAssist.cs")
raw = p.read_bytes()
nl = b"\r\n" if b"\r\n" in raw else b"\n"
t = raw.decode("utf-8").replace("\r\n", "\n")
orig = t


def rep(old: str, new: str, label: str) -> None:
    global t
    if old not in t:
        raise SystemExit(f"FAIL {label}")
    t = t.replace(old, new, 1)


rep(
    "        if (c >= 70_000_000 && sys.Gif.Path3Transfers >= 12)\n"
    "            MaybeBreakMenuCallbackCountdown(sys);",
    "        // Wave-8: gifP3>=11 (not 12) so plateau-11 covers pad accept.\n"
    "        if (c >= 70_000_000 && sys.Gif.Path3Transfers >= 11)\n"
    "            MaybeBreakMenuCallbackCountdown(sys);",
    "cb-gate",
)

rep(
    "        if (c >= 60_000_000 && sys.Cdvd.SectorsRead >= 100_000)\n"
    "            MaybeHoldStreamWorkGate(sys);\n"
    "        // Lock wrappers 0x426EF8/0x426F04 thrash after group-6 fills (refcount @ 0x54E5E0).\n"
    "        if (c >= 70_000_000 && sys.Gif.Path3Transfers >= 12)\n"
    "            MaybeBreakLockWrapperThrash(sys);",
    "        if (c >= 60_000_000 && sys.Cdvd.SectorsRead >= 100_000)\n"
    "            MaybeHoldStreamWorkGate(sys);\n"
    "        // Wave-8: minimal stream cookie *0x5BB860=1 (FUN_0043ccf8 arg / slot-style active).\n"
    "        if (c >= 60_000_000 && sys.Cdvd.SectorsRead >= 100_000)\n"
    "            MaybeInitStreamCookie(sys);\n"
    "        // Lock wrappers 0x426EF8/0x426F04 thrash after group-6 fills (refcount @ 0x54E5E0).\n"
    "        // Wave-8: gifP3>=11 (not 12).\n"
    "        if (c >= 70_000_000 && sys.Gif.Path3Transfers >= 11)\n"
    "            MaybeBreakLockWrapperThrash(sys);",
    "cookie-lock",
)

m = re.search(
    r"    private void MaybeBreakMenuCallbackCountdown\(Ps2System sys\)\n    \{.*?\n    \}",
    t,
    re.S,
)
if not m:
    raise SystemExit("FAIL cb-body")
cb = """    private int _cbCountdownVisits;
    private ulong _lastCbCountdownVisitCyc;

    private void MaybeBreakMenuCallbackCountdown(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pc is < 0x00427570 or > 0x00427598) return;
        if (_cbCountdownBreaks >= 128) return;

        if (sys.MasterCycles - _lastCbCountdownVisitCyc < 200_000)
            _cbCountdownVisits++;
        else
            _cbCountdownVisits = 1;
        _lastCbCountdownVisitCyc = sys.MasterCycles;
        if (sys.MasterCycles - _lastCbCountdownCyc < 80_000) return;

        long s2 = unchecked((int)(uint)sys.EE.GetGpr(18).Lo);
        bool absurd = s2 >= 64;
        bool sticky = _cbCountdownVisits >= 4;
        if (!absurd && !sticky) return;
        if (s2 < 0 && !sticky) return;

        sys.EE.SetGpr(18, new EmotionEngine.Gpr128 { Lo = 0xFFFFFFFFUL });
        sys.EE.PC = 0x0042759C;
        sys.LastGoodEePc = 0x0042759C;
        _lastCbCountdownCyc = sys.MasterCycles;
        _cbCountdownBreaks++;
        _cbCountdownVisits = 0;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_cbCountdownBreaks <= 12 || _cbCountdownBreaks % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] break menu callback countdown s2 was {s2} -> -1 / 0x42759C " +
                $"(absurd={absurd} sticky={sticky}) n={_cbCountdownBreaks} " +
                $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }"""
t = t[: m.start()] + cb + t[m.end() :]

rep(
    "        if (pc is < 0x00385650 or > 0x00385688) return;\n"
    "        if (_vuBlitGuards >= 256) return;",
    "        // Wave-8: include post-blit COP2 siblings (live park 0x38568C).\n"
    "        if (pc is < 0x00385650 or > 0x00385720) return;\n"
    "        if (_vuBlitGuards >= 512) return;",
    "vu-range",
)

m = re.search(
    r"        bool stickyThrash = _vuBlitVisits >= 8;\n"
    r"        if \(!a0InCode && !a0Nonsense && !stickyThrash\) return;.*?"
    r'\$"n=\{_vuBlitGuards\} gifP3=\{sys\.Gif\.Path3Transfers\} cyc=\{sys\.MasterCycles\}"\);\n'
    r"    \}",
    t,
    re.S,
)
if not m:
    raise SystemExit("FAIL vu-body")
vu = """        bool pastEpilogue = pc > 0x00385688;
        bool stickyThrash = _vuBlitVisits >= (pastEpilogue ? 4 : 8);
        if (!a0InCode && !a0Nonsense && !stickyThrash) return;

        const uint scratch = 0x01F00000;
        if (a0InCode || a0Nonsense)
            sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = scratch });
        sys.EE.SetGpr(7, new EmotionEngine.Gpr128 { Lo = 0 });

        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        uint resume = 0x00385688;
        if (ra is >= 0x00100000 and < 0x00800000
            && ra is not (>= 0x00385650 and <= 0x00385720)
            && sys.Memory.IsLikelyEeCode(ra))
            resume = ra;

        if (stickyThrash || pastEpilogue)
        {
            uint force = 0;
            if (sys.Memory.IsLikelyEeCode(0x004147F8UL)) force = 0x004147F8;
            else if (sys.Memory.IsLikelyEeCode(0x00427518UL)) force = 0x00427518;
            else if (sys.Memory.IsLikelyEeCode(0x0043F920UL)) force = 0x0043F920;
            if (force != 0) resume = force;
            ReHomeSpIfInHleScratch(sys);
            try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); } catch { /* ignore */ }
        }

        sys.EE.PC = resume;
        sys.LastGoodEePc = resume;
        _lastVuBlitGuardCyc = sys.MasterCycles;
        _vuBlitGuards++;
        _vuBlitVisits = 0;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_vuBlitGuards <= 16 || _vuBlitGuards % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] escape VU blit thrash a0=0x{a0:X8} pc=0x{pc:X8} -> 0x{resume:X8} " +
                $"(code={a0InCode} nonsense={a0Nonsense} thrash={stickyThrash} pastEp={pastEpilogue}) " +
                $"n={_vuBlitGuards} gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }"""
t = t[: m.start()] + vu + t[m.end() :]

rep(
    "    private int _lockWrapperBreaks;\n"
    "    private ulong _lastLockWrapperBreakCyc;\n"
    "    private int _streamWorkGateHolds;",
    "    private int _lockWrapperBreaks;\n"
    "    private ulong _lastLockWrapperBreakCyc;\n"
    "    private int _lockWrapperVisits;\n"
    "    private ulong _lastLockWrapperVisitCyc;\n"
    "    private int _streamWorkGateHolds;",
    "lock-fields",
)

marker = (
    "    /// <summary>\n"
    "    /// Re-arm frame callback <c>*0x75BDD8</c> after ADX init zeros it."
)
if "MaybeInitStreamCookie(Ps2System" not in t:
    cookie = """    private int _streamCookieInits;
    private ulong _lastStreamCookieInitCyc;

    /// <summary>
    /// Wave-8: minimal init of stream cookie <c>0x5BB860</c> (FUN_0043ccf8 arg). Word0=1.
    /// </summary>
    private void MaybeInitStreamCookie(Ps2System sys)
    {
        if (_streamCookieInits >= 8) return;
        if (sys.MasterCycles - _lastStreamCookieInitCyc < 2_000_000) return;
        bool multiLive = sys.Memory.Read32(0x0075E950) == 0x0043F920u;
        bool frameCbLive = sys.Memory.Read32(0x0075BDD8) == 0x0043F920u;
        if (!multiLive && !frameCbLive) return;
        const uint Cookie = 0x005BB860;
        const uint CookieG2 = 0x005BB830;
        if (sys.Memory.Read32(Cookie) != 0 || sys.Memory.Read32(Cookie + 4) != 0)
        {
            _streamCookieInits = Math.Max(_streamCookieInits, 1);
            return;
        }
        if (sys.Memory.Read32(0x0055E1EC) == 0) sys.Memory.Write32(0x0055E1EC, 1);
        sys.Memory.Write32(Cookie, 1);
        if (sys.Memory.Read32(CookieG2) == 0) sys.Memory.Write32(CookieG2, 1);
        _streamCookieInits++;
        _lastStreamCookieInitCyc = sys.MasterCycles;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _streamCookieInits <= 4)
            Console.Error.WriteLine(
                $"[BIOS] init stream cookie *0x5BB860=1 (was zero) n={_streamCookieInits} " +
                $"multi={(multiLive ? 1 : 0)} fcb={(frameCbLive ? 1 : 0)} " +
                $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

"""
    if marker not in t:
        raise SystemExit("FAIL cookie-marker")
    t = t.replace(marker, cookie + marker, 1)

m = re.search(
    r"    private void MaybeBreakLockWrapperThrash\(Ps2System sys\)\n    \{.*?\n    \}",
    t,
    re.S,
)
if not m:
    raise SystemExit("FAIL lock-body")
lock = """    private void MaybeBreakLockWrapperThrash(Ps2System sys)
    {
        if (_lockWrapperBreaks >= 96) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        bool inWrap = pc is (>= 0x00426EE0 and <= 0x00426F90)
            or (>= 0x00426DF0 and <= 0x00426ED8);
        if (!inWrap) return;

        if (sys.MasterCycles - _lastLockWrapperVisitCyc < 250_000)
            _lockWrapperVisits++;
        else
            _lockWrapperVisits = 1;
        _lastLockWrapperVisitCyc = sys.MasterCycles;
        if (sys.MasterCycles - _lastLockWrapperBreakCyc < 50_000) return;

        uint refc = sys.Memory.Read32(0x0054E5E0);
        bool stickyRef = refc > 8 || refc == 0xFFFFFFFFu;
        bool onHotInsn = pc is (>= 0x00426F00 and <= 0x00426F10)
            or (>= 0x00426EBC and <= 0x00426EC8);
        bool stickyBand = _lockWrapperVisits >= 4;
        if (!stickyRef && !onHotInsn && !stickyBand) return;

        if (stickyRef || stickyBand)
            sys.Memory.Write32(0x0054E5E0, 0);
        sys.Memory.Write32(0x0054E5E4, 0);

        uint resume = 0x00426ED4;
        if (stickyBand)
        {
            if (sys.Memory.IsLikelyEeCode(0x00427518UL)) resume = 0x00427518;
            else if (sys.Memory.IsLikelyEeCode(0x004147F8UL)) resume = 0x004147F8;
            try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); } catch { /* ignore */ }
        }

        sys.EE.PC = resume;
        sys.LastGoodEePc = resume;
        _lastLockWrapperBreakCyc = sys.MasterCycles;
        _lockWrapperBreaks++;
        _lockWrapperVisits = 0;
        Assists++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_lockWrapperBreaks <= 12 || _lockWrapperBreaks % 8 == 0))
            Console.Error.WriteLine(
                $"[BIOS] break lock-wrapper thrash pc=0x{pc:X8} refc={refc} -> 0x{resume:X8} " +
                $"(hot={onHotInsn} stickyRef={stickyRef} stickyBand={stickyBand}) " +
                $"n={_lockWrapperBreaks} cyc={sys.MasterCycles}");
    }"""
t = t[: m.start()] + lock + t[m.end() :]

if t == orig:
    raise SystemExit("NO CHANGE")

out = t if nl == b"\n" else t.replace("\n", "\r\n")
p.write_bytes(out.encode("utf-8"))
print("OK delta", len(t) - len(orig), "nl", nl)
print("MaybeInitStreamCookie", t.count("MaybeInitStreamCookie"))
print("stickyBand", t.count("stickyBand"))
print("pastEpilogue", t.count("pastEpilogue"))
print("Path3Transfers >= 11)", t.count("Path3Transfers >= 11)"))
