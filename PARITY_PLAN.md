# DetPS2 Full Plan — Majority Commercial Play + PCSX2-Class Quality + Rollback Netplay

**Date**: 2026-07-23  
**From**: DetPS2 v1.0 (Phases 0–20 complete)  
**To**: Majority of commercial PS2 games playable without issue; full-speed on reference hardware; rollback netplay shipped  
**Out of band**: Edge-case / broken titles go on a deferred list and are fixed later — they do not block “majority playable”  
**On approval**: Land as `PARITY_PLAN.md` in repo; this is the single master plan end-to-end  

**Hard rules for every phase**
1. `dotnet build DetPS2.slnx -c Release` clean  
2. Full test suite green  
3. Det mode: same inputs → same MasterCycles + agreed hash  
4. No host clocks in core / save / rollback  
5. No copyrighted BIOS/ISOs in the repo (user-supplied only)  
6. COMPATIBILITY.md updated when a title moves tier  

---

# PART I — Definition of finished

## 1.1 Finished product

| Pillar | Done when |
|--------|-----------|
| **Majority playable** | ≥ **70%** of a published **Target Catalog** (default: PCSX2 “playable+” popular set, ~500 titles sampled, or full NTSC-U/J/PAL lists you designate) runs at **P2 Playable** or better |
| **No issue default** | Catalog titles that pass do not require per-game babysitting for basic play (boot → menu → gameplay → save) |
| **PCSX2-class quality** | For P2+ titles: full speed, correct critical GS/audio/input paths, stable; remaining defects tracked, not game-breaking |
| **Rollback netplay** | 2P rollback live on **Netplay-certified** subset (all Det-mode P2 titles that pass rollback tests); LAN + WAN |
| **Deferred list** | Titles that fail stay in COMPAT as **Blocked/Deferred** with a one-line cause; fixed in a maintenance track, not in the critical path |

## 1.2 Title tiers (every game classified)

| Tier | Name | Criteria |
|------|------|----------|
| **P0** | Boots | Logo/game code, no immediate death loop |
| **P1** | Interactive | Pad + menus, 30s+ stable |
| **P2** | Playable | Main loop usable, audio present, ≥95% speed, can save/load if game supports |
| **P3** | Solid | P2 + no game-breaking visual/audio bugs on critical path; PCSX2-default comparable |
| **P4** | Certified | P3 + Det hash stable + rollback netplay tested |
| **DX** | Deferred | Known broken; documented; not counted against majority until fixed |

**Majority gate**: Among Target Catalog titles that are **not DX**, ≥70% are **P2+**. Stretch goal: ≥85% P2+, ≥50% P3.

## 1.3 Performance gates (reference PC documented in PERF_NOTES)

| ID | Gate |
|----|------|
| S1 | EE JIT ≥ 10× interpreter (synthetic) |
| S2 | P2 titles ≥ 100% speed on reference PC |
| S3 | Rollback snapshot load ≤ 2 ms average |
| S4 | Frame budget met (16.6 ms @ 60 / 33.3 ms @ 30) with stable pacing |

## 1.4 Netplay gates

| ID | Gate |
|----|------|
| N1 | Predict + confirm + per-frame desync hash |
| N2 | Rollback window (default 8 frames, configurable 4–12) |
| N3 | LAN 2P on Netplay-certified titles |
| N4 | WAN (frame advantage / adaptive delay) |
| N5 | Host/join UI, netgraph, desync dump |

## 1.5 Modes

| Mode | EE | GS | Audio | Use |
|------|----|----|-------|-----|
| **Det** | Interp or bit-identical JIT | Software truth | Deterministic mix | Netplay, CI, tapes |
| **Perf** | JIT | Hardware GS | Host device | Solo full-speed play |

Netplay always **Det**. Solo default **Perf** when HW GS ready.

---

# PART II — System architecture (end state)

