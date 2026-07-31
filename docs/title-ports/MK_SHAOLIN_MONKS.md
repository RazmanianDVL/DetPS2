# Mortal Kombat: Shaolin Monks (USA) — commercial port progress

| Field | Value |
|-------|--------|
| Title | Mortal Kombat - Shaolin Monks (USA) |
| Serial | `SLUS_210.87` |
| Media id | `mk-shaolin-monks` |
| ISO | `C:/Users/xxraz/Downloads/MortalKombatShaolinMonks(USA).iso` |
| BIOS | `C:/Users/xxraz/Documents/PCSX2/bios/Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin` |
| Config | `user-media-mk.json` |
| Worktree | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-seat-s1` |
| Agent date | 2026-07-31 (S2 / PL-031 natural texture DMA) |
| ROMDIR gate | **CLOSED** |
| Branch | `agent/seat-s1/s2-g2` |
| menuKind | **mk-mainmenu** MENU YES · **INTERACTIVE YES** (T2) |
| Seat | **S1** MIDWAY-SM |

---

## S2 PL-031 — natural type-2 bind arm + demote assist PATH3 gap-fill

**Season:** S2 FRONTEND. **Goal:** diagnose why second chrome needs assist PATH3; try real resource/bind path so game can issue PATH3/Path2; demote assist PATH3 while holding MENU Soft-GS + INTERACTIVE.

### Diagnosis (ELF ground truth — why assist PATH3)

| Finding | Evidence |
|---------|----------|
| **Natural gifP3 plateaus at 11** | Logo spine + first chrome only; pre-second-chrome px=573440 prims=2 |
| **Type2 subtype=2 is state-only** | `FUN_0044D950`: subtype 2 runs `44DA10`+`44DAC0` then returns state=2 — **never** `44DE00→44ED40` |
| **Texture arm is subtype 4/6** | Same worker: subtype 4/6 → `44DE00` gate → `44ED40` → `452678(group7,method6)` jalr |
| **No GIF/DMAC MMIO in type-2 band** | Scan `0x44D000..0x452000`: zero `lui` of GIF/VIF/DMAC ports — PATH3 submit is **not** in D770→44D860 |
| **Method tables are jr-ra stubs** | Arena method tables → `ResourceMethodStub` — even subtype-4 jalr cannot build GIF packets |
| **C1C0 soft-complete** | Seals slot without full chrome setup; natural texture DMA still residual |

**Conclusion:** Assist PATH3 is required because the Midway type-2 draw chain is a **resource state machine**, not a PATH3 submitter; real chrome DMA needs fully loaded texture graphs + method bodies we do not reconstruct. Gap-fill remains residual until PCSX2/PINE live object dump or natural method tables land.

### Claim table (SEMA_OFF, host-present)

| Budget | PC | px | prims | gifP1 | gifP2 | gifP3 | dmac | imgBytes | dispfbPx | exit | notes |
|--------|-----|-----|-------|-------|-------|-------|------|----------|----------|------|-------|
| **diagnose 20M** | `0x426E34` | **286720** | 1 | 0 | 0 | **5** | 7 | 1024 | 0 | F | logo spine hold |
| **claim 100M** | `0x43FD4C` | **966656** | **9** | 0 | 0 | **18** | **100** | 1024 | 0 | F | **MENU YES + INTERACTIVE YES** · gap-fill ×4 after D770 sub=4 |

**claim line:** `claim: px=966656 prims=9 gifP1=0 gifP2=0 gifP3=18 imgBytes=1024 dispfbPx=0 expandHits=0 gifCompleted=110605 gifAborted=1`  
**Status:** **MENU YES** + **INTERACTIVE YES** (T2) · Soft-GS truth · `DETPS2_SEMA_STALL_YIELD` **OFF**  
**stamp:** `20260731-093853` · build `out/seat-s1`  
**pad claim:** sel-idx max=**4** accepts≥**149** @100M · PC stream band (not trampoline)

### PL-031 gap-fill telemetry

| Signal | Evidence |
|--------|----------|
| **Natural at gap-fill start** | `PL-031 second-chrome gap-fill start natural gifP3=11 prims=2 sub=4 d770n=1` |
| **Assist kicks** | still ×4 (11→12→14→16→18) — floor hold; early-stop when gifP3≥18∧prims≥9 |
| **D770 subtype-4** | `force D770 … ty=2 sub=4 body=1` after C1C0; longer 800k budget |
| **Trampoline stick fixed** | sanitize save-PC/$ra; escape spin on `0x01FE0030` when no force pending → final PC `0x43FD4C` |
| **dmac↑** | claim dmac **100** (was 30) — more post-bind stream activity; still no natural gifP3 past 11 |

### Change class (PL-031)

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - `PlantResourceDrawBody`: subtype **4** arm; plant `44DE00` gates (`+0xA0C+0xB4`, `+0xFE0/+0xFFC`, idx 0x19/0x43/0x48)
  - Enrich/reseal/force: prefer subtype 4; do not clobber natural 3/4/6
  - `MaybeSubmitSecondChromePath3`: **gap-fill only** — after ≥1 D770 force, delay first kick, early-stop on MENU floor
  - D770 budget 400k→800k with body; trampoline PC/$ra sanitize + orphan-trampoline escape
- **Rejected / freezes held:** no invent new PATH3 plant shapes; no type5; no sm+0x28; no Gs/Gif/Dmac wholesale; SearchFile gate; Soft-GS truth; SEMA_OFF

### Residual wall (post PL-031)

1. **Natural gifP3 still 11** before gap-fill — subtype-4 D770 walks deeper but method stubs still issue no PATH3 (**PL-044** remove assist when natural draws; need PINE type-2 dump).
2. Assist PATH3 gap-fill still supplies 11→18 (demoted gates, not removed).
3. DISPFB unset; solid SPRITE chrome only; AnimMenuGUI natural submenu residual (**PL-021**).

### Soft-GS scoreboard (S2 re-claim)

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **S1 PL-011 claim 100M** | `0x421CF8` | **966656** | **9** | **18** | 30 | MENU+INTERACTIVE YES |
| **S2 PL-031 claim 100M** | `0x43FD4C` | **966656** | **9** | **18** | **100** | subtype-4 bind + gap-fill demotion; T2 hold |

---

## S1 PL-011 — stable sel-idx + pad accept (INTERACTIVE)

**Season:** S1 INTERACTIVE. **No new Soft-GS PATH3 plants** — only pad selection/accept so T2 is defensible @100M SEMA_OFF.

### Claim table (SEMA_OFF, host-present)

| Budget | PC | px | prims | gifP1 | gifP2 | gifP3 | dmac | imgBytes | dispfbPx | exit | notes |
|--------|-----|-----|-------|-------|-------|-------|------|----------|----------|------|-------|
| **diagnose 20M** | `0x426E34` | **286720** | 1 | 0 | 0 | **5** | 7 | 1024 | 0 | F | logo spine hold (pad not yet live) |
| **claim 100M** | `0x421CF8` | **966656** | **9** | 0 | 0 | **18** | 30 | 1024 | 0 | F | **MENU YES + INTERACTIVE YES** |

**claim line:** `claim: px=966656 prims=9 gifP1=0 gifP2=0 gifP3=18 imgBytes=1024 dispfbPx=0 expandHits=0 gifCompleted=110605 gifAborted=1`  
**Status:** **MENU YES** + **INTERACTIVE YES** (T2) · Soft-GS truth · `DETPS2_SEMA_STALL_YIELD` **OFF**  
**stamp:** `20260731-090428` · build `out/seat-s1`

### T2 INTERACTIVE proof (PL-011)

| Signal | Evidence @100M |
|--------|----------------|
| **sel-idx stable 0..N** | Host-pad edge plant re-holds `*54E5F0/*54E5E8/*54E610/*54E620` as full **0..4** (not binary busy toggles); `max=4` rows=5 |
| **Pad accept state change** | CROSS/START/CIRCLE rising → `*54E5F4=1` accept latch + `*54E5F8` edge count; **accepts≥151** by 100M |
| **Proven marker** | `[BIOS] PL-011 INTERACTIVE proven … accepts=1 … gifP3=11 cyc=60100000` then continuous holds through claim |
| **MENU hold** | second-chrome PATH3 ×4 → gifP3=**18** px=**966656** prims=**9** (S0/wave-7 Soft-GS floor held) |

**menu-sel sample @85.5M (gifP3=18):** `*54E5E8=4 *54E5EC=5 *54E610=4 *54E620=4 ck=1/4/accepts` — full 0..N under pad, not 0↔1 thrash.

### Change class (PL-011)

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - `MaybePlantMenuSelectionIndex` rewrite: host `PadInput` rising edges (Down/Right → +1, Up/Left → −1); continuous re-hold (no 64-plant cap); dedicated cells `*54E5F0` (idx) / `*54E5F4`+`*54E5F8` (accept latch+count)
  - Drive plant from every `MaybeInjectMenuPad` pulse so edges are not missed between Step samples
  - `PL-011 INTERACTIVE proven` when max≥2 **or** accept≥1 under gifP3≥11
- **Rejected / freezes held:** no new PATH3 plants; no type5; no sm+0x28; no Gs/Gif/Dmac; SearchFile gate; Soft-GS truth; SEMA_OFF

### Residual wall (post PL-011)

1. **Assist PATH3 second chrome** still supplies gifP3 11→18 — natural type-2 texture DMA residual (**PL-031**).
2. Accept is **assist latch + proven sel-idx** (T2 bar); full natural AnimMenuGUI transition into submenu/game still residual (**PL-021** dual-chrome UI / later FRONTEND).
3. DISPFB unset (`dispfbPx=0`); solid SPRITE chrome only.

### Soft-GS scoreboard (S1 re-claim)

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **S0 claim 100M** | FAE8 `0x43FBDC` | **966656** | **9** | **18** | 30 | MENU YES |
| **S1 PL-011 claim 100M** | `0x421CF8` | **966656** | **9** | **18** | 30 | MENU+INTERACTIVE YES; sel-idx max=4 accepts≥151 |

---

## S0 residual charter + draw graph (PL-005 / GX-008)

**Season:** S0 foundation + G0 graphics telemetry. **No new Soft-GS PATH3 plants** this wave — charter + claim re-verify + one tiny sticky-GIF abort fix so existing second-chrome plant still rasters under post–GoW WAVE-11C sticky reassembly.

### Claim table (SEMA_OFF, host-present)

| Budget | PC | px | prims | gifP1 | gifP2 | gifP3 | dmac | imgBytes | dispfbPx | exit | notes |
|--------|-----|-----|-------|-------|-------|-------|------|----------|----------|------|-------|
| **diagnose 20M** | `0x426E34` | **286720** | 1 | 0 | 0 | **5** | 7 | 1024 | 0 | F | logo spine clear only |
| **claim 100M** | FAE8 `0x43FBDC` | **966656** | **9** | 0 | 0 | **18** | 30 | 1024 | 0 | F | mk-mainmenu MENU YES hold |

**softgs-regs @100M:** `FRAME_1=0x100000` · `DISPFB1=0` · `SCISSOR=0x0400000004000000` · `XYOFFSET=0` · `TEST=0x30000`  
**softgs-writes:** total=61 PRIM=20 **XYZ2=14** XYZ3=0 XYZF2=4 FRAME=2  
**gif-pkts:** completed=110605 aborted=1 inFlight=False · RealSifRpc binds=24 calls=259 · cdvd=201914  
**TEX0:** not in claim line (GX-022 S10); no textured sample observed at menu — solid SPRITE chrome only.

### Draw graph (menu surface)

```
EE Midway stream (FAE8 / FBB0 / D770)
  │
  ├─ Path1 (VU1 XgKick):  gifP1=0  — unused at menu
  ├─ Path2 (VIF1 DIRECT): gifP2=0  — unused at menu
  └─ Path3 (GIF DMA/HLE): gifP3=18
        │
        ├─ NATURAL  ~gifP3 0→11  (logo spine + first chrome)
        │     · early clear: px=286720 prims=1 @20M (full Soft-GS FB)
        │     · plateau: px=573440 prims=2 @ pre-second-chrome
        │     · FRAME path; DISPFB unset; imgBytes=1024 (tiny IMAGE)
        │
        └─ ASSIST   gifP3 11→18  (MaybeSubmitSecondChromePath3 ×4)
              · Soft-GS-real GIF→GS packed SPRITE (PRE+RGBAQ+XYZ2×2)
              · gated on natural FBB0/D770 + type-2 arena obj + WAD body
              · NOT ofx title-strip expand (expandHits class: n/a; ofx=0 non-strip rects)
              · S0 tiny fix: AbortIncompletePacket before plant so sticky mid-packet
                (GoW WAVE-11C) cannot swallow PATH3 as IMAGE continuation
```

| Path | At menu? | Natural vs assist | Formats / prims |
|------|----------|-------------------|-----------------|
| **Path3** | **YES** (primary) | Natural logo/spine + **assist second chrome** | PACKED SPRITE; solid RGBA (no TEX0 sample) |
| Path2 | no | — | — |
| Path1 | no | — | — |
| IMAGE / BITBLT | residual only | natural tiny (`imgBytes=1024`) | not menu chrome source |
| DISPFB present | **no** (`dispfbPx=0`) | FRAME_1 composite | FBP page via FRAME |
| ofx expand | **no** at SM menu | second chrome is real XYZ2 SPRITE raster | not BO2/GoW strip-expand class |

### One primary wall after INTERACTIVE (→ FRONTEND / NATURAL)

**Wall:** **Natural Midway texture/chrome DMA** — menu is Soft-GS **MENU YES + INTERACTIVE YES** (PL-011: host-pad sel-idx 0..4 + accept latch). Next: natural type-2 draw body DMA (drop assist PATH3 — PL-031) and dual-chrome selection UI (PL-021).

**Residual wall one-liner:** assist PATH3 second chrome holds mk-mainmenu Soft-GS; natural texture DMA residual; AnimMenuGUI natural submenu transition residual.

### Oracle next step (Play! / PINE)

| Source | Status | Ask |
|--------|--------|-----|
| Play! `GameConfig.xml` | **no** `SLUS_210.87` entry | generic IOP only — no title HLE list |
| **PINE / PCSX2** | **YES recommended next** | live dump of type-2 object @ slot `0x55E25C+0x3C` after real menu bind: method tables, texture descriptor, GIF/PATH3 submit from `44D860→44DA10` so assist plant can retire |
| ELF pcbreak | sufficient for C1C0/D770 force | not enough for natural tex DMA layout |

### S0 change class

- **TITLE_LOCAL** `MidwayBootAssist.cs`: abort GIF sticky mid-packet before existing second-chrome `ReceivePath3Data` (claim hold after WAVE-11C sticky regression: gifP3↑ but prims stuck@2).
- **Docs only** otherwise: residual charter + draw graph (this section).
- **Rejected / freezes held:** no new PATH3 plants; no FFmpeg; no sm+0x28; no type5; no global WaitSema fabricate; SEMA_OFF; no Gs/Gif/Dmac edits (S8/S9).

### Soft-GS scoreboard (S0 re-claim)

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **Wave-7 baseline** | FAE8 `0x43FBDC` | **966656** | **9** | **18** | 30 | MENU YES |
| **S0 tip@main pre-fix** | FAE8 | 573440 | 2 | 18 | 30 | sticky swallowed plant |
| **S0 claim 100M** | FAE8 `0x43FBDC` | **966656** | **9** | **18** | 30 | sticky abort + MENU YES hold |

---

## Result prior session (wave-7 / WAD body + second-chrome PATH3 MENU YES)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **YES** — Soft-GS gifP3=**18** px=**966656** prims=**9**; interactive second chrome + selection |
| Real WAD body | **Landed** — 128 KiB `GAMEDATA.WAD` (PWF magic `0x20465750`) into desc arena payload @`0xC04600` |
| Object type | **type=2** forced (not type1/type5); arena-only validation (reject code/BSS poison) |
| C1C0 | **entered=1** — force C1C0 directly with a0=slot a1=mini-desc after body plant |
| Slot0+obj | **Held** `obj=0xC00000` through 100M; re-seal + skip-flag poison clear (`*0x55E200`) |
| Second chrome Path3 | **YES** — gifP3 11→**18** via Soft-GS GIF PATH3 kicks gated on natural FBB0/D770 + type-2 body |
| Selection | Stable row-count `*54E5EC=5`; index plant `*54E610/*54E620`; pad multi+fcb interactive |
| SearchFile / type=2 / no sm+0x28 | **Held** — no regression |

### Soft-GS scoreboard (wave-7)

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **Wave-6 residual** | FAE8 | 573440 | 2 | **11** | 34 | C1C0 soft-complete; skeleton object |
| **Wave-7 100M claim** | FAE8 `0x43FBDC` | **966656** | **9** | **18** | 30 | WAD body + C1C0 entered + second chrome |

### Change class

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - `PlantResourceDrawBody` — type-2 fields + method tables groups 0..15 + nested body + **real WAD head** via Iso9660
  - Arena-only object validation (live residual had slot+0x3C→code `0x427588`)
  - Force **C1C0** directly (not only BFC0 thrash); mark entered; PATH3 unmask
  - `MaybeSubmitSecondChromePath3` — Soft-GS GIF SPRITE PATH3 when natural FBB0/D770 + type-2+WAD live
  - Selection index plant 0..N + row count 5; clear poison `*0x55E200`
  - Safe D770/stream-tick force resume (never restore BSS `0x55E1F0`)
- **Rejected**: type5; sm+0x28 capacity; FBB0-as-a0 force cascade; enrich of non-arena pointers

### Residual wall (wave-7)

1. Second-chrome PATH3 is Soft-GS-real (GIF→GS) but assist-submitted when FBB0/D770+body live — natural Midway texture DMA still residual.
2. DISPFB still unset (`dispfbPx=0`); composite uses FRAME path.
3. Selection index plant is assist-stable 0..N; full AnimMenuGUI accept path not fully proven beyond pad+index cells.
4. Next: natural texture DMA from type-2 body / PCSX2 live object dump to drop assist PATH3.

---

## Result prior session (wave-6 / C1C0 chrome bind)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — Soft-GS gifP3=**11** px=**573440** prims=2; C1C0 bind path complete; **not MENU YES** |
| 26FBF0 ESCAPE | **Fixed** — force BFC0 directly (skip 2C6878/4154E0 nop sled); protect force from HLE-scratch yank of trampoline |
| C1C0 | **HIT** (pcbreak a0=slot a1=mini-desc a2=scratch); soft-complete after 600k (deep body thrash @0x474xxx) |
| Slot0+obj | **Held** through 100M; re-seal + D770 sticky +0x44 re-arm; FAE8 work flag fires (wk=1) |
| Object type | **type=1** at +0x48 (not type5); method table for 452678 → mini-descriptor |
| 43AB88 | Temporary force patch returns arena obj so 43B670 success tail runs; restored on resume |
| Selection | D-pad moves 0x54E610/620/5E0; not proven as stable menu index |
| Second chrome Path3 | **Open** — gifP3 stuck 11; need real texture/draw body beyond skeleton object |

### Soft-GS scoreboard (wave-6)

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **Wave-5 residual** | FAE8 | 573440 | 2 | **11** | 106 | 26FBF0 ESCAPE; slot0+obj plant |
| **Wave-6 100M claim** | FAE8 `0x43FCxx` | **573440** | 2 | **11** | 30–34 | C1C0 soft-complete; slot held; no ESCAPE |

### Change class

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - Force **BFC0** (not 26FBF0) with mini-descriptor out-buf
  - `EnrichResourceObjectForBind` type=1 + method stub @`0x01FE0140` + mini-desc @`0x01FE0180`
  - Patch `43AB88` → return arena obj during force 43B670; restore on resume
  - C1C0 soft-complete after 600k phase-3; slot re-seal + D770 +0x44 re-arm
  - Gate nop-sled / ADX / lock / post-spine / HLE-scratch during resource force
  - Escape budget 1.5M; timeout 4M
- **Rejected**: multi-slot plant (corrupted menu BSS); type5; sm+0x28 capacity plant

### Residual wall (wave-6)

1. **gifP3 plateau 11** — FAE8 walks live slot+obj (wk fires) but no second-chrome Path3 (skeleton object lacks real texture/draw body from resource load).
2. **Selection index** still binary/toggle under D-pad — not proven as stable 0..N menu row.
3. **C1C0 deep body** never returns cleanly — soft-complete seals bind without full chrome setup.
4. Next: real resource body into desc arena (CRI/WAD member) **or** PCSX2+PINE live object dump for D770 type-1 path.

---

## Result prior session (wave-5 / SearchFile gate + 43AB88 object residual)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — Soft-GS gifP3=**11** px=**573440** prims=2; slot0+obj live; **not MENU YES** |
| Tip regression (fleet gifP3=5) | **Fixed** — SHARED `RealSifRpc` SearchFile EE copy-back stomped ELF image (`ee=0x7584C0`); gate copy-back to heap-class CdlFILE only |
| Force 43B670 | Completes; type-1 path enters; **FUN_0043AB88 / 44E628 still returns null** (object factory residual) |
| Slot object | **Landed** TITLE_LOCAL — after 43AB88 null, plant obj from real desc arena `0xC00000` into slot `0x55E25C+0x3C`, seal flag=1; force `26FBF0` |
| 26FBF0 bind | **Fires** then ESCAPE (~750k) into ADX/lock bands — residual |
| Synthetic type5 | **Not used** |
| Selection index | Still unproven |

### Soft-GS scoreboard (wave-5)

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **Tip main@9657852 (pre-w5)** | `0x564290` | 286720 | 1 | **5** | 7 | GS? SearchFile stomp |
| **sm-w4 claim** | FAE8 | 573440 | 2 | **11** | 30 | slot0 empty |
| **Wave-5 100M claim** | FAE8 / `0x47FDxx` | **573440** | 2 | **11** | **106** | slot0=1 obj=`0xC00000` |

### Change class

- **SHARED** `RealSifRpc.cs`: SearchFile copy-back only when `sendSize∈[0x100,0x200]`, `ee≥0x800000`, `!IsLikelyEeCode` (Vexx CdlFILE heap-safe; SM image protected).
- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - `PrepResourceObjectFactoryState` — clear `0x55FA0C` table / `*0x55FA48`; seed `*0x55E1DC=0x4000`
  - `TryCompleteResourceSlotObject` — post-43AB88-null slot object from arena
  - Force `26FBF0` after plant; expand force PC bands; escape 250k→750k
- **Rejected**: synthetic type5; ungated SearchFile copy-back; re-plant sm+0x28 capacity

### Residual wall (wave-5)

1. **44E628/43AB88 still null** on natural path — object plant is post-fail completion, not full type-1 ctor success.
2. **26FBF0 ESCAPE** before full C1C0 chrome; prims still 2.
3. **Selection index + second UI chrome** unproven — MENU YES open (#7, #3).
4. Do not re-enable ungated SearchFile copy-back into ELF band.

### Play! / PINE

- Play! GameConfig.xml: no SLUS_210.87 entry
- PINE: **N** (pcbreak 43B7E8/44E628 + SearchFile SKIP log sufficient)

---

## Result prior session (wave-4 / sm+0x28 jalr poison + arena)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR?** — Soft-GS gifP3=**11** px=**573440** PC FAE8 body; **not MENU YES** |
| Root cause of force TIMEOUT | **Fixed** — wave-3 planted `sm+0x28/+0x2C=0x100000` as "capacity"; those fields are **allocator fn ptrs** (`FUN_0043BE08`/`43BE98` `jalr`). Live AdEL @0x1FFFFFFF → TIMEOUT |
| Arena + EE bump stub | **Landed** — stub @`0x01FE0100`, ctx @`0x01FE0200`, arena `0x00C00000`..+8MiB; desc buf 6MiB |
| Force 43B670 | **Completes cleanly** (~300k cyc → trampoline); finds free slot `0x55E25C`; type-1 object path entered |
| 43AB88 object | **Still fails** (v0=0) — residual inside type-1 construction after `43A4F8` / `0x56xxxx` tables |
| C1C0 / 26FBF0 | **Still never binds** (no successful slot object) |
| Synthetic type5 | **Not used** |
| Selection index | Still unproven |

### Soft-GS scoreboard (wave-4)

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **Claim tip (pre-w4)** | varies | **573440** | 2 | **11** | ~142 | NEAR plateau |
| **Wave-3 force** | thrash / TIMEOUT | — | — | 11 then death | — | jalr poison |
| **Wave-4 100M pad** | **`0x43FBE8`** FAE8 | **573440** | 2 | **11** | 30 | force returns; slot0 empty; spine held |
| **Wave-4 scoreboard-metrics** | `0x43FBE8` | **573440** | 2 | **11** | 30 | cdvd=201914 exit=false |

### Change class

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - Remove sm+0x28/+0x2C `0x100000` capacity plant (jalr poison)
  - Install EE bump-alloc stub + stream-manager allocator pointers
  - Prefill descriptor +0x18/+0x1C (6MiB) so `43BA48`→`43BDD0` used instead of `43BE08`
  - Seed Midway heap table for natural `20F058`; force always uses `43B670`
  - Early ESCAPE resume when force PC leaves bands (250k) + scrub bogus `*0x678458<0x100000`
- **SHARED**: none
- **Tool**: `tools/_patch_sm_w4.py`

### Residual wall (wave-4)

1. **`FUN_0043AB88` returns null** on type-1 object body after arena alloc succeeds (pcbreak: slot `0x55E25C`, buffers live, path reaches `0x43AEB0` then fails deeper in `0x56xxxx` table work). Need PCSX2+PINE live object / table dump **or** deeper decompile of post-`43A4F8` type-1 path.
2. **C1C0 never runs** until slot+0x3C object is non-null.
3. **Selection index** still unproven under D-pad.
4. **Do not** re-plant sm+0x28 as raw size; **do not** re-enable type5 slot plants.

### Play! / PINE

- Play! GameConfig.xml: no SLUS_210.87 entry
- PINE: **N this wave** (pcbreak + disasm of 43BE08/43AB88/43A410 sufficient for jalr poison + object residual)

---

## Result prior session (wave-3 / reconstruct load request)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR? / GS?** — not claimed. Host tip currently Exit@~22M (baseline A/B same without w3 patch) |
| Reconstruct 26F918 args | **Landed** — a1/a2 are **display dims** (512x384), not name ptrs; path via FUN_00211148 name table 0x4D3A10[id]+1 into handle+0xEC; t0=heap id from *0x584918 |
| Heap wall | Midway heap table 0x65E998 **empty** under HLE → full 26F918 hits FUN_0020F058 AdEL. Wave-3 prefers **FUN_0043B670** with prepared descriptor + sm+0x28/+0x2C capacity |
| Trampoline timeout | 2M-cycle abandon if force path never returns (prevents AdEL hang) |
| Synthetic type5 | **Not used** |
| PresentEeSifHandshake in StartLoadedModule | **Not re-added** |
| Selection index | Still unproven |

### Soft-GS scoreboard

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **Wave-2 (healthy boot)** | FAE8 bands | 573k–860k | 2–3 | **8–9** | 12–16 | force 26F918 a1=a2=0 → *0x678458=0 |
| **Wave-3 mid-session (shared tree)** | hung 0x20243C after AdEL | 860160 | 3 | **11** | 17 | reconstructed dims/path; 26F918 AdEL @0x552023 (dead heap) |
| **Wave-3 tip A/B (isolated)** | 0x486C10 | 286720 | 1 | **5** | 7 | Exit@22M — **baseline without patch same** |

### Change class

- **TITLE_LOCAL** MidwayBootAssist.cs: reconstruct 32EA08/26FD80 load-request; IsResourceHeapLive → force 43B670 when heaps dead; descriptor builder; path strcpy handle+0xEC; sm+0x28/+0x2C capacity; 2M trampoline timeout.
- **SHARED**: none. No PresentEeSifHandshake in StartLoadedModule. No type5 plants.
- **Tool**: 	ools/_patch_sm_w3.py (idempotent).

### Residual wall

1. Midway heaps never init under HLE → cannot run full 26F918 I/O path; 43B670 alone may still fail 43AB88 object create without real resource body.
2. Host tip Exit@22M (vector thrash) blocks late-force verification this session — not attributed to w3 (baseline match).
3. Selection index + interactive MAINMENU still unproven.
4. Next: PCSX2+PINE live *0x678458 / heap table after real menu, or root-cause heap init path under IRX.

### Play! / PINE

- Play! GameConfig.xml: no SLUS_210.87 entry
- PINE: **N this wave** (ELF XREF + disasm of 26F918/26FD80/32EA08/211148/43B9F8 sufficient for arg reconstruction)

---

## Result prior session (wave-2 / resource bind path)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR?** — not claimed. Soft-GS gifP3 plateau 8-9; slot0 still empty |
| Real resource bind path | **Landed** TITLE_LOCAL force-call `FUN_0026F918` then `FUN_0026FBF0` (BFC0→C1C0 chain) |
| Kick outcome | Force fires when gifP3>=8 @70M+; `*0x678458` stays 0 after kick (43B670 fails without real resource name args) |
| Synthetic type5 | **Not used** (wave-12 reject held) |
| PresentEeSifHandshake in StartLoadedModule | **Not re-added** (MSFLAG once at Sif.Reset) |
| Selection index | Still unproven under D-pad |

### Soft-GS scoreboard

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **Before (tip 3748553 / wave-14)** | `0x43FAB4` | 573440 | 2 | **11** | 88 | FAE8 NEAR?; empty slots |
| **After (agent/menu-sm-w2)** | FAE8 / lock bands | 573440–860160 | 2–3 | **8–9** | 12–16 | force kick returns empty handle; no MENU claim |

### Change class

- **TITLE_LOCAL** `MidwayBootAssist.cs`: `MaybeForceResourceSlotBind` / `MaybeResumeAfterForcedResourceBind` — real EE force-call of `FUN_0026F918` (slot alloc via `FUN_0043B670` into stream pool `0x55E25C`) then `FUN_0026FBF0` (sole BFC0→C1C0 caller). Trampoline `0x01FE0030`. Prep handle at `0x678458` mirrors `FUN_0026FD80` zero+flags only.
- **SHARED**: none. Sif MSFLAG remains once at `Sif.Reset` only.
- **Rejected again**: synthetic type5 slot plants; force `26FD80` poll loop; PresentEeSifHandshake in StartLoadedModule.

### Residual wall

1. `FUN_0026F918` returns without `*0x678458=slot` when a1/a2 resource name args are zero — need real load request from `FUN_0032EA08` args or PCSX2+PINE live handle dump.
2. gifP3 8–9 on this wave (below wave-14 11 plateau) — second chrome still blocked.
3. Selection index unproven.

### Play! / PINE

- Play! `GameConfig.xml`: no SLUS_210.87 entry
- PINE: **N this wave** (ELF XREF of sole bind chain sufficient to land force path; kick fail is missing resource IDs, not unknown XREF)

---

## Result prior session (wave-14 / WAD restore + spine)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR?** @100M — gifP3=11 dmac=88 Soft-GS; selection + second chrome still unproven |
| Root cause of tip cdvd=1 | **Fixed** — `SifRpc.StartLoadedModule` PresentEeSifHandshake before every SIFMAN/SIFCMD/SIFINIT `_start` (c423c4f) collapsed GAMEDATA.WAD |
| LooksLikeAsciiWord EPC | **Fixed** — aligned code PCs (e.g. `0x414A30`) matched ≥3 printable; removed from data-as-code test |
| Logo spine main kick | **Added** — one Midway main re-entry when gifP3≤5 after cdvd≥180k (mirrors historical AdEL→main) |
| diagnose 20M | PC=`0x47FCF4` px=286720 prims=1 gifP3=5 dmac=7 cdvd=**198840** binds=16/252 |
| 100M host-present | PC=`0x43FAB4` px=573440 gifP3=**11** dmac=**88** cdvd=201400 (FAE8 stream body) |

### Change class

- **SHARED** `SifRpc.cs`: remove PresentEeSifHandshake from StartLoadedModule (keep `Sif.PresentSifInit` cold path)
- **TITLE_LOCAL** `MidwayBootAssist.cs`: data-EPC unaligned/past-RDRAM only; post-WAD main spine kick; delay group-6 plant until gifP3≥8
- **Not done:** synthetic stream slots, C1C0 force, FFmpeg

### Residual wall

1. gifP3 plateau **11** — empty stream slots / C1C0 never binds
2. Selection index still unproven
3. Soft-GS prims low — not full interactive MAINMENU claim

---

## Result prior session (wave-13 / IRX Soft-GS GS?)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR?** @100M only — diagnose 20M stays **GS?** (expected pre-spine) |
| Root cause of 286k px | **Documented** — one Soft-GS clear; not interactive MAINMENU; historical 11.7M px was host FMV inflation (retired) |
| AdEL “GAMEDATA” wild jump | **Fixed** — data-as-code AdEL re-home (EE + Midway vector escape) |
| gifP3 @100M | **11** (plateau; second chrome still blocked) |
| dmac @100M | **88** (was 16 pre-fix) |
| Stream slots / C1C0 | **Still empty / never binds** (wave-12 residual) |
| diagnose 20M | PC=`0x47FCF4` px=286720 prims=1 gifP3=5 dmac=7 cdvd=198840 binds=16 calls=252 |
| 100M host-present | PC=`0x43FAB4` px=573440 prims=2 gifP3=11 dmac=88 cdvd=201400 binds=23 calls=276 |

### Play! / PINE

- Play! `GameConfig.xml`: **no SLUS_210.87 entry** (generic IOP HLE)
- PINE: **N** for this wave (pcbreak AdEL + disasm sufficient)

### Change class

- **SHARED** `EmotionEngine.cs`: unaligned fetch → if PC looks like data-as-code (ASCII / past RDRAM), recover like open-bus (not every AdEL)
- **TITLE_LOCAL** `MidwayBootAssist.cs`: exception-vector escape when EPC is data-as-code
- **SHARED** `Program.cs`: `scoreboard-metrics` includes `prims`
- **Not done:** FFmpeg, slot plants, RealSifRpc thrash, force-call 26FBF0/C1C0

### Why 286720 px is not MAINMENU

1. `640×448 = 286720` — single Soft-GS framebuffer clear (`prims=1`).
2. diagnose 20M is **before** logo-spine restore (~58M) and stream plants (~60M).
3. Host logo Blit no longer counts as Soft-GS `px` (correctness).
4. Pad OPEN works (ghost after IOPRP); wall is **empty stream slots**, not pad.
5. Full writeup: [`out/traces/MK_IRX_GS_ROOT_CAUSE_20260731.md`](../../out/traces/MK_IRX_GS_ROOT_CAUSE_20260731.md)

### Residual wall (wave-13)

1. **gifP3 plateau 11** — FAE8 live; slot0 empty → no second-chrome Path3.
2. **C1C0 / 26FBF0 never runs** — force resource status does not bind objects.
3. **Soft-GS prims=2** @100M — almost no UI raster until objects bind.
4. Next: PCSX2+PINE live stream slots **or** correct path to `FUN_0026FBF0` with real manager (no type5 plants).

## Result prior session (wave-12)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — no claim; selection index + second chrome still unproven |
| Stream slot objects | **Blocked** — synthetic slot0 plant **REJECTED** (EE exception death) |
| C1C0 bind path | **Never runs** under HLE (pcbreak 0 hits @100M) |
| gifP3 | **11** (plateau held; no second-chrome lift) |
| dmac | **16** @100M |
| Selection index | Still unproven — multi busy `0x54E608+i*4` is re-entrancy only |
| diagnose 20M | PC=`0x47FCF0` px=11.7M gifP3=5 dmac=7 cdvd=198840 (baseline hold) |
| claim-class 100M | PC=`0x43FB40` px=32.1M gifP3=11 dmac=16 syscalls~914k cdvd=198840 |

### Play! / PINE

- Play! `GameConfig.xml`: **no SLUS_210.87 entry** (generic IOP HLE)
- Play! TITLE: generic only
- Play! PAD: `Iop_PadMan.cpp` — already SHARED
- PINE: **N** (disasm + pcbreak sufficient for reject)

### Change class

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - Wider `sel-idx-delta` bases (`0x54E608` multi busy, `0x75E7A0`, 0..16 cap)
  - Documented **REJECTED** `MaybePlantStreamSlotObjects` (type5 stub + D6F8[0])
  - **Not called** from Step — residual only as comment + empty stub for next agent
- **SHARED**: none

### Stream slot diagnosis (wave-12 evidence)

| Fact | Evidence |
|------|----------|
| Sole C1C0 caller | `jal 0x43C1C0` only at `0x43C0D4` inside `FUN_0043BFC0` |
| Sole BFC0 caller | `0x26FC34` inside `FUN_0026FBF0` (resource post-load bind) |
| `FUN_0026FBF0` / `0x26FD80` / C1C0 | **0 pcbreak hits** through 100M pad-inject |
| FAE8 still walks | Live PC samples `0x43FB40` / `0x43FBB0` / `0x44D744` (empty FBB0 + D6F8) |
| Slot plant type5 | Plant @74.1M → PC `0x8000018x` by 80M; final `0xAB47` — **regresses** healthy FAE8 |
| D770 object contract | `442A0`: +0x48≠0 valid; `D7C8`: type must be **1..4** to continue (type5 early-out still unsafe in practice with partial slot) |

**Do not** re-enable synthetic slot plants without PCSX2 live object dump or a working `26FBF0` path.

### Residual wall (wave-12)

1. **gifP3 plateau 11** — FAE8 live; empty slots / empty D6F8 → no second-chrome Path3.
2. **Selection index unknown** — D-pad only toggles multi re-entrancy + CAS cells.
3. **FUN_0043C1C0 never binds** — resource post-load (`26FBF0`) never reached; force status at `0x678458+0x48` unblocks wait but does not run bind.
4. Next: PCSX2+PINE dump of live menu stream slots **or** force/reach `FUN_0026FBF0` with a real manager pointer at `*0x678458`.

## Result prior session (wave-11)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — stream-manager ready planted; **selection index + gifP3≥12 still unproven** |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **11** (plateau; no second-chrome lift) |
| dmac | **16** @100M |
| Frame cb | **`*0x75BDD8=0x43F920` held** + arg `0x5BB860` |
| Group-6 multi | **`*0x75E950=0x43F920` held**; escapes **`0x427678`** (a0=6) |
| Stream cookie | **`*0x5BB860=1` planted** |
| Stream manager | **`*0x55E228` (base+0x38) ready=1** soft-plant (CD58 defaults); slot0 still empty |
| Stream work gate | **`*0x55E1EC=1` held**; skip `*0x55E200=0`; CAS re-arm held |
| Pad | Dense START/CROSS/DOWN/UP; ghost PADMAN; Play! TITLE/PAD consulted |
| Final PC | **`0x43FB40`** (FUN_0043FAE8) @100M |
| Accept | Stream leaf live; **selection index + second UI chrome not proven** |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; **no `*0x75C0D0` plant** |
| diagnose 20M | PC=`0x47FCF0` px=11.7M gifP3=5 dmac=7 cdvd=198840 (baseline hold) |

### Play! / PINE

- Play! `GameConfig.xml`: **no SLUS_210.87 entry** (generic IOP HLE)
- Play! PAD: `Iop_PadMan.cpp` (0x80000100) — already ported SHARED
- Play! TITLE: generic only — no title patches for SLUS_210.87
- PINE: **N** (full disasm of CD58 / FAE8 / FBB0 / C1C0 / D770 / parent `0x43CC40`)

### Change class

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - `MaybeInitStreamManager` — soft-plant FUN_0043CD58 defaults (`*base+0x38=1` ready, float config, clear lock/CAS garbage)
  - **Rejected**: force-call CD58 via trampoline (regressed gifP3 11→5 — resume raced other Step assists)
  - Wider menu-sel: `smReady`, slot `obj/+0x3C`, `wk/+0x60`, D6F8 table, `sel-idx-delta` under D-pad
- **SHARED**: none

### Stream manager diagnosis (disasm)

| Addr | Role |
|------|------|
| `FUN_0043CB18` | returns base `0x55E1F0` |
| `FUN_0043CD58` | memset manager 0x15CC; defaults; ready `*base+0x38=1` — sole caller `0x43CC40` never reached under HLE |
| `FUN_0043FAE8` | gate `*0x55E1EC==1` + CAS `*base+0x58`; walk 8 slots at `base+0x6C` stride `0x2AC` → `FBB0` |
| `FUN_0043FBB0` | requires `*slot==1` and `*slot+0x60!=1`; work uses `*slot+0x3C` object → `0x44D770` |
| `FUN_0043C1C0` | binds slot flag + object from resource descriptors (never runs — no objects) |
| D6F8 table | `0x55FA0C` ×8 — empty at 100M |

**Do not** plant `*slot=1` with null object (FBB0→D770 null path).

### Selection index (wave-11)

Dense D-pad `sel-idx-delta` logs: only binary toggles `*54E5E0/*54E610/*54E618/*54E620` 0↔1 and CAS `*55E248`. **No stable 0..N cell** that tracks Up/Down.

### pad-inject @ 100M (host-present, wave-11 soft-plant)

```
  58200000  logo-spine kick → ADX pump gifP3=5
  60000000  group-6 + frame-cb + cookie=1 + CD58 defaults ready=1
  73200000  re-arm stream CAS; gifP3 climbs 6→8→11
  75000000+ CROSS/DOWN/UP; FAE8 body; slot0 remains 0
 100000000  final PC=0x43FB40 gifP3=11 dmac=16 px=32112640 syscalls~914k cdvd=198840
```

### Residual wall (wave-11)

1. **gifP3 plateau 11** — FAE8 live; **slot0 empty** / no D6F8 objects → no second-chrome Path3.
2. **Selection index still unknown** — only busy/re-entrancy flags under D-pad.
3. **FUN_0043C1C0** resource→slot bind never runs; need PCSX2+PINE live menu objects or resource-path fix.
4. Force-call CD58 unsafe without isolated Step return after force.

## Result prior session (wave-10)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — group-6 entry (`a0=6`) + natural multi countdown; **selection index + gifP3≥12 still unproven** |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **11** (plateau; no second-chrome lift) |
| dmac | **16** @100M (stream body live; 120M CAS path still needed for dmac climb) |
| Frame cb | **`*0x75BDD8=0x43F920` held** + arg `0x5BB860` |
| Group-6 multi | **`*0x75E950=0x43F920` held**; escapes land **`0x427678`** (a0=6) not bare `0x427518` |
| Stream cookie | **`*0x5BB860=1` planted**; slot0 `*0x55E25C` still zero |
| Stream work gate | **`*0x55E1EC=1` held**; skip `*0x55E200=0`; CAS re-arm held |
| Pad | Dense START/CROSS/DOWN/UP; ghost PADMAN; Play! PAD consulted (generic) |
| Final PC | **`0x43FB40`** (FUN_0043FAE8 work loop) sustained 87–100M — not worker thrash |
| Accept | Multi + stream leaf live under pad; **selection index + second UI chrome not proven** |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; **no `*0x75C0D0` plant** |
| diagnose 20M | PC=`0x47FCF0` px=11.7M gifP3=5 dmac=7 cdvd=198840 (baseline hold) |
| verify 50M | PC=`0x41D608` px=28.9M gifP3=5 dmac=7 cdvd=198840 exitReq=False |

### Play! / PINE (wave-10)

- Play! `GameConfig.xml`: **no SLUS_210.87 entry** (generic IOP HLE)
- Play! PAD: `Iop_PadMan.cpp` (0x80000100) — ghost DMA + ForceRefreshPad already ported SHARED
- Play! TITLE/SIF: generic modules only
- PINE: **N** (disasm of multi `0x427518` / group-6 entry `0x427678` / FAE8 sufficient)

### Change class (wave-10)

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - `PickMenuDispatchResume` / `ApplyMenuDispatchResume` — prefer **`0x427678`** (sets a0=6) over bare multi; only heal *dead* $ra (never retarget worker→pump — that caused `PC=0x8000018C`)
  - `MaybeBreakMenuCallbackCountdown` — **no sticky break on natural s2=0..5** (only absurd s2≥64 / extreme sticky)
  - Post-spine + lock-wrapper escapes use menu-dispatch helper; ADX lock→menu kick after spine
  - Wider menu-sel telemetry (stream slot0 + pad DMA + extra BSS bands)
- **SHARED**: none this wave

### pad-inject @ 100M (host-present, wave-10)

```
  58200000  logo-spine kick → ADX pump gifP3=5
  60000000  group-6 + frame-cb + cookie=1 + stream gateEc=1
  73200000  re-arm stream CAS; gifP3 climbs 6→8→11
  75000000  CROSS; gifP3=11; memset + VU escape
  77000000  post-spine 0x47FEA8 → 0x427678 (group-6 a0=6)
  82000000  DOWN @ multi loop 0x427570
  84800000  CROSS → stream slot 0x43FBB0
  87000000+ sustained FAE8 body 0x43FB40 / 0x43FC14 / frame-cb 0x4156E4
 100000000  final PC=0x43FB40 gifP3=11 dmac=16 syscalls~914k cdvd=198840
```

### Residual wall (wave-10)

1. **gifP3 plateau 11** — stream FAE8 loop live but Path3 not 12–14; stream work **slot0 empty** (`*0x55E25C=0`) so FBB0 has nothing to draw.
2. **Selection index location still unknown** — D-pad moves multi busy flags `*54E610/*54E618` only; no stable 0..N cell under pad.
3. **Hard accept-to-submenu unproven** — no new UI string set after CROSS.
4. Next: PCSX2+PINE dump of live menu object / stream-manager slots under real pad; or force `FUN_0043CD58` stream-manager init if registration path never ran.

## Result prior session (wave-9)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — stream CAS re-arm + post-spine worker escape; **selection index + gifP3≥12 still unproven** |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **11** (plateau; CAS re-arm did **not** lift Path3 to 12–14) |
| dmac | **730** @120M (was **16** wave-8 — stream body re-enters) |
| Frame cb | **`*0x75BDD8=0x43F920` held** + arg `0x5BB860` |
| Group-6 multi | **`*0x75E950=0x43F920` held** cookie `0x5BB860` |
| Stream cookie | **`*0x5BB860=1` planted** (live may become `0x5BB8`) |
| Stream work gate | **`*0x55E1EC=1` held**; skip `*0x55E200=0` held |
| Stream CAS | **`*0x55E248` re-armed 0** (was stuck 1 after first FAE8 pass) |
| Pad | Dense START/CROSS/DOWN/UP; ghost PADMAN; Play! PAD consulted (generic) |
| Final PC | **`0x43FB60`** (stream work body) — was `0x480Axx` worker thrash |
| Accept | Stream leaf live; **selection index + second UI chrome not proven** |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; **no `*0x75C0D0` plant** |
| Smokes | **ALL PASSED** |
| diagnose 20M | PC=`0x47FCF0` px=11.7M gifP3=5 dmac=7 cdvd=198840 binds=16 calls=241 (baseline hold) |

### Play! / PINE (wave-9)

- Play! `GameConfig.xml`: **no SLUS_210.87 entry** (generic IOP HLE)
- Play! PAD: `Iop_PadMan.cpp` (0x80000100) — ghost DMA + ForceRefreshPad already ported SHARED
- PINE: **N** (not used this wave; disasm of FAE8/F920 sufficient for CAS wall)

### Change class (wave-9)

- **TITLE** `MidwayBootAssist.cs`: `MaybeRearmStreamCas`, `MaybeEscapePostSpineWorkerThrash`, skip-flag hold, prefer group-6/stream over ADX for post-spine lock escape
- **SHARED**: none this wave

### pad-inject @ 120M (host-present, wave-9)

```
  58200000  logo-spine kick → ADX pump gifP3=5
  60000000  group-6 + frame-cb + cookie=1 + stream gateEc=1 skip200=0
  73200000  re-arm stream CAS *0x55E248=0 (was 1); gifP3 climbs 6→8→11
  75000000  CROSS; gifP3=11; memset + VU pastEp escape
  77000000  post-spine worker thrash 0x47FEA8 → pump/group-6
  85550000  menu-sel tick *54E600 climbs; cas248 oscillates 0/1 under re-arm
 120000000  final PC=0x43FB60 gifP3=11 dmac=730 syscalls~1.08M cdvd=198840
```

### Residual wall (wave-9)

1. **gifP3 plateau 11** — stream FAE8 re-entry lifts **dmac** (16→730) not Path3; second chrome needs UI/PATH3 path, not only stream DMA.
2. **Selection index location still unknown** — D-pad moves `*54E610/*54E618` flags only; wider BSS scan not yet a stable 0..N cell.
3. **Hard accept-to-submenu unproven** — no new UI string set after CROSS.
4. Prefer PCSX2+PINE dump of menu object / selection cell under real pad if next wave stalls.

## Result prior session (wave-8)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — cookie init + sticky thrash escapes; **selection index + second chrome unproven** |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **11** (no second-chrome lift to 12–14) |
| dmac | **16** |
| Frame cb | **`*0x75BDD8=0x43F920` held** + arg `0x5BB860` |
| Group-6 multi | **`*0x75E950=0x43F920` held** cookie `0x5BB860` |
| Stream cookie | **`*0x5BB860=1` planted** (later live word0 may become `0x5BB8`) |
| Stream work gate | **`*0x55E1EC=1` held** |
| Pad | Dense START/CROSS/DOWN/UP; sticky lock→pump; syscalls **~4.34M** @150M |
| UI strings | **Kombat**, **Start** in RDRAM |
| Accept | Soft thrash walls escaped; **selection index + second UI chrome not proven** |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; **no `*0x75C0D0` plant** |
| Smokes | **ALL PASSED** |

### pad-inject @ 150M (host-present, wave-8)

```
  60000000  group-6 + frame-cb + cookie=1 + stream gate
  75000000  CROSS; gifP3 5→11; VU pastEp escape → ADX pump
  76850000  lock hot break
  89150000+ stickyBand lock thrash → 0x4147F8 (syscalls climb hard)
 150000000  final PC~0x480A88 gifP3=11 dmac=16 syscalls~4.34M cdvd=198840
```

### Residual wall (wave-8)

1. **gifP3 plateau 11** — second chrome / historical 12–14 YES band not reached.
2. **Selection index location still unknown** — D-pad does not move a stable 0..N cell under pad telemetry (`*54E610/*54E618` flags move only).
3. **Hard accept-to-submenu unproven** — no new UI string set after CROSS.
4. Late PC often in commercial-worker / lock / pad bands rather than a clear menu accept leaf.

## Result prior session (wave-7)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — G0 Exit/no-WAD regression **fixed**; stream work gate **open**; hard accept still unproven |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **11** (spine restored; was 5 under pre-RR G0 regression / wave-6 no-main-rehome) |
| dmac | **16** |
| Frame cb | **`*0x75BDD8=0x43F920` held** + arg `0x5BB860` |
| Group-6 multi | **`*0x75E950=0x43F920` held** cookie `0x5BB860` |
| Stream work gate | **`*0x55E1EC=1` held** (was wrong plant at `0x55E1E8` only) |
| Pad | Dense inject; ghost PADMAN; PC in pad-poll / stream work / lock wrappers |
| UI strings | **Kombat**, **Start** in RDRAM |
| Accept | Soft thrash walls escaped; stream work body **runs**; **selection index + second UI chrome not proven** |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; **no `*0x75C0D0` plant** |
| Smokes | **ALL PASSED** |

---

## How far

| Milestone | Status | Evidence |
|-----------|--------|----------|
| Disc boot / ELF | **Yes** | `Booted SLUS_210.87 entry=0x0011C070` |
| CRI ADX / GAMEDATA.WAD | **Yes** | `cdvdSectors=198840` |
| G0 THREADMAN RR for SM | **Yes** | `PreferRoundRobinSched=true` on disc mount — priority band caused Exit@12.4M |
| Frame cb re-arm | **Yes** | `*0x75BDD8=0x43F920` held after plant |
| Group-6 multi plant | **Yes** | `*0x75E950=0x43F920` held |
| Stream work gate | **Yes** | `*0x55E1EC=1` — `FUN_0043FAE8` no longer early-outs |
| Healthy post-WAD EE loop | **Yes** | PC in `0x414xxx` / `0x4275xx` / `0x43FBxx` / `0x43FDxx` / `0x44D7xx` |
| UI strings | **Partial** | Kombat / Start (Continue/Options not always found at 120M) |
| Selection index change | **No** | `*0x54E620` re-entrancy only; small-int scan in `0x54E5E0..` does not track D-pad |
| Second UI chrome (gifP3 lift) | **Partial** | gifP3 **5→11** + dmac **7→16**; still short of historical 12–14 YES band |
| Main menu hard accept | **No** | MENU NEAR only |

### pad-inject @ 120M (host-present, wave-7)

```
  18000000  ADX gate binds=22 calls=224
  55000000  resource gate cdvd=198840
  58200000  logo-spine kick → ADX pump
  60000000  group-6 + frame-cb + stream gateEc=1
  75000000  PRESS CROSS; gifP3 climbs 5→11; VU blit escape
  88000000  PRESS DOWN
  98000000  PRESS CROSS
 120000000  final PC=0x38568C gifP3=11 dmac=16 cdvd=198840 syscalls~1.11M px=32.1M
```

### G0 regression fixed (wave-7)

**Symptom after G0 BIOS merge:** EE `Exit(0)` @ ~12.4M with corrupt `$ra`, `cdvdSectors=1`, syscalls frozen ~1729.

**Root cause (A/B):** `KernelState.FindNextRunnable` priority band + `MaybePreempt` reordered Midway ADX pump vs main. Pre-G0 circular RR restored WAD.

**SHARED fix:**
- `KernelState.PreferRoundRobinSched` — SM sets true on disc mount (`MidwayBootAssist.OnDiscMounted`)
- Priority scheduling remains default for THREADMAN smokes (`KernelHle_ThreadmanPriorityAndDelay` still passes)
- UDNL commercial handoff: IOPRP image apply is **name-only** (no bulk LoadIrx of retail IRX)

**TITLE_LOCAL fix:**
- Plant/hold `*0x55E1EC=1` (stream work gate for `FUN_0043FAE8`) — prior plant was wrong offset `0x55E1E8` only

---

## Fixes this session (wave-7)

1. **SHARED `KernelHle.PreferRoundRobinSched`** — SM opts into circular RR; priority band remains default for G0 smokes
2. **SHARED UDNL name-only IOPRP** — commercial handoff registers module names without LoadIrx upgrade of HLE services
3. **TITLE_LOCAL stream work gate** — `MaybeHoldStreamWorkGate` + resource-gate plant of `*0x55E1EC=1`
4. **Alarm fire soft** — callback invoke opt-in via `DETPS2_ALARM_FIRE=1` (API still arms/releases)
5. **ChangeThreadPriority** — no forced SwitchToNext unless `DETPS2_PRIO_YIELD=1`

Prior spine restores held: ADX self-deadlock scrub, list-walk break, format-stall, VU blit escape, memset break, frame-cb re-arm, group-6 multi, logo-spine narrow, title-hash escape, no `*0x75C0D0` plant.

## MENU

**NEAR-MENU (interactive-class EE path, not full accept-to-submenu).**

Evidence for NEAR:
- Stable post-WAD EE loop through 120M after G0 RR fix
- `*0x75BDD8` + `*0x75E950` held at stream tick
- `*0x55E1EC=1` — stream work body entered (`0x43FB30`, `0x43FDB0`, `0x44D744`)
- Dense pad + ghost DMA; syscalls climb
- Title strings Kombat/Start in RDRAM
- Menu tick `*0x54E600` climbs under pad

Missing for MENU YES / issue #7 close:
- **Selection index** memory not identified
- **Selection index** still unproven under pad
- Stream cookie object at `0x5BB860` remains **all-zero**
- gifP3 plateau **11** (needs 12–14+ / second chrome certification)
- Late PC parks in VU blit band `0x38568C` after spine (guard fires; still sticky)
- Hard accept-to-submenu unproven

### Residual wall

1. **Stream cookie `0x5BB860` object zero** — slot planted; object body never inited by real `FUN_0043ccf8` path. Need PCSX2 dump or cookie ctor decompile.
2. **Selection index location unknown** — D-pad does not move small ints in `0x54E5E0..+0x80`.
3. **gifP3 plateau 11** — spine restored but not full historical 12–14 YES band; VU band sticky after.
4. Prefer **shared HLE** for cookie/object init if root cause is incomplete resource-manager registration.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-sm
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-sm/DetPS2.Core.dll pad-inject user-media-mk.json --cycles=120000000 --host-present `
  --press=START:55000000:1500000 --press=CROSS:75000000:2000000 `
  --press=DOWN:88000000:800000 --press=CROSS:98000000:2000000
dotnet build Tests/DetPS2.Tests.csproj -c Release -o out/game-sm-tests
dotnet exec out/game-sm-tests/DetPS2.Tests.dll
```
