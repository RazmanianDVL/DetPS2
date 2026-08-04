# C1 design — IOP THREADMAN `CreateThread` / `StartThread` → READY peer (for yield-start)

**Status:** design only — **ready for dual ACK** — **no Core this turn**  
**Date:** 2026-08-04  
**Tip ref:** `a845d74` / `24a8427`  
**Depends on:** C1.1–C1.3 scaffolding, C1 yield-start residual (`DETPS2_IOP_YIELD_START`)  
**Evidence:** `c1-start-trace-bo2.md` (IOPFILE hit budget, no READY peer), yield-start residual only fires when `FindNextReadyThread() >= 0`  
**Locks:** no `Intc.cs` / `EmotionEngine.cs`. Prefer `Iop.cs`, IOP HLE / import stubs, `SifRpc` module start only if needed.

---

## 0. One-line problem

Yield-start residual is **implemented** but **inert on IOPFILE** until something creates a **READY** peer during `_start`. Real modules call THREADMAN `CreateThread` + `StartThread` (or Sleep/WaitSema) via import stubs; DetPS2’s IOP multi-context table only grows when **our** `CreateSecondaryContext` / `CreateThreadContext` runs. Without intercepting the real import, checkpoints never see a yield surface.

---

## 1. Goal

When `DETPS2_IOP_THREADS=1` (and optional product flag below):

1. Real IRX `CreateThread` (THREADMAN export) → allocate `Iop` thread slot with entry PC + stack from args (or unique stack).  
2. Real IRX `StartThread` → mark READY (or switch policy TBD).  
3. `StartLoadedModule` / residual path then sees `FindNextReadyThread() >= 0` and can enter residual slices.  
4. Flag-off / THREADS off: **byte-identical** (stubs stay HLE no-op or existing behavior).

**Success bar (later canary):** BO2 under THREADS+YIELD_START: IOPFILE no longer pure `hit budget 100k ret=False` **or** residual queue non-empty during start; ideally `firstQueue != 0` or LiveRpcHits>0 (may need further WaitSema intercept — phase 2).

---

## 2. Mechanism sketch

### 2.1 Where to intercept

| Option | Description | Bias |
|--------|-------------|------|
| **A. Import stub in IrxLoader link** | When linking `thbase`/`threadman` CreateThread/StartThread, point to DetPS2 trampoline that calls `Iop.CreateThreadContext` | **Preferred** — stays on IOP import path |
| **B. Syscall-only** | Only if IRX uses EE-style syscalls (unlikely for IOP IRX) | No |
| **C. PC-band GameQuirks** | Title-local | **Banned** |

### 2.2 Flag table

| Env | Default | Role |
|-----|---------|------|
| `DETPS2_IOP_THREADS=1` | off | Prerequisite multi-context table |
| `DETPS2_IOP_CREATE_THREAD=1` | **off** | Enable Create/StartThread intercept → READY peers |
| `DETPS2_DISABLE_IOP_CREATE_THREAD=1` | unset | Kill intercept |
| `DETPS2_IOP_YIELD_START=1` | off | Residual start (already landed) |

### 2.3 CreateThread contract (minimal)

Ground-truth against `docs/bios-ports/THREADMAN.md` + Ghidra THREADMAN exports (IOP side):

| Step | Behavior |
|------|----------|
| CreateThread(entry, stackSize, priority, …) | New slot: PC=entry, SP=unique top, status=**DORMANT** or READY depending on real API; store thread id return value in `$v0` |
| StartThread(tid) | DORMANT→**READY**; do **not** steal current `_start` context mid-call unless real semantics require |
| Exit/Delete | Free slot when safe (v1 may leak slots with cap) |

v1 may approximate stack/priority; **must** produce READY peers visible to `FindNextReadyThread`.

### 2.4 WaitSema / Sleep (phase 2, not this design’s implement)

C1.3 hooks exist but IRX must **call** them. Separate dual-ACK: auto-intercept WaitSema import → `ParkAndYieldToReady`. CreateThread alone may be enough for residual surface if StartThread makes READY workers while parent still in `_start` checkpoint.

### 2.5 Files (implement after ACK)

| File | Change |
|------|--------|
| `IrxLoader.cs` / import link | Hook CreateThread/StartThread export numbers |
| `Iop.cs` | Ensure CreateThreadContext returns READY on Start; API polish |
| Optional small HLE table | THREADMAN import dispatch |
| Smokes | Synthetic IRX: CreateThread+StartThread → FindNextReadyThread≥0; with YIELD_START residual enqueues |
| **Not** | Gif/Gs, GameQuirks, invent registerRpc |

---

## 3. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **CT-Q1** | Approve Create/StartThread intercept design as next C1 implement? | **Yes** |
| **CT-Q2** | Opt-in `DETPS2_IOP_CREATE_THREAD=1` vs always-on when THREADS=1? | **Opt-in separate flag** first |
| **CT-Q3** | Include WaitSema auto-intercept in same PR? | **No** — phase 2 |
| **CT-Q4** | Success bar: residual enqueue on BO2 IOPFILE vs LiveRpcHits? | Residual/peer first; LiveRpcHits stretch |

---

## 4. Non-goals

- Full THREADMAN priority queues / message boxes  
- EE CreateThread (already KernelState)  
- Raising StartLoadedModule budget as the fix  

---

## 5. Definition of done (this design seat)

- [x] Problem tied to yield-start READY peer requirement  
- [x] Intercept locus + flags + files  
- [ ] Dual-ACK CT-Q1..Q4  
- [ ] **No Core** until ACK  

---

```text
C1 CreateThread intercept design (docs only)
  READY peers so YIELD_START residual can fire on IOPFILE
  dual-ACK before implement
```
