# DetPS2Sharp v3.1.0 — Release Notes

**Date**: 2026-07-24  
**Codename**: Completeness  
**Baseline**: Phases **0–56**

---

## What v3.1 is

v3.0 foundation **plus** the completeness campaign (Phases 50–56):

| Phase | Delivered |
|-------|-----------|
| 50 | Integrity pass — honest labels, Vif1→Vif, WinMM audio |
| 51 | Real EE ALU JIT; **S1 ≥10×** on synthetic self-loop |
| 52 | `AcceleratedFramePresenter` (parallel CPU upscale) |
| 53 | `DumpBootSpine` — media discovery, readiness, blocker rank |
| 54 | `PlayPathCampaign` — VIF unpack modes, GS/pad/audio/VU pack |
| 55 | `MajorityCatalog` — scored majority + DX publish |
| 56 | `NetplayCertification` — multi-title synthetic cert |

**Checklist**: 11/11 required items green without dumps.

---

## Honest limits

| Claim | Status |
|-------|--------|
| Commercial games majority | **Needs your BIOS/ISOs** (`user-media.json`) |
| Native Vulkan | **Not wired** — use AcceleratedParallel |
| Full MPEG IPU | Stub + SkipFMV |
| Commercial netplay 10‑min | Synthetic cert only |

See [COMPLETENESS.md](COMPLETENESS.md).

---

## CLI

```bash
dotnet run --project src/DetPS2.Core -c Release -- dump-spine
dotnet run --project src/DetPS2.Core -c Release -- play-path
dotnet run --project src/DetPS2.Core -c Release -- majority-catalog
dotnet run --project src/DetPS2.Core -c Release -- netplay-cert 600
dotnet run --project src/DetPS2.Core -c Release -- commercial-checklist
dotnet run --project Tests -c Release
pwsh ./publish.ps1
```

---

## Legal

Provide your own BIOS and game ISOs. DetPS2 does not ship copyrighted dumps.
