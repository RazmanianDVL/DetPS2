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
- [x] IOP with many real instructions + high execution capability
- [x] SIF with functional DMA, commands, and status
- [x] Significantly expanded HLE syscalls (many SIF and early boot calls)
- [x] CDVD completed as a proper stub with many commands
- [x] Improved BIOS loading and boot flow

**Remaining Work**:
- [ ] More HLE syscalls for broader BIOS compatibility
- [ ] Additional IOP instructions + basic exception handling
- [ ] SIF DMA chaining / tag support

**Milestone**: Be able to load and execute basic homebrew or early BIOS code with heavy HLE.

**Status**: Strong progress. Approaching readiness to begin Phase 4.

## Phase 4: Determinism & Tooling

- [ ] Multi-component scheduler
- [ ] Save states (MemoryMarshal + structs)
- [ ] Input recording / TAS support
- [ ] Execution tracer (Tracer.cs already exists)
- [ ] Memory/register viewer

**Status**: Not started (ready once Phase 3 is solid)

---

## Guiding Principles

1. Determinism > Speed early on.
2. Small, verifiable milestones.
3. Clean, well-commented code.
