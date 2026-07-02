# DetPS2 Development Roadmap

**Goal**: Build a clean-slate, deterministic PlayStation 2 emulator from the ground up in pure modern C#.

---

## Phase 0: Foundation
**Status**: Complete

## Phase 1: Capable Emotion Engine Core
**Status**: Complete

## Phase 2: Bits to Pixels
**Status**: Complete

## Phase 3: Boot Real Software
**Status**: Complete

**Completed**:
- Timers + Interrupt Controller (Intc)
- IOP with many real instructions and high throughput
- SIF with functional DMA and command support
- Significantly expanded HLE syscalls (memory, thread, and common BIOS calls)
- CDVD as a proper stub
- Improved BIOS loading and boot flow
- Basic exception handling foundation

## Phase 4: Determinism & Tooling
**Status**: Complete

**Completed**:
- SaveState system (versioned, defensive loading, designed for future compression and netplay)
- Expanded component serialization (Memory, EE, IOP, SIF, Dmac, GS, Vif)

**Note**: SaveState captures a large amount of state. Full real-value serialization for all components will continue to be refined in later phases as needed.

---

## Guiding Principles

1. Determinism > Speed early on.
2. Small, verifiable milestones.
3. Clean, well-commented code.
