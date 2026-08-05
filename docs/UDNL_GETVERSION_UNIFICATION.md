# M4 design seed — UDNL / LOADFILE GetVersion unification

**Status:** design only (no Core code in this doc)  
**Date:** 2026-08-04  
**Tracks:** T7 (EE LOADFILE), T8 (UDNL/IOPRP), T10 (debt demolition)  
**IRX plan anchors:** WP-22, WP-25, WP-26, WP-34 · gates **G2** / **G7**  
**Related:**  
`docs/bios-ports/UDNL.md`, `docs/bios-ports/LOADFILE.md`,  
`docs/irx/UDNL_IOPRP.md`, `docs/irx/EE_LOADFILE.md`,  
`docs/infra-audits/gamequirks-infra-debt.md`

---

## 0. Problem statement

Retail EE code, after `SifIopReset("rom0:UDNL …IOPRPxxx.IMG")` / `DNAS*.IMG`, calls LOADFILE **`LF_F_GET_VERSION` (fno=`0xFF`)** and **memcmp**s the 4-byte reply against IOPRP digits (`"2340"`, `"3000"`, …). Real hardware surfaces that tag because **UDNL applied the disc image**.

DetPS2 today:

1. **Can** parse/apply IOPRP containers (`ApplyIopRpImage` / `ApplyUdnlHandoff`) and extract a version ASCII from the reset arg (`ExtractIopRpVersionAscii`).
2. **Does not** always return that tag from GetVersion (gated by `PreferIopRpGetVersion`, default **false** — SM A/B regression).
3. **Does not** always fill EE BSS/data cells that titles treat as the post-UDNL version buffer (`"...."` placeholders) — many assists **plant** those cells in RDRAM.
4. Sometimes the reset arg is empty / host0 / short-name-garbled, so even the shared path has nothing truthful to return.

**M4 goal:** one shared apply + GetVersion path so commercial titles no longer need per-title RAM plants or ad-hoc `SetIopRpVersionAscii` / UDNL arg rewrites for version gates. Plants stay as safety nets until exit criteria pass.

---

## 1. Real hardware contract (source of truth for design)

```text
EE: SifIopReset("rom0:UDNL cdrom0:\\…\\IOPRPxxx.IMG;1")  // or DNAS*
  → IOP: REBOOT + UDNL opens image, IOPBTCONF load/start order
  → EE: sceSifBindRpc(LOADFILE sid=0x80000006)
  → EE: sceSifCallRpc fno=0xFF GetVersion → 4-byte reply
  → EE: memcmp / store into BSS; fail → 0xFFFEFFFC / Exit / FreezeCache / nop-sled
  → EE: LF_F_MOD_LOAD of disc IRX (SIO2MAN, …)
```

Ground truth docs: `docs/bios-ports/UDNL.md`, `docs/bios-ports/LOADFILE.md`, ps2sdk `loadfile.c` / `loadfile-common.h`.

**Two consumer shapes** (both must be served by shared infra eventually):

| Consumer | What it checks | Typical titles |
|----------|----------------|----------------|
| **A. LOADFILE GetVersion reply** | Packed 4 ASCII LE (or classic dword) from RPC | SotC, Haven, Midway family, BO2 gate, Whip |
| **B. EE RAM version cells** | BSS/`"...."` placeholders filled after reboot / copy-from-GetVersion | GoW `0x2C6D30`, B3 `0x4B22C0`, BO2 `0x536188/90`, Vexx, Whip cells |

Shared path must make **A** honest first. **B** often becomes organic once A works and the title’s own store runs; residual B plants are only needed while copy-from-GetVersion never runs or cells are separate from RPC.

---

## 2. Current plant map (GameQuirks + flags)

### 2.1 Legend

| Kind | Meaning |
|------|---------|
| **RAM plant** | `Write8`/`WriteCString` of IOPRP digits over `"...."` / zero |
| **RPC policy** | `PreferIopRpGetVersion = true` (and sometimes `SetIopRpVersionAscii`) |
| **Arg rewrite** | Force full `rom0:UDNL cdrom0:…IMG` into EE buffer and/or `LastIopRebootArg` path |
| **Policy-only** | PreferIopRp only — **best practice** (no memory plant) |
| **Classic stay** | Explicitly leaves PreferIopRp off / classic `0x00020000` |

### 2.2 Fleet table

