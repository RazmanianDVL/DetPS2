# DetPS2 tooling index (operators + agents)

Master map of bring-up tools, budgets, oracles, and media policy.  
**SOP:** [`docs/AGENT_SOP.md`](AGENT_SOP.md) · **subagent paste prompt:** [`docs/AGENT_PROMPT_TEMPLATE.md`](AGENT_PROMPT_TEMPLATE.md) · **scripts README:** [`tools/README.md`](../tools/README.md).

---

## 1. Operator scripts (`tools/`)

| Script | Role |
|--------|------|
| **`run-title.ps1`** | One title at a fixed cycle budget → `out/traces/` |
| **`scoreboard.ps1`** | Multi-title Soft-GS metrics table → `out/traces/scoreboard-*.md/json` |
| **`play-lookup.ps1`** | Play! GameConfig + wall→source map (**required before new HLE**) |
| **`media-map.ps1`** | Inventory `user-media*.json` + `burnout-only.json` (ISO/BIOS present?) |
| **`clean-traces.ps1`** | Move root `b3-/bo2-/gow-/sm-/*.txt` noise into `out/traces/archive-YYYYMMDD/` |
| **`scoreboard-fleet.json`** | Default fleet ids, serials, media paths, menu kinds |

```powershell
# From repo root (detps2/)
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue

pwsh ./tools/play-lookup.ps1 -Serial SLUS_210.87 -Wall PAD
pwsh ./tools/run-title.ps1 -Media user-media-mk.json -Budget diagnose
pwsh ./tools/scoreboard.ps1 -Budget diagnose
pwsh ./tools/scoreboard.ps1 -Budget verify -Titles mk-shaolin-monks,burnout-3,god-of-war
pwsh ./tools/media-map.ps1 -WriteReport
pwsh ./tools/clean-traces.ps1          # move only
pwsh ./tools/clean-traces.ps1 -DryRun
```

Optional / if present in tree (not required for every wave):

| Name | Role |
|------|------|
| **`scoreboard-metrics`** | Extra Soft-GS / heuristic aggregation CLI if added under `tools/` or Core |
| **`wall-save` / `wall-load`** | Wall snapshot save/load helpers if added (checkpoint PC/RPC/cdvd/px state between runs) |

If those CLIs are missing, use `blocker-trace` / `run-title.ps1` logs under `out/traces/` and scoreboard JSON as the metrics surface.

---

## 2. Cycle budgets (stop wasting wall-clock)

| Budget | Cycles | When |
|--------|--------|------|
| **diagnose** | **20M** | Find the wall. **Default. Always first.** |
| **verify** | **50M** | Confirm a fix moved the wall. |
| **claim** | **100M+** | Only when asserting MENU YES / first real GS. |

Do **not** open an investigation with 150M blind runs. After a fix, re-run **diagnose** scoreboard regression (at least SM + B3 + one Midway + GoW).

---

## 3. Oracle stack (mandatory order)

| # | Tool | Path / command | When |
|---|------|----------------|------|
| 1 | DetPS2 traces | `run-title.ps1 -Budget diagnose` / `blocker-trace` | Always first |
| 2 | **Play! source** | `C:\Windows\Play\` + `play-lookup.ps1` | Every wall **before** new HLE |
| 3 | PCSX2 + **PINE** | same ISO; `EnablePINE=true` | Unsure of live mem/PC/flags |
| 4 | Soft-GS PPM / capture | `detps2_frame.ppm` / capture card | Visual after assets draw |

Play! module map and GameConfig policy: **`docs/PLAY_HLE_ORACLE.md`**.

### Play! (this machine)

| Path | Contents |
|------|----------|
| `C:\Windows\Play\` | Full Play! tree |
| `C:\Windows\Play\GameConfig.xml` | Per-title patches (sparse) |
| `C:\Windows\Play\Source\iop\` | IOP HLE modules |

Port **ABI + side-effects** into DetPS2 C# SHARED HLE — do **not** copy the C++ engine wholesale.  
If the tree is missing: clone `https://github.com/jpd002/Play-` and pass `-PlayRoot`.

### PCSX2 + PINE

```ini
EnablePINE=true
PINESlot=28011
```

```text
pcsx2-qt -batch -- "<path-to-iso>"
```

One PCSX2 instance. Force **dGPU** if present fails (no iGPU on this host). Do not publish personal install paths in the wiki.

---

## 4. Soft-GS vs host dGPU

