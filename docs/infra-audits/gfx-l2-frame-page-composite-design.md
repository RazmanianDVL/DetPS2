# GFX L2 design — FRAME/DISPFB page + presentable RGB (after L1)

**Status:** **3bcedb2 Core REJECTED** (Claude seq0233 visual fail + smoke regression) — Core **reverted to f2f9cd9 L1**; redesign below  
**Parent:** L1 C1+C2 (`f2f9cd9`)  
**Primary:** Burnout 3  

---

## 1. Rejected approach (3bcedb2) — do not revive

| Claim | Reality |
|-------|---------|
| presentColorPct≈34% Tier A | **Visual fail** — vertical cyan/blue **stripe noise** (wrong PSM/stride decode) |
| allowPageMismatch residual cascade | Reinterprets local pages as wrong format — same class as banned goefile-as-pixels |
| Gate on `IsPresentMostlyBlack()` alone | Fires after **successful** small natural composites (1024px still “mostly black” by area) → corrupts `naturalDispfbPx` telemetry |
| Smoke | `Gs_Dispfb_Psmct16_CompositeNoCrtOffset` failed: naturalDispfbPx must be >0 |

**Lesson:** numeric color% without visual dual-check is a false Tier A. Mismatch-allow is not a residual logo fix.

---

## 2. Problem (still open at L1 tip)

B3 @50M product: Soft-GS activity high, present **black**.  
Registers: FRAME FBP=70 CT32 vs DISPFB2 FBP=0 PSMCT16S.  
residualDispfbPx counters mid-run can be non-zero while final present is black (L1 residual).

---

## 3. Redesign principles (L2b — dual-ACK before Core)

1. **Gate on attempt outcome, not frame area**  
   - Run natural DISPFB / FRAME composite first.  
   - Enter residual cascade only if **this call’s natural/first-path `written==0`** (or written>0 but **all** written pixels were rejected / zero RGB), **not** merely `IsPresentMostlyBlack()`.  
   - Never set `natural=false` on a path that only re-touched already-correct natural pixels.

2. **Never re-decode with the wrong PSM**  
   - Keep `IsPageMismatched` for declared DISPFB/FRAME composite.  
   - Residual may use **LastImageTrx with BITBLT’s own DPSM/DBW only** (already tracked) — still subject to visual dual-check.  
   - No `allowPageMismatch` that paints DISPFB layout over pages last written as a different format.

3. **C5 presentLit telemetry is still good**  
   - Re-land as a **separate** tiny seat if desired (metrics only, no composite behavior change).

4. **Tier A** still requires **visual** dual-check of PPM (not stripes, not gray index noise).

---

## 4. Proposed L2b seats (after dual-ACK)

| Seat | Change | Bar |
|------|--------|-----|
| **L2b-C5** | scoreboard `presentLit`/`presentColor` only | metrics only; smokes unchanged |
| **L2b-C4** | residual cascade gated on **written==0 this attempt**; LastImageTrx only with BITBLT PSM; no mismatch-allow on DISPFB | B3 present color **and** visual OK **or** honest still black with TRACE of last IMAGE window |

---

## 5. Dual-ACK (L2b)

| ID | Q | Bias |
|----|---|------|
| **L2b-Q1** | Accept reject of 3bcedb2 Core + redesign principles? | **Yes** (Claude already rejected) |
| **L2b-Q2** | Land C5 metrics-only next? | **Yes** cheap |
| **L2b-Q3** | L2b-C4 LastImage-only residual next design dual-ACK before Core? | **Yes** |

```text
GFX L2 3bcedb2 REJECTED — stripe noise + smoke fail
  Core reverted to f2f9cd9 L1
  redesign: gate on written==0, no wrong-PSM paint
```
