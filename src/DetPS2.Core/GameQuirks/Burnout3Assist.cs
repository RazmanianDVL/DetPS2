using System;

namespace DetPS2.Core;

/// <summary>
/// Burnout 3: Takedown (SLUS_210.50) — IOPRP version plant + GS flip-queue assist.
///
/// <para>
/// <b>Primary blocker (2026-07-30):</b> after FILEIO/LOADFILE GetVersion, boot at
/// <c>0x001D41E0</c> calls <c>SifInitIopHeap</c> then tries to load 10 IRX modules from
/// <c>cdrom0:\IOP\</c> (SIO2MAN, DBCMAN, …). <c>SifLoadModule</c> (<c>0x00113D50</c>)
/// runs a version gate at <c>0x00113678</c> that memcmp's against ASCII <c>"2800"</c>
/// (DNAS280 / IOPRP 2.8.0) and against a version pointer (<c>*0x00484224 → 0x004B22C0</c>)
/// that still holds the unfilled <c>"...."</c> placeholder. Real hardware fills that cell
/// when UDNL applies <c>DNAS280.IMG</c>; HLE has no UDNL image apply, so the check returns
/// non-zero → <c>0xFFFEFFFC</c> → module load aborts → SifInitIopHeap rebinds forever
/// (binds climbing, calls stuck at 6, cdvdSectors=0). Same class as
/// <see cref="BloodOmen2SnAssist"/> <c>"2340"</c> / <see cref="GodOfWarAssist"/> <c>"3000"</c>.
/// </para>
///
/// <para>
/// <b>Flip residual:</b> after early CDVD/FILEIO init, main also parks in the GS
/// flip/watermark loop (<c>0x001F24E0</c> / callback <c>0x00228040</c>). Consumer at
/// <c>0x001F1778</c> only decrements pending on IRQ (a0=VIF1/GIF) and only drains out→in
/// when pending hits 0. Force out←in skips the drain — never do that. Re-credit owed
/// AddDmacHandler calls instead.
/// </para>
/// </summary>
public sealed class Burnout3Assist : IGameQuirkModule
{
    public string Serial => "SLUS_210.50";
    public string DisplayName => "Burnout 3: Takedown (USA)";

    // Live gp=0x4E8670 at path-sync (blocker-trace). Offsets from disasm of 0x1F24E0.
    // pending-count byte: gp-24128 = 0x4E2830
    // out counter:         gp-24120 = 0x4E2838
    // in counter:          gp-24116 = 0x4E283C
    public const uint PendingCountAddr = 0x004E2830;
    public const uint QueueOutAddr = 0x004E2838;
    public const uint QueueInAddr = 0x004E283C;
    /// <summary>gp-23820 at gp=0x4E8670 — 4 flag bytes polled by VBlank wakeup 0x237180.</summary>
    public const uint VblankWakeFlagBase = 0x004E2964;

    /// <summary>Unfilled IOPRP version placeholder in EE .data ("....").</summary>
    public const uint IopVersionPlaceholder = 0x004B22C0;
    /// <summary>Pointer cell that SifLoadModule version gate loads as alt compare target.</summary>
    public const uint IopVersionPtrCell = 0x00484224;
    /// <summary>Rodata expectation ASCII "2800" (DNAS280 / IOPRP 2.8.0).</summary>
    public const uint IopVersionExpected = 0x0048414C;

    /// <summary>
    /// lgDeviceInit post-version flag at <c>0x4B0400</c>. When non-zero, EE memcpys a
    /// 0x160 buffer and issues CallRpc fno=18 on the LGDEV client. Live (2026-07-30): that
    /// path floods SIFCMD cid=0 on the recv buffer + CreateSema/WaitSema thrash and never
    /// reaches GTFS assets. Zero skips to the clean return at <c>0x443C48</c>.
    /// </summary>
    public const uint LgDevPostFlag = 0x004B0400;
    /// <summary>lgDeviceInit CreateSema result cell (<c>-1</c> = need create).</summary>
    public const uint LgDevSemaCell = 0x004B0408;
    /// <summary>lgDeviceInit success epilogue (v0=0, restore, jr ra).</summary>
    public const uint LgDevSuccessReturn = 0x00443C48;
    /// <summary>lgDeviceInit function entry (disasm 0x4438E0: lui/sp frame).</summary>
    public const uint LgDevEntry = 0x004438E0;
    /// <summary>
    /// Post-version LGDEV CallRpc leaf (disasm 0x443DB0): fno≠12 device push with recv size
    /// 0x240 on 0x01ECDF40 — residual thrash source after entry stub. Stub after first success.
    /// </summary>
    public const uint LgDevCallRpcLeaf = 0x00443DB0;
    /// <summary>
    /// Boot wait flag at <c>gp-23028</c> (gp=0x4E8670 → 0x4E2C7C). Live final PC 0x2B34D8:
    /// <c>while (*(gp-23028)==0) SleepThread()</c> — never leaves IRX-only without plant.
    /// </summary>
    public const uint BootWaitFlagDefault = 0x004E2C7C;
    public const int BootWaitFlagGpOff = -23028;

    private int _stableHits;
    private int _clearCount;
    private int _rearms;
    private int _sleepWakeups;
    private int _padInjectPulses;
    private int _lgDevEscapes;
    private int _menuKickPulses;
    private int _vblankExits;
    private int _bootWaitFlagPlants;
    private int _logoPadAdvances;
    private int _presentationLeaves;
    private int _sceneDeltaReports;
    private ulong _lastGifP3;
    private ulong _lastClearCyc;
    private ulong _lastRearmCyc;
    private ulong _lastSleepWakeCyc;
    private ulong _lastMenuKickCyc;
    private ulong _lastVblankExitCyc;
    private ulong _lastBootWaitPlantCyc;
    private ulong _lastLogoPadCyc;
    private ulong _logoChromeFirstCyc;
    private ulong _lastPresentationLeaveCyc;
    private ulong _lastMainWakeCyc;
    // PL-014 scene fingerprint at first Soft-GS chrome (for INTERACTIVE delta evidence).
    private ulong _chromeSnapPc;
    private long _chromeSnapPx;
    private long _chromeSnapPrims;
    private long _chromeSnapImg;
    private uint _chromeSnapFrame1;
    private bool _chromeSnapTaken;
    private bool _interactiveSceneDelta;
    private bool _flipEverUnblocked;
    private bool _versionPlanted;
    private bool _lgDevPostCleared;
    private bool _lgDevFullyDone;
    private bool _stageAssetsPlanted;
    private uint _stageHedEeAddr;
    private uint _stageHedSize;

    /// <summary>
    /// High-RDRAM scratch for STAGEHED.BIN (374784 B). Below EE stack (~0x01FF0000) and
    /// above typical game heaps used during early boot (~0x01000000..0x01800000).
    /// </summary>
    public const uint StageHedScratch = 0x01900000;

    public void Reset()
    {
        _stableHits = 0;
        _clearCount = 0;
        _rearms = 0;
        _sleepWakeups = 0;
        _padInjectPulses = 0;
        _lgDevEscapes = 0;
        _menuKickPulses = 0;
        _vblankExits = 0;
        _bootWaitFlagPlants = 0;
        _logoPadAdvances = 0;
        _presentationLeaves = 0;
        _sceneDeltaReports = 0;
        _tableWalkEscapes = 0;
        _tableWalkEscapes = 0;
        _ioQueueEscapes = 0;
        _deadEpiLeaves = 0;
        _lastGifP3 = 0;
        _lastClearCyc = 0;
        _lastRearmCyc = 0;
        _lastSleepWakeCyc = 0;
        _lastMenuKickCyc = 0;
        _lastVblankExitCyc = 0;
        _lastBootWaitPlantCyc = 0;
        _lastLogoPadCyc = 0;
        _logoChromeFirstCyc = 0;
        _lastPresentationLeaveCyc = 0;
        _lastMainWakeCyc = 0;
        _chromeSnapPc = 0;
        _chromeSnapPx = 0;
        _chromeSnapPrims = 0;
        _chromeSnapImg = 0;
        _chromeSnapFrame1 = 0;
        _chromeSnapTaken = false;
        _interactiveSceneDelta = false;
        _lastFlipLeaveCyc = 0;
        _lastIoQueueEscapeCyc = 0;
        _flipEverUnblocked = false;
        _flipWaitStubPlanted = false;
        _ioQueueStubPlanted = false;
        _versionPlanted = false;
        _lgDevPostCleared = false;
        _lgDevFullyDone = false;
        _stageAssetsPlanted = false;
        _stageHedEeAddr = 0;
        _stageHedSize = 0;
        _postTxdEscapes = 0;
        _lastPostTxdEscapeCyc = 0;
        _frontendPlanted = false;
        _frontendEeAddr = 0;
        _frontendSize = 0;
        _residualBootLeaves = 0;
        _lastResidualBootLeaveCyc = 0;
    }


