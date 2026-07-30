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
        _titleSmEscapes = 0;
        _menuDrawKicks = 0;
        _cacheFlushSkips = 0;
        _snPrintfStubbed = false;
        _vtCallStubbed = false;
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

    public void OnHostPresent(Ps2System sys) => _ = sys;

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
    private int _snQuietPatches;
    private int _padInjectPulses;
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
        if (sys.Cdvd.SectorsRead >= 350 && sys.Gs.PixelsWritten < 50_000)
            SoftStubBadVtCall(sys);

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
        if (sys.Cdvd.SectorsRead >= 350 && sys.Gs.PixelsWritten < 50_000)
        {
            uint pcBad = (uint)(sys.EE.PC & 0x1FFFFFFFUL);
            if (pcBad is >= 0x00A00000 or (>= 0x004A0000 and < 0x02000000)
                || pcBad is >= 0x001C0000 and <= 0x001C1000)
            {
                SoftStubBadVtCall(sys);
                // Prefer return-from-jalr (ra=0x16642C) after vt plant. Never cold-enter
                // 0x48BCD0 (re-thrash). Soft-stub SN only after asset I/O (cdvd≥500) so
                // Manager State CallRpc storm can still complete and open KAIN.IMP.
                if (sys.Cdvd.SectorsRead >= 500)
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

        // Pad inject liberally: once disc I/O is live, pulse START/CROSS often so
        // title/logo/menu code that waits for controller ready or "Press START" advances.
        // Leave WaitSema park alone (PulseWaiters above); DETPS2_SEMA_STALL_YIELD stays OFF.
        if (sys.Cdvd.SectorsRead >= 100 && _padInjectPulses < 8192)
        {
            _padInjectPulses++;
            // PadInput uses active-high Press bits (see PadInput.Button).
            // Dense duty cycle: START and CROSS on alternate pulses with short idle gaps.
            int phase = _padInjectPulses % 5;
            uint buttons = phase switch
            {
                0 or 1 => (uint)PadInput.Button.Start,
                2 or 3 => (uint)PadInput.Button.Cross,
                _ => 0u, // brief release so edge-triggered readers see press/release
            };
            // Occasional dual-press (START+CROSS) for menus that want confirm.
            if (_padInjectPulses % 11 == 0)
                buttons = (uint)(PadInput.Button.Start | PadInput.Button.Cross);
            try { sys.Pad.SetButtons(buttons); } catch { /* Pad may be null early */ }
        }

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
        // Menu-draw kick once real asset I/O has pushed past RKV (ENGLISH.DIR / PRECODE).
        if (sys.Cdvd.SectorsRead >= 500 && sys.Gs.PixelsWritten < 10_000)
            MaybeKickMenuDraw(sys, c);
    }

    private bool _snPrintfStubbed;
    private bool _vtCallStubbed;

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
        // With dead $ra this re-enters exception / WaitSema forever with px=3.
        // Do NOT include 0x4520B0 (next function prologue) — live final5 thrash.
        bool atMenuEpilogue = pc is >= 0x00452080 and < 0x004520B0
            && sys.Gs.PixelsWritten < 50_000;
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
            if (_menuDrawKicks >= 24 && sys.Gs.PixelsWritten < 100)
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
                && sys.Gs.PixelsWritten < 100);
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
        // Sea of nops (dead after bad mid-function jump) — sample 4 words.
        if (pc is >= 0x00100000 and < 0x00200000)
        {
            int nops = 0;
            for (uint i = 0; i < 4; i++)
                if (sys.Memory.Read32(pc + i * 4) == 0) nops++;
            if (nops >= 3) return true;
        }
        // 0x479E04 is a real bit-pack utility (ori v0,v0,0xFFFF) — never a menu-draw
        // entry, but natural calls must run to completion. Do NOT treat as data thrash
        // (live agent-fix30: post-Bind sid=0x29 lands here legitimately; rescuing to
        // 0x48AF30 cold-corrupted RPC frames).
        // Trust resident .text inside the boot ELF window.
        if (pc is >= 0x00120000 and < 0x004A0000)
            return false;
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
