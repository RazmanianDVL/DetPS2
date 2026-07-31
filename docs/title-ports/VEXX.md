# Vexx (USA) — residual + draw-graph charter (S7 / PL-005 · GX-008)

| Field | Value |
|-------|--------|
| **Title** | Vexx (USA) |
| **user-media id** | `vexx` |
| **Serial / BOOT2** | `SLUS_203.83` |
| **ISO** | `user-media-vexx.json` → operator ISO (never commit path) |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-vexx.json` |
| **Seat / branch** | **S7 STREAM** · `agent/seat-s7/s0-g0` |
| **Build** | `out/seat-s7` |
| **Assist** | `VexxAssist.cs` (owned) |
| **Status** | **MENU YES** (title-surface Soft-GS) — STREE0 VFS member stream + multi-prim Path2 |
| **Last updated** | 2026-07-31 |
| **WP** | PL-005 residual + draw-graph; residual focus **TRE VFS** |

### MENU gate

**title-surface Soft-GS** = non-black Soft-GS after STREE0 virtual FS binds frontend assets.
Not MK MAINMENU language.

---

## Claim 100M (SEMA_STALL_YIELD OFF) — 2026-07-31 seat-s7

```
@100M: PC=0x003681D4  px=877186  prims=24  gifPath1=0  gifPath2=12  gifPath3=5  dmac=9
       sifBytes=5984 syscalls=804 cdvdSectors=318 exitRequested=False
       softgs: imgBytes=5120 dispfbPx=0 fragTest=877186 rejBounds=0 rejScissor=0 rejDepth=0 rejAlpha=0
       softgs-regs: FRAME_1=0xA008C DISPFB1=0x80000001400
                    SCISSOR=0x01BF0000027F0000 XYOFFSET=0x720000006C00 TEST=0x380FA
       softgs-writes: total=2019 PRIM=1541 XYZ2=48 XYZ3=0 FRAME=6 SCISSOR=3 TEST=3 XYOFF=3
       gif-pkts: completed=176 aborted=2 spannedCalls=3 tags=178 p2qws=2210
       RealSifRpc: binds=10 calls=31 unknownBindSids=0
       STREE0: stream-map count=11364 indexMembers=7272 host MEMBER opens=17 fails=15
```

Trace: `out/traces/vexx-claim100-20260731-085231-{out,err}.txt`  
SHA tip at claim: `20973c6` (+ this docs commit).

Reproduce:

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s7
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_VEXX='1'
dotnet exec out/seat-s7/DetPS2.Core.dll blocker-trace user-media-vexx.json --cycles=100000000 --host-present
```

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF `SLUS_203.83` | **Yes** |
| IOPRP252 PreferIopRp + pad OPEN | **Yes** |
| SearchFile GAME.TXT / STREE0.TRE | **Yes** (path +0x24) |
| STREE0 TOC + stream-map hash table | **Yes** (count=11364, table host-built) |
| Virtual member FS (NameCRC→off/sz) | **Yes** (index≈7272; 17 MEMBER opens @100M) |
| Soft-GS **px>0** title surface | **Yes** (**px=877186 prims=24**) |
| IMAGE bytes (TEX path residual) | **Partial** (**imgBytes=5120**) |
| DISPFB present path | **No** (dispfbPx=0) |
| Full TRE member completeness | **Residual** (15 open fails; nested stree1/patch0/sound) |
| Pad interactive / frontend deep | **Open** (S1 INTERACTIVE PL-017) |

---

## Draw-graph charter (menu / title-surface)

What Soft-GS actually sees at MENU claim (for S8/S9/S10 handoff):

| Path | Count / signal | Notes |
|------|----------------|-------|
| **Path1 (VU1)** | gifPath1=**0** | No VU1 title path yet |
| **Path2 (VIF1/DIRECT)** | gifPath2=**12**, p2qws=**2210** | Dominant title draws; XYZ2=48 |
| **Path3 (GIF PATH3)** | gifPath3=**5** | Light PATH3; not assist invent |
| **PRIM/XYZ** | PRIM writes **1541**, XYZ2 **48** | Multi-prim surface (not single expand strip) |
| **IMAGE / TEX** | imgBytes=**5120** | Some BITBLT/IMAGE; many `.tgax` still fail open |
| **DISPFB / PCRTC** | dispfbPx=**0**, DISPFB1 set but no present sample | S10 residual |
| **FRAME / XYOFFSET** | FRAME_1=`0xA008C`, ofx=`0x6C00` ofy=`0x7200` | Retail-center band (not ofx=0x8000 expand class) |
| **Rejects** | rejBounds/Scissor/Depth/Alpha = **0** | Draws land in Soft-GS FB |

