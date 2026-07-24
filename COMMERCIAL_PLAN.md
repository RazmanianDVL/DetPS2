# DetPS2 Post–v2.0 Plan — Commercial Play & Production Quality

**Date**: 2026-07-23  
**Baseline**: DetPS2 **v2.0.0** — Phases **0–39** implementation complete; synthetic smoke/majority gates green.  
**Living doc on approval**: `COMMERCIAL_PLAN.md` in repo (do not confuse with completed `PARITY_PLAN.md`).

---

## 1. Status of the old plan

| Layer | Status |
|-------|--------|
| **Numbered phases 0–39** | **Done** (code + synthetic tests + Desktop + publish) |
| **Synthetic / CI product** | **Done** — homebrew, ISO fixtures, rollback sim, JIT parity |
| **Original north star** (majority commercial games playable like PCSX2 + real 2P rollback) | **Not done** — explicitly deferred to user dumps + deeper accuracy |

**Honest gap**: v2.0 is a **shippable foundation**, not a PCSX2 replacement. Unchecked items still open in `PARITY_PLAN.md` (commercial P0/P1 counts, S2, commercial netplay-certified, TARGET_CATALOG 70%, subsystem “complete for catalog”) all require **legal user BIOS/ISOs** and/or deeper hardware fidelity than stubs.

**Nothing mandatory is left for “closing v2.0 phase numbers.”**  
**Everything below is optional/next product campaign (v3)** if you want real games and production multiplayer.

---

## 2. What still needs implementing (post Phase 50 integrity)

**Living honesty doc**: [COMPLETENESS.md](COMPLETENESS.md). Phases 40–49 = **synthetic campaign scaffolding done**; product quality gaps remain.

### A — Commercial boot & majority (highest product value)
| Gap | Status after 40–49 | Work left |
|-----|-------------------|-----------|
| Dump harness | **Done** (`UserMediaConfig`, CLI) | You supply `user-media.json` |
| Real BIOS/ISO P0+ | **Open** (no dumps in CI) | Run commercial-boot; fix blockers |
| 70% P2 commercial | **Open** (synthetic majority only) | Phase 53–55 |
| IRX / telemetry ISA | Partial HLE | Dump-driven closure |

### B — Accuracy depth
GS formats, VU micro catalog, full VIF unpack modes, EE remainder, real IPU MPEG, softlock timing — **open**, synthetic subset only.

### C — Speed
| Gap | Status |
|-----|--------|
| EE real ALU JIT / S1 ≥10× | **Open** (`HasRealAluEmit=false`) — Phase 51 |
| Native Vulkan/D3D | **Open** — SoftwareUpscale only — Phase 52 |
| Snapshot S3 ≤2 ms full | Partial (FastDelta OK; full host-dependent) |

### D — Audio
| Gap | Status |
|-----|--------|
| Host OS output | **WinMM wired** on Windows (Phase 50); WASAPI optional polish |
| SPU2 depth | Partial (mix/reverb/ADPCM subset) |

### E — Netplay
UDP + frame advantage + synthetic soak **done as tooling**; commercial 10‑min cert **open**.

### F — Product / ops
COMPAT commercial rows, nightly dump CI, installer polish — open.

---

## 3. Recommended next campaign: Phases 40–49 (v3)

Execute in order. Each phase: green Release build + tests + docs. **Commercial phases require you to supply BIOS/ISO paths** (config only; never commit dumps).

### Phase 40 — User dump harness & first commercial boots
**Goal**: Instrument and boot **your** BIOS + 3–5 titles.

1. `UserMediaConfig` (paths only; gitignored).  
2. Catalog runner: boot N minutes → JSON (PC, telemetry top, tier).  
3. Fix top blockers for those titles only.  

**DoD**
- [x] `UserMediaConfig` + `user-media.example.json` + gitignore  
- [x] `CommercialBootRunner` JSON report + synthetic fallback (≥3 P0 / ≥1 P1 without dumps)  
- [x] CLI: `dotnet run --project src/DetPS2.Core -- commercial-boot`  
- [ ] ≥3 **commercial** P0 with user BIOS/ISOs (requires your dumps)  
- [x] Suite green  

