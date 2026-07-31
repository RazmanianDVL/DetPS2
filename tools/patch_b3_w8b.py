# -*- coding: utf-8 -*-
from pathlib import Path
import re

p = Path("src/DetPS2.Core/GameQuirks/Burnout3Assist.cs")
t = p.read_text(encoding="utf-8").replace("\r\n", "\n")


def rep(a: str, b: str, lab: str) -> None:
    global t
    if a not in t:
        key = a.split("\n")[0][:70]
        i = t.find(key)
        print("FAIL", lab, "key@", i)
        if i >= 0:
            print(repr(t[i : i + 160]))
        raise SystemExit(1)
    t = t.replace(a, b, 1)
    print("ok", lab)


rep(
    "_postTxdEscapes = 0;\n        _lastPostTxdEscapeCyc = 0;\n    }\n\n    public void OnDiscMounted",
    "_postTxdEscapes = 0;\n        _lastPostTxdEscapeCyc = 0;\n        _frontendKickPulses = 0;\n        _lastFrontendKickCyc = 0;\n    }\n\n    public void OnDiscMounted",
    "reset",
)

rep(
    "bool heavy = _sleepWakeups >= 8 || _menuKickPulses >= 16 || _vblankExits >= 4\n"
    "                    || pc is >= 0x00237120 and <= 0x00237170;\n"
    "                if (heavy)\n"
    "                {\n"
    "                    // Clamp s1 into 0..3 so success path writes a valid slot, then epilogue.\n"
    "                    uint s1 = (uint)(sys.EE.GetGpr(17).Lo & 0xFFUL);\n"
    "                    if (s1 > 3)\n"
    "                        sys.EE.SetGpr(17, new EmotionEngine.Gpr128 { Lo = 0 });\n"
    "                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0\n"
    "                    sys.EE.PC = 0x002371E0; // ld ra / restore / jr ra\n"
    "                    _vblankExits++;\n"
    "                }\n"
    "                else\n"
    "                    sys.EE.PC = 0x002371A0; // past beq delay",
    "bool irxOnly = sys.Cdvd.SectorsRead < 600;\n"
    "                bool heavy = !irxOnly && (_sleepWakeups >= 8 || _menuKickPulses >= 16\n"
    "                    || _vblankExits >= 4 || pc is >= 0x00237120 and <= 0x00237170);\n"
    "                if (heavy)\n"
    "                {\n"
    "                    uint s1 = (uint)(sys.EE.GetGpr(17).Lo & 0xFFUL);\n"
    "                    if (s1 > 3)\n"
    "                        sys.EE.SetGpr(17, new EmotionEngine.Gpr128 { Lo = 0 });\n"
    "                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });\n"
    "                    sys.EE.PC = 0x002371E0;\n"
    "                    _vblankExits++;\n"
    "                }\n"
    "                else\n"
    "                {\n"
    "                    sys.EE.PC = 0x002371A0;",
    "vblank-part1",
)

# close the else block - the original had a comment on the same line after 0x2371A0
# after partial replace we may have " // past beq delay — success body" still
if "sys.EE.PC = 0x002371A0; // past beq delay" in t:
    t = t.replace(
        "sys.EE.PC = 0x002371A0; // past beq delay — success body",
        "sys.EE.PC = 0x002371A0;\n                    if (irxOnly) _vblankExits++;\n                }",
        1,
    )
    print("ok vblank-close-emdash")
elif "sys.EE.PC = 0x002371A0; // past beq delay" in t:
    # emdash may be different
    i = t.find("sys.EE.PC = 0x002371A0; // past beq delay")
    j = t.find("\n", i)
    t = t[:i] + "sys.EE.PC = 0x002371A0;\n                    if (irxOnly) _vblankExits++;\n                }" + t[j:]
    print("ok vblank-close-generic")
else:
    # already closed by part1 if comment was truncated
    if "if (irxOnly) _vblankExits++" not in t:
        raise SystemExit("vblank close failed")

rep(
    "bool forceNow = (pristine && sys.MasterCycles >= 18_000_000)\n"
    "                            || sys.MasterCycles >= 22_500_000;",
    "bool forceNow = (pristine && sys.MasterCycles >= 22_000_000)\n"
    "                            || sys.MasterCycles >= 23_500_000;",
    "forceNow",
)

rep(
    "PlantLgDevEntryStub(sys);\n"
    "            PlantLgDevCallRpcLeafStub(sys);\n"
    "            sys.Memory.Write32(LgDevPostFlag, 0);",
    "if (_lgDevEscapes >= 24) { PlantLgDevEntryStub(sys); PlantLgDevCallRpcLeafStub(sys); }\n"
    "            sys.Memory.Write32(LgDevPostFlag, 0);",
    "fullydone",
)

count = 0
while True:
    i = t.find("_lgDevFullyDone = true;\n            PlantLgDevEntryStub(sys);")
    if i < 0:
        break
    t = (
        t[:i]
        + "_lgDevFullyDone = true;\n            // delay entry stub (wave-8 residual)"
        + t[i + len("_lgDevFullyDone = true;\n            PlantLgDevEntryStub(sys);") :]
    )
    count += 1
print("removed indented force plants", count)

count = 0
while True:
    i = t.find("_lgDevFullyDone = true;\n        PlantLgDevEntryStub(sys);")
    if i < 0:
        break
    t = (
        t[:i]
        + "_lgDevFullyDone = true;\n        // delay entry stub (wave-8 residual)"
        + t[i + len("_lgDevFullyDone = true;\n        PlantLgDevEntryStub(sys);") :]
    )
    count += 1
