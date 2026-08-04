# A1 residual edges — DMAC cycle budget + GIF_STAT single-nudge

**Milestone:** A1 dual-orchestrator timing-realism  
**Scope:** read-only residual inventory (no code changes in this audit)  
**Primary code:**

| Surface | Path |
|---------|------|
| Per-Step QW cap by elapsed cycles | `src/DetPS2.Core/Dmac.cs` → `DoNormalTransfer` |
| Cost model field | `Dmac.DrainCyclesPerQw` (default `1`, save-state wire field) |
| GIF_STAT poll pump | `src/DetPS2.Core/MmioBus.cs` → GIF `…3020` read path |
| Related (not A1-fixed) | `Dmac.WriteRegister` CHCR STR start: up to `512 × Step(256)` under path3Hold / DA VIF TTE |
| FQC / PATH3 hold | `src/DetPS2.Core/Gif.cs` → `ReadStat`, `ReceivePath3Data`, `EnqueueHeldPath3` |
| Scheduler quantum | `src/DetPS2.Core/Scheduler.cs` → `SliceSize` default **64** |

**What A1 landed (baseline, for contrast):**

1. **`DoNormalTransfer`** used to ignore `Step(maxCycles)` for throughput. Progress was only the fixed priority / video-path QW cap (`4 + DPcr*4`, floor **64** for VIF0/VIF1/GIF). `DrainCyclesPerQw` was dead scaffolding (serialized, never read). A1 caps:

   ```text
   cyclesPerQw     = max(1, DrainCyclesPerQw)
   maxQwFromBudget = max(1, cycleBudget / cyclesPerQw)
   budget          = min(priorityBudget, maxQwFromBudget)
   ```

2. **GIF_STAT poll** used to loop up to **16 × `Step(128)`** (≤2048 synthetic DMAC cycles) inside a single MMIO read — enough to finish a channel and raise `INTC` DmaController mid-poll (BO2 stack race class; comment cites `orchestrator-sync.json` known_infra_gap_2). A1 is **one** `Step(128)` when `ReadStat().FQC == 0`.

This document lists residual edges that remain **after** those two fixes.

---

## 1. `DrainCyclesPerQw = 0`

### Current behavior

```csharp
// Dmac.DoNormalTransfer
uint cyclesPerQw = Math.Max(1u, DrainCyclesPerQw);
```

- Property is a public `uint` setter; save-state `ReadState` can restore `0`.
- **0 is silently remapped to 1** — same throughput as default.
- No divide-by-zero today; clamp is the only guard.

### Residual risks

| Risk | Detail |
|------|--------|
| **Semantic trap** | Callers / future tuning that treat `0` as “free / unlimited” (old dead-field intuition) get the opposite: full cost model at 1 cycle/QW. |
| **Save-state round-trip** | States that store `0` reload as effective `1` without validation or log. Netplay / rollback of a deliberately zeroed field still runs as 1. |
| **Future clamp removal** | If someone “simplifies” to raw division, `0` becomes **div-by-zero** / crash. The residual is reliance on a silent clamp with no API contract. |
| **No upper bound** | Extreme values (e.g. `uint.MaxValue`) make `cycleBudget / cyclesPerQw` floor to the `max(1UL, …)` path (see §2) — nearly stalled but never zero progress per successful Step with QWC left. |

### Honest status

Not a live fleet bug at default. Residual is **API / save-state semantics** and future footgun, not current production config.

---

## 2. `cycleBudget = 0` (and sub-unit budgets)

### Current behavior

```csharp
// Dmac.Step
if (maxCycles == 0) return 0;
// …
DoNormalTransfer(channel, ch, maxCycles);
```

```csharp
// DoNormalTransfer
ulong maxQwFromBudget = Math.Max(1UL, cycleBudget / cyclesPerQw);
```

| Entry | Effect |
|-------|--------|
| `Step(0)` | Early return; **no** channel progress; `DoNormalTransfer` never sees 0. |
| `Step(n)` with `0 < n < cyclesPerQw` | Integer division yields 0, then **floored to 1 QW**. One QW still moves per active channel per Step. |
| `Step` with large `n` | Cap only bites when `n / cyclesPerQw < priorityBudget`. |

### Residual risks

