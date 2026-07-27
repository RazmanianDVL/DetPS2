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
**Status**: Complete (ELF loader + BIOS load path)

## Phase 4: Determinism & Tooling
**Status**: Complete (save states, smoke tests, master-cycle determinism)

## Phase 5: Vector Units + Accuracy (VU0 / VU1)
**Status**: Complete (base ISA, conversions, EFU stall hooks, COP2 entry)

## Phase 6: Advanced Accuracy & Integration
**Status**: Complete (Integration Lockdown)

**Delivered**:
- Unified `ISchedulable` contract on all scheduled components
- Single execution path: `Ps2System.RunFor` → `Scheduler.RunFor`
- Work-cost aware adaptive slice sizing (`UseReportedWorkCost`)
- SIF → INTC interrupt raise
- SaveState restores `MasterCycles` (no host time)
- GS test scene + software rasterizer path
- Vu0/Vu1 wired into `Ps2System` / VIF / EE
- Solution (`DetPS2.slnx`), smoke test project, Avalonia desktop shell
- Clean Release build; all smoke tests pass

**Intentionally deferred** (higher phases):
- Full VU microcode / remaining ISA
- Event-queue scheduler (vs fixed-slice round-robin)
- Full VIF1 unpack + command processor integration
- EE COP0 interrupt delivery from INTC
- Expanded save-state component coverage (DMA/GS/VIF bodies)

## Phase 7: Graphics Pipeline & Rendering
**Status**: Complete

**Delivered**:
- Expanded 64-bit `GsRegisters` (PRIM/TEX/FRAME/SCISSOR/TEST/ALPHA/…)
- GIF Path1 / Path2 / Path3 APIs; PACKED (A+D), REGLIST, IMAGE tag formats
- Primitive assembly: point, line, line strip, triangle, strip, fan, sprite
- Software rasterizer: scissor, Gouraud, depth test/write, alpha blend formula, fog
- Texturing: procedural + local GS memory upload (PSMCT32), clamp/repeat
- `GsPipeline` orchestrator; PCRTC present + VBlank → INTC
- DMAC GIF uses start MADR for Path3
- Phase 7 smoke tests (sprite, depth, blend, texture, GIF packed, DMAC path3)

## Phase 8: IOP & Subsystem Completion
**Status**: Complete

**Delivered**:
- Expanded IOP R3000A: delay slots, LO/HI, loads/stores, branches, COP0 MFC/MTC/RFE, SYSCALL
- SIF command queue + SIF0/SIF1 DMA (EE RDRAM ↔ IOP RAM)
- CDVD: TOC stub, deterministic sector buffer, optional ISO/memory mount
- INTC STAT/MASK MMIO; EE COP0 Cause IP sync + `HasCop0Interrupt`
- EE Timers T0–T3 (prescale, compare IRQ, clear-on-compare)
- DMAC: 10 channels, stall, D_STAT/D_MASK, IRQ on complete, SIF/GIF/VIF hooks
- Memory: IOP RAM 2MB, SPR (untranslated), BIOS ROM window, MMIO bus
- MmioBus central decode for timers/INTC/DMAC/SIF
- Phase 8 smoke tests (IOP loop, SIF DMA, timer→COP0, CDVD, SPR, MMIO, DMAC IRQ)

## Phase 9: System Integration & Compatibility
**Status**: Complete

**Delivered**:
- `BiosHle`: graph/pad/file/thread/exit/write/timer syscalls (`$v1` number)
- ELF loader: PT_LOAD, BSS zero, MIPS reginfo GP, `LoadIntoEe`, minimal ELF builder
- Built-in homebrew GS demo ELF (clear + sprite + triangle via HLE)
- ISO9660 minimal reader/builder; `SystemCnf` parse; `DiscBoot` synthetic + image boot
- `PadInput` digital bits + MMIO + Desktop key map
- `Spu2` register stub + silence mix
- `BootTrace` PC sampling; stub BIOS absolute jump (lui/ori/jr)
- Optional EE IRQ exception vector (`TakeExceptions` → `0x80000200`)
- `COMPATIBILITY.md` tracker
- Phase 9 smoke tests (homebrew GS, ISO boot, pad, SPU2, boot trace, save-state)

## Phase 10: Accuracy Polish & Optimization
**Status**: Complete

