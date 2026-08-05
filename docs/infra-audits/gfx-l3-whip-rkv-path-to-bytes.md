# GFX L3 — Whiplash RKV path → member → bytes

**Status:** **PARKED** (2026-08-05 dual-ACK) — names closed as goefile bindings; texels open; next-session scale  
**Date:** 2026-08-05  
**Title:** Whiplash (SLUS_206.84)  
**Parents:** `gfx-l3-whip-rkv-toc-crosscheck.md` (`de1e9df`), Claude dual-ACK seq0302 / park seq0304  
**Author:** Grok

---

## 0. One-line

Resolved several `levels|objects/.../textures/...` path strings to **concrete RKV members** (`firstscreen`, `Code`, `frontend`, `hudscripts`, sample `hubmain`). In every case the path is a **null-terminated ASCII name inside a goefile/symlist (script/param) record**, surrounded by floats and script fields — **not** a TIM2/pixel blob at that site. **TIM2 magic count = 0** across full title members + 512 KiB hubmain sample. Pool pixels remain unlocated; what we closed is “where the path strings live” (script bindings), not “where the texels are.”

---

## 1. Method

- Title members extracted with WAVE-4 offset chain from TOC cross-check:  
  `firstscreen@0x303A8` size `0x2D184`, `Code@0x5D52C` size `0x8C308`, `frontend@0xE9834` size `0x11EC9C`, `hudscripts@0x2184D0` sample 1 MiB.  
- Level sample: `hubmain` TOC off `0x633BC4`, first 512 KiB.  
- Host read from real ISO RKV extent; string/path scan; context dumps around path needles; `goefile`/`symlist`/`TIM2` magic counts.  
- Artifacts: `out/canaries/whip-rkv-path2bytes/` (gitignored).  
- **No Core.**

---

## 2. Member summary

| Member | Size used | `goefile` | `symlist` | `TIM2` | `levels|objects` paths | Role of paths |
|--------|----------:|----------:|----------:|-------:|------------------------:|---------------|
| `firstscreen` | 184 708 | 0 | 57 | **0** | 5 | particle texture **names** + float params |
| `Code` | 574 216 | 2 | 230 | **0** | 47 | UI/frontend + commontextures **name refs** in scripts |
| `frontend` | 1 174 684 | 1 | 284 | **0** | 141 | same; larger UI/script surface |
| `hudscripts` | 1 MiB sample | 0 | 608 | **0** | 35 | particle/HUD texture **names** |
| `hubmain` (sample) | 512 KiB | 1 | yes | **0** | 32+ | level material **name refs** (hub/genetics/commontextures) |

Printable fraction ~14–23%, zeros ~34–51% — consistent with mixed binary script/param containers, not packed image banks.

---

## 3. Context proof (path is a binding, not pixels)

### 3.1 `firstscreen` — particle path + floats

At member `0xAC3D`, path `objects/particle/textures/rutherford/smoke` is null-terminated, preceded by float patterns (`0x3F800000` = 1.0f class) and followed by more floats/flags. Prev `symlist` ~693 bytes earlier. **No `goefile` in this member** (title surface uses `symlist`/bscript-shaped data; head is not `goefile` magic).

### 3.2 `Code` / `frontend` — UI script name tables

Nested `goefile` + `symlist` present (e.g. Code `@0x1CAD4`, `@0x4D2D4`; frontend `@0xAAFCC`). Paths such as `levels/frontend/arrow` and `levels/commontextures/white` sit in long **ASCII identifier streams** with neighboring script symbols (`uiwidgets/...`, `widthoffset`, HUD icon paths). Classic Crystal Dynamics goefile symbol/param layout — matches prior TITLE_HACKS note that firstscreen/frontend/Code are **not** raw textures.

### 3.3 `hubmain` — level goefile material names

`goefile` `@0x39C3C` then `symlist`; within a few KB, paths like:

- `levels/hub/textures/hubmain01/railing_pillar_side`
- `levels/power/textures/target`
- `levels/commontextures/flume_water_3_001` …

Interleaved with object instance names (`basement/bas_beatbox*003`). Again: **name list / material table**, not texel payload at the string site.

---

## 4. What is closed vs open

| Closed | Still open |
|--------|------------|
| Path strings live **inside RKV members** (title + level cells) | Where **pixel bytes** for those names are stored |
| Binding mechanism shape: goefile/symlist ASCII names + params | Runtime resolver: name → upload → GIF IMAGE |
| Not TOC keys (prior seat); not loose ISO files | Whether pixels nest later in same member past sample window |
| Not TIM2 under scanned ranges | Alternate formats (custom CD texture pack, compressed bank) |

---

## 5. Next (dual-ACK before more RE)

1. **Runtime path:** when EE/GOE actually resolves one of these names (stream-table / FILEIO / GOE open) — does any RKV sub-open or EE buffer receive non-script payload?  
2. **Broader member scan:** full `hubmain` size (delta to next TOC offset) for TIM2 / high-entropy image planes after the symlist region.  
3. **Name → hash/id:** some engines store texels under numeric IDs; search TOC/other for records keyed by hash of path (heavier).  

Recommend (1) or (2) as next bounded measure; no Core invent of textures.

```text
Whip RKV path->member->bytes
  firstscreen/Code/frontend/hudscripts/hubmain contain path strings
  all observed as goefile/symlist ASCII bindings + floats/script fields
  TIM2=0 in full title members + hubmain 512KB sample
  closed: where names live; open: where texels live
  next: runtime resolve or full level-cell body scan (dual-ACK)
```

---

## 6. Parking (dual-ACK Claude seq0304)

Chain for tonight: ISO inventory → TOC census → path→member→binding context.  
**Honest bound:** name residency closed; texel source next-session (runtime resolve or deep member/hash RE). No Core. Resume only with a fresh angle + dual-ACK.
