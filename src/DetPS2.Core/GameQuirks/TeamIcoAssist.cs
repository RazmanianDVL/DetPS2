using System;

namespace DetPS2.Core;

/// <summary>
/// IOPRP ASCII LOADFILE GetVersion policy (no memory plants for SotC/Ico).
/// Used by Team ICO first-party titles and other retail discs that memcmp GetVersion
/// against the post-UDNL 4-char IOPRP tag (e.g. <c>"3000"</c>, <c>"2500"</c>).
///
/// <para>
/// <b>Shadow of the Colossus (SCUS_974.72)</b> reboots IOP with
/// <c>rom0:UDNL cdrom0:\IOPRP300.IMG;1</c>, then the EE LOADFILE client memcmp's the
/// GetVersion (fno=0xFF) 4-byte reply against rodata <c>"3000"</c> at <c>0x0013227C</c>
/// before any <c>sceSifLoadModule("cdrom0:\MODULES\SIO2MAN.IRX;1")</c>. With the default
/// classic reply <c>0x00020000</c> the gate returns <c>0xFFFEFFFC</c>, the game hangs in
/// an intentional error nop-sled at <c>0x001035B0</c>, and generic nop-sled rescue can
/// re-home mid VBlank busy-poll (<c>0x00111DA0</c>) with a garbage <c>v1</c>.
/// </para>
///
/// <para>
/// <b>Haven: Call of the King (SLUS_205.17)</b> reboots with
/// <c>rom0:UDNL cdrom0:\SYS250\IOPRP250.IMG;1</c> (shared tag <c>"2500"</c>). Without
/// <see cref="RealSifRpc.PreferIopRpGetVersion"/>, GetVersion stays classic
/// <c>0x00020000</c> and the title <c>Exit(0)</c> before any post-reboot MOD_LOAD / FILEIO.
/// PreferIopRp lands live SYS250 IRX + pad/MC/SD (px=3, cdvd=77 @100M, binds=12/calls=16).
/// </para>
///
/// <para>
/// <b>Haven boot geometry:</b> retail ELF is a single high-VA PT_LOAD at <c>0x01000000</c>
/// (entry <c>0x01000008</c>, ~2.5 MiB packed). Diagnose @20M still sits in the CRT0 bit-stream
/// decompress loop at <c>0x010003F0</c> (syscalls=0) — that is cycle budget, not a TLB/map miss:
/// RDRAM is 32 MiB and <c>TranslateAddress</c> identity-maps kuseg. Decompress finishes ~80–85M;
/// @100M: PC soft-float band, px=3, gifP3=2, full SYS250 (binds=12, cdvd=77).
/// </para>
///
/// <para>
/// <b>Haven residual (#21 — title surface / FILEIO·DLL.DAT):</b> after the IRX stack the EE
/// enters a sin/cos LUT fill at <c>0x0010CCD8</c> —
/// <c>for (i=0..N) table[i] = (float)sin((double)(i * k))</c> with <c>k≈π/16384</c>
/// (<c>0x39490FDB</c>). Each iteration calls soft-double f32→f64 (<c>0x00353A28</c>),
/// sin (<c>0x003432F0</c>), f64→f32 (<c>0x00352E30</c>); the sin poly lives at
/// <c>0x00345C30</c> / mul body <c>0x00352660</c> (band <c>0x00351xxx–0x00352xxx</c>).
/// Interpreter soft-float costs 10k–100k cycles/sin → 100–250M cycles with no
/// <c>DLL.DAT</c>/<c>FILEIO</c> string. Wave-2: register those entries on
/// <see cref="SoftFloatBridge"/> (shared host IEEE). Wave-3: clear software VIF1 busy
/// (<c>*(0x39C0C4)</c>) when the wait at <c>0x188AE0</c> spins while CHCR.STR is clear /
/// channel idle, and credit VIF1 DMA IRQ so the real handler can advance; NUSOUND2
/// (sid <c>0x00012345</c>, not Midway MSL.IRX) bulk fno=0 is handled in
/// <see cref="RealSifRpc"/>. Wave-4: NUSOUND bulk partial recv echo + <b>real-bind</b>
/// root <c>DLL.DAT</c> (~1.1 MiB SN module image) into RDRAM at <c>0x00800000</c>
/// (live residual <c>$ra=0x8925CC</c> / PC high band matched file+base); Soft-GS already
/// paints logo clear (px≈286720 gifP3=68) — next is chrome beyond clear / title surface.
/// Wave-6: post-NUSOUND <c>sceSifCallRpc</c> epilogue at <c>0x32BC94</c> does
/// <c>jr ra</c> with <c>ra=0</c> (stack slot wiped after open-bus thrash) → JREXIT →
/// tid1 <c>Started=false</c> while SIF worker parks WaitSema(3). Rescue: revive main +
/// stack/LastGood resume + worker WaitSema pulse (Whiplash JREXIT class).
/// Haven-only still: VBlankStart sticky + poll-base repair. Disc: <c>DLL.DAT</c>, <c>DATA/</c>.
/// </para>
///
/// <para>
/// Enabling <see cref="RealSifRpc.PreferIopRpGetVersion"/> reuses the shared
/// OnIopReboot ASCII tag path — no title-local memory plant. Same class as
/// <see cref="GodOfWarAssist"/> / <see cref="VexxAssist"/> version policy; no Midway plants.
/// </para>
/// </summary>
public sealed class TeamIcoAssist : IGameQuirkModule
{
    private readonly string _serial;
    private readonly string _displayName;
    private readonly bool _isHaven;

