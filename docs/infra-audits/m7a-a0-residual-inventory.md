# M7-a A0 — residual class inventory (PATH2/3 IMAGE + DISPFB)

**Status:** A0 inventory only — **telemetry / doc; no Core behavior change**  
**Date:** 2026-08-04  
**Tip context:** DetPS2 tip `acb9c20` (worktree)  
**Pri / ID:** P1 / **M7-a** Slice 0 (`docs/infra-audits/m7a-path23-image-dispfb-design.md` §4.2 / §7.2 gate **A0**)  
**Sources:**  
`docs/infra-audits/m7a-path23-image-dispfb-design.md`,  
`docs/infra-audits/m7a-path23-image-dispfb-seed.md`,  
`docs/infra-audits/gamequirks-infra-debt.md` §6 / priority #6,  
`docs/title-ports/SCOREBOARD.md` (residual seat 2026-07-31),  
title-port charters (GoW / Haven / Whip / Dec / DA / BO2 / B3),  
`docs/graphics/DISCOVERY_LOG.md`,  
`tools/SCOREBOARD_SCHEMA.md`  
**Mode:** read-only classification. **No GameQuirks edits. No RealSifRpc edits. No Core change. No commit.**

---

## 1. Purpose

Gate **A0** (design §7.2): emit residual class **R0–R5** labels for ≥6 fleet titles from **existing** docs / scoreboard claim notes / GameQuirks PRESENT residual comments — **no new fleet run required for this note**.

This table drives Slice 1–3 ownership (S8 Path/IMAGE vs S10 DISPFB/composite vs non-M7-a stream). It does **not** assert natural MENU YES and does **not** demote assists.

---

## 2. Residual class taxonomy (from design §3.2)

| Class id | Name | Evidence sketch |
|----------|------|-----------------|
| **R0** | Upstream no bytes | EE ring empty / FILEIO fail; assist Host→Local from disc still lights present |
| **R1** | No IMAGE | EE has tex buffer; `TagsCompletedImage≈0` / game gif IMAGE≈0; Path2 setup-only |
| **R2** | IMAGE wrong page | `imgBytes>0` (game or residual), natural DISPFB programmed, natural lit=0, residual `LastImageTrx` / `Frame` |
| **R3** | DISPFB unset | DISPFB1/2=0; composite Frame/FBP0; B3 honest residual |
| **R4** | Composite skip | IMAGE + DISPFB set; merge cache / black expand stamp prevents natural composite |
| **R5** | Path3 masked starve | M3P sticky + held Path3; never unmask → IMAGE never drains (matrix title) |

**Primary** = single best label for M7-a branch. **Secondary** = co-present wall that may reclassify after primary is fixed.  
**R0 is not M7-a Core work** (media / SIF / FILEIO owners).

Fix branch (design §4.4): **R1/R5 → Slice 2 (S8)** · **R2/R3/R4 → Slice 3 (S10)** · **R0 → defer non-M7-a**.

---

## 3. Six-title residual class table

Classifications are **likely** labels from wall records dated 2026-07-31 (residual seat + title-port charters). Live tip may drift; re-score with claim scrape when Slice 1 starts.

