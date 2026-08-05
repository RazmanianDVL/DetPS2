# B3 — ISR × WakeupThread design note (pre-Core)

**Status:** design / investigation — **no Core yet**; dual-ACK required  
**Date:** 2026-08-05  
**Parents:** `gfx-l2c-b3-frame-dispfb-stall-finding.md` §§10–12 (Grok RA hist + Claude 0x2370A0 watch)  
**Authors:** Grok (this note); Claude measurements in §12  

---

## 0. One-line

Claude’s live evidence (ISR runs, all 4 slots visited, WakeupThread jal fires) is strong.  
C# **`KernelState.WakeupThread` does not context-switch**, and **`MaybePreempt` refuses to run mid-INTC episode** — so “WakeupThread itself abandons the ISR” is **not** supported by the current HLE as written. Next measure must separate: (A) real mid-BIOS abandon, (B) watch-tooling false negative on flag bytes, (C) flag store runs but poller never sees it.

---

## 1. Settled facts (dual-orch evidence)

| Fact | Source |
|------|--------|
| 99.8% SleepThread RA = `0x00237188` | Grok RA hist, §10 |
| Waiter polls `(gp-23820)+slot` until ≠0 | Disasm §10.2 |
| Producer ISR = `0x002370A0` (VBlankStart chain) | Disasm + HLE comments |
| ISR dispatches (~74× / 35M); `s0` visits 0..3 each time | Claude §12.1–12.3 |
| Table `0x01D80700` = tids {3,4,5,6} | Claude dump + Grok dump |
| Multi-handler append walks 4 handlers including 0x2370A0 | Claude INTC_DISPATCH |
| DIRECT-end-truncated typo real, causal-refuted for B3 | Claude §9 |

---

## 2. C# map for `jal 0x0010CCD0`

