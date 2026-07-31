# Burnout 3: Takedown (USA) — title port notes

| Field | Value |
|-------|--------|
| **Id** | `burnout-3-takedown` |
| **Serial** | `SLUS_210.50` |
| **ISO** | `C:/Users/xxraz/Downloads/Burnout3Takedown.iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2` |
| **Agent date** | 2026-07-30 |
| **ROMDIR gate** | **CLOSED** |
| **Status** | Flip-wait bypass + GTFS SIDs + FILEIO fno=23 soft → gifP3=**380** / dmac=**482**; still cdvd=425 IRX-only; menu not reached |

---

## How far

| Checkpoint | Result |
|------------|--------|
| Disc / ELF | OK — `SLUS_210.50` entry `0x00100008` |
| IOPRP `"2800"` plant | **Yes** |
| IRX list (SIO2…GTFS…LGDEVW…NETWORK) | **Yes** |
| **lgDeviceInit version** | **CLEARED** — fno=12 → `0x010B1B00` |
| **lgDeviceInit fno=18 thrash** | **BROKEN** — CallRpc→epilogue + **entry stub @ `0x4438E0`** + residual complete |
| cdvdSectors | **425** (IRX only — no game FILEIO yet) |
| RealSifRpc | binds=13 calls=**555** unknown=**0** |
| PC @ 100M | **`0x00122A20`** (post flip-wait bypass) |
| gifP3 / dmac | **380 / 482** (was 30/24 → 90/72) |
| Main menu | **No** |
| Pad inject | Armed once `SectorsRead>0` |

### Telemetry @ 100M (host-present, SEMA_STALL_YIELD OFF)

```
PC=0x002B34D8  px=0 gifPath3=30 dmac=24 sifBytes=279788 syscalls=4441037 cdvdSectors=425
RealSifRpc: binds=11 calls=444 unknownServiceCalls=0 unknownBindSids=0
```

### Root causes ground-truthed

1. **Version assert `0x443A9C`**: fno=12 must return `*(recv+4)==0x010B1B00` (LGDEVW 1.11.027).  
   Fix: `RealSifRpc.HandleLgDev`.

2. **Post-version fno=18 thrash**: CallRpc WaitSema with `s1==0x01ECDF00` + cid=0 SIFCMD.  
   Fix (this session): rewrite CallRpc's saved `$ra` at `sp+176` to `0x443C44`, then run real CallRpc success epilogue `0x10F3A8` (v0=0, restore, sp+=192) so lgDeviceInit epilogue sees a valid frame. Sticky `j 0x443C44` at `0x443C20`.

3. **VBlank wait `0x237120`**: residual after LGDEV — workers re-enter; not the asset path by itself.

## Fixes this session

| Fix | Notes |
|-----|--------|
| CallRpc→lgDev epilogue via saved-ra | One-shot `_lgDevFullyDone`; main high-stack only |
| Sticky `j 0x443C44` at `0x443C20` | In HandleLgDev + ForceLgDevSuccess |
| High WaitSema id pulse (≥32) | After LGDEV done — avoid low RPC semas |
| No blind SignalSema on RPC ids | Held |

## Remaining

1. First game-data FILEIO/NCMD after LGDEV (GTFS table / Criterion assets) — `cdvd≫425`.  
2. Leave WaitSema(3) SIF-cmd poll + VBlank-only workers.  
3. GS menu frame (`px>0`) + pad confirm.

## Policy

- No `DETPS2_SEMA_STALL_YIELD`
- PollSema-id
- No global DMAC force-finish
- out←in **never** forced

## MENU

**NOT REACHED.** LGDEV entry+CallRpc-leaf stubs; boot-wait `*(gp-23028)` / `*(s0+0x13A4)`; left flip park `0x1F24E0` → **gifP3=90**. Table-walk absurd-bounds stub @ `0x3E9B40`. Still **cdvd=425** IRX-only — no game FILEIO/GTFS assets.

