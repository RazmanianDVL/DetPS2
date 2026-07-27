# DetPS2 Completeness Status

**Product**: **v3.1.0 Completeness** — Phases **0–56** + **MP1 media library UI**  
**Smoke**: `dotnet run --project Tests -c Release`  
**Checklist**: `commercial-checklist` → 11/11  
**Play guide**: [PLAY.md](PLAY.md) · `pwsh ./launch.ps1`

This file is the **single source of truth** for what is complete vs open.

### L0 workflow (BIOS + media folder) — **Done**
- Desktop media library panel; choose folder, rescan, list ISO/ELF  
- Persist `GamesFolder`, `BiosPath`, game list in `%LocalAppData%\DetPS2\config.json`  
- Boot selected / double-click; File → Load ISO; Load ELF uses `Ps2System.LoadElf`  
- Memcard path defaults to `{GamesFolder}\memcards\` (usage later)

### L1/L2 commercial play — **In progress** (real bring-up active, not just tooling)

Using a real BIOS + Mortal Kombat: Shaolin Monks (`SLUS_210.87`) as the case study: boots past the
logo into real gameplay/menu-adjacent code (hundreds of millions of cycles of genuine SIF activity),
currently blocked on a specific runtime-library registry-lookup bug (traced to instruction level —
see `docs/DEVELOPER_GUIDE.md`'s dated entries). Every blocker fixed this way so far has been a
general emulation/HLE bug, not a title-specific one, matching the project's standing hypothesis that
this work has broad value across the library. Not yet at a general "majority" gate — this is one
title's boot path, not a catalog pass.

---

## Complete (synthetic / infrastructure)

| Area | Notes |
|------|--------|
| Determinism / MasterCycles / RunFor | Architecture freeze |
| Full smoke suite | Release gate |
| EE interpreter + real ALU JIT | S1 synthetic met |
| Software GS + AcceleratedParallel present | Det hash = soft GS |
| VIF (V4_32 + other unpack modes) | Phase 54 |
| Dump boot spine infrastructure | Phase 53 — ready when dumps appear |
| Play-path campaign | ≥5 P1 + ≥1 P2 synthetic (gate met) |
| Majority catalog tooling | ≥70% synthetic scored gate |
| Netplay certification runner | ≥1 synthetic certified soak |
| WinMM host audio (Windows) | Real OS output |
| Desktop + publish script | Avalonia shell |
| Virtual HDD (APA + PFS) | Real on-disk format, unit-tested; not yet wired to game-facing I/O |
| `pad-inject` CLI | Scripted controller-input testing against a running boot |

---

## Still open (needs your dumps or deeper silicon)

| Area | Status |
|------|--------|
| **Commercial BIOS/ISO P0+** | In progress against a real dump (see L1/L2 above); not yet closed |
| **Commercial majority P2%** | Tooling done; results need real titles |
| **Native Vulkan/D3D device** | CPU AcceleratedParallel only |
| **Full MPEG IPU** | Stub + SkipFMV |
| **Catalog-complete GS/VU/IRX** | Subset only |
| **Commercial 10‑min 2P netplay cert** | Synthetic cert done; real titles pending |

---

## CLI (v3.1)

```bash
dotnet run --project src/DetPS2.Core -c Release -- dump-spine
dotnet run --project src/DetPS2.Core -c Release -- play-path
dotnet run --project src/DetPS2.Core -c Release -- majority-catalog
dotnet run --project src/DetPS2.Core -c Release -- netplay-cert 600
dotnet run --project src/DetPS2.Core -c Release -- commercial-checklist
dotnet run --project Tests -c Release
```

Copy `user-media.example.json` → `user-media.json` (gitignored) for dumps.

---

## Perf gates

| ID | Status |
|----|--------|
| S1 EE JIT ≥10× | **Met** (synthetic) |
| S2 full-speed titles | Open (dumps) |
| S3 snapshot ≤2 ms | Partial (FastDelta) |

---

## Phase map (50–56)

| Phase | Status |
|-------|--------|
| 50 Integrity | Done |
| 51 Real EE ALU JIT + S1 | Done |
| 52 Accelerated present | Done |
| 53 Dump boot spine | Done (infra); commercial P0 open |
| 54 Play-path campaign | Done (synthetic gate) |
| 55 Majority catalog | Done (synthetic gate) |
| 56 Netplay cert + v3.1 | Done (synthetic cert) |
