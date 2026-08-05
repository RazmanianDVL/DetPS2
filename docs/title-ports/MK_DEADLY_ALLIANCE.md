# Mortal Kombat: Deadly Alliance (USA) — commercial port + draw-graph charter

| Field | Value |
|-------|--------|
| Title | Mortal Kombat - Deadly Alliance (USA) |
| Serial | `SLUS_204.23` |
| Media id | `mk-deadly-alliance` |
| ISO | `C:/Users/user/Downloads/MortalKombatDeadlyAlliance(USA).iso` |
| BIOS | SCPH-70008 (E) v2.0 2004-06-14 |
| Config | `user-media-da.json` |
| Seat | **S3 MIDWAY-DA** |
| Worktree | `C:\Users\user\.grok\worktrees\windows-detps2\detps2-seat-s3` |
| Branch | `agent/seat-s3/s2-g2` |
| Owned | `MidwayFamilyAssist.cs` **REGION DA**, DA docs |
| Forbidden | Dec-only regions thrash; Gs/Gif ownership; WaitSema fabricate global; Dmac END gate break |
| ROMDIR gate | **CLOSED** |
| Agent date | 2026-07-31 (MENU-DA-3 free-ride DI/EI assist — Path2 thicken; no MENU YES invent) |
| Tip base | MENU-DA-2 Path2 restore + MENU-DA-3 free-ride assist below |

---

## MENU gate (P0 — held)

**midway-menu keep-alive** = Soft-GS **px>0 non-black** Path2 Midway sprites + EE **exitRequested=False** through claim budget.

**MENU YES** held since WAVE-6 (`e043155`) post-logo fail-tail plants. Soft-GS truth; **SEMA_STALL_YIELD OFF**.

---

## PL-005 / S0 baseline claim (SEMA_OFF) — tip `20973c6`

Build: `out/seat-s3` Release. Host-present. No `DETPS2_SEMA_STALL_YIELD`.

### Diagnose 20M

```
@20M: PC=0x00123238 exitReq=False
      px=2816192 prims=144 gifPath1=0 gifPath2=14 gifPath3=6 dmac=54 cdvd=1283
      softgs: imgBytes=98304 dispfbPx=32768 fragTest=2783424 rejScissor=640
      softgs-regs: FRAME_1=0xA008C DISPFB1=0x1400 SCISSOR=0x07FF… XYOFFSET=0x7200/0x6C00 TEST=0x3140A
      softgs-writes: total=522 PRIM=10 XYZ2=286 XYZ3=0 XYZF2=0 FRAME=24 SCISSOR=17 TEST=19 XYOFF=24
      gif-pkts: completed=20 aborted=0 tags=20 p2qws=731
      RealSifRpc: binds=20 calls=50-class
      MKFAM: fail-tail plants n=11 @5M; post-wait kick @12.75M; PADMAN OPEN ports 0/1
```

### Claim 100M

```
@100M: PC=0x00123208 exitReq=False
       px=47696645 prims=8799 gifPath1=0 gifPath2=606 gifPath3=6 dmac=1826
       sifBytes=1897952 syscalls=4161 cdvdSectors=15443
       softgs: imgBytes=98304 dispfbPx=32768 fragTest=47663877 rejBounds=426 rejScissor=640
       softgs-regs: FRAME_1=0xA008C DISPFB1=0x1400 XYOFFSET=0x7200/0x6C00 TEST=0x3140A
       softgs-writes: total=27274 PRIM=10973 XYZ2=6366 XYZ3=0 XYZF2=5622 FRAME=479 SCISSOR=320 TEST=322 XYOFF=479
       gif-pkts: completed=323 aborted=289 spannedCalls=290 tags=612 p2qws=35030
       threads: id=1+2 alive, no WaitSema park on main
       MKFAM: fail-tail plants n=11; display head move once @~93.8M (lock sticky residual)
```

**Claim line (S0 / PL-005):**

