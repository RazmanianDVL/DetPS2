# C1 design — yield-start checkpoint peer scoping (precision bug)

**Status:** dual-ACK'd + **S1 landed** (`ba196e6`) + re-canary verified  
**Date:** 2026-08-04  
**Tip ref:** implement `ba196e6`; design `20e23e8`  
**Evidence:** Claude seat C (seq0149) seven-module false residual; re-canary seq0154 after S1  
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

### S1 sketch (landed)

```text
at checkpoint: HasNonEntryReadyPeer(iop)
  skip tid 0 (boot — always READY when on an entry context)
  skip any LoadedIrx.EntryThreadId
  skip RpcDispatchThreadId
  accept READY/RUN only on remaining slots (CreateThread / CreateSecondary workers)
```

**Implement note:** boot exclusion is required — otherwise every multi-thread `StartLoadedModule` sees boot READY after `PrepareModuleEntry` switches to the entry context.

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

## 9. Definition of done

- [x] Problem named from Claude C evidence  
- [x] Options S1–S4 + bias S1  
- [x] Dual-ACK YS-Q1..Q4  
- [x] S1 implement `ba196e6` + smoke  
- [x] Re-canary: LOADCORE/EECONF/SIFCMD no longer divert; 4 remain with real CreateThread peers  
- [x] **S2 parked** until residual distortion proven (seq0154/0155)  

### Closed outcome (docs seat A)

| Result | Detail |
|--------|--------|
| Scaffolding false positive | **Fixed** — entry/boot/rpc slots ignored |
| Cross-module real workers | **Accepted residual** — SIFINIT/IOPFILE/SDRDRV/IOPSNDS may still divert after earlier CreateThread READY peers; S2 OwnerModuleId if later needed |
| Product flags | still default **off** |

---

```text
yield-start peer scoping S1 CLOSED
  HasNonEntryReadyPeer: skip boot + EntryThreadId + rpc dispatch
  canary: 7→4 diversions; scaffolding class gone
  S2 parked
```

