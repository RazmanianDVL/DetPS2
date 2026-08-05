# GFX L2/Dec — CLUT investigation (why the gray strip is gray)

**Status:** investigation only — **no Core this seat**
**Plan:** `gfx-plan-v0.md`
**Title:** MK: Deception (SLUS_208.81)
**Scope:** why Dec's Host→Local gameart tiles present as flat gray instead of real color

---

## 0. One-line

The gray strip is not fabricated noise and not a compositing bug — it is the *correct, honest*
output of real, already-existing CLUT-decode infrastructure encountering PSMT8 (palette-indexed)
texture data with **no palette ever loaded**. The missing piece is narrow and findable: locate and
load the real CLUT for these tiles. This is not a new format-reverse-engineering project on the
scale of Whiplash's MP2 — the pixel *format* is already understood and already has real decode
support; only the palette *source* is unlocated so far.

---

## 1. Confirmed: the fallback is honest, not fabricated

`Gs.cs` already has full, real CLUT infrastructure — `MaybeLoadClut` (~line 749), the `_clut[256]`
cache, `_hasClut` flag, `UploadTexture8` — used elsewhere for genuine TEX0/TEX2-driven texture
sampling (tagged `GX-031`, a real prior feature, not something built for this investigation).

The PSMT8 pixel decode (`Gs.cs:1660`) is:

```csharp
return _hasClut ? _clut[idx8] : 0xFF000000u | ((uint)idx8 << 16) | ((uint)idx8 << 8) | idx8;
```

When no CLUT is loaded, it deliberately falls back to `R=G=B=idx8` — the raw palette index
repeated across channels, i.e. a grayscale ramp of whatever index values are present. This is
already recognized in the code as the *honest* residual case (see `Gs.cs:2629`, the L2b coherence
check explicitly allows "mostly gray residual (index-without-CLUT strip)" through as non-fabricated,
while rejecting genuinely chromatic high-entropy paint).

**This confirms the gray strip's mechanism precisely** — it is not misread bytes producing
accidental noise (like the rejected 3bcedb2/L2b-C4-noise cases); it is a real, working fallback for
real indexed-texture data that has never been given its real palette.

---

## 2. Confirmed: PL-029 feeds PSMT8 tiles, loads zero CLUT

`TryFeedDecGameartHostToLocal` (`MidwayFamilyAssist.cs:595`, "PL-029") walks the nested Midway
`SEC ` container inside the real, fully-loaded `gameart.ssf` (2,836,480 bytes, confirmed real MWFILE
open, per `docs/title-ports/MK_DECEPTION.md`), and BITBLTs real payload bytes Host→Local for up to
48 texture tiles.

Live trace (100M cycles, `user-media-deception.json`, `--host-present`), temporary instrumentation
added and fully reverted after use (`git status` confirmed clean):

```
[SEC-DUMP]  e=0..47  all kind=2, sz=0x1700 (5888B), sequential offsets +0x1700 each, first4=0x00004000
[SEC-DUMP2] e=0..47  all: w0=w1=0x00004000  marker=0x32535018 ("\x18PS2")  w2=0x100  headerSkip=0x100  payload=0x1600
[SEC-DUMP3] e=0..47  all: dpsm=19 (0x13 = PSMT8)
```

**All 48 real, distinct texture tiles are fed as PSMT8.** `dpsm = bpp == 4 ? 0x00 : 0x13;`
(`MidwayFamilyAssist.cs:691`) picks PSMT8 whenever the payload isn't large enough for a 4-byte
PSMCT32 interpretation at the guessed tile size — for all 48 tiles here, that's every one of them.

**No code path in `TryFeedDecGameartHostToLocal` ever calls anything CLUT-related.** No
`MaybeLoadClut`, no `UploadTexture8` with a palette argument, no write to a CLUT base register.
`_hasClut` simply never becomes true for this content. This is the missing mechanism.

---

## 3. Two hypotheses tested and refuted (before this doc, not guessed into it)

