# M8-a remaining — B3 + GoW PreferIopRp / version-plant evidence seed

**Status:** evidence **SEED** — investigation brief + dual-suppress harness proposal  
**Date:** 2026-08-04  
**Tip ref:** `def77d8` (windows-detps2 / detps2)  
**Mode:** docs only. **No permanent Core / GameQuirks changes. No push.**  
**Titles:**

| Title | Serial | Assist | Version tag (disc) |
|-------|--------|--------|--------------------|
| **Burnout 3: Takedown** | `SLUS_210.50` | `Burnout3Assist` | `DNAS280` → **`"2800"`** |
| **God of War** | `SCUS_973.99` | `GodOfWarAssist` | `IOPRP300` → **`"3000"`** |

**Related:**  
`docs/UDNL_GETVERSION_UNIFICATION.md` §2 plant map / §6.2 retirement rows  
`docs/infra-audits/m8a-haven-vexx-retirement-checklist.md` (Haven/Vexx; **explicitly excludes** B3/GoW)  
`docs/infra-audits/m4f-whiplash-dual-suppress-seed.md` (dual-axis pattern + FILEIO coupler)  
`docs/infra-audits/m4g-fileio-getversion-tag-if-applied.md` (**landed** — FILEIO packing now tag-if-applied)  
M4-b LOADFILE tag-if-applied (live in `RealSifRpc.HandleLoadFile`)

---

## 0. One-line summary

Haven/Vexx are the quiet-retire **checklist** seats. **B3 and GoW remain open** because they still own **EE RAM version plants** (B3 is plant-primary with Prefer **deliberately off**; GoW is Prefer + plant + post-empty-reboot `SetIopRpVersionAscii` / forced UDNL handoff). This seed inventories plant sites, proposes a **throwaway dual-suppress harness**, and states acceptance for quiet-retire — **not** a soft-off PR.

---

## 1. Grounding (what is already true in Core)

| Layer | Policy today | PreferIopRp required? |
|-------|--------------|----------------------|
| **LOADFILE** `LF_F_GET_VERSION` | M4-b **tag-if-applied**: pack `_lastIopRpVersionAscii` if non-empty and not `DETPS2_GETVERSION_CLASSIC` | **No** |
| **FILEIO** fno=`0xFF` GetVersion reply dword | M4-g **tag-if-applied** (same predicate as LOADFILE) | **No** for packing |
| **FILEIO-2200 Init arm** | PreferIopRp **and** numeric tag ≥ 3000; PreferSnFileIo hard-disarms | **Yes** for arm only |
| **LITERAL_IRX** post-reboot | `SonyKernelHle.OnIopRebootCompleted`: if literal IRX enabled **and** tag non-empty → `PreferIopRpGetVersion = true` | Auto-set (side channel) |

**Implication for evidence seats:** dual-suppress must control **all three** writers of Prefer and **all** RAM plant call sites — not only `OnDiscMounted`. LITERAL_IRX can re-assert Prefer after a real RESET even when the assist never set it (relevant for **B3**, which product-defaults Prefer off).

---

## 2. Grep inventory — PreferIopRp / plant sites (`GameQuirks`)

Absolute assist paths:

- `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\Burnout3Assist.cs`
- `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\GodOfWarAssist.cs`
- Registry: `GameQuirkRegistry.cs` — `SLUS_210.50` → B3; `SCUS_973.99` → GoW

### 2.1 Burnout 3 — **RAM plant only** (Prefer intentionally OFF)

| Site | What | Digits / cells |
|------|------|----------------|
| `OnDiscMounted` | `PlantIopRpVersion(sys)` only — **does not** set `PreferIopRpGetVersion` | Comment: Prefer=true advances LGDEV thrash ~18.6M and kills residual→STG cadence |
| `PlantIopRpVersion` | (1) `IopVersionPtrCell` `0x00484224` ← `0x004B22C0` if 0 / `"...."`; (2) placeholder `0x004B22C0` ← ASCII **`"2800"`** if `"...."` / 0 | Rodata expect `"2800"` @ `0x0048414C` is **not** written |
| `Step` (~≥500k, one-shot) | Re-plant after ELF PT_LOAD; `_versionPlanted = true` | TRACE_BIOS: `[B3] planted IOPRP version "2800"` |
| Step Prefer re-assert | **None** | Prefer left off for residual→STG |
| External Prefer writer | **LITERAL_IRX** `OnIopRebootCompleted` may set Prefer=true if tag extract non-empty | Must soft-hold Prefer **false** in dual harness if product B3 cadence is the baseline |

