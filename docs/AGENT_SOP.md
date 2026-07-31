# DetPS2 agent SOP — commercial bring-up (mandatory)

**Audience:** human operators and AI subagents.  
**Goal:** faster, less wrong HLE — not more blind 150M runs.

---

## 0. Non-negotiables

1. **BIOS G0 is closed.** Prefer deepening **shared** HLE over inventing title plants.  
2. **No literal IRX execution required** for menu work (Phase L / #12 ignored unless asked).  
3. **Do not guess** boot flow, RPC shapes, FILEIO layouts, or menu type.  
4. **Soft-GS metrics are ground truth for DetPS2** (CPU Soft-GS). Host GPU is only for optional present/PCSX2 UI — this machine has **no iGPU**; use dGPU if a window is required, else stay headless.  
5. **DETPS2_SEMA_STALL_YIELD** must stay **OFF** unless a documented experiment.  
6. **Never commit** BIOS/ISO blobs, private paths in wiki, or root-level `*.txt` traces.

---

## 1. Oracle stack (order is mandatory)

| # | Tool | Path / command | When |
|---|------|----------------|------|
| 1 | DetPS2 traces | `pwsh ./tools/run-title.ps1 -Media … -Budget diagnose` | Always first |
| 2 | **Play! source** | `C:\Windows\Play\` + `pwsh ./tools/play-lookup.ps1` | Every wall before new HLE |
| 3 | PCSX2 + PINE | same ISO; `pwsh ./tools/pine-helper.ps1` | Unsure of live mem/PC/flags |
| 4 | Soft-GS PPM / capture | `detps2_frame.ppm` | Visual after assets draw |

Play! map and GameConfig policy: **`docs/PLAY_HLE_ORACLE.md`**.

```powershell
# Required before inventing FILEIO/SIF/PAD/CDVD/MC HLE:
pwsh ./tools/play-lookup.ps1 -Serial SLUS_200.24 -Wall FILEIO
```

Port **ABI + side-effects** into C#; do **not** copy the C++ engine wholesale.

---

## 2. Cycle budgets (stop wasting wall-clock)

| Budget | Cycles | Use |
|--------|--------|-----|
| **diagnose** | **20M** | Find the wall. Default. |
| **verify** | **50M** | Confirm a fix moved the wall. |
| **claim** | **100M+** | Only when asserting MENU YES / first GS. |

```powershell
pwsh ./tools/run-title.ps1 -Media user-media-mk.json -Budget diagnose
pwsh ./tools/run-title.ps1 -Media user-media-mk.json -Budget verify
# Multi-title:
pwsh ./tools/scoreboard.ps1 -Budget diagnose
pwsh ./tools/scoreboard.ps1 -Budget verify -Titles mk-shaolin-monks,burnout-3
# Fixed four-title matrix (SM + B3 + BO2 + GoW):
pwsh ./tools/regression-matrix.ps1 -Budget diagnose
```

Traces go to **`out/traces/`** (gitignored). Do not dump hundreds of `b3-*.txt` at repo root.

---

## 3. Fix policy

1. Prefer **SHARED** (`RealSifRpc`, `SonyKernelHle`, `BiosBootHost`, CDVD, …).  
2. **GameQuirks** only document/unstick a title-local wall after shared path is insufficient.  
3. After every meaningful change:  
   - unit smokes: `dotnet build Tests … && DetPS2.Tests`  
   - regression: `pwsh ./tools/regression-matrix.ps1 -Budget diagnose` (SM + B3 + BO2 + GoW)  
   - or subset: `scoreboard.ps1 -Budget diagnose -Titles …`  
4. One owner per shared file in multi-agent waves (avoid thrashing `RealSifRpc.cs`).

---

## 4. MENU claim bar (do not lie)

| Title class | MENU / “menu” claim |
|-------------|---------------------|
| MK / Midway | Full interactive main menu: hard accept + second chrome (not soft NEAR) |
| Burnout 3 | Non-black Soft-GS after FRONTEND/logo path |
| Blood Omen 2 | MAINMENU draw with px ≫ logo; pad if applicable |
| GoW / SotC | **First real GS** (px>0 non-black) then pad-interactive — **not** MK MAINMENU language |
| Others | First title surface / logo chrome as defined on the title wiki page |

Scoreboard **heuristic** (`NEAR?` / `GS?`) is **not** a claim. Claims need issue evidence + commit SHA.

---

## 5. PCSX2 + PINE (when unsure)

Use the helper — do not hand-edit blindly:

```powershell
pwsh ./tools/pine-helper.ps1 -CheckConfig
pwsh ./tools/pine-helper.ps1 -WriteConfigSample          # → out/traces/pcsx2-pine-sample.ini
pwsh ./tools/pine-helper.ps1 -Batch -Iso "<same ISO>"
```

- Config: `EnablePINE=true`, `PINESlot=28011` (or local slot).  
- Boot: `pcsx2-qt -batch -- "ISO"` — **one instance** (helper refuses if already running).  
- Compare DetPS2 PC/mem/flags at the same wall.  
- Locate binary via `C:\pcsx2`, `$env:PCSX2_PATH`, or `-Pcsx2Path`.  
- **Do not document personal install paths in the wiki.**  
- Force **discrete GPU** for PCSX2 if Windows has no iGPU / present fails → §6 / `pin-gpu.ps1`.

---

## 6. Hardware notes (this operator machine)

- **No CPU onboard graphics** — Soft-GS headless is the default success path.  
- **dGPU** required only for Desktop present / some PCSX2 HW renderers.  

```powershell
pwsh ./tools/pin-gpu.ps1 -ListAdapters
pwsh ./tools/pin-gpu.ps1 -ExePath "C:\pcsx2\pcsx2-qt.exe"   # High performance preference
```

- **Play!** tree: `C:\Windows\Play\`.  
- **BIOS:** operator `user-media*.json` → SCPH70008 under `Documents\PCSX2\bios` (never commit).

---

## 7. Media library (NAS)

Prefer UNC paths in gitignored `user-media-*.json` — **no bulk ISO copies to C:**.

```powershell
pwsh ./tools/nas-media.ps1 -Probe
pwsh ./tools/nas-media.ps1 -List
pwsh ./tools/nas-media.ps1 -Search "*Shaolin*"
pwsh ./tools/nas-media.ps1 -WriteUserMedia -Serial SLUS_210.87 -Search "*Shaolin*" -Out user-media-mk.json
```

Roots probed: `\\Home_NAS\ND\Emulation\Playstation 2` and IP/spelling alternatives (`docs/LIBRARY_SAMPLING.md`).  
Helper reports **path existence / filenames only** — never credentials.

---

## 8. Scoreboard compare + regression matrix

After a fix wave, gate metrics:

```powershell
# Diff two scoreboard JSON dumps
pwsh ./tools/compare-scoreboard.ps1 `
  -Baseline out/traces/scoreboard-old.json `
  -Current  out/traces/scoreboard-new.json `
  -Out out/traces/delta.md -FailOnRegression

# Always SM + B3 + BO2 + GoW; optional baseline + fail on skip/regression
pwsh ./tools/regression-matrix.ps1 -Budget verify `
  -BaselineJson out/traces/scoreboard-old.json -FailOnSkip
```

Regression flags (compare): px drop, significant cdvd drop, `exitRequested` appeared, RAN→SKIP, gifP3 collapse.  
Matrix writes `out/traces/regression-*.md`. Details: **`tools/README.md`**.

---

## 9. Deliverable template (every agent / PR)

```markdown
## Title / issue
## Wall (PC, RPC, cdvd, px, gifP3)
## Play! consulted (paths + GameConfig hit Y/N)
## PINE used (Y/N + why)
## Change (SHARED vs TITLE_LOCAL)
## Evidence (budget used, scoreboard / regression-matrix row)
## Residual / MENU claim
```

---

## 10. Related docs / tools

| Doc / tool | Role |
|------------|------|
| `docs/PLAY_HLE_ORACLE.md` | Play! module map |
| `docs/LIBRARY_SAMPLING.md` | Open fleet + UNC library policy |
| `docs/TOOLING.md` | Master tooling / budgets / oracles index |
| `docs/AGENT_PROMPT_TEMPLATE.md` | Copy-paste commercial subagent prompt |
| `docs/bios-ports/BIOS_COMPLETION_PLAN.md` | BIOS G0 (done) |
| `docs/title-ports/SCOREBOARD.md` | Last scoreboard run output |
| `tools/README.md` | Full operator tool index |
| `tools/scoreboard.ps1` | Multi-title metrics |
| `tools/run-title.ps1` | Single-title budgets |
| `tools/play-lookup.ps1` | GameConfig + wall map |
| `tools/media-map.ps1` | Media JSON inventory |
| `tools/clean-traces.ps1` | Archive root-level trace noise |
| `tools/pine-helper.ps1` | PCSX2 locate + PINE config / batch boot |
| `tools/pin-gpu.ps1` | List adapters + dGPU pin for exe |
| `tools/nas-media.ps1` | NAS probe / ISO search / media JSON scaffold |
| `tools/compare-scoreboard.ps1` | Scoreboard JSON delta + regression flags |
| `tools/regression-matrix.ps1` | SM+B3+BO2+GoW matrix + optional baseline gate |
