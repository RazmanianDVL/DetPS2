# Menu campaign status — honest Soft-GS table

**Date**: 2026-07-31  
**Seat**: residual agent (Host→Local honesty)  
**Policy**: Soft-GS only · SEMA_OFF · heuristics ≠ MENU YES · **Host→Local residual ≠ natural MENU YES** · pad-script required for interactive claims  
**Primary sources**:
1. **LIVE** — `out/live-queue/done/*.json` + `out/live-queue/logs/*-out.txt`
2. **TRACES** — `out/traces/*` (legacy morning scoreboard stamp)

> This document **does not invent MENU YES**. Columns report measured Soft-GS surface quality only.  
> Fleet “MENU YES 9/9” language in older docs is **stale**. Residual Host→Local chrome is **not** natural MENU YES.

---

## 1. LIVE queue snapshot (authoritative)

| Title | Serial | Live jobs (best) | Soft-GS lit | dispfb | interactive? | residual class | Best claim |
|-------|--------|------------------|-------------|--------|--------------|----------------|------------|
| MK Deception | SLUS_208.81 | `verify-dec-100m` | **YES** 198858 | **natural** 231587 | **?** | PC residual | px=2.13M prims=86 img=557056 |
| Vexx | SLUS_203.83 | `client-a-vexx-40m` | **YES** 6405 | **natural** 6534 | **?** | Sparse lit | px=883720 prims=26 |
| MK Deadly Alliance | SLUS_204.23 | `client-b-da-50m` | **YES** 32768 | **natural** 32768 | **?** | Thin prims | px=462848 |
| MK Shaolin Monks | SLUS_210.87 | `sm-menu-100m` / fleet-50m | **50M NO** · **100M YES** 237568 | 50M none · **100M natural** | **?** | 50M pre-spine expected black | @100M px=1.17M |
| Haven | SLUS_205.17 | `haven-poisonra-100m` / `haven-fleet50m-budget` | **50M NO** · **100M YES** 43132 | **natural present** (Host→Local IMAGE) | **?** | **Host→Local residual**; 50M CRT0 px=0 | @100M px=329852 img=194560 |
| God of War | SCUS_973.99 | `gow-residual-100m` | **YES** 60866 | **residual** residual=60866 natural=0 src=Frame | **?** | **Host→Local residual** + expand | px=634306 img=262144 expandHits=2 |
| Whiplash | SLUS_206.84 | `whip-residual-100m` | **YES** 5189 | **natural** 36933 | **?** | **Host→Local residual** firstscreen | px=610373 img=262144 |
| Blood Omen 2 | SLUS_200.24 | `bo2-residual-100m` | **YES** 85996 | **natural** 85996 | **?** | **Host→Local residual** MAINMENU | px=372716 img=392192 |
| SotC | SCUS_974.72 | `sotc-residual-100m` | **YES** 120153 | **natural** 120153 | **?** | **Host→Local residual** MANAGER/NICO | px=2127193 img=524288 |
| Burnout 3 | SLUS_210.50 | `b3-residual-100m` | **YES** 100106 | **mixed** nat=94208 residual heavy | **?** | logo-frontend Soft-GS | px=5.99M prims=992 |

**Live summary:** 10/10 fleet titles sampled · 9 lit @ claim budget · **0 formal MENU YES** from residual Host→Local honesty · fleet 50M black expected for Haven + SM.

---

## 2. Residual class definitions

| Class | Meaning |
|-------|---------|
| **natural DISPFB** | `naturalDispfbPx>0` and composite from retail DISPFB/DISPLAY circuit |
| **residual DISPFB** | `residualDispfbPx>0` and natural=0 (Frame / plant composite) |
| **Host→Local residual** | assist BITBLT of honest disc/EE plant bytes into Soft-GS IMAGE (`imgBytes>0` without EE GIF image tags) — **not** natural MENU YES |
| **CRT0 / pre-spine** | budget too short for decompress/logo spine (Haven 50M, SM 50M) — expected px/lit=0 |
| **expand residual** | title-strip ofx expand carries Soft-GS FB (GoW Path2 strips) |

---

## 3. Campaign counters (honest)

| Counter | Value | Basis |
|---------|------:|-------|
| Live titles sampled | **10** | residual seat + prior MK/Vexx |
| Live Soft-GS lit @ claim budget | **9** | all except Haven/SM at 50M-only windows |
| Formal MENU YES (this residual seat) | **0** | Host→Local residual honesty |
| Host→Local residual titles | **6** | Haven, GoW, Whip, BO2, SotC (+ Dec gameart is separate path) |
| Fleet MENU YES 9/9 | **false / stale** | superseded |

---

## 4. Core this seat

| Change | File | Effect |
|--------|------|--------|
| MENU-HAVEN-4 poison-`$ra` repair | `GameQuirks/TeamIcoAssist.cs` | When PC is healthy .text and `$ra` is poison (0/1/non-code), seed safe link so natural `jr ra` spine can leave post-escape park. Does **not** invent chrome. |
| Media path fix | `user-media-godwar-burnout.json` | Correct GoW/B3 ISO filenames so free-ride dual-media jobs boot |

**Haven 50M:** still CRT0 at `PC=0x01000450` px=0 — **expected** (decompress ~80–85M). Claim budget **≥100M**.

**GoW:** lit=60866 is Host→Local residual DISPFB (`compositeSource=Frame`, naturalDispfbPx=0) — **not** natural MENU YES.

---

## 5. Related docs

| Doc | Role |
|-----|------|
| [SCOREBOARD.md](SCOREBOARD.md) | LIVE-queue title marks |
| [GOD_OF_WAR.md](GOD_OF_WAR.md) | GoW residual Host→Local charter |
| [WHIPLASH.md](WHIPLASH.md) | Whip Host→Local residual |
| [SHADOW_OF_THE_COLOSSUS.md](SHADOW_OF_THE_COLOSSUS.md) | SotC Host→Local residual |
| [LIVE_GAME_QUEUE.md](../LIVE_GAME_QUEUE.md) | Queue protocol |
| [CORRECTNESS.md](../CORRECTNESS.md) | No pretty lies |