**Version gate (from assist header):** SifLoadModule @ `0x00113678` memcmp against `"2800"` and `*0x00484224 → 0x004B22C0`. Fail → `0xFFFEFFFC` → module load abort → SifInitIopHeap rebind thrash (binds climb, calls stuck, cdvd=0).

**Product default axes (B3):**

| Axis | Product |
|------|---------|
| PreferIopRp | **OFF** (deliberate) |
| RAM plant `"2800"` | **ON** (mount + post-ELF Step) |

So “dual-suppress” for B3 is **not** Prefer+plant both product-on → both off. It is: **suppress plant** while **holding Prefer off** (including blocking LITERAL auto-set / any accidental Prefer). Optional Stage A = Prefer forced ON + plant ON is a **regression probe**, not product dual.

### 2.2 God of War — **Prefer + plant + reboot force**

| Site | What | Digits / cells |
|------|------|----------------|
| `OnDiscMounted` | `PreferIopRpGetVersion = true`; `PlantIopRpVersion` | **Does not** `SetIopRpVersionAscii` early (live claim: early `"3000"` from cyc0 regressed binds 16→10 / dmac) |
| `PlantIopRpVersion` | Placeholder `0x002C6D30` ← ASCII **`"3000"`** if `"...."` / 0 | Single cell (no ptr rewrite) |
| `Step` (~≥500k, one-shot) | Re-plant after ELF PT_LOAD; `_versionPlanted` | TRACE_BIOS plant log |
| `Step` on `IopRebootGeneration` bump | Always `PlantIopRpVersion`; if reboot arg missing `IOPRP300`: **`EnsureIopRpGetVersion`** (`Prefer=true` + `SetIopRpVersionAscii("3000")`) + **`ApplyUdnlHandoff(UdnlIopRp300Arg)`** | Empty SifIopReset residual (~61M live) |
| `EnsureIopRpGetVersion` | Prefer re-assert + force tag store `"3000"` | LOADFILE/FILEIO surface without full `OnIopReboot` clear |
| FreezeCache clear (≥5M) | If flag `0x0029C4DC == 0xFFFEFFFC`: re-plant + clear flag | Version-mismatch error code |
| FreezeCache spin escape (`0x185F90..FA8`) | Re-plant + clear error + force PC `0x185FAC` | Memory clear alone cannot leave spin |

**Version gate (from assist header):** freeze-region constructor memcmp GetVersion cell / placeholder against `"3000"`. Fail → store `0xFFFEFFFC` @ `0x0029C4DC` → FreezeCache bltz spin @ `0x185F9x`.

**Product default axes (GoW):**

| Axis | Product |
|------|---------|
| PreferIopRp | **ON** (mount + Ensure on empty reboot) |
| RAM plant `"3000"` | **ON** (mount, post-ELF, reboot gen, FreezeCache paths) |
| `SetIopRpVersionAscii("3000")` | **Only** post-empty-reboot (not at mount) |
| Forced `ApplyUdnlHandoff(IOPRP300)` | **Only** when reboot arg lacks IOPRP300 |

### 2.3 Side channel (not in GameQuirks, must be in harness design)

| Site | Path | Effect |
|------|------|--------|
| LITERAL Prefer auto-set | `SonyKernelHle.OnIopRebootCompleted` | Prefer=true when literal IRX + tag non-empty |
| Tag extract | `RealSifRpc.OnIopReboot(arg)` | Fills `_lastIopRpVersionAscii` from retail arg (independent of Prefer after M4-b) |
| FILEIO-2200 arm | `HandleFileIo` FioGetVersion Job A | Prefer + iopVer≥3000 → arm; **GoW tag is 3000** → Prefer on can arm Play! layout if dual EE pointers appear |

