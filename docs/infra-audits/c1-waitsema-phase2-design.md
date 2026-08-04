# C1 design — IOP WaitSema / SleepThread import intercept → ParkAndYield (phase 2)

**Status:** design only — **ready for dual ACK** — **no Core this turn**  
**Date:** 2026-08-04  
**Tip ref:** `26706cb` (CreateThread trampoline landed)  
**Depends on:** C1 CreateThread HLE (`DETPS2_IOP_CREATE_THREAD`), yield hooks (`ParkAndYieldToReady`), yield-start residual  
**Evidence:** `c1-start-trace-bo2.md` (IOPFILE/SDRDRV hit 100k); CreateThread alone may make READY peers but parent `_start` still spins unless it **yields** (WaitSema / SleepThread)  
**Locks:** no `Intc.cs` / `EmotionEngine.cs` / Gif/Gs/GameQuirks. Prefer `Iop.cs`, `IrxLoader.cs`, `SifRpc` LoadIrx post-link only.

---

## 0. One-line problem

CreateThread trampoline can plant **READY peers**, but residual yield-start only helps if the **running** `_start` context **stops monopolizing** the R3000 budget. Real IRX typically blocks via **`thsemap` WaitSema** or **`thbase` SleepThread**. Without intercepting those imports into DetPS2’s cooperative table, parent stays RUN forever while peers stay READY-but-never-scheduled under a single-context Step loop.

---

## 1. Goal

When multi-thread + product flag (below):

1. Real IRX `WaitSema` (or minimal SleepThread) → `Iop.ParkAndYieldToReady()` so a READY peer runs.  
2. Real IRX `SignalSema` (minimal) → wake one WAIT peer to READY (or count++ stub) so parent can resume.  
3. Flag-off / THREADS off / CREATE_THREAD-only: **byte-identical** (no WaitSema override).  
4. Full sema count/max/queue fidelity is **stretch** — v1 may approximate; success is **yield surface**, not perfect THREADMAN.

**Success bar (later canary):** BO2 under THREADS+YIELD_START+CREATE_THREAD+WAIT_YIELD: IOPFILE residual or peer switch observed; ideally budget not pure `ret=False` or residual queue non-empty. LiveRpcHits still stretch.

---

## 2. Ordinal ground-truth (this seat — export scan)

`tools/bios-extract/THREADMAN.irx` via `load-irx --scan-exports` (same image as CreateThread scan):

### `lib=thsemap v1.1` (13 exports) — decomp match THREADMAN.md

| Ord | Export addr (image) | Decomp FUN | Name |
|----:|--------------------:|------------|------|
| 4 | `0x13060` | `FUN_00003060` | **CreateSema** |
| 5 | `0x13164` | `FUN_00003164` | **DeleteSema** |
| 6 | `0x1328C` | `FUN_0000328c` | **SignalSema** |
| 7 | `0x13374` | `FUN_00003374` | **iSignalSema** |
| **8** | **`0x13444`** | **`FUN_00003444`** | **WaitSema** ✓ |
| 9 | `0x135B4` | `FUN_000035b4` | PollSema |
| 11–12 | `0x136A4` / `0x1373C` | Refer* | ReferSemaStatus |

### `lib=thbase` (already scanned)

| API | Ord |
|-----|----:|
| SleepThread | **24** |
| WakeupThread | **25** |

**v1 intercept set (bias):**

| Lib | Ord | HLE |
|-----|----:|-----|
| **thsemap** | **8** WaitSema | `ParkAndYieldToReady` (always park for v1; optional later: count gate) |
| **thsemap** | **6** SignalSema | Wake one WAIT→READY **or** no-op count++ stub (pick in dual-ACK) |
| **thbase** | **24** SleepThread | Same as WaitSema park (optional same PR) |

**Out of v1:** CreateSema/DeleteSema full objects, PollSema, iSignalSema (can alias Signal), event flags, mbx.

---

## 3. Mechanism sketch

### 3.1 Where to intercept

Same pattern as CreateThread (**Option A**): after `LinkImports`, re-point import stubs → low-RAM HLE trap PCs; `Iop.Step` handles traps before fetch.