| # | Title | Serial / assist | Primary | Secondary | Assist PRESENT residual | Scoreboard / claim snapshot (residual seat or charter) | Why this class | M7-a next seat |
|---|-------|-----------------|---------|-----------|-------------------------|--------------------------------------------------------|----------------|----------------|
| 1 | **GoW** | `SCUS_973.99` · `GodOfWarAssist` | **R2** | R1 · R4(expand) | Host→Local **R_SHELL / TIT1**; `ForceRefreshPresentComposite` when expand black | @100M `px=634306 lit=60866 imgBytes=262144 naturalDispfbPx=0 residualDispfbPx=60866 compositeSource=Frame expandHits=2 gifP2=31 gifP3=0` (`gow-residual-100m-…`) | Discovery: natural DISPFB CT24 @ high FBP composite **0 lit** while IMAGE / residual lives at high-DBP **PSMT4** → residual Frame / LastImageTrx. Natural gif IMAGE tags≈0 (assist feeds imgBytes). Black ofx expand stamps FB before merge. | Slice 3 (S10 page bind / composite preference) + Slice 2 if game IMAGE never starts after Fedo |
| 2 | **Haven** | `SLUS_205.17` · `TeamIcoAssist` | **R1** | R0@50M fleet | Host→Local **SYSTEM.RW3 / CUBE** (+ MANAGER/NICO class on TeamIco) | @100M `px=329852 lit=43132 imgBytes=194560 naturalDispfbPx=43132 residual=0 compositeSource=NaturalDispfb gifP2=65 gifP3=68` — IMAGE is **assist** plant; @50M CRT0 `px=0` expected | Charter: gif image tags=0 natural; Host→Local residual only. Present may show NaturalDispfb after plant — **not** natural MENU YES. 50M black = decompress not done (budget / stream timing, not Path IMAGE). | Slice 2 (S8 game IMAGE) after ≥100M spine; if EE never binds SYSTEM.RW3 → R0 defer |
| 3 | **Whip** | `SLUS_206.84` · `WhiplashAssist` | **R1** | R4(expand ofx) | Host→Local **GOE firstscreen / frontend** 256 KiB | @100M `px=610373 lit=5189 imgBytes=262144 naturalDispfbPx=36933 residual=0 gif image tags=0` (`whip-residual-100m-…`); Path2 title strip expandHits | GOE Open+Start / ring warm works; bytes **never reach GIF IMAGE** (gif IMAGE=0). Natural DISPFB circuit OK once residual IMAGE lands. ofx=0x8000 expand is G-GFX-6 adjacency, not primary IMAGE gap. | Slice 2 (S8 Path2 IMAGE from GOE ring) |
| 4 | **Dec / DA** | Dec `SLUS_208.81` · DA `SLUS_204.23` · `MidwayFamilyAssist` | **R1** | Dec R3(DISPFB1 often 0) · DA R4(lit≪nat) · Path2 abort | Host→Local **gameart.ssf** SEC tiles (PL-029 / DA feed after gifP2≥2) | Dec: `imgBytes=557056 natural gif-tags image=1 Path2-only menu`; DA residual seat thin `lit=32768` / chrome jobs `imgBytes=360448` Host→Local art-scale | EE has gameart body (MWFILE open 2.8 MiB); natural EE GIF IMAGE residual (image≈1). Assist BITBLT is PRESENT escape. Path2 paint live; gifP3 stuck low. Do **not** invent PATH3. | Slice 2 (S8 natural IMAGE multi-DMA / Path2 hold under Path3 sticky) |
| 5 | **BO2** | `SLUS_200.24` · `BloodOmen2SnAssist` | **R1** | multi-prim / expand | Host→Local **MAINMENU / MAINSKY** (MENU-BO2) | Residual seat @100M `px=372716 lit=85996 imgBytes=392192 naturalDispfb` (assist IMAGE); pre-assist charter: `imgBytes=0 prims=1 gifP2=54` ofx expand title FB with CODE+MAINMENU **streamed** to EE | MAINMENU.BG2 real bytes at EE `@0xC00000` — **not R0**. Natural GIF IMAGE / multi-prim TEX path missing (`imgBytes=0` without assist). Assist Host→Local + present refresh lights lit. | Slice 2 (S8/S9 game IMAGE from streamed BG2); S10 DISPFB after multi-prim |
| 6 | **B3** | `SLUS_210.50` · `Burnout3Assist` | **R3** | residual Frame heavy · M5-a IRQ adjacency | **No** Host→Local chrome plant class; merge composite FRAME/FBP0 when DISPFB unset | @100M residual seat `px=5997653 lit=100106 imgBytes=525824 naturalDispfbPx=94208 residualDispfbPx=1188242 src=Frame` mixed; charter claims often **DISPFB1=0** with large game `imgBytes` / gifP2+P3 | Game Path IMAGE + prims **work** (imgBytes multi-MiB class). Privileged DISPFB often never written — honest FRAME/FBP0 residual (**A4**, do not plant DISPFB). | Slice 3 document / natural DISPFB if retail ever writes it; **A4 OK residual**; M5-a if flip pending blocks later IMAGE chains |

---

## 4. Per-title evidence notes

### 4.1 God of War — primary **R2**