| Risk | Detail |
|------|--------|
| **Zero budget is only at Step gate** | Contract is “0 cycles = no work” only on `Step`. Internal floor guarantees **≥1 QW** whenever `Step` runs with any positive budget, even if that budget is smaller than one QW cost. |
| **Cannot model true stall** | Raising `DrainCyclesPerQw` never produces a Step that does zero QW while a channel is active and `maxCycles > 0`. Partial-slice “not enough cycles yet” is unrepresentable. |
| **IRQ / deliver still possible on tiny Steps** | `Step(1)` on GIF with `QWC==1` still finishes the segment, runs `DeliverSegment`, can raise DMA IRQ — same as a full slice for that last QW. |

### Interaction with A1 intent

A1 wanted “progress proportional to elapsed cycles.” The **1-QW floor** preserves anti-starvation (no permanently stuck active channel under positive Steps) at the cost of **never-sub-QW** accounting. Document, do not treat as cycle-accurate bus.

---

## 3. Multi-channel fairness

### Current behavior (`Dmac.Step`)

- Fixed channel order: VIF0 → VIF1 → GIF → IPU → SIF → SPR (index 0..9).
- Each **active, non-stalled** channel in the **same** `Step(maxCycles)` call receives the **full** `maxCycles` as `cycleBudget` independently.
- There is **no** shared cycle pool: *N* active channels ≈ *N ×* budgeted QW throughput per Step (each still limited by its own priority / video floor).
- `DPcr` only widens the **priority QW cap** (`4 + priority*4`), not the share of `maxCycles`.
- VIF0/VIF1/GIF force `budget = max(budget, 64)` **before** the cycle clamp — low PCR priority does not keep video paths at 4–16 QW if cycles allow.
- `BusContention.NotifyDmaActivity(active)` is post-pass telemetry / EE scaling hook, not a per-channel DMA credit drain inside `DoNormalTransfer`.
- Chain tag fetch (`DoChainTransfer`) is **not** charged against `cycleBudget` (tag parse + TTE side effects are free within the Step).

### Residual risks

| Risk | Detail |
|------|--------|
| **Parallel free bandwidth** | Two concurrent GIF+VIF1 actives each get up to `min(64, maxCycles/cyclesPerQw)` QWs in one Step — not half the bus each. Hardware is closer to arbiter + shared bus; DetPS2 is closer to “all channels step fully every quantum.” |
| **Fixed priority by index** | Lower-index channels always update first within a Step (finish → `DeliverSegment` → IRQ side effects) before higher indices run. Fairness is not time-sliced across Steps for multi-active sets. |
| **Video floor vs A1** | With default `DrainCyclesPerQw=1` and scheduler `SliceSize=64`, GIF/VIF already get **full 64 QW** per real round (`maxQwFromBudget=64`). A1 clamp is often a **no-op** for video paths under default config; multi-channel over-grant is unchanged from pre-A1 for that case. |
| **CHCR path-sync burst** | Separate from fairness: under `Path3MaskedByVif` or DA VIF TTE display TADR, STR set runs **up to 512 × `Step(256)`** on that channel’s start — massive exclusive progress outside scheduler fairness. A1 did not touch this path. |

### Honest status

A1 bound **per-call** QW to **per-call** cycles; it did **not** introduce cross-channel arbitration. Residual multi-channel fairness is **pre-existing architecture**, still live.

---

## 4. Still-instant single GIF_STAT nudge

### Current behavior (`MmioBus.Read32` GIF window)

```csharp
const ulong OneRoundNudgeCycles = 128;
if ((_gif.ReadStat() & 0x1F00_0000u) == 0)  // FQC bits 24–28
    _dmac.Step(OneRoundNudgeCycles);
return _gif.ReadRegister(address);
```

### Math under defaults (`DrainCyclesPerQw=1`, GIF priority floor 64)

| Quantity | Value |
|----------|-------|
| Nudge cycles | **128** |
| Scheduler default `SliceSize` | **64** (comment in MmioBus says “one regular scheduler round”; **mismatch**) |
| `maxQwFromBudget` | `128 / 1 = 128` |
| GIF QW after clamp | **64** (video floor) |
| Completions possible | Full ≤64 QW segment + `DeliverSegment` + `FinishChannel` + Dmac IRQ raise |

### Residual risks

