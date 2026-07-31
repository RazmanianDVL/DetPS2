# Mortal Kombat: Deception (USA) — title port + draw-graph charter

| Field | Value |
|-------|--------|
| **Title** | Mortal Kombat - Deception (USA) |
| **user-media id** | `mk-deception` |
| **Serial / BOOT2** | `SLUS_208.81` |
| **ISO** | `C:/Users/xxraz/Downloads/MortalKombatDeception(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-deception.json` |
| **Assist** | `MidwayFamilyAssist` **REGION DEC** (`IsDeception`) |
| **Seat** | **S2 MIDWAY-DEC** |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-seat-s2` |
| **Branch** | `agent/seat-s2/s1-g1` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **MENU YES** hold · **P1 INTERACTIVE** (assist-stable sel-idx under D-pad) — PL-012 |
| **Last updated** | 2026-07-31 |

---

## S0 charter (PL-005) + S1 pad (PL-012)

**Gates this seat feeds:**

| Gate | Name | Dec status |
|------|------|-------------|
| **P0** | MENU floor | **HOLD** — midway-menu Soft-GS |
| **P1** | INTERACTIVE | **HOLD (assist-stable)** — pad inject moves sel-idx `*0x5DC000` 0..4 under D-pad; Soft-GS Δ after pad; natural AnimMenu accept residual |
| **G-GFX-3** | IMAGE path | **WALL** — `gameart.ssf` body live (2.8 MiB), Soft-GS `imgBytes` plateau **98304** (not art-scale) |

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
| gifPath3 growth / natural IMAGE chrome | **No** — gifP3=**6** stuck; imgBytes plateaus |
| **Pad INTERACTIVE (P1 / PL-012)** | **HOLD** — assist-stable sel-idx + pad inject @ idle-pump |
| Full gameart TEX sample (G-GFX-3) | **Open** — GX-037 / PL-029 consume |

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

### Claim 100M — PL-012 pad inject (SEMA_OFF host-present)

```
@100M: PC=0x001B6BF0 px=22006272 prims=1444 gifPath1=0 gifPath2=145 gifPath3=6 dmac=153
       sifBytes=3567920 syscalls=25390 cdvdSectors=4535 spu2Samples=31928
       softgs: imgBytes=98304 dispfbPx=32768 expandHits=0 fragTest=21973504 rejScissor=640
       softgs-regs: FRAME_1=0xA005B DISPFB1=0 SCISSOR=0x7FFF00007FFF0000
                    XYOFFSET=0x72006C00 TEST=0x3101A
       softgs-writes: total=5177 PRIM=75 XYZ2=2886 FRAME=148 SCISSOR=76 TEST=78 XYOFF=148
       gif-pkts: completed=151 aborted=0 tags=151 p2qws=5988
       RealSifRpc: binds=17 calls=2937
       gameart.ssf: open OK size=2836480 loaded=2836480 data=0x01800000 hdr=0x0061E5A0
       keep-alive: idle-pump 0x1B6A68 / PC@end 0x1B6BF0 exitRequested=False
       pad: inject n≥1280 sel plants=512 *0x5DC000 tracks 0..4 under D-pad
            sel-idx-delta logs (e.g. 0x5DC000:3->4 dpad=1 btn=0x0020)
            post-pad Soft-GS Δprims≈1320 Δpx≈19.5M Δp2≈132 (from pad baseline @32M)
MENU? YES (midway-menu Soft-GS keep-alive)
INTERACTIVE? YES (assist-stable sel-idx under D-pad + Soft-GS growth after pad)
```

> **Note on gifPath2 vs p2qws:** batch-aware `Path2Transfers` may read **145** while **p2qws=5988** matches historical wave-7 gifP2≈5988 (same Path2 QW volume). Prefer **p2qws** for Path2 work comparisons.

**Historical wave-7 baseline (agent/menu-dec-w7):** px≈822k gifP2≈5988 gifP3=6 imgBytes=0 — same gifP3 plateau and Path2 QW class; tip Soft-GS now reports more prims/px and a small early IMAGE footprint (`imgBytes=98304`).

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
                  └─ residual: no art-scale GIF IMAGE / TEX sample into Soft-GS
                     (imgBytes plateaus 98304; DISPFB1=0)
```