---

## 3. FILEIO GetVersion — **open** (unknown for B3 / GoW)

M4-g **landed**: FILEIO packing is tag-if-applied (mirror LOADFILE). That removes the Whip-style Prefer×FILEIO packing coupler **if** these titles still probe FILEIO fno=`0xFF`.

| Question | Status |
|----------|--------|
| Does B3 EE call **FILEIO** GetVersion on the DNAS280 / IRX path? | **Unknown — open** |
| Does GoW EE call **FILEIO** GetVersion around FreezeCache / sound / FILEIO TOC? | **Unknown — open** |
| Does B3 gate rely on RAM cells only (SifLoadModule memcmp) vs LOADFILE store? | Gate is **cell-primary** per assist; whether LOADFILE/FILEIO also feed those cells is **open** |
| Does GoW memcmp RPC result buffer vs only `0x2C6D30`? | Assist documents both GetVersion and placeholder; exact first-reader order **open** |

**How to close (evidence only, TRACE):**

```powershell
$env:DETPS2_TRACE_RPC = "1"
$env:DETPS2_TRACE_REBOOT = "1"
# throwaway dual suppress (see §4) — do not land
pwsh ./tools/run-title.ps1 -Media burnout-only.json -Budget diagnose
pwsh ./tools/run-title.ps1 -Media user-media-god-of-war.json -Budget diagnose
```

Parse stderr for:

- `[LOADFILE] GET_VERSION … ioprp="2800"|"3000" preferIopRp=…`
- `[FILEIO] GET_VERSION … ioprp=… preferIopRp=…`
- Count of FILEIO vs LOADFILE GetVersion lines @ diagnose (20M) and claim (100M)

**Scoreboard optional:** `pwsh ./tools/scoreboard.ps1 -Budget diagnose -Titles burnout-3,god-of-war` with dual-suppress throwaway. **Skipped in this seed** (doc-only; dual suppress needs temporary assist gates — no permanent Core change). Re-run when soft-env plants land for investigation.

---

## 4. Proposed dual-suppress harness pattern

**Shape:** env soft-gates only (default **product behavior** = today’s assists). Investigation envs; **revert** after A/B. Pattern mirrors M4-e/M4-f Whip and M8-a Haven/Vexx soft-off names.

### 4.1 Env names (illustrative)

| Env | Title | Effect |
|-----|-------|--------|
| `DETPS2_M8A_B3_NO_VERSION_PLANT=1` | B3 | Skip `PlantIopRpVersion` on mount + Step (ptr cell + `"2800"`) |
| `DETPS2_M8A_B3_HOLD_PREFER_OFF=1` | B3 | After each Step (or post-reboot hook), force `PreferIopRpGetVersion = false` so LITERAL auto-set cannot arm Prefer during dual seat |
| `DETPS2_M8A_GOW_NO_PREFER_IOPRP=1` | GoW | Skip Prefer assign in `OnDiscMounted` + `EnsureIopRpGetVersion` |
| `DETPS2_M8A_GOW_NO_VERSION_PLANT=1` | GoW | Skip all `PlantIopRpVersion` call sites (mount, post-ELF, reboot gen, FreezeCache) |
| `DETPS2_M8A_GOW_NO_FORCE_TAG=1` | GoW | Skip `SetIopRpVersionAscii("3000")` inside Ensure |
| `DETPS2_M8A_GOW_NO_FORCE_UDNL=1` | GoW | Skip `ApplyUdnlHandoff(IOPRP300)` on empty reboot (**S0 axis** — stage separately) |
| Both Prefer + plant soft-off | GoW | **Dual suppress** (primary M8-a evidence) |
| `DETPS2_GETVERSION_CLASSIC=1` | global | Emergency classic `0x00020000` — **not** title dual |

### 4.2 Dual-suppress definition per title

