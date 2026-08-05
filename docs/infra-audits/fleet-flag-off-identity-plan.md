# Fleet flag-off identity plan (post C1 Core landings)

**Status:** measure plan — dual-ACK optional before run  
**Tip baseline:** `a8ae0aa` / `10e678f`  
**Goal:** confirm product default (all DETPS2 experiment flags unset) scoreboard identity vs pre-C1-session tip for a small fleet  

---

## 1. Scope

| Title | Media | Budget |
|-------|-------|--------|
| BO2 | user-media-bloodomen2.json | diagnose 20M + claim 100M optional |
| Whip | user-media-whiplash.json | 20M |
| B3 | burnout-only.json | 20M |
| GoW | user-media-god-of-war.json | 20M |

**Env:** no DETPS2_IOP_*, no CATCHUP, no TRACE* (product defaults).

**Compare fields:** px, prims, gifP*, calls, binds, cdvd, exitCode, residualDispfbPx (if present).

**Pass:** no worse than tip recorded before C1.1 night (or document intentional drift).

---

## 2. Non-goals

- Flag-on A/B  
- GameQuirks edits  
- Promoting any parked flag  

---

## 3. Owner

Either dual-orch agent; ~30–60 min wall if media present.

```text
fleet flag-off identity plan
  cheap confidence after C1 Core landings
```
