# Burnout 3: Takedown (USA) — title port notes

| Field | Value |
|-------|--------|
| **Id** | `burnout-3-takedown` |
| **Serial** | `SLUS_210.50` |
| **ISO** | `C:/Users/xxraz/Downloads/Burnout3Takedown.iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Worktree / seat** | `detps2` · seat **MENU-B3-2** |
| **Agent date** | 2026-07-31 |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **logo-frontend MENU YES** Soft-GS hold; **INTERACTIVE PARTIAL** (PC left presentation park via pad + dead-ra leave); full main-menu chrome **not claimed**; residual second stream / menu UI Soft-GS scene |

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
| Main menu | **logo-frontend YES** (Soft-GS); pad-interactive main menu **No** |
| Pad inject | Armed denser after chrome (cap 2048); logo→menu advance **unproven** |

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

**logo-frontend Soft-GS YES** (W6 hold, re-claimed seat S0-G0 @100M SEMA_OFF: px≈24.4M, STG+TXD+FRONTEND cdvd=6584).  
**Not** pad-interactive main menu / New Game. Residuals: pad past logo (P1), natural DISPFB (G-GFX-5), natural FRONTEND dest (plant still 4 MiB host).

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

## Seat S0-G0 (2026-07-31) — PL-005 draw-graph + residual charter

| Field | Value |
|-------|-------|
| Seat | **S4 BURNOUT** · branch `agent/seat-s4/s0-g0` tip base `20973c6` |
| Build | `out/seat-s4` Release · **SEMA_OFF** · `--host-present` · media `burnout-only.json` |
| FREEZES | Soft-GS truth · SEMA_OFF · no FFmpeg logos · **no invent DISPFB plant** · no Midway assists |
| WP | **PL-005** residual + draw-graph charter; charter targets **P1 pad past logo** + **G-GFX-5 natural DISPFB** |

### Diagnose 20M (SEMA_OFF)

```
PC=0x00123E24 px=1065600 prims=43 gifP1=0 gifP2=12 gifP3=20 dmac=20 cdvd=425
softgs: imgBytes=98592 dispfbPx=188416 fragTest=877184 rejAlpha=0
softgs-regs: FRAME_1=0xA0046 DISPFB1=0 SCISSOR full XYOFFSET=0x72006C00 TEST=0x5140B
RealSifRpc: binds=11 calls=42 unknown=0
```

- LGDEV force CallRpc→epilogue n=1 @**18.45M** pristine FC00; residual n=2–3 @FC10.
- IRX-only cdvd=**425** (STAGEHED plant @28M not yet); early Soft-GS paint already non-black (boot/logo strip via merge composite).

### Claim 100M (SEMA_OFF) — hold W6 logo-frontend MENU YES

```
PC=0x00253FA0 px=24407048 prims=1513 gifP1=0 gifP2=328 gifP3=491 dmac=424
  sifBytes=149428 syscalls=41025 cdvdSectors=6584
softgs: imgBytes=2694848 dispfbPx=2273160 fragTest=22133895
  rejBounds=0 rejScissor=0 rejDepth=7 rejAlpha=0
softgs-regs: FRAME_1=0xA0046 DISPFB1=0 SCISSOR=0x01BF0000027F0000
  XYOFFSET=0x72006C00 TEST=0x5140B
softgs-writes: total=8706 PRIM=75 XYZ2=2962 XYZF2=82 FRAME=2 SCISSOR=2 TEST=2 XYOFF=2
gif-pkts: completed=1044 aborted=82 tags=1126 p2qws=14526
RealSifRpc: binds=13 calls=226 unknownServiceCalls=0 unknownBindSids=0
```

| Spine event | Cycle | cdvd | Notes |
|-------------|------:|-----:|-------|
| LGDEV force + entry/leaf stubs | 18.45M | 425 | residual n→3 |
| STAGEHED plant @`0x01900000` | 28M | **609** | 374784 B host plant |
| Global.txd / STG FILEIO | ~28–40M | →2000+ | natural DMA after residual |
| **FRONTEND.TXD** plant 4 MiB @`0xA00000` | **40M** | **6584** | 4194304/8517568; host plant (not natural fno=5 dest) |
| Final PC presentation | 100M | 6584 | `0x253FA0` mid draw band |

**Claim line (S0-G0):**

```
B3 SLUS_210.50 seat-s4/s0-g0 SEMA_OFF 100M: STG+TXD+FRONTEND cdvd=6584
  gifP1=0 gifP2=328(p2qws=14526) gifP3=491 dmac=424 prims=1513
  px=24407048 dispfbPx=2273160 imgBytes=2694848 DISPFB1=0
  binds=13 calls=226 PC=0x253FA0 — logo-frontend MENU YES hold
  residual: pad-past-logo (P1) + natural DISPFB (G-GFX-5); no DISPFB plant
