# DetPS2 Development Roadmap

**Goal**: Build a clean-slate, deterministic PlayStation 2 emulator from the ground up in pure modern C#.

Focus areas:
- **Determinism first** (master cycle counter, reproducible execution, save states, input movies)
- Clean, modern C# (value types, `Span<T>`, `Vector128<T>`, NativeAOT)
- Incremental milestones with real wins

---

## Phase 0: Foundation

- [x] Repository setup + public GitHub
- [x] Modern .NET 9 project with NativeAOT + determinism settings
- [x] `SystemMemory`
- [x] `EmotionEngine` skeleton
- [x] `Ps2System` master cycle coordinator
- [x] Basic test harness

**Status**: Complete

---

## Phase 1: Capable Emotion Engine Core

**Goal**: Run real homebrew and meaningful game code.

- [x] Broad MIPS instruction decoder (including JR/JALR, SLTI/SLTIU, etc.)
- [x] Basic COP0 + exception handling
- [x] Correct branch delay slot handling
- [x] LO/HI + multiply/divide

**Milestone**: Load and execute real homebrew ELF.

**Status**: **Complete**

---

## Phase 2: Bits to Pixels (First Major Visual Milestone)

**Goal**: Get actual graphics output.

- [x] DMAC (channels + chain mode + register interface)
- [x] GIF (PATH3 parsing + driving GS)
- [x] Minimal VIF support for PATH3 transfers
- [x] Software GS renderer (triangle, line, quad + PRIM dispatch + texture stub)
- [x] Clean end-to-end pipeline test producing real drawn geometry
- [x] PCRTC / final display output (via Pcrtc + PPM)

**Milestone**: Run code that draws something to the screen using the GS.

**Status**: **Complete** (as of 2026-07-01)

---

## Phase 3: Boot Real Software

**Goal**: Get closer to running commercial games.

- [ ] IOP + SIF communication layer (minimal Iop started)
- [ ] Basic CDVD / disc emulation
- [x] Timers + Interrupt Controller (Intc expanded)
- [ ] Minimal HLE syscalls
- [ ] SPU2 stub

**Status**: Early foundation in place

---

## Phase 4: Determinism & Tooling

- [ ] Multi-component scheduler
- [ ] Save states (MemoryMarshal + structs)
- [ ] Input recording / TAS support
- [ ] Execution tracer (Tracer.cs created)
- [ ] Memory/register viewer

**Status**: Prep work started (Tracer)

---

## Phase 5: Vector Units + Accuracy

- [ ] VU0 / VU1
- [ ] VU pipeline modeling

**Status**: Not started

---

## Phase 6: Polish, Performance & Long-term

- [ ] Advanced GS features (texturing, blending)
- [ ] GitHub Actions CI
- [ ] Full debugging UI

**Status**: Not started

---

## Guiding Principles

1. Determinism > Speed early on.
2. Every component steppable by cycle count.
3. Prefer value types and explicit state.
4. Small, verifiable milestones.
5. Keep code clean and well-commented.

Let's build something excellent.
