# M6-b3 design — post-SignalSema schedule fairness (wake the waiter)

**Status:** design only — dual-ACK before Core  
**Date:** 2026-08-04  
**Depends:** M6-b1/b2 landed; `m6b-next-items.md` P2  
**Locks:** prefer `KernelHle` / `SonyKernelHle` — **no** title PC plants  

---

## 0. One-line

After a successful **SignalSema(id)**, prefer running a **runnable waiter of that id** (if any) instead of arbitrary RR — GoW-class residual is worker schedule fairness, not whole-system deadlock.

---

## 1. Non-goals

- Global “SignalSema while peers runnable” fabricate (M6-a thrash ban)  
- Delete Midway/GoW SwitchTo assists in same PR  
- IOP WaitSema HLE (C1 separate)  

---

## 2. Mechanism sketch

| Step | Behavior |
|------|----------|
| On SignalSema success that wakes a waiter | Record `preferTid = waiter` |
| EE yield / SwitchToNext / schedule tick | Prefer `preferTid` if still runnable once |
| Clear | After one yield to preferTid or if no longer runnable |

Flag: `DETPS2_SEMA_WAKE_PREFER=1` default **off**.

---

## 3. Dual-ACK

| ID | Question | Bias |
|----|----------|------|
| **SF-Q1** | Approve M6-b3 design as next M6 Core after demand? | **Shelf-ready yes; implement only if GoW SwitchTo soft-disable is a goal** |
| **SF-Q2** | Opt-in flag default off? | **Yes** |
| **SF-Q3** | Same PR as assist soft-disable? | **No** — measure first |

---

```text
M6-b3 post-SignalSema fairness design
  prefer woken waiter; default off; dual-ACK before Core
```
