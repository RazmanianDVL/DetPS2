# Post–MENU YES mega phase plan — 10-subagent orchestration

**Status:** ACTIVE — primary commercial plan after MENU YES 9/9  
**Tip anchor:** `45debf9` / `d64cb85` / `649846b` (MENU YES Soft-GS, SEMA_STALL_YIELD OFF)  
**Epic:** [#12](https://github.com/RazmanianDVL/DetPS2/issues/12)  
**Doctrine:** [CORRECTNESS.md](CORRECTNESS.md) · [AGENT_SOP.md](AGENT_SOP.md) · Soft-GS truth only  
**Sibling plan (IOP purity):** [IRX_EXECUTION_PHASE_PLAN.md](IRX_EXECUTION_PHASE_PLAN.md)  
**Orchestration:** **T0 (orchestrator) + 10 concurrent work subagents at all times**

---

## 0. Executive summary

### 0.1 What we achieved

| Gate | Result |
|------|--------|
| Soft-GS **MENU YES** fleet | **9/9** scoreboard `menuKind` bars |
| Shared Soft-GS | Mul80/AFAIL, XYZ2/3 kick, merge composite, Path2 sticky GIF, ofx title-strip expand |
| IRX floor | BIOS IOPBTCONF + StartLoadedModule; disc proprietary residual HLE-adjacent |
| Campaign model | Proven: isolated worktrees, T0 merge/smoke/push, 9-title waves |

### 0.2 What MENU YES is not

- Not fully playable (logo / title-surface / keep-alive loops dominate)
- Not IRX-pure (large `GameQuirks` + RealSifRpc bridges)
- Not natural DISPFB / IMAGE / texture DMA everywhere
- Not pad **New Game → first room**
- Not free-ride of new serials without assists

### 0.3 Dual-stack north-star

```text
STACK PLAY  (this plan)   MENU → INTERACTIVE → FRONTEND → GAMEPLAY → SOAK → FREE-RIDE
STACK IRX   (sibling)     G1 exec → G2 IOPRP → G3 FILEIO → G4 PAD → G5 surface → G6 det → G7 debt
                    └── Block E/F of this plan couple the stacks
```

**Stop saying “behind” when P1–P12 green** (see §1).

### 0.4 Scale of this plan

| Dimension | Size |
|-----------|------|
| Work packages | **WP-PL-000 … WP-PL-099** (100 packages) |
| Seasons | **S0 … S9** (10 seasons) |
| Concurrent agents | **10 always** (seats S1–S10) + **T0** |
| Primary fleet | **9 titles** + free-ride slot (SotC / next) |
| Calendar budget | **~20–28 weeks** wall-clock if 10 agents stay saturated (optimistic); **~6 months** realistic with soak/regress |

---

## 1. Product gates (P1–P12)

| Gate | Name | Criteria (Soft-GS truth, SEMA_OFF) |
|------|------|-------------------------------------|
| **P0** | MENU floor | 9/9 MENU YES — **DONE** |
| **P1** | INTERACTIVE 9/9 | Pad inject changes selection index **or** advances logo/state **or** increases prims/gif after pad @100M |
| **P2** | FRONTEND 9/9 | T3: prims≥10 **or** imgBytes>0 **or** dispfbPx>0 **or** multi-chrome gifP3≥20 |
| **P3** | NATURAL 6/9 | No title-local Soft-GS PATH3 plant / ofx expand hit in claim window (telemetry) **or** retail XYOFFSET armed + expand_hits=0 |
| **P4** | FIRST-GAMEPLAY ≥3 | New Game/Start → Soft-GS scene change + new stream open (title charter) |
| **P5** | FIRST-GAMEPLAY ≥6 | Same bar, six titles |
| **P6** | PLAYABLE-SLICE ≥1 | 60s wall @ deep budget: no Exit, Soft-GS alive, pad moves character/vehicle/cursor in-game |
| **P7** | IRX-FILEIO ≥3 | Real game file open+read through **executing** FILEIO IRX |
| **P8** | IRX-PAD ≥2 | Pad OPEN through executing PADMAN (or equivalent IRX) |
| **P9** | GameQuirk LOC ↓ ≥40% | vs tip `d64cb85` without P0/P1 regression |
| **P10** | Determinism | Tape hash stable @100M for 5 titles |
| **P11** | Free-ride ×2 | Two new serials reach INTERACTIVE with **no new** GameQuirk module |
| **P12** | Release | v0.2.0 “Commercial menus + interactive” notes + fleet matrix artifact |

**Minimum shippable intermediate:** P1 + P4 + P12 notes.  
**Plan complete:** P1–P12 all green or deferred with owner + issue.

---

## 2. Absolute freezes (T0 enforces every merge)

1. Soft-GS truth only — no FFmpeg / host synthetic logos / planted FB as MENU.  
2. `DETPS2_SEMA_STALL_YIELD` **OFF** for claims unless ticketed experiment.  
3. WaitSema fabricate **Whiplash-gated only** (or proven title-local; never global).  
4. **Path3MaskedByVif** + high-TADR END gates unless multi-title soak replaces them.  
5. **SearchFile copy-back gate** (SM ELF poison) stays.  
6. **StartThread** classic PC+4 globally — never broad `$ra` resume (broke GoW).  
7. No `sm+0x28` size plant; no type5 synthetic SM objects as strategy.  
8. No fake warm sector credit (BO2).  
9. Agents **only** edit owned seat files unless handoff ticket.  
10. **Isolated worktrees**; no force-push `main`; T0 merges/smokes/pushes/#12.

### Cross-title kill-list (regression memory)

| Change class | Historical break | Guard |
|--------------|------------------|-------|
| Global WaitSema fabricate | GoW/Dec/DA starve | WHIP-only |
| Early Path3 ungating | Dec/GoW | Path3MaskedByVif |
| Broad SearchFile copy-back | SM ELF wipe | size + ee range + !IsLikelyEeCode |
| StartThread `$ra` resume | GoW boot death | classic PC+4 |
| sm+0x28 plant | SM jalr poison | banned |
| Per-module MSFLAG plant | SM WAD regress | plant once at Reset only |
| Dmac END ADDR=0 always | B3 STG / DA | high-TADR / GIF gate |

---

## 3. Ten permanent agent seats (always 10 subagents)

**T0 = orchestrator (you).** Not a seat.  
**Seats S1–S10 = 10 concurrent general-purpose subagents.** Each has:

- Fixed **worktree** pattern `detps2-seat-{id}`  
- Fixed **branch** pattern `agent/seat-{id}/{wave}`  
- Fixed **file ownership**  
- A **backlog queue** (never idle — pull next WP or soak)

### 3.1 Seat roster

| Seat | Codename | Primary ownership | Owned paths (write) | Forbidden |
|------|----------|-------------------|---------------------|-----------|
| **S1** | **MIDWAY-SM** | MK Shaolin Monks | `MidwayBootAssist.cs`, `docs/title-ports/MK_SHAOLIN_MONKS.md` | GoW assist, Gs.cs without handoff |
| **S2** | **MIDWAY-DEC** | MK Deception | `MidwayFamilyAssist.cs` **Dec-only regions***, Dec docs | SM MidwayBootAssist |
| **S3** | **MIDWAY-DA** | MK Deadly Alliance | `MidwayFamilyAssist.cs` **DA-only regions***, DA docs | Dec-only blocks |
| **S4** | **BURNOUT** | Burnout 3 | `Burnout3Assist.cs`, B3 docs | Midway assists |
| **S5** | **BO2** | Blood Omen 2 | `BloodOmen2SnAssist.cs`, BO2 docs | GoW assist |
| **S6** | **GOW** | God of War | `GodOfWarAssist.cs`, GoW docs | Midway, Whip WaitSema globalize |
| **S7** | **VEXX** | Vexx (+ TRE/VFS shared if seat-owned helper) | `VexxAssist.cs`, Vexx docs | RealSifRpc bulk without handoff |
| **S8** | **WHIP** | Whiplash | `WhiplashAssist.cs`, Whip docs | Global WaitSema |
| **S9** | **HAVEN** | Haven (+ Team Ico class) | `TeamIcoAssist.cs`, Haven docs | KernelHle StartThread `$ra` global |
| **S10** | **PLATFORM** | Shared platform (rotating focus) | See §3.2 sub-lanes | Title GameQuirks |

\*S2/S3 co-own `MidwayFamilyAssist.cs`: **file lock protocol** — only one of S2/S3 may have open merge PR at a time; other works docs/traces or waits T0 merge. Prefer **region comments** `// REGION: DEC` / `// REGION: DA`.

### 3.2 Seat S10 — PLATFORM sub-lanes (always one active focus)

S10 is the **shared infra** agent. T0 assigns **exactly one sub-lane** per wave so S10 never thrash-edits the world:

| Sub-lane | Focus files | Typical WPs |
|----------|-------------|-------------|
| **S10-GS** | `Gs.cs`, `GsRegisters.cs`, Soft-GS present | PL-020…, PL-040… |
| **S10-GIF** | `Gif.cs`, `Vif.cs`, `Dmac.cs` | PL-021…, Path2 sticky residual |
| **S10-SIF** | `Sif.cs`, `SifRpc.cs`, shared `RealSifRpc.cs` | PL-030…, SearchFile gates |
| **S10-IOP** | `Iop.cs`, `IrxLoader.cs`, `IopExtendedBiosHost.cs` | IRX couple PL-070… |
| **S10-EE** | `KernelHle.cs`, `SonyKernelHle.cs`, `Ps2System.cs` hot slices | PL-035… |
| **S10-PAD** | `PadInput.cs`, `Sio2.cs` | PL-050… |
| **S10-CDVD** | `Cdvd.cs`, ISO | PL-031… |
| **S10-TOOL** | `tools/scoreboard*.ps1`, fleet JSON, smoke | PL-001… |
| **S10-DEBT** | delete dead soft-success; TITLE_HACKS hygiene | PL-080… |

When S10 needs a second shared lane, T0 **parks one title seat** (lowest priority residual) for that wave only — **still 10 agents**, seat reassigned.

### 3.3 Overflow: free-ride seat

When a title seat hits **idle backlog** (all of its WPs in current season done):

1. Pull **soak/regress** WPs for own title  
2. Or take **free-ride serial** (SotC) under **no new GameQuirk** rule  
3. Or assist S10 as **read-only disasm / PCSX2 oracle** (docs only)

### 3.4 Agent spawn contract (every subagent prompt must include)

```text
SEAT: S{n} {CODENAME}
WORKTREE: C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-seat-s{n}
BRANCH: agent/seat-s{n}/{season}-{wave}
TIP BASE: {main sha}
OWNED FILES: {list}
FORBIDDEN: {list}
CURRENT WP: WP-PL-{nnn}
EXIT TEST: {one paragraph}
FREEZES: Soft-GS truth; SEMA_OFF; no FFmpeg; no global WaitSema fabricate; ...
BUDGETS: diagnose 20M → verify 50M → claim 100M (deep 500M only if WP says)
DELIVERABLE: commit + push branch; final claim table line; residual if not exit
```

### 3.5 T0 orchestrator loop (never stop)

```text
loop forever until P12:
  1. Ensure 10 seats have live subagents (respawn if completed)
  2. Collect finished branches → merge order by risk (platform first if shared, then titles)
  3. Build + smoke + spot-claim 3 titles if shared files touched
  4. Push main; update SCOREBOARD + #12
  5. Assign next WP from season backlog to freed seats
  6. If conflict thrash → file-lock protocol; serialize S2/S3 and S10
```

---

## 4. Scoreboard tiers (measurement)

Extend `tools/scoreboard-fleet.json` + `scoreboard.ps1`:

| Tier | Code | Heuristic |
|------|------|-----------|
| Boot | T0 | ELF entry; no early Exit; spine live |
| Menu | T1 | Existing `menuKind` YES — **9/9 done** |
| Interactive | T2 | Pad effect on state/PC/sel-idx/prims |
| Frontend | T3 | Richer Soft-GS (prims/img/dispfb/gifP3 bars) |
| Natural | T4 | No expand plant / no assist PATH3 in window |
| Gameplay | T5 | Scene change post–New Game + stream |
| Playable-slice | T6 | Deep budget alive + in-game pad |
| IRX-honest | T7 | FILEIO/PAD via IRX flags in telemetry |

**Claim budgets:** diagnose 20M · verify 50M · claim 100M · deep 500M · soak 2B (optional).

---

## 5. Residual truth table (S0 baseline)

| Title | Seat | T1 | Primary residual | Debt class |
|-------|------|----|------------------|------------|
| SM | S1 | YES | Assist PATH3 chrome; natural texture DMA; pad accept | MidwayBootAssist |
| Dec | S2 | YES | No GIF IMAGE textures; path-hash bridges | MidwayFamilyAssist |
| DA | S3 | YES | Fail-tail keep-alive plants; chrome depth | MidwayFamilyAssist |
| B3 | S4 | YES | DISPFB unset; pad past logo; natural FRONTEND | Burnout3Assist |
| BO2 | S5 | YES | Multi-prim IMAGE; list soft-stubs | BloodOmen2SnAssist |
| GoW | S6 | YES | Fedo decode→PRIM; IRX-only cdvd class; ofx expand | GodOfWarAssist |
| Vexx | S7 | YES | TRE members incomplete; richer frontend | VexxAssist |
| Whip | S8 | YES | Texture ring; WHIP WaitSema fabricate | WhiplashAssist |
| Haven | S9 | YES | IMAGE residual; CallRpc SP fragility | TeamIcoAssist |
| SotC | free-ride | NO | KERNEL.XFF residual historically | none preferred |

**Shared residual (S10):** Path2 garbage DIRECT abort; ofx expand policy; PATH3 unmask matrix; SearchFile dual gate (SM+Vexx); FILEIO IRX; disc IOPRP demotion.

---

## 6. Seasons overview (S0–S9)

| Season | Theme | Primary gates | Default seat mode |
|--------|-------|---------------|-------------------|
| **S0** | Foundation & telemetry | measure | All 10: tooling + residual charters |
| **S1** | INTERACTIVE pad | **P1** | 9 title + S10-PAD |
| **S2** | FRONTEND chrome | **P2** | 9 title + S10-GS/GIF |
| **S3** | NATURAL draw | **P3** | title + S10-GS (reduce plants) |
| **S4** | FIRST-GAMEPLAY wave A | **P4** | 3 gameplay + 6 soak + S10 stream |
| **S5** | FIRST-GAMEPLAY wave B | **P5** | 3 more gameplay + soak |
| **S6** | PLAYABLE-SLICE | **P6** | 1–2 deep + 8 hold MENU |
| **S7** | IRX couple FILEIO/PAD | **P7–P8** | S10-IOP/SIF + 3 title pilots |
| **S8** | Debt demolition | **P9** | S10-DEBT + title delete plants |
| **S9** | Determinism + free-ride + release | **P10–P12** | matrix + SotC×2 + notes |

Each season has **≥8 WPs** and **≥2 merge trains**. Seats never idle: if season WP blocked, run **soak claims** or **oracle notes**.

---

## 7. Work package catalog (WP-PL-000 … WP-PL-099)

Each WP: **ID · Seat(s) · Depends · Deliverable · Exit test · Est**

### Season S0 — Foundation (PL-000 … PL-009)

| ID | Seat | Depends | Deliverable | Exit test | Est |
|----|------|---------|-------------|-----------|-----|
| **PL-000** | T0 | — | Land this plan; NEXT_PLAN pointer; #12 | docs on main | 0.25d |
| **PL-001** | S10-TOOL | PL-000 | Scoreboard T0–T7 columns + JSON schema | `scoreboard.ps1 -Budget diagnose` emits tiers | 1.5d |
| **PL-002** | S10-TOOL | PL-001 | Pad-inject claim mode (`--pad-script`) for T2 | unit + one title demo | 1d |
| **PL-003** | S10-GS | PL-000 | Soft-GS **expand policy** + `expandHits` telemetry | counter in claim output | 1d |
| **PL-004** | S10-TOOL | PL-001 | Fleet matrix script 9×100M → `out/traces/matrix/` | one full matrix | 1d |
| **PL-005** | S1–S9 | PL-000 | Per-title **residual charter** (1 wall, 1 oracle, 1 exit) | 9 docs sections | 0.5d each |
| **PL-006** | S10-DEBT | PL-000 | GameQuirk LOC baseline CSV at `d64cb85` | `docs/debt/QUIRK_LOC_BASELINE.md` | 0.5d |
| **PL-007** | S10-SIF | PL-000 | HLE→IRX matrix refresh commercial SIDs | `docs/irx/HLE_TO_IRX_MATRIX.md` update | 1d |
| **PL-008** | T0 | PL-001 | Worktree seat bootstrap script (10 trees) | `tools/seat-bootstrap.ps1` | 0.5d |
| **PL-009** | T0 | PL-005 | S0 merge train; freeze baseline scoreboard JSON | artifact on main | 0.5d |

**S0 exit:** Measurement live; 10 seats bootstrapped; residual charters exist.

---

### Season S1 — INTERACTIVE (PL-010 … PL-024) → **P1**

| ID | Seat | Depends | Deliverable | Exit test | Est |
|----|------|---------|-------------|-----------|-----|
| **PL-010** | S10-PAD | S0 | Shared pad density policy (no title invent) | doc + PadInput helpers | 1d |
| **PL-011** | S1 | PL-010 | SM sel-idx stable + accept path | T2 SM @100M | 2–3d |
| **PL-012** | S2 | PL-010 | Dec pad on idle-pump menu | T2 Dec | 2d |
| **PL-013** | S3 | PL-010 | DA pad selection keep-alive | T2 DA | 2d |
| **PL-014** | S4 | PL-010 | B3 pad logo→frontend advance | T2 B3 | 2–3d |
| **PL-015** | S5 | PL-010 | BO2 pad past title FB | T2 BO2 | 2d |
| **PL-016** | S6 | PL-010 | GoW pad after Soft-GS px | T2 GoW | 2d |
| **PL-017** | S7 | PL-010 | Vexx pad title-surface | T2 Vexx | 1–2d |
| **PL-018** | S8 | PL-010 | Whip pad title; WHIP WaitSema only | T2 Whip | 1–2d |
| **PL-019** | S9 | PL-010 | Haven pad; no JREXIT | T2 Haven | 2d |
| **PL-020** | S10-EE | PL-010 | Kernel pad RPC / sticky pad without thrash | soak 3 titles | 1d |
| **PL-021** | S1 | PL-011 | SM dual-chrome selection UI | T2 hold + prims≥ | 2d |
| **PL-022** | S4 | PL-014 | B3 START/CROSS scripted | pad-script claim | 1d |
| **PL-023** | S6 | PL-016 | GoW thrash PC band escape without killing DMA tags | T2 hold | 2d |
| **PL-024** | T0 | PL-011…019 | Merge train S1; **P1 assert** | INTERACTIVE 9/9 | 1d |

**S1 exit = P1.** Parallelism: **S1–S9 + S10** = 10 agents entire season.

---

### Season S2 — FRONTEND quality (PL-025 … PL-039) → **P2**

| ID | Seat | Depends | Deliverable | Exit test | Est |
|----|------|---------|-------------|-----------|-----|
| **PL-025** | S10-GS | P1 | DISPFB natural bind investigation | design note + smoke | 2d |
| **PL-026** | S4 | PL-025 | B3 DISPFB or documented composite-only | T3 B3 | 2d |
| **PL-027** | S5 | PL-025 | BO2 multi-prim IMAGE | T3 BO2 imgBytes>0 | 3d |
| **PL-028** | S9 | PL-025 | Haven IMAGE chrome | T3 Haven imgBytes>0 | 3d |
| **PL-029** | S2 | P1 | Dec gameart → GIF IMAGE | T3 Dec textures | 3–4d |
| **PL-030** | S3 | P1 | DA textured chrome | T3 DA | 2–3d |
| **PL-031** | S1 | P1 | SM natural texture DMA (reduce assist PATH3) | T3 SM | 3–4d |
| **PL-032** | S7 | P1 | Vexx TRE member completeness | T3 Vexx prims↑ | 3d |
| **PL-033** | S8 | P1 | Whip full texture ring path | T3 Whip | 2–3d |
| **PL-034** | S6 | P1 | GoW richer Soft-GS (more PRIM after Path2 sticky) | T3 GoW px↑ | 3d |
| **PL-035** | S10-GIF | P1 | Path2 garbage DIRECT residual reduce | aborted≤1 hold; smokes | 2d |
| **PL-036** | S10-SIF | P1 | SearchFile single gate (SM+Vexx) | both MENU+T2 hold | 2d |
| **PL-037** | S10-CDVD | P1 | Pack path audit (Fedo/TRE/SSF/WAD) | matrix doc | 2d |
| **PL-038** | S1–S9 | PL-025… | Frontend claim wave | T3 count report | 2d |
| **PL-039** | T0 | PL-038 | **P2 assert** FRONTEND 9/9 | scoreboard | 0.5d |

**S2 exit = P2.**

---

### Season S3 — NATURAL draw (PL-040 … PL-049) → **P3**

| ID | Seat | Depends | Deliverable | Exit test | Est |
|----|------|---------|-------------|-----------|-----|
| **PL-040** | S10-GS | P2 | Expand-hit demotion when XYOFFSET retail armed | expandHits↓ | 2d |
| **PL-041** | S6 | PL-040 | GoW drop ofx expand if retail ofx set | T4 GoW or documented fail | 2d |
| **PL-042** | S8 | PL-040 | Whip expand demotion | T4 Whip attempt | 2d |
| **PL-043** | S5 | PL-040 | BO2 expand demotion | T4 BO2 attempt | 2d |
| **PL-044** | S1 | P2 | SM remove assist PATH3 when natural FBB0 draws | T4 SM attempt | 3d |
| **PL-045** | S2/S3 | P2 | Midway fail-tail plant reduction | T1+T2 hold; LOC↓ | 3d |
| **PL-046** | S10-GIF | P2 | PATH3 unmask policy matrix (safe clear conditions) | soak 9 titles | 3d |
| **PL-047** | S4 | P2 | B3 natural FRONTEND dest bind | plant↓ | 2d |
| **PL-048** | S10-DEBT | PL-044… | Soft-success call-site inventory | CSV | 1d |
| **PL-049** | T0 | PL-040… | **P3 assert** NATURAL ≥6/9 | scoreboard | 0.5d |

**S3 exit = P3.**

---

### Season S4 — FIRST-GAMEPLAY wave A (PL-050 … PL-059) → **P4**

**Targets (fixed):** B3, DA, Vexx (best stream spine).  
**Other seats:** hold T1–T3 + soak + oracle.

| ID | Seat | Depends | Deliverable | Exit test | Est |
|----|------|---------|-------------|-----------|-----|
| **PL-050** | T0 | P2 | Gameplay charters for B3/DA/Vexx (PC bands, streams) | #12 + docs | 0.5d |
| **PL-051** | S4 | PL-050 | B3 New Game / event load Soft-GS scene | T5 B3 | 4–5d |
| **PL-052** | S3 | PL-050 | DA select fighter → match surface | T5 DA | 4–5d |
| **PL-053** | S7 | PL-050 | Vexx title→game first level | T5 Vexx | 4–5d |
| **PL-054** | S10-SIF | PL-050 | Stream open helpers shared (no plant sectors) | used by 051–053 | 2d |
| **PL-055** | S10-CDVD | PL-050 | Async CDVD pressure under gameplay | no Exit | 2d |
| **PL-056** | S1,S2,S5,S6,S8,S9 | P2 | Soak claims hold T1–T3 | matrix green | 2d |
| **PL-057** | S10-EE | PL-051… | Thread/WaitSema health under load | thrash counters | 2d |
| **PL-058** | S4,S3,S7 | PL-051… | Deep 500M stability | no Exit | 2d |
| **PL-059** | T0 | PL-051…053 | **P4 assert** GAMEPLAY ≥3 | scoreboard | 0.5d |

**S4 agent fill:** S1–S10 all assigned (gameplay trio + soak six + platform).

---

### Season S5 — FIRST-GAMEPLAY wave B (PL-060 … PL-069) → **P5**

**Targets:** Dec, Whip, BO2 (or SM if Midway ready).

| ID | Seat | Depends | Deliverable | Exit test | Est |
|----|------|---------|-------------|-----------|-----|
| **PL-060** | T0 | P4 | Charters Dec/Whip/BO2 | docs | 0.5d |
| **PL-061** | S2 | PL-060 | Dec mode select → fight | T5 Dec | 4–5d |
| **PL-062** | S8 | PL-060 | Whip start run | T5 Whip | 4–5d |
| **PL-063** | S5 | PL-060 | BO2 new game room | T5 BO2 | 4–5d |
| **PL-064** | S1 | P4 | SM optional gameplay spike | T5 SM or residual | 4d |
| **PL-065** | S6 | P4 | GoW shell→first interactive beyond title | T5 GoW attempt | 5d |
| **PL-066** | S9 | P4 | Haven first area attempt | T5 Haven attempt | 4d |
| **PL-067** | S10-* | P4 | Shared blockers from 061–066 | global fixes | 3d |
| **PL-068** | S1–S9 | PL-061… | Hold matrix | T1–T4 hold | 2d |
| **PL-069** | T0 | PL-061…063 | **P5 assert** GAMEPLAY ≥6 | scoreboard | 0.5d |

---

### Season S6 — PLAYABLE-SLICE (PL-070 … PL-074) → **P6**

| ID | Seat | Depends | Deliverable | Exit test | Est |
|----|------|---------|-------------|-----------|-----|
| **PL-070** | T0 | P4 | Pick 1–2 titles for 60s playable slice | charter | 0.25d |
| **PL-071** | best seat | PL-070 | Deep soak Soft-GS alive + in-game pad | T6 @ deep | 5–7d |
| **PL-072** | S10-PAD | PL-071 | Analog/digital fidelity for slice | pad soak | 2d |
| **PL-073** | S10-GS | PL-071 | Frame pacing / present optional | no desync det | 2d |
| **PL-074** | T0 | PL-071 | **P6 assert** | video/log artifact | 0.5d |

Other 8 seats: continuous **MENU hold + natural plant demotion** (S3 backlog).

---

### Season S7 — IRX couple (PL-075 … PL-084) → **P7–P8**

Couples [IRX_EXECUTION_PHASE_PLAN.md](IRX_EXECUTION_PHASE_PLAN.md) Blocks D–E.

| ID | Seat | Depends | Deliverable | Exit test | Est |
|----|------|---------|-------------|-----------|-----|
| **PL-075** | S10-IOP | P2 | Disc IOPRP exec residual matrix | G2 list | 3d |
| **PL-076** | S10-SIF | PL-075 | FILEIO IRX pilot title #1 | **P7 partial** | 5d |
| **PL-077** | title pilot | PL-076 | Second FILEIO IRX title | P7 partial | 4d |
| **PL-078** | title pilot | PL-076 | Third FILEIO IRX title | **P7** | 4d |
| **PL-079** | S10-PAD | PL-075 | PADMAN IRX pilot | **P8 partial** | 5d |
| **PL-080** | title | PL-079 | Second PAD IRX | **P8** | 3d |
| **PL-081** | S10-DEBT | PL-076 | Demote FILEIO soft-success when IRX owns | LOC↓ | 2d |
| **PL-082** | S1–S9 | PL-076 | Fleet soak under LITERAL_IRX | T1 hold | 2d |
| **PL-083** | S10-IOP | PL-075 | MOD_LOAD non-empty commercial paths | Whip-class | 2d |
| **PL-084** | T0 | PL-078,080 | **P7+P8 assert** | #12 | 0.5d |

**Seat mapping in S7:** S10 on IOP/SIF/PAD lanes; 3 title pilots rotate; remaining seats soak.

---

### Season S8 — Debt demolition (PL-085 … PL-091) → **P9**

| ID | Seat | Depends | Deliverable | Exit test | Est |
|----|------|---------|-------------|-----------|-----|
| **PL-085** | S10-DEBT | P4 | LOC report vs baseline | CSV | 1d |
| **PL-086** | S1 | PL-085 | Delete dead SM plants | MENU+T2 hold | 2d |
| **PL-087** | S2/S3 | PL-085 | Midway plant delete pass | hold | 2d |
| **PL-088** | S4–S9 | PL-085 | Per-title plant delete pass | hold | 2d each |
| **PL-089** | S10-SIF | PL-085 | RealSifRpc dead soft-success purge | soak | 3d |
| **PL-090** | S10-EE | PL-085 | Kernel soft-success inventory | doc | 2d |
| **PL-091** | T0 | PL-086… | **P9 assert** LOC ↓ ≥40% | report | 0.5d |

---

### Season S9 — Determinism, free-ride, release (PL-092 … PL-099) → **P10–P12**

| ID | Seat | Depends | Deliverable | Exit test | Est |
|----|------|---------|-------------|-----------|-----|
| **PL-092** | S10-TOOL | P1 | Tape record/replay harness commercial | tool | 2d |
| **PL-093** | S1,S3,S4,S7,S8 | PL-092 | 5-title hash stable @100M | **P10** | 3d |
| **PL-094** | free-ride seat | P3 | SotC INTERACTIVE no new GameQuirk | **P11 partial** | 5–7d |
| **PL-095** | free-ride seat | PL-094 | Second free-ride serial | **P11** | 5–7d |
| **PL-096** | S10-TOOL | P5 | Full fleet matrix artifact archive | `out/traces/release/` | 1d |
| **PL-097** | T0 | P6+P10 | RELEASE_NOTES v0.2.0 | file | 0.5d |
| **PL-098** | T0 | PL-097 | COMPATIBILITY + wiki Commercial-Titles refresh | published | 0.5d |
| **PL-099** | T0 | PL-091…098 | **P12 close-out** #12 | gates table green | 0.5d |

**S9 exit = plan complete.**

---

## 8. Dependency DAG (high level)

```text
S0 measure ──► S1 INTERACTIVE (P1) ──► S2 FRONTEND (P2) ──► S3 NATURAL (P3)
                      │                      │
                      ├──────────────────────┼──► S4 GAMEPLAY A (P4) ──► S5 GAMEPLAY B (P5)
                      │                      │                              │
                      │                      └──────────────────────────────┼──► S6 PLAYABLE (P6)
                      │                                                     │
                      └──► S7 IRX couple (P7–P8) ◄── IRX plan Blocks D–E     │
                                      │                                      │
                                      └──► S8 DEBT (P9) ──► S9 DET/FREE/REL (P10–P12)
```

Platform WPs (S10) run **every season** in parallel with title seats.

---

## 9. Wave calendar (10 agents always on)

### 9.1 Standard week (example)

| Day | T0 | Seats |
|-----|----|-------|
| Mon | Merge weekend; assign WP | All 10 start WP |
| Tue–Wed | Conflict triage; mid-merge shared | Continue; S10 shared land first |
| Thu | Fleet spot-claim 3 titles | Title verify 50M |
| Fri | Full merge train; #12; matrix sample | Push branches; residual notes |
| Sat–Sun | Optional deep claims | Long 100M/500M |

### 9.2 Merge train order (risk-aware)

1. **S10 platform** (Gs/Gif/Vif/Dmac/Sif/Kernel) — always first  
2. **S1 SM** if SearchFile/shared Midway  
3. **S2 Dec → S3 DA** (serialize MidwayFamilyAssist)  
4. **S4 B3 → S5 BO2 → S6 GoW → S7 Vexx → S8 Whip → S9 Haven**  
5. Docs/scoreboard last  

### 9.3 Keeping 10 busy when blocked

| Block type | Idle seat does |
|------------|----------------|
| Waiting merge | Oracle: Play!/PCSX2 notes in title doc |
| Waiting S10 | Diagnose 20M wall map; no code |
| WP exit met | Next WP in season backlog or soak |
| Season complete | Next season charter draft |

---

## 10. Per-seat multi-season backlog (quick map)

| Seat | S1 | S2 | S3 | S4–S5 | S7 | S8 |
|------|----|----|----|-------|----|----|
| S1 SM | PL-011,021 | PL-031 | PL-044 | PL-064 | pilot? | PL-086 |
| S2 Dec | PL-012 | PL-029 | PL-045 | PL-061 | pilot | PL-087 |
| S3 DA | PL-013 | PL-030 | PL-045 | PL-052 | pilot | PL-087 |
| S4 B3 | PL-014,022 | PL-026 | PL-047 | **PL-051** | — | PL-088 |
| S5 BO2 | PL-015 | PL-027 | PL-043 | PL-063 | pilot | PL-088 |
| S6 GoW | PL-016,023 | PL-034 | PL-041 | PL-065 | pilot | PL-088 |
| S7 Vexx | PL-017 | PL-032 | — | **PL-053** | pilot | PL-088 |
| S8 Whip | PL-018 | PL-033 | PL-042 | PL-062 | — | PL-088 |
| S9 Haven | PL-019 | PL-028 | — | PL-066 | — | PL-088 |
| S10 | PAD/TOOL | GS/GIF/SIF | GS/GIF | SIF/CDVD/EE | IOP/SIF/PAD | DEBT/TOOL |

---

## 11. Definition of done

| Milestone | Required |
|-----------|----------|
| **M1** | P1 (INTERACTIVE 9/9) |
| **M2** | P1+P2+P4 (interactive + frontend + 3 gameplay) |
| **M3** | M2+P5+P6 (6 gameplay + playable slice) |
| **M4** | M3+P7+P8+P9 (IRX couple + debt) |
| **M5 / Plan done** | M4+P10+P11+P12 |

---

## 12. Out of scope (explicit defer)

- Full VU micro / hardware GS parity  
- Full SPU2 ADPCM commercial  
- Netplay cert commercial tapes  
- 100% IRX purity (G7 absolute) — tracked via P9, not blocking M2  
- Multi-disc, progressive, widescreen  
- Committing BIOS/ISO  

---

## 13. First 72 hours (start state)

| Hour | Action |
|------|--------|
| 0–4 | T0: confirm plan on main; `seat-bootstrap` 10 worktrees |
| 4–12 | Spawn **10 subagents**: S1–S9 on PL-005 charters + S10 on PL-001 scoreboard tiers |
| 12–24 | S10 lands PL-001/003; titles finish charters |
| 24–48 | T0 merge S0; assign S1 PL-011…S9 PL-019 + S10-PAD PL-010 |
| 48–72 | First INTERACTIVE claims streaming; mid-merge if S10-PAD ready |

**Do not** start free-ride or GameQuirk deletions until **P1**.

---

## 14. References

| Doc | Role |
|-----|------|
| [SCOREBOARD.md](title-ports/SCOREBOARD.md) | Fleet table |
| [IRX_EXECUTION_PHASE_PLAN.md](IRX_EXECUTION_PHASE_PLAN.md) | IOP G1–G7 |
| [AGENT_SOP.md](AGENT_SOP.md) | Budgets, oracle |
| [CORRECTNESS.md](CORRECTNESS.md) | No cheats |
| [TITLE_HACKS.md](TITLE_HACKS.md) | Debt log |
| [PLAY_HLE_ORACLE.md](PLAY_HLE_ORACLE.md) | Play! / PINE |
| Epic #12 | Progress |

---

## 15. Changelog

| Date | Note |
|------|------|
| 2026-07-31 | Initial post-MENU plan (40 WPs) |
| 2026-07-31 | **Mega expansion:** 100 WPs (PL-000…099), 10 permanent seats, 10 seasons, 12 product gates, dual-stack IRX couple |

---

*T0-owned. Revise only via main merge. Ten subagents stay saturated until P12.*