## Reproduce

```powershell
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'
$env:DETPS2_TRACE_RPC='1'
dotnet exec out/menu4build/DetPS2.Core.dll blocker-trace burnout-only.json --cycles=100000000 --host-present
# expect: HandleLgDev fno=0xC, force CallRpc→lgDev epilogue n=1, calls≥500, unknownBindSids=0
```

## Wave-7 (2026-07-30 post-G0)

| Field | Value |
|-------|-------|
| STG / full TXD | YES (deliver) fno=5 n=1146112; FRONTEND open |
| cdvd | 2425 (deliver 80M) |
| gifP3 / dmac / calls | 656 / 831 / 602 |
| px / MENU | 0 / No |
| Wall | post-TXD MMIO probe 0x21A5xx / PC 0x1F308C |
| Assist | e90eaef post-TXD MMIO leave |
| Residual | #20 presentation px>0; tip residual-STG flaky after SM RR |

## Wave-8 (2026-07-30 presentation thrash)

| Field | Value |
|-------|-------|
| Wall disasm | GIF flush `0x21A4F0` bulk lq/sq MMIO src; submit `0x1F3080` / final `0x1F308C` |
| Assist | Collapse absurd gp ring; leave epilogue `0x21A5D8` / `0x218774`; `b3Hot` tight slices; force≥22M; delay entry/leaf stubs n≥24 |
| Deliver 100M | STG+TXD+FRONTEND cdvd=2425 gifP3=656 **px=0** PC=0x1F308C |
| Tip 100M | residual n=2–3 cdvd=**425** (residual-STG still flaky vs deliver) |
| MENU / px | **No / 0** |
| Next | tip residual→STG restore → FRONTEND DMA → sane GIF flush → px>0 |

## Wave-9 (2026-07-30 presentation PATH3 / FRONTEND plant)

| Field | Value |
|-------|-------|
| Play! | `play-lookup SLUS_210.50 TITLE` → no GameConfig; FILEIO handlers OK |
| Diagnose 20M | residual force@~18.6M pristine FC00; PC `0x293A30`; cdvd=425 IRX; px=0 |
| Claim 100M quiet | STG+Global + **FRONTEND plant** cdvd=**6584** gifP3=**436** dmac=423 binds=13 PC=`0x10BE68` **px=0** |
| Assist | sticky PATH3 `SetMskPath3(false)` when M3P+px=0; host-plant FRONTEND.TXD 2MiB @`0xA00000`; post-TXD high WaitSema pulse; flip-wait bypass delayed to ≥95M; dead flip-watermark `$ra` rescue only |
| Rejected | VBlank poll sticky stub @25.9M → UnknownOpcode `0x4E3BD0` + STG loss; generic CallRpc soft-complete → DBC thrash abort |
| MENU / px | **No / 0** — PATH3 unmask fires; prims still 0 (IMAGE/hold or no real PRIM path) |
| Next | natural FRONTEND fno=5 dest bind (SHARED GTFS) + sane prim submit; no invented Soft-GS clear |

## Wave-IRX / Soft-GS (2026-07-31) — branch agent/menu-b3

| Field | Value |
|-------|-------|
| Tip wall (main c423c4f) | gifP3=25 dmac=22 cdvd=425 px=0 PC=0x123E20; GTFSCDVD/LGDEVW StartLoadedModule hit budget |
| Shared Soft-GS | PATH3 M3P hold queue; DISPFB/FRAME local to FB composite; retail XYZ 0x8000 center when XYOFFSET=0 |
| B3 assist | STAGEHED plant residual n>=1; post-LGDEV flag plant + re-home |
| Peak this session | STG+full TXD+FRONTEND cdvd=6584 gifP3~1100-1980 prims=2389 binds=13 — still px=0 |
| Branch claim 50-100M | often cdvd=609 (STAGEHED plant only) when residual-STG flaky |
| MENU / px | No / 0 — not claiming logo-frontend |
| Next | stabilize residual-STG under IRX always-on; prims to px; pad after Soft-GS non-black |

