# M4-S4 design — EE RAM IOPRP version-cell mirror (post-UDNL tag)

**Status:** design only (ready for dual ACK) — **no Core implement this turn**  
**Date:** 2026-08-04  
**Mode:** infra-only. **No GameQuirks deletes. No title PC escape as primary fix. No push.**  
**Tracks:** M4 GetVersion unification **S4** (EE RAM consumer class B); residual after M4-b / M4-g RPC packing; unblocks GoW plant quiet-retire when plant-off + mirror-on is green  
**Related:**  
`docs/UDNL_GETVERSION_UNIFICATION.md` §1 (A vs B), §3.1 **S4**, §5.2 `DETPS2_MIRROR_IOPRP_CELLS`  
`docs/infra-audits/m4g-fileio-getversion-tag-if-applied.md` (FILEIO packing residual — **done design / separate path**)  
`docs/infra-audits/m8a-gow-dual-suppress-results.md` (GoW Prefer free; plant load-bearing)  
`docs/infra-audits/m8a-b3-dual-suppress-results.md` (B3 plant soft-off @ diagnose)  
`docs/infra-audits/gamequirks-infra-debt.md` theme #1  

**Owned code (future PR, not this seat):** shared Core mirror helper + registry (recommended owners below)  
**Out of scope:** GoW-only hardcode of `"3000"` in Core; FreezeCache residual; arg-rewrite (S0); live LOADFILE IRX (S5); mass plant delete without A/B

---

## 0. One-line summary

**M4-b / M4-g** make LOADFILE + FILEIO **GetVersion RPC** return the applied IOPRP/DNAS ASCII tag without `PreferIopRpGetVersion`. That is **consumer class A**. Several titles still **memcmp / load EE BSS cells** that real UDNL + client store would fill (`"...."` → `"3000"` / `"2800"` / …). That is **consumer class B**. GoW dual-suppress proved Prefer free and **plant load-bearing** at diagnose: without `"3000"` at `0x002C6D30`, the gate at `0x00298A10` fails → FreezeCache path. **S4** is a shared, flag-gated **EE RAM mirror** of the same applied tag into a small registry of cells — not a new GetVersion path and not a GoW-only PC escape.

**Recommended land shape (after dual ACK):** **default-off** mirror + **opt-in** registry population from title serial table (or env), kill-switch hard-off, never invent digits from serial alone; tag source = `_lastIopRpVersionAscii` only after real apply / extract.

---

## 1. Problem class vs GetVersion RPC path

### 1.1 Two consumers (same truth, different surfaces)

| Class | Surface | Who checks | M4 stage | Status |
|-------|---------|------------|----------|--------|
| **A. GetVersion RPC** | LOADFILE `LF_F_GET_VERSION` (`0xFF`) + FILEIO fno=`0xFF` reply dword | SotC / Haven / Midway / BO2 gate / Whip FILEIO | **S2** (M4-b, M4-g) | **Done as policy** — tag-if-applied; Prefer free for packing |
| **B. EE RAM version cells** | BSS / `.data` `"...."` placeholders, sometimes via ptr cell | GoW, B3, BO2, Vexx, Whip | **S4** (this doc) | **Open** — still per-title `PlantIopRpVersion` |

Shared ground truth (already published by Core):

```text
SifIopReset("rom0:UDNL …IOPRPxxx / DNASxxx…")
  → ApplyUdnlHandoff / OnIopReboot
  → RealSifRpc._lastIopRpVersionAscii = ExtractIopRpVersionAscii(arg)
       e.g. IOPRP300 → "3000", DNAS280 → "2800", IOPRP234 → "2340"
```

| Path | What the game does | What DetPS2 must provide |
|------|--------------------|---------------------------|
| **A** | `sceSifCallRpc` GetVersion → memcmp reply vs rodata | Packed 4 ASCII LE when tag non-empty (M4-b/g) |
| **B** | Load cell / `*ptr` and memcmp vs expected digits **without** always re-running store from GetVersion first | Tag bytes in EE RDRAM at title-known addresses |

**Class B is not a PreferIopRp bug.** Prefer only ever gated the **RPC reply**. Plants write **memory**. Unifying GetVersion does not fill `0x002C6D30`.

### 1.2 GoW plant-class evidence (canonical S4 driver)