**Draw class:** multi-prim Path2 title chrome with real XYOFFSET band + early IMAGE crumbs.  
**Not** pure ofx=0x8000 single-strip expand (contrast Whiplash / BO2 / GoW expand class).

### Expected next draw graphs (post-pad / frontend)

1. More `.tgax` / loading-screen textures → imgBytes↑, textured sprites.
2. Frontend level `data\levels\frontend\…` after `begin.atr` reads fix → scene/prim change.
3. Possible DISPFB arm once CRT/display spine runs (S10).

---

## Residual truth (S0 baseline) — **TRE VFS**

Primary residual class: **STREE0 virtual filesystem incomplete**.

### Working spine

1. Host CD I/O vtable @ `0x3AD3A8` → stubs ≥1MiB (`HostCdStubBase=0xF00000`).
2. STREE0 stream-map BUILD from ISO TOC (count×24 hash entries).
3. NameCRC32 member index (dual layout A/C) → FileOpenVirtualStream into STREE0 extent.
4. 17 successful MEMBER opens include fonts, hit-flash mats, button1/5/7/10/11, `begin.atr`.

### Open fails @100M (15) — charter backlog for PL-032

| Path class | Examples |
|------------|----------|
| Nested TRE | `stree1.tre`, `patch0.tre`, `data\sound\sound0.tre` |
| Sound pack | `DATA\SOUND\SOUND.AD6` |
| Env shadows | `shadowcircle_nc.tgax`, `shadowsquare.tgax` |
| Default mat | `defaultmaterialmanagertexture.bmpx` |
| Font gaps | `button2/3/4/6/8/9.tgax` (CRC miss / alt path) |
| Loading UI | `loadtimer_light_nm.tgax`, `loadtimer_w-alpha_nm.tgax` |

### Secondary residual

- **host-read BADARGS** (`buf=0xFFFFFFF0 size=0xFFFFFFFF`) on several text opens and `begin.atr` — open succeeds, read args poisoned; limits frontend depth.
- Thread id=2 sleeping WaitSema(3) — not global fabricate; leave alone unless proven shared wall.
- richer frontend / pad (PL-017) after TRE completeness.

### Forbidden

- Global WaitSema fabricate (WHIP-only).
- Invent PATH3 / plant pixels / FFmpeg logos.
- Full 1GB TRE map into EE RAM (TOC + member stream only).

---

## Assists (current — title-local)

- IOPRP252 version cells + PreferIopRpGetVersion
- CRT/string heap bump (`0x1800000–0x2800000`) + freelist escape (cap, not full TRE)
- SearchFile path slide (+0x24) + TRE size cap
- Host CD I/O open/read/seek/tell/size/close
- STREE0 stream-map plant + NameCRC virtual member FS
- Null stream-map / path-normalize / stack-death escapes

## Debt class

`VexxAssist` TITLE · SearchFile dual-gate handoff with S1 (PL-036) · nested TRE/sound open for T3 frontend.

## Next WPs (seat S7)

| WP | Goal |
|----|------|
| PL-017 | Pad on title-surface |
| PL-032 | TRE member completeness (fail list above) |
| GX-062 | First-area textures (post-gameplay charter) |
| PL-053 | Title→game first level |

## Related

- Scoreboard row: [`SCOREBOARD.md`](SCOREBOARD.md)
- Plan: [`POST_MENU_PHASE_PLAN.md`](../POST_MENU_PHASE_PLAN.md) S7 · [`GRAPHICS_PIPELINE_PHASE_PLAN.md`](../GRAPHICS_PIPELINE_PHASE_PLAN.md) GX-008
- Issue family: #19 SearchFile / STREE stream
