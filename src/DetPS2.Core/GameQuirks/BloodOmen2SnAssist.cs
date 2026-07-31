using System;

namespace DetPS2.Core;

/// <summary>
/// Blood Omen 2 (SLUS_200.24) — SN Systems ProDG + IOPRP module-load assist.
///
/// After CRT0 the title copies an SN runtime blob to <c>0x80076000</c>, then <c>main</c>
/// loads SN Debugger extensions by trapping into a handler that pattern-scans
/// <c>0x80000000..0x8007FFFF</c> for three prologues described by templates in high
/// <c>.text</c>. The blob's functions do not match those templates under HLE (and the
/// matching high-text prologues are outside the scan window), so <c>main</c> prints
/// "Can't load SN Debugger extensions.... bad medicine." and <c>Exit(1)</c>.
///
/// Past SN check the title reboots IOP with <c>IOPRP234.IMG</c> and loads disc IRX
/// (SIO2MAN/PADMAN/…). Two structural HLE gaps blocked that path:
/// <list type="number">
/// <item>cdrom short-name rewrite at <c>0x002DBF40</c> (jal 0x2DB138) collapses built
/// paths to a single letter ("c") under HLE, so <c>SifIopReset</c> sees
/// <c>rom0:UDNL c</c> and every <c>SifLoadModule</c> path is garbage.</item>
/// <item>IOPRP version cells at <c>0x536188/0x536190</c> stay as <c>"...."</c>; the
/// LOADFILE client gate at <c>0x48C938</c> then returns <c>0xFFFEFFFC</c> before any
/// <c>LF_F_MOD_LOAD</c> RPC.</item>
/// </list>
/// Prefer real CD short-name resolution + IOPRP version handoff when available; until
/// then these plants are the minimal structural HLE to reach MOD_LOAD.
/// </summary>
public sealed class BloodOmen2SnAssist : IGameQuirkModule
{
    public string Serial => "SLUS_200.24";
    public string DisplayName => "Blood Omen 2 (USA)";

    /// <summary>KSEG0 plant base — below SN blob @0x80076000, inside the 0x80000000..0x8007FFFF scan window.</summary>
    public const uint PlantKseg0Base = 0x8006F000;

    /// <summary>Game EE BSS cells that should hold IOPRP version "2340" after SifIopReset(IOPRP234).</summary>
    public const uint IopVersionCellA = 0x00536188;
    public const uint IopVersionCellB = 0x00536190;

    /// <summary>cdrom path-combine short-name rewrite call site (jal 0x2DB138).</summary>
    public const uint CdromShortNameJal = 0x002DBF40;

    private bool _planted;
    private bool _blobSeen;
    private bool _iopPathFixed;

