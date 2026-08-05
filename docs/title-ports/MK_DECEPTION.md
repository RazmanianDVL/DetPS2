# Mortal Kombat: Deception (USA) — title port + draw-graph charter

| Field | Value |
|-------|--------|
| **Title** | Mortal Kombat - Deception (USA) |
| **user-media id** | `mk-deception` |
| **Serial / BOOT2** | `SLUS_208.81` |
| **ISO** | `C:/Users/user/Downloads/MortalKombatDeception(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-deception.json` |
| **Assist** | `MidwayFamilyAssist` **REGION DEC** (`IsDeception`) |
| **Seat** | **S2 MIDWAY-DEC** / residual main worktree `detps2` |
| **Worktree** | `C:\Users\user\.grok\worktrees\windows-detps2\detps2` (live pad residual) |
| **Branch** | main worktree `detps2` (historical seat: `agent/seat-s2/s2-g2`) |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **MENU YES** hold · **P1 INTERACTIVE YES** (pad free-ride remeasure) · **G-GFX-3 partial** Host→Local — PL-012 + PL-029 + MENU-DEC-2 + freelist rehome |
| **Last updated** | 2026-07-31 (MENU-DEC free-ride residual) |

---

## MENU-DEC free-ride residual + pad queue (2026-07-31)

**Mandate:** Drain `dec-pad-100m-*` / free-ride remeasure; scrape INTERACTIVE (prim Δ, state change, pad pulses); Soft-GS truth; SEMA_OFF; no invent PATH3; queue ≤3. PC stuck `0x00115F64` was SleepThread park residual — keep-alive rehomes.

### Live-queue pad jobs (SEMA_OFF, host-present)

| Job | Budget | pad-script | PC | px | lit | prims | gifP2 | naturalDispfbPx | INTERACTIVE |
|-----|-------:|------------|-----|-----|----:|------:|------:|----------------:|:-----------:|
| **dec-pad-100m-20260731-150519** | 100M | `dec-menu-interactive.pad` (45 acts) | `0x003D9B88` freelist | **3015151** | **198959** | **146** | 16 | **231727** | **YES** proven |
| **dec-pad-free-20260731-150927** | 100M | same (45 acts) | **`0x001237F0` main** | 1834223 | **198959** | **66** | 8 | **231727** | **YES** proven + free-ride PC |
| dec-menu-dec2-150m-pi (prior) | 150M | pad-inject | `0x003BDEA0` freelist | 3015151 | 0 scrape | **146** | 16 | — | YES (prims>86) |

**dec-pad-100m claim:** `claim: px=3015151 prims=146 … gifP3=6 imgBytes=557056 naturalDispfbPx=231727 … lit=198959/286720`  
**dec-pad-free claim:** `claim: px=1834223 prims=66 … gifP2=8 gifP3=6 imgBytes=557056 naturalDispfbPx=231727 … lit=198959/286720`  
**present (both):** `softgs-present: lit=198959/286720 … mostlyBlack=0` · compositeSource=**NaturalDispfb** · enNatural=1

### Interactive signals (dec-pad-free free-ride remeasure)

| Signal | Evidence |
|--------|----------|
| INTERACTIVE proven | `[MKFAM] Dec INTERACTIVE proven sel=1 max=2 accepts=4 naturalΔ=8 … prims=45 pc=0x001B5CA8 cyc=59110000` |
| End pad state | `n=1479 accepts=12 kicks=8 natΔ=24 proven=1 effect=2 Δprims=63 Δpx=1.37M postPlatΔp=21 postPlatΔpx=363439 **pc=0x001237F0** keep=196` |
| Sleep park `0x115F64` | **cleared** — keep-alive `sleepPark=0` throughout; final PC main loop not CRT/sleep |
| Freelist thrash | **end PC fixed** — freelist rehome → `0x1237F0`; prior residual ended `0x3D9B88` / `0x3BDEA0` |
| Soft-GS chrome | **HOLD** lit≈**199k** NaturalDispfb · imgBytes=**557056** |
| prims peak tradeoff | free-ride plateaus **prims=66** (postPlat+21) vs MENU-DEC-2 peak **146** under denser force-process |

