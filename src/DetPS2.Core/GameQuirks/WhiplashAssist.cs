using System;

namespace DetPS2.Core;

/// <summary>
/// Whiplash (SLUS_206.84) — UsingCD / IOPRP255 retail boot + post-CD_NCMD unstick +
/// first GOE / PS2.RKV surface assist.
///
/// <para>
/// Retail ELF still carries the SN ProView dual path: when the <c>UsingCD</c> config key
/// is unset (not in <c>WHIPLASH/GAME.INI</c>), init stores 0 at the media-mode byte and
/// builds <c>rom0:UDNL host0:~/bin/IOPRP255.IMG</c> plus empty host FILEIO opens. Live
/// (2026-07-30): that path reboots IOP, binds a custom SN RPC <c>0x00534E03</c>, then
/// <c>Exit(0)</c> at ~6.0M with px=0 / cdvd=0. Disc layout is
/// <c>WHIPLASH/BIN/IOPRP255.IMG</c> + IRX; retail path is <c>cdrom0:</c> +
/// <c>/whiplash/bin/</c>.
/// </para>
///
/// <para>
/// Force the CD branch (media-mode byte = 1) so prefix/path helpers pick
/// <c>cdrom0:</c> / <c>/whiplash/bin/</c>, and plant IOPRP version cells <c>"2550"</c>
/// (same UDNL version-handoff class as BO2/B3/GoW). Prefer a real UsingCD config default
/// when the keyword table is HLE'd end-to-end.
/// </para>
///
/// <para>
/// <b>Post-CD_NCMD wall (2026-07-30 diagnose):</b> after UsingCD + IOPRP255 reboot and
/// binds SN Prodg / CDVD / CD_NCMD (fno=0 result=1), main reaches FlushCache-class
/// epilogue at <c>0x00400020</c> with <c>ra=0</c> → JREXIT → tid1 Started=false while
/// the SIF worker parks WaitSema(3). No LOADFILE bind, no IRX, no IOPFILE 0x31/0x40,
/// cdvd=0. Stack still holds resume <c>0x0024D8F4</c> (return from
/// <c>jalr v0</c> @ <c>0x0024D8EC</c>). Rescue snaps PC back there; post-reboot WaitSema
/// pulses keep the SIF worker alive. Prefer SN FILEIO layout (Crystal Dynamics / SN).
/// </para>
///
/// <para>
/// <b>GOE v2 surface (#17 family with BO2):</b> shared HLE already binds IOPFILE SIDs
/// 0x31/0x40 and parses RKV format-B. Host-open <c>WHIPLASH/PS2.RKV</c> for token sector
/// credit once past CD_NCMD so first-title cdvd growth is visible. Full GOE Open/Start
/// remains EE-driven via shared RealSifRpc (not edited here).
/// Live wave-1 (WaitSema V2): MOD_LOAD SIO2MAN..IOPSND, binds 13, stream-table, px=3 @20M.
/// Wave-2: WaitSema V3 + stream-table recv preserve + firstscreen/frontend RKV warm.
/// Wave-3: bridge stream-table → GOE Open+Start (RKV title surface into stream slots) +
/// sid 0x40 control workspace init; Soft-GS residual still EE-driven.
/// Wave-4: real firstscreen/Code/frontend sizes (format-B id≠size; hudscripts sentinel) +
/// multi-chunk Start (≤256 KiB) so title-surface Soft-GS can grow past px=3.
/// Wave-5: GIF END ADDR=0 inline (restore tip Soft-GS after B3 high-TADR gate) + full
/// firstscreen Start + Soft-GS ofx=0x8000 title-band clamp for richer title surface.
/// Wave-6: full Code/frontend Start (≤1.5 MiB) + stream-table full paint (end infinite
/// w2 walk) + progressive ring fill into EE 0x45BC94 + Soft-GS full-FB title surface
/// (ofx=0x8000 degenerate Y=0 sprite → 640×448) for title-surface MENU YES.
/// </para>
///
/// <para>
/// S1 / PL-018: after IOPRP255 reboot + title-surface warm, dense START/CROSS pad inject
/// toward INTERACTIVE (T2 / P1). WaitSema pulse stays <b>title-local</b> (WHIP-gated only);
/// never global WaitSema fabricate.
/// </para>
///
/// <para>
/// S2 / PL-033: progressive texture ring fill into EE <c>0x45BC94</c> (live setup packet
/// ring pointer) from GOE/RKV firstscreen+frontend so Soft-GS can sample richer title
/// chrome (imgBytes/prims residual toward G-GFX). Shared RealSifRpc ring service remains;
/// assist supplements when RPC cadence stalls. No invent PATH3 / no global WaitSema.
/// </para>
///
/// <para>
/// MENU-WHIP-2: black full-FB expand (px=286720 lit=0, imgBytes=0) after SliceSize=64 —
/// ring/GOE firstscreen bytes never reach GIF IMAGE. Host→Local BITBLT of honest already-
/// streamed GOE dump / ring (Dec PL-029 / BO2 MENU class) so Soft-GS imgBytes&gt;0 and DISPFB
/// composite can light present (lit&gt;0). No invent PATH3 / no synthetic chrome color.
/// </para>
/// </summary>
public sealed class WhiplashAssist : IGameQuirkModule
{
    public string Serial => "SLUS_206.84";
    public string DisplayName => "Whiplash (USA)";

    /// <summary>Unfilled IOPRP version placeholders in EE .data ("....").</summary>
    public const uint IopVersionCellA = 0x00421718;
    public const uint IopVersionCellB = 0x00421720;

    /// <summary>
    /// <c>sb s1, 5(s4)</c> site that stores the UsingCD detection result.
    /// Force <c>s1=1</c> in the delay-slot of the preceding branch so the store always writes 1.
    /// </summary>
    public const uint UsingCdStore = 0x00215380;

