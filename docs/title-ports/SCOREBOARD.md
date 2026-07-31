# Commercial title scoreboard — main menu campaign

**Date**: 2026-07-31  
**Tip**: **`7fded23`** on `main` (S0/G0 merge train; MENU YES 9/9 Soft-GS)  
**Constraints**: `DETPS2_SEMA_STALL_YIELD` **OFF** · Soft-GS metrics = ground truth · no FFmpeg logos  
**Active campaign**: [POST_MENU_PHASE_PLAN.md](../POST_MENU_PHASE_PLAN.md) + [GRAPHICS_PIPELINE_PHASE_PLAN.md](../GRAPHICS_PIPELINE_PHASE_PLAN.md) · epic [#12](https://github.com/RazmanianDVL/DetPS2/issues/12)  
**Wiki**: [Commercial-Titles](https://github.com/RazmanianDVL/DetPS2/wiki/Commercial-Titles)

| Title | Serial | Menu? | Key tip metric | Wall |
|-------|--------|-------|----------------|------|
| **MK Shaolin Monks** | SLUS_210.87 | **YES** (mk-mainmenu) | gifP3=18 px=966656 prims=9 | wave-7 WAD body + C1C0 + second chrome |
| **Burnout 3** | SLUS_210.50 | **YES** (logo-frontend) | px=24.4M dispfbPx=2.27M cdvd=6584 gifP2=14526 | wave-6 Soft-GS logo chrome; pad main-menu advance residual |
| **Blood Omen 2** | SLUS_200.24 | **YES** (mainmenu title-surface) | px=286720 gifP2=106 stream=2.4MB cdvd=6512 | wave-7 dual list-stub + ofx title FB |
| **God of War** | SCUS_973.99 | **YES** (first-gs Soft-GS) | px=573440 prims=2 FRAME set gifP2=19 | wave-12b ofx=0 title-strip expand |
| **MK Deception** | SLUS_208.81 | **YES** (midway-menu) | px≈22M p2qws=5988 gifP3=6 imgBytes=98304 gameart=2.8MB | S0 walls: INTERACTIVE idle-pad + IMAGE/G-GFX-3 |
| **MK Deadly Alliance** | SLUS_204.23 | **YES** (midway-menu) | px=716800 gifP2=35109 exitReq=False | wave-6 post-logo keep-alive |
| **Vexx** | SLUS_203.83 | **YES** (title-surface) | px=877186 prims=24 gifP2=12 img=5120 cdvd=318 members=17 | S7 seat-s7 claim100; TRE VFS residual ([VEXX.md](VEXX.md)) |
| **Whiplash** | SLUS_206.84 | **YES** (title-surface) | px=286720 prims=1 ofx=0x8000 cdvd=1904 gifP3=2 | S7 seat-s7 claim100; ring+ofx residual ([WHIPLASH.md](WHIPLASH.md)) |
| **Haven** | SLUS_205.17 | **YES** (title-surface) | px=286720 gifP3=68 cdvd=7004 TITLES_VAG | wave-6 JREXIT/SP frame + NUSOUND; IMAGE residual |

\*GoW gate is first real GS + pad-interactive, not MK MAINMENU.

> **MENU YES 9/9** Soft-GS (SEMA_OFF). **Next bars:** INTERACTIVE (P1) + graphics G-GFX-1…9 (path/tex/DISPFB). S0 charters + expandHits/gif metrics landed; S1 pad wave in flight.

## 9-title wave commits (`32b4e62..f807ab1`)

| SHA | Title |
|-----|-------|
| `6579910` | DA MFL gameart open |
| `c3f1800` / `87d6353` | SM slot plant reject |
| `a8956ef` | Whiplash FlushCache + RKV |
| `9cd5d44` | Vexx SearchFile first game-data |
| `1f81af7` | B3 PATH3 + FRONTEND plant |
| `fc3c790` | BO2 format wrapper |
| `499ee3e` | GoW table-index + gifP3=1 |
| `f778232` | Dec post-MSL list/exception |
| `f807ab1` | Haven soft-float residual docs |

## Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
pwsh ./tools/scoreboard.ps1 -Budget diagnose
```