### Free-ride fix (minimal TITLE_LOCAL `MidwayFamilyAssist`)

| Change | Effect |
|--------|--------|
| Freelist thrash bands `0x3BA000–0x3BE000` / `0x3D9000–0x3DB000` (≥55M, Soft-GS live) | Rehome to main/`0x1B6A68` — end PC **`0x1237F0`** |
| Yield to pad-script external Press | Assist dense inject does not clobber `dec-menu-interactive.pad` holds |
| Sleep park keep-alive (existing) | Recovers `0x115F64` SleepThread / CRT park |

### Honest INTERACTIVE?

| Signal | Result |
|--------|--------|
| Soft-GS NaturalDispfb chrome | **YES** lit≈199k mostlyBlack=0 |
| Pad pulses / sel walk / accept | **YES** accepts≥12 natΔ=**24** proven=1 |
| Soft-GS Δ post-plateau | **YES** postPlatΔprims=**21** postPlatΔpx≈**363k** (weaker than MENU-DEC-2 Δ101/1.54M) |
| Final PC main menu loop | **YES** free-ride `0x1237F0` (was freelist residual) |
| Natural AnimMenu without assist kick | **PARTIAL** — natΔ cells move; full free-ride without accept-kick still open |
| gifP3 climb | **NO** — stuck at **6** (Path3MaskedByVif held; no invent) |

**Verdict:** **INTERACTIVE YES** · Soft-GS **MENU chrome HOLD** · free-ride **improved** (main PC + natΔ=24) · residual: prims peak below 146 under freelist rehome, gifP3=6, natural accept without kick.

### Residual walls (post free-ride wave)

1. **prims peak** — freelist→main rehome holds PC on AnimMenu loop but Path2 force-process volume drops (66 vs 146). Prefer main free-ride PC over freelist thrash; do not invent PATH3 to paint prims.
2. **gifP3=6 stuck** — Path3MaskedByVif freeze held.
3. **Natural free-ride** — full AnimMenu accept without assist post-CROSS kick still open.
4. **Dormant main** — keep-alive still fires on `dormant=1` / `0x1277C0` band under pad.

---

## S0 charter (PL-005) + S1 pad (PL-012) + S2 IMAGE (PL-029)

**Gates this seat feeds:**

| Gate | Name | Dec status |
|------|------|-------------|
| **P0** | MENU floor | **HOLD** — midway-menu Soft-GS lit≈199k NaturalDispfb |
| **P1** | INTERACTIVE | **YES (free-ride remeasure)** — accept latch + post-CROSS kick; main PC `0x1237F0`; natΔ≥24; Soft-GS postPlat Δ; freelist end-PC residual **cleared** |
| **G-GFX-3** | IMAGE path | **PARTIAL** — `gameart.ssf` body live (2.8 MiB); Soft-GS Host→Local feed **imgBytes=557056** (tiles=48 fed=458752); natural GIF `image=` still 1 |

**Freezes held:** Soft-GS truth · `DETPS2_SEMA_STALL_YIELD` **OFF** · no invent PATH3 · **Path3MaskedByVif** held · no DA-only thrash · no SM `MidwayBootAssist` · no global WaitSema fabricate.

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF | **OK** — `SLUS_208.81` |
| IOPRP DNAS300 + PADMAN OPEN | **OK** |
| Heap-tree cycle break (shared 0x3BA9xx) | **OK** (breaks @~9.5M / 17M / 20M) |
| Post-MSL Exit redirect plants | **OK** (19 plants @5M) |
| Idle queue process @`0x1B5D10` | **OK** (type 0x40/0x41/0x1101 drain) |
| **gameart.ssf** MWFILE open | **YES** — `\ps2dvd\art\gameart.ssf` size=**2836480** @~28M |
| Path-hash plant + stream publish | **YES** — entries=8 data=`0x01800000` stream=`0x0007E400` |
| Midway Path2 paint | **YES** — p2qws=**5988** Soft-GS px multi-M |
| PowerOff/WaitSema storm kill | **YES** — park idle @`0x1B6A68` keep-alive |
| **MENU YES** (midway-menu) | **YES** — Soft-GS keep-alive, `exitRequested=False` |
| gifPath3 growth / natural IMAGE chrome | **No** — gifP3=**6** stuck; natural `gif-tags image=1` |
| **Pad INTERACTIVE (P1 / PL-012)** | **HOLD** — assist-stable sel-idx + pad inject @ idle-pump |
| Full gameart TEX sample (G-GFX-3 / PL-029) | **PARTIAL** — Host→Local **imgBytes=557056** (SEC tiles); natural EE IMAGE residual |