| Title | “Dual” means | Stage order |
|-------|--------------|-------------|
| **B3** | Plant **off** + Prefer **held off** (LITERAL neutralized) | **B0** baseline (plant on, Prefer off) → **B1** plant suppress only → **B2** plant suppress + Prefer hold (same as B1 if LITERAL unset) → optional **B-pref** Prefer forced **on** + plant on (cadence regress probe) |
| **GoW** | Prefer **off** + plant **off** | **G0** baseline → **G-A** Prefer off, plant on → **G-B** Prefer on, plant off → **G-dual** both off → **G-force** dual + `NO_FORCE_TAG` / `NO_FORCE_UDNL` staged |

### 4.3 LITERAL_IRX auto-set + plant sites + Prefer re-assert in Step

Harness checklist (must all be soft-gated for an honest dual):

1. **LITERAL_IRX Prefer auto-set** — either run A/B with `DETPS2_LITERAL_IRX=0` / force HLE bisect, **or** B3 Prefer-hold / GoW Prefer soft-off that re-clears Prefer after reboot gen (Step tail). Document which product path is baseline (IRX default vs HLE bisect).
2. **All plant sites** — B3: mount + Step 500k. GoW: mount + Step 500k + reboot-gen re-plant + FreezeCache re-plant (two FreezeCache branches).
3. **Prefer re-assert in Step** — B3: none product; only external LITERAL. GoW: `EnsureIopRpGetVersion` on missing IOPRP300 reboot — must soft-off with Prefer gate.
4. **Tag store force** — GoW `SetIopRpVersionAscii` is a third axis (not Prefer, not RAM plant). Stage after dual Prefer×plant.

**Do not** soft-off LGDEV residual, flip CreditOwed, STAGEHED/FRONTEND host plants (B3), or BST/heap/worker/stream residuals (GoW) under this harness — version axes only.

### 4.4 Minimal throwaway patch sketch (investigation; not this PR)

```text
Burnout3Assist.OnDiscMounted / PlantIopRpVersion callers:
  if (env M8A_B3_NO_VERSION_PLANT) skip plant
Step end (optional):
  if (env M8A_B3_HOLD_PREFER_OFF) rpc.PreferIopRpGetVersion = false

GodOfWarAssist.OnDiscMounted:
  if (!env M8A_GOW_NO_PREFER) Prefer = true
  if (!env M8A_GOW_NO_PLANT) Plant…
EnsureIopRpGetVersion:
  if (env M8A_GOW_NO_PREFER) return early (or skip Prefer only)
  if (env M8A_GOW_NO_FORCE_TAG) skip SetIopRpVersionAscii
PlantIopRpVersion static:
  if (env M8A_GOW_NO_PLANT) return
reboot ApplyUdnlHandoff:
  if (env M8A_GOW_NO_FORCE_UDNL) skip
```

After seat: **revert** assist diffs; leave this doc as standing brief.

---

## 5. Acceptance for quiet-retire (M8-a class)

Retire version debt only when **infra proof (E\*)** and **title fleet proof** hold. Aligns with `docs/UDNL_GETVERSION_UNIFICATION.md` §6.2.

### 5.1 Shared infra preconditions

| ID | Check |
|----|-------|
| **P1** | LOADFILE M4-b tag-if-applied live |
| **P2** | FILEIO M4-g packing tag-if-applied live (already) |
| **P3** | Retail reboot arg extracts tag without assist `SetIopRpVersionAscii` (B3: DNAS280→`"2800"`; GoW: IOPRP300→`"3000"`) — TRACE_REBOOT |
| **P4** | SM still green without PreferIopRp; classic override available |
| **P5** | Dual (or plant-suppress) metrics **not worse** than product baseline on version-relevant gates |

### 5.2 Burnout 3 — quiet-retire plant (Prefer may stay OFF)

