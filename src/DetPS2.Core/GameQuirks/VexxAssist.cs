using System;
using System.Collections.Generic;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Vexx (USA) SLUS_203.83 — IOPRP252 + null-path basename + CRT/string heap plant +
/// SearchFile 0x128 path-layout (+0x24) + freelist bump escape + STREE0 re-plant.
///
/// Wave-1 residual: GAME.TXT SearchFile+CdRead (cdvd=4). Wave-2: STREE0.TRE SearchFile ok
/// (lsn/size ~1GB). Wave-3: hang was null CD I/O vtable @0x3BD3A8 (install never ran) →
/// STREE open fails → hash-map walk thrash @0x1DD2E0 with table=null. Plant game default
/// open/read stubs (partial TRE stream, not full 1GB map); expand freelist/bump for TOC
/// (~4.6MB header, not full TRE); escape null-table walk.
///
/// Wave-4: retail open prefixes <c>host:</c> then FILEIO RPC — bind never appears after
/// SearchFile (empty SIFCMD cid=0 thrash), so STREE TOC never CdReads (cdvd=0). Host-serve
/// CD I/O open/read/seek/tell/size/close against the mounted ISO (real sector stream for
/// TRE TOC / GAME.TXT); strip <c>$/</c> virtual root. Soft-GS residual. See issue #19.
///
/// Wave-5: stream-map open at 0x1DCEB0 loads the CD I/O vtable from <c>0x3AD3A8</c>
/// (lui at,0x3B + lw -0x2C58), NOT the 0x3BD3A8 plant target used in waves 3–4. Correct
/// base so host open/read runs: first u32 (entry count) → malloc(count×24) table → full
/// index CdRead-equivalent host read → Soft-GS residual after assets bind.
///
/// Wave-6: residual host-open FAIL for <c>data\…</c> / fonts / frontend (paths live inside
/// STREE0.TRE, not ISO root). Build NameCRC32→(offset,size) from the count×24 index words
/// (ground-truthed: CRC32 of lowercased backslash path matches entry NameCRC; dual layout
/// A=NameCRC,DataCRC,off,sz / C=off,sz,NameCRC,DataCRC) and FileOpenVirtualStream into the
/// STREE0 extent so fonts/textures stream → Soft-GS title-surface pixels.
///
/// S1 / PL-017: after STREE0 stream + Soft-GS title surface, dense START/CROSS pad inject
/// (edge phases + ForceRefreshPad) so frontend/pad readers can advance toward INTERACTIVE
/// (T2 / P1). No WaitSema fabricate; no invent PATH3 / plant pixels.
///
/// S2 / PL-032: TRE member completeness — binary .tgax/.bmpx score was 8–9 &lt; MemberMinScore
/// so many fonts/shadows failed CRC open; host-read BADARGS (buf=0xFFFFFFF0 size=-1) on
/// text/begin.atr recovered via freelist/s-reg buffer hunt + host bump; nested stree1/patch0
/// open as minimal TRE stubs when not in STREE0 index.
///
/// S3 / PL-032b (MENU-VEXX): residual button2–4/9 .tgax NameCRC hits STREE0 but ScoreMemberProbe
/// landed at 5 (&lt; min 8) for compact binary heads (printable≈8 → old +4 tier). Boost binary
/// texture score; expand leaf-open path alts to onscreengraphics/fonts + frontend prefixes
/// (NormalizeHostCdPath leaf-strips full $/Data/… paths). No PATH3 plant / no invent pixels.
///
/// S4 / PL-032f (MENU-VEXX-3): TRE members open (button*/loadtimer/begin.atr) but Soft-GS lit
/// plateaus ~6405 — after begin.atr host-read BADARGS recover wrote into a host-picked buffer
/// the EE never retained, so begin.mtf/ati never opened and frontend Path2 froze. Harden
/// recover: SP/t-reg scan + a1 writeback + .ati magic score; expand memorycard leaf prefixes
/// so bootup/mainmenu/lang packages bind when the EE requests them. No invent PATH3/pixels.
///
/// S5 / PL-032g (MENU-VEXX-4): post-swooshes.swh hang is NOT WaitSema — main stays runnable.
/// PC≈0x330E54 is packed-byte→float expand (mtc1/cvt.s.w/swc1) over a circular object list
/// at 0x330E34..0x331074: <c>s0=*s0</c> until <c>s0==(s5+4)</c>. Corrupted/empty head or a
/// self-loop never hits the sentinel → multi-10M cycle thrash; begin.mtf/ati (named in
/// begin.atr ACTOR_MESH/ACTORINSTANCES) never requested. Escape broken list to epilogue
/// v0=1. Also tighten freelist escape after STREE members bind (walk thrash @0x1CE1x0 with
/// size 0x3F0 post-swooshes). No invent PATH3/pixels; no global WaitSema fabricate.
///
/// S6 / PL-032h (MENU-VEXX-5): post-float-expand residual is NOT bare s0=0 AdEL — live
/// PCBREAK shows ctor entry <c>0x3402E0</c> with valid <c>a0=this</c> (e.g. 0x45B290), delay
/// <c>move s0,a0</c>, then <c>jal 0x2EE7F0</c> base init. During that jal the derived epilogue
/// words at <c>0x34030C</c>/<c>0x340310</c> are overwritten (<c>AE00003C→F000003C</c>,
/// <c>DFBF0010→DF01893F</c>) so <c>ld ra</c> never runs; <c>jr ra</c> with ra still 0x3402F4
/// loops mid-ctor, <c>lq s0</c> restores the pre-delay s0=0, and UnknownOpcode storms.
/// Host-complete the 0x3402E0 object ctor (minimal base fields + derived vtable/zeros) and
/// re-plant epilogue words; keep sticky this-ptr for any residual store path.
///
/// S7 / PL-032i (MENU-VEXX-6): after AdEL clear + begin.pcl MEMBER open, residual is a
/// pure EE poll at <c>0x35E190</c> (<c>v0=*(*0x4311C0+0x2E8)</c>) inside spin-wait
/// <c>0x369790</c> (jal poll; bne leave; yield every 4095). Soft-GS still lit=6405 because
/// begin.pcl BADARGS recover wrote the 690B precache list (names <c>begin.mtf</c>) into a
/// freelist bump after pvsx empty-stub demoted the live EE heap dest at sp+0x30 — and
/// freelist recovers skip the s1/div full-read success patch, so the wrapper rejects the
/// read. Prefer sp+0x30 for real payloads; full-read-patch post-swooshes poison sizes;
/// soft-set the ready flag if the wait band thrash persists after pcl bind.
///
/// S8 / PL-032j (MENU-VEXX-7): *PR skip of begin.mtf between screenproxy.tgax and
/// screenproxy.mtf. Root: begin.pcl BADARGS reclaimed sp+0x30=0x672C10 (still holding
/// begin.atr ACTOR_MESH/ATI path strings) after a tiny commontree stub cleared the
/// single-slot last-recover demotion — pcl overwrote atr so the mesh path the EE holds
/// into that buffer is garbage and host-open for begin.mtf is never issued. Also
/// deitynofade.atr recovered into code-band 0x1F609C (sp+0x30=0) → freelist data-as-code.
/// Track package buffers; prefer fresh s2/sp+0x10; reject code-band package dests.
///
/// S9 / PL-032k (MENU-VEXX-8): begin.mtf still never host-opened after PL-032j (atr live,
/// begin0.tre TRE full-read @0x408700, *PR continues past the slot). Ground-truth:
/// begin0.tre is a 10-entry pack (16B records: off,sz,nameCRC,dataCRC); begin.mtf CRC
/// <c>0x2EA9190F</c> is entry#1 at pack off 0x504 sz 2302 (FILE/ELIF mesh embedded).
/// begin.mtf is dual-slide-only in STREE0 (not in aligned 24B stream-map rows). *PR may
/// treat pack TOC as "already satisfied" without a correct pack-base link (TOC re-read
/// lands at 0x1D050D4 split from full payload), so mesh never binds and Path2 lit stays
/// ~6405. PL-032k: scrub begin.mtf NameCRC from begin0.tre TOC so *PR must host-open via
/// dual-slide STREE member; inject dual CRCs into host stream-map; ACTOR_MESH path alts;
/// post-PR residual escape @0x2243A0. No invent PATH3/pixels.
///
/// S10 / PL-032l (MENU-VEXX-9): after begin.mtf MEMBER OPEN, EE still dies in UnknownMmioRead
/// storm @0x2243A0 (final PC≈0x2243E8 through 100M). PL-032k soft-return used ra when it
/// looked like code — but ra often lands back inside the same 0x2243xx body (nested jal /
/// leaf thrash), so the escape is a no-op and Path2 stays frozen at lit≈6405/prims=26.
/// PL-032l: reject in-band resume; walk stack for a code return above the thrash function;
/// after begin.mtf open escalate hit gate; after many escapes force-leave the 0x224000 band.
///
/// S11 / PL-032m (MENU-VEXX-10): ELF ground-truth — 0x2243A0 is NOT an MMIO probe. It is a
/// name-search loop at <c>0x224360</c>: <c>lw a0,0xC(s5); jal 0x1CF410</c> (case-fold strcmp,
/// fold table @0x3D3010). Bad object slots make a0 land in 0x1000xxxx/0x1100xxxx →
/// UnknownMmioRead storm. PL-032l TRACE: escape once to mid-fn <c>0x225004</c> then idle
/// PC=0x35B534 / lit=6405. PL-032m: (1) host-complete strcmp on bad a0/a1 via caller ra
/// (not jr-delay that reclobbers v0); (2) skip bad slots at 0x2243A0 → continue 0x2243B8;
/// (3) last-resort natural epilogue 0x2243F0 (s0=v0=0) + deep stack sp+0x60; (4) hot-slice
/// 0x1CF410 + 0x224360. Goal: finish actor name bind → Path2/IMAGE → lit&gt;&gt;20k.
/// No invent PATH3/pixels.
///
/// S12 / PL-032p (MENU-VEXX-11): PL-032o FORCE-MATCH + widget.atr FULL cache still ends at
/// residual PC=<c>0x0011C200</c> forever. ELF ground-truth: 0x11C200 is a pure C++ thunk
/// <c>lw t9,0x28(a0); lw t9,0x298(t9); jr t9</c> (family 0x11C170.. with slots 0x274..).
/// Path-object ctor at <c>0x21CDB0</c> sizes the registry object at <c>0x104</c> (alloc 260
/// @0x223BD8) with name@+0xC and payload@+0x10/+0x14 after load; PL-032o stub was only 0x40
/// and zeroed +0x28, so any virtual call on the force-matched object (or open-bus rescue to
/// 0x11C200 as false CRT0) null-jr thrash. PL-032p: (1) path stub = 0x110 retail layout +
/// host vtable at +0x28 (all slots → jr-ra nop); (2) escape null-vtable thunk band via ra;
/// (3) never re-home stack-death to 0x11C200. No invent PATH3/pixels.
///
/// S13 / PL-032q (MENU-VEXX-12): residual after PL-032p5 — SIF unknown sid <c>0x00054323</c>
/// is <c>AAAIOP_driver</c> v3.0.4 (<c>DATA/SOUND/AAAIOP.IRX</c>); post-widget bind-wait at
/// 0x1B12xx is actually CD_BASE <c>0x80000592</c> (not AAAIOP). PL-032q: (1) RealSifRpc
/// SidAaaIop soft HLE; (2) bind-wait hard escape host-completes CD+AAAIOP clients and takes
/// success path 0x1B12C4; (3) AAAIOP server-poll escape @0x38d230. Goal lit&gt;20k / prims&gt;40.
/// No invent PATH3/pixels.
/// </summary>
public sealed class VexxAssist : IGameQuirkModule
{
    public string Serial => "SLUS_203.83";
    public string DisplayName => "Vexx (USA)";