| Flag | Default | Role |
|------|---------|------|
| `DETPS2_IOP_THREADS=1` | off | Multi-context table (required) |
| `DETPS2_IOP_CREATE_THREAD=1` | off | READY peers (already landed) |
| **`DETPS2_IOP_WAIT_YIELD=1`** | **off** | WaitSema/Sleep → ParkAndYield (+ optional Signal wake) |
| `DETPS2_DISABLE_IOP_WAIT_YIELD=1` | unset | Kill |
| `DETPS2_IOP_YIELD_START=1` | off | Residual start (already landed) |

**Mandatory:** WAIT_YIELD does **not** install on THREADS alone. Separate product flag (same CT-Q2 discipline as CREATE_THREAD).

**Independence:** WAIT_YIELD may be useful even without CREATE_THREAD if peers exist another way (secondary context / synthetic); CREATE_THREAD without WAIT_YIELD may still leave parent spinning — canaries should try both stacks.

### 3.2 HLE trap PCs (proposed — free low band)

| PC | Handler |
|----|---------|
| `0xBF08` | WaitSema / SleepThread park |
| `0xBF0C` | SignalSema wake-one |

(CreateThread already uses `0xBF00` / `0xBF04`.)

### 3.3 WaitSema v1 contract

| Step | Behavior |
|------|----------|
| Entry | `$a0` = sema id (ps2sdk `WaitSema(int semid)`) — **v1 may ignore id** |
| Action | `ParkAndYieldToReady()`: current → WAIT; switch to READY peer if any |
| Alone | Stay WAIT on current; `$v0 = 0` or park-return convention TBD — must not hard-hang EE |
| Return | Set `$v0 = 0` on success path; PC = `$ra` |

**Honest limit:** without real count, WaitSema always parks → needs SignalSema (or external wake) to resume. v1 Signal: find first WAIT peer → READY; if none, no-op `$v0=0`.

### 3.4 Files (implement after dual-ACK)

| File | Change |
|------|--------|
| `IrxLoader.cs` | `OverrideThsemapWaitSignalImports` (ord 8, 6) + optional thbase 24 |
| `Iop.cs` | Trap PCs + HLE handlers; optional minimal `_waitSemaId` bookkeeping |
| `SifRpc.cs` | Call override after LinkImports when WAIT_YIELD on |
| Smokes | Wait with READY peer switches; alone parks; flag-off no patch |
| **Not** | Full KernelState sema port on IOP; EE WaitSema (already exists) |

---

## 4. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **WS-Q1** | Approve WaitSema/Sleep phase-2 design as next C1 Core after BO2 CreateThread canary results? | **Yes** (or defer if canary already unlocks residual) |
| **WS-Q2** | Opt-in `DETPS2_IOP_WAIT_YIELD=1` separate flag? | **Yes** (never fold into THREADS alone) |
| **WS-Q3** | v1 always-park WaitSema (ignore count) + Signal wakes one WAIT? | **Yes** for yield surface |
| **WS-Q4** | Include SleepThread (thbase 24) in same PR? | **Yes** if cheap (same park handler) |
| **WS-Q5** | Include SignalSema (thsemap 6) in same PR? | **Yes** — without wake, park is one-way |
| **WS-Q6** | Hold implement until BO2 CREATE_THREAD canary RESULT? | **Yes** — avoid Core thrash if residual already moves |

---

## 5. Non-goals

- Full thsemap count/max/waiter queues  
- iSignalSema interrupt-context fidelity  
- EE CreateSema/WaitSema (already KernelState)  
- Raising StartLoadedModule budget as the fix  

---

## 6. Definition of done (this design seat)

- [x] Problem tied to CreateThread residual / parent monopolize  
- [x] thsemap ordinals from THREADMAN.irx export scan (WaitSema=8, SignalSema=6)  
- [x] Flags + trap PCs + files + dual-ACK WS-Q1..Q6  
- [ ] Dual-ACK from Claude  
- [ ] **No Core** until ACK **and** preferably after BO2 canary report  

---

```text
C1 WaitSema phase-2 design (docs only)
  thsemap WaitSema=8 SignalSema=6; thbase Sleep=24
  ParkAndYieldToReady surface; flag-gated; dual-ACK before Core
```
