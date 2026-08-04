# C1 design — residual module-start drain policy (FIFO HOL)

**Status:** design only — **ready for dual ACK** — **no Core this turn**  
**Date:** 2026-08-04  
**Tip ref:** `cfb2a93` / S1 `ba196e6`  
**Evidence:** Claude seat B (seq0159): post-S1 BO2 still 0 WaitYield; drain = **one** `DrainResidualModuleStarts` per `RunFor`, front-only FIFO  
**Depends on:** S1 peer scoping closed  
**Locks:** `Ps2System.RunFor`, `IopModuleHost.DrainResidualModuleStarts` — no Gif/Gs/GameQuirks  

---

## 0. One-line problem

Yield-start residual is a **shared FIFO** serviced **once per outer `RunFor`**, **one 16 384-insn slice of the front item only**.  
With ~100 `RunFor` calls on a 100M claim (blocker-trace ~1M-cycle outer slices × commercial 50k inner, or host-present boundaries), **SIFINIT ahead of IOPFILE can burn up to 32 drain slots** before IOPFILE reaches the front — then IOPFILE only advances one slice per subsequent `RunFor`. WaitSema may never fire because **drain throughput**, not just checkpoint precision, starves late modules.

---

## 1. Code map (ground truth)

| Site | Behavior |
|------|----------|
| `Ps2System.RunFor` (~416–418) | If residual queue non-empty: **one** `DrainResidualModuleStarts(this)` then commercial slice loop |
| `DrainResidualModuleStarts` | Peek front; run ≤16 384 insn (or Remaining); re-enqueue if not done and `SlicesLeft>0` (cap 32) |
| Queue fill | Partial yield at 16k checkpoint (post-S1: only non-entry READY peers) |

**Not** once per 50k EE slice; **not** proportional to `cyclesToRun`.

---

## 2. Goal

Give residual modules enough R3000 progress under flag-on that a late disc IRX (IOPFILE class) can reach WaitSema/register within a claim budget, without:

- starving EE / commercial assists  
- reintroducing scaffolding false residual (S1 stays)  
- unbounded drain spinning  

Flag-off: still no residual path (byte-identical).

---

## 3. Options

| ID | Approach | Pros | Cons |
|----|----------|------|------|
| **D1** | Drain **N slices per RunFor** (N=4..8 fixed) or until queue empty / budget | Simple | Still FIFO HOL; front hog gets N×16k first |
| **D2** | Drain budget **proportional to `cyclesToRun`** (e.g. 1 slice per K EE cycles, min 1) | Scales with claim | Needs careful K; still FIFO |
| **D3** | **Round-robin residual**: rotate front after each slice (or fair queue) | Anti-HOL | Slightly more state; fairness vs “finish early modules first” |
| **D4** | Drain **inside** commercial while loop (once per 50k EE slice) | More opportunities (~2000/100M if 50k) | Higher IOP vs EE interleave cost |
| **D5** | Cap front **SlicesLeft** lower + drop + TRACE | Stops hog | May drop SIFINIT incomplete |

**Bias: D4 + D1 light** — call drain once per commercial inner slice (D4), optionally allow `maxSlicesPerCall=2` (D1) so one RunFor without commercial loop still progresses. Revisit D3 if HOL remains after D4.

### D4 sketch

```text
// Ps2System.RunFor commercial path
if (yield residual) DrainResidualModuleStarts(this); // keep once at top
while (left > 0) {
  ... EE slice ...
  if (yield residual) DrainResidualModuleStarts(this); // NEW: per slice
}
```

Non-commercial RunFor: keep single top drain or apply same if residual present.

### Success bar (later canary)

BO2 full flag stack: WaitYield TRACE **>0** **or** IOPFILE residual completes / `ret` improves **or** honest: still 0 with measured drain count proving progress per module (telemetry).

---

## 4. Telemetry (same PR or tiny prior)

| Counter | Meaning |
|---------|---------|
| `ResidualDrainCalls` | times Drain entered |
| `ResidualSlicesRan` | slices that retired >0 insn |
| Optional TRACE | `[YIELD-RESIDUAL] name=… ran=… remain=… qdepth=…` under `DETPS2_TRACE_YIELD_START=1` |

Without this, HOL diagnosis stays manual.

---

## 5. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **RD-Q1** | Approve residual drain redesign as next C1 Core? | **Yes** |
| **RD-Q2** | Prefer **D4** (per EE slice) first? | **Yes** |
| **RD-Q3** | Add D1 `maxSlices=2` in same PR? | **Optional yes** if cheap |
| **RD-Q4** | Defer fair/RR queue (D3) until D4 canary? | **Yes** |
| **RD-Q5** | Include TRACE/counters in same PR? | **Yes** (measurement debt) |

---

## 6. Non-goals

- S2 OwnerModuleId (separate)  
- Raising first-call 100k as sole fix  
- Changing WaitSema always-park  

---

## 7. Definition of done (this design seat)

- [x] HOL grounded in RunFor + DrainResidual code + Claude B frequency  
- [x] Options D1–D5 + bias D4  
- [x] Dual-ACK RD-Q1..Q5  
- [ ] Dual-ACK  
- [ ] **No Core** until ACK  

---

```text
residual drain policy design
  one slice per RunFor + FIFO HOL starves IOPFILE
  bias D4: drain once per commercial EE slice + TRACE
```
