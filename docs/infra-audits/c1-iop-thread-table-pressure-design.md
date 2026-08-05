# C1 design — IOP thread table pressure (32 slots) for late disc IRX

**Status:** design only — dual-ACK before Core  
**Date:** 2026-08-04  
**Tip:** `a8ae0aa` (EntryThreadId residual bind fix verified)  
**Evidence:** Claude post-D4 boot-walk + re-canary: by IOPFILE load, slots hold boot + early entries + CreateThread workers (17/24/26/29 class) + RPC dispatch → bind returns −1  

---

## 0. One-line

Honest residual path now requires `EntryThreadId ≥ 1`. Late disc modules (IOPFILE/SDRDRV/IOPSNDS) still hit **table full** → first-call budget only → WaitYield/firstQueue stay 0 for a **capacity/policy** reason, not a silent drop.

---

## 1. Current capacity

| Consumer | Slots |
|----------|------:|
| Boot | 1 (id 0) |
| Module entry contexts (C1.2) | up to ~8+ over boot walk |
| CreateThread HLE workers | many (Encode tid; max 31 non-boot) |
| RPC dispatch | 1 reusable |
| **MaxIopThreadSlots** | **32** |

`CreateDormant` / entry bind fail at full → no residual surface for that module.

---

## 2. Options

| ID | Approach | Pros | Cons |
|----|----------|------|------|
| **T1** | Raise `MaxIopThreadSlots` (e.g. 64/128) | Simple | Stack arenas / memory layout must scale; may hide leaks |
| **T2** | Priority bind: reserve N slots for disc/LOADFILE modules | Protects IOPFILE | Policy complexity; who is “priority”? |
| **T3** | Free/dormant-reclaim idle CreateThread workers that never Start | More free slots | Risk if worker still needed |
| **T4** | Cap CreateThread HLE allocations per module / global soft cap | Prevents one module filling table | May break games that need many threads |
| **T5** | Document 32 as hard residual; product stays HLE FILEIO until real THREADMAN table | No Core thrash | Live register path still blocked |

**Bias:** **T1 to 64** flag-gated `DETPS2_IOP_THREAD_SLOTS=64` (or compile-time with smoke), plus **T4 soft-cap** optional later. Dual-ACK T1 alone first.

### T1 sketch

- `MaxIopThreadSlots` becomes configurable constant (default 32 product identity).  
- Stack arenas: ModuleEntryStackArena + ThreadStackRegion must not overlap — re-check bases if N grows.  
- Smoke: allocate 40 contexts under flag-on slots=64.  
- Flag-off / default 32: byte-identical table size.

---

## 3. Dual-ACK

| ID | Question | Bias |
|----|----------|------|
| **TP-Q1** | Approve table-pressure as next C1 design after bind fix? | **Yes** |
| **TP-Q2** | Prefer T1 raise-to-64 opt-in first? | **Yes** |
| **TP-Q3** | Touch CreateThread soft-cap same PR? | **No** — separate |
| **TP-Q4** | Implement only after dual-ACK + stack layout audit? | **Yes** |

---

## 4. Done (this design seat)

- [x] Problem tied to post-fix honest still-0  
- [x] Options T1–T5 + bias  
- [ ] Dual-ACK  
- [ ] **No Core** until ACK  

```text
IOP thread table pressure design
  32 slots fill before IOPFILE bind
  bias: opt-in MaxIopThreadSlots=64 after dual-ACK
```