**Delivered**:
- Event-queue scheduler mode (`ScheduleEvent`, exact MasterCycles budget)
- VU microprogram memory + run/stop (E-bit), COP2 interlock stalls on EE
- EE MMI subset (PAND/POR/PXOR/PNOR/PADDW/PSUBW/PEXT*/PCPY*)
- Optional I-cache line hit/miss accounting
- `DeterministicFloat` policy + FLOAT_POLICY.md (no FMA, NaN canonicalize)
- `BusContention` EE budget scaling under DMA
- GS hot path: `GetFramebufferSpan`, `ClearFast`
- `RegressionFixtures` FB FNV hash + cycle goldens
- PERF_NOTES.md; Phase 10 smoke tests

## Phase 11: Tooling, Netplay & Advanced Features
**Status**: Complete

**Delivered**:
- `Debugger`: breakpoints, step-one, register/memory format; Desktop Debug menu + reg panel
- `Tracer` v2: cycle-stamped in-memory/file log + `Tracer.Diff` (`docs/TRACE_DIFF.md`)
- Save state **v4**: Deflate envelope, IOP/SPR/pad/VU micro/INTC; empty RAM ≪ 32MB
- `InputRecording` tape (INPR) + identical replay hashes
- `NetplaySession` lockstep quanta over shared pad frames
- `IFramePresenter` / software + hardware stub; GS remains determinism source
- `CONTRIBUTING.md`, `ARCHITECTURE_FREEZE.md`

**Also**: hardware present is a stub (`HardwarePresentStub`); full Vulkan/OpenGL is future work on the same `IFramePresenter` seam.

---

## Phase 12+: Depth campaign

See **[NEXT_PLAN.md](NEXT_PLAN.md)**.

### Phase 12 — EE Kernel, COP0 & Exceptions
**Status**: Complete

- COP0 MFC0/MTC0 (Status, Cause, EPC, Count, Compare, BadVAddr, PRId, Config)
- ERET (clear EXL/ERL, restore PC)
- Exception vectors (BEV-aware); IRQ → `0x80000200` + ERET
- PreferHleSyscalls vs architectural SYSCALL
- LD/SD, LWL/LWR/SWL/SWR (simplified), CACHE/SYNC nop
- COP0 Count ticks with EE steps

### Phase 13 — SIF RPC & IOP modules
**Status**: Complete

- DetPS2 RPC ABI: 16-byte EE packet (cmd, buffer, size, result)
- Commands: open/close/read/write/seek/pad/cdvd/loadmodule/getmodule
- `IopModuleHost` defaults FILEIO, PADMAN, CDVDMAN, SIO2MAN
- `Sif.SubmitRpc` + `Step` processes queue; MMIO `+0x60` submit
- HLE: `SysSifRpcCall` (0x80), `SysLoadModule` (0x81), `SysSifRpcSync` (0x82)

### Phase 14 — Kernel HLE & BIOS path
**Status**: Complete

- `KernelState`: threads, semaphores, event flags, VBlank wait
- EE stalls on `WaitingVblank`; PCRTC → `Hle.OnVblank`
- Expanded BIOS HLE (0x40–0x4E, WaitVblank, LoadExec, FIO via RPC)
- `RunBiosHarness` / BootTrace PC sampling

### Phase 15 — EE / VU / GS accuracy
**Status**: Complete

- EE NOR / SLT / SLTU
- VU1 `XgKick` → GIF Path1
- GS PSMCT16 sample + `UploadTexture16`

### Phase 16 — ISO multi-dir + CDVD async + pad analog
**Status**: Complete

- ISO9660 recursive dirs + `BuildWithDirs`
- CDVD async read (cmd 0x13) + complete IRQ
- Dual-analog sticks; RPC status buffer (8 bytes)

### Phase 17 — Audio
**Status**: Complete

- `IAudioSink` + Capturing + `RingBufferAudioSink`
- SPU2 deterministic square mix @ 48 kHz (6144 cycles/sample)
- Desktop ring sink + meter drain (no host clock in core)

### Phase 18 — Netplay transport + tape UX
**Status**: Complete

- Wire format `NetplayFrameMsg`; in-memory + TCP transports
- Lockstep exchange + desync detector
- Desktop: Record/Play `.inpr`; Netplay Host/Client

### Phase 19 — GPU present path
**Status**: Complete

- `GpuFramePresenter` staging texture + upload stats
- `DeterminismMode` forces software GS as hash truth
- Desktop present mode toggle

### Phase 20 — Compatibility + v1.0
**Status**: Complete

