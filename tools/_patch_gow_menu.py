#!/usr/bin/env python3
"""Apply GoW first-gs-interactive progress fixes (agent/menu-gow).

Does NOT set early GetVersion="3000" (regressed gifPath3 1->0).
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
    # --- Ps2System gowHot expansions ---
    must_replace(
        ROOT / "src/DetPS2.Core/Ps2System.cs",
        """                bool gowHot = ActiveQuirk is GodOfWarAssist && pcPhys is
                    (>= 0x0015F2C0UL and <= 0x0015FA80UL)
                    or (>= 0x001312C0UL and <= 0x001312F0UL)  // link-search thrash
                    or (>= 0x00293C00UL and <= 0x00293C80UL)  // WaitSema empty SIF poll
                    or (>= 0x00294800UL and <= 0x002948A0UL)  // SIF-cmd poll caller (loops WaitSema)
                    or (>= 0x00239300UL and <= 0x00239810UL)  // secondary freelist thrash
                    or (>= 0x0023A900UL and <= 0x0023AA30UL)  // null freelist thrash
                    or (>= 0x002C0000UL and <= 0x02000000UL)  // data/heap as PC
                    or (>= 0x00183880UL and <= 0x001838D0UL)
                    or (>= 0x0017A1D0UL and <= 0x0017A298UL)  // soft-tick wait leaf (*0x29C7D4)
                    or (>= 0x0017A320UL and <= 0x0017A37CUL)  // flag spin + jal tick-wait
                    or (>= 0x00233AD0UL and <= 0x00233B44UL)
                    or (>= 0x00284780UL and <= 0x002848B0UL)
                    or (>= 0x0021FF00UL and <= 0x00220600UL)
                    or (>= 0x0013DED0UL and <= 0x0013DEF8UL)
                    or (>= 0x0013E1C0UL and <= 0x0013E1F4UL)  // global free-search circular
                    or (>= 0x80000180UL and <= 0x80000200UL);""",
        """                bool gowHot = ActiveQuirk is GodOfWarAssist && pcPhys is
                    (>= 0x0015F2C0UL and <= 0x0015FA80UL)
                    or (>= 0x001312C0UL and <= 0x001312F0UL)  // link-search thrash
                    or (>= 0x00293C00UL and <= 0x00293C80UL)  // WaitSema empty SIF poll
                    or (>= 0x00294800UL and <= 0x002948A0UL)  // SIF-cmd poll caller (loops WaitSema)
                    or (>= 0x0027CC00UL and <= 0x0027CE90UL)  // worker entry/dispatch (WaitSema 0x20)
                    or (>= 0x00239300UL and <= 0x00239810UL)  // secondary freelist thrash
                    or (>= 0x0023A900UL and <= 0x0023AA30UL)  // null freelist thrash
                    or (>= 0x002C0000UL and <= 0x02000000UL)  // data/heap as PC
                    or (>= 0x00183880UL and <= 0x001838D0UL)
                    or (>= 0x0017A1D0UL and <= 0x0017A298UL)  // soft-tick wait leaf (*0x29C7D4)
                    or (>= 0x0017A320UL and <= 0x0017A37CUL)  // flag spin + jal tick-wait
                    or (>= 0x00233AD0UL and <= 0x00233B44UL)
                    or (>= 0x00284600UL and <= 0x00284B00UL)  // soft-float + wrappers (0x2849C4 heat)
                    or (>= 0x00155AB0UL and <= 0x00155B94UL)  // table-index zero-step
                    or (>= 0x001390F0UL and <= 0x00139114UL)  // huge byte-sum
                    or (>= 0x0023E7C0UL and <= 0x0023E7F0UL)  // align-zero poison a0
                    or (>= 0x0021FF00UL and <= 0x00220600UL)
                    or (>= 0x0013DED0UL and <= 0x0013DEF8UL)
                    or (>= 0x0013E1C0UL and <= 0x0013E1F4UL)  // global free-search circular
                    or (>= 0x80000180UL and <= 0x80020000UL);""",
        "Ps2System gowHot",
    )

    # --- RealSifRpc StartLoadedModule budget ---
    must_replace(
        ROOT / "src/DetPS2.Core/RealSifRpc.cs",
        """        const ulong maxInsn = 50_000;
        if (string.Equals(irx.Name, "INTRMANP", StringComparison.OrdinalIgnoreCase))
            _host.Memory.IopWrite32(0xBF801450, SystemMemory.IopIoIntrmanConfigDefault);
        else if (string.Equals(irx.Name, "SIFMAN", StringComparison.OrdinalIgnoreCase))
            _host.Memory.IopWrite32(0xBF801450, 0);
        var run = iopModules.StartLoadedModule(_host, mid, maxInsn);""",
        """        // 50k left MC2_D/DS2U_D/989NOMID mid-_start on GoW IOP_MOD list. 100k gives more
        // room without multi-150k host stalls. DETPS2_LOADFILE_START_INSNS overrides.
        ulong maxInsn = 100_000;
        string? maxEnv = Environment.GetEnvironmentVariable("DETPS2_LOADFILE_START_INSNS");
        if (!string.IsNullOrEmpty(maxEnv) && ulong.TryParse(maxEnv, out ulong envMax) && envMax > 0)
            maxInsn = envMax;
        if (string.Equals(irx.Name, "INTRMANP", StringComparison.OrdinalIgnoreCase))
            _host.Memory.IopWrite32(0xBF801450, SystemMemory.IopIoIntrmanConfigDefault);
        else if (string.Equals(irx.Name, "SIFMAN", StringComparison.OrdinalIgnoreCase))
            _host.Memory.IopWrite32(0xBF801450, 0);
        var run = iopModules.StartLoadedModule(_host, mid, maxInsn);""",
        "RealSifRpc StartLoadedModule budget",
    )

    gow = ROOT / "src/DetPS2.Core/GameQuirks/GodOfWarAssist.cs"
    t = gow.read_text(encoding="utf-8")

    # OnDiscMounted note
    old_mount = """        // EE RAM "3000" plant only at boot — do NOT SetIopRpVersionAscii early:
        // live claim with GetVersion="3000" from cyc0 regressed binds 16→10 / dmac 463→321
        // (FILEIO-2200 arming / LOADFILE path skew). Post-empty-reboot handoff below sets it.
        PlantIopRpVersion(sys);"""
    new_mount = """        // EE RAM "3000" plant only at boot — do NOT SetIopRpVersionAscii early:
        // GetVersion="3000" from cyc0/500k regressed gifPath3 1→0 (claim 100M, 2026-07-31)
        // and historically binds 16→10 / dmac 463→321. Post-empty-reboot handoff sets it.
        // PreferIopRp with empty version returns classic 0x00020000; freeze uses EE plant.
        PlantIopRpVersion(sys);"""
    if new_mount in t:
        print("OK already: OnDiscMounted note")
    elif old_mount in t:
        t = t.replace(old_mount, new_mount, 1)
        print("patched: OnDiscMounted note")
    else:
        print("WARN: OnDiscMounted note anchor missing (skip)")

    # Align-zero resume
    old_align = """        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        // Prefer live $ra when it is real .text. Never re-home to 0x26C0E0 (Exit risk),
        // exception vector, or the 0x13FExx nop/unknown band (live align leave → 0x13FEE0
        // UnknownOpcode storm, 4.8M telemetry hits @100M). Fallback: post-FreezeCache.
        static bool IsBadAlignResume(uint p) =>
            p is < 0x00100000 or >= 0x002C0000
            or (>= 0x0023E7C0 and <= 0x0023E7F0)
            or (>= 0x0026C0E0 and <= 0x0026C600)
            or (>= 0x0013FE00 and <= 0x00140000)
            or (>= 0x00185F90 and <= 0x00186120)
            or (>= 0x80000000 and <= 0x80020000)
            || p == 0;
        uint resume;
        if (sys.Memory.IsLikelyEeCode(ra) && !IsBadAlignResume(ra))
            resume = ra;
        else
            resume = 0x00185FAC;

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
    new_align = """        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        // Prefer live $ra when it is real .text. Never re-home to 0x26C0E0 (Exit risk),
        // exception vector, or the 0x13FExx nop/unknown band (live align leave → 0x13FEE0
        // UnknownOpcode storm, 4.8M telemetry hits @100M). Post-CDVD prefer worker dispatch
        // over FreezeCache continue (0x185FAC re-entry fed WaitSema fabricate thrash).
        static bool IsBadAlignResume(uint p) =>
            p is < 0x00100000 or >= 0x002C0000
            or (>= 0x0023E7C0 and <= 0x0023E7F0)
            or (>= 0x0026C0E0 and <= 0x0026C600)
            or (>= 0x0013FE00 and <= 0x00140000)
            or (>= 0x00185F90 and <= 0x00186120)
            or (>= 0x80000000 and <= 0x80020000)
            || p == 0;
        uint resume;
        if (sys.Memory.IsLikelyEeCode(ra) && !IsBadAlignResume(ra))
            resume = ra;
        else if (sys.Cdvd.SectorsRead > 0 && sys.Memory.IsLikelyEeCode(0x0027CC08UL))
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
    if "over FreezeCache continue (0x185FAC re-entry fed WaitSema fabricate thrash)" in t:
        print("OK already: align-zero resume")
    elif old_align in t:
        t = t.replace(old_align, new_align, 1)
        print("patched: align-zero resume")
    else:
        raise SystemExit("MISSING align-zero resume anchor")

    # empty-sifrpc thrash reduction
    old_sif = """        // After CDVD, sifrpc WaitSema trampoline thrash at 0x293Cxx (empty SIF-cmd poll +
        // worker 0x27CCxx). SHARED QueueMaySignalSema + CompleteRpcEnd own real BIND/CALL.
        // Wave-5: paint 989snd done-magic + residual SignalSema. When still stuck mid-leaf,
        // soft-return via live $ra (SIF poll caller is 0x294810 / worker 0x27CC08) — do NOT
        // snap to 0x26C0E0 mid-frame (live w5c data PC / UnknownSyscall 0x2A1364).
        // Live tip residual: PC=0x299328 with $ra=0 after align-zero leave — empty wake
        // alone cannot progress; force leave via stack $ra / post-FreezeCache.
        if (sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && _worldKickPulses >= 8 && (_worldKickPulses % 4) == 0
            && (pc is >= 0x00293C00 and <= 0x00293C80
                || pc is >= 0x00299300 and <= 0x00299480
                || pc is >= 0x00289A00 and <= 0x00289B00))
        {
            TryArmPendingStreamJob(sys, c);
            sys.Memory.Write32(0x0029C7D0, 0);
            const uint Done = 0xFFFFFFFFu;
            sys.Memory.Write32(0x00305600, Done);
            sys.Memory.Write32(0x00305604, 0);
            sys.Memory.Write32(0x00305608, Done);
            if (k != null)
            {
                foreach (var t in k.AllThreads)
                {
                    if (!t.Alive) continue;
                    // Live residual: WaitSemaId=0x20000000 / 0x200000 from poisoned a0 on the
                    // WaitSema trampoline (worker 0x27CC00 delay-slot lw a0,4(v0) with bad v0).
                    // Never SignalSema garbage ids — clear and wake instead.
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
                    // Residual empty poll only: SIF-cmd (3), worker (0x20), game-private (33..256).
                    if (t.WaitSemaId == 3 || t.WaitSemaId == 0x20 || t.WaitSemaId is >= 32 and <= 256)
                    {
                        try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                    else if (t.WaitSemaId == 0 && !t.WaitVblank)
                        k.WakeupThread(t.Id);
                }
            }
            // Soft-return from WaitSema leaf via $ra so poll body can take the empty-queue path.
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            bool left = false;
            if (sys.Memory.IsLikelyEeCode(ra) && ra is (>= 0x0027CC00 and <= 0x0027CD00)
                    or (>= 0x00294800 and <= 0x00294900)
                    or (>= 0x00297600 and <= 0x00297700)
                    or (>= 0x00297300 and <= 0x00297400)
                    or (>= 0x00100000 and < 0x00280000))
            {
                // WaitSema success convention: v0 = sema id (libcdvd / sifrpc check v0==id).
                // Only accept plausible THREADMAN ids — live a0=0x20000000 is poison.
                // Broader $ra accept (any .text) for 0x2993xx residual with null-ra recovery.
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
            // Null / poison $ra residual (live 0x299328): try stack slot then FreezeCache.
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
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] SHARED empty-sifrpc wake pc=0x{pc:X8} ra=0x{ra:X8} left={left} " +
                    $"arms={_streamArmPulses} n={_worldKickPulses} cyc={c}");
        }"""
    new_sif = """        // After CDVD, sifrpc WaitSema trampoline thrash at 0x293Cxx (empty SIF-cmd poll +
        // worker 0x27CCxx). SHARED QueueMaySignalSema + CompleteRpcEnd own real BIND/CALL.
        // Prefer soft-return via $ra over SignalSema fabricate storms (live claim: 1.1M
        // WaitSema+SignalSema after empty wake left=True, syscalls 2.3M @100M).
        // Live tip residual: PC=0x299328 with $ra=0 after align-zero leave — empty wake
        // alone cannot progress; force leave via stack $ra / worker dispatch.
        if (sys.Cdvd.SectorsRead > 0 && sys.Gs.PixelsWritten == 0
            && _worldKickPulses >= 8 && (_worldKickPulses % 4) == 0
            && (pc is >= 0x00293C00 and <= 0x00293C80
                || pc is >= 0x00299300 and <= 0x00299480
                || pc is >= 0x00289A00 and <= 0x00289B00
                || pc is >= 0x0027CC00 and <= 0x0027CE90
                || pc is >= 0x00294800 and <= 0x002948A0))
        {
            TryArmPendingStreamJob(sys, c);
            sys.Memory.Write32(0x0029C7D0, 0);
            const uint Done = 0xFFFFFFFFu;
            sys.Memory.Write32(0x00305600, Done);
            sys.Memory.Write32(0x00305604, 0);
            sys.Memory.Write32(0x00305608, Done);
            // Soft-return from WaitSema leaf via $ra so poll body can take the empty-queue path.
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
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_worldKickPulses % 16) == 0)
                Console.Error.WriteLine(
                    $"[GOW] SHARED empty-sifrpc wake pc=0x{pc:X8} ra=0x{ra:X8} left={left} " +
                    $"arms={_streamArmPulses} n={_worldKickPulses} cyc={c}");
        }"""
    if "Prefer soft-return via $ra over SignalSema fabricate storms" in t:
        print("OK already: empty-sifrpc thrash fix")
    elif old_sif in t:
        t = t.replace(old_sif, new_sif, 1)
        print("patched: empty-sifrpc thrash fix")
    else:
        raise SystemExit("MISSING empty-sifrpc anchor")

    gow.write_text(t, encoding="utf-8")
    print("wrote", gow)


if __name__ == "__main__":
    main()
