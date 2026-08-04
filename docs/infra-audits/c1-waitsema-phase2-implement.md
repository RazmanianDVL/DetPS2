# C1 implement — WaitSema / SignalSema / SleepThread HLE (phase 2)

**Status:** implemented + smoke-passed (flag-gated)  
**Date:** 2026-08-04  
**Tip ref:** after `ce3d306` (CreateThread storm fixed + greenlit)  
**Design:** `c1-waitsema-phase2-design.md` (dual-ACK + greenlight)

---

## Flag

| Env | Default |
|-----|---------|
| `DETPS2_IOP_WAIT_YIELD=1` | **off** |
| `DETPS2_DISABLE_IOP_WAIT_YIELD=1` | unset kill |
| Requires | `DETPS2_IOP_THREADS` multi-thread table |

Independent of `DETPS2_IOP_CREATE_THREAD`.

## Mechanism

| Lib | Ord | Trap | Behavior |
|-----|----:|------|----------|
| thsemap | 8 WaitSema | `0xBF08` | v0=0, PC=ra, `ParkAndYieldToReady` |
| thsemap | 6 SignalSema | `0xBF0C` | wake one WAIT→READY, v0=0 |
| thbase | 24 SleepThread | `0xBF18` | same park as Wait |

**Resume safety:** PC set to `$ra` **before** park so a later wake does not re-enter the trap PC.

**v1 limits:** ignore sema count/id; **always park regardless of `$a0`** (deliberate — Claude dual-ACK note: milder than CreateThread storm class; garbage id → silent park until any Signal wakes one WAIT, not a spin). Alone marks WAIT but continues at ra (no hard-hang). Signal wakes slot ≥0 (including boot).

## Smokes

`IopWaitYield_HleParkAndSignal` — override 6/8/24, park→peer, Signal READY waiter, alone Sleep.

## Residual

BO2 canary with THREADS+CREATE_THREAD+WAIT_YIELD(+YIELD_START) for residual / firstQueue stretch.