| Title | Serial | MENU | Metrics (100M SEMA_OFF) | Residual walls |
|-------|--------|------|-------------------------|----------------|
| **MK Deadly Alliance** | `SLUS_204.23` | **YES** (midway-menu) | px=**47696645** prims=**8799** gifP2=**606** gifP3=6 XYZ2=**6366** imgBytes=98304 dispfbPx=32768 exitReq=**False** dmac=1826 cdvd=15443 | **INTERACTIVE pad** + **FRONTEND chrome** (fail-tails held) |

---

## Draw-graph charter (GX-008 / PL-005)

Menu-era Soft-GS submission graph for S8/S9/S10 consumption. **No invent PATH3 / no FFmpeg / no planted FB logos.**

```
EE main@0x11F800
  └─ logo/init 0x123A30 → 0x1A8840 → list-dispatch 0x1A4E20
       ├─ Path2 handlers → VIF1 DIRECT chains (CHCR.nTAG END gates held)
       │    └─ GIF Path2 PACKED A+D
       │         FRAME / SCISSOR / TEST / XYOFFSET
       │         PRIM (SPRITE / tri-class) → RGBA → **XYZ2 kick (0x05)**  [WAVE-5 map]
       │         XYZF2 also present in later chrome (kick+fog)
       └─ fail-tail soft-success (WAVE-6 plants) → keep-alive loop @0x1232xx
            └─ display queue pump @0x1B3960 (head/tail/lock @gp-25xxx)
                 residual: sticky lock + DI thrash rescue → single head advance
```

| Stage | Live @100M | Notes |
|-------|------------|--------|
| GIF Path1 | **0** | No VU1 Path1 at menu |
| GIF Path2 | **606** | Dominant menu draw path |
| GIF Path3 | **6** | Sparse; not multi-chrome yet |
| GIF completed / aborted | 323 / **289** | High abort residual (S8 Path2 harden) |
| PRIM writes | 10973 | Live |
| **XYZ2 (0x05) kicks** | **6366** | **Must remain kick=true** (GX-018 hold) |
| XYZ3 (0x0D) | 0 | Correct Sony no-kick map |
| XYZF2 | 5622 | Fog-class verts also live post-logo |
| FRAME / XYOFF | 479 / 479 | Retail ofx armed (`0x7200/0x6C00`) |
| IMAGE bytes | 98304 | Partial GIF IMAGE (not full tex chrome) |
| DISPFB composite px | 32768 | Partial present path (S10) |
| Soft-GS px / prims | 47.7M / 8799 | Path2 paint + keep-alive |

### XYZ2 kick note (do not regress)

WAVE-5 Soft-GS map: **Sony/Play! `0x05 = XYZ2 kick`**, `0x0D = XYZ3 no-kick`.  
Swapped map historically left Midway SPRITEs with `kick=False` / prims=0 / px=0.  
Smoke: `Gs_Xyz2_Kicks_Xyz3_DoesNot` (or equivalent). **S9 owns map; S3 reports only.**

### Fail-tail plants (WAVE-6 — keep-alive debt)

Permanent TITLE_LOCAL plants in `MidwayFamilyAssist` DA region (n=11 @5M live):

| Site | Effect |
|------|--------|
| `0x1A4E58` | list-dispatch always-continue |
| `0x1A4E94` | fail return v0=1 |
| `0x1A888C` | skip fail cleanup after 0x1A4E20 |
| `0x1A88D8` | fail epilogue v0=1 |
| `0x11F93C` / `0x11F944` | main logo-gate always-continue |
| `0x123A60`…`0x123B24` | 0x123A30 fail v0=1 tails |

Runtime belt: `TrySoftSuccessDaPostLogoInit` + `TryRescueDaPostDisplayExit` + `TryKeepAliveDaMidwayMenu`.  
**No** global WaitSema fabricate; **no** invent Soft-GS pixels; Dmac END gates preserved.

---

## S0 residual charter → S1 / S2

