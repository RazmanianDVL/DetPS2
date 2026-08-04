# C1 chain rollup — IOP multi-thread / yield / live RPC (2026-08-04)

**Status:** **parked** — infrastructure complete, default **off**, BO2 commercial bar not yet cleared  
**Tip (D4 land):** `bda1212`  
**Partners:** Grok + Claude dual-orch via UNC inbox  

---

## 0. One-line status

C1 mechanisms for cooperative IOP multi-thread, CreateThread READY peers, WaitSema park, yield-start residual, and residual **drain throughput** are **landed, flag-gated, smoke-tested, and canary-instrumented**. On Blood Omen 2, **live `firstQueue` and WaitYield firings remain 0** at 100M claim — honest residual, not an unmeasured black box.

---

## 1. What landed (default all OFF)

| Layer | Tip / flag | Role |
|-------|------------|------|
| Multi-thread table | `DETPS2_IOP_THREADS` | Context switch / entry stacks |
| Yield-start residual | `DETPS2_IOP_YIELD_START` | 16k checkpoint → residual queue |
| S1 peer scope | `ba196e6` | Checkpoint ignores boot + EntryThreadId + RPC dispatch |
| CreateThread HLE | `DETPS2_IOP_CREATE_THREAD` | thbase 4/6/5/7; encoded tid; KE_NO_MEMORY; free slots |
| Storm fix | `ce3d306` | 9.5M retry Create → 39 legit |
| WaitSema phase-2 | `DETPS2_IOP_WAIT_YIELD` | thsemap 8/6 + Sleep 24 → ParkAndYield |
| Drain D4/D1 | `bda1212` | Drain per EE slice + maxSlices=2 entry; TRACE/counters |
| FQ-2 synthetic LiveRpc | existing smoke | Plant queue → `LiveRpcHits≥1` proves prefer-live plumbing |

---

## 2. BO2 evidence trail (honest)

| Finding | Outcome |
|---------|---------|
| Empty firstQueue | SIFCMD image present; queue head never linked |
| CreateThread storm | Fixed (encoded tid + free + KE codes) |
| WaitSema 0 traps pre-drain | Stubs patched; code path unreached / starved |
| Yield-start 7× false residual | S1 fixed scaffolding (LOADCORE etc.) |
| Residual FIFO HOL | D4: SIFINIT +~134k residual insn in one lifecycle |
| Post-D4 WaitYield / firstQueue | Still **0** |
| IOPFILE residual post-D4 | Not always queued (trajectory shift after SIFINIT progress) |

---

## 3. Why park

- Every layer has **independent proof** (smoke and/or TRACE).  
- Next BO2 progress needs a **new lever** (longer claim, boot-walk after D4, other title, or S2 only if residual attribution matters) — not more untargeted Core.  
- Product fleet **unchanged** (flags default off).

---

## 4. Parked / open (do not implement without dual-ACK)

| ID | Item |
|----|------|
| S2 | OwnerModuleId on CreateThread peers |
| D3 | Fair/RR residual queue |
| FQ-3 | Cross-title registerRpc TRACE |
| Boot-walk | Post-D4 BTCONF where IOPFILE lands |
| Long canary | 200M/500M with TRACE_YIELD_START |

---

## 5. Key docs

| Doc |
|-----|
| `c1-yieldstart-s1-closed.md` |
| `c1-createthread-retry-storm-fix.md` |
| `c1-waitsema-phase2-implement.md` |
| `c1-residual-drain-policy-implement.md` |
| `c1-registerrpc-firstqueue-barrier.md` |
| `c1-yield-surviving-start-design.md` |

---

```text
C1 chain PARKED 2026-08-04 @ bda1212
  infra complete flag-off safe
  BO2 firstQueue/WaitYield still 0 (honest)
  resume with a named lever only
```
