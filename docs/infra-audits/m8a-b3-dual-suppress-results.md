# M8-a Burnout 3 — dual-suppress / plant-quiet results

**Date:** 2026-08-04  
**Tip:** `2701533` (+ local GameQuirks / SonyKernelHle gates; no push)  
**Budget:** **diagnose (20M)** via `scoreboard-metrics` + `--host-present`  
**Media:** `burnout-only.json` → `C:/Users/xxraz/Downloads/Burnout3Takedown.iso` (**present**)  
**Seed:** `docs/infra-audits/m8a-b3-gow-evidence-seed.md`  
**Build:** Release → `out/scoreboard-build`

---

## 1. Scope

| Title | Fleet id | Serial | Assist | Product Prefer | Product plant (pre-canary) |
|-------|----------|--------|--------|----------------|----------------------------|
| Burnout 3: Takedown | `burnout-3` | `SLUS_210.50` | `Burnout3Assist` | **OFF** (deliberate residual→STG) | **ON** (`"2800"` @ `0x004B22C0` + ptr cell) |

**Dual-suppress meaning for B3** (not Prefer-on + plant-on both product axes):

| Axis | Baseline (B0) | Dual arm (B2) |
|------|---------------|---------------|
| RAM plant `"2800"` | ON | OFF (`DETPS2_M8A_B3_NO_VERSION_PLANT=1` during evidence) |
| PreferIopRp | product (assist never sets; LITERAL may auto-set) | held OFF (`DETPS2_M8A_B3_HOLD_PREFER_OFF=1` + SonyKernelHle skip auto-set) |

Also ran **B1 plant-only** suppress (plant OFF, no Prefer hold).

---

## 2. Gates landed

### 2.1 Evidence → product soft-off (plant)

| Env | Evidence run | After canary (product) |
|-----|--------------|------------------------|
| `DETPS2_M8A_B3_NO_VERSION_PLANT` | `=1` skip plant; **unset = plant ON** | **soft-off default:** unset / any value except `0`/`false` → **skip plant**; `=0` or `false` → **opt back in** (Vexx/Whip style) |

Sites gated: `OnDiscMounted` + Step one-shot post-ELF re-plant (`PlantIopRpVersion` — ptr cell `0x00484224` + ASCII `"2800"` placeholder `0x004B22C0`).

### 2.2 Prefer hold (evidence only; product Prefer still assist-off)

| Env | Effect |
|-----|--------|
| `DETPS2_M8A_B3_HOLD_PREFER_OFF=1` | `Burnout3Assist` forces `PreferIopRpGetVersion=false` on mount + every Step; `SonyKernelHle.OnIopRebootCompleted` **skips** LITERAL Prefer auto-set |

**Not** flipped to soft-off product: Prefer was already intentionally OFF at assist level; LITERAL auto-set remains product-on for other titles unless this env is set.

---

## 3. Verdict

| Field | Value |
|-------|-------|
| ISO availability | **Present** — ran |
| Baseline status | **RAN** exit 0, `exitRequested=false` |
| Dual-suppress status | **RAN** exit 0, `exitRequested=false` |
| Plant-only suppress status | **RAN** exit 0, `exitRequested=false` |
| Scoreboard-metrics identity | **Byte-identical** baseline ↔ dual ↔ plant-only (SHA256 `98681F3E…E3F3E3` all three JSON) @ **20M diagnose** |
| MENU claim? | **No** — diagnose only; T0=Y T1=Y? G1=Y? (px class, not MENU YES) |

**Honest read:** at diagnose budget, suppressing the EE RAM `"2800"` plant (and additionally holding Prefer off / neutralizing LITERAL Prefer auto-set) does **not** change observed scoreboard metrics vs product plant-on. Plant is not load-bearing for IRX-era floor @ 20M under current M4-b/M4-g tag-if-applied GetVersion.

**Caveat:** diagnose (20M) is pre-LGDEV residual force window (~22M). Seed §5.2 residual cadence / claim-class gates are **not** re-proven here. Soft-off is justified for plant quiet at diagnose identity; claim (100M) optional follow-up if residual cadence is questioned.

---

## 4. Summary table (baseline → dual / plant-only)

