# DetPS2 Architecture Overview

**Last Updated**: 2026-07-22 (Phase 8 IOP & subsystems)

## Execution Model

DetPS2 uses a **deterministic, cycle-driven execution model** centered around a single `Scheduler`.

### Core Flow

```
Ps2System.RunFor(N)
    → Scheduler.RunFor(N)
        → for each registered ISchedulable component:
            component.Step(thisSlice)
        → MasterCycles += thisSlice
        → (optional) adjust next slice from work-cost utilization
```

- `RunFor(ulong cycles)` is the **only public entry point** for execution.
- All scheduled components implement:
  ```csharp
  int Step(ulong maxCycles);  // reported work for this slice
  void Reset();
  ```
- When `Scheduler.UseReportedWorkCost` is true, the sum of `Step` return values influences adaptive slice sizing via `HighUtilizationThreshold` / `LowUtilizationThreshold`.

### Component Registration Order

Fixed order in `Ps2System.RegisterComponents()` (do not reorder without reason):

1. EmotionEngine  
2. EeTimers  
3. Dmac  
4. Vif  
5. Gif  
6. Gs  
7. Pcrtc  
8. Intc  
9. Iop  
10. Cdvd  
11. Sif  

Related but not on the round-robin list: `Vu0` / `Vu1` (driven via VIF/EE COP2).  
`MmioBus` is attached to `SystemMemory` for physical range `0x10000000+`.

### Cycle Accounting

- `MasterCycles` lives in `Scheduler` and is the single source of truth.
- `Ps2System.MasterCycles` exposes it read-only.
- SaveState saves and restores `MasterCycles` via `Scheduler.SetMasterCycles`.
- No host timers (`DateTime`, `Stopwatch`) in the hot path or save/load path.

### SaveState

- Magic `0x44505332` + version header
- Persists: MasterCycles, RDRAM, EE GPRs/COP0, IOP GPRs, SIF status, DMA/GS placeholders
- Deterministic: no wall-clock fields

### Desktop Shell

`DetPS2.Desktop` (Avalonia) owns windowing, input, and framebuffer presentation only. Emulation runs through the same `Ps2System.RunFor` path as headless mode.

### Graphics path (Phase 7)

```
DMAC GIF / VIF1 / VU1 XGKICK
    → Gif.ReceivePath{1,2,3}
        → GIFtag (PACKED / REGLIST / IMAGE)
        → Gs.WriteGsRegister / vertex kick
            → primitive assemble → rasterize → FB
    → Pcrtc.Present / VBlank → Intc
```

`GsPipeline` is the high-level façade for path submit + present.

### SIF RPC ABI (Phase 13)

16-byte packet in EE memory:

| Offset | Field | Meaning |
|--------|--------|---------|
| +0 | cmd | `SifRpcCmd.*` |
| +4 | eeBuffer | EE payload address |
| +8 | size | length / LBA / fd |
| +12 | result | filled by IOP dispatcher |

Submit: `Sif.SubmitRpc(addr)` or HLE `SysSifRpcCall`. Processed in `Sif.Step`.

### Subsystem path (Phase 8)

```
EeTimers.Tick → Intc.Raise(TimerN)
Intc notify → EmotionEngine.SyncInterruptsFromIntc → COP0 Cause IP
Sif SIF0/SIF1 → byte copy EE RDRAM ↔ IOP RAM @ 0x1C000000
Cdvd.ReadSector → 2048B buffer (ISO or deterministic stub)
Memory: SPR @ 0x70000000 (before translate), MMIO via MmioBus
```

### Current Limitations

- Fixed-slice round-robin (no event queue yet)
- Many components use simplified / instantaneous timing
- DMA/VIF/GS timing are not cycle-accurate
- GS texturing is a PSMCT32/local-mem subset (not full CLUT/swizzle)
- EE does not yet take exception vectors on IRQ (Cause flagged only)
- Full commercial BIOS/game boot is Phase 9+

This architecture prioritizes **determinism and clean integration** over raw accuracy in early phases.
