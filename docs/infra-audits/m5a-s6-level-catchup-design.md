# M5-a S6 design — level-sensitive DMAC catch-up (flag-gated Core)

**Status:** design only — **ready for dual ACK** — **no Core implement this turn**  
**Date:** 2026-08-04  
**Tip ref:** `1fe6444`  
**Parent:** `docs/infra-audits/m5a-dmac-vif-gif-completion-design.md` §4.2 Phase 1 / §6 S6  
**Evidence base (measurement seats this session):**

| Seat | Doc | Role |
|------|-----|------|
| B3 diagnose TRACE | `m5a-b3-trace-dmac-sample.md` | GIF under-take; w1c=0; pre-enable not binding |
| B3 claim TRACE | `m5a-b3-trace-dmac-claim-sample.md` | GIF under-take persists; **creditAssist=391** dominates |
| B3 assist-quiet A/B | `m5a-b3-assist-quiet-evidence.md` | Quiet → **more** finish, owed collapses — credits **not** starvation fill |
| Haven claim TRACE | `m5a-haven-trace-dmac-claim-sample.md` | VIF1 under-take 0.50; creditAssist=1; cleaner Core oracle |
| GoW claim TRACE | `m5a-gow-trace-dmac-claim-sample.md` | DMA-starved finish=2; **out of S6** (SECONDARY / residual) |
| Q rollup | `m5a-q1-q5-evidence-rollup.md` | Q2/Q4/Q7 dual-ACK: no busy mirror; keep caps; opt-in first |

**Owned code (future implement):** `src/DetPS2.Core/Dmac.cs` primarily; optional thin hooks in `SonyKernelHle.cs` take path only if proven.  
**Hard bans:** no GameQuirks growth; no invent credits; no title branches; no Core busy-RAM poke; no GIF packet plants; no GoW END-tag Core writes.

---

## 0. One-line proposal

Land **opt-in** Core behavior under `DETPS2_DMAC_LEVEL_CATCHUP=1` (kill-switch `DETPS2_DISABLE_M5A_DMAC=1`) that **re-raises / level-retains DmaController IRQ while owed handler calls remain and channel IRQ is enabled**, without inventing additional owed counts — so game `AddDmacHandler` bodies get a chance to run at hardware-ish level sensitivity. Default fleet remains **byte-identical** until dual-ACK promotes.

---

## 1. Problem statement (evidence-locked)

### 1.1 What is broken

Multiple titles arm `AddDmacHandler(VIF1/GIF)` and expect **completion → CIS → IRQ → handler** to keep software queues draining. TRACE shows:

| Title | Pattern | creditAssist |
|-------|---------|--------------|
| **B3 claim** | GIF finish=208 / takeCis=95 (~0.46); owedPeak=64 / owedNow=63 | **391/ch** (assist-dominated) |
| **Haven claim** | VIF1 finish=134 / takeCis=67 (~0.50); owedPeak=64 / owedNow=63 | **1** (sparse) |
| **B3 quiet** | finish **574→1005**; owed **64→~2**; no hang | 0 |

**Lost CIS storm is not the primary class** (`w1cWhileOwed=0` B3 both budgets; Haven only 1 blip).  
**Pre-enable cap-4 is not binding** on VIF1/GIF in these samples.  
**Under-take + owed backlog** is the repeatable Core-facing signature on Haven; B3 is confounded by assist credits but quiet arm proves real pipeline can sustain **more** work without invent.

### 1.2 What S6 is *not*

| Non-goal | Why |
|----------|-----|
| Product soft-off of B3 `CreditOwedHandlerCall` | Quiet is evidence only; needs dual-ACK + menu/interactive + multi-title (assist-quiet doc) |
| Raise depth caps 8/64/4 | Caps already hit; raising without A/B hides under-take |
| Core invent credits (clone assist into Core) | Explicit ban; quiet arm shows invent may **cap** throughput |
| Fix GoW force-finish END tags | GoW claim is DMA-starved / gifP3=0 — not under-take class |
| Fix Soft-GS DISPFB residual | Orthogonal M7 (B3 lit=0 both quiet arms) |

---

## 2. Mechanism sketch (implement after ACK)

### 2.1 Recommended v1 (candidate A + C refined — no invent)

**Name:** level-sensitive owed re-arm  
**Flag:** `DETPS2_DMAC_LEVEL_CATCHUP=1` (default **off**)  
**Kill-switch:** `DETPS2_DISABLE_M5A_DMAC=1` forces pre-S6 behavior even if catch-up set  

**Behavior when catch-up enabled and kill unset:**

1. **On FinishChannel** (existing): if CIM live → inc owed (cap 64) + sticky CIS + RaiseDmacIrq (unchanged counts).  
2. **On TryTakePendingDmacHandler / after handler eret path** (strengthen existing re-Raise):  
   - If `_owedHandlerCalls[ch] > 0` AND channel IRQ still enabled (CIM) AND INTC path allows → **re-Raise DmaController** without incrementing owed.  
   - Rationale: level-sensitive hardware keeps asserting until software drains; edge-only loss explains finish≫take without W1C storms.  
3. **Ambient nudge (optional second slice, same flag or sub-flag):** once per N scheduler slices / EE IRQ exits, if any channel has owed>0 && CIM && no take in last K cycles → re-Raise only (still **no** invent).  
   - Default for v1 implement: **take-path re-Raise only** (slice 2.1 item 2); ambient as v1.1 if Haven under-take remains with re-Raise alone.