## Wave-2 (2026-07-31) — agent/menu-b3-w2 residual-STG + Soft-GS

| Field | Value |
|-------|-------|
| Tip base | `3748553` |
| Claim 100M | **STG+TXD+FRONTEND** cdvd=**6584** gifP3=**885** dmac=742 prims=**2593** binds=13 calls=342 PC=`0x242AA8` **px=0** |
| STAGEHED plant | cdvd=**2425** @28M (was flaky plant-only 609) |
| FRONTEND plant | 2MiB @40M cdvd=6584 |
| Residual | tip CallRpc complete path (n=2–3 @FC10); force@pristine; PreferIopRp OFF |
| Soft-GS | DISPFB FBP=0 fallback when IMAGE present; broader XYZ 0x8000; PATH3 unmask live |
| Assist | VBlank heavy leave gated until cdvd>=600; b3Hot residual SIF/boot bands (no WaitSema leaf) |
| Rejected | residual jump to parent without CallRpc unwind (sp drop); thrash leave to 0x2AF914 (UnknownSpecial); WaitSema in b3Hot (cdvd=0) |
| MENU / px | **No / 0** — not claiming logo-frontend; prims>0 but Soft-GS still black |
| Next | prims to px (FRAME/DISPFB/scissor/IMAGE present); pad after non-black Soft-GS |

## Wave-3 (2026-07-31) — agent/menu-b3-w3 Soft-GS prims→px

| Field | Value |
|-------|-------|
| Tip base | `8da8267` (main + burnout-only.json) |
| Branch | `agent/menu-b3-w3` @ `9b1af3d` |
| Claim 100M | **STG+TXD+FRONTEND** cdvd=**6584** gifP3=**1651** dmac=1407 prims=**4066** **px=3638** binds=13 calls=659 PC=`0x10BE68` |
| Soft-GS root cause | ATE on + AFAIL=FB_ONLY; MODULATE used /255 so 0x80×0x80→0x40 failed GEQUAL; also ZTE=0 soft-depth, FBP*2048 mis-scale |
| Soft-GS fix | Mul80 (0x80=1.0); AFAIL FB_ONLY/RGB_ONLY paint; ZTE=0 skip soft depth; FRAME FBP×8192 + WriteFrameLocal; scissor staged expand; XYZ off-FB rescue; XYZF2/3 |
| Telemetry | fragTest=3638 rejBounds=428 rejScissor=0 rejDepth=0 rejAlpha=3638 (AFAIL still paints); FRAME_1=`0xA0046` DISPFB1=0 SCISSOR full XYOFFSET=`0x72006C00` TEST=`0x5140B` |
| STG | stable cdvd=6584 (unchanged assist) |
| MENU | **No** — Soft-GS non-black logo-frontend pixels only; not claiming interactive menu |
| Next | pad after non-black Soft-GS; natural DISPFB; improve tex sample so rejAlpha falls |

## Wave-4 (2026-07-31) — agent/menu-b3-w4 residual→STG re-stabilize + Mul80

