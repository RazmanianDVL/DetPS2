# M8-a — Quiet retirement checklist: Haven + Vexx PreferIopRp / version plant

**Date:** 2026-08-04  
**Mode:** ops checklist only — **no GameQuirks mass-delete** in this pass.  
**Scope:** retire **version policy debt only** for:

| Title | Serial | Assist | What this checklist may soft-disable |
|-------|--------|--------|--------------------------------------|
| **Haven: Call of the King** | `SLUS_205.17` | `TeamIcoAssist` | `PreferIopRpGetVersion = true` only (no RAM version plant) |
| **Vexx** | `SLUS_203.83` | `VexxAssist` | `PreferIopRpGetVersion = true` **and** `PlantIopRpVersion` / Step re-plant (`"2520"` @ `0x3D18B8`, `0x3D1938`) |

**Out of scope (do not touch under M8-a):**

- BO2 (`BloodOmen2SnAssist`), B3 (`Burnout3Assist`), GoW (`GodOfWarAssist`), Whip (`WhiplashAssist`) version plants / PreferIopRp / arg rewrites — **require their own A/B** after separate evidence seats.
- Haven non-version residual (SoftFloatBridge, VIF busy, JREXIT, Host→Local chrome, poison-`$ra`).
- Vexx non-version residual (CRT/string heap, STREE0, host CD stubs, path stubs, sid `0x54323`).
- SotC / Ico PreferIopRp (same assist file) — **optional** same-class follow-on; not required to close M8-a Haven row.
- Property `PreferIopRpGetVersion` deletion from `RealSifRpc` (leave until no readers).

**Grounding:** M4-b tag-if-applied in `RealSifRpc.HandleLoadFile` (`LF_F_GET_VERSION`): when `_lastIopRpVersionAscii` is non-empty from UDNL/RESET extract, GetVersion returns `PackAsciiVersion(tag)` **independent of** `PreferIopRpGetVersion`. Design + exit tables: `docs/UDNL_GETVERSION_UNIFICATION.md` §4–§6.  
**Evidence premise (M4-c Haven / M4-d Vexx):** with PreferIopRp **false** and Vexx version **plant suppressed**, LOADFILE GetVersion still matches the UDNL-extracted tag (`"2500"` / `"2520"`) once the title’s real reboot arg is applied — so the legacy flag/plant is redundant for the **version gate**, not for other residual walls.

---

## 1. Why quiet (soft) retirement

| Principle | Practice |
|-----------|----------|
| Prefer env-off | Gate the PreferIopRp assignment and Vexx `PlantIopRpVersion` behind env; default remains today’s behavior until canary green |
| No mass delete | Do not strip Midway/BO2/B3/GoW/Whip in the same PR |
| One axis at a time | Haven: PreferIopRp only. Vexx: PreferIopRp, then plant suppress, then both |
| Keep residual assists | JREXIT / STREE / Host→Local stay on |
| Bisect | `DETPS2_GETVERSION_CLASSIC=1` still forces classic `0x00020000` for SM / panic |

### Suggested soft-disable envs (implement once, use for canary)

Names illustrative — match whatever lands in Core; document the final names on the PR.

| Env | Effect |
|-----|--------|
| `DETPS2_M8A_HAVEN_NO_PREFER_IOPRP=1` | `TeamIcoAssist` for Haven skips `PreferIopRpGetVersion = true` |
| `DETPS2_M8A_VEXX_NO_PREFER_IOPRP=1` | `VexxAssist` skips PreferIopRp assignment |
| `DETPS2_M8A_VEXX_NO_VERSION_PLANT=1` | Skip `PlantIopRpVersion` on mount + Step re-plant (CRT/string/path plants **unchanged**) |
| `DETPS2_GETVERSION_CLASSIC=1` | Global classic GetVersion (emergency; undoes tag-if-applied) — **not** a title soft-disable |

Optional Team ICO blanket (SotC/Ico/Haven): only after Haven green and a separate SotC/Ico A/B.

---

## 2. Preconditions (must be true before accepting soft-disable as “retired”)

| ID | Check | How |
|----|-------|-----|
| **P1** | M4-b tag-if-applied is live | `RealSifRpc` GetVersion: non-empty `_lastIopRpVersionAscii` → packed ASCII without PreferIopRp |
| **P2** | UDNL/RESET publishes tag from retail arg | Haven: `…SYS250\IOPRP250.IMG` → `"2500"`; Vexx: `IOPRP252` → `"2520"` (TRACE_REBOOT / TRACE_RPC) |
| **P3** | SM not in classic-override panic | Fleet SM still green **without** PreferIopRp on SM; do not set PreferIopRp on SM |
| **P4** | Baseline traces exist | Same budget/media as canary, PreferIopRp/plant **on** (product default) |

If P1–P2 fail, **stop** — soft-disable will re-Exit Haven / re-stall Vexx version gate.

---