**Completed (infra)**: 2026-07-23

### Phase 41 — Boot spine closure (global blockers)
**Goal**: Kill high-frequency unknown opcode/MMIO/syscall/IRX failures across your set.

1. Rank blockers across all boot logs.  
2. EE/IOP/SIF/CDVD/kernel fixes in priority order.  

**DoD**
- [x] `BlockerRanker` + report ingest  
- [x] Boot-spine HLE: GsPutDrawEnv/DisplayEnv, SifLoadModuleBuffer/CheckStat, Deci2, KSeg0, RFU, PREF nop  
- [x] Synthetic **P0+ ≥10** (`CommercialBootRunner` expanded spine)  
- [ ] ≥10 **commercial** P0 on user dump set (needs `user-media.json`)  
- [x] Suite green  

**Completed (synthetic)**: 2026-07-23

### Phase 42 — GS + VU play path
**Goal**: Menus and early gameplay visible/correct for P1 titles.

1. Soft GS gaps from title traces.  
2. VU/VIF Path1 failures.  
3. Goldens where legal (homebrew + synthetic still).  

**DoD**
- [x] GS bilinear texture sample path  
- [x] VIF `V4_32` unpack  
- [x] Homebrew **P2** play path smoke  
- [ ] ≥5 commercial **P1** / ≥1 commercial **P2** (needs dumps)  
- [x] Suite green  

**Completed (synthetic)**: 2026-07-23

### Phase 43 — Audio + input production
**Goal**: Hear and control games properly.

1. Host audio device interface + Desktop pump.  
2. SPU2 reverb depth.  
3. Controller mapping table.  

**DoD**
- [x] `IHostAudioDevice` / `MeterHostAudioDevice` + Desktop pump  
- [x] SPU2 reverb mix  
- [x] `InputMapper` binds (SIO2 pad path already green)  
- [ ] Commercial P2 audio+pad soak (needs dumps)  
- [x] Suite green  

**Completed (synthetic)**: 2026-07-23

### Phase 44 — Real hardware GS (Perf)
**Goal**: Full-speed present path.

1. Vulkan-shaped backend behind `GsCommandBuffer`.  
2. Upscale/filtering; Det still software hash.  

**DoD**
- [x] `VulkanFramePresenter` (software GPU path when no native Vulkan)  
- [x] Bilinear upscale + Det hash unchanged  
- [x] Desktop Present → Vulkan Path menu  
- [ ] Native Silk.NET/Vortice device when available (optional)  
- [x] Suite green  

**Completed (staging)**: 2026-07-23

### Phase 45 — Real EE JIT + S1/S2
**Goal**: Performance gates.

1. IL basic-block JIT with **interp parity tests**.  
2. Measure S1; document speedup.  
3. Snapshot delta tuning toward S3.  

**DoD**
- [x] `EeJit.EmitIl` DynamicMethod trampoline (Det = `Step`)  
- [x] JIT ↔ interp parity green  
- [x] Snapshot `FastDelta` measure path  
- [x] S1 measured in smoke (`Perf_EeJitBenchmark`; host-dependent; IL trampoline not yet ≥10×)  
- [ ] S2 on commercial P2 title (needs dumps + HW GS device)  
- [x] Suite green  

**Completed (foundation)**: 2026-07-23

### Phase 46 — Production rollback netplay
**Goal**: Playable 2P on certified titles.

1. UDP transport + frame advantage.  
2. Desktop netgraph / desync dump.  
3. Certify ≥1 commercial or best homebrew P2 with soak.  

**DoD**
- [x] `UdpNetplayTransport` + in-memory test pair  
- [x] `ProductionRollbackPeer` frame advantage + `NetGraph` + `DesyncDumpWriter`  
- [x] Desktop: UDP host/client, NetGraph, desync dump  
- [x] Synthetic soak certifies `homebrew-gs-demo` (≥100 frames sync)  
- [ ] Commercial 10‑min 2P soak (needs dumps)  
- [x] Suite green  

**Completed (synthetic N3/N4)**: 2026-07-23

### Phase 47 — Majority campaign (your catalog)
**Goal**: ≥70% P2+ of **your** scored TARGET_CATALOG subset (not necessarily all 301 untested rows).