From `GodOfWarAssist` + `m8a-gow-dual-suppress-results.md`:

| Item | Value |
|------|--------|
| Serial | `SCUS_973.99` |
| Disc image / tag | `IOPRP300` → **`"3000"`** |
| Placeholder cell | **`0x002C6D30`** (`"...."` until filled) |
| Gate PC | **`0x00298A10`** memcmp vs `"3000"` / version pointer |
| Fail path | writes **`0xFFFEFFFC`** @ `0x0029C4DC` → FreezeCache spin |
| PreferIopRp @ diagnose | **Not load-bearing** (Stage A Prefer-off ≡ baseline) |
| RAM plant @ diagnose | **Load-bearing** (plant-off → cdvd 136→0, calls 21→12) |

**Honesty:** Prefer soft-off is justified. Quiet-retire of GoW plant is **blocked** until class B is served by shared mirror (or title naturally stores GetVersion into the cell before memcmp — not observed under plant-off diagnose).

### 1.3 Why not “just fix GetVersion earlier”

| Approach | Why insufficient as primary |
|----------|-----------------------------|
| M4-b/g only | Title may never copy RPC result into the BSS cell before memcmp; cell stays `"...."` |
| Force PreferIopRp | Already free for GoW packing; does not write EE RAM |
| GoW-only plant forever | Works but grows GameQuirks; multi-title same class |
| GoW-only Core hardcode `"3000"` @ fixed VA | Violates S4 rule: **no per-title digit constants in Core**; no serial→tag invent |
| Title PC escape (skip memcmp / force FreezeCache clear as primary) | Secondary residual only; does not restore version contract |

### 1.4 Non-identity with FILEIO-2200

FILEIO-2200 **arm** still uses Prefer + iopVer ≥ 3000 (M4-g freeze list). **S4 must not** change 2200 arming. Mirror is pure EE `Write8` of 4 ASCII bytes into registered cells.

---

## 2. Current per-title plants inventory

Grep surface: `PlantIopRpVersion` / `IopVersion*` / PreferIopRp in  
`C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\*.cs`  
(+ policy-only rows for contrast).

### 2.1 Legend

| Kind | Meaning |
|------|---------|
| **RAM plant** | Assist writes 4 ASCII digits over `"...."` / zero (class B) |
| **Ptr plant** | Assist also rewrites a pointer cell → placeholder |
| **RPC Prefer** | `PreferIopRpGetVersion = true` (class A policy; mostly redundant after M4-b/g packing) |
| **Force tag / UDNL** | `SetIopRpVersionAscii` / forced `ApplyUdnlHandoff` when reboot arg empty |
| **Policy-only** | Prefer only — **no** RAM plant (best practice for class A titles) |
| **Classic stay** | Must keep GetVersion classic when no image tag (SM) |

### 2.2 Fleet table (version-related)

| Serial | Title | Assist | Tag | Kind | EE cells / notes | Prefer product | Plant product |
|--------|-------|--------|-----|------|------------------|----------------|---------------|
| `SCUS_973.99` | God of War | `GodOfWarAssist` | `"3000"` | **RAM + Prefer soft-off + force tag/UDNL residual** | **`0x002C6D30`**; memcmp `0x00298A10`; FreezeCache `0x0029C4DC` | Soft-off (free) | **ON load-bearing** |
| `SLUS_210.50` | Burnout 3 | `Burnout3Assist` | `"2800"` | **RAM + ptr** (Prefer intentionally OFF) | Placeholder **`0x004B22C0`**; ptr **`0x00484224`** → placeholder; rodata expect `0x0048414C` | OFF | Soft-off default @ diagnose (identity) |
| `SLUS_200.24` | Blood Omen 2 | `BloodOmen2SnAssist` | `"2340"` | **RAM + Prefer soft-off + arg rewrite** | **`0x00536188`**, **`0x00536190`**; reboot buf `0x005361A0`; cells re-zeroed by game | Soft-off default | Soft-off env (`M8A_BO2_NO_VERSION_PLANT`) |
| `SLUS_203.83` | Vexx | `VexxAssist` | `"2520"` | **RAM + Prefer soft-off** | **`0x003D18B8`**, **`0x003D1938`**; Step re-plant if scrubbed | Soft-off default | Soft-off default |
| `SLUS_206.84` | Whiplash | `WhiplashAssist` | `"2550"` | **RAM + Prefer soft-off + host0 arg rewrite** | **`0x00421718`**, **`0x00421720`** (placeholder-only write) | Soft-off default | Soft-off default |
| `SCUS_974.72` | SotC | `TeamIcoAssist` | `"3000"` | **Policy-only** | Rodata expect `"3000"` @ `0x0013227C` — **no plant** | Prefer on | none |
| `SCUS_971.13` | Ico | `TeamIcoAssist` | (family) | **Policy-only** | none | Prefer on | none |
| `SLUS_205.17` | Haven | `TeamIcoAssist` | `"2500"` | **Policy-only** | none | Prefer on | none |
| `SLUS_204.23` / `208.81` / Arm | MK family | `MidwayFamilyAssist` | `"2430"`-class | **Policy-only** (+ PreferSn) | **no IOPRP RAM plant** | Prefer on | none |
| `SLUS_210.87` | Shaolin Monks | `MidwayBootAssist` | — | **Classic stay** | **Must not** global always-ASCII; no version plant | Prefer off | none |