```

**MENU:** logo-frontend Soft-GS **YES** (SOP bar). **Not** claiming pad-interactive main menu / New Game.

---

## Draw-graph charter (PL-005 / GX-008 class)

### A. Boot → assets spine (what must fire before any menu chrome)

```text
ELF SLUS_210.50 @0x00100008
  → IOPRP "2800" plant (SifLoadModule gate)
  → IRX chain (SIO2…GTFS…LGDEVW…)  cdvd≈425 IRX-only
  → lgDeviceInit fno=12 version 0x010B1B00 + CallRpc force @~18.5M
  → residual CallRpc complete n=2–3 (main high-stack FC10)
  → STAGEHED host plant @28M → natural Global.txd FILEIO
  → FRONTEND.TXD host plant 4MiB @40M (cdvd=6584)
  → post-TXD GIF packet-build leave (0x2198xx absurd ring) + Soft-GS paint
```

### B. GIF / Soft-GS graph @ logo-frontend (claim 100M)

| Path | Live? | Evidence | Role at logo |
|------|-------|----------|--------------|
| **Path1** (VU1 XgKick) | **No** | gifP1=0 forever | Not used for boot logo |
| **Path2** (VIF1 DIRECT) | **Yes — dominant** | gifP2 tags + **p2qws=14526**; XYZ2=2962; prims≈1513 | Main logo/UI prim path after DA XYZ2 map + W5 merge composite |
| **Path3** (GIF FIFO) | **Yes — secondary** | gifP3=**491** (stable W5/W6); M3P may hold early IMAGE | Asset/IMAGE sideband; sticky unmask after assets was rejected (path-sync → px=0) |
| **IMAGE / BITBLT** | **Yes** | imgBytes=**2694848** | Local VRAM fill; merge composite copies into Soft-GS FB when DISPFB unset |
| **Privileged DISPFB** | **No** | DISPFB1=**0** · softgs-writes FRAME=2 only · no DISPFB reg write | Output is **merge composite** (FRAME_1 FBP + FBP0 IMAGE fallback), not retail PCRTC bind |

**Registers (sticky at claim):**

| Reg | Value | Note |
|-----|-------|------|
| FRAME_1 | `0xA0046` | FBP high draw target; FBP×8192 Soft-GS scale |
| DISPFB1/2 | **0** | **G-GFX-5 residual** — never programmed on this path |
| SCISSOR_1 | full `0x01BF0000027F0000` | |
| XYOFFSET_1 | `0x72006C00` | retail-ish (not ofx=0 expand strip) |
| TEST_1 | `0x5140B` | ATE/AFAIL path; Mul80 keeps rejAlpha=0 on claim |

**Present path (honest Soft-GS):**

```text
Prim raster (Path2 XYZ2/XYZF2) ──► software FB pixels (px multi-M)
IMAGE → local GS mem ──► CompositeDispfbToFramebuffer() on host-present
                         │
                         ├─ if DISPFB1/2 ≠ 0 → retail composite  (NOT live)
                         └─ else FRAME_1 / FBP=0 merge fill     (LIVE — W5)