    // Haven INTC_STAT VBlankStart busy-poll (disasm residual top PC 0x331650).
    private const uint HavenVbPollA = 0x00331650;
    private const uint HavenVbPollAEnd = 0x00331668;
    private const uint HavenVbPollB = 0x003316F0;
    private const uint HavenVbPollBEnd = 0x0033170C;

    // Haven VIF1 software-busy wait (disasm 0x188AD8: jal 0x1883C8; 0x188AE0: bne v0,0).
    // Callee returns *(0x39C0C4); set when a VIF1 chain is kicked (CHCR=0x1C5), cleared by
    // the DMA completion path. When STR is already clear / channel idle, clear busy and
    // credit VIF1 IRQ so the real handler can run (same class as B3/DA owed-handler assist).
    private const uint HavenVifWaitSpin = 0x00188AE0;
    private const uint HavenVifWaitJal = 0x00188AD8;
    private const uint HavenVifBusyFlag = 0x0039C0C4;
    private const uint HavenVifPendingFlag = 0x0039C0DC;

    // sceSifCallRpc-class epilogue (live WAVE-5/6 residual).
    // 0x32BC6C ld ra,176(sp) … 0x32BC94 jr ra / 0x32BC98 addiu sp,192
    // then fall-through into a small validate leaf @0x32BCA0 that thrash-returns with ra=0.
    private const uint HavenRpcEpiJr = 0x0032BC94;
    private const uint HavenRpcEpiDelay = 0x0032BC98;
    private const uint HavenValidateLeaf = 0x0032BCA0;
    private const uint HavenValidateLeafEnd = 0x0032BCDC;
    // Post-StartThread continuation (live StartAndMaybeSwitch $ra resume target).
    private const uint HavenPostStartContinue = 0x0032A510;
    // CallRpc entry (jal from NUSOUND bulk wrapper) + its return site.
    // Live: 0x2091D0 jal 0x32BAB0; 0x2091D8 lui v1,0x4F (clears busy flag, returns).
    // Stack slot at CallRpc entry (sd ra,176(sp) @0x32BAF8) holds 0x2091D8.
    private const uint HavenCallRpcEntry = 0x0032BAB0;
    private const uint HavenPostNuSoundResume = 0x002091D8;
    private const uint HavenCallRpcFrame = 0xC0; // addiu sp,-192
    // Decompressed EE .text band after CRT0 unpack (high-VA ELF lands here).
    private const uint HavenTextLo = 0x00100000;
    private const uint HavenTextHi = 0x00400000;

