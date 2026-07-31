# DetPS2 Next Plan

**Updated**: 2026-07-30  
**Status**: **v0.1.0 Foundation** + **IRX-first pivot**

## Current focus (authoritative)

**Literal BIOS / disc IRX execution** — primary path to commercial playability.

Full phase plan:

### → [`docs/IRX_EXECUTION_PHASE_PLAN.md`](docs/IRX_EXECUTION_PHASE_PLAN.md)

| Phase | Goal |
|-------|------|
| 0 | Hygiene + freeze title plants |
| 1 | IOP **executes** loaded IRX (today: load/reloc only) |
| 2 | Real IOPBTCONF chain from operator BIOS |
| 3 | Disc IOPRP + FILEIO/PAD via real modules |
| 4 | **First commercial playable Soft-GS surface** |
| 5 | Demote RealSifRpc / GameQuirk debt |
| 6 | Perf / netplay polish |

## Explicitly demoted

- HLE-first multi-title plant waves as the strategy for menus  
- Treating BIOS G0 HLE completeness as “games ready”  
- Host media cheats (FFmpeg logos — already removed)

## Still true

- Determinism + C# core (EE, Soft-GS, devices, scheduler)  
- Operator-provided BIOS/ISO only (never commit dumps)  
- Soft-GS metrics = presentation ground truth  
- PCSX2+PINE for EE oracle when stuck  

## Historical

Engineering phases 0–56 and commercial HLE campaign history: [ROADMAP.md](ROADMAP.md), [COMPLETENESS.md](COMPLETENESS.md), [docs/DEVELOPER_GUIDE.md](docs/DEVELOPER_GUIDE.md).