---

## Soft-GS metrics (SEMA_OFF, host-present)

### Diagnose 20M — `out/seat-s2` tip / seat build 2026-07-31

```
@20M: PC=0x003BA980 px=749568 prims=4 gifPath1=0 gifPath2=1 gifPath3=6 dmac=9
      sifBytes=20972 syscalls=1493 cdvdSectors=287
      softgs: imgBytes=98304 dispfbPx=32768 fragTest=716800 rejScissor=640
      softgs-regs: FRAME_1=0xA005B DISPFB1=0 SCISSOR=0x01BF0000027F0000
                   XYOFFSET=0x72006C00 TEST=0x5101B
      softgs-writes: total=65 PRIM=3 XYZ2=6 FRAME=4
      gif-pkts: completed=7 aborted=0 tags=7 p2qws=12
      RealSifRpc: binds=17 calls=39
      phase: heap-tree cycle band (pre idle-kick / pre gameart)
```

### Claim 100M — PL-012 + PL-029 Host→Local (SEMA_OFF host-present)

```
@100M: PC=0x001B6BF0 px=22126909 prims=1445 gifPath1=0 gifPath2=145 gifPath3=6 dmac=153
       sifBytes=3567920 syscalls=25390 cdvdSectors=4535 spu2Samples=31928
       softgs: imgBytes=557056 dispfbPx=153405 expandHits=0 fragTest=21973504 rejScissor=640
       softgs-regs: FRAME_1=0xA005B DISPFB1=0 SCISSOR=0x01BF0000027F0000
                    XYOFFSET=0x72006C00 TEST=0x5101B
       softgs-writes: total=6025 PRIM=75 XYZ2=2886 FRAME=220 SCISSOR=148 TEST=152 XYOFF=220
       gif-pkts: completed=225 aborted=0 tags=225 p2qws=5988
       gif-tags: packed=224 reglist=0 image=1  (natural EE IMAGE still 1)
       RealSifRpc: binds=17 calls=2937
       gameart.ssf: open OK size=2836480 loaded=2836480 data=0x01800000 hdr=0x0061E5A0
       PL-029: Host->Local tiles=48 fed=458752 imgBytes 98304→557056 @~30M
       keep-alive: idle-pump 0x1B6A68 / PC@end 0x1B6BF0 exitRequested=False
       pad: inject n≥1280 sel plants=512 *0x5DC000 tracks 0..4 under D-pad
            sel-idx-delta logs (e.g. 0x5DC000:3->4 dpad=1 btn=0x0020)
            post-pad Soft-GS Δprims≈1320 Δpx≈19.5M Δp2≈132 (from pad baseline @32M)
MENU? YES (midway-menu Soft-GS keep-alive)
INTERACTIVE? YES (assist-stable sel-idx under D-pad + Soft-GS growth after pad)
G-GFX-3? PARTIAL (Host→Local art-scale imgBytes; natural GIF IMAGE residual)
```

> **Note on gifPath2 vs p2qws:** batch-aware `Path2Transfers` may read **145** while **p2qws=5988** matches historical wave-7 gifP2≈5988 (same Path2 QW volume). Prefer **p2qws** for Path2 work comparisons.

**Historical:** wave-7 imgBytes=0 · PL-012 tip imgBytes=98304 · **PL-029 imgBytes=557056** (Host→Local SEC tiles from real gameart.ssf).

---

## Draw-graph charter (GX-008 / PL-005)

What the game actually submits at MENU keep-alive (Soft-GS ground truth):

