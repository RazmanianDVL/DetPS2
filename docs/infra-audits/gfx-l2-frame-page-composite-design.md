# GFX L2 design — FRAME/DISPFB page + presentable RGB (after L1)

**Status:** **Core C4+C5 landed** — B3 Tier A numeric **PASS** (colorPct≈34% @50M); visual dual-check pending  
**Parent:** L1 C1+C2 landed; B3 present was black (CP3)  
**Primary:** Burnout 3  

---

## 1. Problem (post-L1)

L1 fixed merge-cache arm footgun + scoreboard `GetPresentSpan` parity.  
B3 @50M still: **present 100% black**, metrics px≈18M / imgBytes≈2M, `compositeSource=LastImageTrx`, residualDispfbPx>0 (likely **cumulative mid-run**, not final FB).

Registers:

| Reg | Decode |
|-----|--------|
| FRAME_1 | FBP=**70** (0x8C000), FBW=10, PSM=0 (CT32) |
| DISPFB2 | FBP=**0**, DBW=10, DPSM=**0x0A** (PSMCT16S) |

Prims/`WriteFrameLocal` target **FRAME page 70**. Natural DISPFB composite reads **page 0 PSMCT16S**. LastImageTrx residual may sample a third window. Present ends empty RGB.

---

## 2. Hypotheses

| ID | Hypothesis |
|----|------------|
| **H3** | When present mostly black, always merge **FRAME local** (and FBP0) even if a sparse LastImageTrx residual already incremented counters mid-run |
| **H4** | DISPFB PSMCT16S read of CT32/local IMAGE page mismatches → 0 RGB; need prefer FRAME PSM when DISPFB empty under FBP |
| **H5** | Soft-GS `_framebuffer` prim paint is all black clears; real chrome only in local — final composite must hit the right local page every present |

---

## 3. Proposed seat (after dual-ACK)

**C4** — At end of `CompositeDispfbToFramebuffer`, if `IsPresentMostlyBlack()` and `ImageBytesWritten>0` (or FRAME≠0):

1. Force `CompositeLocalToFb(FRAME_1, …)` merge (ignore “already wrote residual” if present still black).  
2. Then FBP0 synthetic if still black.  
3. Then LastImageTrx if still black.

**C5** — Telemetry: `presentLit` / `presentColor` in scoreboard-metrics JSON from final `GetPresentSpan` sample (so residual counters can’t fake success).

**Non-goals:** invent DISPFB plant; goefile paint; Path2 invent.

---

## 4. Acceptance

| Check | Pass |
|-------|------|
| B3 @50M present | color% ≥5 **or** clear next residual class with PPM visual |
| Dec | no severe regress; gray→color if gameart local is real RGB |
| Smokes | green |
| Visual dual-check | required |

## 5. Dual-ACK

| ID | Q | Bias |
|----|---|------|
| **L2-Q1** | Accept C4+C5 next Core? | **Yes** |
| **L2-Q2** | Keep L3 Whip parallel docs-only? | **Yes** |

## 6. Landed result (post C4+C5)

Canary `out/canaries/gfx-baseline/20260804-203212/` product tip after L2:

| Title | presentColorPct | Tier A numeric | Notes |
|-------|----------------:|:--------------:|-------|
| **B3** | **34.286** | **PASS** | 98304 color px; src=NaturalDispfb (mismatch-allow residual cascade) |
| Dec | 0 | fail | gray strip only (11.4% lit gray) — L2/Dec follow-up |
| Whip | 0 | fail | L3 |

Also: scoreboard emits `presentLit` / `presentColor` / `present*Pct` (C5).  
`DETPS2_GFX_IGNORE_PAGE_MISMATCH=1` remains optional full-ignore diagnostic (not product default).

```text
GFX L2 C4+C5 landed
  B3 present color≈34% Tier A numeric; visual dual-check
  Dec gray residual; Whip L3
```