| Risk | Detail |
|------|--------|
| **Still a mid-poll DMA quantum** | One MMIO read can still advance a full video-path burst and complete a channel. A1 removed the **16×** pump, not the property “STAT read runs DMAC.” |
| **IRQ timing still non-HW** | Completing on the poll that first sees FQC==0 can still deliver `INTC` DmaController in the same EE instruction window as the load from `0x10003020` — softer than 16×2048, same class of re-entrancy / handler ordering bugs if a title is sensitive. |
| **Nudge ≠ scheduler slice** | `128` vs global `SliceSize=64` means poll path is **2×** a default FixedSlice quantum (or equal if adaptive slice grew). Not calibrated to `Scheduler.SliceSize` (hardcoded constant). |
| **Every FQC==0 poll re-nudges** | Tight spin with FQC stuck 0 (segment not finished yet) grants **64 QW per load**. Throughput under pure spin is enormous vs real bus; only multi-round *completion* was slowed relative to the old 16× loop. |
| **FQC gate uses `ReadStat()`** | Masked PATH3 forces synthetic `FQC≥1` (see §5) → nudge **suppressed** under M3P/M3R even if DMAC GIF QWC is still draining. Unmasked path uses real `_fifoCount`. |

### Relative to pre-A1

| | Pre-A1 | A1 |
|--|--------|-----|
| Max DMAC Steps per STAT read | 16 | **1** |
| Max cycles manufactured | 2048 | **128** |
| Max GIF QW (defaults) | up to 16×64 if each Step took full budget | **64** |
| Instant multi-round burst | yes | **reduced, not eliminated** |

---

## 5. PATH3 FQC starvation if nudge too weak

### How FQC becomes non-zero (DetPS2)

1. **Unmasked PATH3:** `DeliverSegment` → `Gif.ReceivePath3Data` → `ProcessTransfer` (instant HLE). FIFO is usually drained; FQC often returns to 0 quickly after process. Pollers that want “FIFO busy” on unmasked PATH3 are underserved by design (instant process).
2. **Masked PATH3 (M3P/M3R):** hold queue + `EnqueueHeldPath3` sets `_fifoCount` from held QWC (capped) so `ReadStat` reports FQC; if still 0, **synthetic FQC=1** while masked.
3. **DMAC progress alone does not raise FQC:** `DoNormalTransfer` only moves MADR/QWC. **FQC changes only after the segment hits QWC==0 and `DeliverSegment` runs.**

### Nudge vs PATH3 kick sequences

| Scenario | Nudge? | Starvation mode |
|----------|--------|-----------------|
| M3P=1, FQC synthetic ≥1, GIF DMA still active | **No** (FQC gate) | Poller proceeds on synthetic FQC; real fill depends on CHCR path3Hold **512×Step(256)** or later scheduler Steps — not on STAT nudge. |
| M3P=0, GIF QWC large, FQC=0 | **Yes**, 64 QW/poll | Needs `ceil(QWC/64)` STAT loads (or scheduler Steps) before `DeliverSegment`. Infinite spin OK; **bounded** “poll N times then fail” loops can miss. |
| `DrainCyclesPerQw` raised (e.g. 8) | Yes, slower | `maxQwFromBudget = 128/8 = 16` QW/nudge → more polls per segment; bounded polls more likely to see permanent FQC=0 mid-transfer. |
| Nudge cycles reduced further (hypothetical) | — | Same class: weaker quantum → longer spin; pure spin still converges unless progress is zero (see §2 floor). |
| Segment QWC=0 already but GIF never delivered (stalled channel / wrong mode) | Depends | Nudge runs other channels; GIF may never fill FQC — **true** hang; nudge does not invent PATH3. |

### Residual risks (explicit)

| Risk | Detail |
|------|--------|
| **FQC is segment-granular, nudge is QW-granular** | Weak or rare nudges leave FQC=0 for the entire multi-Step drain of a large QWC. Titles that sample FQC a few times (not spin-forever) can conclude “empty” while DMA is legitimately in flight. |
| **Synthetic FQC under mask hides missing DMA** | `Path3Masked && fqc==0 → fqc=1` unblocks B3-style spins **without** proving PATH3 data was delivered. If path3Hold drain is disabled for a title class, poller can leave FQC spin while GIF channel still Active — desync later when unmask drains empty hold. |
| **Nudge suppressed exactly when path-sync often runs** | B3 path-sync polls FQC **after** MSKPATH3; synthetic FQC turns the A1 nudge **off**. That path relies on CHCR burst drain + hold queue, not MmioBus. Weakening CHCR burst without strengthening something else re-opens FQC starvation under mask. |
| **A1 + stronger cost model** | Pairing single-nudge with high `DrainCyclesPerQw` (true timing realism) **increases** polls-to-FQC for unmasked in-flight PATH3. Dual-orchestrator “more real” can regress titles that depended on poll-pump filling FQC quickly. |
| **Chain mode** | Tag fetch free; each data segment still needs QWC drain to 0 for deliver. Multi-tag PATH3 needs multiple Steps/nudges between tags; FQC may flap or stay 0 between segments. |

