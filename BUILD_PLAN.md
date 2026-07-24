# DetPS2 Full Build Plan — To Completion

**Purpose**: Single source of truth for shipping DetPS2Sharp from the current foundation through commercial-boot readiness and advanced tooling.  
**Rule**: Each work session completes **one full phase** (definition of done below). Do not partially ship a phase.  
**Date**: 2026-07-22

---

## North Star

A pure-C# PS2 emulator that:

1. Boots legal BIOS and runs a meaningful set of homebrew + commercial titles.
2. Is **bit-deterministic** given identical inputs and master-cycle timestamps.
3. Has a usable desktop UI, save states, and developer tooling.
4. Never ships copyrighted BIOS/game media.

**Honest scope note**: A cycle-accurate PCSX2 competitor is multi-year for a large team. This plan defines a **complete product arc** for DetPS2: functional, deterministic, progressively accurate — not a reimplementation of every GS corner case on day one. Each phase has a hard definition of done and automated tests.

---

## Current Baseline (complete)

| Phase | Name | Status |
|-------|------|--------|
| 0 | Foundation (memory, project, determinism flags) | Done |
| 1 | Emotion Engine interpreter skeleton | Done |
| 2 | Bits to pixels (early GS path) | Done |
| 3 | Boot real software (ELF/BIOS load path) | Done |
| 4 | Determinism & tooling (save state, smoke tests) | Done |
| 5 | Vector units foundation | Done |
| 6 | Integration lockdown (`ISchedulable`, Scheduler) | Done |

---

## Phase 7 — Graphics Pipeline & Rendering

**Goal**: A real software GS that consumes GIF packets, draws primitives correctly, and presents a framebuffer.

### Work items
1. **GsRegisters** — full primary drawing/context register set; 64-bit register storage where needed (SCISSOR, FRAME, etc.); deterministic snapshot for save states.
2. **GIF** — Path3 (DMAC) tag parse for PACKED / REGLIST / IMAGE; Path2 (VIF1) and Path1 (VU1) entry points; NREG/REGS, NLOOP, EOP, PRE/PRIM.
3. **Primitive assembly** — PRIM types: Point, Line, LineStrip, Triangle, TriangleStrip, TriangleFan, Sprite; vertex kick on XYZ2/XYZ3; XYOFFSET; 12.4 fixed-point decode.
4. **Rasterizer** — points, lines (Bresenham), filled triangles (barycentric), sprites (axis-aligned quads); scissor; Gouraud color interpolate.
5. **Texturing** — TEX0-driven size/format subset (PSMCT32/PSMCT16); sample from GS local memory buffer or RDRAM; nearest + bilinear toggle; CLAMP modes (repeat/clamp).
6. **Fragment ops** — depth test (TEST_1 ZTE/ZTST); alpha test stub; alpha blend (ALPHA_1 Cs/Cd/As formula subset); fog factor interpolate.
7. **GsPipeline** — single orchestrator: register write → vertex → assemble → rasterize → FB.
8. **PCRTC** — present framebuffer to UI / PPM; track display start; VBlank raise hook on Intc.
9. **DMAC GIF** — correct start MADR for Path3; optional stall until processed.
10. **Stats** — primitive counts, pixels written (for tests).

### Definition of done
- [x] `dotnet build` clean; Phase 7 smoke tests pass.
- [x] GIF PACKED packet draws a triangle without calling `RenderTestScene`.
- [x] Sprite + textured triangle produce non-uniform pixels.
- [x] Depth test rejects far fragments when enabled.
- [x] Alpha blend mixes source over destination when ALPHA_1 configured.
- [x] Path1/Path2/Path3 APIs exist and Path3 is exercised by tests.
- [x] Desktop still shows framebuffer; test scene still works.

### Tests
- `Gif_PackedTriangle_WritesPixels`
- `Gs_Sprite_FillsRect`
- `Gs_DepthTest_RejectsFar`
- `Gs_AlphaBlend_Mixes`
- `Gs_TextureSample_NonUniform`
- Existing determinism tests remain green.

---

## Phase 8 — IOP & Subsystem Completion

**Goal**: Complete IOP-side execution and EE↔IOP coupling so BIOS services have a plausible target.

### Work items
1. Expand IOP R3000A ISA (loads/stores, branches, COP0 minimal, delay slots).
2. SIF DMA (SIF0/SIF1) with real buffer exchange + command queue.
3. CDVD: disc present, read TOC stub, sector read from host ISO path (optional).
4. INTC: full mask/stat registers; EE COP0 Cause/Status linkage on pending IRQ.
5. DMAC: all 10 channels, stall control, IRQ on complete.
6. Timers (EE T0–T3): count modes, compare IRQ.
7. Scratchpad + IOP RAM maps in SystemMemory/MmioBus.
8. Register MmioBus as central decode for known physical ranges.