| Field | Evidence |
|-------|----------|
| PRESENT assist | `GodOfWarAssist` Host→Local R_SHELL/TIT1; force composite on expand black + imgBytes floor (`gamequirks-infra-debt.md` §6 / one-line roll-up) |
| Natural IMAGE | Discovery + residual claim: **gif image≈0**; gifP3=0; Path2 expand strips dominate prim path |
| DISPFB / composite | `naturalDispfbPx=0`, `residualDispfbPx=60866`, `compositeSource=Frame`; earlier wall: DISPFB PSMCT24 empty vs high-DBP PSMT4 IMAGE → LastImageTrx residual (design §2.2 / discovery MENU-GOW-3) |
| Secondary | **R1** until game GIF IMAGE tags rise without assist; **R4**-adjacent when black ofx expand stamps full FB before composite (`EXPAND_POLICY.md`) |
| Not primary R0 | Assist feeds from disc/RDRAM shell payloads when stream seeds exist; Fedo/type-2 stream completeness is separate INFRA |

### 4.2 Haven — primary **R1**

| Field | Evidence |
|-------|----------|
| PRESENT assist | `TeamIcoAssist` MENU-HAVEN-3 Host→Local SYSTEM.RW3 / CUBE.BIN |
| Natural IMAGE | Title port: **gif image tags=0**; lit via residual only |
| DISPFB / composite | Residual seat: `compositeSource=NaturalDispfb` after plant — present path honest, **source IMAGE is assist** |
| Secondary **R0@50M** | Fleet 50M CRT0 pre-decompress `px=0` expected; claim budget **≥100M** |
| M5-a adjacency | Design open Q6: VIF busy / IRQ may stall next IMAGE chain (Haven VIF busy clear is INFRA, not PRESENT) |

### 4.3 Whiplash — primary **R1**

| Field | Evidence |
|-------|----------|
| PRESENT assist | `WhiplashAssist` Host→Local firstscreen/frontend bulk BITBLT (MENU-WHIP-2) |
| Natural IMAGE | `gif image tags=0`; `imgBytes=262144` from assist; charter: natural EE GIF IMAGE residual |
| Stream | GOE Open+Start firstscreen **yes** — bytes exist in EE/ring → not R0 |
| DISPFB | Natural DISPFB composite **yes** once residual IMAGE present (`dispfbPx=36933`) |
| Secondary | ofx=0x8000 expand title strip (G-GFX-6 demote later) |

### 4.4 Dec / DA (Midway family) — primary **R1**

| Field | Evidence |
|-------|----------|
| PRESENT assist | `MidwayFamilyAssist` Host→Local gameart.ssf (Dec PL-029 tiles; DA `TryFeedDaGameartHostToLocal` after gifP2≥2) |
| Natural IMAGE | Dec: natural `gif-tags image=1` residual vs Host→Local `imgBytes=557056`; Path2-only menu; no invent PATH3 |
| Stream | gameart.ssf MWFILE open / load OK — EE has bytes → not R0 |
| Secondary | Dec charter DISPFB1=0 on some Soft-GS regs (**R3**-like composite fill); DA lit≪`naturalDispfbPx` present-sample residual (**R4**-adjacent); Path2 DIRECT abort residual (S8) |
| PATH3 | gifP3 stuck low (~6); matrix: do not wholesale unmask (`PATH3_MASK_MATRIX.md`) |

### 4.5 Blood Omen 2 — primary **R1**

| Field | Evidence |
|-------|----------|
| PRESENT assist | `BloodOmen2SnAssist` MENU-BO2 Host→Local MAINMENU/MAINSKY + `ForceRefreshPresentComposite` |
| Natural IMAGE | Charter: streamed MAINMENU @ EE but **imgBytes=0** / prims=1 without assist; multi-prim IMAGE residual open |
| Stream | CODE+MAINMENU(+MAINSKY) force-stream live — **not R0** for asset bind |
| DISPFB | Residual seat lights naturalDispfb after assist; pre-assist dispfbPx=0 |
| Secondary | ofx expand carries title FB (px=286720 strip class); multi-prim + DISPFB after game IMAGE |

### 4.6 Burnout 3 — primary **R3**