## 3. Acceptance metrics (compare soft-off vs baseline)

Capture from `blocker-trace` stdout/stderr + `DETPS2_TRACE_RPC=1` / `DETPS2_TRACE_REBOOT=1` as needed.

### 3.1 Haven (`user-media-haven.json`, `SLUS_205.17`)

| Metric | Pass (soft PreferIopRp off) | Fail |
|--------|------------------------------|------|
| GetVersion reply | Packed `"2500"` (`result` shows ASCII LE; `ioprp="2500"`) with `preferIopRp=False` in TRACE_RPC | Classic `0x00020000` after image reboot → Exit pre MOD_LOAD |
| `exitRequested` | False through post-SYS250 stack | True early after reboot gate |
| Binds / calls | ≥ baseline order (historically binds≈12, calls≈16 @100M class) | Collapse to pre-IRX (~0 binds) |
| cdvdSectors | ≥ baseline post-reboot | Stuck 0 after gate |
| PC band @100M claim | Soft-float / post-decompress class (not stuck pre-gate Exit) | Immediate Exit / nop-sled class |
| Non-version residual | SoftFloat / VIF / JREXIT still fire as needed | Do **not** “fix” by deleting residual assists |

**Budget honesty:** fleet **50M** is CRT0 black (px=0) — not a version-gate claim. Version + IRX stack claim budget **≥100M** (see `docs/title-ports/HAVEN.md`).

### 3.2 Vexx (`user-media-vexx.json`, `SLUS_203.83`)

| Metric | Pass (PreferIopRp off + plant suppress) | Fail |
|--------|------------------------------------------|------|
| GetVersion | `"2520"` from extract with preferIopRp=False | Classic / wrong tag |
| Version cells (optional proof) | After natural GetVersion store: cells `"2520"` **without** plant, **or** gate advances even if cells still `"...."` because RPC path is source of truth | Gate fails while plant suppressed |
| `_versionReplants` | 0 under plant suppress (trace if exposed) | Continuous re-plant needed to pass gate |
| Pad / OPEN path | Intact vs baseline (STREE/host CD residual OK) | Pad OPEN regression |
| lit / prims hold | No worse than baseline plateau (version is not the lit wall) | Unexpected early Exit / zero binds |

**Do not** require MENU YES or lit>>20k for M8-a — residual is SIF sid `0x54323` / path graph, not IOPRP digits.

### 3.3 Shared regression guards

| Guard | Expectation |
|-------|-------------|
| SM (`SLUS_210.87`) smoke / spine | Unchanged; no PreferIopRp; classic or tag-if-applied per SM canary policy |
| Midway DA FILEIO | PreferSnFileIo path unchanged (FILEIO-2200 still PreferIopRp-gated separately — **do not** use M8-a to “fix” FILEIO-2200) |
| Other titles PreferIopRp | Untouched |

---

## 4. Canary commands

Repo root; adjust `-o` / media paths if local layout differs. Prefer Release Core build once.

### 4.1 Haven — baseline (product default)

```powershell
$env:DETPS2_TRACE_RPC = "1"
$env:DETPS2_TRACE_REBOOT = "1"
Remove-Item Env:DETPS2_M8A_HAVEN_NO_PREFER_IOPRP -ErrorAction SilentlyContinue
Remove-Item Env:DETPS2_GETVERSION_CLASSIC -ErrorAction SilentlyContinue

pwsh ./tools/run-title.ps1 -Media user-media-haven.json -Budget claim
# or: diagnose first for fast fail, then claim for IRX stack
```

### 4.2 Haven — soft PreferIopRp off (M4-c / M8-a canary)

```powershell
$env:DETPS2_TRACE_RPC = "1"
$env:DETPS2_TRACE_REBOOT = "1"
$env:DETPS2_M8A_HAVEN_NO_PREFER_IOPRP = "1"
Remove-Item Env:DETPS2_GETVERSION_CLASSIC -ErrorAction SilentlyContinue

pwsh ./tools/run-title.ps1 -Media user-media-haven.json -Budget claim
```

**Parse stderr for:**  
`[LOADFILE] GET_VERSION … ioprp="2500" preferIopRp=False` with non-classic result.

### 4.3 Vexx — staged soft-off

```powershell
# Stage A: PreferIopRp off, plant still on
$env:DETPS2_TRACE_RPC = "1"
$env:DETPS2_TRACE_VEXX = "1"
$env:DETPS2_M8A_VEXX_NO_PREFER_IOPRP = "1"
Remove-Item Env:DETPS2_M8A_VEXX_NO_VERSION_PLANT -ErrorAction SilentlyContinue
pwsh ./tools/run-title.ps1 -Media user-media-vexx.json -Budget verify

# Stage B: PreferIopRp off + version plant suppress (M4-d / M8-a)
$env:DETPS2_M8A_VEXX_NO_PREFER_IOPRP = "1"
$env:DETPS2_M8A_VEXX_NO_VERSION_PLANT = "1"
pwsh ./tools/run-title.ps1 -Media user-media-vexx.json -Budget verify
# claim (100M) only if verify metrics match baseline bind/cdvd class
```

