# M1 residual — CHCR force-pump single-round / scheduler drain (design)

**Status:** design shelf — dual-ACK before Core  
**Author:** grok (dual-idle free seat after M7-L1 honesty close)  
**Parents:** `instant-progress-audit.md`, `instant-progress-rescan-g5.md` (R1)  
**Scope:** `src/DetPS2.Core/Dmac.cs` CHCR STR multi-round force-step only

---

## 1. Problem

After A3 cap (`MaxChcrForceSteps=16`, kill-switch restores 512), CHCR STR under `path3Hold || daDisplayVif` still runs:

```text
for (i < maxSteps && Active) Step(256)
```

Up to **16** scheduler-equivalent DMAC rounds inside **one EE MMIO store**. Risk **Med** residual of the old High GIF_STAT / 512-force class: channel finish + IRQ can land mid-store; owed-handler soft queue still relevant.

GIF_STAT multi-round is already fixed to single optional `Step(128)`. CHCR is the highest remaining multi-round site.

---

## 2. Goals / non-goals

| Goal | Non-goal |
|------|----------|
| Bound CHCR STR kick to **≤1** `Step(slice)` by default (mirror GIF_STAT A1) | Fix all path-sync hangs via force-pump |
| Keep kill-switch for claim-class rollback | Remove A1 QW caps / DrainCyclesPerQw |
| Prefer scheduler-driven drain for sticky STR | Invent Path3 progress / FQC fabricate |
| Fleet A/B: DA display, B3 path-sync, GoW plant path | Title-local CHCR plants |

---

## 3. Design options

| Opt | Mechanism | Risk | Notes |
|-----|-----------|------|-------|
| **A (preferred)** | On STR set + gate: **one** `Step(256)` (or budgeted slice); leave STR set; real master slices finish chain | Low–Med | Matches GIF_STAT residual pattern |
| **B** | Zero Step in write handler; schedule DMAC-heavy event / raise slice weight next `Scheduler.RunFor` | Med | Cleaner timing; more plumbing |
| **C** | Cap `maxSteps=1` via default only; keep loop shape | Low | Smallest diff; still multi-round shape if someone raises cap |

**Recommend A** first: one-line semantic change + default `MaxChcrForceSteps=1` (or dedicated `MaxChcrForceSteps=1` product, kill-switch `DETPS2_DISABLE_A3_CHCR_CAP=1` → 512 **or** new `DETPS2_CHCR_FORCE_LEGACY=1` → 16/512). Document migration from 16→1 separately from 512→16.

---

## 4. Acceptance sketch

| Check | Pass |
|-------|------|
| Unit / existing DMAC tests | green |
| Diagnose 20M: DA, B3, GoW, Whip, BO2 product | no new exitRequested; scoreboard identity or **documented** drift fields |
| Claim optional 50–100M: DA display / B3 path-sync | no sticky STR worse than tip-16; if worse, park and keep cap=16 |
| Kill-switch | restores multi-round for bisect |

---

## 5. Explicit bans

- Restore 512-step product default  
- Multi-round GIF_STAT poll-pump  
- Title quirk “force CHCR STR clear” as substitute for this seat  

---

## 6. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **M1R-Q1** | Accept Opt A (single Step on STR) as next Core seat? | **Yes** when dual free + demand |
| **M1R-Q2** | Default cap 1 vs keep 16 until claim A/B? | Prefer **cap=1** with kill-switch; else measure-first with cap=1 experimental env |
| **M1R-Q3** | Park until path-sync titles show STR stick pain? | OK if dual-idle has higher playability seats |

```text
M1 residual CHCR design
  multi-round force-pump → single-round kick (GIF_STAT class)
  dual-ACK before Core; kill-switch retained
```
