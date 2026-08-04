# C1 design — yield-surviving `StartLoadedModule` (IOPFILE-class)

**Status:** design only — **ready for dual ACK** — **no Core this turn**  
**Date:** 2026-08-04  
**Tip ref:** `85f08cf`  
**Evidence:** `docs/infra-audits/c1-start-trace-bo2.md`, `c1-registerrpc-trace-bo2.md`  
**Parent:** `docs/IOP_MULTITHREAD_AND_REAL_RPC.md` §2–3  
**Locks:** do **not** edit `Intc.cs` / `EmotionEngine.cs` for this seat. Prefer `SifRpc.cs` / `Iop.cs` / `IopModuleHost` only.

---

## 0. Problem

`StartLoadedModule` runs a **tight one-shot loop** (`Iop.Step(1)` until sentinel or `maxInstructions`).  
BO2 TRACE: **IOPFILE** and **SDRDRV** hit **100k budget** with `ret=False` under `DETPS2_IOP_THREADS=1`. Parent design already stated larger budgets do not fix worker/yield `_start`. Live SIFCMD queue stays empty (`firstQueue=0`).

**Goal:** when multi-thread is on, a module `_start` that **creates a worker and parks** must be able to **resume after yield** across EE/IOP quanta until registerRpc (or honest resident return), without inventing RPC servers in C#.

---

## 1. Current code shape (accurate sketch)

`IopModuleHost.StartLoadedModule` (`SifRpc.cs` ~729+):

1. `PrepareModuleEntry` (C1.2 unique stack / context when `MultiThreadEnabled`)  
2. Loop: `Iop.Step(1)` until `PC == ModuleReturnSentinel` **or** `insns >= maxInstructions`  
3. On budget: mark resident/budget message; restore prior thread  
4. Caller treats `ok=True` even when `ret=False` (image loaded, start “attempted”)

C1.3 yield hooks exist but **cannot help if the entire budget is burned inside one blocking call** before the EE scheduler runs again.

---

## 2. Proposed mechanism (flag-gated)

### 2.1 Policy

| Principle | Detail |
|-----------|--------|
| Flag-off | Byte-identical to today’s one-shot loop |
| Flag-on | Yield-aware residual start under `DETPS2_IOP_THREADS=1` only |
| No invent | No fake `sceSifRegisterRpc` C# plants |
| No title names in Core | Generic “budget hit + multi-thread + PC not sentinel → residual” |

### 2.2 Residual start queue (v1)

When `DETPS2_IOP_YIELD_START=1` (name TBD) **and** `IOP_THREADS=1`:

1. **First arm** (LOADFILE / literal start): run `_start` in **checkpoints** of `MaxInsnFirstSlice` (default **16k**), up to `MaxInsnFirstCall` (default **100k**, same as today’s hard cap).  
2. After each checkpoint (and at end): if **returned to sentinel** → same as today (modres from v0).  
3. If checkpoint hit **and** multi-thread shows **other READY threads** or parked wait (**yield surface detected**):  
   - Save entry context (already C1.2)  
   - Enqueue **residual start work item** `(moduleId, remainingBudgetCap, slicesLeft)`  
   - Return soft status: `Partial=true` / `ret=False` but **do not** treat as permanent failure  
4. If checkpoint hit **and no yield surface** (straight-line boot, no peer READY / park):  
   - **Continue in the same call** through further checkpoints until return or `MaxInsnFirstCall` — **16k is not a hard cap**.  
   - Rationale (C1-Y2 clarification): a module that legitimately finishes in e.g. 40k insn without yielding must not be marked Partial at 16k.  
5. If `MaxInsnFirstCall` hit with still no return and still no yield surface → same honest budget outcome as today (`ret=False`, no residual invent). Optional later: residual-even-without-yield for spin loops (out of v1).  
6. **IOP Step / SIF Step / scheduler tick:** drain residual queue: each tick runs `MaxInsnPerResidualSlice` on the module’s thread context, then yields to other IOP READY threads (C1.3 RR).  
7. Complete residual when sentinel hit or `TotalInsnCap` / `MaxSlices` exhausted (honest budget residual — document metrics).

### 2.3 Flags

| Env | Default | Role |
|-----|---------|------|
| `DETPS2_IOP_THREADS=1` | off | Prerequisite |
| `DETPS2_IOP_YIELD_START=1` | **off** | Enable residual start |
| `DETPS2_DISABLE_IOP_YIELD_START=1` | unset | Kill residual path |
| Optional `DETPS2_IOP_START_SLICE=N` | 16384 | First/residual slice size |

### 2.4 Files (implement after ACK)

| File | Change |
|------|--------|
| `SifRpc.cs` / `IopModuleHost` | Residual queue + slice loop; metrics |
| `Iop.cs` | Ensure residual drain hooks on Step when queue non-empty |
| Smokes | Synthetic: parent `_start` creates worker, yields, registerRpc; LiveRpcHits>0 under flags |
| **Not** | Intc/EE; GameQuirks; RealSifRpc HLE sid invent |

### 2.5 Acceptance

| Gate | Bar |
|------|-----|
| Flag-off | Byte-identical BO2/diagnose canaries vs tip |
| Flag-on BO2 | `firstQueue != 0` **or** IOPFILE `ret=True` / non-zero LiveRpcHits within claim 100M — **honest if still fail** |
| Synthetic smoke | Parent/worker register path under THREADS+YIELD_START |
| Default-on | Separate dual-ACK after multi-title |

---

## 3. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **C1-Y1** | Approve residual slice design (not budget++ alone)? | **Yes** |
| **C1-Y2** | First slice default 16k vs keep 100k first then residual? | **16k checkpoint, 100k first-call cap** — continue same call if no yield surface (not Partial at 16k alone) |
| **C1-Y3** | Include SDRDRV in success bar or IOPFILE-only? | **IOPFILE primary**; SDRDRV secondary |
| **C1-Y4** | Implement now after ACK or park until next session? | Partner choice — design ready |

---

## 4. Explicit non-goals

- Raising 100k → 1M as “the fix”  
- Removing HLE-owned IOPRP skip list without separate design  
- Multi-take DMAC (M5) coupling  

---

## 5. Definition of done (this design seat)

- [x] Problem named from BO2 TRACE  
- [x] Mechanism + flags + files  
- [ ] Dual-ACK C1-Y1..Y4  
- [ ] **No Core** until ACK  

---

```text
C1 yield-surviving start design (docs only)
  residual slices under IOP_THREADS + YIELD_START
  evidence: IOPFILE/SDRDRV budget hit BO2
  dual-ACK before implement
```
