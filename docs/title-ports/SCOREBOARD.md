# Commercial title scoreboard — LIVE queue truth

**Date**: 2026-07-31  
**Seat**: residual agent (Host→Local honesty + Haven poison-`$ra`)  
**Constraints**: `DETPS2_SEMA_STALL_YIELD` **OFF** · Soft-GS metrics = ground truth · no FFmpeg logos  
**Source of truth for this file**: `out/live-queue/done/*.json` + matching `out/live-queue/logs/*-out.txt` claim lines  
**Campaign status (live + traces)**: [MENU_CAMPAIGN_STATUS.md](MENU_CAMPAIGN_STATUS.md)  
**Schema**: [tools/SCOREBOARD_SCHEMA.md](../../tools/SCOREBOARD_SCHEMA.md) · [POST_MENU_PHASE_PLAN.md](../POST_MENU_PHASE_PLAN.md)

> **Honesty bar:** Soft-GS `px>0` and `lit>0` are **not** MENU YES. Heuristic GS? / NEAR? are **not** MENU YES.  
> **Host→Local residual chrome is NOT natural MENU YES** (assist BITBLT of honest disc bytes ≠ EE GIF IMAGE).  
> **No MENU YES is asserted from live-queue results** in this residual seat. Interactive requires pad-script evidence.

## LIVE queue titles (best Soft-GS per title)

Jobs finished under `out/live-queue/done/` (2026-07-31). Prefer best budget per title.

| Title | Serial | Soft-GS lit | dispfb | interactive? | Residual class | Key live metric (job) |
|-------|--------|-------------|--------|--------------|----------------|------------------------|
| **MK Deception** | SLUS_208.81 | **YES** lit=198858/286720 | **natural** nat=231587 residual=0 | **?** | IMAGE path strong; PC residual | px=2129315 prims=86 imgBytes=557056 (`verify-dec-100m`) |
| **Vexx** | SLUS_203.83 | **YES** lit=6405/286720 (sparse) | **natural** nat=6534 residual=0 | **?** | Sparse lit; unknown SIF | px=883720 prims=26 (`client-a-vexx-40m`) |
| **MK Deadly Alliance** | SLUS_204.23 | **YES** lit=32768/286720 | **natural** nat=32768 residual=0 | **?** | Thin prims=3 chrome | px=462848 (`client-b-da-50m`) |
| **MK Shaolin Monks** | SLUS_210.87 | **YES** lit=237568 @**100M** · **NO** @50M | **100M natural** nat=94208 | **?** | **MENU-SM-4:** fleet 50M pre-spine expected black; claim ≥100M | px=1171968 @100M (`sm-menu-100m`) |
| **Haven** | SLUS_205.17 | **YES** lit=43132 @**100M** · **NO** @50M | **natural present** nat=43132 (Host→Local IMAGE) | **?** | **Host→Local residual** SYSTEM.RW3/CUBE.BIN; **fleet 50M CRT0 pre-decompress px=0 expected** (claim ≥100M) | px=329852 imgBytes=194560 (`haven-poisonra-100m-20260731-151114`; `haven-fleet50m-budget` px=0) |
| **God of War** | SCUS_973.99 | **YES** lit=60866 | **residual** residualDispfb=60866 natural=0 src=**Frame** | **?** | **Host→Local residual** R_SHELL/TIT1 + expand Path2 strips — **not natural DISPFB** | px=634306 prims=3 imgBytes=262144 expandHits=2 (`gow-residual-100m-20260731-151114`) |
| **Whiplash** | SLUS_206.84 | **YES** lit=5189 (sparse) | **natural** nat=36933 | **?** | **Host→Local residual** GOE firstscreen 256KiB; gif image=0 | px=610373 prims=4 imgBytes=262144 (`whip-residual-100m-20260731-151114`) |
| **Blood Omen 2** | SLUS_200.24 | **YES** lit=85996 | **natural** nat=85996 | **?** | **Host→Local residual** MAINMENU/MAINSKY; prims=2 expandHits=1 | px=372716 imgBytes=392192 (`bo2-residual-100m-20260731-151114`) |
| **SotC** | SCUS_974.72 | **YES** lit=120153 | **natural** nat=120153 | **?** | **Host→Local residual** MANAGER/NICO/KERNEL; gif image=0; KERNEL thrash residual | px=2127193 prims=8 imgBytes=524288 (`sotc-residual-100m-20260731-151114`) |
| **Burnout 3** | SLUS_210.50 | **YES** lit=100106 | **mixed** nat=94208 residual=1188242 src=Frame | **?** PARTIAL on pad jobs | logo-frontend Soft-GS; residual DISPFB heavy; free-ride re-verify | px=5997653 prims=992 imgBytes=525824 (`b3-residual-100m-20260731-151114`) |