    private int _vbPulses;
    private int _vbBaseRepairs;
    private int _vifBusyClears;
    private int _lateLogPulses;
    private int _jrExitRescues;
    private int _mainRevives;
    private int _badPcEscapes;
    private int _semaPulses;
    private int _postNuSoundRescues;
    private ulong _lastLogCyc;
    private ulong _lastVbPulseCyc;
    private ulong _lastVifBusyCyc;
    private ulong _lastJrRescueCyc;
    private ulong _lastSemaPulseCyc;
    private uint _lastGoodHavenPc;

    public TeamIcoAssist(string serial, string displayName)
    {
        _serial = serial ?? throw new ArgumentNullException(nameof(serial));
        _displayName = displayName ?? serial;
        _isHaven = string.Equals(_serial, "SLUS_205.17", StringComparison.OrdinalIgnoreCase);
    }

    public string Serial => _serial;
    public string DisplayName => _displayName;

    public void Reset()
    {
        _vbPulses = 0;
        _vbBaseRepairs = 0;
        _vifBusyClears = 0;
        _lateLogPulses = 0;
        _jrExitRescues = 0;
        _mainRevives = 0;
        _badPcEscapes = 0;
        _semaPulses = 0;
        _postNuSoundRescues = 0;
        _lastLogCyc = 0;
        _lastVbPulseCyc = 0;
        _lastVifBusyCyc = 0;
        _lastJrRescueCyc = 0;
        _lastSemaPulseCyc = 0;
        _lastGoodHavenPc = 0;
        if (_isHaven)
            SoftFloatBridge.Reset();
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        if (sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = true;
        if (_isHaven)
            RegisterHavenSoftFloat();
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            || Environment.GetEnvironmentVariable("DETPS2_TRACE_TEAMICO") == "1")
            Console.Error.WriteLine(
                $"[TEAMICO] OnDiscMounted: PreferIopRpGetVersion serial={_serial}"
                + (_isHaven ? $" havenVbAssist=on softFloatEntries={SoftFloatBridge.EntryCount}" : ""));
    }

    /// <summary>
    /// Haven post-decompress soft-double library (live @90M). Shared
    /// <see cref="SoftFloatBridge"/> evaluates IEEE on host so the sin LUT fill at
    /// <c>0x0010CCD8</c> can finish and reach first game-data FILEIO.
    /// </summary>
    private static void RegisterHavenSoftFloat()
    {
        SoftFloatBridge.RegisterMany(new (uint, SoftFloatBridge.Op)[]
        {
            // Core multi-precision arithmetic (sin/cos poly body)
            (0x00352660u, SoftFloatBridge.Op.DMul),
            (0x003525A0u, SoftFloatBridge.Op.DAdd),
            (0x003525F8u, SoftFloatBridge.Op.DSub),
            // libm
            (0x003432F0u, SoftFloatBridge.Op.DSin),
            (0x00342EB0u, SoftFloatBridge.Op.DCos),
            // float↔double bridges used by the 0x10CCD8 LUT fill
            (0x00353A28u, SoftFloatBridge.Op.F32ToF64),
            (0x00352E30u, SoftFloatBridge.Op.F64ToF32),
        });
    }

    public void OnHostPresent(Ps2System sys)
    {
        if (_isHaven && sys.Hle?.Sony?.RealRpc is { Binds: >= 10 })
            PulseHavenVblank(sys, force: false);
    }

    public void Step(Ps2System sys)
    {
        if (!_isHaven) return;

        var rpc = sys.Hle?.Sony?.RealRpc;
        if (rpc == null || rpc.Binds < 10) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFu);
        ulong cyc = sys.Scheduler.MasterCycles;

        if (IsSafeHavenText(sys, pc))
            _lastGoodHavenPc = pc;