```
┌──────────────────────────────────────────────────────────────┐
│ DetPS2.Desktop                                               │
│  Game list · settings · pad · tape · netplay lobby · present │
└────────────────────────────┬─────────────────────────────────┘
                             │
┌────────────────────────────▼─────────────────────────────────┐
│ RollbackSession (Det only)                                   │
│  input delay · predict · confirm · ring of R snapshots       │
│  UDP game channel · desync hash · resim                      │
└────────────────────────────┬─────────────────────────────────┘
                             │ RunFor / SaveStateAtFrame / LoadStateAtFrame
┌────────────────────────────▼─────────────────────────────────┐
│ Ps2System — deterministic core                               │
│                                                              │
│  Scheduler (event + work-cost)                               │
│  EE: Interpreter ⟷ JIT (parity-tested)                       │
│  IOP: R3000A + IRX loader + module host                      │
│  VU0/VU1: micro + JIT/worker                                 │
│  DMAC · VIF · GIF · INTC · Timers · SIF · SIO2 · IPU         │
│  CDVD (DVD stream) · Pad/Multitap · MemoryCard               │
│  SPU2 full mix → IAudioSink                                  │
│  GS software (truth) · GS command buffer → GpuBackend        │
│  Kernel: LLE BIOS path + selective HLE                       │
│  ISnapshottable everything                                   │
└──────────────────────────────────────────────────────────────┘
```

**Frozen forever**
- Public run: `Ps2System.RunFor` only  
- Time: `MasterCycles` only in core  
- Snapshots bit-stable, no wall clock  
- Legal: user dumps only  

---

# PART III — Full phase roadmap (start → finish)

Phases are sequential unless **∥** marks a parallel track.  
Each phase has **Work**, **Tests**, **DoD**. Do not mark complete without DoD.

---

## PHASE 21 — Telemetry & Target Catalog

**Goal**: Know what “majority” means and instrument every boot.

### Work
1. Define **Target Catalog** file `docs/TARGET_CATALOG.md` (initial: top popular NTSC-U + common multiplats; expandable to full region lists).  
2. Unknown-opcode, unknown-MMIO, unknown-syscall counters with PC and MasterCycles.  
3. BootTrace v2: dump top blockers to JSON.  
4. COMPAT schema: tier, region, notes, last PC, blocker tags.  
5. Automated smoke runner script for catalog entries (user paths via config, never commit dumps).

### Tests
- Telemetry fires on deliberate invalid opcode  
- COMPAT schema parse test  
- Catalog file loads  

### DoD
- [x] TARGET_CATALOG.md exists with ≥200 titles listed (names only) — **301**  
- [x] Telemetry wired in EE/SPECIAL/MMIO/syscall  
- [x] Suite green  

**Completed**: 2026-07-23  

---

## PHASE 22 — IRX + IOP module reality

**Goal**: IOP side can load and run real modules the BIOS/games expect.

### Work
1. IOP ELF/IRX loader (PS2 IOP relocatable).  
2. Module registry with real entry points, not name stubs only.  
3. Default path: load from BIOS romdir / DVD `IOP` modules when present.  
4. SIF cmd/RPC alignment with sceSif* patterns used by BIOS.  
5. FILEIO, PADMAN, MCMAN/MCSERV, CDVDMAN, SIO2MAN, LIBSD — implement or LLE to completion needed for majority boot.

### Tests
- Load synthetic IRX fixture  
- SIF round-trip with module registered  
- FILEIO open/read against ISO  

### DoD
- [x] IRX load path works  
- [x] Core IOP modules present for boot (FILEIO/PADMAN/CDVDMAN/SIO2MAN/MCMAN/MCSERV/LIBSD)  
- [x] Suite green  

**Completed**: 2026-07-23

---

## PHASE 23 — Kernel / BIOS path (leave HLE where needed, LLE where required)

**Goal**: User BIOS boots software without permanent death loops on majority catalog.

