# C1 implement — residual drain D4 (per EE slice) + D1 maxSlices=2

**Status:** implemented  
**Tip:** (see commit)  
**Design:** `c1-residual-drain-policy-design.md` dual-ACK RD-Q1..Q5  

## Changes

| Piece | Detail |
|-------|--------|
| `Ps2System.RunFor` commercial | drain once at top with **maxSlices=2**; **again after each EE slice** |
| Non-commercial RunFor | drain maxSlices=2 after Scheduler.RunFor |
| `DrainResidualModuleStarts` | `maxSlices` param; counters `ResidualDrainCalls` / `ResidualSlicesRan` |
| TRACE | `DETPS2_TRACE_YIELD_START=1` → `[YIELD-RESIDUAL] name=… ran=…` |

Flag-off: YieldStartEnabled false → no drain (unchanged).

## Canary bar

BO2 full stack: WaitYield >0 **or** residual slices/progress telemetry **or** honest still-0 with measured drain call rate ≫100/100M.