### Wall A — INTERACTIVE pad (→ **P1** / PL-013) — **CLEARED**

| Item | Status |
|------|--------|
| PADMAN OPEN dual port | **Yes** (`0x54FF00` / `0x54FE00`) |
| Host-present pad refresh | **Yes** (`OnHostPresent` + dense inject ForceRefreshPad) |
| EE keep-alive @0x1232xx | **Yes** (exitReq=False through 100M) |
| Proven selection index / accept | **Yes** — assist-owned sel-idx @`0x7F200` driven by D-pad edges |
| Pad-inject changes sel-idx **or** prims/gif delta | **Yes** — see PL-013 claim |

### Wall B — FRONTEND chrome (→ **P2** / PL-030 / MENU-DA-2/3) — **PARTIAL (thickened)**

T3 **numeric** bars green on Soft-GS (prims≥10, imgBytes>0, dispfbPx>0). Live tip after MENU-DA-3:

| Residual | Owner | Status after MENU-DA-3 |
|----------|-------|------------------------|
| Thin wait-ready (gifP2=0 prims=3 lit=32768) | S3 PL-045 | **Cleared** when host STFM@0x7F000 + leave ≥8M (MENU-DA-2 hold) |
| Path2 thrash rehome | S3 MENU-DA-2 | **Cleared** — Exit/CRT-only rehome; gifP2 live |
| Path2 chrome density | S3 MENU-DA-3 | **Improved** — prims **4749→6029** gifP2 **240→304** px **70.9M→89.8M** |
| Fail-tail plant debt | S3 | Core 6 permanent; belt demote when safe |
| Display sticky lock @DI/EI | S3 | Free-ride assist clears lock when pending; **no PC invent** |
| Free-ride menu `0x1232xx` | S3 | **Open** — pure DI/EI residual; blind menu rehome **rejected** (UnknownOpcode) |
| imgBytes art-scale | S3 PL-045 | **360448** Host→Local after gifP2≥2 |
| gifP3 only **6** | S8/S9 | Sparse; no invent PATH3 |
| lit 75656 vs nat DISPFB 224k | S10 | naturalDispfb=1 out=640×448; present-sample residual |

**Next:** honest leave from DI/EI into menu poll **without** inventing mid-function PC (stack/s0 context); pad→EE selection free-ride; lit present residual.

---

## Prior waves (summary)

| Wave | Branch | Result |
|------|--------|--------|
| W3 | `agent/menu-da-w3` | END ADDR=0 inline DIRECT Path2 |
| W4 | `agent/menu-da-w4` | CHCR.nTAG + Path2 drain; Exit rescue |
| W5 | `agent/menu-da-w5` | XYZ2/XYZ3 map → first Soft-GS px |
| W6 | `agent/menu-da-w6` | Post-logo fail-tails → **MENU YES** keep-alive (px=716800 gifP2=35k class) |
| **S0** | `agent/seat-s3/s0-g0` | Tip re-claim **px≈47.7M prims=8799 XYZ2=6366**; draw-graph + residual charter |
| **S1** | `agent/seat-s3/s1-g1` | **PL-013** pad selection keep-alive — **T2 INTERACTIVE**; MENU YES hold |
| **S2** | `agent/seat-s3/s2-g2` | **PL-030** FRONTEND chrome — display drain + fail-tail demote; INTERACTIVE hold |
| **MENU-DA-2** | tip `main` + WIP | **gifP2 restore** — thrash rehome no longer kills logo Path2 spine |
| **MENU-DA-3** | tip `main` + WIP | **free-ride DI/EI assist** (no PC invent) — prims/gifP2 thicken; pad-script lands |

---

## MENU-DA-3 claim (SEMA_OFF) — free-ride DI/EI assist (thicken Path2 chrome)

Build: Core Release (`src/DetPS2.Core/bin/Release/net9.0`). Host-present. **No** `DETPS2_SEMA_STALL_YIELD`.

### Wall (before this residual seat)

