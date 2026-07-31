# Commercial title scoreboard — main menu campaign

**Date**: 2026-07-30 (9-title concurrent wave)  
**Branch / PR**: [`campaign/issue-knockout` #28](https://github.com/RazmanianDVL/DetPS2/pull/28) tip **`f807ab1`**  
**Constraints**: `DETPS2_SEMA_STALL_YIELD` **OFF** · Soft-GS metrics = ground truth · no iGPU  
**Smokes**: ALL PASSED after integrate  
**Wiki**: [Commercial-Titles](https://github.com/RazmanianDVL/DetPS2/wiki/Commercial-Titles)

| Title | Serial | Menu? | Key tip metric | Wall |
|-------|--------|-------|----------------|------|
| **MK Shaolin Monks** | SLUS_210.87 | **NEAR** | gifP3=11 FAE8 slot0+obj C1C0 | second-chrome Path3 (gifP3≥12); MENU YES open |
| **Burnout 3** | SLUS_210.50 | **No** | gifP3=436 cdvd=6584 | presentation px=0 |
| **Blood Omen 2** | SLUS_200.24 | **No** | px=3 cdvd=2135 stream=2.4MB LIST+ENGLISH PC=0x2CD884 | WAVE-5 low-ELF thrash fix; Soft-GS menu not drawn |
| **God of War** | SCUS_973.99 | **No*** | **gifP3=1** first | FILEIO/LoadWad; px=0 |
| **MK Deception** | SLUS_208.81 | **No** | cdvd 287→399 | no member .ssf CallRpc |
| **MK Deadly Alliance** | SLUS_204.23 | **No** | **gameart open** cdvd=771 | px=0 post-open |
| **Vexx** | SLUS_203.83 | **No** | **cdvd 0→4** GAME.TXT | WaitSema / more assets |
| **Whiplash** | SLUS_206.84 | **No** | cdvd=256 RKV warm | MOD_LOAD path="" |
| **Haven** | SLUS_205.17 | **No** | px=3 gifP3=67 dmac=197 cdvd=77 | soft-float cleared via SoftFloatBridge; residual VIF1 init spin @0x188AE0; no FILEIO yet |

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
