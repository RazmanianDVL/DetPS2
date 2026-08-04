# M4-g design — FILEIO GetVersion tag-if-applied (mirror LOADFILE M4-b)

**Status:** design only (ready for implement ACK) — **no Core change in this note**  
**Date:** 2026-08-04  
**Tip ref:** `5f0941f` (windows-detps2 / detps2)  
**Mode:** infra-only. **No GameQuirks edits. No title PC plants. No push.**  
**Tracks:** M4 GetVersion unification S2 residual (FILEIO path); M4-f H1 fix candidate; unblocks M8-a-class Whip quiet-retire seat  
**Related:**  
`docs/UDNL_GETVERSION_UNIFICATION.md` §4.3 / §5–§6  
`docs/infra-audits/m4f-whiplash-dual-suppress-seed.md` (H1 TOP)  
`docs/infra-audits/m8a-haven-vexx-retirement-checklist.md` (Whip out of M8-a; follow-on after M4-g)  
**Owned code (future PR):** `src/DetPS2.Core/RealSifRpc.cs` only — `HandleFileIo` case `FioGetVersion` packing branch  
**Out of scope:** BO2 version plants / PreferIopRp / arg rewrite (**M4-h**); FILEIO-2200 ABI rewrite; PreferSnFileIo retirement; live FILEIO IRX

---

## 0. One-line summary

**LOADFILE** `LF_F_GET_VERSION` already packs applied IOPRP/DNAS ASCII when `_lastIopRpVersionAscii` is non-empty **without** `PreferIopRpGetVersion` (M4-b). **FILEIO** fno=`0xFF` still Prefer-gates that pack. M4-g unifies the **reply dword only**; **do not** touch PreferSnFileIo or FILEIO-2200 arming.

**Recommended land shape:** **default-on** tag-if-applied for FILEIO packing (mirror M4-b), kill-switched by existing **`DETPS2_GETVERSION_CLASSIC=1`** (shared with LOADFILE). Optional separate `DETPS2_FILEIO_GETVERSION_CLASSIC` only if dual ACK demands FILEIO-only bisect.

---

## 1. Problem statement

### 1.1 Asymmetry after M4-b

| Path | Site | Packs ASCII tag when? |
|------|------|------------------------|
| **LOADFILE** `LF_F_GET_VERSION` (`0xFF`) | `HandleLoadFile` | Tag non-empty **and** not `DETPS2_GETVERSION_CLASSIC` — **PreferIopRp not required** (M4-b) |
| **FILEIO** fno=`0xFF` GetVersion | `HandleFileIo` case `FioGetVersion` | Tag non-empty **and** `PreferIopRpGetVersion == true` — **still Prefer-gated** |

Both share the same tag store (`_lastIopRpVersionAscii` from `OnIopReboot` / `SetIopRpVersionAscii`) and the same pack helper (`PackAsciiVersion`). Only the **policy predicate** diverges.

### 1.2 Why this matters (M4-f H1)

Whip dual-suppress (PreferIopRp **off** + RAM plant **off**) is **not** a failed LOADFILE GetVersion gate: TRACE still packs `"2550"` with `preferIopRp=False`. Claim metrics still drift:

| Metric | Baseline (both on) | Dual off | Δ |
|--------|--------------------|----------|---|
| syscalls / calls | ~3175 | ~3344 | **+169** |
| cdvdSectors | ~1463 | ~1461 | **−2** |
| binds / exit / menuHeuristic | hold | hold | 0 |

M4-f ranks **H1 — FILEIO GetVersion still Prefer-gated** as the top coupler hypothesis: with cells empty and Prefer off, a FILEIO (or dual-layout) probe can still see classic `0x00020000` while LOADFILE already returns `"2550"`, driving a short retry/syscall band.

**Throwaway evidence (context, not product):** a temporary FILEIO tag-if-applied patch collapsed the Whip dual +169; do **not** re-land as a Whip-only hack — land as shared S2 residual with Midway A/B.