### 2.3 Plant method signatures (implementers)

| Assist | Method | Write policy |
|--------|--------|--------------|
| `GodOfWarAssist.PlantIopRpVersion` | single cell | if word is `"...."` or 0 → write `"3000"` bytes |
| `Burnout3Assist.PlantIopRpVersion` | ptr + cell | if ptr 0/`"...."` → point at placeholder; if placeholder empty → `"2800"` |
| `BloodOmen2SnAssist.PlantIopRpVersion` | two cells | always write `"2340"` (game may zero; re-plant on reboot) |
| `VexxAssist.PlantIopRpVersion` | two cells | write `"2520"`; Step if `!VersionCellsOk` |
| `WhiplashAssist.PlantIopRpVersion` | two cells | write `"2550"` only if `"...."` or 0 |

### 2.4 Inventory conclusion for S4

- **5 titles** still own class-B RAM plants (GoW / B3 / BO2 / Vexx / Whip).  
- **GoW is the load-bearing canary** for mirror-on / plant-off.  
- **B3 plant soft-off is already quiet @ 20M diagnose** — mirror may be no-op there at that budget, still needed for claim-class residual or other titles.  
- Policy-only titles must **not** gain registry rows unless a future diagnose proves a BSS cell consumer.

---

## 3. Proposed Core mechanism

### 3.1 Intent

After UDNL/IOPRP apply publishes a non-empty tag, **optionally** mirror that tag’s 4 ASCII bytes into a **small registry of EE physical addresses** (and optional pointer cells). Core never hardcodes `"3000"` for GoW; Core writes **`LastIopRpVersionAscii`** wherever the registry says.

```text
OnIopReboot / SetIopRpVersionAscii / post-ApplyUdnl
        │
        ▼
  _lastIopRpVersionAscii = "3000" | "2800" | …
        │
        ▼  if mirror enabled && tag non-empty && !classic override
  IopRpEeMirror.Apply(sys, tag)
        │
        ├─ for each registered cell: write 4 ASCII if placeholder / always (policy)
        └─ for each registered ptr cell: ensure points at a registered buffer (optional)
```

### 3.2 API sketch (non-binding names)

Recommended new type (future PR):  
`src/DetPS2.Core/IopRpEeVersionMirror.cs` (or nested static on `RealSifRpc` if dual ACK prefers minimal files).

```csharp
// Illustrative — not landed this seat.
public sealed class IopRpEeVersionMirror
{
    public enum CellMode
    {
        /// Write only if current dword is 0 or "...." (GoW / Whip style).
        PlaceholderOrZero,
        /// Always write 4 ASCII (BO2 re-zero races).
        Always,
    }

    public readonly struct Cell
    {
        public uint PhysAddr;     // EE RDRAM phys, e.g. 0x002C6D30
        public CellMode Mode;
    }

    public readonly struct PtrCell
    {
        public uint PtrAddr;      // e.g. B3 0x00484224
        public uint TargetAddr;   // e.g. B3 0x004B22C0
    }

    // Registry for current title (cleared on Reset / disc change).
    List<Cell> Cells;
    List<PtrCell> Ptrs;

    public void Clear();
    public void RegisterCell(uint phys, CellMode mode = CellMode.PlaceholderOrZero);
    public void RegisterPtr(uint ptrAddr, uint targetAddr);

    /// Called when tag store updates (OnIopReboot extract, SetIopRpVersionAscii).
    public void MirrorIfEnabled(Ps2System sys, string tag4);
}
```