### 4.4 Optional unit / synthetic (no disc)

Existing Bios/UDNL smokes that apply IOPRP arg and assert GetVersion without GameQuirk PreferIopRp remain the **infra** gate (E1–E2). Title canaries remain the **fleet** gate.

### 4.5 Explicit non-goals canary

Do **not** flip soft-disable envs for BO2/B3/GoW/Whip under this checklist. If someone runs:

```text
# FORBIDDEN as M8-a acceptance
DETPS2_M8A_* on blood-omen-2 / burnout / god-of-war / whiplash media
```

…that is a **different** ticket with its own baseline.

---

## 5. Retirement stages (order)

| Stage | Action | Exit |
|-------|--------|------|
| **0** | Confirm M4-b + P1–P4 | Doc + smoke |
| **1** | Land soft-disable envs (default off = current behavior) | Build green; no behavior change |
| **2** | Haven canary PreferIopRp soft-off @ claim | Metrics §3.1 pass |
| **3** | Vexx Stage A then B | Metrics §3.2 pass |
| **4** | Default soft-off **on** for Haven/Vexx only (or invert env to opt-back-in) | Fleet scoreboard no regress |
| **5** | Comment / dead-code PreferIopRp assignment + plant call sites behind `#if false` or delete **Haven/Vexx lines only** | PR after stage 4 holds ≥1 fleet cycle |
| **6** | Global property removal | Only when Midway/BO2/Whip/GoW/SotC readers gone |

**Never jump 0 → 5.** Soft-disable first.

---

## 6. Rollback plan

| Symptom | Immediate rollback | Follow-up |
|---------|-------------------|-----------|
| Haven Exit after reboot / classic GetVersion | Unset `DETPS2_M8A_HAVEN_NO_PREFER_IOPRP`; re-run claim | Check reboot arg extract; TRACE_REBOOT for empty tag |
| Vexx version gate fail under plant suppress | Unset `DETPS2_M8A_VEXX_NO_VERSION_PLANT` first; if still fail unset PreferIopRp soft-off | Title may read BSS cells before GetVersion store — residual S4 mirror, **not** mass plant restore for other titles |
| SM / Midway regress after tag-if-applied change | `DETPS2_GETVERSION_CLASSIC=1` for bisect; revert GetVersion policy PR if needed | M8-a envs are title-only — should not affect SM |
| FILEIO-2200 wrong arm | PreferIopRp still gates FILEIO-2200 arming; Haven/Vexx soft-off may change FILEIO path if tag ≥3000 — if seen, keep PreferIopRp on that title or decouple FILEIO arm from PreferIopRp (separate ticket) | Do not re-enable plants to “fix” FILEIO |
| Residual lit/chrome drop | Restore default envs; confirm version gate still not the cause | Non-version residual is out of M8-a |

Rollback is **env unset** or revert of the soft-disable commit — not a reintroduction of BO2/B3 plants.

---

## 7. Sign-off template

```text
M8-a Haven: soft PreferIopRp OFF @100M
  GET_VERSION ioprp=2500 preferIopRp=False result=<packed>
  exit=0 binds=… calls=… cdvd=… vs baseline binds=… calls=… cdvd=…
  residual assists still active: SoftFloat/VIF/JREXIT/HostLocal = yes
  PASS / FAIL

M8-a Vexx: PreferIopRp OFF + plant suppress @50M/100M
  GET_VERSION ioprp=2520 preferIopRp=False
  versionReplants=0 cells=… pad/OPEN ok
  PASS / FAIL

Explicit: BO2/B3/GoW/Whip NOT retired under this seat.
```

---

## 8. File index

| Path | Role |
|------|------|
| `src/DetPS2.Core/RealSifRpc.cs` | Tag-if-applied GetVersion; PreferIopRp property; FILEIO-2200 PreferIopRp gate |
| `src/DetPS2.Core/GameQuirks/TeamIcoAssist.cs` | Haven PreferIopRp set in `OnDiscMounted` |
| `src/DetPS2.Core/GameQuirks/VexxAssist.cs` | PreferIopRp + `PlantIopRpVersion` / Step re-plant |
| `docs/UDNL_GETVERSION_UNIFICATION.md` | M4 design, E1–E6, per-title retirement table |
| `docs/title-ports/HAVEN.md` | Budget honesty 50M vs 100M |
| `docs/title-ports/VEXX.md` | Residual walls (non-version) |
| `user-media-haven.json` / `user-media-vexx.json` | Canary media |
| `tools/run-title.ps1` | Fixed-budget blocker-trace runner |

---

*Checklist only. Soft-disable implementation and code deletion are separate PRs after canary PASS.*
