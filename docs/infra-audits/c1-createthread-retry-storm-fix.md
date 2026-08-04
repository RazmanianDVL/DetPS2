# C1 fix — CreateThread HLE retry storm (BO2 canary)

**Date:** 2026-08-04  
**Prior tip:** `26706cb` (v1 trampoline)  
**Canary:** Claude BO2 `THREADS+YIELD_START+CREATE_THREAD` — ~9.5M HLE Create firings after table fill  
**Evidence:** `out/canaries/c1-createthread-bo2-canary/`, inbox seq0133  

---

## 1. Symptoms

| Observation | Detail |
|-------------|--------|
| First ~dozen Creates | Real entry PCs, sequential slots up to ~29 |
| Then | `tid=-1` (0xFFFFFFFF) forever; later `entry=0 ra=0xBEE0` (ModuleReturnSentinel) |
| firstQueue | stayed 0 |
| Budget | ~9.5% of 100M claim burned on Create HLE alone |

## 2. Root causes (stack)

1. **No slot reclaim** — Create only; no DeleteThread/ExitThread free → table fills permanently (design non-claim “v1 may leak slots”).  
2. **Wrong failure code** — v1 returned plain `-1` / `0xFFFFFFFF`. Real THREADMAN Create returns **`0xFFFFFE70`** on alloc fail (`FUN_00000c5c`).  
3. **Wrong success tid shape** — real tid is encoded `…|1` (bit0 set):  
   `return obj << 5 | (gen & 0x3f) << 1 | 1`.  
   Plain even slot ids fail bit0 checks; `0xFFFFFFFF` has bit0 set so may look “encoded-ish” then fail object validation → **retry Create forever**.  
4. **No entry validation** — unaligned / zero entry should be **`0xFFFFFE6E`**, not allocate.

## 3. Fix (flag still default OFF)

| Change | Detail |
|--------|--------|
| Encode | `EncodeThbaseTid(slot) = (slot << 5) \| 1`; Start/Delete decode |
| Errors | full → `KeThNoMemory` (`0xFFFFFE70`); bad entry → `KeThIllegalEntry`; bad tid → `KeThIllegalThid` |
| Lifecycle | HLE **DeleteThread** ord 5 / **ExitThread** ord 7 → `FreeThreadSlot` |
| TRACE | throttle fail spam (first 32 fails then quiet until success) |
| Smokes | encode, KE_NO_MEMORY on full, Delete reclaims, ord 4/5/6/7 patch |

## 4. Residual

- firstQueue / LiveRpcHits still need yield surface (WaitSema phase-2) after re-canary  
- Encoding is simplified (no generation counter); enough for bit0 + slot reclaim  
- Re-run BO2 canary expected: Create count << 9.5M; table can recycle  

```text
CreateThread retry-storm fix
  encoded tid + KE_NO_MEMORY + Delete/Exit free
```