| Hypothesis | Test | Result |
|---|---|---|
| SEC TOC `kind=1` entries are separate CLUT/palette chunks (interleaved with `kind=2` texture tiles) | Direct trace of all accepted TOC entries in the 100M run | **Refuted** — zero `kind=1` entries appear at all; all 48 accepted entries are `kind=2` |
| The per-tile "header" region (`headerSkip`, currently discarded before the texture payload) is itself a per-tile palette | Dumped `w0/w1/w2/marker/headerSkip` for all 48 tiles | **Refuted (probable)** — the header is **byte-identical** (`w0=w1=0x4000`, `marker=0x18PS2`, `w2=0x100`, `headerSkip=0x100`) across all 48 genuinely different real texture tiles. A real per-texture CLUT would very likely differ between different UI/HUD tiles; a fixed-format struct header (dimensions/flags/type) would not. This looks like a struct header, not palette data — though its *field layout* has not been decoded, so this is probable, not certain. |

Ruling these out narrows the search rather than closing it — see §4.

---

## 4. Open: where the real CLUT actually is

Not yet found. Two live leads, neither pursued yet:

1. **Decode the 256-byte uniform header's field layout.** It is fixed and repeats identically
   across every tile, consistent with a real Midway struct (marker `"\x18PS2"` at +0x14 suggests a
   deliberate, documented-internally format, not incidental bytes). If any field is an
   offset/pointer, it may point to a shared palette elsewhere in the loaded `gameart.ssf` image
   (base `0x01800000`) rather than per-tile.
2. **Search outside this specific nested SEC TOC.** The root SEC container (`docs/title-ports/MK_DECEPTION.md`
   notes root TOC entries at `+0x20`, first nested SEC usually at `+0x800`) may have sibling
   entries — a global/shared palette resource for the whole tile set — that the current TOC walk
   never visits because it only follows `rootEnt0Off`/`rootEnt0Sz` into one nested container.

**Assigned:** Grok is decoding the 256-byte header structure (lead 1) as the next investigation
step (docs/TRACE only, no Core, per plan discipline).

---

## 4b. Header field layout (Grok, tile0 live dump)

**Capture:** `DETPS2_TEMP_DEC_HEADER_DUMP=1` (instrumentation added + fully reverted).  
Tile0: `nestBase=0x01800800` `off=0x2980` `sz=0x1700` `texAddr=0x01803180`  
Artifacts (gitignored): `out/canaries/gfx-dec-header/tile0-header.txt`, `tile0-off2980-sz1700.bin`

### Word table (LE u32, first 0x100)

| Off | Value | Working decode |
|-----|-------|----------------|
| +0x00 | `0x00004000` | **W** = 64 as `(dim<<8)` → 0x40<<8 |
| +0x04 | `0x00004000` | **H** = 64 same encoding |
| +0x08 | `0x00000100` | **Header size / payload skip** = 256 (matches `headerSkip`) |
| +0x0C | `0x00000800` | 2048 — candidate **data size** or mip stride (not full payload 0x1600) |
| +0x10 | `0x03FFFF00` | Mask / flags class (repeats) |
| +0x14 | `0x32535018` | Magic **`\x18PS2`** |
| +0x18 | `0x00110100` | Packed? lo16=0x0100 (256), hi16=0x0011 (17) — unclear |
| +0x1C | `0x00000200` | 512 — candidate **palette byte count** (128×RGB555=256 or 128×RGBA32=512) |
| +0x20 | `0x00000400` | 1024 — candidate 256×RGBA32 CLUT size |
| +0x24 | `0x03FFFF00` | flags |
| +0x28 | `0x00000018` | 24 |
| +0x2C–0x3C | 0x200 / 0x400 / mask / 0x18 / 0x100 | Sub-descriptor repeat pattern |
| +0x40 | `0x00155700` | 1 399 552 — **too large** for in-tile offset; not a tile-relative pointer |
| +0x4C–0x5C | 0x4000 / mask / 0x4018 / 0x4000 / 0x800 | More dim/flag echo |
| +0x60 | `0x02250400` | Unknown packed |
| +0x64 | `0x30800000` | Unknown |
| +0x68 | `0x00000599` | 1433 |
| +0x6C | `0x00001C20` | 7200 |
| +0xAC.. | ASCII | **`PAD128` repeated** (`.PAD128PAD128PAD128…`) |

### Implications

