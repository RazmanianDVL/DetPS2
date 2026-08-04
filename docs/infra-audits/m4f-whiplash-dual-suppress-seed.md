# M4-f seed — Whiplash dual-suppress interaction (PreferIopRp × RAM plant)

**Status:** root-cause **SEED** — investigation brief, not a retirement ticket  
**Date:** 2026-08-04  
**Mode:** read-only. **No Core / GameQuirks code changes** in this note.  
**Title:** Whiplash (USA) `SLUS_206.84`  
**Assist (product default, plants still live):** `src/DetPS2.Core/GameQuirks/WhiplashAssist.cs`  
**Related:** M4-b tag-if-applied (`RealSifRpc` `LF_F_GET_VERSION`); M4-c Haven / M4-d Vexx / M4-e Whip A/B evidence;  
`docs/UDNL_GETVERSION_UNIFICATION.md` §4–§6; `docs/infra-audits/m8a-haven-vexx-retirement-checklist.md` (Whip **out of scope** for quiet retire)

---

## 1. Problem statement

M4-e ran **one-axis** soft-suppress A/B on Whiplash version debt:

| Axis | Soft-off | Result vs product baseline @100M claim |
|------|----------|----------------------------------------|
| **Stage A** | `PreferIopRpGetVersion` **off** only (RAM plant **still on**) | **Byte-identical** to baseline |
| **Stage B** | RAM version plant **off** only (`PreferIopRp` **still on**) | **Byte-identical** to baseline |
| **Dual** | PreferIopRp **off** **and** plant **off** | **Not** byte-identical — small, **deterministic** metric drift |

**Dual-suppress delta (M4-e, reproduced twice):**

| Metric | Baseline (both on) | Dual off | Δ |
|--------|--------------------|----------|---|
| syscalls / calls | 3175 | 3344 | **+169** |
| cdvdSectors | 1463 | 1461 | **−2** |
| binds | (baseline) | same | **0** |
| exitRequested / menuHeuristic | (baseline) | same | **0** |

**Critical negative result:** under dual suppress, `LF_F_GET_VERSION` still returns packed **`"2550"`** with `preferIopRp=False` in TRACE_RPC — i.e. **not** an S2 / M4-b GetVersion failure. Tag-if-applied from UDNL/RESET extract (`IOPRP255` → `"2550"`) is live.

**Why M4-f exists:** Haven (M4-c) and Vexx (M4-d) could treat PreferIopRp / plant as **independently** redundant for the version gate. Whip dual-suppress shows **coupling**: each axis alone is a no-op at claim metrics, but **both off** moves the EE/HLE work volume. Quiet retirement of Whip PreferIopRp + plant is **blocked** until this interaction is attributed — not “assumed harmless +169.”

**Plants in tree:** product default remains PreferIopRp + `PlantIopRpVersion` + host0→cdrom arg rewrite. M4-e soft plants were **REVERTED**; do **not** re-land permanent quiet retire in `WhiplashAssist` under this ticket.

---

## 2. Evidence matrix (M4-e only — evidence, not product flags)

Budget / media class: **100M claim**, `user-media-whiplash.json`, same tip as M4-e run pair.  
UsingCD patches, PreferSnFileIo, host0 arg rewrite, WaitSema/JREXIT residual: **unchanged** across stages (version axes only).

| Config | PreferIopRp | RAM plant `"2550"` | GET_VERSION | Claim metrics vs baseline |
|--------|-------------|--------------------|-------------|---------------------------|
| Product default | ON | ON | `"2550"` | baseline (syscalls≈3175, cdvd≈1463, …) |
| Stage A | **OFF** | ON | `"2550"` (preferIopRp=False) | **byte-identical** |
| Stage B | ON | **OFF** | `"2550"` | **byte-identical** |
| Dual suppress | **OFF** | **OFF** | `"2550"` (preferIopRp=False) | syscalls **3175→3344**, cdvd **1463→1461**; binds/exit/menu **unchanged** |
| Dual re-run | OFF | OFF | `"2550"` | **same deltas** (deterministic) |

### 2.1 What this rules out

