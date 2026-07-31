#!/usr/bin/env python3
"""WAVE-2 God of War (agent/menu-gow-w2) patches — apply on tip 3748553.

1) SHARED WaitSema: gate WHIP always-fabricate to WhiplashAssist; restore TryYield-first.
2) GodOfWarAssist: CRT0 re-entry rescue + null-ra empty-SIF prefer worker 0x27CC08.
3) Ps2System: gowHot includes CRT0 band.
No early GetVersion plants.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def must_replace(path: Path, old: str, new: str, label: str) -> None:
    t = path.read_text(encoding="utf-8")
    if new in t and old not in t:
        print(f"OK already: {label}")
        return
    if old not in t:
        raise SystemExit(f"MISSING anchor: {label} in {path}")
    path.write_text(t.replace(old, new, 1), encoding="utf-8")
    print(f"patched: {label}")


def main() -> None:
    # --- SonyKernelHle WaitSema ---
    must_replace(
        ROOT / "src/DetPS2.Core/SonyKernelHle.cs",
        """                        else
                        {
                            // WHIP_SEMA_FIX_V2: non-RPC soft-signal (Whiplash SN seq + SIF worker).
                            // No yield-without-wake; VBlank park only when ThreadCount < 2.
                            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                                Console.Error.WriteLine($"[RPC] WaitSema FABRICATING signal for sema=0x{a0:X} (WHIP_SEMA_FIX_V2)");
                            if (_kernel.ThreadCount < 2)
                                _kernel.WaitSemaVblank();
                            _kernel.SignalSema((int)a0);
                            _kernel.WakeupThread(_kernel.CurrentThreadId);
                        }""",
        """                        else if (_system.ActiveQuirk is WhiplashAssist)
                        {
                            // WHIP_SEMA_FIX_V2 (title-local): multi-thread soft-signal for SN seq
                            // + SIF worker. Global always-fabricate (wave-1 whip merge) starved
                            // GoW SIF-cmd WaitSema(3) — 0.5M fabricate thrash @20M with binds=0
                            // and blocked MOD_LOAD/cdvd (agent/menu-gow-w2, tip 3748553).
                            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                                Console.Error.WriteLine($"[RPC] WaitSema FABRICATING signal for sema=0x{a0:X} (WHIP_SEMA_FIX_V2)");
                            if (_kernel.ThreadCount < 2)
                                _kernel.WaitSemaVblank();
                            _kernel.SignalSema((int)a0);
                            _kernel.WakeupThread(_kernel.CurrentThreadId);
                        }
                        else if (!_kernel.TryYieldToOtherRunnable(ee))
                        {
                            // SHARED (pre-WHIP): yield to peer when possible; only if alone and
                            // no matching SIF RPC pending — park on VBlank then soft-signal.
                            // Restores GoW DualInfo/MOD_LOAD path past empty SIF poll thrash.
                            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
                                Console.Error.WriteLine($"[RPC] WaitSema FABRICATING signal for sema=0x{a0:X} (no matching RPC / no runnable thread)");
                            _kernel.WaitSemaVblank();
                            _kernel.SignalSema((int)a0);
                            _kernel.WakeupThread(_kernel.CurrentThreadId);
                        }""",
        "SonyKernelHle WaitSema WHIP gate",
    )

    # --- Ps2System gowHot CRT0 ---
    must_replace(
        ROOT / "src/DetPS2.Core/Ps2System.cs",
        """                    or (>= 0x0023A900UL and <= 0x0023AA30UL)  // null freelist thrash
                    or (>= 0x002C0000UL and <= 0x02000000UL)  // data/heap as PC
                    or (>= 0x00183880UL and <= 0x001838D0UL)""",
        """                    or (>= 0x0023A900UL and <= 0x0023AA30UL)  // null freelist thrash
                    or (>= 0x002C0000UL and <= 0x02000000UL)  // data/heap as PC
                    or (>= 0x00100000UL and <= 0x00100200UL)  // CRT0 re-entry after AdEL (wave-2)
                    or (>= 0x00183880UL and <= 0x001838D0UL)""",
        "Ps2System gowHot CRT0",
    )

    gow = ROOT / "src/DetPS2.Core/GameQuirks/GodOfWarAssist.cs"
    t = gow.read_text(encoding="utf-8")

    # CRT0 early data-PC rescue
    old1 = """        // Data/heap as PC (live 0x57xxxx after object-dispatch poison). Hard-cap resume
        // below 0x2C0000 for GoW .text. CRT0 BSS band is in IsDeathBand (never re-home TO
        // it) but is not force-escaped here — aggressive CRT0 leave regressed RPC (81 vs 153).
        uint pcPhysEarly = pc & 0x1FFFFFFFu;
        bool dataPc = pcPhysEarly >= 0x002C0000u
            || pc is >= 0x80000180 and <= 0x80000200
            || pcPhysEarly < 0x00100000;
        if (c >= 35_000_000 && sys.Gs.PixelsWritten == 0 && dataPc)
        {
            uint resume = PickSafeResume(sys, 0x0026C0EC);
            sys.Memory.Write32(0x002A1338, 0);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (c % 5_000_000) < 50_000)
                Console.Error.WriteLine(
                    $"[GOW] early data-PC rescue pc=0x{pc:X8} -> 0x{resume:X8} cyc={c}");
            pc = resume;
        }"""
    new1 = """        // Data/heap as PC (live 0x57xxxx after object-dispatch poison). Hard-cap resume
        // below 0x2C0000 for GoW .text. CRT0 BSS band is in IsDeathBand (never re-home TO
        // it). Wave-2: also force-leave CRT0/BSS re-entry after IRX progress — AdEL rescue
        // to 0x00100008 then spin at 0x00100140 froze claim metrics (gifPath3 path lost).
        // Gate on cdvd progress so early boot CRT0 is not skipped (RPC 81 vs 153).
        uint pcPhysEarly = pc & 0x1FFFFFFFu;
        bool crt0Reentry = pcPhysEarly is >= 0x00100000 and <= 0x00100200
            && (sys.Cdvd.SectorsRead > 0 || c >= 40_000_000);
        bool dataPc = pcPhysEarly >= 0x002C0000u
            || pc is >= 0x80000180 and <= 0x80000200
            || pcPhysEarly < 0x00100000
            || crt0Reentry;
        if (c >= 35_000_000 && sys.Gs.PixelsWritten == 0 && dataPc)
        {
            uint resume = PickSafeResume(sys,
                sys.Cdvd.SectorsRead > 0 ? 0x0027CC08u : 0x0026C0ECu);
            sys.Memory.Write32(0x002A1338, 0);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128
            {
                Lo = resume == 0x00185FAC ? 0x00330000UL : 1UL
            });
            sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = 0 });
            if (crt0Reentry)
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (c % 5_000_000) < 50_000)
                Console.Error.WriteLine(
                    $"[GOW] early data-PC rescue pc=0x{pc:X8} -> 0x{resume:X8} cyc={c}");
            pc = resume;
        }"""
    if "bool crt0Reentry = pcPhysEarly" in t:
        print("OK already: CRT0 early data-PC")
    elif old1 in t:
        t = t.replace(old1, new1, 1)
        print("patched: CRT0 early data-PC")
    else:
        raise SystemExit("MISSING: CRT0 early data-PC")

    old2 = """        // Rescue if world kick landed in unknown-opcode data (0x2A0xxx), mid-function
        // 0x229xxx, or CRT0 re-entry. Always leave for a safe epilogue — never re-CRT0.
        bool badBand = pc is (>= 0x002A0000 and <= 0x002B0000)
            or (>= 0x00229000 and <= 0x0022A000)
            || pc == 0x00100008u;
        if (badBand && sys.Gs.PixelsWritten == 0)
        {
            // Prefer stream-ready poll continue (0x26C0EC) or post-FreezeCache — not empty
            // tag epilogue which re-enters and $ra's into 0x2A0xxx again (live menu17).
            uint resume = 0x0026C0EC;
            if (!sys.Memory.IsLikelyEeCode(resume))
                resume = 0x00185FAC;
            uint lg = (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL);
            if (lg is >= 0x00100000 and < 0x00280000
                && lg != 0x00100008
                && lg is not (>= 0x002A0000 and <= 0x002B0000)
                && lg is not (>= 0x00229000 and <= 0x0022A000)
                && lg is not (>= 0x00170BB0 and <= 0x00170C20))
                resume = lg;
            sys.Memory.Write32(0x002A1338, 0); // stream ready
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] rescue bad band pc=0x{pc:X8} -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
        }"""
    new2 = """        // Rescue if world kick landed in unknown-opcode data (0x2A0xxx), mid-function
        // 0x229xxx, or CRT0 re-entry. Always leave for a safe epilogue — never re-CRT0.
        // Wave-2: AdEL-data rescue after empty-reboot soft-return often lands at 0x00100008
        // then runs to 0x00100140 (live claim100: metrics frozen binds=16 gifPath3=0). Prior
        // check only matched exact entry PC — broaden to full CRT0/BSS band after progress.
        bool crt0Band = pc is >= 0x00100000 and <= 0x00100200;
        bool badBand = pc is (>= 0x002A0000 and <= 0x002B0000)
            or (>= 0x00229000 and <= 0x0022A000)
            || crt0Band;
        if (badBand && sys.Gs.PixelsWritten == 0
            && (!crt0Band || sys.Cdvd.SectorsRead > 0 || c >= 40_000_000))
        {
            // Prefer worker dispatch (gifPath3 residual path @0x27CC) after IRX.
            uint resume = PickSafeResume(sys,
                sys.Cdvd.SectorsRead > 0 && sys.Memory.IsLikelyEeCode(0x0027CC08UL)
                    ? 0x0027CC08u
                    : 0x0026C0ECu);
            sys.Memory.Write32(0x002A1338, 0); // stream ready
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128
            {
                Lo = resume == 0x00185FAC ? 0x00330000UL : 1UL
            });
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 8) == 0)
                Console.Error.WriteLine(
                    $"[GOW] rescue bad band pc=0x{pc:X8} -> 0x{resume:X8} n={_worldKickPulses} cyc={c}");
        }"""
    if "bool crt0Band = pc is >= 0x00100000" in t:
        print("OK already: CRT0 badBand")
    elif old2 in t:
        t = t.replace(old2, new2, 1)
        print("patched: CRT0 badBand")
    else:
        raise SystemExit("MISSING: CRT0 badBand")

    old3 = """            // Null / poison $ra residual (live 0x299328): try stack slot then FreezeCache.
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
                    resume = 0x00185FAC;
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128
                {
                    Lo = resume == 0x00185FAC ? 0x00330000UL : 3UL
                });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                left = true;
            }"""
    new3 = """            // Null / poison $ra residual (live 0x299328): try stack slot then worker /
            // post-FreezeCache. Prefer 0x27CC08 over bare 0x185FAC after IRX — live wave-2
            // null-ra → 0x185FAC → AdEL 0x06207265 → CRT0 death (gifPath3 lost).
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
                        && stacked is not (>= 0x0026C0E0 and <= 0x0026C600)
                        && stacked is not (>= 0x00100000 and <= 0x00100200))
                        resume = stacked;
                }
                if (resume == 0)
                    resume = PickSafeResume(sys, 0x0027CC08);
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128
                {
                    Lo = resume == 0x00185FAC ? 0x00330000UL : 3UL
                });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                left = true;
            }"""
    if "null-ra → 0x185FAC → AdEL" in t or "null-ra -> 0x185FAC -> AdEL" in t:
        print("OK already: null-ra empty-sif")
    elif old3 in t:
        t = t.replace(old3, new3, 1)
        print("patched: null-ra empty-sif")
    else:
        # try with special unicode dash variants
        raise SystemExit("MISSING: null-ra empty-sif")

    gow.write_text(t, encoding="utf-8")
    print("done")


if __name__ == "__main__":
    main()
