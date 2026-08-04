# M5-a Q1–Q5 evidence rollup (provisional dual-ACK)

**Date:** 2026-08-04  
**Author:** grok (docs only; parallel to Claude M7 Slice 3 design)  
**Tip at write:** `55d8f9b`  
**Mode:** synthesis of existing S1 TRACE samples — **no Core changes, no GameQuirks edits**  
**Design parent:** `docs/infra-audits/m5a-dmac-vif-gif-completion-design.md` §8  
**Sources (measurement only):**

| Sample | Path | Budget | Tip |
|--------|------|--------|-----|
| B3 TRACE | `m5a-b3-trace-dmac-sample.md` | diagnose 20M | `f19144e` |
| Haven TRACE diagnose | `m5a-haven-trace-dmac-sample.md` | diagnose 20M (pre-DMA empty) | `64184b7` |
| Haven TRACE claim | `m5a-haven-trace-dmac-claim-sample.md` | claim 100M | `d93255d` / claim seat |

**Status of this doc:** provisional dual-ACK answers. Does **not** unlock S6 behavior by itself. Missing: B3 claim/assist-quiet, GoW sticky TRACE (Q3), formal partner ACK on Q4–Q7.

---

## 0. One-line posture

**S1 telemetry is enough to prefer “handler take cadence / owed backlog” over “lost CIS storm” and over “pre-enable cap-4 under-count” for the two measured oracles.** Channel stress is **title-opposite** (B3 → GIF under-take; Haven claim → VIF1 under-take). **S6 remains blocked** until assist-quiet (or equivalent) and GoW bisect land; recommended first behavior experiment class is **level re-arm / catch-up under flag**, never invent credits and never Core-mirror busy RAM.

---

## 1. Evidence matrix (verbatim counters)

### 1.1 Burnout 3 diagnose 20M

| ch | finish | tryTakeCis | take/finish | owedPeak | owedNow | creditAssist | w1cWhileOwed | preEnablePromote |
|----|-------:|-----------:|------------:|---------:|--------:|-------------:|-------------:|-----------------:|
| VIF1 | 4 | 5 | 1.25† | 3 | 2 | **3** | **0** | 1 |
| GIF | 8 | 4 | **0.50** | **8** | **7** | **3** | **0** | 2 |
| SPR_FROM | 8 | 0 | 0 | 0 | 0 (preNow=8) | 0 | 0 | 0 |

† Takes ≥ finishes because assist credits inflate take path.

**B3 primary signal:** GIF under-take + owed backlog; **w1cWhileOwed=0**; pre-enable cap not binding.

### 1.2 Haven claim 100M (diagnose 20M was pre-DMA empty — superseded for Q2)

| ch | finish | tryTakeCis | take/finish | owedPeak | owedNow | creditAssist | w1cWhileOwed | preEnablePromote |
|----|-------:|-----------:|------------:|---------:|--------:|-------------:|-------------:|-----------------:|
| VIF1 | 134 | 67 | **0.50** | **64** | **63** | **1** | **1** | 0 |
| GIF | 68 | 68 | **1.00** | 1 | **0** | **0** | 0 | 0 |

**Haven primary signal:** VIF1 under-take + depth-64 backlog; GIF healthy; assist credit sparse (not inventing the backlog); one weak W1C blip.

### 1.3 Cross-title read

| Axis | B3 @20M | Haven @100M |
|------|---------|-------------|
| Under-take channel | **GIF** | **VIF1** |
| Balanced channel | VIF1 (assist-inflated) | **GIF** |
| creditAssist role | **dominant residual** (3+3) | **sparse** (1 VIF1) |
| w1cWhileOwed | 0 | 1 (VIF1) |
| Cap pressure | GIF owedPeak=8 | VIF1 owedPeak=**64** |

Same TRACE harness, **opposite channel stress**. Do not collapse Haven VIF busy and B3 flip into one channel fix without per-oracle S6 evidence.

---

## 2. Provisional answers (Q0–Q7)