1. **Dimensions confirmed:** 64×64 via `(dim<<8)` in w0/w1 — matches PL-029 `side=64` for these slabs.  
2. **Format tag `PAD128`:** Midway-style name strongly implies **128-entry palette** texture (not full 256). Aligns with PSMT8 indices but only low 7 bits used / half-CLUT.  
3. **+0x1C=0x200 / +0x20=0x400:** best in-header size candidates for a CLUT blob (512 or 1024 bytes). **Not proof of location.**  
4. **No tile-relative pointer into a shared palette found** in the first 0x100 that lands inside `sz=0x1700` or obvious RDRAM gameart range as a file offset. `0x00155700` is not a usable in-member offset for this 2.8 MiB file layout without more context.  
5. **Payload after skip:** `0x1600` bytes = 5632. One 64×64 PSMT8 = 4096 → **1536 bytes residual** after pure indices — enough for 128×RGBA32 (512) + extra, or mips, or second plane — **if** palette is **after** the header (before or after indices). Need a second dump of bytes at `texAddr+0x100` vs `texAddr+0x100+4096` entropy to place palette vs texels.  
6. **Header-as-CLUT remains unlikely** (Claude §3): identical across tiles; now also carries a format **name** and fixed dims, not 256 unique colors.

### Next investigation (not done this seat)

| Step | Why |
|------|-----|
| Dump `texAddr+0x100 .. +0x1600` entropy / histogram for tile0 | Tell indices vs palette-looking RGBA |
| Compare two tiles’ post-header 512B | Shared vs per-tile palette |
| Walk **sibling** root SEC TOC entries (Claude lead 2) | Shared CLUT resource outside nest |
| If palette found: design dual-ACK to `MaybeLoadClut` before PSMT8 Host→Local | Real color path |

---

## 4c. Leftover-payload entropy (Claude, tile0 + tile1)

**Capture:** `DETPS2_TEMP_DEC_LEFTOVER_DUMP=1` (instrumentation added, used, fully reverted —
`git status` confirmed clean before and after). Assumed `64×64` PSMT8 index block = `0x1000`
(4096B) immediately after the `0x100`-byte header; dumped the remaining `0x600` (1536B) of the
`0x1600` payload for the first two tiles.

```
tile=0 idxBase=0x01803280 leftoverBase=0x01804280 leftoverLen=0x600 uniqueVals=217 nonZero=1436 ffCount=4
  first64: 35 5C 43 3B 96 8F 36 5E 3B 8F 51 5C 35 4F 4A 42 85 9D B2 5B 84 84 3F 62 A5 35 A7 4D 91 3F 83 ...
tile=1 idxBase=0x01804980 leftoverBase=0x01805980 leftoverLen=0x600 uniqueVals=212 nonZero=1400 ffCount=3
  first64: ED BB EF A3 DE C6 ED D6 C7 6C CC BF C7 86 CC C2 D7 B6 91 90 8F B6 A2 AE 8F A9 A2 A9 A9 A9 91 ...
```

**Refutes the in-payload-embedded-palette hypothesis too:**

1. **Not shared between tiles.** Tile0 and tile1's leftover bytes are completely different byte
   sequences (not a rotation/offset of each other either, by inspection). A real shared 128/256-entry
   CLUT reused across all 48 tiles would be byte-identical here. It is not — this is per-tile data.
2. **Not palette-shaped.** 217/212 unique byte values out of 256 possible, ~93% non-zero, no
   repeating alpha-channel stride (a real RGBA32 palette would show `FF` or a constant byte every
   4th position; a real RGB555/A1 palette would show constrained 16-bit value ranges). The raw bytes
   read as dense, high-entropy, texture-like data — consistent with **more index/pixel data**, not a
   color table.

**Working conclusion:** the `0x1600` payload is very likely *entirely* pixel/index data for a tile
larger or differently-shaped than the naive `64×64` PSMT8 assumption (`64×64=4096` undersizes the
real `0x1600=5632`-byte payload by exactly `1536` bytes) — not `4096 index + 1536 palette`. The
`side=64`/`bpp=1` guess in `TryFeedDecGameartHostToLocal` (driven by payload-size heuristics, not
a decoded dimension field) may itself be wrong for these tiles, independent of the CLUT question.