**Write helper (shared, digit-agnostic):**

```csharp
static void WriteAscii4(SystemMemory mem, uint addr, string tag4)
{
    // tag4 already validated length 4 ASCII digits from Extract / SetIop.
    for (int i = 0; i < 4; i++)
        mem.Write8(addr + (uint)i, (byte)tag4[i]);
}
```

**Placeholder test:** dword `0x2E2E2E2E` (`"...."`) or `0` — match existing assist gates.

### 3.3 When to fire mirror

| Hook | Role |
|------|------|
| `RealSifRpc.OnIopReboot` after tag extract non-empty | Primary — retail reboot gen |
| `RealSifRpc.SetIopRpVersionAscii` after store | GoW empty-arg residual path still updates tag |
| Optional: once post-ELF / early Step if tag already set and cells still empty | Covers titles that scrub cells before first reboot (BO2 zeros @ `0x48C9C8`) — **rate-limit**; prefer registry re-apply on reboot gen bump |
| **Do not** fire every 25k forever by default | Continuous re-plant is assist debt; Core should be **event-driven** + optional “re-apply if scrubbed” gated by env |

**Generation rule:** mirror only after tag is non-empty. Never write digits when extract empty (SM / homebrew / pre-image).

### 3.4 Tag authority (invariant)

Same as UDNL §4.1:

1. Applied IOPRP/DNAS extract from reboot arg / image name.  
2. Temporary `SetIopRpVersionAscii` only when S0 empty-arg residual remains (GoW).  
3. **Never** invent from serial (`SCUS_973.99` → `"3000"`) inside Core.  
4. `DETPS2_GETVERSION_CLASSIC=1` → **no mirror** (and classic RPC) for unified bisect.

### 3.5 Registry population (who knows addresses)

Addresses are **title-local knowledge** today. Core must not grow a permanent switch of magic VAs without a deliberate table. Options in §5.

**B3 ptr special case:** registry supports `PtrCell` so Core can restore `*0x484224 = 0x4B22C0` when broken, then write tag at target — same as assist, without digit constants.

### 3.6 Relationship to existing tag store

| API | Role after S4 |
|-----|----------------|
| `_lastIopRpVersionAscii` | Single source for RPC packing **and** EE mirror |
| `PackAsciiVersion` | RPC only |
| `PlantIopRpVersion` in assists | Safety net until plant-off A/B green with mirror on; then T10 delete |
| `PreferIopRpGetVersion` | FILEIO-2200 arm / legacy; **not** required for mirror |

---

## 4. Flag-gated, kill-switch, default-safe strategy

### 4.1 Recommended defaults

| Control | Default | Purpose |
|---------|---------|---------|
| **Mirror feature** | **OFF** | Default-safe: no EE writes for titles without registry rows; SM/homebrew untouched |
| **Registry** | empty until opt-in | No silent multi-title mutation |
| **Tag invent** | never | Serial never implies digits |
| **Classic kill-switch** | honors `DETPS2_GETVERSION_CLASSIC` | One knob freezes packing + mirror |

### 4.2 Env flags (names illustrative; dual ACK may rename)

| Env | Semantics | Recommendation |
|-----|-----------|----------------|
| `DETPS2_MIRROR_IOPRP_CELLS=1` | Enable mirror engine (still needs non-empty registry + tag) | **Primary opt-in** (matches UDNL §5.2 seed name) |
| `DETPS2_MIRROR_IOPRP_CELLS=0` / unset | Engine off | **Product default** until fleet A/B green |
| `DETPS2_GETVERSION_CLASSIC=1` | Classic RPC **and** skip mirror | Shared emergency |
| `DETPS2_MIRROR_IOPRP_FORCE=1` (optional) | Mirror even if cell not placeholder (Always mode global) | Debug only |
| `DETPS2_TRACE_MIRROR=1` | Log addr + tag + skip reason | Diagnose |

