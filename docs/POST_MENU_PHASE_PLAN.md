# Post–MENU YES phase plan — commercial playability campaign

**Status:** ACTIVE (supersedes “hit menu on 9” as the north-star)  
**Tip anchor:** `d64cb85` / `649846b` — **MENU YES 9/9** Soft-GS (SEMA_STALL_YIELD OFF)  
**Epic:** [#12](https://github.com/RazmanianDVL/DetPS2/issues/12)  
**Doctrine:** [CORRECTNESS.md](CORRECTNESS.md) · [AGENT_SOP.md](AGENT_SOP.md) · Soft-GS truth only  
**Prior plan (still in force for IRX core):** [IRX_EXECUTION_PHASE_PLAN.md](IRX_EXECUTION_PHASE_PLAN.md)

---

## 0. Executive summary

### What we just achieved

| Gate | Result |
|------|--------|
| Commercial fleet Soft-GS **MENU YES** | **9/9** (scoreboard `menuKind` bar) |
| Shared Soft-GS infrastructure | Mul80/AFAIL, XYZ2/XYZ3 kick map, merge composite, Path2 sticky GIF, ofx title-strip expand |
| IRX floor | BIOS IOPBTCONF + StartLoadedModule path live; residual proprietary disc IRX still HLE-adjacent |

### What MENU YES is *not*

- Not “fully playable.” Most titles are **logo / title-surface / keep-alive menu loop**, often with **assist-gated** chrome, **expand strips**, or **soft-complete** paths.
- Not IRX purity. Large `GameQuirks` + RealSifRpc title bridges remain.
- Not natural DISPFB/IMAGE/texture DMA on all titles.
- Not pad-driven **New Game → first room**.

### North-star for this plan

```text
MENU YES (done)  →  INTERACTIVE MENU  →  FIRST GAMEPLAY  →  IRX-DEBT DEMOTED  →  FREE-RIDE TITLES
```

**Stop saying “behind” when:**

| Gate | Criteria |
|------|----------|
| **P1** | **INTERACTIVE-MENU 9/9** — pad changes selection / advances past logo without thrash; Soft-GS still real |
| **P2** | **FIRST-GAMEPLAY ≥3 titles** — New Game / Start loads level or first interactive 3D/2D surface (not logo) |
| **P3** | **NATURAL-DRAW ≥5 titles** — prims/textures from game GIF DMA without title-local Soft-GS PATH3 plants / ofx expand crutches where retail already programs XYOFFSET |
| **P4** | **IRX-FILEIO ≥3 titles** — open+read of a real game file through **executing** FILEIO IRX (not only host RealSifRpc bridges) |
| **P5** | **GameQuirk LOC ↓ ≥30%** vs tip `d64cb85` without MENU regression |
| **P6** | **Determinism** — claim tape hash stable @100M for 3 titles under SEMA_OFF |
| **P7** | **Free-ride title** — one new serial hits INTERACTIVE-MENU with **no new** GameQuirk module (shared path only) |

---

## 1. Absolute freezes (orchestrator enforces)

1. **Soft-GS truth only** — no FFmpeg / host synthetic logos / plant FB pixels as “MENU.”  
2. **SEMA_STALL_YIELD OFF** for claims unless a ticketed experiment.  
3. **WaitSema fabricate** stays **Whiplash-gated only** (or title-local with proof it does not starve GoW/Dec/DA).  
4. **Dmac Path3MaskedByVif** + high-TADR END gates stay unless multi-title soak proves a safer rule.  
5. **SearchFile copy-back gate** (SM ELF poison) stays.  
6. **StartThread resume** stays classic PC+4 globally; Haven ExitThread fall-through via SonyKernelHle SP restore / title assist — **not** broad `$ra` resume.  
7. No new multi-title plant waves without T0 approval; prefer **global** GS/GIF/VIF/SIF/CDVD.  
8. Agents use **isolated worktrees**; T0 merges, builds, scoreboard, push, #12.

---

## 2. Scoreboard evolution

### 2.1 Bars (tools + docs)

Extend `tools/scoreboard-fleet.json` + `tools/scoreboard.ps1` with **tier columns** (not only MENU):

| Tier | Name | Heuristic (Soft-GS + runtime) |
|------|------|-------------------------------|
| **T0** | BOOT | ELF entry, no Exit(1), cdvd>0 or IRX spine |
| **T1** | MENU | Existing `menuKind` YES (current 9/9) |
| **T2** | INTERACTIVE | pad inject changes PC/state **or** selection index **or** gif/prims increase after pad |
| **T3** | FRONTEND | multi-prim + imgBytes>0 or dispfbPx>0 **or** prims≥10 with texture path |
| **T4** | GAMEPLAY | post–New Game load: new stream open + Soft-GS scene change (title-specific PC band optional) |
| **T5** | NATURAL | no soft-success plant hit in claim window (telemetry counters) |

### 2.2 Claim budgets

| Budget | Cycles | Use |
|--------|--------|-----|
| diagnose | 20M | Wall find |
| verify | 50M | Fix moved wall |
| claim | 100M | T1–T3 asserts |
| deep | 500M | T4 gameplay only |

### 2.3 Fleet (9 + optional 10th)

Keep current 9. Optional free-ride candidate already in fleet JSON: **Shadow of the Colossus** (`SCUS_974.72`) — do not block P1–P3 on it.

---

## 3. Residual truth table (start of plan)

| Title | T1 MENU | Primary residual wall | Debt class |
|-------|---------|----------------------|------------|
| **SM** | YES | Assist PATH3 second chrome; natural texture DMA; AnimMenuGUI accept | MidwayBootAssist |
| **B3** | YES | DISPFB unset; pad main-menu advance past logo; natural FRONTEND dest | Burnout3Assist |
| **BO2** | YES | Multi-prim IMAGE/DISPFB chrome; list soft-stubs | BloodOmen2SnAssist |
| **GoW** | YES | IRX-only cdvd class often; Fedo shell decode→PRIM; ofx expand crutch; post-type2 queue empty | GodOfWarAssist + shared Gif |
| **Dec** | YES | No GIF IMAGE menu textures; path-hash publish bridges | MidwayFamilyAssist |
| **DA** | YES | Fail-tail plants keep-alive; richer chrome | MidwayFamilyAssist |
| **Vexx** | YES | More TRE members / richer frontend; SearchFile vexx-packet exception | VexxAssist |
| **Whip** | YES | Full texture ring path; WaitSema WHIP fabricate residual | WhiplashAssist |
| **Haven** | YES | IMAGE residual; JREXIT/SP path fragile | TeamIcoAssist + KernelHle |

**Shared residual (multi-title):**

| Area | Wall |
|------|------|
| GIF/VIF Path2 | Sticky reassembly shipped; garbage DIRECT abort residual |
| Soft-GS | ofx=0 / 0x8000 title-strip expand used by Whip/BO2/GoW — promote to policy + document; reduce when retail XYOFFSET armed |
| PATH3 | Path3MaskedByVif — need measured unmask policy per title class |
| FILEIO | RealSifRpc bridges dominate; executing FILEIO IRX incomplete for commercial packs |
| IOPRP | Disc proprietary packs skip HLE-owned StartLoadedModule |
| Pad | Dense pad exists; selection→accept unproven on several titles |

---

## 4. Phase blocks (WP-PM-00 … WP-PM-39)

Each WP: **ID · Tracks · Depends · Deliverable · Exit test · Est**

### Block A — Hygiene & measurement (WP-PM-00 … WP-PM-04)

| ID | Track | Depends | Deliverable | Exit test | Est |
|----|-------|---------|-------------|-----------|-----|
| **PM-00** | T0 | — | Freeze this plan; update `NEXT_PLAN.md`, SCOREBOARD header, COMPATIBILITY.md “MENU 9/9” | #12 comment + docs on main | 0.5d |
| **PM-01** | T10 | PM-00 | Scoreboard **T1–T5** columns + JSON claim schema | `scoreboard.ps1 -Budget diagnose` emits tiers | 1d |
| **PM-02** | T10 | PM-01 | Per-title residual tickets (or #12 subtasks) with **one wall each** | 9 open residual issues linked | 0.5d |
| **PM-03** | T9 | PM-00 | Soft-GS **policy doc**: when title-strip expand is legal; telemetry for expand hits | doc + counter in claim | 0.5d |
| **PM-04** | T0 | PM-01 | Nightly/local **fleet claim matrix** script (9×100M optional CI-lite) | one full matrix log in `out/traces/` | 1d |

### Block B — Interactive menu (WP-PM-05 … WP-PM-14) → **P1**

Goal: **T2 INTERACTIVE** on all 9 without inventing Soft-GS.

| ID | Track | Title focus | Deliverable | Exit test |
|----|-------|-------------|-------------|-----------|
| **PM-05** | T6 + title | SM | Stable sel-idx + pad accept without type5 | T2 @100M SM |
| **PM-06** | T6 + title | B3 | Pad advances logo→frontend state | T2 @100M B3 |
| **PM-07** | T6 + title | BO2 | Pad / list advance past title FB | T2 @100M BO2 |
| **PM-08** | T6 + title | GoW | Pad after FRAME+px; no Exit | T2 @100M GoW |
| **PM-09** | T6 + title | Dec | Idle pump + pad; PowerOff storm stays dead | T2 @100M Dec |
| **PM-10** | T6 + title | DA | Keep-alive + pad selection | T2 @100M DA |
| **PM-11** | T6 + title | Vexx | Pad on title-surface | T2 @100M Vexx |
| **PM-12** | T6 + title | Whip | Pad on title; WHIP WaitSema only | T2 @100M Whip |
| **PM-13** | T6 + title | Haven | Pad + no JREXIT death | T2 @100M Haven |
| **PM-14** | T0 | all | Merge wave; scoreboard T2 count | **P1: INTERACTIVE 9/9** |

**Parallelism:** 9 title agents + T0 (same model as menu campaign). Shared pad/kernel only via T6/T7 with soak.

### Block C — Frontend quality / natural draw (WP-PM-15 … WP-PM-24) → **P3 partial**

| ID | Track | Deliverable | Exit test |
|----|-------|-------------|-----------|
| **PM-15** | T9 | Natural DISPFB bind path for B3/BO2 (no invent) | dispfbPx>0 without composite-only on ≥1 title |
| **PM-16** | T9 | IMAGE / BITBLT host→local fidelity (Haven + Dec textures) | imgBytes>0 on claim |
| **PM-17** | T9 | Reduce ofx expand hits when retail XYOFFSET armed (GoW/Whip) | expand counter ↓; px still ≥ title floor |
| **PM-18** | T4/T5 | Midway gameart.ssf → GIF IMAGE (Dec/DA/SM) | textured chrome prims↑ |
| **PM-19** | T5 | Vexx TRE member VFS completeness | cdvd↑ + prims↑ |
| **PM-20** | T5 | GoW Fedo R_SHELL decode consumer (natural PRIM after type-2) | cdvd>IRX-only; gifP3↑ without invent |
| **PM-21** | T9 | PATH3 unmask policy matrix (when Path3MaskedByVif can clear) | multi-title soak green |
| **PM-22** | T4 | SearchFile / CdlFILE copy-back generalized (SM gate + Vexx packet) | single code path; both titles hold MENU |
| **PM-23** | T9 | Soft-GS smoke suite for Path2 sticky + ofx expand | all smokes pass |
| **PM-24** | T0 | Scoreboard T3 FRONTEND ≥5/9 | **partial P3** |

### Block D — First gameplay (WP-PM-25 … WP-PM-31) → **P2**

Pick **3 titles** with best stream spine (recommended order):

1. **Burnout 3** — FRONTEND already heavy; pad + race load  
2. **MK Deadly Alliance / Deception** — Midway menu loop solid  
3. **Vexx or Whiplash** — stream maps live  

| ID | Track | Deliverable | Exit test |
|----|-------|-------------|-----------|
| **PM-25** | T0 | Choose 3 gameplay targets; write load-gate criteria | design note on #12 |
| **PM-26** | title+T5 | B3 New Game / event load Soft-GS scene change | T4 B3 |
| **PM-27** | title+T5 | Midway (DA or Dec) fight/select → match surface | T4 Midway |
| **PM-28** | title+T5 | Vexx or Whip first level/title-to-game | T4 |
| **PM-29** | T8 | Disc IOPRP residual for chosen 3 (less RealSifRpc) | FILEIO via IRX where possible |
| **PM-30** | T7 | WaitSema / thread health under gameplay load | no thrash > baseline |
| **PM-31** | T0 | **P2: FIRST-GAMEPLAY ≥3** | scoreboard T4 ≥3 |

### Block E — IRX debt demolition (WP-PM-32 … WP-PM-36) → **P4 + P5**

Align with IRX plan Blocks D–G; **do not regress T1**.

| ID | Track | Deliverable | Exit test |
|----|-------|-------------|-----------|
| **PM-32** | T2/T3/T8 | Disc IOPRP StartLoadedModule demotion matrix | G2 residual list updated |
| **PM-33** | T4/T5 | FILEIO open+read through executing IRX for ≥3 titles | **P4** |
| **PM-34** | T6 | PADMAN path through IRX for ≥1 title | pad OPEN IRX |
| **PM-35** | T10 | GameQuirk LOC audit + delete dead soft-success | **P5 ≥30% LOC↓** or issue exceptions |
| **PM-36** | T1/T4 | SIF handshake hygiene (no per-module MSFLAG plant) | soak 9 titles T1 hold |

### Block F — Free-ride + determinism (WP-PM-37 … WP-PM-39) → **P6 + P7**

| ID | Track | Deliverable | Exit test |
|----|-------|-------------|-----------|
| **PM-37** | T10 | Tape replay hash @100M for SM, B3, DA | **P6** |
| **PM-38** | T0+shared | SotC or new serial: INTERACTIVE without new GameQuirk | **P7** |
| **PM-39** | T0 | Version bump policy: v0.2.0 “Commercial menus” release notes | RELEASE_NOTES + tag |

---

## 5. Agent tracks (post-menu)

Same 10-track model; **title fan-out** overlays tracks for Blocks B–D.

| Track | Role | Owned areas |
|-------|------|-------------|
| **T0** | Orchestrator | merge, smoke, push, #12, wiki, scoreboard, this plan |
| **T1** | IOP core | `Iop.cs`, exceptions |
| **T2** | IRX loader | `IrxLoader.cs` |
| **T3** | BIOS boot | `BiosBootHost`, ROMDIR |
| **T4** | SIF / RealSifRpc shared | `Sif*.cs`, shared RealSifRpc only |
| **T5** | CDVD / packs | `Cdvd.cs`, TRE/Fedo/WAD host helpers if shared |
| **T6** | Pad / SIO2 | `PadInput`, `Sio2`, pad density policy |
| **T7** | EE kernel | `KernelHle`, `SonyKernelHle` |
| **T8** | Disc IOPRP | `IopExtendedBiosHost` |
| **T9** | Soft-GS / GIF / VIF / DMAC | `Gs`, `Gif`, `Vif`, `Dmac` |
| **T10** | Debt + tooling | GameQuirk strip, scoreboard, traces |
| **Title agents** | One worktree per serial | `GameQuirks/*Assist`, title docs only |

**Fan-out:** default **9 title agents** for Block B; **3 gameplay agents** for Block D; **shared infra agents** for Block C/E never edit title assists without handoff.

---

## 6. Parallel waves (example)

| Wave | Parallel agents | Goal |
|------|-----------------|------|
| **W0** | T0 + T10 | PM-00…04 measurement |
| **W1** | 9 title (PM-05…13) | INTERACTIVE |
| **W2** | T0 merge + claim fleet | P1 |
| **W3** | T9 + T4 + 3 title (PM-15…20) | FRONTEND natural |
| **W4** | 3 gameplay | FIRST-GAMEPLAY |
| **W5** | T2/T4/T8/T10 | IRX debt |
| **W6** | free-ride + determinism | P6/P7 + v0.2.0 |

---

## 7. Merge / quality gates (T0 checklist)

Every merge wave:

1. `dotnet build` Release clean  
2. Smoke suite (incl. new Soft-GS Path2 / ofx tests)  
3. No conflict markers; TITLE_HACKS + SCOREBOARD coherent  
4. Spot claim: at least **3 titles** that touch shared files still T1  
5. Push `main`; #12 progress table  
6. Never force-push `main`

Regression kill-list (known cross-title landmines):

| Change class | Breaks | Guard |
|--------------|--------|-------|
| Global WaitSema fabricate | GoW/Dec/DA | WHIP-only |
| Ungate Path3 early | Dec/GoW history | Path3MaskedByVif |
| SearchFile copy-back broad | SM ELF | size + ee range + !code |
| StartThread `$ra` resume | GoW boot | classic PC+4 |
| sm+0x28 size plant | SM jalr | banned |
| Fake warm sector credit | BO2 stream | banned |

---

## 8. Definition of done (this plan)

**Plan complete when P1–P7 all green** (or explicitly deferred with issue + owner).

Minimum shippable intermediate: **P1 + P2 + PM-39 (v0.2.0 notes)** even if P5 incomplete.

---

## 9. Out of scope (defer)

- Full VU micro accuracy / hardware GS  
- Full SPU2 ADPCM commercial audio  
- Netplay cert on commercial tapes  
- Complete IRX purity (G7 of IRX plan) — **tracked in PM-35**, not blocking P1  
- Multi-disc / progressive scan / 16:9  
- Committing BIOS/ISO  

---

## 10. First actions (this week)

1. **T0:** land this file + `NEXT_PLAN.md` pointer; refresh COMPATIBILITY.md MENU 9/9.  
2. **T10:** implement scoreboard T1–T5 schema (PM-01).  
3. **9 agents:** Block B INTERACTIVE wave (PM-05…13) on tip `d64cb85+`.  
4. **T9:** Soft-GS expand policy + GoW ofx residual plan (PM-03, PM-17).  
5. **Do not** start new title GameQuirks for free-ride until P1 green.

---

## 11. References

| Doc | Role |
|-----|------|
| [title-ports/SCOREBOARD.md](title-ports/SCOREBOARD.md) | Fleet MENU table |
| [IRX_EXECUTION_PHASE_PLAN.md](IRX_EXECUTION_PHASE_PLAN.md) | IRX WPs / G1–G7 |
| [AGENT_SOP.md](AGENT_SOP.md) | Budgets, oracle order |
| [CORRECTNESS.md](CORRECTNESS.md) | No cheats |
| [TITLE_HACKS.md](TITLE_HACKS.md) | Per-title debt log |
| [PLAY_HLE_ORACLE.md](PLAY_HLE_ORACLE.md) | Play! / PCSX2+PINE |
| Epic #12 | Progress comments |

---

*Plan authored post–MENU YES 9/9 campaign. Orchestrator-owned; revise only via T0 merge to main.*