```

`dispfbPx=2273160` is **composite fill count**, not proof that privileged DISPFB was written.

### C. Pad graph (P1 — pad past logo)

| Phase | Cap / pattern | Live? | Effect |
|-------|---------------|-------|--------|
| Pre-assets | cap 256 · Start/Cross edges | Yes | Boot noise only |
| Soft-GS px>0 + cdvd≥2000 | cap 1024 | Yes | Logo chrome alive |
| Rich chrome px>10k | cap **2048** · Start/Cross/Circle/D-pad | Yes | Edges fire every Step phase |
| **Logo → frontend menu advance** | need game state machine leave | **No proof** | Final PC stays `0x253FA0` presentation; no second stream open / scene change |
| **START/CROSS scripted claim** | PL-022 later | Not yet | Need `--pad-script` once consumer polls pad in menu state |

**P1 exit for B3 (PL-014 / PL-022):** Soft-GS non-black **and** pad-driven leave of logo band (PC/stream change or second chrome). Current: chrome only.

### D. Residuals (owned by S4; shared GFX handoffs)

| Residual | Gate | Owner | Forbidden | Next honest move |
|----------|------|-------|-----------|------------------|
| **DISPFB1=0** forever | **G-GFX-5** / PL-026 / GX-045 | S4 + S10 DISPLAY | **Invent DISPFB plant** | Trace when retail writes DISPFB (PCSX2+PINE / privileged GS 0x12000070); demote merge composite only when natural DISPFB appears |
| **FRONTEND host plant** 4 MiB | P2 / PL-047 | S4 | Fake full TXD as success | Natural GTFS/FILEIO fno=5 dest bind to EE buffer |
| **Pad past logo** | **P1** / PL-014 | S4 | FFmpeg / synthetic menu | Map pad consumer after chrome; scripted START once state polls; no menu FB invent |
| GIF packet-build MMIO thrash `0x2198xx` | assist leave | S4 | Permanent flush stub | Keep absurd-ring leave; sane flush needed for px |
| Path1 = 0 | G-GFX-8 later | S8/S9 + S4 | Force Path1 prims | Not needed for logo; revisit for race |

### E. Reproduce (SEMA_OFF)

```powershell
# do NOT set DETPS2_SEMA_STALL_YIELD
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
$env:DETPS2_TRACE_BIOS = '1'
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s4 --nologo -v q
dotnet exec out/seat-s4/DetPS2.Core.dll blocker-trace burnout-only.json --cycles=20000000 --host-present
dotnet exec out/seat-s4/DetPS2.Core.dll blocker-trace burnout-only.json --cycles=100000000 --host-present
# expect 100M: STG+TXD+FRONTEND cdvd=6584 px≈24.4M dispfbPx≈2.27M imgBytes≈2.69M DISPFB1=0 gifP3=491
```

### F. Next seats (do not expand this WP)

1. **PL-014 residual** — capture libpad2 SetWorkAddr / DS2O SRData so DBC work buffer gets host START; then re-claim INTERACTIVE.  
2. **PL-022** — START/CROSS pad-script claim once consumer polls.  
3. **PL-026 / GX-045** — natural DISPFB or **documented** FRAME-only present.  
4. **PL-047** — natural FRONTEND dest bind (plant↓).  

---

## Seat MENU-B3 (2026-07-31) — Soft-GS menu residual re-hold

| Field | Value |
|-------|-------|
| Seat | **MENU-B3** · worktree `detps2` · owns `Burnout3Assist.cs` |
| Media | `burnout-only.json` · **SEMA_OFF** · live-queue 100M `--host-present` |
| Job | `out/live-queue/done/menu-b3-100m-host-20260731-115249.json` |

### Wall (tip before fix)

100M host-present baseline: **cdvd=609** (STAGEHED plant only) gifP3=20 **px=380928** PC=`0x002B4580` binds=11 — residual died before Global.txd/FRONTEND. EE parked in bitfield set @`0x2B44E0` after missing early post-LGDEV poll.

### Root cause

Post-LGDEV has **two** SleepThread polls:

1. `0x2AF750` — `while (*(gp-23096)==0 && s0<600)` — primary status word **277** continues @`0x2AF7E0`
2. `0x2AF80C` — `while (*(gp-23104)==0 && s0<600)` — non-zero → `0x2AF914`

Assist only planted `gp-23104`. Live residual sampled **`0x2AF750` first**, never filled `gp-23096`, then thrashed bitfield `0x2B45xx`. After STAGEHED plant (cdvd=609) periodic boot-wait assist also stopped (`cdvd < 600` gate).

### Assist fix (`Burnout3Assist.cs`)

| Change | Notes |
|--------|-------|
| Plant `*(gp-23096)=277` + `*(gp-23104)=1` | Dual post-LGDEV flags |
| Leave early poll `0x2AF750` | Natural re-enter with flag set |
| Residual thrash leave (gated) | Only in-poll / bitfield / boot-wait bodies — **no** blind `0x293xxx`→post-LGDEV snap |
| Bitfield absurd leave | `0x2B44E0..0x2B45D4` → epi / `$ra` when span garbage |
| Periodic plant until cdvd&lt;2000 | Cover STAGEHED-plant-only window |

### Claim 100M (SEMA_OFF, live-queue host-present)

```
PC=0x00237138 px=65022869 prims=13812 gifP1=0 gifP2=229 gifP3=1375 dmac=920
  sifBytes=345188 syscalls=122571 cdvdSectors=6584
