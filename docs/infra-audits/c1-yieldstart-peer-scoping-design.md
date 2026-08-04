# C1 design — yield-start checkpoint peer scoping (precision bug)

**Status:** design only — **ready for dual ACK** — **no Core this turn**  
**Date:** 2026-08-04  
**Tip ref:** `6e5fe86` / stack `27af7d7`  
**Evidence:** Claude seat C (seq0149): post-yield-start BO2 TRACE — **seven** modules hit identical 16 384 partial residual (LOADCORE, EECONF, SIFCMD, SIFINIT, IOPFILE, SDRDRV, IOPSNDS), including **LOADCORE** before THREADMAN exists  
**Locks:** prefer `SifRpc.cs` / `Iop.cs` only; no Gif/Gs/GameQuirks  

---

## 0. One-line problem

Yield-start checkpoint uses **`FindNextReadyThread() >= 0` (global table scan)**.  
C1.2 `PrepareModuleEntry` leaves **prior modules’ entry contexts** `InUse` (often READY/DORMANT-class leftovers). Later modules see those slots as a “yield surface” and divert to residual at 16 384 even when **this** module never created a worker. That makes residual enqueue **nearly universal** after the first multi-thread start — not evidence of real WaitSema/CreateThread progress.

Revises earlier framing: “IOPFILE hits 100k” is **stale**; “WaitSema unreached because budget” is incomplete — **checkpoint precision** is also wrong.

---

## 1. Mechanism today

| Piece | Behavior |
|-------|----------|
| `PrepareModuleEntry` | `BindModuleEntryContext` → unique entry tid per module (`EntryThreadId`) |
| `StartLoadedModule` checkpoint | every 16 384 insns: if `FindNextReadyThread() >= 0` → enqueue residual, break |
| `FindNextReadyThread` | any other InUse READY/RUN slot (global RR) |

**False positive class:** leftover entry contexts + CreateThread workers + RPC dispatch slot all count equally.

---

## 2. Goal

Checkpoint should divert to residual **only when this module’s start has a real concurrency surface**, e.g.:

1. A peer this module’s IRX **created** (CreateThread HLE / StartThread READY), **or**  
2. Explicitly a non-entry-context worker (not another module’s `EntryThreadId`), **or**  
3. (stretch) WaitSema park of **this** entry thread with a READY peer that is a true worker.

Flag-off: still byte-identical (yield-start off).

---

## 3. Options

| ID | Approach | Pros | Cons |
|----|----------|------|------|
| **S1** | Checkpoint: peer READY **and** peer tid ∉ any module’s `EntryThreadId` and ≠ rpc dispatch | Minimal; filters C1.2 scaffolding | Real CreateThread peers still count; needs module list walk |
| **S2** | Tag `IopThreadContext.OwnerModuleId`; CreateThread HLE sets owner=current module; checkpoint only same-owner peers | Precise | Field + plumbing; entry bind sets owner too |
| **S3** | Snapshot `ThreadCount` / next-slot at PrepareModuleEntry; checkpoint only if `ThreadCount` grew | Simple | Misses peer created then Exit’d; false if other modules create mid-start |
| **S4** | Free/demote prior entry slots when leaving StartLoadedModule | Shrinks leak | Risk if residual still needs that context |

**Bias: S1 first** (smallest blast radius), optional **S2** if S1 insufficient after smoke/canary.

### S1 sketch

```text
at checkpoint:
  peer = FindNextReadyThread()
  if peer < 0: continue
  if IsModuleEntryThread(peer) or peer == RpcDispatchThreadId: continue  // not a yield surface
  // else: enqueue residual (true worker or orphan READY)
```

`IsModuleEntryThread(tid)`: walk `IopModuleHost` irx records for `EntryThreadId == tid`.

### S2 sketch (phase 2)

- `IopThreadContext.OwnerModuleId` int (−1 = none)  
- `CreateDormant` / CreateThread HLE: set owner from `StartLoadedModule`’s current module id (thread-local or parameter)  
- Checkpoint: `FindNextReadyThread` filtered to same OwnerModuleId **or** any non-entry peer with owner==current  

---

## 4. Residual drain note (out of scope for S1 but related)

Claude: FIFO residual with many modules may starve IOPFILE slices. Separate dual-ACK if needed (priority residual, drain budget, TRACE). **Do not** mix into S1 PR.

---

## 5. Flags

No new product flag required — fix precision under existing `DETPS2_IOP_YIELD_START`.  
Optional kill: none new.

---

## 6. Smokes (after dual-ACK implement)

| Smoke | Expect |
|-------|--------|
| Minimal IRX A then B, both multi-thread entry, **no** CreateThread | B does **not** PartialYield at 16k solely because A’s entry slot exists |
| CreateThread HLE peer during start | PartialYield **does** fire when peer READY (non-entry) |
| Flag-off | unchanged |

Update `IopYieldStart_ResidualOnReadyPeer` if it used CreateSecondaryContext peer (still non-entry — still OK) or invent two-module false-positive smoke.

---

## 7. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **YS-Q1** | Approve redesign of yield-start checkpoint peer test? | **Yes** |
| **YS-Q2** | Prefer **S1** (exclude entry tids) first? | **Yes** |
| **YS-Q3** | Defer residual FIFO starvation to separate seat? | **Yes** |
| **YS-Q4** | Who implements after ACK? | Grok Iop/SifRpc **or** Claude — either |

---

## 8. Non-goals

- Raising 100k budget alone  
- Faking firstQueue / registerRpc  
- Changing WaitSema always-park policy  

---

## 9. Definition of done (this design seat)

- [x] Problem named from Claude C evidence  
- [x] Options S1–S4 + bias S1  
- [x] Smokes + dual-ACK YS-Q1..Q4  
- [ ] Dual-ACK  
- [ ] **No Core** until ACK  

---

```text
yield-start peer scoping design
  global FindNextReadyThread false-positive from C1.2 entry slots
  bias S1: exclude module EntryThreadId + rpc dispatch from checkpoint surface
```