    /// <summary>
    /// M8-a quiet plant half for B3 "2800" EE RAM plant (m8a-b3-dual-suppress-results.md).
    /// Default soft-off: skip PlantIopRpVersion on mount + Step (Prefer stays intentionally OFF).
    /// M4-b/M4-g tag-if-applied GetVersion supplies digits without the RAM plant at diagnose.
    /// Rollback: DETPS2_M8A_B3_NO_VERSION_PLANT=0 (or false) opts back into legacy plant.
    /// </summary>
    private static bool SkipVersionPlant
    {
        get
        {
            string? v = Environment.GetEnvironmentVariable("DETPS2_M8A_B3_NO_VERSION_PLANT");
            return v is null || !(string.Equals(v, "0", StringComparison.Ordinal) ||
                                   string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Dual-suppress Prefer hold: force PreferIopRpGetVersion=false so LITERAL_IRX auto-set
    /// cannot arm Prefer during plant-quiet seat. DETPS2_M8A_B3_HOLD_PREFER_OFF=1.
    /// </summary>
    private static bool HoldPreferOff
    {
        get
        {
            string? v = Environment.GetEnvironmentVariable("DETPS2_M8A_B3_HOLD_PREFER_OFF");
            return string.Equals(v, "1", StringComparison.Ordinal) ||
                   string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        // PreferIopRpGetVersion left OFF for residual→STG cadence (wave5/w6):
        // PreferIopRp=true advances LGDEV thrash to ~18.6M; force@pristine then residual
        // dies n=2–3 and STG never binds. EE IOPRP "2800" plant covers SifLoadModule.
        // FILEIO/LOADFILE classic 0x00020000 matches menu4 residual force@~22M FC00 window
        // when thrash is not pulled forward. Re-enable only with proven residual n≈48.
        if (!SkipVersionPlant)
            PlantIopRpVersion(sys);
        if (HoldPreferOff && sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = false;
    }

    public void OnHostPresent(Ps2System sys)
    {
        // Soft-GS: PATH3 may upload logo IMAGE under M3P; DISPFB→FB composite each present.
        sys.Gs.CompositeDispfbToFramebuffer();
        // PL-014 / MENU-B3-2: after logo Soft-GS chrome, pulse START/CROSS edges on present
        // ticks so libpad2/DBC poll sees press→release; wake main + presentation leave.
        // No DISPFB plant.
        if (LogoChromeLive(sys))
        {
            if (_logoChromeFirstCyc == 0)
                _logoChromeFirstCyc = sys.MasterCycles;
            MaybeSnapshotLogoChrome(sys);
            PulseLogoPadAdvance(sys, fromPresent: true);
            MaybeWakeMainForPad(sys);
            MaybeLeavePresentationPark(sys);
            MaybeReportSceneDelta(sys);
        }
    }

    /// <summary>Soft-GS non-black + FRONTEND/STG spine (cdvd≥2000) — logo chrome live.</summary>
    private static bool LogoChromeLive(Ps2System sys) =>
        sys.Gs.PixelsWritten > 10_000 && sys.Cdvd.SectorsRead >= 2000;

    /// <summary>
    /// Plant IOPRP 2.8.0 version tag the SifLoadModule gate compares after GetVersion.
    /// Real hardware fills this when UDNL applies DNAS280.IMG.
    /// </summary>
    public static void PlantIopRpVersion(Ps2System sys)
    {
        // Ensure the pointer cell targets the placeholder (retail does this at boot).
        uint ptr = sys.Memory.Read32(IopVersionPtrCell);
        if (ptr == 0 || ptr == 0x2E2E2E2Eu)
            sys.Memory.Write32(IopVersionPtrCell, IopVersionPlaceholder);

        uint w = sys.Memory.Read32(IopVersionPlaceholder);
        if (w == 0x2E2E2E2Eu || w == 0) // "...." or zero
        {
            // ASCII "2800"
            sys.Memory.Write8(IopVersionPlaceholder + 0, (byte)'2');
            sys.Memory.Write8(IopVersionPlaceholder + 1, (byte)'8');
            sys.Memory.Write8(IopVersionPlaceholder + 2, (byte)'0');
            sys.Memory.Write8(IopVersionPlaceholder + 3, (byte)'0');
        }
    }

    public void Step(Ps2System sys)
    {
        // Re-plant after ELF PT_LOAD (OnDiscMounted runs before BootDiscFile's ElfLoader).
        if (!_versionPlanted && sys.MasterCycles >= 500_000)
        {
            if (!SkipVersionPlant)
            {
                PlantIopRpVersion(sys);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine(
                        $"[B3] planted IOPRP version \"2800\" @ 0x{IopVersionPlaceholder:X8} cyc={sys.MasterCycles}");
            }
            else if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            {
                Console.Error.WriteLine(
                    $"[B3] skip IOPRP version plant (DETPS2_M8A_B3_NO_VERSION_PLANT) cyc={sys.MasterCycles}");
            }
            _versionPlanted = true;
        }

        // Dual-suppress Prefer hold: neutralize LITERAL_IRX Prefer auto-set side channel.
        if (HoldPreferOff && sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = false;

        // Residual lgDeviceInit assert sink (0x443A90..A4) if LGDEV RPC version was wrong.
        // Prefer RealSifRpc.HandleLgDev; this is a belt-and-suspenders escape that plants the
        // expected 0x010B1B00 at *(s0+4) and snaps to the success path at 0x443AD0.
        // Also clear post-version fno=18 thrash flag and force clean return.
        // CallRpc WaitSema thrash (PC=0x10BE64 ra=0x10F3A0) after version also forces complete.
        if (sys.MasterCycles >= 18_000_000)
            MaybeEscapeLgDeviceAssert(sys);

        // Once IRX/LGDEV is live, keep *0x4B0400=0 so lgDeviceInit never issues fno=18.
        if (!_lgDevPostCleared && sys.MasterCycles >= 20_000_000 && sys.Cdvd.SectorsRead > 0)
        {
            sys.Memory.Write32(LgDevPostFlag, 0);
            _lgDevPostCleared = true;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[B3] clear lgDeviceInit post-flag *0x{LgDevPostFlag:X8}=0 (skip fno=18) cyc={sys.MasterCycles}");
        }
        else if (_lgDevPostCleared && (sys.MasterCycles % 500_000) < 50_000)
            sys.Memory.Write32(LgDevPostFlag, 0); // sticky: game may re-set

        // menu4: kick from ~22M even before FullyDone so peer threads settle while residual
        // CallRpc runs on main. Gating solely on FullyDone left residual monopolizing EE.
        if (sys.MasterCycles >= 22_000_000 && sys.Cdvd.SectorsRead > 0)
            MaybeKickPostGtfsMenu(sys);

        // Wave-2 residual-STG: tip parks in SIF/stream/bitfield bands with IRX-only or
        // STAGEHED-plant-only cdvd (425–609) and never binds Global.txd. Leave is gated to
        // pre-STG (cdvd<2000) and known thrash PCs — not a blind snap to 0x2AF914.
        if (_lgDevFullyDone && sys.MasterCycles >= 22_000_000 && _lgDevEscapes >= 1
            && sys.Cdvd.SectorsRead is >= 400 and < 2000)
            MaybeLeaveResidualBootThrash(sys);

        // Direct flip-leave once LGDEV is done — do not depend solely on menu-kick cadence
        // (live menu14 stuck at 0x1F24E0 with re-arm only, never leave).
        if (_lgDevFullyDone && sys.MasterCycles >= 24_000_000 && sys.Cdvd.SectorsRead >= 400)
            MaybeLeaveFlipPark(sys);

        // Plant STAGEHED after residual LGDEV settled. Tip IRX-era residual dies at n=2–3
        // after force@pristine (entry+leaf stubs) — n≥48 left STAGEHED unplanted forever.
        // Wave-7: also plant once game FILEIO already advanced cdvd (≫2000).
        if (_lgDevFullyDone && sys.MasterCycles >= 28_000_000
            && (_lgDevEscapes >= 1 || sys.Cdvd.SectorsRead >= 2000)
            && sys.Cdvd.SectorsRead >= 400)
            MaybePlantStageAssets(sys);

        // Post full-TXD presentation: leave GIF flush MMIO thrash so Soft-GS can draw
        // FRONTEND/logo chrome. Does not touch residual force timing / STG bind.
        // TXD completes ~43M on deliver; do not wait until 55M to start escapes.
        if (_lgDevFullyDone && sys.Cdvd.SectorsRead >= 2000 && sys.MasterCycles >= 40_000_000)
            MaybeEscapePostTxdHang(sys);

        if (sys.MasterCycles < 16_000_000) return;
        if (sys.Gif.Path3Transfers < 4) return;
        if (_clearCount >= 128 && _rearms >= 512 && _sleepWakeups >= 64
            && _padInjectPulses >= 256 && _menuKickPulses >= 128
            && _vblankExits >= 32) return;

        uint pending = sys.Memory.Read32(PendingCountAddr) & 0xFF;
        uint qOut = sys.Memory.Read32(QueueOutAddr);
        uint qIn = sys.Memory.Read32(QueueInAddr);

        bool gifMoving = sys.Gif.Path3Transfers != _lastGifP3;
        if (gifMoving)
        {
            _lastGifP3 = sys.Gif.Path3Transfers;
            _stableHits = 0;
        }

        bool flipHealthy = pending == 0 && qOut == qIn;

        // --- Flip residual handling (only when GS is quiet and queues not healthy) ---
        if (!flipHealthy && !gifMoving)
        {
            _stableHits++;
            // Faster re-arm cadence than before: lost DMAC IRQs leave pending stuck; the
            // real consumer must run while out≠in so drain can process software packets.
            if (_stableHits >= 4 && sys.Dmac.ActiveChannelCount == 0)
            {
                if (qOut != qIn && _rearms < 512
                    && sys.MasterCycles - _lastRearmCyc >= 100_000)
                {
                    uint delta = qIn > qOut ? qIn - qOut : qOut - qIn;
                    ArmFlipConsumer(sys);
                    // pending+1: N decrements then one extra invocation with pending==0
                    // so the consumer body drains out→in (see 0x1F17EC fall-through).
                    // When pending is already 0 but out≠in, credit a single drain call.
                    int need = pending > 0 ? (int)Math.Min(pending + 1, 6u) : 1;
                    sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.VIF1, need);
                    sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.GIF, need);
                    // Do NOT force out←in. That sets out==in before pending hits 0 and the
                    // drain path early-outs — observed as infinite gifP3 growth with
                    // calls stuck at 6 / cdvdSectors=0 (B3 80M telemetry).
                    _rearms++;
                    _lastRearmCyc = sys.MasterCycles;
                    _stableHits = 0;
                    // Rearms mean the flip path is live; allow post-flip assists (wake flags).
                    if (_rearms >= 2)
                        _flipEverUnblocked = true;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                        && (_rearms <= 12 || _rearms % 32 == 0))
                        Console.Error.WriteLine(
                            $"[B3] re-arm flip consumer out=0x{qOut:X8} in=0x{qIn:X8} delta=0x{delta:X} " +
                            $"pending={pending} need={need} gifP3={sys.Gif.Path3Transfers} " +
                            $"n={_rearms} cyc={sys.MasterCycles}");
                }

                // out==in but residual pending: no drain work; clear so path-sync can exit.
                if (qOut == qIn && pending > 0 && _clearCount < 128
                    && sys.MasterCycles - _lastClearCyc >= 500_000)
                {
                    ArmFlipConsumer(sys);
                    // Prefer real IRQ path first (a few credits) before soft-clear.
                    if (_clearCount < 4)
                    {
                        sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.VIF1, (int)pending + 1);
                        sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.GIF, (int)pending + 1);
                    }
                    else
                    {
                        sys.Memory.Write8(PendingCountAddr, 0);
                    }
                    _clearCount++;
                    _lastClearCyc = sys.MasterCycles;
                    _stableHits = 0;
                    _flipEverUnblocked = true;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                        Console.Error.WriteLine(
                            $"[B3] flip pending residual (was {pending}) out=in=0x{qOut:X8} " +
                            $"gifP3={sys.Gif.Path3Transfers} n={_clearCount} cyc={sys.MasterCycles}");
                }
            }
        }
        else if (flipHealthy)
        {
            _stableHits = 0;
            // Once queues are truly healthy, keep post-flip path alive.
            if (_clearCount > 0 || _rearms > 0)
                _flipEverUnblocked = true;
        }

        // --- Post-flip progress (MUST run when flip has ever been live) ---
        if (!_flipEverUnblocked) return;

        if (_sleepWakeups < 64 && sys.MasterCycles - _lastSleepWakeCyc >= 500_000)
        {
            _lastSleepWakeCyc = sys.MasterCycles;
            var k = sys.Hle?.Kernel;
            if (k != null)
            {
                int woke = 0;
                foreach (var t in k.AllThreads)
                {
                    if (!t.Alive || !t.Started || !t.Sleeping) continue;
                    if (t.WaitSemaId != 0 || t.WaitVblank) continue;
                    k.WakeupThread(t.Id);
                    woke++;
                }
                if (woke > 0)
                {
                    _sleepWakeups++;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _sleepWakeups <= 8)
                        Console.Error.WriteLine(
                            $"[B3] wake pure SleepThread n={woke} total={_sleepWakeups} " +
                            $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
                }
            }
        }

        // VBlank wakeup flags — plant every Step once flip is healthy so SleepThread
        // poll at 0x237180 sees non-zero bytes even when EE PC sample misses the body.
        if (sys.MasterCycles >= 20_000_000)
        {
            PlantWakeFlags(sys, VblankWakeFlagBase);
            uint gp = (uint)(sys.EE.GetGpr(28).Lo & 0x1FFFFFFFUL);
            if (gp >= 0x00400000)
            {
                uint baseP = gp - 23820u;
                if (baseP != VblankWakeFlagBase && baseP is >= 0x00400000 and < 0x01000000)
                    PlantWakeFlags(sys, baseP);
            }

            // Live: flag byte at base+3 is cleared by the game between 50k-cycle Step
            // samples (s0=base+3, lbu sees 0). When EE is in the poll body, force the
            // non-zero path past beqz so boot can leave SleepThread park.
            // After many visits still stuck, snap to the function epilogue (0x2371E0).
            // Also cover prologue 0x237120..174 (final telemetry parks at 0x237124).
            // MENU-B3-2: once Soft-GS logo chrome is live + pad edges running, stop
            // heavy epilogue stomp — continuous 0x2371E0 monopolized EE (final PC stuck
            // 0x237138) and starved presentation/menu pad consumers. Plant flags only.
            uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
            if (pc is >= 0x00237120 and <= 0x0023719C)
            {
                uint s0 = (uint)(sys.EE.GetGpr(16).Lo & 0x1FFFFFFFUL);
                if (s0 is >= 0x00400000 and < 0x01000000)
                    sys.Memory.Write8(s0, 1);
                // Keep all 4 wake flags hot (s1 indexes base+0..3). Also plant base+4
                // for the s1==4 fall-through (all slots filled) which otherwise OOBs.
                PlantWakeFlags(sys, VblankWakeFlagBase);
                sys.Memory.Write8(VblankWakeFlagBase + 4, 1);
                sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = 1 }); // v1 = non-zero
                bool chromePad = LogoChromeLive(sys) && _logoPadAdvances >= 8;
                // Prefer natural fall-through (0x2371A0) so s1-indexed store runs;
                // after heavy thrash / when parked at prologue, epilogue return.
                bool allowHeavy = !chromePad
                    && (sys.Cdvd.SectorsRead >= 600 || _menuKickPulses >= 48);
                bool heavy = allowHeavy && (_sleepWakeups >= 8 || _menuKickPulses >= 16
                    || _vblankExits >= 4 || pc is >= 0x00237120 and <= 0x00237170);
                if (heavy)
                {
                    // Clamp s1 into 0..3 so success path writes a valid slot, then epilogue.
                    uint s1 = (uint)(sys.EE.GetGpr(17).Lo & 0xFFUL);
                    if (s1 > 3)
                        sys.EE.SetGpr(17, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0
                    sys.EE.PC = 0x002371E0; // ld ra / restore / jr ra
                    _vblankExits++;
                }
                else if (!chromePad)
                    sys.EE.PC = 0x002371A0; // past beq delay — success body
                // chromePad: flags only — let lbu see non-zero and leave SleepThread naturally
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (sys.MasterCycles - _lastVblankExitCyc >= 5_000_000 || _vblankExits <= 4))
                {
                    _lastVblankExitCyc = sys.MasterCycles;
                    Console.Error.WriteLine(
                        $"[B3] force VBlank wakeup exit pc was 0x{pc:X8} s0=0x{s0:X8} " +
                        $"heavy={heavy} chromePad={chromePad} n={_vblankExits} cyc={sys.MasterCycles}");
                }
            }
        }

        // Pad inject once disc assets stream — menu probes need non-zero buttons.
        // Wave-5/6: raise cap after Soft-GS logo chrome.
        // PL-014: after logo chrome, prefer edge pulse (press/release) + DBC work refresh
        // so Criterion libpad2 sees START/CROSS transitions (not level-hold thrash).
        if (LogoChromeLive(sys))
        {
            if (_logoChromeFirstCyc == 0)
                _logoChromeFirstCyc = sys.MasterCycles;
            MaybeSnapshotLogoChrome(sys);
            PulseLogoPadAdvance(sys, fromPresent: false);
            MaybeWakeMainForPad(sys);
            MaybeLeavePresentationPark(sys);
            MaybeReportSceneDelta(sys);
            return;
        }