softgs: imgBytes=7262944 dispfbPx=0 naturalDispfbPx=94208 expandHits=458
  fragTest=213659434 rejDepth=148730773 rejAlpha=64641921
softgs-present: lit=94228/286720 mostlyBlack=1
softgs-regs: FRAME_1=0xA0046 DISPFB1=0 DISPFB2=0x51400 XYOFFSET=0x72006C00 TEST=0x5140B
RealSifRpc: binds=13 calls=504 unknown=0
PL-014: dbcWork=0x0067CCC0 paints≥1854; logo-pad edges n≥704
```

**Claim line:**

```
B3 SLUS_210.50 MENU-B3 SEMA_OFF 100M host-present: STG+TXD+FRONTEND cdvd=6584
  gifP1=0 gifP2=229 gifP3=1375 dmac=920 prims=13812
  px=65022869 lit=94228 imgBytes=7262944 DISPFB1=0
  binds=13 calls=504 PC=0x237138 — logo-frontend MENU YES re-hold
  menuKind=logo-frontend menuHeuristic=LIKELY-NEAR
  residual: pad-driven scene leave + natural DISPFB; no DISPFB plant
```

**MENU:** logo-frontend Soft-GS **YES** (SOP bar: non-black Soft-GS after FRONTEND/logo spine). **Not** claiming pad-interactive main menu.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
# enqueue:
@{ id='menu-b3-100m'; media='burnout-only.json'; cycles=100000000; hostPresent=$true; priority=0 } |
  ConvertTo-Json | Set-Content out/live-queue/inbox/menu-b3-100m.json
pwsh ./tools/live-game-queue.ps1 -MaxConcurrent 1 -Once
# expect: cdvd=6584 px multi-M gifP3≥100 DISPFB1=0; [B3] leave post-LGDEV … flag23096=277
```

---

## Seat MENU-B3-2 (2026-07-31) — INTERACTIVE past logo-frontend

| Field | Value |
|-------|-------|
| Seat | **MENU-B3-2** · worktree `detps2` · owns `Burnout3Assist.cs` |
| WP | Push pad past logo presentation (PC change + Soft-GS scene delta) |
| Media | `burnout-only.json` · **SEMA_OFF** · live-queue **pad-inject** + blocker-trace |
| Pad script | `tools/pad-scripts/b3-menu-interactive.pad` (39 events, dense START/CROSS 28–100M) |
| Job | `out/live-queue/done/menu-b3-2-claim3-20260731-131547.json` (+ pad3 twin) |

### Assist changes (`Burnout3Assist.cs`)

| Change | Notes |
|--------|-------|
| Soft VBlank after chrome | Stop heavy `0x2371E0` epilogue stomp once logo pad edges ≥8 — plant flags only |
| Denser logo pad | Faster edges after chrome ≥8M; long START holds; yield to pad-script external presses |
| DBC refresh | Keep ForceRefreshDbcPad; work=`0x0067CCC0` paints multi-k |
| Main wake | Wake tid=1 pure-Sleep after chrome so pad consumers run |
| Presentation leave | After chrome + pad≥16: leave flip/VBlank/logo-draw parks |
| **Dead `$ra` fix** | Live freeze `$ra=0x200` @ `0x253F64` ~85M → plant presentation continue **`0x223228`** |
| Scene-delta telemetry | Chrome snap @ FRONTEND (cdvd≥6000); log PC/prims/img delta; `leftPark` |
| post-TXD cap | Escape cap 8192 under chrome (was 2048 → Soft-GS freeze ~8M after thrash) |

