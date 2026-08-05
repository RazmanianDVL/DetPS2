# GFX Dec — EE decompile groundwork (seat #5)

**Status:** groundwork complete — tooling **works** for Dec EE; next is texture-consumer targeting  
**Date:** 2026-08-04 late  
**Title:** MK: Deception (SLUS_208.81)  
**Scope:** Can bios-decomp-style / existing Ghidra R5900 tooling target the Dec **EE** boot ELF for CLUT / texture-consumer investigation?  
**Parent:** `gfx-dec-clut-investigation.md` (palette source still unlocated; TEX0 cld=0 closed container leads)  
**Core:** **none** this seat (docs/tooling only)

---

## 0. Verdict (one line)

**Yes.** Same pipeline as Shaolin/Burnout3/Whip works on Dec: `extract-file` → Ghidra headless import `r5900:LE:32:default` → auto-analysis → postScript decompile/xref. Project is live. No Core.

---

## 1. Tooling inventory (reused, not reinvented)

| Piece | Path / note |
|-------|-------------|
| Ghidra | `C:\Users\xxraz\ghidra\ghidra_12.1.2_PUBLIC` |
| R5900 language | `ghidra-emotionengine-reloaded` → **`r5900:LE:32:default`** |
| Scripts | `C:\Users\xxraz\ghidra\scripts\` (`DecompileTargets`, `BiosModuleDecomp`, new Dec helpers) |
| CLI extract | `detps2 extract-file <iso> <path> <out>` |
| CLI sections | `detps2 elf-sections` |
| Prior EE projects | ShaolinMonks, Burnout3, Whiplash (same recipe) |

Local-only machine paths: see gitignored `TOOLING.local.md`.

---

## 2. Extract (done)

| Item | Result |
|------|--------|
| ISO | `MortalKombatDeception(USA).iso` |
| SYSTEM.CNF | `BOOT2 = cdrom0:\SLUS_208.81;1` |
| Boot ELF | **`SLUS_208.81`**, 5,072,296 bytes |
| Local scratch (never commit) | `C:\Users\xxraz\ghidra\dec_ee\dec_boot.elf` + `SYSTEM.CNF` |
| ELF identity | `e_machine=8` (MIPS), `e_entry=0x00100008`, single large PT_LOAD `@0x00100000` size `0x4D6280` |

---

## 3. Ghidra import + analysis (done)

```text
analyzeHeadless … MkDeception -import dec_boot.elf
  -processor r5900:LE:32:default -cspec default
  -analysisTimeoutPerFile 900
```

| Check | Result |
|-------|--------|
| Project | `C:\Users\xxraz\ghidra\projects\MkDeception` program **`dec_boot.elf`** |
| Language | `r5900:LE:32:default` (postScript confirmed) |
| Auto-analysis | **succeeded** (~97s): Disassemble, Function, R5900 Constant Ref, Stack, Decompiler Switch, etc. |
| Import | **REPORT: Import succeeded** |

Headless re-open recipe (no re-analysis):

```text
analyzeHeadless projects MkDeception -process dec_boot.elf -noanalysis
  -scriptPath scripts -postScript <Script>.java [args…]