    public void Reset()
    {
        _planted = false;
        _blobSeen = false;
        _iopPathFixed = false;
        _lastPulseCyc = 0;
        _lastTitleSmCyc = 0;
        _snQuietPatches = 0;
        _padInjectPulses = 0;
        _titlePadPulses = 0;
        _lastPadInjectCyc = 0;
        _titleSmEscapes = 0;
        _menuDrawKicks = 0;
        _cacheFlushSkips = 0;
        _snPrintfStubbed = false;
        _vtCallStubbed = false;
        _entityPrintfGlueStubbed = false;
        _fmtScanStubbed = false;
        _goeTokenEscapes = 0;
        _codeOpenNudges = 0;
        _inMapEscapes = 0;
        _useBigfileForces = 0;
        _useBigfileForced = false;
        _useBigfileResumePending = false;
        _useBigfileSavedPc = 0;
        _useBigfileSavedGpr = null;
        _droveCodeBg2 = false;
        _droveMainmenuBg2 = false;
        _streamedCodeBg2 = false;
        _streamedMainmenuBg2 = false;
        _mainLayerForces = 0;
        _postEnglishDrawKicks = 0;
        _sawListTxt = false;
        _sawEnglishDir = false;
        _listWalkStubbed = false;
        _entityParseLeaves = 0;
        _displaySpineKicks = 0;
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        // ELF load happens after OnDiscMounted and rewrites .text — only plant RDRAM
        // stubs that live outside the PT_LOAD window here. Code patches must re-apply
        // in Step() once the boot ELF is resident (see ApplyPostElfPatches).
        if (sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = true;
        PlantSnExtensionStubs(sys);
        ForceSnScanSuccess(sys);
        PlantIopRpVersion(sys);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BO2") == "1")
            Console.Error.WriteLine("[BO2-SN] OnDiscMounted: low-RDRAM SN stubs + IOPRP version cells");
    }

    /// <summary>
    /// Re-apply EE .text patches after the retail ELF has been loaded (PT_LOAD overwrites
    /// anything written in OnDiscMounted). Idempotent.
    /// </summary>
    public static void ApplyPostElfPatches(Ps2System sys)
    {
        PatchSnLoadToSucceed(sys);
        PatchCdromPathCombine(sys);
        PlantIopRpVersion(sys);
        ForceSnScanSuccess(sys);
    }

    /// <summary>
    /// <c>0x00463008</c> is <c>TEQ $0,$0</c> then load result from <c>0x5672EC</c>.
    /// Replace with <c>li v0,1; jr ra; nop</c> so main's <c>bnel v0,zero,ok</c> takes the success path.
    /// </summary>
    public static void PatchSnLoadToSucceed(Ps2System sys)
    {
        // 0x00463008 was the TEQ entry used by jal 0x00463008 from the wrapper.
        // Wrapper at 0x00463018 does SetVCommonHandler then jal 0x00463008.
        // Patch the TEQ gadget itself:
        sys.Memory.Write32(0x00463008, 0x24020001u); // addiu v0, zero, 1
        sys.Memory.Write32(0x0046300C, 0x03E00008u); // jr ra
        sys.Memory.Write32(0x00463010, 0x00000000u); // nop
        // Also force result cells for any other reader.
        ForceSnScanSuccess(sys);
    }

    public void OnHostPresent(Ps2System sys)
    {
        // PL-015: keep PADMAN dual-buffer DMA STABLE so EE padGetState/padRead see
        // host inject between VBlank ticks (title-FB interactive residual).
        try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Nop the cdrom short-name rewrite <c>jal 0x2DB138</c> inside path combine.
    /// Under HLE that helper collapses <c>cdrom0:IOPRP234.IMG</c> / IRX paths to a single
    /// letter, which makes every subsequent SifIopReset / SifLoadModule fail.
    /// </summary>
    public static void PatchCdromPathCombine(Ps2System sys)
    {
        // Make short-name rewrite (0x2DB138) an identity: jr ra; nop.
        // Under HLE the real helper collapses built paths to a single letter ("c").
        // Callers keep their input buffer intact instead of a truncated result.
        sys.Memory.Write32(0x002DB138, 0x03E00008u); // jr ra
        sys.Memory.Write32(0x002DB13C, 0x00000000u); // nop
        // Restore jal if a prior session nop'd it.
        if (sys.Memory.Read32(CdromShortNameJal) == 0)
            sys.Memory.Write32(CdromShortNameJal, 0x0C0B6C4Eu); // jal 0x2DB138
    }

    /// <summary>
    /// Plant IOPRP 2.3.4 version tag the LOADFILE client compares after GetVersion.
    /// Real hardware fills these when UDNL applies IOPRP234.IMG; HLE has no UDNL image apply.
    /// </summary>
    public static void PlantIopRpVersion(Ps2System sys)
    {
        // ASCII "2340\0" — matches rodata expectation at 0x49B944 and *0x49C1AC / *0x49C1A0.
        WriteCString4(sys, IopVersionCellA, "2340");
        WriteCString4(sys, IopVersionCellB, "2340");
        // Reboot arg buffer at 0x5361A0 is built by a path-combine that under HLE leaves only
        // "rom0:UDNL \0" (arglen=11). Real string is "rom0:UDNL cdrom0:\IOPRP234.IMG;1".
        // Only rewrite when truncated/wrong — never append (live doubled path observed).
        const string FullArg = "rom0:UDNL cdrom0:\\IOPRP234.IMG;1";
        string cur = ReadCStringSimple(sys, 0x005361A0, 80);
        if (cur.Length < 20 || !cur.Contains("IOPRP234", StringComparison.OrdinalIgnoreCase)
            || cur.IndexOf("IOPRP234", StringComparison.OrdinalIgnoreCase)
               != cur.LastIndexOf("IOPRP234", StringComparison.OrdinalIgnoreCase))
            WriteCString(sys, 0x005361A0, FullArg);
    }

    private static string ReadCStringSimple(Ps2System sys, uint addr, int max)
    {
        var sb = new System.Text.StringBuilder(Math.Min(max, 64));
        for (int i = 0; i < max; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static void WriteCString4(Ps2System sys, uint addr, string s)
    {
        for (int i = 0; i < 4; i++)
            sys.Memory.Write8(addr + (uint)i, i < s.Length ? (byte)s[i] : (byte)0);
    }

    private static void WriteCString(Ps2System sys, uint addr, string s)
    {
        for (int i = 0; i < s.Length; i++)
            sys.Memory.Write8(addr + (uint)i, (byte)s[i]);
        sys.Memory.Write8(addr + (uint)s.Length, 0);
    }

    public void Step(Ps2System sys)
    {
        ulong c = sys.Scheduler.MasterCycles;
        uint pc = (uint)sys.EE.PC;

        // Re-apply .text patches as soon as the boot ELF is mapped (entry is in high .text).
        // OnDiscMounted runs before BootDiscFile's ElfLoader, so earlier patches are wiped.
        if (!_iopPathFixed && c >= 1_000)
        {
            // Confirm ELF resident: first word of path-combine site is a real opcode, not zero.
            uint probe = sys.Memory.Read32(CdromShortNameJal);
            if (probe != 0)
            {
                ApplyPostElfPatches(sys);
                _iopPathFixed = true;
                _planted = true;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BO2") == "1")
                    Console.Error.WriteLine($"[BO2-SN] post-ELF patches applied cyc={c} jalWas=0x{probe:X8}");
            }
        }

        // Keep result cells non-zero once planted so a late scan overwrite to 0 still loses
        // to the next Step (25k-cycle poll). Return path loads 0x5672EC after TEQ.
        if (_planted)
        {
            ForceSnScanSuccess(sys);
            // Version cells must stay "2340" across SifIopReset (game zeros them at 0x48C9C8).
            PlantIopRpVersion(sys);
            // Post-SN-check boot parks thread 1 on WaitSema(id) with no producer yet
            // (SN ProDG / disc gate). Periodically SignalSema any waiter so SIF/CDVD progress.
            PulseWaiters(sys);
            // PL-015: pad inject is NOT rate-gated by PulseWaiters interval — title FB
            // consumers sample pad between thrash-residual slices.
            MaybeInjectInteractivePad(sys);
            // If main is on the fail branch that does li v0,1 / Exit, nudge past by forcing v0.
            if (pc is >= 0x00297DA0 and <= 0x00297DC8)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = PlantKseg0Base }); // v0 = success pointer
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BO2") == "1")
                    Console.Error.WriteLine($"[BO2-SN] force v0 success at PC=0x{pc:X8} cyc={c}");
            }
            return;
        }
        // Wait until CRT0 has had time to Copy the SN blob to 0x80076000 (~300–700k cycles).
        if (c < 200_000) return;

        // Detect blob: first word at 0x80076000 becomes non-zero after Copy.
        uint blob0 = sys.Memory.Read32(0x80076000);
        if (blob0 != 0)
            _blobSeen = true;

        // Plant once blob is present, or emergency after 500k even if detection missed.
        if (!_blobSeen && c < 500_000) return;

        PlantSnExtensionStubs(sys);
        ForceSnScanSuccess(sys);
        ApplyPostElfPatches(sys);
        _planted = true;
        _iopPathFixed = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BO2") == "1")
            Console.Error.WriteLine($"[BO2-SN] planted SN stubs + post-ELF patches @ 0x{PlantKseg0Base:X8} cyc={c}");
    }

    /// <summary>
    /// Publish a non-zero SN-extension bind result so <c>main</c> does not Exit(1).
    /// Points at the planted stubs in the low-mem scan window.
    /// </summary>
    public static void ForceSnScanSuccess(Ps2System sys)
    {
        // Result cells observed in the SN load path (scan success + return load).
        sys.Memory.Write32(0x005672E8, PlantKseg0Base);
        sys.Memory.Write32(0x005672EC, PlantKseg0Base);
    }

    private ulong _lastPulseCyc;
    private ulong _lastTitleSmCyc;
    private ulong _lastPadInjectCyc;
    private int _snQuietPatches;
    private int _padInjectPulses;
    private int _titlePadPulses;
    private int _titleSmEscapes;
    private int _menuDrawKicks;

    /// <summary>
    /// Wake EE threads blocked on WaitSema so boot can advance when the real SN/IOP
    /// producer is only partially modeled. Rate-limited to avoid thrash.
    /// Also deci2/SN debug print storms (SIFCMD cid=ASCII "****") park on fresh semas —
    /// keep pulsing so PC can leave 0x488898 toward asset/menu code (0x2CD7E0 observed).
    /// After first disc I/O, inject pad activity so title/menu code that waits for
    /// controller ready / START can proceed.
    /// </summary>
    private void PulseWaiters(Ps2System sys)
    {
        ulong c = sys.Scheduler.MasterCycles;
        // PulseWaiters is backup for CompleteRpcEnd. After RKV/GOE (cdvd≈350) do NOT
        // SignalSema sleepers — CompleteRpcEnd + STALLING own RPC leave. Extra SignalSema
        // during SN "Manager State" races half-updated CallRpc frames → thrash 0x5387xx.
        // Keep thrash-rescue interval short (50k) so jalr→string storms are planted/stubbed
        // before they burn tens of M cycles; pre-RKV still needs structural pulses.
        ulong interval = sys.Cdvd.SectorsRead >= 100
            ? (sys.Cdvd.SectorsRead >= 350 ? 50_000UL : 80_000UL)
            : 250_000UL;
        if (c - _lastPulseCyc < interval) return;
        _lastPulseCyc = c;
        var k = sys.Hle.Kernel;
        bool postGoe = sys.Cdvd.SectorsRead >= 350;
        foreach (var t in k.AllThreads)
        {
            if (!t.Alive || !t.Sleeping) continue;
            if (t.WaitSemaId > 0)
            {
                if (postGoe) continue; // CompleteRpcEnd only
                k.SignalSema(t.WaitSemaId);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BO2") == "1")
                    Console.Error.WriteLine($"[BO2-SN] SignalSema({t.WaitSemaId}) for tid={t.Id} cyc={c}");
            }
            else if (!t.WaitVblank)
            {
                // Pure SleepThread park — WakeupThread, not SignalSema(0).
                k.WakeupThread(t.Id);
            }
        }

        // Soft-quiet SN ProDG T10000 printf channel: if EE is spinning the WaitSema RPC
        // complete at 0x488894 with no progress for long runs, ensure version cells stay
        // planted (already done) and SN load result stays non-zero.
        if (_snQuietPatches < 8 && c >= 10_000_000)
        {
            ForceSnScanSuccess(sys);
            PlantIopRpVersion(sys);
            _snQuietPatches++;
        }

        // WAVE 3 (warm no sector credit → cdvd plateaus ≈380 after RKV/GOE 0x29):
        // After bind 0x29 the title floods SN ProDG "Manager State" via CallRpc sid=0x534E03.
        // Historical rkv-final interleaved that storm with FILEIO (KAIN.IMP / ENGLISH.DIR).
        // Live thrash root: 0x166424 jalr through object+100 into goefile strings (0x5387xx).
        // Plant that jalr→li v0,0 *before* the first thrash so Manager State can finish and
        // reach FILEIO. Do NOT soft-stub SN printf early (needs ~30–50 CallRpc for interleave).
        // Do NOT force-leave WaitSema@0x488894 (menu14–19 thrash class). CompleteRpcEnd owns RPC.
        // WAVE-6: IsPreMainmenuSurface — logo Soft-GS ~71k must not gate thrash escapes.
        if (sys.Cdvd.SectorsRead >= 350 && IsPreMainmenuSurface(sys))
            SoftStubBadVtCall(sys);

        // Post-KAIN pack-resident open (honest RealSifRpc.Bo2PackResidentOpens) — not faked
        // CODE/MAINMENU sector credit. Soft-stub SN printf so Dest Database cannot monopolize.
        // Keep cdvd>=500 as legacy fallback if pack opens were served without the counter.
        bool postPackAsset = HasBo2PackAssetIo(sys);
        if (postPackAsset && IsPreMainmenuSurface(sys))
            SoftStubSnPrintf(sys);

        // Post-KAIN goefile token thrash @0x4830xx - unwind frame toward CODE/MAINMENU.
        if (postPackAsset && IsPreMainmenuSurface(sys))
            MaybeEscapeGoeFileTokenThrash(sys, c);

        // Post-KAIN: suppress entity Dest-Database printf glue (format+SN) so cycles leave
        // mid-wrapper park @0x480500. Do NOT soft-stub game printf 0x2B99B8 (host AV).
        // Do NOT permanent-stub shared format leaf/wrapper (breaks InMap 0x485318).
        if (postPackAsset && IsPreMainmenuSurface(sys))
            SoftStubEntityPrintfChain(sys);

        // Post-KAIN InMap a1==0 / bad vtable jalr — leave the destination helper instead of
        // rescue-looping mid 0x2B9F34 (honest wall after format plants were removed).
        if (postPackAsset && IsPreMainmenuSurface(sys))
            MaybeEscapeInMapNullDest(sys, c);

        // Post-InMap residual: bit-pack / multiprecision heat @0x479E30 / 0x47A0xx burns
        // tens of M with no CODE.BG2 Open. Soft-leave via $ra so usebigfile / StartBigFile
        // can run (member extract alone does not open CODE).
        // WAVE-6: also leave bit-pack when pack-resident but InMap never counted (natural
        // pass or missed null-dest) — live tip heat is pure 0x479E30 with _inMapEscapes=0.
        if (postPackAsset && IsPreMainmenuSurface(sys)
            && (_inMapEscapes > 0 || IsInBitPackHeat(sys)))
            MaybeEscapePostEntityBitPack(sys, c);

        // WAVE-3/4: force real usebigfile / "Starting code big file" Open path.
        // Pack-member KAIN alone never issues CODE.BG2 / MAINMENU.BG2 game Open.
        // WAVE-4: correct ELF PCs (w3 was off-by-0x1000) + stream packs into EE + main layer.
        // WAVE-6: allow force when post-pack residual (InMap leave OR bit-pack heat).
        if (postPackAsset && IsPreMainmenuSurface(sys)
            && (_inMapEscapes > 0 || _codeOpenNudges > 0 || IsInBitPackHeat(sys)))
        {
            MaybeResumeAfterForcedUseBigfile(sys);
            MaybeForceUseBigfileOpen(sys, c);
            // If EE force is mid-flight or returned without Open, drive real CODE/MAINMENU
            // via Open+FileRead stream (countSectors:true) — open alone never draws.
            MaybeDriveGameBg2Open(sys, c);
            MaybeKickCreatingMainLayer(sys, c);
        }

        // After GOE/RKV (cdvd≈300+ without host-warm inflation), main sometimes ends
        // started=False — re-start so boot can continue past RPC-complete plateau.
        if (sys.Cdvd.SectorsRead >= 200)
        {
            foreach (var t in k.AllThreads)
            {
                if (!t.Alive || t.Entry == 0) continue;
                if (!t.Started && (t.Id == 1 || t.Entry is >= 0x002C0000 and <= 0x004A0000))
                {
                    try
                    {
                        k.StartAndMaybeSwitch(sys.EE, t.Id, switchNow: t.Id == 1, arg: 0, fromSyscall: false);
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                            && _menuDrawKicks < 8)
                            Console.Error.WriteLine(
                                $"[BO2] re-start tid={t.Id} entry=0x{t.Entry:X8} cyc={c}");
                    }
                    catch { /* ignore */ }
                }
            }
        }

        // Post-GOE: if SN storm still lands in goefile string tables (0x5387xx), soft-stub
        // SN and rescue. Prefer $ra/stack; last-good boot PC; cold 0x48A980 only as fallback.
        // Do not rescue while a usebigfile force-call is in flight (WAVE-3).
        if (!_useBigfileResumePending
            && sys.Cdvd.SectorsRead >= 350 && IsPreMainmenuSurface(sys))
        {
            uint pcBad = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
            if (pcBad is >= 0x00A00000 or (>= 0x004A0000 and < 0x02000000)
                || pcBad is >= 0x001C0000 and <= 0x001C1000)
            {
                SoftStubBadVtCall(sys);
                // Prefer return-from-jalr (ra=0x16642C) after vt plant. Never cold-enter
                // 0x48BCD0 (re-thrash). Soft-stub SN only after pack-resident asset I/O so
                // Manager State CallRpc storm can still complete and open KAIN.IMP.
                if (HasBo2PackAssetIo(sys))
                    SoftStubSnPrintf(sys);
                uint raDump = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
                uint spDump = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && _titleSmEscapes < 4)
                    Console.Error.WriteLine(
                        $"[BO2] thrash-frame pc=0x{pcBad:X8} ra=0x{raDump:X8} sp=0x{spDump:X8} " +
                        $"lastGood=0x{sys.LastGoodEePc:X8} cyc={c}");

                uint resume;
                if (raDump is >= 0x00166420 and <= 0x00166440)
                    resume = raDump;
                else if (IsColdSafeResume(sys, raDump) && raDump != pcBad
                         && raDump is < 0x00A00000)
                    resume = raDump;
                else
                {
                    resume = PickSafeResume(sys, pcBad);
                    if (resume == 0 || resume == pcBad
                        || resume is >= 0x0048AF00 and <= 0x0048C800
                        || resume is >= 0x00A00000)
                        resume = 0x0048A980;
                    if (!IsSafeCodeTarget(sys, resume) || resume == pcBad)
                        resume = 0x0048A980;
                    if (!IsColdSafeResume(sys, raDump) || raDump == pcBad || raDump >= 0x00A00000)
                        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
                }
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                ArmGifPath3(sys);
                _titleSmEscapes++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_titleSmEscapes <= 12 || _titleSmEscapes % 16 == 0))
                    Console.Error.WriteLine(
                        $"[BO2] rescue post-GOE data thrash 0x{pcBad:X8} -> 0x{resume:X8} " +
                        $"(vt-stub) cyc={c}");
            }
        }

        // Pad inject moved to MaybeInjectInteractivePad (called every planted Step) so
        // PL-015 title-FB edges are not starved by the 50k thrash-residual interval.

        // Exception vector rescue (always safe post GOE). Data/NOP rescue after RKV token.
        if (sys.Cdvd.SectorsRead >= 200)
        {
            uint pcNow = (uint)(sys.EE.PC & 0xFFFFFFFFUL);
            if (pcNow is >= 0x80000180 and <= 0x80000200 || (pcNow & 0x1FFFFFFFu) < 0x00100000u)
            {
                uint resume = PickSafeResume(sys, pcNow & 0x1FFFFFFFu);
                if (resume == 0) resume = 0x0048A980; // post-flush init (not mid-RPC complete)
                sys.EE.COP0_Status &= ~0x6u;
                sys.EE.PC = resume;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && _titleSmEscapes < 8)
                    Console.Error.WriteLine(
                        $"[BO2] rescue exception vector -> 0x{resume:X8} cyc={c}");
                _titleSmEscapes++;
                _lastTitleSmCyc = c;
            }
            else if (sys.Cdvd.SectorsRead >= 350)
                MaybeRescueBadPc(sys, c);
        }
        // Cache-flush leaf after RKV token (cdvd≈380). HLE has no real cache.
        if (sys.Cdvd.SectorsRead >= 350)
            MaybeSkipCacheFlush(sys, c);
        // Menu-draw kick once pack-resident / post-RKV asset I/O is live (not faked cdvd).
        // WAVE-6: IsPreMainmenuSurface covers logo Soft-GS ~71k + post-stream residual.
        if (HasBo2PackAssetIo(sys) && IsPreMainmenuSurface(sys))
            MaybeKickMenuDraw(sys, c);
    }

    /// <summary>True when EE PC is in the multiprecision / bit-pack heat band (post-entity).</summary>
    private static bool IsInBitPackHeat(Ps2System sys)
    {
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        return pc is >= 0x00479E00 and <= 0x0047A280;
    }

    /// <summary>
    /// True after first BO2 pack-resident open (KAIN.IMP etc.) or legacy cdvd≥500 fallback.
    /// Prefer <see cref="RealSifRpc.Bo2PackResidentOpens"/> over inflated CODE/MAINMENU notes.
    /// </summary>
    private static bool HasBo2PackAssetIo(Ps2System sys)
    {
        int packOpens = sys.Hle.Sony?.RealRpc.Bo2PackResidentOpens ?? 0;
        int gameBg2 = sys.Hle.Sony?.RealRpc.Bo2GameBg2Opens ?? 0;
        return packOpens > 0 || gameBg2 > 0 || sys.Cdvd.SectorsRead >= 500;
    }

    /// <summary>
    /// PL-015: pad inject past ofx title-surface Soft-GS (INTERACTIVE residual).
    ///
    /// Pre-title: Start/Cross duty cycle after first disc I/O (boot pad open path).
    /// Post-title (CODE+MAINMENU streamed + ofx FB px≥250k): denser Start/Cross/Circle/D-pad
    /// with release edges + dualshock AnalogMode + immediate <see cref="RealSifRpc.ForceRefreshPad"/>
    /// so PADMAN dual-buffer DMA (OPEN @0x540740/0x540880 live) sees presses without waiting
    /// for the next VBlank tick. Never invents menu pixels / selection index plants.
    /// </summary>
    private void MaybeInjectInteractivePad(Ps2System sys)
    {
        if (sys.Cdvd.SectorsRead < 100) return;
        if (_padInjectPulses >= 16384) return;

        ulong c = sys.Scheduler.MasterCycles;
        long streamed = sys.Hle.Sony?.RealRpc.Bo2GameBg2StreamedBytes ?? 0;
        long px = sys.Gs.PixelsWritten;
        // Title-surface Soft-GS (ofx=0x8000 full FB 286720) after CODE+MAINMENU stream.
        bool titleFb = streamed > 1_000_000 && px >= 250_000;
        // Pre-title: 20k (was buried in 50k PulseWaiters). Post-title: 8k for edge density.
        ulong padInterval = titleFb ? 8_000UL : 20_000UL;
        if (c - _lastPadInjectCyc < padInterval) return;
        _lastPadInjectCyc = c;
        _padInjectPulses++;

        // PadInput uses active-high Press bits; RealSifRpc WritePadButtonData inverts to
        // active-low padButtonStatus for DMA.
        uint buttons;
        if (titleFb)
        {
            int p = _padInjectPulses % 10;
            buttons = p switch
            {
                0 or 1 => (uint)PadInput.Button.Start,
                2 => (uint)(PadInput.Button.Start | PadInput.Button.Cross),
                3 or 4 => (uint)PadInput.Button.Cross,
                5 => (uint)PadInput.Button.Circle,
                6 => (uint)PadInput.Button.Down,
                7 => (uint)PadInput.Button.Up,
                8 => (uint)(PadInput.Button.Start | PadInput.Button.Circle),
                _ => 0u, // release so edge-triggered readers see press→release
            };
            // Hold START longer in a window so Press-START consumers can latch.
            if ((_padInjectPulses % 16) < 4)
                buttons = (uint)PadInput.Button.Start;
            _titlePadPulses++;
        }
        else
        {
            int phase = _padInjectPulses % 5;
            buttons = phase switch
            {
                0 or 1 => (uint)PadInput.Button.Start,
                2 or 3 => (uint)PadInput.Button.Cross,
                _ => 0u,
            };
            if (_padInjectPulses % 11 == 0)
                buttons = (uint)(PadInput.Button.Start | PadInput.Button.Cross);
        }

        try
        {
            sys.Pad.AnalogMode = true;
            sys.Pad.SetButtons(buttons);
            sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad);
        }
        catch { /* Pad / RealRpc may be null early */ }

        if (titleFb && Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_titlePadPulses <= 8 || _titlePadPulses % 64 == 0))
        {
            int opens = sys.Hle?.Sony?.RealRpc?.OpenPadCount ?? 0;
            Console.Error.WriteLine(
                $"[BO2] title-FB pad inject n={_titlePadPulses} btn=0x{buttons:X4} " +
                $"opens={opens} px={px} prims={sys.Gs.PrimitivesDrawn} " +
                $"gifP2={sys.Gif?.Path2Transfers ?? 0} cyc={c}");
        }
    }

    /// <summary>
    /// WAVE-6: Soft-GS still pre-mainmenu. Main-tip Mul80/AFAIL Soft-GS paints a logo-class
    /// clear of ~71k px (prims=1) early in boot. WAVE-3..5 thrash escapes gated at px&lt;50k
    /// never fired under that chrome → stuck forever in bit-pack @0x479E30 with no
    /// CODE/MAINMENU stream. Logo-class / sparse-prim Soft-GS is still pre-menu.
    /// WAVE-7: title-surface Soft-GS (ofx=0x8000 full FB 286720) after CODE+MAINMENU stream
    /// is MENU-class chrome for claims (Whiplash wave-6 class), but thrash residual stays
    /// live until multi-prim / richer Soft-GS so freelist/ETP path can still advance.
    /// </summary>
    private static bool IsPreMainmenuSurface(Ps2System sys)
    {
        long px = sys.Gs.PixelsWritten;
        long streamed = sys.Hle.Sony?.RealRpc.Bo2GameBg2StreamedBytes ?? 0;
        long prims = sys.Gs.PrimitivesDrawn;
        // Rich multi-prim Soft-GS past logo — thrash residual may rest.
        if (streamed > 1_000_000 && px >= 500_000 && prims > 8)
            return false;
        if (px < 250_000)
            return true;
        // Sparse logo clear only (no CODE/MAINMENU stream yet).
        if (streamed == 0 && prims <= 4)
            return true;
        // Post-stream: keep residual while prims sparse (title FB alone is MENU-class
        // Soft-GS for claims, but freelist/ETP thrash still needs soft-leave).
        if (streamed > 0 && prims <= 8)
            return true;
        if (streamed > 0 && px < 500_000)
            return true;
        return false;
    }

    private bool _snPrintfStubbed;
    private bool _vtCallStubbed;
    private bool _entityPrintfGlueStubbed;
    private bool _fmtScanStubbed;

    private int _goeTokenEscapes;
    private int _codeOpenNudges;
    private int _inMapEscapes;
    private int _useBigfileForces;
    private bool _useBigfileForced;
    private bool _useBigfileResumePending;
    private ulong _useBigfileSavedPc;
    private ulong[]? _useBigfileSavedGpr;

    /// <summary>
    /// Scratch trampoline for non-destructive force into usebigfile / Starting-code path.
    /// Same pattern as MidwayBootAssist force-calls (self-loop; resume restores GPRs/PC).
    /// </summary>
    private const uint UseBigfileReturnTrampoline = 0x01FE0040;

    /// <summary>
    /// WAVE-4 ELF ground-truth (SLUS_200.24): PreCode printf + StartBigFile body jal
    /// (<c>0x346E48</c>). WAVE-3 used <c>0x1B6708</c> (off by 0x1000 — mid wrong function).
    /// </summary>
    private const uint PreCodeBigFilePc = 0x001B5708;

    /// <summary>
    /// WAVE-4: "Starting code big file" printf + jal <c>0x346DF8</c> (StartBigFile wrapper).
    /// WAVE-3 constant <c>0x1B6798</c> was off-by-0x1000 and never reached FILEIO Open.
    /// </summary>
    private const uint StartingCodeBigFilePc = 0x001B5798;

    /// <summary>
    /// WAVE-4: "Finished code big file" continue (post StartBigFile jal).
    /// </summary>
    private const uint FinishedCodeBigFilePc = 0x001B57AC;

    /// <summary>
    /// WAVE-6: boot spine after Finished-code printf — <c>jal 0x339DC8</c> (display/object
    /// setup). Creating main layer returns here so Soft-GS residual can reach GIF submit.
    /// </summary>
    private const uint PostFinishedCodeContinuePc = 0x001B57B8;

    /// <summary>
    /// WAVE-4/5: "Creating main layer" — true function entry (addiu sp,-32). WAVE-4 used
    /// <c>0x1B5AC4</c> (mid-prologue after stack alloc). Soft-GS residual needs correct frame.
    /// </summary>
    private const uint CreatingMainLayerPc = 0x001B5AC0;

    /// <summary>
    /// WAVE-5: EI helper w4 residual parks at after short-circuit 0x48A980 re-entry
    /// (<c>*0x4AC108 != 0</c> → j EI). Real code, but dead-ra loops here with px=3.
    /// </summary>
    private const uint EiHelperPc = 0x0048CF50;

    /// <summary>
    /// WAVE-4: StartBigFile wrapper called from Starting-code path (not 0x346DE0 epilogue).
    /// </summary>
    private const uint StartBigFileWrapperPc = 0x00346DF8;

    /// <summary>
    /// WAVE-4: StartBigFile body (PreCode path jal target).
    /// </summary>
    private const uint StartBigFileBodyPc = 0x00346E48;

    /// <summary>
    /// Prefer Starting-code straight-line when object state is weak; PreCode needs s6 path.
    /// Boot function entry is large — mid-path constants above are the safe force targets.
    /// </summary>
    private const uint UseBigfileBootFn = StartingCodeBigFilePc;

    /// <summary>
    /// EE plant destinations past PRECODE load @0xA242A0 (172028 B). CODE ~914 KiB,
    /// MAINMENU ~1.5 MiB — high RDRAM so factory/goefile parsers can see real bytes.
    /// </summary>
    private const uint CodeBg2EeDest = 0x00B00000;
    private const uint MainmenuBg2EeDest = 0x00C00000;

    /// <summary>
    /// Unstick post-KAIN format thrash toward CODE/MAINMENU Open.
    ///
    /// Live (2026-07-31): pure thrash-only escape failed — Step is 50k-cycle sliced and
    /// deep '%' scan (0x483048/0x486EC0) + bulk memcpy (0x4803EC) burn tens of M with
    /// no reliable stack ra. Permanent soft-stub of the format LEAF only (0x482F60)
    /// after pack-resident open kills the heat; wrapper/bridge stay intact.
    /// InMap 0x485318 then completes (leaf returns 0) and MaybeEscapeInMapNullDest
    /// leaves the a1==0 / bad-jalr wall that previously rescue-looped at 0x2B9F34.
    /// Entity Dest-Database glue 0x2AD8E0 remains soft-stubbed.
    ///
    /// Residual (#17/#8): still no proven game FILEIO/IOPFILE Open of CODE.BG2 /
    /// MAINMENU.BG2 after entity path. Pack path now uses goefile member extract.
    /// </summary>
    private void MaybeEscapeGoeFileTokenThrash(Ps2System sys, ulong c)
    {
        // Permanent format leaf stub after pack open — only path that stops 0x483048 heat
        // under 50k-cycle Step slices. Wrapper/bridge NOT stubbed (InMap 0x485318).
        if (!_fmtScanStubbed && HasBo2PackAssetIo(sys))
        {
            uint head = sys.Memory.Read32(0x00482F60);
            if (head != 0 && head != 0x03E00008u)
            {
                sys.Memory.Write32(0x00482F60, 0x03E00008u); // jr ra
                sys.Memory.Write32(0x00482F64, 0x0000102Du); // daddu v0, zero, zero
                _fmtScanStubbed = true;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine(
                        "[BO2] soft-stub format leaf @ 0x482F60 (jr ra; v0=0; wrapper intact)");
            }
        }

        if (_goeTokenEscapes >= 96) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        uint a2 = (uint)(sys.EE.GetGpr(6).Lo & 0x1FFFFFFFUL);

        // Soft-stubbed leaf is two words — let jr ra complete.
        if (_fmtScanStubbed && pc is >= 0x00482F60 and <= 0x00482F68)
            return;

        // Bulk byte-copy thrash near format lib (live heat 0x4802E8 + 0x4803F4): when remaining
        // count (a2) is absurd, force return. WAVE-6: cover full byte-copy leaf (0x4802E0..).
        // Do NOT treat rem=0xFFFFFFFF (-1 sentinel) as huge thrash — claim2 aborted those and
        // broke LIST→ENGLISH timing / GAMEKEEPER load.
        if (pc is >= 0x004802E0 and <= 0x00480410)
        {
            uint rem = (uint)(sys.EE.GetGpr(6).Lo & 0xFFFFFFFFUL);
            // Positive absurd sizes only (exclude -1 / high bit set).
            if (rem is > 0x200000u and < 0x80000000u)
            {
                uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
                if (!IsSafeCodeTarget(sys, ra) || !IsColdSafeResume(sys, ra) || ra == pc)
                    ra = 0x0048A980;
                sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = ra;
                sys.EE.COP0_Status &= ~0x6u;
                _goeTokenEscapes++;
                _titleSmEscapes++;
                _lastTitleSmCyc = c;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_goeTokenEscapes <= 12 || _goeTokenEscapes % 8 == 0))
                    Console.Error.WriteLine(
                        $"[BO2] abort huge memcpy thrash rem=0x{rem:X8} -> 0x{ra:X8} n={_goeTokenEscapes} cyc={c}");
                return;
            }
        }

        // Real format epilogue — never interrupt mid-restore.
        if (pc is >= 0x00484448 and <= 0x00484478)
            return;

        // Entry gate residual (if leaf plant not yet applied this slice).
        if (pc is >= 0x00482F60 and <= 0x00482FA0 && LooksLikeBo2BinaryFmtPtr(a2))
        {
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            if (IsSafeCodeTarget(sys, ra) && IsColdSafeResume(sys, ra) && ra != pc)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = ra;
                sys.EE.COP0_Status &= ~0x6u;
                _goeTokenEscapes++;
                _titleSmEscapes++;
                _lastTitleSmCyc = c;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_goeTokenEscapes <= 16 || _goeTokenEscapes % 16 == 0))
                    Console.Error.WriteLine(
                        $"[BO2] reject binary format entry a2=0x{a2:X8} -> ra=0x{ra:X8} " +
                        $"n={_goeTokenEscapes} cyc={c}");
                return;
            }
        }

        if (c - _lastTitleSmCyc < 40_000) return;

        // Mid format-wrapper body (0x4804F0..0x480534): frame addiu sp,-144 / ra@0(sp).
        bool inFmtWrapper = pc is > 0x004804F0 and < 0x00480538;
        if (inFmtWrapper && LooksLikeBo2BinaryFmtPtr(a2))
        {
            uint spW = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
            if (spW is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 0x90u)
            {
                uint raSlot = sys.Memory.Read32(spW + 0x0) & 0x1FFFFFFFu;
                if (raSlot is >= 0x00200000 and < 0x004A0000
                    && raSlot is not (>= 0x004804E8 and <= 0x00480538)
                    && raSlot is not (>= 0x00482E00 and <= 0x00486F00)
                    && IsSafeCodeTarget(sys, raSlot)
                    && IsColdSafeResume(sys, raSlot))
                {
                    sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = spW + 0x90 });
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                    sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = raSlot });
                    sys.EE.PC = raSlot;
                    sys.EE.COP0_Status &= ~0x6u;
                    ArmGifPath3(sys);
                    EnsureMainThreadRunning(sys);
                    _goeTokenEscapes++;
                    _codeOpenNudges++;
                    _titleSmEscapes++;
                    _lastTitleSmCyc = c;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                        && (_goeTokenEscapes <= 12 || _goeTokenEscapes % 8 == 0))
                        Console.Error.WriteLine(
                            $"[BO2] unwind format-wrapper 0x{pc:X8} -> 0x{raSlot:X8} " +
                            $"(bad a2=0x{a2:X8}) n={_goeTokenEscapes} cdvd={sys.Cdvd.SectorsRead} cyc={c}");
                    return;
                }
            }
        }

        // Deep '%' scan / mbtowc helper (live PcProfiler heat). a2 is clobbered mid-loop —
        // any long stay past 0x483000 post-pack is thrash (real format strings finish fast).
        bool inFmtScan = pc is >= 0x00483000 and < 0x00484448
            || pc is >= 0x00486EC0 and <= 0x00486EF8;
        bool inFmtFrame = pc is > 0x00482F68 and < 0x00484448 || inFmtScan;
        // WAVE-5: boot ELF PT_LOAD code lives at 0x100000..~0x4A477C including low C++ lib
        // (list splice @0x100F48, path helpers @0x10CFD8, MMI epilogues @0x101A10). w4 treated
        // all PC<0x120000 as thrash and yanked mid-helper — killed LIST/ENGLISH Soft-GS path.
        // IsLikelyEeCode rejects valid MMI (lq/sq) so do NOT use it as thrash gate here.
        // Format thrash is handled via inFmtFrame; high heaps / below-image are thrash.
        bool dataThrash = (pc is < 0x00100000)
            || (pc is >= 0x004A0000 and < 0x02000000);
        if (!inFmtFrame && !dataThrash) return;

        if (inFmtFrame)
        {
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
            uint resume = 0;
            if (sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 0x2D0u)
            {
                foreach (uint off in new uint[] { 0x2C0, 0x2D0, 0x10, 0x0, 0x60 })
                {
                    if (sp + off + 4 > (uint)SystemMemory.RDRAM_SIZE) continue;
                    uint raSlot = sys.Memory.Read32(sp + off) & 0x1FFFFFFFu;
                    if (raSlot is >= 0x00200000 and < 0x004A0000
                        && raSlot is not (>= 0x00482E00 and <= 0x00486F00)
                        && raSlot is not (>= 0x004804E8 and <= 0x00480538)
                        && IsSafeCodeTarget(sys, raSlot)
                        && IsColdSafeResume(sys, raSlot))
                    {
                        resume = raSlot;
                        uint pop = off >= 0x2C0 ? 0x2D0u : (off + 0x10u);
                        if (sp + pop < (uint)SystemMemory.RDRAM_SIZE)
                            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = sp + pop });
                        break;
                    }
                }
            }
            if (resume != 0)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                ArmGifPath3(sys);
                EnsureMainThreadRunning(sys);
                _goeTokenEscapes++;
                _titleSmEscapes++;
                _lastTitleSmCyc = c;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_goeTokenEscapes <= 12 || _goeTokenEscapes % 8 == 0))
                    Console.Error.WriteLine(
                        $"[BO2] unwind format scan 0x{pc:X8} -> 0x{resume:X8} " +
                        $"n={_goeTokenEscapes} cdvd={sys.Cdvd.SectorsRead} cyc={c}");
                return;
            }
        }

        if (!dataThrash) return;

        uint resume2 = PickSafeResume(sys, pc);
        if (resume2 is >= 0x00482E00 and <= 0x00486F00)
            resume2 = 0;
        if (resume2 is >= 0x004804E8 and <= 0x00480538)
            resume2 = 0;
        if (resume2 == 0 || resume2 == pc || !IsSafeCodeTarget(sys, resume2)
            || !IsColdSafeResume(sys, resume2))
            resume2 = 0x0048A980;
        uint spNow = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        if (spNow < 0x00100000u || spNow >= (uint)SystemMemory.RDRAM_SIZE)
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FF0000 });
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume2 });
        sys.EE.PC = resume2;
        sys.EE.COP0_Status &= ~0x6u;
        ArmGifPath3(sys);
        EnsureMainThreadRunning(sys);
        _goeTokenEscapes++;
        _titleSmEscapes++;
        _lastTitleSmCyc = c;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_goeTokenEscapes <= 12 || _goeTokenEscapes % 8 == 0))
            Console.Error.WriteLine(
                $"[BO2] rescue data thrash 0x{pc:X8} -> 0x{resume2:X8} " +
                $"n={_goeTokenEscapes} cdvd={sys.Cdvd.SectorsRead} cyc={c}");
    }

    /// <summary>
    /// Format a2 is thrash-class when it lands in goefile string heaps / high unpacked
    /// blobs rather than ELF rodata (0x4Bxxxx error strings, path templates).
    /// </summary>
    private static bool LooksLikeBo2BinaryFmtPtr(uint a2)
    {
        if (a2 is >= 0x00500000 and < 0x02000000) return true; // goefile / BSS heaps
        if (a2 is >= 0x00A00000 and < 0x02000000) return true; // PRECODE load @0xA242A0
        if (a2 is < 0x00100000) return true; // null / low garbage
        // ELF rodata window (usebigfile / InMap / entity strings live here) — keep.
        if (a2 is >= 0x004B0000 and < 0x00520000) return false;
        return false;
    }

    /// <summary>
    /// Soft-leave post-InMap multiprecision / bit-pack heat (<c>0x479E00..0x47A280</c>).
    /// Live wave-1 residual after InMap leave: profiler heat in bit-pack helpers with no
    /// FILEIO Open of CODE.BG2. Natural $ra when cold-safe; else post-flush init.
    /// Rate-limited; never fakes CODE/MAINMENU sector credit.
    /// </summary>
    private void MaybeEscapePostEntityBitPack(Ps2System sys, ulong c)
    {
        if (_codeOpenNudges >= 48) return;
        if (c - _lastTitleSmCyc < 80_000) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        // Bit-pack / div helpers (live heat) — not menu-draw entries.
        bool inBitPack = pc is >= 0x00479E00 and <= 0x0047A280;
        if (!inBitPack) return;

        // WAVE-6: prefer Starting-code bigfile path when no stream yet so StartBigFile can
        // register packages (force-stream alone plants bytes without boot-spine registration).
        long streamed = sys.Hle.Sony?.RealRpc.Bo2GameBg2StreamedBytes ?? 0;
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        uint resume = 0;
        if (streamed == 0)
            resume = StartingCodeBigFilePc;
        else if (IsColdSafeResume(sys, ra) && ra != pc && ra is < 0x004A0000)
            resume = ra;
        if (resume == 0)
            resume = PickSafeResume(sys, pc);
        if (resume == 0 || resume == pc || (!IsColdSafeResume(sys, resume) && resume != StartingCodeBigFilePc))
            resume = streamed == 0 ? StartingCodeBigFilePc : 0x0048A980;
        if (resume != StartingCodeBigFilePc && (!IsSafeCodeTarget(sys, resume) || resume == pc))
            resume = streamed == 0 ? StartingCodeBigFilePc : 0u;
        if (resume == 0)
            return;

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = FinishedCodeBigFilePc });
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        ArmGifPath3(sys);
        _codeOpenNudges++;
        _titleSmEscapes++;
        _lastTitleSmCyc = c;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_codeOpenNudges <= 12 || _codeOpenNudges % 8 == 0))
            Console.Error.WriteLine(
                $"[BO2] leave post-entity bit-pack 0x{pc:X8} -> 0x{resume:X8} " +
                $"n={_codeOpenNudges} streamed={streamed} cyc={c}");
    }


    /// <summary>
    /// WAVE-3/4: force EE into real usebigfile / "Starting code big file" path so CODE.BG2
    /// Open is game-initiated (countSectors:true → RealSifRpc.Bo2GameBg2Opens).
    ///
    /// WAVE-4 ELF truth: Starting-code @0x1B5798 jals StartBigFile wrapper 0x346DF8;
    /// PreCode @0x1B5708 jals body 0x346E48. WAVE-3 PCs were +0x1000 and never FILEIO'd.
    /// </summary>
    private void MaybeForceUseBigfileOpen(Ps2System sys, ulong c)
    {
        if (_useBigfileResumePending) return;
        int gameOpens = sys.Hle.Sony?.RealRpc.Bo2GameBg2Opens ?? 0;
        long streamed = sys.Hle.Sony?.RealRpc.Bo2GameBg2StreamedBytes ?? 0;
        // Keep forcing until stream has landed, not merely Open+close (WAVE-3 residual).
        if (gameOpens > 0 && streamed > 0)
        {
            _useBigfileForced = true;
            return;
        }
        if (_useBigfileForces >= 6) return;
        // WAVE-6: InMap leave OR bit-pack residual after pack assets (no longer require escapes).
        if (_inMapEscapes == 0 && _codeOpenNudges == 0 && !IsInBitPackHeat(sys)) return;
        if (!HasBo2PackAssetIo(sys)) return;
        if (!IsPreMainmenuSurface(sys)) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        // Already inside StartBigFile wrapper/body — let it run.
        if (pc is >= StartBigFileWrapperPc and <= 0x00347200) return;
        // Inside correct big-boot straight-line (PreCode..Creating main layer) — let it run
        // unless stuck without Open for a long slice.
        if (pc is >= PreCodeBigFilePc and <= CreatingMainLayerPc + 0x80
            && _useBigfileForces > 0 && gameOpens == 0
            && c - _lastTitleSmCyc >= 500_000)
        {
            // fall through to re-target Starting-code
        }
        else if (pc is >= PreCodeBigFilePc and <= CreatingMainLayerPc + 0x80) return;

        // Wait until post-InMap residual has settled a few slices (avoid yank mid-leave).
        if (c - _lastTitleSmCyc < 200_000 && _useBigfileForces == 0) return;

        // Prefer residual bands: WaitSema/RPC fabric, bit-pack, cold post-flush, InMap leave.
        // WAVE-6: also force when post-pack with zero stream (bit-pack leave may land off-band).
        long streamedNow = sys.Hle.Sony?.RealRpc.Bo2GameBg2StreamedBytes ?? 0;
        bool residual =
            pc is >= 0x00488800 and <= 0x00489200
            || pc is >= 0x00479E00 and <= 0x0047A280
            || pc is >= 0x0048A980 and <= 0x0048B200
            || pc is >= 0x00441F00 and <= 0x00442080 // post-drive thrash band (w3 final)
            || (pc is >= 0x002B9E00 and <= 0x002B9F98 && _inMapEscapes >= 1)
            || (streamedNow == 0 && _codeOpenNudges > 0);
        if (!residual && _useBigfileForces == 0) return;

        uint a0 = PickUseBigfileObject(sys);
        // First force: PreCode (s6 path) if object live; else Starting-code (jal 0x346DF8).
        // Later forces: Starting-code or direct StartBigFile wrapper.
        uint target;
        if (_useBigfileForces == 0)
            target = a0 != 0 ? PreCodeBigFilePc : StartingCodeBigFilePc;
        else if (_useBigfileForces == 1)
            target = StartingCodeBigFilePc;
        else
            target = StartBigFileWrapperPc;

        if (sys.Memory.Read32(UseBigfileReturnTrampoline) != 0x1000FFFFu)
        {
            sys.Memory.Write32(UseBigfileReturnTrampoline, 0x1000FFFFu); // beq zero,zero,self
            sys.Memory.Write32(UseBigfileReturnTrampoline + 4, 0u);
        }

        _useBigfileSavedPc = sys.EE.PC;
        _useBigfileSavedGpr = new ulong[32];
        for (int i = 0; i < 32; i++)
            _useBigfileSavedGpr[i] = sys.EE.GetGpr(i).Lo;

        ulong sp = sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL;
        if (sp < 0x00100000 || sp >= (ulong)SystemMemory.RDRAM_SIZE - 0x200)
        {
            sp = 0x01FE8000;
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = sp });
        }

        uint pathBuf = (uint)sp + 0x10;
        // Plant "CODE" path token for StartBigFile / printf helpers that read s6.
        WriteCString(sys, pathBuf, "CODE");
        for (uint i = 8; i < 0x40; i += 4)
            if (sys.Memory.Read32(pathBuf + i) == 0) { /* keep zeros */ }

        if (a0 != 0)
            sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = a0 }); // a0
        else
            sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = pathBuf });

        sys.EE.SetGpr(22, new EmotionEngine.Gpr128 { Lo = pathBuf }); // s6
        sys.EE.SetGpr(30, new EmotionEngine.Gpr128 { Lo = a0 != 0 ? a0 : pathBuf }); // fp
        sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = 0 }); // a1
        sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 }); // a2
        sys.EE.SetGpr(23, new EmotionEngine.Gpr128 { Lo = 0x00490000 }); // s7

        // Return into Finished-code continue so we don't bounce to trampoline mid-draw.
        uint ret = FinishedCodeBigFilePc;
        if (!IsSafeCodeTarget(sys, ret))
            ret = UseBigfileReturnTrampoline;
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = ret });
        sys.EE.PC = target;
        sys.EE.COP0_Status &= ~0x6u;
        EnsureMainThreadRunning(sys);
        ArmGifPath3(sys);

        _useBigfileForced = true;
        _useBigfileResumePending = true;
        _useBigfileForces++;
        _codeOpenNudges++;
        _titleSmEscapes++;
        _lastTitleSmCyc = c;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BO2] force usebigfile target=0x{target:X8} a0=0x{(a0 != 0 ? a0 : pathBuf):X8} " +
                $"savedPc=0x{_useBigfileSavedPc:X8} n={_useBigfileForces} cyc={c}");
    }

    /// <summary>
    /// Resume after forced usebigfile call returns to trampoline, or drop force when
    /// game BG2 open already happened.
    /// </summary>
    private void MaybeResumeAfterForcedUseBigfile(Ps2System sys)
    {
        if (!_useBigfileResumePending) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        int gameOpens = sys.Hle.Sony?.RealRpc.Bo2GameBg2Opens ?? 0;
        long streamed = sys.Hle.Sony?.RealRpc.Bo2GameBg2StreamedBytes ?? 0;

        // Let correct big-boot / StartBigFile run.
        if (pc is >= PreCodeBigFilePc and <= CreatingMainLayerPc + 0x80) return;
        if (pc is >= StartBigFileWrapperPc and <= 0x00347200) return;
        if (pc is >= 0x002CB4E0 and <= 0x002CB600) return; // printf family
        if (pc is >= 0x002B3000 and <= 0x002B3400) return;

        bool atTrampoline = pc == UseBigfileReturnTrampoline;
        if (gameOpens > 0 || streamed > 0)
        {
            _useBigfileResumePending = false;
            if (atTrampoline)
            {
                uint cont = streamed > 0 ? CreatingMainLayerPc : FinishedCodeBigFilePc;
                if (IsSafeCodeTarget(sys, cont))
                    sys.EE.PC = cont;
                else if (_useBigfileSavedPc != 0)
                    sys.EE.PC = _useBigfileSavedPc;
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[BO2] usebigfile force done gameOpens={gameOpens} streamed={streamed} " +
                    $"forced={_useBigfileForced} pc=0x{pc:X8}");
            return;
        }

        if (!atTrampoline) return;

        if (_useBigfileSavedGpr != null)
        {
            for (int i = 1; i < 32; i++)
                sys.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = _useBigfileSavedGpr[i] });
        }
        sys.EE.PC = _useBigfileSavedPc;
        sys.LastGoodEePc = _useBigfileSavedPc;
        _useBigfileResumePending = false;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BO2] usebigfile force resume savedPc=0x{_useBigfileSavedPc:X8} " +
                $"(no game BG2 open yet) n={_useBigfileForces}");
    }


    /// <summary>
    /// WAVE-3/4: when EE usebigfile force has not produced a game CODE/MAINMENU Open+stream,
    /// drive Open+FileRead via <see cref="RealSifRpc.ForceBo2GameBg2Stream"/> (same disc path
    /// as usebigfile/FILEIO). WAVE-3 Open+immediate Close left Soft-GS at px=3 (open≠draw).
    /// </summary>
    private bool _droveCodeBg2;
    private bool _droveMainmenuBg2;
    private bool _streamedCodeBg2;
    private bool _streamedMainmenuBg2;
    private int _mainLayerForces;

    private void MaybeDriveGameBg2Open(Ps2System sys, ulong c)
    {
        var rpc = sys.Hle.Sony?.RealRpc;
        if (rpc == null) return;
        if (_streamedCodeBg2 && _streamedMainmenuBg2) return;
        // WAVE-6: stream after InMap leave OR bit-pack residual (pack open alone stalls).
        if (_inMapEscapes == 0 && _codeOpenNudges == 0 && !IsInBitPackHeat(sys)
            && !_useBigfileForced)
            return;
        // WAVE-6: after usebigfile force, give StartBigFile ~2.5M cycles to Open naturally
        // before force-stream plants raw bytes (stream≠register wall).
        if (_useBigfileForces > 0 && rpc.Bo2GameBg2Opens == 0
            && c - _lastTitleSmCyc < 2_500_000)
            return;
        if (_useBigfileForces == 0 && _codeOpenNudges > 0
            && c - _lastTitleSmCyc < 1_500_000
            && rpc.Bo2GameBg2Opens == 0)
            return;
        if (_useBigfileForces == 0 && c - _lastTitleSmCyc < 200_000
            && _codeOpenNudges == 0 && !IsInBitPackHeat(sys))
            return;
        if (_codeOpenNudges > 96) return;

        var iop = sys.IopModules;
        if (iop == null) return;

        // CODE first (usebigfile Starting code), then MAINMENU for menu surface.
        string[] tokens = _streamedCodeBg2
            ? new[] { "MAINMENU" }
            : new[] { "CODE", "MAINMENU" };
        foreach (string token in tokens)
        {
            if (token == "CODE" && _streamedCodeBg2) continue;
            if (token == "MAINMENU" && _streamedMainmenuBg2) continue;

            uint dest = token == "MAINMENU" ? MainmenuBg2EeDest : CodeBg2EeDest;
            int n = rpc.ForceBo2GameBg2Stream(sys.Memory, iop, sys.Cdvd, token, dest);
            if (n <= 0)
            {
                // Fallback: Open-only (WAVE-3) if stream fails — still better than nothing.
                int fd = rpc.ForceBo2GameBg2Open(iop, sys.Cdvd, token);
                if (fd < 0) continue;
                try { iop.FileClose(fd); } catch { /* ignore */ }
            }

            if (token == "CODE")
            {
                _droveCodeBg2 = true;
                if (n > 0) _streamedCodeBg2 = true;
            }
            if (token == "MAINMENU")
            {
                _droveMainmenuBg2 = true;
                if (n > 0) _streamedMainmenuBg2 = true;
            }
            _codeOpenNudges++;
            ArmGifPath3(sys);
            EnsureMainThreadRunning(sys);
            uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
            // After CODE stream → Finished-code; after MAINMENU → Creating main layer.
            uint cont = _streamedMainmenuBg2 ? CreatingMainLayerPc
                : _streamedCodeBg2 ? FinishedCodeBigFilePc
                : StartingCodeBigFilePc;
            if (pc < 0x00120000 || pc >= 0x004A0000
                || (pc >= 0x00488800 && pc <= 0x00489200)
                || (pc >= 0x004BE000 && pc <= 0x004C0000)
                || (pc >= 0x00441F00 && pc <= 0x00442080)
                || pc == UseBigfileReturnTrampoline
                || _streamedMainmenuBg2)
            {
                if (IsSafeCodeTarget(sys, cont)
                    || cont is CreatingMainLayerPc or FinishedCodeBigFilePc or StartingCodeBigFilePc)
                {
                    sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                    sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = cont });
                    sys.EE.PC = cont;
                    sys.EE.COP0_Status &= ~0x6u;
                }
            }
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[BO2] drive-game BG2 token={token} n={n} gameOpens={rpc.Bo2GameBg2Opens} " +
                    $"streamed={rpc.Bo2GameBg2StreamedBytes} dest=0x{dest:X8} " +
                    $"cdvd={sys.Cdvd.SectorsRead} droveCode={_droveCodeBg2} droveMenu={_droveMainmenuBg2} " +
                    $"streamCode={_streamedCodeBg2} streamMenu={_streamedMainmenuBg2} cyc={c}");
            if (_streamedCodeBg2 && _streamedMainmenuBg2)
            {
                EnsureMainThreadRunning(sys);
                try
                {
                    foreach (var t in sys.Hle.Kernel.AllThreads)
                    {
                        if (!t.Alive || t.Id != 1) continue;
                        if (!t.Started)
                            sys.Hle.Kernel.StartAndMaybeSwitch(sys.EE, 1, switchNow: true, arg: 0, fromSyscall: false);
                    }
                }
                catch { /* ignore */ }
            }
            return; // one open/stream per Step slice
        }
    }

    /// <summary>
    /// WAVE-4/5: after CODE+MAINMENU stream into EE, force "Creating main layer" once so
    /// LIST.TXT/ENGLISH.DIR natural FILEIO can run. Do NOT re-yank once post-stream asset
    /// I/O advances (live w4b: re-kick looped LIST.TXT open forever, px stuck 3).
    /// WAVE-5: true entry prologue; do not yank real low-ELF helpers; post-ENGLISH Soft-GS
    /// residual advances mainmenu-bg2 draw instead of short-circuit 0x48A980 → EI park.
    /// </summary>
    private void MaybeKickCreatingMainLayer(Ps2System sys, ulong c)
    {
        if (!_streamedCodeBg2 || !_streamedMainmenuBg2) return;
        if (!IsPreMainmenuSurface(sys) && sys.Gs.PixelsWritten >= 500_000) return;

        RefreshListEnglishSignals(sys);

        // After first kick: only rescue hard data thrash — never interrupt LIST.TXT spine.
        if (_mainLayerForces >= 1)
        {
            // Soft-GS residual composite while EE processes entity list / English dir.
            if (_mainLayerForces <= 12 && c - _lastTitleSmCyc >= 1_500_000)
            {
                try { sys.Gs.CompositeDispfbToFramebuffer(); } catch { /* ignore */ }
                ArmGifPath3(sys);
                EnsureMainThreadRunning(sys);
                _mainLayerForces++; // count residual pulses without PC yank
                _lastTitleSmCyc = c;
            }

            // WAVE-5/6: after LIST+ENGLISH full reads, drive post-ENGLISH Soft-GS residual.
            // WAVE-6: logo Soft-GS ~71k must not block residual (IsPreMainmenuSurface).
            if (_sawListTxt && _sawEnglishDir && IsPreMainmenuSurface(sys))
                MaybeKickPostEnglishMenuDraw(sys, c);

            uint pcNow = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
            // Live w4c: after ENGLISH.DIR full read, EE executes path strings in asset
            // buffers (LIST @0xA4EA90 / ENGLISH @0xA62140) → UnknownOpcode storm.
            // WAVE-5: do NOT treat real low-ELF (0x10xxxx helpers) as hard thrash.
            bool assetAsCode = pcNow is >= 0x00A00000 and < 0x02000000;
            bool hardThrash = assetAsCode
                || pcNow is >= 0x004A0000 and < 0x00A00000
                || pcNow < 0x00100000
                || pcNow == UseBigfileReturnTrampoline;
            if (hardThrash && _mainLayerForces < 24 && c - _lastTitleSmCyc >= 500_000)
            {
                // Prefer Finished-code continue over short-circuit 0x48A980 (already-init →
                // EI park @0x48CF50, w4 residual). Never cold-enter mid-Creating.
                uint cont = _sawEnglishDir ? FinishedCodeBigFilePc : 0x001B5B3C;
                if (!IsSafeCodeTarget(sys, cont))
                    cont = FinishedCodeBigFilePc;
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = cont });
                sys.EE.PC = cont;
                sys.EE.COP0_Status &= ~0x6u;
                ArmGifPath3(sys);
                try { sys.Gs.CompositeDispfbToFramebuffer(); } catch { /* ignore */ }
                _mainLayerForces++;
                _titleSmEscapes++;
                _lastTitleSmCyc = c;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_mainLayerForces <= 12 || _mainLayerForces % 4 == 0))
                    Console.Error.WriteLine(
                        $"[BO2] rescue asset-as-code thrash pc=0x{pcNow:X8} -> 0x{cont:X8} " +
                        $"n={_mainLayerForces} list={_sawListTxt} eng={_sawEnglishDir} " +
                        $"px={sys.Gs.PixelsWritten} cyc={c}");
            }
            return;
        }

        if (c - _lastTitleSmCyc < 100_000) return;

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pc is >= CreatingMainLayerPc and <= CreatingMainLayerPc + 0x100) return;
        if (pc is >= StartBigFileWrapperPc and <= 0x00347200) return;

        bool menuPlanted = sys.Memory.Read8(MainmenuBg2EeDest) == (byte)'g'
            && sys.Memory.Read8(MainmenuBg2EeDest + 1) == (byte)'o';

        ulong sp = sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL;
        if (sp < 0x00100000 || sp >= (ulong)SystemMemory.RDRAM_SIZE - 0x100)
        {
            sp = 0x01FE8000;
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = sp });
        }

        // WAVE-5: true entry @0x1B5AC0 (stack alloc).
        // WAVE-6: $ra = post-Finished continue @0x1B57B8 (jal 0x339DC8) so Creating returns
        // into boot spine that can advance display setup — not nop pad @0x1B5B3C.
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = PostFinishedCodeContinuePc });
        sys.EE.PC = CreatingMainLayerPc;
        sys.EE.COP0_Status &= ~0x6u;
        EnsureMainThreadRunning(sys);
        ArmGifPath3(sys);
        try
        {
            sys.Pad.AnalogMode = true;
            sys.Pad.SetButtons((uint)(PadInput.Button.Start | PadInput.Button.Cross));
            sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad);
        }
        catch { /* ignore */ }
        try { sys.Gs.CompositeDispfbToFramebuffer(); } catch { /* ignore */ }

        _mainLayerForces = 1;
        _menuDrawKicks++;
        _lastTitleSmCyc = c;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[BO2] kick Creating main layer from pc=0x{pc:X8} planted={menuPlanted} " +
                $"entry=0x{CreatingMainLayerPc:X8} ra=0x{PostFinishedCodeContinuePc:X8} n=1 " +
                $"px={sys.Gs.PixelsWritten} " +
                $"streamed={sys.Hle.Sony?.RealRpc.Bo2GameBg2StreamedBytes ?? 0} cyc={c}");
    }

    private bool _sawListTxt;
    private bool _sawEnglishDir;
    private int _postEnglishDrawKicks;
    private bool _listWalkStubbed;
    private int _entityParseLeaves;
    private int _displaySpineKicks;

    /// <summary>
    /// WAVE-4: post-Finished boot spine continues after display setup jal 0x339DC8.
    /// WAVE-7: after list stubs free entity-parse budget, re-enter here so factory
    /// type-75 / type-66 / layer helpers can issue Path2 PRIM past logo clear.
    /// </summary>
    private const uint PostDisplaySetupPc = 0x001B57C0;

    /// <summary>
    /// WAVE-5: poll RealSifRpc LIST.TXT / ENGLISH.DIR full-read counters (honest FILEIO).
    /// </summary>
    private void RefreshListEnglishSignals(Ps2System sys)
    {
        var rpc = sys.Hle.Sony?.RealRpc;
        if (rpc == null) return;
        if (rpc.Bo2ListTxtBytesRead > 0) _sawListTxt = true;
        if (rpc.Bo2EnglishDirBytesRead > 0) _sawEnglishDir = true;
    }

    /// <summary>
    /// WAVE-7: dual soft-stub for circular / unterminated entity lists after ENGLISH.
    ///
    /// Live w6 claim4 profiler: after soft-stub of insert leaf <c>0x2C3E30</c>, heat
    /// moved to sibling search leaf <c>0x2C3F08</c> (<c>lw next; bnel key</c>) — still
    /// tens of M with gifP2=111 prims=1. Both leaves walk circular lists built from
    /// partial LIST/ENGLISH parse under HLE.
    ///
    /// <list type="bullet">
    /// <item><c>0x2C3E30</c> insert-at-end: plant <c>sw a1,4(a0); jr ra</c> so callers
    /// still get a store without walking <c>node→next</c>.</item>
    /// <item><c>0x2C3EE8</c> search-by-key: plant <c>jr ra; v0=0</c> (not found) so
    /// outer entity parse at <c>0x2C7204</c> advances the loop index.</item>
    /// </list>
    /// </summary>
    private void SoftStubBo2CircularLists(Ps2System sys)
    {
        if (_listWalkStubbed) return;
        // insert-at-end: preserve store semantics without circular walk.
        //   sw a1, 4(a0)
        //   jr ra
        //   nop
        uint insertHead = sys.Memory.Read32(0x002C3E30);
        if (insertHead != 0 && insertHead != 0xAC850004u)
        {
            sys.Memory.Write32(0x002C3E30, 0xAC850004u); // sw a1, 4(a0)
            sys.Memory.Write32(0x002C3E34, 0x03E00008u); // jr ra
            sys.Memory.Write32(0x002C3E38, 0x00000000u); // nop
        }
        // search-by-key leaf: immediate not-found.
        //   jr ra
        //   daddu v0, zero, zero
        uint searchHead = sys.Memory.Read32(0x002C3EE8);
        if (searchHead != 0 && searchHead != 0x03E00008u)
        {
            sys.Memory.Write32(0x002C3EE8, 0x03E00008u); // jr ra
            sys.Memory.Write32(0x002C3EEC, 0x0000102Du); // daddu v0, zero, zero
        }
        _listWalkStubbed = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                "[BO2] soft-stub dual list leaves @ 0x2C3E30 (sw a1,4(a0);jr) + " +
                "0x2C3EE8 (jr;v0=0) WAVE-7");
    }

    /// <summary>
    /// WAVE-5 residual: after LIST.TXT + ENGLISH.DIR full FILEIO, Soft-GS still logo-class
    /// (px=3). w4 parked at EI helper (0x48CF50) via short-circuit 0x48A980 re-entry.
    /// Unstick that park, arm PATH3, composite DISPFB, and nudge past dead-ra EI loops so
    /// MAINMENU surface can issue GIF prims. Never invent pixels.
    /// WAVE-7: dual list-stub early; leave entity-parse outer loop; display-spine residual
    /// so Path2 PRIM can grow past logo clear (prims=1 px=71680).
    /// </summary>
    private void MaybeKickPostEnglishMenuDraw(Ps2System sys, ulong c)
    {
        if (_postEnglishDrawKicks >= 320) return;
        if (c - _lastTitleSmCyc < 150_000) return;
        // WAVE-6: logo Soft-GS ~71k is still pre-menu — do not abort residual on 50k.
        if (!IsPreMainmenuSurface(sys)) return;
        if (!_sawListTxt || !_sawEnglishDir) return;

        // WAVE-7: plant dual list stubs immediately once ENGLISH is live (do not wait
        // for 12 leaves — w6 spent the residual budget inside circular walks).
        SoftStubBo2CircularLists(sys);

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        long px0 = sys.Gs.PixelsWritten;
        long gif0 = (long)(sys.Gif?.Path3Transfers ?? 0UL);
        long gif2 = (long)(sys.Gif?.Path2Transfers ?? 0UL);
        long prims0 = sys.Gs.PrimitivesDrawn;

        // Soft-GS pulse every visit (IMAGE/DISPFB residual while EE draws).
        try { sys.Gs.CompositeDispfbToFramebuffer(); } catch { /* ignore */ }
        ArmGifPath3(sys);
        // WAVE-6/7: unmask PATH3 if VIF MSKPATH3 holds transfers with no new Soft-GS.
        try
        {
            if (sys.Gif != null && sys.Gif.Path3MaskedByVif
                && prims0 <= 8 && px0 < 500_000)
                sys.Gif.SetMskPath3(false);
        }
        catch { /* ignore */ }
        EnsureMainThreadRunning(sys);
        try
        {
            sys.Pad.AnalogMode = true;
            sys.Pad.SetButtons((uint)(PadInput.Button.Start | PadInput.Button.Cross));
            sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad);
        }
        catch { /* ignore */ }

        // WAVE-6/7: huge byte-copy heat @0x4802E8 — positive absurd rem only (not -1).
        // WAVE-7: also leave medium-large copies (>64 KiB) after dual list-stub so the
        // residual budget reaches display spine (w6 claim4 still 1.3M samples here).
        if (pc is >= 0x004802E0 and <= 0x00480410)
        {
            uint rem = (uint)(sys.EE.GetGpr(6).Lo & 0xFFFFFFFFUL);
            bool absurd = rem is > 0x200000u and < 0x80000000u;
            bool mediumThrash = _listWalkStubbed && rem is > 0x10000u and < 0x80000000u
                && _postEnglishDrawKicks >= 4;
            if (absurd || mediumThrash)
            {
                uint cont = PostFinishedCodeContinuePc;
                sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = cont });
                sys.EE.PC = cont;
                sys.EE.COP0_Status &= ~0x6u;
                _postEnglishDrawKicks++;
                _menuDrawKicks++;
                _lastTitleSmCyc = c;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine(
                        $"[BO2] post-ENGLISH abort memcpy rem=0x{rem:X8} -> 0x{cont:X8} " +
                        $"n={_postEnglishDrawKicks} px={px0} gifP2={gif2} cyc={c}");
                return;
            }
        }

        // WAVE-6/7: circular list insert @0x2C3E30 / search @0x2C3EE8 body heat.
        // Dual stub is permanent; still snap out of mid-body if PC is inside.
        if ((pc is >= 0x002C3E30 and <= 0x002C3E54)
            || (pc is >= 0x002C3EE8 and <= 0x002C3F20))
        {
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            uint cont = IsSafeCodeTarget(sys, ra) && ra != pc ? ra : 0x002C7308u;
            if (!IsSafeCodeTarget(sys, cont))
                cont = PostFinishedCodeContinuePc;
            // Search leaf returns v0=node; insert returns via delay-slot store.
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = cont;
            sys.EE.COP0_Status &= ~0x6u;
            _postEnglishDrawKicks++;
            _menuDrawKicks++;
            _lastTitleSmCyc = c;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_postEnglishDrawKicks <= 16 || _postEnglishDrawKicks % 8 == 0))
                Console.Error.WriteLine(
                    $"[BO2] post-ENGLISH leave list-walk 0x{pc:X8} -> 0x{cont:X8} " +
                    $"n={_postEnglishDrawKicks} px={px0} gifP2={gif2} cyc={c}");
            return;
        }

        // WAVE-7: entity-parse outer loop @0x2C71D0..0x2C7500 (LIST/ENGLISH consumer).
        // After dual list-stub, if Soft-GS still logo-class and we re-enter this band,
        // soft-leave via $ra so Creating can return to post-Finished display spine.
        bool atEntityParse = pc is >= 0x002C7100 and <= 0x002C7600;
        if (atEntityParse && _listWalkStubbed && prims0 <= 8
            && _entityParseLeaves < 48
            && c - _lastTitleSmCyc >= 250_000)
        {
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            uint cont;
            if (IsSafeCodeTarget(sys, ra) && ra != pc
                && ra is not (>= 0x002C7100 and <= 0x002C7600))
                cont = ra;
            else
                cont = PostFinishedCodeContinuePc;
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = PostFinishedCodeContinuePc });
            sys.EE.PC = cont;
            sys.EE.COP0_Status &= ~0x6u;
            _entityParseLeaves++;
            _postEnglishDrawKicks++;
            _menuDrawKicks++;
            _lastTitleSmCyc = c;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_entityParseLeaves <= 12 || _entityParseLeaves % 4 == 0))
                Console.Error.WriteLine(
                    $"[BO2] post-ENGLISH leave entity-parse 0x{pc:X8} -> 0x{cont:X8} " +
                    $"n={_entityParseLeaves} px={px0} prims={prims0} gifP2={gif2} cyc={c}");
            return;
        }

        // WAVE-7: display-spine residual — after list stubs, force post-Finished chain
        // (jal 0x339DC8 / 0x313fd8 / 0x1B4D68) when Soft-GS still sparse-prim and EE is
        // not in productive WaitSema/RPC. Never invent pixels; only re-enter real .text.
        // WAVE-7b: cap spine once Path2 is live (gifP2≥40) — further kicks ICON-storm
        // without prim growth (claim1: spine 32× / ICON loop, GAMEKEEPER only at tail).
        bool logoClass = prims0 <= 8 && px0 < 500_000;
        bool atBootSpine = pc is (>= PostFinishedCodeContinuePc and <= PostFinishedCodeContinuePc + 0x80)
            || pc is (>= CreatingMainLayerPc and <= CreatingMainLayerPc + 0x80)
            || pc is (>= 0x00339DC0 and <= 0x00339E40)
            || pc is (>= 0x00313FD0 and <= 0x00314040);
        bool spineBudget = _displaySpineKicks < 12 && gif2 < 40;
        if (logoClass && _listWalkStubbed && spineBudget
            && !atBootSpine
            && pc is >= 0x00100000 and < 0x004A0000
            && c - _lastTitleSmCyc >= 400_000
            && (_entityParseLeaves >= 2 || _postEnglishDrawKicks >= 8))
        {
            bool atWaitSemaQuick = pc is >= 0x00488800 and <= 0x00488920
                || pc is >= 0x0048AF00 and <= 0x0048B200;
            if (!atWaitSemaQuick)
            {
                // Alternate post-Finished (display setup) and post-display (type-66 factory).
                uint spine = (_displaySpineKicks % 2 == 0)
                    ? PostFinishedCodeContinuePc
                    : PostDisplaySetupPc;
                ulong sp = sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL;
                if (sp < 0x00100000 || sp >= (ulong)SystemMemory.RDRAM_SIZE - 0x100)
                    sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FE8000 });
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = PostDisplaySetupPc });
                sys.EE.PC = spine;
                sys.EE.COP0_Status &= ~0x6u;
                _displaySpineKicks++;
                _postEnglishDrawKicks++;
                _menuDrawKicks++;
                _lastTitleSmCyc = c;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_displaySpineKicks <= 12 || _displaySpineKicks % 4 == 0))
                    Console.Error.WriteLine(
                        $"[BO2] post-ENGLISH display-spine kick -> 0x{spine:X8} " +
                        $"n={_displaySpineKicks} px={px0} prims={prims0} gifP2={gif2} " +
                        $"gifP3={gif0} cyc={c}");
                return;
            }
        }

        // WAVE-7b / PL-015: post-GAMEKEEPER memcpy heat — free residual so entity ETP
        // parse can finish. Live claim1: 0x4802F0 after GAMEKEEPER.ETP.
        // PL-015 fix: do NOT leave 0x4814xx solely on PC — S0 claim rem=0x01FExxxx was a
        // stack/BSS pointer in a2, not a copy count (false thrash yanked productive frames).
        if (_listWalkStubbed && gif2 >= 20
            && (pc is >= 0x004802E0 and <= 0x00480410
                || pc is >= 0x00481400 and <= 0x00481800)
            && c - _lastTitleSmCyc >= 200_000)
        {
            uint rem = (uint)(sys.EE.GetGpr(6).Lo & 0xFFFFFFFFUL);
            // Real copy counts only: positive, bounded. Stack/BSS pointers live ≥0x01000000
            // (S0 claim rem=0x01FExxxx was pointer-as-rem, not thrash).
            bool realCopy = rem > 0x1000u && rem <= 0x00100000u;
            bool absurdCopy = rem > 0x00100000u && rem < 0x01000000u;
            bool inMemcpyLeaf = pc is >= 0x004802E0 and <= 0x00480410;
            bool inFmtBand = pc is >= 0x00481400 and <= 0x00481800;
            // Memcpy leaf: medium or absurd rem. Format band: medium rem only (no ptr-as-rem).
            bool leave = (inMemcpyLeaf && (realCopy || absurdCopy))
                || (inFmtBand && realCopy);
            if (leave)
            {
                sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 });
                uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
                uint cont = IsSafeCodeTarget(sys, ra) && ra != pc
                    && ra is not (>= 0x004802E0 and <= 0x00481800)
                    ? ra : PostDisplaySetupPc;
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = PostDisplaySetupPc });
                sys.EE.PC = cont;
                sys.EE.COP0_Status &= ~0x6u;
                _postEnglishDrawKicks++;
                _menuDrawKicks++;
                _lastTitleSmCyc = c;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_postEnglishDrawKicks <= 24 || _postEnglishDrawKicks % 8 == 0))
                    Console.Error.WriteLine(
                        $"[BO2] post-ENGLISH leave post-stream thrash 0x{pc:X8} -> 0x{cont:X8} " +
                        $"rem=0x{rem:X8} n={_postEnglishDrawKicks} px={px0} prims={prims0} cyc={c}");
                return;
            }
        }

        // WAVE-7b / PL-015: freelist / heap-walk thrash after GAMEKEEPER.
        // S0 variance final PC=0x2BB968 (below old 0x2BBD00 band). Expand leave window so
        // drawable / MainMenu path can run under title-surface Soft-GS + pad inject.
        if (_listWalkStubbed && logoClass
            && pc is >= 0x002BB900 and <= 0x002BBD80
            && c - _lastTitleSmCyc >= 250_000)
        {
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            uint cont = IsSafeCodeTarget(sys, ra) && ra != pc
                && ra is not (>= 0x002BB900 and <= 0x002BBD80)
                ? ra : PostDisplaySetupPc;
            // Skip to epilogue store path when mid-body (sw t0,0(a1) @0x2BBD4C).
            if (pc is >= 0x002BBD20 and <= 0x002BBD48
                && IsSafeCodeTarget(sys, 0x002BBD4C))
                cont = 0x002BBD4C;
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = cont;
            sys.EE.COP0_Status &= ~0x6u;
            _postEnglishDrawKicks++;
            _menuDrawKicks++;
            _lastTitleSmCyc = c;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_postEnglishDrawKicks <= 24 || _postEnglishDrawKicks % 8 == 0))
                Console.Error.WriteLine(
                    $"[BO2] post-ENGLISH leave freelist-walk 0x{pc:X8} -> 0x{cont:X8} " +
                    $"n={_postEnglishDrawKicks} px={px0} prims={prims0} gifP2={gif2} cyc={c}");
            return;
        }

        // WAVE-6 residual (claim truth):
        // - Do NOT yank WaitSema — CompleteRpcEnd owns FILEIO (ICON/ETP). Live 100M: WaitSema
        //   kicks interrupted RPC and looped post-Finished without Soft-GS growth.
        // - Do NOT re-kick Creating after LIST+ENGLISH (ICON open storm).
        // - Soft-GS composite + PATH3 arm + pad still pulse every visit above.
        // - Only unstick EI park / data thrash / absurd memcpy.
        bool atWaitSema = pc is >= 0x00488800 and <= 0x00488920
            || pc is >= 0x0048AF00 and <= 0x0048B200; // CallRpc complete fabric
        bool atEiOrFlush = pc is (>= EiHelperPc and <= EiHelperPc + 0x20)
            || pc is (>= 0x0048A980 and <= 0x0048A9C0);
        bool atMemcpy = pc is >= 0x004802E0 and <= 0x00480410;

        // WaitSema / CallRpc complete — leave alone (FILEIO ICON/GAMEKEEPER path).
        if (atWaitSema)
        {
            if (_postEnglishDrawKicks < 8 || _postEnglishDrawKicks % 16 == 0)
            {
                _postEnglishDrawKicks++;
                _lastTitleSmCyc = c;
            }
            return;
        }

        // Real .text progress — leave alone. Soft-GS arm above still runs.
        // WAVE-7: except low-ELF vector insert thrash @0x1019xx after list stubs when
        // Soft-GS still logo — nudge display spine (live w6 final PC=0x1019B8).
        if (pc is >= 0x00101900 and <= 0x00101A20
            && logoClass && _listWalkStubbed && _displaySpineKicks < 32
            && c - _lastTitleSmCyc >= 300_000)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = PostDisplaySetupPc });
            sys.EE.PC = PostFinishedCodeContinuePc;
            sys.EE.COP0_Status &= ~0x6u;
            _displaySpineKicks++;
            _postEnglishDrawKicks++;
            _menuDrawKicks++;
            _lastTitleSmCyc = c;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[BO2] post-ENGLISH leave vector-insert 0x{pc:X8} -> 0x{PostFinishedCodeContinuePc:X8} " +
                    $"n={_displaySpineKicks} px={px0} gifP2={gif2} cyc={c}");
            return;
        }

        if (pc is >= 0x00100000 and < 0x004A0000
            && !atEiOrFlush && !atMemcpy
            && !IsExecutingDataOrNopSled(sys, pc))
        {
            if (_postEnglishDrawKicks < 24 || _postEnglishDrawKicks % 8 == 0)
            {
                _postEnglishDrawKicks++;
                _lastTitleSmCyc = c;
            }
            return;
        }

        // Stuck at EI helper / data / absurd memcpy — force. Never Creating / never WaitSema yank.
        if (atEiOrFlush
            || IsExecutingDataOrNopSled(sys, pc)
            || pc is >= 0x00A00000
            || pc is >= 0x004A0000 and < 0x00A00000
            || (atMemcpy && (uint)(sys.EE.GetGpr(6).Lo & 0xFFFFFFFFUL) is > 0x200000u and < 0x80000000u))
        {
            try
            {
                if (sys.Memory.Read32(0x004AC108) != 0)
                    sys.Memory.Write32(0x004AC108, 0);
            }
            catch { /* ignore */ }

            if (atMemcpy)
                sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 });

            uint cont = atEiOrFlush ? 0x0048A980u : PostFinishedCodeContinuePc;
            ulong sp = sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL;
            if (sp < 0x00100000 || sp >= (ulong)SystemMemory.RDRAM_SIZE - 0x100)
                sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = 0x01FE8000 });

            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = PostFinishedCodeContinuePc });
            sys.EE.PC = cont;
            sys.EE.COP0_Status &= ~0x6u;
            _postEnglishDrawKicks++;
            _menuDrawKicks++;
            _lastTitleSmCyc = c;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_postEnglishDrawKicks <= 12 || _postEnglishDrawKicks % 4 == 0))
                Console.Error.WriteLine(
                    $"[BO2] post-ENGLISH menu-draw kick pc=0x{pc:X8} -> 0x{cont:X8} " +
                    $"n={_postEnglishDrawKicks} listB={sys.Hle.Sony?.RealRpc.Bo2ListTxtBytesRead ?? 0} " +
                    $"engB={sys.Hle.Sony?.RealRpc.Bo2EnglishDirBytesRead ?? 0} " +
                    $"px={sys.Gs.PixelsWritten} gifP2={sys.Gif?.Path2Transfers ?? 0} " +
                    $"gifP3={sys.Gif?.Path3Transfers ?? 0} cyc={c}");
        }
    }

    /// <summary>
    /// Pick a live manager / boot object pointer for big-boot a0 (fp).
    /// Prefers globals the big-boot path loads (0x492E68 family).
    /// </summary>
    private static uint PickUseBigfileObject(Ps2System sys)
    {
        foreach (uint slot in new uint[]
                 {
                     0x00492E68,
                     0x0049374C,
                     0x00495760,
                     0x004968F8,
                 })
        {
            uint v = sys.Memory.Read32(slot) & 0x1FFFFFFFu;
            if (v is >= 0x00100000 and < 0x02000000)
            {
                uint w0 = sys.Memory.Read32(v);
                if (w0 != 0 && w0 != 0xFFFFFFFFu)
                    return v;
            }
        }
        return 0;
    }

    /// <summary>
    /// Escape post-KAIN InMap null-destination path.
    /// Live (2026-07-31): after KAIN.IMP full read, EE parks mid <c>0x2B9F34</c>
    /// (a1==0 → "Bad Destination for InMap") with ra=<c>0x2B9E28</c>, then the caller's
    /// <c>jalr *(s1+4)</c> lands in goefile/data and rescue re-enters 0x2B9F34.
    /// Soft-return the helper so boot can leave entity registration toward usebigfile.
    /// </summary>
    private void MaybeEscapeInMapNullDest(Ps2System sys, ulong c)
    {
        if (_inMapEscapes >= 32) return;
        if (c - _lastTitleSmCyc < 40_000) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);

        bool inInMapHelper = pc is >= 0x002B9F08 and <= 0x002B9F94;
        bool inInMapCaller = pc is >= 0x002B9E0C and <= 0x002B9E38;
        if (!inInMapHelper && !inInMapCaller) return;

        uint a1 = (uint)(sys.EE.GetGpr(5).Lo & 0x1FFFFFFFUL);
        uint s1 = (uint)(sys.EE.GetGpr(17).Lo & 0x1FFFFFFFUL);
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);

        if (inInMapCaller)
        {
            uint target = pc <= 0x002B9E14
                ? (uint)(sys.EE.GetGpr(2).Lo & 0x1FFFFFFFUL)
                : (uint)(sys.EE.GetGpr(3).Lo & 0x1FFFFFFFUL);
            if (target != 0 && IsSafeCodeTarget(sys, target) && target is < 0x004A0000)
                return;
            uint cont = pc <= 0x002B9E14 ? 0x002B9E14u : 0x002B9E38u;
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
            sys.EE.PC = cont;
            sys.EE.COP0_Status &= ~0x6u;
            _inMapEscapes++;
            _titleSmEscapes++;
            _lastTitleSmCyc = c;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_inMapEscapes <= 12 || _inMapEscapes % 8 == 0))
                Console.Error.WriteLine(
                    $"[BO2] skip InMap bad jalr pc=0x{pc:X8} tgt=0x{target:X8} -> 0x{cont:X8} " +
                    $"n={_inMapEscapes} cyc={c}");
            return;
        }

        // In helper: a1==0 error path or mid-body after several visits — return default slot.
        if (a1 != 0 && _inMapEscapes < 4) return;

        uint slot = 0;
        if (s1 is >= 0x00500000 and < 0x00600000)
            slot = s1 + 0x78A8u;

        if (sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE - 0x50u)
        {
            uint raSlot = sys.Memory.Read32(sp + 0x40) & 0x1FFFFFFFu;
            if (raSlot is >= 0x002B9E00 and <= 0x002B9F00)
            {
                sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = sp + 0x50 });
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = slot });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = raSlot });
                sys.EE.PC = raSlot;
                sys.EE.COP0_Status &= ~0x6u;
                _inMapEscapes++;
                _codeOpenNudges++;
                _titleSmEscapes++;
                _lastTitleSmCyc = c;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_inMapEscapes <= 12 || _inMapEscapes % 8 == 0))
                    Console.Error.WriteLine(
                        $"[BO2] leave InMap helper 0x{pc:X8} -> ra=0x{raSlot:X8} slot=0x{slot:X8} " +
                        $"n={_inMapEscapes} cyc={c}");
                return;
            }
        }

        if (ra is >= 0x002B9E14 and <= 0x002B9E40 && IsSafeCodeTarget(sys, ra))
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = slot });
            sys.EE.PC = ra;
            sys.EE.COP0_Status &= ~0x6u;
            _inMapEscapes++;
            _codeOpenNudges++;
            _titleSmEscapes++;
            _lastTitleSmCyc = c;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_inMapEscapes <= 12 || _inMapEscapes % 8 == 0))
                Console.Error.WriteLine(
                    $"[BO2] leave InMap helper 0x{pc:X8} -> ra=0x{ra:X8} slot=0x{slot:X8} " +
                    $"n={_inMapEscapes} cyc={c}");
        }
    }

    /// <summary>
    /// Soft-stub entity Dest-Database printf glue after KAIN pack I/O.
    /// <c>0x2AD8E0</c> / <c>0x2AD910</c>: format-wrapper + SN printf with binary a2.
    /// Do NOT permanent-stub shared format leaf/wrapper — InMap / other consumers need them.
    /// </summary>
    private void SoftStubEntityPrintfChain(Ps2System sys)
    {
        if (_entityPrintfGlueStubbed) return;
        uint head = sys.Memory.Read32(0x002AD8E0);
        if (head == 0) return;
        if (head == 0x03E00008u) { _entityPrintfGlueStubbed = true; return; }

        sys.Memory.Write32(0x002AD8E0, 0x03E00008u); // jr ra
        sys.Memory.Write32(0x002AD8E4, 0x24020001u); // addiu v0, zero, 1
        uint head2 = sys.Memory.Read32(0x002AD910);
        if (head2 != 0 && head2 != 0x03E00008u)
        {
            sys.Memory.Write32(0x002AD910, 0x03E00008u);
            sys.Memory.Write32(0x002AD914, 0x24020001u);
        }
        _entityPrintfGlueStubbed = true;
        SoftStubSnPrintf(sys);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                "[BO2] soft-stub entity printf glue @ 0x2AD8E0/0x2AD910 (jr ra; v0=1; format intact)");
    }

    private static void EnsureMainThreadRunning(Ps2System sys)
    {
        try
        {
            foreach (var t in sys.Hle.Kernel.AllThreads)
            {
                if (!t.Alive || t.Id != 1) continue;
                if (!t.Started)
                    sys.Hle.Kernel.StartAndMaybeSwitch(sys.EE, 1, switchNow: true, arg: 0, fromSyscall: false);
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// SN ProDG printf channel entry at <c>0x46FAF8</c> (sp-=160, CallRpc sid=0x534E03).
    /// Soft-stub to <c>jr ra; li v0,0</c> so debug prints cannot park WaitSema after GOE.
    /// Not the MAINMENU draw path (disasm: a1=0x534E03). Only after real asset I/O.
    /// </summary>
    private void SoftStubSnPrintf(Ps2System sys)
    {
        if (_snPrintfStubbed) return;
        // Only plant once the ELF is resident (probe first opcode of entry).
        uint head = sys.Memory.Read32(0x0046FAF8);
        if (head == 0) return;
        // Already stubbed?
        if (head == 0x03E00008u) { _snPrintfStubbed = true; return; }
        sys.Memory.Write32(0x0046FAF8, 0x03E00008u); // jr ra
        sys.Memory.Write32(0x0046FAFC, 0x24020001u); // addiu v0, zero, 1  (success / non-zero)
        _snPrintfStubbed = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine("[BO2] soft-stub SN printf @ 0x46FAF8 (jr ra; v0=1)");
    }

    /// <summary>
    /// Permanent plant for the post-GOE method-table walker at <c>0x166390</c>.
    /// Body: loop [a1..a2) calling <c>*(vtable+100)</c> on each object. Live: vtable
    /// slots hold goefile string pointers → jalr to 0x5387xx thrash (ra=0x16642C).
    /// Soft-failing just the jalr leaves a multi-M-cycle empty loop (PC stuck 0x166414).
    /// Stub the whole leaf: <c>jr ra; li v0,0</c> so Manager State can finish and
    /// historical FILEIO KAIN.IMP / ENGLISH.DIR path can issue.
    /// </summary>
    private void SoftStubBadVtCall(Ps2System sys)
    {
        if (_vtCallStubbed) return;
        uint head = sys.Memory.Read32(0x00166390);
        if (head == 0) return;
        // Already entry-stubbed?
        if (head == 0x03E00008u) { _vtCallStubbed = true; return; }
        sys.Memory.Write32(0x00166390, 0x03E00008u); // jr ra
        sys.Memory.Write32(0x00166394, 0x24020000u); // addiu v0, zero, 0  (delay)
        // Also neutralize the jalr site if already mid-function when we plant.
        sys.Memory.Write32(0x00166424, 0x24020000u); // addiu v0, zero, 0
        _vtCallStubbed = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine("[BO2] soft-stub method-walker @ 0x166390 (jr ra; v0=0)");
    }

    private int _cacheFlushSkips;

    /// <summary>
    /// BO2 post-RPC cache writeback leaf at <c>0x48A8D0..0x48A974</c>: tight
    /// <c>sync; cache; t2--</c> loop. With HLE cache as nop the countdown still runs but
    /// stalls the title for tens of M cycles with px=3. Permanent entry stub + safe $ra
    /// before <c>jr ra</c> so we never land in data/memcpy parks after the skip.
    /// </summary>
    private void MaybeSkipCacheFlush(Ps2System sys, ulong c)
    {
        // Permanent: make the leaf a no-op (jr ra) so re-entry never re-stalls.
        // Entry @ 0x48A8D0 (lui t9 / blez a1 / … / jr ra @ 0x48A974).
        if (sys.Memory.Read32(0x0048A8D0) != 0x03E00008u)
        {
            sys.Memory.Write32(0x0048A8D0, 0x03E00008u); // jr ra
            sys.Memory.Write32(0x0048A8D4, 0x00000000u); // nop
        }

        if (_cacheFlushSkips >= 128) return;
        if (c - _lastTitleSmCyc < 50_000) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (pc is < 0x0048A8D0 or > 0x0048A974) return;

        uint t2 = (uint)(sys.EE.GetGpr(10).Lo & 0xFFFFFFFFUL); // t2 = gpr 10
        // Always snap out once inside the body (even small t2 — HLE has no real cache).
        // Ensure $ra is real .text: 0x48A974 is jr ra — bad $ra → data/memcpy park (live).
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        if (!IsSafeCodeTarget(sys, ra) || ra == pc)
        {
            uint safe = PickSafeResume(sys, pc);
            if (safe == 0 || safe == pc)
                safe = 0x0048A980; // next function after flush leaf (real prologue)
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = safe });
            ra = safe;
        }
        sys.EE.SetGpr(10, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(9, new EmotionEngine.Gpr128 { Lo = 0 }); // t1 outer count
        sys.EE.PC = 0x0048A974; // jr ra
        sys.EE.COP0_Status &= ~0x6u;
        _cacheFlushSkips++;
        _lastTitleSmCyc = c;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_cacheFlushSkips <= 8 || _cacheFlushSkips % 16 == 0))
            Console.Error.WriteLine(
                $"[BO2] skip cache-flush t2 was 0x{t2:X8} -> jr ra=0x{ra:X8} n={_cacheFlushSkips} " +
                $"px={sys.Gs.PixelsWritten} cyc={c}");
    }

    /// <summary>
    /// After real MAINMENU.BG2 (cdvd≈1649), px stuck near logo. Prefer natural code
    /// resume via $ra / stack / last-good — never jump mid-utility (0x479E04 is
    /// <c>ori v0,v0,0xFFFF</c> bit pack, not a draw entry). Also leave memcpy-epilogue
    /// and pure RPC-complete parks toward post-flush init at 0x48A980.
    /// Live final PC <c>0x4520AC</c> is the delay slot of a large-frame <c>jr ra</c>
    /// (sp+=208) — if $ra is dead / WaitSema, boot never draws MAINMENU; force a safe
    /// post-MAINMENU continue and arm PATH3.
    /// </summary>
    private void MaybeKickMenuDraw(Ps2System sys, ulong c)
    {
        if (_menuDrawKicks >= 256) return;
        if (c - _lastTitleSmCyc < 100_000) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);

        // Live final: large-frame epilogue at 0x4520A8 (jr ra) / 0x4520AC (sp+=208).
        // With dead $ra this re-enters exception / WaitSema forever with logo Soft-GS.
        // Do NOT include 0x4520B0 (next function prologue) — live final5 thrash.
        bool atMenuEpilogue = pc is >= 0x00452080 and < 0x004520B0
            && IsPreMainmenuSurface(sys);
        if (atMenuEpilogue)
        {
            uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
            uint resume = IsSafeCodeTarget(sys, ra) && ra != pc ? ra : 0;
            if (resume == 0)
                resume = PickSafeResume(sys, pc);
            // Prefer post-flush init / RPC worker when $ra is WaitSema/RPC-complete park.
            // Never cold-enter 0x4520B0 (needs a0 object) or 0x2CD7E0 (needs s1 list).
            if (resume == 0 || resume is (>= 0x00488800 and <= 0x0048B200)
                || !IsSafeCodeTarget(sys, resume))
                resume = 0x0048A980;
            if (_menuDrawKicks >= 24 && IsPreMainmenuSurface(sys))
            {
                uint[] alts = { 0x0048A980, 0x004891A0, 0x0048AF30 };
                int idx = (_menuDrawKicks / 8) % alts.Length;
                if (IsSafeCodeTarget(sys, alts[idx]))
                    resume = alts[idx];
            }
            if (IsSafeCodeTarget(sys, resume) && resume != pc)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
                sys.EE.PC = resume;
                sys.EE.COP0_Status &= ~0x6u;
                _menuDrawKicks++;
                _lastTitleSmCyc = c;
                try { sys.Pad.SetButtons((uint)(PadInput.Button.Start | PadInput.Button.Cross)); }
                catch { /* ignore */ }
                ArmGifPath3(sys);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && (_menuDrawKicks <= 12 || _menuDrawKicks % 16 == 0))
                    Console.Error.WriteLine(
                        $"[BO2] menu-epilogue kick pc=0x{pc:X8} ra=0x{ra:X8} -> 0x{resume:X8} " +
                        $"n={_menuDrawKicks} cdvd={sys.Cdvd.SectorsRead} px={sys.Gs.PixelsWritten} cyc={c}");
                return;
            }
        }

        // Live menu14 final PC=0x488898 thrash: if still in WaitSema body with low px after
        // MAINMENU, force match path even outside the 250k gate (handled above with 100k).

        // Only act when EE is not making progress on real .text: WaitSema / CallRpc
        // complete left alone; help pure idle / dead NOP / data-as-code / memcpy park.
        // Do NOT kick off the RPC-complete band (live menu6: jumped into string tables).
        bool needsKick = IsExecutingDataOrNopSled(sys, pc)
            || (pc is (>= 0x002F1700 and <= 0x002F1750) // function epilogue delay
                && IsPreMainmenuSurface(sys));
        // Do NOT blanket-kick 0x4520xx — 0x4520B0 is real post-MAINMENU prologue (live thrash).
        if (!needsKick) return;

        uint resume2 = PickSafeResume(sys, pc);
        if (resume2 == 0 || resume2 == pc) return;
        // Never resume into high data / exception vectors / low HLE stubs.
        if (resume2 < 0x00120000u || resume2 >= 0x004A0000u) return;

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
        sys.EE.PC = resume2;
        sys.EE.COP0_Status &= ~0x6u;
        _menuDrawKicks++;
        _lastTitleSmCyc = c;
        try { sys.Pad.SetButtons((uint)(PadInput.Button.Start | PadInput.Button.Cross)); }
        catch { /* ignore */ }
        ArmGifPath3(sys);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_menuDrawKicks <= 8 || _menuDrawKicks % 16 == 0))
            Console.Error.WriteLine(
                $"[BO2] menu-draw kick pc=0x{pc:X8} -> 0x{resume2:X8} " +
                $"n={_menuDrawKicks} cdvd={sys.Cdvd.SectorsRead} px={sys.Gs.PixelsWritten} cyc={c}");
    }

    private static void ArmGifPath3(Ps2System sys)
    {
        try
        {
            sys.Intc.SetMask(sys.Intc.Mask | (1u << (int)Intc.InterruptSource.DmaController));
            sys.Dmac.EnableChannelIrq((int)Dmac.Channel.GIF);
            sys.Dmac.EnableChannelIrq((int)Dmac.Channel.VIF1);
            sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.GIF, 2);
            sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.VIF1, 2);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Rescue when EE PC lands in goefile DATA (0x538xxx tables) or a pure NOP sled
    /// (observed 0x12BAxx after bad mid-function kicks). Prefer $ra / stack / last-good.
    /// </summary>
    private void MaybeRescueBadPc(Ps2System sys, ulong c)
    {
        if (c - _lastTitleSmCyc < 150_000) return;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
        if (!IsExecutingDataOrNopSled(sys, pc)) return;
        if (_titleSmEscapes >= 128) return;

        uint resume = PickSafeResume(sys, pc);
        if (resume == 0 || resume == pc) return;

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        _titleSmEscapes++;
        _lastTitleSmCyc = c;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BO2") == "1"
            || (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                && (_titleSmEscapes <= 8 || _titleSmEscapes % 16 == 0)))
            Console.Error.WriteLine(
                $"[BO2] rescue bad PC pc=0x{pc:X8} -> 0x{resume:X8} " +
                $"n={_titleSmEscapes} cdvd={sys.Cdvd.SectorsRead} cyc={c}");
    }

    private static bool IsExecutingDataOrNopSled(Ps2System sys, uint pc)
    {
        // Never treat WaitSema / CallRpc complete path as "bad" — real producer waits.
        if (pc is >= 0x00488800 and <= 0x00488920) return false;
        if (pc is >= 0x0048AF00 and <= 0x0048B200) return false;

        // Memcpy epilogue park (live pad-final PC=0x4890B4) — jr-ra with dead frame.
        if (pc is >= 0x00489090 and <= 0x004890B8) return true;
        // Cache-flush body should never be "good" progress under HLE.
        if (pc is >= 0x0048A8D0 and <= 0x0048A974) return true;

        // High-image goefile tables / anything past ELF .text (memsz ~0x4A477C).
        if (pc is >= 0x004A0000 and < 0x02000000)
            return true;
        // WAVE-5: trust entire boot ELF PT_LOAD (0x100000..~0x4A477C) including low C++
        // lib with MMI (lq/sq). IsLikelyEeCode rejects MMI and falsely flagged 0x10xxxx
        // as data — post-ENGLISH kicks then yanked mid-helper (w5 live 0x1019D8).
        // Only a pure NOP sled in low mem is thrash.
        if (pc is >= 0x00100000 and < 0x004A0000)
        {
            if (pc is < 0x00200000)
            {
                int nops = 0;
                for (uint i = 0; i < 4; i++)
                    if (sys.Memory.Read32(pc + i * 4) == 0) nops++;
                if (nops >= 3) return true;
            }
            return false;
        }
        // 0x479E04 is a real bit-pack utility (ori v0,v0,0xFFFF) — never a menu-draw
        // entry, but natural calls must run to completion. Do NOT treat as data thrash
        // (live agent-fix30: post-Bind sid=0x29 lands here legitimately; rescuing to
        // 0x48AF30 cold-corrupted RPC frames).
        return !sys.Memory.IsLikelyEeCode(pc) && pc is >= 0x00100000 and < 0x02000000;
    }

    private static bool IsColdSafeResume(Ps2System sys, uint addr)
    {
        if (!IsSafeCodeTarget(sys, addr)) return false;
        // Mid-RPC Bind/Call complete needs a live frame — only natural WaitSema $ra
        // may re-enter (handled separately). Cold rescue into 0x48AFxx corrupts state.
        if (addr is >= 0x0048AF00 and <= 0x0048C800) return false;
        if (addr is >= 0x004891A0 and <= 0x00489200) return false; // RPC worker entry cold
        // Low EE library / syscall stubs — live final thrash re-entered 0x16642C mid-frame.
        if (addr is >= 0x00120000 and < 0x00200000) return false;
        // Live w3g/fix80: stack holds 0x35CF40 mid-object entry — cold re-thrash / open-bus.
        if (addr is >= 0x0035C000 and <= 0x0035E000) return false;
        // 0x2F17xx epilogue delay parks are real code but cold-resume without frame is dead.
        if (addr is >= 0x002F1700 and <= 0x002F1780) return false;
        // Format leaf / mid-body / mbtowc helper — only natural $ra after jal may re-enter.
        if (addr is >= 0x00482E00 and <= 0x00486F00) return false;
        // Format-wrapper (soft-stubbed post-KAIN) — no cold re-entry.
        if (addr is >= 0x004804E8 and <= 0x00480538) return false;
        // Entity printf glue (soft-stubbed).
        if (addr is >= 0x002AD8E0 and <= 0x002AD940) return false;
        // InMap / destination helpers (live park 0x2B9F34 a1==0 + caller 0x2B9E28).
        // Cold re-entry mid-frame rescue-loops forever after permanent format stubs were removed.
        if (addr is >= 0x002B9DB0 and <= 0x002B9F98) return false;
        return true;
    }

    private static uint PickSafeResume(Ps2System sys, uint pc)
    {
        // 1) Live $ra when cold-safe.
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFUL);
        if (IsColdSafeResume(sys, ra) && ra != pc)
            return ra;

        // 2) Stack scan for return addresses (deep enough for SN printf frames ~sp-160).
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFUL);
        if (sp is >= 0x00100000 and < (uint)SystemMemory.RDRAM_SIZE)
        {
            for (uint off = 0; off <= 0x200; off += 4)
            {
                uint cand = sys.Memory.Read32(sp + off) & 0x1FFFFFFFu;
                if (IsColdSafeResume(sys, cand) && cand != pc)
                    return cand;
            }
        }

        // 3) Last good EE PC tracked by the core.
        if (sys.LastGoodEePc is >= 0x00100000 and < 0x00500000)
        {
            uint lg = (uint)(sys.LastGoodEePc & 0x1FFFFFFFUL);
            if (IsColdSafeResume(sys, lg) && lg != pc)
                return lg;
        }

        // 4) Cold-resume only into self-contained prologues.
        foreach (uint cand in new uint[] { 0x0048A980, 0x002D71E4 })
        {
            if (IsColdSafeResume(sys, cand) && cand != pc)
                return cand;
        }
        return 0;
    }

    private static bool IsSafeCodeTarget(Ps2System sys, uint addr)
    {
        // BO2 ELF .text ends ~0x4A477C (PT_LOAD memsz) — never resume into high goefile data.
        if (addr is < 0x00100000 or >= 0x004A0000) return false;
        if (addr is >= 0x00479E00 and <= 0x00479E80) return false; // bit-pack (no cold entry)
        if (addr is >= 0x00488890 and <= 0x00488920) return false; // WaitSema body
        if (addr is >= 0x00489090 and <= 0x004890B8) return false; // memcpy epilogue (jr-ra park)
        if (addr is >= 0x0048A8D0 and <= 0x0048A974) return false; // cache-flush leaf
        if (addr is >= 0x0046FAF8 and <= 0x0046FC80) return false; // SN printf channel
        if (addr == 0x00100008) return false; // never re-CRT0
        // Object-dependent mid-functions — cold kicks thrash into data (live menu19).
        if (addr is >= 0x002CD7C0 and <= 0x002CD810) return false; // list-walk needs s1
        if (addr is >= 0x004520B0 and <= 0x00452100) return false; // needs a0 object
        // menu15 false rescue 0x1C03F0 → data thrash at 0xA227xx — reject low mid-blob.
        if (addr is >= 0x001C0000 and <= 0x001C1000) return false;
        // Low BIOS/HLE stubs (0x10xxxx) are real but re-entering mid-syscall thrashes.
        if (addr is >= 0x00100000 and < 0x00120000) return false;
        // Reject page-aligned data that IsLikelyEeCode sometimes accepts (live: 0x490000).
        if ((addr & 0xFFF) == 0 && addr is >= 0x00490000 and <= 0x004A0000) return false;
        // High goefile / unpacked blobs (live UnknownSpecial at 0xA227xx).
        if (addr is >= 0x00A00000) return false;
        return sys.Memory.IsLikelyEeCode(addr);
    }

    /// <summary>
    /// Three identical-shape prologues (templates at 0x497230 / 0x4973F8 / 0x4975B0 all start
    /// with the same continuous sequence of real opcodes between 0xFFFFFFFF mask slots).
    /// Scanner walks low mem for this chain; stubs return 0 safely if ever called.
    /// </summary>
    public static void PlantSnExtensionStubs(Ps2System sys)
    {
        // Continuous match sequence (masks 0xFFFFFFFF between template entries are wildcards
        // in the table encoding, not memory bytes — memory must be contiguous real opcodes):
        //   27BDFFD0  addiu sp,sp,-48
        //   FFB10010  sd    s1,16(sp)
        //   FFB00000  sd    s0,0(sp)
        //   0080882D  daddu s1,a0,zero
        //   FFBF0020  sd    ra,32(sp)
        uint[] prologue =
        {
            0x27BDFFD0u, // addiu sp,sp,-48
            0xFFB10010u, // sd s1,16(sp)
            0xFFB00000u, // sd s0,0(sp)
            0x0080882Du, // daddu s1,a0,zero
            0xFFBF0020u, // sd ra,32(sp)
            // Clean return (template tail not required once the 5-word head matches):
            0xDFBF0020u, // ld ra,32(sp)
            0xDFB10010u, // ld s1,16(sp)
            0xDFB00000u, // ld s0,0(sp)
            0x03E00008u, // jr ra
            0x27BD0030u, // addiu sp,sp,48  (delay)
        };

        // Plant three copies at 0x100-byte spacing so three independent template hits succeed.
        for (int n = 0; n < 3; n++)
        {
            uint baseP = PlantKseg0Base + (uint)(n * 0x100);
            for (int i = 0; i < prologue.Length; i++)
                sys.Memory.Write32(baseP + (uint)(i * 4), prologue[i]);
        }
    }
}