| Serial | Title | Assist | Disc image / tag | Kind | Key sites / notes |
|--------|-------|--------|------------------|------|-------------------|
| `SLUS_200.24` | Blood Omen 2 | `BloodOmen2SnAssist` | `IOPRP234` → **`"2340"`** | RAM + RPC + **arg rewrite** | Cells `0x536188`, `0x536190`; reboot buf `0x5361A0` → full UDNL arg; PreferIopRp; short-name path patch is **path** INFRA (not version) |
| `SLUS_210.50` | Burnout 3 | `Burnout3Assist` | `DNAS280` → **`"2800"`** | **RAM only** | Placeholder `0x4B22C0`, ptr `0x484224`, expected rodata `0x48414C`; PreferIopRp **OFF** (LGDEV thrash / residual cadence) |
| `SCUS_973.99` | God of War | `GodOfWarAssist` | `IOPRP300` → **`"3000"`** | RAM + RPC + **forced UDNL handoff** | Placeholder `0x2C6D30`; FreezeCache `0x29C4DC`/`0xFFFEFFFC`; PreferIopRp; post-empty-reboot `SetIopRpVersionAscii("3000")` + `ApplyUdnlHandoff(IOPRP300)`; **do not** SetIop early (binds regress) |
| `SLUS_203.83` | Vexx | `VexxAssist` | `IOPRP252` → **`"2520"`** | RAM + RPC | Cells `0x3D18B8`, `0x3D1938`; re-plant if cells scrubbed |
| `SLUS_206.84` | Whiplash | `WhiplashAssist` | `IOPRP255` → **`"2550"`** | RAM + RPC + **host0→cdrom arg rewrite** | Cells `0x421718`, `0x421720`; PreferIopRp + PreferSnFileIo; UsingCD force is media INFRA |
| `SCUS_974.72` | Shadow of the Colossus | `TeamIcoAssist` | `IOPRP300` → **`"3000"`** | **Policy-only** | PreferIopRp; rodata expect `"3000"` @ `0x13227C`; classic → error sled `0x1035B0` |
| `SCUS_971.13` | Ico | `TeamIcoAssist` | (shared Team ICO policy) | **Policy-only** | PreferIopRp |
| `SLUS_205.17` | Haven | `TeamIcoAssist` | `SYS250/IOPRP250` → **`"2500"`** | **Policy-only** | PreferIopRp; classic → `Exit` pre MOD_LOAD |
| `SLUS_204.23` | MK: Deadly Alliance | `MidwayFamilyAssist` | IOPRP family (**`"2430"`** class) | **Policy-only** (+ Pad/Sn) | PreferIopRp + PreferSnFileIo + PadModVerMajor4; no IOPRP RAM plant |
| `SLUS_208.81` | MK: Deception | `MidwayFamilyAssist` | IOPRP300-class | same | PreferSnFileIo avoids false FILEIO-2200 |
| `SLUS_215.50` / `215.43` | MK: Armageddon | `MidwayFamilyAssist` | same family | same | same |
| `SLUS_210.87` | MK: Shaolin Monks | `MidwayBootAssist` | IOPRP gen≥2 storms | **Classic stay** | **Must not** PreferIopRp; always-ASCII regressed spine A/B |

### 2.3 Shared HLE surfaces (not title plants)

| Surface | File / API | Role today |
|---------|------------|------------|
| Tag extract | `RealSifRpc.ExtractIopRpVersionAscii` | `IOPRPxxx` / `DNASxxx` → 4-char (`234`→`"2340"`) |
| Tag store | `RealSifRpc._lastIopRpVersionAscii` | Filled by `OnIopReboot(arg)` or `SetIopRpVersionAscii` |
| GetVersion | `HandleLoadFile` fno=`0xFF` | PreferIopRp && tag → `PackAsciiVersion`; else **`0x00020000`** |
| Post-reboot | `SonyKernelHle.OnIopRebootCompleted` | `ApplyPostIopRebootContracts` → `ApplyUdnlHandoff`; then `RealRpc.OnIopReboot(arg)` |
| UDNL apply | `IopExtendedBiosHost.ApplyUdnlHandoff` | Resolve disc bytes → `ApplyIopRpImage` (name-only unless LITERAL_IRX) |
| MOD_LOAD image | `ApplyIopRpImageBytes` | Same parser when EE loads `IOPRP*.IMG` / `DNAS*.IMG` via LOADFILE |
| LITERAL_IRX opt-in | `OnIopRebootCompleted` | If tag non-empty → set PreferIopRp (no RAM plant) |

### 2.4 Why plants still exist