**Explicitly NOT in v1:**

- Incrementing `_owedHandlerCalls` outside FinishChannel / CreditOwedHandlerCall / pre-enable promote.  
- Changing pre-enable promote cap.  
- Changing depth 64.  
- Assist env hooks (separate PR if ever productized).

### 2.2 Files

| File | Change |
|------|--------|
| `Dmac.cs` | `MaybeLevelCatchupRaise` + `catchupRaise` TRACE; flags |
| `SonyKernelHle.cs` | `MaybeLevelCatchupAfterDmacDispatch` thin forwarder to Dmac |
| `EmotionEngine.cs` | **v1 required (one call):** after viaDmacFallback `Acknowledge`+`ClearCpuLatch` for DmaController, call catch-up. Necessary because Acknowledge edge-clears the TryTake re-Raise; EE is the only site that pairs take+ack. Flag-off no-op. |
| `Intc.cs` | **Out of v1** — generic Acknowledge has no take/owed context; hooking every DmaController ack couples Intc→Dmac worse than the EE one-liner |

### 2.3 Flag table (S6)

| Env | Default | Role |
|-----|---------|------|
| `DETPS2_DMAC_LEVEL_CATCHUP=1` | **off** | Opt-in S6 behavior |
| `DETPS2_DISABLE_M5A_DMAC=1` | unset | Hard kill → pre-S6 |
| `DETPS2_TRACE_DMAC=1` | off | Existing telemetry; add `catchupRaise` line |

### 2.4 Acceptance (implement turn)

| Gate | Bar |
|------|-----|
| Flag-off | Byte-identical diagnose canaries vs tip before PR (B3, Haven, GoW, BO2 if cheap) |
| Flag-on + TRACE | Haven: take/finish improves or owedNow decreases without invent; **no** crash |
| Flag-on B3 | finish/take not worse; creditAssist may stay high until assist quiet PR |
| Quiet B3 (throwaway or future env) | Prefer non-regression of quiet arm throughput (finish not collapse) |
| Smokes | Existing DMAC handler smokes green both arms |
| Product default-on | **Not** in first PR — second PR only after multi-title dual-ACK |

---

## 3. Dual-ACK questions (S6)

| ID | Question | Design bias |
|----|----------|-------------|
| **S6-Q1** | Approve opt-in level re-Raise (no invent) as **first** Core behavior PR? | **Yes** |
| **S6-Q2** | Include ambient owed re-Raise in v1 or defer to v1.1? | **Defer** ambient; take-path only first |
| **S6-Q3** | Keep depth caps 8/64/4 unchanged in S6? | **Yes** |
| **S6-Q4** | B3 product assist soft-off in same PR as S6? | **No** — separate, after S6 evidence |
| **S6-Q5** | GoW force-finish in M5-a S6 scope? | **No** — SECONDARY / residual until sticky probe |
| **S6-Q6** | Default-on timeline: only after flag-on claim A/B on Haven + B3 + one more title? | **Yes** |

---

## 4. Implementation order (after dual-ACK)

| Step | Work | Behavior change? |
|------|------|------------------|
| S6.0 | Dual-ACK this doc | No |
| S6.1 | `DETPS2_DMAC_LEVEL_CATCHUP` + take-path re-Raise + TRACE counter | **Yes, opt-in** |
| S6.2 | Smokes + diagnose flag-off identity + flag-on Haven/B3 TRACE | Measure |
| S6.3 | Claim A/B writeup | Measure |
| S6.4 | Optional ambient re-Raise | Yes, opt-in |
| S6.5 | Default-on proposal (separate dual-ACK) | Policy |
| S6.6 | Assist quiet product hooks (separate) | Assist-side |

---

## 5. Definition of done (this design seat)

- [x] Evidence synthesis from B3/Haven/GoW/quiet  
- [x] Concrete mechanism without invent credits  
- [x] Flag/kill-switch table  
- [x] Open dual-ACK Qs  
- [ ] Partner ACK on S6-Q1–Q6  
- [ ] **No Core** until ACK  

---

## 6. Sign-off

```text
M5-a S6 design (docs only) tip 1fe6444+
  mechanism: opt-in level re-Raise while owed>0 && CIM; no invent credits
  kill: DETPS2_DISABLE_M5A_DMAC=1
  out of scope: B3 assist product soft-off, GoW END tags, cap raises, Soft-GS
  dual-ACK required before Core
```

---

*Design only. Supersedes nothing in parent until dual-ACK; refines parent Phase 1 candidate A/C with session evidence.*

---

## 7. S6.1 implement note (2026-08-04, tip b36dfd1)

**Landed:** opt-in level catch-up. Full smoke suite green; `Dmac_LevelCatchup_DefaultOff_NoOp` smoke.

**File-scope amendment (Claude review seq0058):** §2.2 originally listed EmotionEngine out of v1. Implement **must** call catch-up from EmotionEngine after viaDmacFallback Acknowledge because that Acknowledge edge-clears any re-Raise performed inside `TryTakePendingDmacHandler`. Intc.Acknowledge is source-generic and lacks take/owed context — not a better seam. Confirmed necessary (not convenient); table updated.

**Flags (process start):** `DETPS2_DMAC_LEVEL_CATCHUP=1` (static readonly at type load, same pattern as `TRACE_DMAC`); kill `DETPS2_DISABLE_M5A_DMAC=1`.