| Layer | Location | Behavior |
|-------|----------|----------|
| Game ISR | `0x2370F0` `jal 0x10CCD0` | Intended WakeupThread(tid) |
| BIOS stub | `0x10CCD0` | Real BIOS: save ra, **`syscall`** (v1=−47 → #0x33), check return |
| HLE | `SonyKernelHle` case **0x33 / 0x34** | `_kernel.WakeupThread((int)a0)` only — **no** `SwitchToNext` |
| Kernel | `KernelState.WakeupThread` (`KernelHle.cs:841`) | Pure SleepThread: clear `Sleeping` if `SuspendCount==0`; else `WakeupCount++`. WaitSema routed to `SignalSema`. **No PC change.** |

Contrast: **SleepThread** HLE *does* call `SwitchToNext` when the sleeper is parked. WakeupThread does not.

---

## 3. Mid-ISR preemption guard (already in tree)

`KernelState.MaybePreempt` (`KernelHle.cs:1374–1379`):

```csharp
// Never force-preempt across an HLE INTC episode.
if ((ee.COP0_Status & 0x2) != 0 || ee.HasOutstandingIntcDispatch)
    return;
```

`HasOutstandingIntcDispatch` ≡ `_savedGprAcrossIntcDispatch.Count > 0` (pushed in `TryDispatchRegisteredIntcHandler` before jumping to handler).

**Implication:** Once 0x2370A0 is running under a normal INTC dispatch frame, timeslice preemption is **supposed to be impossible**. A pure “WakeupThread marks tid 4 ready → MaybePreempt steals the core” story **contradicts this guard** unless EXL/stack is wrong mid-handler.

---

## 4. ISR epilogue (why flag store should be hard to skip)

After `jal 0x10CCD0` returns to `0x2370F8`:

```
2370F8  addiu v0, gp, -23820
2370FC  addiu v1, zero, 1
237100  addu  v0, v0, s0
237104  b     0x2370D0          ; always taken
237108  sb    v1, 0(v0)         ; delay slot — MIPS always executes
```

If control returns from BIOS WakeupThread into `0x2370F8`, the **`sb` must execute** (delay slot). Skipping the store without skipping the whole return path needs either:

- never returning from `0x10CCD0` / syscall path, or  
- returning with PC/ra corrupted so `0x2370F8+` never runs, or  
- store executes but **watch instrumentation mis-attributes** which byte was written.

---

## 5. Tooling caveat (Write8 watch)

`SystemMemory.Write8` (`SystemMemory.cs:287`):

```csharp
(vaddr & 0xFFFFFFFFUL & ~3UL) == WatchAddr.Value
```

| WatchAddr set to | Effect |
|------------------|--------|
| `0x4E2964` (word base) | Hits on **any** of the 4 flag bytes in that word |
| `0x4E2965` / `66` / `67` | **Never matches** Write8 (aligned base ≠ exact unaligned addr) |

So:

- “398 hits all at 0x4E2964” **cannot alone prove only slot 0** was written.  
- Separate watches on `0x4E2965+` with current Write8 logic **always show zero** even if those bytes are written.

**Required re-measure before Core:** dump flag bytes as a u32 (`Read32(0x4E2964)`) on a timer, or fix watch to exact-byte match for Write8, or log PC+vaddr on every Write8 in that word without ~3.

Table slot watches (`Write32` at `0x1D80704`) use exact vaddr match — those remain credible.

---

## 6. Hypotheses (ranked)

| ID | Hypothesis | Compatible with HLE? | Next test |
|----|------------|----------------------|-----------|
| **H1** | Mid-ISR **context switch** abandons epilogue after WakeupThread | **Weak** — WakeupThread no switch; MaybePreempt blocked mid-INTC | Log `CurrentThreadId`+PC across `0x2370F0..108`; assert EXL+stack depth stay constant |
| **H2** | **Watch false negative** — flag stores for slots 1–3 happen but mis-watched | **Strong tooling** | Word dump / exact-byte watch; correlate with PC=`0x237108` |
| **H3** | Store runs but **wrong GP/s0** so bytes land elsewhere | Medium | On `0x237108`, log gp, s0, computed v0 |
| **H4** | **Nested INTC / multi-handler re-Raise** re-enters 0x2370A0 or another handler before epilogue completes | Medium | Trace `_savedGprAcrossIntcDispatch.Count` and nested `[INTC_DISPATCH]` during 0x2370A0 |
| **H5** | Waiter “success” path (table −1 / re-reg) is **not** proof of flag for that slot (different code path) | Check | Map exact PCs Claude saw (`0x2371B8`, `0x2371DC`) to waiter-only vs shared |

---

## 7. Proposed measure plan (still no Core)

1. **Flag word dump** every VBlank: `u32` at `0x4E2964` + table `0x1D80700..0F` (product dump path, no permanent Core).  
2. **PC-gated log** (temp, fully reverted): hit `0x00237108` with `s0`, `gp`, store addr, tid. Count per `s0`.  
3. **EXL / intc stack depth** log at entry/exit of `0x10CCD0` and at `0x237108`.  
4. Only if H1 confirmed: design Core fix (never switch mid-INTC; or complete ISR epilogue before yield) with multi-title smokes.

---

## 8. Explicit bans

- Invent DISPFB flip / present page `0x46` mismatch.  
- “Fix” B3 by title-local WakeupThread plant without proving EE mechanism.  
- Land Gif DIRECT-end-truncated string fix in the same commit as any ISR change (separate seat).  

---

## 9. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **ISR-Q1** | Accept that HLE `WakeupThread` alone does **not** SwitchToNext, so pure H1 needs another agent (preempt guard failure / nested dispatch / non-return)? | **Yes** |
| **ISR-Q2** | Re-measure flag stores with word dump / exact-byte watch before any Core? | **Yes — mandatory** |
| **ISR-Q3** | After re-measure, if `sb` never hits for s0≠0 with EXL held, open Core design for mid-ISR control-flow loss? | **Yes when dual free** |

```text
B3 ISR×WakeupThread design
  WakeupThread HLE: clear Sleeping only — no SwitchToNext
  MaybePreempt: blocked when EXL or outstanding INTC dispatch
  Write8 watch ~3 alignment: cannot attribute flag slot alone
  next: re-measure flag stores + log s0 at 0x237108 — dual-ACK before Core
```
