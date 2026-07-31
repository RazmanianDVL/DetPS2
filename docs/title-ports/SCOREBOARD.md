# Commercial title scoreboard — main menu campaign

**Date**: 2026-07-30 (9-title concurrent wave)  
**Branch / PR**: [`campaign/issue-knockout` #28](https://github.com/RazmanianDVL/DetPS2/pull/28) tip **`f807ab1`**  
**Constraints**: `DETPS2_SEMA_STALL_YIELD` **OFF** · Soft-GS metrics = ground truth · no iGPU  
**Smokes**: ALL PASSED after integrate  
**Wiki**: [Commercial-Titles](https://github.com/RazmanianDVL/DetPS2/wiki/Commercial-Titles)

| Title | Serial | Menu? | Key tip metric | Wall |
|-------|--------|-------|----------------|------|
| **MK Shaolin Monks** | SLUS_210.87 | **NEAR** | gifP3=11 FAE8 | selection/second chrome; C1C0 never binds |
| **Burnout 3** | SLUS_210.50 | **No** | gifP3=436 cdvd=6584 | presentation px=0 |
| **Blood Omen 2** | SLUS_200.24 | **No** | px=3 cdvd=1733 gameOpens=2 | force-game CODE+MAINMENU Open; Soft-GS menu not drawn |
| **God of War** | SCUS_973.99 | **No*** | **gifP3=1** first | FILEIO/LoadWad; px=0 |
| **MK Deception** | SLUS_208.81 | **No** | cdvd 287→399 | no member .ssf CallRpc |
| **MK Deadly Alliance** | SLUS_204.23 | **No** | **gameart open** cdvd=771 | px=0 post-open |
| **Vexx** | SLUS_203.83 | **No** | **cdvd 0→4** GAME.TXT | WaitSema / more assets |
| **Whiplash** | SLUS_206.84 | **No** | cdvd=256 RKV warm | MOD_LOAD path="" |
| **Haven** | SLUS_205.17 | **No** (LIKELY-NEAR) | px=286720 gifP3=68 cdvd=923 fioOps≥8 | w4: DLL.DAT real bind @0x800000 (1.17MiB); NUSOUND partial echo; Soft-GS logo clear only; WaitSema residual |

\*GoW gate is first real GS + pad-interactive, not MK MAINMENU.

> **No MENU YES.**

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