**In-payload palette hypothesis refuted** (Claude leftover dump). Nested `kind=1` palette refuted.
Per-tile header palette refuted. **Root sibling walk is not empty** — see §4d.

---

## 4d. Root SEC sibling TOC (Grok)

**Capture:** `DETPS2_TEMP_DEC_ROOT_TOC=1` (fully reverted). Artifact: `out/canaries/gfx-dec-header/root-sec-toc.txt`.  
`loaded=2836480`. Root `+0x10=0x0E` → **14 TOC entries**.

| e | off | sz | kind | first | Notes |
|---|-----|-----|------|-------|--------|
| **0** | `0x800` | `0x247580` (~2.28 MiB) | 1 | `SEC ` | Tile nest — **only PL-029 walks this** |
| **1** | `0x248000` | `0x22C00` (~139 KiB) | 1 | `SEC ` | **Sibling SEC — unvisited** |
| 2–7 | ~`0x26B000`… | ~30–97 KiB | 1 | `0` / `0xDD` | Non-SEC blobs |
| 8–13 | ~`0x2AB000`… | ~5–6.7 KiB | 1/0 | `0xA` | Small — few PAD128 CLUTs fit |

**Findings:** Root `kind=1` is normal (unlike nested TOC). Shared palette is **more plausible in e=1 or e=8–13** than in identical tile headers. Not a negative result.

**Next:** walk e=1 nested SEC TOC; sample e=8 as 512/1024-byte CLUT-shaped tables — **done §4e**.

---

## 4e. e=1 nest TOC + e=8..13 CLUT sample (Grok)

**Capture:** `DETPS2_TEMP_DEC_E1_E8=1` (fully reverted). Artifact: `out/canaries/gfx-dec-header/e1-e8-sample.txt`.

### e=1 nested SEC (`@0x248000`, sz=`0x22C00`)

| Field | Value |
|-------|--------|
| magic | `SEC ` |
| `+0x10` | `0x18` → **24** TOC entries |
| `+0x18` | `0x22C00` (matches member size) |

Nested TOC **ne=0..23**: all **kind=2**, **sz=0x1700**, first=`0x4000`, mark=`\x18PS2` — **same PAD128 tile slabs as e=0**, not palettes.  
ne≥24: parse runs into **ASCII name table** (`ENDING`, `ON_ENDING`, `GIG`, `PAD128` strings visible as u32 garbage) — not real TOC.

**Verdict:** e=1 is a **second texture nest** (~24 more UI tiles), not a CLUT bank. PL-029 only feeding e=0 means half (or more) of gameart tiles never Host→Local — separate issue from gray indices.

### e=8..13 samples (first 512B)

| Metric | Observation |
|--------|-------------|
| first u32 | `0x0000000A` (10) all six |
| hex head | `0A 00 00 00  00 00 00 00  01 00 00 00  7F 00 00 00  0C 00 00 00 …` structured records |
| uniq / nz | ~45–50 unique, ~90/512 non-zero — **sparse**, not dense CLUT |
| “alpha” stride (+3) | **aFf=0/128, a00=128/128** — never 0xFF; **not** RGBA32 palette with solid alpha |

**Verdict:** e=8–13 are **record/script/param blobs**, not PAD128 CLUT tables.

### Investigation status after §4e

| Hypothesis | Result |
|------------|--------|
| Nested kind=1 palettes | Refuted (nested) |
| Per-tile 0x100 header = palette | Refuted |
| In-payload leftover = palette | Refuted (Claude) |
| Root e=1 = palette SEC | **Refuted — more tiles** |
| Root e=8–13 = CLUT blobs | **Refuted — sparse records** |

**Remaining paths (harder):** EE SSF/texture consumer decompile for CLUT upload; search gameart for `PAD128` string + following 512B tables; TEX0/TEXCLUT GS register traces when natural menu draws.

---

## 4f. Real EE TEX0/TEXCLUT trace (Claude) — decisive negative

