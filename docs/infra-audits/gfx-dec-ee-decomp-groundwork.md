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

---

## 10. EE-native SEC parser (CLUT-adjacent breakthrough)

### 10.1 Load chain (confirmed by decomp)

```text
FUN_00267090(desc)                 // set current package
  → FUN_00222790(name,1)           // path builder: often "/art/" + name → "/art/gameart.ssf"
  → FUN_0021d810(mount, path)      // normalize cdrom0:/host0:, then open via FUN_0023a180
FUN_001a44d0(resourceId)           // lookup
  → FUN_001a4960 → FUN_00478950    // bind/load
  → FUN_001a4830                   // instantiate resources
       if type==2: FUN_0036c630    // SEC parse (simple)
       if type==1: FUN_0036da90    // SEC parse (full)
```

Magic check in both parsers: `*payload == 0x53454320` (**`"SEC "`**).

### 10.2 Type-1 SEC walk (`FUN_0036da90`) — kinds that matter

TOC entry stride **0x10**. Kind = `entry[0] & 0x3fffffff`.

| Kind | EE behavior |
|------|-------------|
| **2, 3** | Treated as “normal” payload tiles (not tracked as last-special) |
| **9** | Nested pointer block: relocates `count` pointer words relative to nested base |
| **other** | Updates `lastSpecialIndex`; may drive follow-on `FUN_001ab750` / `FUN_0036dd80` processing for **later** TOC entries |

After TOC fixups: **`FUN_00478da0`**, mark ready.

PL-029 only feeds **kind=2** Host→Local tiles and never runs this EE instantiate path. That is a structural gap vs real game code — not proof of CLUT location yet, but it explains why container-only TRACE (no EE consumer) can exhaust while chrome stays gray.

### 10.3 Path names (string dump)

| VA | String |
|----|--------|
| `0x5A6DE8` | `sysart.sec` |
| `0x5A6E00` | `fightingart.sec` |
| `0x5A6E10` | **`gameart.ssf`** |
| `0x5A6E50+` | `permanent_strings_*.mko` / `.ssf` |

Descriptor `@0x5A6E20` double-indirects to the `gameart.ssf` table record (`0x50AD28` → name `0x5A6E10`).

### 10.4 Next EE dig (ordered)

1. **`FUN_0036dd80` / `FUN_001ab750`** — what “special” kinds produce (palette blob? decompress?).  
2. **`FUN_00478da0`** — post-SEC registration; any GS/CBP/TEX path.  
3. Live TRACE: when frontend hits `FUN_001a44d0(0x10005, …)`, dump SEC TOC kinds from the **in-memory** package after EE parse (compare to PL-029’s kind=2-only view).  
4. Only after EE-proven palette source: plan Core to either run real consumer further or load CLUT from that source — dual-ACK before Core.

Still **no Core** this seat.

---

## 11. Special-kind path + honesty bound on “open”

### 11.1 `FUN_0036dd80` (called from type-1 SEC for post-special entries)

Stream reader (not a palette blitter):

1. Read `u8` name length + name bytes  
2. Read two `u32`  
3. `FUN_00229da0` → object alloc  
4. Flag byte low bits set to **2** or **4** via `FUN_001b16c0`  
5. `FUN_001b2930` copies name into object  

So “special” kinds drive **named sub-object materialization** from a sequential stream, not an obvious 256-entry CLUT DMA.

### 11.2 `FUN_00478da0` / package bind

Post-SEC cleanup + freelist; async bind queue via `FUN_00478e10` / `FUN_00478f30` / `FUN_00266f40` (name lookup in current package, optional completion callback `FUN_00479100`). **No GS/TEX0/CLUT constants** in this chain.

### 11.3 Honest bound

| Mapped | Not yet mapped |
|--------|----------------|
| Disc path → package open → SEC instantiate → kind taxonomy | **Where PSMT8 tiles get a palette into GS** (draw/upload / TEX0-cld path) |
| Why PL-029 is incomplete vs EE | Live TOC kinds **after** real EE open (may differ from Host→Local-only view) |

**CLUT is not sitting in the package-open prologue.** Next productive EE seats:

1. Live TRACE: after frontend calls `FUN_001a44d0(0x10005,…)`, dump in-memory SEC TOC kinds/sizes (compare to PL-029).  
2. Static: find writers of GS CLUT-related paths / `sceGs` load-image callers that take SEC tile payloads (xref from `sceGsExecLoadImage` string or known BITBLT helpers).  
3. Only then Core dual-ACK.

Park open-path dig here unless new evidence appears.

---

## 12. Live TRACE — in-memory SEC TOC (50M host-present)

**Method:** `blocker-trace user-media-deception.json --cycles=50000000 --host-present --dump=…`  
**Present residual:** gray strip still `lit=32768/286720 s0=0xFF808080` (unchanged honesty).

### 12.1 Nested gameart tiles (`0x01800800`)

| Field | Value |
|-------|--------|
| Magic | `SEC ` |
| Count `@+0x10` | **404** (`0x194`) |
| TOC `@+0x1C` stride `0x10` | **kind histogram: kind=2 × 404** (zero non-2) |

EE special-kind path (kind 9 / stream `FUN_0036dd80`) **does not fire** for this nested container. PL-029’s kind=2-only walk matches the live nested TOC.

### 12.2 Root SEC (`0x01800000`)

| Field | Value |
|-------|--------|
| Count | **14** kind=**1** entries |
| e0 | off=`0x800` sz=`0x247580` → nested tile SEC above |
| e1 | off=`0x248000` → **another `SEC `** of kind=2 tiles (more textures, not palette) |
| e8–e13 | ~5–6.7 KiB blobs starting `0x0000000A…` — sparse param/record shape, **not** 256×RGBA32 CLUT layout (reconfirms prior SS4e-class refutation with live addresses) |

### 12.3 CLUT status after live TRACE

| Hypothesis | Status |
|------------|--------|
| Nested non-2 kinds hide CLUT | **Refuted** (all 404 kind=2) |
| Root kind=1 siblings are palettes | **Refuted for e1** (more SEC tiles); e8–13 not CLUT-shaped |
| Open-path EE SEC parse loads CLUT | **Not supported** by open-path decomp + this TOC |
| **Still open** | Draw-side / TEX0-cld / GS upload path; or CLUT inside **per-tile** payload (not TOC kind) |

Artifacts (gitignored): `out/canaries/dec-ee-live-toc/`.