| Surface | Role |
|---------|------|
| **CPU Soft-GS** | **Ground truth** for DetPS2 metrics (`px`, `gifPath3`, dmac, CDVD, PC, binds/calls) |
| **Host present / Desktop** | Optional window; may need **dGPU** |
| **iGPU** | **Do not assume** — this operator machine has **no** onboard graphics |
| **PCSX2 HW renderer** | dGPU when required; Soft-GS/PPM still fine for DetPS2 claims |

Scoreboard and `run-title` report Soft-GS metrics only. MENU YES is **manual/claim**, not the heuristic alone (`NEAR?` / `GS?` ≠ claim).

`DETPS2_SEMA_STALL_YIELD` must stay **OFF** unless a documented experiment.

---

## 5. NAS / media library

| Item | Policy |
|------|--------|
| Library sampling | **`docs/LIBRARY_SAMPLING.md`** — open fleet, not locked to scoreboard 9 |
| Media JSON | `user-media*.json`, `burnout-only.json` (gitignored except `user-media.example.json`) |
| Paths | Prefer **UNC** / mapped library paths; **do not** bulk-copy ISOs onto C: |
| Inventory | `pwsh ./tools/media-map.ps1` (−WriteReport → `out/traces/media-map.md`) |
| Never commit | BIOS, ISOs, dumps, private wiki path leaks |

Schema: `biosPath` + `titles[]` with `id`, `title`, `path`, `kind` (`iso`/`elf`/`bin`) — see `user-media.example.json`.

---

## 6. Scoreboard

| Artifact | Location |
|----------|----------|
| Fleet config | `tools/scoreboard-fleet.json` |
| Runner | `tools/scoreboard.ps1` |
| Per-run output | `out/traces/scoreboard-YYYYMMDD-HHMMSS.md` + `.json` |
| Campaign doc (opt-in) | `docs/title-ports/SCOREBOARD.md` via `-UpdateDoc` |

```powershell
pwsh ./tools/scoreboard.ps1 -Budget diagnose
pwsh ./tools/scoreboard.ps1 -Budget verify -Titles mk-shaolin-monks,burnout-3
pwsh ./tools/scoreboard.ps1 -Budget claim -UpdateDoc   # only when claiming
```

Heuristic columns are progress signals. Claims need issue evidence + commit SHA and the MENU bar in `docs/AGENT_SOP.md` §4.

---

## 7. Wall snapshots CLI (if present)

Some waves may add lightweight wall snapshot helpers:

| CLI (if present) | Intended use |
|------------------|--------------|
| `scoreboard-metrics` | Parse / summarize Soft-GS rows without a full re-run |
| `wall-save` | Save wall context (PC, RPC, cdvd, px, gifP3, last log paths) |
| `wall-load` | Reload a prior wall snapshot for compare / resume notes |

**If absent:** treat `out/traces/*-out.txt` + scoreboard JSON as the wall record. Do not invent snapshot formats mid-wave without updating this index.

Core diagnostics still live on `DetPS2.Core` (`blocker-trace`, `pad-inject`, `disasm`, `probe-frame`, …) — see `docs/DEVELOPER_GUIDE.md`.

---

## 8. Trace hygiene

| Rule | Detail |
|------|--------|
| New runs | Write under **`out/traces/`** (gitignored) via `run-title` / `scoreboard` |
| Root litter | `b3-*.txt`, `bo2-*.txt`, `gow-*.txt`, `sm-*.txt`, `*-err.txt`, `*-out.txt` |
| Clean | `pwsh ./tools/clean-traces.ps1` → `out/traces/archive-YYYYMMDD/` |
| Delete | Only with **`-Delete`** (default is move) |
| Never commit | Root investigation `*.txt`, BIOS/ISO blobs |

---

## 9. Related docs

| Doc | Role |
|-----|------|
| [`docs/AGENT_SOP.md`](AGENT_SOP.md) | Mandatory commercial bring-up SOP |
| [`docs/AGENT_PROMPT_TEMPLATE.md`](AGENT_PROMPT_TEMPLATE.md) | Copy-paste subagent prompt |
| [`docs/PLAY_HLE_ORACLE.md`](PLAY_HLE_ORACLE.md) | Play! wall → module map |
| [`docs/LIBRARY_SAMPLING.md`](LIBRARY_SAMPLING.md) | Open fleet / NAS sampling |
| [`docs/title-ports/SCOREBOARD.md`](title-ports/SCOREBOARD.md) | Last campaign scoreboard snapshot |
| [`tools/README.md`](../tools/README.md) | Short script cheat sheet |
| [`CONTRIBUTING.md`](../CONTRIBUTING.md) | Process + architecture freeze |

**Git policy for agents:** local commit only if the operator asked; **no push / no PR** from scaffolding subagents unless the human explicitly takes over.
