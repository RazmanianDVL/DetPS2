using System;

namespace DetPS2.Core;

/// <summary>
/// Minimal Midway MK-family boot assist for titles that need SN/PADMAN version gates
/// without Shaolin Monks' CRI/WAD plant machinery.
///
/// Targets:
/// <list type="bullet">
/// <item>MK: Deadly Alliance (SLUS_204.23) — PADMAN GetModVer major 4 + IOPRP ASCII GetVersion</item>
/// <item>MK: Deception (SLUS_208.81) — IOPRP ASCII GetVersion (and XPADMAN-class pad gate)</item>
/// <item>MK: Armageddon (SLUS_215.50 standard / SLUS_215.43 Premium Edition) — same SN-family gates</item>
/// </list>
///
/// Does <b>not</b> run <see cref="MidwayBootAssist"/> SM plants (no CRI, no logo spine,
/// no ADX thrash escapes). Flips <see cref="RealSifRpc"/> version policy flags and applies
/// a shared Midway heap-tree cycle break (see <see cref="TryBreakHeapTreeCycle"/>).
/// </summary>
public sealed class MidwayFamilyAssist : IGameQuirkModule
{
    private readonly string _serial;
    private readonly string _displayName;

    // Midway custom heap: block lookup walk via node+0x24 / +0x28. After incomplete
    // MWo3 overlay free (GAMER.OVL stub → no GAMEFD.ovl body), free can leave a
    // right-child cycle so the walk never exits.
    // Prefer breaking the cycle in RDRAM (repair) over planting a permanent code stub.
    // PC bands differ by title build; +0x24/+0x28 layout is SHARED across DA/Dec/Arm.
    // Dec SLUS_208.81 / DA family: 0x3BA948..0x3BA98C, ret0 @ 0x3BA900
    // Arm Premium SLUS_215.43 (live 2026-07-30): 0x42940C..0x42944C, ret0 @ 0x429450
    private static readonly (uint Lo, uint Hi, uint Ret0)[] HeapWalkBands =
    {
        (0x003BA948, 0x003BA98C, 0x003BA900), // DA / Deception
        (0x0042940C, 0x0042944C, 0x00429450), // Armageddon PE (SLUS_215.43)
    };

    // DA (SLUS_204.23) wait-for-ready: while (*s0 != 4) { spin; Delay(50); poll MSL }.
    // Live: after MSL fno=0xDADA, boot opens gameart.ssf (MKDA.PAK artps2 member) and
    // parks here. When archive stream/host was never mounted, s0 stays 0 and the wait
    // is unbounded (primary DA wall @0x2F5580). Shared shape with Dec asset waits.
    private const uint WaitReadyPcLo = 0x002F5564;
    private const uint WaitReadyPcHi = 0x002F55AC;
    private const uint WaitReadyEpilogue = 0x002F55B0; // restore s0/ra; jr ra
    // MSL EE response ring (DA live @0x587E60): +0 capacity, +4 count. count==0 ⇒ poll
    // short-circuits and async file completions never land.
    private const uint MslRingDa = 0x00587E60;
    // Scratch status word used when wait is entered with s0==null (no job object).
    private const uint WaitReadyScratch = 0x0007FF00;

    // Dec SLUS_208.81 post-MSL main abort (live 2026-07-30, 200M host-present):
    //   main@0x1235B0 → 0x127900 → 0x126CE0 → 0x1D8120 → jal 0x1D9620
    //   0x1D9620 (type/factory register for ids 0x509/0x50E/0x510/0x1F) returns 0
    //   → 0x1D8120 fails → 0x126CE0 fails → 0x127900 fails → main epilogue@0x1238E0
    //   → CRT Exit(0) @ ~188M BEFORE any EE CallRpc member .ssf open.
    // Soft-success fail-tails so main can leave CRT Exit and reach game loop @0x1237F0
    // without force-completing DA wait status=4. TITLE_LOCAL Dec only.
    // Root poison: type id 0x510 factory 0x1D5270→0x1AB810 returns -1 @0x1D97D4.
    private const uint DecSysInitBandLo = 0x001D8120;
    private const uint DecSysInitBandHi = 0x001D8290;

    private uint _walkLastV1;
    private int _walkSameV1Hits;
    private int _walkBandHits;
    private int _cycleBreaks;
    private int _walkForcedExits;
    private int _waitReadyHits;
    private int _waitReadyEscapes;
    private int _mslRingSeeds;
    private int _decSysInitEscapes;
    private bool _decSysInitPlanted;