**Capture:** `DETPS2_TEMP_CLUT_TRACE=1`, instrumented `Gs.MaybeLoadClut` (called only from `ApplyTex0`/
`ApplyTex2`, i.e. real GS `TEX0`/`TEX2` register writes — **not** PL-029's BITBLT path, which never
touches these registers). Fully reverted after use; `git diff --stat src/DetPS2.Core/Gs.cs` confirmed
empty before rebuild.

100M-cycle Dec trace (`user-media-deception.json`, `--host-present`):

```
Total MaybeLoadClut invocations: 1 (entire 100M-cycle run)
[CLUT-TRACE] MaybeLoadClut tex0=0x0000000220011000 cld=0 texBase=0x40000 psm(tex0field)=0
```

**One single `TEX0`/`TEX2` write in the whole run, and it requests `cld=0`** (no CLUT load at
all). Real EE code essentially **never engages the GS's native indexed-texture/CLUT pipeline**
during this trace window — not "loads the wrong palette," not "loads a palette we can't find" —
it doesn't attempt a CLUT-backed texture draw at all.

**This changes the conclusion.** The missing piece was never a static palette blob sitting
somewhere in `gameart.ssf` waiting to be located by file-layout guessing (five hypotheses on that
front are now refuted: nested `kind=1`, tile header, in-payload leftover, root `e=1`, `e=8–13`).
It is that **the real EE draw code path for this indexed content does not run at all** within the
traced budget/menu state — the same shape of gap as Whiplash's stalled stream producer, just
surfacing differently (a silent no-op instead of a visible stall). PL-029's Host→Local BITBLT feed
was built specifically because the natural path doesn't produce this content — that was already
documented (`MK_DECEPTION.md`: "natural GIF `image=` still 1"); this trace confirms the CLUT side
of that same gap, not a separate problem.

**Recommendation:** park this specific lead. Finishing it for real needs either (a) a longer/later
trace window in case the real texture draw happens further into actual gameplay past the menu
(untested — would need a claim-tier run well past 100M, and even then may not trigger without
deeper interactive state), or (b) an actual EE decompile of the SSF texture consumer to find why
the natural draw path is gated out — the same class of work Whip's MP2 problem needs, at smaller
scope. Not a quick-TRACE-tier question anymore.

---

## 4g. Full-file PAD128 / `\x18PS2` scan (Grok) — no tags outside nests

**Capture:** `DETPS2_TEMP_DEC_PAD128_SCAN=1` (fully reverted).  
`loaded=2836480`.

| Pattern | e0-nest | e1-nest | e2plus / root / other |
|---------|--------:|--------:|------------------------|
| ASCII `PAD128` | 9307 | 867 | **0** |
| Magic `\x18PS2` | 404 | **24** | **0** |

e1 `PS2=24` matches 24 kind=2 tiles. `PAD128` inflated by repeated format string in headers.  
**Zero** hits outside the two texture nests — confirms no separate PAD128-tagged palette bank.

---

## 5. Non-goals

- Do not invent a synthetic/procedural palette to make the gray strip "look better" — that is
  exactly the class of fabrication this whole GFX effort exists to avoid (goefile-as-pixels /
  3bcedb2 stripe-noise class).
- Do not treat "gray but not black" as Tier A — per `gfx-plan-v0.md`, only real recovered color
  (Tier B — driven by real EE/asset data, visually confirmed) counts.
- Do not touch `TryFeedDecGameartHostToLocal`'s tile-feed logic itself until the real palette
  source is confirmed — changing tile parsing without a confirmed CLUT source would not fix
  anything and risks introducing exactly the coherence-check-driven rollback class of bug already
  seen once this session.

---

```text
GFX Dec CLUT investigation — CLOSED for this wave, honest park
  gray = honest PSMT8-without-CLUT fallback; real infra; not fabricated
  PL-029 never loads a CLUT for its 48 fed tiles
  5 static-layout palette hypotheses refuted + full PAD128/PS2 scan (only e0/e1 nests)
  decisive dynamic check: real EE code issues exactly 1 TEX0/TEX2 write in
    100M cycles, cld=0 — native CLUT-texture draw path does not run at all
  conclusion: not a findable static blob; real draw path is gated out,
    same class of gap as Whip's MP2 (smaller scope) — needs EE decompile
  park pending real demand for that investment; not a TRACE-tier fix
```
