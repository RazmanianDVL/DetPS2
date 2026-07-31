#!/usr/bin/env python3
"""Follow-up: reject thrash-band soft-return; restore align-zero 0x185FAC."""
from pathlib import Path

p = Path(__file__).resolve().parents[1] / "src/DetPS2.Core/GameQuirks/GodOfWarAssist.cs"
t = p.read_text(encoding="utf-8")

old_align = """        else if (sys.Cdvd.SectorsRead > 0 && sys.Memory.IsLikelyEeCode(0x0027CC08UL))
            resume = 0x0027CC08;
        else
            resume = PickSafeResume(sys, 0x0027CC08);

        // Publish a harmless aligned arena pointer so any caller that re-uses a0 is not a0=2.
        uint block = AllocArenaBlock(sys, 0x40);
        sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = block }); // a0
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128
        {
            Lo = resume == 0x00185FAC ? 0x00330000UL : 0UL
        });
        sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = block + 8 }); // v1 as post-loop
        if (resume is 0x00185FAC or 0x0027CC08)
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
        sys.EE.PC = resume;"""

new_align = """        else
            resume = 0x00185FAC; // post-FreezeCache (gifPath3=1 residual path)

        // Publish a harmless aligned arena pointer so any caller that re-uses a0 is not a0=2.
        uint block = AllocArenaBlock(sys, 0x40);
        sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = block }); // a0
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128
        {
            Lo = resume == 0x00185FAC ? 0x00330000UL : 0UL
        });
        sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = block + 8 }); // v1 as post-loop
        if (resume == 0x00185FAC)
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
        sys.EE.PC = resume;"""

if "post-FreezeCache (gifPath3=1 residual path)" in t:
    print("OK already: align")
elif old_align in t:
    t = t.replace(old_align, new_align, 1)
    print("patched: align")
else:
    print("WARN: align anchor missing")

old_sif = """            // Soft-return from WaitSema leaf via $ra so poll body can take the empty-queue path.
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            bool left = false;
            if (sys.Memory.IsLikelyEeCode(ra) && ra is (>= 0x0027CC00 and <= 0x0027CF00)
                    or (>= 0x00294800 and <= 0x00294900)
                    or (>= 0x00297600 and <= 0x00297700)
                    or (>= 0x00297300 and <= 0x00297400)
                    or (>= 0x00100000 and < 0x00280000))
            {
                // WaitSema success convention: v0 = sema id (libcdvd / sifrpc check v0==id).
                // Only accept plausible THREADMAN ids — live a0=0x20000000 is poison.
                if (ra is not (>= 0x00293C00 and <= 0x00293C80)
                    && ra is not (>= 0x00299300 and <= 0x00299480)
                    && ra is not (>= 0x0026C0E0 and <= 0x0026C600))
                {
                    uint a0 = (uint)sys.EE.GetGpr(4).Lo;
                    uint sema = a0 is >= 1 and <= 256 ? a0 : 3u;
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = sema });
                    sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = sema }); // keep a0 coherent
                    sys.EE.PC = ra;
                    sys.EE.COP0_Status &= ~0x6u;
                    left = true;
                }
            }
            // Null / poison $ra residual (live 0x299328): try stack slot then worker.
            if (!left && (ra == 0 || !sys.Memory.IsLikelyEeCode(ra)
                          || ra is (>= 0x00299300 and <= 0x00299480)
                          || ra is (>= 0x00293C00 and <= 0x00293C80))
                && (_worldKickPulses % 8) == 0)
            {
                uint resume = 0;
                uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
                if (sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 16u)
                {
                    uint stacked = sys.Memory.Read32(sp) & 0x1FFFFFFFu;
                    if (sys.Memory.IsLikelyEeCode(stacked) && stacked is >= 0x00100000 and < 0x002C0000
                        && stacked is not (>= 0x00299300 and <= 0x00299480)
                        && stacked is not (>= 0x00293C00 and <= 0x00293C80)
                        && stacked is not (>= 0x0026C0E0 and <= 0x0026C600))
                        resume = stacked;
                }
                if (resume == 0)
                    resume = sys.Memory.IsLikelyEeCode(0x0027CC08UL) ? 0x0027CC08u : 0x00185FACu;
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128
                {
                    Lo = resume == 0x00185FAC ? 0x00330000UL : 3UL
                });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                left = true;
            }
            // Only pulse waiters when soft-return failed — SignalSema on empty SIF poll
            // re-enters WaitSema immediately (1M+ fabricate thrash @100M claim).
            if (!left && k != null && (_worldKickPulses % 16) == 0)
            {
                foreach (var t in k.AllThreads)
                {
                    if (!t.Alive) continue;
                    if (t.WaitSemaId is < 0 or > 256)
                    {
                        t.WaitSemaId = 0;
                        if (t.Sleeping && !t.WaitVblank)
                        {
                            try { k.WakeupThread(t.Id); } catch { /* ignore */ }
                        }
                        continue;
                    }
                    if (!t.Sleeping) continue;
                    if (t.WaitSemaId == 3 || t.WaitSemaId == 0x20 || t.WaitSemaId is >= 32 and <= 256)
                    {
                        try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                    else if (t.WaitSemaId == 0 && !t.WaitVblank)
                        k.WakeupThread(t.Id);
                }
            }"""

