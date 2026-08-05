# GFX L3 — Whiplash MP2 bounded first-look (not full format decode)

**Status:** first-look only, no Core — confirms container hierarchy, does not decode MPGM payload
**Scope:** locate/inspect the real `.MP2` container structure per `gfx-l3-whip-texture-methodology.md` §2. Not a commitment to finish the full VU1-microcode format.

---

## Method

Read the real `WHIPLASH/MAP/*.MP2` files directly off the real ISO
(`C:/Users/xxraz/Downloads/Whiplash(USA).iso`) via the existing real `Iso9660` reader
(`Iso9660.OpenFile`/`FindFile`/`ReadFileRange` — the same code the real boot path uses, not a
simulated/synthetic parse). Temp diagnostic (`Tests/TempMp2FirstLook.cs`, gated on
`DETPS2_TEMP_MP2_ISO`) added, run, fully reverted (`git status` clean after, confirmed).

14 `.MP2` files found under `WHIPLASH/MAP/`. `BASEMENT.MP2` is a 68-byte stub (empty/unused
level). `POWER.MP2` (394,604 bytes) is the largest — used as the real-content sample.

## Result — confirmed chunk hierarchy

```
offset 0x0000  "goefile" + size field (~0x605AC, matches file size)
offset 0x000C  "symlist" + count field
offset ~0x0014 null-terminated ASCII name table: "power", "map_plasma_core_01",
               "power/plasma_core", "power_door_bottom_air_blast", ... (level object names)
offset 0x08AC  tag "MAP0"  next8=0000FFFF0E000000
offset 0x08B8  tag "MPSN"  next8=0100010000000000
offset 0x08C4  tag "MPGM"  next8=79C909C78718CCC6   <- binary from here, not ASCII
```

This **exactly matches** the design doc's §2 hypothesis (`goefile → MAP0 → MPGM`), plus one
previously-unlisted intermediate chunk (`MPSN`, between `MAP0` and `MPGM`, 12 bytes long: tag
+ 8 bytes `01 00 01 00 00 00 00 00`). `MPGM`'s payload begins immediately after its own
12-byte tag+header and is genuinely non-ASCII binary — consistent with "VU1-microcode-packed
vertex blobs" per the doc, not more text/symbol data.

## What this does and doesn't establish

**Established:** the container hierarchy is real and locatable via the real ISO reader; the
symlist name table gives per-object names (useful for later cross-referencing which MPGM
blob is which visible object); MPGM's payload is real binary geometry/microcode data, not a
red herring.

**Not established (deliberately out of scope this seat, per the doc's own §6 non-goal):**
the actual MPGM binary layout (VU1 unpack format, vertex/index structure), where `MPIM`
(materials, referenced by the design doc as texture-by-name) actually appears in this file
(not found in the first 4096 bytes of POWER.MP2 — likely further into the MPGM payload or a
separate chunk after it), and — critically — where the **shared texture pixel pool** itself
lives, which the design doc already flagged as still entirely unlocated even after chunk
decode. None of that is resolved by this first-look; it remains the larger, separate
multi-session RE effort.

## Next step (not started)

If/when this becomes a claimed seat again: scan further into POWER.MP2 past the MPGM chunk
for an `MPIM` tag (materials-by-name), and check whether its record format gives a lead on
where the actual texture pool file/chunk lives — that would be the next concrete, bounded
sub-step, not a jump to full VU1 decode.

```text
Whip MP2 first-look
  goefile -> symlist (name table) -> MAP0 -> MPSN -> MPGM (binary, real data)
  confirmed via real Iso9660 reader against real ISO, not simulated
  MPIM (materials) not found in first 4KB of POWER.MP2 — next lead if resumed
  texture pixel pool location: still unknown, out of scope this seat
```

---

## Follow-up: MPIM found (whole-file scan) — content is NOT materials-by-name, revises the hypothesis

Resumed per the note above. Temp diagnostic (`Tests/TempMp2MpimScan.cs`, gated
`DETPS2_TEMP_MP2_ISO`, fully reverted after — `git status` clean) scanned the **entire**
POWER.MP2 (not just the first 4KB) for chunk tags, using the real `Iso9660` reader against
the real ISO.

### Result

**14 `MPIM` occurrences**, each part of a repeating per-object triplet:
`MPIM → MPSN → MPGM`, roughly matching the ~14 real named sub-objects visible in the
`symlist` name table from the original first-look (door/tank/entrance objects etc.) — i.e.
each mesh/object in the map gets its own materials+geometry group, not one global table.

Dumped the first `MPIM`'s payload in full (`0x2B14`):

```
MPIM 00000000 18000000  <- tag, then count = 0x18 = 24 records
0200 0300 0300 0100  7BAC07C7 9461D2C6 7B26FFC6
0200 0400 0300 0200  6CB307C7 2EFAE0C6 A940FFC6
0200 0500 0300 0300  9C0A0BC7 61BFC5C6 7B26FFC6
0200 0600 0300 0400  E4020BC7 574FB7C6 0541FFC6
... (continues for all 24 records, same shape)
```

Each record: 4× `uint16` (first field constant `0x0002`, second incrementing, third constant
`0x0003`, fourth incrementing) followed by 3× `float32` (values in the hundreds-to-thousands
range by their exponent bytes — shape consistent with per-instance position/transform data,
not text).

### What this means — revises the original hypothesis

**MPIM's content is purely binary numeric records — no embedded ASCII anywhere in the 512
bytes dumped.** This contradicts the design doc's original guess ("Materials reference
textures by name via MPIM chunks") — MPIM is not a texture-by-name lookup table. Its shape
(index pairs + 3 floats, 24 repeats) looks much more like a **placement/instance table**
(e.g. per-object transform or position entries) than a materials/texture reference list.

This means the search for where texture-by-name (or texture-by-index) references actually
live needs to look elsewhere — most likely either inside `MPGM`'s own binary geometry stream
(material index numbers referencing a table elsewhere, not names, not here), or the texture
pool is a genuinely separate mechanism entirely (fitting well with Grok's parallel
whole-ISO texture-file inventory — if names aren't inside MP2's chunks at all, the real
texture data more plausibly lives as separate files elsewhere on the disc).

### Still not started

Full VU1/MPGM decode, and confirming whether MPGM's binary stream contains material-index
references at all — both out of scope for this bounded follow-up, same as the original
first-look's own non-goals.

```text
MPIM follow-up
  14 MPIM occurrences in POWER.MP2, each in a MPIM->MPSN->MPGM per-object triplet
  MPIM payload = 24 binary records (2x uint16 pairs + 3x float32), zero ASCII text
  NOT a materials-by-name table -- original hypothesis revised/refuted
  looks more like per-object placement/instance data
  real texture references likely live in MPGM's stream (by index) or as separate ISO files
```
