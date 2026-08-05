# GFX L3 — Whiplash runtime name → pixel resolve (30M)

**Status:** measure complete — **no Core**; temp FILEIO log fully reverted  
**Date:** 2026-08-05  
**Title:** Whiplash (SLUS_206.84)  
**Parents:** `gfx-l3-whip-rkv-path-to-bytes.md` (`d0d8037`), user correction resume (seq0308)  
**Author:** Grok

---

## 0. One-line

Over **30M** cycles with live GOE stream delivery of `Code` / `firstscreen` / `frontend` into EE ring `0x0045BC94`, the EE issues **exactly one** FILEIO open (`GAME.INI`). It **never** opens any `levels/.../textures/...` or other texture-shaped path via FILEIO/RKV. Texture path strings therefore are **not resolved as separate archive opens** in this window — they only ride inside the already-streamed goefile members (prior path-to-bytes seat). No non-script “pixel load” stage appears after name materialization in the title ring.

---

## 1. Method

- Temp `DETPS2_TEMP_WHIP_TEX=1` on `RealSifRpc` FioOpen: log every open + RKV hit/miss for asset-shaped paths.  
- Also `DETPS2_TRACE_RPC=1` for IOPFILE stream-table / whip stream service.  
- `blocker-trace user-media-whiplash.json --cycles=30000000 --host-present`.  
- **Fully reverted** (`RealSifRpc.cs` clean). Artifacts: `out/canaries/whip-runtime-tex/`.

---

## 2. FILEIO / RKV open census (30M)

| Event | Count / detail |
|-------|----------------|
| `[WHIP-TEX] FIO_OPEN` | **1** — `cdrom0:\WHIPLASH\GAME.INI;1` discFd=1 texish=False |
| RKV_HIT / open RKV (texture-shaped) | **0** |
| RKV_MISS (texture-shaped) | **0** (no such open attempted) |
| Other FILEIO open (TRACE) | same single GAME.INI |

Conclusion: runtime does **not** look up texture path names as FILEIO/RKV keys in this boot window.

---

## 3. What **does** load (GOE stream-table)

RKV mounted (format-B, title sizes repaired). Stream service rotates:

| Member | Size (repaired) | Delivery |
|--------|----------------:|----------|
| `Code` | `0x8C308` (574 216) | progressive 4 KiB chunks → `0x0045BC94` |
| `firstscreen` | `0x2D184` (184 708) | same ring |
| `frontend` | `0x12EC9C` (~1.24 MiB) | same ring; **partial** by 30M (~187 KB Code pos, frontend still mid) |

This matches prior methodology: honest title goefile streaming, **not** per-texture opens. Those members **contain** the path strings (offline seat) but delivery is whole-member script/param data.

---

## 4. Product state @ 30M

```
px=286720 prims=1 imgBytes=0 lit=0 mostlyBlack=1
gif IMAGE tags=0  path3=2 small
```

Full-FB clear class only — consistent with no texture upload.

---

## 5. Interpretation

| Prior offline finding | Runtime confirmation |
|----------------------|----------------------|
| Paths are goefile/symlist name bindings | No FILEIO resolve of those names |
| Texels not at string site | No separate pixel open stage observed |
| Title chrome is script containers | Streamed as Code/firstscreen/frontend only |

**Next concrete angles (keep going, build tooling if needed):**

1. **After firstscreen 100% + frontend further:** does any second-wave open appear (level cell TOC names like `hubmain`, or texture-shaped)? Extend cycles with same WHIP-TEX log (re-apply temp).  
2. **In-memory resolve:** `--find-string=levels/frontend/arrow` (or particle path) after N cycles + `--find-writer` on the string address — who materializes the name, and what PC follows?  
3. **GIF IMAGE still zero:** until `imgBytes>0`, pixel path may be gated on script interpreter progress past stream stall (producer stops after ~1 MB total historically).

```text
Whip runtime name->pixel (30M)
  FILEIO opens: only GAME.INI
  zero texture-shaped FIO/RKV opens
  GOE streams Code/firstscreen/frontend into EE ring (script members)
  path strings not resolved as separate loads in this window
  next: longer run / find-string on path in EE RAM / after more stream progress
```
