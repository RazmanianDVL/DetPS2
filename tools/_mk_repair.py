from pathlib import Path
p = Path("src/DetPS2.Core/GameQuirks/MidwayFamilyAssist.cs")
t = p.read_text(encoding="utf-8")
if "TryRepairGameartHost" in t:
    print("repair exists")
else:
    old = """        var iop = sys.IopModules;
        var cdvd = sys.Cdvd;
        if (iop == null || cdvd == null) return;
        rpc.PumpMslFileRequests(sys.Memory, iop, cdvd);
    }"""
    new = """        var iop = sys.IopModules;
        var cdvd = sys.Cdvd;
        if (iop == null || cdvd == null) return;
        rpc.PumpMslFileRequests(sys.Memory, iop, cdvd);
        rpc.TryEnsureMkdaArtPathHash(sys.Memory, iop, cdvd);
        TryRepairGameartHost(sys);
    }

    /// <summary>
    /// DA: 0x2D31D0 can race ahead of path-hash plant (one-shot open). After plant, if
    /// host slot 0x40B44C is still null but gameart stream was HLE-planted, publish it as
    /// host+4 and point the wait job slot at stream+20 (status=4) so wait-ready can exit
    /// without the false-complete Exit path of null-s0 *s0=4 plant.
    /// </summary>
    private void TryRepairGameartHost(Ps2System sys)
    {
        const uint hostSlot = 0x0040B448;
        const uint hostPlus4 = 0x0040B44C;
        const uint jobSlot = 0x005320E4;
        const uint stream = 0x0007F000;
        if (sys.Memory.Read32(hostPlus4) != 0) return;
        if (sys.Memory.Read32(stream) != 0x5354464Du) return;
        sys.Memory.Write32(hostPlus4, stream);
        if (sys.Memory.Read32(hostSlot) == 0)
            sys.Memory.Write32(hostSlot, 0x003F7840);
        if (sys.Memory.Read32(jobSlot) == 0)
            sys.Memory.Write32(jobSlot, stream + 20);
        // If EE is already spinning wait with s0==null, retarget s0 to the job status word.
        uint pc = (uint)sys.EE.PC;
        if (pc >= WaitReadyPcLo && pc <= WaitReadyPcHi)
        {
            uint s0 = (uint)sys.EE.GetGpr(16).Lo;
            if (s0 < 0x00100000 || s0 >= 0x02000000)
                sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = stream + 20 });
        }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[MKFAM] repair gameart host+4=0x{stream:X8} job=0x{stream + 20:X8} cyc={sys.MasterCycles}");
    }"""
    if old not in t:
        raise SystemExit("pump anchor missing")
    t = t.replace(old, new, 1)
    p.write_text(t, encoding="utf-8")
    print("added repair")
