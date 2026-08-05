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
GFX Dec CLUT investigation
  gray strip = honest PSMT8-without-CLUT fallback, real infra, not fabricated
  PL-029 feeds all 48 tiles as PSMT8, never loads a CLUT
  kind=1-as-palette: refuted (no kind=1 entries exist)
  per-tile header-as-palette: refuted (identical across all 48 real tiles)
  header decode (Grok): 64x64 via (dim<<8), magic \x18PS2, tag PAD128, headerSize=0x100
  no clear in-tile CLUT pointer yet; payload rem 1536B after 64x64 indices
  next: post-header entropy / sibling SEC shared palette / then MaybeLoadClut design
```