    public MidwayFamilyAssist(string serial, string displayName)
    {
        _serial = serial ?? throw new ArgumentNullException(nameof(serial));
        _displayName = displayName ?? serial;
    }

    public string Serial => _serial;
    public string DisplayName => _displayName;

    public void Reset()
    {
        _walkLastV1 = 0;
        _walkSameV1Hits = 0;
        _walkBandHits = 0;
        _cycleBreaks = 0;
        _walkForcedExits = 0;
        _waitReadyHits = 0;
        _waitReadyEscapes = 0;
        _mslRingSeeds = 0;
        _mslFilePumps = 0;
        _decSysInitEscapes = 0;
        _decSysInitPlanted = false;
    }

    /// <summary>True when this assist is bound to Deception (SLUS_208.81).</summary>
    public bool IsDeception =>
        _serial.Equals("SLUS_208.81", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dec sys-init fail band that aborts main→Exit after MSL. Exposed so
    /// <see cref="Ps2System"/> can tighten the EE slice and catch one-instruction gates.
    /// </summary>
    public static bool IsDecSysInitHotPc(ulong pcPhys) =>
        pcPhys is >= DecSysInitBandLo and <= DecSysInitBandHi
            or (>= 0x00126CE0UL and <= 0x00126F60UL)
            or (>= 0x00127900UL and <= 0x00127A00UL)
            or (>= 0x001D9620UL and <= 0x001D9900UL);

    public void OnDiscMounted(Ps2System sys) => ApplyVersionPolicy(sys);

    public void OnHostPresent(Ps2System sys)
    {
        // Keep pad DMA buffers STABLE after OPEN so EE padGetState / dual-buffer polls
        // leave the post-pad SyncDCache thrash (Dec 0x10C6xx) and continue IRX load.
        try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); }
        catch { /* ignore */ }
    }

    public void Step(Ps2System sys)
    {
        // Re-assert after IOP reboot / RealRpc internal resets that clear open pad state
        // but leave flags; cheap idempotent set in case a future path recreates RealRpc.
        ApplyVersionPolicy(sys);
        TrySeedMslRing(sys);
        // SHARED: complete EE-queued MSL/MFL file opens (MKDA.PAK / art|artps2 members) via
        // RealSifRpc so gameart.ssf can reach status==4 without planting *s0=4 (Exit).
        // Restored after accidental drop in 8313945 (Arm PE freelist multi-band refactor).
        TryPumpMslFiles(sys);
        // Prefer honest host job status over force-writing *s0 (arbitrary s0 can corrupt
        // unrelated words and leave post-wait dormancy / Exit). Only escape when host is live.
        if (sys.Memory.Read32(0x0040B44C) != 0)
            TryEscapeWaitReady(sys);
        TryBreakHeapTreeCycle(sys);
        // TITLE_LOCAL Dec: soft-success post-MSL factory/sys-init so main does not Exit(0)
        // before member .ssf CallRpc (see DecSysInit* constants).
        if (IsDeception)
            TryEscapeDecSysInitFail(sys);
    }

    /// <summary>
    /// Deception only: rewrite fail-tails so post-MSL subsystem init returning 0 does not
    /// abort main→CRT Exit(0) before the game loop / member .ssf path.
    ///
    /// Live chain (200M): 0x1D9620→0x1D8120→0x126CE0→0x127900→main@0x1238E0→Exit.
    /// Plants:
    /// <list type="bullet">
    /// <item>0x1D8120 fail tails (factory register 0x1D9620 / 0x1D3F10 / 0x1E1340)</item>
    /// <item>0x127900 fail tails after 0x126CE0 and sibling inits (covers later 0x126CE0 gates)</item>
    /// </list>
    /// One-shot RDRAM plant — Step cannot catch single-instruction gates across slices.
    /// Does not plant wait status=4 (DA Exit lesson).
    /// </summary>
    private void TryEscapeDecSysInitFail(Ps2System sys)
    {
        if (_decSysInitPlanted) return;
        // EE code resident after PT_LOAD; plant once early so it's live before MSL (~180M).
        if (sys.MasterCycles < 5_000_000) return;

        int plants = 0;

        // --- 0x1D8120 fail tails (inner factory/sys register) ---
        // 0x1D8250: b fail → b success@0x1D8258; delay v0=1
        if (sys.Memory.Read32(0x001D8250) == 0x1000000Bu)
        {
            sys.Memory.Write32(0x001D8250, 0x10000001u);
            sys.Memory.Write32(0x001D8254, 0x24020001u);
            plants++;
        }
        if (sys.Memory.Read32(0x001D8268) == 0x10000005u)
        {
            sys.Memory.Write32(0x001D8268, 0x10000001u);
            sys.Memory.Write32(0x001D826C, 0x24020001u);
            plants++;
        }
        if (sys.Memory.Read32(0x001D8278) == 0x0002102Bu)
        {
            sys.Memory.Write32(0x001D8278, 0x24020001u); // addiu v0, zero, 1
            plants++;
        }

        // --- 0x127900 fail tails (main's direct gate; covers all 0x126CE0 failures) ---
        // Pattern: bne v0,success; b fail; move v0,zero  →  b success; addiu v0,1
        // After 0x1AFDA0 @0x127928 (imm 0x32 → 0x1279F4)
        if (sys.Memory.Read32(0x00127928) == 0x10000032u)
        {
            sys.Memory.Write32(0x00127928, 0x10000001u); // → 0x127930
            sys.Memory.Write32(0x0012792C, 0x24020001u);
            plants++;
        }
        // After 0x126CE0 @0x127950 (imm 0x28 → 0x1279F4) — live Exit path
        if (sys.Memory.Read32(0x00127950) == 0x10000028u)
        {
            sys.Memory.Write32(0x00127950, 0x10000001u); // → 0x127958
            sys.Memory.Write32(0x00127954, 0x24020001u);
            plants++;
        }
        // After 0x227A00 @0x127978 (imm 0x1E → 0x1279F4)
        if (sys.Memory.Read32(0x00127978) == 0x1000001Eu)
        {
            sys.Memory.Write32(0x00127978, 0x10000005u); // → 0x127990
            sys.Memory.Write32(0x0012797C, 0x24020001u);
            plants++;
        }
        // After 0x1AFC00-null path @0x127988 (imm 0x1A → 0x1279F4)
        if (sys.Memory.Read32(0x00127988) == 0x1000001Au)
        {
            sys.Memory.Write32(0x00127988, 0x10000001u); // → 0x127990
            sys.Memory.Write32(0x0012798C, 0x24020001u);
            plants++;
        }
        // After 0x1AFAF0 @0x1279B8 (imm 0x0E → 0x1279F4)
        if (sys.Memory.Read32(0x001279B8) == 0x1000000Eu)
        {
            sys.Memory.Write32(0x001279B8, 0x10000001u); // → 0x1279C0
            sys.Memory.Write32(0x001279BC, 0x24020001u);
            plants++;
        }

        // Also force 0x126CE0 fail epilogue to return success (v0=1) if any printf path hit.
        // 0x126F5C: daddu v0,zero,zero before jr → addiu v0,1
        if (sys.Memory.Read32(0x00126F5C) == 0x0000102Du)
        {
            sys.Memory.Write32(0x00126F5C, 0x24020001u);
            plants++;
        }

        // 0x1D9620: type id 0x510 register via 0x1D5270 returns -1 (live), then
        // `or s0,s0,v0` at 0x1D97D8 poisons s0 → bgez fails → return 0.
        // Nop the poison OR so earlier successful registrations keep s0>=0; then
        // 0x1DA0F0 (stub returns 1) completes the function successfully.
        if (sys.Memory.Read32(0x001D97D8) == 0x02028025u) // or s0, s0, v0
        {
            sys.Memory.Write32(0x001D97D8, 0x00000000u); // nop
            plants++;
        }
        // Belt-and-suspenders: if s0 still negative, force success path to 0x1DA0F0.
        // 0x1D98E4: b fail@0x1D9900 → b 0x1D98F0
        if (sys.Memory.Read32(0x001D98E4) == 0x10000006u)
        {
            sys.Memory.Write32(0x001D98E4, 0x10000002u); // → 0x1D98F0
            sys.Memory.Write32(0x001D98E8, 0x24020001u);
            plants++;
        }

        if (plants == 0) return;
        _decSysInitPlanted = true;
        _decSysInitEscapes = plants;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[MKFAM] Dec post-MSL Exit redirect plants={plants} cyc={sys.MasterCycles}");
    }

    private int _mslFilePumps;

    /// <summary>
    /// Drive shared MFL ring completion while DA sits in wait-ready or after MSL init.
    /// Throttled: once per ~64 steps in the wait band, else every ~4k steps globally.
    /// </summary>
    private void TryPumpMslFiles(Ps2System sys)
    {
        var rpc = sys.Hle?.Sony?.RealRpc;
        if (rpc == null) return;
        uint pc = (uint)sys.EE.PC;
        bool inWait = pc >= WaitReadyPcLo && pc <= WaitReadyPcHi;
        _mslFilePumps++;
        if (inWait)
        {
            if ((_mslFilePumps & 63) != 0) return;
        }
        else if ((_mslFilePumps & 4095) != 0)
            return;

        var iop = sys.IopModules;
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
    /// Also re-assert stream size @+8/+12 when EE zeros +8 after plant (live dump).
    /// </summary>
    private void TryRepairGameartHost(Ps2System sys)
    {
        const uint hostSlot = 0x0040B448;
        const uint hostPlus4 = 0x0040B44C;
        const uint jobSlot = 0x005320E4;
        const uint stream = 0x0007F000;
        if (sys.Memory.Read32(stream) != 0x5354464Du) return;

        // Size repair: plant wrote msz at +8/+12; EE sometimes zeros +8 while +12 keeps size.
        uint sz8 = sys.Memory.Read32(stream + 8);
        uint sz12 = sys.Memory.Read32(stream + 12);
        if (sz8 == 0 && sz12 > 0x1000 && sz12 < 0x0400_0000)
            sys.Memory.Write32(stream + 8, sz12);
        else if (sz12 == 0 && sz8 > 0x1000 && sz8 < 0x0400_0000)
            sys.Memory.Write32(stream + 12, sz8);
        // Status word must stay 4 for wait-ready.
        if (sys.Memory.Read32(stream + 20) != 4)
            sys.Memory.Write32(stream + 20, 4);

        if (sys.Memory.Read32(hostPlus4) == 0)
        {
            sys.Memory.Write32(hostPlus4, stream);
            if (sys.Memory.Read32(hostSlot) == 0)
                sys.Memory.Write32(hostSlot, 0x003F7840);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[MKFAM] repair gameart host+4=0x{stream:X8} job=0x{stream + 20:X8} cyc={sys.MasterCycles}");
        }
        if (sys.Memory.Read32(jobSlot) == 0)
            sys.Memory.Write32(jobSlot, stream + 20);

        // In wait band: always prefer s0 → honest job status (stream+20) when host is live.
        // Force-writing a random valid s0 (live: 0x34FF88) false-completes the wrong object.
        uint pc = (uint)sys.EE.PC;
        if (pc >= WaitReadyPcLo && pc <= WaitReadyPcHi)
        {
            uint s0 = (uint)sys.EE.GetGpr(16).Lo;
            uint job = stream + 20;
            if (s0 != job)
                sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = job });
        }
    }

    private static void ApplyVersionPolicy(Ps2System sys)
    {
        var rpc = sys.Hle?.Sony?.RealRpc;
        if (rpc == null) return;
        rpc.PadModVerMajor4 = true;
        rpc.PreferIopRpGetVersion = true;
        // IOPRP300 digits would otherwise arm Play! FILEIO-2200 (SotC path). Midway EE is SN
        // ProDG FILEIO — keep classic open/read/lseek reply shapes so GAMER.OVL full-read and
        // later MKDA.PAK member opens complete.
        rpc.PreferSnFileIo = true;
    }

    /// <summary>
    /// If the DA MSL response ring looks initialized (capacity 0x28) but count is still 0
    /// after MSL bind/init, seed count=1 so EE poll helpers do not hard-skip. Does not
    /// invent full async payloads — only unblocks the empty-ring short-circuit.
    /// Safe: only when cap==0x28, count==0, and ring base is a valid RDRAM pointer.
    /// </summary>
    private void TrySeedMslRing(Ps2System sys)
    {
        if (_mslRingSeeds != 0) return;
        uint cap = sys.Memory.Read32(MslRingDa);
        uint count = sys.Memory.Read32(MslRingDa + 4);
        if (cap != 0x28 || count != 0) return;
        // Only seed once PAD/MSL boot has progressed (ring buffer base non-null).
        uint basePtr = sys.Memory.Read32(MslRingDa + 8);
        if (basePtr < 0x00100000 || basePtr >= 0x02000000) return;
        sys.Memory.Write32(MslRingDa + 4, 1);
        _mslRingSeeds = 1;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine($"[MKFAM] MSL ring seed count=1 base=0x{basePtr:X8} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Escape DA wait-for-ready at 0x2F5564..0x2F55AC when host stream is live.
    /// Prefer retargeting s0 to the planted job status (stream+20 already=4) over writing
    /// *s0=4 on an arbitrary object (live s0=0x34FF88 was wrong — post-wait dormancy).
    /// Null-s0 falls back to job slot / scratch.
    /// </summary>
    private void TryEscapeWaitReady(Ps2System sys)
    {
        uint pc = (uint)sys.EE.PC;
        if (pc < WaitReadyPcLo || pc > WaitReadyPcHi)
        {
            _waitReadyHits = 0;
            return;
        }

        _waitReadyHits++;
        if (_waitReadyHits < 64) return;

        const uint stream = 0x0007F000;
        const uint job = stream + 20;
        bool hostLive = sys.Memory.Read32(0x0040B44C) != 0
            && sys.Memory.Read32(stream) == 0x5354464Du;
        if (hostLive && sys.Memory.Read32(job) != 4)
            sys.Memory.Write32(job, 4);

        uint s0 = (uint)sys.EE.GetGpr(16).Lo;
        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1";
        ulong cyc = sys.MasterCycles;

        // Honest path: point s0 at host job status when stream is planted.
        if (hostLive)
        {
            if (s0 != job)
            {
                sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = job });
                if (sys.Memory.Read32(0x005320E4) == 0)
                    sys.Memory.Write32(0x005320E4, job);
                _waitReadyEscapes++;
                _waitReadyHits = 0;
                if (trace && _waitReadyEscapes <= 16)
                    Console.Error.WriteLine(
                        $"[MKFAM] wait-ready retarget s0=0x{job:X8} (was 0x{s0:X8}) " +
                        $"pc=0x{pc:X8} n={_waitReadyEscapes} cyc={cyc}");
            }
            return;
        }

        // No host yet: do not force *s0=4 (Exit). Null-s0 scratch only after long wait.
        if (s0 >= 0x00100000 && s0 < 0x02000000)
            return;

        if (_waitReadyHits < 96) return;
        sys.Memory.Write32(WaitReadyScratch, 4);
        sys.Memory.Write32(0x005320E4, WaitReadyScratch);
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = WaitReadyScratch });
        _waitReadyEscapes++;
        _waitReadyHits = 0;
        if (trace && _waitReadyEscapes <= 16)
            Console.Error.WriteLine(
                $"[MKFAM] wait-ready null-s0 plant scratch=0x{WaitReadyScratch:X8}=4 " +
                $"slot=0x5320E4 n={_waitReadyEscapes} cyc={cyc}");
    }

    /// <summary>
    /// Detect Midway heap range-tree walk stuck on a +0x28 cycle (post-OVL free corruption)
    /// and repair by nulling one right-child link, or force a null lookup return.
    /// Shared across DA/Deception/Armageddon — same +0x24/+0x28 tree layout; PC bands vary.
    /// </summary>
    private void TryBreakHeapTreeCycle(Ps2System sys)
    {
        uint pc = (uint)sys.EE.PC;
        uint ret0 = 0;
        bool inBand = false;
        foreach (var (lo, hi, bandRet0) in HeapWalkBands)
        {
            if (pc < lo || pc > hi) continue;
            inBand = true;
            ret0 = bandRet0;
            break;
        }
        if (!inBand)
        {
            _walkBandHits = 0;
            _walkSameV1Hits = 0;
            return;
        }

        _walkBandHits++;
        uint v1 = (uint)sys.EE.GetGpr(3).Lo; // current node
        if (v1 != 0 && v1 == _walkLastV1)
            _walkSameV1Hits++;
        else
        {
            _walkLastV1 = v1;
            _walkSameV1Hits = 0;
        }

        // Fast path: same node re-entered many times inside the band ⇒ likely cycle.
        // Also fire if we have been in-band for a long time with varying nodes (full cycle).
        bool stickyNode = _walkSameV1Hits >= 8;
        bool longBand = _walkBandHits >= 64;
        if (!stickyNode && !longBand)
            return;

        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1";
        ulong cyc = sys.MasterCycles;

        // Attempt structural repair: from current v1, walk +0x28 up to 16 hops; if a node
        // repeats, null the link that closed the cycle.
        if (v1 >= 0x00100000 && v1 < 0x02000000)
        {
            if (BreakRightChildCycle(sys, v1, out uint cutAt, out uint cutTo))
            {
                _cycleBreaks++;
                _walkBandHits = 0;
                _walkSameV1Hits = 0;
                if (trace && _cycleBreaks <= 16)
                    Console.Error.WriteLine(
                        $"[MKFAM] heap-tree cycle break node=0x{cutAt:X8} +0x28 was 0x{cutTo:X8} " +
                        $"pc=0x{pc:X8} n={_cycleBreaks} cyc={cyc}");
                return;
            }
        }

        // Fallback: force null return from lookup (band ret0 epilogue / exit sets v0=0).
        // Used when the cycle walk cannot be resolved (garbage pointers).
        if (_walkBandHits >= 128 || _cycleBreaks >= 4)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
            sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 }); // s0
            sys.EE.PC = ret0;
            _walkForcedExits++;
            _walkBandHits = 0;
            _walkSameV1Hits = 0;
            if (trace && _walkForcedExits <= 16)
                Console.Error.WriteLine(
                    $"[MKFAM] heap-walk force-ret0 pc=0x{pc:X8} v1=0x{v1:X8} ret0=0x{ret0:X8} " +
                    $"n={_walkForcedExits} cyc={cyc}");
        }
    }

    /// <summary>
    /// Walk node+0x28 chain from <paramref name="start"/>; if a cycle is found, null the
    /// right-child pointer that would re-enter a seen node. Returns true if a link was cut.
    /// </summary>
    private static bool BreakRightChildCycle(Ps2System sys, uint start, out uint cutAt, out uint cutTo)
    {
        cutAt = 0;
        cutTo = 0;
        // Tortoise/hare + explicit set for the cut site.
        Span<uint> seen = stackalloc uint[24];
        int n = 0;
        uint cur = start;
        for (int hop = 0; hop < 24; hop++)
        {
            if (cur < 0x00100000 || cur >= 0x02000000)
                return false;
            for (int i = 0; i < n; i++)
            {
                if (seen[i] != cur) continue;
                // Cycle: predecessor is seen[n-1] (or start if n==0 — use cur itself).
                uint pred = n > 0 ? seen[n - 1] : cur;
                uint next = sys.Memory.Read32(pred + 0x28);
                // Prefer nulling pred→cur if that is the back-edge; else null cur's right.
                if (next == cur || next == start)
                {
                    cutAt = pred;
                    cutTo = next;
                    sys.Memory.Write32(pred + 0x28, 0);
                    return true;
                }
                cutAt = cur;
                cutTo = sys.Memory.Read32(cur + 0x28);
                sys.Memory.Write32(cur + 0x28, 0);
                return true;
            }
            if (n < seen.Length)
                seen[n++] = cur;
            uint r = sys.Memory.Read32(cur + 0x28);
            if (r == 0)
            {
                // Try left child chain as well (walker uses +0x24 when range matches).
                uint l = sys.Memory.Read32(cur + 0x24);
                if (l == 0) return false;
                // Detect left-cycle similarly on a short walk.
                return BreakLeftChildCycle(sys, cur, out cutAt, out cutTo);
            }
            // Direct back-edge to start or any previous.
            for (int i = 0; i < n; i++)
            {
                if (r != seen[i] && r != start) continue;
                cutAt = cur;
                cutTo = r;
                sys.Memory.Write32(cur + 0x28, 0);
                return true;
            }
            cur = r;
        }
        // Long chain without null — cut current right as last resort.
        if (cur >= 0x00100000 && cur < 0x02000000)
        {
            uint r = sys.Memory.Read32(cur + 0x28);
            if (r != 0)
            {
                cutAt = cur;
                cutTo = r;
                sys.Memory.Write32(cur + 0x28, 0);
                return true;
            }
        }
        return false;
    }

    private static bool BreakLeftChildCycle(Ps2System sys, uint start, out uint cutAt, out uint cutTo)
    {
        cutAt = 0;
        cutTo = 0;
        uint cur = start;
        Span<uint> seen = stackalloc uint[16];
        int n = 0;
        for (int hop = 0; hop < 16; hop++)
        {
            if (cur < 0x00100000 || cur >= 0x02000000) return false;
            for (int i = 0; i < n; i++)
            {
                if (seen[i] != cur) continue;
                uint pred = n > 0 ? seen[n - 1] : cur;
                cutAt = pred;
                cutTo = sys.Memory.Read32(pred + 0x24);
                sys.Memory.Write32(pred + 0x24, 0);
                return true;
            }
            if (n < seen.Length) seen[n++] = cur;
            uint l = sys.Memory.Read32(cur + 0x24);
            if (l == 0) return false;
            for (int i = 0; i < n; i++)
            {
                if (l != seen[i]) continue;
                cutAt = cur;
                cutTo = l;
                sys.Memory.Write32(cur + 0x24, 0);
                return true;
            }
            cur = l;
        }
        return false;
    }
}
