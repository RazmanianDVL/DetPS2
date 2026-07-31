# DetPS2 Next Plan

**Updated**: 2026-07-31  
**Status**: **v0.1.0** · **MENU YES 9/9** · **10-subagent** playability **+ graphics pipeline**

## Current focus (authoritative)

### Triple stack (run in parallel)

```text
STACK GFX   → honest Soft-GS pipeline (leave menus, discover real walls)
STACK PLAY  → INTERACTIVE → FRONTEND → GAMEPLAY → free-ride
STACK IRX   → FILEIO/PAD via executing IRX (S7 couple)
```

---

### 1. Graphics pipeline (required past MENU)

### → [`docs/GRAPHICS_PIPELINE_PHASE_PLAN.md`](docs/GRAPHICS_PIPELINE_PHASE_PLAN.md)

| Item | Scale |
|------|-------|
| Work packages | **WP-GX-000 … WP-GX-079** (80) |
| Gates | **G-GFX-0 … G-GFX-9** |
| Seasons | **G0–G6** interleaved with play S0–S9 |
| Permanent seats | **S8 PATH · S9 RASTER · S10 DISPLAY** |

**Why:** Menu YES often uses expand strips / assist PATH3 / composite-only. Pad without real Path/IMAGE/TEX/DISPFB **hides** post-menu bugs.

| Gate | Meaning |
|------|---------|
| G-GFX-1/2 | Path1/2/3 + register fidelity |
| G-GFX-3/4 | IMAGE + texture sample (PSM/CLUT) |
| G-GFX-5/6 | Natural DISPFB + demote ofx expand |
| G-GFX-7/8 | Natural Path3 + Path1/VU1 |
| G-GFX-9 | Textured gameplay Soft-GS ≥3 titles |

---

### 2. Post–MENU playability (10 seats)

### → [`docs/POST_MENU_PHASE_PLAN.md`](docs/POST_MENU_PHASE_PLAN.md)

| Item | Scale |
|------|-------|
| Work packages | **WP-PL-000 … WP-PL-099** (100) |
| Gates | **P0–P12** (P0 MENU done) |
| Seasons | **S0–S9** |
| Agents | **T0 + S1–S10 always** |

| Seat | Role |
|------|------|
| S1–S3 | Midway SM / Dec / DA |
| S4–S6 | B3 / BO2 / GoW |
| S7 | Vexx + Whiplash queue |
| **S8–S10** | **Graphics triad** (not optional platform) |

---

### 3. IRX core (ongoing)

### → [`docs/IRX_EXECUTION_PHASE_PLAN.md`](docs/IRX_EXECUTION_PHASE_PLAN.md)

Coupled in play season **S7**. Do not block S1–S6 or G0–G3 on full IRX purity.

---

## Explicitly demoted

- Treating MENU YES as playable / “graphics done”  
- Stalling S8–S10 on title assists  
- ofx expand / assist PATH3 as permanent strategy  
- FFmpeg logos · global WaitSema fabricate · StartThread `$ra` resume  

## Still true

Soft-GS ground truth · SEMA_OFF claims · operator BIOS/ISO only · isolated worktrees · T0 merge/smoke/push/#12 · Play!/PINE oracle  

## Scoreboard

**MENU YES 9/9** — [`docs/title-ports/SCOREBOARD.md`](docs/title-ports/SCOREBOARD.md)  
Next bars: INTERACTIVE + **GFX path/tex/present/natural** columns (post-menu §4).

## First 72 hours

1. Bootstrap 10 seat worktrees  
2. S8–S10: GX telemetry + Path2 harden + expand policy  
3. S1–S7: residual + draw-graph charters  
4. Then INTERACTIVE pad **in parallel** with G1 path fidelity  

## Historical

[ROADMAP.md](ROADMAP.md) Phase 7 Soft-GS baseline · [COMPLETENESS.md](COMPLETENESS.md) · [docs/DEVELOPER_GUIDE.md](docs/DEVELOPER_GUIDE.md)
