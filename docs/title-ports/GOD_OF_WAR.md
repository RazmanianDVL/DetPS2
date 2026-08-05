# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/user/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\user\.grok\worktrees\windows-detps2\detps2-seat-s6` |
| **Branch** | `agent/seat-s6/s2-g2` |
| **Seat** | **S6 GOW** (PL-016 pad + **PL-023** thrash leave; owned `GodOfWarAssist.cs` + this doc) |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Host→Local residual Soft-GS lit** (not natural MENU YES): @100M **px=634306 lit=60866** prims=3 expandHits=2 imgBytes=262144. compositeSource=**Frame**, naturalDispfbPx=**0**, residualDispfbPx=**60866**. Path2 expand strips + assist BITBLT R_SHELL/TIT1. PADMAN open=0 residual. |
| **Last updated** | 2026-07-31 residual honesty |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

**Honesty (2026-07-31 residual seat):** Soft-GS lit via **Host→Local residual** + expand-class Path2 is **not** natural MENU YES. Live claim:

```
claim: px=634306 prims=3 gifP2=31 gifP3=0 imgBytes=262144
       dispfbPx=60866 naturalDispfbPx=0 residualDispfbPx=60866 expandHits=2
       lit=60866/286720 compositeSource=Frame
job: gow-residual-100m-20260731-151114
```

Prior “MENU YES hold” language = title-surface Soft-GS progress only; **do not promote natural MENU YES** while residual DISPFB + Host→Local IMAGE dominate.

---

## PL-005 + GX-008 — draw-graph charter (S6 / seat-s6)

**Tip base:** `20973c6` · **Build:** `out/seat-s6` Release · **Claim env:** `DETPS2_SEMA_STALL_YIELD` **OFF** (critical for GoW — SEMA_ON starves worker/type-2)

### Draw graph at MENU (Soft-GS truth)

```text
EE / Fedo stream (type-2 residual)
   │  R_SHELL / LoadWad seed (assist); natural decode not complete
   ▼
VIF1 DIRECT ──► GIF Path2 (PACKED A+D + SPRITE)
   │              sticky reassembly (WAVE-11C); 1 garbage DIRECT abort residual
   │
   ├─ Path2 SPRITE packs (tag#3 / tag#5 class)
   │     ofx/ofy = 0 at kick; corners (0,0)→(512,0) → raw 512×1 strips
   │     Gs.DrawSprite title-strip expand → 640×448 Soft-GS FB ×2
   │     color/UV from real prim (no host plant, no PATH3 invent)
   │
   ├─ Later Path2 A+D: FRAME/SCISSOR/TEST/XYOFFSET armed (after draws)
   │     @100M: FRAME_1=0x80000 XYOFFSET=0x7300/0x7000
   │
   └─ Path1 = 0 · Path3 = 0  (Path3MaskedByVif held; no shell IMAGE yet)
```

| Path | MENU role | Live @100M | Notes |
|------|-----------|------------|-------|
| **Path2** | **Primary** title surface | gifPath2=**31** · p2qws=**1262** · tags=31 | Real SPRITE + reg A+D; sticky GIF; PL-023 leave grew Path2 |
| **Path3** | Shell IMAGE / richer chrome | gifPath3=**0** · imgBytes=**0** | Do **not** invent PATH3; wait Fedo unmask |
| **Path1** | VU1 → GIF (gameplay/3D) | gifPath1=**0** | Post-menu / first-room target; natural only |
| **DISPFB** | Present composite | dispfbPx=**0** | S10 G-GFX-5/6; claim uses Soft-GS FB pixels |

### Expand-like strips (G-GFX demote target)

| Item | Value |
|------|--------|
| Class | Path2 SPRITE **ofx=0 / Y=0** full-width thin strips |
| Raw without expand | 512×1 ×2 ≈ **px=1026** (WAVE-11C residual) |
| With expand (MENU YES) | 640×448 ×2 = **px=573440** prims=2 |
| Expand owner | **S9 GFX-RASTER** (`Gs.DrawSprite` titleStrip) — S6 must **not** widen ofx policy without S9 note |
| NATURAL bar (P3 / G-GFX-6) | retail XYOFFSET armed **before** kick **or** expand_hits=0; PL-041 owns demote attempt |
| S6 duty | Keep Path2 sticky + Fedo consumer path; report walls to S8/S9; **demote expand is G-GFX, not S6 invent** |

