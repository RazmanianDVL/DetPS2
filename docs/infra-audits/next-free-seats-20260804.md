# Next free seats inventory (updated 2026-08-04 night — dual-idle)

**Author:** grok (standing: dual-idle → propose next, **no mutual-hold**)  
**Tip at write:** post M7-L1 results  
**Purpose:** non-empty free-seat list; closed seats not re-opened without evidence.

---

## Closed / parked this session (do not re-open without new evidence)

| Stream | Status |
|--------|--------|
| **C1** full arc | storm + S1 + D4 + EntryThreadId; firstQueue 0 = honest table pressure |
| C1 table pressure | design shelf; dual-ACK on demand only |
| M4 S4 mirror + GoW plant | plant ON residual; `m4-gow-plant-residual-next.md` |
| M5-a S6 CATCHUP | **PARKED** (B3 collapse) |
| M7 residual honesty | rollup closed; R1 only with named title |
| **M7-L1 Whip IMAGE** | **measured** — product imgBytes=0; MENU-WHIP-2 already off → **R1 honest residual** (`m7-l1-whip-assist-off-image-results.md`) |
| **M7-L1 BO2 IMAGE** | **measured** — product imgBytes=0; MENU-BO2/PL-027 already off → **R1 honest residual** (`m7-l1-bo2-assist-off-image-results.md`) |
| M8 Prefer fleet | audit + soft-off canary |
| M4-g FILEIO GetVersion | already Core |
| M6-b1 Sleep rescue | already Core |
| M6-b2 starvation counters | already Core |
| Fleet flag-off identity | BO2+Whip+B3+GoW 20M match |

---

## Ready / high leverage free seats

| ID | Seat | Risk | Notes |
|----|------|------|-------|
| **M1 residual** | CHCR single-round force-pump | Med | Design ready: `m1-residual-chcr-single-round-design.md` — dual-ACK before Core |
| **M6-b3** | post-SignalSema fairness | Med | Design shelf; only if GoW SwitchTo soft-disable goal |
| **M6-b4** | JREXIT main-revive scaffold | Med | Design-first; env off default |
| **M7-L1 Haven** | assist-off IMAGE TRACE | Med | Whip+BO2 closed honest; optional peer |
| **C1-TP** | Table pressure T1 slots=64 | Med | Only if live register demand |
| **M3-b/c** | Dual-path formalization | Low–Med | Needs non-empty live registry |

---

## Explicit non-seats

- Promote CATCHUP default-on  
- Gs.cs composite without oracle  
- GoW plant soft-off without S0 TRACE  
- Re-enable MENU-WHIP-2 goefile paint  
- Invent Path2 IMAGE for Whip without real MAP/\*.MP2 path  
- Mutual-hold / “wait for signal” without a proposed next course  

---

## Suggested next dual-idle pick

1. **M1 residual** dual-ACK → Core Opt A (or measure-first env cap=1)  
2. Or **M6-b3** if GoW fairness is the active product goal  
3. Or **M7-L1 Haven** same honesty bar (measure only)

```text
next-free-seats post M7-L1 Whip+BO2 honesty close
  prefer M1 CHCR residual or M6-b3 on demand
  dual-idle = propose, never mutual-hold
```