1. Systematic pass; global fixes; TITLE_HACKS only when needed.  
2. DX list live via `DxTracker`.  

**DoD**
- [x] `RunScoredCampaign` + scored majority gate  
- [x] `TitleHackTable` / TITLE_HACKS parse+apply  
- [x] `WriteReportMarkdown` + live `DxTracker.FromCampaign`  
- [x] CLI `majority-campaign`  
- [x] Synthetic majority ≥70%  
- [ ] Full commercial catalog score (needs dumps)  
- [x] Suite green  

**Completed (synthetic)**: 2026-07-23

### Phase 48 — IPU/FMV + multimedia
**Goal**: FMV no longer mass-DX.

1. Expand IPU/MPEG or quality SkipFMV.  
2. Re-score IPU-blocked titles.  

**DoD**
- [x] IPU IQ, MPEG start-code detect, bitstream consume, SkipFMV fast path  
- [x] `IpuFmvPolicy.RescoreIpuBlocked` / `RankIpuDx`  
- [x] IPU not top DX after rescore (smoke)  
- [ ] Full MPEG silicon / commercial FMV (ongoing)  
- [x] Suite green  

**Completed (foundation)**: 2026-07-23

### Phase 49 — v3.0 ship
**Goal**: Production release.

1. Regression suite + commercial smoke checklist.  
2. Netplay-certified list (real titles where legal to document names only).  
3. RELEASE_NOTES 3.0; version tag; publish.  

**DoD**
- [x] `VersionInfo` **3.0.0** Commercial  
- [x] `CommercialSmokeChecklist` (7 required items)  
- [x] Netplay-certified + soak list  
- [x] RELEASE_NOTES / README / publish.ps1 / docs  
- [x] Suite green  

**Completed**: 2026-07-23 — **v3.0.0 shipped** (synthetic commercial campaign complete).

---

## 4. Dependency graph

```
40 Dump harness ──► 41 Boot blockers ──► 42 GS/VU play
                           │                    │
                           ▼                    ▼
                    43 Audio/input      44 HW GS ──► 45 JIT/S2
                           │                    │
                           └────────┬───────────┘
                                    ▼
                             46 Rollback prod
                                    │
                                    ▼
                             47 Majority catalog
                                    │
                                    ▼
                             48 IPU/FMV ──► 49 v3.0 ship
```

**Parallel after 41**: 42 ∥ 43; 44 ∥ 45 after 42 starts.

---

## 5. What you do **not** need to implement (unless you want)

| Item | Reason |
|------|--------|
| More synthetic phase numbers for their own sake | v2.0 foundation closed |
| Committing BIOS/ISOs | Illegal / policy |
| Pixel-perfect PCSX2 parity day one | Multi-year accuracy; track per title |
| Matchmaking/ranked | After N3–N4 only |

---

## 6. Prerequisites for the next plan to work

1. **You supply** a legal PS2 BIOS path and at least a few ISO/ELF paths in a **gitignored** config.  
2. Without dumps, only A-lite (synthetic) and C/D/E **infra** can proceed; commercial DoD cannot close.  
3. Keep determinism laws: `RunFor` only, no host clocks in core, Det for netplay.

---

## 7. Suggested first action after approval

1. Land `COMMERCIAL_PLAN.md` (this document).  
2. Start **Phase 40**: `UserMediaConfig` + boot runner + first real-title telemetry report.  
3. Batch size: continue **5 phases at a time** when you say so (40–44, then 45–49).

---

## 8. Bottom line

| Question | Answer |
|----------|--------|
| Is the **current (parity) plan** complete? | **Yes** for numbered phases / synthetic v2.0. |
| Is there **anything else** that needs implementing? | **Yes**, if the goal is still **real games + production netplay + PCSX2-class speed/feel**. That is a **new campaign (Phases 40–49)**, not unfinished phase paperwork. |
| If you only care about synthetic/homebrew + tooling? | **No further phases required**; optional polish only. |

---

## 9. Approval

Approve to:
1. Write `COMMERCIAL_PLAN.md` into the repo.  
2. Optionally begin **Phase 40** when you provide dump path config (or start Phase 40 harness that no-ops without dumps).
