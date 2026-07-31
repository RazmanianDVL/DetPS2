#!/usr/bin/env python3
"""Wave-2 B3 residual-STG + Soft-GS patches (ASCII anchors)."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main() -> None:
    branch_note = ROOT / "src/DetPS2.Core/GameQuirks/Burnout3Assist.cs"
    t = branch_note.read_text(encoding="utf-8")

    # 1) thrash leave call after menu kick
    needle = "            MaybeKickPostGtfsMenu(sys);\n"
    insert = (
        "            MaybeKickPostGtfsMenu(sys);\n"
        "\n"
        "        // Wave-2: after force FullyDone, tip residual often parks in WaitSema/SIF poll\n"
        "        // bands (0x293xxx / 0x123Exx) with IRX-only cdvd - never reaches STG bind.\n"
        "        if (_lgDevFullyDone && sys.MasterCycles >= 20_000_000\n"
        "            && sys.Cdvd.SectorsRead is >= 400 and < 2000)\n"
        "            MaybeLeaveResidualBootThrash(sys);\n"
    )
    if "MaybeLeaveResidualBootThrash(sys)" not in t:
        if needle not in t:
            raise SystemExit("kick needle missing")
        t = t.replace(needle, insert, 1)
        print("injected thrash call")
    else:
        print("thrash call already")

    # 2) VBlank heavy gate
    oldv = (
        "bool heavy = _sleepWakeups >= 8 || _menuKickPulses >= 16 || _vblankExits >= 4\n"
        "                    || pc is >= 0x00237120 and <= 0x00237170;"
    )
    newv = (
        "bool allowHeavy = sys.Cdvd.SectorsRead >= 600 || _menuKickPulses >= 48;\n"
        "                bool heavy = allowHeavy && (_sleepWakeups >= 8 || _menuKickPulses >= 16\n"
        "                    || _vblankExits >= 4 || pc is >= 0x00237120 and <= 0x00237170);"
    )
    if oldv not in t:
        raise SystemExit("vblank heavy missing")
    t = t.replace(oldv, newv, 1)
    print("vblank ok")

    # 3) residual complete after FullyDone -> parent post-jal
    fd = t.find("if (_lgDevFullyDone)")
    if fd < 0:
        raise SystemExit("FullyDone block missing")
    start = t.find("if (IsLgDevCallRpcThrash(sys, pc, ra) && _lgDevEscapes < 256)", fd)
    if start < 0:
        raise SystemExit("residual thrash if missing")
    end = t.find("// Deep LGDEV body after bad residual return", start)
    if end < 0:
        raise SystemExit("deep LGDEV marker missing")
    new_block = """if (IsLgDevCallRpcThrash(sys, pc, ra) && _lgDevEscapes < 256)
            {
                // After STG/game FILEIO, stop faking LGDEV residual CallRpc.
                if (sys.Cdvd.SectorsRead >= 600)
                    return;
                uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
                if (sp is >= 0x01FFF000 and < 0x02000000)
                {
                    // Wave-2: residual n=2-3 must not return into LGDEV leaf (0x443D94).
                    // Complete to parent post-jal so STG can bind (was plant-only cdvd=609).
                    sys.Memory.Write32(sp + 176, 0x004427FCu);
                    sys.Memory.Write32(sp + 180, 0);
                    uint leafSp = sp + 192;
                    if (leafSp is >= 0x01FFF000 and < 0x02000000)
                    {
                        sys.Memory.Write32(leafSp + 40, 0x004427FCu);
                        sys.Memory.Write32(leafSp + 44, 0);
                    }
                    PlantLgDevEntryStub(sys);
                    PlantLgDevCallRpcLeafStub(sys);
                    sys.Memory.Write32(0x01ECDF00, 0);
                    sys.Memory.Write32(LgDevPostFlag, 0);
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = 0x004427FCu });
                    sys.EE.PC = 0x004427FCu;
                    sys.EE.COP0_Status &= ~(1u << 1);
                    _lgDevEscapes++;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                        && (_lgDevEscapes <= 8 || _lgDevEscapes % 16 == 0))
                        Console.Error.WriteLine(
                            $"[B3] residual LGDEV->parent post-jal pc=0x{pc:X8} sp=0x{sp:X8} " +
                            $"-> 0x4427FC n={_lgDevEscapes} cyc={sys.MasterCycles}");
                }
            }
            """
    t = t[:start] + new_block + t[end:]
    print("residual replaced")

    # 4) reset fields
    oldr = (
        "        _frontendPlanted = false;\n"
        "        _frontendEeAddr = 0;\n"
        "        _frontendSize = 0;\n"
        "    }"
    )
    newr = (
        "        _frontendPlanted = false;\n"
        "        _frontendEeAddr = 0;\n"
        "        _frontendSize = 0;\n"
        "        _residualBootLeaves = 0;\n"
        "        _lastResidualBootLeaveCyc = 0;\n"
        "    }"
    )
    if oldr not in t:
        raise SystemExit("reset block missing")
    t = t.replace(oldr, newr, 1)
    print("reset ok")

    # 5) method
    marker = (
        "    /// <summary>\n"
        "    /// Boot wait chain after LGDEV (disasm 0x2B34C0..0x2B35C0):"
    )
    method = r'''
    private int _residualBootLeaves;
    private ulong _lastResidualBootLeaveCyc;

    /// <summary>
    /// Wave-2 residual-STG: after LGDEV force, tip parks in SIF WaitSema (0x293Axx) /
    /// stream poll (0x123Exx). Leave toward post-LGDEV success so STG can bind.
    /// </summary>
    private void MaybeLeaveResidualBootThrash(Ps2System sys)
    {
        if (_residualBootLeaves >= 128) return;
        if (sys.MasterCycles - _lastResidualBootLeaveCyc < 40_000) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        bool sifWaitBand = pc is >= 0x00293A00 and <= 0x00294200
            || ra is >= 0x00293A00 and <= 0x00294200;
        bool streamPoll = pc is >= 0x00123E00 and <= 0x00124000;
        bool postLgDev = pc is >= 0x002AF800 and <= 0x002AF994
            || ra is >= 0x002AF800 and <= 0x002AF994;
        bool bootWait = pc is >= 0x002B34C0 and <= 0x002B35D0;
        bool waitSemaBoot = pc is >= 0x0010BE60 and <= 0x0010BE70
            && (postLgDev || sifWaitBand || ra is >= 0x002B34C0 and <= 0x002B35D0
                || ra is >= 0x00123E00 and <= 0x00124000);
        bool badPc = pc is >= 0x004E0000 and < 0x02000000
            || pc is >= 0x80000180 and <= 0x80000200;

        if (!sifWaitBand && !streamPoll && !waitSemaBoot && !postLgDev && !bootWait && !badPc)
            return;

        _lastResidualBootLeaveCyc = sys.MasterCycles;
        _residualBootLeaves++;

        uint gp = (uint)(sys.EE.GetGpr(28).Lo & 0x1FFFFFFFUL);
        if (gp is < 0x00400000 or >= 0x01000000) gp = 0x004E8670;
        uint f23104 = unchecked((uint)((int)gp - 23104));
        uint f23028 = unchecked((uint)((int)gp + BootWaitFlagGpOff));
        if (f23104 is >= 0x00400000 and < 0x01000000)
            sys.Memory.Write32(f23104, 1);
        if (f23028 is >= 0x00400000 and < 0x01000000)
            sys.Memory.Write32(f23028, 1);
        sys.Memory.Write32(BootWaitFlagDefault, 1);
        uint f27128 = unchecked((uint)((int)gp - 27128));
        if (f27128 is >= 0x00400000 and < 0x01000000
            && sys.Memory.Read32(f27128) == 0xFFFFFFFFu)
            sys.Memory.Write32(f27128, 1);

        const uint postLgDevSuccess = 0x002AF914u;
        const uint bootWaitContinue = 0x002B34E8u;
        uint resume = sys.Cdvd.SectorsRead < 600 ? postLgDevSuccess : bootWaitContinue;
        if (bootWait) resume = bootWaitContinue;
        if (postLgDev) resume = postLgDevSuccess;
        if (badPc) resume = bootWaitContinue;

        uint s0w = (uint)(sys.EE.GetGpr(16).Lo & 0xFFFFFFFFUL);
        if (s0w >= 600 || s0w == 0 || (s0w & 3) != 0
            || s0w is >= 0x01000000 or < 0x00400000)
            sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 1 });

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~(1u << 1);

        var k = sys.Hle?.Kernel;
        if (k != null)
        {
            foreach (var th in k.AllThreads)
            {
                if (!th.Alive || !th.Sleeping) continue;
                if (th.WaitSemaId >= 32)
                {
                    try { k.SignalSema(th.WaitSemaId); } catch { /* ignore */ }
                }
                if (th.Id == 1 && sys.Cdvd.SectorsRead < 600)
                {
                    th.SavedPc = postLgDevSuccess;
                    if (th.HasFullSave && th.SavedGprFull != null && th.SavedGprFull.Length > 2)
                    {
                        th.SavedGprFull[2] = 1;
                        if (th.SavedGprFull.Length > 16) th.SavedGprFull[16] = 1;
                    }
                    th.WaitSemaId = 0;
                    th.Sleeping = false;
                    th.WaitVblank = false;
                }
                else if (th.WaitSemaId == 0 && !th.WaitVblank)
                    k.WakeupThread(th.Id);
            }
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_residualBootLeaves <= 16 || _residualBootLeaves % 16 == 0))
            Console.Error.WriteLine(
                $"[B3] residual boot thrash leave pc=0x{pc:X8} ra=0x{ra:X8} -> 0x{resume:X8} " +
                $"n={_residualBootLeaves} cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
    }

'''
    if "void MaybeLeaveResidualBootThrash" not in t:
        if marker not in t:
            raise SystemExit("boot wait marker missing")
        t = t.replace(marker, method + marker, 1)
        print("method inserted")
    else:
        print("method already")

    branch_note.write_text(t, encoding="utf-8")
    print("Burnout3Assist OK")

    # Ps2System
    p2 = ROOT / "src/DetPS2.Core/Ps2System.cs"
    t2 = p2.read_text(encoding="utf-8")
    oldh = (
        "                // Burnout 3 post-TXD GIF flush thrash.\n"
        "                bool b3Hot = ActiveQuirk is Burnout3Assist && pcPhys is\n"
        "                    (>= 0x0021A4F0UL and <= 0x0021A5E8UL)\n"
        "                    or (>= 0x001F3080UL and <= 0x001F3500UL)\n"
        "                    or (>= 0x00218700UL and <= 0x00218790UL);"
    )
    newh = (
        "                // Burnout 3: post-TXD GIF flush thrash + residual-STG WaitSema/SIF bands.\n"
        "                bool b3Hot = ActiveQuirk is Burnout3Assist && pcPhys is\n"
        "                    (>= 0x0021A4F0UL and <= 0x0021A5E8UL)\n"
        "                    or (>= 0x001F3080UL and <= 0x001F3500UL)\n"
        "                    or (>= 0x00218700UL and <= 0x00218790UL)\n"
        "                    or (>= 0x00293A00UL and <= 0x00294200UL)\n"
        "                    or (>= 0x00123E00UL and <= 0x00124080UL)\n"
        "                    or (>= 0x002AF800UL and <= 0x002AF994UL)\n"
        "                    or (>= 0x002B34C0UL and <= 0x002B35D0UL)\n"
        "                    or (>= 0x0010BE60UL and <= 0x0010BE70UL);"
    )
    if oldh not in t2:
        raise SystemExit("b3Hot missing")
    p2.write_text(t2.replace(oldh, newh, 1), encoding="utf-8")
    print("Ps2System OK")

    # Gs
    p3 = ROOT / "src/DetPS2.Core/Gs.cs"
    t3 = p3.read_text(encoding="utf-8")
    oldx = (
        "        if (ofx == 0 && ofy == 0)\n"
        "        {\n"
        "            if (xRaw >= 0x6000 || yRaw >= 0x6000)\n"
        "            {\n"
        "                x = (xRaw - 0x8000) >> 4;\n"
        "                y = (yRaw - 0x8000) >> 4;\n"
        "            }\n"
        "            else if (xRaw > FB_WIDTH * 16 || yRaw > FB_HEIGHT * 16)\n"
        "            {\n"
        "                x = (xRaw * FB_WIDTH) / 4096;\n"
        "                y = (yRaw * FB_HEIGHT) / 4096;\n"
        "            }\n"
        "        }"
    )
    newx = (
        "        if (ofx == 0 && ofy == 0)\n"
        "        {\n"
        "            int sxRaw = unchecked((short)(ushort)xRaw);\n"
        "            int syRaw = unchecked((short)(ushort)yRaw);\n"
        "            if (xRaw >= 0x6000 || yRaw >= 0x6000 || sxRaw < -16 || syRaw < -16)\n"
        "            {\n"
        "                if (xRaw >= 0x4000 || yRaw >= 0x4000)\n"
        "                {\n"
        "                    x = (xRaw - 0x8000) >> 4;\n"
        "                    y = (yRaw - 0x8000) >> 4;\n"
        "                }\n"
        "                else\n"
        "                {\n"
        "                    x = sxRaw >> 4;\n"
        "                    y = syRaw >> 4;\n"
        "                }\n"
        "            }\n"
        "            else if (xRaw > FB_WIDTH * 16 || yRaw > FB_HEIGHT * 16)\n"
        "            {\n"
        "                x = (xRaw * FB_WIDTH) / 4096;\n"
        "                y = (yRaw * FB_HEIGHT) / 4096;\n"
        "            }\n"
        "        }\n"
        "        else if ((ofx == 0 && ofy != 0) || (ofy == 0 && ofx != 0))\n"
        "        {\n"
        "            if (x < -64 || y < -64 || x >= FB_WIDTH + 64 || y >= FB_HEIGHT + 64)\n"
        "            {\n"
        "                if (xRaw >= 0x4000 || yRaw >= 0x4000)\n"
        "                {\n"
        "                    x = (xRaw - 0x8000) >> 4;\n"
        "                    y = (yRaw - 0x8000) >> 4;\n"
        "                }\n"
        "            }\n"
        "        }"
    )
    if oldx not in t3:
        raise SystemExit("xyz block missing")
    t3 = t3.replace(oldx, newx, 1)

    oldc = (
        "        if (fb == 0) return 0;\n"
        "\n"
        "        int fbp;\n"
        "        int fbw;\n"
        "        int psm;\n"
        "        int dbx = 0, dby = 0;\n"
        "        if (fromDispfb)\n"
        "        {\n"
        "            fbp = (int)(fb & 0x1FF);\n"
        "            fbw = (int)((fb >> 9) & 0x3F) * 64;\n"
        "            psm = (int)((fb >> 15) & 0x1F);\n"
        "            dbx = (int)((fb >> 32) & 0x7FF);\n"
        "            dby = (int)((fb >> 43) & 0x7FF);\n"
        "        }\n"
        "        else\n"
        "        {\n"
        "            fbp = (int)(fb & 0x1FF);\n"
        "            fbw = (int)((fb >> 16) & 0x3F) * 64;\n"
        "            psm = (int)((fb >> 24) & 0x3F);\n"
        "        }"
    )
    newc = (
        "        bool syntheticFb = false;\n"
        "        if (fb == 0)\n"
        "        {\n"
        "            if (ImageBytesWritten <= 0) return 0;\n"
        "            fromDispfb = false;\n"
        "            syntheticFb = true;\n"
        "        }\n"
        "\n"
        "        int fbp;\n"
        "        int fbw;\n"
        "        int psm;\n"
        "        int dbx = 0, dby = 0;\n"
        "        if (syntheticFb)\n"
        "        {\n"
        "            fbp = 0;\n"
        "            fbw = FB_WIDTH;\n"
        "            psm = 0x00;\n"
        "        }\n"
        "        else if (fromDispfb)\n"
        "        {\n"
        "            fbp = (int)(fb & 0x1FF);\n"
        "            fbw = (int)((fb >> 9) & 0x3F) * 64;\n"
        "            psm = (int)((fb >> 15) & 0x1F);\n"
        "            dbx = (int)((fb >> 32) & 0x7FF);\n"
        "            dby = (int)((fb >> 43) & 0x7FF);\n"
        "        }\n"
        "        else\n"
        "        {\n"
        "            fbp = (int)(fb & 0x1FF);\n"
        "            fbw = (int)((fb >> 16) & 0x3F) * 64;\n"
        "            psm = (int)((fb >> 24) & 0x3F);\n"
        "        }"
    )
    if oldc not in t3:
        raise SystemExit("composite block missing")
    t3 = t3.replace(oldc, newc, 1)
    p3.write_text(t3, encoding="utf-8")
    print("Gs OK")
    print("ALL OK")


if __name__ == "__main__":
    main()
