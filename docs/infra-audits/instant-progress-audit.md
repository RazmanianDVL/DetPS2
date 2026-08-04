# Instant multi-round progress audit

**Date:** 2026-08-04  
**Scope (read-only):** `MmioBus.cs`, `Gif.cs`, `Sif.cs`, `Pcrtc.cs`, `EmotionEngine.cs`, `Scheduler.cs`, plus closely related MMIO/syscall Step sites required to classify the GIF_STAT class of bug.  
**No Core code changes** in this pass.

## Pattern definition

An **instant multi-round progress** bug is any path that, inside a single MMIO access or a single EE instruction/syscall, manufactures many scheduler-equivalent rounds of device work (e.g. loops of `Dmac.Step` / `component.Step`), so channel completion, IRQs, or RPC replies become visible at a wall-clock / master-cycle moment real hardware cannot produce.

Canonical historical example (now mitigated):

| Site | Was | Now |
|------|-----|-----|
| `MmioBus.Read32` GIF_STAT (`…3020`) | Up to **16×** `Dmac.Step(128)` (~2048 cycle-units + possible channel IRQ) inside one STAT read | Single bounded nudge: one `Dmac.Step(128)` only when FQC==0 |

Comments at the site name this the **"instant multi-round burst"** / **"GIF_STAT poll-pump"** and link it to Blood Omen 2 stack-corruption race work (orchestrator A1 timing-realism milestone).

---

## Findings (requested files)

### 1. `MmioBus.cs` — residual single-round GIF_STAT nudge

| Field | Value |
|-------|--------|
| **Location** | `src/DetPS2.Core/MmioBus.cs:89–114` |
| **Code** | On GIF control window read, if `(address & 0xFF) == 0x20` (GIF_STAT) and `(_gif.ReadStat() & 0x1F00_0000) == 0` (FQC==0), call `_dmac.Step(128)` once. |
| **Risk** | **Low** (residual) / **Med** if combined with other force-steps in the same tight poll |
| **Class** | Poll-side DMAC advance (no longer multi-round) |
| **Suggested fix class** | **Scheduler-visible progress only.** Prefer: (a) drop read-side nudge entirely once path-sync titles survive on pure RR slices + cycle-capped `DoNormalTransfer`; or (b) record a "nudge debt" that the next scheduler DMAC slice absorbs without inventing IRQs mid-load/store. Do not reintroduce multi-round loops on STAT read. |

Notes: Multi-round loop is gone. Remaining concern is side-effecting a **read** (non-idempotent STAT poll) and finishing a small amount of DMA + possible IRQ edge inside the load instruction.

---

### 2. `Gif.cs` — instant HLE complete + poll-friendly FQC, no Step loops

| Location | Behavior | Risk | Fix class |
|----------|----------|------|-----------|
| `Gif.cs:548–568` `ReadStat` | While PATH3 masked and real FQC==0, **fabricate FQC=1** so path-sync pollers exit. | **Med** (fake status; not multi-Step) | **Honest FIFO fill:** FQC should track held/queued QWs only; starve-avoidance belongs in DMAC/scheduler, not status invent. |
| `Gif.cs:626–634` `WriteFifo` | Masked: hold words (good). Unmasked: drain on QW boundary. | **Low** | Keep; ensure drain cost eventually surfaces via `Step`/work-cost, not free completion. |
| `Gif.cs:637–644` `DrainFifoQuadwords` | Instant-empty FIFO ("instant-process" comment). | **Low** | Defer to time-budgeted drain in `Gif.Step` or DMAC deliver quanta. |
| `Gif.cs:687–697` `ReceivePath3Data` | Unmasked: **process entire transfer immediately** (`ProcessTransfer` in call). Comment: "instant HLE". | **Med** | **Segment-budgeted process:** process ≤N QW per DMAC segment/Step; sticky mid-packet already exists for Path2 — extend discipline to unmasked Path3 size. |
| `Gif.cs:706–739` Path2 | Same immediate `ProcessTransfer` for full QWC segment. | **Med** (same class) | Same as Path3; VIF1 often slices QW-by-QW already. |
| `Gif.cs:780–796` Path1 | Immediate full process. | **Low–Med** | Same time-budget class. |
| `Gif.cs:813+` `ProcessTransfer` | `while (remaining > 0)` drains all available QWs in one call. | **Med** when called with large QWC from one DMA segment finish | Cap body drain per invocation; leave sticky for next slice. |
| `Gif.cs:1071–1079` `Step` | Only converts last QWC into GS work-cost report; **does not** time-slice GS work. | **Low** (reporting only) | Optionally real drain-from-queue in `Step` when de-instantizing Path3/FIFO. |