### Definition of done
- [x] IOP can run a small hand-assembled loop deterministically.
- [x] SIF SendCommand + DMA round-trip updates EE-visible memory.
- [x] Timer compare raises INTC; EE sees pending via COP0 hook.
- [x] CDVD ReadSector returns deterministic zero-filled or ISO data.
- [x] Smoke tests for SIF DMA, timer IRQ, IOP step.

---

## Phase 9 — System Integration & Compatibility

**Goal**: BIOS reaches interactive state on a subset of paths; homebrew ELFs run; first commercial titles attempt boot.

### Work items
1. BIOS HLE for common syscalls (file IO stubs, thread stubs, graph stubs).
2. ELF loader: PS2-specific flags, entry + gp setup, BSS clear.
3. Disc boot path: ISO9660 root scan, SYSTEM.CNF parse, ELF load.
4. Input foundation: digital pad state → EE-readable regs; desktop key map.
5. SPU2 stub: registers accept writes; optional silence buffer.
6. Compatibility tracker doc + per-title notes.
7. Fix top blockers discovered during BIOS run (tight loops, missing MMIO).

### Definition of done
- [x] Documented BIOS boot progress (PC trace after N cycles without crash).
- [x] At least one public-domain/homebrew ELF runs a visible frame via GS.
- [x] SYSTEM.CNF parse + ELF load from ISO (test ISO fixture or synthetic).
- [x] Input sets pad bits readable by software stub.
- [x] Save/load state across a short BIOS run remains stable.

---

## Phase 10 — Accuracy Polish & Optimization

**Goal**: Closer timing, better VU/FP, faster hot paths without breaking determinism.

### Work items
1. Event-queue Scheduler (next-event times) replacing pure fixed-slice when preferred.
2. VU microprogram run loops; better COP2 interlock.
3. EE MMI subset used by games; cache-line aware optional model.
4. Deterministic float policy (soft-float or bit-stable IEEE mode documented).
5. Profile-guided hot path: Span, unsafe FB writes, batch GIF.
6. Memory bus contention stub affecting DMA/EE budgets.
7. Regression pack: golden MasterCycles + FB hashes for fixtures.

### Definition of done
- [x] Event scheduler mode passes same MasterCycles budget tests.
- [x] FB hash fixtures for Phase 7 scenes stay stable under optimisations.
- [x] Documented float policy; no host non-determinism in core.
- [x] Measurable RunFor timing recorded in PERF_NOTES / smoke (host-dependent; determinism is the gate).

---

## Phase 11 — Tooling, Netplay & Advanced Features

**Goal**: Ship-quality developer experience and multiplayer foundation.

### Work items
1. Debugger: breakpoints, step, register/memory view in Desktop.
2. Execution tracer (optional) with cycle stamps; diff tool docs.
3. Save state v4: compression, optional delta; GS/VU full dump.
4. Input recording/playback for deterministic replay.
5. Netplay foundation: lockstep over recorded inputs (LAN first).
6. Optional hardware present path (Veldrid/Vulkan) keeping software GS as source of truth for determinism mode.
7. Contributor guide + architecture freeze notes.

### Definition of done
- [x] Breakpoint can stop EE on address match.
- [x] Replay of recorded input yields identical MasterCycles + FB hash.
- [x] Save state compresses below raw RDRAM size for empty RAM.
- [x] CONTRIBUTING.md and final README ship section.

---

## Cross-Cutting Rules (all phases)

1. **ISchedulable only** — `int Step(ulong maxCycles); void Reset();`
2. **Run path only** — `Ps2System.RunFor`
3. **No host time** in core/save paths
4. **Every phase** updates ROADMAP.md status + adds smoke tests
5. **Build must be green** before phase is marked complete
6. **Legal** — never commit BIOS/ISOs

---

## Session Protocol

1. Read this file + ROADMAP for the **next incomplete phase**.
2. Implement **all** work items for that phase.
3. Add/expand tests; run full smoke suite.
4. Mark phase complete in ROADMAP + this file checklist.
5. Respond to the user only when the phase definition of done is met.

---

## Progress Tracker

| Phase | Status | Completed |
|-------|--------|-----------|
| 0–6 | Complete | 2026-07-22 |
| 7 Graphics | **Complete** | 2026-07-22 |
| 8 IOP & Subsystems | **Complete** | 2026-07-22 |
| 9 Integration & Compatibility | **Complete** | 2026-07-22 |
| 10 Accuracy & Optimization | **Complete** | 2026-07-22 |
| 11 Tooling & Netplay | **Complete** | 2026-07-22 |
| 12–20 Depth + ship | See [NEXT_PLAN.md](NEXT_PLAN.md) | **12–20 complete — v1.0** |

---

## Product “Done” (v1 arc)

Phases 0–11 form the v1 product arc (complete). Ongoing accuracy work continues in [NEXT_PLAN.md](NEXT_PLAN.md) (Phase 12+).
