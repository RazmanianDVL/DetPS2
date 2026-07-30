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
| 3 | PCSX2 + PINE | same ISO; `EnablePINE=true` | Unsure of live mem/PC/flags |
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
```

Traces go to **`out/traces/`** (gitignored). Do not dump hundreds of `b3-*.txt` at repo root.

---

## 3. Fix policy

1. Prefer **SHARED** (`RealSifRpc`, `SonyKernelHle`, `BiosBootHost`, CDVD, …).  
2. **GameQuirks** only document/unstick a title-local wall after shared path is insufficient.  
3. After every meaningful change:  
   - unit smokes: `dotnet build Tests … && DetPS2.Tests`  
   - regression: `scoreboard.ps1 -Budget diagnose` for **at least** SM + B3 + one Midway + GoW  
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

- Config: `EnablePINE=true`, `PINESlot=28011` (or local slot).  
- Boot: `pcsx2-qt -batch -- "ISO"` — one instance.  
- Compare DetPS2 PC/mem/flags at the same wall.  
- **Do not document personal install paths in the wiki.**  
- Force **discrete GPU** for PCSX2 if Windows has no iGPU / present fails.

---

## 6. Hardware notes (this operator machine)

- **No CPU onboard graphics** — Soft-GS headless is the default success path.  
- **dGPU** required only for Desktop present / some PCSX2 HW renderers.  
- **Play!** tree: `C:\Windows\Play\`.  
- **BIOS:** operator `user-media*.json` → SCPH70008 (never commit).

---

## 7. Deliverable template (every agent / PR)

```markdown
## Title / issue
## Wall (PC, RPC, cdvd, px, gifP3)
## Play! consulted (paths + GameConfig hit Y/N)
## PINE used (Y/N + why)
## Change (SHARED vs TITLE_LOCAL)
## Evidence (budget used, scoreboard row)
## Residual / MENU claim
```

---

## 8. Related docs

| Doc | Role |
|-----|------|
| `docs/PLAY_HLE_ORACLE.md` | Play! module map |
| `docs/TOOLING.md` | Master tooling / budgets / oracles index |
| `docs/AGENT_PROMPT_TEMPLATE.md` | Copy-paste commercial subagent prompt |
| `docs/bios-ports/BIOS_COMPLETION_PLAN.md` | BIOS G0 (done) |
| `docs/title-ports/SCOREBOARD.md` | Last scoreboard run output |
| `tools/scoreboard.ps1` | Multi-title metrics |
| `tools/run-title.ps1` | Single-title budgets |
| `tools/play-lookup.ps1` | GameConfig + wall map |
| `tools/media-map.ps1` | Media JSON inventory |
| `tools/clean-traces.ps1` | Archive root-level trace noise |