### INTERACTIVE pad charter (PL-016 / PL-023 / P1)

| Item | Plan |
|------|------|
| Gate P1 | Pad inject changes selection/state **or** prims/gif increase after pad @100M |
| **PL-016 landed** | Pad inject **only after Soft-GS `px>0`** (`MaybeInjectPadAfterSoftGs`); dense START/CROSS/D-pad/Circle edges @~50k; `ForceRefreshPad` + `OnHostPresent` refresh |
| **PL-023 landed** | Sticky DMA tag finish at `0x13F5xx` (W10 protect: never rehome as thrash). Write END `0x70000000`, advance `*0x32F168`, leave via `$ra` / post-FreezeCache; optional worker yield under Soft-GS |
| Evidence @100M | pad n≈1664; softGsΔ gifP2 6→**31**; stateΔ≥3; **PL-023 n=1** leave `0x13F5F8` → final PC=`0x1837D0`; **PADMAN open=0 ghost=0** |
| Residual | Selection-index not claim-green (no PAD OPEN); softGsΔ concurrent Path2 not proven pad-causal |
| Next WP | Natural PADMAN OPEN so padRead dual-buffer sees START/CROSS; Fedo past IRX-only |
| Freezes | No global WaitSema; classic StartThread PC+4; SEMA_OFF claims; no invent PATH3; **no kill DMA tag .text** |

### Fedo / Path1 / Path3 natural charter

| Track | Residual | Honest next |
|-------|----------|-------------|
| **Fedo** | type-2 post-stream; shell decode seed after first Soft-GS px; **cdvd=142** IRX-only class | Consumer → natural PRIM/XYZ beyond 2 expand strips; no cmd invent |
| **Path3** | 0 transfers; Path3MaskedByVif | Real game unmask + GIF PATH3 IMAGE; **no plant** |
| **Path1** | 0 | Gameplay VU1 path after frontend; S8 Path1 prelude (GX-012+) |
| **cdvd** | 142 sticky | Separate from title-surface Soft-GS; stream past IRX-only when Fedo opens more |

### Residual / walls (for S8–S10 DISCOVERY_LOG)

1. **Expand dependency** — MENU px is expand-class; NATURAL needs ofx-armed-before-draw or strip fidelity (S9 GX-021 / PL-041).  
2. **gifPath3=0 / imgBytes=0** — no shell IMAGE texture path yet (G-GFX-3).  
3. **aborted=1** — intentional garbage first DIRECT IMM=0xBF0; leave.  
4. **PC thrash `0x13F5F8`** — **PL-023 cleared** (finish END; final PC left band). Do not rehome as thrash.  
5. **Pad INTERACTIVE partial** — pad-after-px densified; **PAD OPEN=0** residual blocks selection prove.

### Overflow note (Haven)

Haven is **S6/S7 secondary queue only**. This WP is GoW-primary; Haven IMAGE residual / CallRpc SP left for overflow when GoW INTERACTIVE holds. No Haven claim this seat wave.

---

## Seat-s6 claim evidence (PL-005 measure) — SEMA_OFF

Build: `dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s6`  
Traces: `out/traces/seat-s6/gow-20m.txt`, `gow-100m.txt`

### Diagnose 20M

```
@20M:  PC=0x00283F08 px=573440 prims=2 gifPath1=0 gifPath2=6 gifPath3=0 dmac=1 cdvdSectors=142
       softgs: imgBytes=0 dispfbPx=0 fragTest=573440 rejBounds=0 rejScissor=0 rejDepth=0 rejAlpha=0
       softgs-regs: FRAME_1=0 DISPFB1=0x800000090D0 SCISSOR=0x01BF0000027F0000 XYOFFSET=0 TEST=0
       softgs-writes: total=1742 PRIM=1230 XYZ2=4 XYZ3=245 FRAME=0 SCISSOR=0 TEST=0 XYOFF=0
       gif-pkts: completed=5 aborted=1 spannedCalls=1 tags=6 p2qws=887
       expand-class: title-surface px already at 20M (2× full Soft-GS FB from Path2 strips)
```

