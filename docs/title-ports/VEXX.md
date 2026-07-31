# Vexx (USA) — residual + draw-graph charter (S7 / PL-005 · GX-008)

| Field | Value |
|-------|--------|
| **Title** | Vexx (USA) |
| **user-media id** | `vexx` |
| **Serial / BOOT2** | `SLUS_203.83` |
| **ISO** | `user-media-vexx.json` → operator ISO (never commit path) |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-vexx.json` |
| **Seat / branch** | **S7 STREAM** · `agent/seat-s7/s2-g2` |
| **Build** | `out/seat-s7` |
| **Assist** | `VexxAssist.cs` (owned) |
| **Status** | **MENU YES** + **PL-032 TRE member↑** — imgBytes/dispfb/prims improve @100M; pad inject live |
| **Last updated** | 2026-07-31 |
| **WP** | PL-032 TRE member completeness + host-read BADARGS recover |

### MENU gate

**title-surface Soft-GS** = non-black Soft-GS after STREE0 virtual FS binds frontend assets.
Not MK MAINMENU language.

---

## Claim 100M (SEMA_STALL_YIELD OFF) — S2 PL-032 · 2026-07-31 seat-s7

```
@100M: PC=0x003681DC  px=877830  prims=25  gifPath1=0  gifPath2=12  gifPath3=5  dmac=9
       sifBytes=9531 syscalls=889 cdvdSectors=519 exitRequested=False
       softgs: imgBytes=38912 dispfbPx=644 naturalDispfbPx=644 expandHits=0 fragTest=877186
       softgs-regs: FRAME_1=0xA008C DISPFB1=0x80000001400
                    SCISSOR=0x01BF0000027F0000 XYOFFSET=0x720000006C00 TEST=0x380FA
       softgs-writes: total=130 PRIM=6 XYZ2=48 FRAME=6 SCISSOR=3 TEST=3 XYOFF=3
       gif-pkts: completed=17 aborted=0 tags=17 p2qws=2210 image=4
       RealSifRpc: binds=13 calls=48 unknownBindSids=1
       pad: inject ≥1536 START/CROSS edges + ForceRefreshPad
       STREE0: index=9674; MEMBER opens=23; BADARGS recover=4 (fontindex/history/textindex/begin.atr)
```

**CLAIM LINE (Vexx / PL-032):**  
`Vexx SEMA_OFF @100M MENU hold px=877830 prims=25 img=38912 dispfb=644 cdvd=519 | TRE members=23 BADARGS recover=4 | pad inject live (≥1536) | T3 partial (imgBytes↑ dispfb↑ vs S1 img=5120)`

Trace: `out/traces/vexx-claim100-pl032-20260731-094200-{out,err}.txt`  
S1 baseline: imgBytes=5120 prims=24 dispfbPx=0 members=17

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
| Virtual member FS (NameCRC→off/sz) | **Yes** (index≈9674; **23 MEMBER** opens @100M) |
| Soft-GS **px>0** title surface | **Yes** (**px=877830 prims=25**) |
| IMAGE bytes (TEX path) | **Yes↑** (**imgBytes=38912**, was 5120) |
| DISPFB present path | **Partial** (**dispfbPx=644**, was 0) |
| host-read BADARGS (text/begin.atr) | **Recovered** (freelist/s-reg/host-bump buffer) |
| Full TRE member completeness | **Residual** (9 fails: stree1/patch0/sound0/button2–4/9/shadowcircle/loadtimer_light) |
| Pad inject START/CROSS (PL-017) | **Yes** (≥1536 pulses @100M) |
| T2 INTERACTIVE (state/prim delta) | **Residual** (pad live; frontend depth residual) |

---

## Draw-graph charter (menu / title-surface)

| Path | Count / signal | Notes |
|------|----------------|-------|
| **Path1 (VU1)** | gifPath1=**0** | No VU1 title path yet |
| **Path2 (VIF1/DIRECT)** | gifPath2=**12**, p2qws=**2210** | Dominant title draws; XYZ2=48 |
| **Path3 (GIF PATH3)** | gifPath3=**5** | Light PATH3; not assist invent |
| **PRIM/XYZ** | prims=**25**, XYZ2 **48** | Multi-prim surface |
| **IMAGE / TEX** | imgBytes=**38912**, image tags=**4** | PL-032 texture crumbs↑ |
| **DISPFB / PCRTC** | dispfbPx=**644**, naturalDispfb=1 | S10 partial |
| **FRAME / XYOFFSET** | FRAME_1=`0xA008C`, ofx=`0x6C00` ofy=`0x7200` | Retail-center band |
| **Expand** | expandHits=**0** | Natural draw class (not ofx strip) |
| **Rejects** | all **0** | Draws land in Soft-GS FB |

**Draw class:** multi-prim Path2 title chrome + IMAGE crumbs + early DISPFB sample.

---

## Residual truth — **TRE VFS (PL-032 progress)**

### Working spine

1. Host CD I/O vtable @ `0x3AD3A8` → stubs ≥1MiB (`HostCdStubBase=0xF00000`).
2. STREE0 stream-map BUILD + **aligned 24-byte** NameCRC index (`[2]=CRC [4]=off [5]=sz`).
3. Binary texture score ≥ min (compact .tgax/.bmpx no longer rejected at score 8–9).
4. BADARGS bulk-read recover (recent freelist / s-reg / host bump).
5. 23 MEMBER opens: defaultmat, fonts, text, SOUND.AD6, memcard, shadows, hit-flash mats, buttons 1/5–8/10/11, loadtimer_w-alpha, **begin.atr**.

### Open fails @100M (9) — residual

| Path class | Examples |
|------------|----------|
| Nested TRE probe | `stree1.tre`, `patch0.tre` (**must FAIL** — stub success walks stree1…24 forever) |
| Sound pack | `data\sound\sound0.tre` |
| Env shadows | `shadowcircle_nc.tgax` |
| Font gaps | `button2/3/4/9.tgax` (CRC/score residual) |
| Loading UI | `loadtimer_light_nm.tgax` |

### Forbidden

- Global WaitSema fabricate (WHIP-only).
- Invent PATH3 / plant pixels / FFmpeg logos.
- Full 1GB TRE map into EE RAM (TOC + member stream only).
- Empty nested-TRE open stubs for `streeN.tre` (causes probe cascade).

---

## Assists (current — title-local)

- IOPRP252 version cells + PreferIopRpGetVersion
- CRT/string heap bump + freelist escape
- SearchFile path slide + TRE size cap
- Host CD I/O open/read/seek/tell/size/close
- STREE0 stream-map + NameCRC virtual member FS (aligned + sliding)
- **PL-032** binary texture score + BADARGS recover + sound.ad6 stub only
- **PL-017** dense pad inject + ForceRefreshPad

## Debt class

`VexxAssist` TITLE · button2–4/9 CRC residual · nested stree packs not in STREE0 · T2 state advance

## Next WPs (seat S7)

| WP | Goal |
|----|------|
| PL-017 | **Done (pad live)** |
| PL-032 | **Partial** — members 17→23, img 5k→38k, dispfb 0→644; residual fails |
| GX-062 | First-area textures |
| PL-053 | Title→game first level |

## Related

- Scoreboard row: [`SCOREBOARD.md`](SCOREBOARD.md)
- Plan: [`POST_MENU_PHASE_PLAN.md`](../POST_MENU_PHASE_PLAN.md) S7 · [`GRAPHICS_PIPELINE_PHASE_PLAN.md`](../GRAPHICS_PIPELINE_PHASE_PLAN.md) GX-008
- Issue family: #19 SearchFile / STREE stream
