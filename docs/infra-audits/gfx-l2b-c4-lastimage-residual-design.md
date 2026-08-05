# GFX L2b-C4 design — LastImage residual only (after reject of 3bcedb2)

**Status:** **Core landed** (gate `written==0` this attempt; no allowPageMismatch)  
**Parent:** L2 reject + L2b redesign (`gfx-l2-frame-page-composite-design.md`)  
**Primary:** Burnout 3 (still black) / Dec (color metrics — **visual dual-check required**)  

---

## 1. Scope (narrow)

| In | Out |
|----|-----|
| After **this composite attempt’s** natural DISPFB/FRAME path returns `written==0` (not “frame mostly black by area”) | `allowPageMismatch` on DISPFB/FRAME |
| Optional **one** residual: `CompositeLastImageTransfer` using BITBLT’s tracked DPSM/DBW/DBP only | Cascade re-paint that sets `natural=false` after successful natural writes |
| Visual dual-check of B3 PPM before Tier A | Numeric color% alone |

---

## 2. Mechanism

In `CompositeDispfbToFramebuffer`, after the existing natural/residual paths that already exist pre-3bcedb2:

```text
if (written == 0 && _lastImageByteCount > 0 && ImageBytesWritten > 0)
{
    // only if THIS attempt found nothing presentable on declared DISPFB/FRAME paths
    long img = CompositeLastImageTransfer(mergeMode: true);
    // LastImage uses BITBLT DPSM — keep IsPageMismatched OR skip only when
    // last-write PSM equals _lastImageDpsm (same transfer); never re-decode as other PSM
}
```

**Do not** gate on `IsPresentMostlyBlack()` alone.  
**Do not** re-enter DISPFB with a different layout.  
If LastImage still paints stripes under visual check → **accept residual black** and open L3/EE path for B3 logo delivery, not more residual decode.

---

## 3. Acceptance

| Check | Pass |
|-------|------|
| Smokes full suite | green (`Gs_Dispfb_Psmct16_*` natural counts hold) |
| B3 @50M | presentColorPct may rise **only if** visual shows real chrome (logo/UI), not stripes |
| Dec | no gray→stripe regress; gray strip residual OK |
| Visual dual-check | Claude + Grok |

---

## 4. Dual-ACK

| ID | Q | Bias |
|----|---|------|
| **L2bC4-Q1** | Accept this narrow LastImage-only seat? | **Yes** |
| **L2bC4-Q2** | If visual still stripes/black, park residual decode and document? | **Yes** |

## 5. Post-land measure (50M)

| Title | presentLit | presentColor | colorPct | residualDispfbPx | compositeSource | Notes |
|-------|----------:|-------------:|---------:|-----------------:|-----------------|-------|
| **B3** | 0 | 0 | 0 | 13030 | SyntheticFbp0 | **Still black present** — residual counters mid-run only; park further residual decode for B3 (Q2) |
| **Dec** | 63410 | 30229 | **10.54** | (see metrics) | LastImageTrx | Numeric color; **must visual dual-check** before Tier A (no stripes) |

Smokes Gs composite set: green.

```text
L2b-C4 landed
  B3 present still black → park residual tricks
  Dec color%≈10 — visual dual-check before claim
```
