# M5-a S6 residual — **parked** (opt-in dormant; do not promote)

**Date:** 2026-08-04  
**Tip:** `5bfbd53`  
**Status:** **PARKED** — product default remains flag-off; no further promotion without redesign dual-ACK

---

## 1. What landed

| Slice | Tip | Outcome |
|-------|-----|---------|
| **S6 design** | `aa58a89` | Dual-ACK Q1–Q6 approved (opt-in re-Raise, no invent) |
| **S6.1 implement** | `b36dfd1` | `DETPS2_DMAC_LEVEL_CATCHUP=1` + kill `DETPS2_DISABLE_M5A_DMAC=1`; EE post-ack call necessary (amended `3fa29b3`) |
| **S6.2 claim A/B** | `199e8ee` | **Haven flat** (catchupRaise fires, tryTake does not improve). **B3 collapse to ~3% of baseline** (−97% px/work), kill-switch causal |
| **S6.2b root-cause** | `5bfbd53` | Claim+CATCHUP metrics **identity-match diagnose 20M floor** for full 100M; primary hyp **CATCHUP × assist/high-owed thrash** |

Flag-off remains a **true no-op** (smokes + claim baselines match pre-S6).

---

## 2. Standing decisions (dual-ACK)

1. **Do not** product-default CATCHUP.  
2. **Do not** S6.4 ambient re-Raise or S6.5 default-on until redesign.  
3. Keep S6.1 code in tree as **dormant opt-in + kill** for future experiments.  
4. Soft-GS DISPFB residual and GoW force-finish remain **out of M5-a S6**.  
5. B3 CreditOwed assist quiet is evidence-only — **not** product soft-off from this stream.

---

## 3. Redesign seeds (not dual-ACKed — backlog only)

If M5-a S6 is ever reopened:

- Rate-limit `catchupRaise`  
- Distinguish FinishChannel-owed vs CreditOwed when deciding re-Raise  
- Require low/no assist credit window  
- Separate experiment flag from any product path  

---

## 4. Pointers

| Doc | Role |
|-----|------|
| `m5a-s6-level-catchup-design.md` | Design + implement amend |
| `m5a-s6.2-claim-ab-results.md` | Claim A/B numbers |
| `m5a-s6.2b-b3-catchup-rootcause.md` | Collapse root-cause |
| `m5a-b3-assist-quiet-evidence.md` | Assist OFF → more finish (context for thrash hyp) |
| `m5a-q1-q5-evidence-rollup.md` | Earlier Q dual-ACK |

---

```text
M5-a S6 PARKED tip 5bfbd53
  CATCHUP default OFF; S6.2 gate failed (B3 -97% collapse)
  root-cause: early plateau thrash under assist x re-Raise
  no further promote without redesign dual-ACK
```
