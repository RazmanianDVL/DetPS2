# Commercial title scoreboard — main menu campaign

**Date**: 2026-07-30 (9-title concurrent wave)  
**Branch / PR**: [`campaign/issue-knockout` #28](https://github.com/RazmanianDVL/DetPS2/pull/28) tip **`f807ab1`**  
**Constraints**: `DETPS2_SEMA_STALL_YIELD` **OFF** · Soft-GS metrics = ground truth · no iGPU  
**Smokes**: ALL PASSED after integrate  
**Wiki**: [Commercial-Titles](https://github.com/RazmanianDVL/DetPS2/wiki/Commercial-Titles)

| Title | Serial | Menu? | Key tip metric | Wall |
|-------|--------|-------|----------------|------|
| **MK Shaolin Monks** | SLUS_210.87 | **YES** (mk-mainmenu) | gifP3=18 px=966656 prims=9 | wave-7 WAD body + C1C0 + second chrome |
| **Burnout 3** | SLUS_210.50 | **YES** (logo-frontend) | px=24.4M dispfbPx=2.27M cdvd=6584 gifP2=14526 | wave-6 Soft-GS logo chrome; pad main-menu advance residual |
| **Blood Omen 2** | SLUS_200.24 | **YES** (mainmenu title-surface) | px=286720 gifP2=106 stream=2.4MB cdvd=6512 | wave-7 dual list-stub + ofx title FB |
| **God of War** | SCUS_973.99 | **No** | cdvd=555 PART1/TOC FILEIO gifP2=962 px=0 | VPK/WAD decode → PATH3 residual |
| **MK Deception** | SLUS_208.81 | **YES** (midway-menu) | px=822k gifP2=5988 idle-pump | wave-7 PowerOff storm kill |
| **MK Deadly Alliance** | SLUS_204.23 | **YES** (midway-menu) | px=716800 gifP2=35109 exitReq=False | wave-6 post-logo keep-alive |
| **Vexx** | SLUS_203.83 | **YES** (title-surface) | px=581954 prims=4 cdvd=318 members=17 | wave-6 STREE0 VFS |
| **Whiplash** | SLUS_206.84 | **YES** (title-surface) | px=286720 frontend Start full | wave-6 full title path |
| **Haven** | SLUS_205.17 | **YES** (title-surface) | px=286720 gifP3=68 cdvd=7004 TITLES_VAG | wave-6 JREXIT/SP frame + NUSOUND; IMAGE residual |

\*GoW gate is first real GS + pad-interactive, not MK MAINMENU.

> **Burnout 3 logo-frontend MENU YES** (wave-6 Soft-GS). Other titles still open.

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