**Do not** default-on global mirror for all titles. Even with empty registry, prefer explicit enable so scoreboard canaries stay deterministic.

### 4.3 Post-canary product path (sequence)

1. Land Core mirror **OFF by default**, unit-tested.  
2. Opt-in registry for GoW only; run **plant-off + mirror-on** vs plant-on baseline.  
3. If green: product can enable mirror for GoW registry row **with plant soft-off**.  
4. Expand registry title-by-title; quiet-retire plants.  
5. Only after multi-title green consider default-on mirror **for registered cells only** (still empty registry = no-op).

### 4.4 Interaction with existing plant soft-off envs

| Env (existing) | Interaction |
|----------------|-------------|
| `DETPS2_M8A_GOW_NO_VERSION_PLANT=1` | Evidence: skip assist plant — use with mirror-on to prove S4 |
| `DETPS2_M8A_B3_NO_VERSION_PLANT` soft-off | Plant already quiet; mirror optional |
| `DETPS2_M8A_*_NO_PREFER_IOPRP` | Orthogonal — Prefer free for packing |

---

## 5. How titles opt in without long-term GameQuirks growth

### 5.1 Goal

Stop adding new `PlantIopRpVersion` methods. Grow a **data registry**, not imperative Step loops.

### 5.2 Options (pick one primary at dual ACK)

| Option | Mechanism | Pros | Cons |
|--------|-----------|------|------|
| **A. Static Core table by serial** | `Dictionary<string, Cell[]>` in `IopRpEeVersionMirror` or small `IopRpVersionCellTable.cs` | Simple; no assist code for new known cells | Core still lists VAs (acceptable if **data-only**, no digit strings) |
| **B. Assist registers once on mount** | `OnDiscMounted`: `sys.RealRpc.EeMirror.RegisterCell(0x2C6D30)` — **no write**, register only | Addresses stay in title file; Core owns write timing | Assists still exist for registration until table moves |
| **C. External JSON / user-media** | `versionCells: [0x2C6D30]` per media map | Zero Core growth for community titles | Tooling + trust; overkill for fleet of 5 |
| **D. Auto-discover** | Scan EE for `"...."` near strcmp | No | Too magic; false positives |

**Recommended long-term:** **A for the known five** (serial → addresses only; tag from apply) + **B as escape hatch** for experimental titles.  
**Recommended first land:** **B or A for GoW only** to minimize blast radius.

### 5.3 What opt-in must never do

- Register digit strings (`"3000"`) — only addresses + write mode.  
- Call `SetIopRpVersionAscii` from the mirror table.  
- Patch memcmp PC / FreezeCache as part of S4.  
- Enable PreferIopRp.

### 5.4 Retirement of GameQuirks plants

| Step | Action |
|------|--------|
| 1 | Mirror + registry green for title under plant-off |
| 2 | Soft-off plant default (pattern: B3 / Vexx / Whip) |
| 3 | Delete `PlantIopRpVersion` body / call sites (T10 / WP-34) |
| 4 | Leave PRESENT / INTERACTIVE / SECONDARY assists untouched |

### 5.5 Policy-only titles

SotC / Haven / Midway Prefer lines stay Prefer-only until Prefer is globally redundant; **do not** add phantom cells to “be thorough.”

---

## 6. Validation plan

**No permanent assist delete in the implement PR.** Use env plant suppress + mirror enable.

### 6.1 Unit / smoke (no ISO)

| Check | Expect |
|-------|--------|
| No registry + mirror on + tag set | No EE writes; smokes unchanged |
| Registry cell + tag `"3000"` + mirror on | Cell bytes `33 30 30 30`; placeholder policy respected |
| Tag empty + mirror on | No write |
| `DETPS2_GETVERSION_CLASSIC=1` | No write even if registry + prior tag |
| Existing `BiosUdnl_*` / LoadFile GetVersion smokes | Green; classic without image reboot |
| Full smoke matrix | Green |

### 6.2 GoW — plant-off with mirror on (primary exit)