| Gap | Effect |
|-----|--------|
| PreferIopRp default **false** | GetVersion stays classic; titles that memcmp digits fail without assists |
| Empty / wrong reset arg (GoW `arg=""`, Whip `host0:…`, BO2 short-name) | Extract returns `""`; tag store empty even after reboot |
| Title uses **B** (RAM cells) without re-running store after GetVersion | Cells stay `"...."` even if RPC were correct |
| Commercial UDNL handoff often **name-only** | Image “applied” for name probes but not full real stack; residual for WP-25, secondary for version tag (tag can come from arg alone) |
| SM needs classic GetVersion | Global always-ASCII breaks SM — unification must be **arg-driven**, not “always IOPRP digits” |

---

## 3. Proposed shared apply path

Single spine for all titles. GameQuirks only fill holes until each stage is green.

```text
                    ┌─────────────────────────────────────┐
                    │ EE SifIopReset / RESET_CMD           │
                    │  → Sif.LastIopRebootArg (truthful)   │
                    └─────────────────┬───────────────────┘
                                      ▼
                    ┌─────────────────────────────────────┐
                    │ BiosBootHost.ApplyPostIopReboot      │
                    │  → IOMAN/STDIO re-seed               │
                    │  → ApplyUdnlHandoff(arg)  [T8]       │
                    │       TryResolveUdnlImageBytes       │
                    │       ApplyIopRpImageCore            │
                    │         RegisterModule + optional    │
                    │         LoadIrx (+exec under LITERAL)│
                    │       LastUdnlVersion from arg/image │
                    └─────────────────┬───────────────────┘
                                      ▼
                    ┌─────────────────────────────────────┐
                    │ RealSifRpc.OnIopReboot(arg)  [T4/T7] │
                    │  _lastIopRpVersionAscii = extract    │
                    │  (optional: publish AppliedIopRpTag) │
                    └─────────────────┬───────────────────┘
                                      ▼
                    ┌─────────────────────────────────────┐
                    │ GetVersion policy (unified)          │
                    │  if applied tag non-empty:           │
                    │    reply = PackAsciiVersion(tag)     │
                    │  else if never rebooted with image:  │
                    │    reply = 0x00020000  (classic)     │
                    │  SM / empty-arg: stays classic       │
                    └─────────────────┬───────────────────┘
                                      ▼
                    ┌─────────────────────────────────────┐
                    │ EE client store / memcmp             │
                    │  → natural fill of BSS cells (B)     │
                    │  → MOD_LOAD chain                    │
                    └─────────────────────────────────────┘
```

### 3.1 Stages (implementation order — design only)

| Stage | Name | Intent | Retires |
|-------|------|--------|---------|
| **S0** | **Arg fidelity** | Path combine / UsingCD / short-name so `LastIopRebootArg` is retail-shaped without title rewrite | BO2 arg plant, Whip host0 rewrite, GoW empty-arg force |
| **S1** | **Tag always from apply** | One publisher: after successful extract from arg **or** from applied image name, set `_lastIopRpVersionAscii` (and host `LastUdnlVersion`) — never title `SetIopRpVersionAscii` for healthy args | GoW EnsureIopRp*, ad-hoc SetIop |
| **S2** | **GetVersion = applied tag when present** | When reboot gen has a non-empty IOPRP/DNAS tag, return packed ASCII **without** requiring title PreferIopRp; when no image reboot, classic `0x00020000` (SM safe) | PreferIopRp flags on TeamIco / Midway / BO2 / Whip / Vexx / GoW |
| **S3** | **Image apply reliability** | Disc path resolve + apply on every UDNL handoff; LITERAL_IRX LoadIrx+exec (WP-25) | Soft name-only residual; not required for pure version memcmp |
| **S4** | **Optional EE mirror** | Only if some titles read cells **before** GetVersion store: shared helper “if placeholder `....` and applied tag set, write tag at **caller-supplied** addresses” — **not** per-title digit constants in Core; better: fix so GetVersion runs first | RAM plants (B3/GoW/Vexx/Whip/BO2) |
| **S5** | **Live LOADFILE IRX** | WP-22: R3000 LOADFILE answers GetVersion; HLE fallback only | Entire HLE version policy long-term |

### 3.2 Explicit non-goals for S0–S2

- No new GameQuirk RAM plants.
- No global “always return `"3000"`” / fleet-default digits.
- No SM PreferIopRp.
- No MagicGate secrets; MG_* stays honest fail.
- No Core edits in this design doc deliverable.