### Claim 100M (SEMA_OFF, pad-script, host-present) — claim3

```
PC=0x0012DF84 px=23560827 prims=4476 gifP1=0 gifP2=296 gifP3=438 dmac=375
  sifBytes=132532 syscalls=105085 cdvdSectors=6584
softgs: imgBytes=2431936 dispfbPx=4084024 naturalDispfbPx=3956736
softgs-present: lit=100106/286720 mostlyBlack=0
RealSifRpc: binds=13 calls=202 unknown=0
pad-script: 39 events / 77 press+release fires
PL-014: chrome snap @40M cdvd=6584; presentation leave n≥5 ra→0x223228;
  scene-delta leftPark=True interactive=True (PC 0x10BE68 → 0x12DFxx / 0x207Fxx)
```

**pad-inject twin (pad3):** final PC `0x0012DF84`; after leave, PC continues to move on pad
events (`0x12DF84`/`0x12E32C`/`0x207F20`/`0x13A034`) with syscall growth (73k→105k) while Soft-GS
metrics hold (presentation graph left; no new prim stream yet).

**Claim line:**

```
B3 SLUS_210.50 MENU-B3-2 SEMA_OFF 100M pad-script host-present:
  STG+TXD+FRONTEND cdvd=6584 gifP2=296 gifP3=438 dmac=375 prims=4476
  px=23560827 lit=100106 imgBytes=2431936 DISPFB1=0
  binds=13 calls=202 PC=0x12DF84 — logo-frontend MENU YES hold
  INTERACTIVE PARTIAL: leftPark=True (left 0x253F/0x2371 presentation);
    pad-script 77 fires + DBC paints; PC reacts post-leave
  residual: full main-menu chrome / second stream Soft-GS scene; natural DISPFB
```

| Gate | Result |
|------|--------|
| logo-frontend Soft-GS | **YES** (lit≈100k mostlyBlack=0 after FRONTEND) |
| PC leave presentation | **YES** (`leftPark=True`; final `0x12DF84` not logo/VBlank park) |
| Pad-driven activity | **YES** (script fires + PC/syscall movement post-leave) |
| Full main-menu INTERACTIVE | **NO** — no second UI stream / menu selection proof |
| Soft-GS scene change (new chrome) | **PARTIAL** — prims freeze after leave; no multi-chrome delta |

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
$env:DETPS2_TRACE_BIOS = '1'
# pad-inject INTERACTIVE probe:
@{
  id='menu-b3-2-pad'; media='burnout-only.json'; cycles=100000000
  hostPresent=$true; command='pad-inject'
  padScript='tools/pad-scripts/b3-menu-interactive.pad'
  extraArgs=@('--sample-every=2000000'); priority=0
} | ConvertTo-Json | Set-Content out/live-queue/inbox/menu-b3-2-pad.json
# claim metrics twin:
@{
  id='menu-b3-2-claim'; media='burnout-only.json'; cycles=100000000
  hostPresent=$true; command='blocker-trace'
  padScript='tools/pad-scripts/b3-menu-interactive.pad'; priority=1
} | ConvertTo-Json | Set-Content out/live-queue/inbox/menu-b3-2-claim.json
pwsh ./tools/live-game-queue.ps1 -MaxConcurrent 2 -Once
# expect: PC outside 0x253F/0x2371; [B3] PL-014 scene-delta leftPark=True; lit>0
```

---

## Seat S1-G1 (2026-07-31) — PL-014 pad logo→frontend

| Field | Value |
|-------|-------|
| Seat | **S4 BURNOUT** · branch `agent/seat-s4/s1-g1` |
| WP | **PL-014** B3 pad logo→frontend advance INTERACTIVE |
| Build | `out/seat-s4` Release · **SEMA_OFF** · `--host-present` · `burnout-only.json` |
| FREEZES | Soft-GS truth · SEMA_OFF · no FFmpeg · **no invent DISPFB plant** |

### Discovery

| Finding | Evidence |
|---------|----------|
| **No PADMAN OPEN** | TRACE_RPC 50–100M: zero `[RPC] PADMAN OPEN`; pad inject via classic PADMAN DMA is a no-op |
| **libdbc / DBCMAN pad path** | IRX: SIO2MAN→SIO2D→DBCMAN→**DS2O**; binds `0x80001300` + siblings `0x8000131B/1C`; live poll fno `0x80001301`/`02` thrash |
| **PsIIlibpad2 2800** | ISO strings next to `PsIIlibdbc 2800`; states EXECCMD/STABLE/NOLINK |
| Flip thrash after chrome | Pre-fix: leave flip park every ~1.6M @ `0x1F2508` with out=in=0 monopolized EE 50–100M |

### Assist / HLE changes

| Change | File | Notes |
|--------|------|-------|
| Edge pad train after logo chrome | `Burnout3Assist.cs` | START/CROSS/Circle/D-pad with explicit release frames; AnalogMode; cap via PL-014 pulse (n≈700 @100M) |
| Flip healthy leave gate | `Burnout3Assist.cs` | After Soft-GS chrome + healthy queues, stop PC-stomp thrash at flip wait |
| DBC work-buffer refresh | `RealSifRpc.cs` | `HandleDbcMan` captures high-RDRAM SetWorkAddr-class pointer and paints DualShock `padButtonStatus` (active-low) |
| Rejected | — | Painting `0x4E28xx` / random 64-align arg pointers / recv+0x10 during DBC init → residual→STG collapse (cdvd=609) |

### Claim 100M (SEMA_OFF)

```
PC=0x00253F88 px=24407048 prims=1513 gifP1=0 gifP2=328 gifP3=491 dmac=424
  sifBytes=149428 syscalls=41025 cdvdSectors=6584
