# Blood Omen 2 (USA) — title port report

| Field | Value |
|-------|--------|
| **Id** | `blood-omen-2` |
| **Serial** | `SLUS_200.24` |
| **ISO** | operator `user-media-bloodomen2.json` (never commit paths) |
| **BIOS** | SCPH70008 / native BIOS HLE |
| **ROMDIR gate** | **CLOSED** |
| **Seat** | **S5 BO2** — worktree `detps2-seat-s5` |
| **Branch** | `agent/seat-s5/s0-g0` |
| **Owned** | `BloodOmen2SnAssist.cs`, this doc |
| **Forbidden** | invent pixels; fake warm CODE/MAINMENU sector credit |
| **Date** | 2026-07-31 |
| **Status** | **S0/PL-005:** MENU YES hold (title-surface Soft-GS); residual multi-prim IMAGE + INTERACTIVE pad charter |

---

## S0 baseline claim (PL-005 remeasure)

**Build:** `out/seat-s5` · **SEMA_STALL_YIELD:** OFF · **host-present** · tip base `20973c6` + this charter.

### diagnose 20M

| Metric | Value |
|--------|-------|
| PC | `0x00488898` (WaitSema) |
| px / prims | **286720 / 1** |
| gifP1 / gifP2 / gifP3 / dmac | 0 / 0 / 2 / 9 |
| cdvd / sifBytes / syscalls | 770 / 19048 / 712 |
| imgBytes / dispfbPx | **0 / 0** |
| XYOFFSET | `0x80008000` (ofx=ofy=0x8000) |
| Stream | warm PRECODE/CODE/MAINMENU only (no game Open yet); pack index **201** |
| RealSifRpc | binds=15 calls=63 unknownBindSids=0 |

### claim 100M

| Metric | Value |
|--------|-------|
| PC | `0x00488898` (WaitSema; freelist variance OK vs wave-7 `0x002BB968`) |
| px / prims | **286720 / 1** |
| gifP1 / gifP2 / gifP3 / dmac | 0 / **54** / 2 / 233 |
| cdvd / sifBytes / syscalls | **6112** / 84112 / 2806 |
| softgs | imgBytes=**0** dispfbPx=**0** fragTest=286720 |
| softgs-writes | total=115 PRIM=82 XYZ2=5 FRAME=1 XYOFF=1 |
| gif-pkts | completed=4 aborted=1 spannedCalls=53 p2qws=106 |
| XYOFFSET | `0x80008000` — ofx title-band **expand active** (full FB 640×448) |
| Game stream | **CODE** 914084 → `@0xB00000` + **MAINMENU** 1511408 → `@0xC00000` **streamedTotal=2425492** |
| Creating main layer | **YES** entry=`0x1B5AC0` ra=`0x1B57B8` @ ~22.3M |
| LIST.TXT / ENGLISH.DIR | **67957 / 254918** full reads |
| Dual list-stub | **YES** `@0x2C3E30` + `@0x2C3EE8` WAVE-7 |
| GAMEKEEPER / RUMBLEDATABASE | pack-member Open+read (honest cdvd; no fake sector plant) |
| RealSifRpc | binds=15 calls=253 unknownBindSids=0 fio2200=False |
| **MENU?** | **YES** (mainmenu title-surface Soft-GS) |

```
[BO2] force-game BG2 stream CODE+MAINMENU streamedTotal=2425492
[BO2] kick Creating main layer entry=0x1B5AC0 ra=0x1B57B8
[FILEIO] LIST.TXT total=67957; ENGLISH.DIR total=254918
[BO2] soft-stub dual list leaves @ 0x2C3E30 + 0x2C3EE8 WAVE-7
softgs: px=286720 prims=1 imgBytes=0 dispfbPx=0 XYOFFSET=0x80008000
MENU? YES (mainmenu title-surface Soft-GS) — ofx expand carries FB; multi-prim IMAGE residual
```

**Claim line (S0):**  
`BO2 SLUS_200.24 S5 PL-005 | MENU YES title-surface | px=286720 prims=1 gifP2=54 gifP3=2 cdvd=6112 imgBytes=0 ofx=0x8000 expand | stream CODE+MAINMENU 2425492 | residual multi-prim IMAGE + INTERACTIVE`