- `TitleFixtures` synthetic campaign (4 titles)
- EE MULTU/DIVU/DSLL* + likely branches
- [RELEASE_NOTES.md](RELEASE_NOTES.md) — **v1.0 shipped**

### Phase 21 — Telemetry & Target Catalog (v2.0 campaign)
**Status**: Complete

- `Telemetry`: unknown opcode / SPECIAL / MMIO / syscall with PC + MasterCycles
- BootTrace v2 JSON + telemetry blockers
- `CompatEntry` schema + majority % helper
- [docs/TARGET_CATALOG.md](docs/TARGET_CATALOG.md) — **301** titles

### Phases 22–26 (v2.0 foundation)
**Status**: Complete (implementation + smoke)

- **22** IRX loader + MCMAN/LIBSD defaults + MemoryCard stub  
- **23** Kernel HLE expand + PreferHle toggle + LoadIrx syscall  
- **24** CDVD dual-layer/stream/async multi-sector  
- **25** EE LQ/SQ, LQC2/SQC2, COP1, BEQL nullify, more MMI  
- **26** VIF MSCAL/MPG + VU1 micro run  

### Phases 27–31
**Status**: Complete

- **27** DMAC chain/MFIFO/priority, timer gate/clock, bus knobs  
- **28** GS PSMT8/CLUT, alpha test, TEXFLUSH  
- **29** GsCommandBuffer + GPU scale/aspect  
- **30** SPU2 ADPCM + multi-voice ADSR  
- **31** SIO2, multitap, memory card  

### Phases 32–36
**Status**: Complete (implementation + smoke)

- **32** EE/IOP basic-block JIT + VU accelerator (Det parity)  
- **33** SnapshotEngine full/delta + CoW pages + fuzz  
- **34** RollbackSession (predict/confirm/resim, 2P sim)  
- **35** MajorityCampaign synthetic runner (gate math)  
- **36** IPU command/DMA/FMV stub  

### Phases 37–39 (v2.0 ship)
**Status**: Complete (implementation)

- **37** Settings, game library scan, frame limit, run-ahead, memcard I/O, crash log, Desktop menus, `publish.ps1`  
- **38** Version **2.0.0**, RELEASE_NOTES, PERF_NOTES, netplay-certified synthetic list  
- **39** `DxTracker` promote/save DX markdown (commercial DX ongoing)

**Product version**: **v2.0.0** — synthetic gates green; commercial majority needs user dumps.

### Phases 40–49 (commercial campaign → v3.0)
**Status**: Complete (implementation + synthetic DoD)

- **40** `UserMediaConfig` + `CommercialBootRunner`  
- **41** `BlockerRanker`, boot-spine HLE, ≥10 synthetic P0  
- **42** GS bilinear, VIF V4_32, homebrew P2  
- **43** Host audio, SPU2 reverb, `InputMapper`  
- **44** `VulkanFramePresenter` staging (Det hash unchanged)  
- **45** EE JIT `EmitIl`, FastDelta  
- **46** UDP + production rollback peer, netgraph, desync dump, soak cert  
- **47** Scored majority campaign, TITLE_HACKS, DxTracker reports  
- **48** IPU IQ/MPEG/SkipFMV + rescore policy  
- **49** **v3.0.0** Commercial ship (checklist, notes, publish)

**Product version**: **v3.0.0** — synthetic commercial gates green; real-catalog majority needs user dumps.

### Completeness campaign (Phases 50–56, v3.1.0)
**Status**: Complete (synthetic gates) — see [NEXT_PLAN.md](NEXT_PLAN.md) / [COMPLETENESS.md](COMPLETENESS.md) for the phase table.

**Product version**: **v3.1.0 Completeness**.

## Since v3.1.0 (not phase-numbered)

Work continues on real commercial bring-up against user-supplied dumps rather than further
numbered phases — see [NEXT_PLAN.md](NEXT_PLAN.md) for the current focus and
[docs/DEVELOPER_GUIDE.md](docs/DEVELOPER_GUIDE.md) for the dated, detailed investigation log
(general emulation/HLE bugs found and fixed via real commercial boot paths, a virtual HDD
foundation, and CLI tooling for scripted input testing).

## Guiding Principles

1. Determinism > Speed early on.
2. Small, verifiable milestones.
3. Clean, well-commented code.
4. Integration is a first-class responsibility — the project must always build and pass smoke tests.
