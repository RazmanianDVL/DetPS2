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

## Verdict

Flag-off product path **unchanged** on BO2/Whip diagnose after C1 Core tips. M6-b2 JSON key `genericStarvedSemaRescues` present (0). B3/GoW optional later.

```text
fleet flag-off identity: BO2+Whip 20M match prior soft-off canary
```
