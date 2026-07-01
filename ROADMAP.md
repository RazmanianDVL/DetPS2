# DetPS2 Development Roadmap

**Goal**: Build a clean-slate, deterministic PlayStation 2 emulator from the ground up in pure modern C#.

Focus areas:
- **Determinism first** (master cycle counter, reproducible execution, save states, input movies)
- Clean, modern C# (value types, `Span<T>`, `Vector128<T>`, NativeAOT)
- Incremental milestones with real wins

---

## Phase 0: Foundation (Current)

- [x] Repository setup + public GitHub
- [x] Modern .NET 9 project with NativeAOT + determinism settings
- [x] `SystemMemory` (32 MB RDRAM + Scratchpad + basic KSEG translation)
- [x] `EmotionEngine` skeleton + minimal instruction dispatch
- [x] `Ps2System` master cycle coordinator
- [x] Basic test harness

**Status**: Done

---

## Phase 1: Capable Emotion Engine Core

**Goal**: Run real homebrew and meaningful game code.

### High Priority
- [x] Significantly expand MIPS instruction decoder (ADDI/ADDIU, ADDU/SUBU, SLT/SLTI/SLTU, AND/ANDI/XOR/XORI/NOR, shifts, JR/JALR, etc.)
- [x] Basic COP0 implementation (Status, Cause, EPC, exception handling)
- [x] Branch delay slot handling (correct)
- [ ] Simple I-cache / D-cache behavior (or accurate enough model) — *deferred to later*

### Medium Priority
- [x] LO/HI + multiply/divide with reasonable timing
- [x] Better exception and syscall entry points

**Milestone**: Load and execute a real PS2 homebrew ELF with meaningful behavior.

**Status**: **Complete** (as of 2026-07-01)

---

## Phase 2: Bits to Pixels (First Major Visual Milestone)

**Goal**: Get actual graphics output.

- [x] DMAC implementation (major channels + chain mode + register interface)
- [x] GIF (especially PATH3) — improved sequential parsing driving GS
- [ ] Minimal VIF support for PATH3 transfers — *deferred*
- [x] Software GS renderer (primitive handling + basic drawing: triangle, line, quad)
- [x] Clean pipeline test producing real drawn geometry from emulated commands
- [ ] PCRTC / frame output to a window — *using PPM for now (deterministic)*

**Milestone**: Run a homebrew that successfully draws something to the screen using the GS.

**Status**: **Strong progress — real primitives now drawn through full DMAC→GIF→GS pipeline** (as of 2026-07-01)

---

## Phase 3: Boot Real Software

**Goal**: Get closer to running commercial games.

- [ ] IOP + SIF communication layer
- [ ] Basic CDVD / disc emulation
- [x] Timers + Interrupt Controller (INTC) — *minimal skeleton started*
- [ ] Minimal HLE for key BIOS syscalls (or begin LLE BIOS exploration)
- [ ] SPU2 stub

**Milestone**: Boot a commercial PS2 game to a menu or in-game (with heavy HLE allowed).

**Status**: Early skeleton work begun (Intc + timers)

---

## Phase 4: Determinism & Tooling (Core Differentiator)

This phase makes DetPS2 special.

- [ ] Proper multi-component scheduler (correct EE + IOP clock domain interleaving)
- [ ] Save state system using `MemoryMarshal` + structs for byte-exact states
- [ ] Timestamped input recording + playback (TAS / movie support)
- [ ] Optional full execution tracer / logger
- [ ] Memory viewer + register inspector (initially console-based)
- [ ] Deterministic build mode + benchmarking harness

**Status**: Not started

---

## Phase 5: Vector Units + Accuracy

- [ ] VU0 (macro mode + micro mode)
- [ ] VU1 (micro mode + microprogram memory)
- [ ] Accurate VU pipeline modeling and stalls
- [ ] VU0 ↔ EE COP2 integration

**Status**: Not started

---

## Phase 6: Polish, Performance & Long-term

- [ ] Improve software GS renderer (texturing, blending, effects)
- [ ] Optional hardware-accelerated GS path (keep deterministic software path)
- [ ] Heavy `Vector128<T>` / intrinsics optimization where beneficial
- [ ] GitHub Actions CI
- [ ] Better ELF loader + debugging symbols support
- [ ] Full debugging UI (registers, memory, breakpoints, single-step)

**Status**: Not started

---

## Cross-Cutting Concerns

- Testing strategy (homebrew test suite, known-good ELFs, automated comparison where possible)
- Documentation (memory map, register behavior, determinism guarantees)
- Performance profiling harness
- Long-term: Netplay / rollback experimentation (enabled by strong determinism)

## Guiding Principles

1. **Determinism > Speed** early on. We can optimize later.
2. Every component should be steppable by cycle count.
3. Prefer value types (`struct`) and explicit state for reproducibility.
4. Small, verifiable milestones beat big rewrites.
5. Keep the codebase clean and well-commented — this is a learning + research project as much as an emulator.

Let's build something excellent.
