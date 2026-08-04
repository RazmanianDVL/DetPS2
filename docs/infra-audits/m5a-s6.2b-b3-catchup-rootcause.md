# M5-a S6.2b — B3 `DETPS2_DMAC_LEVEL_CATCHUP` regression root-cause (investigation)

**Date:** 2026-08-04  
**Tip:** `199e8ee` (S6.1 `b36dfd1` dormant)  
**Mode:** investigation / docs — **no Core product change**  
**Prior:** `docs/infra-audits/m5a-s6.2-claim-ab-results.md` (Claude S6.2 NEGATIVE)  
**Build:** Release `out/scoreboard-build/DetPS2.Core.dll`  
**Media:** `burnout-only.json` claim 100M `--host-present` + `DETPS2_TRACE_DMAC=1`

---

## 0. Verdict

**Root class (high confidence):** level catch-up interacts with B3’s **high owed backlog + CreditOwed assist pressure** such that claim-tier progression **plateaus at the diagnose (~20M) work envelope** for the remaining ~80M cycles. Not a crash; not invent credits; kill-switch still restores baseline.

**Promotion status:** unchanged — **do not enable CATCHUP by default**; do not ship ambient re-Raise (S6.4) until a redesigned gate exists.

---

## 1. Independent reproduce (this seat)

| Arm | px | prims | gifCompleted | finish | raise | catchupRaise | wall |
|-----|---:|------:|-------------:|-------:|------:|-------------:|-----:|
| baseline (flag unset) | **30249654** | 6373 | 3392 | **574** | 1345 | 0 | 14.6s |
| CATCHUP=1 | **877187** | 172 | 92 | **20** | 31 | **14** | 20.6s |

Matches Claude S6.2 B3 numbers exactly (px/prims/gifCompleted). `levelCatchup=1` on catchup arm TRACE line confirms flag armed at process start.

---

## 2. Critical identity: catchup-claim == diagnose 20M floor

From `docs/infra-audits/m5a-b3-trace-dmac-sample.md` (diagnose 20M, pre-S6):

| Field | Diagnose 20M | **Claim 100M + CATCHUP** |
|-------|-------------:|-------------------------:|
| px | 877187 | **877187** |
| prims | 172 | **172** |
| gifP2 / gifP3 | 12 / 20 | **12 / 20** |
| dmac finish | 20 | **20** |
| imgBytes | 65728 | **65728** |
| gifCompleted | 92 | **92** |

**Read:** under CATCHUP, a 100M claim run produces the **same Soft-GS / DMAC envelope as a 20M diagnose run**. The extra 80M cycles buy almost no additional commercial work. Baseline claim multiplies that envelope ~34× (px 877k→30M). Catchup freezes progression at the early-boot/DMA-sparse regime.

---

## 3. Mechanism reading (code path)

S6.1 post-ack path (`EmotionEngine` viaDmacFallback → `MaybeLevelCatchupRaise`):

1. `TryTakePendingDmacHandler` takes one handler, may re-Raise if more owed.  
2. `Acknowledge` edge-clears DmaController.  
3. Catchup re-Raises if **any** IRQ-enabled channel still has CIS **or** `_owedHandlerCalls>0` — **without invent**, but also **without distinguishing FinishChannel-owed vs CreditOwed invent**.

On B3 claim baseline TRACE, **creditAssist=391/ch** and **owedPeak=64** (assist-dominated). Catchup therefore treats assist-manufactured owed the same as real completion owed: after every productive take, if assist/queue still holds depth, **level re-assert fires again**.

Haven (creditAssist=1, cleaner under-take) only goes **flat** (catchupRaise=130, tryTake unchanged) — consistent with “signal without progress” without catastrophic assist interaction. B3’s severe plateau matches **assist × catchup** coupling.

`catchupRaise=14` on B3 is **not** millions of raises — absolute count is low because **DMA submission itself collapses** (finish 574→20). Wall time **increases** (14.6s→20.6s) despite far less real work → remaining cycles spent in non-progressing IRQ/dispatch/assist loops rather than submitting new DMA.

---

## 4. Hypotheses ranked

| ID | Hypothesis | Status after this seat |
|----|------------|------------------------|
| **H1** | Catchup × CreditOwed / high owed → early plateau thrash (EE stuck servicing DmaController level assert; game stops issuing new DMA) | **Primary — supported** by diagnose-identity + assist-heavy B3 vs sparse Haven |
| **H2** | Pure re-Raise storm without assist | **Weaker** — Haven catchupRaise high without collapse; B3 catchupRaise only 14 with collapse |
| **H3** | Flag accidentally default-on / build skew | **Ruled out** — baseline matches pre-S6; kill restores (Claude); TRACE `levelCatchup=0/1` correct |
| **H4** | Soft-GS / DISPFB interaction | **Ruled out** — residual class orthogonal; progression metrics (RPC, gifCompleted) collapse too |

**Not done here:** re-arm throwaway `DETPS2_M5A_B3_NO_CREDIT_ASSIST` × CATCHUP cross (would isolate H1 further). Optional follow-up seat.

---

## 5. Design implications (no code this seat)

| Decision | Recommendation |
|----------|----------------|
| Product default CATCHUP | **Keep OFF** |
| S6.4 ambient re-Raise | **Blocked** until redesign |
| S6.5 default-on | **Blocked** |
| Future redesign options (not dual-ACKed) | (a) rate-limit catchupRaise/sec; (b) only re-Raise when last take drained **FinishChannel-owed** not assist credit; (c) require `creditAssist==0` window; (d) separate opt-in experiment flag |
| S6.1 code in tree | **Keep** as dormant opt-in + kill — flag-off proven no-op |

---

## 6. Sign-off

```text
M5-a S6.2b B3 CATCHUP root-cause tip 199e8ee+
  reproduce: claim CATCHUP px=877187 finish=20 catchupRaise=14 (== diagnose 20M floor)
  primary: assist/high-owed × level re-Raise → early plateau thrash
  no Core change; CATCHUP stays default OFF; no S6.4/S6.5
```