```text
EE idle pump @0x1B6A68 / process wrapper @0x1B5D10
        │
        ├─ type 0x40 / 0x41  mode locks (flags gp-25032/25036)
        ├─ type 0x01 / 0x1101  VIF1 → GIF Path2 DIRECT (Midway sprites)
        │         └─ Path2: p2qws≈5988 · PRIM/XYZ2 packs · FRAME/XYOFFSET/TEST A+D
        │
        ├─ Path3 DMA: gifPath3=6 early, then STUCK (Path3MaskedByVif held)
        ├─ Path1 / VU1 XgKick: 0
        │
        └─ gameart.ssf stream @0x01800000 (2.8 MiB member)
                  ├─ PL-029: title Host→Local BITBLT of nested SEC type-2 tiles
                  │         → Soft-GS imgBytes=557056 (fed=458752 @~30M)
                  └─ residual: natural EE GIF IMAGE tags still image=1; TME sample path
```

| Path / class | At MENU | Evidence | Residual |
|--------------|---------|----------|----------|
| **Path1** | none | gifPath1=0 | post-menu 3D |
| **Path2** | **primary** | p2qws=5988, prims=1445, XYZ2=2886 | keep-alive force-process assist |
| **Path3** | early only | gifPath3=**6** | **do not invent**; mask held |
| **IMAGE (flg)** | early + Host→Local | natural image=1; **imgBytes=557056** | natural EE IMAGE residual (S8/S9) |
| **DISPFB** | unset | DISPFB1=0, dispfbPx=153405 | S10 / G-GFX-5 |
| **gameart.ssf** | **loaded + fed** | MWFILE + path-hash + Host→Local SEC tiles | EE SSF consumer / TME bind residual |
| **Pad / sel-idx** | **assist plant** | `*0x5DC000` 0..4 under D-pad | natural AnimMenu accept residual |

### gameart.ssf state machine (live claim)

1. MSL DADA warms archive registry.  
2. ~28.1M: MWFILE open `\ps2dvd\art\gameart.ssf` size=2836480 (pak-member force-dec).  
3. MSL-MFL path-hash plant: entries=8 loaded=2836480 data=`0x01800000`.  
4. MKFAM table-open kick `0x1B6A8C→0x267090` + publish stream=`0x0007E400`.  
5. ~30M: **PL-029** Host→Local walk nested `SEC ` type-2 slabs → imgBytes **557056**.  
6. ~32M: **PL-012 pad inject** starts (Start/Cross/D-pad + ForceRefreshPad).  
7. ~35M: PowerOff/WaitSema storm → keep-alive park `0x1B6A68`.  
8. Through 100M: Path2 continues via force-process; sel-idx plant tracks D-pad; **no** gifP3 climb; imgBytes holds art-scale.

---

## Walls

### 1. INTERACTIVE wall (P1 / PL-012 + MENU-DEC-2 + free-ride) — **YES**

- Main thrash class: idle-pump residual; post-plateau thrash `@0x1E69` / `@0x3153` / freelist `@0x3BAxxx` (rehomed ≥55M).  
- **MENU-DEC-2 proven @150M pad-inject** (`dec-menu-dec2-150m-pi`): Soft-GS **prims 45→146**; postPlatΔprims=101; natΔ=14; freelist end PC residual.  
- **Free-ride remeasure @100M pad** (`dec-pad-free-20260731-150927`, SEMA_OFF + `dec-menu-interactive.pad`):  
  - Soft-GS **lit=198959** NaturalDispfb · imgBytes=557056.  
  - **INTERACTIVE proven** natΔ=**24** accepts≥12 kicks≥8 postPlatΔp=21 · final **PC=`0x001237F0` main**.  
  - Sleep park `0x115F64` **cleared** (keep-alive sleepPark=0).  
  - Freelist end-PC residual **cleared** via TITLE_LOCAL freelist thrash rehome.  
- **Residual (hard):** prims peak 66 under free-ride rehome (vs 146 force-process peak); full natural accept without kick still open; gifP3=6 stuck; historical p2qws≈5988 not reproduced.

### 2. IMAGE wall (G-GFX-3 / GX-037) — PL-029 **PARTIAL** + S8/S9 handoff