### 3.3 Call graph to keep (owners)

| Step | Owner today | Unification note |
|------|-------------|------------------|
| Capture arg | `Sif` / `SonyKernelHle` RESET_CMD | S0: ensure arg bytes match retail |
| UDNL handoff | `IopExtendedBiosHost.ApplyUdnlHandoff` | Single apply; version side-effect |
| Tag store | `RealSifRpc.OnIopReboot` | S1: only writer for healthy path |
| GetVersion | `RealSifRpc.HandleLoadFile` | S2: tag-if-present policy |
| LOADFILE MOD_LOAD of `.IMG` | `ApplyIopRpImageBytes` | Same core as UDNL; refresh tag if MOD_LOAD is how image arrives |
| Title assists | `GameQuirks/*` | Fall back only; T10 deletes when exit tests pass |

---

## 4. LOADFILE GetVersion — source of truth

### 4.1 Authority order (proposed)

When answering `LF_F_GET_VERSION`:

1. **Applied IOPRP/DNAS tag** from last completed UDNL/RESET with parseable image name  
   (`_lastIopRpVersionAscii` / `LastUdnlVersion` — same digits, one store).  
2. Else **classic LOADFILE dword** `0x00020000` (pre-image / SM / homebrew / smokes).  
3. **Never** invent digits from title serial alone in Core.  
4. Long-term (WP-22): **live LOADFILE.IRX** reply supersedes HLE when `PreferLiveLoadFileRpc` + runnable IRX.

### 4.2 Current vs proposed

| Case | Current | Proposed (S2) |
|------|---------|----------------|
| No image reboot / empty extract | `0x00020000` | unchanged |
| Image reboot, PreferIopRp **false** (default) | `0x00020000` (wrong for commercial gates) | **Packed tag** if extract non-empty |
| Image reboot, PreferIopRp **true** | Packed tag | Packed tag (PreferIopRp becomes no-op / deprecated) |
| SM IOPRP storm path | Needs classic | Still classic when extract empty **or** explicit SM-safe rule: only switch on **first successful image extract this gen**, not on bare gen++ |
| Title `SetIopRpVersionAscii` | Override for empty arg | Temporary until S0; then dead code |

### 4.3 FILEIO GetVersion coupling

`RealSifRpc` also gates FILEIO-2200-style behavior on PreferIopRp + numeric tag ≥ 3000 (Play! path). Midway uses **PreferSnFileIo** to suppress false 2200 arming.

**Unification rule:** decouple “GetVersion returns IOPRP ASCII” from “arm FILEIO-2200”.  

- GetVersion follows §4.1.  
- FILEIO layout stays **SN vs Play!** via PreferSnFileIo / disc fingerprint — **not** solely “digits ≥ 3000”.  
Document this so S2 does not re-break DA/Dec GAMER.OVL (already noted in `PreferSnFileIo` comments).

### 4.4 Smoke contracts that must remain green

| Test | Expectation after unification |
|------|-------------------------------|
| `RealSifRpc_LoadFileModuleElfSetGetSearch` | Default GetVersion **`0x00020000`** (no image reboot) |
| `BiosUdnl_IopRpImageApplyAndSecrMgPath` | Image apply + MG paths still pass |
| Commercial diagnose (opt-in) | After real UDNL arg, GetVersion matches extract without GameQuirk PreferIopRp |

---

## 5. Flag strategy

### 5.1 Existing flags (keep; document roles)

| Flag / property | Role | Unification stance |
|-----------------|------|--------------------|
| `PreferIopRpGetVersion` | Title/LITERAL opt-in for ASCII GetVersion | **Deprecate after S2** — behavior becomes “tag if applied”; leave property as override/off for bisect |
| `PreferSnFileIo` | SN ProDG FILEIO reply layout | **Keep** — independent of GetVersion |
| `PadModVerMajor4` | PADMAN major | **Keep** — not GetVersion |
| `DETPS2_LITERAL_IRX=1` | LoadIrx on commercial handoff + PreferIopRp opt-in today | Keep for WP-25/exec; S2 should not require it for correct GetVersion digits |
| `DETPS2_LITERAL_IRX=0` / unset / `FORCE_HLE_IOP` | HLE-first bisect | Smokes stay classic GetVersion without image |
| `DETPS2_IOPRP_NAME_ONLY=1` | Force name-only apply | Bisect only |
| `DETPS2_UDNL_SKIP_IMAGE=1` | Skip image bytes | Diagnostic |
| `DETPS2_UDNL_SKIP_HANDOFF=1` | Skip full UDNL handoff | A/B early Exit titles |
| `PreferLiveLoadFileRpc` | Skip HLE CALL when live LOADFILE owns sid | WP-22 scaffold — off until IRX ready |
| `DETPS2_TRACE_RPC` / `TRACE_REBOOT` / `TRACE_BIOS` | Log tag + handoff | Keep for exit evidence |

