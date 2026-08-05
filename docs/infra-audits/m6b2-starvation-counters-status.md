# M6-b2 — starvation counters status

**Date:** 2026-08-04  
**Tip:** (see commit)  

## Gap vs `m6b-next-items.md` P1

| Counter | Property | blocker-trace | scoreboard JSON |
|---------|----------|---------------|-----------------|
| Generic WaitSema rescue | `GenericStarvedSemaRescues` | **was missing → added** | **was missing → added** |
| Generic Sleep rescue | `GenericStarvedSleepRescues` | already printed | already present |

No behavior change — scrape-only. Fabricate/stall WaitSema rates still optional later.

```text
M6-b2 partial land: both generic starve rescues scrapeable
```