        int padCap = sys.Gs.PixelsWritten > 0 && sys.Cdvd.SectorsRead >= 2000 ? 1024 : 256;
        if (sys.Cdvd.SectorsRead > 0 && _padInjectPulses < padCap)
        {
            _padInjectPulses++;
            int phase = _padInjectPulses % 10;
            uint buttons = 0;
            if (phase is 2 or 3) buttons = (uint)PadInput.Button.Start;
            else if (phase is 5 or 6) buttons = (uint)PadInput.Button.Cross;
            else if (phase == 7) buttons = (uint)PadInput.Button.Circle;
            else if (phase == 8) buttons = (uint)PadInput.Button.Down;
            else if (phase == 9) buttons = (uint)PadInput.Button.Up;
            try { sys.Pad.SetButtons(buttons); } catch { /* Pad may be null early */ }
        }
    }

    /// <summary>
    /// PL-014 logo→frontend pad advance: edge-based START/CROSS/Circle/D-pad with explicit
    /// release frames, DualShock analog mode, and ForceRefreshPad / DBC work paint so
    /// libpad2 (PsIIlibpad2 2800) + DBCMAN polls see host buttons. No invented DISPFB.
    /// MENU-B3-2: yield to pad-script external presses; denser long START holds after chrome.
    /// </summary>
    private void PulseLogoPadAdvance(Ps2System sys, bool fromPresent)
    {
        // Present ticks are rare (~1M); Step may fire every ~50k — rate-limit Step path.
        // After chrome live ≥8M, faster edges so skip-logo detectors see more transitions.
        ulong chromeAge = _logoChromeFirstCyc != 0
            ? sys.MasterCycles - _logoChromeFirstCyc : 0UL;
        ulong minGap = fromPresent ? 0UL
            : chromeAge >= 8_000_000 ? 40_000UL : 80_000UL;
        if (!fromPresent && sys.MasterCycles - _lastLogoPadCyc < minGap) return;
        if (_logoPadAdvances >= 8192) return;
        _lastLogoPadCyc = sys.MasterCycles;
        _logoPadAdvances++;
        _padInjectPulses++;

        try { sys.Pad.AnalogMode = true; } catch { /* ignore */ }

        // Yield to pad-script / external Press: do not clobber non-zero host buttons.
        // Still refresh DBC work so libpad2 sees whatever is currently held.
        bool externalHold = false;
        try { externalHold = sys.Pad.Buttons != 0; } catch { /* ignore */ }

        uint buttons = 0;
        if (!externalHold)
        {
            // 16-phase edge train: press windows with clear zeros between so edge detectors fire.
            // After chrome ≥5M: longer START holds (phases 0-2, 12-14) for skip-logo / press-start.
            int phase = _logoPadAdvances % 16;
            bool longStart = chromeAge >= 5_000_000;
            buttons = phase switch
            {
                0 or 1 => (uint)PadInput.Button.Start,
                2 => longStart ? (uint)PadInput.Button.Start : 0u,
                3 or 4 => (uint)PadInput.Button.Cross,
                5 => 0u,
                6 => (uint)(PadInput.Button.Start | PadInput.Button.Cross),
                7 => 0u,
                8 => (uint)PadInput.Button.Circle,
                9 => 0u,
                10 => (uint)PadInput.Button.Down,
                11 => 0u,
                12 => (uint)PadInput.Button.Up,
                13 => longStart ? (uint)PadInput.Button.Start : 0u,
                14 => (uint)PadInput.Button.Start,
                _ => 0u
            };
            // Every 32nd edge after chrome ≥10M: hard START+CROSS chord (menu accept class).
            if (longStart && chromeAge >= 10_000_000 && (_logoPadAdvances % 32) < 4)
                buttons = (uint)(PadInput.Button.Start | PadInput.Button.Cross);

            try { sys.Pad.SetButtons(buttons); } catch { /* Pad may be null early */ }
        }
        else
        {
            try { buttons = sys.Pad.Buttons; } catch { /* ignore */ }
        }

        // B3 uses libdbc/DBCMAN+DS2O — no PADMAN OPEN. ForceRefreshPad now also paints
        // the captured DBC work buffer (SetWorkAddr/create) so EE pad2Read sees buttons
        // between poll RPCs (pad path is EE-side DMA, same class as PADMAN TickPadDma).
        var rpc = sys.Hle?.Sony?.RealRpc;
        try
        {
            rpc?.ForceRefreshPad(sys.Memory, sys.Pad);
            // Explicit DBC refresh even if PADMAN map empty (ForceRefresh covers both).
            rpc?.ForceRefreshDbcPad(sys.Memory, sys.Pad);
        }
        catch { /* ignore */ }

        // PL-014 residual: if DBC work still unset after create thrash, probe EE client
        // scratch near live B3 recvBuf 0x00679840 / bind cds for a SetWorkAddr pointer.
        if (rpc != null && rpc.DbcWorkAddr == 0 && _logoPadAdvances is >= 4 and <= 32)
            TryDiscoverDbcWorkNearClient(sys, rpc);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_logoPadAdvances <= 8 || _logoPadAdvances % 64 == 0))
        {
            uint work = rpc?.DbcWorkAddr ?? 0;
            int paints = rpc?.DbcPadPaintCount ?? 0;
            Console.Error.WriteLine(
                $"[B3] PL-014 logo-pad edge n={_logoPadAdvances} btn=0x{buttons:X4} " +
                $"ext={externalHold} present={fromPresent} px={sys.Gs.PixelsWritten} " +
                $"cdvd={sys.Cdvd.SectorsRead} dbcWork=0x{work:X8} paints={paints} " +
                $"pc=0x{(uint)(sys.EE.PC & 0x1FFFFFFFUL):X8} cyc={sys.MasterCycles}");
        }
    }

    /// <summary>
    /// Snapshot Soft-GS/PC once FRONTEND spine is live (cdvd≥2000 + px>10k) so
    /// INTERACTIVE scene-delta measures pad-era change, not early boot strip paint.
    /// </summary>
    private void MaybeSnapshotLogoChrome(Ps2System sys)
    {
        if (_chromeSnapTaken) return;
        // Prefer post-FRONTEND plant snap (cdvd≥6000); fall back after chrome live ≥12M.
        bool frontendEra = sys.Cdvd.SectorsRead >= 6000;
        bool aged = _logoChromeFirstCyc != 0
            && sys.MasterCycles - _logoChromeFirstCyc >= 12_000_000;
        if (!frontendEra && !aged) return;
        _chromeSnapTaken = true;
        _chromeSnapPc = sys.EE.PC & 0x1FFFFFFFUL;
        _chromeSnapPx = sys.Gs.PixelsWritten;
        _chromeSnapPrims = sys.Gs.PrimitivesDrawn;
        _chromeSnapImg = sys.Gs.ImageBytesWritten;
        _chromeSnapFrame1 = (uint)(sys.Gs.Registers.FRAME_1 & 0xFFFFFFFFUL);
        Console.Error.WriteLine(
            $"[B3] PL-014 chrome snap pc=0x{_chromeSnapPc:X8} px={_chromeSnapPx} " +
            $"prims={_chromeSnapPrims} img={_chromeSnapImg} FRAME1=0x{_chromeSnapFrame1:X} " +
            $"cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// After logo chrome, keep main (tid=1) runnable so presentation/menu pad consumers
    /// can poll DBC work. MENU-B3 claim left main pure-Sleeping while tid=3 monopolized
    /// VBlank park — pad edges painted but nobody advanced logo state.
    /// </summary>
    private void MaybeWakeMainForPad(Ps2System sys)
    {
        if (sys.MasterCycles - _lastMainWakeCyc < 200_000) return;
        _lastMainWakeCyc = sys.MasterCycles;
        var k = sys.Hle?.Kernel;
        if (k == null) return;
        foreach (var t in k.AllThreads)
        {
            if (!t.Alive || t.Id != 1) continue;
            if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                k.WakeupThread(t.Id);
            else if (t.Sleeping && t.WaitSemaId >= 32)
            {
                try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
            }
            while (t.SuspendCount > 0)
                k.ResumeThread(t.Id);
            if (t.SoftSuspended) t.SoftSuspended = false;
        }
    }

    /// <summary>
    /// Presentation leave assist (MENU-B3-2 / PL-014 residual): after Soft-GS chrome + pad
    /// edges, if EE is stuck in VBlank wakeup (0x2371xx) or logo draw band (0x253Fxx) with
    /// healthy flip queues, nudge past the park so logo→menu state can advance.
    /// No DISPFB plant; no invented Soft-GS pixels.
    /// </summary>
    private void MaybeLeavePresentationPark(Ps2System sys)
    {
        if (!LogoChromeLive(sys)) return;
        if (_logoPadAdvances < 16) return;
        if (_logoChromeFirstCyc == 0 || sys.MasterCycles - _logoChromeFirstCyc < 3_000_000) return;
        if (sys.MasterCycles - _lastPresentationLeaveCyc < 500_000) return;
        if (_presentationLeaves >= 256) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        bool inVblankPark = pc is >= 0x00237120 and <= 0x002371E8;
        bool inLogoDraw = pc is >= 0x00253F00 and <= 0x00254080
            || pc is >= 0x00253A00 and <= 0x00254200;
        bool inFlipWait = pc is >= 0x001F24E0 and <= 0x001F251C;
        if (!inVblankPark && !inLogoDraw && !inFlipWait) return;

        uint pending = sys.Memory.Read32(PendingCountAddr) & 0xFF;
        uint qOut = sys.Memory.Read32(QueueOutAddr);
        uint qIn = sys.Memory.Read32(QueueInAddr);
        bool queuesHealthy = qOut == qIn && pending == 0;

        _lastPresentationLeaveCyc = sys.MasterCycles;
        _presentationLeaves++;

        // Prefer resume at healthy $ra (outside park bands) so presentation state machine
        // continues; fall back to flip drain or VBlank epilogue.
        // Live freeze: $ra collapses to 0x200 mid logo-draw (0x253F64) after ~85M — plant a
        // presentation-graph continue (0x223228 after pad-era beq @0x223224) so jr ra cannot
        // jump to null page. No DISPFB plant.
        bool deadRa = ra < 0x00100000 || ra >= 0x00400000 || !sys.Memory.IsLikelyEeCode(ra)
            || ra is (>= 0x00237120 and <= 0x002371E8)
            || ra is (>= 0x001F24E0 and <= 0x001F2520);
        if (deadRa && (inLogoDraw || inFlipWait || inVblankPark))
        {
            const uint presentationContinue = 0x00223228u; // delay-slot fall-through after 0x223224
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = presentationContinue });
            ra = presentationContinue;
        }

        uint resume = 0;
        if (ra is >= 0x00100000 and < 0x00400000 && sys.Memory.IsLikelyEeCode(ra)
            && ra is not (>= 0x00237120 and <= 0x002371E8)
            && ra is not (>= 0x001F24E0 and <= 0x001F2520)
            && ra is not (>= 0x00253F00 and <= 0x00254200))
            resume = ra;
        else if (inFlipWait)
            resume = 0x001F2520u;
        else if (inVblankPark)
            resume = 0x002371E0u; // epilogue once per cadence (not every Step)
        // Logo draw with dead ra fixed above: hop to presentation continue so pad can advance.
        else if (inLogoDraw && (deadRa || !queuesHealthy || _presentationLeaves >= 4))
            resume = 0x00223228u;

        if (resume != 0)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = resume;
            sys.EE.COP0_Status &= ~0x6u;
        }

        ArmFlipConsumer(sys);
        PlantWakeFlags(sys, VblankWakeFlagBase);
        MaybeWakeMainForPad(sys);

        // Dense START while leaving presentation — skip-logo class.
        try
        {
            sys.Pad.AnalogMode = true;
            sys.Pad.SetButtons((uint)PadInput.Button.Start);
            sys.Hle?.Sony?.RealRpc?.ForceRefreshDbcPad(sys.Memory, sys.Pad);
        }
        catch { /* ignore */ }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_presentationLeaves <= 12 || _presentationLeaves % 16 == 0))
            Console.Error.WriteLine(
                $"[B3] PL-014 presentation leave pc=0x{pc:X8} ra=0x{ra:X8} " +
                $"-> 0x{resume:X8} n={_presentationLeaves} pad={_logoPadAdvances} " +
                $"px={sys.Gs.PixelsWritten} prims={sys.Gs.PrimitivesDrawn} " +
                $"cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Log Soft-GS / PC scene change after pad edges relative to chrome snapshot.
    /// INTERACTIVE evidence = PC left presentation/VBlank park band OR prims/img/FRAME delta.
    /// </summary>
    private void MaybeReportSceneDelta(Ps2System sys)
    {
        if (!_chromeSnapTaken || _logoPadAdvances < 32) return;
        if (_sceneDeltaReports >= 8 && _interactiveSceneDelta) return;
        if (_logoPadAdvances % 64 != 0 && _sceneDeltaReports > 0) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        long px = sys.Gs.PixelsWritten;
        long prims = sys.Gs.PrimitivesDrawn;
        long img = sys.Gs.ImageBytesWritten;
        uint frame1 = (uint)(sys.Gs.Registers.FRAME_1 & 0xFFFFFFFFUL);

        bool pcLeftPark = !IsPresentationParkPc(pc)
            && IsPresentationParkPc((uint)_chromeSnapPc);
        bool pcMoved = (pc & ~0xFFu) != ((uint)_chromeSnapPc & ~0xFFu);
        bool primsMoved = prims > _chromeSnapPrims + 200;
        bool imgMoved = img > _chromeSnapImg + 64_000;
        bool frameMoved = frame1 != 0 && frame1 != _chromeSnapFrame1;
        bool scene = pcLeftPark || (pcMoved && (primsMoved || imgMoved || frameMoved));

        if (scene)
            _interactiveSceneDelta = true;

        _sceneDeltaReports++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" || scene)
            Console.Error.WriteLine(
                $"[B3] PL-014 scene-delta scene={scene} interactive={_interactiveSceneDelta} " +
                $"pc=0x{_chromeSnapPc:X8}->0x{pc:X8} leftPark={pcLeftPark} " +
                $"prims={_chromeSnapPrims}->{prims} img={_chromeSnapImg}->{img} " +
                $"FRAME1=0x{_chromeSnapFrame1:X}->0x{frame1:X} " +
                $"pad={_logoPadAdvances} px={px} cyc={sys.MasterCycles}");
    }

    private static bool IsPresentationParkPc(uint pc) =>
        pc is (>= 0x00237120 and <= 0x002371E8)
            or (>= 0x00253A00 and <= 0x00254200)
            or (>= 0x001F24E0 and <= 0x001F2520)
            or (>= 0x00228040 and <= 0x00228070)
            or (>= 0x00223200 and <= 0x00223300)   // pad-era flip/helper (live 0x223224)
            or (>= 0x0010BE60 and <= 0x0010BE70);  // WaitSema park

    /// <summary>
    /// Scan B3 libdbc client region for an EE work-buffer pointer if RPC capture missed it.
    /// Live: bind cds @0x6797C8/F0/818, recv scratch @0x679840. Only adopts 64-align
    /// RDRAM pointers outside flip-queue / stack. No DISPFB plant.
    /// </summary>
    private static void TryDiscoverDbcWorkNearClient(Ps2System sys, RealSifRpc rpc)
    {
        if (rpc.DbcWorkAddr != 0) return;
        // Probe a few fixed B3 client / socket neighborhoods observed under TRACE_RPC.
        ReadOnlySpan<uint> bases = stackalloc uint[]
        {
            0x006797C0u, 0x00679840u, 0x00679880u, 0x00679900u,
            0x0067A000u, 0x004E4000u, 0x004E6000u, 0x01C00000u
        };
        foreach (uint b in bases)
        {
            for (uint off = 0; off < 0x100; off += 4)
            {
                uint cand = sys.Memory.Read32(b + off) & 0x1FFFFFFFu;
                if (cand is < 0x00400000u or >= 0x01F00000u) continue;
                if ((cand & 0x3Fu) != 0) continue;
                if (cand is >= 0x004E2800u and < 0x004E2A00u) continue;
                if (cand is >= 0x00679000u and < 0x0067B000u) continue; // self-ref client
                if (sys.Memory.IsLikelyEeCode(cand)) continue;
                if (rpc.TryRegisterDbcWorkAddr(cand))
                {
                    rpc.ForceRefreshDbcPad(sys.Memory, sys.Pad);
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                        Console.Error.WriteLine(
                            $"[B3] PL-014 DBC work discovered via client scan " +
                            $"base=0x{b:X8}+{off:X} work=0x{cand:X8} paints={rpc.DbcPadPaintCount}");
                    return;
                }
            }
        }
    }

    private static void PlantWakeFlags(Ps2System sys, uint baseP)
    {
        // Unconditional: live dump showed base+0..2 stay 1 but base+3 is repeatedly
        // zeroed by the game between Step samples (s0=base+3, s1=3). Force all four.
        for (uint i = 0; i < 4; i++)
            sys.Memory.Write8(baseP + i, 1);
    }

    private static void ArmFlipConsumer(Ps2System sys)
    {
        sys.Intc.SetMask(sys.Intc.Mask | (1u << (int)Intc.InterruptSource.DmaController));
        sys.Dmac.EnableChannelIrq((int)Dmac.Channel.VIF1);
        sys.Dmac.EnableChannelIrq((int)Dmac.Channel.GIF);
    }

    /// <summary>
    /// Burnout 3 <c>lgDeviceInit</c> assert / thrash:
    /// <list type="bullet">
    /// <item><c>0x443A90</c>: infinite <c>beq</c> after wrong version — plant
    ///   <c>0x010B1B00</c> and continue at <c>0x443AD0</c>.</item>
    /// <item><c>0x443B38</c>: CreateSema failed assert.</item>
    /// <item>Post-version fno=18 CallRpc thrash — clear <see cref="LgDevPostFlag"/> and
    ///   force clean return at <see cref="LgDevSuccessReturn"/>.</item>
    /// </list>
    /// </summary>
    private void MaybeEscapeLgDeviceAssert(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);

        // Sticky: after first clean exit, keep lgDeviceInit a no-op. Residual CallRpc on
        // the LGDEV client (leaf 0x443DB0): complete and stub the leaf so fno≠12 cannot
        // re-enter cid=0 thrash. Prefer epilogue 0x443D94 over re-entry into CallRpc.
        // Wave-4: fix dead $ra at leaf delay-slot 0x443DA8 (parent post-jal). Residual
        // only on main high-stack (menu4 stable FC10); rewrite whole LGDEV .text savedRa.
        if (_lgDevFullyDone)
        {
            // Deliver residual→STG plants entry+leaf stubs at force (n=1) and still binds STG.
            // Wave-8 delayed stubs to n≥24 + force@22M and tip residual died n=2–3 (cdvd=425).
            // Restore deliver: keep stubs sticky once FullyDone so fno≠12 cannot re-thrash.
            PlantLgDevEntryStub(sys);
            PlantLgDevCallRpcLeafStub(sys);
            sys.Memory.Write32(LgDevPostFlag, 0);

            // 0x443DA8 = delay slot of leaf jr ra (ld ra; jr ra; addiu sp,48). Dead $ra
            // → parent post-jal 0x4427FC and re-issue jr. Do NOT hard-leave mid-body.
            if (pc == 0x00443DA8u)
            {
                bool badRa = ra < 0x00100000 || ra >= 0x00400000
                    || ra is (>= 0x00443800 and < 0x00445000)
                    || ra is (>= 0x00443D80 and <= 0x00443DB0);
                if (badRa)
                {
                    sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = 0x004427FCu });
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.PC = 0x00443DA4; // jr ra
                    sys.EE.COP0_Status &= ~(1u << 1);
                    _lgDevEscapes++;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                        && (_lgDevEscapes <= 12 || _lgDevEscapes % 16 == 0))
                        Console.Error.WriteLine(
                            $"[B3] fix leaf jr-ra delay pc=0x443DA8 ra was 0x{ra:X8} " +
                            $"-> ra=0x4427FC n={_lgDevEscapes} cyc={sys.MasterCycles}");
                }
                return;
            }

            if (IsLgDevCallRpcThrash(sys, pc, ra) && _lgDevEscapes < 256)
            {
                // After STG/game FILEIO, stop faking LGDEV residual CallRpc — live tip
                // re-entered n→32 @92–99M and monopolized EE after full FRONTEND DMA.
                if (sys.Cdvd.SectorsRead >= 600)
                    return;
                uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
                // menu4 residual: main high-stack (FC10). Also allow mid-high frames that
                // share the CallRpc leaf shape; skip pure worker 0x01EDxxxx parks.
                if (sp is >= 0x01FFF000 and < 0x02000000)
                {
                    uint savedRa = sys.Memory.Read32(sp + 176) & 0x1FFFFFFFu;
                    // menu4 rewrite window (pre-401dbbb): leaf body + bad + CallRpc + init.
                    if (savedRa is >= 0x00443D00 and <= 0x00443DAC
                        || savedRa is < 0x00100000 or >= 0x00800000
                        || savedRa is (>= 0x0010BE00 and <= 0x0010F400)
                        || savedRa is (>= 0x004438E0 and <= 0x00443C6C)
                        || savedRa is >= 0x00443800 and < 0x00445000)
                    {
                        sys.Memory.Write32(sp + 176, 0x00443D94u);
                        sys.Memory.Write32(sp + 180, 0);
                        savedRa = 0x00443D94u;
                    }
                    // Plant leaf frame $ra after CallRpc epi pops 192 (delay-slot 0x443DA8).
                    uint leafSp = sp + 192;
                    if (leafSp is >= 0x01FFF000 and < 0x02000000)
                    {
                        sys.Memory.Write32(leafSp + 40, 0x004427FCu);
                        sys.Memory.Write32(leafSp + 44, 0);
                    }
                    // After menu4 residual window (~48), complete CallRpc to parent post-jal
                    // so residual cannot monopolize EE (HEAD: n→256 WaitSema, no STG).
                    if (_lgDevEscapes >= 47)
                    {
                        sys.Memory.Write32(sp + 176, 0x004427FCu);
                        sys.Memory.Write32(sp + 180, 0);
                        savedRa = 0x004427FCu;
                        PlantLgDevEntryStub(sys);
                        PlantLgDevCallRpcLeafStub(sys);
                    }
                    sys.Memory.Write32(0x01ECDF00, 0);
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.PC = 0x0010F3A8;
                    sys.EE.COP0_Status &= ~(1u << 1);
                    _lgDevEscapes++;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                        && (_lgDevEscapes <= 8 || _lgDevEscapes % 16 == 0))
                        Console.Error.WriteLine(
                            $"[B3] residual LGDEV CallRpc complete pc=0x{pc:X8} sp=0x{sp:X8} " +
                            $"savedRa=0x{savedRa:X8} n={_lgDevEscapes} cyc={sys.MasterCycles}");
                }
            }
            // Deep LGDEV body after bad residual return — snap to parent post-jal.
            else if (pc is >= 0x00443E00 and < 0x00445000 && _lgDevEscapes >= 2)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = 0x004427FCu;
                sys.EE.COP0_Status &= ~(1u << 1);
                _lgDevEscapes++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_lgDevEscapes <= 12 || _lgDevEscapes % 16 == 0))
                    Console.Error.WriteLine(
                        $"[B3] leave deep LGDEV pc=0x{pc:X8} -> 0x4427FC n={_lgDevEscapes} " +
                        $"cyc={sys.MasterCycles}");
            }
            return;
        }

        if (_lgDevEscapes >= 48) return;

        // Already in success epilogue — let jr ra finish; mark done once past it.
        if (pc is >= 0x00443C48 and <= 0x00443C6C)
        {
            if (pc >= 0x00443C64)
            {
                _lgDevFullyDone = true;
                PlantLgDevEntryStub(sys);
            }
            return;
        }

        // Always suppress the fno=18 post path while in/near lgDeviceInit.
        sys.Memory.Write32(LgDevPostFlag, 0);

        // Version-assert sink.
        if (pc is >= 0x00443A90 and <= 0x00443AA8)
        {
            uint s0 = (uint)(sys.EE.GetGpr(16).Lo & 0x1FFFFFFFUL);
            if (s0 is >= 0x00100000 and < 0x02000000)
            {
                sys.Memory.Write32(s0 + 0, 0);
                sys.Memory.Write32(s0 + 4, RealSifRpc.LgDevVersion_1_11_027);
            }
            ForceLgDevSuccess(sys, pc, "version-assert");
            return;
        }

        // CreateSema-fail assert (0x443B38 infinite nop loop).
        if (pc is >= 0x00443B38 and <= 0x00443B54)
        {
            ForceLgDevSuccess(sys, pc, "CreateSema-assert");
            return;
        }

        // Mid-init body after version (0x443AD0..0x443C44).
        if (pc is >= 0x00443AD0 and <= 0x00443C44)
        {
            ForceLgDevSuccess(sys, pc, "mid-init");
            return;
        }

        // CallRpc WaitSema thrash after LGDEV version: PC at WaitSema, ra inside CallRpc
        // (0x10F3A0). Live: dest-climbing cid=0 SIFCMD on recv 0x01ECDF40.
        // Force the *entire* lgDeviceInit success epilogue so boot leaves the wheel path.
        // Only when CallRpc's s1 (cd) is the LGDEV client at 0x01ECDF00 — never other RPCs.
        //
        // Cadence (menu4 residual→STG):
        //   force @ first thrash with sp@0x01FFFC00..FC20 (pristine) from ≥18M, else ≥22.5M.
        // PreferIopRp makes thrash arrive ~18–19M; waiting to 22.5M climbs sp (FC10→FC70)
        // and residual dies n=2–4. menu4 without early thrash forced ~22.75M @ FC00.
        if (IsLgDevCallRpcThrash(sys, pc, ra) && _lgDevEscapes < 256)
        {
            uint s1 = (uint)(sys.EE.GetGpr(17).Lo & 0x1FFFFFFFUL);
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
            if (sp is < 0x01FFF000 or >= 0x02000000)
                return;
            bool pristine = sp is >= 0x01FFFC00 and <= 0x01FFFC20;
            // Deliver residual→STG: force at first pristine thrash ≥18M (live ~19.65M @ FC00).
            // Wave-8 delayed to ≥22M and tip residual died n=2–3 with UnknownOpcode thrash.
            bool forceNow = (pristine && sys.MasterCycles >= 18_000_000)
                            || sys.MasterCycles >= 22_500_000;
            if (!forceNow)
                return;
            // Permanent structural break of fno=18 path.
            if (sys.Memory.Read32(0x00443C3C) != 0)
                sys.Memory.Write32(0x00443C3C, 0); // nop jal CallRpc fno=18
            // Complete CallRpc cleanly: rewrite CallRpc's saved $ra (sd ra,176(sp)) to
            // lgDeviceInit's post-fno18 clear (0x443C44), then run the real CallRpc
            // success epilogue at 0x10F3A8 (v0=0, restore, jr ra, sp+=192).
            sys.Memory.Write32(0x01ECDF00, 0);
            sys.Memory.Write32(LgDevPostFlag, 0);
            if (unchecked((int)sys.Memory.Read32(LgDevSemaCell)) < 0)
                sys.Memory.Write32(LgDevSemaCell, 1);
            if (sys.Memory.Read32(0x00443C3C) != 0)
                sys.Memory.Write32(0x00443C3C, 0);
            sys.Memory.Write32(0x00443C20, 0x08110F11u); // j 0x443C44 sticky
            sys.Memory.Write32(0x00443C24, 0);
            // CallRpc: ld ra,176(sp) at 0x10F3AC — plant return into lgDeviceInit clear.
            sys.Memory.Write32(sp + 176, 0x00443C44);
            sys.Memory.Write32(sp + 180, 0);
            // Leaf frame under CallRpc (sp+192) so residual delay-slot has parent $ra.
            uint leafSp = sp + 192;
            if (leafSp is >= 0x01FFF000 and < 0x02000000)
            {
                sys.Memory.Write32(leafSp + 40, 0x004427FCu);
                sys.Memory.Write32(leafSp + 44, 0);
            }
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = 0x0010F3A8;
            sys.EE.COP0_Status &= ~(1u << 1);
            _lgDevEscapes++;
            _lgDevFullyDone = true;
            // Deliver plants stubs at force; residual still completes 1–2 in-flight CallRpcs
            // then STG binds. Do not delay stubs (wave-8 n≥24 broke tip residual→STG).
            PlantLgDevEntryStub(sys);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[B3] force CallRpc→lgDev epilogue pc=0x{pc:X8} sp=0x{sp:X8} s1=0x{s1:X8} " +
                    $"ra*=0x443C44 pristine={pristine} n={_lgDevEscapes} cyc={sys.MasterCycles}");
        }
    }

    /// <summary>
    /// Permanent no-op stub for <see cref="LgDevEntry"/> so residual callers cannot
    /// re-enter fno=12/18 CallRpc thrash after the first successful init.
    /// </summary>
    private static void PlantLgDevEntryStub(Ps2System sys)
    {
        // jr ra ; move v0, zero  (delay-slot success)
        if (sys.Memory.Read32(LgDevEntry) != 0x03E00008u)
        {
            sys.Memory.Write32(LgDevEntry + 0, 0x03E00008u); // jr ra
            sys.Memory.Write32(LgDevEntry + 4, 0x0000102Du); // daddu v0, zero, zero
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[B3] plant lgDeviceInit entry stub @ 0x{LgDevEntry:X8} cyc={sys.MasterCycles}");
        }
        PlantLgDevCallRpcLeafStub(sys);
        sys.Memory.Write32(LgDevPostFlag, 0);
        sys.Memory.Write32(0x00443C20, 0x08110F11u); // j 0x443C44 sticky
        sys.Memory.Write32(0x00443C24, 0);
        if (sys.Memory.Read32(0x00443C3C) != 0)
            sys.Memory.Write32(0x00443C3C, 0);
    }

    /// <summary>
    /// Permanent no-op for post-version LGDEV CallRpc leaf at <see cref="LgDevCallRpcLeaf"/>
    /// (device-table push, size 0x240). Live residual thrash returns to 0x443D48 forever.
    /// </summary>
    private static void PlantLgDevCallRpcLeafStub(Ps2System sys)
    {
        if (sys.Memory.Read32(LgDevCallRpcLeaf) == 0x03E00008u) return;
        sys.Memory.Write32(LgDevCallRpcLeaf + 0, 0x03E00008u); // jr ra
        sys.Memory.Write32(LgDevCallRpcLeaf + 4, 0x0000102Du); // daddu v0, zero, zero
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[B3] plant LGDEV CallRpc leaf stub @ 0x{LgDevCallRpcLeaf:X8} cyc={sys.MasterCycles}");
    }

    private static bool IsLgDevCallRpcThrash(Ps2System sys, uint pc, uint ra)
    {
        bool inCallRpcWait = (pc is >= 0x0010BE60 and <= 0x0010BE68 && ra is >= 0x0010F380 and <= 0x0010F3B0)
            || (pc is >= 0x0010F1E8 and <= 0x0010F3A4)
            || (pc is >= 0x0010BE60 and <= 0x0010BE68
                && sys.Memory.Read32(0x01ECDF44) == RealSifRpc.LgDevVersion_1_11_027
                && (uint)(sys.EE.GetGpr(17).Lo & 0x1FFFFFFFUL) == 0x01ECDF00);
        if (!inCallRpcWait) return false;
        uint s1 = (uint)(sys.EE.GetGpr(17).Lo & 0x1FFFFFFFUL);
        uint verWord = sys.Memory.Read32(0x01ECDF44);
        return s1 == 0x01ECDF00
            || (verWord == RealSifRpc.LgDevVersion_1_11_027 && s1 is >= 0x01ECD000 and <= 0x01ECE000);
    }

    private void ForceLgDevSuccess(Ps2System sys, uint fromPc, string why)
    {
        sys.Memory.Write32(LgDevPostFlag, 0);
        if (unchecked((int)sys.Memory.Read32(LgDevSemaCell)) < 0)
            sys.Memory.Write32(LgDevSemaCell, 1);
        // Permanent skip of fno=18 setup body.
        sys.Memory.Write32(0x00443C20, 0x08110F11u); // j 0x00443C44
        sys.Memory.Write32(0x00443C24, 0x00000000u);
        if (sys.Memory.Read32(0x00443C3C) != 0)
            sys.Memory.Write32(0x00443C3C, 0);
        // Ensure version recv looks healthy if s0 still points at it.
        uint s0 = (uint)(sys.EE.GetGpr(16).Lo & 0x1FFFFFFFUL);
        if (s0 is >= 0x00100000 and < 0x02000000)
        {
            sys.Memory.Write32(s0 + 0, 0);
            sys.Memory.Write32(s0 + 4, RealSifRpc.LgDevVersion_1_11_027);
        }
        sys.Memory.Write32(0x01ECDF40, 0);
        sys.Memory.Write32(0x01ECDF44, RealSifRpc.LgDevVersion_1_11_027);
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.PC = LgDevSuccessReturn;
        sys.EE.COP0_Status &= ~(1u << 1);
        _lgDevEscapes++;
        _lgDevFullyDone = true;
        PlantLgDevEntryStub(sys);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[B3] force lgDeviceInit complete ({why}) pc=0x{fromPc:X8} " +
                $"-> 0x{LgDevSuccessReturn:X8} sp=0x{(uint)sys.EE.GetGpr(29).Lo:X8} n={_lgDevEscapes} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Post-GTFS/LGDEV: wake pure SleepThread workers and inject pad so Criterion boot can
    /// stream assets and observe START/CROSS for the front-end menu.
    /// After LGDEV thrash is broken, also re-start unstarted peers and leave the pure
    /// VBlank park so FILEIO/NCMD game opens can progress (cdvd must rise past IRX-only 425).
    /// </summary>
    private void MaybeKickPostGtfsMenu(Ps2System sys)
    {
        if (sys.MasterCycles - _lastMenuKickCyc < 100_000) return;
        _lastMenuKickCyc = sys.MasterCycles;
        if (_menuKickPulses >= 2048) return;
        _menuKickPulses++;

        // Only Wake pure SleepThread / Suspend — never SignalSema on RPC WaitSema ids.
        // Blind SignalSema races sceSifCallRpc completion and was observed as CreateSema/
        // WaitSema thrash (sema ids climbing past 0x500, 60k+ WaitSema syscalls @ 100M).
        var k = sys.Hle?.Kernel;
        if (k != null)
        {
            foreach (var t in k.AllThreads)
            {
                if (!t.Alive) continue;
                // Main (id=1) only — live menu17 peer re-start left garbage WaitSemaIds
                // (e.g. 1176047169) and exception thrash. Only re-start main.
                if (!t.Started && t.Entry != 0 && t.Id == 1 && _lgDevEscapes >= 2
                    && sys.Cdvd.SectorsRead >= 400)
                {
                    try
                    {
                        k.StartAndMaybeSwitch(sys.EE, t.Id, switchNow: false, arg: 0, fromSyscall: false);
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                            && _menuKickPulses <= 16)
                            Console.Error.WriteLine(
                                $"[B3] re-start main tid=1 entry=0x{t.Entry:X8} cyc={sys.MasterCycles}");
                    }
                    catch { /* ignore */ }
                }
                if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                    k.WakeupThread(t.Id);
                // Also wake pure Sleep on unstarted-but-alive main after LGDEV.
                if (t.Id == 1 && t.Sleeping && t.WaitSemaId == 0)
                    k.WakeupThread(t.Id);
                while (t.SuspendCount > 0)
                    k.ResumeThread(t.Id);
                if (t.SoftSuspended) t.SoftSuspended = false;
            }
        }

        // Residual WaitSema(3) poll loop at 0x10CB68 (SIF cmd queue) after LGDEV —
        // fabricate is already attempted by kernel; ensure flag wake for VBlank peers.
        uint pcNow = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (_lgDevFullyDone && pcNow is >= 0x00237120 and <= 0x002371E0
            && sys.Cdvd.SectorsRead >= 400 && sys.Cdvd.SectorsRead < 5000
            && (_menuKickPulses % 4) == 0)
        {
            // Plant wake flags + exit VBlank so another thread can run FILEIO open.
            PlantWakeFlags(sys, VblankWakeFlagBase);
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = 0x002371E0;
        }

        // Post-LGDEV dual poll:
        //   0x2AF750: while(*(gp-23096)==0 && s0<600) SleepThread — flag==277 → primary
        //             continue @0x2AF7E0; flag!=0 && !=277 → alternate @0x2AF8A4.
        //   0x2AF80C: while(*(gp-23104)==0 && s0<600) SleepThread —
        //             flag!=0 && s0!=600 → 0x2AF914 v0=1 → epi 0x2AF984.
        // Fail timeout: 0x2AF91C/0x2AF920 — never soft-leave there.
        // MENU-B3: tip residual samples 0x2AF750 (before 0x2AF80C) then bitfield thrash
        // 0x2B45xx with STAGEHED-plant-only cdvd=609 — plant BOTH flags and leave.
        bool irxOnly = sys.Cdvd.SectorsRead is >= 400 and < 600;
        bool stagePlantOnly = sys.Cdvd.SectorsRead is >= 600 and < 2000;
        bool preStg = irxOnly || stagePlantOnly;
        bool postTxd = sys.Cdvd.SectorsRead >= 2000;
        if (_lgDevFullyDone && sys.MasterCycles >= 22_000_000 && (preStg || postTxd))
        {
            uint pcW = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
            uint raW = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            const uint postLgDevSuccess = 0x002AF914u;
            const uint postLgDevFlag277 = 277u; // live status word for primary continue
            bool raInPostLgDev = raW is >= 0x002AF700 and <= 0x002AF994;
            bool pcInPostLgDevEarly = pcW is >= 0x002AF750 and <= 0x002AF7C4;
            bool pcInPostLgDev = pcW is >= 0x002AF800 and <= 0x002AF980;
            bool pcInSleep = pcW is >= 0x0010C0A0 and <= 0x0010C0AC;
            bool pcInWaitSema = pcW is >= 0x0010BE60 and <= 0x0010BE70;
            if (preStg && (pcInPostLgDevEarly || pcInPostLgDev || (pcInSleep && raInPostLgDev)
                || (pcInWaitSema && raInPostLgDev)))
            {
                uint gpW = (uint)(sys.EE.GetGpr(28).Lo & 0x1FFFFFFFUL);
                if (gpW is < 0x00400000 or >= 0x01000000) gpW = 0x004E8670;
                uint f23096 = unchecked((uint)((int)gpW - 23096));
                uint f23104 = unchecked((uint)((int)gpW - 23104));
                if (f23096 is >= 0x00400000 and < 0x01000000)
                    sys.Memory.Write32(f23096, postLgDevFlag277);
                if (f23104 is >= 0x00400000 and < 0x01000000)
                    sys.Memory.Write32(f23104, 1);
                sys.Memory.Write32(BootWaitFlagDefault, 1);
                uint s0w = (uint)(sys.EE.GetGpr(16).Lo & 0xFFFFFFFFUL);
                if (s0w >= 600)
                    sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 1 });
                // Early poll: re-enter 0x2AF750 so delay-slot sets v0=600 then natural
                // success junction (flag==277 → 0x2AF7E0). Late poll: 0x2AF914.
                uint resume = pcInPostLgDevEarly || (raW is >= 0x002AF750 and <= 0x002AF7C4)
                    ? 0x002AF750u
                    : postLgDevSuccess;
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~(1u << 1);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_menuKickPulses % 8) == 0)
                    Console.Error.WriteLine(
                        $"[B3] leave post-LGDEV spin SUCCESS pc=0x{pcW:X8} ra=0x{raW:X8} " +
                        $"-> 0x{resume:X8} flag23096=277 cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
            }
            if (preStg && k != null && (_menuKickPulses % 2) == 0)
            {
                foreach (var t in k.AllThreads)
                {
                    if (!t.Alive || !t.Sleeping) continue;
                    uint savedRa = (uint)(t.SavedRa & 0x1FFFFFFFUL);
                    if (savedRa == 0 && t.HasFullSave && t.SavedGprFull != null && t.SavedGprFull.Length > 31)
                        savedRa = (uint)(t.SavedGprFull[31] & 0x1FFFFFFFUL);
                    uint savedPc = (uint)(t.SavedPc & 0x1FFFFFFFUL);
                    bool postPark = (savedRa is >= 0x002AF700 and <= 0x002AF994)
                        || (savedPc is >= 0x002AF700 and <= 0x002AF994)
                        || (savedPc is >= 0x0010C0A0 and <= 0x0010C0AC && savedRa is >= 0x002AF700 and <= 0x002AF994)
                        || (savedPc is >= 0x0010BE60 and <= 0x0010BE70 && savedRa is >= 0x002AF700 and <= 0x002AF994)
                        || (t.Id == 1 && t.WaitSemaId >= 0x40)
                        || (t.Id == 1 && t.WaitSemaId == 0 && !t.WaitVblank && _menuKickPulses >= 16);
                    if (t.WaitSemaId >= 32)
                    {
                        try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                    if (postPark && t.Id == 1)
                    {
                        t.SavedPc = postLgDevSuccess;
                        if (t.HasFullSave && t.SavedGprFull != null && t.SavedGprFull.Length > 2)
                        {
                            t.SavedGprFull[2] = 1;
                            if (t.SavedGprFull.Length > 16) t.SavedGprFull[16] = 1;
                        }
                        t.WaitSemaId = 0;
                        t.Sleeping = false;
                        t.WaitVblank = false;
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                            && (_menuKickPulses % 8) == 0)
                            Console.Error.WriteLine(
                                $"[B3] re-home sleeping main post-LGDEV SUCCESS " +
                                $"savedRa=0x{savedRa:X8} -> 0x{postLgDevSuccess:X8} " +
                                $"cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
                    }
                    else if (t.WaitSemaId == 0 && !t.WaitVblank)
                        k.WakeupThread(t.Id);
                }
            }
            else if (k != null && (_menuKickPulses % 2) == 0)
            {
                foreach (var t in k.AllThreads)
                {
                    if (!t.Alive || !t.Sleeping) continue;
                    if (t.WaitSemaId >= 32)
                    {
                        try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                    }
                    if (t.WaitSemaId == 0 && !t.WaitVblank)
                        k.WakeupThread(t.Id);
                }
            }
        }

        // Sticky re-plant entry stub after LGDEV so boot cannot re-enter wheel init.
        if (_lgDevFullyDone)
            PlantLgDevEntryStub(sys);

        // Boot wait-flag plant: break while (*(gp-23028)==0) SleepThread at 0x2B34D8.
        MaybePlantBootWaitFlag(sys);

        // Post-wait flip park at 0x1F24E0 (path-sync watermark) after leaving 0x2B35xx —
        // re-arm flip consumer + plant wake so boot can open game FILEIO.
        MaybeLeaveFlipPark(sys);

        // Post-flip nested table walk at 0x3E9Bxx (live final5 PC 0x3E9BD0): double loop
        // t1<a0 / t2<a2 looking for a match — with empty tables never hits, gifP3 climbs
        // but FILEIO never opens. Force outer exit so boot continues.
        MaybeEscapeTableWalk(sys);

        // Empty iovec / stream queue walk at 0x122990 / 0x122A20 (live final PC band):
        // while (*(s4+4)==0) s4+=8 — with HLE-empty GTFS/stream tables this never hits a
        // non-zero size word → forever park, cdvd stuck at IRX-only 425. Force empty-queue
        // epilogue so callers can fall through to real FILEIO/NCMD open.
        MaybeEscapeEmptyIoQueue(sys);

        // Proactive table-walk stub only after STG window — early stub blocked menu4 STG path.
        if (_lgDevFullyDone && _vblankExits >= 4 && sys.Gif.Path3Transfers >= 90
            && sys.Cdvd.SectorsRead is >= 600 and < 2000
            && sys.MasterCycles >= 55_000_000
            && sys.Memory.Read32(0x003E9B40) != 0x03E00008u)
        {
            sys.Memory.Write32(0x003E9B40, 0x03E00008u); // jr ra
            sys.Memory.Write32(0x003E9B44, 0x0000102Du); // daddu v0, zero, zero
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[B3] proactive table-walk stub @ 0x003E9B40 gifP3={sys.Gif.Path3Transfers} " +
                    $"cyc={sys.MasterCycles}");
        }

        // Dense START/CROSS after disc IRX path (cdvd>0). Pad inject is allowed.
        // PL-014 / MENU-B3-2: after Soft-GS logo chrome, edge pulse + DBC + presentation leave.
        if (LogoChromeLive(sys))
        {
            if (_logoChromeFirstCyc == 0)
                _logoChromeFirstCyc = sys.MasterCycles;
            MaybeSnapshotLogoChrome(sys);
            PulseLogoPadAdvance(sys, fromPresent: false);
            MaybeWakeMainForPad(sys);
            MaybeLeavePresentationPark(sys);
            MaybeReportSceneDelta(sys);
        }
        else
        {
            int phase = _menuKickPulses % 6;
            uint buttons = phase switch
            {
                0 or 1 => (uint)PadInput.Button.Start,
                3 or 4 => (uint)PadInput.Button.Cross,
                _ => 0u
            };
            if (_menuKickPulses % 11 == 0)
                buttons = (uint)(PadInput.Button.Start | PadInput.Button.Cross);
            if (_lgDevEscapes >= 3 && (_menuKickPulses % 4) < 2)
                buttons = (uint)PadInput.Button.Start;
            try { sys.Pad.SetButtons(buttons); } catch { /* Pad may be null early */ }
        }

        // Sticky: keep fno=18 path dead while we try to leave VBlank-only workers.
        sys.Memory.Write32(LgDevPostFlag, 0);

        // Rescue bad PC after flip leave (live menu15: 0x4FBxxx BSS / 0x171EC4 junk).
        MaybeRescueBadPc(sys);

        // Live menu18: after empty-iovec stub, parks on jr-ra delay at 0x219C84 with
        // garbage s0 (0x02100000 past RDRAM) and dead $ra — force a boot continue so
        // FILEIO open path can run instead of re-spinning the same epilogue.
        MaybeLeaveDeadEpilogue(sys);
    }

    private int _deadEpiLeaves;
    private int _postTxdEscapes;
    private ulong _lastPostTxdEscapeCyc;
    private bool _frontendPlanted;
    private uint _frontendEeAddr;
    private uint _frontendSize;

    /// <summary>
    /// After STG + full Global.txd (cdvd>=2000). Wave-9: sticky PATH3 M3P unmask,
    /// host-plant FRONTEND.TXD slice, dead flip-watermark $ra rescue.
    /// Never soft-complete generic CallRpc (DBC abort). No residual LGDEV force rewrite.
    /// </summary>
    private void MaybeEscapePostTxdHang(Ps2System sys)
    {
        // MENU-B3-2: chrome runs for tens of M after FRONTEND; 2048 leaves @4k gap
        // only cover ~8M then Soft-GS freezes (pad-inject saw px/prims stick ~85M while
        // 0x2199xx UnknownMmioWrite thrash). Raise cap under logo chrome.
        int escapeCap = LogoChromeLive(sys) ? 8192 : 2048;
        if (_postTxdEscapes >= escapeCap) return;
        ulong minGap = LogoChromeLive(sys) ? 2_000UL : 4_000UL;
        if (sys.MasterCycles - _lastPostTxdEscapeCyc < minGap) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);

        if (!_frontendPlanted && sys.Cdvd.SectorsRead >= 2000)
            MaybePlantFrontendTxd(sys);

        // PATH3 M3P: transfers count while packets are held -> gifP3 climbs, px=0.
        if (sys.Gif.Path3MaskedByVif && sys.Gs.PixelsWritten == 0
            && sys.Gif.Path3Transfers >= 30)
        {
            sys.Gif.SetMskPath3(false);
            _postTxdEscapes++;
            _lastPostTxdEscapeCyc = sys.MasterCycles;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_postTxdEscapes <= 8 || _postTxdEscapes % 32 == 0))
                Console.Error.WriteLine(
                    $"[B3] post-TXD unmask PATH3 M3P gifP3={sys.Gif.Path3Transfers} " +
                    $"px={sys.Gs.PixelsWritten} n={_postTxdEscapes} cyc={sys.MasterCycles}");
        }

        // Flip watermark jr @0x228068 - only when $ra is dead.
        if (pc is >= 0x00228054 and <= 0x0022806C)
        {
            bool badRa = ra < 0x00100000 || ra >= 0x00400000 || !sys.Memory.IsLikelyEeCode(ra)
                || ra is (>= 0x00228040 and <= 0x00228070)
                || ra is (>= 0x001F24E0 and <= 0x001F2510);
            if (badRa)
            {
                const uint resume = 0x001F2518u;
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                _postTxdEscapes++;
                _lastPostTxdEscapeCyc = sys.MasterCycles;
                ArmFlipConsumer(sys);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_postTxdEscapes <= 16 || _postTxdEscapes % 16 == 0))
                    Console.Error.WriteLine(
                        $"[B3] post-TXD leave flip-watermark jr ra was 0x{ra:X8} -> 0x{resume:X8} " +
                        $"n={_postTxdEscapes} cyc={sys.MasterCycles}");
                return;
            }
        }

        // Wave-8: GIF path-flush 0x21A4F0 bulk lq/sq with MMIO src (UnknownMmioRead) /
        // submit 0x1F308C. Collapse absurd gp ring; leave flush epilogue (not 0x1F2520).
        // Wave-6: also cover packet-build 0x2198xx (sq into gp-23960 ring) when cursor
        // lands in EE MMIO / VU mem (live 50M UnknownMmioWrite 0x1000xxxx / 0x1100xxxx).
        // Do not permanent-stub flush entry — sane flushes needed for Soft-GS px>0.
        bool inGifFlush = pc is >= 0x0021A4F0 and <= 0x0021A5E4;
        bool inGifSubmit = pc is >= 0x001F3080 and <= 0x001F3500;
        bool inFlushCaller = pc is >= 0x00218700 and <= 0x00218790;
        // Wave-6/MENU-B3-2: packet-build includes 0x2198xx..0x219Axx ring + live thrash
        // at 0x219900 writing EE MMIO 0x1000FFxx / VU 0x1100F0xx (pad claim freeze).
        bool inGifPacketBuild = pc is >= 0x00219800 and <= 0x00219B00;
        bool mmioProbe = (inGifFlush || inGifSubmit || inFlushCaller || inGifPacketBuild)
                         && sys.Cdvd.SectorsRead >= 2000;
        if (mmioProbe)
        {
            uint gp = (uint)(sys.EE.GetGpr(28).Lo & 0x1FFFFFFFUL);
            if (gp is < 0x00400000 or >= 0x01000000) gp = 0x004E8670;
            uint startCell = gp - 27936u, endCell = gp - 23960u, dstCell = gp - 24240u;
            uint startPhys = sys.Memory.Read32(startCell) & 0x1FFFFFFFu;
            uint endPhys = sys.Memory.Read32(endCell) & 0x1FFFFFFFu;
            bool absurd = startPhys >= 0x10000000u || endPhys >= 0x10000000u
                || endPhys < startPhys
                || (endPhys > startPhys && endPhys - startPhys > 0x00080000u)
                || startPhys < 0x00100000u
                || startPhys >= (uint)SystemMemory.RDRAM_SIZE;
            // Packet-build writes sq @ a3 / v1 from ring cursor — treat EE MMIO/VU as absurd.
            if (!absurd && inGifPacketBuild)
            {
                uint a3p = (uint)(sys.EE.GetGpr(7).Lo & 0x1FFFFFFFu);
                uint v1p = (uint)(sys.EE.GetGpr(3).Lo & 0x1FFFFFFFu);
                if (a3p is >= 0x10000000u and < 0x12000000u
                    || v1p is >= 0x10000000u and < 0x12000000u
                    || endPhys is >= 0x10000000u and < 0x12000000u)
                    absurd = true;
            }
            if (!absurd && inGifFlush)
            {
                uint t7 = (uint)(sys.EE.GetGpr(15).Lo & 0xFFFFFFFFUL);
                if ((t7 & 0x1FFFFFFFu) >= 0x10000000u) absurd = true;
            }
            if (!absurd && inGifSubmit)
            {
                uint a1 = (uint)(sys.EE.GetGpr(5).Lo & 0xFFFFFFFFUL);
                if (a1 > 0x4000u) absurd = true;
            }
            if (!absurd) return; // sane GIF path — let Soft-GS draw

            _lastPostTxdEscapeCyc = sys.MasterCycles;
            _postTxdEscapes++;
            // Empty the ring (start==end==dst) so re-entry does a zero-size flush and
            // returns cleanly; sane later fills can still produce Soft-GS px>0.
            uint safeRaw = sys.Memory.Read32(startCell);
            uint safePhys = safeRaw & 0x1FFFFFFFu;
            uint safe = safePhys is >= 0x00100000 and < 0x01E00000u ? safeRaw : 0x00700000u;
            sys.Memory.Write32(startCell, safe);
            sys.Memory.Write32(endCell, safe); // empty range → s0≈0 after (end-start+8)>>4
            sys.Memory.Write32(dstCell, safe);

            // Prefer flush/packet epilogue so callers see a clean return; only use
            // raw $ra when it is outside the flush/submit/packet thrash band.
            uint resume = inGifPacketBuild ? 0x00219A04u
                : inGifFlush ? 0x0021A5D8u
                : inGifSubmit ? 0x00218774u
                : 0x00218774u;
            if (ra is >= 0x00100000 and < 0x00400000 && sys.Memory.IsLikelyEeCode(ra)
                && ra is not (>= 0x0021A4F0 and <= 0x0021A5E8)
                && ra is not (>= 0x001F3080 and <= 0x001F3500)
                && ra is not (>= 0x001F24E0 and <= 0x001F2520)
                && ra is not (>= 0x00218700 and <= 0x00218790)
                && ra is not (>= 0x00219800 and <= 0x00219B00))
                resume = ra;
            // Sticky thrash at submit final (0x1F308C): after many leaves, bypass submit
            // entry to caller so FRONTEND draw path can continue past empty rings.
            if (inGifSubmit && _postTxdEscapes >= 32
                && sys.Memory.Read32(0x001F3080) != 0x03E00008u)
            {
                // One-shot soft-return only when still absurd — do not permanent-stub
                // forever; rewrite s0 count in-frame instead.
                sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = 0 }); // a1 size = 0
            }
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
                int p = _postTxdEscapes % 6;
                uint btn = p switch
                {
                    0 or 1 => (uint)PadInput.Button.Start,
                    2 => (uint)PadInput.Button.Cross,
                    3 => (uint)PadInput.Button.Circle,
                    4 => (uint)PadInput.Button.Down,
                    _ => (uint)(PadInput.Button.Start | PadInput.Button.Cross)
                };
                sys.Pad.SetButtons(btn);
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

        // SIF DMA copy body (0x10FB30 first path + 0x10FB80 second path).
        bool sifCopy = pc is (>= 0x0010FB30 and <= 0x0010FB7C)
            or (>= 0x0010FB80 and <= 0x0010FBD0);
        bool waitOnWorker = pc is >= 0x0010BE60 and <= 0x0010BE70
                            && ra is >= 0x00242A40 and <= 0x00242B80;

        if (!sifCopy && !waitOnWorker) return;

        // Only break absurd SIF copies (huge size or dest outside RDRAM).
        if (sifCopy)
        {
            uint a3 = (uint)(sys.EE.GetGpr(7).Lo & 0x1FFFFFFFUL);
            uint size = 0, dest = 0;
            if (a3 is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 16)
            {
                size = sys.Memory.Read32(a3 + 4);
                dest = sys.Memory.Read32(a3 + 12);
            }
            uint a2 = (uint)(sys.EE.GetGpr(6).Lo & 0xFFFFFFFFUL);
            bool absurd = size > 0x00040000 || a2 > 0x00040000
                          || (dest != 0 && (dest & 0x1FFFFFFFu) >= 0x02000000u)
                          || (dest & 0x1FFFFFFFu) is > 0 and < 0x00010000u;
            if (!absurd && size != 0 && size <= 0x10000 && a2 < size)
                return; // let a sane small copy finish

            _lastPostTxdEscapeCyc = sys.MasterCycles;
            _postTxdEscapes++;

            // Clamp size so blez paths would also leave; then jump loop exit.
            if (a3 is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 16)
            {
                sys.Memory.Write32(a3 + 0, 0);
                sys.Memory.Write32(a3 + 4, 0);
            }
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = 0 }); // v1
            sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 }); // a2 cursor
            sys.EE.PC = 0x0010FD9C; // post-copy continue (disasm beq → 0x10FD9C)
            sys.EE.COP0_Status &= ~0x6u;

            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_postTxdEscapes <= 16 || _postTxdEscapes % 16 == 0))
                Console.Error.WriteLine(
                    $"[B3] post-TXD SIF-copy exit pc=0x{pc:X8} a3=0x{a3:X8} size={size} " +
                    $"dest=0x{dest:X8} a2={a2} -> 0x10FD9C n={_postTxdEscapes} " +
                    $"cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
            return;
        }

        // WaitSema worker path — pulse high waiters only (no PC rewrite).
        _lastPostTxdEscapeCyc = sys.MasterCycles;
        _postTxdEscapes++;
        var k = sys.Hle?.Kernel;
        if (k != null)
        {
            foreach (var t in k.AllThreads)
            {
                if (!t.Alive || !t.Sleeping) continue;
                if (t.WaitSemaId >= 32)
                {
                    try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                }
            }
        }

        try
        {
            uint buttons = (_postTxdEscapes % 4) < 2
                ? (uint)PadInput.Button.Start
                : (uint)PadInput.Button.Cross;
            sys.Pad.SetButtons(buttons);
            _padInjectPulses++;
        }
        catch { /* ignore */ }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_postTxdEscapes <= 12 || _postTxdEscapes % 16 == 0))
            Console.Error.WriteLine(
                $"[B3] post-TXD worker WaitSema pulse ra=0x{ra:X8} n={_postTxdEscapes} " +
                $"cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Function epilogue park at <c>0x219C74..0x219C84</c> (jr ra / sp+=48) after empty
    /// iovec leave. When $ra is not real .text or s0 is past RDRAM, snap to a known
    /// post-LGDEV continue (boot-wait chain or last-good) so Criterion can open assets.
    /// </summary>
    private void MaybeLeaveDeadEpilogue(Ps2System sys)
    {
        if (!_lgDevFullyDone) return;
        if (sys.Cdvd.SectorsRead < 400) return;
        if (_deadEpiLeaves >= 64) return;
        if ((_menuKickPulses % 2) != 0) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        // 0x219C74..88 / 0x219A04..1C: empty-iovec family epilogues.
        // 0x2B366C..74: boot-wait chain epilogue (live final8 PC 0x2B3674) with dead $ra.
        // 0x2220CC..D4: post-GTFS return jr-ra delay (live gtfs4 final PC 0x2220D0) dead $ra.
        bool atEpi = pc is (>= 0x00219C74 and <= 0x00219C88)
            or (>= 0x00219A04 and <= 0x00219A1C)
            or (>= 0x002B366C and <= 0x002B3678)
            or (>= 0x002220CC and <= 0x002220D8);
        if (!atEpi) return;

        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        uint s0 = (uint)(sys.EE.GetGpr(16).Lo & 0x1FFFFFFFUL);
        bool badRa = ra < 0x00100000 || ra >= 0x00400000 || !sys.Memory.IsLikelyEeCode(ra)
            || ra is (>= 0x00219A00 and <= 0x00219D00)
            || ra is (>= 0x002B3600 and <= 0x002B3700)
            || ra is (>= 0x00222000 and <= 0x00222100);
        bool badS0 = s0 >= 0x02000000 || (s0 != 0 && s0 < 0x00100000);

        if (!badRa && !badS0 && pc is not (>= 0x002B366C and <= 0x002B3678)
            && pc is not (>= 0x002220CC and <= 0x002220D8)) return;

        // Boot-wait epi → next function at 0x2B3680 (jal 0x296600 asset path).
        // Empty-iovec epi → re-enter boot-wait continue while IRX-only.
        uint resume = pc is (>= 0x002B366C and <= 0x002B3678)
            ? 0x002B3680u
            : 0x002B34E8u;
        if (sys.Cdvd.SectorsRead >= 800 && !badRa && ra is >= 0x00120000 and < 0x00400000
            && ra is not (>= 0x00219A00 and <= 0x00219D00)
            && ra is not (>= 0x001F24E0 and <= 0x001F2520)
            && ra is not (>= 0x002B3600 and <= 0x002B3700))
            resume = ra;

        // 0x2B3680 takes a0 as object (moves to s0); live garbage a0=0x2100000 past RDRAM
        // makes jal 0x296600 thrash. Plant a known EE object (live boot-wait s0 0x4E41C0).
        if (resume == 0x002B3680u || (badS0 && resume == 0x002B34E8u))
        {
            const uint liveObj = 0x004E41C0;
            sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = liveObj }); // a0
            sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = liveObj }); // s0
            // Ensure object-local ready flags so wait-3 style checks pass.
            sys.Memory.Write32(liveObj + 0x13A4, 1);
            if (sys.Memory.Read32(liveObj + 0x13A0) == 0)
                sys.Memory.Write32(liveObj + 0x13A0, 1);
        }

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        _deadEpiLeaves++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_deadEpiLeaves <= 12 || _deadEpiLeaves % 16 == 0))
            Console.Error.WriteLine(
                $"[B3] leave dead epilogue pc=0x{pc:X8} ra=0x{ra:X8} s0=0x{s0:X8} " +
                $"-> 0x{resume:X8} n={_deadEpiLeaves} cdvd={sys.Cdvd.SectorsRead} " +
                $"cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// After flip leave + table-walk stub, EE sometimes lands in BSS/data (0x4FBxxx) or
    /// mid-junk (0x171EC4 UnknownOpcode). Re-home to last-good or known boot continue.
    /// </summary>
    private void MaybeRescueBadPc(Ps2System sys)
    {
        if (!_lgDevFullyDone) return;
        if (sys.MasterCycles - _lastFlipLeaveCyc < 80_000 && _vblankExits < 4) return;
        uint pc = (uint)(sys.EE.PC & 0xFFFFFFFFUL);
        uint phys = pc & 0x1FFFFFFFu;
        bool bad = pc is >= 0x80000180 and <= 0x80000200
            || phys < 0x00100000
            || phys is >= 0x004E0000 and < 0x02000000 // high BSS/data (live 0x4FB168)
            || (phys is >= 0x00171E00 and <= 0x00171F00 && sys.Memory.Read32(phys) == 0x4C0899E8u);
        if (!bad) return;

        uint resume = 0;
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        if (ra is >= 0x00100000 and < 0x00400000 && sys.Memory.IsLikelyEeCode(ra))
            resume = ra;
        if (resume == 0 && sys.LastGoodEePc is >= 0x00100000 and < 0x00400000)
            resume = (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL);
        // Known live post-LGDEV continues (not CRT0, not flip wait body).
        if (resume == 0 || resume is (>= 0x001F24E0 and <= 0x001F2520) || resume == 0x00100008)
            resume = 0x002B34E8; // past boot-wait-1 → continue Criterion boot
        if (resume is >= 0x00400000) resume = 0x002B34E8;

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_menuKickPulses % 16) == 0)
            Console.Error.WriteLine(
                $"[B3] rescue bad PC 0x{pc:X8} -> 0x{resume:X8} gifP3={sys.Gif.Path3Transfers} " +
                $"cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// After wait-chain exit, main parks in GS flip/watermark at <c>0x1F24E0</c> (live
    /// menu13/14 final). Loop: while (out!=in || pending) { optional callback }. Soft-clear
    /// residual pending (never force out←in), credit VIF1/GIF, then snap past the
    /// <c>bne s0,zero</c> so boot reaches the di/ei drain at 0x1F2520+ and FILEIO.
    /// After a few snaps still re-entering, permanently <c>j 0x1F2520</c> the wait entry.
    /// </summary>
    private ulong _lastFlipLeaveCyc;
    private bool _flipWaitStubPlanted;

    private void MaybeLeaveFlipPark(Ps2System sys)
    {
        if (!_lgDevFullyDone) return;
        if (sys.Cdvd.SectorsRead < 400) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        // 0x1F24E0 path-sync wait body (through bne s0 back-edge).
        bool inFlipWait = pc is >= 0x001F24E0 and <= 0x001F251C;
        bool nearFlip = inFlipWait
            || pc is (>= 0x001F1700 and <= 0x001F2600)
            || pc is (>= 0x00228000 and <= 0x00228100);
        if (!nearFlip && _flipWaitStubPlanted) return;
        if (!nearFlip && !inFlipWait) return;
        // Own cadence — do NOT share _lastRearmCyc with flip residual (that starved leave).
        if (sys.MasterCycles - _lastFlipLeaveCyc < 50_000) return;
        _lastFlipLeaveCyc = sys.MasterCycles;

        ArmFlipConsumer(sys);
        uint pending = sys.Memory.Read32(PendingCountAddr) & 0xFF;
        uint qOut = sys.Memory.Read32(QueueOutAddr);
        uint qIn = sys.Memory.Read32(QueueInAddr);
        if (qOut != qIn || pending > 0)
        {
            int need = pending > 0 ? (int)Math.Min(pending + 1, 6u) : 2;
            sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.VIF1, need);
            sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.GIF, need);
            // Soft-clear residual pending after credits (same class as flip residual clear).
            if (pending > 0)
                sys.Memory.Write8(PendingCountAddr, 0);
            // Do NOT soft-advance out toward in (live menu15: partial out advance + high
            // gifP3 left EE in data at 0x4FBxxx / 0x171EC4). Credits + pending clear only.
            _rearms++;
            _flipEverUnblocked = true;
        }
        PlantWakeFlags(sys, VblankWakeFlagBase);

        // Permanent bypass of path-sync wait once we've left a few times (re-entry thrash).
        // j 0x1F2520 = 0x08000000 | (0x001F2520 >> 2) = 0x0807C948
        // After FRONTEND/Global DMA (cdvd≫2000), delay bypass so real flip can present
        // Soft-GS frames — early bypass left gifP3 climbing with px=0.
        // Wave-9: after STG only plant at >=95M while still px=0.
        if (_vblankExits >= 2 && !_flipWaitStubPlanted
            && (sys.Cdvd.SectorsRead < 2000
                || (sys.MasterCycles >= 95_000_000 && sys.Gs.PixelsWritten == 0
                    && sys.Gif.Path3Transfers >= 100))
            && sys.Memory.Read32(0x001F24E0) != 0x0807C948u)
        {
            sys.Memory.Write32(0x001F24E0, 0x0807C948u); // j 0x001F2520
            sys.Memory.Write32(0x001F24E4, 0x00008021u); // addu s0, zero, zero
            _flipWaitStubPlanted = true;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[B3] plant flip-wait bypass j 0x1F2520 @ 0x001F24E0 " +
                    $"gifP3={sys.Gif.Path3Transfers} px={sys.Gs.PixelsWritten} cyc={sys.MasterCycles}");
        }

        // Snap past the wait loop so di/ei drain + FILEIO path can run.
        // PL-014: once logo Soft-GS chrome is live and queues are healthy (out==in,
        // pending==0), do NOT thrash-leave every 50k — that monopolized EE at 0x1F2508
        // for 50–100M and starved libpad2/DBC pad consumers needed for logo→menu.
        bool queuesHealthy = qOut == qIn && pending == 0;
        bool logoChrome = LogoChromeLive(sys);
        bool allowHealthyLeave = !logoChrome || !queuesHealthy;

        if (allowHealthyLeave
            && (inFlipWait || (_flipWaitStubPlanted && pc is >= 0x001F24E0 and <= 0x001F251C)))
        {
            sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 }); // s0 = 0 → fall through
            sys.EE.PC = 0x001F2520; // di / sync / mfc0 Status
            sys.EE.COP0_Status &= ~0x6u;
            _vblankExits++;
        }
        else if (nearFlip && _vblankExits >= 4 && sys.Cdvd.SectorsRead < 800)
        {
            // Callback park 0x2280xx / consumer — kick to post-wait drain after thrash.
            sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = 0x001F2520;
            sys.EE.COP0_Status &= ~0x6u;
            _vblankExits++;
        }
        else if (logoChrome && queuesHealthy && inFlipWait)
        {
            // Soft re-arm only — keep flip consumer live without PC stomp.
            ArmFlipConsumer(sys);
            PlantWakeFlags(sys, VblankWakeFlagBase);
        }

        var k = sys.Hle?.Kernel;
        if (k != null)
        {
            foreach (var t in k.AllThreads)
            {
                if (!t.Alive || !t.Sleeping) continue;
                if (t.WaitSemaId == 0 && !t.WaitVblank)
                    k.WakeupThread(t.Id);
                else if (t.WaitSemaId >= 32)
                {
                    try { k.SignalSema(t.WaitSemaId); } catch { /* ignore */ }
                }
            }
        }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_vblankExits <= 16 || _vblankExits % 16 == 0))
            Console.Error.WriteLine(
                $"[B3] leave flip park pc=0x{pc:X8} -> {(inFlipWait || nearFlip ? "0x1F2520" : "rearm")} " +
                $"pending={pending} out=0x{qOut:X8} in=0x{qIn:X8} stub={_flipWaitStubPlanted} " +
                $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

    private int _tableWalkEscapes;
    private int _ioQueueEscapes;
    private ulong _lastIoQueueEscapeCyc;
    private bool _ioQueueStubPlanted;

    /// <summary>
    /// Stream/iovec consume at <c>0x00122990</c> / <c>0x00122A20</c> (live menu16/17 final):
    /// <c>lw s2,4(s4); beq s2,zero,self; addiu s4,8</c> — walks 8-byte {ptr,size} pairs until
    /// size≠0. Prefer planting a real STAGEHED iovec at <c>s4</c> (so the non-empty path at
    /// <c>0x122A40</c> runs) over permanent empty-epi stubs that skip asset consume.
    /// </summary>
    private void MaybeEscapeEmptyIoQueue(Ps2System sys)
    {
        if (!_lgDevFullyDone) return;
        if (sys.Cdvd.SectorsRead < 400) return;
        if (_ioQueueEscapes >= 256) return;

        // Ensure STAGEHED is in EE before we try to plant an iovec (short residual n=2–3 OK).
        if (!_stageAssetsPlanted && sys.MasterCycles >= 28_000_000 && _lgDevEscapes >= 1)
            MaybePlantStageAssets(sys);

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        bool inScan = pc is (>= 0x00122990 and <= 0x001229AC)
            or (>= 0x00122A18 and <= 0x00122A3C)
            or (>= 0x00124020 and <= 0x00124050); // memcpy tail of same family (a2 countdown)
        if (!inScan)
        {
            // Do NOT permanent-stub empty iovec before STG bind (menu4 reached STG without).
            if (!_ioQueueStubPlanted && _vblankExits >= 4 && sys.Gif.Path3Transfers >= 90
                && sys.Cdvd.SectorsRead is >= 600 and < 900 && _stageHedSize == 0
                && sys.MasterCycles >= 55_000_000)
            {
                sys.Memory.Write32(0x00122990, 0x08048B2Fu); // j 0x00122CBC
                sys.Memory.Write32(0x00122994, 0x0000102Du);
                sys.Memory.Write32(0x00122A20, 0x08048B2Fu);
                sys.Memory.Write32(0x00122A24, 0x0000102Du);
                _ioQueueStubPlanted = true;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine(
                        $"[B3] plant empty-iovec stub @ 0x122990/0x122A20 -> 0x122CBC " +
                        $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
            }
            return;
        }

        if (sys.MasterCycles - _lastIoQueueEscapeCyc < 40_000) return;
        _lastIoQueueEscapeCyc = sys.MasterCycles;

        uint s2 = (uint)(sys.EE.GetGpr(18).Lo & 0xFFFFFFFFUL); // s2 = size
        uint s4 = (uint)(sys.EE.GetGpr(20).Lo & 0x1FFFFFFFUL); // s4 = cursor
        uint sizeWord = 0;
        if (s4 is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 8)
            sizeWord = sys.Memory.Read32(s4 + 4);

        bool empty = s2 == 0 && sizeWord == 0;
        bool absurdS4 = s4 < 0x00100000 || s4 >= 0x02000000 || (s4 & 3) != 0;
        bool hugeCopy = pc is >= 0x00124020 and <= 0x00124050
            && (uint)(sys.EE.GetGpr(6).Lo & 0xFFFFFFFFUL) > 0x10000;

        if (!empty && !absurdS4 && !hugeCopy) return;

        // Prefer planting a real {ptr,size} iovec so the non-empty body at 0x122A40 runs
        // (jal 0x123F58 consume) instead of skipping with empty-epi v0=0.
        if (empty && !absurdS4 && _stageHedSize > 0 && _stageHedEeAddr != 0
            && s4 is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 16)
        {
            uint plantSize = Math.Min(_stageHedSize, 0x10000u); // first 64KiB slice
            sys.Memory.Write32(s4 + 0, _stageHedEeAddr);
            sys.Memory.Write32(s4 + 4, plantSize);
            // Terminator after one entry.
            sys.Memory.Write32(s4 + 8, 0);
            sys.Memory.Write32(s4 + 12, 0);
            sys.EE.SetGpr(18, new EmotionEngine.Gpr128 { Lo = plantSize }); // s2 = size
            sys.EE.SetGpr(19, new EmotionEngine.Gpr128 { Lo = _stageHedEeAddr }); // s3 = ptr
            // Re-enter scan head so bne s2,zero takes the non-empty path.
            sys.EE.PC = 0x00122A18;
            sys.EE.COP0_Status &= ~0x6u;
            _ioQueueEscapes++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_ioQueueEscapes <= 16 || _ioQueueEscapes % 16 == 0))
                Console.Error.WriteLine(
                    $"[B3] plant iovec STAGEHED @ s4=0x{s4:X8} ptr=0x{_stageHedEeAddr:X8} " +
                    $"size=0x{plantSize:X} n={_ioQueueEscapes} cdvd={sys.Cdvd.SectorsRead} " +
                    $"cyc={sys.MasterCycles}");
            return;
        }

        // Fallback: empty-queue success epilogue when no stage plant possible.
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(18, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.PC = 0x00122CBC;
        sys.EE.COP0_Status &= ~0x6u;
        _ioQueueEscapes++;

        if (_ioQueueEscapes >= 4 && !_ioQueueStubPlanted && _stageHedSize == 0)
        {
            sys.Memory.Write32(0x00122990, 0x08048B2Fu);
            sys.Memory.Write32(0x00122994, 0x0000102Du);
            sys.Memory.Write32(0x00122A20, 0x08048B2Fu);
            sys.Memory.Write32(0x00122A24, 0x0000102Du);
            _ioQueueStubPlanted = true;
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_ioQueueEscapes <= 16 || _ioQueueEscapes % 16 == 0))
            Console.Error.WriteLine(
                $"[B3] escape empty iovec pc=0x{pc:X8} s2=0x{s2:X} s4=0x{s4:X8} " +
                $"-> 0x122CBC n={_ioQueueEscapes} cdvd={sys.Cdvd.SectorsRead} " +
                $"gifP3={sys.Gif.Path3Transfers} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Load <c>DATA/STAGEHED.BIN</c> (+ HEADUS) into EE RDRAM scratch and credit disc
    /// sectors. Gives empty-iovec / GTFS stream walks a real payload after IRX-only boot.
    /// </summary>
    private void MaybePlantStageAssets(Ps2System sys)
    {
        if (_stageAssetsPlanted) return;
        if (sys.Cdvd.MountedPath == null) return;

        try
        {
            var vol = Iso9660.OpenFile(sys.Cdvd.MountedPath);
            if (vol == null) return;

            byte[]? hed = Iso9660.ReadFile(vol, "DATA/STAGEHED.BIN")
                           ?? Iso9660.ReadFile(vol, "STAGEHED.BIN");
            if (hed == null || hed.Length == 0) return;

            uint dest = StageHedScratch;
            if (dest + (uint)hed.Length >= (uint)SystemMemory.RDRAM_SIZE)
                dest = 0x01800000;
            if (dest + (uint)hed.Length >= (uint)SystemMemory.RDRAM_SIZE)
                return;

            for (int i = 0; i < hed.Length; i++)
                sys.Memory.Write8(dest + (uint)i, hed[i]);

            _stageHedEeAddr = dest;
            _stageHedSize = (uint)hed.Length;
            sys.Cdvd.NoteHostReadSectors((hed.Length + 2047) / 2048);

            // HEADUS menu strings (UTF-16 ONLINE/CRASH/RACE …) — small, plant after STAGEHED.
            byte[]? head = Iso9660.ReadFile(vol, "DATA/HEADUS.BIN")
                           ?? Iso9660.ReadFile(vol, "HEADUS.BIN");
            if (head != null && head.Length > 0)
            {
                uint hDest = (dest + (uint)hed.Length + 15u) & ~15u;
                if (hDest + (uint)head.Length < (uint)SystemMemory.RDRAM_SIZE)
                {
                    for (int i = 0; i < head.Length; i++)
                        sys.Memory.Write8(hDest + (uint)i, head[i]);
                    sys.Cdvd.NoteHostReadSectors((head.Length + 2047) / 2048);
                }
            }

            // Seed GTFS status cells live B3 uses (recv buffers from RPC log).
            // 0x4E2730 fno=1 reply; 0x66E080 fno=3 reply — size words non-zero.
            sys.Memory.Write32(0x004E2730, 0);
            sys.Memory.Write32(0x004E2734, 1);
            sys.Memory.Write32(0x004E2738, _stageHedSize);
            sys.Memory.Write32(0x004E273C, 1);
            sys.Memory.Write32(0x0066E080, 0);
            sys.Memory.Write32(0x0066E084, 2);
            sys.Memory.Write32(0x0066E088, _stageHedSize);
            sys.Memory.Write32(0x0066E08C, 1);

            _stageAssetsPlanted = true;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[B3] plant STAGEHED @ 0x{dest:X8} size={hed.Length} " +
                    $"cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine($"[B3] STAGEHED plant failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Nested search at <c>0x3E9B98..0x3E9BEC</c> (live final5): for t2 in 0..a2 for t1 in
    /// 0..a0 load table[t1] and compare — empty HLE tables never match → infinite with
    /// gifP3 climbing. Snap counters to ends and fall out of both loops.
    /// </summary>
    /// <summary>High-RDRAM scratch for FRONTEND.TXD (~8.5 MiB).</summary>
    public const uint FrontendScratch = 0x00A00000;

    /// <summary>
    /// After STG + Global.txd (cdvd>=2000), host-plant a capped FRONTEND.TXD slice so
    /// presentation/logo walks can see real TXD payload. Wave-6: 4 MiB (was 2) so more
    /// early mips/UI pages bind under Soft-GS without claiming a full 8+ MiB plant.
    /// </summary>
    private void MaybePlantFrontendTxd(Ps2System sys)
    {
        if (_frontendPlanted) return;
        if (sys.Cdvd.MountedPath == null) return;

        try
        {
            var vol = Iso9660.OpenFile(sys.Cdvd.MountedPath);
            if (vol == null) return;

            byte[]? fe = Iso9660.ReadFile(vol, "DATA/FRONTEND.TXD")
                         ?? Iso9660.ReadFile(vol, "FRONTEND.TXD")
                         ?? Iso9660.ReadFile(vol, "Data/FRONTEND.TXD");
            if (fe == null || fe.Length == 0) return;

            // Wave-6: 4 MiB — dir + more early mips than wave-9's 2 MiB plant.
            // Stay below STAGEHED scratch (0x01900000) and EE stack (~0x01FF0000).
            const int maxPlant = 4 * 1024 * 1024;
            int n = Math.Min(fe.Length, maxPlant);
            uint dest = FrontendScratch;
            if (dest + (uint)n >= (uint)SystemMemory.RDRAM_SIZE
                || dest + (uint)n >= StageHedScratch)
                dest = 0x00C00000;
            if (dest + (uint)n >= (uint)SystemMemory.RDRAM_SIZE
                || dest + (uint)n >= StageHedScratch)
            {
                // Fall back to 2 MiB if 4 MiB collides with stage scratch.
                n = Math.Min(fe.Length, 2 * 1024 * 1024);
                dest = FrontendScratch;
            }
            if (dest + (uint)n >= (uint)SystemMemory.RDRAM_SIZE)
                return;

            for (int i = 0; i < n; i++)
                sys.Memory.Write8(dest + (uint)i, fe[i]);

            _frontendEeAddr = dest;
            _frontendSize = (uint)n;
            sys.Cdvd.NoteHostReadSectors((fe.Length + 2047) / 2048);

            sys.Memory.Write32(0x0066E090, 0);
            sys.Memory.Write32(0x0066E094, 2);
            sys.Memory.Write32(0x0066E098, (uint)fe.Length);
            sys.Memory.Write32(0x0066E09C, dest);

            _frontendPlanted = true;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[B3] plant FRONTEND.TXD @ 0x{dest:X8} planted={n}/{fe.Length} " +
                    $"cdvd={sys.Cdvd.SectorsRead} cyc={sys.MasterCycles}");
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine($"[B3] FRONTEND plant failed: {ex.Message}");
            _frontendPlanted = true;
        }
    }

    private void MaybeEscapeTableWalk(Ps2System sys)
    {
        if (!_lgDevFullyDone) return;
        if (_tableWalkEscapes >= 64) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pc is < 0x003E9B90 or > 0x003E9BF0) return;
        if (sys.MasterCycles - _lastFlipLeaveCyc < 50_000 && _tableWalkEscapes > 0) return;

        uint a0 = (uint)(sys.EE.GetGpr(4).Lo & 0xFFFFFFFFUL);
        uint a2 = (uint)(sys.EE.GetGpr(6).Lo & 0xFFFFFFFFUL);
        // Live: a0/a2 arrive as garbage (~6e8 / ~2e9) → never terminate. Prefer $ra return
        // over counter snap (which re-enters with same bounds). After a few absurd hits,
        // permanent-stub the search head at 0x3E9B40 so caller cannot re-enter.
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        bool absurd = a0 > 0x10000 || a2 > 0x10000;
        if (absurd && _tableWalkEscapes >= 2
            && sys.Memory.Read32(0x003E9B40) != 0x03E00008u)
        {
            sys.Memory.Write32(0x003E9B40, 0x03E00008u); // jr ra
            sys.Memory.Write32(0x003E9B44, 0x0000102Du); // daddu v0, zero, zero
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[B3] plant table-walk stub @ 0x003E9B40 cyc={sys.MasterCycles}");
        }
        if (absurd && ra is >= 0x00100000 and < 0x00800000 && ra is not (>= 0x003E9B00 and <= 0x003E9C00))
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = ra;
        }
        else
        {
            if (absurd)
            {
                a0 = 0; a2 = 0;
                sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 });
            }
            sys.EE.SetGpr(9, new EmotionEngine.Gpr128 { Lo = a0 });
            sys.EE.SetGpr(10, new EmotionEngine.Gpr128 { Lo = a2 });
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = 0x003E9BF0;
        }
        sys.EE.COP0_Status &= ~0x6u;
        _tableWalkEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_tableWalkEscapes <= 8 || _tableWalkEscapes % 8 == 0))
            Console.Error.WriteLine(
                $"[B3] escape table walk pc=0x{pc:X8} a0={a0} a2={a2} absurd={absurd} " +
                $"-> 0x{(uint)sys.EE.PC:X8} n={_tableWalkEscapes} gifP3={sys.Gif.Path3Transfers} " +
                $"cyc={sys.MasterCycles}");
    }


    private int _residualBootLeaves;
    private ulong _lastResidualBootLeaveCyc;

    /// <summary>
    /// Wave-2 / MENU-B3 residual-STG: after LGDEV force, tip parks in SIF WaitSema
    /// (0x293Axx), stream poll (0x123Exx), dual post-LGDEV flags (0x2AF750/0x2AF80C),
    /// or bitfield set thrash (0x2B44E0..0x2B45D4) with STAGEHED-plant-only cdvd.
    /// Leave toward natural success junctions so STG/Global.txd can bind.
    /// </summary>
    private void MaybeLeaveResidualBootThrash(Ps2System sys)
    {
        if (_residualBootLeaves >= 256) return;
        if (sys.MasterCycles - _lastResidualBootLeaveCyc < 40_000) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        // MENU-B3: do NOT snap 0x293xxx / 0x123Exx → post-LGDEV (wave-2 class:
        // UnknownOpcode @0x49FExx / dead frame). Only leave when EE is already in a
        // known wait/bitfield body with a live stack (or absurd bitfield span).
        bool postLgDevEarly = pc is >= 0x002AF750 and <= 0x002AF7C4;
        bool postLgDevLate = pc is >= 0x002AF800 and <= 0x002AF994;
        bool postLgDev = postLgDevEarly || postLgDevLate;
        bool bootWait1 = pc is >= 0x002B34C0 and <= 0x002B34E4;
        bool bootWait2 = pc is >= 0x002B3510 and <= 0x002B3540;
        bool bootWait3 = pc is >= 0x002B35A0 and <= 0x002B35C0;
        bool bootWait = bootWait1 || bootWait2 || bootWait3;
        // Bitfield set/clear at 0x2B44E0: outer t6..t1 byte loop. Live residual final
        // PC 0x2B4580 with garbage t1 monopolizes EE after STAGEHED plant (cdvd=609).
        bool bitfieldThrash = pc is >= 0x002B44E0 and <= 0x002B45D4;
        bool waitSemaPostLg = pc is >= 0x0010BE60 and <= 0x0010BE70
            && (ra is >= 0x002AF750 and <= 0x002AF994);
        bool sleepPostLg = pc is >= 0x0010C0A0 and <= 0x0010C0AC
            && (ra is >= 0x002AF750 and <= 0x002AF994);
        bool badPc = pc is >= 0x004E0000 and < 0x02000000
            || pc is >= 0x80000180 and <= 0x80000200;

        // Bitfield: only leave when span is absurd (not mid-sane STAGEHED index paint).
        if (bitfieldThrash)
        {
            uint t1 = (uint)(sys.EE.GetGpr(9).Lo & 0xFFFFFFFFUL);  // t1 end
            uint t6 = (uint)(sys.EE.GetGpr(14).Lo & 0xFFFFFFFFUL); // t6 cursor
            uint a0b = (uint)(sys.EE.GetGpr(4).Lo & 0x1FFFFFFFUL);
            bool absurd = t1 > 0x10000 || t6 > 0x10000
                || (t1 >= t6 && t1 - t6 > 0x4000)
                || a0b is < 0x00100000 or >= 0x02000000
                || _residualBootLeaves >= 8; // sticky thrash after several visits
            if (!absurd) return;
        }

        if (!waitSemaPostLg && !sleepPostLg && !postLgDev && !bootWait
            && !bitfieldThrash && !badPc)
            return;

        _lastResidualBootLeaveCyc = sys.MasterCycles;
        _residualBootLeaves++;

        uint gp = (uint)(sys.EE.GetGpr(28).Lo & 0x1FFFFFFFUL);
        if (gp is < 0x00400000 or >= 0x01000000) gp = 0x004E8670;
        uint f23096 = unchecked((uint)((int)gp - 23096));
        uint f23104 = unchecked((uint)((int)gp - 23104));
        uint f23028 = unchecked((uint)((int)gp + BootWaitFlagGpOff));
        if (f23096 is >= 0x00400000 and < 0x01000000)
            sys.Memory.Write32(f23096, 277); // primary post-LGDEV status
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
        const uint postLgDevEarlyPoll = 0x002AF750u;
        const uint bootWaitContinue = 0x002B34E8u;
        const uint bitfieldEpi = 0x002B45CCu; // lq s0 / jr ra
        // Prefer natural re-entry of the active poll so delay slots set v0 correctly.
        uint resume = postLgDevSuccess;
        if (postLgDevEarly
            || ((waitSemaPostLg || sleepPostLg) && ra is >= 0x002AF750 and <= 0x002AF7C4))
            resume = postLgDevEarlyPoll;
        else if (postLgDevLate || waitSemaPostLg || sleepPostLg)
            resume = postLgDevSuccess;
        if (bootWait1) resume = bootWaitContinue;          // 0x2B34E8
        else if (bootWait2) resume = 0x002B356Cu;          // after wait-2 result check
        else if (bootWait3) resume = 0x002B35C0u;          // past wait-3 → jal 0x2AFDD0
        if (bitfieldThrash)
        {
            // Prefer real $ra when it looks like a caller; else clean epilogue.
            if (ra is >= 0x00100000 and < 0x00400000 && sys.Memory.IsLikelyEeCode(ra)
                && ra is not (>= 0x002B44E0 and <= 0x002B45D8))
                resume = ra;
            else
                resume = bitfieldEpi;
        }
        if (badPc) resume = bootWaitContinue;

        uint s0w = (uint)(sys.EE.GetGpr(16).Lo & 0xFFFFFFFFUL);
        // Poll counter must stay in 1..599; reject zero, timeout, and pointer-shaped s0.
        if (s0w >= 600 || s0w == 0 || s0w >= 0x01000000u
            || (s0w >= 0x00400000u && (s0w & 3) == 0))
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
                if (th.Id == 1 && sys.Cdvd.SectorsRead < 2000)
                {
                    th.SavedPc = (resume == postLgDevEarlyPoll || resume == postLgDevSuccess)
                        ? resume
                        : postLgDevSuccess;
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

    /// <summary>
    /// Boot wait chain after LGDEV (disasm 0x2B34C0..0x2B35C0):
    /// <list type="number">
    /// <item><c>0x2B34D8</c>: while <c>*(gp-23028)==0</c> SleepThread — plant flag=1.</item>
    /// <item><c>0x2B351C</c>: while <c>*(gp-27128)==-1</c> (async result) — plant != -1.</item>
    /// <item><c>0x2B35B0</c>: while <c>*(s0+0x13A4)==0</c> SleepThread — plant 1 on object.</item>
    /// </list>
    /// Without these producers under HLE boot never opens game FILEIO (cdvd stuck at 425).
    /// </summary>
    private void MaybePlantBootWaitFlag(Ps2System sys)
    {
        if (!_lgDevFullyDone) return;
        if (sys.Cdvd.SectorsRead < 400) return;
        if (sys.MasterCycles - _lastBootWaitPlantCyc < 80_000) return;
        if (_bootWaitFlagPlants >= 1024) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        bool inWait1 = pc is >= 0x002B34C0 and <= 0x002B34E4;
        bool inWait2 = pc is >= 0x002B3510 and <= 0x002B3540;
        bool inWait3 = pc is >= 0x002B35A0 and <= 0x002B35C0;
        bool inSleep = pc is >= 0x0010C0A0 and <= 0x0010C0AC;
        // Dual post-LGDEV polls before STG bind (MENU-B3 residual samples 0x2AF750 first).
        bool inPostLgDevEarly = pc is >= 0x002AF750 and <= 0x002AF77C;
        bool inPostLgDevSpin = pc is >= 0x002AF800 and <= 0x002AF910;
        bool inPostLgDevWaitSema = pc is >= 0x0010BE60 and <= 0x0010BE70
            && ((uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL) is >= 0x002AF700 and <= 0x002AF910);
        // Keep planting until Global.txd/FRONTEND spine (cdvd≥2000) — STAGEHED plant alone
        // leaves cdvd=609 and used to stop periodic assist → stuck bitfield 0x2B45xx.
        bool periodic = (_menuKickPulses % 4) == 0 && sys.Cdvd.SectorsRead is >= 400 and < 2000;
        if (!inWait1 && !inWait2 && !inWait3 && !inSleep && !inPostLgDevSpin
            && !inPostLgDevEarly && !inPostLgDevWaitSema && !periodic) return;

        _lastBootWaitPlantCyc = sys.MasterCycles;
        _bootWaitFlagPlants++;

        uint gp = (uint)(sys.EE.GetGpr(28).Lo & 0x1FFFFFFFUL);
        if (gp is < 0x00400000 or >= 0x01000000)
            gp = 0x004E8670; // live retail gp

        // Wait-1: *(gp-23028)
        uint flag1 = unchecked((uint)((int)gp + BootWaitFlagGpOff));
        sys.Memory.Write32(flag1, 1);
        sys.Memory.Write32(BootWaitFlagDefault, 1);

        // Wait-2: *(gp-27128) / *(gp-27124) init to -1, producer must replace.
        // Plant 1 (ready handle) — 0 can be read as "failed/empty" by later checks.
        uint flag2a = unchecked((uint)((int)gp - 27128));
        uint flag2b = unchecked((uint)((int)gp - 27124));
        if (flag2a is >= 0x00400000 and < 0x01000000)
        {
            // Only overwrite the sentinel -1 (never clobber a real producer result).
            if (sys.Memory.Read32(flag2a) == 0xFFFFFFFFu)
                sys.Memory.Write32(flag2a, 1);
            if (sys.Memory.Read32(flag2b) == 0xFFFFFFFFu)
                sys.Memory.Write32(flag2b, 1);
        }

        // Wait-3: *(s0+0x13A4) — object-local ready flag after jal 0x2AEFC0 (a3=21).
        // Only plant when s0 looks like a real EE object in .data/bss (not high heap/stack).
        // Live tip residual: s0=0x01E7DDF0 heap cursor — writing +0x13A4 corrupted STG path.
        uint s0 = (uint)(sys.EE.GetGpr(16).Lo & 0x1FFFFFFFUL);
        if (s0 is >= 0x00400000 and < 0x01000000 && (s0 & 3) == 0)
        {
            sys.Memory.Write32(s0 + 0x13A4, 1);
            // Sibling cells used nearby (0x13A0 / 0x1380) — keep non-zero for follow-on.
            if (sys.Memory.Read32(s0 + 0x13A0) == 0)
                sys.Memory.Write32(s0 + 0x13A0, 1);
        }

        // Post-LGDEV dual flags: gp-23096 (0x2AF750, primary status 277) + gp-23104 (0x2AF80C).
        uint flag23096 = unchecked((uint)((int)gp - 23096));
        uint flag23104 = unchecked((uint)((int)gp - 23104));
        if (flag23096 is >= 0x00400000 and < 0x01000000)
            sys.Memory.Write32(flag23096, 277);
        if (flag23104 is >= 0x00400000 and < 0x01000000)
            sys.Memory.Write32(flag23104, 1);

        // Snap PC out of the active wait body.
        if (inWait1)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = 0x002B34E8;
            sys.EE.COP0_Status &= ~0x6u;
        }
        else if (inWait2)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // != -1
            sys.EE.PC = 0x002B356C; // success continue after first result check
            sys.EE.COP0_Status &= ~0x6u;
        }
        else if (inWait3 || (inSleep && s0 is >= 0x00100000 and < 0x02000000
                             && sys.Memory.Read32(s0 + 0x13A4) != 0))
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = 0x002B35C0; // past wait-3 → jal 0x2AFDD0
            sys.EE.COP0_Status &= ~0x6u;
        }
        else if (inPostLgDevEarly)
        {
            // Re-enter early poll with flag=277 + s0!=600 → natural 0x2AF7E0 continue.
            uint s0w = (uint)(sys.EE.GetGpr(16).Lo & 0xFFFFFFFFUL);
            if (s0w >= 600)
                sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 277 });
            sys.EE.PC = 0x002AF750;
            sys.EE.COP0_Status &= ~0x6u;
        }
        else if (inPostLgDevSpin || inPostLgDevWaitSema)
        {
            // Success leave: flag set + s0!=600 → v0=1 epi (NOT timeout 0x2AF920).
            uint s0w = (uint)(sys.EE.GetGpr(16).Lo & 0xFFFFFFFFUL);
            if (s0w >= 600)
                sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = 0x002AF914;
            sys.EE.COP0_Status &= ~0x6u;
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_bootWaitFlagPlants <= 12 || _bootWaitFlagPlants % 32 == 0))
            Console.Error.WriteLine(
                $"[B3] plant boot-wait flags pc=0x{pc:X8} s0=0x{s0:X8} " +
                $"*flag1=1 *s0+13A4=1 n={_bootWaitFlagPlants} cdvd={sys.Cdvd.SectorsRead} " +
                $"cyc={sys.MasterCycles}");
    }
}