### 5.2 Proposed policy flags (design — names illustrative)

| Proposed | Purpose |
|----------|---------|
| **Tag-if-applied** (default on for GetVersion) | If `_lastIopRpVersionAscii` non-empty → pack; else classic. Replaces PreferIopRp as primary. |
| `DETPS2_GETVERSION_CLASSIC=1` | Emergency: always `0x00020000` (SM / smoke bisect) |
| `DETPS2_GETVERSION_FORCE_ASCII=<tag>` | Debug only — never production default |
| Optional `DETPS2_MIRROR_IOPRP_CELLS=1` | Opt-in EE placeholder mirror for residual B titles during S4 — **off** by default |

### 5.3 PreferIopRp retirement sequence

1. Implement tag-if-applied in GetVersion; run SM A/B + full smoke matrix.  
2. If SM green: leave PreferIopRp set-but-redundant in assists.  
3. T10: remove PreferIopRp assignments from TeamIco / Midway / BO2 / Whip / Vexx / GoW when diagnose proves natural.  
4. Delete property only after no readers remain (or keep as explicit force-classic=false override).

### 5.4 LITERAL_IRX relationship

| Concern | Flag |
|---------|------|
| GetVersion digits from disc arg | **Should not** require LITERAL_IRX (arg extract is enough) |
| LoadIrx + `_start` of image modules | LITERAL_IRX / WP-25 |
| Live LOADFILE server | LITERAL_IRX + PreferLiveLoadFileRpc + WP-22 |