| Claim | Status |
|-------|--------|
| “Dual fail = GetVersion fell back to classic `0x00020000`” | **Ruled out** — TRACE still packs `"2550"` with PreferIopRp false |
| “PreferIopRp still required for LOADFILE GetVersion after M4-b” | **Ruled out for Whip** — Stage A + dual both preferIopRp=False + tag present |
| “Plant alone is load-bearing at claim” | **Ruled out** — Stage B byte-identical |
| “PreferIopRp alone is load-bearing at claim” | **Ruled out** — Stage A byte-identical |
| “Noise / non-determinism” | **Ruled out** — dual delta reproduced twice |

### 2.2 What remains open

- **Why** dual off adds **+169** syscalls/calls and **−2** cdvd while binds and menu heuristic hold.
- Whether dual path is **benign extra work** (retry loops that still converge) or a **latent wall** that grows at longer budgets / pad / residual paths.
- Whether FILEIO (still PreferIopRp-gated in places) or BSS cell timing is the coupler (see §3).

---

## 3. Code anchors (read path only)

### 3.1 `WhiplashAssist` version surface

| Site | Behavior |
|------|----------|
| `OnDiscMounted` | `PreferIopRpGetVersion = true`; `PreferSnFileIo = true`; `PlantIopRpVersion` |
| `PlantIopRpVersion` | Cells `0x00421718`, `0x00421720` ← `"2550"` if `"...."` or zero |
| `Step` | Re-assert PreferIopRp + PreferSnFileIo after reboot clears; re-plant while `_versionPlanted`; host0→cdrom arg rewrite (≤4); UsingCD EE patches |
| UsingCD / arg rewrite | **Media/S0 debt** — not the M4-e suppress axes; keep on for dual A/B honesty |

### 3.2 `RealSifRpc` GetVersion / reboot

| Path | PreferIopRp role after M4-b |
|------|----------------------------|
| `OnIopReboot(arg)` | Extract `IOPRP255` → `_lastIopRpVersionAscii = "2550"` (independent of PreferIopRp) |
| `HandleLoadFile` `LF_F_GET_VERSION` | **Tag-if-applied:** pack tag if non-empty and not `DETPS2_GETVERSION_CLASSIC`; PreferIopRp **not** required |
| FILEIO fno=`0xFF` GetVersion | **Still** gated on `PreferIopRpGetVersion` for packed ASCII in current Core (unlike LOADFILE) — candidate coupler |
| FILEIO-2200 arm | PreferIopRp + numeric tag ≥3000; Whip tag `"2550"` should **not** arm; PreferSnFileIo hard-blocks 2200 |

### 3.3 UDNL unification retirement note (Whip)

From `docs/UDNL_GETVERSION_UNIFICATION.md` plant map / §6.2:

| Title | Plant to retire | Pass if |
|-------|-----------------|---------|
| Whip | `"2550"` cells + PreferIopRp + **arg rewrite** | Retail cdrom UDNL; no host0 rewrite; E1–E2 |

M8-a checklist explicitly: **BO2 / B3 / GoW / Whip require their own A/B** — Whip dual-suppress is that seat’s blocking finding before any quiet Prefer/plant delete.

---

## 4. Hypotheses (ordered)

Ordered by fit to **dual-only** drift + intact GetVersion `"2550"` + binds/menu hold.

### H1 — FILEIO GetVersion still PreferIopRp-gated (LOADFILE is not) **[TOP]**

**Idea:** EE / SN stack may probe **FILEIO** GetVersion (fno `0xFF`) or a dual-layout path when BSS cells are still `"...."`. LOADFILE already returns `"2550"` without PreferIopRp (explains Stage A / dual TRACE). FILEIO packing still needs PreferIopRp today → classic dword when Prefer off.

| Config | Cells early | FILEIO GetVersion | Outcome |
|--------|-------------|-------------------|---------|
| Stage A | Plant fills | Prefer off → classic, but plant short-circuits EE | baseline |
| Stage B | Empty until store | Prefer on → FILEIO packs `"2550"` if probed | baseline |
| Dual | Empty until store | Prefer off → FILEIO classic **or** mismatch branch | **extra syscalls** (+169), eventual LOADFILE path still OK |

**Fits:** dual-only; GetVersion LOADFILE still `"2550"`; small cdvd wobble (retry open/read cadence).  
**Falsify:** TRACE_RPC / FILEIO trace with dual suppress shows **zero** FILEIO GetVersion; or FILEIO also tag-if-applied and dual delta remains.

### H2 — BSS memcmp before GetVersion store; recovery path Prefer-sensitive