        bool inPollA = pc is >= HavenVbPollA and < HavenVbPollAEnd;
        bool inPollB = pc is >= HavenVbPollB and < HavenVbPollBEnd;
        if (inPollA || inPollB)
        {
            // Mid-function re-home can leave v1 != INTC_STAT (same class as
            // Ps2System.TryRepairIntcStatPollBase).
            uint v1 = (uint)(sys.EE.GetGpr(3).Lo & 0xFFFFFFFFu);
            if (v1 != Intc.AddrStat)
            {
                sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = Intc.AddrStat });
                _vbBaseRepairs++;
            }
            PulseHavenVblank(sys, force: true);
        }
        else if (rpc.FileIoOps == 0 && (cyc - _lastVbPulseCyc) > 500_000UL)
        {
            PulseHavenVblank(sys, force: false);
        }

        // VIF1 software-busy spin @0x188AE0 (post soft-float residual).
        bool inVifWait = pc is >= HavenVifWaitJal and <= HavenVifWaitSpin + 4
            || (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu) is >= HavenVifWaitJal and <= HavenVifWaitSpin + 4;
        if (inVifWait)
            MaybeClearHavenVifBusy(sys, cyc);

        // Post-NUSOUND (binds≥13): JREXIT / open-bus / dead-main wall.
        if (rpc.Binds >= 13 && cyc >= 88_000_000UL)
        {
            MaybeRescueHavenJrExit(sys, pc, cyc);
            MaybeReviveHavenMain(sys, cyc);
            MaybeEscapeHavenBadPc(sys, pc, cyc);
            MaybePulseHavenWaiters(sys, cyc);
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_TEAMICO") == "1"
            && (cyc - _lastLogCyc) > 5_000_000UL
            && _lateLogPulses < 8)
        {
            _lastLogCyc = cyc;
            _lateLogPulses++;
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu);
            Console.Error.WriteLine(
                $"[TEAMICO-HAVEN] residual #{_lateLogPulses} cyc={cyc} pc=0x{pc:X8} ra=0x{ra:X8} "
                + $"binds={rpc.Binds} calls={rpc.Calls} fioOps={rpc.FileIoOps} "
                + $"intcStat=0x{sys.Intc.Stat:X} vbPulse={_vbPulses} vbFix={_vbBaseRepairs} "
                + $"vifBusyClr={_vifBusyClears} jrResc={_jrExitRescues} mainRev={_mainRevives} "
                + $"badPc={_badPcEscapes} semaPulse={_semaPulses}");
        }
    }

    /// <summary>
    /// When the wait-for-VIF1-idle loop at <c>0x188AE0</c> is live and the software busy
    /// flag is stuck while VIF1 CHCR.STR is clear / DMAC channel idle, clear the flag and
    /// credit the VIF1 AddDmacHandler path so completion side-effects can run.
    /// </summary>
    private void MaybeClearHavenVifBusy(Ps2System sys, ulong cyc)
    {
        if (_vifBusyClears >= 64) return;
        if ((cyc - _lastVifBusyCyc) < 50_000UL) return;

        uint busy = sys.Memory.Read32(HavenVifBusyFlag);
        uint pending = sys.Memory.Read32(HavenVifPendingFlag);
        if (busy == 0 && pending == 0) return;

        bool vifActive = sys.Dmac.IsActive(Dmac.Channel.VIF1);
        uint chcr = sys.Dmac.ReadRegister(0x10009000);
        bool str = (chcr & 0x100) != 0;
        if (vifActive || str) return;

        if (busy != 0)
            sys.Memory.Write32(HavenVifBusyFlag, 0);
        if (pending != 0)
            sys.Memory.Write32(HavenVifPendingFlag, 0);

        try
        {
            sys.Dmac.EnableChannelIrq((int)Dmac.Channel.VIF1);
            sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.VIF1, 1);
        }
        catch { /* ignore */ }

        _vifBusyClears++;
        _lastVifBusyCyc = cyc;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_TEAMICO") == "1"
            && _vifBusyClears <= 16)
            Console.Error.WriteLine(
                $"[TEAMICO-HAVEN] VIF1 busy clear n={_vifBusyClears} chcr=0x{chcr:X} "
                + $"busyWas=0x{busy:X} pendWas=0x{pending:X} cyc={cyc}");
    }

    private void PulseHavenVblank(Ps2System sys, bool force)
    {
        ulong cyc = sys.Scheduler.MasterCycles;
        if (!force && (cyc - _lastVbPulseCyc) < 200_000UL) return;
        Intc.CurrentCycleForTrace = cyc;
        if (sys.Intc.IsRaised(Intc.InterruptSource.VBlankStart))
            sys.Intc.RearmCpuLatch(Intc.InterruptSource.VBlankStart);
        else
            sys.Intc.Raise(Intc.InterruptSource.VBlankStart);
        _vbPulses++;
        _lastVbPulseCyc = cyc;
    }

    /// <summary>
    /// Live WAVE-6: after NUSOUND bulk, <c>sceSifCallRpc</c> epilogue @0x32BC94 does
    /// <c>jr ra</c> with a drifted SP (open-bus thrash) so <c>ld ra,176(sp)</c> reads 0 →
    /// JREXIT kills main. Reconstruct the CallRpc epilogue: restore s0–s7/fp from the
    /// 192-byte frame, pop SP, resume at <see cref="HavenPostNuSoundResume"/> (live
    /// <c>sd ra</c> slot value <c>0x2091D8</c> — NUSOUND bulk wrapper after
    /// <c>jal 0x32BAB0</c>).
    /// </summary>
    private void MaybeRescueHavenJrExit(Ps2System sys, uint pc, ulong cyc)
    {
        if (_jrExitRescues >= 24) return;
        if ((cyc - _lastJrRescueCyc) < 20_000UL) return;

        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu);
        bool inEpi = pc is >= HavenRpcEpiJr and <= HavenRpcEpiDelay + 4
            || pc is >= 0x0032BC6C and <= HavenRpcEpiDelay; // from ld ra through delay
        bool inValidate = pc is >= HavenValidateLeaf and <= HavenValidateLeafEnd;
        bool inCallRpc = pc is >= HavenCallRpcEntry and <= HavenValidateLeafEnd;
        bool mainDead = false;
        try
        {
            foreach (var t in sys.Hle.Kernel.AllThreads)
            {
                if (t.Id == 1 && t.Alive && !t.Started) { mainDead = true; break; }
            }
        }
        catch { /* ignore */ }

        // Only act when ra is dead, main Exit'd, or we're stuck in the CallRpc/validate band
        // with a bad link. Do not hop a valid in-progress return.
        if (!mainDead && ra != 0 && IsSafeHavenText(sys, ra) && !inValidate && !inEpi)
            return;
        if (!inEpi && !inValidate && !inCallRpc && !mainDead)
            return;

        // First post-NUSOUND rescue: complete the bulk CallRpc frame → 0x2091D8.
        // Later CallRpc JREXITs: complete whatever frame is live (any safe ra slot), never
        // re-apply a stale 0x2091D8 frame that already returned (thrash loop at frameSp=0x1FFF650).
        bool preferNu = _postNuSoundRescues == 0;
        if (!TryCompleteHavenCallRpcFrame(sys, preferNuSound: preferNu, out uint resume, out uint frameSp))
        {
            resume = preferNu ? HavenPostNuSoundResume : PickHavenResume(sys);
            if (resume == 0) resume = HavenPostNuSoundResume;
            if (!IsSafeHavenText(sys, resume)) return;
            frameSp = 0;
        }

        ReviveHavenMain(sys, resume);
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // CallRpc OK
        // Don't force $ra=resume (that re-enters the same site). Leave epilogue-restored $ra
        // when frame complete supplied it; only seed when we have no frame.
        if (frameSp == 0)
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        sys.EE.ClearPendingThreadStall();

        _jrExitRescues++;
        if (resume == HavenPostNuSoundResume) _postNuSoundRescues++;
        _lastJrRescueCyc = cyc;
        // Pulse SIF waiters so the next CallRpc can complete without a dead peer.
        MaybePulseHavenWaiters(sys, cyc);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_TEAMICO") == "1")
            Console.Error.WriteLine(
                $"[TEAMICO-HAVEN] JREXIT rescue 0x{pc:X8} -> 0x{resume:X8} n={_jrExitRescues} "
                + $"raWas=0x{ra:X8} frameSp=0x{frameSp:X8} mainDead={mainDead} cyc={cyc}");
    }

    /// <summary>
    /// Locate the live CallRpc frame (ra slot == <see cref="HavenPostNuSoundResume"/> or any
    /// safe return in .text) and finish its epilogue: restore s0–s7/fp, pop 192 bytes.
    /// </summary>
    private static bool TryCompleteHavenCallRpcFrame(Ps2System sys, bool preferNuSound,
        out uint resume, out uint frameSp)
    {
        resume = 0;
        frameSp = 0;
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
        // After JREXIT current thread is often the SIF worker — use main's SavedSp when
        // EE $sp is not the high-stack CallRpc frame.
        uint mainSp = 0;
        try
        {
            foreach (var t in sys.Hle.Kernel.AllThreads)
            {
                if (t.Id != 1 || !t.Alive) continue;
                mainSp = (uint)(t.SavedSp & 0x1FFFFFFFu);
                break;
            }
        }
        catch { /* ignore */ }

        // Scan a window of high-stack SP candidates (open-bus drifts SP by 0x10).
        var candidates = new System.Collections.Generic.List<uint>();
        void add(uint s)
        {
            if (s != 0 && !candidates.Contains(s)) candidates.Add(s);
        }
        add(sp); add(sp & ~0xFu); add(sp - 0x10u); add(sp + 0x10u);
        add(mainSp); add(mainSp - 0x10u); add(mainSp + 0x10u);
        // Live NUSOUND bulk frame + neighbours.
        add(0x01FFF8A0u); add(0x01FFF8B0u); add(0x01FFF890u); add(0x01FFF880u);
        // Walk a small band so later CallRpc frames (lower SP) still resolve.
        for (uint s = 0x01FFF400u; s <= 0x01FFFA00u; s += 0x10u) add(s);

        uint bestKnown = 0, bestKnownSp = 0, bestSafe = 0, bestSafeSp = 0;
        foreach (uint candSp in candidates)
        {
            if (candSp < 0x01FFE000 || candSp > 0x01FFFF80) continue;
            if (candSp + HavenCallRpcFrame + 4 >= (uint)SystemMemory.RDRAM_SIZE) continue;

            uint raSlot = sys.Memory.Read32(candSp + 0xB0); // 176
            uint raPhys = raSlot & 0x1FFFFFFFu;
            bool known = raPhys == HavenPostNuSoundResume;
            bool safe = IsSafeHavenText(sys, raPhys)
                && raPhys is < HavenCallRpcEntry or > HavenValidateLeafEnd
                && raPhys is >= 0x00100000 and < 0x00320000
                && LooksLikeReturnSite(sys, raPhys);
            if (known && bestKnown == 0) { bestKnown = raPhys; bestKnownSp = candSp; }
            if (safe && !known && bestSafe == 0) { bestSafe = raPhys; bestSafeSp = candSp; }
        }

        uint useRa, useSp;
        if (preferNuSound && bestKnown != 0) { useRa = bestKnown; useSp = bestKnownSp; }
        else if (bestSafe != 0) { useRa = bestSafe; useSp = bestSafeSp; }
        else if (bestKnown != 0) { useRa = bestKnown; useSp = bestKnownSp; }
        else return false;

        // Restore callee-saved from the frame (matches 0x32BC6C..0x32BC90 ld sequence).
        sys.EE.SetGpr(30, new EmotionEngine.Gpr128 { Lo = sys.Memory.Read32(useSp + 0xA0) }); // fp
        sys.EE.SetGpr(23, new EmotionEngine.Gpr128 { Lo = sys.Memory.Read32(useSp + 0x90) }); // s7
        sys.EE.SetGpr(22, new EmotionEngine.Gpr128 { Lo = sys.Memory.Read32(useSp + 0x80) }); // s6
        sys.EE.SetGpr(21, new EmotionEngine.Gpr128 { Lo = sys.Memory.Read32(useSp + 0x70) }); // s5
        sys.EE.SetGpr(20, new EmotionEngine.Gpr128 { Lo = sys.Memory.Read32(useSp + 0x60) }); // s4
        sys.EE.SetGpr(19, new EmotionEngine.Gpr128 { Lo = sys.Memory.Read32(useSp + 0x50) }); // s3
        sys.EE.SetGpr(18, new EmotionEngine.Gpr128 { Lo = sys.Memory.Read32(useSp + 0x40) }); // s2
        sys.EE.SetGpr(17, new EmotionEngine.Gpr128 { Lo = sys.Memory.Read32(useSp + 0x30) }); // s1
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = sys.Memory.Read32(useSp + 0x20) }); // s0
        sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = useSp + HavenCallRpcFrame });
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = useRa });

        resume = useRa;
        frameSp = useSp;
        return true;
    }

    /// <summary>Revive tid1 after JREXIT left Started=false (worker still WaitSema).</summary>
    private void MaybeReviveHavenMain(Ps2System sys, ulong cyc)
    {
        if (_mainRevives >= 16) return;
        try
        {
            foreach (var t in sys.Hle.Kernel.AllThreads)
            {
                if (!t.Alive || t.Id != 1) continue;
                if (t.Started) return;

                uint resume = PickHavenResume(sys);
                if (resume == 0) resume = HavenPostStartContinue;
                ReviveHavenMain(sys, resume);
                uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFu);
                if (!IsSafeHavenText(sys, pc) || pc < HavenTextLo)
                    sys.EE.PC = resume;
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
                sys.EE.COP0_Status &= ~0x6u;
                sys.EE.ClearPendingThreadStall();
                _mainRevives++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_TEAMICO") == "1")
                    Console.Error.WriteLine(
                        $"[TEAMICO-HAVEN] revive tid=1 -> 0x{resume:X8} n={_mainRevives} cyc={cyc}");
                return;
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Open-bus / data-as-code after NUSOUND (live: 0x1BBF0090, 0x04C600B8 → generic
    /// 0x520000/0x11C200 Midway rehomes). Snap back to Haven .text.
    /// </summary>
    private void MaybeEscapeHavenBadPc(Ps2System sys, uint pc, ulong cyc)
    {
        if (_badPcEscapes >= 48) return;

        uint pcRaw = (uint)(sys.EE.PC & 0xFFFFFFFFUL);
        bool exceptionVec = pcRaw is >= 0x80000180 and <= 0x80000280
            || pc is >= 0x00000180 and <= 0x00000280;
        bool pastRdram = pc >= (uint)SystemMemory.RDRAM_SIZE;
        bool lowBad = pc < HavenTextLo && !exceptionVec;
        // Midway open-bus defaults + known garbage landings from Haven residual.
        bool midwayRehome = pc is >= 0x00520000 and < 0x00530000
            || pc is >= 0x0011C200 and < 0x0011C400;
        bool dataAsCode = pc is >= 0x00400000 and < HavenTextHi + 0x00100000
            && !IsSafeHavenText(sys, pc);
        // Validate-leaf thrash with ra=0 (post-JREXIT fallthrough).
        bool validateThrash = pc is >= HavenValidateLeaf and <= HavenValidateLeafEnd
            && (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu) == 0;
        // VIF helper leaf @0x186878 (ldl/ldr packet copy) entered with garbage $a0 after
        // open-bus rehome — not a safe resume target.
        bool vifLeafStuck = pc is >= 0x00186878 and <= 0x001868B4
            && (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu) is >= 0x00186878 and <= 0x001868B4;

        if (!exceptionVec && !pastRdram && !lowBad && !midwayRehome && !dataAsCode
            && !validateThrash && !vifLeafStuck)
            return;

        uint resume = PickHavenResume(sys);
        // Never re-enter the VIF leaf as the resume target.
        if (resume is >= 0x00186878 and <= 0x001868B4)
            resume = HavenPostNuSoundResume;
        if (resume == 0) return;

        ReviveHavenMain(sys, resume);
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        sys.EE.ClearPendingThreadStall();
        _badPcEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_TEAMICO") == "1"
            && (_badPcEscapes <= 12 || _badPcEscapes % 8 == 0))
            Console.Error.WriteLine(
                $"[TEAMICO-HAVEN] bad-PC escape 0x{pcRaw:X8} -> 0x{resume:X8} n={_badPcEscapes} cyc={cyc}");
    }

    /// <summary>Keep SIF worker moving when main was dead (WaitSema parks).</summary>
    private void MaybePulseHavenWaiters(Ps2System sys, ulong cyc)
    {
        if (_semaPulses >= 64) return;
        if ((cyc - _lastSemaPulseCyc) < 200_000UL) return;

        bool need = _jrExitRescues > 0 || _mainRevives > 0 || _badPcEscapes > 0;
        if (!need) return;

        var k = sys.Hle.Kernel;
        int pulsed = 0;
        foreach (var t in k.AllThreads)
        {
            if (!t.Alive || !t.Sleeping) continue;
            int sema = t.WaitSemaId;
            if (sema <= 0) continue;
            try { k.SignalSema(sema); pulsed++; }
            catch { /* ignore */ }
        }
        if (pulsed == 0) return;
        _semaPulses++;
        _lastSemaPulseCyc = cyc;
        try { k.RequestImmediatePreempt(); } catch { /* ignore */ }
    }

    private static void ReviveHavenMain(Ps2System sys, uint resumePc)
    {
        try
        {
            foreach (var t in sys.Hle.Kernel.AllThreads)
            {
                if (t.Id != 1 || !t.Alive) continue;
                t.Started = true;
                t.EverStarted = true;
                t.Sleeping = false;
                t.WaitSemaId = 0;
                t.SavedPc = resumePc;
                break;
            }
        }
        catch { /* ignore */ }
    }

    private uint PickHavenResume(Ps2System sys)
    {
        // Prefer the proven NUSOUND bulk wrapper return (wave-6 live ground truth).
        if (IsSafeHavenText(sys, HavenPostNuSoundResume))
            return HavenPostNuSoundResume;

        static bool skipHelper(uint p) =>
            p is >= HavenCallRpcEntry and <= HavenValidateLeafEnd
            || p is >= 0x00186000 and <= 0x00189000; // VIF packet helpers

        uint last = _lastGoodHavenPc;
        if (IsSafeHavenText(sys, last) && !skipHelper(last))
            return last;

        uint lg = (uint)(sys.LastGoodEePc & 0x1FFFFFFFu);
        if (IsSafeHavenText(sys, lg) && !skipHelper(lg))
            return lg;

        // Stack scan: prefer return sites in game .text (skip CallRpc / VIF bands).
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
        if (sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE)
        {
            uint best = 0;
            uint limit = Math.Min(sp + 0x200, (uint)SystemMemory.RDRAM_SIZE - 4);
            for (uint a = sp; a + 4 <= limit; a += 4)
            {
                uint cand = sys.Memory.Read32(a) & 0x1FFFFFFFu;
                if (!IsSafeHavenText(sys, cand)) continue;
                if (cand is >= HavenCallRpcEntry and <= HavenValidateLeafEnd) continue;
                if (cand is >= 0x00188000 and <= 0x00189000) continue;
                if (cand is >= 0x00200000 and < 0x00300000)
                    return cand; // strong: main game .text
                if (cand < 0x0032B000)
                    best = cand;
            }
            if (best != 0) return best;
        }

        if (IsSafeHavenText(sys, HavenPostStartContinue))
            return HavenPostStartContinue;
        return 0;
    }

    private static bool IsSafeHavenText(Ps2System sys, uint pc)
    {
        if (pc < HavenTextLo || pc >= HavenTextHi || (pc & 3u) != 0) return false;
        if (pc >= (uint)SystemMemory.RDRAM_SIZE) return false;
        try { return sys.Memory.IsLikelyEeCode(pc); }
        catch { return false; }
    }

    /// <summary>
    /// True when <paramref name="pc"/> looks like a jal/jalr return site (not a function
    /// prologue). Rejects bare entries like end_function @0x211878 that appear as data on
    /// the stack and would re-enter with a garbage frame.
    /// </summary>
    private static bool LooksLikeReturnSite(Ps2System sys, uint pc)
    {
        if (pc < 8) return false;
        try
        {
            // Function prologue at pc → not a return site.
            uint here = sys.Memory.Read32(pc);
            // addiu sp, sp, -imm (frame setup)
            if ((here & 0xFFFF0000u) == 0x27BD0000u && (short)(here & 0xFFFF) < 0)
                return false;
            // Prefer: instruction before delay slot is jal/jalr (caller linked here).
            uint prev = sys.Memory.Read32(pc - 8);
            uint op = prev >> 26;
            if (op == 3) return true; // JAL
            if (op == 0 && (prev & 0x3F) == 9) return true; // JALR
            // Also accept mid-function sites (not prologue) in game .text.
            return pc is >= 0x00200000 and < 0x00300000;
        }
        catch { return false; }
    }
}
