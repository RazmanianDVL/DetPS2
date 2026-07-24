# DetPS2 Next Plan

**Created**: 2026-07-22  
**Updated**: 2026-07-23  
**Status**: **v1.0 shipped** (Phases 0–20). **v2.0 majority-play plan active** — see **[PARITY_PLAN.md](PARITY_PLAN.md)** (Phases 21–38).

**Rule (unchanged)**: Finish one full phase (definition of done + green tests + docs) before reporting.

**Status**: **v3.1.0 Completeness** — Phases **50–56** done (synthetic gates).  
**Authoritative**: [COMPLETENESS.md](COMPLETENESS.md).  
**Next**: Your dumps via `user-media.json` → real commercial P0/majority/netplay cert.

---

## Progress

| Phase | Status |
|-------|--------|
| 12 EE Kernel & Exceptions | **Complete** |
| 13 SIF RPC & IOP modules | **Complete** |
| 14 Kernel HLE & BIOS path | **Complete** |
| 15 EE/VU/GS accuracy | **Complete** |
| 16 ISO/CDVD/Pad | **Complete** |
| 17 Audio sink + SPU2 | **Complete** |
| 18 Netplay transport + tape UX | **Complete** |
| 19 Hardware / GPU present | **Complete** |
| 20 Compatibility campaign + v1.0 | **Complete** |

---

## Phase 18 — Netplay Transport + Replay UX

**Completed**: 2026-07-23

### Delivered
- `NetplayFrameMsg` fixed 16-byte wire format  
- `INetplayTransport` + `InMemoryNetplayTransport` + `TcpNetplayTransport`  
- `DesyncDetector` (MasterCycles^PC^pad hash)  
- `NetplaySession.ExchangeLockstep` / `AdvanceNetworked`  
- Desktop: Record/Play `.inpr` tape; Netplay Host/Client menus  

### Smoke
- `Netplay_InMemory_LockstepSync`  
- `Netplay_DesyncDetector_FlagsMismatch`  
- `Netplay_FrameMsg_RoundTrip`  
- `InputTape_SerializeDeserialize`  

---

## Phase 19 — Hardware Present Path

**Completed**: 2026-07-23

### Delivered
- `GpuFramePresenter` (texture staging + upload stats)  
- `PresentPipeline.DeterminismMode` always keeps software snapshot for hashes  
- Desktop View → Present Software / GPU  

### Smoke
- `Present_Gpu_UploadsAndDeterminismMode`  
- `Present_HashAlwaysSoftwareGs`  

---

## Phase 20 — Title Compatibility Campaign + v1.0 ship

**Completed**: 2026-07-23

### Delivered
- `TitleFixtures` synthetic campaign (homebrew, ISO boot, multi-dir, replay)  
- EE: MULTU/DIVU correct, DSLL/DSRL/DSRA/DSLL32/…, likely branches (BEQL/…)  
- [RELEASE_NOTES.md](RELEASE_NOTES.md), README v1.0 section, COMPATIBILITY update  

### Smoke
- `Ee_MultuDivu_Dsll`  
- `TitleCampaign_SyntheticPack`  

---

## Commercial Phases 40–49 (done — v3.0)

**Completed**: 2026-07-23

| Phase | Delivered |
|-------|-----------|
| 40–45 | Dump harness, boot spine, play path, audio, Vulkan staging, IL JIT |
| 46 | UDP netplay, frame advantage, netgraph, desync dump, soak cert |
| 47 | Scored majority campaign, TITLE_HACKS, DxTracker reports |
| 48 | IPU IQ/MPEG/SkipFMV, IPU not mass-DX policy |
| 49 | **v3.0.0** checklist, RELEASE_NOTES, publish |

### Completeness campaign (50–56)

| Phase | Focus | Status |
|-------|--------|--------|
| **50** | Integrity: honest labels, Vif1→Vif, WinMM audio | **Done** |
| **51** | Real EE ALU JIT + S1 ≥10× | **Done** |
| **52** | Accelerated parallel present (CPU) | **Done** |
| **53** | Dump boot spine + readiness/discovery | **Done** (infra; commercial open) |
| **54** | Play-path campaign (VIF/GS/pad/audio) | **Done** (synthetic gate) |
| **55** | Majority catalog + DX publish | **Done** (synthetic gate) |
| **56** | Netplay cert runner + **v3.1.0** | **Done** (synthetic cert) |

---

## Post–v1.0 ideas (not blocking)

1. OS audio device on `RingBufferAudioSink`  
2. Real Vulkan/OpenGL upload behind `GpuFramePresenter`  
3. IRX ELF loader + more kernel HLE  
4. Full likely-branch nullify  
5. Expand compatibility matrix with user-run homebrew notes  