print("removed ForceLgDevSuccess plants", count)

rep(
    "if (_lgDevFullyDone)\n            PlantLgDevEntryStub(sys);",
    "if (_lgDevFullyDone && _lgDevEscapes >= 24)\n            PlantLgDevEntryStub(sys);",
    "menukick",
)

rep(
    "if (_postTxdEscapes >= 256) return;\n        if (sys.MasterCycles - _lastPostTxdEscapeCyc < 40_000) return;",
    "if (_postTxdEscapes >= 1024) return;\n        if (sys.MasterCycles - _lastPostTxdEscapeCyc < 4_000) return;",
    "throttle",
)

old_mmio = """        // Live deliver: post full-TXD UnknownMmioRead flood @ 0x21A5xx / park 0x1F308C (px=0).
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

new_mmio = """        // Wave-8: GIF path-flush thrash (0x21A4F0 bulk lq/sq MMIO src / 0x1F308C).
        bool inGifFlush = pc is >= 0x0021A4F0 and <= 0x0021A5E4;
        bool inGifSubmit = pc is >= 0x001F3080 and <= 0x001F3500;
        bool inFlushCaller = pc is >= 0x00218700 and <= 0x00218790;
        bool mmioProbe = (inGifFlush || inGifSubmit || inFlushCaller) && sys.Cdvd.SectorsRead >= 2000;
        if (mmioProbe)
        {
            uint gp = (uint)(sys.EE.GetGpr(28).Lo & 0x1FFFFFFFUL);
            if (gp is < 0x00400000 or >= 0x01000000) gp = 0x004E8670;
            uint startCell = gp - 27936u, endCell = gp - 23960u, dstCell = gp - 24240u;
            uint startPhys = sys.Memory.Read32(startCell) & 0x1FFFFFFFu;
            uint endPhys = sys.Memory.Read32(endCell) & 0x1FFFFFFFu;
            bool absurd = startPhys >= 0x10000000u || endPhys >= 0x10000000u
                || endPhys < startPhys || (endPhys > startPhys && endPhys - startPhys > 0x80000u)
                || startPhys < 0x00100000u || startPhys >= (uint)SystemMemory.RDRAM_SIZE;
            if (!absurd && inGifFlush)
            {
                uint t7 = (uint)(sys.EE.GetGpr(15).Lo & 0xFFFFFFFFUL);
                if ((t7 & 0x1FFFFFFFu) >= 0x10000000u) absurd = true;
            }
            if (!absurd && inGifSubmit)
            {
                uint a1 = (uint)(sys.EE.GetGpr(5).Lo & 0xFFFFFFFFUL);
                if (a1 > 0x4000) absurd = true;
            }
            if (!absurd) return;
            _lastPostTxdEscapeCyc = sys.MasterCycles;
            _postTxdEscapes++;
            uint safe = startPhys is >= 0x00100000 and < 0x01E00000u
                ? sys.Memory.Read32(startCell) : 0x00700000u;
            sys.Memory.Write32(startCell, safe);
            sys.Memory.Write32(endCell, safe);
            sys.Memory.Write32(dstCell, safe);
            uint resume = inGifFlush ? 0x0021A5D8u : 0x00218774u;
            if (ra is >= 0x00100000 and < 0x00400000 && sys.Memory.IsLikelyEeCode(ra)
                && ra is not (>= 0x0021A4F0 and <= 0x0021A5E8)
                && ra is not (>= 0x001F3080 and <= 0x001F3500)
                && ra is not (>= 0x001F24E0 and <= 0x001F2520))
                resume = ra;
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 });
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
                    $"[B3] post-TXD GIF-flush leave pc=0x{pc:X8} ra=0x{ra:X8} -> 0x{resume:X8} " +
                    $"n={_postTxdEscapes} cdvd={sys.Cdvd.SectorsRead} gifP3={sys.Gif.Path3Transfers} " +
                    $"px={sys.Gs.PixelsWritten} cyc={sys.MasterCycles}");
            return;
        }"""

rep(old_mmio, new_mmio, "mmio")

# declare frontend fields near postTxdEscapes
if "private int _frontendKickPulses" not in t:
    t = t.replace(
        "private int _postTxdEscapes;\n    private ulong _lastPostTxdEscapeCyc;",
        "private int _postTxdEscapes;\n    private ulong _lastPostTxdEscapeCyc;\n"
        "    private int _frontendKickPulses;\n    private ulong _lastFrontendKickCyc;",
        1,
    )
    print("ok field decls")

p.write_text(t.replace("\n", "\r\n"), encoding="utf-8")
print("assist OK", "irxOnly" in t, "GIF-flush" in t)

p2 = Path("src/DetPS2.Core/Ps2System.cs")
t2 = p2.read_text(encoding="utf-8").replace("\r\n", "\n")
m = re.search(
    r"(or \(>= 0x80000180UL and <= 0x80000200UL\);)\n(\s*)ulong slice = \(criHot \|\| gowHot\) \? sliceCri : sliceDefault;",
    t2,
)
if not m:
    i = t2.find("0x80000180")
    print(repr(t2[i : i + 220]))
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
p2.write_text(t2.replace("\n", "\r\n"), encoding="utf-8")
print("Ps2System OK")
