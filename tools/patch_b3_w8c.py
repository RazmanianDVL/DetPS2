# -*- coding: utf-8 -*-
from pathlib import Path
import re

p = Path("src/DetPS2.Core/GameQuirks/Burnout3Assist.cs")
t = p.read_text(encoding="utf-8").replace("\r\n", "\n")


def rep(a, b, lab):
    global t
    if a not in t:
        print("FAIL", lab)
        raise SystemExit(1)
    t = t.replace(a, b, 1)
    print("ok", lab)


# Fix vblank close left open by prior partial patch
old = "sys.EE.PC = 0x002371A0; // past beq delay \u2014 success body\n                if (Environment"
new = (
    "sys.EE.PC = 0x002371A0;\n"
    "                    if (irxOnly) _vblankExits++;\n"
    "                }\n"
    "                if (Environment"
)
if old in t:
    t = t.replace(old, new, 1)
    print("ok vblank-close")
elif "if (irxOnly) _vblankExits++" not in t:
    t2 = re.sub(
        r"sys\.EE\.PC = 0x002371A0; // past beq delay . success body\n                if \(Environment",
        "sys.EE.PC = 0x002371A0;\n                    if (irxOnly) _vblankExits++;\n                }\n                if (Environment",
        t,
        count=1,
    )
    if t2 == t:
        raise SystemExit("vblank close failed")
    t = t2
    print("ok vblank-close-re")
else:
    print("vblank already closed")

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

for needle, repls in [
    (
        "_lgDevFullyDone = true;\n            PlantLgDevEntryStub(sys);",
        "_lgDevFullyDone = true;\n            // delay entry stub (wave-8 residual)",
    ),
    (
        "_lgDevFullyDone = true;\n        PlantLgDevEntryStub(sys);",
        "_lgDevFullyDone = true;\n        // delay entry stub (wave-8 residual)",
    ),
]:
    c = 0
    while needle in t:
        t = t.replace(needle, repls, 1)
        c += 1
    print("strip", c)

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

start = t.find("// Live deliver: post full-TXD UnknownMmioRead")
end = t.find("// SIF DMA copy body")
if start < 0 or end < 0:
    raise SystemExit(f"mmio anchors {start} {end}")

new_mmio = """// Wave-8: GIF path-flush thrash (0x21A4F0 bulk lq/sq MMIO src / 0x1F308C).
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
        }

        """
t = t[:start] + new_mmio + t[end:]
print("ok mmio")

p.write_text(t.replace("\n", "\r\n"), encoding="utf-8")
print("assist", "irxOnly" in t, "GIF-flush" in t)

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
if "b3Hot" not in t2:
    ind = m.group(2)
    repl = (
        m.group(1)
        + f"\n{ind}// Burnout 3 post-TXD GIF flush thrash."
        + f"\n{ind}bool b3Hot = ActiveQuirk is Burnout3Assist && pcPhys is"
        + f"\n{ind}    (>= 0x0021A4F0UL and <= 0x0021A5E8UL)"
        + f"\n{ind}    or (>= 0x001F3080UL and <= 0x001F3500UL)"
        + f"\n{ind}    or (>= 0x00218700UL and <= 0x00218790UL);"
        + f"\n{ind}ulong slice = (criHot || gowHot || b3Hot) ? sliceCri : sliceDefault;"
    )
    t2 = t2[: m.start()] + repl + t2[m.end() :]
    p2.write_text(t2.replace("\n", "\r\n"), encoding="utf-8")
    print("Ps2System b3Hot added")
else:
    print("Ps2System already has b3Hot")
print("DONE")
