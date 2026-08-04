# C1 closed — yield-start S1 peer scoping

**Status:** closed (verified)  
**Tip:** `ba196e6`  
**Date:** 2026-08-04  

## What shipped

`IopModuleHost.HasNonEntryReadyPeer` used at yield-start 16 384 checkpoint instead of raw `FindNextReadyThread`.

| Skipped | Reason |
|---------|--------|
| tid 0 | Boot always READY after switch-to-entry |
| Any `EntryThreadId` | C1.2 module-entry scaffold |
| RPC dispatch tid | Compose scaffold |

## Canary (Claude seq0154, BO2 50M)

| Before S1 | After S1 |
|-----------|----------|
| 7 modules identical 16k residual | **3 clear** (LOADCORE, EECONF, SIFCMD) |
| incl. pre-THREADMAN LOADCORE | **4 remain** (SIFINIT, IOPFILE, SDRDRV, IOPSNDS) after real CreateThread workers |

## Residual (accepted / parked)

- **S2** OwnerModuleId: only if cross-module worker attribution distorts residual/WaitSema  
- Claude **B** seat: IOPFILE residual-drain TRACE after thinner queue  

## Smokes

- `IopYieldStart_EntryThread_NotFalsePeer`  
- `IopYieldStart_ResidualOnReadyPeer` (positive worker path)  

```text
S1 closed ba196e6
  scaffolding false residual fixed
  4 real-worker diversions accepted for now
```
