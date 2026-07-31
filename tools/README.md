# DetPS2 operator tools

Faster commercial bring-up without blind 150M runs. Soft-GS metrics do **not** require an iGPU; host present / PCSX2 UI may need a dGPU pin on this machine.

| Script | Purpose |
|--------|---------|
| **`scoreboard.ps1`** | Multi-title Soft-GS metrics + **T0–T7 / G1–G4** → `out/traces/scoreboard-*.md` + `.json` |
| **`SCOREBOARD_SCHEMA.md`** | Claim field + tier schema (PL-001 / GX-001/002 / PL-002 / GX-040) |
| **`pad-scripts/`** | Example `--pad-script=` files for T2 interactive claims (PL-002) |
| **`run-title.ps1`** | One title at diagnose/verify/claim cycle budget |
| **`play-lookup.ps1`** | Play! GameConfig + wall→source map (`C:\Windows\Play`) |
| **`scoreboard-fleet.json`** | Default fleet list + serials + media JSON names + tier schema pointer |
| **`pine-helper.ps1`** | Locate PCSX2, check/write PINE config, optional `-batch` ISO boot |
| **`pin-gpu.ps1`** | List adapters + pin exe to high-performance dGPU (UserGpuPreferences) |
| **`nas-media.ps1`** | Probe PS2 dump library UNC, list/search ISOs, scaffold `user-media-*.json` |
| **`compare-scoreboard.ps1`** | Markdown delta of two scoreboard JSONs; flag regressions |
| **`regression-matrix.ps1`** | Fixed SM + B3 + BO2 + GoW matrix via scoreboard; optional baseline gate |