| Metric / gate | Pass under plant suppress (+ Prefer held off) | Fail |
|---------------|-----------------------------------------------|------|
| SifLoadModule version gate | Passes; IRX list still binds (SIO2…GTFS…LGDEV class) | `0xFFFEFFFC` / binds climb / calls stuck pre-IRX |
| GetVersion TRACE | Packed `"2800"` from extract when image reboot applied (`preferIopRp` false OK) | Classic after DNAS reboot → gate fail |
| LGDEV residual cadence | Residual force @~22M window **not worsened** (n→STG still achievable class) | Prefer-on thrash pulled to ~18.6M, residual dies n=2–3, STG never binds |
| cdvd / binds / calls @ diagnose→claim | ≥ baseline IRX-era floor; no re-introduction of cdvd=0 post-gate | Regression to pre-plant IRX abort |
| Non-version residual | Flip CreditOwed, LGDEV stubs, STAGEHED/FRONTEND plants, pad **still on** | Do not “fix” version by deleting residual |

**Quiet-retire action (later PR):** delete or env-default-off `PlantIopRpVersion` only. **Do not** flip Prefer on as part of “unification cleanup.”

### 5.3 God of War — quiet-retire Prefer + plant (+ force tag/UDNL staged)

| Metric / gate | Pass under dual suppress | Fail |
|---------------|--------------------------|------|
| FreezeCache flag | Never holds `0xFFFEFFFC` through post-sound gate @ claim class | Flag stuck error / spin `0x185F9x` |
| GetVersion | Packed `"3000"` with `preferIopRp=False` after real IOPRP300 reboot | Classic / empty tag after retail arg |
| RAM cell `0x2C6D30` | Becomes `"3000"` via title store **or** gate advances without plant | Gate fails while plant suppressed |
| Ensure / forced UDNL | **Not required** for green (empty-arg S0 fixed or retail arg present) | Still need `SetIopRpVersionAscii` or forced handoff |
| binds / dmac | Not worse than early-SetIop regression (16→10 / 463→321 class) | Early tag force reintroduced |
| Non-version residual | BST/heap/worker/stream/R_SHELL **still on** | Residual delete as false green |

**Quiet-retire order (GoW):** Prefer soft-off → plant suppress → drop Ensure SetIop → drop forced UDNL (S0). Never jump to Core property delete.

### 5.4 Sign-off template

```text
M8-a B3: plant suppress @diagnose/claim
  GET_VERSION ioprp=2800 preferIopRp=False (or N/A if no probe)
  FILEIO GET_VERSION count=… (open closed)
  SifLoadModule gate=PASS IRX binds=… vs baseline
  LGDEV residual cadence hold=yes/no
  PASS / FAIL

M8-a GoW: Prefer OFF + plant suppress [@ + NO_FORCE_TAG]
  GET_VERSION ioprp=3000 preferIopRp=False
  FreezeCache error never / spin escape unused
  Ensure/SetIop/forced UDNL needed=yes/no
  binds/dmac vs baseline
  PASS / FAIL

Explicit: residual assists NOT retired under this seat.
```

---

## 6. Non-goals

| Non-goal | Why |
|----------|-----|
| Permanent Core / GameQuirks quiet-delete in this seed | Evidence only; no push |
| Haven/Vexx soft-off rework | Already M8-a checklist |
| Whip dual-suppress / M4-f close | Separate title |
| BO2 Prefer/plant/arg rewrite | M4-h / own A/B |
| Delete LGDEV residual, flip CreditOwed, STAGEHED/FRONTEND plants (B3) | Non-version INFRA / PRESENT |
| Delete BST/heap/worker/stream/R_SHELL / FreezeCache **non-version** escapes (GoW) | SECONDARY / PRESENT residual |
| Change SM GetVersion policy / always-ASCII | SM classic stay |
| FILEIO-2200 arm rule change | Prefer+≥3000 stays; GoW Prefer-off may **help** avoid false Play! arm |
| Global `PreferIopRpGetVersion` property removal | T10 after all readers gone |
| Require MENU YES / full lit growth for M8-a version retire | Version gate + IRX/FreezeCache class only |
| Land dual-suppress envs as product default without A/B | Soft-off first, default-on only after green |
| Scoreboard claim fleet as this seed’s acceptance | Optional diagnose later |

