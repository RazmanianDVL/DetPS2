# M5-a next status — after C1 park (2026-08-04)

**Status:** docs only — **no Core this seat**  
**Tip at write:** `3e7a57c`  
**Parent park:** `m5a-s6-residual-parked.md` (`5bfbd53`)  

---

## 0. One-line recommendation

**Keep M5-a S6 CATCHUP parked (default off).** Do not re-open Core without a **new dual-ACK redesign**. Prefer **measurement-only** next seats if DMAC remains a playability lever; otherwise pick non-M5 work (M7 residual honesty / GoW plant / M8 Prefer).

---

## 1. What’s already proven

| Fact | Doc |
|------|-----|
| B3 GIF under-take + owed backlog (diagnose) | `m5a-q1-q5-evidence-rollup.md` |
| Haven VIF1 under-take claim 100M | same + claim samples |
| CATCHUP re-Raise fires but **does not improve tryTake** (Haven flat) | `m5a-s6.2-claim-ab-results.md` |
| CATCHUP **collapses B3 ~97%** work; kill-switch causal | same + `m5a-s6.2b-b3-catchup-rootcause.md` |
| Haven flat = one-take-per-pass × finish rate (throughput), not lost raise | `m5a-haven-signal-no-consumer.md` |
| Flag-off is true no-op | parked doc + smokes |

---

## 2. Standing decisions (still binding)

1. **No** product-default `DETPS2_DMAC_LEVEL_CATCHUP`.  
2. **No** S6.4 ambient re-Raise / S6.5 default-on.  
3. S6.1 code may stay dormant opt-in + kill for lab only.  
4. Soft-GS / DISPFB residual is **out of M5-a S6**.  

---

## 3. Candidate next seats (dual-ACK before Core)

| ID | Seat | Type | When |
|----|------|------|------|
| **M5-N1** | Multi-take / consumer throughput design (Haven: one-take-per-pass) | design | Only if VIF1 under-take is still top playability lever |
| **M5-N2** | CATCHUP redesign seeds (rate-limit, Finish-owed vs CreditOwed, assist-quiet window) | design | Only if reopening S6 class |
| **M5-N3** | GoW claim TRACE DMAC sticky (fill Q3 gap) | measure | Low cost; may re-rank titles |
| **M5-N4** | **Park M5 entirely** — work M7 / plant / Prefer quiet | plan | If no title’s playability is gated on DMAC under-take now |

**Bias after C1 park:** **M5-N4 or M5-N3** — do not spend dual-orch bandwidth on CATCHUP redesign without a live title demand. Multi-take (N1) is the only “positive” infra direction if Haven-class under-take still blocks playability.

---

## 4. Dual-ACK questions (planning)

| ID | Question | Bias |
|----|----------|------|
| **M5-Q1** | Leave S6 parked as standing decision? | **Yes** |
| **M5-Q2** | Next dual-orch pick: N4 park / N3 GoW TRACE / N1 multi-take design? | **N4 or N3** |
| **M5-Q3** | Touch CATCHUP Core this week? | **No** |

---

```text
M5-a next status after C1 park
  CATCHUP remains parked (B3 -97% collapse)
  bias: park M5 or cheap GoW TRACE; no CATCHUP Core
```
