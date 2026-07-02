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

## Phase 6: Advanced Accuracy & Integration

**Goal**: Deepen system integration and improve accuracy.

**Planned Work**:
- Full VU1 + Vif1 + Gif pipeline integration
- Higher accuracy floating-point handling with determinism focus
- Complete remaining VU instruction set
- Improved timing and synchronization between EE, IOP, and VUs
- Interrupt generation from VUs
- Expanded SaveState features

**Status**: Not started.

## Phase 7: Graphics Pipeline & Rendering

**Goal**: Implement a functional and accurate Graphics Synthesizer (GS).

**Planned Work**:
- Full GS register set and primitive rasterization
- Texture mapping, filtering, and blending
- Depth testing, alpha blending, and fog
- Framebuffer and display pipeline
- GIF path 1/2/3 handling
- Basic software renderer (later replaceable with hardware acceleration)

**Status**: Not started.

## Phase 8: IOP & Subsystem Completion

**Goal**: Complete the IOP subsystem and related components.

**Planned Work**:
- Full IOP (R3000A) instruction accuracy and timing
- Complete SIF DMA and command handling
- CDVD subsystem implementation
- Full interrupt controller (INTC) behavior
- DMA controller (DMAC) refinements
- SPR (Scratchpad RAM) and other memory regions

**Status**: Not started.

## Phase 9: System Integration & Compatibility

**Goal**: Achieve basic commercial game boot and compatibility.

**Planned Work**:
- BIOS boot improvements and HLE refinement
- Game loading and basic execution
- Fixing major compatibility blockers
- Sound/SPU2 stub or basic implementation
- Input handling foundation
- Save/load state robustness

**Status**: Not started.

## Phase 10: Accuracy Polish & Optimization

**Goal**: Improve overall accuracy and performance while maintaining determinism.

**Planned Work**:
- Cycle-accurate timing improvements
- Better floating-point and vector unit accuracy
- Performance optimizations (without breaking determinism)
- Memory access timing and bus emulation
- Scheduler improvements

**Status**: Not started.

## Phase 11: Tooling, Netplay & Advanced Features

**Goal**: Build developer tooling and prepare for advanced features like netplay.

**Planned Work**:
- Debugger and memory viewer
- Execution tracer and logging tools
- Save state compression and delta states
- Netplay foundation (deterministic replay, input recording)
- Optional hardware acceleration path (Vulkan/OpenGL)
- Documentation and contributor guidelines

**Status**: Not started.

---

## Guiding Principles

1. Determinism > Speed early on.
2. Small, verifiable milestones.
3. Clean, well-commented code.
