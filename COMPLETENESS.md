# DetPS2 Completeness Status

**Product**: **v0.1.0 Foundation** — engineering phases **0–56** synthetic; **commercial Soft-GS
MENU YES 9/9** (2026-07-31, SEMA_OFF). **Not** fully playable / not interactive fleet-wide.
`v1.0.0` reserved for ≥10% catalog fully playable — see `src/DetPS2.Core/VersionInfo.cs`.  
**Smoke**: `dotnet run --project Tests -c Release`  
**Scoreboard**: [docs/title-ports/SCOREBOARD.md](docs/title-ports/SCOREBOARD.md)  
**Play guide**: [PLAY.md](PLAY.md) · `pwsh ./launch.ps1`

This file is the **single source of truth** for what is complete vs open.

### L0 workflow (BIOS + media folder) — **Done**
- Desktop media library panel; choose folder, rescan, list ISO/ELF  
- Persist `GamesFolder`, `BiosPath`, game list in `%LocalAppData%\DetPS2\config.json`  
- Boot selected / double-click; File → Load ISO; Load ELF uses `Ps2System.LoadElf`  
- Memcard path defaults to `{GamesFolder}\memcards\` (usage later)

### L1 commercial Soft-GS menus — **MENU YES 9/9** (2026-07-31)

Nine operator fleet titles reach Soft-GS **menuKind** surfaces (logo / title / midway keep-alive).
Truth = Soft-GS metrics only (no FFmpeg). Residual: pad INTERACTIVE, natural DISPFB/IMAGE/tex,
GameQuirk debt, IRX FILEIO purity. Plans: [NEXT_PLAN.md](NEXT_PLAN.md),
[docs/POST_MENU_PHASE_PLAN.md](docs/POST_MENU_PHASE_PLAN.md),
[docs/GRAPHICS_PIPELINE_PHASE_PLAN.md](docs/GRAPHICS_PIPELINE_PHASE_PLAN.md). Epic **#12**.

### L2 first gameplay / interactive — **In progress**

Season S1 INTERACTIVE + G1 Soft-GS path fidelity (10 concurrent seats).

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

## CLI

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
| 56 Netplay cert | Done (synthetic cert) |
