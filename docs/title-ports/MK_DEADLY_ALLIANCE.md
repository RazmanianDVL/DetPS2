# Mortal Kombat: Deadly Alliance (USA) — commercial port + draw-graph charter

| Field | Value |
|-------|--------|
| Title | Mortal Kombat - Deadly Alliance (USA) |
| Serial | `SLUS_204.23` |
| Media id | `mk-deadly-alliance` |
| ISO | `C:/Users/xxraz/Downloads/MortalKombatDeadlyAlliance(USA).iso` |
| BIOS | SCPH-70008 (E) v2.0 2004-06-14 |
| Config | `user-media-da.json` |
| Seat | **S3 MIDWAY-DA** |
| Worktree | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-seat-s3` |
| Branch | `agent/seat-s3/s2-g2` |
| Owned | `MidwayFamilyAssist.cs` **REGION DA**, DA docs |
| Forbidden | Dec-only regions thrash; Gs/Gif ownership; WaitSema fabricate global; Dmac END gate break |
| ROMDIR gate | **CLOSED** |
| Agent date | 2026-07-31 (PL-030 / S2 FRONTEND chrome after INTERACTIVE) |
| Tip base | S1 `0b09a21` / PL-030 claim below |

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

### Wall B — FRONTEND chrome (→ **P2** / PL-030) — **PARTIAL (S2)**

T3 **numeric** bars green on Soft-GS (prims≥10, imgBytes>0, dispfbPx>0). PL-030 title-local chrome:

| Residual | Owner | Status after PL-030 |
|----------|-------|---------------------|
| Fail-tail plant debt | S3 | **Reduced** — belt 5 demoted @20M; **core 6** remain |
| gif completed ratio | S3+S8 | **completed 323→2980**; abort n=289 residual (S8 Path2 sticky class) |
| Display-queue sticky lock | S3 | **Improved** — head moves ≥16 from menu-band (was 1 @93.8M) |
| imgBytes=98304 floor | S9 G2 / PL-045 | Held; no art-scale gameart TEX yet |
| gifP3 only **6** | S8/S9 | Sparse; no invent PATH3 |
| DISPFB 32k / circuit 640×448 | S10 | naturalDispfb=1 out=640x448; composite residual |

**Next:** art-scale IMAGE (GX-037 / S9) + further plant demotion when natural list-dispatch succeeds (PL-045).

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
cd C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-seat-s3
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
cd C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-seat-s3
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
cd C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-seat-s3
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s3 --nologo -v q
pwsh ./tools/run-title.ps1 -Media user-media-da.json -Budget diagnose -BuildOut out/seat-s3 -SkipBuild -HostPresent
pwsh ./tools/run-title.ps1 -Media user-media-da.json -Budget claim    -BuildOut out/seat-s3 -SkipBuild -HostPresent
```
