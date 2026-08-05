# GFX L3 — Whiplash ISO texture inventory (parallel to MPIM seat)

**Status:** measure complete — **no Core**  
**Date:** 2026-08-05  
**Title:** Whiplash (SLUS_206.84)  
**Parents:** `gfx-l3-whip-mp2-firstlook.md`, `gfx-l3-whip-texture-methodology.md`  
**Author:** Grok (parallel seat while Claude took POWER.MP2 MPIM dig)

---

## 0. One-line

The retail ISO has **no separate texture/pixel-pool files**. The only multi‑hundred‑MB asset container is **`WHIPLASH/PS2.RKV`** (`ArchivePs2` in `GAME.INI`). First 32 MB of RKV already embeds **hundreds of logical texture paths** (`levels/*/textures/*`, `objects/*/textures/*`). Combined with Claude’s MPIM revise (`0202347` — MPIM is not materials-by-name), the shared texture pool lead is **inside RKV’s stream archive**, not MAP/\*.MP2 and not a missing loose ISO file.

---

## 1. Method

- Real ISO via existing `Iso9660.OpenFile` (`user-media-whiplash.json` path).  
- Full volume file list (recursive directory parse already in `Iso9660`).  
- `GAME.INI` / `NEWGAME.DAT` via `ReadFile`.  
- RKV: raw `FileStream` at file extent LBA; string-scan first 1 MB + first 32 MB for path-like / texture-ish ASCII.  
- Artifacts (gitignored): `out/canaries/whip-iso-tex-inv/`.  
- **No Core.**

---

## 2. Whole-ISO inventory

| Fact | Value |
|------|------:|
| Volume id | `SLUS_20684` |
| Total file entries | **35** (incl. dirs in listing count of file nodes ~30 files) |
| Dominant blob | `WHIPLASH/PS2.RKV` **1 384 263 680** bytes (~1.29 GiB) |
| Map packages | 14× `WHIPLASH/MAP/*.MP2` (68 B–394 604 B) |
| ELF | `SLUS_206.84` 3 294 840 |
| IOP modules | `WHIPLASH/BIN/*.IRX` (+ `IOPRP255.IMG`) |
| Other | `GAME.INI`, `NEWGAME.DAT`, `ICONS/…` |

### Extension histogram (files only)

| Ext | Count | Notes |
|-----|------:|-------|
| MP2 | 14 | levels only |
| IRX | 9 | IOP |
| RKV | 1 | **archive** |
| DAT / INI / ICN / IMG / CNF / ELF | 1 each | config / chrome / boot |

**No** `.TIM` / `.TXD` / `.TM2` / standalone texture extensions appear anywhere on the disc.

### `GAME.INI` (load-bearing)

```
string "ArchivePs2"="whiplash/ps2.rkv";
string "gamepath"="whiplash";
```

The game itself declares RKV as **the** PS2 archive. There is no second archive path for textures.

### Large files outside `MAP/`

Only RKV (1.29 GiB), the ELF (~3.3 MB), and IOP images/modules. Nothing texture-shaped as a loose file.

---

## 3. RKV string scan (first 32 MB)

Not a full TOC decode this seat — only an existence/path lead.

- First 1 MB: ~7k printable strings (mixed noise + UI: `Loading...`, Eidos copyright, etc.).  
- First 32 MB filter for texture-related substrings: **1596** hits.  
- Clean unique paths matching `levels/…` or `objects/…` **and** containing `texture`: **622**.

### Sample paths (real logical names)

```
levels/atrium/textures/a_dirt03_a
levels/atrium/textures/console_panels
levels/atrium/textures/door_02_basic
objects/particle/textures/rutherford/smoke
objects/particle/textures/glows/glow_grey
objects/pow_coin/textures/glow_green
```

### Path buckets (clean set)

| Prefix | Count (approx) |
|--------|---------------:|
| `levels/atrium/textures/*` | 238 |
| `objects/particle/*` | 102 |
| `levels/commontextures/*` | 29 |
| `objects/interactive/*` | 28 |
| `levels/medlab/*` / `hub/*` / `ceotower/*` / `power/*` … | smaller |

These are **logical resource paths**, not ISO paths — they live as ASCII inside RKV payloads (scripts, goefile records, or resource tables). That is enough to locate the pool: **whatever serves `levels/…/textures/…` is reached through the RKV archive system**, not a missing file next to MAP.

---

## 4. Tie-in to Claude MPIM result (`0202347`)

| Prior guess | Status after dual seats |
|-------------|-------------------------|
| MPIM = materials-by-name → texture names | **Refuted** (Claude: pure binary instance records) |
| Separate ISO texture files outside MP2 | **Refuted** (this inventory: none exist) |
| Texture pool location | **Narrowed to PS2.RKV** (path strings + sole archive) |

MP2 remains geometry/placement (MAP0/MPSN/MPGM/MPIM). Texture **pixels** (if not procedural) almost certainly stream from RKV under those logical names (or under TOC names that map to them).

---

## 5. What this does / does not claim

**Established**

1. Disc surface has no loose texture assets.  
2. RKV is the only plausible shared pool container.  
3. RKV contains a large set of `levels/*/textures/*` and `objects/*/textures/*` path strings.  
4. Inventory is consistent with Claude’s MPIM revise.

**Not established (next seats, dual-ACK)**

1. Full RKV TOC layout (prior work claimed audio-heavy TOC — may still be true for *named streams*, with textures under different stream classes or nested goefiles).  
2. Whether a given path string points at **pixel bytes** vs **script-only** references.  
3. How EE/GOE stream-table resolves those paths at runtime (ties existing RealSifRpc GOE work).  
4. Full 1.29 GiB RKV scan (this seat only first 32 MB for paths).

---

## 6. Next (no Core)

1. **RKV TOC / stream name dump** (existing tooling if any; else bounded RE) — count non-`vo/*` / non-`streams/wav/*` entries; search TOC for `texture` / `levels/` / `frontend` / `firstscreen` / `Code`.  
2. **Resolve one path → bytes** — pick e.g. `levels/atrium/textures/black` or a particle glow; show offset+size or prove script-only.  
3. Optional: full-RKV path harvest (not required if TOC already lists them).

```text
Whip ISO texture inventory
  35 files on disc; no loose texture assets
  GAME.INI ArchivePs2=whiplash/ps2.rkv
  RKV first 32MB: 622 clean levels|objects .../textures/... paths
  pool lead = inside RKV, not MAP/*.MP2, not a missing ISO file
  pairs with Claude MPIM revise (not materials-by-name)
  next: RKV TOC / one path->bytes resolve (dual-ACK)
```
