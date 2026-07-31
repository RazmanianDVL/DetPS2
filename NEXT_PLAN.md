# DetPS2 Next Plan

**Updated**: 2026-07-30  
**Status**: **v0.1.0 Foundation** + **IRX-first pivot**

## Current focus (authoritative)

**Literal BIOS / disc IRX execution** — primary path to commercial playability.

Full phase plan (**50 work packages WP-00…WP-49**, **10 agent tracks**):

### → [`docs/IRX_EXECUTION_PHASE_PLAN.md`](docs/IRX_EXECUTION_PHASE_PLAN.md)

| Block | WP range | Goal |
|-------|----------|------|
| A | 00–04 | Hygiene + freeze + HLE→IRX matrix |
| B | 05–14 | **IOP executes IRX** (critical path — start here) |
| C | 15–24 | BIOS IOPBTCONF chain executes |
| D | 25–34 | Disc IOPRP + FILEIO/PAD via IRX |
| E | 35–41 | **First commercial Soft-GS playable surface** |
| F | 42–45 | Second title free-ride |
| G | 46–49 | Demolish GameQuirk / soft-success debt |

**Orchestrator:** max 10 parallel agents by track ownership; no HLE plant waves.

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
