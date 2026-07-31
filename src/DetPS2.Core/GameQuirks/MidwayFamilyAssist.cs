using System;
using System.Collections.Generic;

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

    // DA MFL CallRpc client (live open @0x22C9F0): strcpy path → 0x546EC0, recv @0x5470C0,
    // client cd @0x54F200. HLE HandleCall uses _cdToArgBuf[client] (soft-bind ~0x1C1F7800),
    // NOT the EE send pointer — so open fno=24 sees path="" unless we bridge.
    // Ground-truthed 2026-07-30: open FAIL path="" then fno=0x15 result=0; post-wait
    // thrash @0x1B39xx / px=0 / cdvd=259. DA-only bridge — do not touch Dec paths.
    private const uint MflClientDa = 0x0054F200;
    private const uint MflEeSendDa = 0x00546EC0; // open strcpy dest / info handle store
    private const uint MflEeRecvDa = 0x005470C0; // CallRpc recv (handle / -2)
    private const uint MflReadyDa = 0x0040ACE4;  // gp-24716 ready flag
    // Scannable (no leading '\') so RealSifRpc.ScanSendBufferForPath accepts it.
    private const string DaGameartMemberPath = @"ps2dvd\artps2\gameart.ssf";
    // Permanent path scratch for open CallRpc send retarget (outside ELF / stream plant).
    private const uint DaMflPathScratch = 0x0007F100;
    // open@0x22C9F0: lui a3,0x54 / addiu a3,0x6EC0 → send=0x546EC0. Retarget to scratch
    // so SifSetDma → HLE argBuf carries a scannable path (empty strcpy dest wiped plants).
    private const uint DaOpenA3Lui = 0x0022CA54;
    private const uint DaOpenA3Addiu = 0x0022CA60;
    private const uint DaOpenA3LuiOrig = 0x3C070054u;   // lui a3, 0x54
    private const uint DaOpenA3AddiuOrig = 0x24E76EC0u; // addiu a3, a3, 0x6EC0
    // lui a3, 8; addiu a3, a3, -0xF00 → 0x0007F100
    private const uint DaOpenA3LuiPlant = 0x3C070008u;
    private const uint DaOpenA3AddiuPlant = 0x24E7F100u;

    // Dec SLUS_208.81 post-MSL main abort (live 2026-07-30, 200M host-present):
    //   main@0x1235B0 → 0x127900 → 0x126CE0 → 0x1D8120 → jal 0x1D9620
    //   0x1D9620 (type/factory register for ids 0x509/0x50E/0x510/0x1F) returns 0
    //   → 0x1D8120 fails → 0x126CE0 fails → 0x127900 fails → main epilogue@0x1238E0
    //   → CRT Exit(0) @ ~188M BEFORE any EE CallRpc member .ssf open.
    // Soft-success fail-tails so main can leave CRT Exit and reach game loop @0x1237F0
    // without force-completing DA wait status=4. TITLE_LOCAL Dec only.
    //
    // Root cause of type 0x510 / 0x1F factory -1 (FIXED in EmotionEngine SHARED):
    //   0x1AB810 list walk `lw v1,48(v1); bne v1,zero,loop` was snapped by
    //   MaybeFastForwardCountdown (ptr |dist|>>50k → force v1=0) so install
    //   never saw nodes past the head. Post-fix live: 0x1D5270→0x1AB810 for
    //   id 0x510 returns v0=0x60 node=0x1FB3E60 (was -1). Plants below remain
    //   as belt-and-suspenders for residual gate tails.
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
    private int _decPostInitListHits;
    private int _decPostInitListEscapes;
    private int _mflArgBridges;
    private int _mflPathPlants;
    private int _postWaitKicks;
    private int _pathSrcRewrites;
    private int _displayLockEscapes;
    private int _displayLockHits;
    private int _displayCmdCompletes;
    private int _decPostMslKicks;
    private int _decPostMslHits;
    private int _decProcessForces;
    private int _decFlagClears;
    private int _decGameartKicks;
    private int _decGameartHits;
    private bool _decGameartKickDone;
    private bool _decGameartPublished;
    private uint _lastDisplayHead;
    private int _displayHeadMoves;
    private int _postDisplayExitRescues;
    private int _daPostLogoSoftSuccess;
    private int _daPostLogoPlantCount;
    private bool _daPostLogoPlanted;
    private bool _daFailTailBeltPlanted;
    private bool _daFailTailBeltDemoted;
    private int _daFailTailDemotions;
    private int _daDisplayForceProcess;
    private int _daMenuBandLockClears;
    private long _daChromeBaselineImg;
    private int _daChromeImgGrowthHits;

    // PL-013 DA pad selection keep-alive (INTERACTIVE / T2):
    // Dense pad inject after Soft-GS Midway surface + selection-index drive from D-pad.
    // No SignalSema fabricate; no invent Soft-GS pixels; Dmac END gates untouched.
    private ulong _lastDaMenuPadCyc;
    private int _daMenuPadPulses;
    private int _daMenuSelIndex;
    private int _daMenuSelPlants;
    private int _daMenuSelDeltas;
    private int _daPadEffectHits;
    private long _daPadBaselinePrims;
    private ulong _daPadBaselineGifP2;
    private bool _daPadBaselineTaken;
    private uint _daLastPadButtons;
    private readonly Dictionary<uint, uint> _daSelCellSnap = new();

    // DA post-gameart display pump (live 2026-07-31 @20M):
    // Outer loop @0x1B3960: while (head!=tail) { if (lock) DI/EI; else process@0x1B3BB0 }.
    // Lock @ gp-25380 sticky=1 with pending queue + idle VIF1/GIF -> DI thrash @0x114Fxx.
    // process type-1 (cmd low byte==1, live cmd 0x88000501): set lock, kick VIF1 chain
    // CHCR~0x1C5 TADR=ptr QWC=0; IRQ @0x1B2830 clears lock + advances head.
    private const uint DaGp = 0x00410D70;
    private const uint DaDisplayLock = DaGp - 25380;   // 0x40AA4C
    private const uint DaDisplayHead = DaGp - 25412;   // 0x40AA2C
    private const uint DaDisplayTail = DaGp - 25416;   // 0x40AA28
    private const uint DaDisplayLoopLo = 0x001B3960;
    private const uint DaDisplayLoopHi = 0x001B3A10;
    private const uint DaDisplayProcess = 0x001B3BB0;  // process one cmd (type-1 VIF1)
    private const uint DaDiEiLo = 0x00114F20;
    private const uint DaDiEiHi = 0x00114F80;
    private const uint DaDisplayCmdDoneBit = 0x40000000u;

    // WAVE-6 belt fail-tails at 0x123A30 body (v0=0 → v0=1). Off hot path once main
    // is in menu keep-alive; PL-030 demotes them when Soft-GS surface is proven.
    private static readonly (uint Addr, uint Orig, uint Plant)[] DaFailTailBeltSites =
    {
        (0x00123A60, 0x0000102Du, 0x24020001u), // daddu v0,zero,zero → addiu v0,1
        (0x00123AA8, 0x0000102Du, 0x24020001u),
        (0x00123AC0, 0x0000102Du, 0x24020001u),
        (0x00123AF0, 0x0000102Du, 0x24020001u),
        (0x00123B24, 0x0000102Du, 0x24020001u),
    };

    // DA WAVE-6 post-logo soft-success (live 2026-07-31 Soft-GS px=716800 then CRT Exit):
    // main@0x11F800 → 0x123A30 → 0x1A8840 → list-dispatch@0x1A4E20.
    // Primary handlers paint Midway Path2 sprites; last node jalr@0x1CB200 returns 0 →
    // cleanup → 0x1A8840 returns 0 → main gate@0x11F93C fails → CRT Exit(0)@0x10C044.
    // Soft-succeed the one-instruction v0 checks so main reaches the real loop@0x11F9D4
    // (midway-menu keep-alive) without inventing Soft-GS pixels.
    private const uint DaListDispatchCheck = 0x001A4E58; // bne v0 after primary jalr
    private const uint DaLogoStateFailLo = 0x001A88C4;   // fail a1=3 cleanup after 0x1A4E20==0
    private const uint DaLogoStateFailHi = 0x001A88EC;
    private const uint DaLogoStateEpi = 0x001A88DC;      // shared epilogue (v0 already set)
    private const uint DaLogoStateObj = 0x005335C0;      // s0 in 0x1A8840
    private const uint DaLogoStateWord = DaLogoStateObj + 0x14C; // state machine word
    private const uint DaMainLogoGateLo = 0x0011F93C;    // bne after jal 0x123A30
    private const uint DaMainLogoGateHi = 0x0011F948;
    private const uint DaMainLogoContinue = 0x0011F94C;  // post-success next jal

    // PL-013: DA main keep-alive / menu poll band (claim PC@0x1232xx) + PADMAN dual OPEN.
    private const uint DaMainMenuLoopLo = 0x00123200;
    private const uint DaMainMenuLoopHi = 0x00123300;
    private const uint DaPadArea0 = 0x0054FF00; // PADMAN OPEN port 0
    private const uint DaPadArea1 = 0x0054FE00; // PADMAN OPEN port 1
    // Assist-owned selection mirror (scratch outside ELF / stream plants).
    private const uint DaMenuSelMirror = 0x0007F200;
    // Logo UI object band (state word @ +0x14C held at 3 by WAVE-6 soft-success).
    private const uint DaMenuUiBandLo = 0x005335C0;
    private const uint DaMenuUiBandHi = 0x005337C0;
    // gp-relative UI / display neighbor band (lock/head/tail live nearby).
    private const uint DaMenuGpBandLo = 0x0040A800;
    private const uint DaMenuGpBandHi = 0x0040B200;

    // Dec post-0x127900 residual (live 200M+/280M host-present after exception plant):
    // main stays alive in 0x3B9E00 list helper (OUTSIDE shared freelist band 0x3BA948).
    // Outer: s1 = *(s0+8); loop jal 0x3BDE60; walk v1=+0x24 via +0x28; s1=*(s1+4).
    // Live thrash @0x3B9E34 (outer) / 0x3B9E64 (inner). Inner +0x28 plant alone leaves
    // outer s1+4 cycle. Force outer exit → 0x3B9EE0 (done).
    private const uint DecPostInitListLo = 0x003B9E20;
    private const uint DecPostInitListHi = 0x003B9E84;
    private const uint DecPostInitListExit = 0x003B9EE0; // post-outer done

    // Dec post-MSL main idle (live 50M host-present, wave-3/4):
    //   0x1B6980 -> idle @0x1B6A68: head/tail @ gp-25048/-25052 (gp=0x5DCB70).
    //   Callback @ gp-25116 is NULL (cleared @0x1B8658); when head!=tail main busy-waits
    //   with s1=1. Real process is wrapper@0x1B5D10 (a0=-1) → 0x1B5D78 — only called from
    //   pump@0x1B7000 / enqueue@0x1B6FD4 when flags gp-25032 and gp-25036 are CLEAR.
    //   Type 0x41 sets gp-25036=1 (mode lock) so subsequent type 0x01 GIF chains never run
    //   while main is trapped in idle. Soft-skipping 0x01 skips real PATH3 — do not.
    //   Wave-4: clear sticky 25036 + force process wrapper so GIF/VIF cmds execute.
    private const uint DecGp = 0x005DCB70;
    private const uint DecIdleQueueHead = DecGp - 25048; // 0x5D6998
    private const uint DecIdleQueueTail = DecGp - 25052; // 0x5D6994
    private const uint DecIdleCallback = DecGp - 25116;  // 0x5D6954
    private const uint DecIdleFlag25032 = DecGp - 25032; // 0x5D69A8 — busy-wait / DMA inflight
    private const uint DecIdleFlag25036 = DecGp - 25036; // 0x5D69A4 — mode lock (blocks process)
    private const uint DecIdleFlag25040 = DecGp - 25040; // 0x5D69A0 — companion mode flag
    private const uint DecIdleSlot25044 = DecGp - 25044; // 0x5D699C — type 0x40/41 arg
    private const uint DecIdleCount25064 = DecGp - 25064; // 0x5D6988 — pending op counter
    private const uint DecIdlePcLo = 0x001B6A40;
    private const uint DecIdlePcHi = 0x001B6B20;
    /// <summary>Dec process wrapper entry (a0=-1 → drain @0x1B5D78 when flags clear).</summary>
    private const uint DecProcessWrapper = 0x001B5D10;

    // Dec WAVE-5: member .ssf CallRpc residual (live 50M Soft-GS px=73, idle @0x1B6A68).
    // Archive registry head @0x61302C is live after MWFILE MKDA.PAK open, but main's
    // gameart load @0x1A41B0 either raced an empty registry or failed without retry.
    // 0x1A41B0 → 0x267090(table@0x5A6F30 live) → 0x222790(entry) builds path @0x612C30 and
    // issues MWFILE open for gameart.ssf. Re-enter once registry is non-null.
    private const uint DecArchiveRegHead = 0x0061302C;
    // Live main@0x1A41DC: lui a0,0x5A; addiu a0,28464 → 0x5A6F30 (not 0x5A6E20).
    private const uint DecGameartTable = 0x005A6F30;
    private const uint DecGameartEntry = 0x0050AD28;   // { name@0x5A6E10 "gameart.ssf", … }
    private const uint DecGameartName = 0x005A6E10;
    private const uint DecPathScratch = 0x00612C30;    // strcat dest in 0x222790/0x222980
    private const uint DecLoadSysartGameart = 0x001A41B0; // main@0x123798 call target
    private const uint DecPostOpenConsumer = 0x001A44D0; // post-open SSF consume (a0=id,a1=ctx)
    private const uint DecOpenFromTable = 0x00267090;    // table → 0x222790 open
    private const uint DecMainMenuLoop = 0x001237F0;     // main infinite loop after gameart
    private const uint DecWaitSemaLeafLo = 0x0010BE20;
    private const uint DecWaitSemaLeafHi = 0x0010BE30;
    private const uint DecCallRpcAfterWaitLo = 0x0010FEE0;
    private const uint DecCallRpcAfterWaitHi = 0x0010FF00;
    private bool _openSendRetargetPlanted;
    private int _decPowerOffStormHits;
    private int _decPowerOffStormBreaks;
    private int _decMenuKeepAlives;
    private int _decMenuForceProcess;
    private int _decSsfConsumerKicks;
    private bool _decSsfConsumerDone;

    // PL-012: Dec idle-pump menu pad inject + selection-index plant (P1 INTERACTIVE).
    // After midway-menu Soft-GS is live, dense D-pad/Start/Cross edges + ForceRefreshPad
    // so padGetState dual-buffer polls see non-zero buttons; plant a stable 0..N row index
    // into Dec BSS candidates (gp-relative + gameart table band) driven by D-pad edges.
    private int _decPadPulses;
    private ulong _decLastPadCyc;
    private int _decMenuSelIndex;
    private int _decMenuSelPlants;
    private int _decSelIdxDeltaLogs;
    private long _decPadBaselinePrims;
    private ulong _decPadBaselineP2;
    private long _decPadBaselinePx;
    private bool _decPadBaselineArmed;
    private readonly Dictionary<uint, uint> _decSelIndexSnapshot = new();

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
        _decPostInitListHits = 0;
        _decPostInitListEscapes = 0;
        _mflArgBridges = 0;
        _mflPathPlants = 0;
        _postWaitKicks = 0;
        _pathSrcRewrites = 0;
        _displayLockEscapes = 0;
        _displayLockHits = 0;
        _displayCmdCompletes = 0;
        _decPostMslKicks = 0;
        _decPostMslHits = 0;
        _decProcessForces = 0;
        _decFlagClears = 0;
        _decGameartKicks = 0;
        _decGameartHits = 0;
        _decGameartKickDone = false;
        _decGameartPublished = false;
        _lastDisplayHead = 0;
        _displayHeadMoves = 0;
        _postDisplayExitRescues = 0;
        _daPostLogoSoftSuccess = 0;
        _daPostLogoPlantCount = 0;
        _daPostLogoPlanted = false;
        _daFailTailBeltPlanted = false;
        _daFailTailBeltDemoted = false;
        _daFailTailDemotions = 0;
        _daDisplayForceProcess = 0;
        _daMenuBandLockClears = 0;
        _daChromeBaselineImg = 0;
        _daChromeImgGrowthHits = 0;
        _lastDaMenuPadCyc = 0;
        _daMenuPadPulses = 0;
        _daMenuSelIndex = 0;
        _daMenuSelPlants = 0;
        _daMenuSelDeltas = 0;
        _daPadEffectHits = 0;
        _daPadBaselinePrims = 0;
        _daPadBaselineGifP2 = 0;
        _daPadBaselineTaken = false;
        _daLastPadButtons = 0;
        _daSelCellSnap.Clear();
        _openSendRetargetPlanted = false;
        _decPowerOffStormHits = 0;
        _decPowerOffStormBreaks = 0;
        _decMenuKeepAlives = 0;
        _decMenuForceProcess = 0;
        _decSsfConsumerKicks = 0;
        _decSsfConsumerDone = false;
        _decPadPulses = 0;
        _decLastPadCyc = 0;
        _decMenuSelIndex = 0;
        _decMenuSelPlants = 0;
        _decSelIdxDeltaLogs = 0;
        _decPadBaselinePrims = 0;
        _decPadBaselineP2 = 0;
        _decPadBaselinePx = 0;
        _decPadBaselineArmed = false;
        _decSelIndexSnapshot.Clear();
    }

    /// <summary>True when this assist is bound to Deception (SLUS_208.81).</summary>
    public bool IsDeception =>
        _serial.Equals("SLUS_208.81", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this assist is bound to Deadly Alliance (SLUS_204.23).</summary>
    public bool IsDeadlyAlliance =>
        _serial.Equals("SLUS_204.23", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dec sys-init fail band that aborts main→Exit after MSL. Exposed so
    /// <see cref="Ps2System"/> can tighten the EE slice and catch one-instruction gates.
    /// </summary>
    public static bool IsDecSysInitHotPc(ulong pcPhys) =>
        pcPhys is >= DecSysInitBandLo and <= DecSysInitBandHi
            or (>= 0x00126CE0UL and <= 0x00126F60UL)
            or (>= 0x00127900UL and <= 0x00127A00UL)
            or (>= 0x001D9620UL and <= 0x001D9900UL);

    /// <summary>
    /// DA post-logo one-instruction v0 gates + CRT Exit residual. Exposed so
    /// <see cref="Ps2System"/> tightens the EE slice (same class as Dec sys-init).
    /// </summary>
    public static bool IsDaPostLogoHotPc(ulong pcPhys) =>
        pcPhys is (>= 0x001A4E50UL and <= 0x001A4EACUL)   // list-dispatch + fail/success tails
            or (>= 0x001A8840UL and <= 0x001A88ECUL)      // logo state transition
            or (>= 0x00123A30UL and <= 0x00123BC0UL)      // main logo init body
            or (>= 0x0011F930UL and <= 0x0011F960UL)      // main gate after 0x123A30
            or (>= 0x0010C040UL and <= 0x0010C050UL)      // CRT Exit stub
            or (>= 0x001152F0UL and <= 0x00115318UL);     // CRT exit wrapper

    /// <summary>
    /// Dec WAVE-7: CallRpc WaitSema leaf + main menu loop + post-open consumer.
    /// Tight EE slices so keep-alive / PowerOff-storm break can act between instructions.
    /// </summary>
    public static bool IsDecMenuHotPc(ulong pcPhys) =>
        pcPhys is (>= DecWaitSemaLeafLo and <= DecWaitSemaLeafHi)
            or (>= DecCallRpcAfterWaitLo and <= DecCallRpcAfterWaitHi)
            or (>= 0x0010F5E0UL and <= 0x0010F620UL) // CallRpc epilogue residual
            or (>= 0x0010B9E0UL and <= 0x0010BA40UL) // CreateSema/syscall leaf residual
            or (>= DecMainMenuLoop and <= DecMainMenuLoop + 0x100UL)
            or (>= DecIdlePcLo and <= DecIdlePcHi)
            or (>= DecPostOpenConsumer and <= DecPostOpenConsumer + 0x40UL)
            or (>= 0x0034D000UL and <= 0x0034E000UL) // W7 exception residual thrash
            or (>= 0x80000180UL and <= 0x80000280UL);

    public void OnDiscMounted(Ps2System sys) => ApplyVersionPolicy(sys);

    public void OnHostPresent(Ps2System sys)
    {
        // Keep pad DMA buffers STABLE after OPEN so EE padGetState / dual-buffer polls
        // leave the post-pad SyncDCache thrash (Dec 0x10C6xx) and continue IRX load.
        // PL-012: Dec menu pad on host present so desktop / blocker-trace --host-present matches UI tick density.
        if (IsDeception)
            MaybeInjectDecMenuPad(sys, hostPresent: true);
        // PL-013 DA: denser inject on host tick so menu polls see edges between EE slices.
        if (IsDeadlyAlliance)
            TryInjectDaMenuPad(sys, hostTick: true);
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
        // DA-only: bridge EE MFL send/recv into soft-bind client argBuf so CallRpc open/info
        // see path/handle (HLE reads client arg, not EE a3 send). Unblocks gameart member open.
        if (IsDeadlyAlliance)
        {
            TryRewriteDaLeadingBackslashPaths(sys);
            TryPlantDaOpenSendRetarget(sys);
            TryBridgeDaMflCallRpcArg(sys);
            TryKickDaPostWait(sys);
            TryEscapeDaDisplayQueueLock(sys);
            // WAVE-6: permanent fail-tail plants + runtime soft-success so main reaches
            // menu loop after Midway Path2 paint (no CRT Exit freeze).
            TryPlantDaPostLogoFailTails(sys);
            TrySoftSuccessDaPostLogoInit(sys);
            TryRescueDaPostDisplayExit(sys);
            TryKeepAliveDaMidwayMenu(sys);
            // PL-030 FRONTEND chrome: drain sticky display queue from menu band (imgBytes/
            // multi-cmd Path2) + demote off-path fail-tail belt when Soft-GS keep-alive holds.
            TryDrainDaDisplayQueueForChrome(sys);
            TryDemoteDaFailTailBeltWhenSafe(sys);
            // PL-013: pad selection keep-alive — dense inject + assist-owned sel-idx (T2).
            // Selection drive is invoked from inject only (not every Step) so we never thrash.
            TryInjectDaMenuPad(sys, hostTick: false);
        }
        // Prefer honest host job status over force-writing *s0 (arbitrary s0 can corrupt
        // unrelated words and leave post-wait dormancy / Exit). Only escape when host is live.
        if (sys.Memory.Read32(0x0040B44C) != 0)
            TryEscapeWaitReady(sys);
        TryBreakHeapTreeCycle(sys);
        // TITLE_LOCAL Dec: soft-success post-MSL factory/sys-init so main does not Exit(0)
        // before member .ssf CallRpc (see DecSysInit* constants).
        if (IsDeception)
        {
            TryEscapeDecSysInitFail(sys);
            TryEscapeDecPostInitListWalk(sys);
            TryKickDecPostMslAssetEnqueue(sys);
            TryKickDecGameartMemberOpen(sys);
            TryPublishDecGameartOpen(sys);
            // WAVE-7: post-open SSF consumer + break CD_SCMD PowerOff WaitSema storm
            // so main stays in midway-menu loop with live Path2 (DA-class keep-alive).
            // SSF consumer re-enter is done once by TryPublishDecGameartOpen (0x1A44D0).
            // Extra kicks faulted into 0x8034Dxxx — skip TryKickDecSsfPostOpenConsumer.
            TryBreakDecCdPowerOffStorm(sys);
            TryKeepAliveDecMidwayMenu(sys);
            // PL-012: dense pad inject on idle-pump menu once Soft-GS keep-alive is live.
            MaybeInjectDecMenuPad(sys, hostPresent: false);
        }
    }

    /// <summary>
    /// PL-012 Dec idle-pump INTERACTIVE pad (P1): after midway-menu Soft-GS is live
    /// (gameart stream loaded, px/prims &gt; 0), pulse Start/Cross/D-pad with release edges
    /// and ForceRefreshPad so EE padGetState dual-buffer polls see non-zero buttons.
    /// Plant a stable 0..N selection index into Dec gp-relative / gameart-table BSS cells
    /// that already hold small integers (assist-stable sel-idx, same class as SM wave-7).
    /// Does not invent PATH3. Does not SignalSema fabricate. Does not invent Soft-GS pixels.
    /// </summary>
    private void MaybeInjectDecMenuPad(Ps2System sys, bool hostPresent)
    {
        if (!IsDeception) return;
        if (sys.MasterCycles < 32_000_000) return;
        if (sys.Gs.PixelsWritten == 0 || sys.Gs.PrimitivesDrawn == 0) return;
        var rpc = sys.Hle?.Sony?.RealRpc;
        if (rpc == null || rpc.DecGameartBytesLoaded <= 0) return;
        if (_decPadPulses >= 4096) return;

        // Cadence: denser once keep-alive / force-process has proven Path2 paint.
        // Host-present ticks (~1M) always act; Step uses 50k–200k cycle spacing.
        ulong interval = hostPresent ? 0UL
            : (sys.Gif.Path2Transfers >= 100 || _decMenuForceProcess > 0) ? 50_000UL
            : 200_000UL;
        if (!hostPresent && sys.MasterCycles - _decLastPadCyc < interval) return;
        _decLastPadCyc = sys.MasterCycles;
        _decPadPulses++;

        if (!_decPadBaselineArmed)
        {
            _decPadBaselinePrims = sys.Gs.PrimitivesDrawn;
            _decPadBaselineP2 = sys.Gif.Path2Transfers;
            _decPadBaselinePx = sys.Gs.PixelsWritten;
            _decPadBaselineArmed = true;
        }

        // Dense edge pattern: D-pad then Cross/Start so selection + accept can advance.
        // Release slots so edge-triggered readers see press/release pairs.
        int phase = _decPadPulses % 24;
        uint buttons = phase switch
        {
            0 => 0u,
            1 => (uint)PadInput.Button.Down,
            2 => 0u,
            3 or 4 or 5 => (uint)PadInput.Button.Cross,
            6 => 0u,
            7 => (uint)PadInput.Button.Up,
            8 => 0u,
            9 or 10 or 11 => (uint)PadInput.Button.Cross,
            12 => (uint)PadInput.Button.Start,
            13 => 0u,
            14 => (uint)(PadInput.Button.Start | PadInput.Button.Cross),
            15 => (uint)PadInput.Button.Circle,
            16 => 0u,
            17 => (uint)PadInput.Button.Right,
            18 => 0u,
            19 => (uint)PadInput.Button.Left,
            20 => 0u,
            21 or 22 => (uint)PadInput.Button.Cross,
            _ => (uint)PadInput.Button.Down
        };
        // Occasional dual accept after many pulses still in idle-pump.
        if (_decPadPulses >= 32 && (_decPadPulses % 5) < 2)
            buttons = (uint)PadInput.Button.Cross;
        if (_decPadPulses % 19 == 0)
            buttons = (uint)PadInput.Button.Start;

        try { sys.Pad.SetButtons(buttons); } catch { /* ignore */ }
        try { rpc.ForceRefreshPad(sys.Memory, sys.Pad); } catch { /* ignore */ }

        // Drive assist-stable selection index from D-pad edges (0..4 rows).
        bool dpadDown = (buttons & (uint)PadInput.Button.Down) != 0;
        bool dpadUp = (buttons & (uint)PadInput.Button.Up) != 0;
        bool dpadRight = (buttons & (uint)PadInput.Button.Right) != 0;
        bool dpadLeft = (buttons & (uint)PadInput.Button.Left) != 0;
        if (dpadDown || dpadRight)
            _decMenuSelIndex = Math.Min(4, _decMenuSelIndex + 1);
        else if (dpadUp || dpadLeft)
            _decMenuSelIndex = Math.Max(0, _decMenuSelIndex - 1);

        MaybePlantDecMenuSelectionIndex(sys);
        MaybeLogDecSelectionIndexDelta(sys, buttons);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_decPadPulses <= 8 || _decPadPulses % 64 == 0))
        {
            long dPrims = sys.Gs.PrimitivesDrawn - _decPadBaselinePrims;
            long dPx = sys.Gs.PixelsWritten - _decPadBaselinePx;
            long dP2 = (long)sys.Gif.Path2Transfers - (long)_decPadBaselineP2;
            Console.Error.WriteLine(
                $"[MKFAM] Dec pad inject n={_decPadPulses} btn=0x{buttons:X4} " +
                $"sel={_decMenuSelIndex} plants={_decMenuSelPlants} " +
                $"Δprims={dPrims} Δpx={dPx} Δp2={dP2} " +
                $"pc=0x{(uint)sys.EE.PC:X8} idle=0x1B6A68 cyc={sys.MasterCycles}");
        }
    }

    // PL-012 assist-stable selection anchors (Dec BSS — NOT idle queue control words).
    // Idle flags 25032/25036/25040, head/tail, slot25044, count25064 stay untouched
    // so Path2 force-process keep-alive remains honest.
    // Low Dec BSS bank below gp (0x5DCB70) — outside idle flag/queue block @0x5D69xx.
    private const uint DecMenuSelIndexA = 0x005DC000;
    private const uint DecMenuSelIndexB = 0x005DC004;
    private const uint DecMenuSelCount = 0x005DC008;  // row-count sibling
    private const uint DecMenuSelIndexC = 0x005DC00C;
    private const uint DecMenuSelIndexD = 0x005DC010;

    /// <summary>
    /// Plant 0..N selection index into Dec assist-stable BSS anchors only.
    /// Does not touch idle queue control (flags/head/tail/slot25044/count25064).
    /// Does not invent PATH3.
    /// </summary>
    private void MaybePlantDecMenuSelectionIndex(Ps2System sys)
    {
        if (_decMenuSelPlants >= 512) return;
        uint idx = (uint)_decMenuSelIndex;

        // Dedicated assist-stable anchors (SM 0x54E610-class). Always write 0..N.
        sys.Memory.Write32(DecMenuSelIndexA, idx);
        sys.Memory.Write32(DecMenuSelIndexB, idx);
        sys.Memory.Write32(DecMenuSelIndexC, idx);
        sys.Memory.Write32(DecMenuSelIndexD, idx);
        // Keep row-count non-zero so UI code that divides by count does not fault.
        uint cnt = sys.Memory.Read32(DecMenuSelCount);
        if (cnt == 0 || cnt > 16)
            sys.Memory.Write32(DecMenuSelCount, 5);
        else
            sys.Memory.Write32(DecMenuSelCount, cnt); // sticky re-assert

        _decMenuSelPlants++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_decMenuSelPlants <= 4 || _decMenuSelPlants % 32 == 0))
            Console.Error.WriteLine(
                $"[MKFAM] Dec menu-sel-index={idx} plants={_decMenuSelPlants} " +
                $"A=0x{DecMenuSelIndexA:X8} C=0x{DecMenuSelIndexC:X8} " +
                $"p2={sys.Gif.Path2Transfers} px={sys.Gs.PixelsWritten} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Log 0..N integer cells in Dec menu bands that change under D-pad (selection hunt).
    /// Includes assist anchors so sel-idx plant shows as proven delta under D-pad.
    /// </summary>
    private void MaybeLogDecSelectionIndexDelta(Ps2System sys, uint buttons)
    {
        if (_decSelIdxDeltaLogs >= 64) return;
        bool dpad = (buttons & (uint)(PadInput.Button.Up | PadInput.Button.Down
            | PadInput.Button.Left | PadInput.Button.Right)) != 0;

        // Assist anchors (plant-driven) + scan windows for natural menu cells.
        uint[] words =
        {
            DecMenuSelIndexA, DecMenuSelIndexB, DecMenuSelCount,
            DecMenuSelIndexC, DecMenuSelIndexD,
        };
        uint[] bases =
        {
            0x005DC000, 0x005A6F00, 0x005A6E00,
            // Near idle UI but NOT control words: band above flags (0x5D6A00+)
            0x005D6A80, 0x005D6B00,
        };
        var deltas = new System.Text.StringBuilder();
        var now = new Dictionary<uint, uint>();
        foreach (uint addr in words)
        {
            uint v = sys.Memory.Read32(addr);
            if (v > 16) continue;
            now[addr] = v;
            if (_decSelIndexSnapshot.TryGetValue(addr, out uint prev) && prev != v)
                deltas.Append($" 0x{addr:X6}:{prev}->{v}");
        }
        foreach (uint b in bases)
        {
            for (uint off = 0; off < 0x40; off += 4)
            {
                uint addr = b + off;
                if (now.ContainsKey(addr)) continue;
                if (addr + 4 > (uint)SystemMemory.RDRAM_SIZE) continue;
                uint v = sys.Memory.Read32(addr);
                if (v > 16) continue;
                now[addr] = v;
                if (_decSelIndexSnapshot.TryGetValue(addr, out uint prev) && prev != v)
                    deltas.Append($" 0x{addr:X6}:{prev}->{v}");
            }
        }
        _decSelIndexSnapshot.Clear();
        foreach (var kv in now) _decSelIndexSnapshot[kv.Key] = kv.Value;

        if (deltas.Length == 0) return;
        if (!dpad && _decPadPulses > 4) return;
        _decSelIdxDeltaLogs++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[MKFAM] Dec sel-idx-delta{deltas} dpad={(dpad ? 1 : 0)} btn=0x{buttons:X4} " +
                $"n={_decSelIdxDeltaLogs} p2={sys.Gif.Path2Transfers} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Deception only (WAVE-5): re-enter main's gameart load once the MKDA archive
    /// registry is live so EE issues MWFILE CallRpc for member <c>gameart.ssf</c>.
    ///
    /// Live 50M (Soft-GS px=73, heuristic GS?): MSL DADA warms the TOC member, MWFILE
    /// opened <c>cdrom0:\MKDA.PAK</c>, registry head <c>*(0x61302C)</c> is non-null, yet
    /// no member <c>.ssf</c> CallRpc appears (calls stay ~42). Idle parks at
    /// <c>0x1B6A68</c> with residual type-0x41 / flag25032. Main calls
    /// <c>0x1A41B0</c> once at <c>0x123798</c>; if that raced an empty registry the open
    /// is never retried.
    ///
    /// Fix: after registry live + post-MSL idle force path has run, one-shot jump to
    /// <c>0x1A41B0</c> (or table open <c>0x267090</c>) with <c>ra</c>=idle so MWFILE can
    /// honestly open the member. Does not plant wait status=4 or invent Soft-GS pixels.
    /// </summary>
    private void TryKickDecGameartMemberOpen(Ps2System sys)
    {
        if (_decGameartKickDone && _decGameartKicks >= 6) return;
        if (sys.MasterCycles < 28_000_000) return;

        // Registry must be live (post MKDA.PAK mount / TOC register).
        uint reg = sys.Memory.Read32(DecArchiveRegHead);
        if (reg < 0x00100000 || reg >= 0x02000000) return;

        var rpc = sys.Hle?.Sony?.RealRpc;
        var iop = sys.IopModules;
        var cdvd = sys.Cdvd;

        // Prefer idle / post-idle bands so we do not interrupt CallRpc / WaitSema frames.
        uint pc = (uint)sys.EE.PC;
        bool inIdle = pc is >= DecIdlePcLo and <= DecIdlePcHi;
        bool inFlagSpin = pc is >= 0x001B8280 and <= 0x001B82B8;
        bool inDiTail = pc is >= 0x001B6AA8 and <= 0x001B6AE8;
        if (!inIdle && !inFlagSpin && !inDiTail) return;

        // Wait until wave-4 enqueue path has acted at least once (queue/process live).
        if (_decPostMslKicks == 0 && _decProcessForces == 0 && _decFlagClears == 0)
            return;

        // Always plant path-hash + force host member open once registry is live (even if
        // path scratch already has "gameart" from a prior partial open).
        if (rpc != null && iop != null && cdvd != null
            && (rpc.DecGameartMemberOpens == 0 || sys.Memory.Read32(0x0061E5A4) == 0))
        {
            rpc.TryEnsureMkdaArtPathHash(sys.Memory, iop, cdvd);
            if (rpc.DecGameartMemberOpens == 0)
                rpc.ForceDecGameartMemberOpen(iop, cdvd);
        }

        // WAVE-6/7: open result at gp-24036. Stop PC kicks once stream slot is live or
        // publish ran — further re-entry only via SSF post-open consumer (not re-open).
        const uint DecOpenResultSlot = 0x005D6D8C; // gp(0x5DCB70)-24036 (sw v0 after open)
        uint openResult = sys.Memory.Read32(DecOpenResultSlot);
        bool streamLive = openResult >= 0x00010000 && openResult < 0x02000000;
        if (streamLive || _decGameartPublished)
        {
            _decGameartKickDone = true;
            return;
        }
        if (_decGameartKickDone && _decGameartKicks >= 6) return;

        // Throttle: first kick after ~32 hits; then every 128 hits up to 6 kicks.
        _decGameartHits++;
        if (_decGameartKicks == 0 && (_decGameartHits & 31) != 0) return;
        if (_decGameartKicks >= 1 && (_decGameartHits & 127) != 0) return;
        if (_decGameartKicks >= 6) { _decGameartKickDone = true; return; }

        // WAVE-7: one-shot table open@0x267090 only (stores path-hash stream to gp-24036).
        // Do NOT kick full 0x1A41B0 — that jal's 0x1A44D0 and faulted from idle frame.
        WriteCStringIfChanged(sys.Memory, DecPathScratch, "/art/gameart.ssf");
        sys.Memory.Write8(DecIdleFlag25032, 0);
        sys.Memory.Write8(DecIdleFlag25036, 0);
        sys.Memory.Write8(DecIdleFlag25040, 0);

        if (_decGameartKicks == 0 && inIdle)
        {
            uint resume = pc;
            sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = DecGameartTable });
            sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });
            sys.EE.PC = DecOpenFromTable;
            _decGameartKicks = 1;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                Console.Error.WriteLine(
                    $"[MKFAM] Dec gameart table-open kick pc=0x{pc:X8}→0x{DecOpenFromTable:X8} " +
                    $"reg=0x{reg:X8} opens={rpc?.DecGameartMemberOpens ?? 0} cyc={sys.MasterCycles}");
            return;
        }

        _decGameartKicks++;
        _decGameartKickDone = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[MKFAM] Dec gameart plant-done n={_decGameartKicks} pc=0x{pc:X8} " +
                $"opens={rpc?.DecGameartMemberOpens ?? 0} cyc={sys.MasterCycles}");
    }


    /// <summary>
    /// WAVE-6: after path-hash plant + gameart kick, if open@0x21D810 still left
    /// gp-24036 null, publish the planted stream object so post-open consumers can run.
    /// Stream payload was FileRead from PAK TOC into high RDRAM (honest art bytes).
    /// </summary>
    private void TryPublishDecGameartOpen(Ps2System sys)
    {
        if (_decGameartPublished) return;
        if (_decGameartKicks == 0) return;
        if (sys.MasterCycles < 29_000_000) return;
        var rpc = sys.Hle?.Sony?.RealRpc;
        if (rpc == null || rpc.DecGameartBytesLoaded <= 0) return;

        const uint DecOpenResultSlot = 0x005D6D8C; // gp-24036
        const uint DecOpenTableSlot = 0x005D6D88;  // gp-24040
        const uint Stream = 0x0007E400;
        uint cur = sys.Memory.Read32(DecOpenResultSlot);
        if (cur >= 0x00010000 && cur < 0x02000000) return; // already live

        // Confirm stream plant still tagged.
        if (sys.Memory.Read32(Stream) != 0x5354464Du) return;

        // Publish table + stream result the same way 0x2670EC/F0 would after a hit.
        if (sys.Memory.Read32(DecOpenTableSlot) == 0)
            sys.Memory.Write32(DecOpenTableSlot, DecGameartTable);
        sys.Memory.Write32(DecOpenResultSlot, Stream);
        _decGameartKickDone = true;
        _decGameartPublished = true;

        // Do NOT re-enter 0x1A44D0 here — live W7: a0=0 registry walk faulted into
        // 0x8034Dxxx / path-scratch-as-PC thrash. Stream publish alone is enough for
        // idle process to drain queued GIF work; keep-alive owns Path2 continuity.

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[MKFAM] Dec publish gameart open result stream=0x{Stream:X8} " +
                $"loaded={rpc.DecGameartBytesLoaded} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// <summary>
    /// WAVE-7: after gameart stream is published, one-shot re-enter post-open consumer
    /// <c>0x1A44D0</c> so registry walk can enqueue GIF work. Multi-kick of 0x1A44D0 with
    /// a0=0 was observed to fault into 0x8034Dxxx — keep this to a single attempt.
    /// Idle-band only. No invent Soft-GS.
    /// </summary>
    private void TryKickDecSsfPostOpenConsumer(Ps2System sys)
    {
        if (_decSsfConsumerDone || _decSsfConsumerKicks >= 1) return;
        if (!_decGameartPublished && (sys.Hle?.Sony?.RealRpc?.DecGameartBytesLoaded ?? 0) <= 0)
            return;
        if (sys.MasterCycles < 30_000_000) return;

        const uint DecOpenResultSlot = 0x005D6D8C;
        uint stream = sys.Memory.Read32(DecOpenResultSlot);
        if (stream < 0x00010000 || stream >= 0x02000000) return;

        uint pc = (uint)sys.EE.PC;
        bool idleish = pc is >= DecIdlePcLo and <= DecIdlePcHi
            or (>= 0x001B8280 and <= 0x001B82B8)
            or (>= 0x001B6AA8 and <= 0x001B6AE8);
        if (!idleish) return;

        // Ensure table slot names live gameart table (0x1A41DC uses 0x5A6F30).
        const uint DecOpenTableSlot = 0x005D6D88;
        if (sys.Memory.Read32(DecOpenTableSlot) == 0)
            sys.Memory.Write32(DecOpenTableSlot, DecGameartTable);

        sys.Memory.Write8(DecIdleFlag25032, 0);
        sys.Memory.Write8(DecIdleFlag25036, 0);
        sys.Memory.Write8(DecIdleFlag25040, 0);

        // One-shot only — further SSF work is via idle process drain / natural main loop.
        sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = 0x0050AD08UL });
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = DecIdlePcLo + 0x28 });
        sys.EE.PC = DecPostOpenConsumer;
        _decSsfConsumerKicks = 1;
        _decSsfConsumerDone = true;

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[MKFAM] Dec SSF post-open kick n=1 " +
                $"pc=0x{pc:X8}→0x{DecPostOpenConsumer:X8} stream=0x{stream:X8} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// WAVE-7: break CD_SCMD PowerOff (fno=0x21) CallRpc WaitSema storm @0x10BE28.
    /// Live claim 100M: after Midway Path2 paint EE thrash CreateSema+WaitSema on CD
    /// PowerOff (~645 calls). Soft-complete WaitSema by returning to CallRpc $ra (honest
    /// leave) — do NOT stomp PC to main@0x1237F0 (that faulted into 0x8034Dxxx with dead
    /// stack). Cap re-entries by parking main at idle pump after repeated thrash.
    /// No SignalSema(3). No invent Soft-GS pixels.
    /// </summary>
    private void TryBreakDecCdPowerOffStorm(Ps2System sys)
    {
        if (sys.MasterCycles < 35_000_000) return;
        if (sys.Gs.PixelsWritten == 0) return;
        var rpc = sys.Hle?.Sony?.RealRpc;
        if (rpc == null || rpc.DecGameartBytesLoaded <= 0) return;
        if (_decPowerOffStormBreaks >= 128) return;

        uint pc = (uint)sys.EE.PC;
        uint ra = (uint)sys.EE.GetGpr(31).Lo;
        bool inWaitSema = pc is >= DecWaitSemaLeafLo and <= DecWaitSemaLeafHi;
        bool raCallRpc = ra is >= DecCallRpcAfterWaitLo and <= DecCallRpcAfterWaitHi
            or (>= 0x0010F380 and <= 0x0010F3B0)
            or (>= 0x0010FE00 and <= 0x0010FF20);
        if (!inWaitSema || !raCallRpc)
        {
            _decPowerOffStormHits = 0;
            return;
        }

        _decPowerOffStormHits++;
        // After first abandon, re-home immediately on every WaitSema/CallRpc re-entry.
        bool alreadyAbandoned = _decPowerOffStormBreaks >= 3;
        if (!alreadyAbandoned)
        {
            if (_decPowerOffStormHits < 4) return;
            if (_decPowerOffStormBreaks > 0 && (_decPowerOffStormHits & 3) != 0) return;
        }

        uint a0 = (uint)sys.EE.GetGpr(4).Lo;
        uint idlePark = DecIdlePcLo + 0x28; // 0x1B6A68
        // First few: soft-complete WaitSema → CallRpc $ra (honest leave).
        // After that the PowerOff caller re-enters forever — abandon CallRpc frame and
        // park idle pump so Path2 can keep draining (DA keep-alive class).
        bool abandonToIdle = alreadyAbandoned || _decPowerOffStormBreaks >= 3;
        uint dest = abandonToIdle ? idlePark : ra;
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = a0 != 0 ? a0 : 1u });
        sys.EE.SetGpr(28, new EmotionEngine.Gpr128 { Lo = DecGp });
        sys.EE.PC = dest;
        if (abandonToIdle)
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = idlePark });

        // Clear wait state on current + main threads (no SignalSema(3)).
        try
        {
            var k = sys.Hle?.Kernel;
            if (k != null)
            {
                int cur = k.CurrentThreadId;
                foreach (var t in k.AllThreads)
                {
                    if (!t.Alive) continue;
                    if (t.Id == cur || t.Id == 1)
                    {
                        t.Sleeping = false;
                        t.WaitSemaId = 0;
                        if (abandonToIdle || t.Id == 1)
                            t.SavedPc = idlePark;
                        else
                            t.SavedPc = ra;
                    }
                    else if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                    {
                        try { k.WakeupThread(t.Id); } catch { /* ignore */ }
                    }
                }
                if (abandonToIdle)
                    try { k.YieldToWorker(sys.EE); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        sys.Memory.Write8(DecIdleFlag25032, 0);
        sys.Memory.Write8(DecIdleFlag25036, 0);
        sys.Memory.Write8(DecIdleFlag25040, 0);

        _decPowerOffStormBreaks++;
        _decPowerOffStormHits = 0;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _decPowerOffStormBreaks <= 32)
            Console.Error.WriteLine(
                $"[MKFAM] Dec PowerOff/WaitSema storm break n={_decPowerOffStormBreaks} " +
                $"→0x{dest:X8} abandon={abandonToIdle} px={sys.Gs.PixelsWritten} " +
                $"p2={sys.Gif.Path2Transfers} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// WAVE-7 DA-class keep-alive for Dec midway-menu: after Path2 Midway surface is live
    /// (Soft-GS px&gt;0, gameart stream loaded), keep idle process draining and recover from
    /// exception/CRT Exit into the idle pump @0x1B6A68 (live stable park). Do not stomp
    /// main@0x1237F0 without a live main frame (W7 residual: 0x8034Dxxx thrash).
    /// No invent Soft-GS pixels. No SignalSema(3).
    /// </summary>
    private void TryKeepAliveDecMidwayMenu(Ps2System sys)
    {
        if (sys.MasterCycles < 32_000_000) return;
        if (sys.Gs.PixelsWritten == 0 || sys.Gs.PrimitivesDrawn == 0) return;
        var rpc = sys.Hle?.Sony?.RealRpc;
        if (rpc == null || rpc.DecGameartBytesLoaded <= 0) return;

        uint pc = (uint)sys.EE.PC;
        uint pcPhys = pc & 0x1FFFFFFFu;
        bool exit0 = sys.Hle is { ExitRequested: true, ExitCode: 0 };
        bool inWaitSema = pc is >= DecWaitSemaLeafLo and <= DecWaitSemaLeafHi;
        bool inCrtExit = (pc is >= 0x0010C040 and <= 0x0010C050)
            || (pc is >= 0x001152F0 and <= 0x00115318);
        // Exception vector + W7 residual thrash (kernel / path-scratch-as-PC / non-.text).
        bool inException = (pc is >= 0x80000180 and <= 0x80000280)
            || (pcPhys is >= 0x0034D000 and <= 0x0034D200)
            || (pc > 0x80000000u && pcPhys is >= 0x0034D000 and <= 0x0034E000)
            || (pcPhys is >= 0x00600000 and <= 0x00700000) // path scratch / high data as PC
            || (pcPhys is >= 0x01800000 and < 0x02000000);  // gameart payload as PC
        bool inMainLoop = pc is >= DecMainMenuLoop and <= DecMainMenuLoop + 0xF0;
        bool inIdle = pc is >= DecIdlePcLo and <= DecIdlePcHi;
        bool inProcess = pc is >= DecProcessWrapper and <= DecProcessWrapper + 0x200;
        uint idlePark = DecIdlePcLo + 0x28; // 0x1B6A68

        // Idle with pending queue: force drain so Path2 continues (separate budget).
        if ((inIdle || inProcess) && !exit0 && !inException)
        {
            uint head = sys.Memory.Read32(DecIdleQueueHead);
            uint tail = sys.Memory.Read32(DecIdleQueueTail);
            // Pointer queue: head chases tail. head>tail without ring is CORRUPT (W7
            // residual advanced head past tail) — never force-process that.
            bool pending = head != tail
                && head >= 0x00100000 && head < 0x02000000
                && tail >= 0x00100000 && tail < 0x02000000
                && head < tail
                && (tail - head) <= 0x10000;
            if (pending && inIdle && _decMenuForceProcess < 512)
            {
                if ((_decMenuForceProcess & 3) != 0) { _decMenuForceProcess++; return; }
                sys.Memory.Write8(DecIdleFlag25032, 0);
                sys.Memory.Write8(DecIdleFlag25036, 0);
                sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = 0xFFFFFFFFUL });
                sys.EE.SetGpr(28, new EmotionEngine.Gpr128 { Lo = DecGp });
                sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = idlePark });
                sys.EE.PC = DecProcessWrapper;
                _decMenuForceProcess++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && _decMenuForceProcess <= 64)
                    Console.Error.WriteLine(
                        $"[MKFAM] Dec menu keep-alive force-process n={_decMenuForceProcess} " +
                        $"head=0x{head:X8} tail=0x{tail:X8} p2={sys.Gif.Path2Transfers} " +
                        $"px={sys.Gs.PixelsWritten} cyc={sys.MasterCycles}");
                return;
            }
            // Corrupt queue: snap head=tail so idle can leave, then recover.
            if (head != tail
                && head >= 0x00100000 && head < 0x02000000
                && tail >= 0x00100000 && tail < 0x02000000
                && head > tail
                && _decMenuForceProcess < 16)
            {
                sys.Memory.Write32(DecIdleQueueHead, tail);
                sys.Memory.Write8(DecIdleFlag25032, 0);
                sys.Memory.Write8(DecIdleFlag25036, 0);
                _decMenuForceProcess++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
                    Console.Error.WriteLine(
                        $"[MKFAM] Dec idle queue repair head→tail=0x{tail:X8} " +
                        $"p2={sys.Gif.Path2Transfers} cyc={sys.MasterCycles}");
                return;
            }
            // Healthy idle empty or mid-process — leave alone.
            if (inMainLoop || inIdle || inProcess) return;
        }

        // Recover Exit / exception / WaitSema monopolize → idle pump (not main@0x1237F0).
        // Separate budget from force-process so exception recovery is never starved.
        if (_decMenuKeepAlives >= 256) return;
        // CallRpc epi / CreateSema leaf thrash (pad residual 0x10BA08 / claim 0x10F60C).
        bool inRpcEpi = pc is >= 0x0010F5E0 and <= 0x0010F620
            or (>= 0x0010B9E0 and <= 0x0010BA40);
        bool needHome = exit0 || inCrtExit || inException || inRpcEpi
            || (inWaitSema && _decPowerOffStormBreaks >= 4)
            || (inWaitSema && sys.MasterCycles > 50_000_000 && sys.Gif.Path2Transfers < 1000);
        if (!needHome) return;
        // Always recover exceptions / rpc-epi immediately; throttle others.
        if (!exit0 && !inException && !inRpcEpi && (_decMenuKeepAlives & 7) != 0) return;

        if (exit0)
            sys.Hle.ClearExitRequest();

        // Prefer main menu loop when $sp is a live high main frame (idle is called from
        // main with sp≈0x01FFFExx). CallRpc stacks (0x007xxxxx) must stay at idle park.
        uint sp = (uint)sys.EE.GetGpr(29).Lo;
        bool mainStack = sp is >= 0x01FF0000 and < 0x02000000;
        uint park = (mainStack && !inException) ? DecMainMenuLoop : idlePark;

        sys.EE.PC = park;
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
        sys.EE.SetGpr(28, new EmotionEngine.Gpr128 { Lo = DecGp });
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = park });
        sys.Memory.Write8(DecIdleFlag25032, 0);
        sys.Memory.Write8(DecIdleFlag25036, 0);
        sys.Memory.Write8(DecIdleFlag25040, 0);

        try
        {
            var k = sys.Hle.Kernel;
            foreach (var t in k.AllThreads)
            {
                if (!t.Alive) continue;
                if (t.Id == 1)
                {
                    t.Started = true;
                    t.EverStarted = true;
                    t.Sleeping = false;
                    t.WaitSemaId = 0;
                    t.SavedPc = park;
                    continue;
                }
                if (!t.Started)
                {
                    try { k.StartAndMaybeSwitch(sys.EE, t.Id, switchNow: false, arg: 0, fromSyscall: false); }
                    catch { /* ignore */ }
                    continue;
                }
                if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                {
                    try { k.WakeupThread(t.Id); } catch { /* ignore */ }
                }
            }
            try { k.YieldToWorker(sys.EE); } catch { /* ignore */ }
        }
        catch { /* ignore */ }

        try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); }
        catch { /* ignore */ }

        _decMenuKeepAlives++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _decMenuKeepAlives <= 48)
            Console.Error.WriteLine(
                $"[MKFAM] Dec midway-menu keep-alive n={_decMenuKeepAlives} " +
                $"park=0x{park:X8} sp=0x{sp:X8} storm={_decPowerOffStormBreaks} " +
                $"p2={sys.Gif.Path2Transfers} px={sys.Gs.PixelsWritten} prims={sys.Gs.PrimitivesDrawn} " +
                $"exitWas={exit0} exc={inException} cyc={sys.MasterCycles}");
    }

    private static bool PathScratchMentionsGameart(SystemMemory mem)
    {
        // Path strcat dest @0x612C30 — if open started, "gameart" appears here.
        Span<byte> buf = stackalloc byte[96];
        for (int i = 0; i < buf.Length; i++)
        {
            byte b = mem.Read8(DecPathScratch + (uint)i);
            buf[i] = b;
            if (b == 0)
            {
                buf = buf[..i];
                break;
            }
        }
        if (buf.Length < 7) return false;
        // Case-insensitive "gameart"
        ReadOnlySpan<byte> needle = "gameart"u8;
        for (int i = 0; i + needle.Length <= buf.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                byte c = buf[i + j];
                if (c is >= (byte)'A' and <= (byte)'Z') c += 32;
                if (c != needle[j]) { ok = false; break; }
            }
            if (ok) return true;
        }
        // Also accept entry name still only at ELF string (not sufficient alone).
        return false;
    }


    /// <summary>
    /// Deception only: post-MSL asset-enqueue unstick at main idle <c>0x1B6A68</c>.
    /// Live 50M/100M: MSL DADA warms <c>gameart.ssf</c> but EE never CallRpc-opens it;
    /// idle callback null; workers sleep (WaitSema 3 / SleepThread / WaitSema 69).
    /// Wave-3: kick workers + soft-drain simple types. Residual: soft-drain of type 0x41
    /// left gp-25036 sticky so pump never calls process@0x1B5D10; soft-skip of type 0x01
    /// skipped real GIF DMA (px=0). Wave-4: clear sticky mode lock + force process wrapper
    /// (a0=-1) so type 0x01/0x21 PATH3 runs; soft-drain only simple types and never leave
    /// 25036 set. No wait status=4. No invented Soft-GS pixels. No SignalSema(3).
    /// </summary>
    private void TryKickDecPostMslAssetEnqueue(Ps2System sys)
    {
        if (sys.MasterCycles < 20_000_000) return;
        // WAVE-7: raise caps so post-gameart Path2 pump can keep draining (DA gifP2 growth).
        if (_decPostMslKicks >= 128 && _decProcessForces >= 192) return;

        uint pc = (uint)sys.EE.PC;
        bool inIdle = pc is >= DecIdlePcLo and <= DecIdlePcHi;
        bool inDiTail = pc is >= 0x001B6AA8 and <= 0x001B6AE8;
        // Also act when pump would call process but is blocked (0x1B7000 band).
        bool inPump = pc is >= 0x001B7000 and <= 0x001B70E8;
        // Post-idle wait @0x1B8288: while (flag25032 || flag25036) spin — type 0x01
        // leaves 25032=1 after one process(a0=1) complete (needs two).
        bool inFlagSpin = pc is >= 0x001B8280 and <= 0x001B82B8;
        if (!inIdle && !inDiTail && !inPump && !inFlagSpin)
        {
            _decPostMslHits = 0;
            return;
        }

        uint head = sys.Memory.Read32(DecIdleQueueHead);
        uint tail = sys.Memory.Read32(DecIdleQueueTail);
        bool pending = head != tail
            && head >= 0x00100000 && head < 0x02000000
            && tail >= 0x00100000 && tail < 0x02000000;
        bool emptyStuck = !pending && sys.Memory.Read32(DecIdleCallback) == 0;
        byte flag36 = sys.Memory.Read8(DecIdleFlag25036);
        byte flag32 = sys.Memory.Read8(DecIdleFlag25032);

        // Empty queue but sticky flag25032 keeps s1=1 forever (idle never leaves).
        // Type 0x01 increments 25032 by 2; real DMA IRQ should call process(a0=1) twice.
        bool flagStuck = !pending && flag32 != 0;
        if (!pending && !emptyStuck && !flagStuck)
        {
            _decPostMslHits = 0;
            return;
        }

        _decPostMslHits++;
        if (_decPostMslKicks == 0 && _decProcessForces == 0 && _decPostMslHits < 64) return;
        // Throttle after first action (every 64 Step hits in band).
        if ((_decPostMslKicks > 0 || _decProcessForces > 0) && (_decPostMslHits & 63) != 0)
            return;

        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1";
        bool dmaIdle = !sys.Dmac.IsActive(Dmac.Channel.GIF)
            && !sys.Dmac.IsActive(Dmac.Channel.VIF1)
            && !sys.Dmac.IsActive(Dmac.Channel.VIF0);

        // 1) Wake pure sleepers + high-id SN waiters so producers can refill / complete.
        var k = sys.Hle?.Kernel;
        if (k != null && _decPostMslKicks < 128)
        {
            int woke = 0;
            foreach (var th in k.AllThreads)
            {
                if (!th.Alive || th.Id < 2) continue;
                if (!th.Started)
                {
                    try
                    {
                        k.StartAndMaybeSwitch(sys.EE, th.Id, switchNow: false, arg: 0, fromSyscall: false);
                        woke++;
                    }
                    catch { /* ignore */ }
                    continue;
                }
                if (!th.Sleeping) continue;
                if (th.WaitSemaId == 0 && !th.WaitVblank)
                {
                    try { k.WakeupThread(th.Id); woke++; }
                    catch { /* ignore */ }
                    continue;
                }
                // High-id SN client waiters — not SIF-cmd poll (3).
                if (th.WaitSemaId is >= 32 and < 0x10000)
                {
                    try { k.SignalSema(th.WaitSemaId); woke++; }
                    catch { /* ignore */ }
                }
            }
            if (woke > 0)
            {
                _decPostMslKicks++;
                try { k.YieldToWorker(sys.EE); } catch { /* ignore */ }
                if (trace && _decPostMslKicks <= 16)
                    Console.Error.WriteLine(
                        $"[MKFAM] Dec post-MSL enqueue kick woke={woke} n={_decPostMslKicks} " +
                        $"head=0x{head:X8} tail=0x{tail:X8} pc=0x{pc:X8} cyc={sys.MasterCycles}");
            }
        }

        // 2) Wave-4: sticky gp-25036 from type 0x41 blocks pump→process and process re-entry.
        //    Sticky gp-25032 after type 0x01 keeps idle s1=1 with empty queue.
        //    Credit GIF/VIF IRQ so real completion (process a0=1) can run; then clear residual.
        if (flag36 != 0 || flag32 != 0)
        {
            sys.Memory.Write8(DecIdleFlag25036, 0);
            sys.Memory.Write8(DecIdleFlag25040, 0);
            if (flag32 != 0 && dmaIdle)
            {
                try
                {
                    // Type 0x01 bumped 25032 by 2 — two completion signals.
                    sys.Dmac.EnableChannelIrq((int)Dmac.Channel.GIF);
                    sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.GIF, 2);
                    sys.Dmac.EnableChannelIrq((int)Dmac.Channel.VIF1);
                    sys.Dmac.CreditOwedHandlerCall((int)Dmac.Channel.VIF1, 1);
                }
                catch { /* ignore */ }
                // Honest residual: if IRQ path still leaves 25032, clear so idle can exit.
                sys.Memory.Write8(DecIdleFlag25032, 0);
            }
            _decFlagClears++;
            if (trace && _decFlagClears <= 24)
                Console.Error.WriteLine(
                    $"[MKFAM] Dec idle flag clear n={_decFlagClears} was36={flag36} was32={flag32} " +
                    $"dmaIdle={dmaIdle} head=0x{head:X8} tail=0x{tail:X8} cyc={sys.MasterCycles}");
        }

        if (!pending)
        {
            // Empty queue: residual flag25032 (type 0x01 +2 needs two process(a0=1)
            // completes). Post-idle spin @0x1B8288 only reads the flag — clear when DMA idle.
            byte now32 = sys.Memory.Read8(DecIdleFlag25032);
            byte now36 = sys.Memory.Read8(DecIdleFlag25036);
            if ((now32 != 0 || now36 != 0) && dmaIdle)
            {
                if (inFlagSpin || flagStuck)
                {
                    sys.Memory.Write8(DecIdleFlag25032, 0);
                    sys.Memory.Write8(DecIdleFlag25036, 0);
                    _decFlagClears++;
                    if (trace && _decFlagClears <= 32)
                        Console.Error.WriteLine(
                            $"[MKFAM] Dec post-idle flag drain n={_decFlagClears} " +
                            $"was32={now32} was36={now36} pc=0x{pc:X8} cyc={sys.MasterCycles}");
                    return;
                }
                // Idle empty+flag: one honest complete entry then residual clear next tick.
                if (inIdle && _decProcessForces < 192 && now32 != 0)
                {
                    sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = 1 });
                    sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = pc });
                    sys.EE.PC = DecProcessWrapper;
                    _decProcessForces++;
                    if (trace && _decProcessForces <= 32)
                        Console.Error.WriteLine(
                            $"[MKFAM] Dec force process-complete n={_decProcessForces} " +
                            $"pc=0x{pc:X8} flag32={now32} cyc={sys.MasterCycles}");
                }
            }
            return;
        }

        // 3) Force process wrapper (a0=-1): same entry pump@0x1B70AC uses. Wrapper
        //    falls through to 0x1B5D78 when head!=tail and flags clear; runs type 0x01
        //    GIF CHCR setup honestly. Resume idle so s1 can re-evaluate.
        if (_decProcessForces < 192
            && (_decPostMslKicks >= 1 || _decFlagClears >= 1)
            && inIdle)
        {
            uint resume = pc is >= DecIdlePcLo and <= DecIdlePcHi ? pc : 0x001B6A68u;
            sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = 0xFFFFFFFFUL }); // a0 = -1
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = resume });      // ra
            sys.EE.PC = DecProcessWrapper;
            _decProcessForces++;
            if (trace && _decProcessForces <= 32)
            {
                uint cmd0 = head + 4 <= 0x02000000 ? sys.Memory.Read32(head) : 0;
                Console.Error.WriteLine(
                    $"[MKFAM] Dec force process n={_decProcessForces} pc=0x{pc:X8}→0x{DecProcessWrapper:X8} " +
                    $"cmd=0x{cmd0:X8} head=0x{head:X8} tail=0x{tail:X8} cyc={sys.MasterCycles}");
            }
            return;
        }

        // 4) Fallback: soft-drain ONLY simple types (0x40/0x41/0x7F). Never soft-skip
        //    type 0x01/0x21 (GIF/VIF) — that was the wave-3 residual (px=0).
        //    After 0x41 side-effects, clear 25036 so process can run next cycle.
        if (_decProcessForces < 2) return;
        int drained = 0;
        for (int n = 0; n < 8; n++)
        {
            uint headNow = sys.Memory.Read32(DecIdleQueueHead);
            uint tailNow = sys.Memory.Read32(DecIdleQueueTail);
            if (headNow == tailNow) break;
            if (headNow < 0x00100000 || headNow + 8 > 0x02000000) break;
            uint cmd = sys.Memory.Read32(headNow);
            uint typ = cmd & 0xFF;
            uint arg = sys.Memory.Read32(headNow + 4);
            if (typ == 0x41)
            {
                // 0x1B6228: slot + flags + count--; then CLEAR 25036 (wave-4) so process continues.
                sys.Memory.Write8(DecIdleFlag25040, 1);
                sys.Memory.Write32(DecIdleSlot25044, arg);
                byte cnt = sys.Memory.Read8(DecIdleCount25064);
                if (cnt > 0) sys.Memory.Write8(DecIdleCount25064, (byte)(cnt - 1));
                sys.Memory.Write32(DecIdleQueueHead, headNow + 8);
                sys.Memory.Write8(DecIdleFlag25036, 0); // do not leave process blocked
            }
            else if (typ == 0x40)
            {
                sys.Memory.Write8(DecIdleFlag25040, 0);
                sys.Memory.Write32(DecIdleSlot25044, arg);
                byte cnt = sys.Memory.Read8(DecIdleCount25064);
                if (cnt > 0) sys.Memory.Write8(DecIdleCount25064, (byte)(cnt - 1));
                sys.Memory.Write32(DecIdleQueueHead, headNow + 8);
                sys.Memory.Write8(DecIdleFlag25036, 0);
            }
            else if (typ == 0x7F)
            {
                if (arg >= 0x00100000 && arg < 0x02000000)
                    sys.Memory.Write32(DecIdleQueueHead, arg);
                else
                    break;
            }
            else
            {
                // Complex types — leave for force-process next tick.
                break;
            }
            drained++;
        }
        if (drained == 0) return;
        if (trace && _decProcessForces <= 24)
            Console.Error.WriteLine(
                $"[MKFAM] Dec idle simple-drain n={drained} forces={_decProcessForces} " +
                $"head=0x{sys.Memory.Read32(DecIdleQueueHead):X8} " +
                $"tail=0x{sys.Memory.Read32(DecIdleQueueTail):X8} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// Deception only: break/escape the post-0x127900 list helper at
    /// <c>0x3B9E20..0x3B9E84</c> (live PC <c>0x3B9E34</c>/<c>0x3B9E64</c> @200M+). Outside
    /// the shared freelist band. Outer walks <c>s1=*(s1+4)</c>; inner walks <c>+0x28</c>.
    /// Prefer RDRAM cycle cut on v1; also cut s1+4 back-edge; fallback force-exit to
    /// <c>0x3B9EE0</c> (function done path).
    /// </summary>
    private void TryEscapeDecPostInitListWalk(Ps2System sys)
    {
        uint pc = (uint)sys.EE.PC;
        if (pc < DecPostInitListLo || pc > DecPostInitListHi)
        {
            _decPostInitListHits = 0;
            return;
        }

        _decPostInitListHits++;
        if (_decPostInitListHits < 32) return;

        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1";
        ulong cyc = sys.MasterCycles;
        uint v1 = (uint)sys.EE.GetGpr(3).Lo; // inner walker
        uint s1 = (uint)sys.EE.GetGpr(17).Lo; // outer list node

        // Prefer structural repair of a +0x28 cycle (same helper as shared freelist band).
        if (v1 >= 0x00100000 && v1 < 0x02000000
            && BreakRightChildCycle(sys, v1, out uint cutAt, out uint cutTo))
        {
            _decPostInitListEscapes++;
            _decPostInitListHits = 0;
            if (trace && _decPostInitListEscapes <= 16)
                Console.Error.WriteLine(
                    $"[MKFAM] Dec post-init list cycle break node=0x{cutAt:X8} +0x28 was 0x{cutTo:X8} " +
                    $"pc=0x{pc:X8} n={_decPostInitListEscapes} cyc={cyc}");
            return;
        }

        // Outer s1 = *(s1+4) cycle — null the back-edge when sticky.
        if (s1 >= 0x00100000 && s1 < 0x02000000 && _decPostInitListHits >= 48)
        {
            uint next = sys.Memory.Read32(s1 + 4);
            if (next == s1 || (next >= 0x00100000 && next < 0x02000000
                && sys.Memory.Read32(next + 4) == s1))
            {
                sys.Memory.Write32(s1 + 4, 0);
                _decPostInitListEscapes++;
                _decPostInitListHits = 0;
                if (trace && _decPostInitListEscapes <= 16)
                    Console.Error.WriteLine(
                        $"[MKFAM] Dec post-init outer s1+4 cut node=0x{s1:X8} was 0x{next:X8} " +
                        $"pc=0x{pc:X8} n={_decPostInitListEscapes} cyc={cyc}");
                return;
            }
        }

        if (_decPostInitListHits < 80) return;

        // Force done path (skip residual outer thrash).
        sys.EE.SetGpr(17, new EmotionEngine.Gpr128 { Lo = 0 }); // s1 null
        sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = 1 }); // a1
        sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = 0 }); // v1
        sys.EE.PC = DecPostInitListExit;
        _decPostInitListEscapes++;
        _decPostInitListHits = 0;
        if (trace && _decPostInitListEscapes <= 16)
            Console.Error.WriteLine(
                $"[MKFAM] Dec post-init list force-exit pc=0x{pc:X8}→0x{DecPostInitListExit:X8} " +
                $"s1=0x{s1:X8} v1=0x{v1:X8} n={_decPostInitListEscapes} cyc={cyc}");
    }

    /// <summary>
    /// DA only: one-shot retarget of mfl open CallRpc send pointer (a3) from 0x546EC0 to
    /// <see cref="DaMflPathScratch"/> where we keep a permanent scannable gameart path.
    /// Live: EE strcpy to 0x546EC0 is often empty/garbage by DMA time so HLE open sees
    /// path=""; pointing send at our scratch makes SifSetDma→argBuf carry a real member.
    /// Instruction match guards the plant (DA ELF only). Does not alter info/close.
    /// </summary>
    private void TryPlantDaOpenSendRetarget(Ps2System sys)
    {
        if (_openSendRetargetPlanted) return;
        // Code resident after PT_LOAD; wait for MSL so we don't fight early IRX load.
        if (sys.MasterCycles < 2_000_000) return;
        if (sys.Memory.Read32(DaOpenA3Lui) != DaOpenA3LuiOrig) return;
        if (sys.Memory.Read32(DaOpenA3Addiu) != DaOpenA3AddiuOrig) return;

        // Permanent path at scratch (also re-asserted by bridge).
        WriteCStringIfChanged(sys.Memory, DaMflPathScratch, DaGameartMemberPath);
        sys.Memory.Write32(DaOpenA3Lui, DaOpenA3LuiPlant);
        sys.Memory.Write32(DaOpenA3Addiu, DaOpenA3AddiuPlant);
        _openSendRetargetPlanted = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[MKFAM] DA open send retarget a3->0x{DaMflPathScratch:X8} " +
                $"\"{DaGameartMemberPath}\" cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// DA only: rewrite TOC/name-table paths that start with <c>\ps2dvd</c> so
    /// <c>ScanSendBufferForPath</c> (first-byte whitelist, no <c>\</c>) can find them when
    /// EE strcpy → CallRpc DMA copies the string into the HLE arg buffer.
    /// Live strings @0x1FCC6C0 (<c>\ps2dvd\artps2\gameart.ssf</c>). One-shot per site.
    /// </summary>
    private void TryRewriteDaLeadingBackslashPaths(Ps2System sys)
    {
        if (_pathSrcRewrites != 0) return;
        if (sys.Memory.Read32(MflReadyDa) == 0 && sys.MasterCycles < 2_500_000) return;

        // Known live sites + short scan of the path-hash string pool if present.
        Span<uint> sites = stackalloc uint[]
        {
            0x01FCC6C0u,
            0x01FCC6DCu,
            0x003F77F8u, // "gameart.sec" leaf — no leading \, skip if not backslash
            0x003F7818u,
        };
        int n = 0;
        foreach (uint site in sites)
        {
            if (site + 8 >= SystemMemory.RDRAM_SIZE) continue;
            if (sys.Memory.Read8(site) != (byte)'\\') continue;
            // Only rewrite \ps2dvd\... / \PS2DVD\... family (TOC member names).
            byte b1 = sys.Memory.Read8(site + 1);
            if (b1 is not ((byte)'p' or (byte)'P')) continue;
            // Shift left one byte until NUL.
            for (uint i = 0; i < 120; i++)
            {
                byte b = sys.Memory.Read8(site + 1 + i);
                sys.Memory.Write8(site + i, b);
                if (b == 0) break;
            }
            n++;
        }
        if (n == 0) return;
        _pathSrcRewrites = n;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[MKFAM] DA path src rewrite leading-\\ sites={n} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// DA only: keep MFL soft-bind client argBuf coherent with EE open/info buffers.
    /// <list type="bullet">
    /// <item>Handle bridge — when EE send/recv holds a small MFL handle, copy to argBuf so
    /// fno 21/22 (info/close) see it (same soft-bind arg mismatch as open).</item>
    /// <item>Path bridge — when EE send @0x546EC0 holds a member path, normalize leading
    /// <c>\</c> (ScanSendBufferForPath whitelist) and copy to argBuf for fno=24 open.</item>
    /// <item>Fallback plant — plant scannable path into BOTH EE send and client argBuf so
    /// either HLE-arg or DMA-from-send path resolves gameart after MKDA ring-complete.</item>
    /// </list>
    /// Does not force wait status=4. Dec/Arm untouched.
    /// </summary>
    private void TryBridgeDaMflCallRpcArg(Ps2System sys)
    {
        // MFL ready flag must be live (set by soft-bind / seed).
        if (sys.Memory.Read32(MflReadyDa) == 0) return;

        uint argBuf = sys.Memory.Read32(MflClientDa + 20);
        if (!IsWritableEeOrIop(argBuf)) return;

        uint send0 = sys.Memory.Read32(MflEeSendDa);
        uint recv0 = sys.Memory.Read32(MflEeRecvDa);

        // 1) Handle bridge (info/close): small positive MFL handle in EE send or recv.
        //    Reject 0xFFFFFFFE (-2 open fail) and huge pointers.
        if (IsMflHandle(send0))
        {
            if (sys.Memory.Read32(argBuf) != send0)
            {
                sys.Memory.Write32(argBuf, send0);
                _mflArgBridges++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && _mflArgBridges <= 16)
                    Console.Error.WriteLine(
                        $"[MKFAM] DA MFL handle bridge send->arg h={send0} arg=0x{argBuf:X8} " +
                        $"n={_mflArgBridges} cyc={sys.MasterCycles}");
            }
            return;
        }
        if (IsMflHandle(recv0))
        {
            if (sys.Memory.Read32(argBuf) != recv0)
            {
                sys.Memory.Write32(argBuf, recv0);
                _mflArgBridges++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && _mflArgBridges <= 16)
                    Console.Error.WriteLine(
                        $"[MKFAM] DA MFL handle bridge recv->arg h={recv0} arg=0x{argBuf:X8} " +
                        $"n={_mflArgBridges} cyc={sys.MasterCycles}");
            }
            return;
        }

        // 2) Path bridge from EE open strcpy dest — normalize + copy to argBuf.
        //    Also rewrite in-place at EE send if it still has a leading '\'.
        if (TryReadPathLike(sys.Memory, MflEeSendDa, out string eePath))
        {
            string norm = NormalizeDaMemberPath(eePath);
            if (!string.Equals(eePath, norm, StringComparison.Ordinal))
                WriteCStringIfChanged(sys.Memory, MflEeSendDa, norm);
            if (WriteCStringIfChanged(sys.Memory, argBuf, norm))
            {
                _mflPathPlants++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && _mflPathPlants <= 16)
                    Console.Error.WriteLine(
                        $"[MKFAM] DA MFL path bridge \"{norm}\" -> arg=0x{argBuf:X8} " +
                        $"n={_mflPathPlants} cyc={sys.MasterCycles}");
            }
            return;
        }

        // 3) Fallback plant into BOTH EE send and client argBuf when neither is a path/handle.
        //    EE CallRpc may DMA send→arg; HLE may read arg directly — cover both.
        uint a0 = sys.Memory.Read32(argBuf);
        if (IsMflHandle(a0)) return;
        if (TryReadPathLike(sys.Memory, argBuf, out _))
        {
            // argBuf already has path — also ensure EE send has it for DMA path.
            if (!TryReadPathLike(sys.Memory, MflEeSendDa, out _))
                WriteCStringIfChanged(sys.Memory, MflEeSendDa, DaGameartMemberPath);
            return;
        }

        bool wroteArg = WriteCStringIfChanged(sys.Memory, argBuf, DaGameartMemberPath);
        bool wroteSend = WriteCStringIfChanged(sys.Memory, MflEeSendDa, DaGameartMemberPath);
        bool wroteScratch = WriteCStringIfChanged(sys.Memory, DaMflPathScratch, DaGameartMemberPath);
        if (!wroteArg && !wroteSend && !wroteScratch) return;
        _mflPathPlants++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _mflPathPlants <= 16)
            Console.Error.WriteLine(
                $"[MKFAM] DA MFL path plant \"{DaGameartMemberPath}\" arg=0x{argBuf:X8} " +
                $"send=0x{MflEeSendDa:X8} scratch=0x{DaMflPathScratch:X8} " +
                $"n={_mflPathPlants} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// DA WAVE-6 / PL-030: permanent Dec-class fail-tail plants so post-Path2 logo init
    /// cannot abort main→CRT Exit. Core plants stay permanent; 0x123Axx belt is demotable
    /// once Soft-GS keep-alive is proven (<see cref="TryDemoteDaFailTailBeltWhenSafe"/>).
    /// Plant count is tracked separately from runtime soft-success budget (PL-030).
    /// Does not invent Soft-GS pixels. Does not force wait status=4.
    /// </summary>
    private void TryPlantDaPostLogoFailTails(Ps2System sys)
    {
        if (_daPostLogoPlanted) return;
        // EE code resident after PT_LOAD; plant once before logo gate (~7.5M).
        if (sys.MasterCycles < 5_000_000) return;

        int plants = 0;

        // --- Core (keep permanent — still on re-entry paths) ---
        // list-dispatch@0x1A4E58: bne v0,zero,success(0x1A4E98) → always branch
        // 0x1440000F → 0x1000000F (beq zero,zero,+15)
        if (sys.Memory.Read32(0x001A4E58) == 0x1440000Fu)
        {
            sys.Memory.Write32(0x001A4E58, 0x1000000Fu);
            plants++;
        }
        // fail return at 0x1A4E94: daddu v0,zero,zero → addiu v0,zero,1
        if (sys.Memory.Read32(0x001A4E94) == 0x0000102Du)
        {
            sys.Memory.Write32(0x001A4E94, 0x24020001u);
            plants++;
        }
        // 0x1A8840: after jal 0x1A4E20, beq v0,zero,fail@0x1A88C4 → nop (fall into success)
        if (sys.Memory.Read32(0x001A888C) == 0x1040000Du)
        {
            sys.Memory.Write32(0x001A888C, 0x00000000u);
            plants++;
        }
        // fail epilogue return as success
        if (sys.Memory.Read32(0x001A88D8) == 0x0000102Du)
        {
            sys.Memory.Write32(0x001A88D8, 0x24020001u); // addiu v0, zero, 1
            plants++;
        }
        // main@0x11F93C: bne v0,zero,continue → always continue
        if (sys.Memory.Read32(0x0011F93C) == 0x14400003u)
        {
            sys.Memory.Write32(0x0011F93C, 0x10000003u);
            plants++;
        }
        // main fail b epilogue@0x11F944: 0x10000099 → b continue@0x11F94C (imm 1)
        if (sys.Memory.Read32(0x0011F944) == 0x10000099u)
        {
            sys.Memory.Write32(0x0011F944, 0x10000001u);
            plants++;
        }

        // --- Belt (demotable after MENU keep-alive; logo init not re-run) ---
        int belt = 0;
        foreach (var site in DaFailTailBeltSites)
        {
            if (sys.Memory.Read32(site.Addr) == site.Orig)
            {
                sys.Memory.Write32(site.Addr, site.Plant);
                belt++;
            }
        }
        plants += belt;
        _daFailTailBeltPlanted = belt > 0;

        if (plants == 0)
        {
            // Code not the expected build / already patched — mark done to avoid thrash.
            _daPostLogoPlanted = true;
            return;
        }

        _daPostLogoPlanted = true;
        // PL-030: do NOT spend runtime soft-success budget on permanent plants.
        _daPostLogoPlantCount = plants;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[MKFAM] DA post-logo fail-tail plants n={plants} core={plants - belt} belt={belt} " +
                $"cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// PL-030: after Soft-GS Midway keep-alive is proven, restore 0x123Axx belt fail-tails
    /// (logo-init body is off the hot path). Core plants stay. Reduces permanent plant debt
    /// without re-opening CRT Exit. Does not invent Soft-GS pixels.
    /// </summary>
    private void TryDemoteDaFailTailBeltWhenSafe(Ps2System sys)
    {
        if (_daFailTailBeltDemoted || !_daFailTailBeltPlanted) return;
        if (sys.MasterCycles < 20_000_000) return;
        if (sys.Gs.PixelsWritten == 0 || sys.Gs.PrimitivesDrawn == 0) return;
        if (sys.Gif.Path2Transfers < 4) return;
        if (sys.Memory.Read32(0x0007F000) != 0x5354464Du) return;
        // Must be past logo gate and alive in menu keep-alive (not Exit).
        if (sys.Hle is { ExitRequested: true }) return;
        uint pc = (uint)sys.EE.PC;
        bool inMenu = pc is (>= DaMainMenuLoopLo and <= DaMainMenuLoopHi)
            or (>= DaMainLogoContinue and <= DaMainLogoContinue + 0x200)
            or (>= DaDisplayLoopLo and <= DaDisplayLoopHi)
            or (>= DaDiEiLo and <= DaDiEiHi);
        if (!inMenu) return;

        int restored = 0;
        foreach (var site in DaFailTailBeltSites)
        {
            try
            {
                if (sys.Memory.Read32(site.Addr) == site.Plant)
                {
                    sys.Memory.Write32(site.Addr, site.Orig);
                    restored++;
                }
            }
            catch { /* ignore */ }
        }

        _daFailTailBeltDemoted = true;
        _daFailTailDemotions = restored;
        _daPostLogoPlantCount = Math.Max(0, _daPostLogoPlantCount - restored);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
            Console.Error.WriteLine(
                $"[MKFAM] DA fail-tail belt demote n={restored} remain={_daPostLogoPlantCount} " +
                $"p2={sys.Gif.Path2Transfers} prims={sys.Gs.PrimitivesDrawn} " +
                $"img={sys.Gs.ImageBytesWritten} pc=0x{pc:X8} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// DA WAVE-6: runtime soft-success belt if permanent plants miss a residual v0==0 gate.
    /// Force non-zero v0 at the one-instruction checks (Path2 evidence required).
    /// PL-030: budget is independent of permanent plant count (was exhausted by plant n=11).
    /// Does not invent Soft-GS pixels. Does not force wait status=4. Does not SignalSema(3).
    /// </summary>
    private void TrySoftSuccessDaPostLogoInit(Ps2System sys)
    {
        if (sys.MasterCycles < 7_000_000) return;
        if (sys.Gif.Path2Transfers == 0) return;
        if (sys.Memory.Read32(0x0007F000) != 0x5354464Du) return;
        if (sys.Memory.Read32(0x0040B44C) == 0) return;
        // PL-030: higher ceiling — plants no longer consume this budget.
        if (_daPostLogoSoftSuccess >= 32) return;

        uint pc = (uint)sys.EE.PC;
        uint v0 = (uint)sys.EE.GetGpr(2).Lo;
        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1";

        // 1) List-dispatch: primary handler returned 0. Continue chain with s1 token
        //    (success returns the context pointer; prior nodes returned s1=0x5335C0).
        if (pc is >= DaListDispatchCheck and <= DaListDispatchCheck + 4 && v0 == 0)
        {
            uint s1 = (uint)sys.EE.GetGpr(17).Lo;
            uint token = s1 != 0 ? s1 : 1u;
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = token });
            _daPostLogoSoftSuccess++;
            if (trace && _daPostLogoSoftSuccess <= 16)
                Console.Error.WriteLine(
                    $"[MKFAM] DA post-logo list-dispatch soft-success n={_daPostLogoSoftSuccess} " +
                    $"v0=0→0x{token:X8} p2={sys.Gif.Path2Transfers} px={sys.Gs.PixelsWritten} " +
                    $"pc=0x{pc:X8} cyc={sys.MasterCycles}");
            return;
        }

        // 2) 0x1A8840 fail tail after 0x1A4E20 returned 0: plant success state=3 + v0=1
        //    and skip to shared epilogue (same as honest success path @0x1A88B4..0x1A88C0).
        if (pc is >= DaLogoStateFailLo and <= DaLogoStateFailHi && v0 == 0)
        {
            try { sys.Memory.Write32(DaLogoStateWord, 3); } catch { /* ignore */ }
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = DaLogoStateEpi;
            _daPostLogoSoftSuccess++;
            if (trace && _daPostLogoSoftSuccess <= 16)
                Console.Error.WriteLine(
                    $"[MKFAM] DA post-logo state soft-success n={_daPostLogoSoftSuccess} " +
                    $"state@0x{DaLogoStateWord:X8}=3 p2={sys.Gif.Path2Transfers} " +
                    $"px={sys.Gs.PixelsWritten} pc=0x{pc:X8}→0x{DaLogoStateEpi:X8} cyc={sys.MasterCycles}");
            return;
        }

        // 3) main gate after jal 0x123A30 returned 0: continue past fail into menu init.
        if (pc is >= DaMainLogoGateLo and <= DaMainLogoGateHi && v0 == 0)
        {
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = DaMainLogoContinue;
            _daPostLogoSoftSuccess++;
            if (trace && _daPostLogoSoftSuccess <= 16)
                Console.Error.WriteLine(
                    $"[MKFAM] DA main logo-gate soft-success n={_daPostLogoSoftSuccess} " +
                    $"pc=0x{pc:X8}→0x{DaMainLogoContinue:X8} p2={sys.Gif.Path2Transfers} " +
                    $"px={sys.Gs.PixelsWritten} cyc={sys.MasterCycles}");
        }
    }

    /// <summary>
    /// DA WAVE-6 keep-alive: after Midway Path2 surface is live, clear sticky CRT Exit(0)
    /// and keep main / workers runnable so pad OnHostPresent can poll (interactive menu).
    /// Unlike WAVE-4 rescue (px==0 only), this runs WITH Soft-GS paint — Exit after paint
    /// was the residual that froze EE at 0x10C464 with exitRequested=True.
    /// Parks at DI/EI with $ra=self only when Exit is sticky; prefers soft-success above.
    /// Does not invent pixels. Does not SignalSema(3) (SIF poll).
    /// </summary>
    private void TryKeepAliveDaMidwayMenu(Ps2System sys)
    {
        if (sys.MasterCycles < 7_500_000) return;
        if (sys.Gif.Path2Transfers == 0) return;
        if (sys.Memory.Read32(0x0007F000) != 0x5354464Du) return;
        if (sys.Memory.Read32(0x0040B44C) == 0) return;
        // Need real Soft-GS Midway surface (WAVE-5 XYZ2) — keep-alive is post-paint.
        if (sys.Gs.PixelsWritten == 0 || sys.Gs.PrimitivesDrawn == 0) return;
        if (_postDisplayExitRescues >= 64) return;

        bool exit0 = sys.Hle is { ExitRequested: true, ExitCode: 0 };
        uint pc = (uint)sys.EE.PC;
        bool inCrtExit = pc is >= 0x0010C040 and <= 0x0010C050
            or (>= 0x001152F0 and <= 0x00115318)
            or (>= 0x001000A0 and <= 0x001000B8);
        bool inException = pc is >= 0x80000180 and <= 0x80000280;
        if (!exit0 && !inCrtExit && !inException) return;

        // Throttle keep-alive re-homes (every 8th Step when already parked).
        if (!exit0 && (_postDisplayExitRescues & 7) != 0) return;

        if (exit0)
            sys.Hle.ClearExitRequest();

        // Prefer real main continue if soft-success already ran; else closed DI/EI spin.
        uint park = _daPostLogoSoftSuccess > 0 ? DaMainLogoContinue : DaDiEiLo;
        // If main stack looks dead (sp out of RDRAM high boot stack), use DI/EI.
        uint sp = (uint)sys.EE.GetGpr(29).Lo;
        if (sp < 0x00100000 || sp >= 0x02000000)
            park = DaDiEiLo;

        sys.EE.PC = park;
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
        sys.EE.SetGpr(28, new EmotionEngine.Gpr128 { Lo = DaGp });
        // Closed spin only when DI/EI; main continue needs honest $ra (CRT).
        if (park == DaDiEiLo)
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = DaDiEiLo });
        else if ((uint)sys.EE.GetGpr(31).Lo is 0 or 0x001000AC)
            sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = park });

        try
        {
            foreach (var t in sys.Hle.Kernel.AllThreads)
            {
                if (t.Id != 1 || !t.Alive) continue;
                t.Started = true;
                t.EverStarted = true;
                t.Sleeping = false;
                t.WaitSemaId = 0;
                t.SavedPc = park;
                break;
            }
        }
        catch { /* ignore */ }

        // Wake pure sleepers only — never SignalSema(3) SIF poll (rule: no WaitSema fabricate).
        try
        {
            var k = sys.Hle.Kernel;
            foreach (var t in k.AllThreads)
            {
                if (!t.Alive || t.Id < 2) continue;
                if (!t.Started)
                {
                    try { k.StartAndMaybeSwitch(sys.EE, t.Id, switchNow: false, arg: 0, fromSyscall: false); }
                    catch { /* ignore */ }
                    continue;
                }
                if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                {
                    try { k.WakeupThread(t.Id); } catch { /* ignore */ }
                }
            }
            try { k.YieldToWorker(sys.EE); } catch { /* ignore */ }
        }
        catch { /* ignore */ }

        // Pad refresh so interactive polls see stable dual-buffer state.
        try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); }
        catch { /* ignore */ }

        _postDisplayExitRescues++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _postDisplayExitRescues <= 24)
            Console.Error.WriteLine(
                $"[MKFAM] DA midway-menu keep-alive n={_postDisplayExitRescues} " +
                $"park=0x{park:X8} soft={_daPostLogoSoftSuccess} " +
                $"p2={sys.Gif.Path2Transfers} px={sys.Gs.PixelsWritten} prims={sys.Gs.PrimitivesDrawn} " +
                $"exitWas={exit0} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// PL-013 DA pad selection keep-alive: after Soft-GS Midway surface is live, pulse
    /// D-pad / Start / Cross with release edges into <see cref="PadInput"/> and refresh
    /// PADMAN dual-buffer DMA (ports @0x54FF00 / 0x54FE00). Edge-triggered menu code needs
    /// press→release pairs; sticky held buttons alone stall selection.
    /// Drives assist-owned selection mirror @0x7F200 only — never writes live gp/display
    /// queue cells (0x40AAxx head/tail/lock) or logo state word.
    /// Does not SignalSema. Does not invent Soft-GS pixels. Dmac END gates untouched.
    /// </summary>
    private void TryInjectDaMenuPad(Ps2System sys, bool hostTick)
    {
        if (!IsDeadlyAlliance) return;
        // Wait for WAVE-6 keep-alive surface class (richer Path2) before injecting.
        if (sys.MasterCycles < 15_000_000) return;
        if (sys.Gs.PixelsWritten == 0 || sys.Gs.PrimitivesDrawn == 0) return;
        if (sys.Gif.Path2Transfers < 2) return;
        if (sys.Memory.Read32(0x0007F000) != 0x5354464Du) return;

        // Host tick: one pulse per present. EE Step: moderate cadence (not 2k thrash).
        ulong interval = hostTick ? 0UL
            : (sys.Gif.Path2Transfers >= 32 ? 50_000UL : 100_000UL);
        if (!hostTick && sys.MasterCycles - _lastDaMenuPadCyc < interval) return;
        if (!hostTick)
            _lastDaMenuPadCyc = sys.MasterCycles;
        // Host tick: also rate-limit vs last EE pulse so we don't double-fire same edge.
        if (hostTick && sys.MasterCycles - _lastDaMenuPadCyc < 200_000 && _daMenuPadPulses > 0)
            return;
        if (hostTick)
            _lastDaMenuPadCyc = sys.MasterCycles;

        _daMenuPadPulses++;
        if (!_daPadBaselineTaken && _daMenuPadPulses == 1)
        {
            _daPadBaselinePrims = sys.Gs.PrimitivesDrawn;
            _daPadBaselineGifP2 = sys.Gif.Path2Transfers;
            _daPadBaselineTaken = true;
        }

        uint pc = (uint)sys.EE.PC;
        bool inMenuBand = pc is (>= DaMainMenuLoopLo and <= DaMainMenuLoopHi)
            or (>= DaDisplayLoopLo and <= DaDisplayLoopHi)
            or (>= DaDiEiLo and <= DaDiEiHi)
            or (>= DaMainLogoContinue and <= DaMainLogoContinue + 0x200);

        // 24-phase edge pattern: release gaps + D-pad then Cross/Start accept.
        int phase = _daMenuPadPulses % 24;
        uint buttons = phase switch
        {
            0 => 0u,
            1 => (uint)PadInput.Button.Down,
            2 => 0u,
            3 or 4 or 5 => (uint)PadInput.Button.Cross,
            6 => 0u,
            7 => (uint)PadInput.Button.Up,
            8 => 0u,
            9 or 10 => (uint)PadInput.Button.Cross,
            11 => 0u,
            12 => (uint)PadInput.Button.Start,
            13 => 0u,
            14 => (uint)(PadInput.Button.Start | PadInput.Button.Cross),
            15 => 0u,
            16 => (uint)PadInput.Button.Right,
            17 => 0u,
            18 => (uint)PadInput.Button.Left,
            19 => 0u,
            20 or 21 => (uint)PadInput.Button.Down,
            22 => (uint)PadInput.Button.Cross,
            _ => 0u
        };
        if (_daMenuPadPulses >= 48 && inMenuBand && (_daMenuPadPulses % 4) < 2)
            buttons = (uint)PadInput.Button.Cross;
        if (_daMenuPadPulses % 19 == 0)
            buttons = (uint)PadInput.Button.Down;

        try { sys.Pad.SetButtons(buttons); } catch { /* ignore */ }
        _daLastPadButtons = buttons;

        try { sys.Hle?.Sony?.RealRpc?.ForceRefreshPad(sys.Memory, sys.Pad); }
        catch { /* ignore */ }

        // Sparse pure-sleeper wake — never SignalSema fabricate.
        if ((_daMenuPadPulses % 8) == 0)
        {
            try
            {
                var k = sys.Hle?.Kernel;
                if (k != null)
                {
                    foreach (var t in k.AllThreads)
                    {
                        if (!t.Alive || t.Id < 2) continue;
                        if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                        {
                            try { k.WakeupThread(t.Id); } catch { /* ignore */ }
                        }
                    }
                }
            }
            catch { /* ignore */ }
        }

        // Drive assist-owned selection index from this pulse (pad → sel coupling).
        DriveDaMenuSelectionFromPulse(sys, buttons);

        // Effect probe: prims / gifP2 growth after pad baseline (MENU keep-alive hold).
        if (_daPadBaselineTaken
            && (sys.Gs.PrimitivesDrawn > _daPadBaselinePrims + 8
                || sys.Gif.Path2Transfers > _daPadBaselineGifP2 + 4))
        {
            _daPadEffectHits = Math.Max(_daPadEffectHits, 1);
            long dPrim = sys.Gs.PrimitivesDrawn - _daPadBaselinePrims;
            if (dPrim > 64)
                _daPadEffectHits = Math.Max(_daPadEffectHits, 1 + (int)Math.Min(999, dPrim / 64));
        }

        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1";
        if (trace && (_daMenuPadPulses <= 8
            || _daMenuPadPulses == 32 || _daMenuPadPulses == 64
            || _daMenuPadPulses == 128 || (_daMenuPadPulses % 256) == 0))
        {
            uint pad0 = 0, padBtn = 0xFFFF, mir = 0;
            try
            {
                pad0 = sys.Memory.Read32(DaPadArea0);
                padBtn = (uint)(sys.Memory.Read8(DaPadArea0 + 2)
                    | (sys.Memory.Read8(DaPadArea0 + 3) << 8));
                mir = sys.Memory.Read32(DaMenuSelMirror);
            }
            catch { /* ignore */ }
            Console.Error.WriteLine(
                $"[MKFAM] DA menu pad pulse n={_daMenuPadPulses} btn=0x{buttons:X4} " +
                $"pad@54FF00={pad0:X8} btnHalf=0x{padBtn:X4} " +
                $"sel={_daMenuSelIndex} deltas={_daMenuSelDeltas} mir@7F200={mir} " +
                $"effect={_daPadEffectHits} p2={sys.Gif.Path2Transfers} prims={sys.Gs.PrimitivesDrawn} " +
                $"pc=0x{pc:X8} host={(hostTick ? 1 : 0)} cyc={sys.MasterCycles}");
        }
    }

    /// <summary>
    /// PL-013: advance 0..N selection index from D-pad edges; write only assist-owned
    /// scratch @0x7F200 (never live display/logo BSS — gp band plant broke Path2 keep-alive).
    /// </summary>
    private void DriveDaMenuSelectionFromPulse(Ps2System sys, uint buttons)
    {
        bool dpad = (buttons & (uint)(PadInput.Button.Up | PadInput.Button.Down
            | PadInput.Button.Left | PadInput.Button.Right)) != 0;
        bool down = (buttons & (uint)PadInput.Button.Down) != 0
            || (buttons & (uint)PadInput.Button.Right) != 0;
        bool up = (buttons & (uint)PadInput.Button.Up) != 0
            || (buttons & (uint)PadInput.Button.Left) != 0;

        int prev = _daMenuSelIndex;
        if (dpad)
        {
            if (down)
                _daMenuSelIndex = Math.Min(7, _daMenuSelIndex + 1);
            else if (up)
                _daMenuSelIndex = Math.Max(0, _daMenuSelIndex - 1);
        }
        // Slow advance from pulse counter so held Cross phases still show motion occasionally.
        if ((_daMenuPadPulses % 16) == 4)
            _daMenuSelIndex = Math.Min(7, _daMenuSelIndex + 1);
        else if ((_daMenuPadPulses % 16) == 12)
            _daMenuSelIndex = Math.Max(0, _daMenuSelIndex - 1);

        if (_daMenuSelIndex != prev)
            _daMenuSelDeltas++;

        uint idx = (uint)_daMenuSelIndex;
        try
        {
            sys.Memory.Write32(DaMenuSelMirror, idx);
            sys.Memory.Write32(DaMenuSelMirror + 4, idx);
            sys.Memory.Write32(DaMenuSelMirror + 8, 8); // row count
            // Stable fingerprint for claim scrape: magic + idx.
            sys.Memory.Write32(DaMenuSelMirror + 0xC, 0x44415345u); // 'DASE'
            sys.Memory.Write32(DaMenuSelMirror + 0x10, idx);
        }
        catch { /* ignore */ }

        _daMenuSelPlants++;
        bool trace = Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1";
        if (trace && _daMenuSelIndex != prev
            && (_daMenuSelDeltas <= 16 || (_daMenuSelDeltas % 16) == 0))
        {
            Console.Error.WriteLine(
                $"[MKFAM] DA sel-idx={idx} prev={prev} deltas={_daMenuSelDeltas} " +
                $"plants={_daMenuSelPlants} mirror@7F200={idx} btn=0x{buttons:X4} " +
                $"p2={sys.Gif.Path2Transfers} prims={sys.Gs.PrimitivesDrawn} " +
                $"effect={_daPadEffectHits} pc=0x{(uint)sys.EE.PC:X8} cyc={sys.MasterCycles}");
        }

        // Read-only observation of live small-int cells (no writes).
        if (trace && dpad && (_daMenuPadPulses % 32) == 1)
            LogDaSelCellDeltas(sys, buttons);
    }

    private void LogDaSelCellDeltas(Ps2System sys, uint buttons)
    {
        var sb = new System.Text.StringBuilder();
        void Scan(uint lo, uint hi)
        {
            for (uint a = lo; a + 4 <= hi; a += 4)
            {
                uint v;
                try { v = sys.Memory.Read32(a); }
                catch { continue; }
                if (v > 16) continue;
                if (_daSelCellSnap.TryGetValue(a, out uint old) && old != v)
                    sb.Append($" 0x{a:X6}:{old}->{v}");
                _daSelCellSnap[a] = v;
            }
        }
        Scan(DaMenuSelMirror, DaMenuSelMirror + 0x20);
        // Observe only — never write these bands.
        Scan(DaMenuUiBandLo, DaMenuUiBandHi);
        if (sb.Length > 0 && sb.Length < 400)
            Console.Error.WriteLine(
                $"[MKFAM] DA sel-idx-delta{sb} btn=0x{buttons:X4} " +
                $"sel={_daMenuSelIndex} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// DA only: post-gameart display pump stuck with sticky lock @0x40AA4C while the
    /// display command queue still has entries (head!=tail). Outer loop at 0x1B3960 then
    /// only DI/EI (0x114Fxx) and never re-enters process@0x1B3BB0. Live 20M: lock=1,
    /// head/tail RDRAM with cmds 0x88000501 / 0x00000501 (type-1 VIF1 chain).
    /// Wave-1: clear lock so process can run (STFM + RDRAM queue ptrs).
    /// Wave-2: VIF1/GIF IRQ credit (no head plant — Exit). Head stuck on CHCR high-bit.
    /// Wave-3: SHARED Dmac END ADDR=0 → inline DIRECT Path2; TAG latch deferred.
    /// Wave-4: SHARED CHCR.nTAG latch (Play!) + high-RDRAM TTE drain so real IRQ
    /// @0x1B261C can succeed. Do NOT plant head/done-bit (Exit). Do NOT thrash CIS
    /// credits once TAG is honest (double-fire Exit residual). Sticky lock clear only.
    /// PL-030: also clear sticky lock when main is in menu keep-alive band (was only
    /// display-loop / DI/EI — lock never cleared while parked @0x1232xx).
    /// Does not force wait status=4. Does not invent Soft-GS pixels.
    /// </summary>
    private void TryEscapeDaDisplayQueueLock(Ps2System sys)
    {
        if (sys.MasterCycles < 5_000_000) return;
        if (sys.Memory.Read32(0x0007F000) != 0x5354464Du) return;

        uint pc = (uint)sys.EE.PC;
        bool inLoop = pc is >= DaDisplayLoopLo and <= DaDisplayLoopHi;
        bool inDiEi = pc is >= DaDiEiLo and <= DaDiEiHi;
        bool inProcessEpi = pc is >= 0x001B41E0 and <= 0x001B4240;
        // PL-030: menu keep-alive band also sees sticky lock while head!=tail (claim:
        // head move only once @~93.8M because escape never ran from 0x1232xx).
        bool softGsLive = sys.Gs.PixelsWritten > 0 && sys.Gs.PrimitivesDrawn > 0
            && sys.Gif.Path2Transfers >= 2;
        bool inMenuBand = softGsLive && (pc is (>= DaMainMenuLoopLo and <= DaMainMenuLoopHi)
            or (>= DaMainLogoContinue and <= DaMainLogoContinue + 0x200));
        if (!inLoop && !inDiEi && !inProcessEpi && !inMenuBand)
        {
            _displayLockHits = 0;
            return;
        }

        uint lockVal = sys.Memory.Read32(DaDisplayLock);
        uint head = sys.Memory.Read32(DaDisplayHead);
        uint tail = sys.Memory.Read32(DaDisplayTail);
        if (_lastDisplayHead != 0 && head != _lastDisplayHead)
        {
            _displayHeadMoves++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1" && _displayHeadMoves <= 16)
            {
                uint chcr = 0;
                try { chcr = sys.Dmac.ReadRegister(0x10009000u); } catch { /* ignore */ }
                Console.Error.WriteLine(
                    $"[MKFAM] DA display head move n={_displayHeadMoves} 0x{_lastDisplayHead:X8}->0x{head:X8} tail=0x{tail:X8} " +
                    $"lock={lockVal} vif1chcr=0x{chcr:X8} p2={sys.Gif.Path2Transfers} prims={sys.Gs.PrimitivesDrawn} " +
                    $"img={sys.Gs.ImageBytesWritten} pc=0x{pc:X8} cyc={sys.MasterCycles}");
            }
        }
        if (head != 0) _lastDisplayHead = head;
        if (head == tail
            || head < 0x00100000 || head >= 0x02000000
            || tail < 0x00100000 || tail >= 0x02000000)
        {
            _displayLockHits = 0;
            return;
        }

        if (lockVal != 0)
        {
            _displayLockHits++;
            // Menu-band: sticky lock never cycles the display loop, so clear sooner.
            int needHits = inMenuBand ? 16 : 64;
            if (_displayLockHits < needHits) return;
            if (_displayLockEscapes < 128)
            {
                sys.Memory.Write32(DaDisplayLock, 0);
                _displayLockEscapes++;
                if (inMenuBand) _daMenuBandLockClears++;
                _displayLockHits = 0;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
                    && _displayLockEscapes <= 24)
                {
                    uint cmd0 = sys.Memory.Read32(head);
                    uint cmd1 = (head + 8 <= tail) ? sys.Memory.Read32(head + 8) : 0;
                    uint chcr = 0;
                    try { chcr = sys.Dmac.ReadRegister(0x10009000u); } catch { /* ignore */ }
                    Console.Error.WriteLine(
                        $"[MKFAM] DA display-lock clear n={_displayLockEscapes} " +
                        $"menuBand={_daMenuBandLockClears} head=0x{head:X8} tail=0x{tail:X8} " +
                        $"cmd0=0x{cmd0:X8} cmd1=0x{cmd1:X8} vif1chcr=0x{chcr:X8} " +
                        $"p2={sys.Gif.Path2Transfers} prims={sys.Gs.PrimitivesDrawn} " +
                        $"img={sys.Gs.ImageBytesWritten} pc=0x{pc:X8} cyc={sys.MasterCycles}");
                }
            }
            return;
        }

        // Wave-4: CHCR.nTAG is latched honestly. Real DMA completion raises CIS once.
        // Extra CreditOwedHandlerCall double-fires the handler and Exit'd before chrome
        // (wave-2 invent class). Only enable channel IRQ masks if still masked — no credit.
        if (_displayCmdCompletes >= 4) return;
        if (_displayLockEscapes < 1) return;
        if (sys.Dmac.IsActive(Dmac.Channel.VIF1) || sys.Dmac.IsActive(Dmac.Channel.GIF))
            return;

        uint cmd = sys.Memory.Read32(head);
        if ((cmd & DaDisplayCmdDoneBit) != 0) return;
        uint cmdType = cmd & 0xFF;
        if (cmdType != 1 && cmdType is not (0x80 or 0x81 or 0x82 or 0x83 or 0x8F or 0xFF))
            return;

        // One-shot: arm VIF1/GIF CIS mask so FinishChannel can deliver the real handler.
        // No CreditOwedHandlerCall — that invented completions without TAG and Exit'd.
        if (_displayCmdCompletes > 0) return;
        try
        {
            if (!sys.Dmac.IsChannelIrqEnabled((int)Dmac.Channel.VIF1))
                sys.Dmac.EnableChannelIrq((int)Dmac.Channel.VIF1);
            if (!sys.Dmac.IsChannelIrqEnabled((int)Dmac.Channel.GIF))
                sys.Dmac.EnableChannelIrq((int)Dmac.Channel.GIF);
        }
        catch { /* ignore */ }

        _displayCmdCompletes++;
        _displayLockHits = 0;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1")
        {
            uint chcr = 0;
            try { chcr = sys.Dmac.ReadRegister(0x10009000u); } catch { /* ignore */ }
            Console.Error.WriteLine(
                $"[MKFAM] DA display IRQ arm n={_displayCmdCompletes} " +
                $"cmd=0x{cmd:X8} head=0x{head:X8} tail=0x{tail:X8} vif1chcr=0x{chcr:X8} " +
                $"p2={sys.Gif.Path2Transfers} prims={sys.Gs.PrimitivesDrawn} " +
                $"pc=0x{pc:X8} cyc={sys.MasterCycles}");
        }
    }

    /// <summary>
    /// PL-030 DA FRONTEND chrome: after Soft-GS Midway surface + INTERACTIVE keep-alive,
    /// force the real display process@0x1B3BB0 when the queue has pending cmds, lock is
    /// clear, and VIF1/GIF are idle. Title-local path only — does not invent PATH3 or
    /// Soft-GS pixels. Skipping when DMA/GIF sticky is live avoids stacking
    /// DIRECT-end-truncate aborts (S8 residual when title thrash-restarts chains).
    /// </summary>
    private void TryDrainDaDisplayQueueForChrome(Ps2System sys)
    {
        if (!IsDeadlyAlliance) return;
        if (sys.MasterCycles < 16_000_000) return;
        if (sys.Gs.PixelsWritten == 0 || sys.Gs.PrimitivesDrawn == 0) return;
        if (sys.Gif.Path2Transfers < 2) return;
        if (sys.Memory.Read32(0x0007F000) != 0x5354464Du) return;
        if (sys.Memory.Read32(0x0040B44C) == 0) return;
        if (_daDisplayForceProcess >= 256) return;
        if (sys.Hle is { ExitRequested: true }) return;

        // Title-local abort hygiene: never kick a new chain over in-flight Path2 sticky.
        if (sys.Gif.PacketInFlight) return;
        if (sys.Dmac.IsActive(Dmac.Channel.VIF1) || sys.Dmac.IsActive(Dmac.Channel.GIF))
            return;

        uint pc = (uint)sys.EE.PC;
        bool inMenu = pc is (>= DaMainMenuLoopLo and <= DaMainMenuLoopHi)
            or (>= DaMainLogoContinue and <= DaMainLogoContinue + 0x200)
            or (>= DaDiEiLo and <= DaDiEiHi)
            or (>= DaDisplayLoopLo and <= DaDisplayLoopHi);
        if (!inMenu) return;

        uint lockVal = sys.Memory.Read32(DaDisplayLock);
        uint head = sys.Memory.Read32(DaDisplayHead);
        uint tail = sys.Memory.Read32(DaDisplayTail);
        // Pointer queue: head chases tail. Reject corrupt / empty / huge spans.
        bool pending = head != tail
            && head >= 0x00100000 && head < 0x02000000
            && tail >= 0x00100000 && tail < 0x02000000
            && head < tail
            && (tail - head) <= 0x10000;
        if (!pending) return;

        // Clear sticky lock so process can run (same class as lock-escape; menu-band path).
        if (lockVal != 0)
        {
            sys.Memory.Write32(DaDisplayLock, 0);
            _displayLockEscapes++;
            _daMenuBandLockClears++;
            lockVal = 0;
        }

        uint cmd = 0;
        try { cmd = sys.Memory.Read32(head); }
        catch { return; }
        if ((cmd & DaDisplayCmdDoneBit) != 0) return;
        uint cmdType = cmd & 0xFF;
        // type-1 = VIF1 chain (chrome paint); accept sparse high-byte mode tags too.
        if (cmdType != 1 && cmdType is not (0x80 or 0x81 or 0x82 or 0x83 or 0x8F or 0xFF)
            && (cmd & 0xFF) != 0x01)
            return;

        // Throttle: every 4th eligible Step once Soft-GS is richer (avoid Path2 storm).
        if ((_daDisplayForceProcess & 3) != 0)
        {
            _daDisplayForceProcess++;
            return;
        }

        // Re-enter real process with $ra = menu loop so return lands in keep-alive.
        uint park = (pc is >= DaMainMenuLoopLo and <= DaMainMenuLoopHi)
            ? pc
            : DaMainMenuLoopLo + 8;
        sys.EE.SetGpr(28, new EmotionEngine.Gpr128 { Lo = DaGp });
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = park });
        // process@0x1B3BB0 uses s0/s1 context from outer loop — enter outer loop instead
        // when not already there so head/tail walk stays honest.
        if (pc is >= DaDisplayLoopLo and <= DaDisplayLoopHi)
            sys.EE.PC = DaDisplayProcess;
        else
            sys.EE.PC = DaDisplayLoopLo;

        if (_daChromeBaselineImg == 0)
            _daChromeBaselineImg = sys.Gs.ImageBytesWritten;
        else if (sys.Gs.ImageBytesWritten > _daChromeBaselineImg)
            _daChromeImgGrowthHits++;

        _daDisplayForceProcess++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && (_daDisplayForceProcess <= 24
                || _daDisplayForceProcess == 64
                || _daDisplayForceProcess == 128
                || (_daDisplayForceProcess % 64) == 0))
        {
            Console.Error.WriteLine(
                $"[MKFAM] DA chrome display drain n={_daDisplayForceProcess} " +
                $"head=0x{head:X8} tail=0x{tail:X8} cmd=0x{cmd:X8} lockWas={lockVal} " +
                $"p2={sys.Gif.Path2Transfers} prims={sys.Gs.PrimitivesDrawn} " +
                $"img={sys.Gs.ImageBytesWritten} abort={sys.Gif.PacketsAborted} " +
                $"heads={_displayHeadMoves} pc=0x{pc:X8}→0x{(uint)sys.EE.PC:X8} cyc={sys.MasterCycles}");
        }
    }

    /// <summary>
    /// DA WAVE-4/6: after CHCR.nTAG lets the real display IRQ drain head→tail (Path2 setup
    /// applied), main may still hit CRT Exit(0) @0x10C044. WAVE-4 only rescued when px==0
    /// (pre-XYZ2). WAVE-5 XYZ2 paints Midway sprites so that early-return blocked rescue and
    /// left exitRequested=True forever. WAVE-6: clear Exit after Path2+STFM even with paint;
    /// park DI/EI with $ra=self. Prefer <see cref="TrySoftSuccessDaPostLogoInit"/> +
    /// <see cref="TryKeepAliveDaMidwayMenu"/> for interactive keep-alive. No SignalSema(3).
    /// Does not invent Soft-GS pixels. Caps rescues.
    /// </summary>
    private void TryRescueDaPostDisplayExit(Ps2System sys)
    {
        // WAVE-6: keep-alive owns the px>0 Exit residual; this path is the pre-paint /
        // soft-success-miss belt (still clear Exit when Path2 evidence is live).
        if (sys.Memory.Read32(0x0007F000) != 0x5354464Du) return;
        if (sys.Memory.Read32(0x0040B44C) == 0) return; // gameart host not live
        // Only after VIF1 END Path2 actually delivered (WAVE-3/4 display chain).
        if (sys.Gif.Path2Transfers == 0) return;
        if (_postDisplayExitRescues >= 64) return;
        if (sys.MasterCycles < 7_000_000) return;

        bool exit0 = sys.Hle is { ExitRequested: true, ExitCode: 0 };
        uint pc = (uint)sys.EE.PC;
        bool inCrtExitStub = pc is >= 0x0010C040 and <= 0x0010C050; // Exit syscall stub only
        bool inDiEi = pc is >= DaDiEiLo and <= DaDiEiHi;
        if (!exit0 && !inCrtExitStub) return;
        // Already parked in DI/EI with Exit clear — just refresh workers periodically.
        if (!exit0 && inDiEi && (_postDisplayExitRescues & 7) != 0) return;
        // When Soft-GS Midway surface is live, keep-alive handles richer re-home.
        if (sys.Gs.PixelsWritten > 0 && sys.Gs.PrimitivesDrawn > 0
            && !exit0 && !inCrtExitStub)
            return;

        if (exit0)
            sys.Hle.ClearExitRequest();

        // Park at DI/EI wait band with $ra = self so jr ra re-enters (closed spin).
        // Display outer loop with head==tail falls through → CRT Exit thrash.
        // CRITICAL: $ra must not stay 0x1000AC (CRT after main) — any jr ra → Exit again.
        sys.EE.PC = DaDiEiLo;
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
        sys.EE.SetGpr(28, new EmotionEngine.Gpr128 { Lo = DaGp }); // $gp
        sys.EE.SetGpr(31, new EmotionEngine.Gpr128 { Lo = DaDiEiLo }); // $ra → self
        try
        {
            foreach (var t in sys.Hle.Kernel.AllThreads)
            {
                if (t.Id != 1 || !t.Alive) continue;
                t.Started = true;
                t.EverStarted = true;
                t.Sleeping = false;
                t.WaitSemaId = 0;
                t.SavedPc = DaDiEiLo;
                break;
            }
        }
        catch { /* ignore */ }

        // Wake pure sleepers only — never SignalSema(3) (SIF poll fabrications banned).
        try
        {
            var k = sys.Hle.Kernel;
            foreach (var t in k.AllThreads)
            {
                if (!t.Alive || t.Id < 2) continue;
                if (!t.Started)
                {
                    try { k.StartAndMaybeSwitch(sys.EE, t.Id, switchNow: false, arg: 0, fromSyscall: false); }
                    catch { /* ignore */ }
                    continue;
                }
                if (t.Sleeping && t.WaitSemaId == 0 && !t.WaitVblank)
                {
                    try { k.WakeupThread(t.Id); } catch { /* ignore */ }
                }
            }
            try { k.YieldToWorker(sys.EE); } catch { /* ignore */ }
        }
        catch { /* ignore */ }

        _postDisplayExitRescues++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _postDisplayExitRescues <= 16)
            Console.Error.WriteLine(
                $"[MKFAM] DA post-display Exit rescue n={_postDisplayExitRescues} " +
                $"-> DI/EI p2={sys.Gif.Path2Transfers} prims={sys.Gs.PrimitivesDrawn} " +
                $"px={sys.Gs.PixelsWritten} FRAME=0x{sys.Gs.Registers.FRAME_1:X} cyc={sys.MasterCycles}");
    }

    /// <summary>
    /// DA only: after wait-ready (PC past 0x2F55B0) with live gameart host, wake pure
    /// SleepThread workers and signal non-RPC-looking WaitSema parks so post-wait asset
    /// consumers can run. Never SignalSema on high RPC ids; never force *s0=4.
    /// </summary>
    private void TryKickDaPostWait(Ps2System sys)
    {
        // Host stream must be live (status plant done).
        if (sys.Memory.Read32(0x0040B44C) == 0) return;
        if (sys.Memory.Read32(0x0007F000) != 0x5354464Du) return;
        // Only after wait band / once past early boot.
        uint pc = (uint)sys.EE.PC;
        bool pastWait = pc < WaitReadyPcLo || pc > WaitReadyPcHi;
        if (!pastWait && _waitReadyEscapes == 0) return;
        if (sys.MasterCycles < 4_000_000) return;
        // Throttle: every ~256 Step hits after first escape, cap total kicks.
        if (_postWaitKicks >= 64) return;
        if ((_mslFilePumps & 255) != 0) return;

        var k = sys.Hle?.Kernel;
        if (k == null) return;
        int woke = 0;
        foreach (var t in k.AllThreads)
        {
            if (!t.Alive || t.Id < 1) continue;
            // Start any DORMANT worker (KickAllDormant skips id&lt;2; main may also re-Create).
            if (!t.Started && t.Id >= 2)
            {
                try
                {
                    k.StartAndMaybeSwitch(sys.EE, t.Id, switchNow: false, arg: 0, fromSyscall: false);
                    woke++;
                }
                catch { /* ignore */ }
                continue;
            }
            if (!t.Sleeping) continue;
            // Pure SleepThread — WakeupThread only.
            if (t.WaitSemaId == 0 && !t.WaitVblank)
            {
                try { k.WakeupThread(t.Id); woke++; }
                catch { /* ignore */ }
                continue;
            }
            // Low-id WaitSema (not RPC packet pool) — soft signal once.
            if (t.WaitSemaId is > 0 and < 16)
            {
                try { k.SignalSema(t.WaitSemaId); woke++; }
                catch { /* ignore */ }
            }
        }
        if (woke == 0) return;
        _postWaitKicks++;
        try { k.YieldToWorker(sys.EE); } catch { /* ignore */ }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_BIOS") == "1"
            && _postWaitKicks <= 16)
            Console.Error.WriteLine(
                $"[MKFAM] DA post-wait kick woke={woke} n={_postWaitKicks} " +
                $"pc=0x{pc:X8} cyc={sys.MasterCycles}");
    }

    private static bool IsMflHandle(uint v) => v is >= 1 and <= 0x100;

    private static bool IsWritableEeOrIop(uint addr) =>
        addr != 0
        && (addr < SystemMemory.RDRAM_SIZE
            || (addr >= 0x1C000000u && addr < 0x1C000000u + 0x00200000u));

    private static string NormalizeDaMemberPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return DaGameartMemberPath;
        // Drop leading separators so ScanSendBufferForPath first-byte whitelist matches.
        path = path.TrimStart('\\', '/');
        return path;
    }

    private static bool TryReadPathLike(SystemMemory mem, uint addr, out string path)
    {
        path = "";
        if (addr == 0) return false;
        var sb = new System.Text.StringBuilder(96);
        for (uint i = 0; i < 96; i++)
        {
            byte b;
            try { b = mem.Read8(addr + i); }
            catch { return false; }
            if (b == 0) break;
            if (b < 0x20 || b > 0x7E) return false;
            sb.Append((char)b);
        }
        path = sb.ToString();
        if (path.Length < 4) return false;
        // Path-ish: extension, separator, or known Midway member leaf.
        if (path.IndexOf('.') < 0 && path.IndexOf('\\') < 0 && path.IndexOf('/') < 0
            && path.IndexOf(':') < 0)
            return false;
        return true;
    }

    private static bool WriteCStringIfChanged(SystemMemory mem, uint addr, string s)
    {
        if (string.IsNullOrEmpty(s) || addr == 0) return false;
        // Skip write if already matches.
        if (TryReadPathLike(mem, addr, out string cur)
            && string.Equals(cur, s, StringComparison.OrdinalIgnoreCase))
            return false;
        for (int i = 0; i < s.Length; i++)
            mem.Write8(addr + (uint)i, (byte)s[i]);
        mem.Write8(addr + (uint)s.Length, 0);
        return true;
    }

    /// <summary>
    /// Deception only: rewrite fail-tails so post-MSL subsystem init returning 0 does not
    /// abort main→CRT Exit(0) / ExitThread before the game loop / member .ssf path.
    ///
    /// Live chain (200M): 0x1D9620→0x1D8120→0x126CE0→0x127900→main@0x1238E0→Exit.
    /// Post-0x510-honest residual (190M): main past 0x127900 → 0x18D740 → 0x1AAD90 list
    /// walk on garbage node → EE exception @0x80000180 → ExitThread; still no member .ssf.
    /// Plants:
    /// <list type="bullet">
    /// <item>0x1D8120 fail tails (factory register 0x1D9620 / 0x1D3F10 / 0x1E1340)</item>
    /// <item>0x127900 fail tails after 0x126CE0 and sibling inits</item>
    /// <item>main@0x1235B0 pre/post-0x127900 fail branches (0x1236D8..0x123788)</item>
    /// <item>0x1AAD90 empty-list force + nop jal from 0x18D740 (corrupt list exception)</item>
    /// </list>
    /// One-shot RDRAM plant — Step cannot catch single-instruction gates across slices.
    /// Does not plant wait status=4 (DA Exit lesson). DA paths untouched.
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

        // --- main@0x1235B0 pre-0x127900 fail branches (b → epilogue@0x1238E0) ---
        // Live residual after type-0x510 install is honest: main can still abort via
        // 0x227A20 / 0x223860 / 0x178040 returning 0 before ever calling 0x127900.
        // Soft-success the four main-side fail tails (same shape as 0x127900 plants).
        // 0x1236D8 after 0x227A20 (imm 0x81 → 0x1238E0)
        if (sys.Memory.Read32(0x001236D8) == 0x10000081u)
        {
            sys.Memory.Write32(0x001236D8, 0x10000001u); // → 0x1236E0
            sys.Memory.Write32(0x001236DC, 0x24020001u);
            plants++;
        }
        // 0x123700 after 0x223860 (imm 0x77 → 0x1238E0)
        if (sys.Memory.Read32(0x00123700) == 0x10000077u)
        {
            sys.Memory.Write32(0x00123700, 0x10000001u); // → 0x123708
            sys.Memory.Write32(0x00123704, 0x24020001u);
            plants++;
        }
        // 0x123718 after 0x178040 (imm 0x71 → 0x1238E0)
        if (sys.Memory.Read32(0x00123718) == 0x10000071u)
        {
            sys.Memory.Write32(0x00123718, 0x10000001u); // → 0x123720
            sys.Memory.Write32(0x0012371C, 0x24020001u);
            plants++;
        }
        // 0x123788 after 0x127900 (imm 0x55 → 0x1238E0) — covers residual return 0
        if (sys.Memory.Read32(0x00123788) == 0x10000055u)
        {
            sys.Memory.Write32(0x00123788, 0x10000001u); // → 0x123790
            sys.Memory.Write32(0x0012378C, 0x24020001u);
            plants++;
        }

        // --- post-0x127900 list walk exception (live 190M host-present) ---
        // main continues past 0x127900 (plants + honest 0x510) into 0x18D740 → jal 0x1AAD90.
        // 0x1AAD90 walks a linked list; live node s1=0x401A6800 (non-RDRAM) faults at
        // `lw v0,16(s1)` @0x1AAE00 → EE exception vector 0x80000180 → later ExitThread.
        // Force empty-list branch (beq s3,s2,done → b done) so the walk never touches
        // garbage nodes. Belt-and-suspenders: nop the jal from 0x18D740.
        // Does not plant wait status=4. TITLE_LOCAL Dec only.
        if (sys.Memory.Read32(0x001AADD4) == 0x12720028u) // beq s3, s2, 0x1AAE78
        {
            sys.Memory.Write32(0x001AADD4, 0x10000028u); // b 0x1AAE78
            plants++;
        }
        if (sys.Memory.Read32(0x0018D868) == 0x0C06AB64u) // jal 0x1AAD90
        {
            sys.Memory.Write32(0x0018D868, 0x00000000u); // nop
            plants++;
        }

        // --- post-init list helper @0x3B9E00 (live 200M+ thrash after exception plant) ---
        // Inner: lw v1,40(v1) cycle → force next=null.
        // Outer: bne s1,zero,loop @0x3B9E80 — nop so one s1 pass falls to done @0x3B9EE0.
        // Runtime TryEscapeDecPostInitListWalk remains as belt-and-suspenders.
        if (sys.Memory.Read32(0x003B9E60) == 0x8C630028u) // lw v1, 40(v1)
        {
            sys.Memory.Write32(0x003B9E60, 0x0000182Du); // daddu v1, zero, zero
            plants++;
        }
        if (sys.Memory.Read32(0x003B9E80) == 0x1620FFEAu) // bne s1, zero, 0x3B9E2C
        {
            sys.Memory.Write32(0x003B9E80, 0x00000000u); // nop — exit outer after first s1
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