**Idea:** Title memcmp cells `0x421718` / `0x421720` (or copies) **before** LOADFILE result is written. Plant makes first check pass. Without plant, a short retry/wait loop runs; that loop’s syscall mix or SIF cadence is slightly different when PreferIopRp is also false (FILEIO layout branch, re-Init, or Step no longer re-asserting Prefer — even if LOADFILE is tag-if-applied).

**Fits:** Stage B alone OK if Prefer keeps some secondary path hot; dual adds retries.  
**Falsify:** EE PC trace shows no pre-store cell read; or dual PC band identical to Stage B through first GetVersion.

### H3 — PreferIopRp × plant only as **stability couple** on Step noise / thrash assist timing

**Idea:** Continuous `PlantIopRpVersion` in `Step` and Prefer re-assert interact with post-CD_NCMD WaitSema pulse / FlushCache rescue timing. One axis alone keeps EE memory or RPC flags “warm”; both off shifts when thrash escapes fire → +syscalls, −2 cdvd, same binds.

**Fits:** residual-heavy Whip assist; non-version residual still on.  
**Falsify:** dual suppress with Step plant/Prefer blocks no-op’d but thrash paths frozen shows same +169 (then not thrash); or dual with thrash soft-off removes delta (then thrash coupler, not version).

### H4 — Harmless convergence (extra work, same terminal state)

**Idea:** Dual off takes a longer legal path to the same menu class; +169 is pure overhead, not a bug. Safe to quiet-retire **after** longer budget (200M+) and pad/residual hold prove no divergence growth.

**Fits:** binds/exit/menuHeuristic unchanged.  
**Falsify:** +Δ grows with cycle budget; or lit/px/imgBytes/residual metrics diverge past noise; or dual hits a new wall after 100M.

### H5 — Arg rewrite / UsingCD interaction (low priority for **this** delta)

**Idea:** host0 rewrite or UsingCD force couples to version suppress. Unlikely: M4-e kept those on; dual axes were Prefer + plant only.

**Falsify:** same dual delta with UsingCD/arg rewrite forced identical and logged equal reboot args.

---

## 5. Proposed investigation steps (M4-f execution)

No product quiet-retire until attribution. Prefer **env soft-suppress** temporary plants (revert after), same as M4-e.

### 5.1 EE PC / syscall delta attribution

1. **Baseline vs dual @100M** with:
   - `DETPS2_TRACE_RPC=1`
   - `DETPS2_TRACE_REBOOT=1`
   - optional syscall histogram / `blocker-trace` PC samples at fixed cycle marks (1M, 2M, 5M, 10M, 20M, 50M, 100M).
2. Diff:
   - Count of `LF_F_GET_VERSION` vs FILEIO GetVersion.
   - First time cells `0x421718` / `0x421720` become `"2550"` (memory peek at marks).
   - Syscall id histogram: which numbers account for **+169**.
3. **Stage A / Stage B control traces** at same marks — prove single-axis identity holds mid-run, not only final claim line.

### 5.2 FILEIO isolation (H1)

1. Temporary dual suppress + TRACE that logs FILEIO fno `0xFF` result and PreferIopRp.
2. Optional experiment (**throwaway, revert**): make FILEIO GetVersion use tag-if-applied like LOADFILE; re-run dual — if +169 collapses, H1 confirmed; **do not** land as Whip-only hack without design note (FILEIO-2200 / Midway PreferSnFileIo interaction).

### 5.3 Cell timing isolation (H2)

1. Dual suppress + one-shot plant **only at** first LOADFILE GetVersion completion (or only after OnIopReboot) — not continuous Step re-plant.
2. If dual becomes byte-identical → continuous re-plant / early cell was the Stage B compensator; document residual S4 mirror need vs natural store order.

### 5.4 Budget growth (H4)

1. Dual vs baseline @ **200M** (and optional pad script): does Δsyscalls stay ~constant or grow?
2. Capture lit/px/imgBytes/binds/cdvd/exit — any new residual wall?

### 5.5 PreferSnFileIo control (sanity)

1. Dual suppress **must leave** PreferSnFileIo **on** (Crystal Dynamics SN FILEIO).  
2. Do **not** conflate version dual-suppress with SN layout off.

---

## 6. Non-goals

