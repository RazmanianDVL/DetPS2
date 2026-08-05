# GFX L1 design — present black despite Soft-GS activity (CP2 dual-ACK)

**Status:** **Core C1+C2 landed** — CP3 B3 still fails Tier A → escalate **L2** (H3/H4)  
**Plan:** `gfx-plan-v0.md`  
**Baseline:** `gfx-baseline-2026-08-04.md`  
**Primary title:** Burnout 3 (Dec secondary)

---

## 1. Problem

At tip product, B3 @50M:

- Soft-GS **activity:** px≈17.8M, prims≈3769, imgBytes≈1.97M, gifP2+P3≈611  
- Soft-GS **present PPM:** **100% black** (lit=0, color=0)  
- compositeSource often `LastImageTrx` with residualDispfbPx small (e.g. 6515) while FB ends black  

Desktop shows the present buffer (`GetPresentSpan` → blit). Metrics alone hide the bug.

---

## 2. Hypotheses (ranked)

| ID | Hypothesis | Evidence |
|----|------------|----------|
| **H1** | Black full-FB **prims** wipe Soft-GS FB after a successful residual composite; merge **cache** skips re-merge (`_mergeBlackBypassArmed` / `DispfbPixelsComposited` early return) | `Clear()` invalidates cache; StorePixel black clears do **not**. Scoreboard host-present composites mid-run then more RunFor. |
| **H2** | Scoreboard final path calls `CompositeDispfbToFramebuffer` only — **not** Desktop’s `GetPresentSpan` (ForceRefresh path) | `Program.cs` scoreboard-metrics vs `EmulationWorker` |
| **H3** | Composite targets **wrong page/PSM** (DISPFB FBP0 PSMCT16S vs FRAME FBP70 CT32); LastImageTrx residual sparse/wrong | B3 FRAME FBP=70, DISPFB2 FBP=0 DPSM=0x0A |
| **H4** | Local IMAGE bytes are non-zero but `LoadLocalPixelForPresent` / `IsPageMismatched` drops RGB → written residual then still black | residual counter vs PPM mismatch history |

**CP2 seat scope:** H1 + H2 first (present path / cache). H3–H4 = L2 follow-up if B3 still fails Tier A after H1/H2.

---

## 3. Proposed Core changes (after dual-ACK only)

### C1 — Merge cache when present is mostly black

In `CompositeDispfbToFramebuffer`:

- Early-return skip **only** when present is **not** mostly black (chrome already on screen).  
- If mostly black and local IMAGE exists: **re-merge**; arm `_mergeBlackBypassArmed` **only after** a composite that writes **0** while still black (proved empty RGB).  
- On written > 0: clear bypass arm.

Do **not** arm bypass *before* fall-through (current footgun).

### C2 — Scoreboard present path = Desktop

Final scoreboard-metrics step: `_ = smSys.Gs.GetPresentSpan()` (not lone `CompositeDispfbToFramebuffer`) so dump/metrics match Desktop.

### C3 — Optional (same seat if small)

Invalidate present composite cache when a **full-FB black stamp** is detected (e.g. N consecutive black StorePixel covering FB) — only if H1 remains after C1/C2. Prefer measuring first.

---

## 4. Acceptance

| Check | Pass |
|-------|------|
| Smoke `Gs_*Composite*` tests | green |
| B3 @50M present PPM | **color% ≥ 5** **or** documented residual with H3/H4 next |
| Visual dual-check | Grok + Claude open PPM — not garbage noise / single gray strip unless accepted residual |
| Dec | no severe regress (gray strip may remain until L2) |
| Whip | no invent IMAGE; may stay black (L3) |
| Kill-switch | none required if change is cache correctness; optional env to restore old early-return if needed |

---

## 5. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **L1-Q1** | Accept C1+C2 as first Core seat? | **Yes** |
| **L1-Q2** | Defer C3 / H3–H4 to L2 if B3 still black? | **Yes** |
| **L1-Q3** | Ban goefile re-enable still holds? | **Yes** |

## 6. CP3 result (post C1+C2)

Canary `out/canaries/gfx-baseline/20260804-201537/` @ Core land:

| Title | present lit% | color% | Tier A |
|-------|-------------:|-------:|:------:|
| B3 | 0 | 0 | **FAIL** (still pure black) |
| Dec | 11.4 | 0 | **FAIL** (gray strip only) |
| Whip | 0 | 0 | expected L3 |

Smokes Gs composite set: **pass**.  
**Verdict:** C1+C2 correct path hygiene; **insufficient** for B3 Tier A. residualDispfbPx counters mid-run ≠ final present RGB. Escalate **L2** (H3 FRAME FBP≠DISPFB, H4 page/PSM load).

```text
GFX L1 C1+C2 landed
  smokes OK; B3 present still black → L2
```