**No** `for`/`while` calling `Dmac.Step` / peer `Step` inside GIF MMIO. Instant-progress risk is **HLE complete-in-one-call**, not multi-round Step.

---

### 3. `Sif.cs` — bounded RPC drain in `Step`; intentional anti-instant RPC queue

| Location | Behavior | Risk | Fix class |
|----------|----------|------|-----------|
| `Sif.cs:43–72` `_realRpcQueue` docs | Explicitly forbids answering real RPC in the same EE instruction / same scheduler generation that submitted it. | **Clean** (by design) | Keep generation gate; do not move drain into MMIO or `PerformSifSetDma` sync path. |
| `Sif.cs:383–400` `TryDequeueRealRpc` | Refuses same-generation packets. | **Clean** | — |
| `Sif.cs:612–637` `Step` | Drains up to **16** simplified `_rpcPacketAddrs` HLE packets per Step call. | **Low** when only scheduler calls `Step`; **Med** when syscall HLE calls `Sif.Step(N)` mid-instruction (see related sites) | **One packet per slice** (or cost-proportional) for simplified RPC; keep real RPC on generation gate. |
| MMIO read/write paths in `Sif` | No `component.Step` loops observed on register access. | **Clean** | — |

---

### 4. `Pcrtc.cs` — no multi-round Step; sticky VBlank for pollers

| Location | Behavior | Risk | Fix class |
|----------|----------|------|-----------|
| `Pcrtc.cs:90–97` `EndVblank` | Does not auto-clear VBlankStart (busy-pollers). | **Low** (sticky IRQ, not multi-Step) | Hardware-ish sticky STAT is acceptable; document only. |
| `Pcrtc.cs:99–128` `Step` | Accumulates cycles; raises Start/End at half/full period; re-asserts Start if cleared mid-vblank. | **Low** | Avoid inventing multiple vblank edges inside one slice; current half-period logic is single-edge per threshold. |

**No** nested `Step` loops. Not an instant multi-round DMAC/GIF pattern.

---

### 5. `EmotionEngine.cs` — normal instruction loop; VU0 interlock only

| Location | Behavior | Risk | Fix class |
|----------|----------|------|-----------|
| `EmotionEngine.cs:531+` `Step` | Instruction loop up to `maxCycles`; stalls (vblank HLE, sema, thread) burn cycles without device multi-Step. | **Clean** | — |
| `EmotionEngine.cs:662–670` | COP2 interlock: `_vu0.Step(1)` per EE cycle while interlocked. | **Clean** (1:1, not multi-round burst) | — |
| Mid-instruction hooks / stalls | No `Dmac.Step` / `Sif.Step` / multi-round device pumps in EE core. | **Clean** | Device progress must stay in scheduler components (or known HLE syscall sites outside EE.cs). |

---

### 6. `Scheduler.cs` — legitimate multi-component Step (not a bug)