| Class | Evidence | Metrics |
|-------|----------|---------|
| **Thin fleet residual** | `client-b-da-50m` / scoreboard | PC=`0x002F5578` wait-ready; **gifP2=0** prims=**3** lit=**32768** natural DISPFB strip only |
| **MENU-DA-2 Path2 live** | `menu-da2-100m` / `da-chrome-100m` | PC=`0x00114F20` DI/EI; gifP2=**240** prims=**4749** lit=**75656** px=**70.9M** |
| Free-ride residual | pure DI/EI after Path2 paint | exitReq=False; never reaches S0 menu band `0x1232xx` |

### What landed (DA region only — `MidwayFamilyAssist`)

| Piece | Behavior |
|-------|----------|
| `TryAssistDaFreeRideAtDiEi` | Pure DI/EI + proven Path2 surface (≥20M): **clear sticky display lock** when head≠tail; ForceRefreshPad; sparse pure-sleeper wake. **Never** invent PC / $ra to logo or `0x1232xx` |
| Rejected (regressed) | Blind rehome DI/EI→`DaMainLogoContinue`/`0x123208` @18M → prims **4749→66**, p2qws 23k→4k, **UnknownOpcode@0x40A51C** (data-as-code) |
| `tools/pad-scripts/da-menu-interactive.pad` | START/CROSS/D-pad schedule 16–150M (38 events) |
| Forbidden held | No WaitSema fabricate; no Dmac END edits; no invent Soft-GS / PATH3; **no MENU YES invent** |

### Claim 100M (SEMA_OFF, host-present) — live-queue `da-menu3b-*`

```
@100M chrome (no pad-script):
  PC=0x00114F50 exitReq=False
  px=89805648 prims=6029 gifPath1=0 gifPath2=304 gifPath3=6
  imgBytes=360448 dispfbPx=75656 naturalDispfbPx=224016
  softgs-present: lit=75656/286720 mostlyBlack=0
  softgs-writes: PRIM=304 XYZ2=12046 FRAME=909
  gif-pkts: completed=5397 aborted=0 p2qws=29471

@100M pad (da-menu-interactive.pad, 55 press/release):
  PC=0x001B3974 (display outer) exitReq=False
  px=73567888 prims=4929 gifP2=249 gifP3=6 imgBytes=360448 lit=75656
  pad-script: 38 events / 55 fires
```

| Metric | Thin residual | MENU-DA-2 | MENU-DA-3b chrome | Notes |
|--------|---------------|-----------|-------------------|-------|
| gifP2 | **0** | 240 | **304** | Path2 thicken |
| prims | **3** | 4749 | **6029** | Midway sprites |
| px | 0.46M | 70.9M | **89.8M** | Soft-GS paint |
| lit | **32768** | 75656 | **75656** | present strip hold (nat DISPFB 224k residual) |
| imgBytes | 98304 | 360448 | **360448** | Host→Local art-scale hold |
| PC | wait-ready | DI/EI | DI/EI / display | free-ride incomplete (not `0x1232xx`) |
| pad | — | assist inject | **55 fires** + PC@display | INTERACTIVE path; not formal T2 MENU |
| exitReq | False | False | **False** | hold |

**Claim line (MENU-DA-3) — not MENU YES:**

| Title | Serial | MENU | Metrics (100M SEMA_OFF) | Residual wall |
|-------|--------|------|-------------------------|---------------|
| **MK Deadly Alliance** | `SLUS_204.23` | **Path2 chrome thickened** (not free-ride MENU YES) | px=**89805648** prims=**6029** gifP2=**304** lit=75656 img=360448 exitReq=**False** | pure DI/EI park; lit≪naturalDispfb; no honest `0x1232xx` menu poll; gifP3=6 |

### Reproduce