| ID | Provisional answer | Confidence | What would flip it |
|----|--------------------|------------|--------------------|
| **Q0** | **ACK already implied** — S1 `DETPS2_TRACE_DMAC` landed, zero behavior, samples usable. | High | N/A |
| **Q1** (B3 wedge) | Prefer **handler cadence / owed backlog** over lost CIS (`w1c=0`) and over pre-enable cap (not binding). Assist credits **confound** pure Core under-delivery. | Medium | B3 claim + assist-quiet A/B where creditAssist=0; if GIF take/finish → 1.0 without invent, S6 may shrink to “assist residual only” for B3 |
| **Q2** (Haven busy) | **ACK: no Core busy mirror / no game-RAM poke.** Busy stays game RAM + handler path. Claim proves Finish fires; problem is **take cadence / owed**, not missing FinishChannel. | Medium-high for “no busy mirror”; medium for “cadence is root” | Measure-only busy/pending correlator at under-take windows; optional TeamIco silence A/B |
| **Q3** (GoW sticky) | **Still open — no TRACE.** Do **not** promote GoW force-finish END tags into M5-a Core. Default bias from design: treat as SECONDARY EE thrash until Finish/IRQ absence is causal. | Low (no data) | GoW TRACE during sticky `0x13F5xx` park |
| **Q4** (caps / save-state) | **Keep 8/64/4 defaults.** Haven VIF1 **hits 64**; B3 GIF **hits 8**. Do **not** raise caps without A/B. **No owed save-state in v1.** | High for “don’t raise”; high for no save-state | Evidence that depth cap *causes* drops rather than reflecting backlog |
| **Q5** (external oracle) | **Optional before S6**, not required to continue S3–S5 measurement. Prefer more TRACE (B3 claim, GoW sticky) over Play!/PCSX2 snapshot first. | Medium (policy) | Partner prefers external oracle as hard gate |
| **Q6** (assist env-off) | **Separate assist PR after Core quiet evidence**, not a prerequisite for more TRACE. CreditAssist counters already measure residual with assists loaded. | Medium | If dual-ACK wants silence A/B before any S6 sketch |
| **Q7** (default-on policy) | Any S6 behavior: **opt-in first** (`DETPS2_DMAC_LEVEL_CATCHUP=1` or equivalent), kill-switched; promote default-on only after smokes + multi-title diagnose hold. | High (safety) | Partner wants kill-switched default-on earlier |

---

## 3. What is *not* claimed

- S6 Core behavior is **not** ready.
- B3 diagnose 20M is **not** flip/MENU soak; LGDEV residual window ~22M+ not covered.
- Haven Soft-GS `LastImageTrx` residual is **orthogonal** to M5 completion (belongs M7 Slice 3 / composite — Claude’s seat).
- GoW plant / S0 / S4 residual is **orthogonal** (M4).
- `tryTakeOwed=0` on both samples means all recorded takes used CIS path; owed-only fallback never exercised — does **not** by itself prove CIS is healthy (could mean take path never walks owed).

---

## 4. Recommended next seats (priority order)

1. **B3 claim-class TRACE_DMAC (≥100M)** — same harness as Haven claim; test whether GIF under-take survives past diagnose and how creditAssist scales.  
2. **Assist-quiet or creditAssist-normalized read** — if env hooks exist, A/B creditAssist=0; else document that residual confounds B3 and that Haven is the cleaner Core under-take oracle (creditAssist=1 vs backlog 63).  
3. **GoW sticky TRACE (Q3)** — Finish/owed during `0x13F5xx` park; SECONDARY reclass or feed S6.  
4. **Only then:** S6 design sketch for **level catch-up / re-arm under flag** if under-take persists without invent credits.  
5. **Do not** start: Core busy-RAM mirror, raise depth caps, invent GIF packets, GameQuirks growth under M5-a.

---

## 5. Dual-ACK ask (to Claude when free)

Please ACK or amend:

1. Q2 provisional: **no Core busy mirror** (yes/no).  
2. Q4: keep caps; no owed savestate v1 (yes/no).  
3. Q7: opt-in catch-up first (yes/no).  
4. Next free seat after your Slice 3 design: prefer **B3 claim TRACE** vs **GoW sticky TRACE** vs other?

No user escalation required for these — technical dual-ACK only.

---

## 6. Sign-off

```text
M5-a Q1–Q5 evidence rollup (provisional) tip 55d8f9b
  sources: B3 diagnose TRACE + Haven claim TRACE
  Q1: cadence/owed backlog (not lost-CIS, not pre-enable cap) — medium, needs quiet A/B
  Q2: no Core busy mirror; Haven VIF1 under-take 0.50 — medium-high
  Q3: open (no GoW TRACE)
  Q4: keep 8/64/4; no owed savestate v1
  Q5: external oracle optional
  Q6: assist env-off separate; TRACE residual counters first
  Q7: opt-in catch-up first
  S6 blocked. No Core. No GameQuirks. Parallel to Claude M7 Slice 3 design.
```

---

*Docs-only rollup. Does not supersede the design parent; feeds dual-ACK for Qs before any S6 PR.*