### Related docs

- `docs/graphics/PATH3_MASK_MATRIX.md` — M3P hold / FQC contract  
- `docs/irx/SOFTGS_IRX_ERA.md` — path-sync residual note on held PATH3 FIFO  

---

## 6. Cross-cutting interactions (A1 package)

```text
                    ┌─────────────────────────────┐
  Scheduler.RunFor  │ SliceSize default 64        │
                    │ each ISchedulable.Step(64)  │
                    └─────────────┬───────────────┘
                                  │
                                  v
                    ┌─────────────────────────────┐
  Dmac.Step(c)      │ all active chans full c     │  ← multi-channel fairness residual
                    │ DoNormalTransfer(c)         │
                    │ min(pri, max(1,c/DCpQ)) QW  │  ← DrainCyclesPerQw=0 → 1; floor 1 QW
                    └─────────────┬───────────────┘
                                  │ QWC→0
                                  v
                    DeliverSegment → Gif PATH3/VIF…
                                  │
  EE load GIF_STAT  ┌─────────────┴───────────────┐
                    │ if FQC==0: Step(128) once    │  ← still-instant single nudge
                    │ (mask synthetic FQC skips)   │  ← PATH3 FQC starvation class
                    └─────────────────────────────┘

  CHCR STR start    ── optional 512×Step(256) ──►  still multi-round instant (not A1)
```

### Default-config effectiveness note

Under **shipping defaults** (`DrainCyclesPerQw=1`, `SliceSize=64`, GIF/VIF floor 64):

- Real scheduler rounds already grant the full video QW cap; A1’s cycle clamp **rarely reduces** GIF/VIF progress.
- A1’s **meaningful** win is blocking **artificial multi-`Step` pumps** (old 16× STAT loop, and any future caller that spammed `Step` hoping for free QW per call — each call still pays `maxCycles`).
- Residuals above matter most when: cost model is tuned up, slices shrink, multi-channel bus accuracy is claimed, or STAT nudge is weakened further.

---

## 7. Residual checklist (do not plant; document only)

| ID | Edge | Severity (fleet today) | Next honest step (when owned) |
|----|------|------------------------|--------------------------------|
| A1-R1 | `DrainCyclesPerQw=0` silent → 1 | Low | Validate on set/load; document “0 illegal” or define free vs floor |
| A1-R2 | `cycleBudget` floor 1 QW / positive Step | Low–Med | Optional carry of residual cycle debt across Steps |
| A1-R3 | Multi-channel full budget each | Med (accuracy) | Shared bus credits / arbiter; charge chain tags |
| A1-R4 | Single STAT nudge still ≤64 QW + IRQ | Med (timing races) | Decouple completion IRQ from poll Step; or charge nudge to master cycles |
| A1-R5 | PATH3 FQC vs weak/suppressed nudge | Med (path-sync) | FQC progress during drain; keep mask synthetic explicit; don’t cut CHCR burst without replacement |
| A1-R6 | Nudge 128 ≠ SliceSize 64 | Low | Tie constant to `Scheduler.SliceSize` or document 2× intent |
| A1-R7 | CHCR 512×Step(256) bypasses A1 spirit | Med | Same dual-orchestrator milestone family; not fixed by A1 |

---

## 8. Sources (code anchors)

| Topic | Location |
|-------|----------|
| `DrainCyclesPerQw` default / save | `Dmac.cs` ~L35, L87, L103 |
| `Step` / zero gate / per-channel loop | `Dmac.cs` ~L180–L243 |
| A1 QW clamp | `Dmac.cs` `DoNormalTransfer` ~L456–L493 |
| CHCR multi-Step drain | `Dmac.cs` ~L700–L719 |
| GIF_STAT single nudge | `MmioBus.cs` ~L89–L115 |
| FQC compose + mask floor | `Gif.cs` `ReadStat` ~L548–L568 |
| PATH3 hold → FIFO count | `Gif.cs` `EnqueueHeldPath3` ~L381–L411 |
| Scheduler quantum | `Scheduler.cs` `SliceSize` default 64 ~L29–L36 |

---

*Audit only — no production code changes. Revisit when dual-orchestrator work touches DMAC cost, STAT poll, or CHCR path-sync drain.*