    /// <summary>
    /// M8-a quiet retirement (docs/infra-audits/m8a-haven-vexx-retirement-checklist.md §5
    /// stage 4): once M4-b's tag-if-applied GetVersion policy is proven sufficient for Vexx
    /// (see playability-roadmap.json M4-d/M8-a evidence), this title no longer needs its own
    /// PreferIopRpGetVersion opt-in -- default is now to SKIP setting it. Set
    /// DETPS2_M8A_VEXX_NO_PREFER_IOPRP=0 to opt back in to the legacy per-title flag
    /// (rollback path per checklist §6).
    /// </summary>
    private static bool SkipPreferIopRp
    {
        get
        {
            string? v = Environment.GetEnvironmentVariable("DETPS2_M8A_VEXX_NO_PREFER_IOPRP");
            return v is null || !(string.Equals(v, "0", StringComparison.Ordinal) ||
                                   string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// M8-a quiet retirement, plant half: default is now to SKIP the "2520" IopVersionCellA/B
    /// RAM plant and its Step() per-tick re-plant, since GetVersion's own tag-if-applied policy
    /// (M4-b) already supplies the same digits without it (playability-roadmap.json M8-a Vexx
    /// evidence). Set DETPS2_M8A_VEXX_NO_VERSION_PLANT=0 to opt back in (rollback path §6).
    /// </summary>
    private static bool SkipVersionPlant
    {
        get
        {
            string? v = Environment.GetEnvironmentVariable("DETPS2_M8A_VEXX_NO_VERSION_PLANT");
            return v is null || !(string.Equals(v, "0", StringComparison.Ordinal) ||
                                   string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));
        }
    }

    public const uint IopVersionCellA = 0x003D18B8;
    public const uint IopVersionCellB = 0x003D1938;
    public const uint PathBasenameA = 0x00146170;
    public const uint PathBasenameB = 0x00146230;
    public const uint StubA = 0x00090000;
    public const uint StubB = 0x00090040;
    public const uint CrtMallocSlot = 0x003BCD00;
    public const uint CrtFreeSlot = 0x003BCD04;
    public const uint CrtReallocSlot = 0x003BCD08;
    public const uint StringAllocHook = 0x00444998;
    public const uint StringFreeHook = 0x004449A0;
    public const uint SmallPoolRoot = 0x003F71B0;
    public const uint MallocStub = 0x00090100;
    public const uint FreeStub = 0x00090140;
    public const uint ReallocStub = 0x00090160;
    public const uint BumpCursorCell = 0x00090180;
    public const uint BumpArenaBase = 0x01800000;
    /// <summary>16 MiB bump — STREE0 TOC (~4.6MB) + stream tables + GAME.TXT headroom; never full 1GB TRE.</summary>
    public const uint BumpArenaEnd = 0x02800000;
    /// <summary>Freelist host-bump cap (partial TRE header / stream tables, not full map).</summary>
    public const uint FreelistMaxBump = 0x00A00000;
    public const uint PathNormalizeLoop = 0x00372ABC;
    public const uint PathNormalizeAfterLoop = 0x00372B04;
    public const uint EmptyStringSentinel = 0x003C4C58;
    public const uint FreelistWalkLo = 0x001CE190;
    public const uint FreelistWalkHi = 0x001CE210;
    public const uint FreelistSuccessStore = 0x001CE280;
    /// <summary>PL-032h: second freelist walker (post-begin0.tre thrash PC≈0x1CE314 nop in loop).</summary>
    public const uint Freelist2WalkLo = 0x001CE2B0;
    public const uint Freelist2WalkHi = 0x001CE3F0;
    public const uint Freelist2SuccessStore = 0x001CE3E8;
    /// <summary>Broader freelist family after begin0 — residual PCs drift 0x1CE3xx–0x1CExxx.</summary>
    public const uint FreelistFamilyLo = 0x001CE190;
    public const uint FreelistFamilyHi = 0x001CEA80;
    public const uint SearchFileArgBuf = 0x1C1F4000;

    /// <summary>
    /// CD file-backend vtable the stream open path loads (EE 0x1DCEFC: lui at,0x3B;
    /// lw open=-0x2C58 → <c>0x3AD3A8</c>). Wave 3–4 wrongly planted <c>0x3BD3A8</c> (never
    /// read) while retail defaults live here — open returned through host:+FILEIO (fail) so
    /// STREE stream map table at obj+8 stayed null. Defaults: 0x1D0CE0 open, 0x1D0CA0 read.
    /// </summary>
    public const uint CdIoVtableBase = 0x003AD3A8;
    public const uint CdIoDefaultOpen = 0x001D0CE0;
    public const uint CdIoDefaultClose = 0x001D0C40;
    public const uint CdIoDefaultRead = 0x001D0CA0;
    public const uint CdIoDefaultWrite = 0x001D0CB0;
    public const uint CdIoDefaultStub0 = 0x001D0CC0;
    public const uint CdIoDefaultSeek = 0x001D0E60;
    public const uint CdIoDefaultTell = 0x001D0CD0;
    public const uint CdIoDefaultSize = 0x001D0ED0;
    public const uint CdIoDefaultMisc = 0x001D0F40;

    /// <summary>
    /// Host-serve CD I/O stubs (spin until Step fulfills). Vtable points here so open/read
    /// cannot race past a single-instruction PC sample (wave-4).
    /// Layout: open, close, read, write, seek, tell, size — 0x20 bytes each (spin + nops).
    /// Must live at ≥0x00100000 — <see cref="KernelBootstrap.RescueIfLostInLowMem"/> treats
    /// PC below 1MiB as lost and re-homes before ActiveQuirk.Step can service the spin
    /// (wave-5: stubs at 0x90200 never ran; stream open hung).
    /// </summary>
    public const uint HostCdStubBase = 0x00F00000;
    public const uint HostCdStubOpen = HostCdStubBase + 0x00;
    public const uint HostCdStubClose = HostCdStubBase + 0x20;
    public const uint HostCdStubRead = HostCdStubBase + 0x40;
    public const uint HostCdStubWrite = HostCdStubBase + 0x60;
    public const uint HostCdStubSeek = HostCdStubBase + 0x80;
    public const uint HostCdStubTell = HostCdStubBase + 0xA0;
    public const uint HostCdStubSize = HostCdStubBase + 0xC0;
    public const uint HostCdStubEnd = HostCdStubBase + 0xE0;

    /// <summary>Hash-map lookup thrash when stream table at s5+8 is null (PC 0x1DD2E0).</summary>
    public const uint StreamMapLookupLo = 0x001DD2C0;
    public const uint StreamMapLookupHi = 0x001DD370;
    public const uint StreamMapLookupFail = 0x001DD370;

    /// <summary>
    /// PL-032g: packed-field float expand after actor/swoosh property parse.
    /// Outer circular list walk <c>s0=*s0</c> until sentinel <c>s5+4</c>; body does
    /// mtc1/cvt.s.w/swc1 for 8 field groups. Hang sample PC≈0x330E54 (bltz a1 delay of u8 unpack).
    /// </summary>
    public const uint FloatExpandLo = 0x00330E34;
    public const uint FloatExpandHi = 0x00331074;
    /// <summary>
    /// List compare: <c>bne s0, v0, expand</c> then natural epilogue (restore + v0=1 + jr ra).
    /// Prefer landing here with s0==v0==sentinel over jumping into the epilogue body (ra must stay valid).
    /// </summary>
    public const uint FloatExpandListCompare = 0x00331070;
    /// <summary>Success epilogue body (only after compare falls through with live stack/ra).</summary>
    public const uint FloatExpandEpilogue = 0x00331078;
    /// <summary>Step samples in float-expand band before force-leave (50k-cycle slices × N).</summary>
    public const int FloatExpandMaxStepHits = 24;

    /// <summary>
    /// PL-032h: derived object ctor that jals base init <c>0x2EE7F0</c> then stores vtable/zeros via s0.
    /// Live PCBREAK: jal corrupts epilogue at 0x34030C → ra-restore never runs → mid-ctor loop.
    /// </summary>
    public const uint ObjCtorEntry = 0x003402E0;
    public const uint ObjCtorJalBase = 0x003402EC;
    public const uint ObjCtorPostJal = 0x003402F4;
    public const uint ObjCtorEpilogue = 0x00340310;
    public const uint ObjCtorEnd = 0x00340320;
    /// <summary>Base object init target of the derived ctor jal (saves a0@sp+0x3C, heavy a1 path).</summary>
    public const uint ObjBaseInitLo = 0x002EE7F0;
    public const uint ObjBaseInitHi = 0x002EE9A8;
    /// <summary>Retail derived vtable written at this+0x1C after base returns.</summary>
    public const uint ObjDerivedVtable = 0x003F5690;
    /// <summary>Retail base vtable written by 0x2EE7F0 / 0x2EC720 at this+0x1C.</summary>
    public const uint ObjBaseVtable = 0x003F5060;
    /// <summary>Big side-object size allocated on the a1!=0 path inside 0x2EE7F0.</summary>
    public const uint ObjBaseSideAllocSize = 0x43D8;

    /// <summary>
    /// PL-032h residual after ctor: id-table search at <c>0x32CEE0</c> walks
    /// <c>for (i=0;i&lt;count;i++) if (table[i]==a1)</c> with count at s0+0xA4. Corrupt/huge
    /// count (or never-match with count≫40) burns multi-10M before the retail cap at
    /// <c>slti t0,40</c>. Soft-escape when count or index exceeds the retail limit.
    /// </summary>
    public const uint IdTableSearchLo = 0x0032CEE0;
    public const uint IdTableSearchHi = 0x0032CF28;
    public const int IdTableRetailCap = 40;

    /// <summary>
    /// PL-032i: frontend ready-flag getter <c>0x35E190</c> —
    /// <c>v0 = *(*(0x4311C0) + 0x2E8)</c>. Spin-wait at <c>0x369790</c> jal's this until
    /// non-zero (yield via 0x1D18A0 every 4095 polls). Claim residual PC after begin.pcl.
    /// </summary>
    public const uint ReadyFlagGlobalPtr = 0x004311C0;
    public const uint ReadyFlagObjOff = 0x2E8;
    public const uint ReadyFlagGetter = 0x0035E190;
    public const uint ReadyFlagWaitLo = 0x00369790;
    public const uint ReadyFlagWaitHi = 0x003697F4;
    public const uint ReadyFlagPollAltLo = 0x003681D4;
    public const uint ReadyFlagPollAltHi = 0x00368220;
    /// <summary>Step hits in wait/getter band before force-set (post-begin.pcl only).</summary>
    public const int ReadyFlagWaitMaxHits = 48;

    /// <summary>Allow freelist bump after CRT plant settles (not during whip-era thrash).</summary>
    public const ulong FreelistEscapeMinCycles = 1_000_000UL;

    /// <summary>Cap single host read (TOC / stream tables — never full 1GB TRE).</summary>
    public const uint HostReadMaxBytes = 0x00800000;

    private bool _pathPatched, _mallocPlanted, _cdIoPlanted;
    private int _versionReplants, _nullPathEscapes, _pathNormEscapes, _mallocReplants;
    private int _hookReplants, _freelistEscapes, _searchPathFixes, _searchPlants;
    private int _stackRescues, _cdIoReplants, _streamMapEscapes;
    private int _hostOpens, _hostReads, _hostCloses, _hostSeeks;
    private int _hostMemberOpens, _hostMemberFail;
    private int _hostBadArgsRecovered, _hostNestedTreStubs;
    private int _streamMapProbes, _streamMapPlants;
    private int _streamMapLookupHits;
    private int _padInjectPulses;
    private int _floatExpandHits, _floatExpandEscapes;
    private bool _swooshesLoaded;
    /// <summary>PL-032i: begin.pcl MEMBER opened (precache list that names begin.mtf).</summary>
    private bool _beginPclLoaded;
    /// <summary>PL-032k: begin0.tre MEMBER opened / full-read landed.</summary>
    private bool _begin0TreLoaded;
    /// <summary>PL-032k: begin.mtf host-open observed (goal signal).</summary>
    private bool _beginMtfOpened;
    /// <summary>PL-032k: full begin0.tre payload base (mesh at +0x504).</summary>
    private uint _begin0TrePackBase;
    private int _begin0TreTocScrubs;
    private int _beginMtfStreamMapInjects;
    private int _postPrResidualEscapes;
    private int _postPrResidualHits;
    private int _nameSearchForceMatches;
    private int _vtableThunkEscapes;
    private int _sifRpcBindWaitEscapes;
    private bool _hostActorVtablePlanted;
    /// <summary>
    /// PL-032p/q: SIF RPC bind-wait after widget.atr in <c>0x1B1198</c> — binds
    /// <b>sid=0x80000592</b> (CD_BASE), not 0x54323. jal 0x1C5018 fail → 0x100000-iter delay
    /// @0x1B1294/0x1B131C → retry. Residual PC≈0x1B1330.
    /// PL-032q: unknown sid <c>0x00054323</c> is AAAIOP_driver (client @0x4455D0); HLE in
    /// <see cref="RealSifRpc.SidAaaIop"/>. Bind-wait hard escape host-completes CD client and
    /// takes success path 0x1B12C4 so CdInit CallRpc runs.
    /// </summary>
    public const uint SifRpcBindWaitLo = 0x001B1260;
    public const uint SifRpcBindWaitHi = 0x001B1350;
    public const uint SifRpcBindDelayA = 0x001B1294;
    public const uint SifRpcBindDelayB = 0x001B131C;
    public const uint SifRpcBindCall = 0x001C5018;
    public const uint SifRpcBindSuccess = 0x001B12C4;
    public const uint SifRpcCallRpc = 0x001C51E8;
    /// <summary>PL-032q: CD_BASE client data used by 0x1B1198 bind (<c>s1 = 0x3F7CA8</c>).</summary>
    public const uint SifCdBaseClient = 0x003F7CA8;
    /// <summary>PL-032q: AAAIOP client data (bind @0x38d1f4, server poll @0x4455F4=+0x24).</summary>
    public const uint SifAaaIopClient = 0x004455D0;
    /// <summary>PL-032q: AAAIOP server-ready poll loop (lw cbuf @0x4455F4; beq zero retry).</summary>
    public const uint AaaIopServerWaitLo = 0x0038D230;
    public const uint AaaIopServerWaitHi = 0x0038D264;
    private int _readyFlagWaitHits, _readyFlagForceSets;
    private bool _tocProbeDone;
    /// <summary>PL-032n/o: paths that completed at least one host-read (eligible for force-match).</summary>
    private readonly string[] _recentOpenPaths = new string[16];
    private int _recentOpenPathCount;
    /// <summary>PL-032o: last host-open path pending first successful read.</summary>
    private string _pendingOpenPath = "";
    /// <summary>PL-032o: normalized path → 1-based host handle (for eager payload on force-match).</summary>
    private readonly Dictionary<string, int> _pathOpenHandles = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>PL-032n: host-built path-object stubs (name @+0xC) for force-match returns.</summary>
    private readonly Dictionary<string, uint> _pathObjectStubs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>PL-032o: path → (payload base, size) cached at open while fd is live.</summary>
    private readonly Dictionary<string, (uint baseAddr, uint size)> _pathPayloadCache =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>PL-032o: max eager bulk-read (widget.atr ≈10MB; bump arena 16MB).</summary>
    public const uint EagerPathPayloadMax = 12u * 1024 * 1024;

    /// <summary>
    /// PL-032k: NameCRC32 of <c>data\levels\frontend\memorycard\begin\begin.mtf</c>
    /// (ground-truth; matches begin0.tre entry + dual-slide STREE member).
    /// </summary>
    public const uint BeginMtfNameCrc = 0x2EA9190Fu;
    /// <summary>begin.ati ACTORINSTANCES companion (same folder; dual-slide C).</summary>
    public const uint BeginAtiNameCrc = 0xB70CFDFAu;
    /// <summary>begin0.tre full retail size (count=10 pack with embedded meshes).</summary>
    public const uint Begin0TreFullSize = 23257;
    /// <summary>begin0.tre TOC bytes after u32 count (10 × 16).</summary>
    public const uint Begin0TreTocBytes = 160;
    /// <summary>Pack-local file offset of embedded begin.mtf FILE/ELIF mesh.</summary>
    public const uint Begin0TreMtfOff = 0x504;
    /// <summary>Post-*PR residual thrash sample PC (UnknownMmioRead storm after screenproxy.atr).</summary>
    public const uint PostPrResidualPc = 0x002243A0;
    /// <summary>Tight body around the jal storm (PL-032k band).</summary>
    public const uint PostPrResidualLo = 0x00224380;
    public const uint PostPrResidualHi = 0x00224400;
    /// <summary>PL-032l: wider thrash band — final PC drifts 0x2243E8 and re-enters via nearby code.</summary>
    public const uint PostPrThrashBandLo = 0x00224000;
    public const uint PostPrThrashBandHi = 0x00225000;
    /// <summary>
    /// PL-032m: natural miss epilogue of name-search <c>0x224360</c>
    /// (<c>move v0,s0</c> @0x2243F0 → restore → <c>jr ra</c>). Never 0x225004 (mid-other-fn).
    /// </summary>
    public const uint PostPrHardLeavePc = 0x002243F0;
    /// <summary>PL-032m: case-fold strcmp used by post-*PR name search (jal from 0x2243A4).</summary>
    public const uint NameStrcmpEntry = 0x001CF410;
    /// <summary>jr ra of strcmp — delay slot is <c>subu v0,v1,v0</c>; host-complete must NOT land here.</summary>
    public const uint NameStrcmpJrRa = 0x001CF448;
    /// <summary>PL-032m: name-search loop body (lw a0,0xC(s5) then jal strcmp).</summary>
    public const uint NameSearchLwA0 = 0x002243A0;
    /// <summary>PL-032m: continue loop after no-match (addiu s2,s2,4 path).</summary>
    public const uint NameSearchContinue = 0x002243B8;
    /// <summary>PL-032n: name-search success epilogue (<c>move v0,s0</c> then restore/jr).</summary>
    public const uint NameSearchFoundReturn = 0x002243F0;
    /// <summary>
    /// PL-032p: path-object size — retail alloc 260 (0x104) at 0x223BD8 → ctor 0x21CDB0.
    /// 0x110 adds headroom past +0x100 (ref-list fields used by 0x21E160).
    /// </summary>
    public const uint PathObjectStubSize = 0x110;
    /// <summary>Retail path-object ctor / size constant.</summary>
    public const uint PathObjectCtor = 0x0021CDB0;
    public const uint PathObjectRetailSize = 0x104;
    /// <summary>
    /// PL-032p: C++ virtual thunk family <c>lw t9,0x28(a0); lw t9,slot(t9); jr t9</c>.
    /// Residual 0x11C200 is slot 0x298. Open-bus rescue falsely treats this as CRT0.
    /// </summary>
    public const uint VtableThunkLo = 0x0011C170;
    public const uint VtableThunkHi = 0x0011C400;
    public const uint VtableThunkResidual = 0x0011C200;
    /// <summary>Host no-op method (jr ra; v0=0) and host actor-ish vtable (≥0x320 for slot 0x298).</summary>
    public const uint HostNopRet0 = HostCdStubEnd + 0x00;
    public const uint HostNopRet1 = HostCdStubEnd + 0x10;
    public const uint HostNopRetThis = HostCdStubEnd + 0x20;
    public const uint HostActorVtable = HostCdStubEnd + 0x40;
    public const uint HostActorVtableBytes = 0x400;
    /// <summary>PL-032m: name-search function entry (addiu sp,-112).</summary>
    public const uint NameSearchFunc = 0x00224360;
    private Iso9660.Volume? _isoVol;
    private string? _isoVolPath;
    /// <summary>Game 1-based handle → IopModules FILEIO fd (0-based).</summary>
    private readonly Dictionary<int, int> _hostFds = new();
    /// <summary>1-based handle → last successful open size (for BADARGS bulk-read recovery).</summary>
    private readonly Dictionary<int, uint> _hostFdSizes = new();
    /// <summary>Recent freelist bump bases (PL-032 BADARGS buffer hunt).</summary>
    private readonly uint[] _recentBumpBases = new uint[8];
    /// <summary>Parallel sizes for recent freelist bumps (match want when possible).</summary>
    private readonly uint[] _recentBumpSizes = new uint[8];
    private int _recentBumpCount;
    private uint _lastBumpBase;
    private uint _lastBumpSize;
    /// <summary>Last BADARGS destination — demote on next recover so packages do not clobber.</summary>
    private uint _lastRecoveredBuf;
    /// <summary>Size of last BADARGS recover — tiny stubs (≤32) may yield the EE heap slot.</summary>
    private uint _lastRecoveredSize;
    /// <summary>
    /// PL-032j: ring of prior real-package recover dests (≥64B). Tiny stubs must not clear
    /// demotion of live atr buffers — pcl reclaim of 0x672C10 killed begin.mtf path strings.
    /// </summary>
    private readonly uint[] _packageBufRing = new uint[8];
    private readonly uint[] _packageSzRing = new uint[8];
    private int _packageBufCount;

    /// <summary>STREE0 disc byte offset (LBA×2048) once resolved.</summary>
    private long _stree0DiscByteOff;
    private uint _stree0Size;
    /// <summary>NameCRC32 (path) → (offset within STREE0, size, score). Built once from TOC words.</summary>
    private Dictionary<uint, (uint Off, uint Size, int Score)>? _streeMemberByCrc;
    private int _streeMemberIndexCount;
    /// <summary>
    /// Minimum payload probe score. PL-032: was 10 which rejected compact binary .tgax
    /// (score 8–9). PL-032b: compact button2–9 still landed at score 5 with the mid tier
    /// boost (+4); binary texture head now scores ≥11 (see <see cref="ScoreMemberProbe"/>).
    /// </summary>
    private const int MemberMinScore = 8;

    /// <summary>
    /// Retail NameCRC paths for leaf-only opens. HostCdPath normalizes to leaf (ISO root
    /// bias); STREE0 indexes full lowercased backslash paths under data\textures\….
    /// Telemetry-grounded prefixes from fontindex / *PR material lists (not invented).
    /// </summary>
    private static readonly string[] MemberLeafPrefixes =
    {
        "data\\textures\\onscreengraphics\\fonts\\",
        "data\\textures\\onscreengraphics\\loadingscreen\\",
        "data\\textures\\onscreengraphics\\frontend\\",
        "data\\textures\\onscreengraphics\\",
        "data\\textures\\frontend\\controls\\",
        "data\\textures\\frontend\\",
        "data\\textures\\environment\\",
        "data\\textures\\particle\\",
        "data\\textures\\hud\\",
        "data\\textures\\colorswatches\\",
        "data\\fonts\\",
        "data\\text\\",
        "data\\sound\\",
        "data\\materials\\",
        "data\\history\\",
        "data\\memcard\\",
        // PL-032h: post-swooshes precache autolists (begin.pcl / begin0.tre / commontree.pcl)
        "data\\precachelists\\autolists\\",
        "data\\precachelists\\",
        // GAME.TXT frontend memorycard spine (begin → bootup → lang → mainmenu → …)
        "data\\levels\\frontend\\memorycard\\begin\\",
        "data\\levels\\frontend\\memorycard\\bootupsequence\\",
        "data\\levels\\frontend\\memorycard\\languageselect\\",
        "data\\levels\\frontend\\memorycard\\mainmenu\\",
        "data\\levels\\frontend\\memorycard\\newgame\\",
        "data\\levels\\frontend\\memorycard\\loadgame\\",
        "data\\levels\\frontend\\memorycard\\initialspacecheck\\",
        "data\\levels\\frontend\\memorycard\\",
        "data\\levels\\frontend\\legalscreen\\",
        "data\\levels\\frontend\\intro\\",
        "data\\levels\\frontend\\attractmode\\",
        "data\\levels\\frontend\\menuscreenone\\",
        "data\\levels\\frontend\\options\\",
        "data\\levels\\frontend\\progscan\\",
        "data\\levels\\frontend\\",
        "data\\actors\\frontend\\normalmenuwidget\\",
        "data\\actors\\frontend\\",
        "data\\actors\\widgets\\",
        // PL-032j: *PR deity / begin mesh leaves
        "data\\actors\\deity\\deitynofade\\",
        "data\\actors\\deity\\",
        "data\\actors\\devices\\creatureshadow\\",
        "data\\actors\\devices\\",
        "data\\swooshes\\",
    };

    public void Reset()
    {
        _pathPatched = _mallocPlanted = _cdIoPlanted = false;
        _versionReplants = _nullPathEscapes = _pathNormEscapes = _mallocReplants = 0;
        _hookReplants = _freelistEscapes = _searchPathFixes = _searchPlants = 0;
        _stackRescues = _cdIoReplants = _streamMapEscapes = 0;
        _hostOpens = _hostReads = _hostCloses = _hostSeeks = 0;
        _hostMemberOpens = _hostMemberFail = 0;
        _hostBadArgsRecovered = _hostNestedTreStubs = 0;
        _streamMapProbes = _streamMapPlants = 0;
        _streamMapLookupHits = 0;
        _padInjectPulses = 0;
        _floatExpandHits = _floatExpandEscapes = 0;
        _objCtorHostCompletes = 0;
        _objCtorCodeRepairs = 0;
        _idTableSearchEscapes = 0;
        _stickyFieldClearObj = 0;
        _stickyCtorThis = 0;
        _swooshesLoaded = false;
        _beginPclLoaded = false;
        _begin0TreLoaded = false;
        _beginMtfOpened = false;
        _begin0TrePackBase = 0;
        _begin0TreTocScrubs = 0;
        _beginMtfStreamMapInjects = 0;
        _postPrResidualEscapes = 0;
        _postPrResidualHits = 0;
        _nameSearchForceMatches = 0;
        _recentOpenPathCount = 0;
        Array.Clear(_recentOpenPaths);
        _pendingOpenPath = "";
        _pathOpenHandles.Clear();
        _pathPayloadCache.Clear();
        _pathObjectStubs.Clear();
        _readyFlagWaitHits = _readyFlagForceSets = 0;
        _tocProbeDone = false;
        _streamMapTable = _streamMapCount = _streamMapObj = 0;
        _hostFds.Clear();
        _hostFdSizes.Clear();
        _recentBumpCount = 0;
        Array.Clear(_recentBumpBases);
        Array.Clear(_recentBumpSizes);
        _lastBumpBase = _lastBumpSize = 0;
        _lastRecoveredBuf = _lastRecoveredSize = 0;
        _packageBufCount = 0;
        Array.Clear(_packageBufRing);
        Array.Clear(_packageSzRing);
        _stree0DiscByteOff = 0;
        _stree0Size = 0;
        _streeMemberByCrc = null;
        _streeMemberIndexCount = 0;
        try { _isoVol?.Disc?.Dispose(); } catch { }
        _isoVol = null; _isoVolPath = null;
    }

    public void OnDiscMounted(Ps2System sys)
    {
        Reset();
        if (!SkipPreferIopRp && sys.Hle?.Sony?.RealRpc != null)
            sys.Hle.Sony.RealRpc.PreferIopRpGetVersion = true;
        if (!SkipVersionPlant)
            PlantIopRpVersion(sys);
        PlantCrtMallocTable(sys);
        PlantStringHeapHook(sys);
        // Host CD stubs ready; live vtable wired after STREE0 TOC CdReads (see Step).
        PlantHostCdStubs(sys);
        EnsureHostActorVtable(sys);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine("[VEXX] OnDiscMounted: IOPRP252 + CRT/string heap; CD I/O stubs planted");
    }

    public void OnHostPresent(Ps2System sys) => _ = sys;

    public void Step(Ps2System sys)
    {
        if (!SkipVersionPlant && !VersionCellsOk(sys)) { PlantIopRpVersion(sys); _versionReplants++; }

        if (!_mallocPlanted || sys.Memory.Read32(CrtMallocSlot) == 0)
        {
            PlantCrtMallocTable(sys);
            _mallocPlanted = true;
            _mallocReplants++;
        }

        if (sys.Memory.Read32(StringAllocHook) != MallocStub)
        {
            PlantStringHeapHook(sys);
            _hookReplants++;
        }

        if (!_pathPatched || !PathStubActive(sys, PathBasenameA))
        {
            PatchNullPathBasename(sys);
            _pathPatched = true;
        }

        uint pc = (uint)(sys.EE.PC & 0x1FFFFFFFu);

        // Wire live CD I/O vtable after STREE0 TOC CdReads (~89 sectors) so multi-chunk
        // libcdvd assembly is not interrupted; then host-serve secondary .TRE opens.
        if (sys.Cdvd.SectorsRead >= 80UL
            && (!_cdIoPlanted || sys.Memory.Read32(CdIoVtableBase) != HostCdStubOpen))
        {
            PlantCdIoVtable(sys);
            _cdIoReplants++;
        }

        // SearchFile path slide/plant + TRE size cap (TOC only, not full ~1GB).
        // Only the IOP arg buffer (sceCdlFILE) — never the EE SIF packet at 0x3F7B00
        // (wave-4: sliding the packet produced "E.TXT;1" / "EE0.TRE;1" garbage).
        if (sys.Scheduler.MasterCycles >= 500_000UL)
        {
            uint buf = SearchFileArgBuf;
            if (MaybeFixSearchFilePathLayout(sys, buf)) _searchPathFixes++;
            if (MaybePlantSearchFileResult(sys, buf)) _searchPlants++;
            if (MaybeCapTreSearchSize(sys, buf)) _searchPlants++;
        }

        // Wave-5: host-serve CD I/O once vtable is wired (STREE0 stream-map open/read).
        if (_cdIoPlanted && MaybeHostCdIo(sys, pc))
            return;

        if ((pc is >= 0x0014619C and <= 0x001461BC) || (pc is >= 0x0014625C and <= 0x0014627C))
        {
            if (sys.EE.GetGpr(16).Lo == 0)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });
                sys.EE.PC = sys.EE.GetGpr(31).Lo;
                _nullPathEscapes++;
            }
        }