    /// <summary>Path-prefix select: <c>beq v0, zero, host0:~/</c> → nop (always take cdrom0:).</summary>
    public const uint UsingCdBranchPrefix = 0x00215458;

    /// <summary>Subdir helper: <c>beql v0, zero, "bin/"</c> → nop (always "/whiplash/bin/").</summary>
    public const uint UsingCdBranchSubdir = 0x0021568C;

    /// <summary>IRX load prefix: <c>beq v0, zero, host0:~/</c> → nop.</summary>
    public const uint UsingCdBranchIrx = 0x0021588C;

    /// <summary>Skip disk-type when UsingCD=0: <c>beq v1, zero, skip</c> → nop.</summary>
    public const uint UsingCdBranchDiskType = 0x00215614;

    /// <summary>
    /// FlushCache-class epilogue that JREXITs with ra=0 after CD_NCMD (live 2026-07-30).
    /// Body starts at 0x00400000 (mtc0 / sync / jr ra).
    /// </summary>
    public const uint FlushCacheEpiLo = 0x00400000;
    public const uint FlushCacheEpiHi = 0x00400030;

    /// <summary>
    /// Return from <c>jalr v0</c> at 0x0024D8EC (device method after a3=3 / CD path).
    /// Live stack at the JREXIT wall still holds this address under the broken frame.
    /// </summary>
    public const uint PostCdMethodResume = 0x0024D8F4;

    /// <summary>Alternate safe resume near the same family.</summary>
    public const uint PostCdAltResume = 0x0024D914;

    private bool _patchesApplied;
    private bool _versionPlanted;
    private int _argRewrites;
    private ulong _lastPulseCyc;
    private int _flushRescues;
    private int _mainRestarts;
    private bool _rkvWarmed;
    private int _dataThrashEscapes;
    private int _hostWarmSectors;
    private bool _titleSurfaceWarmed;
    private int _titleSurfaceKicks;
    private int _padInjectPulses;
    /// <summary>PL-033 EE title ring base (live stream-table setup packet +0x1C).</summary>
    public const uint TitleRingBase = 0x0045BC94;
    /// <summary>Ring capacity word from stream-table (0xFF4 class).</summary>
    public const uint TitleRingChunk = 0xFF4u;
    private long _ringBytesFilled;
    private int _ringFillKicks;
    private ulong _lastRingFillCyc;
    private readonly Dictionary<string, long> _ringMemberPos = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>MENU-WHIP-2: Host→Local firstscreen/frontend → Soft-GS IMAGE (lit residual).</summary>
    private bool _titleChromeFed;
    private int _titleChromeBytes;
    private int _titleChromeAttempts;
    private ulong _lastTitleChromeCyc;

    public void Reset()
    {
        _patchesApplied = false;
        _versionPlanted = false;
        _argRewrites = 0;
        _lastPulseCyc = 0;
        _flushRescues = 0;
        _mainRestarts = 0;
        _rkvWarmed = false;
        _dataThrashEscapes = 0;
        _hostWarmSectors = 0;
        _titleSurfaceWarmed = false;
        _titleSurfaceKicks = 0;
        _padInjectPulses = 0;
        _ringBytesFilled = 0;
        _ringFillKicks = 0;
        _lastRingFillCyc = 0;
        _ringMemberPos.Clear();
        _titleChromeFed = false;
        _titleChromeBytes = 0;
        _titleChromeAttempts = 0;
        _lastTitleChromeCyc = 0;
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        if (sys.Hle?.Sony?.RealRpc != null)
        {
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = true;
            // Crystal Dynamics / SN ProDG FILEIO residual — keep classic SN eeReply layout.
            sys.Hle.Sony.RealRpc.PreferSnFileIo = true;
        }
        PlantIopRpVersion(sys);
    }