| Field | Evidence |
|-------|----------|
| PRESENT assist | No Dec-class gameart Host→Local plant; Soft-GS merge composite FRAME/FBP0 when DISPFB unset (GX-041 / design A4) |
| Natural IMAGE | Strong: claim-class `imgBytes` multi-MiB, gifP2 + gifP3 live, prims hundreds–thousands |
| DISPFB | Charter: **DISPFB1=0** forever on logo-frontend path; `naturalDispfb` circuit 0 (no plant); residualDispfb / Frame fill dominate mixed scoreboard rows |
| Secondary | Flip-queue / CreditOwedHandlerCall = **M5-a** DMA IRQ INFRA (may starve later IMAGE if pending stuck) — document dependency, not invent PATH3 |
| A4 | Honest residual FRAME/FBP0 is **allowed**; ban plant DISPFB |

---

## 5. Roll-up for Slice 1 branch

| Primary class | Titles (this inventory) | Default owner |
|---------------|-------------------------|---------------|
| **R1** | Haven, Whip, Dec/DA, BO2 | S8 Path2/3 IMAGE delivery (Slice 2); confirm EE tex bound first |
| **R2** | GoW | S10 local page / composite preference (Slice 3); S8 if natural IMAGE tags stay 0 |
| **R3** | B3 | S10 document A4 residual; optional natural DISPFB only if retail writes it |
| **R0** | *(none primary)* — Haven fleet-50M only as budget artifact | media / decompress / SIF (non-M7-a) |
| **R4** | *(secondary only)* GoW expand stamp; DA lit≪nat | S10 present refresh / expand order (after R1/R2) |
| **R5** | *(none primary on this six)* | matrix soak titles if inventory later shows held Path3 IMAGE starve |

**Assist policy (unchanged):** residual Host→Local stays **on** until A3. Do not count assist-only `imgBytes` as G-GFX-3 pass without split (design open Q2).

---

## 6. Metrics to re-scrape at Slice 1 (claim budget, SEMA_OFF preferred)

Per title @ claim (design §4.2 Slice 0):

```text
gifP2 / gifP3
gif packets completed / aborted
TagsCompletedImage / gif-tags image=
imgBytes
naturalDispfb / enNaturalDispfb
dispfbPx / naturalDispfbPx / residualDispfbPx
LastCompositeSource / compositeSource
expandHits
Path3MaskedByVif / held Path3 (if available)
LastImage* (if scraped)
```

Optional: `DETPS2_TRACE_GIF=1` for TRXDIR / BITBLTBUF / DPSM on GoW, Dec/DA, Whip.

Re-run classifier (design §4.4 pseudocode) if claim lines disagree with this static table — update this file append-only or replace primary labels with dated rows.

---

## 7. Explicit non-actions (A0)

| Do | Do not |
|----|--------|
| Label residual class from existing walls | Change Core / Soft-GS / GIF behavior |
| Point S8 vs S10 ownership | Edit `GameQuirks/*` or RealSifRpc |
| Keep PRESENT assists documented | Invent PATH3 / plant DISPFB / FFmpeg logos |
| Note M5-a dependency where IRQ may block IMAGE chains | Treat assist Host→Local as natural MENU YES |
| Document B3 R3 as A4-honest residual | Force fake DISPFB on B3 |

---

## 8. Source map

| Artifact | Path |
|----------|------|
| Design (taxonomy + A0 gate) | `docs/infra-audits/m7a-path23-image-dispfb-design.md` |
| Seed | `docs/infra-audits/m7a-path23-image-dispfb-seed.md` |
| Debt §6 PRESENT | `docs/infra-audits/gamequirks-infra-debt.md` |
| Residual scoreboard seat | `docs/title-ports/SCOREBOARD.md` |
| Title charters | `docs/title-ports/{GOD_OF_WAR,HAVEN,WHIPLASH,MK_DECEPTION,MK_DEADLY_ALLIANCE,BLOOD_OMEN_2,BURNOUT_3}.md` |
| Discovery walls | `docs/graphics/DISCOVERY_LOG.md` |
| PATH2 / PATH3 / expand | `docs/graphics/{PATH2_STICKY_W11C,PATH3_MASK_MATRIX,EXPAND_POLICY}.md` |
| Scoreboard schema | `tools/SCOREBOARD_SCHEMA.md` |

---

*A0 complete. Prefer shared Path/IMAGE/DISPFB work that silences Host→Local residual under env-off over growing assist chrome. Correct black + honest residual beats pretty lie.*
