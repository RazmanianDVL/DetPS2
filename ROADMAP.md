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

## Phase 4: Determinism & Tooling
**Status**: Complete

---

## Phase 5: Vector Units + Accuracy (VU0 / VU1)

**Goal**: Implement VU0 and VU1 with high determinism and reasonable accuracy. This phase is critical for future netplay support.

**Core Principles for Phase 5**:
- Determinism first: All VU execution must be fully reproducible.
- No hidden host state or non-deterministic floating-point behavior in the hot path.
- Full state capture in SaveState (registers, accumulators, control state).
- Clean separation between VU0 (coprocessor) and VU1 (Vif1 + Gif path).

**Planned Work**:
- [ ] Create deterministic `VectorUnit` base class
- [ ] VU register file (32x 128-bit registers + accumulator)
- [ ] VU0 integration with Emotion Engine (COP2)
- [ ] VU1 + Vif1 + Gif pipeline integration
- [ ] Implement core VU instruction set (integer + controlled floating-point)
- [ ] Improve timing/synchronization between EE, IOP, and VUs
- [ ] Expand SaveState with real VU state
- [ ] Basic interrupt generation from VUs

**Status**: Just started. Determinism is the top priority.

---

## Guiding Principles

1. Determinism > Speed early on.
2. Small, verifiable milestones.
3. Clean, well-commented code.