### Claim 100M (SEMA_STALL_YIELD OFF) — PL-023

```
@100M: PC=0x001837D0 px=573440 prims=2 gifPath1=0 gifPath2=31 gifPath3=0 dmac=50
       softgs: imgBytes=0 dispfbPx=0 expandHits=2 fragTest=573440 rejBounds=0 rejScissor=0 rejDepth=0 rejAlpha=0
       softgs-regs: FRAME_1=0x80000 DISPFB1=0 SCISSOR=0x019F000001FF0000
                    XYOFFSET=0x730000007000 TEST=0x50000
       softgs-writes: total=2092 PRIM=1230 XYZ2=4 XYZ3=245 FRAME=25 SCISSOR=25 TEST=25 XYOFF=25
       gif-pkts: completed=30 aborted=1 spannedCalls=1 inFlight=False tags=31 p2qws=1262
       Path2: 2 real SPRITE packs expanded to 2× full Soft-GS FB (no PATH3 invent)
       cdvdSectors=142 (IRX-only stream residual; separate from title-surface Soft-GS)
       PL-023: finish DMA tag n=1 @0x13F5F8 cursorBad (slot=0x01CFD000 END) → leave
               switch-worker sleep-cmd tid=3; final PC left 0x13F5xx band
       PL-016 pad: after Soft-GS only; n≈1664 @~50k edges; open=0 ghost=0
                   softGsΔ concurrent gifP2 6→31 in pad window; stateΔ≥3
       RealSifRpc: binds=10 calls=66 unknownServiceCalls=0
```

**Claim line (scoreboard):**

```
GoW SCUS_973.99 | S6 seat-s6 | MENU YES hold | SEMA_OFF | @100M px=573440 prims=2 gifP1=0 gifP2=31 gifP3=0 FRAME=0x80000 completed=30 aborted=1 cdvd=142 expandHits=2 | PL-023 leave 0x13F5F8 (END finish, no tag kill) PC=0x1837D0 | PL-016 pad densified (open=0 residual) | NATURAL=no (expand)
```

---

### Wave-12B evidence (agent/menu-gow-w12b) — title-surface scale

#### Root cause (px=1026 residual after WAVE-11C)