---

## 7. Open questions

1. **FILEIO GetVersion:** Do B3 and/or GoW ever call FILEIO fno=`0xFF` post-IOPRP, or only LOADFILE / pure RAM memcmp? (TRACE_RPC count — §3)
2. **B3 cell fill order:** Without plant, does the title ever store GetVersion result into `0x4B22C0` / via `*0x484224` before SifLoadModule memcmp, or is the plant load-bearing forever until S4 mirror?
3. **B3 × LITERAL_IRX:** With product Prefer off, does default IRX path auto-set Prefer and **already** shift LGDEV thrash vs the historical Prefer-off residual seat? Baseline must name LITERAL on/off.
4. **GoW empty reboot:** Is `LastIopRebootArg=""` still live @ tip, or did S0/media fixes make forced UDNL obsolete? Dual evidence should log reboot gen + arg.
5. **GoW FILEIO-2200:** With Prefer on + tag `"3000"`, does any path arm 2200? Dual Prefer-off should keep arm false — confirm no OVL-style regress (GoW is not Midway SN, but arm still changes FILEIO layout).
6. **FreezeCache non-version:** Can `0xFFFEFFFC` appear for non-version failures after dual green? If yes, keep FreezeCache clear as residual, retire only version plant/Prefer.
7. **Budget honesty:** Is diagnose (20M) enough to pass SifLoadModule (B3) / FreezeCache (GoW), or is verify/claim required for dual acceptance?
8. **M4-g impact:** With FILEIO packing unified, is B3/GoW dual expected to be **byte-identical** (unlike pre-M4-g Whip), or still cell-timing coupled (M4-f H2 class)?

---

## 8. File index

| Path | Role |
|------|------|
| `src/DetPS2.Core/GameQuirks/Burnout3Assist.cs` | `"2800"` plant; Prefer intentionally off |
| `src/DetPS2.Core/GameQuirks/GodOfWarAssist.cs` | Prefer + `"3000"` plant + Ensure + forced UDNL + FreezeCache |
| `src/DetPS2.Core/RealSifRpc.cs` | LOADFILE/FILEIO GetVersion tag-if-applied; FILEIO-2200 Prefer arm |
| `src/DetPS2.Core/SonyKernelHle.cs` | LITERAL_IRX Prefer auto-set on reboot completed |
| `docs/UDNL_GETVERSION_UNIFICATION.md` | Plant map; B3/GoW §6.2 rows |
| `docs/infra-audits/m8a-haven-vexx-retirement-checklist.md` | Quiet-retire process; B3/GoW out of scope there |
| `docs/infra-audits/m4f-whiplash-dual-suppress-seed.md` | Dual-axis methodology |
| `docs/infra-audits/m4g-fileio-getversion-tag-if-applied.md` | FILEIO packing (landed) |
| `docs/title-ports/BURNOUT_3.md` | Claim class / residual charter |
| `docs/title-ports/GOD_OF_WAR.md` | Title port notes |
| `burnout-only.json` / `user-media-god-of-war.json` | Media for canary |
| `tools/scoreboard.ps1` / `scoreboard-fleet.json` | diagnose=20M / verify=50M / claim=100M |

---

## 9. Next actions (ordered)

| # | Action | Owner class |
|---|--------|-------------|
| 1 | Throwaway soft-env gates in B3/GoW assists (default product) | Investigation PR or local only |
| 2 | TRACE_RPC dual seats @ diagnose; close FILEIO GetVersion open Q | Evidence |
| 3 | B3 plant-suppress vs baseline (Prefer held off) | Evidence |
| 4 | GoW Stage A / B / dual; then force-tag / force-UDNL off | Evidence |
| 5 | Write pass/fail into this doc or successor; only then quiet-retire PR | M8-a follow-on |
| 6 | **Do not** merge permanent plant delete without §5 PASS | Gate |

---

*Seed only. No Core/GameQuirks permanent changes. No push. Dual-suppress harness is proposed, not landed.*