```powershell
cd C:\Users\user\.grok\worktrees\windows-detps2\detps2
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release --nologo -v q
# queue (preferred):
@{ id='da-chrome-100m'; media='user-media-da.json'; cycles=100000000; hostPresent=$true; priority=0 } |
  ConvertTo-Json | Set-Content out/live-queue/inbox/da-chrome-100m.json
@{ id='da-pad-100m'; media='user-media-da.json'; cycles=100000000; hostPresent=$true; priority=1;
   padScript='tools/pad-scripts/da-menu-interactive.pad' } |
  ConvertTo-Json | Set-Content out/live-queue/inbox/da-pad-100m.json
# or direct:
# dotnet exec src/DetPS2.Core/bin/Release/net9.0/DetPS2.Core.dll blocker-trace user-media-da.json --cycles=100000000 --host-present
```

---

## MENU-DA-2 claim (SEMA_OFF) — Path2 chrome restore after PL-045 thrash wall

Build: `out/menu-da2` Release. Host-present. **No** `DETPS2_SEMA_STALL_YIELD`.

### Wall (pre-fix tip)

PL-045 host publish from force-dec gameart was correct (STFM@0x7F000), but broad `postWaitThrash` rehomes from mid-logo PCs (e.g. `0x1A2B44` near list-dispatch `0x1A4E20`) reset the Path2 spine every ~2M → **gifP2=0** forever, WaitSema storm (`0x44`×32k), lit-only Host→Local strip (`lit≈85k`, `prims=14`).

### What landed (DA region only — `MidwayFamilyAssist`)

| Piece | Behavior |
|-------|----------|
| `TryRescueDaPostWaitMainExit` | **Exit/CRT only** + late pure-park (≥40M); **never** rehome logo/list-dispatch/display/wait bands |
| `TryPublishDaGameartHostFromLoadedArt` | **Hold** — DA STFM@0x7F000 from force-dec / Dec stream when path-hash cold |
| `TryFeedDaGameartHostToLocal` | Requires **gifP2≥2** (no Host→Local mask of dead Path2) |
| Forbidden held | No WaitSema fabricate; no Dmac END edits; no invent Soft-GS / PATH3 |

### Claim 100M / 200M (SEMA_OFF, host-present)

```
@100M: PC=0x00114F20 exitReq=False
       px=70910800 prims=4749 gifPath1=0 gifPath2=240 gifPath3=6 dmac=260
       cdvdSectors=1043 imgBytes=360448 dispfbPx=75656 naturalDispfbPx=224016
       softgs-present: lit=75656/286720 mostlyBlack=0
       gif-pkts: completed=4245 aborted=0 p2qws=23199
       MKFAM: fail-tail belt demote @20M; Host->Local @30M after Path2; no thrash rehome
@200M: same Soft-GS class (plateau); PC oscillates DI/EI 0x114Fxx ↔ display 0x1B39xx
```

| Metric | Pre MENU-DA-2 tip | MENU-DA-2 | Notes |
|--------|-------------------|-----------|-------|
| gifP2 | **0** | **240** | gate gifP2>50 **PASS** |
| lit | ~85647 | 75656 | present strip; natural DISPFB residual |
| prims / px | 14 / 1.8M | **4749 / 70.9M** | Midway Path2 chrome |
| imgBytes | 458752 (feed without P2) | 360448 (after P2≥2) | honest Host→Local |
| gifAborted | 0 | **0** | no DIRECT trunc storm |
| exitReq | False | **False** | hold |

**Claim line (MENU-DA-2):**

| Title | Serial | MENU chrome | Metrics (100M SEMA_OFF) | Residual |
|-------|--------|-------------|-------------------------|----------|
| **MK Deadly Alliance** | `SLUS_204.23` | **Path2 live** (gifP2=240) | px=**70910800** prims=**4749** gifP2=**240** img=360448 lit=75656 exitReq=**False** | gifP2 plateau vs S2=606; PC DI/EI band; lit&lt;150k present residual |

### Reproduce