### Residual re-verify inventory (2026-07-31 residual seat)

| Job id | Title | cycles | ok | px | lit | prims | residual class |
|--------|-------|--------|----|----|-----|-------|----------------|
| `haven-fleet50m-budget-20260731-151114` | Haven | 50M | true | **0** | **0** | 0 | CRT0 pre-spine (expected) |
| `haven-poisonra-100m-20260731-151114` | Haven | 100M | true | 329852 | **43132** | 2 | Host→Local (imgBytes=194560) |
| `gow-residual-100m-20260731-151114` | GoW | 100M | true | 634306 | **60866** | 3 | Host→Local + residual DISPFB |
| `whip-residual-100m-20260731-151114` | Whiplash | 100M | true | 610373 | **5189** | 4 | Host→Local firstscreen |
| `bo2-residual-100m-20260731-151114` | BO2 | 100M | true | 372716 | **85996** | 2 | Host→Local MAINMENU |
| `sotc-residual-100m-20260731-151114` | SotC | 100M | true | 2127193 | **120153** | 8 | Host→Local MANAGER/NICO |
| `b3-residual-100m-20260731-151114` | Burnout 3 | 100M | true | 5997653 | **100106** | 992 | logo-frontend (mixed DISPFB) |

## MENU YES ledger (live-queue — residual seat)

| Title | MENU YES? | Why |
|-------|-----------|-----|
| MK Deception | **not asserted** | Strong Soft-GS; formal midway-menu + interactive residual |
| Vexx | **not asserted** | Sparse lit title-surface |
| MK Deadly Alliance | **not asserted** | Thin chrome; no interactive |
| MK Shaolin Monks | **not asserted** | lit YES @100M; formal menuKind residual |
| Haven | **not asserted** | Host→Local residual chrome @100M; 50M CRT0 black |
| God of War | **not asserted** | Host→Local residual + expand; residual DISPFB only |
| Whiplash | **not asserted** | Host→Local firstscreen residual |
| Blood Omen 2 | **not asserted** | Host→Local MAINMENU residual |
| SotC | **not asserted** | Host→Local residual; not natural EE IMAGE |
| Burnout 3 | **not asserted** here | logo-frontend Soft-GS lit; charter MENU is separate seat claim |
| Fleet 9/9 MENU YES | **false / stale** | Host→Local residual ≠ natural MENU YES |

## Claim excerpts (residual seat 151114)

```text
# Haven 50M (CRT0 budget — expected black)
claim: px=0 prims=0 … lit=0/286720  PC=0x01000450

# Haven 100M (Host→Local residual)
claim: px=329852 prims=2 gifP2=65 gifP3=68 imgBytes=194560 dispfbPx=43132 naturalDispfbPx=43132 residualDispfbPx=0 lit=43132/286720
softgs: compositeSource=NaturalDispfb  # present path; IMAGE is Host→Local plant

# GoW 100M (Host→Local residual DISPFB)
claim: px=634306 prims=3 gifP2=31 gifP3=0 imgBytes=262144 dispfbPx=60866 naturalDispfbPx=0 residualDispfbPx=60866 expandHits=2 lit=60866/286720
softgs: compositeSource=Frame

# Whiplash 100M
claim: px=610373 prims=4 imgBytes=262144 dispfbPx=36933 naturalDispfbPx=36933 lit=5189/286720

# BO2 100M
claim: px=372716 prims=2 imgBytes=392192 dispfbPx=85996 naturalDispfbPx=85996 lit=85996/286720

# SotC 100M
claim: px=2127193 prims=8 imgBytes=524288 dispfbPx=120153 naturalDispfbPx=120153 lit=120153/286720

# Burnout 3 100M free-ride
claim: px=5997653 prims=992 imgBytes=525824 dispfbPx=1282450 naturalDispfbPx=94208 residualDispfbPx=1188242 lit=100106/286720
```

## Reproduce / enqueue

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
# Prefer live-game-queue over free-for-all blocker-trace
# See docs/LIVE_GAME_QUEUE.md — write inbox/*.json, drain done/

# Offline fleet heuristics only (does NOT write MENU YES):
pwsh ./tools/scoreboard.ps1 -Budget diagnose
```

**Core this seat:** `TeamIcoAssist` MENU-HAVEN-4 poison-`$ra` repair (natural jr-return spine after bad-PC escape).  
**Wiki**: [Commercial-Titles](https://github.com/RazmanianDVL/DetPS2/wiki/Commercial-Titles) (update only after re-verify)