M4 GetVersion unification is **orthogonal** to full IRX purity; it unblocks the largest plant class early (audit priority #1).

---

## 6. Exit criteria — retire per-title plants

Retire a plant only when **both** infra proof and title fleet proof hold. Do not mass-delete assists.

### 6.1 Shared infra exit (M4 core)

| ID | Criterion | Evidence |
|----|-----------|----------|
| **E1** | After `SifIopReset` with parseable `IOPRP*`/`DNAS*` arg, `_lastIopRpVersionAscii` matches `ExtractIopRpVersionAscii(arg)` without GameQuirk `SetIopRpVersionAscii` | TRACE_REBOOT / unit |
| **E2** | `LF_F_GET_VERSION` returns `PackAsciiVersion(tag)` for that reboot **without** title PreferIopRp | RPC trace + smoke with synthetic reboot |
| **E3** | With empty / non-image reboot arg, GetVersion remains **`0x00020000`** | Existing LoadFile smokes |
| **E4** | Shaolin Monks spine A/B (or fixed diagnose budget) **does not regress** under tag-if-applied | SM claim / scoreboard |
| **E5** | Midway DA/Dec FILEIO still completes OVL path under PreferSnFileIo (no false 2200-only path) | DA diagnose |
| **E6** | `ApplyUdnlHandoff` still runs from post-reboot contracts; BiosUdnl smoke green | CI |

### 6.2 Per-title plant retirement checklist

For each row: disable plant under env or compile-time probe; run title diagnose; compare to baseline MENU/binds/cdvd.

| Title | Plant to retire | Preconditions | Pass if |
|-------|-----------------|---------------|---------|
| SotC / Ico / Haven | PreferIopRp only | E1–E3; real UDNL arg from game | GetVersion gate passes; no Exit / error sled; PreferIopRp unset |
| DA / Dec / Arm | PreferIopRp (keep PreferSnFileIo until FILEIO IRX) | E1–E5 | Version gate + SN FILEIO opens |
| BO2 | `"2340"` cells + PreferIopRp + arg rewrite | S0 short-name + E1–E2 | Cells filled by game or unused; MOD_LOAD after gate |
| B3 | `"2800"` RAM plant | E1–E2; PreferIopRp may stay off | SifLoadModule version gate passes; LGDEV cadence not worsened |
| GoW | `"3000"` plant + FreezeCache clear + forced handoff | S0 empty-arg fix or retail arg; E1–E2 | FreezeCache not `0xFFFEFFFC`; no EnsureIopRp / forced ApplyUdnl |
| Vexx | `"2520"` cells + PreferIopRp | E1–E2 | VersionCellsOk without plant; pad OPEN path intact |
| Whip | `"2550"` cells + PreferIopRp + arg rewrite | S0 UsingCD/media default; E1–E2 | Retail cdrom UDNL; no host0 rewrite |
| SM | (none for version) | E4 | Still classic / no PreferIopRp |

### 6.3 T10 deletion order (after green)

Aligns with `docs/IRX_EXECUTION_PHASE_PLAN.md` **WP-26** / **WP-34** and debt audit priority #1:

1. Policy-only PreferIopRp (TeamIco → Midway PreferIopRp line).  
2. RAM plants whose titles already copy from GetVersion (prove with memory read after gate).  
3. Arg rewrites after path/media INFRA.  
4. GoW FreezeCache **version** clear only if mismatch path never writes error (other FreezeCache causes stay).  
5. Do **not** delete PRESENT/INTERACTIVE/SECONDARY assists under this M4 ticket.

### 6.4 Definition of done (M4)

- [ ] Design accepted (this doc).  
- [ ] S1–S2 implemented in Core (separate PR; not this seed).  
- [ ] E1–E6 green.  
- [ ] ≥3 titles from plant map run version gate with PreferIopRp **false** and **no** RAM version plant (recommend: Haven, SotC, DA).  
- [ ] Remaining plants listed in GameQuirks with comment `// residual until S0/E#` or removed.  
- [ ] Scoreboard / TITLE_HACKS note: “GetVersion unified; plants retired for …”.

---

## 7. Mapping to IRX work packages

| WP | Relation to M4 |
|----|----------------|
| **WP-22** | Live LOADFILE GetVersion (S5); M4 HLE tag-if-applied is interim G2 partial |
| **WP-25** | Image LoadIrx+exec; strengthens apply, not required for digit reply |
| **WP-26** | “IOPRP version string path matches EE strcmp without RAM plant” — **M4 S2+S4 exit** |
| **WP-34** | Delete version RAM plants where path works — **T10 after E*** |

---

## 8. Risks and mitigations

| Risk | Mitigation |
|------|------------|
| SM regression if GetVersion always ASCII | Tag-if-applied only when extract non-empty; `GETVERSION_CLASSIC` bisect; SM does not set PreferIopRp |
| Early SetIop / early PreferIopRp regresses binds (GoW, B3) | Never plant tag before real reboot gen; only publish on completed RESET |
| FILEIO-2200 false arm on Midway | Keep PreferSnFileIo; decouple from GetVersion (§4.3) |
| Empty arg (GoW) | S0: find why RESET arg empty; until then temporary SetIop remains titled residual |
| Titles that only read BSS cells | S4 optional mirror or ensure GetVersion+store runs before memcmp |
| Dual writers of tag (assist vs OnIopReboot) | Single writer rule after S1; assists only if store empty |

---

## 9. Implementation sketch (non-binding; future PR)

Not implemented in this seed. Intended touch list when coding:

| Area | Change |
|------|--------|
| `RealSifRpc.HandleLoadFile` GetVersion | Tag-if-applied (§4.2); PreferIopRp as optional force |
| `RealSifRpc.OnIopReboot` | Sole tag publisher from arg |
| `IopExtendedBiosHost.ApplyUdnlHandoff` | Ensure version published even when image skip / name-only |
| `SonyKernelHle.OnIopRebootCompleted` | PreferIopRp auto-set under LITERAL may become redundant |
| GameQuirks | Guard plants with “if tag already correct / GetVersion would pass, skip”; later delete |
| Tests | Unit: extract → GetVersion without PreferIopRp; SM classic; BiosUdnl unchanged |

---

## 10. References (absolute paths)

| Artifact | Path |
|----------|------|
| UDNL port | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\bios-ports\UDNL.md` |
| LOADFILE port | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\bios-ports\LOADFILE.md` |
| UDNL IOPRP track | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\irx\UDNL_IOPRP.md` |
| EE LOADFILE track | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\irx\EE_LOADFILE.md` |
| Quirks debt audit | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\gamequirks-infra-debt.md` |
| IRX phase plan | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\IRX_EXECUTION_PHASE_PLAN.md` |
| GameQuirks | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\` |
| RealSifRpc GetVersion | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\RealSifRpc.cs` |
| UDNL host | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\IopExtendedBiosHost.cs` |

---

*Design seed only. No Core code changes. Implement S1–S2 under T7/T8; retire plants under T10 when exit criteria pass.*