```

New scripts (local under `ghidra/scripts`, not DetPS2 Core):

- `DecFindGameart.java` — string hunt + xrefs + decompile containing funcs  
- `DecInspectXref.java` — neighborhood dump around data xrefs  

---

## 4. First static anchors (CLUT path adjacent)

### 4.1 Strings (ELF scan + Ghidra)

| Anchor | Notes |
|--------|--------|
| **`gameart.ssf`** | Present in EE image (matches PL-029 Host→Local source name) |
| `MWFILE` / `mwFile*` | Full Midway file RPC/server string table (EE-side client) |
| `sceGs*` | Real SCE GS helpers (`sceGsExecLoadImage`, `sceGsSyncPath`, …) |
| No literal `CLUT` / `TEX0` / `PSMT` ASCII | Expected — GS register setup is usually numeric/SDK macros, not strings |

### 4.2 `gameart.ssf` placement

| Field | Value |
|-------|--------|
| String VA | **`0x005A6E10`** (also mirrored images at `0x205A6E10`, `0x306A6E10` — EE multi-region; primary is low) |
| Data pointer | **`0x0050AD28` → `0x005A6E10`** (DATA xref) |
| Containing function | **none yet** — site is a **path / asset table**, not a code site |
| Nearest funcs | before `FUN_004f73a8`, after `FUN_005141f0` (large rodata gap) |

Table neighborhood (words @ `0x50AD00+`) looks like repeated records:

```text
count?, pad, name_ptr, other_ptr, …
… 00000001 00000000 005a6e00 005a6e20
… 00000001 00000000 005a6e10 005a6e20   ← gameart.ssf
… 00000004 00000000 005a6e50 005a6f30
```

**Implication for CLUT:** consumers almost certainly walk this table (or a loader keyed by these paths), not hardcode `"gameart.ssf"` in a single `jal`. Next step is **find table base + walkers** (Ghidra refs to table base / `lui`+`addiu` half-immediates for `0x0050ADxx`), then decompile open/read → decode → GS upload chain.

Artifacts (gitignored machine-local):  
`C:\Users\xxraz\ghidra\dec_ee\gameart-xrefs.txt`, `gameart-inspect.txt`, `string-scan.txt`, `ghidra-import.log`.

---

## 5. What this unlocks / does not unlock

| Unlocks | Still out of scope |
|---------|-------------------|
| Full EE decompile of any VA via `DecompileTargets` / postScripts | Inventing CLUT bytes in Core |
| Xref + string + table reverse on real Dec code | Claiming MENU / color without evidence |
| Parallel to Whip Ghidra work (same install) | Finishing MP2 / multi-session format RE |

**Does not** by itself load a palette — only proves the reverse-engineering seat is open and reproducible.

---

## 6. Recommended next EE steps (when claimed)

1. Identify **table base** for the path records around `0x50AD28` and every **code ref** to it.  
2. Decompile **open/load path** for `gameart.ssf` (likely MWFILE open → buffer @ `0x01800000` class).  
3. Trace from load complete → any **GS path** that sets CBP/TEX0/TEX2 or packs a CLUT transfer (BITBLT to CLUT buffer / `sceGs` load image).  
4. Cross-check live DetPS2: `scanword` / `--dump` / temporary TRACE only if needed; revert instrumentation.  
5. Feed results back into `gfx-dec-clut-investigation.md` §4+ (replace “unlocated palette” with EE-proven source or honest new refutation).

---

## 7. Non-overlap / bans

- No Core, no Host→Local invent, no re-opening refuted SEC e=1 / e=8–13 as CLUT without new EE evidence.  
- Do not commit `dec_boot.elf` / BIOS / ISO extracts.  
- Ghidra project stays on local machine under `C:\Users\xxraz\ghidra\`.

---

## 8. Dual-orch handoff

- Seat #5 groundwork **done** tip base `0ec10e7` (M1 unrelated).  
- Claude free to dual-ACK / continue C1 + MP2.  
- Follow-on EE table-walker seat is shovel-ready without waiting on demand-gate.

---

## 9. Follow-on: path-table walkers (same session)

Ghidra script `DecFindTableWalkers` → `table-walkers.txt` / `loaders-decomp.txt` (local).

### 9.1 Code that touches the table region

| Function | Role (from decomp) |
|----------|--------------------|
| **`FUN_00267090`** | “Set current package”: takes a path-descriptor; opens via `FUN_00222790(*(name),1)`; caches handle in globals |
| **`FUN_001a44d0`** | Resource lookup by packed id `param_1` (hi16 bucket / lo16 entry); then `FUN_001a4960` + `FUN_001a4830` |
| **`FUN_001a41b0` / `FUN_001a4200`** | Early boot: `FUN_00267090(PTR@0x50AD0C)` then load id `0` with table `0x50AD08` |
| **`FUN_003e8170` / `FUN_001710b0` / others** | Frontend paths: `FUN_00267090(0x5A6E20)` then `FUN_001a44d0(0x10005, 0x50AD18)` |

### 9.2 Implication for CLUT

Real EE code loads Midway packages through **`FUN_00267090` → open → `FUN_001a44d0` resource ids**, not only through DetPS2’s PL-029 Host→Local feed. Next dig:

1. Decompile **`FUN_001a4830` / `FUN_001a4960`** (post-lookup decode / register).  
2. Decompile **`FUN_00222790`** (actual file open / buffer destination — confirm `0x01800000` class).  
3. Trace whether any load path issues **GS CLUT / TEX0** after package open (or only index BITBLT).  
4. Compare package-open path vs PL-029 residual: is palette expected from a **sibling resource id** under the same package?

No Core this extension either — still docs/TRACE.