### 1.3 Explicit non-goals / non-scope

| Item | Owner / note |
|------|----------------|
| BO2 PreferIopRp / `"2340"` cells / arg rewrite | **M4-h** — BO2 does **not** depend on FILEIO GetVersion for its version gate in the same way; out of M4-g scope |
| Quiet-delete `PreferIopRpGetVersion` property | T10 after all title seats |
| PreferSnFileIo off / SN layout change | DA/Dec GAMER.OVL; Midway SN FILEIO |
| FILEIO-2200 arm rule change | SotC Play! vs SN false-arm; keep Prefer-gated arm |
| RAM plant retirement for Whip | Separate quiet-retire **after** dual A/B green post-M4-g |
| Haven / Vexx M8-a | Already policy-only / soft-off path; M4-g is FILEIO residual for dual titles |

---

## 2. Exact code site — packing vs classic vs 2200

**File:** `src/DetPS2.Core/RealSifRpc.cs`  
**Method:** `HandleFileIo`  
**Case:** `FioGetVersion` (`private const uint FioGetVersion = 0xFF`)

The case body does **two independent jobs**. M4-g edits **only job B**.

### 2.1 Job A — FILEIO-2200 Init capture / arm (**DO NOT CHANGE**)

```text
if PreferSnFileIo:
    _fio2200Armed = false          // hard disarm (Midway / Whip SN layout)
else if send has dual EE pointers (rp0, rp1 both IsEeRamPointer):
    store _fio2200ResultPtr0/1
    if PreferIopRpGetVersion && TryParseIopRpVersionNumber ≥ 3000:
        _fio2200Armed = true       // Play! / SotC-class Init only
else SN-shaped single pointer:
    log only; do not arm
```

**Invariants (must remain after M4-g):**

| Rule | Why |
|------|-----|
| PreferSnFileIo **always** forces `_fio2200Armed = false` on this path | DA/Dec/Arm/Whip SN clients; false 2200 → broken OVL / open ABI |
| Dual EE pointers alone do **not** arm without Prefer + iopVer ≥ 3000 | BO2 `"2340"` / SN dual-buffer shapes must not auto-arm |
| Arm is **not** driven by “tag non-empty alone” | UDNL §4.3: decouple GetVersion ASCII from 2200 layout |

### 2.2 Job B — GetVersion reply dword (**M4-g ONLY**)

**Current (Prefer-gated):**

```csharp
// ~RealSifRpc.cs case FioGetVersion tail
if (PreferIopRpGetVersion && !string.IsNullOrEmpty(_lastIopRpVersionAscii))
    return PackAsciiVersion(_lastIopRpVersionAscii);
return 0x00020000;
```

**LOADFILE mirror (already landed M4-b):**

```csharp
// HandleLoadFile case LfGetVersion
result = !GetVersionClassicOverride && !string.IsNullOrEmpty(_lastIopRpVersionAscii)
    ? PackAsciiVersion(_lastIopRpVersionAscii)
    : 0x00020000;
```

### 2.3 Shared helpers (read-only for this ticket)

| API | Role |
|-----|------|
| `_lastIopRpVersionAscii` | Tag store; filled by `OnIopReboot(arg)` extract or `SetIopRpVersionAscii` |
| `ExtractIopRpVersionAscii` | `IOPRPxxx` / `DNASxxx` → 4-char (`255`→`"2550"`) |
| `PackAsciiVersion` | 4 ASCII LE dword |
| `GetVersionClassicOverride` | env `DETPS2_GETVERSION_CLASSIC=1` / `true` |
| `PreferIopRpGetVersion` | Legacy title opt-in; still used by **Job A arm** and assists; **must stay** for 2200 |
| `PreferSnFileIo` | SN layout + hard 2200 block; **must stay** |

---

## 3. Proposed policy

### 3.1 Packing rule (FILEIO = LOADFILE)

When answering FILEIO fno=`0xFF` **result dword**:

1. If `GetVersionClassicOverride` → **`0x00020000`** (classic).  
2. Else if `_lastIopRpVersionAscii` non-empty → **`PackAsciiVersion(tag)`**.  
3. Else → **`0x00020000`**.

**PreferIopRpGetVersion is not consulted for packing** after M4-g.

This matches UDNL §4.1 authority order and §4.3 unification rule:  
**“GetVersion returns IOPRP ASCII” ≠ “arm FILEIO-2200”.**

### 3.2 Kill-switch strategy

| Option | Env | Scope | Recommendation |
|--------|-----|--------|----------------|
| **A. Shared classic (recommended)** | `DETPS2_GETVERSION_CLASSIC=1` | LOADFILE **and** FILEIO packing | **Default choice** — one bisect knob; SM / panic already use it for M4-b |
| **B. FILEIO-only classic** | e.g. `DETPS2_FILEIO_GETVERSION_CLASSIC=1` | FILEIO packing only | Only if dual ACK needs FILEIO-only rollback without undoing LOADFILE M4-b |
| PreferIopRp false | (title assist soft-off) | Does **not** force classic packing after M4-g | Correct; Prefer becomes no-op for packing, still gates 2200 arm |

**Do not** introduce a third global “always pack digits” force for production.

### 3.3 Default-on vs kill-switched default

| Decision | Choice | Rationale |
|----------|--------|-----------|
| **Product default** | **ON** (tag-if-applied when tag non-empty) | Mirrors M4-b; throwaway Whip dual fix; SM/homebrew stay classic when extract empty |
| **Emergency off** | `DETPS2_GETVERSION_CLASSIC=1` | Shared with LOADFILE; no new fleet flag required |
| **2200 arm default** | **Unchanged** (Prefer + ≥3000 + dual ptrs; PreferSn hard block) | Protect DA/Dec SN layout |

**Recommended land:** **default-on packing**, **shared classic kill-switch**, **zero change to PreferSnFileIo / 2200 arm**.

---

## 4. Explicit NON-change list

Implementers **must not** touch the following under M4-g unless a **separate** dual-ACK ticket expands scope with Midway + SotC A/B:

| Surface | File / site | Why frozen |
|---------|-------------|------------|
| `PreferSnFileIo` property + all readers | `RealSifRpc`, `MidwayFamilyAssist`, `WhiplashAssist` | SN open/read/eeReply ABI; hard 2200 disarm |
| FILEIO-2200 arm predicate | `FioGetVersion` dual-pointer branch: `PreferIopRp && iopVer >= 3000` | SotC needs arm; BO2/SN must not false-arm |
| Open path 2200 decode gates | `FioOpen` PreferSn / `LooksLikeSnFioWrapper` / `TryDecodeFio2200Open` | Layout selection, not version reply |
| `_fio2200ResultPtr0/1` capture | same case, before packing | Play! Init bookkeeping |
| LOADFILE GetVersion policy | `HandleLoadFile` `LfGetVersion` | Already M4-b; only ensure kill-switch remains shared |
| GameQuirks Prefer / plant / arg rewrite | `WhiplashAssist`, Midway, BO2, … | Quiet-retire is **post** M4-g A/B, not this PR |
| BO2 version debt | M4-h | BO2 not FILEIO-GetVersion-scoped for this seat |
| `OnIopReboot` tag extract | leave as-is | Publisher already shared |

---

## 5. Implementation sketch (minimal diff)

### 5.1 Diff intent (~3 lines of behavior + comments/trace)

In `HandleFileIo` case `FioGetVersion`, **after** the PreferSn / 2200 Init block, replace packing only:

