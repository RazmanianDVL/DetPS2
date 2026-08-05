# Next free seats inventory (updated 2026-08-04 late)

**Author:** grok (dual-orch standing: dual-idle → propose next, no mutual-hold)  
**Tip:** `a8ae0aa` / designs `99087d3`  
**Purpose:** non-empty free-seat list after C1 arc close.

---

## Closed / parked this session (do not re-open without new evidence)

| Stream | Status |
|--------|--------|
| **C1** full arc | storm fix + S1 scoping + D4 drain + EntryThreadId bind; firstQueue still 0 for **honest table pressure**; table-pressure design parked pending demand |
| M4 S4 mirror + GoW plant | plant ON residual; next doc `m4-gow-plant-residual-next.md` |
| M7 residual honesty | rollup `m7-residual-honesty-rollup-2026-08-04.md` — reopen only R1 with named title |
| M5-a S6 | **PARKED** CATCHUP |
| M8 Prefer fleet | audit landed; GoW plant intentional residual |
| M4-g FILEIO GetVersion | **already Core** (`m4g-fileio-getversion-landed.md`) |

---

## Ready / high leverage free seats

| ID | Seat | Risk | Notes |
|----|------|------|-------|
| **M6-b1** | Shared SleepThread starve rescue | Med | `m6b-next-items.md` P0 — clear GAP; flag-gated |
| **M6-b2** | Starvation counters in scoreboard | Low | Observability first |
| **M1 residual** | A3.1 CHCR / M1-f GIF_STAT | Med | Backlog design-first |
| **M7-L1** | Whip/BO2 assist-off IMAGE TRACE | Med | Named bar from M7 rollup |
| **C1-TP** | Table pressure T1 slots=64 | Med | Design ready; only if live register demand |
| **M3-b/c** | Dual-path formalization | Low–Med | Needs non-empty live registry to matter |
| **Fleet flag-off identity** | Scoreboard after recent Core | Low | Cheap confidence |

---

## Explicit non-seats

- Promote CATCHUP default-on  
- Gs.cs composite without oracle  
- GoW plant soft-off without S0 TRACE  
- Mutual-hold / “wait for signal” without a proposed next course  

---

## Suggested next dual-idle pick

1. **M6-b2 counters** (cheap) or **M6-b1 Sleep rescue design dual-ACK**  
2. Or **M1 residual** design-first  
3. Or **M7-L1** measure plan for one title  

```text
next-free-seats updated post C1 close
  prefer M6-b or M1 residual; C1-TP only on demand
```