| Field | Value |
|-------|-------|
| Tip wall | `7123e3c` quiet 50–100M: **cdvd=609** (STAGEHED plant only) PC=`0x127464`/`0x12746C` px=0 prims=0 — flaky residual→STG after DA/GoW merges |
| Isolated w3 | `737f414` 50M: cdvd=**6584** **px=1156** prims=1292 (Mul80 path live) |
| Bisect | good `d57ab18`/`ec3191a`/`45d8c3c`/`609904e` (cdvd≥2425); **bad** merge `1d2348f` (agent/menu-da-w3) |
| Root cause | DA `END ADDR=0` rewrite applied to **all** END tags (not just DA high-TADR display). Combined with Path3Masked drain gate, B3 END tags with legitimate ADDR=0 remapped to TADR+16 garbage → residual died before STG/Global.txd |
| Fix | `Dmac.cs` case END: gate ADDR=0 inline payload to `TADR ∈ [0x01F00000,0x02000000)` (DA display chains only). GoW Path3Masked gate + Mul80 Soft-GS unchanged |
| Claim 100M ×2 | **identical** STG+TXD+FRONTEND cdvd=**6584** gifP3=**1447** dmac=1238 prims=**3513** **px=3091** binds=13 calls=584 PC=`0x10BE68` fragTest=3091 rejBounds=422 rejAlpha=3091 (AFAIL paints) |
| Soft-GS | Mul80 path re-confirmed; logo-frontend non-black stable |
| MENU | **No** — Soft-GS px>0 only; not claiming interactive menu |
| Next | pad after non-black Soft-GS; natural DISPFB; DA display END remains gated high-TADR |


## Wave-5 (2026-07-31) — agent/menu-b3-w5 Soft-GS merge chrome + pad

| Field | Value |
|-------|-------|
| Tip wall | main `9657852` fleet claim: **px=1258** gifP3=491 cdvd=**6584** dispfbPx=0 (sparse AFAIL prims; IMAGE blocked) |
| Soft-GS fix | Merge composite: sparse prim paint no longer skips DISPFB/FRAME/FBP0 IMAGE present. Fills **black** Soft-GS pixels only from local VRAM; dual-try FRAME then FBP=0 when DISPFB unset |
| Assist | Denser START/CROSS after Soft-GS non-black + FRONTEND (pad cap 1024); no PATH3 sticky unmask (regressed path-sync → px=0) |
| Claim 100M | **STG+TXD+FRONTEND** cdvd=**6584** gifP3=**491** dmac=424 prims=1407 **px=25594** dispfbPx=**24336** fragTest=1258 rejAlpha=1258 PC=`0x253FA0` binds=13 |
| Heuristic | **NEAR?** (px>10000); logo-frontend Soft-GS chrome **YES** (SOP bar: non-black after FRONTEND/logo) |
| Rejected | Sticky PATH3 unmask after assets; healthy-`$ra` flip-watermark thrash leave |
| MENU | **logo-frontend Soft-GS YES** (px=25594); not claiming pad-interactive main menu |
| Next | natural DISPFB; more prims/tex sample; optional pad-driven menu advance |

## Wave-6 (2026-07-31) — agent/menu-b3-w6 tip re-claim + packet-ring leave

| Field | Value |
|-------|-------|
| Tip base | main `6dabb99` (W5 merge composite + DA XYZ2/3 kick + whip/bo2/dec) |
| Soft-GS synergy | DA XYZ2/3 map + W5 merge composite → full Path2 prim paint (was sparse AFAIL only) |
| Assist W6 | GIF packet-build band `0x2198xx` absurd-ring leave (MMIO/VU cursor); FRONTEND plant **4 MiB**; denser pad Start/Cross/Circle/D-pad after chrome (cap 2048) |
| Claim 100M | **STG+TXD+FRONTEND** cdvd=**6584** gifP2=**14526** gifP3=**491** dmac=424 prims=**1506** **px=24407048** dispfbPx=**2273160** imgBytes=2694848 fragTest=**22133888** rejAlpha=**0** PC=`0x253FA0` binds=13 calls=226 |
| softgs-regs | FRAME_1=`0xA0046` DISPFB1=0 SCISSOR full XYOFFSET=`0x72006C00` TEST=`0x5140B` |
| Heuristic | **LIKELY-NEAR** (px≫100k gifP3≥12); logo-frontend Soft-GS chrome **YES** |
| MENU | **logo-frontend MENU YES** (scoreboard menuKind bar: non-black Soft-GS after FRONTEND/logo spine) |
| Residual | DISPFB still unset (FRAME+FBP0 composite); PC mid presentation draw `0x253FA0` — pad-interactive main-menu advance not proven beyond logo chrome |

