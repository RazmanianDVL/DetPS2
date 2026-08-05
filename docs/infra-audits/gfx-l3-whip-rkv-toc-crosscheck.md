# GFX L3 — Whiplash PS2.RKV TOC cross-check (vs audio-only + path strings)

**Status:** measure complete — **no Core**  
**Date:** 2026-08-05  
**Title:** Whiplash (SLUS_206.84)  
**Parents:** `gfx-l3-whip-iso-texture-inventory.md` (`14e8099`), Claude dual-ACK seq0300, `TITLE_HACKS.md` 2026-08-02 audio-only note  
**Author:** Grok

---

## 0. One-line

A full **format-B sequential TOC walk** of `WHIPLASH/PS2.RKV` finds **~2448 file members** (title chrome + ~780 level-cell streams + ~272 `streams/*` + ~1392 `vo/*`). **Zero** TOC names contain `texture`, `levels/`, `objects/`, `material`, or `mpim`. The **622** `levels|objects/.../textures/...` strings from the earlier raw 32 MB scan are **not archive keys** — they live inside payloads as references (hypothesis **b**), not as a missed texture sub-table (hypothesis **a** refuted for TOC names). Texture **pixels** may still nest inside level-cell or title goefile members; they are not first-class RKV TOC entries by those path names.

---

## 1. Method

- Host read of real ISO RKV at LBA `1152865` (same extent as Iso9660 inventory).  
- Header: `ver=1`, `tocBytes=0x1C7F8` (matches live `RealSifRpc` comment).  
- **Sequential format-B walk** from the planted title chain start (`nlen` of `Code` at file `0x37`):  
  `u32 nlen`, `name[nlen]`, `u32 type`, `u32 w1`, `u32 w2`, `u32 pad` — same layout as `ParseRkvTocFromHost` format-B.  
- Resolve offset when `w2` or `w1` lies in `[tocBytes, archiveSize)`.  
- Artifacts: `out/canaries/whip-rkv-toc/toc-sequential.tsv` (gitignored).  
- **No Core.**

---

## 2. TOC census (sequential walk)

| Class | Count | Name shape |
|-------|------:|------------|
| `vo/*` | **1392** | voice lines |
| level cell / other | **780** | e.g. `atr01_cell_*`, `hubmain`, `sec_glob`, `wt_3sewer_cell_*` |
| `streams/*` + `music/*` | **272** | wav/stream paths |
| title chrome | **4** | `Code`, `firstscreen`, `frontend`, `hudscripts` |
| **Total resolved files** | **~2448** | |

Title chain offsets (TOC fields; sizes need WAVE-4 delta repair as in Core — ids sit in `w1`):

| Name | TOC offset field |
|------|------------------|
| `firstscreen` | `0x303A8` |
| `Code` | `0x5D52C` |
| `frontend` | `0xE9834` |
| `hudscripts` | `0x2184D0` |

Matches prior WAVE-4 ground truth for the title offset chain.

---

## 3. Needle search on **TOC names only** (decisive)

| Needle | Hits in TOC member names |
|--------|-------------------------:|
| `texture` / `textures` | **0** |
| `levels/` | **0** |
| `objects/` | **0** |
| `material` / `mpim` / `particle` | **0** |
| `.tga` / `.tim` / `.tm2` | **0** |

Contrast: raw byte-scan of first 32 MB of the **same file** still shows hundreds of embedded path strings. Those strings are **payload content**, not TOC keys.

---

## 4. Hypothesis resolution (Claude seq0300)

| Hypothesis | Result |
|------------|--------|
| **(a)** Prior full TOC walk only recognized vo/wav and **missed a texture sub-table** | **Refuted for TOC names.** Sequential walk lists title + level cells + streams + vo; still no texture-named members. |
| **(b)** 622 path strings are **unindexed leftovers / script references** | **Supported.** Paths do not appear as TOC entry names. |
| Prior “RKV audio-only” one-liner | **Tempered, not fully restated.** RKV TOC is **not** only audio: ~780 level-cell members are first-class. It **is** still true that TOC has **no texture-named pool entries**, and vo+streams dominate name count. |

---

## 5. What this means for the texture pool

1. **There is no RKV TOC key** like `levels/atrium/textures/a_dirt03_a`.  
2. Logical texture paths are referenced from **inside** streamed members (title goefiles, level cells, scripts).  
3. Pixel data, if present on disc at all, is either:  
   - nested inside those members (goefile / cell packages), or  
   - still unlocated under a **non-path** TOC name, or  
   - not stored as raw named blobs in RKV (procedural / runtime synthesis — weaker prior).  
4. Next concrete measure (option 2 from inventory seat): **path → containing member → bytes** — e.g. open `firstscreen`/`frontend`/`hubmain` with known sizes (WAVE-4 repair), scan member body for one texture path string, dump surrounding record; or pick a small level cell and inspect for goefile/texture headers.

---

## 6. Tie-ins

| Seat | Link |
|------|------|
| ISO inventory `14e8099` | No loose ISO textures; only RKV is big enough — still true; refined that TOC does not name the pool. |
| MPIM revise `0202347` | MPIM not materials-by-name; textures not in MP2 TOC either. |
| `TITLE_HACKS` audio-only | Update mental model: audio-heavy + level cells + title chrome; **not** “texture TOC entries exist and were skipped.” |
| `RealSifRpc.ParseRkvTocFromHost` | Format-B + title plant still correct; sliding `+=4` scan under-finds unaligned entries — sequential walk from `Code` is better for census dumps. |

```text
Whip RKV TOC cross-check
  sequential format-B from Code: ~2448 files
  vo~1392 streams~272 level_cells~780 title=4
  TOC name needles texture|levels/|objects/ = ZERO
  622 raw path strings are payload refs (hyp b), not missed TOC sub-table (hyp a)
  prior audio-only tempered: level cells are real TOC members; still no texture keys
  next: path->member->bytes inside title/level goefile (dual-ACK)
```