---

## How far (wave-7 → S0 hold)

| Milestone | Result |
|-----------|--------|
| Disc / ELF | **OK** — `SLUS_200.24` |
| SN + IOPRP234 + MOD_LOAD | **OK** |
| FILEIO SN path (not FILEIO-2200) | **OK** |
| GOE_FSRV IOPFILE sids | **OK** — bind 0x20/0x21/0x29/0x30, unknownBindSids=0 |
| PS2.RKV mount + TOC | **OK** — **5592** keys |
| PRECODE/CODE/MAINMENU .BG2 | **Real disc** (warm no sector; **game Open+stream** WAVE-4/7) |
| Thrash @0x538738 | **CLEARED** — method-walker @0x166390 stubbed |
| FILEIO `KAIN.IMP` | **YES** — pack-member extract |
| Pack index | **201** members |
| Game Open+stream CODE.BG2 | **YES** — 914084 B → EE `@0xB00000` |
| Game Open+stream MAINMENU.BG2 | **YES** — 1511408 B → EE `@0xC00000` (streamedTotal=2425492) |
| Creating main layer | **YES** — entry `@0x1B5AC0`, `$ra`=post-Finished `@0x1B57B8` |
| FILEIO `LIST.TXT` | **YES** — full read **67957** |
| FILEIO `ENGLISH.DIR` | **YES** — full read **254918** |
| Circular list-walk `@0x2C3E30` + search `@0x2C3EE8` | **DUAL SOFT-STUB** WAVE-7 |
| GAMEKEEPER.ETP / RUMBLEDATABASE.ETP | **YES** — pack-member Open+read after list stubs |
| Main menu (`mainmenu-bg2`) | **YES** — Soft-GS title-surface (ofx=0x8000 full FB; stream live) |

### ofx expand usage (Soft-GS truth — temporary; G-GFX-6 demotes)

| Fact | Detail |
|------|--------|
| Retail XYOFFSET | `0x80008000` (ofx=ofy=0x8000) armed early |
| Expand class | `Gs.DrawSprite` **titleStrip**: retail ofx band + w≥FB/2 + h&lt;FB/2 → expand to **640×448** |
| px attribution | **286720 = expanded strip**, not multi-prim scene; color/UV from real Path2 prim |
| prims | **1** — single expanded title band |
| imgBytes / DISPFB1 | **0 / 0** — no GIF IMAGE BITBLT; no natural display FB present |
| Policy | Expand is **MENU-class chrome only**; never invent pixels / FFmpeg overlays |
| Telemetry debt | `expandHits` counter is **S9/PL-003** — claim here uses XYOFFSET + prims=1 as proxy |

### Wall analysis (wave-7 hold)

1. **Stream path solid:** force-game CODE+MAINMENU Open+stream + Creating + LIST/ENGLISH + ETP.
2. **Dual list-stub** frees circular walks; residual freelist / WaitSema heat remains.
3. **ofx=0x8000 title band expand** paints full Soft-GS FB from one strip (Whiplash-class MENU YES).
4. **Display-spine residual** climbs gifP2 via post-Finished kicks (variance: ~50–100+ @100M).
5. **Soft-GS residual:** prims=1; imgBytes=0; DISPFB1=0 — **multi-prim IMAGE** still open.
6. **Rejected forever:** WaitSema global fabricate; fake warm CODE sectors; invent pixels.

### Assists (current — demote over seasons)

- Goefile member extract; format leaf; method-walker / SN printf / entity glue stubs
- Force usebigfile + ForceBo2GameBg2Stream + Creating main layer kick
- Post-ENGLISH dual list-stub + display-spine + freelist leave
- Soft-GS ofx title-band expand (**shared Gs.cs** — S9 owns demotion)
- **No** fake CODE/MAINMENU sector credit without open

## MENU / #8

