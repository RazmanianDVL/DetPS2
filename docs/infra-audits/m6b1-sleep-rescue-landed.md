# M6-b1 — Shared SleepThread starve rescue **LANDED**

**Date:** 2026-08-04  
**Status:** already in Core — do not re-design as greenfield  
**Code:** `KernelHle.MaybeRescueGenericStarvedSleep` + `Ps2System` ambient tick  
**Counter scrape:** `GenericStarvedSleepRescues` (M6-b2)  

---

## Policy (live)

| Item | Value |
|------|--------|
| Kill | `DETPS2_DISABLE_M6B_SLEEP_RESCUE=1` |
| Grace pure sleep | 2M cycles |
| Grace suspend | 400k cycles |
| Default gate | whole-system deadlock only (no other runnable peer) |
| Orphan pure-sleep | `DETPS2_STARVED_SLEEP_ORPHAN=1` after 2× grace |
| SignalSema | **never** from this path |

---

## Remaining M6 (from `m6b-next-items.md`)

| ID | Status |
|----|--------|
| M6-b1 Sleep rescue | **LANDED** |
| M6-b2 counters | **LANDED** (Sema+Sleep scrape, tip `581f444`) |
| M6-b3 post-SignalSema fairness | open design |
| M6-b4 JREXIT revive | open design, opt-in later |

```text
M6-b1 LANDED
  next M6 design: b3 fairness or b4 JREXIT if demand
```