| Arm | status | exitReq | syscalls | calls | binds | cdvd | px | prims | dmac | sifBytes | PC | identity |
|-----|--------|---------|----------|-------|-------|------|-----|-------|------|----------|-----|----------|
| baseline (plant ON) | RAN | F | 806 | 42 | 11 | 425 | 877187 | 172 | 20 | 22780 | `0x00123E84` | — |
| dual (plant OFF + Prefer hold) | RAN | F | 806 = | 42 = | 11 = | 425 = | 877187 = | 172 = | 20 = | 22780 = | `0x00123E84` = | **byte-identical** |
| plant-only (plant OFF) | RAN | F | 806 = | 42 = | 11 = | 425 = | 877187 = | 172 = | 20 = | 22780 = | `0x00123E84` = | **byte-identical** |

Also identical: `gifP3=20`, `expandHits=6`, `gifCompleted=92`, `imgBytes=65728`, tiers `T0=Y T1=Y? T3=Y? G1=Y? G2=Y`, `exitCode=0`, `liveRpcHits=0`.

Wall times ~2.9–3.0 s per arm (informational only).

Artifacts:

```text
out/canaries/m8a-b3-dual-suppress/20260804-105508/
  baseline/{burnout-3-metrics.json,out.txt,err.txt}
  dual-suppress/{…}
  plant-suppress/{…}
  summary.json
```

---

## 5. Product follow-through (this seat)

Because plant suppress was **clean byte-identical** at diagnose:

1. **Landed:** default **soft-off** `SkipVersionPlant` (Vexx/Whip/BO2 pattern). Unset → plant skipped; `DETPS2_M8A_B3_NO_VERSION_PLANT=0` rollback.
2. **Left as-is:** Prefer still assist-OFF (no Prefer soft-off product change). `HOLD_PREFER_OFF` remains investigation-only.
3. **Residual assists stay on:** LGDEV residual, flip CreditOwed, STAGEHED/FRONTEND plants, pad — not in this seat.

---

## 6. Commands (repro)

```powershell
# Repo root; Release Core
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/scoreboard-build --nologo -v q
$dll = "out/scoreboard-build/DetPS2.Core.dll"
$cycles = 20000000

# --- Evidence shape (pre soft-off product): plant ON baseline ---
# With current soft-off product default, opt plant back in for B0:
$env:DETPS2_M8A_B3_NO_VERSION_PLANT = "0"
Remove-Item Env:DETPS2_M8A_B3_HOLD_PREFER_OFF -ErrorAction SilentlyContinue
dotnet exec $dll scoreboard-metrics burnout-only.json --cycles=$cycles --out=out/b3-plant-on.json --host-present

# Dual suppress under soft-off product: plant already off; Prefer hold still env
Remove-Item Env:DETPS2_M8A_B3_NO_VERSION_PLANT -ErrorAction SilentlyContinue
$env:DETPS2_M8A_B3_HOLD_PREFER_OFF = "1"
dotnet exec $dll scoreboard-metrics burnout-only.json --cycles=$cycles --out=out/b3-dual.json --host-present
Remove-Item Env:DETPS2_M8A_B3_HOLD_PREFER_OFF -ErrorAction SilentlyContinue
```

Evidence run that produced this doc used **pre-soft-off** gate semantics (`=1` suppress, unset plant ON) at stamp `20260804-105508`.

---

## 7. Sign-off (diagnose)

```text
M8-a B3: plant suppress @diagnose(20M)
  scoreboard-metrics: BYTE-IDENTICAL baseline vs plant-only vs dual (Prefer hold)
  binds=11 calls=42 cdvd=425 syscalls=806 pc=0x00123E84
  SifLoadModule gate: no IRX abort class (binds/calls/cdvd hold vs baseline)
  LGDEV residual cadence: NOT measured (budget < 22M force window)
  Prefer: assist remains OFF; HOLD_PREFER_OFF evidence-only
  Product: SkipVersionPlant soft-off default (rollback =0)
  PASS @diagnose — claim residual optional follow-up
```

---

## 8. Follow-ups (not done)

1. Optional **claim (100M)** A/B for LGDEV residual / residual→STG cadence under plant soft-off.  
2. TRACE_RPC FILEIO vs LOADFILE GetVersion counts (seed open Q §3).  
3. GoW Prefer+plant dual seat (separate; not this doc).  
4. No mass-delete of `PlantIopRpVersion` method body until claim-class accepted if desired — soft-off is enough for quiet product path.  
5. No push from this seat.

---

*Results + soft-off land. Prefer left product-off at assist; LITERAL Prefer auto-set unchanged unless HOLD_PREFER_OFF.*