Live Path2 SPRITEs (tag#3 / tag#5) kick with **ofx/ofy still 0** and both corners **Y=0**:
`(0,0) → (512,0)` → Soft-GS **512×1** strips ×2 = **px≈1026**.  
XYOFFSET=`0x7000/0x7300` is armed only by later A+D packets **after** the draws.  
Whiplash/BO2 title-strip expand required ofx=0x8000 — missed this ofx=0 Path2 class.

#### Fix

1. **Gs.DrawSprite:** full-width thin strips (`w ≥ FB_WIDTH/2`, `h < FB_HEIGHT/2`) with ofx/ofy = 0 **or** 0x8000 **or** retail-center band `0x6000..0x9000` expand to full Soft-GS title FB (640×448). Color/UV still from the real prim.
2. **GodOfWarAssist:** keep post-type-2 stream kick + shell decode seed after first Soft-GS px; residual thrash escape at `0x13F5F8` no longer requires `cdvd>400 && px==0` (live IRX-only is cdvd=142).

Smokes: prior WAVE-11C Path2 suite + `Gs_Path2_Ofx0_Y0_Sprite_ExpandsTitleSurface` (px≥143360 title floor).

### Wave-11C evidence (agent/menu-gow-w11c) — Soft-GS Path2

#### Root cause (why FRAME_1=0 / prims=0 at gifP2=1082)

1. **VIF1 QW-sliced Path2** delivered one QW per `ReceivePath2Data` → GIFtag consumed, PACKED body dropped (sticky reassembly fix).
2. **DIRECT mid-QW** started Path2 at `addr&0xF!=0` → garbage IMAGE/REGLIST tags (QW-align pad fix).
3. **First DIRECT IMM=0xBF0** at `0x46BE90` was non-GIF payload (`A90BB00D…`). Sticky REGLIST `nloop=12301` **swallowed later real PACKED A+D** at `0x3969xx`. Fix: abort incomplete GIF packet on new DIRECT / DIRECT-end truncate.

#### Claim 100M (pre-expand) — Soft-GS

```
@100M: px=1026 prims=2 gifPath2=19 FRAME_1=0x80000
       gif-pkts: completed=18 aborted=1  (aborted=1 = garbage first DIRECT residual)
```

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** |
| Soft-GS Path2 FRAME+PRIM+XYZ2 | **Yes** (w11c) |
| Soft-GS **px>0** | **Yes** (**px=573440** title-surface) |
| first-gs-interactive MENU | **YES** (title-surface Soft-GS + pad inject) |
| PL-016 pad-after-px | **YES** (gated Soft-GS; densified; ForceRefreshPad) |
| PL-023 thrash leave `0x13F5F8` | **YES** (finish END; final PC=`0x1837D0`; no tag .text kill) |
| INTERACTIVE (P1 selection/state) | **Partial** — pad surface live; **PAD OPEN=0** residual |
| NATURAL (no expand) | **No** (ofx expand class; G-GFX demote) |
| gifPath3 / shell IMAGE | **No** (gifPath3=0; imgBytes=0) |
| Full R_SHELL / type-2 stream | Residual (cdvd=142 IRX-only variance) |

### Wall / next

1. Natural **PADMAN OPEN** so padRead dual-buffer sees START/CROSS (open=0 residual).  
2. **G-GFX demote expand** (S9) when retail ofx armed before SPRITE kick — S6 reports only.  
3. **Shell / PATH3 IMAGE** for richer chrome — do not invent PATH3 packets.  
4. **Post-type-2 stream** past IRX-only when cdvd stuck 142 (Fedo consumer → more natural PRIM/XYZ).  
5. Path3MaskedByVif held unless game unmasks with real GIF PATH3.  
6. **aborted=1** residual is intentional (garbage DIRECT IMM=0xBF0) — leave.  
7. Final PC band `0x1837D0` / flip residual — observe only; do not rehome healthy `0x13F5xx`.

### PL-016 / PL-023 assist notes

`GodOfWarAssist.MaybeInjectPadAfterSoftGs`:
- Gate: `sys.Gs.PixelsWritten > 0` only (no pre-Soft-GS pad thrash).
- Cadence: ~50k MasterCycles; phase START / CROSS / Down / Up / Circle / dual / Left / Right / release.
- `RealSifRpc.ForceRefreshPad` after each edge; `OnHostPresent` refreshes when Soft-GS live.
- Tracks softGsΔ (prims/gifP2/gifP3) and stateΔ (flip `*0x2AC7D0` / worker cmd) for claim telemetry.

`GodOfWarAssist.TryFinishDmaTagBuilder` (PL-023 / WAVE-11B restore):
- Band: `0x13F540..0x13F6A8` retail GIF/VIF DMA tag builder (QWC + END).
- **Never** rehome as residual thrash (W10 CRITICAL — kills FRAME chain).
- Sticky ≥200k mid-align-pad / poison `*0x32F168` → write END `0x70000000`, advance cursor, leave via `$ra` / `0x185FAC`.
- Gate: Soft-GS `px>0` **or** type-2 complete (MENU floor path does not need type-2).
- After leave: credit GIF/VIF1 handlers; optional `TryYieldToPendingWorker` under Soft-GS.
- Forbidden: invent PATH3; ofx expand without S9; SEMA_ON; StartThread `$ra` global; kill DMA tag builders.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s6
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/seat-s6/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=20000000 --host-present
dotnet exec out/seat-s6/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