Policy: **`docs/AGENT_SOP.md`**. Play! map: **`docs/PLAY_HLE_ORACLE.md`**. Library: **`docs/LIBRARY_SAMPLING.md`**.  
**IRX-first plan:** **`docs/IRX_EXECUTION_PHASE_PLAN.md`** (epic #12).

### Environment: IRX is the product (not a “mode”)

The emulator **always** loads and runs real BIOS/disc IRX on the IOP. There is no separate IRX mode.

| Variable | Values | Meaning |
|----------|--------|---------|
| **`DETPS2_FORCE_HLE_IOP`** | unset (normal) / `1` | **Emergency bisect only** — disables IRX exec arming and forces name-only/HLE-first IOP path. Do not use for normal runs. |
| `DETPS2_LITERAL_IRX` | unset / `0` | **Legacy:** only `=0` opts out (same as FORCE_HLE_IOP). Unset means IRX on. |
| `DETPS2_IOPRP_NAME_ONLY` | `1` | Force name-only IOPRP register (bisect). |
| `DETPS2_TRACE_IOP` | `1` | Sample IOP PC. |
| `DETPS2_SEMA_STALL_YIELD` | unset / OFF | Must stay off. |

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
# IRX on by default — do NOT set FORCE_HLE_IOP for normal use
```

**Freeze:** no new GameQuirk thrash plants / multi-title HLE plant waves while #12 is active.

---

## Quick start

```powershell
# From repo root (detps2/)
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
# IRX is default — no DETPS2_LITERAL_IRX required

# Before inventing HLE:
pwsh ./tools/play-lookup.ps1 -Serial SLUS_210.87 -Wall PAD

# Single title, short diagnose (20M):
pwsh ./tools/run-title.ps1 -Media user-media-mk.json -Budget diagnose

# Full fleet diagnose (expects media JSON + ISOs):
pwsh ./tools/scoreboard.ps1 -Budget diagnose

# Native JSON metrics + optional Soft-GS PPM dump dir (px>0 only):
pwsh ./tools/scoreboard.ps1 -Budget diagnose -NativeMetrics -DumpSoftGsDir out/traces/softgs `
  -Titles mk-shaolin-monks,god-of-war

# Subset after a fix:
pwsh ./tools/scoreboard.ps1 -Budget verify -Titles mk-shaolin-monks,burnout-3,god-of-war

# Schema (T0–T7, G1–G4, claim fields):
#   tools/SCOREBOARD_SCHEMA.md

# Pad-script T2 claim (PL-002) — does not invent DISPFB; only drives PadInput:
#   dotnet exec out/seat-s10/DetPS2.Core.dll pad-inject user-media-b3.json `
#     --cycles=20000000 --host-present --pad-script=tools/pad-scripts/example-start-cross.pad

# Fixed four-title regression matrix:
pwsh ./tools/regression-matrix.ps1 -Budget diagnose
pwsh ./tools/regression-matrix.ps1 -Budget verify -BaselineJson out/traces/scoreboard-prev.json -FailOnSkip
```

---

## Cycle budgets

| Budget | Cycles | When |
|--------|--------|------|
| diagnose | 20M | Find wall (default) |
| verify | 50M | Confirm fix |
| claim | 100M | MENU / first-GS claim only |

Traces → **`out/traces/`** (gitignored). Do not litter repo root with `b3-*.txt` / `bo2t.txt` / `*-err.txt`.

```powershell
pwsh ./tools/clean-traces.ps1           # move root noise → out/traces/archive-YYYYMMDD/
pwsh ./tools/clean-traces.ps1 -DryRun
pwsh ./tools/clean-traces.ps1 -ReportSize
```

HLE→IRX SID debt matrix: **`docs/irx/HLE_TO_IRX_MATRIX.md`**.

---

## PCSX2 + PINE (`pine-helper.ps1`)

When boot flow, RPC, FILEIO layouts, or live flags are unclear: same ISO in PCSX2 with PINE. **Do not guess.**

```powershell
# Locate binary + report EnablePINE / PINESlot
pwsh ./tools/pine-helper.ps1 -CheckConfig

# Write sample keys to out/traces/pcsx2-pine-sample.ini (not user profile)
pwsh ./tools/pine-helper.ps1 -WriteConfigSample

# Optional: merge into Documents\PCSX2\inis\PCSX2.ini
pwsh ./tools/pine-helper.ps1 -WriteConfigSample -ForceUserConfig

# Boot ISO (one instance; refuses if pcsx2 already running)
pwsh ./tools/pine-helper.ps1 -Batch -Iso "\\Home_NAS\ND\Emulation\Playstation 2\game.iso"
```

| Item | Value |
|------|--------|
| Keys | `EnablePINE=true`, `PINESlot=28011` |
| Boot | `pcsx2-qt -batch -- "<ISO>"` (avoid `-nogui` if it hangs) |
| Locate | `C:\pcsx2`, `$env:PCSX2_PATH`, common Program Files roots |
| SOP | One instance; compare DetPS2 at the same wall; pin dGPU if no iGPU |

---

## dGPU pin (`pin-gpu.ps1`)

This operator host has **no iGPU**. Soft-GS stays headless; pin only when PCSX2 / Desktop present needs a window.

```powershell
pwsh ./tools/pin-gpu.ps1 -ListAdapters
pwsh ./tools/pin-gpu.ps1 -ExePath "C:\pcsx2\pcsx2-qt.exe"
pwsh ./tools/pin-gpu.ps1 -ExePath "C:\pcsx2\pcsx2-qt.exe" -Preference high
pwsh ./tools/pin-gpu.ps1 -ExePath "C:\pcsx2\pcsx2-qt.exe" -Remove
```

Uses `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` (`GpuPreference=2;` = high performance). Also prints Windows Settings → Graphics steps if registry is unavailable.

---

## NAS media library (`nas-media.ps1`)

Probe the legal dump share; scaffold gitignored media JSON. **Never prints credentials** — only path reachability and public filenames.

```powershell
pwsh ./tools/nas-media.ps1 -Probe
pwsh ./tools/nas-media.ps1 -List
pwsh ./tools/nas-media.ps1 -Search "*Burnout*"
pwsh ./tools/nas-media.ps1 -WriteUserMedia -Serial SLUS_210.87 -Search "*Shaolin*" -Out user-media-mk.json
```

Default roots tried (in order):

- `\\Home_NAS\ND\Emulation\Playstation 2`
- `\\Home_NAS\ND\Emulation\PlayStation 2`
- `\\192.168.0.17\ND\Emulation\Playstation 2` (and PlayStation spelling)

BIOS for templates: first match under `Documents\PCSX2\bios` containing `scph70008` / `SCPH70008`. Prefer UNC `path` entries — do not bulk-copy ISOs to `C:`.

---

## Scoreboard compare (`compare-scoreboard.ps1`)

Diff two `scoreboard-*.json` (or compatible row objects).

```powershell
pwsh ./tools/compare-scoreboard.ps1 `
  -Baseline out/traces/scoreboard-old.json `
  -Current  out/traces/scoreboard-new.json `
  -Out out/traces/delta.md `
  -FailOnRegression
```

Markdown columns: status, PC changed?, px, gifP3, dmac, cdvd, binds/calls, flags.

**Regression flags (defaults):**

| Condition | Flag |
|-----------|------|
| px drop ≥ 25% | `REGRESS:px-down-*` |
| cdvd drop ≥ 20% (baseline cdvd > 100) | `REGRESS:cdvd-down-*` |
| `exitRequested` False→True | `REGRESS:exit-appeared` |
| RAN → SKIP-* | `REGRESS:became-skip` |
| gifP3 ≥5 → &lt;2 | `REGRESS:gifP3-collapse` |

Exit code **2** with `-FailOnRegression` when any regression fires.

---

## Regression matrix (`regression-matrix.ps1`)

Always runs these fleet ids via `scoreboard.ps1`:

1. `mk-shaolin-monks`
2. `burnout-3`
3. `blood-omen-2`
4. `god-of-war`

```powershell
pwsh ./tools/regression-matrix.ps1 -Budget diagnose
pwsh ./tools/regression-matrix.ps1 -Budget verify -FailOnSkip
pwsh ./tools/regression-matrix.ps1 -Budget verify `
  -BaselineJson out/traces/scoreboard-20260730-171946.json `
  -FailOnSkip
```

Outputs:

- `out/traces/regression-YYYYMMDD-HHMMSS.md`
- underlying `out/traces/scoreboard-*.json` / `.md`
- optional `out/traces/regression-compare-*.md` when `-BaselineJson` is set

| Exit | Meaning |
|------|---------|
| 0 | OK |
| 1 | `-FailOnSkip` and a title was SKIP/MISSING |
| 2 | baseline compare regressions (`-FailOnRegression`, default on when baseline set) |
| 3 | both skip + regression |

---

## Notes

- Do **not** break `scoreboard.ps1` / `run-title.ps1` / `play-lookup.ps1` call contracts when editing them — matrix/compare wrap them.
- `DETPS2_SEMA_STALL_YIELD` must stay **OFF** unless a documented experiment.
- Never commit BIOS/ISO blobs, private wiki paths, or root-level `*.txt` traces.
- Scoreboard **heuristic** (`NEAR?` / `GS?`) is not a MENU YES claim.
