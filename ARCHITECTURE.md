# DetPS2 Architecture Overview (Phase 6.2)

**Last Updated**: 2026-07-06

## Execution Model

DetPS2 uses a **deterministic, cycle-driven execution model** centered around a single `Scheduler`.

### Core Flow

```
Ps2System.RunFor(N)
    → Scheduler.RunFor(N)
        → for each registered ISchedulable component:
            component.Step(maxCycles)
        → MasterCycles += N
```

- `RunFor(ulong cycles)` is the **only public entry point** for execution.
- All components implement the `ISchedulable` interface:
  ```csharp
  int Step(ulong maxCycles);
  void Reset();
  ```
- The `int` return value from `Step()` is currently captured but **not yet used** for back-pressure or variable timing (reserved for future accuracy work).

### Component Registration

Components are registered in `Ps2System.RegisterComponents()` in a fixed order. This order is stable and deterministic.

Current registration order:
1. Ps2System (self)
2. EmotionEngine
3. Dmac
4. Vif
5. Gif
6. Gs
7. Pcrtc
8. Intc
9. Iop
10. Cdvd
11. Sif

### Cycle Accounting

- `MasterCycles` lives in `Scheduler` and is the single source of truth for elapsed time.
- `Ps2System.MasterCycles` exposes it read-only.
- SaveState explicitly saves and restores `MasterCycles`.
- All timing-sensitive behavior should eventually be driven from `MasterCycles` rather than host timers.

### SaveState

- Magic header + versioned format.
- No `DateTime` or host clock is used.
- `MasterCycles` is persisted.
- EE, IOP, SIF, and placeholder sections for DMA/GS/VIF exist.

### Current Limitations (Phase 6.2)

- Fixed slice round-robin scheduling (no event queue yet).
- `Step()` return value is ignored for timing adjustment.
- Many components still use simplified / instantaneous timing.
- DMA, VIF, and GS timing are not yet cycle-accurate.

This architecture prioritizes **determinism and clean integration** over raw accuracy in early phases.
