# Dual-orch session rollup — 2026-08-04

**Tip (end):** `7018516`  
**Standing order:** dual-idle → propose next course (no mutual-hold)

---

## Bugs found & fixed (Core)

| Bug | Fix tip | Verified |
|-----|---------|----------|
| CreateThread HLE retry storm (wrong tid / no free) | `ce3d306` | Claude re-canary |
| Yield-start false residual (entry/boot peers) | `ba196e6` S1 | Claude re-canary |
| Residual drain once/RunFor FIFO HOL | `bda1212` D4 | Claude re-canary |
| Residual enqueue with EntryThreadId=-1 silent drop | `ab4e3e6` + free narrow `a8ae0aa` | full suite + re-canary |

---

## Docs / parks (no Core thrash)

| Area | Status | Doc |
|------|--------|-----|
| C1 chain | infra complete; firstQueue 0 = honest table pressure | `c1-chain-rollup-2026-08-04.md` |
| C1 table pressure | design shelf; dual-ACK deferred on demand | `c1-iop-thread-table-pressure-design.md` |
| M5 CATCHUP | parked (B3 collapse) | `m5a-next-status-2026-08-04.md` |
| M7 residual | honesty rollup; reopen only R1 named title | `m7-residual-honesty-rollup-2026-08-04.md` |
| M8 Prefer | fleet audit; GoW plant ON | `m8-prefer-quiet-fleet-audit-2026-08-04.md` |
| M4-g FILEIO GetVersion | already Core | `m4g-fileio-getversion-landed.md` |
| M6-b1 Sleep rescue | already Core | `m6b1-sleep-rescue-landed.md` |
| M6-b2 counters | Sema scrape added | `m6b2-starvation-counters-status.md` / tip `581f444` |
| M6-b3 fairness | design shelf | `m6b3-post-signalsema-fairness-design.md` |
| Fleet flag-off | BO2+Whip+B3+GoW 20M OK | `fleet-flag-off-identity-results.md` |

---

## Product path

Diagnose 20M product-default scoreboard **identity** holds after C1 Core landings (four titles sampled).

---

## Next free (demand-gated)

1. C1 table slots=64 if live register demand  
2. M6-b3 if GoW SwitchTo soft-disable goal  
3. M7-L1 assist-off IMAGE TRACE named title  
4. M1 residual design-first  

```text
session 2026-08-04 dual-orch
  4 Core bug fixes + parks + fleet identity
  dual-idle = propose, never mutual-hold
```