```csharp
// BEFORE (Prefer-gated packing):
if (PreferIopRpGetVersion && !string.IsNullOrEmpty(_lastIopRpVersionAscii))
    return PackAsciiVersion(_lastIopRpVersionAscii);
return 0x00020000;

// AFTER (M4-g: tag-if-applied, mirror LOADFILE M4-b):
// PreferIopRpGetVersion is intentionally NOT consulted for the reply dword.
// FILEIO-2200 arming above still uses PreferIopRp + iopVer>=3000.
// DETPS2_GETVERSION_CLASSIC=1 forces classic for LOADFILE and FILEIO packing.
int fioVer = !GetVersionClassicOverride && !string.IsNullOrEmpty(_lastIopRpVersionAscii)
    ? PackAsciiVersion(_lastIopRpVersionAscii)
    : 0x00020000;
if (Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
{
    Console.Error.WriteLine(
        $"[FILEIO] GET_VERSION result=0x{unchecked((uint)fioVer):X8} " +
        $"ioprp=\"{_lastIopRpVersionAscii}\" preferIopRp={PreferIopRpGetVersion} " +
        $"sn={PreferSnFileIo} fio2200={_fio2200Armed}");
}
return fioVer;
```

### 5.2 Comment hygiene

Update the stale comment above the packing branch that says *“Same PreferIopRpGetVersion gate as LOADFILE”* — LOADFILE no longer uses that gate. Point at this doc + UDNL §4.3.

Optional: one-line doc note on `PreferIopRpGetVersion` property that it remains load-bearing for **FILEIO-2200 arm**, not for GetVersion packing (LOADFILE or FILEIO).

### 5.3 What not to “simplify”

- Do **not** rewrite arm to `!GetVersionClassicOverride && tag && iopVer>=3000` without SotC + DA A/B (out of scope).  
- Do **not** clear PreferIopRp assignments from assists in the same PR.  
- Do **not** change `PackAsciiVersion` / extract.  
- Do **not** special-case Whip serial in Core.

### 5.4 Optional FILEIO-only kill-switch (ACK gate)

If dual ACK requires option B:

```csharp
static bool FileIoGetVersionClassicOverride =>
    GetVersionClassicOverride
    || env DETPS2_FILEIO_GETVERSION_CLASSIC in {1, true};
```

Default product still ON when both envs unset.

---

## 6. Validation plan

Run after land (same media maps / budgets as existing seats). Prefer env soft-suppress for Whip dual; **no permanent assist delete** in the implement PR.

### 6.1 Smokes / unit surface

| Check | Expect |
|-------|--------|
| Existing LoadFile GetVersion smoke (no image reboot) | Still classic `0x00020000` |
| FILEIO RPC smoke (`FILEIO fno` matrix if present) | Open/read/close unchanged; fno=`0xFF` classic without tag |
| Full smoke matrix / CI green | No PreferSn / 2200 regressions from packing-only change |
| Synthetic: set tag via reboot arg, PreferIopRp **false**, call FILEIO `0xFF` | Packed tag (new); classic under `DETPS2_GETVERSION_CLASSIC=1` |

### 6.2 Whip dual-suppress A/B (M4-f exit path A)

Media: `user-media-whiplash.json`, claim **100M** (repeat dual @200M if 100M green for H4 bound).  
Axes (investigation envs — names from M4-f seed; PreferSnFileIo **always on**):

| Config | PreferIopRp | Plant `"2550"` | Expect after M4-g |
|--------|-------------|----------------|-------------------|
| Product baseline | ON | ON | Unchanged metrics band |
| Stage A | OFF | ON | Still ~byte-identical to baseline |
| Stage B | ON | OFF | Still ~byte-identical |
| **Dual** | OFF | OFF | **Target:** syscalls/cdvd **return to baseline** (or Δ bounded & non-growing); TRACE FILEIO GET_VERSION packs `"2550"` with preferIopRp=False |

Instrumentation: `DETPS2_TRACE_RPC=1`, `DETPS2_TRACE_REBOOT=1`. Count FILEIO vs LOADFILE GetVersion lines; confirm dual no longer returns FILEIO classic.

**If dual +169 remains with FILEIO packing fixed:** H1 falsified → document; do **not** quiet-retire Whip Prefer+plant; re-open M4-f H2/H3.

### 6.3 DA + Deception diagnose (SN layout hold)

