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
