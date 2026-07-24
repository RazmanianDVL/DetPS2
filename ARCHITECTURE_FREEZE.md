# Architecture Freeze Notes (Phase 11)

DetPS2 phases 0–11 establish the product shape. Future work should extend, not rewrite, these seams.

## Frozen

| Seam | Contract |
|------|----------|
| Execution | `Ps2System.RunFor` → `Scheduler` → `ISchedulable.Step` |
| Time | `ulong MasterCycles` only in core |
| Save | Magic `0x44505332`, v4 + optional deflate envelope bit |
| Input tape | Magic `INPR`, cycle-keyed pad frames |
| HLE | `$v1` syscall number, `$v0` return |
| Present | Software GS is truth; `IFramePresenter` is display-only |
| Tests | `Tests/SmokeTests.cs` is the CI gate |

## Soft (may evolve)

- Event-queue vs fixed-slice defaults  
- MMI / VU opcode coverage  
- Netplay network transport (core is lockstep only)  
- Hardware present backend (stub until GPU path lands)  

## Non-goals (still)

- Shipping copyrighted BIOS/games  
- Cycle-perfect EE/GS parity with PCSX2 on day one  
