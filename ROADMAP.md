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

## Phase 5: Vector Units + Accuracy (VU0 / VU1)
**Status**: Complete

**Completed**:
- Created deterministic `VectorUnit` base class
- Implemented core VU instruction set (integer + basic vector ops)
- Added basic SaveState support for VU registers
- Integrated VU0 into Emotion Engine as COP2
- Functional COP2 instruction routing with full operand passing
- `ExecuteVuInstruction` properly decodes and executes VU0 instructions

**Note**: VU1 + Vif1 integration, advanced floating-point accuracy, and deeper timing work will be addressed in Phase 6.

---

## Phase 6: Advanced Accuracy & Integration

**Goal**: Improve overall system accuracy, integrate VU1, and move toward commercial game compatibility.

**Planned Work**:
- Full VU1 + Vif1 + Gif pipeline integration
- Higher accuracy floating-point handling with determinism focus
- More complete VU instruction set
- Improved timing and synchronization between EE, IOP, and VUs
- Interrupt generation from VUs
- Expanded SaveState features

**Status**: Not started.

---

## Guiding Principles

1. Determinism > Speed early on.
2. Small, verifiable milestones.
3. Clean, well-commented code.