| Arm | Prefer | Plant | Mirror | Expect (diagnose ≥20M) |
|-----|--------|-------|--------|-------------------------|
| G0 product | soft-off | ON | off | Baseline (cdvd 136 class, PC freeze-region success neighborhood) |
| G-plant-off | soft-off | OFF (`M8A_GOW_NO_VERSION_PLANT=1`) | off | **Known diverge** (cdvd→0) — control |
| **G-S4** | soft-off | OFF | **ON** + cell `0x002C6D30` registered | **≈ G0** metrics / no FreezeCache fail; cell reads `"3000"` after tag publish |
| G-S4 classic | any | OFF | ON + `GETVERSION_CLASSIC=1` | No fill; like plant-off (kill-switch proof) |

**Pass:** G-S4 scoreboard identity or claim-class non-worse vs G0 on gates that plant-off fails (cdvd, calls, sifBytes, PC not stuck early freeze constructor).  
**Fail:** G-S4 still cdvd 0 → either tag never published before memcmp (S0/arg) or cell/mode wrong.

Also verify: after successful apply, `Read32(0x002C6D30)` == little-endian `'3''0''0''0'`.

### 6.3 Other titles (secondary)

| Title | Plant soft-off env | Mirror cells | Note |
|-------|--------------------|--------------|------|
| B3 | already soft-off | `0x4B22C0` + ptr `0x484224` | Diagnose identity already without plant; claim 100M optional |
| Vexx | `M8A_VEXX_NO_VERSION_PLANT` | dual cells | Prefer already soft-off |
| Whip | `M8A_WHIP_NO_VERSION_PLANT` | dual cells | FILEIO packing is M4-g; cells residual |
| BO2 | `M8A_BO2_NO_VERSION_PLANT` | dual cells | May need Always mode + reboot re-apply (game zeros cells) |
| SM | — | **no registry** | Must stay classic; no mirror writes |

### 6.4 Negative fleet

| Title | Check |
|-------|--------|
| Haven / SotC | Mirror default off / empty registry → no EE surprise writes; Prefer path unchanged |
| SM spine | `GETVERSION_CLASSIC` + no image tag → no digits in RPC or RAM |

### 6.5 Trace evidence

With `DETPS2_TRACE_REBOOT=1` + `DETPS2_TRACE_MIRROR=1`:

```text
[RPC] OnIopReboot: ioprpVer="3000" arg="rom0:UDNL …IOPRP300…"
[MIRROR] write "3000" @ 0x002C6D30 mode=PlaceholderOrZero
```

---

## 7. Non-goals

| Non-goal | Why |
|----------|-----|
| **GoW-only PC escape as primary fix** | Skipping `0x00298A10` / forced FreezeCache clear papers over class B; FreezeCache clear remains **secondary** residual only |
| Hardcode `"3000"` / `"2800"` constants in Core | Tag must come from apply extract |
| Replacing M4-b/g GetVersion policy | RPC path stays; S4 is additive for class B |
| FILEIO-2200 arm changes | Frozen per M4-g |
| PreferIopRp retirement as this PR | Separate T10; Prefer already free for GoW packing |
| S0 arg fidelity (empty/host0/short-name) | Separate; GoW force SetIop/UDNL may remain until S0 |
| S5 live LOADFILE IRX | WP-22 long-term |
| Mass-delete plants without A/B | Soft-off first |
| Continuous Step re-plant in Core by default | Event-driven mirror |
| Magic auto-scan for `"...."` | Too risky |
| Implementing Core this design seat | **Design only unless dual ACK + trivial land approved** |

---

## 8. Open questions for dual ACK