| Path / class | At MENU | Evidence | Residual |
|--------------|---------|----------|----------|
| **Path1** | none | gifPath1=0 | post-menu 3D |
| **Path2** | **primary** | p2qws=5988, prims=1444, XYZ2=2886 | keep-alive force-process assist |
| **Path3** | early only | gifPath3=**6** | **do not invent**; mask held |
| **IMAGE (flg)** | early footprint | imgBytes=**98304** both 20M+100M | **G-GFX-3 wall** — not gameart-scale |
| **DISPFB** | unset | DISPFB1=0, dispfbPx=32768 | S10 / G-GFX-5 |
| **gameart.ssf** | **loaded** | MWFILE + path-hash + publish | consumer TEX bind residual |
| **Pad / sel-idx** | **assist plant** | `*0x5DC000` 0..4 under D-pad | natural AnimMenu accept residual |

### gameart.ssf state machine (live claim)

1. MSL DADA warms archive registry.  
2. ~28.1M: MWFILE open `\ps2dvd\art\gameart.ssf` size=2836480 (pak-member force-dec).  
3. MSL-MFL path-hash plant: entries=8 loaded=2836480 data=`0x01800000`.  
4. MKFAM table-open kick `0x1B6A8C→0x267090` + publish stream=`0x0007E400`.  
5. ~32M: **PL-012 pad inject** starts (Start/Cross/D-pad + ForceRefreshPad).  
6. ~35M: PowerOff/WaitSema storm → keep-alive park `0x1B6A68`.  
7. Through 100M: Path2 continues via force-process; sel-idx plant tracks D-pad; **no** gifP3 climb; **imgBytes** unchanged from 20M.

---

## Walls

### 1. INTERACTIVE wall (P1 / PL-012) — **HOLD assist-stable**

- Main thrash class: **idle-pump** `@0x1B6A68` + keep-alive force-process (not a natural selection GUI loop).  
- **Proven @100M SEMA_OFF:**  
  - Dense pad inject (`MaybeInjectDecMenuPad`) n≥1280 with release edges + `ForceRefreshPad`.  
  - Assist-stable selection index plant at `0x5DC000..0x5DC010` (0..4) driven by D-pad edges — `sel-idx-delta` logs under `dpad=1`.  
  - Soft-GS growth after pad baseline (Δprims≈1320 Δpx≈19.5M Δp2≈132).  
- **Residual:** natural game menu accept / AnimMenuGUI row cell (not assist plant); idle thrash still dominates PC.  
- Same honesty class as SM wave-7 assist-stable sel-idx (not free-ride natural UI).

### 2. IMAGE wall (G-GFX-3 / GX-037) — S8/S9 + S2 consume (PL-029)

- Soft-GS residual: **gameart GIF IMAGE textures**.  
- Body is in RDRAM; Soft-GS does **not** show art-scale `imgBytes` growth.  
- Report to GFX triad: Path2-only Midway menu; need Host↔Local / Local↔Local BITBLT fidelity for SSF tex upload, **no** assist PATH3 plant.  
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
- **Not used:** invent PATH3; DA wait-ready / MFL path plants; SM CRI/WAD; global WaitSema fabricate; idle queue control-word stomps.

---

## Reproduce

```powershell
cd C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-seat-s2
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
Dec S2 PL-012 @100M SEMA_OFF host-present: MENU YES + INTERACTIVE (assist-stable sel-idx)
  PC=0x1B6BF0 px=22006272 prims=1444 gifP2=145 p2qws=5988 gifP3=6 dmac=153
  imgBytes=98304 dispfbPx=32768 cdvd=4535 gameart.ssf=2836480@0x01800000 exitReq=False
  pad: inject n≥1280 *0x5DC000 sel 0..4 under D-pad (sel-idx-delta) Δprims≈1320 after pad
  residual: natural AnimMenu accept + IMAGE wall (G-GFX-3 gameart TEX) — no invent PATH3
```