- **Done (title path):** after publish, `TryFeedDecGameartHostToLocal` programs Soft-GS BITBLT Host→Local (TRXDIR=0) from real nested SEC type-2 payloads in RDRAM — **imgBytes 98304→557056** (tiles=48, fed=458752). Real MKDA.PAK bytes only; no synthetic chrome; no invent PATH3.  
- **Residual for GFX triad (S8/S9):** natural EE GIF `image=` tag count still **1**; Path2-only Midway menu never programs BITBLT itself under keep-alive. Local↔Local / richer PSM + TME-modulated prims still open.  
- **Path3MaskedByVif** remains frozen until natural unmask.

### 3. Held residuals (not this wave)

- UnknownOpcode on path string bytes `@0x612C30` (~39–41M) — path scratch as PC residual (known).  
- AdEL-data rescues during force-process.  
- DISPFB1=0 composite-only present class.

---

## Assists in play (DEC region only)

`MidwayFamilyAssist` when `IsDeception` (`SLUS_208.81`):

- Post-MSL Exit redirect plants; heap-tree cycle break (shared band).  
- Idle enqueue kick + sticky flag clear (25032/25036) + force process wrapper.  
- gameart table-open kick + path-hash + stream publish.  
- PowerOff/WaitSema storm break → midway-menu keep-alive.  
- **PL-012:** dense pad inject on idle-pump + assist-stable sel-idx plant (`0x5DC000`) + ForceRefreshPad.  
- **MENU-DEC-2:** accept latch + post-CROSS kick to main/`0x1B5D10`; thrash rehome `0x1E69/0x3153/0x2E3A`; natural sel scan; Soft-GS post-plateau Δ.  
- **PL-029:** Host→Local Soft-GS BITBLT of nested `gameart.ssf` SEC tiles (imgBytes art-scale).  
- **Not used:** invent PATH3; Path3MaskedByVif remove; DA wait-ready / MFL path plants; SM CRI/WAD; global WaitSema fabricate; idle queue control-word stomps.

---

## Reproduce

```powershell
cd C:\Users\user\.grok\worktrees\windows-detps2\detps2-seat-s2
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s2 --nologo
$env:DETPS2_TRACE_BIOS='1'
# diagnose 20M
dotnet exec out/seat-s2/DetPS2.Core.dll blocker-trace user-media-deception.json --cycles=20000000 --host-present
# claim 100M
dotnet exec out/seat-s2/DetPS2.Core.dll blocker-trace user-media-deception.json --cycles=100000000 --host-present
```

Or: `pwsh ./tools/run-title.ps1 -Media user-media-deception.json -Budget diagnose|claim -HostPresent -BuildOut out/seat-s2`

Look for stderr: `[MKFAM] Dec pad inject`, `Dec menu-sel-index=`, `Dec sel-idx-delta`.

---

## Claim line (copy for scoreboard / #12)

```
Dec MENU-DEC-2 @150M pad-inject SEMA_OFF host-present: MENU YES + INTERACTIVE + Soft-GS post-plateau
  PC@end=0x3BDEA0 px=3015151 prims=146 (>86) gifP3=6 dmac=25 sif=64496
  postPlatΔprims=101 postPlatΔpx≈1.54M accepts=518 kicks=48 natΔ=14 proven=1
  pad: *0x5DC000 sel 0..4 + accept latch *0x5DC014; kick →0x1237F0 / 0x1B5D10
  NATURAL sel-delta @0x5D6A8C/90 (free-ride PARTIAL)
  residual: freelist thrash final PC; full natural AnimMenu free-ride; p2 thin vs historical
  no invent PATH3; mask held; MidwayFamilyAssist Dec-only
```

### Historical seat claim (PL-029 @100M, force-process heavy)

```
Dec S2 PL-029 @100M SEMA_OFF host-present: MENU YES + INTERACTIVE + G-GFX-3 PARTIAL
  PC=0x1B6BF0 px=22126909 prims=1445 gifP2=145 p2qws=5988 gifP3=6 dmac=153
  imgBytes=557056 (was 98304) dispfbPx=153405 cdvd=4535 gameart.ssf=2836480@0x01800000
  PL-029 Host->Local tiles=48 fed=458752 exitReq=False
  pad: inject n≥1280 *0x5DC000 sel 0..4 under D-pad Δprims≈1320 after pad
  residual: natural EE GIF image=1 + AnimMenu accept — no invent PATH3; mask held
```