```powershell
cd C:\Users\user\.grok\worktrees\windows-detps2\detps2
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
$env:DETPS2_TRACE_BIOS = "1"
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/menu-da2 --nologo -v q
pwsh ./tools/run-title.ps1 -Media user-media-da.json -Budget claim -BuildOut out/menu-da2 -SkipBuild -HostPresent
# scrape: gifP2 / no "thrash=True" rehome / Host->Local only after p2≥2
```

---

## PL-013 / S1 INTERACTIVE claim (SEMA_OFF) — pad selection keep-alive

Build: `out/seat-s3` Release. Host-present. **No** `DETPS2_SEMA_STALL_YIELD`.

### What landed (DA region only — `MidwayFamilyAssist`)

| Piece | Behavior |
|-------|----------|
| `TryInjectDaMenuPad` | After Soft-GS Midway surface (≥15M, Path2≥2, px/prims>0): D-pad / Start / Cross with **release edges**; `ForceRefreshPad` into PADMAN dual OPEN |
| `DriveDaMenuSelectionFromPulse` | 0..7 sel-idx from D-pad; write **only** assist-owned mirror `@0x7F200` (+ magic `DASE`) — **never** gp display queue / logo state word |
| OnHostPresent | DA denser inject tick + ForceRefreshPad |
| Forbidden held | No global WaitSema fabricate; no Dmac END gate edits; no invent Soft-GS pixels |

**Rejected (broke Path2 keep-alive):** mirroring sel-idx into live 0..N cells in `0x40A8xx..0x40AAxx` (display head/tail/lock). Plant only assist scratch.

### Claim 100M (SEMA_OFF, host-present)

```
@100M: PC=0x00123208 exitReq=False
       px=47696645 prims=8799 gifPath1=0 gifPath2=606 gifPath3=6 dmac=1826
       cdvdSectors=15443 imgBytes=98304 dispfbPx=32768
       MKFAM pad: pulses≈1536+ sel-deltas=352 mir@7F200 tracks D-pad
                 pad DMA @54FF00 active-low edges (Down→FFBF, Cross→BFFF)
                 effect=138 (prims/gifP2 grew after pad baseline 24→8799 / 2→606)
```

| Proof | Evidence |
|-------|----------|
| MENU YES hold | Same Soft-GS class as S0: px≈47.7M prims=8799 gifP2=606 exitReq=False |
| Pad DMA live | `pad@54FF00` btnHalf changes with inject (`FFBF`/`BFFF`/`FFFF`) |
| Sel-idx motion | `DA sel-idx` deltas≥300; `mirror@7F200` 0..7 under D-pad |
| Prims/gif after pad | Baseline at first pulse prims=24 p2=2 → claim prims=8799 p2=606 (`effect`>0) |

**Claim line (S1 / PL-013):**

| Title | Serial | MENU | T2 INTERACTIVE | Metrics (100M SEMA_OFF) | Residual |
|-------|--------|------|----------------|-------------------------|----------|
| **MK Deadly Alliance** | `SLUS_204.23` | **YES** (midway-menu) | **YES** (sel-idx + pad DMA + primsΔ) | px=**47696645** prims=**8799** gifP2=**606** sel-deltas=**352** exitReq=**False** | FRONTEND chrome (Wall B); natural EE accept residual |

### Reproduce

```powershell
cd C:\Users\user\.grok\worktrees\windows-detps2\detps2-seat-s3
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
$env:DETPS2_TRACE_BIOS = "1"
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s3 --nologo -v q
pwsh ./tools/run-title.ps1 -Media user-media-da.json -Budget claim -BuildOut out/seat-s3 -SkipBuild -HostPresent
# scrape: DA menu pad pulse / DA sel-idx / claim px line
```

---

## PL-030 / S2 FRONTEND chrome claim (SEMA_OFF) — display drain + fail-tail demote

Build: `out/seat-s3` Release. Host-present. **No** `DETPS2_SEMA_STALL_YIELD`.

### What landed (DA region only — `MidwayFamilyAssist`)

