# DetPS2 Development Roadmap

**Goal**: Build a clean-slate, deterministic PlayStation 2 emulator from the ground up in pure modern C#.

---

## Phase 0: Foundation
**Status**: Complete

## Phase 1: Capable Emotion Engine Core
**Status**: Complete

## Phase 2: Bits to Pixels
**Status**: Complete

## Phase 3: Boot Real Software (Strong Progress - Approaching Completion)

**Goal**: Get closer to running commercial games.

**Current Progress**:
- [x] Timers + Interrupt Controller (Intc)
- [x] IOP with many real instructions
- [x] SIF with functional DMA and command support
- [x] Significantly expanded HLE syscalls
- [x] CDVD completed as a proper stub
- [x] Improved BIOS loading and boot flow

**Remaining**:
- [ ] More HLE syscalls + basic exception handling
- [ ] SIF DMA chaining improvements

**Status**: Strong progress. Approaching readiness for Phase 4.

## Phase 4: Determinism & Tooling (Started)

- [x] Initial SaveState foundation created (header + versioned format, designed for future compression/netplay)
- [ ] Full component serialization (Memory, EE, IOP, GS, etc.)
- [ ] Multi-component scheduler improvements
- [ ] Input recording / TAS support
- [ ] Execution tracer improvements
- [ ] Memory/register viewer

**Status**: Foundation started. Save states designed with netplay in mind.

---

## Guiding Principles

1. Determinism > Speed early on.
2. Small, verifiable milestones.
3. Clean, well-commented code.