**MENU YES** (mainmenu title-surface Soft-GS): px=286720 full Soft-GS FB after CODE+MAINMENU
stream + LIST/ENGLISH + GAMEKEEPER path. Soft-GS ofx=0x8000 class (Whiplash title-surface).
Multi-prim IMAGE / DISPFB / pad-interactive residual remains open (issue #8 family + PL-015/027).

---

## Draw-graph charter (PL-005) — INTERACTIVE + IMAGE multi-prim

Maps to gates **P1 INTERACTIVE** (PL-015) and **G-GFX-3/4 / P2 FRONTEND** (PL-027).  
Hand-off partners: **S8 GIF path**, **S9 Soft-GS raster/TEX**, **S10 DISPFB/present**.

### Graph (current → next)

```
Disc PS2.RKV / GOE
  └─ PRECODE.BG2 (warm) → pack index
  └─ CODE.BG2     Open+stream → EE @0xB00000   [LIVE S0]
  └─ MAINMENU.BG2 Open+stream → EE @0xC00000   [LIVE S0]
        │
        ├─ Creating main layer @0x1B5AC0 → LIST.TXT + ENGLISH.DIR  [LIVE]
        ├─ Dual list-stub → GAMEKEEPER.ETP / RUMBLEDATABASE.ETP   [LIVE]
        │
        ├─ Path2 SPRITE (ofx=0x8000 thin strip)
        │     └─ Soft-GS titleStrip EXPAND → px=286720 prims=1   [LIVE MENU YES]
        │           residual: expandHits demote (G-GFX-6 / S9)
        │
        ├─ [MISSING] GIF IMAGE / BITBLT tex upload (imgBytes=0)
        │     └─ TEX0 sample multi-prim chrome (G-GFX-3/4)
        │
        ├─ [MISSING] DISPFB1 natural present (dispfbPx=0)          [S10]
        │
        └─ [MISSING] Pad selection / Press-START → menu state     [PL-015]
              └─ prims↑ / gifP2↑ / stream opens beyond ETP
```

### S1 — INTERACTIVE (PL-015 exit)

| Item | Spec |
|------|------|
| Goal | Pad changes menu state **or** increases prims/gif after pad @100M (P1) |
| Baseline assist | `BloodOmen2SnAssist` already pulses START/CROSS after cdvd≥100 |
| Evidence needed | Selection index / press-start advance **or** prims&gt;1 / new gifPath after inject |
| Not enough | Title FB alone with pad thrash and prims=1 |
| Forbidden | Global WaitSema fabricate; invent menu UI pixels |
| Exit test | `pad-inject` claim @100M: MENU hold + T2 signal (state or Soft-GS delta) |

### S2 — IMAGE multi-prim (PL-027 + G-GFX-3/4)

| Item | Spec |
|------|------|
| Goal | **imgBytes&gt;0** and **prims≥2** (or textured multi-prim) on mainmenu path |
| Source | MAINMENU.BG2 / CODE pack members → real GS IMAGE/BITBLT (not expand strip) |
| Soft-GS | PSMCT32/16 + PSMT8+CLUT when TEX0.TBP set; **no procedural tex plant** |
| Shared | S8 Path2 DIRECT fidelity; S9 TEX sample; S10 DISPFB when FRAME set |
| Forbidden | Invent pixels; plant PATH3 chrome; fake DISPFB composite-only as FRONTEND |
| Exit test | claim @100M: imgBytes&gt;0 **and** MENU hold; expandHits documented (not sole px source preferred) |

### Handoffs / freezes

| Freeze | Rule |
|--------|------|
| Soft-GS truth | px/prims/imgBytes/gifP* are ground truth |
| SEMA_OFF | `DETPS2_SEMA_STALL_YIELD` stays **OFF** |
| No fake sector credit | Warm BG2 must never count as game stream cdvd |
| No invent pixels | Expand reuses prim color/UV only |
| Gs/Gif ownership | Title seat does not edit Gs/Gif without S8/S9 merge train |

### Next WP queue (after S0 merge)

1. **PL-015** — pad past title FB (INTERACTIVE)  
2. **PL-027** — multi-prim IMAGE (with S8/S9/S10)  
3. Demote list soft-stubs when natural walk exits freelist  
4. G-GFX-6 expand demotion when retail multi-prim fills FB without titleStrip

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s5
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/seat-s5/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=20000000 --host-present
dotnet exec out/seat-s5/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: stream CODE+MAINMENU; Creating; LIST+ENGLISH; dual list-stub; GAMEKEEPER;
#         Soft-GS px=286720 prims=1 imgBytes=0 ofx=0x8000; MENU? YES (title-surface)
```