| ID | Question | Options | Design bias |
|----|----------|---------|-------------|
| **Q1** | Default-off vs default-on (registered cells only)? | (a) default-off + `DETPS2_MIRROR_IOPRP_CELLS=1` (b) default-on when registry non-empty | **(a)** first land; flip to (b) after GoW G-S4 green |
| **Q2** | Registry ownership: static serial table (A) vs assist Register on mount (B)? | A / B / A+B | **B first** (GoW mount register-only) **or A for five known** if ACK wants zero assist churn |
| **Q3** | Should mirror re-apply on every reboot gen only, or also scrub-detect (BO2 zeros)? | event-only / event + cheap scrub on Step when registry non-empty | **event-only first**; BO2 Always mode + reboot re-apply; scrub Step only if BO2 fails |
| **Q4** | Empty-arg GoW: must S0 land before S4 can quiet-retire force SetIop/UDNL? | S4 only fills cell when tag exists — force tag may still be needed | **ACK:** S4 can prove plant-off if Ensure still sets tag; force-UDNL residual separate |
| **Q5** | B3: ship registry row if diagnose plant is already free? | skip B3 / include for claim safety | **include optional row**, low priority vs GoW |
| **Q6** | Shared kill-switch only (`GETVERSION_CLASSIC`) or dedicated `MIRROR_IOPRP_CELLS=0` force? | shared / dual | **both:** classic freezes all version surfaces; mirror env is feature gate |
| **Q7** | New file `IopRpEeVersionMirror.cs` vs methods on `RealSifRpc`? | new file / RealSifRpc | **new file** if >~80 lines; else RealSifRpc to minimize surface |
| **Q8** | Ptr-cell support in v1? | v1 cells only / v1 cells+ptr | **cells+ptr in v1** if B3 included; else cells-only and B3 later |
| **Q9** | Product soft-off GoW plant when G-S4 green at diagnose only, or wait claim 100M? | diagnose / claim | **diagnose identity first** for soft-off evidence; claim before MENU rhetoric |
| **Q10** | Savestate: must registry + last tag round-trip? | yes / defer | **defer** to A2 unless A/B hits reload mid-boot |

---

## 9. Implementation sketch (future PR only)

### 9.1 Touch list

| Area | Change |
|------|--------|
| New helper / registry | Mirror apply + cell/ptr register API |
| `RealSifRpc.OnIopReboot` / `SetIopRpVersionAscii` | Call `MirrorIfEnabled` after tag update |
| `Ps2System.Reset` / assist Reset | `mirror.Clear()` |
| GoW opt-in | Register `0x002C6D30` PlaceholderOrZero (table or mount) — **remove write from plant when soft-off** |
| Tests | Synthetic tag + fake memory cell; classic / empty tag negatives |
| Docs | Cross-link UDNL §3.1 S4 status when landed |

### 9.2 Minimal GoW-first land (if dual ACK wants smallest PR)

1. Env `DETPS2_MIRROR_IOPRP_CELLS=1` enables engine.  
2. Hard-coded **serial allowlist one row**: `SCUS_973.99` → `{0x002C6D30}` only (addresses, not digits).  
3. Fire from `OnIopReboot` / `SetIopRpVersionAscii`.  
4. Validate G-S4.  
5. Expand table; do not grow imperative plants.

### 9.3 Explicit non-diff

- No change to `HandleLoadFile` / `HandleFileIo` packing (already M4-b/g).  
- No PreferIopRp assignment.  
- No FreezeCache primary patch.  
- No UNC / emulator write outside local detps2 tree.

---

## 10. Definition of done (S4)

- [ ] Dual ACK on Q1–Q10 (or recorded deferrals).  
- [ ] Core mirror lands behind flag with unit coverage.  
- [ ] GoW **plant-off + mirror-on** ≈ plant-on baseline at agreed budget.  
- [ ] SM / empty-tag paths never receive invented digits.  
- [ ] At least one other plant title either green under plant-off+mirror or documented residual (mode/scrub).  
- [ ] TITLE_HACKS / UDNL unification note: “S4 EE mirror; plants retired for …”.  
- [ ] **This design seat:** document only — **no Core implement unless ACK marks trivial.**

---

## 11. References (absolute paths)

| Artifact | Path |
|----------|------|
| M4 unification seed (S4 row) | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\docs\UDNL_GETVERSION_UNIFICATION.md` |
| M4-g FILEIO packing | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m4g-fileio-getversion-tag-if-applied.md` |
| GoW dual-suppress | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m8a-gow-dual-suppress-results.md` |
| B3 dual-suppress | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m8a-b3-dual-suppress-results.md` |
| B3/GoW evidence seed | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m8a-b3-gow-evidence-seed.md` |
| Quirks debt | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\gamequirks-infra-debt.md` |
| GoW assist | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\GodOfWarAssist.cs` |
| B3 assist | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\Burnout3Assist.cs` |
| BO2 assist | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\BloodOmen2SnAssist.cs` |
| Vexx assist | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\VexxAssist.cs` |
| Whip assist | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\WhiplashAssist.cs` |
| RealSifRpc tag store | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\RealSifRpc.cs` |

---

*Design only. No Core code changes in this seat. Implement after dual ACK under M4 S4 / WP-26 residual.*
