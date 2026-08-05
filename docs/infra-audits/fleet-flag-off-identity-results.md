# Fleet flag-off identity — diagnose 20M results

**Date:** 2026-08-04  
**Tip:** `6f69475`+ Core with C1 landings + M6-b2 scrape  
**Env:** all experiment flags **unset** (product default)

## BO2 (`SLUS_200.24`) @ 20M

| Field | This run | m8a soft-off canary baseline |
|-------|----------|------------------------------|
| pc | `0x00488898` | `0x00488898` |
| px | 286720 | 286720 |
| prims | 1 | 1 |
| gifP3 | 2 | 2 |
| cdvd | 2211 | 2211 |
| syscalls | 701 | 701 |
| calls | 62 | 62 |
| binds | 14 | 14 |
| exit | 0 | 0 |

**Match:** identity on load-bearing gates vs prior soft-off diagnose.

## Whip (`SLUS_206.84`) @ 20M

| Field | This run | m8a soft-off canary baseline |
|-------|----------|------------------------------|
| pc | `0x003145A8` | `0x003145A8` |
| px | 286720 | 286720 |
| syscalls | 921 | 921 |
| calls | 114 | 114 |
| cdvd | 916 | 916 |
| binds | 13 | 13 |

**Match:** identity.

## B3 (`SLUS_210.50`) @ 20M

| Field | This run | m8a dual-suppress diagnose baseline |
|-------|----------|--------------------------------------|
| pc | `0x00123E84` | `0x00123E84` |
| px | 877187 | 877187 |
| prims | 172 | 172 |
| syscalls | 806 | 806 |
| calls | 42 | 42 |
| cdvd | 425 | 425 |
| binds | 11 | 11 |

**Match:** identity (diagnose-class floor; not claim 100M baseline).

## GoW (`SCUS_973.99`) @ 20M product plant ON

| Field | This run | m8a G0 dual-suppress baseline |
|-------|----------|-------------------------------|
| pc | `0x002846A4` | (diagnose class) |
| cdvd | 136 | 136 |
| calls | 21 | 21 |
| binds | 10 | 10 |
| syscalls | 2284 | — |
| px | 1433600 | diagnose floor |
| gifP3 | 0 | expected R1/R2 class |

**Match:** load-bearing gates (cdvd/calls/binds) match plant-ON diagnose floor from dual-suppress evidence.

## Verdict

Flag-off product path **unchanged** on BO2/Whip/B3/GoW diagnose after C1 Core tips. M6-b2 JSON keys present (0).

```text
fleet flag-off identity: BO2+Whip+B3+GoW 20M diagnose OK
```
