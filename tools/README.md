# DetPS2 operator tools

Faster commercial bring-up without blind 150M runs.

| Script | Purpose |
|--------|---------|
| **`scoreboard.ps1`** | Multi-title Soft-GS metrics table → `out/traces/` + `docs/title-ports/SCOREBOARD.md` |
| **`run-title.ps1`** | One title at diagnose/verify/claim cycle budget |
| **`play-lookup.ps1`** | Play! GameConfig + wall→source map (`C:\Windows\Play`) |
| **`media-map.ps1`** | Inventory `user-media*.json` + `burnout-only.json` (ISO/BIOS exists?) |
| **`clean-traces.ps1`** | Move root `b3-/bo2-/gow-/sm-/*.txt` noise → `out/traces/archive-YYYYMMDD/` |
| **`scoreboard-fleet.json`** | Default fleet list + serials |

Full index (budgets, Play!, PINE, Soft-GS, NAS, scoreboard): **`docs/TOOLING.md`**.  
Agent SOP / paste prompt: **`docs/AGENT_SOP.md`**, **`docs/AGENT_PROMPT_TEMPLATE.md`**.

## Quick start

```powershell
# From repo root (detps2/)
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue

# Before inventing HLE:
pwsh ./tools/play-lookup.ps1 -Serial SLUS_210.87 -Wall PAD

# Single title, short diagnose (20M):
pwsh ./tools/run-title.ps1 -Media user-media-mk.json -Budget diagnose

# Full fleet diagnose (expects media JSON + ISOs):
pwsh ./tools/scoreboard.ps1 -Budget diagnose

# Subset after a fix:
pwsh ./tools/scoreboard.ps1 -Budget verify -Titles mk-shaolin-monks,burnout-3,god-of-war

# Media inventory + root trace hygiene:
pwsh ./tools/media-map.ps1 -WriteReport
pwsh ./tools/clean-traces.ps1
```

## Cycle budgets

| Budget | Cycles | When |
|--------|--------|------|
| diagnose | 20M | Find wall (default) |
| verify | 50M | Confirm fix |
| claim | 100M | MENU / first-GS claim only |

## Notes

- Traces → **`out/traces/`** (gitignored). Do not litter repo root with `b3-*.txt`.
- Soft-GS metrics do **not** require iGPU; host-present window may need dGPU.
- Agent policy: **`docs/AGENT_SOP.md`**.