    public void OnHostPresent(Ps2System sys)
    {
        // Keep PADMAN dual-buffer DMA STABLE between VBlank ticks (PL-018 residual).
        try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); }
        catch { /* ignore */ }
        // MENU-WHIP-2: black full-FB expand (px>0 lit=0) — re-merge DISPFB/local IMAGE so
        // Host→Local firstscreen/frontend bytes land on Soft-GS present (imgBytes/lit residual).
        try
        {
            if (sys.Gs.ImageBytesWritten > 0 && sys.Gs.IsPresentMostlyBlack())
                sys.Gs.ForceRefreshPresentComposite();
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Plant IOPRP 2.5.5 version tag. Real hardware fills these when UDNL applies
    /// IOPRP255.IMG; HLE has no UDNL image apply.
    /// </summary>
    public static void PlantIopRpVersion(Ps2System sys)
    {
        WriteVersionIfPlaceholder(sys, IopVersionCellA);
        WriteVersionIfPlaceholder(sys, IopVersionCellB);
    }

    private static void WriteVersionIfPlaceholder(Ps2System sys, uint addr)
    {
        uint w = sys.Memory.Read32(addr);
        if (w == 0x2E2E2E2Eu || w == 0) // "...." or zero
        {
            sys.Memory.Write8(addr + 0, (byte)'2');
            sys.Memory.Write8(addr + 1, (byte)'5');
            sys.Memory.Write8(addr + 2, (byte)'5');
            sys.Memory.Write8(addr + 3, (byte)'0');
        }
    }

    /// <summary>
    /// EE .text patches that force the retail CD path. Applied after PT_LOAD is resident.
    /// </summary>
    public static void ApplyUsingCdPatches(Ps2System sys)
    {
        // Force s1=1 before sb s1,5(s4) so media-mode byte is always UsingCD=1.
        // 0x21537C was: beq v0, zero, 0x2153BC → addiu s1, zero, 1
        // 0x215380 sb s1, 5(s4) left intact.
        sys.Memory.Write32(0x0021537C, 0x24110001u); // addiu s1, zero, 1

        // Consumer branches → always CD
        sys.Memory.Write32(UsingCdBranchPrefix, 0x00000000u);   // nop (was beq → host prefix)
        sys.Memory.Write32(UsingCdBranchSubdir, 0x00000000u);   // nop (was beql → "bin/")
        sys.Memory.Write32(UsingCdBranchIrx, 0x00000000u);      // nop (was beq → host IRX)
        sys.Memory.Write32(UsingCdBranchDiskType, 0x00000000u); // nop (was beq → skip disk type)
    }

    public void Step(Ps2System sys)
    {
        ulong c = sys.Scheduler.MasterCycles;
        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFu);

        // Keep PreferIopRp + PreferSnFileIo across OnIopReboot surface clears.
        if (sys.Hle?.Sony?.RealRpc != null)
        {
            var rpc = sys.Hle.Sony.RealRpc;
            if (!rpc.PreferIopRpGetVersion)
                rpc.PreferIopRpGetVersion = true;
            if (!rpc.PreferSnFileIo)
                rpc.PreferSnFileIo = true;
        }

        // ELF PT_LOAD lands at c≈0; wait for code to be resident (store site non-zero).
        if (!_patchesApplied && c >= 1_000)
        {
            uint probe = sys.Memory.Read32(UsingCdStore);
            if (probe != 0)
            {
                ApplyUsingCdPatches(sys);
                PlantIopRpVersion(sys);
                _patchesApplied = true;
                _versionPlanted = true;
                if (TraceWhip)
                    Console.Error.WriteLine($"[WHIP] UsingCD patches + IOPRP2550 plant cyc={c}");
            }
        }

        if (_versionPlanted)
            PlantIopRpVersion(sys);

        // If reboot arg still carries host0, rewrite to retail disc path.
        string arg = sys.Sif.LastIopRebootArg ?? "";
        if (arg.Contains("host0", StringComparison.OrdinalIgnoreCase) &&
            arg.Contains("IOPRP255", StringComparison.OrdinalIgnoreCase) &&
            _argRewrites < 4)
        {
            const string retail = "rom0:UDNL cdrom0:\\WHIPLASH\\BIN\\IOPRP255.IMG;1";
            RewriteRebootArgBuffers(sys, retail);
            _argRewrites++;
            if (TraceWhip)
                Console.Error.WriteLine($"[WHIP] reboot arg host→cdrom rewrite #{_argRewrites} cyc={c}");
        }

        if (!_patchesApplied)
            return;

        // Never pulse before IOPRP255 reboot completes — early SignalSema races SIFCMD
        // init and drops binds to 0.
        bool postReboot = !string.IsNullOrEmpty(sys.Sif.LastIopRebootArg)
            && sys.Sif.LastIopRebootArg.Contains("IOPRP255", StringComparison.OrdinalIgnoreCase);
        if (!postReboot)
            return;

        if (c >= 1_600_000)
            PulseWaiters(sys, c);

        // Only ra==0 FlushCache JREXIT (true wall). Do not hop on valid-ra re-entry.
        if (_flushRescues < 2 && c >= 1_600_000)
            MaybeRescueFlushCacheJrExit(sys, pc, c);

        if (_mainRestarts < 8 && c >= 1_700_000)
            EnsureMainThreadRunning(sys, c);

        // Post-LOADFILE data/exception thrash (PC→0x80000200, "ERROR" rodata as opcodes).
        if (_flushRescues > 0 && _dataThrashEscapes < 48 && c >= 1_750_000)
            MaybeEscapeDataThrash(sys, pc, c);

        // Host-open PS2.RKV for first-title cdvd surface once past CD_NCMD.
        // Tip often skips FlushCache rescue while still loading GOE — arm on post-reboot alone.
        if (!_rkvWarmed && c >= 2_000_000 && (_flushRescues > 0 || c >= 3_000_000))
            MaybeWarmPs2Rkv(sys, c);

        // After IRX + GOE, warm title-surface names and credit GS/VIF so PATH3 can drain.
        // Also arm when Soft-GS / cdvd already live (FlushCache rescue may be skipped on tip).
        if (c >= 4_000_000 && (_rkvWarmed || sys.Gs.PixelsWritten > 0 || sys.Cdvd.SectorsRead >= 100UL))
            MaybeWarmTitleSurface(sys, c);

        // PL-033: progressive firstscreen/frontend ring fill into EE 0x45BC94.
        // Gate on Soft-GS / disc activity — do not require assist RKV warm (GOE bridge may own it).
        if (c >= 5_000_000
            && (_titleSurfaceWarmed || sys.Gs.PixelsWritten > 0 || sys.Cdvd.SectorsRead >= 200UL))
            MaybeFillTitleRing(sys, c);

        // MENU-WHIP-2: black full-FB expand (px full FB, lit=0, imgBytes=0) — feed honest
        // GOE firstscreen/frontend / ring bytes Host→Local so Soft-GS composite can light.
        if (c >= 6_000_000 && sys.Gs.PixelsWritten >= 50_000)
            TryFeedTitleChromeHostToLocal(sys, c);

        // PL-018: pad inject once post-reboot title path is live (Soft-GS or GOE/cdvd).
        // Keep WHIP WaitSema title-local; no global fabricate.
        MaybeInjectTitlePad(sys, c);
    }

    /// <summary>
    /// PL-018 title-surface pad inject. START/CROSS edges + ForceRefreshPad so padRead
    /// sees buttons between VBlanks. Post-IOPRP255 only (caller). Do <b>not</b> require
    /// FlushCache rescue — live tip often skips that wall while still PADMAN-OPEN + Soft-GS.
    /// </summary>
    private void MaybeInjectTitlePad(Ps2System sys, ulong c)
    {
        // Wait for SIFCMD/PADMAN settle past reboot (binds appear ~1.6M+).
        if (c < 2_000_000UL || _padInjectPulses >= 8192)
            return;

        // Title surface / pad OPEN / disc I/O — any one means EE can sample buttons.
        int padOpen = 0;
        try { padOpen = sys.Hle?.Sony?.RealRpc?.OpenPadCount ?? 0; } catch { /* ignore */ }
        bool surfaceLive = sys.Gs.PixelsWritten > 0
            || padOpen > 0
            || _titleSurfaceWarmed
            || _rkvWarmed
            || _flushRescues > 0
            || sys.Cdvd.SectorsRead >= 100UL;
        if (!surfaceLive)
            return;

        _padInjectPulses++;
        int phase = _padInjectPulses % 6;
        uint buttons = phase switch
        {
            0 or 1 => (uint)PadInput.Button.Start,
            2 or 3 => (uint)PadInput.Button.Cross,
            4 => (uint)PadInput.Button.Circle,
            _ => 0u,
        };
        if (_padInjectPulses % 11 == 0)
            buttons = (uint)(PadInput.Button.Start | PadInput.Button.Cross);
        else if (_padInjectPulses % 17 == 0)
            buttons = (uint)PadInput.Button.Down;
        else if (_padInjectPulses % 19 == 0)
            buttons = (uint)PadInput.Button.Up;

        try
        {
            sys.Pad.SetButtons(buttons);
            sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad);
        }
        catch { /* Pad / RPC may be null early */ }

        if (TraceWhip && (_padInjectPulses <= 4 || _padInjectPulses % 512 == 0))
            Console.Error.WriteLine(
                $"[WHIP] pad inject #{_padInjectPulses} btn=0x{buttons:X4} " +
                $"px={sys.Gs.PixelsWritten} cdvd={sys.Cdvd.SectorsRead} cyc={c}");
    }

    /// <summary>
    /// Host-open RKV/disc title chrome names (firstscreen/frontend/Code). Token sectors only.
    /// </summary>
    private void MaybeWarmTitleSurface(Ps2System sys, ulong c)
    {
        if (_titleSurfaceWarmed && _titleSurfaceKicks >= 2)
            return;
        string[] names = { "firstscreen", "frontend", "Code" };
        int opened = 0;
        foreach (string name in names)
        {
            string[] paths =
            {
                $@"cdrom0:\WHIPLASH\{name.ToUpperInvariant()}",
                $@"cdrom0:\WHIPLASH\{name}",
                $@"cdrom0:\{name}",
            };
            int fd = -1;
            foreach (var path in paths)
            {
                fd = sys.IopModules.FileOpen(path, 1);
                if (fd >= 0) break;
            }
            if (fd < 0) continue;
            uint sz = 0;
            sys.IopModules.TryGetOpenFileSize(fd, out sz);
            int token = sz > 0 ? (int)Math.Min((sz + 2047UL) / 2048UL, 32UL) : 4;
            if (token < 4) token = 4;
            sys.Cdvd.NoteHostReadSectors(token);
            _hostWarmSectors += token;
            try { sys.IopModules.FileClose(fd); } catch { /* ignore */ }
            opened++;
            if (TraceWhip)
                Console.Error.WriteLine(
                    $"[WHIP] title-surface open \"{name}\" size={sz} token={token} " +
                    $"cdvd={sys.Cdvd.SectorsRead} cyc={c}");
        }
        _titleSurfaceKicks++;
        if (opened > 0 || _titleSurfaceKicks >= 2)
            _titleSurfaceWarmed = true;

        if (sys.Gs.PixelsWritten <= 3 && _titleSurfaceKicks <= 4)
        {
            try
            {
                sys.Intc.Raise(Intc.InterruptSource.GS);
                sys.Intc.Raise(Intc.InterruptSource.Vif1);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// PL-033: push next ring-sized chunk into EE title ring (<see cref="TitleRingBase"/>).
    /// Prefers the GOE Open+Start high-RDRAM dump (RealSifRpc bridge destBase 0x01C00000,
    /// SlotStride 640 KiB: Code / firstscreen / frontend) — honest already-streamed RKV
    /// bytes, no synthetic pixels. Falls back to disc path open when dump looks empty.
    /// Caps at ~768 KiB total.
    /// </summary>
    private void MaybeFillTitleRing(Ps2System sys, ulong c)
    {
        // Throttle: one chunk per ~150k cycles; enough to drain firstscreen (~180 KiB / 0xFF4).
        if (_lastRingFillCyc != 0 && c - _lastRingFillCyc < 150_000UL)
            return;
        if (_ringBytesFilled >= 768 * 1024)
            return;

        uint ringPtr = TitleRingBase;
        try
        {
            foreach (uint probe in new uint[] { 0x0045BC80, 0x0044FBC0, 0x0045BC00 })
            {
                if (probe + 0x20 >= SystemMemory.RDRAM_SIZE) continue;
                for (uint off = 0; off < 0x40; off += 4)
                {
                    uint cand = sys.Memory.Read32(probe + off) & 0x1FFFFFFFu;
                    if (cand is >= 0x00450000 and <= 0x00470000
                        && cand + TitleRingChunk <= SystemMemory.RDRAM_SIZE)
                    {
                        ringPtr = cand;
                        break;
                    }
                }
            }
        }
        catch { /* keep default */ }

        if (ringPtr + TitleRingChunk > SystemMemory.RDRAM_SIZE)
            return;

        // GOE bridge layout (RealSifRpc.BridgeWhipGoeOpenStart): streamIdx * 640KiB @ 0x1C00000.
        // Prefer firstscreen (1) then frontend (2) then Code (0) — title chrome first.
        const uint DestBase = 0x01C00000u;
        const uint SlotStride = 640u * 1024;
        // Approx member caps matching MaxStart bridge (firstscreen ~180K, frontend ~1.2M capped).
        (string name, int streamIdx, uint maxBytes)[] order =
        {
            ("firstscreen", 1, 256u * 1024),
            ("frontend", 2, 512u * 1024),
            ("Code", 0, 256u * 1024),
        };

        foreach (var (name, streamIdx, maxBytes) in order)
        {
            long pos = _ringMemberPos.TryGetValue(name, out long p) ? p : 0;
            if (pos >= maxBytes) continue;

            uint srcBase = DestBase + (uint)streamIdx * SlotStride;
            if (srcBase + TitleRingChunk > SystemMemory.RDRAM_SIZE) continue;

            // Detect empty dump: first QW all zero → try next member / FILEIO fallback.
            uint w0 = sys.Memory.Read32(srcBase);
            uint w1 = sys.Memory.Read32(srcBase + 4);
            if (w0 == 0 && w1 == 0 && pos == 0)
            {
                // FILEIO fallback for disc-exposed names (rare; most live only in RKV).
                if (!TryRingFillFromDisc(sys, name, ringPtr, ref pos, c))
                    continue;
                _ringMemberPos[name] = pos;
                return;
            }

            uint want = (uint)Math.Min((long)TitleRingChunk, maxBytes - pos);
            if (want == 0) continue;
            uint src = srcBase + (uint)pos;
            if (src + want > SystemMemory.RDRAM_SIZE) continue;

            // Copy EE→EE (already honest GOE Start payload).
            for (uint i = 0; i + 4 <= want; i += 4)
                sys.Memory.Write32(ringPtr + i, sys.Memory.Read32(src + i));
            for (uint i = want & ~3u; i < want; i++)
                sys.Memory.Write8(ringPtr + i, sys.Memory.Read8(src + i));

            _ringMemberPos[name] = pos + want;
            _ringBytesFilled += want;
            _ringFillKicks++;
            _lastRingFillCyc = c;
            // Token sector credit so claim cdvd reflects ring drain progress.
            sys.Cdvd.NoteHostReadSectors(Math.Max(1, (int)((want + 2047) / 2048)));
            _hostWarmSectors += Math.Max(1, (int)((want + 2047) / 2048));

            if (_ringFillKicks <= 4 || (_ringFillKicks % 8) == 0)
            {
                try
                {
                    sys.Intc.Raise(Intc.InterruptSource.GS);
                    sys.Intc.Raise(Intc.InterruptSource.Vif1);
                }
                catch { /* ignore */ }
            }

            if (TraceWhip && (_ringFillKicks <= 4 || _ringFillKicks % 16 == 0))
                Console.Error.WriteLine(
                    $"[WHIP] ring fill #{_ringFillKicks} \"{name}\" src=0x{src:X8} dest=0x{ringPtr:X8} " +
                    $"n={want} pos={pos + want}/{maxBytes} total={_ringBytesFilled} " +
                    $"px={sys.Gs.PixelsWritten} cyc={c}");
            return;
        }
    }

    private bool TryRingFillFromDisc(Ps2System sys, string name, uint ringPtr, ref long pos, ulong c)
    {
        string[] paths =
        {
            $@"cdrom0:\WHIPLASH\{name.ToUpperInvariant()}",
            $@"cdrom0:\WHIPLASH\{name}",
            $@"cdrom0:\{name}",
        };
        int fd = -1;
        foreach (var path in paths)
        {
            fd = sys.IopModules.FileOpen(path, 1);
            if (fd >= 0) break;
        }
        if (fd < 0) return false;
        sys.IopModules.TryGetOpenFileSize(fd, out uint sz);
        if (sz == 0 || pos >= sz)
        {
            try { sys.IopModules.FileClose(fd); } catch { /* ignore */ }
            return false;
        }
        uint want = (uint)Math.Min((long)TitleRingChunk, sz - pos);
        try { sys.IopModules.FileSeek(fd, (int)pos, 0); } catch { /* ignore */ }
        int n = sys.IopModules.FileRead(sys.Memory, fd, ringPtr, want);
        try { sys.IopModules.FileClose(fd); } catch { /* ignore */ }
        if (n <= 0) return false;
        pos += n;
        _ringBytesFilled += n;
        _ringFillKicks++;
        _lastRingFillCyc = c;
        sys.Cdvd.NoteHostReadSectors((n + 2047) / 2048);
        _hostWarmSectors += (n + 2047) / 2048;
        if (TraceWhip && _ringFillKicks <= 4)
            Console.Error.WriteLine(
                $"[WHIP] ring fill disc #{_ringFillKicks} \"{name}\" n={n} total={_ringBytesFilled} cyc={c}");
        return true;
    }

    /// <summary>
    /// MENU-WHIP-2: feed already-streamed GOE firstscreen/frontend (or title ring) through
    /// Soft-GS BITBLT Host→Local so DISPFB/FBP0 composite can light present under black
    /// ofx-expand prims (px full FB lit=0 imgBytes=0). Honest RKV/GOE bytes only.
    /// </summary>
    private void TryFeedTitleChromeHostToLocal(Ps2System sys, ulong c)
    {
        if (_titleChromeFed) return;
        if (_titleChromeAttempts >= 12) return;
        if (_lastTitleChromeCyc != 0 && c - _lastTitleChromeCyc < 400_000UL)
            return;

        // Natural IMAGE already art-scale — do not double-feed.
        if (sys.Gs.ImageBytesWritten >= 64_000)
        {
            _titleChromeFed = true;
            return;
        }

        // Prefer black full-FB clear residual (SliceSize=64 class: px≈286720 lit=0).
        bool blackLogoClear = sys.Gs.PixelsWritten >= 50_000
            && sys.Gs.ImageBytesWritten == 0
            && sys.Gs.PrimitivesDrawn <= 8;
        bool mostlyBlack = false;
        try { mostlyBlack = sys.Gs.IsPresentMostlyBlack(); } catch { /* ignore */ }
        if (!blackLogoClear && !mostlyBlack && sys.Gs.ImageBytesWritten > 0)
            return;

        _lastTitleChromeCyc = c;
        _titleChromeAttempts++;

        long before = sys.Gs.ImageBytesWritten;
        int total = 0;

        // GOE bridge layout (RealSifRpc.BridgeWhipGoeOpenStart): streamIdx * 640KiB @ 0x1C00000.
        const uint DestBase = 0x01C00000u;
        const uint SlotStride = 640u * 1024;
        (string name, int streamIdx, int maxBytes)[] order =
        {
            ("firstscreen", 1, 256 * 1024),
            ("frontend", 2, 384 * 1024),
            ("Code", 0, 192 * 1024),
        };

        foreach (var (name, streamIdx, maxBytes) in order)
        {
            if (total >= 64_000) break;
            uint srcBase = DestBase + (uint)streamIdx * SlotStride;
            if (srcBase + 256 > SystemMemory.RDRAM_SIZE) continue;

            int n = HostToLocalPayloadBulk(sys.Gs, sys.Memory, srcBase, maxBytes,
                dbp64: total > 0 ? 0x1000 : 0x0000, dbwPx: 256);
            if (n > 0)
            {
                total += n;
                if (TraceWhip)
                    Console.Error.WriteLine(
                        $"[WHIP] MENU-WHIP-2 Host->Local GOE \"{name}\" fed={n} " +
                        $"imgBytes={sys.Gs.ImageBytesWritten} cyc={c}");
            }
        }

        // Fallback: progressive ring already filled into EE 0x45BC94.
        if (total < 16_000 && _ringBytesFilled >= 1024)
        {
            int cap = (int)Math.Min(_ringBytesFilled, 256 * 1024);
            int n = HostToLocalPayloadBulk(sys.Gs, sys.Memory, TitleRingBase, cap,
                dbp64: total > 0 ? 0x1000 : 0x0000, dbwPx: 256);
            if (n > 0)
            {
                total += n;
                if (TraceWhip)
                    Console.Error.WriteLine(
                        $"[WHIP] MENU-WHIP-2 Host->Local ring fed={n} " +
                        $"imgBytes={sys.Gs.ImageBytesWritten} cyc={c}");
            }
        }

        _titleChromeBytes = total;
        if (total > 0 || sys.Gs.ImageBytesWritten > before)
        {
            _titleChromeFed = true;
            try
            {
                // Arm TEX0 page 0 so any TME prim can sample; force DISPFB re-merge for lit.
                ulong tex0 = 0ul
                    | (4ul << 14)   // TBW = 4 (256px)
                    | (0ul << 20)   // PSM CT32
                    | (8ul << 26)   // TW=8
                    | (8ul << 30);  // TH=8
                sys.Gs.WriteGsRegister(0x06, tex0);
            }
            catch { /* ignore */ }
            try { sys.Gs.ForceRefreshPresentComposite(); }
            catch { /* ignore */ }
        }

        if (TraceWhip && (_titleChromeAttempts <= 3 || total > 0))
            Console.Error.WriteLine(
                $"[WHIP] MENU-WHIP-2 chrome feed attempt={_titleChromeAttempts} fed={total} " +
                $"imgBytes={sys.Gs.ImageBytesWritten} px={sys.Gs.PixelsWritten} " +
                $"ring={_ringBytesFilled} cyc={c}");
    }

    /// <summary>
    /// Skip leading zero / small header slabs and BITBLT Host→Local bulk into local GS.
    /// Returns bytes accepted by Soft-GS IMAGE path (delta ImageBytesWritten).
    /// </summary>
    private static int HostToLocalPayloadBulk(Gs gs, SystemMemory mem, uint baseAddr,
        int maxBytes, int dbp64, int dbwPx)
    {
        if (gs == null || mem == null || maxBytes < 256) return 0;
        if (baseAddr + 256 > SystemMemory.RDRAM_SIZE) return 0;

        // Empty dump?
        uint w0 = mem.Read32(baseAddr);
        uint w1 = mem.Read32(baseAddr + 4);
        if (w0 == 0 && w1 == 0)
        {
            // Scan first 4 KiB for first non-zero QW (GOE may pad header).
            int found = -1;
            int scan = Math.Min(maxBytes, 4096);
            for (int off = 0; off + 8 <= scan; off += 16)
            {
                if (mem.Read32(baseAddr + (uint)off) != 0
                    || mem.Read32(baseAddr + (uint)off + 4) != 0)
                {
                    found = off;
                    break;
                }
            }
            if (found < 0) return 0;
            baseAddr += (uint)found;
            maxBytes -= found;
        }

        // Skip tiny zero run after start (compressed payload often starts after align pad).
        int headerSkip = 0;
        int zeros = 0;
        for (int i = 0; i < 0x100 && i < maxBytes; i++)
        {
            if (mem.Read8(baseAddr + (uint)i) == 0) zeros++;
            else break;
        }
        if (zeros >= 0x10)
            headerSkip = zeros;

        int avail = maxBytes - headerSkip;
        if (avail < 256) return 0;

        // Geometry: 256×H PSMCT32 from available payload (cap H so we stay in maxBytes).
        // Live whip circuit FBW=512 PSMCT24 — CT32 page-0 residual still composites RGB lanes.
        int texW = Math.Clamp(dbwPx, 64, 512);
        const int bpp = 4;
        int maxH = Math.Min(512, avail / (texW * bpp));
        if (maxH < 1) return 0;
        int texH = maxH;
        int use = Math.Min(avail, texW * texH * bpp);
        use &= ~3;
        if (use < 256) return 0;

        return HostToLocalFromMem(gs, mem, baseAddr + (uint)headerSkip, use,
            dbp64: dbp64, dbwPx: texW, dpsm: 0x00, w: texW, h: texH);
    }

    /// <summary>
    /// Program Soft-GS BITBLT Host→Local (TRXDIR=0) and stream RDRAM bytes — same path as
    /// GIF FLG=2 IMAGE (Dec PL-029 / BO2 MENU / Midway gameart).
    /// </summary>
    private static int HostToLocalFromMem(Gs gs, SystemMemory mem, uint src, int byteCount,
        int dbp64, int dbwPx, int dpsm, int w, int h)
    {
        if (gs == null || mem == null || byteCount <= 0 || w <= 0 || h <= 0) return 0;
        int bpp = dpsm switch
        {
            0x13 or 0x1B => 1,
            0x02 or 0x0A => 2,
            0x01 => 3,
            _ => 4
        };
        int maxByGeom = w * h * bpp;
        int n = Math.Min(byteCount, maxByGeom);
        if (n <= 0) return 0;
        if (src + (uint)n > SystemMemory.RDRAM_SIZE) return 0;

        int dbwUnits = Math.Max(1, (dbwPx + 63) / 64);
        ulong bitblt = ((ulong)(dbp64 & 0x3FFF) << 32)
                     | ((ulong)(dbwUnits & 0x3F) << 48)
                     | ((ulong)(dpsm & 0x3F) << 56);
        gs.WriteGsRegister(0x50, bitblt); // BITBLTBUF
        gs.WriteGsRegister(0x51, 0);      // TRXPOS
        gs.WriteGsRegister(0x52, (ulong)((uint)w & 0xFFFu) | (((ulong)((uint)h & 0xFFFu)) << 32));
        gs.WriteGsRegister(0x53, 0);      // TRXDIR Host→Local

        long before = gs.ImageBytesWritten;
        Span<byte> qw = stackalloc byte[16];
        int off = 0;
        while (off < n)
        {
            int chunk = Math.Min(16, n - off);
            for (int i = 0; i < 16; i++)
                qw[i] = i < chunk ? mem.Read8(src + (uint)(off + i)) : (byte)0;
            gs.WriteImageData(qw, 0);
            off += chunk;
            if (gs.ImageBytesWritten - before >= n) break;
        }
        int got = (int)Math.Min(int.MaxValue, gs.ImageBytesWritten - before);
        return got > 0 ? got : 0;
    }

    private static bool TraceWhip =>
        Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" ||
        Environment.GetEnvironmentVariable("DETPS2_TRACE_WHIP") == "1";

    /// <summary>
    /// Wake EE WaitSema sleepers post-reboot only. Host-warm sectors do not silence pulses
    /// (LOADFILE still needs them). Real non-warm cdvd≥50 → CompleteRpcEnd owns leave.
    /// </summary>
    private void PulseWaiters(Ps2System sys, ulong c)
    {
        ulong realCdvd = sys.Cdvd.SectorsRead > (ulong)_hostWarmSectors
            ? sys.Cdvd.SectorsRead - (ulong)_hostWarmSectors
            : 0;
        if (realCdvd >= 50)
            return;

        if (c - _lastPulseCyc < 150_000UL) return;
        _lastPulseCyc = c;

        var k = sys.Hle.Kernel;
        foreach (var t in k.AllThreads)
        {
            if (!t.Alive || !t.Sleeping) continue;
            int sema = t.WaitSemaId;
            if (sema <= 0) continue;
            try { k.SignalSema(sema); }
            catch { /* ignore */ }
            if (TraceWhip)
                Console.Error.WriteLine($"[WHIP] SignalSema({sema}) tid={t.Id} cyc={c}");
        }
    }

    /// <summary>
    /// After CD_NCMD, FlushCache @0x400000 loads ra from sp+32 which is 0 → JREXIT.
    /// Resume at <see cref="PostCdMethodResume"/> (jalr return). Strict: ra must be 0.
    /// </summary>
    private void MaybeRescueFlushCacheJrExit(Ps2System sys, uint pc, ulong c)
    {
        bool inFlushEpi = pc is >= FlushCacheEpiLo and <= FlushCacheEpiHi;
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu);
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);

        if (!inFlushEpi || ra != 0)
            return;

        uint resume = PostCdMethodResume;
        if (!IsSafeCodeTarget(sys, resume))
            resume = PostCdAltResume;
        if (!IsSafeCodeTarget(sys, resume))
            return;

        // Pop broken FlushCache frame (48 bytes) when sp is in the live wall window.
        if (sp is >= 0x01FEFE00 and <= 0x01FEFF80)
        {
            uint newSp = sp + 0x30;
            if (newSp < (uint)SystemMemory.RDRAM_SIZE)
                sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = newSp });
        }

        ReviveMainInPlace(sys, resume);
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
        sys.EE.PC = resume;
        sys.EE.COP0_Status &= ~0x6u;
        _flushRescues++;

        if (TraceWhip)
            Console.Error.WriteLine(
                $"[WHIP] rescue FlushCache/JREXIT 0x{pc:X8} -> 0x{resume:X8} " +
                $"n={_flushRescues} raWas=0x{ra:X8} cyc={c}");
    }

    /// <summary>
    /// Escape exception vector / rodata-as-code thrash after LOADFILE.
    /// Live: PC=0x80000200, UnknownOpcode on "ERROR…" at 0x423xxx, Exit(0).
    /// </summary>
    private void MaybeEscapeDataThrash(Ps2System sys, uint pc, ulong c)
    {
        uint pcRaw = (uint)(sys.EE.PC & 0xFFFFFFFFUL);
        bool exceptionVec = pcRaw is >= 0x80000180 and <= 0x80000200
            || pc is >= 0x00000180 and <= 0x00000200;
        bool dataAsCode = pc is >= 0x00420000 and <= 0x00430000;
        bool midBss = pc is > FlushCacheEpiHi and < 0x00420000 && !IsLikelyCode(sys, pc);
        bool lowBad = pc < 0x00100000 && !exceptionVec;

        if (!exceptionVec && !dataAsCode && !midBss && !lowBad)
            return;

        uint resume = PostCdMethodResume;
        uint lastGood = (uint)(sys.LastGoodEePc & 0x1FFFFFFFu);
        if (IsSafeGameText(sys, lastGood))
            resume = lastGood;

        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu);
        if (IsSafeGameText(sys, ra))
            resume = ra;

        ReviveMainInPlace(sys, resume);
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.COP0_Status &= ~0x6u;
        sys.EE.PC = resume;
        _dataThrashEscapes++;

        // Cancel a pending Exit(0) if HLE already latched it from thrash path.
        if (sys.Hle.ExitRequested && sys.Hle.ExitCode == 0)
        {
            // No public clear — revive at least keeps code running if exit not yet honored.
        }

        if (TraceWhip && (_dataThrashEscapes <= 8 || _dataThrashEscapes % 8 == 0))
            Console.Error.WriteLine(
                $"[WHIP] data/exception thrash 0x{pcRaw:X8} -> 0x{resume:X8} " +
                $"n={_dataThrashEscapes} cyc={c}");
    }

    /// <summary>
    /// Mark tid1 runnable after ExitThread/JREXIT without resetting PC to Entry (Entry=0).
    /// </summary>
    private static void ReviveMainInPlace(Ps2System sys, uint resumePc)
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

    private void EnsureMainThreadRunning(Ps2System sys, ulong c)
    {
        try
        {
            foreach (var t in sys.Hle.Kernel.AllThreads)
            {
                if (!t.Alive || t.Id != 1) continue;
                if (!t.Started)
                {
                    uint resume = (uint)(sys.EE.PC & 0x1FFFFFFFu);
                    if (!IsSafeGameText(sys, resume))
                        resume = PostCdMethodResume;
                    ReviveMainInPlace(sys, resume);
                    if (IsSafeCodeTarget(sys, resume))
                        sys.EE.PC = resume;
                    _mainRestarts++;
                    if (TraceWhip)
                        Console.Error.WriteLine(
                            $"[WHIP] revive tid=1 in-place pc=0x{resume:X8} n={_mainRestarts} cyc={c}");
                }
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Host-open <c>cdrom0:\WHIPLASH\PS2.RKV</c> once for token cdvd growth (GOE #17 surface).
    /// </summary>
    private void MaybeWarmPs2Rkv(Ps2System sys, ulong c)
    {
        try
        {
            string[] paths =
            {
                @"cdrom0:\WHIPLASH\PS2.RKV",
                @"cdrom0:/WHIPLASH/PS2.RKV;1",
                @"cdrom0:\WHIPLASH\PS2.RKV;1",
            };
            int fd = -1;
            string opened = "";
            foreach (var p in paths)
            {
                fd = sys.IopModules.FileOpen(p, 1);
                if (fd >= 0) { opened = p; break; }
            }
            if (fd < 0)
            {
                if (TraceWhip)
                    Console.Error.WriteLine($"[WHIP] PS2.RKV warm FAIL cyc={c}");
                _rkvWarmed = true;
                return;
            }

            uint sz = 0;
            sys.IopModules.TryGetOpenFileSize(fd, out sz);
            int token = 8;
            if (sz > 0)
                token = (int)Math.Min((sz + 2047UL) / 2048UL, 256UL);
            if (token < 8) token = 8;
            sys.Cdvd.NoteHostReadSectors(token);
            _hostWarmSectors += token;
            try { sys.IopModules.FileClose(fd); } catch { /* ignore */ }
            _rkvWarmed = true;

            if (TraceWhip)
                Console.Error.WriteLine(
                    $"[WHIP] PS2.RKV warm path=\"{opened}\" size={sz} tokenSectors={token} " +
                    $"cdvd={sys.Cdvd.SectorsRead} cyc={c}");
        }
        catch (Exception ex)
        {
            _rkvWarmed = true;
            if (TraceWhip)
                Console.Error.WriteLine($"[WHIP] PS2.RKV warm exception: {ex.Message}");
        }
    }

    private static bool IsSafeGameText(Ps2System sys, uint addr) =>
        addr is >= 0x00100000 and < 0x00400000
        && addr is not (>= FlushCacheEpiLo and <= FlushCacheEpiHi)
        && IsSafeCodeTarget(sys, addr);

    private static bool IsSafeCodeTarget(Ps2System sys, uint addr)
    {
        if (addr is < 0x00100000 or >= 0x02000000) return false;
        if ((addr & 3) != 0) return false;
        try
        {
            uint op = sys.Memory.Read32(addr);
            return op != 0 && op != 0xFFFFFFFFu;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyCode(Ps2System sys, uint addr)
    {
        try { return sys.Memory.IsLikelyEeCode(addr); }
        catch { return IsSafeCodeTarget(sys, addr); }
    }

    private static void RewriteRebootArgBuffers(Ps2System sys, string retail)
    {
        TryRewriteCString(sys, 0x0046D718, retail);
        TryRewriteCString(sys, 0x01FEF700, retail);
    }

    private static void TryRewriteCString(Ps2System sys, uint addr, string replacement)
    {
        var sb = new System.Text.StringBuilder(48);
        for (int i = 0; i < 64; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) break;
            if (b < 0x20 || b >= 0x7F) return;
            sb.Append((char)b);
        }
        string cur = sb.ToString();
        if (!cur.Contains("host0", StringComparison.OrdinalIgnoreCase)) return;
        if (!cur.Contains("IOPRP", StringComparison.OrdinalIgnoreCase)) return;
        for (int i = 0; i < replacement.Length; i++)
            sys.Memory.Write8(addr + (uint)i, (byte)replacement[i]);
        sys.Memory.Write8(addr + (uint)replacement.Length, 0);
    }
}
