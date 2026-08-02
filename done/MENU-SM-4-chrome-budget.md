# MENU-SM-4 — Soft-GS natural chrome budget (SLUS_210.87)

**Date**: 2026-07-31  
**Seat**: MENU-SM-4 · MidwayBootAssist natural path  
**Policy**: Soft-GS truth · SEMA_OFF · no invent DISPFB · PATH3 gap-fill default OFF  

## Mandate

`fleet-50m-mk` had **lit=0** at 50M while earlier **100M** had lit high. Ensure Soft-GS natural chrome by 50M **or** document need 100M. Keep natural path. Enqueue 50M + 100M.

## Verdict

**Document need 100M** — Soft-GS NaturalDispfb chrome is **not** honest at fleet 50M.

| Budget | lit | gifP3 | naturalDispfbPx | compositeSource | Evidence |
|--------|----:|------:|----------------:|-----------------|----------|
| 40M | 0 | 5 | 0 | None | `client-b-sm-40m` |
| **50M** | **0** | 5 | 0 | None | **`fleet-50m-mk`** |
| **100M** | **237568** | **10** | **94208** | **NaturalDispfb** | **`sm-menu-100m`** |
| 200M | 237568 | 10 | 94208 | NaturalDispfb | `sm-menu-200m` hold |

**fleet-50m-mk claim:** `px=286720 prims=1 gifP3=5 lit=0/286720` · DISPFB2 FBP=`0x8C000` empty RGB · mostlyBlack=1  
**sm-menu-100m claim:** `px=1171968 prims=6 gifP2=1 gifP3=10 naturalDispfbPx=94208 lit=237568/286720`

## Why not 50M

1. Logo-spine natural main kick: early **48M** (MENU-SM-4) / primary **58M** — 50M is pre/just-into spine.
2. Natural timeline needs spine → group-6/frame-cb → gifP3≈10 → DISPFB FBP=0 composite → lit≈237k (**~100M**).
3. Invent DISPFB / PATH3 gap-fill to fake lit@50M **rejected** (MENU-SM-3: assist PATH3 zeroed NaturalDispfb).

## MidwayBootAssist (TITLE_LOCAL)

| Constant / knob | Value |
|-----------------|-------|
| `SoftGsNaturalChromeClaimCycles` | 100_000_000 |
| `SoftGsLogoSpineEarlyKickCycles` | 48_000_000 |
| PATH3 gap-fill / D770 invent | OFF (opt-in `DETPS2_SM_PATH3_GAPFILL=1`) |

## Enqueued jobs

| id | cycles | expected |
|----|-------:|----------|
| `sm-sm4-50m` | 50M | lit=0 class (diagnose) |
| `sm-sm4-100m` | 100M | lit>1000 NaturalDispfb re-claim |

## Docs

- `docs/title-ports/MK_SHAOLIN_MONKS.md` — MENU-SM-4 section  
- `docs/title-ports/SCOREBOARD.md` / `MENU_CAMPAIGN_STATUS.md` — fleet 50M vs 100M  