new_sif = """            // Soft-return from WaitSema leaf via $ra so poll body can take the empty-queue path.
            // Never soft-return into thrash bands (live: ra=0x27CC08 == entry → left=True spin,
            // gifPath3 1→0, dmac 121→3, WaitSema 1.9M @100M).
            static bool IsThrashResume(uint p) =>
                p is (>= 0x00293C00 and <= 0x00293C80)
                or (>= 0x00299300 and <= 0x00299480)
                or (>= 0x0027CC00 and <= 0x0027CF00)
                or (>= 0x0026C0E0 and <= 0x0026C600)
                or (>= 0x00294800 and <= 0x002948A0);
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            bool left = false;
            if (sys.Memory.IsLikelyEeCode(ra) && ra is >= 0x00100000 and < 0x00280000
                && !IsThrashResume(ra))
            {
                // WaitSema success convention: v0 = sema id (libcdvd / sifrpc check v0==id).
                uint a0 = (uint)sys.EE.GetGpr(4).Lo;
                uint sema = a0 is >= 1 and <= 256 ? a0 : 3u;
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = sema });
                sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = sema }); // keep a0 coherent
                sys.EE.PC = ra;
                sys.EE.COP0_Status &= ~0x6u;
                left = true;
            }
            // Null / poison / thrash $ra: stack then post-FreezeCache (gifPath3 path).
            if (!left && (_worldKickPulses % 8) == 0)
            {
                uint resume = 0;
                uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
                if (sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 16u)
                {
                    uint stacked = sys.Memory.Read32(sp) & 0x1FFFFFFFu;
                    if (sys.Memory.IsLikelyEeCode(stacked) && stacked is >= 0x00100000 and < 0x002C0000
                        && !IsThrashResume(stacked))
                        resume = stacked;
                }
                if (resume == 0)
                    resume = 0x00185FAC;
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128
                {
                    Lo = resume == 0x00185FAC ? 0x00330000UL : 3UL
                });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                left = true;
            }
            // Rate-limited waiter pulse so kernel fabricate is not sole progress.
            if (k != null && (_worldKickPulses % 8) == 0)
            {
                foreach (var t in k.AllThreads)
                {
                    if (!t.Alive) continue;
                    if (t.WaitSemaId is < 0 or > 256)
                    {
                        t.WaitSemaId = 0;
                        if (t.Sleeping && !t.WaitVblank)
                        {
                            try { k.WakeupThread(t.Id); } catch { /* ignore */ }
                        }
                        continue;
                    }
                    if (!t.Sleeping) continue;
                    if (t.WaitSemaId == 3 || t.WaitSemaId == 0x20 || t.WaitSemaId is >= 32 and <= 256)
                    {
                        try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                    else if (t.WaitSemaId == 0 && !t.WaitVblank)
                        k.WakeupThread(t.Id);
                }
            }"""

if "IsThrashResume" in t:
    print("OK already: thrash")
elif old_sif in t:
    t = t.replace(old_sif, new_sif, 1)
    print("patched: thrash")
else:
    raise SystemExit("MISSING thrash soft-return anchor")

p.write_text(t, encoding="utf-8")
print("wrote", p)