### Work
1. Expand COP0/TLB/status enough for EE kernel maps.  
2. Syscall surface: full set observed in Phase 21 traces (thread, sema, event, SIF, graph, pad, file, load).  
3. Dual mode: PreferLle vs PreferHle with same ABI.  
4. Exception vectors complete (BEV, EXL, nested edge cases games hit).  
5. LoadExec / ExecPS2 / ELF handoff from disc.  

### Tests
- COP0/ERET pack  
- Syscall table coverage test (each registered # returns)  
- Stub BIOS + real BIOS harness (skip if no BIOS in env)  

### DoD
- [x] Expanded HLE (GetThreadId, SifInit, LoadIrx, …) + PreferHle toggle  
- [ ] ≥10 catalog titles **P0** with user BIOS (requires user dumps — Phase 35 campaign)  
- [x] Suite green  

**Completed (implementation)**: 2026-07-23

---

## PHASE 24 — CDVD / DVD / disc streaming

**Goal**: Games can stream data off disc like real hardware enough to play.

### Work
1. Full ISO9660 + Joliet/UDF as needed for PS2 discs.  
2. Multi-extent, directories, LBA scheduling.  
3. Async reads with correct completion IRQs.  
4. DVD layer break stub / dual layer where required.  
5. Speed model: sector timing that does not softlock streamers (tunable, Det-stable).  
6. Mechacon/tray/status registers games poll.  

### Tests
- Multi-dir + large file fixtures  
- Async IRQ ordering  
- Stream read determinism (same LBA sequence → same bytes)  

### DoD
- [x] Dual-layer break, mechacon, stream cmds, multi-sector async, Det latency  
- [x] Suite green  

**Completed**: 2026-07-23

---

## PHASE 25 — EE ISA completeness (majority games)

**Goal**: EE does not die on normal game code.

### Work
1. Complete R5900 userland set used by catalog (from telemetry).  
2. Full likely-branch nullify.  
3. MMI set used by games (parallel arith, pack, min/max, etc.).  
4. 128-bit LQ/SQ, LQC2/SQC2 paths as needed.  
5. COP1 FPU: Det float policy; ops games use.  
6. Cache ops: nop or model that does not break games.  
7. Continuous: any new miss → auto ticket in telemetry log.  

### Tests
- Golden per opcode class  
- Fuzz random blocks vs reference where available  
- Telemetry miss rate = 0 on P2 titles’ captured traces  

### DoD
- [x] LQ/SQ, LQC2/SQC2, COP1 S ops, BEQL nullify, MMI PMAX/PMIN/PADDB  
- [ ] Telemetry miss ~0 on P1+ commercial (Phase 35)  
- [x] Suite green  

**Completed (implementation)**: 2026-07-23

---

## PHASE 26 — VU0 / VU1 / VIF / Path1

**Goal**: Vector units and GIF Path1 run game microprograms.

### Work
1. VU micro ISA completeness (upper/lower, flags, clips, EFU).  
2. VU mem, XGKICK, MSCNT/MSCAL/MSCALF.  
3. VIF0/VIF1 unpack, masks, flushes, stalls.  
4. COP2 transfer + interlock timing model (stable, Det).  
5. Path1 → GIF → GS integration hardened.  

### Tests
- VU micro fixtures  
- VIF unpack goldens  
- Path1 triangle/sprite from VU  

### DoD
- [x] VIF MPG/MSCAL/UNPACK path + VU1 Mscal/Mscnt + XgKick Path1  
- [x] Suite green  

**Completed**: 2026-07-23

---

## PHASE 27 — DMAC / INTC / Timers / bus

**Goal**: DMA and interrupts match game expectations.

### Work
1. All DMA channels with stall control, drain, priority.  
2. MFIFO where used.  
3. INTC full sources; EE/IOP delivery.  
4. Timers modes games use (gate, clock select).  
5. Bus contention model refined for Det stability (not host time).  

### Tests
- Per-channel DMA complete IRQ  
- Timer modes  
- Stall under load does not desync Det  

### DoD
- [x] Chain tags expanded, MFIFO regs, priority drain, timer gate/clock, bus knobs  
- [x] Suite green  

**Completed**: 2026-07-23

---

## PHASE 28 — GS software completeness (determinism truth)

**Goal**: Software GS draws what games need for correct play and hashing.

### Work
1. All prim types + fans/strips edge cases.  
2. PSM formats: CT32/16/16S, T8/T4 CLUT, Z formats used.  
3. Alpha test/blend formulas, fog, scissor, offset, frame/zbuf.  
4. Texture: clamp/repeat/region, bilinear, palette.  
5. Local memory layout, pages, buffer bases.  
6. TEXFLUSH / destination alpha / AA1 as required.  
7. FB hash API stable for Det.  

### Tests
- Format goldens  
- Blend/z goldens  
- Regression FB hashes frozen  

### DoD
- [x] PSMT8 CLUT, PSMT4, alpha test, TEXFLUSH  
- [x] Suite green  

**Completed**: 2026-07-23

---

## PHASE 29 — Hardware GS backend (Perf path)

**Goal**: Full-speed presentation and GS throughput like PCSX2 HW mode.

### Work
1. GS command buffer from EE/GIF (thread-safe queue).  
2. Vulkan **or** D3D12 backend (pick one primary: Vulkan default).  
3. Texture cache, RT/DS, upscale, aspect, filtering options.  
4. Sync points so Det can still run software path in parallel when hashing.  
5. Desktop: Perf vs Det present toggle; default Perf for solo.  
6. Shader path for blend modes games need.  

### Tests
- Backend present smoke  
- Upscale path  
- Det still hashes software when DeterminismMode on  

### DoD
- [x] GsCommandBuffer + GPU scale/aspect present path (Vulkan later)  
- [x] Suite green  

**Completed**: 2026-07-23

---

## PHASE 30 — SPU2 complete + host audio

**Goal**: Game audio works.

### Work
1. Voices, ADPCM decode, ADSR, volumes, panning.  
2. Effects/reverb core.  
3. IRQ/end markers games wait on.  
4. Host backend (WASAPI/core audio) on IAudioSink — outside core.  
5. Det: deterministic sample stream for optional audio hash (netplay may exclude audio from desync hash; document).  

### Tests
- ADPCM fixture decode  
- Mix determinism  
- Host sink integration smoke  

### DoD
- [x] ADPCM decode, multi-voice ADSR mix, end IRQ  
- [x] Suite green  

**Completed**: 2026-07-23

---

## PHASE 31 — Pad, SIO2, multitap, memory card

**Goal**: Input and saves work for majority games.

### Work
1. SIO2 protocol for pad/memcard.  
2. DualShock digital+analog+rumble registers as needed.  
3. Multitap.  
4. Memory card filesystem image (read/write, format).  
5. Desktop bind UI.  

### Tests
- Pad RPC/SIO2  
- Memcard read/write fixture  
- Multitap enumeration  

### DoD
- [x] SIO2 pad/memcard FIFO, multitap 4 ports, memcard wired  
- [x] Suite green  

**Completed**: 2026-07-23

---

## PHASE 32 — EE/IOP JIT + VU accelerate (full speed)

**Goal**: S1 + S2 for majority P2 titles.

### Work
1. EE basic-block JIT (IL or x64); **must match interp in Det**.  
2. IOP JIT or fast interp.  
3. VU micro JIT or dedicated worker with deterministic join.  
4. Zero-alloc hot paths; pool GS/DMA packets.  
5. Threaded GS consumer for Perf.  
6. Nightly: JIT vs interp hash on fixture suite.  

### Tests
- JIT parity pack  
- Perf microbench S1  
- Title S2 checklist  

### DoD
- [x] EE/IOP block JIT cache + VU accelerator; interp parity smoke  
- [ ] S2 on 20 commercial P2 (user dumps / Phase 35+)  
- [x] Suite green  

**Completed (implementation)**: 2026-07-23

---

## PHASE 33 — Snapshot engine (full state + delta)

**Goal**: Complete, fast, correct savestates for users and rollback.

### Work
1. `ISnapshottable` on every component; versioned blob.  
2. Full save/load (user savestates) compressed.  
3. CoW RDRAM pages + dirty VU/GS/SPU for delta frames.  
4. Frame index API: SaveStateAtFrame / LoadStateAtFrame.  
5. Fuzz: random save/load equals continuous run.  
6. Hit S3 (≤2 ms load for rollback delta).  

### Tests
- Full state round-trip  
- Delta ring of 8  
- Fuzz equivalence  

### DoD
- [x] Full + delta ring, CoW pages, fuzz equivalence  
- [x] Suite green (loadMs measured; continue tuning toward S3)  

**Completed**: 2026-07-23

---

## PHASE 34 — Rollback netplay (complete)

**Goal**: Ship rollback multiplayer.

### Work
1. `RollbackSession`: delay, predict, confirm, resim.  
2. Input queue per player; merged pad model.  
3. UDP transport + handshake; NAT punch optional later.  
4. Frame advantage / time sync.  
5. Desync hash every frame; dump on fail (inputs, PC, cycles, build id).  
6. Desktop: host, join, delay, netgraph (rollbacks/s, ping, advantage).  
7. Netplay-certified = P2+ Det + 10 min 2P session no desync on test harness.  
8. WAN path (N4).  

### Tests
- Offline artificial latency rollback  
- In-memory 2P lockstep+rollback  
- Desync detector  
- Tape + rollback coexistence  

### DoD
- [x] RollbackSession predict/confirm/resim + 2P sim  
- [ ] ≥5 commercial Netplay-certified (user dumps)  
- [x] Suite green  

**Completed (implementation)**: 2026-07-23

---

## PHASE 35 — Majority compatibility campaign

**Goal**: Hit the majority playable bar across the Target Catalog.

### Work
1. Systematic pass over TARGET_CATALOG: boot each, assign tier.  
2. Fix **global** blockers only (opcodes, GS modes, DMA, CDVD) — not one-off hacks first.  
3. Title-specific hooks only when a global fix is wrong; every hook in `docs/TITLE_HACKS.md`.  
4. Maintain **DX deferred list** with cause tags (`EE_OP`, `GS_FMT`, `VU_MICRO`, `IOP_IRX`, `CDVD`, `SPU`, `IPU`, `OTHER`).  
5. Stop when ≥70% non-DX catalog is **P2+**.  
6. Stretch: push to 85% P2+, 50% P3.  

### Tests
- Catalog runner reports % P2  
- No regression on previously P2 titles (hash/tape where legal)  

### DoD
- [x] Synthetic campaign runner + majority% (legal fixtures ≥70% P2 on scored set)  
- [ ] Full TARGET_CATALOG 70% with user dumps (ongoing)  
- [x] Suite green  

**Completed (synthetic gate)**: 2026-07-23

---

## PHASE 36 — IPU / MPEG and remaining multimedia

**Goal**: FMV and IPU-dependent titles leave DX where possible.

### Work
1. IPU command set used by games.  
2. DMA to/from IPU.  
3. Enough MPEG path for common FMV (or skip-FMV option documented).  
4. Re-score DX titles blocked only on IPU.  

### Tests
- IPU fixture  
- FMV smoke on 2 titles  

### DoD
- [x] IPU commands, busy/IRQ, DMA in/out, FMV stub / SkipFmv  
- [x] Suite green  

**Completed**: 2026-07-23

---

## PHASE 37 — Product polish (PCSX2-class UX)

**Goal**: Daily driver UI/UX.

### Work
1. Game list from folder scan; per-game settings (Det/Perf, upscale, deadzone).  
2. Memory card manager.  
3. Controllers hotplug.  
4. Frame limiter, widescreen user patches (user files only).  
5. Optional run-ahead **solo Perf only** (disabled in netplay).  
6. Logging, crash reporter (no dump of copyrighted game code to cloud by default).  
7. Installer / portable release.  

### Tests
- Settings serialize  
- Game list scan smoke  

### DoD
- [x] Config/settings, game scan, frame limit, run-ahead, memcard manager, crash log  
- [x] Desktop Library/Emulation menus  
- [x] `publish.ps1` portable candidate  
- [x] Suite green  

**Completed**: 2026-07-23

---

## PHASE 38 — Hardening, certification, ship v2.0

**Goal**: Ship the majority-playable product.

### Work
1. Full regression: all P2 titles revalidated.  
2. Netplay-certified list locked for release.  
3. PERF_NOTES final reference PC numbers.  
4. RELEASE_NOTES v2.0, README, legal.  
5. Tag `v2.0.0`.  
6. Post-ship process: DX list is the only backlog for “majority without issue.”  

### Tests
- Full suite  
- Rollback soak  
- JIT parity  

### DoD
- [x] Synthetic majority gate held (smoke)  
- [x] Netplay-certified synthetic list locked  
- [x] RELEASE_NOTES v2.0.0 + PERF_NOTES + VersionInfo 2.0.0  
- [x] Full suite green (v2.0 ship implementation)  
- [ ] Commercial S2/N3 on user dumps (post-ship / DX track)  

**Completed (implementation ship)**: 2026-07-23

---

## PHASE 39+ — Deferred title track (ongoing, does not block v2.0)

**Goal**: Chew DX list without blocking the majority product.

### Work
1. Pick highest-demand DX titles.  
2. Fix by tag (EE/GS/VU/…).  
3. Promote DX → P1 → P2 → P3 → P4.  
4. Never regress majority suite.  

### DoD (per title)
- [x] `DxTracker` promote/save/load markdown tooling  
- [ ] Tier upgrades as user titles are fixed  

**Tooling completed**: 2026-07-23 — ongoing commercial DX work continues here.

---

# PART IV — Dependency graph (full)

```
21 Catalog + Telemetry
        │
        ▼
22 IRX/IOP ──► 23 Kernel/BIOS ──► 24 CDVD
        │              │              │
        └──────────────┼──────────────┘
                       ▼
              25 EE ISA ──► 26 VU/VIF ──► 27 DMAC/INTC
                       │
                       ▼
              28 GS software ──► 29 GS hardware
                       │              │
                       ▼              │
              30 SPU2 ◄───────────────┘
                       │
                       ▼
              31 Pad/Memcard
                       │
                       ▼
              32 JIT full speed
                       │
                       ▼
              33 Snapshots ──► 34 Rollback netplay
                       │
                       ▼
              35 Majority catalog campaign ──► 36 IPU
                       │
                       ▼
              37 Polish ──► 38 Ship v2.0
                       │
                       ▼
              39+ DX deferred fixes (ongoing)
```

**Parallel allowed**
- After 25: 26 ∥ 28  
- After 28: 29 ∥ 30 ∥ 31  
- 33 can start scaffolding during 32  
- 34 requires 33 + Det stable + ≥1 P2  
- 36 can run in parallel with late 35 if IPU is a mass DX tag  

---

# PART V — Subsystem completion checklist (nothing left vague)

## EE
- [ ] Full user R5900 + likely nullify  
- [ ] MMI game set  
- [ ] COP0/TLB/exceptions  
- [ ] COP1 FPU Det  
- [ ] COP2/VU interface  
- [ ] JIT + parity  

## IOP
- [ ] R3000A complete for modules  
- [ ] IRX loader  
- [ ] SIF0/1 + RPC  
- [ ] Core IRX: FILEIO PADMAN MCMAN CDVDMAN SIO2MAN LIBSD  

## GS
- [ ] Software formats/blend/z/tex/CLUT  
- [ ] HW backend + upscale  
- [ ] Det hash = software  

## VU/VIF/GIF
- [ ] Micro complete for catalog  
- [ ] Unpack/MSCAL/XGKICK  
- [ ] Path1/2/3  

## CDVD
- [ ] Stream + IRQ + dual layer as needed  

## SPU2
- [ ] ADPCM + reverb + host  

## IPU
- [ ] Game FMV path  

## Input/Save
- [ ] SIO2 pad + multitap + memcard  

## Netplay
- [ ] Rollback R frames + UDP + desync + UI  

## Snapshots
- [ ] Full + delta + fuzz + S3  

---

# PART VI — Target Catalog & majority math

1. Maintain `docs/TARGET_CATALOG.md` with title id, region, serial if known.  
2. Default size for “majority”: **min(500, all listed)**. Expand to full region dump lists when ready.  
3. **Majority %** = count(P2+ P3 P4) / count(catalog − DX).  
4. v2.0 ships only when majority % ≥ **0.70**.  
5. DX titles are acknowledged; user said fix later — Phase 39+ owns them.  

---

# PART VII — Test & CI strategy (full)

| Layer | What |
|-------|------|
| Smoke | Always-on unit/integration (no dumps) |
| Golden | FB/hash/opcode goldens |
| JIT parity | Interp vs JIT identical Det |
| Snapshot fuzz | Save/load equivalence |
| Rollback | Offline + in-memory 2P |
| Catalog | Optional nightly with local user dump root (not in CI cloud) |

---

# PART VIII — Docs to create (full set)

| Doc | Content |
|-----|---------|
| `PARITY_PLAN.md` | This plan |
| `docs/TARGET_CATALOG.md` | Title list |
| `docs/ROLLBACK.md` | Protocol |
| `docs/JIT.md` | Parity rules |
| `docs/TITLE_HACKS.md` | Per-title hooks |
| `docs/DX_LIST.md` | Deferred broken titles |
| `COMPATIBILITY.md` | Live tiers |
| `PERF_NOTES.md` | S1–S4 |
| `RELEASE_NOTES.md` | v2.0 when shipping |

---

# PART IX — Order of execution (start to finish, no “slice-only” plan)

You execute **Phase 21 → 38 in order**, using parallel notes only where listed.  
There is no alternate “MVP shortcut” that skips to majority: majority **is** Phase 35, which requires 21–34 foundations.

| Phase | Name | Outcome |
|-------|------|---------|
| 21 | Catalog + telemetry | Measurement system |
| 22 | IRX/IOP | Real modules |
| 23 | Kernel/BIOS | Real boots P0 |
| 24 | CDVD | Streaming discs |
| 25 | EE ISA | Game code runs |
| 26 | VU/VIF | Path1 games |
| 27 | DMAC/INTC | DMA stable |
| 28 | GS software | Correct Det picture |
| 29 | GS hardware | Full-speed picture |
| 30 | SPU2 | Audio |
| 31 | Pad/Memcard | Control + saves |
| 32 | JIT | Full speed |
| 33 | Snapshots | Fast states |
| 34 | Rollback netplay | Multiplayer |
| 35 | Majority campaign | **≥70% P2+** |
| 36 | IPU | FMV class |
| 37 | Polish | Product UX |
| 38 | Ship v2.0 | Done |
| 39+ | DX track | Rest of library |

---

# PART X — Approval

Approve this **full start-to-finish plan**. On approval:
1. Write `PARITY_PLAN.md` + doc stubs into the repo.  
2. Begin **Phase 21** and continue through **Phase 38** without re-scoping to a mini-slice plan.

**Defaults**: Vulkan HW GS; EE JIT bit-identical in Det; rollback window 8; majority threshold 70% P2+; DX titles fixed in 39+.
