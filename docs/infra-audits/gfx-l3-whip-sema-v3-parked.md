# GFX L3 — WHIP_SEMA_FIX_V3 remove **PARKED** (starvation hypothesis refuted)

**Status:** parked — do not soft-disable V3 for “progress”  
**Date:** 2026-08-04  
**Evidence owner:** Claude (seq0239 A/B); dual-orch ACK seq0241  
**Parent:** `gfx-l3-whip-texture-methodology.md`

---

## Result

| Arm | Stream producer | EE progress |
|-----|-----------------|-------------|
| **V3 on** (product) | Code≈72% / frontend≈34% by 50M (real plateau) | Continues to budget |
| **V3 off** (`DETPS2_TEMP_DISABLE_WHIP_SEMA_V3=1` measure only) | **No further stream bytes** | Stalls ~3.16M cycles |

Fabricate is **load-bearing**, not a livelock to delete. Prior “thread 2 starves producer” narrative is **refuted** for removal as a lever.

---

## Open (not this park)

- Real intended **SignalSema** for WaitSema(3) — needs Whiplash EE Ghidra (not in repo)  
- **MP2 / texture pool** format RE — multi-session; escalate to user before committing  

---

## Ban

- Soft-off WHIP_SEMA_FIX_V3 as default or “graphics” experiment without new dual-ACK + user resource call  

```text
WHIP V3 remove PARKED
  A/B: off stalls EE + kills stream progress
  next graphics: MP2 RE (user-scoped) or Dec CLUT/PSM
```
