# C1 implement — IOP THREADMAN CreateThread/StartThread HLE trampoline

**Status:** implemented + smoke-passed (flag-gated)  
**Date:** 2026-08-04  
**Depends on:** dual-ACK design (`c1-createthread-intercept-design.md`), ordinal scan (`c1-thbase-ordinal-scan.md`)  
**Locks honored:** no Gif/Gs/GameQuirks; Core only `Iop.cs`, `IrxLoader.cs`, `SifRpc.cs` LoadIrx post-link

---

## Mechanism

| Piece | Behavior |
|-------|----------|
| Flag | `DETPS2_IOP_CREATE_THREAD=1` (kill: `DETPS2_DISABLE_IOP_CREATE_THREAD=1`); **default off** |
| Prerequisite | `DETPS2_IOP_THREADS=1` / multi-thread table — CREATE_THREAD alone does **not** grow the table |
| Link | After `LinkImports`, `IrxLoader.OverrideThbaseCreateStartImports` re-points **thbase** ord **4** → `0xBF00`, ord **6** → `0xBF04` |
| Create HLE | Read entry from `*(a0+8)`; `CreateDormantThreadContext` → DORMANT; `$v0=tid`; return via `$ra` |
| Start HLE | `StartThreadReady(a0)` → READY; `$v0=0`/`-1` |
| Yield surface | READY peer visible to `FindNextReadyThread` → unlocks residual yield-start when both flags on |

**THREADS alone:** no override (still real THREADMAN.IRX Create/Start).

---

## Smokes

- `IopCreateThread_HleTrampoline_ReadyPeer` — override patch identity, DORMANT→READY API, Step HLE under process flag  
- Existing: `IopYieldStart_ResidualOnReadyPeer` (still uses synthetic peer; product path now can create peers via IRX)

---

## Residual / next

1. **Claude post-implement review** — flag-off byte-identical, scope vs dual-ACK design  
2. Optional BO2 canary: `THREADS+YIELD_START+CREATE_THREAD` — residual / peer during IOPFILE `_start`  
3. Phase 2 (separate dual-ACK): WaitSema auto-intercept → `ParkAndYieldToReady`

```text
C1 CreateThread trampoline landed (flag-gated)
  thbase 4/6 → 0xBF00/0xBF04 HLE → DORMANT/READY peers
```