| Location | Behavior | Risk | Fix class |
|----------|----------|------|-----------|
| `Scheduler.cs:100–127` `RunForFixedSlice` | Advances master cycles in slices (`SliceSize` default **64**). | **N/A** (correct) | Keep slice small for commercial SIF/IOP (comments warn SliceSize=512 breaks Whip/Haven). |
| `Scheduler.cs:175–199` `StepComponents` | One `component.Step(budget)` per registered component per slice. | **N/A** | Do not call `StepComponents` from MMIO. |
| Event fire callbacks | Could theoretically re-enter device work; no evidence of multi-round DMAC pump in scheduler itself. | **Low** | Callbacks should not force-finish DMA. |

---

## Related high-impact site (same bug class; MMIO write path)

### 7. `Dmac.cs` — CHCR STR force multi-round Step loop (**primary remaining**)

| Field | Value |
|-------|--------|
| **Location** | `src/DetPS2.Core/Dmac.cs:700–719` (`WriteRegister` CHCR, STR set) |
| **Code** | After `StartTransfer`, if `(path3Hold \|\| daDisplayVif)` and channel is VIF0/VIF1/GIF: **`for (i = 0; i < 512 && Active; i++) Step(256);`** |
| **Bound** | Up to **512** Step calls × **256** cycle-units ≈ **131072** cycle-units of DMAC progress (and chained tag fetch / deliver / `FinishChannel` / INTC raise) inside **one CHCR write** (one EE store via `MmioBus`). |
| **Risk** | **High** |
| **Why it matches the old GIF_STAT pattern** | Explicit multi-round `Step` loop on an MMIO edge to prevent path-sync / DA display STR stick; comments cite B3 path-sync and DA VIF1 TTE. Complements the already-fixed STAT poll-pump; A1 comments in `DoNormalTransfer` (`Dmac.cs:464–476`) note multi-call Step was the bypass for cycle budgeting — **this loop is still that bypass**. |
| **Collateral** | `Dmac.cs:316–320`, `339–343`, `SonyKernelHle.cs:577–579` document **path-sync force-step** completions racing D_STAT W1C → owed-handler soft queue. That soft queue exists **because** force-step finishes channels too early relative to EE dispatch. |
| **Suggested fix class** | **(A) Bounded single-round kick** (mirror post-A1 GIF_STAT: at most one `Step(slice)` on STR, or zero). **(B) Scheduler priority / event:** on STR for masked PATH3 or DA TTE, schedule DMAC-heavy slices without inventing N rounds in the write handler. **(C) Honest chain progress:** keep STR set across real master-cycle slices; fix title hangs by correct FQC/CIS/IRQ level timing, not instant drain. Cap any transitional loop far below 512 and under remaining cycle budget. |

Also note:

| Location | Behavior | Risk | Fix class |
|----------|----------|------|-----------|
| `Dmac.cs:456–480` `DoNormalTransfer` | Per-Step QW cap now includes `maxCycles / DrainCyclesPerQw` (A1 fix). | **Fixed / Low residual** | Keep; only effective if callers stop multi-Step bursts. |
| `Dmac.cs:461–463` | GIF/VIF0/VIF1 budget floor 64 QW per Step. | **Low–Med** with force-step loop | Lower floor once multi-round CHCR loop is removed. |

---

## Related syscall HLE (single-instruction device Step; not multi-round loops)

These are outside the six-file focus but complete the "progress inside one EE instruction" picture:

| Location | Behavior | Risk | Fix class |
|----------|----------|------|-----------|
| `BiosHle.cs:312,409,421,484` | `Sif.Step(16\|32\|64)` from syscalls | **Med** | Defer to scheduler generation / ambient `Sif.Step`; return "busy" until next slice. |
| `SonyKernelHle.cs:1606` | `Sif.Step(64)` at end of SifSetDma path | **Med** | Prefer only `DrainRealRpcQueue` (already generation-gated); avoid bulk simplified RPC complete in syscall. |
| `Ps2System.cs:1028` (and similar boot helpers) | One-shot `Sif.Step` | **Low** (boot) | Boot-only OK; not hot path-sync. |

---

## Summary table