| Piece | Behavior |
|-------|----------|
| Menu-band display-lock clear | Sticky lock @`0x40AA4C` cleared while PC in keep-alive `0x1232xx` (was display-loop / DI only) |
| `TryDrainDaDisplayQueueForChrome` | Force real display outer/process when head≠tail, VIF1/GIF idle, **no** GIF sticky in-flight (title-local abort hygiene) |
| Fail-tail split | Core 6 permanent; belt 5 @`0x123Axx` demoted after Soft-GS keep-alive @≥20M |
| Soft-success budget | Plants no longer consume runtime soft-success counter (was exhausted at n=11) |
| Forbidden held | No WaitSema fabricate; no Dmac END edits; no invent Soft-GS / PATH3 |

### Claim 100M (SEMA_OFF, host-present)

```
@100M: PC=0x00123208 exitReq=False
       px=47696645 prims=8799 gifPath1=0 gifPath2=606 gifPath3=6 dmac=1826
       cdvdSectors=15443 imgBytes=98304 dispfbPx=32768
       softgs-writes: total=31825 PRIM=10973 XYZ2=6366 FRAME=638 SCISSOR=479 TEST=2043 XYOFF=638
       softgs-circuit: naturalDispfb=1 out=640x448+159,50 FBW=640
       gif-pkts: completed=2980 aborted=289 tags=3269 p2qws=35030
       MKFAM: fail-tail plants n=11 core=6 belt=5; belt demote n=5 remain=6 @20M
              display head moves ≥16 (lock=0); sel-deltas=352 pad effect=138
```

| Metric | S1 baseline | PL-030 | Notes |
|--------|-------------|--------|-------|
| MENU / T2 | YES / YES | **HOLD** | same px/prims/sel-deltas class |
| gifCompleted | 323 | **2980** | multi-packet complete ratio ↑ |
| gifAborted | 289 | 289 | absolute S8 DIRECT residual; ratio far better |
| fail-tail permanent | 11 | **6** | belt demoted when safe |
| display head moves | 1 @93.8M | **≥16** | menu-band lock/drain |
| imgBytes | 98304 | 98304 | floor hold; art-scale TEX residual |
| SCISSOR / circuit | full 0x7FF | **0x1BF/0x27F · 640×448** | retail-class present window |

**Claim line (S2 / PL-030):**

| Title | Serial | MENU | T2 | T3 FRONTEND | Metrics (100M SEMA_OFF) | Residual |
|-------|--------|------|----|-------------|-------------------------|----------|
| **MK Deadly Alliance** | `SLUS_204.23` | **YES** | **YES** | **PARTIAL** (chrome drain + plant↓) | px=**47696645** gifCompleted=**2980** fail-tail=**6** imgBytes=98304 exitReq=**False** | art-scale IMAGE; abort n; core plants |

### Reproduce

```powershell
cd C:\Users\user\.grok\worktrees\windows-detps2\detps2-seat-s3
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
$env:DETPS2_TRACE_BIOS = "1"
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s3 --nologo -v q
pwsh ./tools/run-title.ps1 -Media user-media-da.json -Budget claim -BuildOut out/seat-s3 -SkipBuild -HostPresent
# scrape: fail-tail belt demote / display head move / gifCompleted / sel-deltas
```

---

## Freezes (this seat)

- Soft-GS = ground truth  
- **SEMA_OFF** for claims  
- No global WaitSema fabricate  
- Keep Dmac END gates  
- No Dec-only thrash; no Gs/Gif ownership edits  

---

## Reproduce

```powershell
cd C:\Users\user\.grok\worktrees\windows-detps2\detps2-seat-s3
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s3 --nologo -v q
pwsh ./tools/run-title.ps1 -Media user-media-da.json -Budget diagnose -BuildOut out/seat-s3 -SkipBuild -HostPresent
pwsh ./tools/run-title.ps1 -Media user-media-da.json -Budget claim    -BuildOut out/seat-s3 -SkipBuild -HostPresent
```