| Title | Serial | Assist flags that must stay on | Pass |
|-------|--------|--------------------------------|------|
| MK: Deadly Alliance | `SLUS_204.23` | PreferIopRp + **PreferSnFileIo** | Version gate + SN FILEIO opens (GAMER.OVL path class); **no** false 2200 arm in TRACE (`PreferSnFileIo — FILEIO-2200 disarmed` or never armed) |
| MK: Deception | `SLUS_208.81` | same | same |

Budget: existing diagnose / claim class used for Midway FILEIO seats. Fail if open/lseek/close-only OVL probe returns (classic false-2200 failure mode).

### 6.4 Optional SM canary

| Title | Expect |
|-------|--------|
| Shaolin Monks `SLUS_210.87` | No PreferIopRp; spine not regressed. If extract empty → classic FILEIO+LOADFILE. If gen storm re-extracts digits, shared classic override still available. |

Run only if dual ACK flags SM risk; M4-b already default-on for LOADFILE with SM canary history.

### 6.5 Optional sanity (not blockers)

| Seat | Note |
|------|------|
| SotC | PreferIopRp still on → 2200 arm path unchanged; packing already had ASCII under Prefer |
| Haven / Vexx soft-off | Should remain green; FILEIO may not be on their hot path |
| B3 | PreferIopRp **off** product; if FILEIO GetVersion is probed with DNAS tag present, packing now returns `"2800"` without Prefer — watch LGDEV thrash cadence once; rollback via classic env if needed |

---

## 7. Exit criteria — Whip quiet-retire after land (M8-a-class follow-on)

M4-g **lands Core packing**. Whip PreferIopRp + plant **quiet-retire is a separate checklist** (not M8-a Haven/Vexx; name e.g. **M8-a Whip** / T10 Whip row).

### 7.1 Preconditions to open Whip quiet-retire

| ID | Criterion |
|----|-----------|
| **W1** | M4-g merged; FILEIO packing = tag-if-applied + classic kill-switch |
| **W2** | Whip dual-suppress @100M: metrics **byte-identical** to baseline **or** Δ proven bounded + non-growing @≥200M with binds/exit/menu/residual hold (M4-f close path B) |
| **W3** | TRACE under dual: both LOADFILE and FILEIO GetVersion pack `"2550"` with preferIopRp=False when probed |
| **W4** | PreferSnFileIo / UsingCD / host0 rewrite **not** in retire set (media/S0 residual) |
| **W5** | DA/Dec diagnose green post-M4-g (fleet safety) |

### 7.2 Quiet-retire order (after W1–W5)

One axis at a time (same discipline as M8-a):

1. Soft-off PreferIopRp only (Stage A already green pre-M4-g — reconfirm post-M4-g).  
2. Soft-off plant only.  
3. Dual soft-off → must match W2.  
4. Only then delete assignments / plant calls from `WhiplashAssist` (or leave env soft-off as permanent default-off).

### 7.3 Do not claim under M4-g alone

- MENU YES / lit growth  
- host0 arg rewrite retirement (S0)  
- WaitSema / JREXIT residual delete  
- PreferSnFileIo off  

---

## 8. Open questions for dual ACK

Resolve **before** or **with** implement PR ACK. Defaults in parentheses are author recommendation.