softgs: imgBytes=2694848 dispfbPx=2273160 fragTest=22133895
  rejBounds=0 rejScissor=0 rejDepth=7 rejAlpha=0
softgs-regs: FRAME_1=0xA0046 DISPFB1=0 SCISSOR full XYOFFSET=0x72006C00 TEST=0x5140B
RealSifRpc: binds=13 calls=226 unknown=0
PL-014 logo-pad edges: n≥704 (first @28.6M cdvd=2425; denser after FRONTEND@40M)
```

**Claim line:**

```
B3 SLUS_210.50 seat-s4/s1-g1 SEMA_OFF 100M: STG+TXD+FRONTEND cdvd=6584
  gifP1=0 gifP2=328(p2qws=14526) gifP3=491 dmac=424 prims=1513
  px=24407048 dispfbPx=2273160 imgBytes=2694848 DISPFB1=0
  binds=13 calls=226 PC=0x253F88 — logo-frontend MENU YES hold
  PL-014: edge pad n≥704 + DBC path mapped; INTERACTIVE logo→menu NOT claimed
  residual: DBC SetWorkAddr/SRData button DMA + natural DISPFB; no DISPFB plant
```

**MENU:** logo-frontend Soft-GS **YES** (hold). **Not** claiming pad-interactive main menu / P1 complete — no second stream / scene change / sel-index delta after pad.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
$env:DETPS2_TRACE_BIOS = '1'
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s4 --nologo -v q
dotnet exec out/seat-s4/DetPS2.Core.dll blocker-trace burnout-only.json --cycles=100000000 --host-present
# expect: cdvd=6584 px≈24.4M DISPFB1=0; [B3] PL-014 logo-pad edge lines; no DETPS2_SEMA_STALL_YIELD
```

---

## Seat S2-G2 (2026-07-31) — PL-014 residual + PL-026 DBC work DMA

| Field | Value |
|-------|-------|
| Seat | **S4 BURNOUT** · branch `agent/seat-s4/s2-g2` |
| WP | **PL-014 residual** wire DBC SetWorkAddr/DS2O so pad reaches game; **PL-026** natural DISPFB if safe |
| Build | `out/seat-s4` Release · **SEMA_OFF** · `--host-present` · `burnout-only.json` |
| FREEZES | Soft-GS truth · SEMA_OFF · no FFmpeg · **no invent DISPFB plant** |