        if (pc is >= PathNormalizeLoop and <= PathNormalizeAfterLoop)
        {
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
            if (sp >= 0x1000 && sp + 0x40 < SystemMemory.RDRAM_SIZE)
            {
                uint pathPtr = sys.Memory.Read32(sp + 0x38);
                if (pathPtr < 0x10000u)
                {
                    sys.Memory.Write32(sp + 0x38, EmptyStringSentinel);
                    sys.EE.SetGpr(7, new EmotionEngine.Gpr128 { Lo = EmptyStringSentinel });
                    sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = 0 });
                    sys.EE.PC = PathNormalizeAfterLoop;
                    _pathNormEscapes++;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                        Console.Error.WriteLine(
                            $"[VEXX] path-normalize escape #{_pathNormEscapes} wasPtr=0x{pathPtr:X} cyc={sys.Scheduler.MasterCycles}");
                }
            }
        }

        // Early freelist escape (pre-pad) corrupts CRT and open-bus thrash (binds=0).
        // Wave-3: allow up to FreelistMaxBump for STREE TOC / stream tables (not full 1GB).
        // PL-032g: after STREE members bind, freelist walk thrash @0x1CE1x0 (post-swooshes
        // size=0x3F0, s6 walks→1000+) burns 10M+ cycles before property float-expand —
        // lower walk threshold so host-bump lands sooner.
        // PL-032h: second walker @0x1CE2B0 (PC≈0x1CE314) after begin0.tre — same host-bump.
        if (sys.Scheduler.MasterCycles >= FreelistEscapeMinCycles
            && (pc is >= FreelistWalkLo and <= FreelistWalkHi
                || pc is >= Freelist2WalkLo and <= Freelist2WalkHi))
        {
            bool walker2 = pc is >= Freelist2WalkLo and <= Freelist2WalkHi;
            long walks = walker2
                ? (20L - (long)(int)sys.EE.GetGpr(11).Lo) // t3 counts down from 20
                : (long)sys.EE.GetGpr(22).Lo;             // s6 walk count (walker1)
            // Walker2: size is a2 (r6) at entry, kept in a2 through the loop.
            uint size = walker2
                ? (uint)sys.EE.GetGpr(6).Lo
                : (uint)sys.EE.GetGpr(16).Lo;
            int walkGate = (_hostMemberOpens >= 30 || _floatExpandEscapes > 0) ? 8
                : (_hostMemberOpens >= 20 || _swooshesLoaded) ? 32 : 64;
            // Walker2 only has ~20 outer buckets — escape as soon as post-assets and looping.
            if (walker2 && (_floatExpandEscapes > 0 || _hostMemberOpens >= 30))
                walkGate = 1;
            if (walks > walkGate || (walker2 && (_floatExpandEscapes > 0 || _hostMemberOpens >= 30)))
            {
                if (size == 0)
                    size = 16;
                if (size > 0 && size < FreelistMaxBump)
                {
                    uint mem = HostBumpAlloc(sys, size + 64);
                    if (mem != 0)
                    {
                        NoteBumpBase(mem, size + 64);
                        if (walker2)
                        {
                            // 0x1CE3E8: sw t4,0(s1); sw t5,0(s0) — block + cursor.
                            sys.EE.SetGpr(12, new EmotionEngine.Gpr128 { Lo = mem });      // t4
                            sys.EE.SetGpr(13, new EmotionEngine.Gpr128 { Lo = mem + 32 }); // t5
                            sys.EE.PC = Freelist2SuccessStore;
                        }
                        else
                        {
                            sys.EE.SetGpr(20, new EmotionEngine.Gpr128 { Lo = mem });
                            sys.EE.SetGpr(21, new EmotionEngine.Gpr128 { Lo = mem + 32 });
                            sys.EE.PC = FreelistSuccessStore;
                        }
                        _freelistEscapes++;
                        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                            && (_freelistEscapes <= 16 || _freelistEscapes % 32 == 0))
                            Console.Error.WriteLine(
                                $"[VEXX] freelist bump #{_freelistEscapes} w{(walker2 ? 2 : 1)} " +
                                $"size=0x{size:X} mem=0x{mem:X} walks={walks} cyc={sys.Scheduler.MasterCycles}");
                    }
                }
                else
                {
                    if (walker2)
                    {
                        sys.EE.SetGpr(12, new EmotionEngine.Gpr128 { Lo = 0 });
                        sys.EE.SetGpr(13, new EmotionEngine.Gpr128 { Lo = 0 });
                        sys.EE.PC = Freelist2SuccessStore;
                    }
                    else
                    {
                        sys.EE.SetGpr(20, new EmotionEngine.Gpr128 { Lo = 0 });
                        sys.EE.SetGpr(21, new EmotionEngine.Gpr128 { Lo = 0 });
                        sys.EE.PC = FreelistSuccessStore;
                    }
                    _freelistEscapes++;
                    if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _freelistEscapes <= 16)
                        Console.Error.WriteLine(
                            $"[VEXX] freelist fail w{(walker2 ? 2 : 1)} size=0x{size:X} cyc={sys.Scheduler.MasterCycles}");
                }
            }
        }

        // PL-032g: packed-byte→float expand circular-list hang (PC≈0x330E54 after swooshes).
        if (MaybeEscapeFloatExpandList(sys, pc))
            return;

        // PL-032h: host-complete derived ctor 0x3402E0 (jal 0x2EE7F0 corrupts epilogue words).
        // Also re-plant AE00003C/DFBF0010 if anything still stomps them.
        if (MaybeHostCompleteObjectCtor(sys, pc))
            return;

        // PL-032g/h: residual null-s0 field stores if host-complete missed a mid-epilogue land.
        if (MaybeRescueNullObjectFieldClear(sys, pc))
            return;

        // PL-032h: id-table search thrash @0x32CF18 after ctor (count at +0xA4 huge/never-match).
        if (MaybeEscapeIdTableSearch(sys, pc))
            return;

        // PL-032i: ready-flag spin-wait @0x369790 / getter @0x35E190 after begin.pcl.
        if (MaybeForceReadyFlag(sys, pc))
            return;

        // PL-032n: path name-search for a just-opened host path → force-found stub object.
        if (MaybeForceNameSearchMatch(sys, pc))
            return;

        // PL-032m: name-search strcmp @0x1CF410 — bad a0/a1 → UnknownMmioRead; host-complete.
        if (MaybeHostCompleteNameStrcmp(sys, pc))
            return;

        // PL-032m: name-search loop @0x2243A0 — skip bad object slots; do not abort the walk.
        if (MaybeSkipBadNameSearchSlot(sys, pc))
            return;

        // PL-032p: null-vtable thunk band 0x11C170.. (residual 0x11C200) after FORCE-MATCH.
        if (MaybeEscapeNullVtableThunk(sys, pc))
            return;

        // PL-032p/q: SIF RPC bind-retry busy-wait after widget.atr (PC≈0x1B1330, 1M-spin).
        if (MaybeEscapeSifRpcBindWait(sys, pc))
            return;

        // PL-032q: AAAIOP server-ready poll (client+0x24) after bind 0x54323.
        if (MaybeEscapeAaaIopServerWait(sys, pc))
            return;

        // PL-032k/l: residual thrash in 0x224xxx band after screenproxy.atr (last resort).
        if (MaybeEscapePostPrResidual(sys, pc))
            return;

        // Null / planted stream-table hash walk.
        if (sys.Scheduler.MasterCycles >= FreelistEscapeMinCycles
            && pc is >= StreamMapLookupLo and <= StreamMapLookupHi)
        {
            _streamMapLookupHits++;
            MaybeEscapeNullStreamMap(sys, pc);
        }
        else
            _streamMapLookupHits = 0;

        // Wave-5: after STREE TOC CdReads (cdvd≥50), probe/build stream map so asset
        // lookups leave null-table thrash and Soft-GS can receive real prims.
        if (!_tocProbeDone && sys.Cdvd.SectorsRead >= 50UL
            && sys.Scheduler.MasterCycles >= 4_000_000UL)
            MaybeFinishStreamMap(sys);

        // Stack death residual: PC lands in path ASCII (STREE0.TRE / GAME.TXT) as code.
        if (sys.Scheduler.MasterCycles >= FreelistEscapeMinCycles && LooksLikePathAsciiPc(sys, pc))
            MaybeRescueStackDeath(sys, pc);

        // PL-017: pad inject after STREE0 / title path is live. Dense START/CROSS edges
        // so Press-START / confirm readers can leave logo/title surface (T2 INTERACTIVE).
        // Gate: real disc sectors OR Soft-GS title chrome — never pulse during pure CRT thrash.
        MaybeInjectTitlePad(sys);
    }

    /// <summary>
    /// PL-032g: leave infinite/corrupt circular list in float-expand (0x330E34..0x331074).
    /// Retail walks <c>s0 = *s0</c> until <c>s0 == (s5+4)</c> (list head sentinel). Null head,
    /// self-loop, or multi-slice thrash after swooshes blocks begin.mtf/ati opens forever.
    /// Force s0=sentinel and jump to success epilogue (v0=1) — skip remaining float stores.
    /// </summary>
    private bool MaybeEscapeFloatExpandList(Ps2System sys, uint pc)
    {
        if (pc is < FloatExpandLo or > FloatExpandHi)
        {
            // Only reset once we have fully left the band (avoid wiping mid-body samples).
            if (pc is < FloatExpandLo - 0x40 or > FloatExpandEpilogue + 0x40)
                _floatExpandHits = 0;
            return false;
        }

        _floatExpandHits++;
        uint s0 = (uint)sys.EE.GetGpr(16).Lo;
        uint s5 = (uint)sys.EE.GetGpr(21).Lo;
        uint phys0 = s0 & 0x1FFFFFFFu;
        bool badNode = s0 == 0 || phys0 < 0x1000u || phys0 >= SystemMemory.RDRAM_SIZE;
        if (!badNode)
        {
            try
            {
                uint next = sys.Memory.Read32(phys0);
                // Self-loop never reaches sentinel; also catch next→invalid.
                if (next == s0)
                    badNode = true;
                else
                {
                    uint nPhys = next & 0x1FFFFFFFu;
                    if (next != 0 && (nPhys < 0x1000u || nPhys >= SystemMemory.RDRAM_SIZE))
                        badNode = true;
                }
            }
            catch { badNode = true; }
        }

        // Post-swooshes: escape sooner — diag shows multi-10M stuck with lit plateau.
        int hitGate = (_swooshesLoaded || _hostMemberOpens >= 28) ? 8 : FloatExpandMaxStepHits;
        if (!badNode && _floatExpandHits < hitGate)
            return false;

        // Sentinel = s5+4 (list head address), also stashed at sp+0xC9C by 0x331400.
        uint sentinel = s5 + 4;
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
        if (sp is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0xD00u)
        {
            uint at = sys.Memory.Read32(sp + 0xC9C);
            uint atPhys = at & 0x1FFFFFFFu;
            if (at != 0 && atPhys is >= 0x1000 and < SystemMemory.RDRAM_SIZE)
                sentinel = at;
        }

        // Land on bne s0,v0 with both equal → fall through to epilogue with live SP/RA.
        // Jumping straight to 0x331078 with a host-fabricated v0=1 left ra/stack wrong and
        // crashed into UnknownOpcode @0x34030C (diag PL-032g2).
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = sentinel }); // s0
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = sentinel });  // v0 (compare equal)
        // Keep sp+0xC9C coherent for any re-entry through 0x33106C.
        if (sp is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0xD00u)
            sys.Memory.Write32(sp + 0xC9C, sentinel);
        sys.EE.PC = FloatExpandListCompare;
        _floatExpandEscapes++;
        _floatExpandHits = 0;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && _floatExpandEscapes <= 12)
            Console.Error.WriteLine(
                $"[VEXX] float-expand list escape #{_floatExpandEscapes} wasS0=0x{s0:X} " +
                $"sentinel=0x{sentinel:X} s5=0x{s5:X} bad={(badNode ? 1 : 0)} " +
                $"cyc={sys.Scheduler.MasterCycles}");
        return true;
    }

    /// <summary>
    /// PL-032g residual / PL-032h assist counters. Sticky this-ptr survives the mid-ctor loop
    /// after <c>jal 0x2EE7F0</c> corrupts <c>ld ra</c> and <c>lq s0</c> restores pre-delay s0=0.
    /// </summary>
    private int _objCtorHostCompletes;
    private int _objCtorCodeRepairs;
    private int _idTableSearchEscapes;
    private uint _stickyFieldClearObj;
    private uint _stickyCtorThis;
    public const uint ObjFieldClearStoreLo = 0x00340300;
    public const uint ObjFieldClearStoreHi = 0x0034030C;
    public const uint ObjFieldClearVtable = ObjDerivedVtable;

    /// <summary>
    /// Retail words for <c>0x3402E0..0x34031C</c> (derived ctor). PL-032h: <c>jal 0x2EE7F0</c>
    /// stomps 0x34030C/0x340310 — re-plant whenever they diverge so a natural re-entry can finish.
    /// </summary>
    private static readonly uint[] ObjCtorRetailWords =
    {
        0x27BDFFE0, // 0x3402E0 addiu sp,sp,-32
        0xFFBF0010, // 0x3402E4 sd ra,16(sp)
        0x7FB00000, // 0x3402E8 sq s0,0(sp)
        0x0C0BB9FC, // 0x3402EC jal 0x2EE7F0
        0x0080802D, // 0x3402F0 move s0,a0
        0x3C03003F, // 0x3402F4 lui v1,0x3F
        0x0200102D, // 0x3402F8 move v0,s0
        0x24635690, // 0x3402FC addiu v1,v1,0x5690
        0xAE03001C, // 0x340300 sw v1,0x1C(s0)
        0xAE000034, // 0x340304 sw zero,0x34(s0)
        0xAE000038, // 0x340308 sw zero,0x38(s0)
        0xAE00003C, // 0x34030C sw zero,0x3C(s0)
        0xDFBF0010, // 0x340310 ld ra,16(sp)
        0x7BB00000, // 0x340314 lq s0,0(sp)
        0x03E00008, // 0x340318 jr ra
        0x27BD0020, // 0x34031C addiu sp,sp,32
    };

    /// <summary>
    /// PL-032h: host-complete derived object ctor at <c>0x3402E0</c>. Retail jals base init
    /// <c>0x2EE7F0</c> (a0=this, a1=name/side obj, a2=opt string) then stores derived vtable
    /// 0x3F5690 and zeros +0x34/38/3C via s0. Live PCBREAK: during the jal, epilogue words at
    /// 0x34030C/0x340310 are overwritten so ld ra never runs → jr ra loops to 0x3402F4 with
    /// lq-restored s0=0 → UnknownOpcode storm; begin.mtf never opens. Host-init base fields +
    /// derived stores and return to the caller's ra (never enter the corrupted epilogue).
    /// </summary>
    private bool MaybeHostCompleteObjectCtor(Ps2System sys, uint pc)
    {
        // Only arm after frontend assets bind — pre-swooshes must not host-fake this ctor.
        bool postAssets = _swooshesLoaded || _floatExpandEscapes > 0 || _hostMemberOpens >= 28;
        if (!postAssets)
            return false;

        // Re-plant stomped epilogue words whenever the band is live.
        MaybeRepairObjectCtorCode(sys);

        bool inDerivedCtor = pc is >= ObjCtorEntry and < ObjCtorEnd;
        bool inBaseInit = pc is >= ObjBaseInitLo and <= ObjBaseInitHi;
        if (!inDerivedCtor && !inBaseInit)
            return false;

        // Capture this-ptr early (entry / jal delay) before anything clobbers a0.
        if (pc is >= ObjCtorEntry and <= ObjCtorJalBase + 4)
        {
            uint a0 = (uint)sys.EE.GetGpr(4).Lo;
            uint a0p = a0 & 0x1FFFFFFFu;
            if (a0 != 0 && a0p is >= 0x00100000u and < SystemMemory.RDRAM_SIZE - 0x40u)
                _stickyCtorThis = a0;
        }

        // Inside base init after derived jal: finish from sticky this + live ra on stack.
        if (inBaseInit)
        {
            uint self = ResolveCtorThis(sys);
            if (self == 0)
                return false;
            uint ra = (uint)sys.EE.GetGpr(31).Lo; // jal from 0x3402EC sets ra=0x3402F4
            // Prefer outer caller ra if the base frame already saved it (sd ra,32(sp) at entry).
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
            if (sp is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0x40u)
            {
                // Base frame is -64; derived frame under it has outer ra at derived_sp+16.
                // After base prolog sp_base = derived_sp - 64; outer ra at sp_base+64+16.
                uint outerRa = sys.Memory.Read32(sp + 0x50);
                uint outerPhys = outerRa & 0x1FFFFFFFu;
                if (outerPhys is >= 0x00100000u and < 0x00580000u && (outerRa & 3) == 0
                    && outerPhys != ObjCtorPostJal)
                    ra = outerRa;
            }
            HostInitObjectFields(sys, self, keepSideFromA1: true);
            return FinishObjectCtor(sys, self, ra, fromBase: true);
        }

        // Derived ctor body / mid-loop after corruption.
        {
            uint self = ResolveCtorThis(sys);
            if (self == 0)
            {
                // Entry with null a0 — allocate so callers that skipped the beq still proceed.
                self = HostBumpAlloc(sys, 0x80);
                if (self == 0)
                    return false;
                NoteBumpBase(self, 0x80);
                _stickyCtorThis = self;
            }

            uint ra;
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
            if (pc == ObjCtorEntry)
            {
                // Prolog not yet run — ra is the outer caller (0x3402C4).
                ra = (uint)sys.EE.GetGpr(31).Lo;
                HostInitObjectFields(sys, self, keepSideFromA1: true);
                return FinishObjectCtor(sys, self, ra, fromBase: false);
            }

            // Mid-function: try ld ra from derived frame (sp+16); fall back to live ra / sticky.
            ra = 0;
            if (sp is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0x20u)
            {
                uint stacked = sys.Memory.Read32(sp + 0x10);
                uint spPhys = stacked & 0x1FFFFFFFu;
                if (spPhys is >= 0x00100000u and < 0x00580000u && (stacked & 3) == 0
                    && spPhys is < ObjCtorEntry or >= ObjCtorEnd)
                    ra = stacked;
            }
            if (ra == 0)
            {
                uint live = (uint)sys.EE.GetGpr(31).Lo;
                uint livePhys = live & 0x1FFFFFFFu;
                // ra==0x3402F4 means we are looping after jal — outer is under the frame.
                if (livePhys == ObjCtorPostJal && sp is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0x20u)
                {
                    // If prolog ran, outer ra is at sp+16; if base also ran, sp may be deeper.
                    foreach (uint off in new uint[] { 0x10, 0x50, 0x30 })
                    {
                        if (sp + off + 4 >= SystemMemory.RDRAM_SIZE) continue;
                        uint cand = sys.Memory.Read32(sp + off);
                        uint cp = cand & 0x1FFFFFFFu;
                        if (cp is >= 0x00100000u and < 0x00580000u && (cand & 3) == 0
                            && cp is < ObjCtorEntry or >= ObjCtorEnd
                            && cp != ObjCtorPostJal)
                        {
                            ra = cand;
                            break;
                        }
                    }
                }
                else if (livePhys is >= 0x00100000u and < 0x00580000u
                         && livePhys is < ObjCtorEntry or >= ObjCtorEnd)
                    ra = live;
            }
            if (ra == 0)
                ra = 0x003402C4; // sole retail call site return (after jal 0x3402E0)

            HostInitObjectFields(sys, self, keepSideFromA1: true);
            // If prolog ran, pop the derived frame so caller stack is intact.
            if (pc is > ObjCtorEntry && sp is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0x20u
                && sp + 32 < SystemMemory.RDRAM_SIZE)
            {
                // Only pop when sp looks like the derived -32 frame (high EE stack band).
                if (sp is >= 0x01F00000u)
                    sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = sp + 32 });
            }
            return FinishObjectCtor(sys, self, ra, fromBase: false);
        }
    }

    private uint ResolveCtorThis(Ps2System sys)
    {
        foreach (uint c in new[]
                 {
                     _stickyCtorThis,
                     (uint)sys.EE.GetGpr(4).Lo,  // a0
                     (uint)sys.EE.GetGpr(16).Lo, // s0
                     (uint)sys.EE.GetGpr(2).Lo,  // v0
                     _stickyFieldClearObj,
                 })
        {
            uint p = c & 0x1FFFFFFFu;
            if (c != 0 && p is >= 0x00100000u and < SystemMemory.RDRAM_SIZE - 0x40u)
            {
                _stickyCtorThis = c;
                return c;
            }
        }
        return 0;
    }

    /// <summary>
    /// Minimal safe stand-in for <c>0x2EE7F0</c> + derived stores. Sets base/derived vtables,
    /// zeros the field-clear band, and (when a1 looks live) plants a zeroed side block at +0x30
    /// so later actor mesh binds are not hard-null. Skips the virtual-call / string path that
    /// live-stomps the epilogue at 0x34030C.
    /// </summary>
    private void HostInitObjectFields(Ps2System sys, uint self, bool keepSideFromA1)
    {
        uint phys = self & 0x1FFFFFFFu;
        if (phys + 0x40u > SystemMemory.RDRAM_SIZE)
            return;

        // Base-ish fields (0x2EC720 / 0x2EE7F0 light path).
        if (sys.Memory.Read32(phys + 0x1C) == 0)
            sys.Memory.Write32(phys + 0x1C, ObjBaseVtable);
        // Derived vtable wins (same as post-jal stores at 0x340300).
        sys.Memory.Write32(phys + 0x1C, ObjDerivedVtable);
        sys.Memory.Write32(phys + 0x20, 0);
        sys.Memory.Write32(phys + 0x2C, 0xFFFFFFFFu);
        sys.Memory.Write32(phys + 0x34, 0);
        sys.Memory.Write32(phys + 0x38, 0);
        sys.Memory.Write32(phys + 0x3C, 0);

        if (keepSideFromA1)
        {
            uint a1 = (uint)sys.EE.GetGpr(5).Lo;
            uint a1p = a1 & 0x1FFFFFFFu;
            if (a1 != 0 && a1p is >= 0x00100000u and < SystemMemory.RDRAM_SIZE)
                sys.Memory.Write32(phys + 0x28, a1);

            uint side = sys.Memory.Read32(phys + 0x30);
            uint sidePhys = side & 0x1FFFFFFFu;
            if (side == 0 || sidePhys < 0x00100000u || sidePhys >= SystemMemory.RDRAM_SIZE - 0x40u)
            {
                // Only allocate the heavy side block when the retail a1 path would (a1 != 0).
                if (a1 != 0 && a1p is >= 0x00100000u and < SystemMemory.RDRAM_SIZE)
                {
                    side = HostBumpAlloc(sys, ObjBaseSideAllocSize);
                    if (side != 0)
                    {
                        NoteBumpBase(side, ObjBaseSideAllocSize);
                        // Zero a useful head so later readers do not walk garbage.
                        uint n = Math.Min(ObjBaseSideAllocSize, 0x100u);
                        for (uint i = 0; i < n; i += 4)
                            sys.Memory.Write32(side + i, 0);
                        sys.Memory.Write32(phys + 0x30, side);
                    }
                }
            }
        }
    }

    private bool FinishObjectCtor(Ps2System sys, uint self, uint ra, bool fromBase)
    {
        uint raPhys = ra & 0x1FFFFFFFu;
        if (raPhys < 0x00100000u || raPhys >= 0x00580000u || (ra & 3) != 0
            || (raPhys is >= ObjCtorEntry and < ObjCtorEnd))
            ra = 0x003402C4;

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = self });  // v0 = this
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = self }); // s0 = this
        sys.EE.SetGpr(4, new EmotionEngine.Gpr128 { Lo = self });  // a0 coherent
        sys.EE.PC = ra;
        _stickyCtorThis = self;
        _stickyFieldClearObj = self;
        _objCtorHostCompletes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && _objCtorHostCompletes <= 16)
            Console.Error.WriteLine(
                $"[VEXX] object-ctor host-complete #{_objCtorHostCompletes} this=0x{self:X} " +
                $"ra=0x{ra:X} base={(fromBase ? 1 : 0)} cyc={sys.Scheduler.MasterCycles}");
        return true;
    }

    private void MaybeRepairObjectCtorCode(Ps2System sys)
    {
        // Only re-plant when the known stomp sites diverge (AE00003C / DFBF0010). Full-band
        // compare false-fired at ~9M before any jal 0x2EE7F0 (pre-swooshes) and is wasteful.
        uint w30c = sys.Memory.Read32(0x0034030C);
        uint w310 = sys.Memory.Read32(0x00340310);
        if (w30c == 0xAE00003Cu && w310 == 0xDFBF0010u)
            return;

        for (int i = 0; i < ObjCtorRetailWords.Length; i++)
            sys.Memory.Write32(ObjCtorEntry + (uint)(i * 4), ObjCtorRetailWords[i]);
        _objCtorCodeRepairs++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && _objCtorCodeRepairs <= 8)
            Console.Error.WriteLine(
                $"[VEXX] object-ctor code repair #{_objCtorCodeRepairs} " +
                $"was30C=0x{w30c:X} was310=0x{w310:X} cyc={sys.Scheduler.MasterCycles}");
    }

    /// <summary>
    /// PL-032g residual: if we still land on field stores with a bad s0 (host-complete missed),
    /// rehome to sticky this and finish the ctor via <see cref="FinishObjectCtor"/>.
    /// </summary>
    private bool MaybeRescueNullObjectFieldClear(Ps2System sys, uint pc)
    {
        if (pc is < ObjFieldClearStoreLo or > ObjFieldClearStoreHi)
        {
            if (pc is < ObjCtorEntry or > ObjCtorEnd)
                _stickyFieldClearObj = 0;
            return false;
        }

        // Prefer full host-complete (repairs code + returns) over per-store rehome.
        return MaybeHostCompleteObjectCtor(sys, pc);
    }

    /// <summary>
    /// PL-032h: leave infinite id-table scan at <c>0x32CEE0..0x32CF28</c>.
    /// Retail: <c>t0=*(s0+0xA4)</c> count; loop until <c>a2&gt;=t0</c>, then <c>slti t0,40</c>.
    /// Live residual used s0≈swooshes payload (0x4464F0) with count=0x1D102E4 — clamping and
    /// continuing mutated that buffer and later stack-died into path ASCII. When count is absurd
    /// (&gt;retail cap) force fail-return (v0=0) via the natural epilogue instead of append.
    /// </summary>
    private bool MaybeEscapeIdTableSearch(Ps2System sys, uint pc)
    {
        if (pc is < IdTableSearchLo or > IdTableSearchHi)
            return false;
        if (!_swooshesLoaded && _floatExpandEscapes == 0)
            return false;

        uint s0 = (uint)sys.EE.GetGpr(16).Lo;
        uint s0p = s0 & 0x1FFFFFFFu;
        if (s0 == 0 || s0p + 0xB0u > SystemMemory.RDRAM_SIZE)
            return false;

        uint count = sys.Memory.Read32(s0p + 0xA4);
        uint a2 = (uint)sys.EE.GetGpr(6).Lo; // loop index
        bool absurdCount = count > (uint)IdTableRetailCap;
        bool longScan = a2 >= (uint)IdTableRetailCap;
        if (!absurdCount && !longScan)
            return false;

        // Fail-return: v0=0, restore s0/ra from frame if present, else jr live ra.
        // Epilogue at 0x32CFCC: ld ra,16(sp); lq s0,0(sp); jr ra; addiu sp,32.
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
        uint ra = (uint)sys.EE.GetGpr(31).Lo;
        if (sp is >= 0x01000000u and < SystemMemory.RDRAM_SIZE - 0x20u)
        {
            uint stackedRa = sys.Memory.Read32(sp + 0x10);
            uint rp = stackedRa & 0x1FFFFFFFu;
            if (rp is >= 0x00100000u and < 0x00580000u && (stackedRa & 3) == 0
                && rp is < IdTableSearchLo or > 0x0032CFD8u)
                ra = stackedRa;
            uint stackedS0 = sys.Memory.Read32(sp + 0x00);
            // lq is 16-byte; low word is enough for pointer restore.
            sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = stackedS0 });
            sys.EE.SetGpr(29, new EmotionEngine.Gpr128 { Lo = sp + 32 });
        }
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0 = fail
        sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = 0 }); // v1 = not-found
        uint raPhys = ra & 0x1FFFFFFFu;
        if (raPhys < 0x00100000u || raPhys >= 0x00580000u || (ra & 3) != 0
            || (raPhys is >= IdTableSearchLo and <= 0x0032CFD8u))
        {
            // Land on retail epilogue with v0=0 so ld/lq run if frame intact.
            sys.EE.PC = 0x0032CFCC;
        }
        else
            sys.EE.PC = ra;

        _idTableSearchEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && _idTableSearchEscapes <= 12)
            Console.Error.WriteLine(
                $"[VEXX] id-table search FAIL-escape #{_idTableSearchEscapes} s0=0x{s0:X} " +
                $"wasCount=0x{count:X} a2=0x{a2:X} ra=0x{ra:X} cyc={sys.Scheduler.MasterCycles}");
        return true;
    }

    /// <summary>
    /// PL-032i: leave spin-wait on frontend ready flag after begin.pcl bind.
    /// Retail getter <c>0x35E190</c> returns <c>*(*(0x4311C0)+0x2E8)</c>; wait <c>0x369790</c>
    /// loops until non-zero. When the async start path never stores 1 (pcl full-read was
    /// rejected / worker not kicked), force the cell so the wait falls through.
    /// </summary>
    private bool MaybeForceReadyFlag(Ps2System sys, uint pc)
    {
        bool inGetter = pc == ReadyFlagGetter || pc == ReadyFlagGetter + 4;
        bool inWait = pc is >= ReadyFlagWaitLo and <= ReadyFlagWaitHi
            || pc is >= ReadyFlagPollAltLo and <= ReadyFlagPollAltHi;
        if (!inGetter && !inWait)
        {
            if (pc is < ReadyFlagWaitLo - 0x40 or > ReadyFlagWaitHi + 0x40)
                if (pc is < ReadyFlagGetter - 0x40 or > ReadyFlagGetter + 0x40)
                    if (pc is < ReadyFlagPollAltLo - 0x40 or > ReadyFlagPollAltHi + 0x40)
                        _readyFlagWaitHits = 0;
            return false;
        }

        // Only arm after precache list bind (or late post-swooshes member flood).
        if (!_beginPclLoaded && !(_swooshesLoaded && _hostMemberOpens >= 30))
            return false;

        _readyFlagWaitHits++;
        int gate = _beginPclLoaded ? ReadyFlagWaitMaxHits : ReadyFlagWaitMaxHits * 2;
        if (_readyFlagWaitHits < gate)
            return false;

        uint obj = 0;
        try
        {
            obj = sys.Memory.Read32(ReadyFlagGlobalPtr);
        }
        catch { return false; }
        uint objPhys = obj & 0x1FFFFFFFu;
        if (obj == 0 || objPhys < 0x00100000u || objPhys + ReadyFlagObjOff + 4 > SystemMemory.RDRAM_SIZE)
            return false;

        uint cur = sys.Memory.Read32(objPhys + ReadyFlagObjOff);
        if (cur != 0)
        {
            // Flag already set — just return non-zero from getter if we landed mid-poll.
            if (inGetter)
            {
                sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = cur });
                sys.EE.PC = (uint)sys.EE.GetGpr(31).Lo;
                _readyFlagWaitHits = 0;
                return true;
            }
            _readyFlagWaitHits = 0;
            return false;
        }

        sys.Memory.Write32(objPhys + ReadyFlagObjOff, 1);
        _readyFlagForceSets++;
        _readyFlagWaitHits = 0;
        // Also present v0=1 and leave the getter so the wait's bne fires next.
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
        if (inGetter)
        {
            uint ra = (uint)sys.EE.GetGpr(31).Lo;
            uint raPhys = ra & 0x1FFFFFFFu;
            if (raPhys is >= 0x00100000u and < 0x00580000u && (ra & 3) == 0)
                sys.EE.PC = ra;
            else
                sys.EE.PC = ReadyFlagWaitLo + 0x1C; // 0x3697AC bne v0 path
        }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && _readyFlagForceSets <= 8)
            Console.Error.WriteLine(
                $"[VEXX] ready-flag force-set #{_readyFlagForceSets} obj=0x{obj:X} " +
                $"+0x{ReadyFlagObjOff:X} pc=0x{pc:X} cyc={sys.Scheduler.MasterCycles}");
        return inGetter;
    }

    /// <summary>
    /// PL-017 title-surface pad inject. Active-high <see cref="PadInput.Button"/> bits;
    /// PADMAN DMA gets active-low via shared WritePadButtonData. Force-refresh open pad
    /// areas so EE padRead sees edges between VBlanks.
    /// </summary>
    private void MaybeInjectTitlePad(Ps2System sys)
    {
        // Need STREE/TOC activity or Soft-GS surface before pad matters.
        bool surfaceLive = sys.Gs.PixelsWritten > 0
            || sys.Cdvd.SectorsRead >= 80UL
            || _hostMemberOpens > 0
            || _streamMapTable != 0;
        if (!surfaceLive || _padInjectPulses >= 8192)
            return;

        _padInjectPulses++;
        int phase = _padInjectPulses % 6;
        uint buttons = phase switch
        {
            0 or 1 => (uint)PadInput.Button.Start,
            2 or 3 => (uint)PadInput.Button.Cross,
            4 => (uint)PadInput.Button.Circle,
            _ => 0u, // release edge for edge-triggered padRead
        };
        // Occasional dual-press + D-pad for menus that want confirm / move selection.
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

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && (_padInjectPulses <= 4 || _padInjectPulses % 512 == 0))
            Console.Error.WriteLine(
                $"[VEXX] pad inject #{_padInjectPulses} btn=0x{buttons:X4} " +
                $"px={sys.Gs.PixelsWritten} cdvd={sys.Cdvd.SectorsRead} cyc={sys.Scheduler.MasterCycles}");
    }

    /// <summary>
    /// Install host-serve CD file backends. Wave-3 planted retail defaults that go through
    /// <c>host:</c>+FILEIO RPC (bind never appears). Wave-4 points the vtable at spin stubs
    /// serviced by <see cref="MaybeHostCdIo"/> with real ISO open/read + sector credit.
    /// </summary>
    public void PlantCdIoVtable(Ps2System sys)
    {
        PlantHostCdStubs(sys);
        // Slot layout (8-byte stride): +0 open, +8 close, +16 read, +24 write, +32 stub0,
        // +40 seek, +48 tell, +56 size, +64 misc — matches default-install order.
        // Live open path (0x1DCEFC) loads 0x3AD3A8; also keep legacy 0x3BD3A8 covered.
        foreach (uint baseAddr in new[] { CdIoVtableBase, 0x003BD3A8u })
        {
            sys.Memory.Write32(baseAddr + 0x00, HostCdStubOpen);
            sys.Memory.Write32(baseAddr + 0x08, HostCdStubClose);
            sys.Memory.Write32(baseAddr + 0x10, HostCdStubRead);
            sys.Memory.Write32(baseAddr + 0x18, HostCdStubWrite);
            sys.Memory.Write32(baseAddr + 0x20, CdIoDefaultStub0);
            sys.Memory.Write32(baseAddr + 0x28, HostCdStubSeek);
            sys.Memory.Write32(baseAddr + 0x30, HostCdStubTell);
            sys.Memory.Write32(baseAddr + 0x38, HostCdStubSize);
            sys.Memory.Write32(baseAddr + 0x40, CdIoDefaultMisc);
        }
        _cdIoPlanted = true;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine(
                $"[VEXX] CD I/O vtable @0x{CdIoVtableBase:X} (+legacy 0x3BD3A8) host-stubs open=0x{HostCdStubOpen:X} read=0x{HostCdStubRead:X}");
    }

    /// <summary>
    /// Wave-5: after STREE0 TOC CdReads, host-load the hash index (u32 count + count×24)
    /// into bump RAM. On null-stream-map lookup, plant obj+8 = table so asset paths resolve.
    /// </summary>
    private uint _streamMapTable;
    private uint _streamMapCount;
    private uint _streamMapObj;

    private void MaybeFinishStreamMap(Ps2System sys)
    {
        _streamMapProbes++;
        if (_streamMapTable == 0)
            TryBuildStreamMapFromIso(sys);

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _streamMapProbes <= 3)
            Console.Error.WriteLine(
                $"[VEXX] stream-map probe #{_streamMapProbes} cdvd={sys.Cdvd.SectorsRead} " +
                $"table=0x{_streamMapTable:X} count={_streamMapCount} " +
                $"hostOpen={_hostOpens} hostRead={_hostReads} mapEsc={_streamMapEscapes} " +
                $"cyc={sys.Scheduler.MasterCycles}");

        if (_streamMapTable != 0)
            _tocProbeDone = true;
    }

    /// <summary>
    /// STREE0 on-disk: u32 count, then count × 24-byte hash entries (stream open @ 0x1DCFE0).
    /// </summary>
    private void TryBuildStreamMapFromIso(Ps2System sys)
    {
        string? isoPath = sys.Cdvd.MountedPath;
        if (string.IsNullOrEmpty(isoPath)) return;
        try
        {
            if (_isoVol == null || _isoVolPath != isoPath)
            {
                try { _isoVol?.Disc?.Dispose(); } catch { }
                _isoVol = Iso9660.OpenFile(isoPath);
                _isoVolPath = isoPath;
            }
            if (_isoVol?.Disc == null) return;
            var entry = Iso9660.FindFile(_isoVol, "STREE0.TRE");
            if (entry == null) return;

            var hdr = new byte[8];
            int got = _isoVol.Disc.ReadAt((long)entry.ExtentLba * Iso9660.SectorSize, hdr);
            if (got < 4) return;
            uint count = BitConverter.ToUInt32(hdr, 0);
            if (count is 0 or > 200_000) return;

            // PL-032k: reserve +16 slots for dual-slide inject (begin.mtf etc.).
            const uint DualInjectSlots = 16;
            uint bytes = count * 24u;
            uint alloc = bytes + DualInjectSlots * 24u + 32u;
            uint table = HostBumpAlloc(sys, alloc);
            if (table == 0) return;

            // File layout: +0 count (4), +4 entries. Stream open reads count then entries.
            var buf = new byte[bytes];
            int n = _isoVol.Disc.ReadAt((long)entry.ExtentLba * Iso9660.SectorSize + 4, buf);
            if (n <= 0) return;
            for (int i = 0; i < n; i++)
                sys.Memory.Write8(table + (uint)i, buf[i]);
            for (int i = n; i < (int)(bytes + DualInjectSlots * 24u); i++)
                sys.Memory.Write8(table + (uint)i, 0);

            _streamMapTable = table;
            _streamMapCount = count;
            _streamMapPlants++;
            sys.Cdvd.NoteHostReadSectors((int)((4 + bytes + 2047) / 2048));
            // PL-032k: dual-slide-only frontend members (begin.mtf etc.) are absent from the
            // aligned 24B STREE rows that fill this table — append high-confidence entries.
            InjectDualSlideStreamMapEntries(sys, DualInjectSlots);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                Console.Error.WriteLine(
                    $"[VEXX] stream-map BUILD table=0x{table:X} count={_streamMapCount} bytes={bytes} " +
                    $"dualInject={_beginMtfStreamMapInjects} cyc={sys.Scheduler.MasterCycles}");
        }
        catch
        {
            /* keep trying next probe */
        }
    }

    /// <summary>
    /// PL-032k: append dual-slide STREE members for begin mesh/ati (and *PR dual-only leaves)
    /// after the aligned STREE rows. Raises <see cref="_streamMapCount"/> so plants see them.
    /// </summary>
    private void InjectDualSlideStreamMapEntries(Ps2System sys, uint maxAppend)
    {
        if (_streamMapTable == 0 || _streamMapCount == 0 || maxAppend == 0) return;
        EnsureStreeMemberIndex(sys);
        if (_streeMemberByCrc == null || _streeMemberByCrc.Count == 0) return;

        // PL-032k4: do NOT inject BeginMtfNameCrc — a stream-map HIT without a working
        // STREE seek path caused *PR to skip path-open (diag: reorder+scrub still skip;
        // dual-only peers that miss stream-map still host-open). Inject only companions.
        uint[] want =
        {
            BeginAtiNameCrc,
            0x43338178u, // screenproxy.mtf
            0x27892F6Bu, // deitynofade.atr
            0x8AF95DFAu, // begin.pcl
            0x8B12435Du, // begin0.tre
            0x3D693416u, // begin.atr
            0x97EDE0A2u, // swooshes.swh
        };

        uint baseCount = _streamMapCount;
        int injected = 0;
        foreach (uint pick in want)
        {
            if (injected >= maxAppend) break;
            if (!_streeMemberByCrc.TryGetValue(pick, out var m) || m.Size == 0) continue;
            bool already = false;
            for (uint j = 0; j < baseCount + (uint)injected; j++)
            {
                if (sys.Memory.Read32(_streamMapTable + j * 24u + 8) == pick)
                { already = true; break; }
            }
            if (already) continue;
            uint ent = _streamMapTable + (baseCount + (uint)injected) * 24u;
            sys.Memory.Write32(ent + 0, 0);
            sys.Memory.Write32(ent + 4, 0);
            sys.Memory.Write32(ent + 8, pick);
            sys.Memory.Write32(ent + 12, 0);
            sys.Memory.Write32(ent + 16, m.Off);
            sys.Memory.Write32(ent + 20, m.Size);
            injected++;
            if (pick == BeginMtfNameCrc)
                _beginMtfStreamMapInjects++;
        }
        _streamMapCount = baseCount + (uint)injected;
        if (injected > 0 && Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine(
                $"[VEXX] stream-map dual-inject {injected} entries count={_streamMapCount} " +
                $"(beginMtf={_beginMtfStreamMapInjects})");
    }

    /// <summary>Plant host-built hash table into the live stream object (s5 / a0).</summary>
    private void MaybePlantStreamMapOnObject(Ps2System sys, uint obj)
    {
        if (_streamMapTable == 0 || obj < 0x1000 || obj + 0x420 >= SystemMemory.RDRAM_SIZE)
            return;
        uint cur = sys.Memory.Read32(obj + 8);
        if (cur == _streamMapTable) return;
        sys.Memory.Write32(obj + 8, _streamMapTable);
        sys.Memory.Write32(obj + 0xC, _streamMapCount);
        // Zero bucket count used by insert path; lookups walk the flat table via hash.
        if (sys.Memory.Read32(obj + 0x418) == 0)
            sys.Memory.Write32(obj + 0x418, 0);
        _streamMapObj = obj;
        _streamMapPlants++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _streamMapPlants <= 8)
            Console.Error.WriteLine(
                $"[VEXX] stream-map PLANT obj=0x{obj:X} table=0x{_streamMapTable:X} count={_streamMapCount}");
    }

    /// <summary>Spin loops so Step cannot miss the open/read PC (single-insn race).</summary>
    private static void PlantHostCdStubs(Ps2System sys)
    {
        // beq r0,r0,0; nop  — tight spin at each stub base
        for (uint s = HostCdStubBase; s < HostCdStubEnd; s += 0x20)
        {
            sys.Memory.Write32(s + 0, 0x1000FFFFu); // beq zero,zero,-1 (branch to self in delay)
            sys.Memory.Write32(s + 4, 0x00000000u); // nop delay
            for (uint i = 8; i < 0x20; i += 4)
                sys.Memory.Write32(s + i, 0);
        }
    }

    /// <summary>
    /// Host-serve CD I/O vtable entries: open/read/close/seek/tell/size against mounted ISO.
    /// Real disc bytes + <see cref="Cdvd.NoteHostReadSectors"/> — honest TRE TOC stream.
    /// </summary>
    private bool MaybeHostCdIo(Ps2System sys, uint pc)
    {
        var mods = sys.IopModules;
        if (mods == null) return false;
        // Accept any PC inside the stub slot (spin may land on +0 or +4).
        if (pc is >= HostCdStubOpen and < HostCdStubClose)
            return HostCdOpen(sys, mods);
        if (pc is >= HostCdStubClose and < HostCdStubRead)
            return HostCdClose(sys, mods);
        if (pc is >= HostCdStubRead and < HostCdStubWrite)
            return HostCdRead(sys, mods);
        if (pc is >= HostCdStubWrite and < HostCdStubSeek)
        {
            ReturnHost(sys, unchecked((uint)(-1))); // write not used for TRE rb
            return true;
        }
        if (pc is >= HostCdStubSeek and < HostCdStubTell)
            return HostCdSeek(sys, mods);
        if (pc is >= HostCdStubTell and < HostCdStubSize)
            return HostCdTell(sys, mods);
        if (pc is >= HostCdStubSize and < HostCdStubEnd)
            return HostCdSize(sys, mods);
        // Also catch retail entries if something still jumps there
        if (pc == CdIoDefaultOpen) return HostCdOpen(sys, mods);
        if (pc == CdIoDefaultRead) return HostCdRead(sys, mods);
        if (pc == CdIoDefaultClose) return HostCdClose(sys, mods);
        if (pc == CdIoDefaultSeek) return HostCdSeek(sys, mods);
        if (pc == CdIoDefaultTell) return HostCdTell(sys, mods);
        if (pc == CdIoDefaultSize) return HostCdSize(sys, mods);
        return false;
    }

    private bool HostCdOpen(Ps2System sys, IopModuleHost mods)
    {
        uint pathPtr = (uint)(sys.EE.GetGpr(4).Lo & 0x1FFFFFFFu); // a0
        string raw = ReadCString(sys, pathPtr, 256);
        string path = NormalizeHostCdPath(raw);
        if (path.Length == 0)
        {
            // PL-032k: silent empty path previously hid *PR / ACTOR_MESH opens (no FAIL log).
            // Recover printable path near a0 when present (pcl / atr path scratch).
            string recovered = TryRecoverPathNear(sys, pathPtr);
            if (recovered.Length > 0)
            {
                raw = recovered;
                path = NormalizeHostCdPath(raw);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                    Console.Error.WriteLine(
                        $"[VEXX] host-open empty-path recover a0=0x{pathPtr:X} → \"{raw}\" cyc={sys.Scheduler.MasterCycles}");
            }
            if (path.Length == 0)
            {
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                    && (_hostOpens + _hostMemberFail) <= 64)
                    Console.Error.WriteLine(
                        $"[VEXX] host-open empty-path a0=0x{pathPtr:X} cyc={sys.Scheduler.MasterCycles}");
                ReturnHost(sys, 0);
                return true;
            }
        }

        // PL-032k5: pcl *PR alias → real begin.mtf STREE member.
        if (raw.IndexOf("zzbeginmeshforce", StringComparison.OrdinalIgnoreCase) >= 0
            || raw.IndexOf("zzzzzzzzzzzzzzzzzzbegin.mtf", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("zzzzzzzzzzzzzzzzzzbegin.mtf", StringComparison.OrdinalIgnoreCase) >= 0
            || (path.EndsWith("begin.mtf", StringComparison.OrdinalIgnoreCase)
                && path.Contains("actors\\widgets", StringComparison.OrdinalIgnoreCase)
                && path.Contains('z')))
        {
            raw = "data\\levels\\frontend\\memorycard\\begin\\begin.mtf";
            path = raw;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                Console.Error.WriteLine(
                    $"[VEXX] begin.mtf alias remap → \"{path}\" cyc={sys.Scheduler.MasterCycles}");
        }

        // Prefer cdrom0: so FileOpen hits disc path (STREE0.TRE / GAME.TXT / DATA\SOUND\…).
        string tryPath = path.Contains(':') ? path : "cdrom0:\\" + path;
        int fd = mods.FileOpen(tryPath, 1);
        if (fd < 0 && !tryPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            fd = mods.FileOpen(path, 1);
        if (fd < 0)
        {
            string leaf = System.IO.Path.GetFileName(path.Replace('/', '\\'));
            if (!string.IsNullOrEmpty(leaf))
                fd = mods.FileOpen("cdrom0:\\" + leaf, 1);
        }

        // Wave-6 / PL-032: data\… / fonts / frontend live inside STREE0 — virtual member stream.
        bool member = false;
        uint memberSz = 0;
        if (fd < 0)
        {
            // Prefer full raw path for CRC (NormalizeHostCdPath may leaf-strip).
            fd = TryOpenStreeMember(sys, mods, raw, path, out memberSz);
            member = fd >= 0;
        }

        // Nested probe packs (streeN.tre / patch0.tre / sound0.tre) must FAIL open.
        // Dual-slide NameCRC noise can hit a tiny false positive (sound0.tre sz=457) and
        // retail then walks nested packs forever — kills Path2 title chrome (PL-032d).
        // Only stub sound.ad6 when CRC miss (audio residual, not the streeN probe loop).
        if (fd >= 0 && LooksLikeNestedTreProbe(raw, path))
        {
            mods.FileClose(fd);
            fd = -1;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                && _hostMemberFail < 24)
                Console.Error.WriteLine(
                    $"[VEXX] nested-TRE force-FAIL \"{raw}\" (probe pack) cyc={sys.Scheduler.MasterCycles}");
        }

        if (fd < 0 && LooksLikeSoundPackOnly(raw, path))
        {
            fd = OpenNestedTreStub(mods, raw.Length > 0 ? raw : path);
            if (fd >= 0)
            {
                memberSz = 16;
                _hostNestedTreStubs++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                    && _hostNestedTreStubs <= 8)
                    Console.Error.WriteLine(
                        $"[VEXX] sound-pack stub #{_hostNestedTreStubs} \"{raw}\" cyc={sys.Scheduler.MasterCycles}");
            }
        }

        // Optional pre-vis (.pvsx) not shipped in STREE0 — open empty success so actor mesh
        // load (begin.mtf) is not aborted by a hard ENOENT on the pvs probe.
        if (fd < 0 && LooksLikeOptionalPvsx(raw, path))
        {
            fd = OpenNestedTreStub(mods, "pvsx:" + (raw.Length > 0 ? raw : path));
            if (fd >= 0)
            {
                memberSz = 16;
                _hostNestedTreStubs++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                    && _hostNestedTreStubs <= 12)
                    Console.Error.WriteLine(
                        $"[VEXX] pvsx empty-stub #{_hostNestedTreStubs} \"{raw}\" cyc={sys.Scheduler.MasterCycles}");
            }
        }

        // PL-032h: leaf-only precache miss (commontree.pcl) — empty success so begin0.tre
        // mesh bind is not aborted by hard ENOENT (NameCRC may not hit STREE0 for this leaf).
        if (fd < 0 && LooksLikeOptionalPrecachePcl(raw, path))
        {
            fd = OpenNestedTreStub(mods, "pcl:" + (raw.Length > 0 ? raw : path));
            if (fd >= 0)
            {
                memberSz = 16;
                _hostNestedTreStubs++;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                    && _hostNestedTreStubs <= 16)
                    Console.Error.WriteLine(
                        $"[VEXX] pcl empty-stub #{_hostNestedTreStubs} \"{raw}\" cyc={sys.Scheduler.MasterCycles}");
            }
        }

        if (fd < 0)
        {
            _hostMemberFail++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                && (_hostOpens + _hostMemberFail) <= 48)
                Console.Error.WriteLine(
                    $"[VEXX] host-open FAIL \"{raw}\" → \"{path}\" cyc={sys.Scheduler.MasterCycles}");
            ReturnHost(sys, 0);
            return true;
        }

        int handle = fd + 1; // retail open returns 1-based; read does a0--
        _hostFds[handle] = fd;
        if (!member) mods.TryGetOpenFileSize(fd, out memberSz);
        if (memberSz > 0) _hostFdSizes[handle] = memberSz;
        _hostOpens++;
        if (member) _hostMemberOpens++;
        // PL-032g: mark swooshes so freelist/float-expand escapes arm for the post-load hang.
        string leafLower = System.IO.Path.GetFileName((raw.Length > 0 ? raw : path).Replace('/', '\\'))
            .ToLowerInvariant();
        if (leafLower is "swooshes.swh" || leafLower.EndsWith(".swh", StringComparison.Ordinal))
            _swooshesLoaded = true;
        // PL-032i: begin.pcl precache autolist names begin.mtf / screenproxy / deity — mark
        // so ready-flag wait can arm and BADARGS full-read prefers EE heap.
        if (leafLower is "begin.pcl"
            || (leafLower.EndsWith(".pcl", StringComparison.Ordinal)
                && (raw.Contains("begin", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("begin", StringComparison.OrdinalIgnoreCase))))
            _beginPclLoaded = true;
        if (leafLower is "begin0.tre"
            || (leafLower.EndsWith(".tre", StringComparison.Ordinal)
                && leafLower.StartsWith("begin", StringComparison.Ordinal)))
            _begin0TreLoaded = true;
        // PL-032k: goal signal — begin.mtf finally host-opened (ACTOR_MESH / *PR).
        if (leafLower is "begin.mtf"
            || (leafLower.EndsWith(".mtf", StringComparison.Ordinal)
                && (raw.Contains("memorycard\\begin", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("memorycard\\begin", StringComparison.OrdinalIgnoreCase)
                    || raw.Contains("memorycard/begin", StringComparison.OrdinalIgnoreCase))))
            _beginMtfOpened = true;
        // PL-032n/o: track open for force-match. Retail often closes the fd before the
        // path-table name-search, so cache the payload NOW while fd is live (widget.atr 10MB).
        string openNorm = NormalizeOpenPath(raw.Length > 0 ? raw : path);
        NoteRecentOpenPath(openNorm, openNorm);
        if (openNorm.Length >= 4)
        {
            _pathOpenHandles[openNorm] = handle;
            string openLeaf = System.IO.Path.GetFileName(openNorm.Replace('/', '\\'));
            if (openLeaf.Length >= 4)
                _pathOpenHandles[openLeaf] = handle;
            // Reserve path stub BEFORE large payload cache so FORCE-MATCH cannot OOM after 10MB atr.
            uint reservedStub = EnsurePathObjectStub(sys, openNorm, 0);
            // Cache bytes immediately — force-match later attaches them to the path stub.
            MaybeCacheOpenPayload(sys, mods, openNorm, handle, fd, memberSz);
            if (reservedStub != 0 && _pathPayloadCache.TryGetValue(openNorm, out var cached))
            {
                try
                {
                    sys.Memory.Write32(reservedStub + 0x10, cached.baseAddr);
                    sys.Memory.Write32(reservedStub + 0x14, cached.size);
                }
                catch { /* ignore */ }
            }
        }
        _pendingOpenPath = openNorm;
        // Do NOT credit full 1GB TRE at open — only actual FileRead bytes (TOC stream).
        // Member open credits a small sector token so cdvd advances with asset binds.
        if (member && memberSz > 0)
            sys.Cdvd.NoteHostReadSectors((int)Math.Min((memberSz + 2047) / 2048, 64));
        bool logOpen = Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && (_hostOpens <= 96
                || leafLower is "begin.mtf" or "begin.ati" or "begin0.tre" or "begin.pcl"
                || leafLower.Contains("begin", StringComparison.Ordinal));
        if (logOpen)
        {
            Console.Error.WriteLine(
                $"[VEXX] host-open #{_hostOpens}{(member ? " MEMBER" : "")} \"{raw}\" → \"{path}\" " +
                $"h={handle} fd={fd} size={memberSz} members={_hostMemberOpens}" +
                $"{(_beginMtfOpened ? " beginMtf=1" : "")} cyc={sys.Scheduler.MasterCycles}");
        }
        // PL-032k5: alias path makes EE host-open begin.mtf; keep skip detector as warn-only.
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && leafLower is "screenproxy.mtf" && !_beginMtfOpened && _begin0TreLoaded)
            Console.Error.WriteLine(
                $"[VEXX] PL-032k *PR skip still active (screenproxy.mtf without begin.mtf) cyc={sys.Scheduler.MasterCycles}");
        ReturnHost(sys, unchecked((uint)handle));
        return true;
    }

    private void NoteBumpBase(uint mem, uint size = 0)
    {
        if (mem < BumpArenaBase || mem >= BumpArenaEnd) return;
        int slot = _recentBumpCount % _recentBumpBases.Length;
        _recentBumpBases[slot] = mem;
        _recentBumpSizes[slot] = size;
        _recentBumpCount++;
        _lastBumpBase = mem;
        _lastBumpSize = size;
    }

    private static bool LooksLikeSoundPackOnly(string raw, string path)
    {
        string s = (raw.Length > 0 ? raw : path).ToLowerInvariant().Replace('/', '\\');
        string leaf = System.IO.Path.GetFileName(s);
        // Never stub streeN.tre / patch0.tre — open FAIL is required for probe skip.
        return leaf is "sound.ad6" || s.EndsWith("\\sound.ad6");
    }

    /// <summary>Optional precomputed visibility (.pvsx) — absent from STREE0 retail index.</summary>
    private static bool LooksLikeOptionalPvsx(string raw, string path)
    {
        string s = (raw.Length > 0 ? raw : path).ToLowerInvariant().Replace('/', '\\');
        return s.EndsWith(".pvsx", StringComparison.Ordinal);
    }

    /// <summary>
    /// Optional precache list leaf (commontree.pcl) — not always in STREE0 under the leaf name.
    /// begin.pcl / begin0.tre already NameCRC-hit; commontree is a shared miss that must not hard-fail.
    /// </summary>
    private static bool LooksLikeOptionalPrecachePcl(string raw, string path)
    {
        string s = (raw.Length > 0 ? raw : path).ToLowerInvariant().Replace('/', '\\');
        string leaf = System.IO.Path.GetFileName(s);
        return leaf.EndsWith(".pcl", StringComparison.Ordinal)
               && (leaf.Contains("common", StringComparison.Ordinal)
                   || s.Contains("precache", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nested stream packs probed after STREE0 — must open-FAIL (not false NameCRC success).
    /// PL-032j: <c>begin0.tre</c> is a real small TRE (count=10, names begin.mtf CRC) under
    /// dual-slide C; dual-slide A is atr-as-tre noise. Do NOT force-FAIL beginN.tre — open
    /// prefers TRE count-header over atr (see <see cref="TryOpenStreeMember"/>). streeN /
    /// patch0 / sound0 still force-FAIL.
    /// </summary>
    private static bool LooksLikeNestedTreProbe(string raw, string path)
    {
        string s = (raw.Length > 0 ? raw : path).ToLowerInvariant().Replace('/', '\\');
        string leaf = System.IO.Path.GetFileName(s);
        if (leaf is "stree0.tre" or "sound.ad6") return false;
        if (leaf is "patch0.tre" or "sound0.tre") return true;
        // stree1.tre … stree24.tre
        if (leaf.StartsWith("stree", StringComparison.Ordinal) && leaf.EndsWith(".tre", StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>Minimal empty payload so sound-pack open leaves ENOENT thrash.</summary>
    private static int OpenNestedTreStub(IopModuleHost mods, string name)
    {
        var stub = new byte[16];
        return mods.FileOpenMemoryStub("vexx-sound-stub:" + name, stub);
    }

    /// <summary>
    /// Open a path that lives inside STREE0.TRE via NameCRC32 → (offset,size) virtual stream.
    /// </summary>
    private int TryOpenStreeMember(Ps2System sys, IopModuleHost mods, string raw, string path,
        out uint size)
    {
        size = 0;
        EnsureStreeMemberIndex(sys);
        if (_streeMemberByCrc == null || _streeMemberByCrc.Count == 0 || _stree0DiscByteOff == 0)
            return -1;

        string key = NormalizeMemberPath(raw.Length > 0 ? raw : path);
        if (key.Length == 0) return -1;

        // Path alts: full, leaf, data\ prefix, $/ strip already done in Normalize.
        // PL-032b: leaf-only opens (button2.tgax) need onscreengraphics/fonts\… prefixes —
        // NameCRC is the full path; NormalizeHostCdPath leaf-strips before FileOpen.
        var alts = new List<string>(32) { key };
        string leaf = System.IO.Path.GetFileName(key.Replace('/', '\\'));
        if (!string.IsNullOrEmpty(leaf) && leaf != key)
        {
            alts.Add(leaf);
            if (!key.StartsWith("data\\", StringComparison.Ordinal))
                alts.Add("data\\" + key);
        }
        // SOUND.AD6 retail sometimes uppercases the whole path after Normalize lower — CRC is
        // lowercased; also try without data\ for packs that live under sound\ only.
        if (key.StartsWith("data\\", StringComparison.Ordinal) && key.Length > 5)
            alts.Add(key[5..]);

        // PL-032k: ACTOR_MESH atr paths use $\DATA\…\begin.mtf (and $/\Data\Actors\…).
        // NormalizeMemberPath already strips $ / y: — ensure begin mesh/ati full keys present
        // when the open is leaf-only after path normalize.
        if (leaf.Equals("begin.mtf", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("begin.ati", StringComparison.OrdinalIgnoreCase))
        {
            string full = "data\\levels\\frontend\\memorycard\\begin\\" + leaf.ToLowerInvariant();
            if (!alts.Contains(full))
                alts.Insert(0, full);
        }

        // Leaf / short key → telemetry path prefixes (buttonN / frontend / env textures).
        if (!string.IsNullOrEmpty(leaf) && leaf.IndexOf('.') > 0)
        {
            foreach (string pref in MemberLeafPrefixes)
            {
                string full = pref + leaf;
                if (!alts.Contains(full))
                    alts.Add(full);
            }
        }

        string leafLower = leaf.ToLowerInvariant();
        bool beginAutolistTre = leafLower.StartsWith("begin", StringComparison.Ordinal)
            && leafLower.EndsWith(".tre", StringComparison.Ordinal);

        foreach (string a in alts)
        {
            uint crc = Crc32Ascii(a);
            if (!_streeMemberByCrc.TryGetValue(crc, out var ent) || ent.Size == 0)
                continue;
            // Reject weak matches (e.g. sound0.tre false positive → UnknownOpcode thrash).
            if (ent.Score < MemberMinScore) continue;
            // Nested .tre must start with a plausible entry-count header (score≥20 from probe).
            if (a.EndsWith(".tre", StringComparison.Ordinal) && ent.Score < 12
                && !beginAutolistTre)
                continue;

            // PL-032j: begin0.tre dual-slide A is \x01atr noise (score 29); C is real TRE
            // count=10 @23257B that lists begin.mtf. Prefer TRE head; reject atr-as-tre.
            uint useOff = ent.Off;
            uint useSz = ent.Size;
            if (beginAutolistTre || a.EndsWith(".tre", StringComparison.Ordinal))
            {
                if (!TryResolveTrePayload(crc, ent, out useOff, out useSz))
                {
                    if (beginAutolistTre)
                        continue; // do not open atr-as-tre
                    // non-begin .tre: keep ent if probe ok
                    useOff = ent.Off;
                    useSz = ent.Size;
                }
            }

            long abs = _stree0DiscByteOff + useOff;
            if (abs > uint.MaxValue) continue;
            int vfd = mods.FileOpenVirtualStream("stree:" + a, (uint)abs, useSz);
            if (vfd >= 0)
            {
                size = useSz;
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                    && beginAutolistTre)
                    Console.Error.WriteLine(
                        $"[VEXX] begin-autolist TRE open \"{a}\" off=0x{useOff:X} sz={useSz}");
                return vfd;
            }
        }
        return -1;
    }

    /// <summary>
    /// PL-032j: for a NameCRC, pick a TRE count-header payload over atr-magic dual-slide noise.
    /// Rescans STREE0 index words (same dual-slide as build) and returns the best TRE head.
    /// </summary>
    private bool TryResolveTrePayload(uint nameCrc,
        (uint Off, uint Size, int Score) preferred,
        out uint off, out uint sz)
    {
        off = preferred.Off;
        sz = preferred.Size;
        if (_isoVol?.Disc == null || _stree0DiscByteOff == 0 || _stree0Size == 0)
            return LooksLikeTreHead(preferred.Off, preferred.Size);

        // Fast path: preferred already TRE.
        if (LooksLikeTreHead(preferred.Off, preferred.Size))
            return true;

        // Preferred is atr/other — rescan TOC for same NameCRC with TRE head.
        try
        {
            uint count;
            var hdr = new byte[4];
            if (_isoVol.Disc.ReadAt(_stree0DiscByteOff, hdr) < 4) return false;
            count = BitConverter.ToUInt32(hdr, 0);
            if (count is 0 or > 200_000) return false;
            uint bytes = count * 24u;
            var buf = new byte[bytes];
            int n = _isoVol.Disc.ReadAt(_stree0DiscByteOff + 4, buf);
            if (n < 24) return false;
            int words = n / 4;
            uint tocEnd = 4 + bytes;
            uint bestOff = 0, bestSz = 0;
            int bestScore = -1;

            void Offer(uint ncrc, uint o, uint s)
            {
                if (ncrc != nameCrc || s < 16 || s > 32u * 1024 * 1024) return;
                if (o < tocEnd || o >= _stree0Size || (ulong)o + s > _stree0Size) return;
                if (!LooksLikeTreHead(o, s)) return;
                int sc = (int)Math.Min(s, 100000u); // prefer larger real TRE
                if (sc > bestScore)
                {
                    bestScore = sc;
                    bestOff = o;
                    bestSz = s;
                }
            }

            for (int e = 0; e < (int)(bytes / 24u); e++)
            {
                int baseOff = e * 24;
                uint nc = BitConverter.ToUInt32(buf, baseOff + 8);
                uint o = BitConverter.ToUInt32(buf, baseOff + 16);
                uint s = BitConverter.ToUInt32(buf, baseOff + 20);
                Offer(nc, o, s);
            }
            for (int i = 0; i + 3 < words; i++)
            {
                uint w0 = BitConverter.ToUInt32(buf, i * 4);
                uint w1 = BitConverter.ToUInt32(buf, i * 4 + 4);
                uint w2 = BitConverter.ToUInt32(buf, i * 4 + 8);
                uint w3 = BitConverter.ToUInt32(buf, i * 4 + 12);
                Offer(w2, w0, w1); // C
                Offer(w0, w2, w3); // A
            }

            if (bestSz > 0)
            {
                off = bestOff;
                sz = bestSz;
                return true;
            }
        }
        catch { /* fall through */ }
        return false;
    }

    private bool LooksLikeTreHead(uint off, uint sz)
    {
        if (_isoVol?.Disc == null || sz < 8) return false;
        var head = new byte[8];
        try
        {
            if (_isoVol.Disc.ReadAt(_stree0DiscByteOff + off, head) < 8) return false;
        }
        catch { return false; }
        // Reject atr / ati package magic.
        if (head[0] == 0x01 && head[1] == (byte)'a' && head[2] == (byte)'t'
            && (head[3] == (byte)'r' || head[3] == (byte)'i'))
            return false;
        uint w0 = BitConverter.ToUInt32(head, 0);
        // Small autolist TRE (begin0 count=10) or larger nested TRE.
        return w0 is >= 1 and <= 200_000;
    }

    /// <summary>Lowercase backslash path, strip device / <c>$/</c> / <c>y:</c> / version suffix.</summary>
    internal static string NormalizeMemberPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string path = raw.Trim();
        if (path.StartsWith("host0:", StringComparison.OrdinalIgnoreCase)) path = path[6..];
        else if (path.StartsWith("host:", StringComparison.OrdinalIgnoreCase)) path = path[5..];
        else if (path.StartsWith("cdrom0:", StringComparison.OrdinalIgnoreCase)) path = path[7..];
        else if (path.StartsWith("cdrom:", StringComparison.OrdinalIgnoreCase)) path = path[6..];
        path = path.TrimStart('\\', '/');
        if (path.StartsWith("$/", StringComparison.Ordinal) || path.StartsWith("$\\", StringComparison.Ordinal))
            path = path[2..];
        else if (path.Length > 0 && path[0] == '$')
            path = path[1..].TrimStart('\\', '/');
        // begin.pcl *PR entries use y:\data\… (and atr packs use Y:\Data\…).
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            path = path[2..].TrimStart('\\', '/');
        // PL-032k: ACTOR_MESH residual form "$\\DATA\\…" (dollar without slash).
        if (path.Length > 0 && path[0] == '$')
            path = path[1..].TrimStart('\\', '/');
        int semi = path.IndexOf(';');
        if (semi >= 0) path = path[..semi];
        path = path.Replace('/', '\\').ToLowerInvariant();
        while (path.StartsWith(".\\")) path = path[2..];
        return path.Trim();
    }

    private void EnsureStreeMemberIndex(Ps2System sys)
    {
        if (_streeMemberByCrc != null) return;
        _streeMemberByCrc = new Dictionary<uint, (uint Off, uint Size, int Score)>(16384);
        string? isoPath = sys.Cdvd.MountedPath;
        if (string.IsNullOrEmpty(isoPath)) return;
        try
        {
            if (_isoVol == null || _isoVolPath != isoPath)
            {
                try { _isoVol?.Disc?.Dispose(); } catch { }
                _isoVol = Iso9660.OpenFile(isoPath);
                _isoVolPath = isoPath;
            }
            if (_isoVol?.Disc == null) return;
            var entry = Iso9660.FindFile(_isoVol, "STREE0.TRE");
            if (entry == null) return;

            _stree0DiscByteOff = (long)entry.ExtentLba * Iso9660.SectorSize;
            _stree0Size = entry.Size;

            var hdr = new byte[4];
            if (_isoVol.Disc.ReadAt(_stree0DiscByteOff, hdr) < 4) return;
            uint count = BitConverter.ToUInt32(hdr, 0);
            if (count is 0 or > 200_000) return;

            uint bytes = count * 24u;
            var buf = new byte[bytes];
            int n = _isoVol.Disc.ReadAt(_stree0DiscByteOff + 4, buf);
            if (n < 24) return;
            int words = n / 4;
            uint tocEnd = 4 + bytes;

            // STREE0 hash entry is 24 bytes / 6 u32 (ground-truthed PL-032):
            //   [0] link/unk  [1] unk  [2] NameCRC  [3] DataCRC  [4] offset  [5] size
            // Prefer aligned entries; also keep dual sliding 4-word layouts for residual.
            int entryCount = (int)(bytes / 24u);
            for (int e = 0; e < entryCount; e++)
            {
                int baseOff = e * 24;
                uint nameCrc = BitConverter.ToUInt32(buf, baseOff + 8);
                uint off = BitConverter.ToUInt32(buf, baseOff + 16);
                uint sz = BitConverter.ToUInt32(buf, baseOff + 20);
                TryOfferMember(nameCrc, off, sz, tocEnd, fromAligned: true);
            }
            for (int i = 0; i + 3 < words; i++)
            {
                uint w0 = BitConverter.ToUInt32(buf, i * 4);
                uint w1 = BitConverter.ToUInt32(buf, i * 4 + 4);
                uint w2 = BitConverter.ToUInt32(buf, i * 4 + 8);
                uint w3 = BitConverter.ToUInt32(buf, i * 4 + 12);

                // Prefer layout C before A — begin.atr / several frontend packages ground-truth as C.
                TryOfferMember(w2, w0, w1, tocEnd, fromAligned: false); // C: ncrc=w2 off=w0 sz=w1
                TryOfferMember(w0, w2, w3, tocEnd, fromAligned: false); // A: ncrc=w0 off=w2 sz=w3
            }

            _streeMemberIndexCount = _streeMemberByCrc.Count;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                Console.Error.WriteLine(
                    $"[VEXX] STREE0 member-index entries={_streeMemberIndexCount} " +
                    $"count={count} discOff=0x{_stree0DiscByteOff:X}");
        }
        catch
        {
            /* leave empty; retry not needed — null dict means built-failed */
            _streeMemberByCrc ??= new Dictionary<uint, (uint Off, uint Size, int Score)>();
        }
    }

    private void TryOfferMember(uint nameCrc, uint off, uint sz, uint tocEnd, bool fromAligned = false)
    {
        if (_streeMemberByCrc == null || nameCrc == 0 || sz < 8 || sz > 32u * 1024 * 1024)
            return;
        if (off < tocEnd || off >= _stree0Size) return;
        if ((ulong)off + sz > _stree0Size) return;
        if (_isoVol?.Disc == null) return;

        // Payload probe for scoring (prefer real text/asset heads over random TOC noise).
        int probeLen = (int)Math.Min(sz, 96u);
        var probe = new byte[probeLen];
        int got = 0;
        try { got = _isoVol.Disc.ReadAt(_stree0DiscByteOff + off, probe); }
        catch { return; }
        if (got < 4) return;

        int score = ScoreMemberProbe(probe.AsSpan(0, got));
        if (score < MemberMinScore) return;
        // Ground-truth 24-byte rows beat dual-slide noise of equal class.
        if (fromAligned) score += 2;

        // PL-032b/f: real begin.atr is \x01atr @200B; begin.ati is \x01ati; dual-slide *PARTDEF
        // noise can score high. Prefer package magic strongly; never let non-magic replace it.
        bool magicPkg = got >= 4 && probe[0] == 0x01 && probe[1] == (byte)'a'
            && probe[2] == (byte)'t' && (probe[3] == (byte)'r' || probe[3] == (byte)'i');
        if (_streeMemberByCrc.TryGetValue(nameCrc, out var prev))
        {
            if (prev.Score >= score && !magicPkg)
                return;
            // Existing magic package wins unless new is also magic with higher score.
            if (prev.Score >= 20 && score < prev.Score + 8 && !magicPkg)
                return;
        }
        _streeMemberByCrc[nameCrc] = (off, sz, score);
    }

    /// <summary>Heuristic payload score; below <see cref="MemberMinScore"/> = reject.</summary>
    private static int ScoreMemberProbe(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) return -1;
        int score = 0;
        uint w0 = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));

        // Strong text / index markers (fontindex / history / textindex).
        if (b[0] == (byte)';' || b[0] == (byte)'#' || (b[0] == (byte)'/' && b.Length > 1 && b[1] == (byte)'/'))
            score += 25;
        if (ContainsAscii(b, "This is a list") || ContainsAscii(b, "add history")
            || ContainsAscii(b, "font files") || ContainsAscii(b, "message text"))
            score += 35;
        // Actor / level package heads
        if (b[0] == (byte)'.' && ContainsAscii(b, "ACTOR")) score += 20;
        // Vexx binary .atr packages: 01 'a''t''r' … ACTOR (begin.atr / level atr family).
        // PL-032b: without this, dual-slide noise (sz=1174) beat real begin.atr (sz=200).
        // PL-032f: .ati actor-instance packs (begin.ati / mainmenu.ati) use 01 'a''t''i'.
        if (b.Length >= 8 && b[0] == 0x01 && b[1] == (byte)'a' && b[2] == (byte)'t' && b[3] == (byte)'r')
            score += 24;
        else if (b.Length >= 8 && b[0] == 0x01 && b[1] == (byte)'a' && b[2] == (byte)'t' && b[3] == (byte)'i')
            score += 24;
        else if (b.Length >= 8 && ContainsAscii(b[..Math.Min(b.Length, 48)], "ACTOR"))
            score += 12;
        // Printable density first — used by script vs binary dual-slide tie-break.
        int printable = 0;
        int n = Math.Min(b.Length, 32);
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c is >= 32 and < 127 or 9 or 10 or 13) printable++;
        }
        // PL-032i: binary mesh .mtf (begin.mtf head e0 40 44 …) — structured binary, low printable.
        if (b.Length >= 16 && printable <= 8 && w0 != 0 && (w0 & 0xFF) >= 0x80)
            score += 12;
        // PL-032j: FILE/MESH container (ELIF little-endian) is the retail .mtf class
        // (creatureshadow/screenproxy/begin.mtf C-layout). Boost so dual-slide texture
        // noise (A-layout e0 40 44 heads) cannot beat the real FILE mesh.

        if (b[0] == (byte)'*' && (StartsWithAscii(b, "*PARTDEF") || StartsWithAscii(b, "*EMITDEF")
            || StartsWithAscii(b, "*LEVEL") || StartsWithAscii(b, "*DataPath")
            || StartsWithAscii(b, "*ST") || StartsWithAscii(b, "*PR")
            || StartsWithAscii(b, "*SWOOSH") || StartsWithAscii(b, "*VERTS")))
            score += 14;
        // Text scripts (*SWOOSH / brace blocks) beat dual-slide binary noise of equal CRC.
        if (b[0] == (byte)'*' && printable > 12) score += 12;

        // FILE / MESH containers (little-endian tags ELIF / HSEM)
        // PL-032j: strong boost — real begin.mtf is `…ELIF…` @2302B (score must beat
        // dual-slide texture-ish A-layout @11056B).
        if (b.Length >= 8 && (ContainsAscii(b[..Math.Min(b.Length, 48)], "ELIF")
            || ContainsAscii(b[..Math.Min(b.Length, 48)], "HSEM")))
            score += 28;
        if (ContainsAscii(b[..Math.Min(b.Length, 48)], "MINA")
            || ContainsAscii(b[..Math.Min(b.Length, 48)], "EPYT"))
            score += 14;
        // Memcard / save template
        if (ContainsAscii(b, "SLUS") || ContainsAscii(b, "/BA")) score += 20;
        // Nested TRE: first u32 count + second word TOC-ish
        if (b.Length >= 8)
        {
            uint w1 = (uint)(b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24));
            if (w0 is >= 1 and <= 200_000 && w1 is >= 0x1000 and <= 0x1000000)
                score += 30; // strong nested-TRE
            else if (w0 is >= 1 and <= 200_000)
                score += 6;
        }

        // Binary texture-ish: low printable, non-zero header (tgax/bmpx/atf).
        // PL-032: compact binary was score 8–9 &lt; old min 10 → FAIL.
        // PL-032b: button2–9 / timerback land printable≈7–8 with structured 0x80….. heads —
        // old mid-tier +4 → total score 5 &lt; min 8. Raise mid-tier to +10; light boost for
        // slightly noisier binary (printable≤16) so residual frontend TEX binds.
        score += printable / 5;
        if (printable <= 6 && w0 != 0) score += 10; // ultra-compact binary head
        else if (printable <= 12 && w0 != 0) score += 10; // PL-032b: button/timer .tgax class
        else if (printable <= 16 && w0 != 0 && b.Length >= 16) score += 6;

        // Common Vexx .tgax lead byte 0x80 + non-zero dimension word (button/timer family).
        if (b[0] == 0x80 && b.Length >= 8 && w0 != 0 && score < MemberMinScore + 2)
            score += 6;

        // Reject pure zeros / near-empty
        if (score < 4 && printable < 4) return -1;
        return score;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> b, string s)
    {
        if (b.Length < s.Length) return false;
        for (int i = 0; i < s.Length; i++)
            if (b[i] != (byte)s[i]) return false;
        return true;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> b, string s)
    {
        if (b.Length < s.Length) return false;
        for (int i = 0; i <= b.Length - s.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < s.Length; j++)
            {
                if (b[i + j] != (byte)s[j]) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }

    /// <summary>Standard CRC-32 (ZIP/PNG), matches retail NameCRC and Python binascii.crc32.</summary>
    internal static uint Crc32Ascii(string s)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < s.Length; i++)
        {
            crc ^= (byte)s[i];
            for (int k = 0; k < 8; k++)
            {
                uint mask = (uint)-(int)(crc & 1);
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }
        return ~crc;
    }

    private bool HostCdRead(Ps2System sys, IopModuleHost mods)
    {
        // Entry is `j real_read; addiu a0,a0,-1` — intercept before delay-slot, a0 still 1-based.
        int handle = (int)sys.EE.GetGpr(4).Lo;
        uint buf = (uint)sys.EE.GetGpr(5).Lo;
        uint size = (uint)sys.EE.GetGpr(6).Lo;
        if (!_hostFds.TryGetValue(handle, out int fd))
        {
            // Maybe already 0-based from a direct call
            if (_hostFds.TryGetValue(handle + 1, out fd))
                handle = handle + 1;
            else
            {
                ReturnHost(sys, unchecked((uint)(-9))); // EBADF-ish
                return true;
            }
        }

        // PL-032: retail text/begin.atr path often arrives with buf=0xFFFFFFF0 size=0xFFFFFFFF
        // (unwired out-buffer + "read all"). Recover a destination from recent freelist bumps
        // or s-registers before failing EFAULT — open already succeeded with real size.
        uint phys = buf & 0x1FFFFFFFu;
        bool badBuf = buf == 0 || phys < 0x1000u || phys >= SystemMemory.RDRAM_SIZE;
        // Also treat high poison patterns (0xFFFFFFxx) as bad even if phys masks into RDRAM.
        if (!badBuf && (buf & 0xFFF00000u) == 0xFFF00000u)
            badBuf = true;
        if (badBuf)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                && _hostBadArgsRecovered < 8)
            {
                uint sp0 = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
                Console.Error.WriteLine(
                    $"[VEXX] host-read BADARGS dump h={handle} a0=0x{(uint)sys.EE.GetGpr(4).Lo:X} " +
                    $"a1=0x{(uint)sys.EE.GetGpr(5).Lo:X} a2=0x{(uint)sys.EE.GetGpr(6).Lo:X} " +
                    $"a3=0x{(uint)sys.EE.GetGpr(7).Lo:X} sp=0x{sp0:X} " +
                    $"s0=0x{(uint)sys.EE.GetGpr(16).Lo:X} s1=0x{(uint)sys.EE.GetGpr(17).Lo:X} " +
                    $"s2=0x{(uint)sys.EE.GetGpr(18).Lo:X} s3=0x{(uint)sys.EE.GetGpr(19).Lo:X} " +
                    $"s4=0x{(uint)sys.EE.GetGpr(20).Lo:X} s5=0x{(uint)sys.EE.GetGpr(21).Lo:X} " +
                    $"t0=0x{(uint)sys.EE.GetGpr(8).Lo:X} t1=0x{(uint)sys.EE.GetGpr(9).Lo:X} " +
                    $"ra=0x{(uint)sys.EE.GetGpr(31).Lo:X} cyc={sys.Scheduler.MasterCycles}");
                if (sp0 is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0x40)
                {
                    var sb = new StringBuilder("[VEXX] BADARGS sp[0..0x3C]=");
                    for (uint o = 0; o <= 0x3C; o += 4)
                        sb.Append($" {o:X2}:0x{sys.Memory.Read32(sp0 + o):X}");
                    Console.Error.WriteLine(sb.ToString());
                }
            }
            uint recovered = RecoverReadBuffer(sys, handle, mods, ref size);
            if (recovered != 0)
            {
                buf = recovered;
                phys = recovered & 0x1FFFFFFFu;
                badBuf = false;
                _hostBadArgsRecovered++;
                // Retail load wrapper @0x1D8500 after jal read:
                //   div v0,s1; mflo v1; li v0,1; beq v1,v0,ok  → full-read needs v0==s1.
                // s1 was loaded from s3+0x828 (size). When size is poison 0xFFFFFFFF, patch
                // s1 (+ object size/buffer fields) so quotient==1; leave non-poison alone
                // (fontindex freelist path already works without memory stores).
                sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = recovered }); // a1
                sys.EE.SetGpr(6, new EmotionEngine.Gpr128 { Lo = size });      // a2
                uint s3 = (uint)(sys.EE.GetGpr(19).Lo & 0x1FFFFFFFu);
                uint sizeField = 0;
                if (s3 is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0x900u)
                    sizeField = sys.Memory.Read32(s3 + 0x828);
                // Force full-read success when dest is a live EE heap buffer (begin.atr
                // sp+0x30 class). Freelist-bump recovers (fontindex/history) keep the
                // legacy fail-tolerant path early — forcing success there killed Path2
                // title chrome. PL-032i: after swooshes / begin.pcl, poison-size package
                // loads (begin.pcl 690B names begin.mtf) must also pass the div check even
                // if recover landed in a freelist bump, or the precache list is discarded.
                bool eeHeapDest = recovered is >= 0x00100000u and < BumpArenaBase;
                bool sizePoison = sizeField == 0 || sizeField == 0xFFFFFFFFu
                    || sizeField > HostReadMaxBytes;
                // Full-read only for package-era real payloads (begin.atr/swooshes/pcl ≥64B).
                // Early fontindex and tiny empty stubs (pvsx/commontree 16B) must keep the
                // fail-tolerant path — forcing them into EE heap / s1-div killed Path2 and
                // landed PC in the recover buffer (diag PL-032i2 @0x1F5FA0).
                bool packageEraRead = _swooshesLoaded || _beginPclLoaded || _hostMemberOpens >= 28;
                bool forceFullRead = sizePoison && packageEraRead && size >= 64;
                if (forceFullRead)
                {
                    // div v0,s1 after return needs s1==v0; also plant buffer at s3+0x834 for
                    // the success tail that reloads dest from that slot.
                    sys.EE.SetGpr(17, new EmotionEngine.Gpr128 { Lo = size });
                    if (s3 is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0x900u)
                    {
                        sys.Memory.Write32(s3 + 0x828, size);
                        sys.Memory.Write32(s3 + 0x834, recovered);
                    }
                }
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                    && _hostBadArgsRecovered <= 16)
                    Console.Error.WriteLine(
                        $"[VEXX] host-read BADARGS recover #{_hostBadArgsRecovered} h={handle} " +
                        $"buf→0x{buf:X} size=0x{size:X} s3=0x{s3:X} szField=0x{sizeField:X} " +
                        $"poison={(sizePoison ? 1 : 0)} cyc={sys.Scheduler.MasterCycles}");
            }
            else
            {
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _hostReads < 24)
                    Console.Error.WriteLine(
                        $"[VEXX] host-read BADARGS h={handle} buf=0x{buf:X} size=0x{size:X} cyc={sys.Scheduler.MasterCycles}");
                ReturnHost(sys, unchecked((uint)(-14))); // EFAULT-ish
                return true;
            }
        }

        // Wave-6: retail often passes size=0xFFFFFFFF ("read all") for host:$ members.
        // Clamp to file remaining + HostReadMaxBytes + RDRAM before rejecting.
        if (size == 0)
        {
            ReturnHost(sys, 0);
            return true;
        }
        if (size > HostReadMaxBytes)
            size = HostReadMaxBytes;
        if (mods.TryGetOpenFileSize(fd, out uint fsz) && fsz > 0)
        {
            int pos = mods.FileSeek(fd, 0, 1); // SEEK_CUR
            if (pos >= 0 && (uint)pos < fsz)
            {
                uint remain = fsz - (uint)pos;
                if (size > remain) size = remain;
            }
            // restore position after tell
            if (pos >= 0) mods.FileSeek(fd, pos, 0); // SEEK_SET
        }
        else if (_hostFdSizes.TryGetValue(handle, out uint known) && known > 0 && size > known)
            size = known;
        if (phys + size > SystemMemory.RDRAM_SIZE)
            size = SystemMemory.RDRAM_SIZE - phys;
        if (size == 0)
        {
            ReturnHost(sys, 0);
            return true;
        }

        int n = mods.FileRead(sys.Memory, fd, phys, size);
        if (n > 0)
            sys.Cdvd.NoteHostReadSectors((n + 2047) / 2048);
        _hostReads++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _hostReads <= 48)
            Console.Error.WriteLine(
                $"[VEXX] host-read #{_hostReads} h={handle} buf=0x{buf:X} size=0x{size:X} n={n} cdvd={sys.Cdvd.SectorsRead} cyc={sys.Scheduler.MasterCycles}");

        // PL-032o: promote pending open path to force-match set only after real bytes land.
        if (n > 0 && !string.IsNullOrEmpty(_pendingOpenPath))
        {
            NoteRecentOpenPath(_pendingOpenPath, _pendingOpenPath);
            if (n >= 256 || (_hostFdSizes.TryGetValue(handle, out uint full) && full > 0 && (uint)n >= full))
                _pendingOpenPath = ""; // fully consumed or substantial payload
        }

        // PL-032k: begin0.tre pack — remember full payload base; scrub begin.mtf NameCRC from
        // TOC so *PR cannot treat the mesh as pack-satisfied without a correct base link.
        if (n > 0)
            MaybeNoteBegin0TreRead(sys, phys, (uint)n);
        // PL-032k: after begin.pcl lands, swap *PR order so begin.mtf is requested after
        // screenproxy.mtf (diag: slot between tgax and screenproxy.mtf is always skipped).
        if (n > 0)
            MaybeReorderBeginPclPrList(sys, phys, (uint)n);

        ReturnHost(sys, unchecked((uint)n));
        return true;
    }

    /// <summary>
    /// PL-032k5: begin.pcl *PR path for begin.mtf is path-specifically skipped (reorder
    /// diag: third slot still skip). Alias the *PR path to a same-length actors\widgets
    /// style string that the walker will host-open; HostCdOpen maps the alias back to the
    /// real dual-slide begin.mtf member.
    /// Retail length 50: <c>y:\data\levels\frontend\memorycard\begin\begin.mtf</c>.
    /// </summary>
    private const string BeginMtfPrRetail =
        "y:\\data\\levels\\frontend\\memorycard\\begin\\begin.mtf";
    /// <summary>Same length as <see cref="BeginMtfPrRetail"/> (50).</summary>
    private const string BeginMtfPrAlias =
        "y:\\data\\actors\\widgets\\zzzzzzzzzzzzzzzzzzbegin.mtf";

    private void MaybeReorderBeginPclPrList(Ps2System sys, uint phys, uint n)
    {
        if (n < 200 || n > 4096 || phys < 0x1000 || phys + n > SystemMemory.RDRAM_SIZE)
            return;
        var bytes = new byte[n];
        for (uint i = 0; i < n; i++)
            bytes[i] = sys.Memory.Read8(phys + i);
        string text = Encoding.ASCII.GetString(bytes);
        if (text.IndexOf("begin.mtf", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        string retail = BeginMtfPrRetail;
        string alias = BeginMtfPrAlias;
        if (alias.Length != retail.Length)
            return; // programmer error — keep lengths matched
        int idx = text.IndexOf(retail, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return;
        for (int i = 0; i < alias.Length; i++)
            sys.Memory.Write8(phys + (uint)(idx + i), (byte)alias[i]);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine(
                $"[VEXX] begin.pcl *PR path alias buf=0x{phys:X} \"{alias}\" cyc={sys.Scheduler.MasterCycles}");
    }

    /// <summary>
    /// PL-032k: after a host-read into EE RAM, detect begin0.tre count/TOC/full pack and
    /// zero the begin.mtf NameCRC field so the precache walker must host-open the mesh.
    /// </summary>
    private void MaybeNoteBegin0TreRead(Ps2System sys, uint phys, uint n)
    {
        if (phys < 0x1000 || phys + n > SystemMemory.RDRAM_SIZE || n < 4)
            return;

        // Full pack (23257) or any buffer that starts with count=10 + deity size entry.
        uint count = sys.Memory.Read32(phys);
        bool looksLikeBegin0 = count == 10
            && n >= 4 + Begin0TreTocBytes
            && (n >= Begin0TreFullSize - 16 || n == 4 + Begin0TreTocBytes);
        // Entry layout at pack+4: [off,sz,ncrc,dcrc]×10. begin.mtf is entry index 1:
        //   +4+16= +20: off=0x504, +24: sz=0x8FE, +28: ncrc=BeginMtfNameCrc
        if (!looksLikeBegin0 && n >= Begin0TreFullSize - 64)
        {
            // Size clamp may shrink slightly; accept large reads with ELIF at +0x504.
            if (n > 0x500 && phys + Begin0TreMtfOff + 8 < SystemMemory.RDRAM_SIZE)
            {
                uint tag = sys.Memory.Read32(phys + Begin0TreMtfOff + 4);
                // 'ELIF' little-endian 0x46494C45
                if (tag == 0x46494C45u)
                    looksLikeBegin0 = true;
            }
        }

        if (looksLikeBegin0 && n >= Begin0TreMtfOff + 32)
        {
            _begin0TreLoaded = true;
            _begin0TrePackBase = phys;
            ScrubBeginMtfNameCrcInTre(sys, phys, hasCountHeader: true);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                && _begin0TreTocScrubs <= 8)
                Console.Error.WriteLine(
                    $"[VEXX] begin0.tre pack base=0x{phys:X} n={n} mtfScrub=#{_begin0TreTocScrubs} cyc={sys.Scheduler.MasterCycles}");
            return;
        }

        // TOC-only (count already consumed): 10×16 at dest. Entry1 ncrc at +16+8 = +24.
        if (n == Begin0TreTocBytes || (n >= Begin0TreTocBytes && n <= Begin0TreTocBytes + 16))
        {
            // Detect via begin.mtf CRC at entry1 nameCRC slot (offset +24 from TOC start).
            uint ncrc1 = sys.Memory.Read32(phys + 24);
            uint off1 = sys.Memory.Read32(phys + 16);
            if (ncrc1 == BeginMtfNameCrc || off1 == Begin0TreMtfOff)
            {
                ScrubBeginMtfNameCrcInTre(sys, phys, hasCountHeader: false);
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                    && _begin0TreTocScrubs <= 8)
                    Console.Error.WriteLine(
                        $"[VEXX] begin0.tre TOC scrub @0x{phys:X} n={n} scrub=#{_begin0TreTocScrubs} cyc={sys.Scheduler.MasterCycles}");
            }
        }
    }

    /// <summary>
    /// Zero begin.mtf TOC slot (off/sz/nameCRC) in a begin0.tre count-header or bare TOC
    /// buffer. PL-032k2: CRC-only scrub left *PR skip active — EE may index-load pack
    /// entries by slot; clearing off/sz forces the mesh onto the dual-slide host-open path.
    /// </summary>
    private void ScrubBeginMtfNameCrcInTre(Ps2System sys, uint baseAddr, bool hasCountHeader)
    {
        uint toc = hasCountHeader ? baseAddr + 4 : baseAddr;
        // 10 entries × 16 bytes: [off, sz, nameCRC, dataCRC]
        for (int e = 0; e < 10; e++)
        {
            uint ent = toc + (uint)(e * 16);
            if (ent + 16 > SystemMemory.RDRAM_SIZE) break;
            uint ncrc = sys.Memory.Read32(ent + 8);
            uint off = sys.Memory.Read32(ent + 0);
            if (ncrc == BeginMtfNameCrc || off == Begin0TreMtfOff)
            {
                sys.Memory.Write32(ent + 0, 0); // off
                sys.Memory.Write32(ent + 4, 0); // sz
                sys.Memory.Write32(ent + 8, 0); // nameCRC
                sys.Memory.Write32(ent + 12, 0); // dataCRC
                _begin0TreTocScrubs++;
                return;
            }
        }
    }

    /// <summary>PL-032k: recover a path string near a failed open a0 pointer.</summary>
    private static string TryRecoverPathNear(Ps2System sys, uint pathPtr)
    {
        if (pathPtr < 0x1000 || pathPtr >= SystemMemory.RDRAM_SIZE) return "";
        // Direct re-read with larger limit.
        string s = ReadCString(sys, pathPtr, 256);
        if (s.Length >= 4 && s.IndexOf('.') > 0) return s;
        // Scan back up to 64 bytes for start of printable path (pcl *PR strings).
        uint start = pathPtr > 64 ? pathPtr - 64 : 0x1000;
        for (uint p = start; p <= pathPtr && p + 8 < SystemMemory.RDRAM_SIZE; p++)
        {
            byte c = sys.Memory.Read8(p);
            if (c is (byte)'y' or (byte)'Y' or (byte)'d' or (byte)'D' or (byte)'$' or (byte)'c' or (byte)'C')
            {
                string cand = ReadCString(sys, p, 256);
                if (cand.Length >= 8
                    && (cand.Contains(".mtf", StringComparison.OrdinalIgnoreCase)
                        || cand.Contains(".atr", StringComparison.OrdinalIgnoreCase)
                        || cand.Contains("data", StringComparison.OrdinalIgnoreCase)))
                    return cand;
            }
        }
        return "";
    }

    /// <summary>
    /// PL-032n/p: after *PR host-open, EE name-searches the path in a table of objects
    /// (<c>lw a0,0xC(s5); jal strcmp</c>) but every slot is null/garbage (table never filled).
    ///
    /// PL-032o FORCE-MATCH returned a host stub — residual a0=1 at 0x11C200 (not a real actor).
    /// Retail miss path at caller 0x2A2AA4 is <c>jal 0x223970</c> (open stream + register via
    /// ctor 0x21CDB0 size 0x104). PL-032p: FORCE-MISS (v0=s0=0) so create/register runs, while
    /// still pre-caching the payload and ensuring a path stub exists if create later needs it.
    /// </summary>
    private bool MaybeForceNameSearchMatch(Ps2System sys, uint pc)
    {
        bool inSearch = pc is >= NameSearchLwA0 and <= (NameSearchFoundReturn + 0x20)
            || pc is >= NameStrcmpEntry and <= (NameStrcmpEntry + 0x40);
        if (!inSearch)
            return false;
        if (!_beginMtfOpened && _hostMemberOpens < 30)
            return false;
        if (_recentOpenPathCount == 0)
            return false;

        uint needlePtr = 0;
        if (pc is >= NameStrcmpEntry and <= (NameStrcmpEntry + 0x40))
            needlePtr = (uint)(sys.EE.GetGpr(5).Lo & 0x1FFFFFFFu); // a1
        else
            needlePtr = (uint)(sys.EE.GetGpr(19).Lo & 0x1FFFFFFFu); // s3
        if (!IsReadableCStringPtr(sys, needlePtr))
        {
            needlePtr = (uint)(sys.EE.GetGpr(5).Lo & 0x1FFFFFFFu);
            if (!IsReadableCStringPtr(sys, needlePtr))
                return false;
        }

        string needle = ReadCString(sys, needlePtr, 160);
        if (needle.Length < 8)
            return false;
        string norm = NormalizeOpenPath(needle);
        if (!IsRecentOpenPath(norm))
            return false;

        // Keep stub+payload warm for any residual that still needs a host object, but do NOT
        // short-circuit the search as FOUND — retail create (0x223970) builds a real 0x104 object.
        uint stub = EnsurePathObjectStub(sys, norm, needlePtr);
        uint payload = 0, payloadN = 0;
        if (stub != 0)
            MaybeEagerReadPathPayload(sys, norm, stub, out payload, out payloadN);

        // FORCE-MISS: natural epilogue with s0=v0=0 → callers take jal 0x223970 create path.
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 }); // s0
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });  // v0
        sys.EE.PC = NameSearchFoundReturn;
        _nameSearchForceMatches++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && _nameSearchForceMatches <= 16)
            Console.Error.WriteLine(
                $"[VEXX] name-search FORCE-MISS #{_nameSearchForceMatches} needle=\"{needle}\" " +
                $"(create-path) stub=0x{stub:X} payload=0x{payload:X} n={payloadN} " +
                $"cyc={sys.Scheduler.MasterCycles}");
        return true;
    }

    /// <summary>
    /// PL-032o: while open fd is still live, cache file bytes into bump RAM keyed by path.
    /// Force-match later attaches the cache to the path stub (+0x10/+0x14).
    /// </summary>
    private void MaybeCacheOpenPayload(Ps2System sys, IopModuleHost mods, string norm,
        int handle, int fd, uint memberSz)
    {
        if (string.IsNullOrEmpty(norm) || fd < 0)
            return;
        if (_pathPayloadCache.ContainsKey(norm))
            return;
        uint want = memberSz;
        if (want == 0 && _hostFdSizes.TryGetValue(handle, out uint known))
            want = known;
        if (want == 0 && mods.TryGetOpenFileSize(fd, out uint fsz))
            want = fsz;
        if (want == 0)
            return;
        // Only cache actor/precache packages that name-search force-match needs.
        // Caching every .tgax would exhaust the 16MB bump arena before widget.atr (10MB).
        string leafLower = System.IO.Path.GetFileName(norm.Replace('/', '\\')).ToLowerInvariant();
        bool cacheClass = leafLower.EndsWith(".atr", StringComparison.Ordinal)
            || leafLower.EndsWith(".mtf", StringComparison.Ordinal)
            || leafLower.EndsWith(".ati", StringComparison.Ordinal)
            || leafLower.EndsWith(".pcl", StringComparison.Ordinal)
            || leafLower.EndsWith(".tre", StringComparison.Ordinal)
            || leafLower.Contains("widget", StringComparison.Ordinal)
            || leafLower.Contains("screenproxy", StringComparison.Ordinal)
            || leafLower.Contains("begin", StringComparison.Ordinal);
        if (!cacheClass)
            return;
        uint readWant = want > EagerPathPayloadMax ? EagerPathPayloadMax : want;
        uint buf = HostBumpAlloc(sys, readWant + 64);
        if (buf == 0)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                Console.Error.WriteLine(
                    $"[VEXX] cache-open bump fail want=0x{readWant:X} \"{norm}\" cyc={sys.Scheduler.MasterCycles}");
            return;
        }
        try
        {
            mods.FileSeek(fd, 0, 0);
            // Stream member FileRead may cap ~2MB per call — loop until full want.
            int total = 0;
            while ((uint)total < readWant)
            {
                uint chunk = readWant - (uint)total;
                if (chunk > 0x200000u) chunk = 0x200000u; // 2MiB slices
                int n = mods.FileRead(sys.Memory, fd, buf + (uint)total, chunk);
                if (n <= 0)
                    break;
                total += n;
                if ((uint)n < chunk)
                    break; // EOF
            }
            // Rewind so a later retail HostCdRead still works if issued.
            mods.FileSeek(fd, 0, 0);
            if (total <= 0)
            {
                if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                    Console.Error.WriteLine(
                        $"[VEXX] cache-open read fail n={total} \"{norm}\" cyc={sys.Scheduler.MasterCycles}");
                return;
            }
            sys.Cdvd.NoteHostReadSectors((total + 2047) / 2048);
            _pathPayloadCache[norm] = (buf, (uint)total);
            string leaf = System.IO.Path.GetFileName(norm.Replace('/', '\\'));
            if (leaf.Length >= 4)
                _pathPayloadCache[leaf] = (buf, (uint)total);
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                && (total >= 256 || leaf.Contains("widget", StringComparison.OrdinalIgnoreCase)
                    || leaf.Contains("screenproxy", StringComparison.OrdinalIgnoreCase)
                    || leaf.EndsWith(".atr", StringComparison.OrdinalIgnoreCase)))
                Console.Error.WriteLine(
                    $"[VEXX] cache-open \"{norm}\" buf=0x{buf:X} n={total}/{want} cdvd={sys.Cdvd.SectorsRead} " +
                    $"cyc={sys.Scheduler.MasterCycles}");
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                Console.Error.WriteLine(
                    $"[VEXX] cache-open ex={ex.GetType().Name}:{ex.Message} \"{norm}\" cyc={sys.Scheduler.MasterCycles}");
        }
    }

    /// <summary>
    /// PL-032o: attach cached open payload to path stub (+0x10 payload, +0x14 size).
    /// </summary>
    private void MaybeEagerReadPathPayload(Ps2System sys, string norm, uint stub,
        out uint payload, out uint payloadN)
    {
        payload = 0;
        payloadN = 0;
        if (stub == 0 || string.IsNullOrEmpty(norm))
            return;
        try
        {
            uint existing = sys.Memory.Read32(stub + 0x10);
            uint existingN = sys.Memory.Read32(stub + 0x14);
            if (existing != 0 && existingN > 0)
            {
                payload = existing;
                payloadN = existingN;
                return;
            }
        }
        catch { /* ignore */ }

        string leaf = System.IO.Path.GetFileName(norm.Replace('/', '\\'));
        if (!_pathPayloadCache.TryGetValue(norm, out var cached)
            && !_pathPayloadCache.TryGetValue(leaf, out cached))
        {
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                Console.Error.WriteLine(
                    $"[VEXX] eager fail no-cache \"{norm}\" cyc={sys.Scheduler.MasterCycles}");
            return;
        }
        payload = cached.baseAddr;
        payloadN = cached.size;
        try
        {
            sys.Memory.Write32(stub + 0x10, payload);
            sys.Memory.Write32(stub + 0x14, payloadN);
        }
        catch { /* ignore */ }
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine(
                $"[VEXX] eager attach \"{norm}\" stub=0x{stub:X} buf=0x{payload:X} n={payloadN} " +
                $"cyc={sys.Scheduler.MasterCycles}");
    }

    private void NoteRecentOpenPath(string raw, string path)
    {
        string n = NormalizeOpenPath(raw.Length > 0 ? raw : path);
        if (n.Length < 4)
            return;
        int i = _recentOpenPathCount % _recentOpenPaths.Length;
        _recentOpenPaths[i] = n;
        if (_recentOpenPathCount < _recentOpenPaths.Length)
            _recentOpenPathCount++;
        string leaf = System.IO.Path.GetFileName(n.Replace('/', '\\'));
        if (leaf.Length >= 4 && !leaf.Equals(n, StringComparison.OrdinalIgnoreCase))
        {
            int j = (_recentOpenPathCount) % _recentOpenPaths.Length;
            _recentOpenPaths[j] = leaf;
            if (_recentOpenPathCount < _recentOpenPaths.Length)
                _recentOpenPathCount++;
        }
    }

    private static string NormalizeOpenPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        p = p.Replace('/', '\\').Trim();
        if (p.StartsWith("y:\\", StringComparison.OrdinalIgnoreCase))
            p = p[3..];
        if (p.StartsWith("host:", StringComparison.OrdinalIgnoreCase))
            p = p[5..];
        while (p.StartsWith(".\\", StringComparison.Ordinal))
            p = p[2..];
        return p;
    }

    private bool IsRecentOpenPath(string norm)
    {
        if (string.IsNullOrEmpty(norm)) return false;
        string leaf = System.IO.Path.GetFileName(norm.Replace('/', '\\'));
        for (int i = 0; i < _recentOpenPaths.Length; i++)
        {
            string? c = _recentOpenPaths[i];
            if (string.IsNullOrEmpty(c)) continue;
            if (c.Equals(norm, StringComparison.OrdinalIgnoreCase)) return true;
            if (c.Equals(leaf, StringComparison.OrdinalIgnoreCase)) return true;
            if (norm.EndsWith(c, StringComparison.OrdinalIgnoreCase)) return true;
            if (c.EndsWith(norm, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// PL-032p: plant host no-op methods + actor-ish vtable once (covers thunk slot 0x298).
    /// Layout lives past HostCdStubEnd so CD stubs are untouched.
    /// </summary>
    private void EnsureHostActorVtable(Ps2System sys)
    {
        if (_hostActorVtablePlanted)
            return;
        try
        {
            // jr ra ; addu v0, zero, zero
            sys.Memory.Write32(HostNopRet0 + 0, 0x03E00008u);
            sys.Memory.Write32(HostNopRet0 + 4, 0x00001021u);
            // jr ra ; addiu v0, zero, 1
            sys.Memory.Write32(HostNopRet1 + 0, 0x03E00008u);
            sys.Memory.Write32(HostNopRet1 + 4, 0x24020001u);
            // jr ra ; move v0, a0  (return this)
            sys.Memory.Write32(HostNopRetThis + 0, 0x03E00008u);
            sys.Memory.Write32(HostNopRetThis + 4, 0x00801021u); // addu v0, a0, zero

            for (uint o = 0; o < HostActorVtableBytes; o += 4)
            {
                // Prefer ret-this for low slots (often identity/getters); ret0 for high slots.
                uint target = o < 0x80 ? HostNopRetThis : HostNopRet0;
                // Common "is ready / count" slots: return 1 so wait loops can proceed.
                if (o is 0x58 or 0xF0 or 0x298 or 0x29C or 0x2A0)
                    target = HostNopRet1;
                sys.Memory.Write32(HostActorVtable + o, target);
            }
            _hostActorVtablePlanted = true;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                Console.Error.WriteLine(
                    $"[VEXX] host actor vtable @0x{HostActorVtable:X} nop0=0x{HostNopRet0:X} " +
                    $"slot298=0x{HostNopRet1:X}");
        }
        catch
        {
            /* leave unplanted; thunk escape still covers residual */
        }
    }

    private uint EnsurePathObjectStub(Ps2System sys, string norm, uint needlePtr)
    {
        EnsureHostActorVtable(sys);
        if (_pathObjectStubs.TryGetValue(norm, out uint existing) && existing != 0)
        {
            FillPathObjectStubFields(sys, existing, needlePtr, refreshPayload: true, norm);
            return existing;
        }
        uint stub = HostBumpAlloc(sys, PathObjectStubSize);
        if (stub == 0)
            return 0;
        for (uint o = 0; o < PathObjectStubSize; o += 4)
        {
            try { sys.Memory.Write32(stub + o, 0); } catch { break; }
        }
        FillPathObjectStubFields(sys, stub, needlePtr, refreshPayload: true, norm);
        _pathObjectStubs[norm] = stub;
        string leaf = System.IO.Path.GetFileName(norm.Replace('/', '\\'));
        if (leaf.Length >= 4 && !leaf.Equals(norm, StringComparison.OrdinalIgnoreCase))
            _pathObjectStubs[leaf] = stub;
        return stub;
    }

    /// <summary>
    /// PL-032p: retail path-object layout (ctor 0x21CDB0, size 0x104):
    /// +0x0/+0x4 zero, +0xC name, +0x10 payload base, +0x14 payload size,
    /// +0x28 host vtable (actor-compat for 0x11Cxxx thunks), +0xF0 loaded-flag=0,
    /// +0xFC/+0x100 ref list heads empty. Empty-string sentinel at 0x3C4C60 used by retail
    /// ctor for unset string fields — we keep zeros so strcmp needle match is via +0xC only.
    /// </summary>
    private void FillPathObjectStubFields(Ps2System sys, uint stub, uint needlePtr,
        bool refreshPayload, string norm)
    {
        try
        {
            if (needlePtr != 0 && IsReadableCStringPtr(sys, needlePtr))
                sys.Memory.Write32(stub + 0xC, needlePtr);
            // Actor-compat vtable so 0x11C200-class thunks jr into host nop instead of null.
            if (_hostActorVtablePlanted)
                sys.Memory.Write32(stub + 0x28, HostActorVtable);
            // Already-loaded flag must stay 0 so caller 0x2A2AEC processes the object.
            sys.Memory.Write32(stub + 0xF0, 0);
            sys.Memory.Write32(stub + 0xFC, 0);
            sys.Memory.Write32(stub + 0x100, 0);
            // 1.0f scale defaults like ctor (0x3F800000 at +0x94/+0x98/+0x9C).
            sys.Memory.Write32(stub + 0x94, 0x3F800000u);
            sys.Memory.Write32(stub + 0x98, 0x3F800000u);
            sys.Memory.Write32(stub + 0x9C, 0x3F800000u);

            if (refreshPayload && !string.IsNullOrEmpty(norm))
            {
                string leaf = System.IO.Path.GetFileName(norm.Replace('/', '\\'));
                if (_pathPayloadCache.TryGetValue(norm, out var cached)
                    || _pathPayloadCache.TryGetValue(leaf, out cached))
                {
                    sys.Memory.Write32(stub + 0x10, cached.baseAddr);
                    sys.Memory.Write32(stub + 0x14, cached.size);
                }
            }
        }
        catch { /* ignore partial */ }
    }

    /// <summary>
    /// PL-032p: host-complete the 0x11Cxxx vtable thunk family when a0 is null/garbage or
    /// +0x28 is not a readable vtable. Open-bus rescue falsely re-homes to 0x11C200 (thunk)
    /// which re-nulls forever after FORCE-MATCH. Return via ra with v0=0 (safe no-op method).
    /// </summary>
    private bool MaybeEscapeNullVtableThunk(Ps2System sys, uint pc)
    {
        if (pc is < VtableThunkLo or >= VtableThunkHi)
            return false;
        // Only after path assets / force-match era — do not intercept early CRT-ish code.
        if (!_beginMtfOpened && _nameSearchForceMatches == 0 && _hostMemberOpens < 30)
            return false;

        uint a0 = (uint)(sys.EE.GetGpr(4).Lo & 0x1FFFFFFFu);
        bool badThis = a0 < 0x00100000u || a0 + 0x30u >= SystemMemory.RDRAM_SIZE;
        uint vt = 0;
        if (!badThis)
        {
            try { vt = sys.Memory.Read32(a0 + 0x28) & 0x1FFFFFFFu; }
            catch { badThis = true; }
        }
        bool badVt = badThis
            || vt < 0x00100000u
            || vt + 0x2A0u >= SystemMemory.RDRAM_SIZE
            || vt is >= 0x10000000u and < 0x20000000u;

        // If our host vtable is live, let the real thunk run (jr → HostNopRet*).
        if (!badVt && vt == (HostActorVtable & 0x1FFFFFFFu))
            return false;
        if (!badVt)
        {
            // Probe method slot at residual offset 0x298 — if non-zero code-ish, let retail run.
            try
            {
                uint meth = sys.Memory.Read32(vt + 0x298) & 0x1FFFFFFFu;
                if (meth is >= 0x00100000u and < 0x00460000u && (meth & 3) == 0)
                    return false;
            }
            catch { /* treat as bad */ }
        }

        // Host-complete: return to caller as a no-op virtual method (v0=0).
        // Reject FPU mid-body "ra" (0x32xxxx COP1 dens) and thunk-band — open-bus leaves
        // garbage $ra that previously re-entered float code @0x325964 (PL-032p2).
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu);
        if (!IsSafeThunkReturn(sys, ra))
        {
            ra = 0;
            uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
            if (sp is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0x80u)
            {
                foreach (uint off in new uint[] { 0x00, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80 })
                {
                    try
                    {
                        uint cand = sys.Memory.Read32(sp + off) & 0x1FFFFFFFu;
                        if (IsSafeThunkReturn(sys, cand))
                        {
                            ra = cand;
                            break;
                        }
                    }
                    catch { /* next */ }
                }
            }
        }
        if (ra == 0)
        {
            // Yield helper used by ready-flag wait — safe no-op progress without CRT0 re-entry.
            ra = 0x001D18A0;
            if (!sys.Memory.IsLikelyEeCode(ra))
                ra = 0x00100008;
        }

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0 = 0
        sys.EE.PC = ra;
        _vtableThunkEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && _vtableThunkEscapes <= 24)
            Console.Error.WriteLine(
                $"[VEXX] vtable-thunk escape #{_vtableThunkEscapes} pc=0x{pc:X} a0=0x{a0:X} " +
                $"vt=0x{vt:X} → ra=0x{ra:X} cyc={sys.Scheduler.MasterCycles}");
        return true;
    }

    /// <summary>
    /// PL-032p/q: after widget.atr MEMBER open the EE sits in SIF RPC bind-retry for
    /// <b>CD_BASE 0x80000592</b> (fn 0x1B1198): <c>jal 0x1C5018</c> returns &lt;0 or
    /// cbuf==0 → 0x100000-cycle delay → loop @0x1B1260.
    /// Skip pure delay nops; after several retries <b>host-complete</b> the CD client and
    /// take retail success path <c>0x1B12C4</c> so CdInit CallRpc runs (not fail-return v0=0).
    /// Also soft-bind AAAIOP client 0x4455D0 / sid 0x54323. No invent PATH3.
    /// </summary>
    private bool MaybeEscapeSifRpcBindWait(Ps2System sys, uint pc)
    {
        if (pc is < SifRpcBindWaitLo or > SifRpcBindWaitHi)
            return false;
        if (!_beginMtfOpened || _hostMemberOpens < 38)
            return false;

        // Only break the pure countdown bodies — leave real jal/bind to run once per retry.
        bool inDelay = pc is >= SifRpcBindDelayA and <= (SifRpcBindDelayA + 0x28)
            || pc is >= SifRpcBindDelayB and <= (SifRpcBindDelayB + 0x28);
        if (!inDelay)
            return false;

        _sifRpcBindWaitEscapes++;
        if (_sifRpcBindWaitEscapes <= 4)
        {
            // Skip one 0x100000-iter delay body → natural retry of jal 0x1C5018.
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0xFFFFFFFFu });
            sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = 0xFFFFFFFFu });
            // Land on the beq-back after the delay bne.
            sys.EE.PC = inDelay && pc >= SifRpcBindDelayB
                ? 0x001B1344u
                : 0x001B12BCu;
        }
        else
        {
            // PL-032q hard leave: force success path, not fail-return.
            // Retail after bind: beq v0,zero,fail @0x1B12C4 with v0=cbuf (client+0x24).
            var rpc = sys.Hle?.Sony?.RealRpc;
            if (rpc != null)
            {
                rpc.HostCompleteBind(sys.Memory, SifCdBaseClient, RealSifRpc.SidCdBase);
                rpc.HostCompleteBind(sys.Memory, SifAaaIopClient, RealSifRpc.SidAaaIop);
            }
            else
            {
                // Minimal plant without RealSifRpc (should not happen on commercial path).
                try
                {
                    sys.Memory.Write32(SifCdBaseClient + 20, 0x000B0100u);
                    sys.Memory.Write32(SifCdBaseClient + 24, 0x000B0200u);
                    sys.Memory.Write32(SifCdBaseClient + 36, RealSifRpc.SidCdBase);
                }
                catch { /* ignore */ }
            }

            uint cbuf = 0;
            try { cbuf = sys.Memory.Read32(SifCdBaseClient + 24); } catch { /* ignore */ }
            if (cbuf == 0) cbuf = 0x000B0200u;

            // Keep s1 = CD client for CallRpc setup on the success path.
            sys.EE.SetGpr(17, new EmotionEngine.Gpr128 { Lo = SifCdBaseClient }); // s1
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = cbuf }); // v0 = cbuf ≠ 0
            // Clear "init failed" flag word that call-fail path zeros (0x3B9FE4).
            try { sys.Memory.Write32(0x003B9FE4u, 1u); } catch { /* ignore */ }
            sys.EE.PC = SifRpcBindSuccess;
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && _sifRpcBindWaitEscapes <= 16)
            Console.Error.WriteLine(
                $"[VEXX] sif-rpc bind-wait escape #{_sifRpcBindWaitEscapes} pc=0x{pc:X} → 0x{sys.EE.PC:X} " +
                $"cyc={sys.Scheduler.MasterCycles}");
        return true;
    }

    /// <summary>
    /// PL-032q: AAAIOP post-bind server poll at 0x38d230 — <c>lw v0,0x4455F4; beq v0,zero,retry</c>.
    /// Host-complete client so cbuf is non-zero and continue to CallRpc fno=0x10.
    /// </summary>
    private bool MaybeEscapeAaaIopServerWait(Ps2System sys, uint pc)
    {
        if (pc is < AaaIopServerWaitLo or > AaaIopServerWaitHi)
            return false;
        if (!_beginMtfOpened)
            return false;

        var rpc = sys.Hle?.Sony?.RealRpc;
        rpc?.HostCompleteBind(sys.Memory, SifAaaIopClient, RealSifRpc.SidAaaIop);

        uint cbuf = 0;
        try { cbuf = sys.Memory.Read32(SifAaaIopClient + 24); } catch { /* ignore */ }
        if (cbuf == 0)
        {
            try
            {
                sys.Memory.Write32(SifAaaIopClient + 24, 0x000B0300u);
                sys.Memory.Write32(SifAaaIopClient + 36, RealSifRpc.SidAaaIop);
                cbuf = 0x000B0300u;
            }
            catch { return false; }
        }

        // Land past the beq-zero retry (0x38d264 continues to CallRpc fno=0x10).
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = cbuf });
        sys.EE.PC = 0x0038D264u;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine(
                $"[VEXX] AAAIOP server-wait escape pc=0x{pc:X} cbuf=0x{cbuf:X} → 0x38D264 " +
                $"cyc={sys.Scheduler.MasterCycles}");
        return true;
    }

    /// <summary>Reject thunk-band, name-search thrash, and COP1-dense float bodies as return targets.</summary>
    private static bool IsSafeThunkReturn(Ps2System sys, uint ra)
    {
        if (ra is < 0x00100000u or >= 0x00460000u || (ra & 3) != 0)
            return false;
        if (ra is >= VtableThunkLo and < VtableThunkHi)
            return false;
        if (ra is >= NameSearchFunc and <= PostPrThrashBandHi)
            return false;
        // Float-expand / COP1 bodies around 0x32xxxx–0x33xxxx often sit in $ra after open-bus.
        if (ra is >= 0x00320000u and < 0x00340000u)
            return false;
        if (!sys.Memory.IsLikelyEeCode(ra))
            return false;
        // Prefer returns that look like post-jal sites (not mid-COP1): reject if word is COP1.
        try
        {
            uint w = sys.Memory.Read32(ra);
            if (((w >> 26) & 0x3F) == 0x11) // COP1
                return false;
        }
        catch { return false; }
        return true;
    }

    /// <summary>
    /// PL-032m: host-complete case-fold strcmp at <c>0x1CF410</c> when a0/a1 are not valid
    /// RAM C-strings (would UnknownMmioRead). Return non-match (v0≠0) via caller ra —
    /// not <c>jr ra</c>@0x1CF448 whose delay <c>subu v0,v1,v0</c> would clobber v0.
    /// </summary>
    private bool MaybeHostCompleteNameStrcmp(Ps2System sys, uint pc)
    {
        // Cover entry through the tight compare loop (not the whole 0x1CFxxx family).
        if (pc is < NameStrcmpEntry or > (NameStrcmpEntry + 0x40))
            return false;
        if (!_begin0TreLoaded && !_beginPclLoaded && _hostMemberOpens < 30)
            return false;

        uint a0 = (uint)(sys.EE.GetGpr(4).Lo & 0x1FFFFFFFu);
        uint a1 = (uint)(sys.EE.GetGpr(5).Lo & 0x1FFFFFFFu);
        bool a0Ok = IsReadableCStringPtr(sys, a0);
        bool a1Ok = IsReadableCStringPtr(sys, a1);
        if (a0Ok && a1Ok)
            return false; // let retail strcmp run

        // Bad pointer(s): force non-match so the name-search loop advances.
        // Return via ra (0x2243A8 delay of jal) — NameStrcmpJrRa delay reclobbers v0.
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 }); // v0 ≠ 0 → no match
        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu);
        if (ra is >= 0x00100000u and < 0x00460000u && (ra & 3) == 0)
            sys.EE.PC = ra;
        else
        {
            // Seed delay-slot inputs so subu v0,v1,v0 yields non-zero if we must jr.
            sys.EE.SetGpr(3, new EmotionEngine.Gpr128 { Lo = 1 }); // v1
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0; delay → v0=1
            sys.EE.PC = NameStrcmpJrRa;
        }
        _postPrResidualEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && _postPrResidualEscapes <= 32)
        {
            string needle = a1Ok ? ReadCString(sys, a1, 96) : "";
            Console.Error.WriteLine(
                $"[VEXX] name-strcmp host-complete #{_postPrResidualEscapes} a0=0x{a0:X} a1=0x{a1:X} " +
                $"a0ok={(a0Ok ? 1 : 0)} a1ok={(a1Ok ? 1 : 0)} needle=\"{needle}\" ret=0x{sys.EE.PC:X} " +
                $"cyc={sys.Scheduler.MasterCycles}");
        }
        return true;
    }

    /// <summary>
    /// PL-032m: at name-search <c>lw a0,0xC(s5)</c>, if s5 or name ptr is garbage, skip the
    /// slot (continue loop) instead of jaling strcmp into MMIO thrash.
    /// </summary>
    private bool MaybeSkipBadNameSearchSlot(Ps2System sys, uint pc)
    {
        if (pc != NameSearchLwA0 && pc != NameSearchLwA0 + 4)
            return false;
        if (!_begin0TreLoaded && !_beginPclLoaded && _hostMemberOpens < 30)
            return false;

        uint s5 = (uint)(sys.EE.GetGpr(21).Lo & 0x1FFFFFFFu);
        if (!IsReadableObjectPtr(sys, s5))
        {
            // Advance past match-store: no-match path at addiu s2 / s1.
            sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
            sys.EE.PC = NameSearchContinue;
            _postPrResidualEscapes++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
                && _postPrResidualEscapes <= 32)
                Console.Error.WriteLine(
                    $"[VEXX] name-search skip bad s5=0x{s5:X} → cont cyc={sys.Scheduler.MasterCycles}");
            return true;
        }

        uint namePtr;
        try { namePtr = sys.Memory.Read32(s5 + 0xC) & 0x1FFFFFFFu; }
        catch { namePtr = 0; }
        if (IsReadableCStringPtr(sys, namePtr))
            return false; // retail path OK

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 1 });
        sys.EE.PC = NameSearchContinue;
        _postPrResidualEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && _postPrResidualEscapes <= 32)
            Console.Error.WriteLine(
                $"[VEXX] name-search skip bad namePtr=0x{namePtr:X} s5=0x{s5:X} → cont " +
                $"cyc={sys.Scheduler.MasterCycles}");
        return true;
    }

    private static bool IsReadableObjectPtr(Ps2System sys, uint p)
    {
        if (p < 0x00100000u || p + 0x10u >= SystemMemory.RDRAM_SIZE)
            return false;
        // Reject MMIO windows and high-stack path scratch as object bases.
        if (p is >= 0x10000000u and < 0x20000000u)
            return false;
        if (p is >= 0x01F00000u and < 0x02000000u)
            return false;
        return true;
    }

    private static bool IsReadableCStringPtr(Ps2System sys, uint p)
    {
        if (p < 0x00100000u || p + 4u >= SystemMemory.RDRAM_SIZE)
            return false;
        if (p is >= 0x10000000u and < 0x20000000u)
            return false;
        try
        {
            // At least one printable or NUL in first 4 bytes (empty string OK for strcmp).
            byte b0 = sys.Memory.Read8(p);
            if (b0 == 0) return true;
            if (b0 is >= 0x20 and <= 0x7E) return true;
            // Allow path-ish dollars / backslashes already covered by printable.
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// PL-032k/l: last-resort leave of residual thrash in 0x224xxx after screenproxy.atr.
    /// PL-032m: prefer name-strcmp/slot skips; only abort via natural epilogue after many misses.
    /// </summary>
    private bool MaybeEscapePostPrResidual(Ps2System sys, uint pc)
    {
        bool inTight = pc is >= PostPrResidualLo and <= PostPrResidualHi
            || pc is >= NameSearchFunc and <= PostPrHardLeavePc + 0x28;
        bool inBand = pc is >= PostPrThrashBandLo and <= PostPrThrashBandHi;
        if (!inBand)
        {
            if (_postPrResidualHits > 0 && pc is < PostPrThrashBandLo - 0x80 or > PostPrThrashBandHi + 0x80)
                _postPrResidualHits = 0;
            return false;
        }
        if (!_begin0TreLoaded && !_beginPclLoaded && _hostMemberOpens < 30)
            return false;
        _postPrResidualHits++;
        // High gate: slot-skip / strcmp host should handle the common path first.
        int hitGate = _beginMtfOpened ? 64 : (inTight ? 128 : 256);
        if (_postPrResidualHits < hitGate)
            return false;

        uint resume = PickPostPrResume(sys);
        // Miss-return: s0/v0 = null pointer so callers do not treat 1 as an object*.
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 }); // s0
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 });  // v0
        sys.EE.PC = resume;
        _postPrResidualEscapes++;
        _postPrResidualHits = 0;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1"
            && _postPrResidualEscapes <= 24)
            Console.Error.WriteLine(
                $"[VEXX] post-PR residual escape #{_postPrResidualEscapes} from=0x{pc:X} → 0x{resume:X} " +
                $"v0=0 beginMtf={(_beginMtfOpened ? 1 : 0)} members={_hostMemberOpens} " +
                $"cyc={sys.Scheduler.MasterCycles}");
        return true;
    }

    /// <summary>
    /// PL-032l/m: resume OUT of thrash body. Prefer loop-continue; then ra / deep stack
    /// (sp+0x60 holds saved ra of 0x224360); last resort natural epilogue (never 0x225004).
    /// </summary>
    private uint PickPostPrResume(Ps2System sys)
    {
        static bool IsCodeOutOfBand(uint p) =>
            p is >= 0x00100000u and < 0x00460000u
            && (p < PostPrThrashBandLo || p > PostPrThrashBandHi);

        // Prefer continuing the name-search loop over aborting it.
        if (_postPrResidualEscapes < 48)
            return NameSearchContinue;

        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu);
        if (IsCodeOutOfBand(ra))
            return ra;

        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
        if (sp >= 0x1000u && sp + 0x80 < SystemMemory.RDRAM_SIZE)
        {
            // 0x224360 frame: sd ra @ sp+0x60 (ELF FFBF0060).
            ReadOnlySpan<uint> offs = stackalloc uint[]
            {
                0, 4, 0x10, 0x14, 0x1C, 0x20, 0x24, 0x28, 0x30, 0x34, 0x38, 0x3C,
                0x40, 0x48, 0x50, 0x58, 0x60, 0x68, 0x70, 0x78,
            };
            foreach (uint off in offs)
            {
                try
                {
                    uint cand = sys.Memory.Read32(sp + off) & 0x1FFFFFFFu;
                    if (IsCodeOutOfBand(cand))
                        return cand;
                }
                catch
                {
                    // ignore bad stack slots
                }
            }
        }

        // Last resort: natural miss epilogue of name-search (not mid-next-fn 0x225004).
        return PostPrHardLeavePc;
    }

    /// <summary>
    /// PL-032 / PL-032f / PL-032j BADARGS recovery: pick a writable EE buffer for bulk
    /// host-read when a1/a2 are poison. PL-032j minimal delta over PL-032i:
    /// (1) ring prior package-era dests so tiny stubs cannot clear demotion of atr;
    /// (2) prefer s2/sp+0x10 freelist when sp+0x30 is a prior package (pcl after atr);
    /// (3) reject PT_LOAD code-band package dests (deity → 0x1F609C).
    /// Keep PL-032i scores so atr@0x672C10 / swooshes@0x446610 still win first load.
    /// </summary>
    private uint RecoverReadBuffer(Ps2System sys, int handle, IopModuleHost mods, ref uint size)
    {
        uint want = size;
        if (want == 0 || want > HostReadMaxBytes)
            want = HostReadMaxBytes;
        if (_hostFdSizes.TryGetValue(handle, out uint known) && known > 0)
            want = Math.Min(want, known);
        else if (_hostFds.TryGetValue(handle, out int fd)
                 && mods.TryGetOpenFileSize(fd, out uint fsz) && fsz > 0)
            want = Math.Min(want, fsz);
        if (want == 0 || want > HostReadMaxBytes)
            want = 0x1000;
        size = want;

        uint best = 0;
        int bestScore = -1;

        // Retail EE stack ≈0x01FE0000+. Actor packs mid-RDRAM after ELF (~0x446000–0x1800000).
        // CRT bump arena 0x1800000+. PT_LOAD code ~0x100000–0x445F00 never hosts packages.
        static bool IsHighStack(uint p) => p is >= 0x01F00000u and < 0x02000000u;
        static bool IsCodeBand(uint p) => p is >= 0x00100000u and < 0x00446000u;
        bool IsGameHeap(uint p) => p is >= 0x00446000u and < BumpArenaBase;
        // True freelist only — exclude EE high stack which sits inside the 0x1800000–0x2800000
        // range on paper (0x01F0_0000+) but is never a payload dest.
        bool IsFreelistBump(uint p) =>
            p >= BumpArenaBase && p < BumpArenaEnd && !IsHighStack(p);

        bool IsPriorPackageBuf(uint p)
        {
            p &= 0x1FFFFFFFu;
            if (p == 0) return false;
            int nPkg = Math.Min(_packageBufCount, _packageBufRing.Length);
            for (int i = 0; i < nPkg; i++)
            {
                if (_packageBufRing[i] == p && _packageSzRing[i] >= 64)
                    return true;
            }
            return false;
        }

        void Consider(uint p, int score)
        {
            p &= 0x1FFFFFFFu;
            if (p < 0x1000u || p >= SystemMemory.RDRAM_SIZE) return;
            if (want > SystemMemory.RDRAM_SIZE - p) return;
            if ((p & 0xF) != 0) score -= 2;
            if (IsHighStack(p)) score -= 40;
            // PL-032j: package payloads must not land in PT_LOAD (deity → 0x1F609C).
            if (want >= 64 && IsCodeBand(p))
                score -= 250;
            if (_lastRecoveredBuf != 0 && p == _lastRecoveredBuf)
                score -= _lastRecoveredSize >= 64 ? 120 : 50;
            // PL-032j: demote ANY prior package-era buffer (survives tiny-stub last-recover).
            if (want >= 64 && IsPriorPackageBuf(p))
                score -= 160;
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        bool packageEra = _swooshesLoaded || _beginPclLoaded || _hostMemberOpens >= 28;

        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
        uint at30 = 0;
        if (sp is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0x40u)
            at30 = sys.Memory.Read32(sp + 0x30) & 0x1FFFFFFFu;
        bool at30IsPriorPkg = want >= 64 && IsPriorPackageBuf(at30);

        // 0) PL-032j: when sp+0x30 is a prior package (pcl after atr), prefer fresh s2
        //    freelist alloc (pcl dump: s2 == sp+0x10 == 0x18C0878).
        //    Reject high-stack (swooshes s2=0x1FEFB10 looks freelist-ranged but is SP).
        if (packageEra && want >= 64 && at30IsPriorPkg)
        {
            uint s2 = (uint)(sys.EE.GetGpr(18).Lo & 0x1FFFFFFFu);
            if (IsFreelistBump(s2) && !IsHighStack(s2) && s2 + want <= BumpArenaEnd
                && !IsPriorPackageBuf(s2))
                Consider(s2, 200);
            else if (IsGameHeap(s2) && s2 + want <= SystemMemory.RDRAM_SIZE
                     && !IsPriorPackageBuf(s2) && !IsCodeBand(s2))
                Consider(s2, 200);
        }

        // 1) Fresh freelist bump (fontindex/history path). PL-032i scores.
        bool freelistTight = _lastBumpBase != 0 && _lastBumpSize >= want
            && _lastBumpSize <= Math.Max(want * 4u, want + 0x800u)
            && _lastBumpBase >= BumpArenaBase && _lastBumpBase + want <= BumpArenaEnd;
        if (freelistTight)
        {
            int flScore = want < 64 ? 140
                : packageEra ? 70
                : 130;
            Consider(_lastBumpBase, flScore);
        }

        // 2) PL-032f/i sp+0x30 retail dest (begin.atr → 0x672C10). Score 180 as PL-032i.
        //    PL-032j: extra demotion when this slot is a prior package (pcl must not reclaim).
        if (sp is >= 0x1000 and < SystemMemory.RDRAM_SIZE - 0x40u)
        {
            if (IsGameHeap(at30) && at30 + want <= SystemMemory.RDRAM_SIZE && !IsCodeBand(at30))
            {
                if (packageEra && want >= 64)
                {
                    int score = 180;
                    if ((at30 & 0xF) != 0) score -= 2;
                    if (_lastRecoveredBuf == at30 && _lastRecoveredSize >= 64)
                        score -= 120;
                    if (at30IsPriorPkg)
                        score -= 160; // → 20; freelist s2@200 / sp+0x10@155 win
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = at30;
                    }
                }
                else if (want >= 64)
                    Consider(at30, 150);
            }

            uint spEnd = Math.Min(sp + 0x120u, SystemMemory.RDRAM_SIZE);
            for (uint a = sp; a + 4 <= spEnd; a += 4)
            {
                if (a == sp + 0x30) continue;
                uint p = sys.Memory.Read32(a) & 0x1FFFFFFFu;
                // PL-032i: game-heap SP (swooshes sp+0x10=0x446610) score 155.
                // Freelist SP (pcl sp+0x10) score 155 when package-era after prior-pkg demotion.
                if (IsGameHeap(p) && p + want <= SystemMemory.RDRAM_SIZE && want >= 64
                    && !IsPriorPackageBuf(p) && !IsCodeBand(p))
                    Consider(p, freelistTight && !packageEra ? 90 : 155);
                else if (IsFreelistBump(p) && p + want <= BumpArenaEnd && want >= 64
                         && at30IsPriorPkg && !IsPriorPackageBuf(p))
                    Consider(p, 155); // pcl freelist after atr kept
                else if (IsFreelistBump(p) && p + want <= BumpArenaEnd)
                    Consider(p, 70);
            }
        }

        // 3) Recent freelist bumps.
        int n = Math.Min(_recentBumpCount, _recentBumpBases.Length);
        for (int i = 0; i < n; i++)
        {
            int slot = (_recentBumpCount - 1 - i) % _recentBumpBases.Length;
            if (slot < 0) slot += _recentBumpBases.Length;
            uint baseAddr = _recentBumpBases[slot];
            uint bsz = _recentBumpSizes[slot];
            if (baseAddr >= BumpArenaBase && baseAddr + want <= BumpArenaEnd)
                Consider(baseAddr, (bsz >= want && bsz <= Math.Max(want * 4u, want + 0x800u) ? 100 : 70) - i);
        }

        // 4) Callee-saved + temps. Skip code-band for package-era real payloads.
        for (int r = 4; r <= 23; r++)
        {
            uint p = (uint)(sys.EE.GetGpr(r).Lo & 0x1FFFFFFFu);
            if (IsGameHeap(p))
                Consider(p, 85);
            else if (IsFreelistBump(p) && p + want <= BumpArenaEnd)
                Consider(p, 70);
            else if (!IsCodeBand(p) && p is >= 0x00100000u and < SystemMemory.RDRAM_SIZE)
                Consider(p, packageEra && want >= 64 ? 10 : 30);
        }

        // PL-032j: drop prior-package / code-band winners so host-bump can take deity.
        if (best != 0 && want >= 64
            && (IsCodeBand(best) || (IsPriorPackageBuf(best) && bestScore < 80)))
        {
            best = 0;
            bestScore = -1;
        }

        if (best != 0)
        {
            sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = best });
            _lastRecoveredBuf = best;
            _lastRecoveredSize = want;
            if (packageEra && want >= 64)
                NotePackageBuffer(best, want);
            return best;
        }

        // 5) Host bump last resort — package-era deity (sp+0x30=0) must not hit code-band.
        uint alloc = HostBumpAlloc(sys, want + 64);
        if (alloc != 0)
        {
            NoteBumpBase(alloc, want + 64);
            sys.EE.SetGpr(5, new EmotionEngine.Gpr128 { Lo = alloc });
            _lastRecoveredBuf = alloc;
            _lastRecoveredSize = want;
            if (packageEra && want >= 64)
                NotePackageBuffer(alloc, want);
            return alloc;
        }
        return 0;
    }

    /// <summary>PL-032j: remember real-package recover destinations (≥64B) for demotion.</summary>
    private void NotePackageBuffer(uint buf, uint size)
    {
        if (buf == 0 || size < 64) return;
        buf &= 0x1FFFFFFFu;
        int slot = _packageBufCount % _packageBufRing.Length;
        _packageBufRing[slot] = buf;
        _packageSzRing[slot] = size;
        _packageBufCount++;
    }

    private bool HostCdClose(Ps2System sys, IopModuleHost mods)
    {
        int handle = (int)sys.EE.GetGpr(4).Lo;
        if (_hostFds.TryGetValue(handle, out int fd))
        {
            mods.FileClose(fd);
            _hostFds.Remove(handle);
            _hostFdSizes.Remove(handle);
            _hostCloses++;
            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _hostCloses <= 24)
                Console.Error.WriteLine(
                    $"[VEXX] host-close #{_hostCloses} h={handle} cyc={sys.Scheduler.MasterCycles}");
        }
        ReturnHost(sys, 0);
        return true;
    }

    private bool HostCdSeek(Ps2System sys, IopModuleHost mods)
    {
        // seek(fd, off, whence): a0=handle, a1=off, a2=whence (retail wrapper).
        int handle = (int)sys.EE.GetGpr(4).Lo;
        int off = (int)sys.EE.GetGpr(5).Lo;
        int whence = (int)sys.EE.GetGpr(6).Lo;
        if (!_hostFds.TryGetValue(handle, out int fd))
        {
            // tell path uses a0-- before jump; accept 0-based
            if (!_hostFds.TryGetValue(handle + 1, out fd))
            {
                ReturnHost(sys, unchecked((uint)(-1)));
                return true;
            }
        }
        int pos = mods.FileSeek(fd, off, whence);
        _hostSeeks++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _hostSeeks <= 16)
            Console.Error.WriteLine(
                $"[VEXX] host-seek #{_hostSeeks} h={handle} off={off} wh={whence} → {pos} cyc={sys.Scheduler.MasterCycles}");
        ReturnHost(sys, unchecked((uint)pos));
        return true;
    }

    private bool HostCdTell(Ps2System sys, IopModuleHost mods)
    {
        // tell entry: addiu a0,a0,-1 then j seek-like with whence=1 off=0 — handle still 1-based here.
        int handle = (int)sys.EE.GetGpr(4).Lo;
        if (!_hostFds.TryGetValue(handle, out int fd))
        {
            ReturnHost(sys, unchecked((uint)(-1)));
            return true;
        }
        int pos = mods.FileSeek(fd, 0, 1); // SEEK_CUR
        ReturnHost(sys, unchecked((uint)pos));
        return true;
    }

    private bool HostCdSize(Ps2System sys, IopModuleHost mods)
    {
        int handle = (int)sys.EE.GetGpr(4).Lo;
        if (!_hostFds.TryGetValue(handle, out int fd))
        {
            // Size wrappers may a0-- before call (0-based).
            if (!_hostFds.TryGetValue(handle + 1, out fd))
            {
                if (_hostFdSizes.TryGetValue(handle, out uint known0))
                {
                    ReturnHost(sys, known0);
                    return true;
                }
                ReturnHost(sys, 0);
                return true;
            }
            handle = handle + 1;
        }
        if (!mods.TryGetOpenFileSize(fd, out uint sz))
            sz = 0;
        if (sz == 0 && _hostFdSizes.TryGetValue(handle, out uint known))
            sz = known;
        // Cap only multi-100MB TRE roots (STREE0 ~1GB) so malloc takes TOC headroom.
        // Wave-6 members (fonts/textures/mcf) must report real size — do not clamp them.
        if (sz > 100u * 1024 * 1024)
            sz = 0x00492570; // STREE0 TOC byte length from header w1
        if (sz > 0) _hostFdSizes[handle] = sz;
        ReturnHost(sys, sz);
        return true;
    }

    private static void ReturnHost(Ps2System sys, uint v0)
    {
        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = v0 });
        sys.EE.PC = sys.EE.GetGpr(31).Lo;
    }

    /// <summary>
    /// Map retail <c>host:$/stree0.tre</c> / <c>$/Data/…</c> / bare leaves onto ISO open paths.
    /// </summary>
    internal static string NormalizeHostCdPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string path = raw.Trim();
        // Strip device prefixes (open also strcat's "host:" in retail — we intercept before that
        // when a0 is the caller's path; stream open passes "$/stree0.tre" or resolved leaf).
        if (path.StartsWith("host0:", StringComparison.OrdinalIgnoreCase))
            path = path[6..];
        else if (path.StartsWith("host:", StringComparison.OrdinalIgnoreCase))
            path = path[5..];
        else if (path.StartsWith("cdrom0:", StringComparison.OrdinalIgnoreCase))
            path = path[7..];
        else if (path.StartsWith("cdrom:", StringComparison.OrdinalIgnoreCase))
            path = path[6..];
        path = path.TrimStart('\\', '/');
        if (path.StartsWith("$/", StringComparison.Ordinal) || path.StartsWith("$\\", StringComparison.Ordinal))
            path = path[2..];
        else if (path.Length > 0 && path[0] == '$')
            path = path[1..].TrimStart('\\', '/');
        int semi = path.IndexOf(';');
        if (semi >= 0) path = path[..semi];
        // Prefer leaf for ISO root files (STREE0.TRE / GAME.TXT live at disc root).
        string leaf = System.IO.Path.GetFileName(path.Replace('/', '\\'));
        if (!string.IsNullOrEmpty(leaf) && leaf.IndexOf('.') > 0
            && leaf.IndexOfAny(new[] { '/', '\\' }) < 0)
            return leaf;
        return path.Replace('/', '\\');
    }

    /// <summary>
    /// When hash-map lookup runs with a null table pointer, return miss (v0=0) instead of
    /// infinite chain walk / AdEL thrash.
    /// </summary>
    private void MaybeEscapeNullStreamMap(Ps2System sys, uint pc)
    {
        uint s5 = (uint)(sys.EE.GetGpr(21).Lo & 0x1FFFFFFFu); // s5
        uint table = 0;
        if (s5 >= 0x1000 && s5 + 0x20 < SystemMemory.RDRAM_SIZE)
            table = sys.Memory.Read32(s5 + 8);

        // Wave-5: plant host-built STREE0 index before giving up on the lookup.
        if ((table == 0 || table >= SystemMemory.RDRAM_SIZE) && _streamMapTable == 0
            && sys.Cdvd.SectorsRead >= 50UL)
            TryBuildStreamMapFromIso(sys);
        if ((table == 0 || table >= SystemMemory.RDRAM_SIZE) && _streamMapTable != 0)
        {
            MaybePlantStreamMapOnObject(sys, s5);
            table = sys.Memory.Read32(s5 + 8);
            if (table == _streamMapTable)
            {
                // Restart at `lw v1, 8(s5)` so the walk uses the planted table.
                sys.EE.PC = 0x001DD2CCu;
                return;
            }
        }

        bool tableBad = table == 0
            || table >= SystemMemory.RDRAM_SIZE
            || (table & 3) != 0;
        // Also bail if a3 is a non-canonical / high garbage pointer mid-walk.
        uint a3 = (uint)sys.EE.GetGpr(7).Lo;
        bool a3Bad = a3 >= SystemMemory.RDRAM_SIZE || (a3 & 0x80000000u) != 0;
        // Planted flat STREE0 index: entry pointer must fall inside [table, table+count*24).
        if (!tableBad && _streamMapTable != 0 && table == _streamMapTable && _streamMapCount > 0)
        {
            uint mapEnd = _streamMapTable + _streamMapCount * 24u;
            if (a3 < _streamMapTable || a3 >= mapEnd)
                a3Bad = true;
        }
        // Stuck in lookup band across many quirk slices (planted table, bad chain) → miss.
        if (!tableBad && !a3Bad && _streamMapLookupHits < 8)
            return;

        sys.EE.SetGpr(2, new EmotionEngine.Gpr128 { Lo = 0 }); // v0 = miss
        sys.EE.SetGpr(16, new EmotionEngine.Gpr128 { Lo = 0 }); // s0 = not-found
        sys.EE.PC = StreamMapLookupFail;
        _streamMapEscapes++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _streamMapEscapes <= 16)
            Console.Error.WriteLine(
                $"[VEXX] null-stream-map escape #{_streamMapEscapes} pc=0x{pc:X} s5=0x{s5:X} table=0x{table:X} a3=0x{a3:X} cyc={sys.Scheduler.MasterCycles}");
    }

    private void MaybeRescueStackDeath(Ps2System sys, uint pc)
    {
        uint sp = (uint)(sys.EE.GetGpr(29).Lo & 0x1FFFFFFFu);
        uint resume = 0;
        // Vexx PT_LOAD code is ~0x100000–0x445F00. Never resume into freelist bump
        // (0x1800000+) or EE stack (0x1F00000+) — IsLikelyEeCode false-positives there
        // (diag ra=0x18B3F7F). Also skip freelist family 0x1CE000–0x1CF000 (real code but
        // mid-walker resume is wrong — diag landed 0x1CE774 and re-thrashed).
        static bool IsCodeBand(uint p) =>
            p is >= 0x00100000u and < 0x00460000u && (p & 3) == 0
            && p is not (>= 0x001CE000u and <= 0x001CF200u);

        uint ra = (uint)(sys.EE.GetGpr(31).Lo & 0x1FFFFFFFu);
        if (IsCodeBand(ra) && sys.Memory.IsLikelyEeCode(ra))
            resume = ra;

        if (resume == 0 && sp is >= 0x00100000 and < SystemMemory.RDRAM_SIZE)
        {
            for (uint off = 0; off <= 0xC0; off += 4)
            {
                uint cand = sys.Memory.Read32(sp + off);
                uint cp = cand & 0x1FFFFFFFu;
                if (IsCodeBand(cp) && sys.Memory.IsLikelyEeCode(cand))
                {
                    resume = cp;
                    break;
                }
            }
        }
        // Prefer ready-flag wait leave / host-present spin over CRT seed when post-pcl.
        if (resume == 0 && _beginPclLoaded)
            resume = 0x003697E4; // wait success epilogue (flag force will set v0)
        if (resume == 0 && sys.Memory.IsLikelyEeCode(0x0029A61Cu))
            resume = 0x0029A61Cu;
        // PL-032p: do NOT re-home to 0x11C200 — on Vexx that is a null-vtable thunk family,
        // not Midway CRT0 SetupThread. Open-bus rescue looping there freezes Path2 at lit=6405.
        if (resume == 0 && sys.Memory.IsLikelyEeCode(0x00100008u))
            resume = 0x00100008u;
        if (resume == 0) return;

        sys.EE.PC = resume;
        _stackRescues++;
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1" && _stackRescues <= 24)
            Console.Error.WriteLine(
                $"[VEXX] stack-death rescue #{_stackRescues} from=0x{pc:X} -> 0x{resume:X} " +
                $"ra=0x{ra:X} cyc={sys.Scheduler.MasterCycles}");
    }

    private static bool LooksLikePathAsciiPc(Ps2System sys, uint pc)
    {
        // PL-032i: post-begin0.tre residual lands PC in EE stack path scratch
        // (0x01FEF8CC key=0x7F696C65 / "lists") — must rescue, not only mid-RDRAM path
        // blobs (pc ≥ 0x300000). High stack 0x01F0_0000..0x0200_0000 is never code.
        // Also freelist bump arena (0x1800000–0x2800000) holds swooshes/pcl payloads that
        // the EE may jalr into (diag key=0x4F4F5753 "SWOO" @0x18A17B4).
        bool stackBand = pc is >= 0x01F00000u and < 0x02000000u;
        bool bumpBand = pc is >= BumpArenaBase and < BumpArenaEnd;
        bool midBand = pc is >= 0x00300000u and < BumpArenaBase;
        if (!stackBand && !bumpBand && !midBand) return false;
        if (pc + 4 >= SystemMemory.RDRAM_SIZE) return false;
        if (!stackBand && !bumpBand && sys.Memory.IsLikelyEeCode(pc)) return false;
        int printable = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = sys.Memory.Read8(pc + (uint)i);
            if (b is >= 0x20 and <= 0x7E) printable++;
        }
        // Stack / bump-arena death: mostly-printable or known asset tags.
        uint w = sys.Memory.Read32(pc);
        // "SWOO" "Glow" "list" "STRE" "GAME" path fragments
        if (w is 0x4F4F5753u or 0x776F6C47u or 0x7473696Cu
            or 0x45525453u or 0x454D4147u or 0x742E3065u)
            return true;
        if ((stackBand || bumpBand) && printable >= 2) return true;
        if (printable < 3) return false;
        for (int i = 0; i < 12; i++)
        {
            byte b = sys.Memory.Read8(pc + (uint)i);
            if (b is (byte)'.' or (byte)'\\' or (byte)'/' or (byte)';') return true;
        }
        return printable >= 4;
    }

    public static void PlantIopRpVersion(Ps2System sys)
    {
        WriteCString4(sys, IopVersionCellA, "2520");
        WriteCString4(sys, IopVersionCellB, "2520");
    }

    public static void PlantStringHeapHook(Ps2System sys)
    {
        if (sys.Memory.Read32(MallocStub) == 0)
            PlantCrtMallocTable(sys);
        sys.Memory.Write32(StringAllocHook, MallocStub);
        sys.Memory.Write32(StringFreeHook, 0x001CEBC0); // CRT free trampoline
        sys.Memory.Write32(SmallPoolRoot, 0);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine($"[VEXX] string-hook malloc=0x{MallocStub:X} free→CRT; pool cleared");
    }

    public static void PlantCrtMallocTable(Ps2System sys)
    {
        uint cur = BumpCursorCell, stub = MallocStub, end = BumpArenaEnd;
        uint existing = sys.Memory.Read32(cur);
        if (existing < BumpArenaBase || existing >= BumpArenaEnd)
            sys.Memory.Write32(cur, BumpArenaBase);

        uint[] mallocOps =
        {
            0x3C080000u | (cur >> 16), 0x35080000u | (cur & 0xFFFF), 0x8D020000u,
            0x2489000Fu, 0x00094902u, 0x00094900u, 0x00495021u,
            0x3C0B0000u | (end >> 16), 0x356B0000u | (end & 0xFFFF),
            0x014B602Bu, 0x11800004u, 0x00000000u, 0xAD0A0000u,
            0x03E00008u, 0x00000000u, 0x03E00008u, 0x0000102Du,
        };
        for (int i = 0; i < mallocOps.Length; i++)
            sys.Memory.Write32(stub + (uint)(i * 4), mallocOps[i]);

        sys.Memory.Write32(FreeStub + 0, 0x03E00008u);
        sys.Memory.Write32(FreeStub + 4, 0x00000000u);
        sys.Memory.Write32(ReallocStub + 0, 0x00A0202Du);
        sys.Memory.Write32(ReallocStub + 4, 0x08000000u | ((MallocStub >> 2) & 0x03FFFFFF));
        sys.Memory.Write32(ReallocStub + 8, 0x00000000u);
        sys.Memory.Write32(CrtMallocSlot, MallocStub);
        sys.Memory.Write32(CrtFreeSlot, FreeStub);
        sys.Memory.Write32(CrtReallocSlot, ReallocStub);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine($"[VEXX] CRT malloc table → bump 0x{BumpArenaBase:X}-0x{BumpArenaEnd:X}");
    }

    public static uint HostBumpAlloc(Ps2System sys, uint size)
    {
        uint cur = sys.Memory.Read32(BumpCursorCell);
        if (cur < BumpArenaBase || cur >= BumpArenaEnd)
        {
            cur = BumpArenaBase;
            sys.Memory.Write32(BumpCursorCell, cur);
        }
        uint aligned = (size + 15u) & ~15u;
        if (aligned == 0) aligned = 16;
        ulong next = (ulong)cur + aligned;
        if (next >= BumpArenaEnd) return 0;
        sys.Memory.Write32(BumpCursorCell, (uint)next);
        return cur;
    }

    public static bool MaybeFixSearchFilePathLayout(Ps2System sys, uint buf)
    {
        if (buf + 0x120 >= SystemMemory.RDRAM_SIZE) return false;
        // Do not touch a completed sceCdlFILE (valid lsn + planted leaf) — sliding mid-string
        // fragments like "E.TXT;1" after GAME.TXT corrupts the live SearchFile result while
        // NCMD CdRead is in flight (wave-4 residual).
        uint curLsn = sys.Memory.Read32(buf);
        string planted = ReadCStringStatic(sys, buf + 8, 16);
        if (curLsn != 0 && IsPlausibleSearchLeaf(planted))
            return false;

        byte at24 = sys.Memory.Read8(buf + 0x24);
        // Require path-shaped start: \ / $ or drive-ish — not mid-leaf "E.TXT".
        if (at24 is not ((byte)'\\' or (byte)'/' or (byte)'$'))
            return false;

        var tmp = new byte[0x100];
        int len = 0;
        for (; len < tmp.Length; len++)
        {
            byte b = sys.Memory.Read8(buf + 0x24 + (uint)len);
            tmp[len] = b;
            if (b == 0) { len++; break; }
        }
        if (len <= 1) return false;

        // Slide when +0x20 empty OR stale (different leaf than +0x24) — STREE0 after GAME.TXT.
        string path24 = Encoding.ASCII.GetString(tmp, 0, Math.Max(0, len - 1));
        string path20 = ReadCStringStatic(sys, buf + 0x20, 128);
        string leaf24 = NormalizeSearchLeaf(path24);
        string leaf20 = NormalizeSearchLeaf(path20);
        if (!IsPlausibleSearchLeaf(leaf24)) return false;
        bool needSlide = path20.Length == 0 || (leaf24.Length > 0 && leaf24 != leaf20);
        if (!needSlide) return false;

        for (int i = 0; i < len; i++)
            sys.Memory.Write8(buf + 0x20 + (uint)i, tmp[i]);
        // New path: clear stale lsn/size so plant / HLE rewrite for STREE0 etc.
        if (leaf24.Length > 0 && leaf24 != leaf20)
        {
            sys.Memory.Write32(buf + 0, 0);
            sys.Memory.Write32(buf + 4, 0);
        }

        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine($"[VEXX] SearchFile path slide @0x{buf:X} → \"{path24}\"");
        return true;
    }

    public bool MaybePlantSearchFileResult(Ps2System sys, uint buf)
    {
        string? isoPath = sys.Cdvd.MountedPath;
        if (string.IsNullOrEmpty(isoPath) || buf + 0x30 >= SystemMemory.RDRAM_SIZE) return false;

        string name = ReadCString(sys, buf + 0x20, 128);
        if (name.Length == 0) name = ReadCString(sys, buf + 0x24, 128);
        if (name.Length == 0) return false;

        name = NormalizeSearchLeaf(name);
        if (!IsPlausibleSearchLeaf(name)) return false;
        if (name.Contains('\\') || name.Contains('/') || name.StartsWith('$')) return false;

        // Re-plant when lsn empty OR planted leaf at +8 mismatches requested path (STREE0).
        string plantedLeaf = ReadCString(sys, buf + 8, 16);
        uint curLsn = sys.Memory.Read32(buf);
        if (curLsn != 0 && string.Equals(plantedLeaf, name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (curLsn != 0 && plantedLeaf.Length > 0
            && name.StartsWith(plantedLeaf, StringComparison.OrdinalIgnoreCase))
            return false;

        if (_isoVol == null || _isoVolPath != isoPath)
        {
            try { _isoVol?.Disc?.Dispose(); } catch { }
            _isoVol = Iso9660.OpenFile(isoPath);
            _isoVolPath = isoPath;
        }
        if (_isoVol == null) return false;

        try
        {
            var entry = Iso9660.FindFile(_isoVol, name)
                ?? Iso9660.FindFile(_isoVol, System.IO.Path.GetFileName(name));
            if (entry == null) return false;

            uint reportSize = CapTreSizeIfNeeded(entry.Name, entry.Size, _isoVol, entry);
            sys.Memory.Write32(buf + 0, entry.ExtentLba);
            sys.Memory.Write32(buf + 4, reportSize);
            string leaf = entry.Name.Length > 15 ? entry.Name[..15] : entry.Name;
            for (int i = 0; i < 16; i++)
                sys.Memory.Write8(buf + 8 + (uint)i, i < leaf.Length ? (byte)leaf[i] : (byte)0);

            if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
                Console.Error.WriteLine(
                    $"[VEXX] SearchFile plant @0x{buf:X} \"{name}\" lsn={entry.ExtentLba} size={reportSize}" +
                    (reportSize != entry.Size ? $" (full={entry.Size})" : ""));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// After HLE SearchFile writes full ~1GB STREE size, rewrite +4 to TOC byte length so
    /// freelist/bump can allocate and host-open can stream the header.
    /// </summary>
    private bool MaybeCapTreSearchSize(Ps2System sys, uint buf)
    {
        if (buf + 0x20 >= SystemMemory.RDRAM_SIZE) return false;
        uint size = sys.Memory.Read32(buf + 4);
        if (size <= 8 * 1024 * 1024u) return false;
        string leaf = ReadCString(sys, buf + 8, 16);
        if (leaf.Length < 4 || !leaf.EndsWith(".TRE", StringComparison.OrdinalIgnoreCase)
            && !leaf.StartsWith("STREE", StringComparison.OrdinalIgnoreCase))
        {
            // Also sniff path at +0x20/+0x24
            string p = ReadCString(sys, buf + 0x20, 64);
            if (p.Length == 0) p = ReadCString(sys, buf + 0x24, 64);
            if (p.IndexOf(".TRE", StringComparison.OrdinalIgnoreCase) < 0
                && p.IndexOf("STREE", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            leaf = System.IO.Path.GetFileName(p.Replace('/', '\\'));
        }

        uint lsn = sys.Memory.Read32(buf);
        uint toc = 0;
        string? isoPath = sys.Cdvd.MountedPath;
        if (!string.IsNullOrEmpty(isoPath) && lsn != 0)
        {
            try
            {
                if (_isoVol == null || _isoVolPath != isoPath)
                {
                    try { _isoVol?.Disc?.Dispose(); } catch { }
                    _isoVol = Iso9660.OpenFile(isoPath);
                    _isoVolPath = isoPath;
                }
                if (_isoVol?.Disc != null)
                {
                    var hdr = new byte[16];
                    int got = _isoVol.Disc.ReadAt((long)lsn * Iso9660.SectorSize, hdr);
                    if (got >= 8)
                    {
                        uint w0 = BitConverter.ToUInt32(hdr, 0);
                        uint w1 = BitConverter.ToUInt32(hdr, 4);
                        if (w1 is >= 0x10000 and <= 0x800000) toc = w1;
                        else if (((ulong)w0 << 4) is >= 0x10000 and <= 0x800000) toc = w0 << 4;
                    }
                }
            }
            catch { /* fall through */ }
        }
        if (toc == 0) toc = 0x00480000; // ~4.5MB default
        if (toc >= size) return false;
        sys.Memory.Write32(buf + 4, toc);
        if (Environment.GetEnvironmentVariable("DETPS2_TRACE_VEXX") == "1")
            Console.Error.WriteLine(
                $"[VEXX] TRE size cap @0x{buf:X} \"{leaf}\" {size} → {toc} cyc={sys.Scheduler.MasterCycles}");
        return true;
    }

    private static uint CapTreSizeIfNeeded(string name, uint fullSize, Iso9660.Volume? vol, Iso9660.FileEntry entry)
    {
        if (fullSize <= 8 * 1024 * 1024u) return fullSize;
        if (name.IndexOf(".TRE", StringComparison.OrdinalIgnoreCase) < 0
            && name.IndexOf("STREE", StringComparison.OrdinalIgnoreCase) < 0)
            return fullSize;
        try
        {
            if (vol?.Disc != null && entry.ExtentLba != 0)
            {
                var hdr = new byte[16];
                int got = vol.Disc.ReadAt((long)entry.ExtentLba * Iso9660.SectorSize, hdr);
                if (got >= 8)
                {
                    uint w0 = BitConverter.ToUInt32(hdr, 0);
                    uint w1 = BitConverter.ToUInt32(hdr, 4);
                    if (w1 is >= 0x10000 and <= 0x800000) return w1;
                    if (((ulong)w0 << 4) is >= 0x10000 and <= 0x800000) return w0 << 4;
                }
            }
        }
        catch { /* ignore */ }
        return 0x00480000;
    }

    private static string NormalizeSearchLeaf(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int colon = name.IndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        name = name.TrimStart('\\', '/');
        int semi = name.IndexOf(';');
        if (semi >= 0) name = name[..semi];
        return name.Trim();
    }

    /// <summary>ISO leaf like GAME.TXT / STREE0.TRE — not "." or empty junk.</summary>
    private static bool IsPlausibleSearchLeaf(string leaf)
    {
        if (string.IsNullOrEmpty(leaf) || leaf.Length is < 3 or > 64) return false;
        if (leaf is "." or "..") return false;
        bool hasAlnum = false, hasDot = false;
        foreach (char c in leaf)
        {
            if (char.IsAsciiLetterOrDigit(c)) hasAlnum = true;
            else if (c == '.') hasDot = true;
            else if (c is not ('_' or '-' or ' ')) return false;
        }
        return hasAlnum && hasDot;
    }

    private static string ReadCStringStatic(Ps2System sys, uint addr, int max)
    {
        var sb = new StringBuilder(max);
        for (int i = 0; i < max; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) break;
            if (b is < 32 or >= 127) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static bool VersionCellsOk(Ps2System sys) =>
        ReadCString4(sys, IopVersionCellA) == "2520" || ReadCString4(sys, IopVersionCellB) == "2520";

    private static bool PathStubActive(Ps2System sys, uint entry) =>
        (sys.Memory.Read32(entry) >> 26) == 2;

    public static void PatchNullPathBasename(Ps2System sys)
    {
        PlantOne(sys, PathBasenameA, StubA);
        PlantOne(sys, PathBasenameB, StubB);
    }

    private static void PlantOne(Ps2System sys, uint entry, uint stub)
    {
        uint w0 = sys.Memory.Read32(entry);
        uint w1 = sys.Memory.Read32(entry + 4);
        if ((w0 >> 26) == 2) return;
        uint cont = (entry + 8) >> 2;
        sys.Memory.Write32(stub + 0x00, 0x10800005u);
        sys.Memory.Write32(stub + 0x04, 0x00000000u);
        sys.Memory.Write32(stub + 0x08, w0);
        sys.Memory.Write32(stub + 0x0C, w1);
        sys.Memory.Write32(stub + 0x10, 0x08000000u | (cont & 0x03FFFFFF));
        sys.Memory.Write32(stub + 0x14, 0x00000000u);
        sys.Memory.Write32(stub + 0x18, 0x03E00008u);
        sys.Memory.Write32(stub + 0x1C, 0x0000102Du);
        sys.Memory.Write32(entry + 0x00, 0x08000000u | ((stub >> 2) & 0x03FFFFFF));
        sys.Memory.Write32(entry + 0x04, 0x00000000u);
    }

    private static string ReadCString4(Ps2System sys, uint addr)
    {
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) return new string(chars, 0, i);
            chars[i] = (char)b;
        }
        return new string(chars);
    }

    private static void WriteCString4(Ps2System sys, uint addr, string s)
    {
        for (int i = 0; i < 4; i++)
            sys.Memory.Write8(addr + (uint)i, i < s.Length ? (byte)s[i] : (byte)0);
    }

    private static string ReadCString(Ps2System sys, uint addr, int max)
    {
        var sb = new StringBuilder(max);
        for (int i = 0; i < max; i++)
        {
            byte b = sys.Memory.Read8(addr + (uint)i);
            if (b == 0) break;
            if (b is < 32 or >= 127) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}