| Non-goal | Why |
|----------|-----|
| Quiet retire PreferIopRp / plant in `WhiplashAssist` | Blocked until dual attributed; M4-e plants already reverted |
| Delete PreferSnFileIo, UsingCD patches, host0 rewrite, WaitSema/JREXIT, Host→Local | Out of M4 version debt; S0 media / residual seats |
| Change SM / fleet default GetVersion policy | `DETPS2_GETVERSION_CLASSIC` / M4-b stays |
| M8-a-style “soft-off default green” for Whip | Dual is not green-identical |
| Core FILEIO tag-if-applied land without Midway A/B | H1 experiment only until designed |
| Push / Core PR from this seed | Docs only |

---

## 7. Acceptance for closing M4-f

M4-f closes when **one** of the following is true and written up:

| Close path | Required proof |
|------------|----------------|
| **A. Root cause named** | Dual +169 attributed to a specific path (e.g. FILEIO GetVersion Prefer gate, pre-store memcmp retry, thrash timing) with TRACE/PC evidence |
| **B. Safe no-op** | Dual suppress byte-identical **or** Δ proven bounded + non-growing @≥200M with binds/exit/menu/residual hold; then **separate** quiet-retire ticket may soft-off Prefer+plant |
| **C. Residual plant justified** | Dual proves plant **or** Prefer still load-bearing for a non-GetVersion reason → leave product default; document “not T10 Whip yet” in UDNL §6.2 row |

**Minimum evidence pack for close:**

1. Dual vs baseline claim table (syscalls, cdvd, binds, exit, menuHeuristic, GET_VERSION line).  
2. Attribution note (H1/H2/H3/H4 or new) with at least one falsified alternative.  
3. Explicit **go / no-go** for quiet Prefer+plant retire (default **no-go** until A or B).

M4-f does **not** require MENU YES / lit growth; residual chrome is out of scope.

---

## 8. Open questions

1. Does Whip EE ever call **FILEIO** GetVersion on the post-IOPRP255 path, or only LOADFILE?
2. Does the title **read** version cells before writing GetVersion result, and from which PC?
3. Is +169 entirely in EE HLE syscall counters, or does it include inflated “calls” from SIF RPC telemetry naming?
4. Why **−2** cdvd only — aborted sector pair vs shifted open order?
5. Does dual delta appear **before** first MOD_LOAD of disc IRX or only after GOE/RKV warm?
6. After H1 fix (if any), does Stage-B-style plant suppress alone remain byte-identical at 200M+?
7. Should Whip T10 order be **Prefer first** (already Stage A green) then plant, or **never dual** until FILEIO GetVersion unified with LOADFILE?

---

## 9. Suggested soft-suppress envs (investigation only)

Illustrative names — match whatever temporary plant M4-e used; **default product = both on**.

| Env | Effect |
|-----|--------|
| `DETPS2_M4F_WHIP_NO_PREFER_IOPRP=1` | Skip PreferIopRp assign in OnDiscMounted/Step (PreferSnFileIo **stays**) |
| `DETPS2_M4F_WHIP_NO_VERSION_PLANT=1` | Skip `PlantIopRpVersion` mount + Step re-plant |
| Both set | Dual suppress — the M4-f subject |
| `DETPS2_TRACE_RPC=1` / `DETPS2_TRACE_REBOOT=1` | Required for GetVersion / tag extract proof |

After each A/B seat: **revert** Core/assist changes; leave this doc as the standing root-cause brief.

---

## 10. References

| Path | Role |
|------|------|
| `src/DetPS2.Core/GameQuirks/WhiplashAssist.cs` | PreferIopRp, plant cells, Step, UsingCD, arg rewrite |
| `src/DetPS2.Core/RealSifRpc.cs` | `OnIopReboot`, `LF_F_GET_VERSION` tag-if-applied, FILEIO GetVersion Prefer gate |
| `docs/UDNL_GETVERSION_UNIFICATION.md` | S2 / T10 / Whip plant row |
| `docs/infra-audits/m8a-haven-vexx-retirement-checklist.md` | Quiet retire process; Whip excluded |
| `docs/title-ports/WHIPLASH.md` | Claim class / residual charter |
| `docs/infra-audits/gamequirks-infra-debt.md` | Version plant class debt |

---

## 11. One-line summary

**Whip dual-suppress is not a failed GetVersion gate** (`"2550"` still packs with PreferIopRp false); it is a **PreferIopRp × RAM-plant coupling** that only appears when **both** are off (+169 syscalls, −2 cdvd, binds/menu hold). Attribute before any quiet retire.