### Root cause (S1 residual)

| Finding | Evidence |
|---------|----------|
| DBC create arg+4 is work buffer | TRACE_RPC create `fno=0x80001304` → capture `work=0x0067CCC0` off=+4 |
| S1 only accepted high-RDRAM ≥0x01000000 | Mid-heap BSS `0x0067CCC0` was **rejected** → `_dbcWorkAddr` stayed 0 |
| ForceRefreshPad was PADMAN-only | B3 has **zero** PADMAN OPEN → force-refresh was a no-op between poll RPCs |
| Live poll thrash | `fno=0x80001301/02` on fixed `recvBuf=0x00679840` (client scratch ≠ pad DMA) |

### Fixes

| Change | File | Notes |
|--------|------|-------|
| Broader work-addr capture | `RealSifRpc.cs` | Scan arg words 0..5; accept 64-align EE RDRAM ≥0x00400000 (not stack / not flip 0x4E28xx) |
| pad_data_old dual-buffer paint | `RealSifRpc.cs` | STABLE + active-low DualShock @ work / work+0x40; bare status @ +0x80 |
| ForceRefreshPad → DBC too | `RealSifRpc.cs` | `ForceRefreshDbcPad` so logo edges refresh work between SIF polls |
| Logo-pad telemetry | `Burnout3Assist.cs` | Log `dbcWork` + `paints`; client-scan fallback if capture misses |
| Rejected | — | No DISPFB plant; no recv+0x10 paint during create (STG collapse class) |

### Claim 100M (SEMA_OFF)

```
PC=0x00253F88 px=23620619 prims=4954 gifP1=0 gifP2=340 gifP3=491 dmac=424
  sifBytes=149428 syscalls=41025 cdvdSectors=6584
softgs: imgBytes=2694848 dispfbPx=1486728 naturalDispfbPx=1474560 expandHits=164
  fragTest=75399048 rejDepth=53265157 rejAlpha=3
softgs-regs: FRAME_1=0xA0046 DISPFB1=0 SCISSOR full XYOFFSET=0x72006C00 TEST=0x5140B
softgs-writes: total=24960 PRIM=7054 XYZ2=7229 FRAME=84 SCISSOR=84
RealSifRpc: binds=13 calls=226 unknown=0
DBC: work=0x0067CCC0 paints≥1588 (create capture + ForceRefresh)
PL-014 logo-pad edges: n≥704
```

**Claim line:**

```
B3 SLUS_210.50 seat-s4/s2-g2 SEMA_OFF 100M: STG+TXD+FRONTEND cdvd=6584
  gifP1=0 gifP2=340(p2qws=14538) gifP3=491 dmac=424 prims=4954
  px=23620619 dispfbPx=1486728 naturalDispfbPx=1474560 imgBytes=2694848 DISPFB1=0
  binds=13 calls=226 PC=0x253F88 — logo-frontend MENU YES hold
  PL-014: DBC work=0x0067CCC0 paints≥1588 + edge pad n≥704; T2 INTERACTIVE logo→menu NOT claimed
  residual: pad-driven scene leave (PC/stream) + natural DISPFB; no DISPFB plant
```

**MENU:** logo-frontend Soft-GS **YES** (hold). **Not** claiming pad-interactive main menu — final PC still presentation `0x253Fxx` / flip thrash `0x1F2508`; no second stream open.

### PL-026 / DISPFB note (S10 shared path)

- Privileged **DISPFB1=0** still; `naturalDispfb` circuit **0** (no plant).
- `naturalDispfbPx` / `dispfbPx` remain **merge composite** fill counts (FRAME_1 + FBP0 IMAGE), not retail PCRTC bind.
- Prefer S10 GX-040 circuit decode; do not invent DISPFB registers.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
$env:DETPS2_TRACE_BIOS = '1'
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s4 --nologo -v q
dotnet exec out/seat-s4/DetPS2.Core.dll blocker-trace burnout-only.json --cycles=100000000 --host-present
# expect: cdvd=6584 DISPFB1=0; [RPC] DBC SetWorkAddr-class capture work=0x0067CCC0;
#         [B3] PL-014 logo-pad … dbcWork=0x0067CCC0 paints=…
```