| File:line | Pattern | Risk | Suggested fix class |
|-----------|---------|------|---------------------|
| `MmioBus.cs:89–114` | GIF_STAT read: single `Dmac.Step(128)` if FQC==0 (was 16×) | **Low** residual | Remove read-side nudge or fold into next scheduler DMAC slice; never restore multi-round loop |
| `Dmac.cs:717–718` | CHCR STR: **up to 512× `Step(256)`** for path3Hold / daDisplayVif VIF/GIF | **High** | Single-round kick or scheduler-driven drain; retire force-step + owed-handler race hacks over time |
| `Dmac.cs:456–480` | Cycle-capped QW drain (A1); defeated by multi-Step callers | **Low** (fixed core) | Preserve; depends on removing multi-Step call sites |
| `Gif.cs:548–568` | Masked PATH3 fabricates FQC≥1 | **Med** | Honest held-QW FQC only |
| `Gif.cs:687–697` (+ Path1/2 ProcessTransfer) | Instant full-transfer HLE process | **Med** | Time-/QW-budgeted process per DMAC segment or `Gif.Step` |
| `Gif.cs:637–644` | Instant FIFO empty | **Low** | Budgeted FIFO drain |
| `Gif.cs:1071–1079` | `Step` = work-cost report only | **Low** | Optional real deferred work |
| `Sif.cs:612–637` | Up to 16 HLE RPC completes per `Step` | **Low–Med** | 1 packet / slice; keep real-RPC generation gate (`Sif.cs:383–400`) |
| `Sif.cs` MMIO | No Step loops on register R/W | **Clean** | — |
| `Pcrtc.cs:99–128` | VBlank sticky re-raise for pollers | **Low** | Sticky STAT OK; no multi-Step |
| `EmotionEngine.cs:662–670` | `_vu0.Step(1)` per interlock cycle | **Clean** | — |
| `EmotionEngine.cs` (rest) | No DMAC/SIF multi-round pumps | **Clean** | — |
| `Scheduler.cs:175–199` | One Step per component per slice | **N/A** (correct) | Do not invoke from MMIO |
| `BiosHle.cs` / `SonyKernelHle.cs:1606` | `Sif.Step(N)` inside syscall | **Med** | Scheduler-deferred completion |

---

## Verdict

1. **GIF_STAT multi-round poll-pump is fixed** down to a single optional `Step(128)` on FQC==0 (`MmioBus.cs`).
2. **The same bug class still exists at full strength on DMAC CHCR write** (`Dmac.cs:717–718`: up to 512× `Step(256)`), which is the highest remaining instant multi-round progress site in the graphics/path-sync path and is reached through normal MMIO stores.
3. **Gif** contributes **instant-complete HLE** (process whole segment in `ReceivePath*`) and **fake FQC**, not Step-loops.
4. **Sif / Pcrtc / EmotionEngine / Scheduler** do not reintroduce GIF_STAT-style multi-round Step pumps on MMIO; Sif’s real-RPC generation gate is the correct anti-pattern. Residual risk is syscall-time `Sif.Step(N)` and simplified RPC batching.
5. **Recommended priority:** de-risk **`Dmac` CHCR force-step** first (High), then **Gif instant ProcessTransfer / FQC fabricate** (Med), then residual **MmioBus** nudge + **syscall Sif.Step** (Low–Med).

---

## Search notes (method)

- Grep for `Step(`, `for`/`while` around Step, `force-finish`, `poll-pump`, `instant multi-round`, `OneRoundNudge`, path-sync comments.
- Full read of GIF_STAT block (`MmioBus`), CHCR STR block (`Dmac`), `ReadStat`/`ReceivePath*`/`Step` (`Gif`), RPC queue + `Sif.Step` (`Sif`), `Pcrtc.Step`, `Scheduler.StepComponents`, EE COP2 interlock.
- No modifications to `src/DetPS2.Core/**`.
