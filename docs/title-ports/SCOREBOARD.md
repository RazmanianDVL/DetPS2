# Commercial title scoreboard — main menu campaign

**Date**: 2026-07-30 (orchestrator sync)  
**Branch / PR**: [`campaign/issue-knockout` #28](https://github.com/RazmanianDVL/DetPS2/pull/28) tip `7d7d6ef`  
**Build**: Release campaign tip  
**Constraints**: `DETPS2_SEMA_STALL_YIELD` **OFF** · Soft-GS metrics = ground truth · no iGPU  
**Smokes**: build + Phase 56 media required after merges  
**Wiki**: [Commercial-Titles](https://github.com/RazmanianDVL/DetPS2/wiki/Commercial-Titles)

| Title | Serial | Menu? | px | gifP3 | dmac | cdvd | RPC binds/calls | Key wall (tip) |
|-------|--------|-------|----|-------|------|------|-----------------|----------------|
| **MK Shaolin Monks** | SLUS_210.87 | **NEAR** | held path | ~11 | high under stream | ~198k WAD | pad-poll | selection index + second chrome (#7 #3) |
| **Burnout 3** | SLUS_210.50 | **No** | 0 | high on deliver | high | STG/TXD path flaky | GTFS | presentation after FRONTEND |
| **Blood Omen 2** | SLUS_200.24 | **No** | ~3 | low | high | pack KAIN path | SN FILEIO | GOE Open / MAINMENU draw (#17 #8) |
| **God of War** | SCUS_973.99 | **No*** | **0** | 0 | ~463 | ~142 IRX | 16 / ~443 | first real GS + FILEIO (#11) |

\*GoW gate is first real GS + pad-interactive title surface, not MK MAINMENU language.

## MENU yes/no (campaign bar)

| Title | MENU? |
|-------|-------|
| MK Shaolin Monks | **NEAR** (not YES) |
| Burnout 3 | **No** |
| Blood Omen 2 | **No** |
| God of War | **No** (no first GS) |

> **No title has proven true interactive main-menu YES.**

## Next priority

1. **MK #7/#3** — hard accept + selection index + second chrome (PINE if ambiguous)  
2. **Dec #22** — stop post-MSL Exit so EE opens member `.ssf`  
3. **BO2 #17/#8** — GOE Open residual → MAINMENU px ≫ logo  
4. **GoW #11** — FILEIO past cdvd≈142 → px>0 non-black  
5. **DA #16 / Haven #21 / Vexx #19** — post-stack asset stream

## Reproduce (tooling)

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
pwsh ./tools/regression-matrix.ps1 -Budget diagnose
pwsh ./tools/scoreboard.ps1 -Budget diagnose
# Title-scoped:
pwsh ./tools/run-title.ps1 -Media user-media-mk.json -Budget diagnose
pwsh ./tools/run-title.ps1 -Media burnout-only.json -Budget diagnose
pwsh ./tools/run-title.ps1 -Media user-media-bloodomen2.json -Budget diagnose
pwsh ./tools/run-title.ps1 -Media user-media-god-of-war.json -Budget diagnose
```

Budgets: diagnose=20M · verify=50M · claim=100M+. See `docs/AGENT_SOP.md`.