| # | Question | Options | Rec |
|---|----------|---------|-----|
| **Q1** | Default-on packing vs env-gated first land? | (a) default-on + classic kill-switch (b) `DETPS2_FILEIO_GETVERSION_TAG=1` opt-in first fleet week | **(a)** — mirror M4-b; throwaway already proved Whip dual |
| **Q2** | Shared vs FILEIO-only classic kill-switch? | (a) `DETPS2_GETVERSION_CLASSIC` only (b) add FILEIO-only env | **(a)**; add (b) only if LOADFILE must stay tag-on during FILEIO bisect |
| **Q3** | Must Whip dual A/B be **in** the implement PR evidence pack, or post-merge canary? | (a) block merge on dual green (b) land + canary within 24h | **(a)** preferred if media available; else (b) with automatic classic rollback plan |
| **Q4** | B3 with Prefer off: is new FILEIO ASCII pack a thrash risk? | (a) accept + watch (b) require B3 50M A/B before merge | **(a)** unless historical B3 FILEIO GetVersion probe proven; then (b) |
| **Q5** | Should 2200 arm later move to “dual ptrs + iopVer≥3000 without Prefer”? | out of M4-g | **No** this ticket — separate design + SotC/DA A/B |
| **Q6** | If dual +169 **persists** after packing fix? | (a) hold Whip retire; reopen M4-f H2/H3 (b) still soft-off Prefer if Stage A green | **(a)** — dual is the retire bar |
| **Q7** | TRACE_RPC line required in product or debug-only? | always behind TRACE_RPC | TRACE only (as sketched) |

**ACK needed on Q1–Q3** before Core land; Q4–Q7 can default as recommended.

---

## 9. Risk matrix

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| DA/Dec false 2200 | **Low** if PreferSn + arm predicate untouched | Non-change list + DA/Dec diagnose |
| SM spine regression | Low (empty extract → classic) | `DETPS2_GETVERSION_CLASSIC`; no Prefer on SM |
| B3 thrash earlier | Low–med | Watch; classic kill-switch |
| Whip dual still +169 | Med (H1 may be incomplete) | W2 gate; do not quiet-retire |
| Scope creep into BO2 plants | Process | M4-h explicit; this PR RealSifRpc packing only |

---

## 10. References

| Path | Role |
|------|------|
| `src/DetPS2.Core/RealSifRpc.cs` | `FioGetVersion` packing + 2200 arm; `LfGetVersion` M4-b mirror; `GetVersionClassicOverride` |
| `docs/UDNL_GETVERSION_UNIFICATION.md` | §4.3 FILEIO coupling; §5 flags; §6 exit tables |
| `docs/infra-audits/m4f-whiplash-dual-suppress-seed.md` | Dual +169; H1 FILEIO Prefer gate |
| `docs/infra-audits/m8a-haven-vexx-retirement-checklist.md` | Quiet-retire process; Whip excluded |
| `docs/bios-ports/FILEIO.md` | FILEIO port gaps (context) |
| `src/DetPS2.Core/GameQuirks/WhiplashAssist.cs` | PreferIopRp + PreferSnFileIo + plant (post-land retire only) |
| `src/DetPS2.Core/GameQuirks/MidwayFamilyAssist.cs` | PreferSnFileIo fleet safety |

---

## 11. Implement PR checklist (copy into PR body)

- [ ] Diff limited to `RealSifRpc.HandleFileIo` case `FioGetVersion` **packing** (+ TRACE + comment fix)  
- [ ] PreferSnFileIo / 2200 arm lines **byte-identical**  
- [ ] Smokes green  
- [ ] Whip dual A/B table (baseline / A / B / dual) attached or linked  
- [ ] DA + Dec diagnose note (SN hold / no false 2200)  
- [ ] Optional SM note  
- [ ] No GameQuirks deletes  
- [ ] Dual ACK Q1–Q3 recorded in PR description  

---

## 12. Deliverable decision (this design)

| Item | Decision |
|------|----------|
| **Doc path** | `docs/infra-audits/m4g-fileio-getversion-tag-if-applied.md` |
| **Code change this task** | **None** (design-only) |
| **Recommended product default** | **Default-on** tag-if-applied for FILEIO GetVersion packing when `LastIopRpVersionAscii` non-empty |
| **Kill-switch** | **`DETPS2_GETVERSION_CLASSIC=1`** (shared with LOADFILE M4-b) |
| **Not default-changed** | PreferSnFileIo, FILEIO-2200 arm, PreferIopRp property, GameQuirks |
| **Whip quiet-retire** | **After** M4-g + dual A/B green (W1–W5); not part of packing PR |
