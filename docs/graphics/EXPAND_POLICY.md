# Soft-GS title-strip expand policy (GX-004)

**Owner seat:** S9 GFX-RASTER (`Gs.cs` `DrawSprite`)  
**Status:** ACTIVE temporary crutch with telemetry (`ExpandHits`)  
**Demotion gate:** G-GFX-6 — expand hits → 0 on ≥6 titles while px floor held  
**Doctrine:** Soft-GS truth · no invent PATH3 · no planted host pixels · FLOAT_POLICY · SEMA_OFF claims

---

## 1. Why expand exists

Commercial title menus often submit **full-width thin SPRITEs** (logo clear / title strip) whose screen-space height collapses to 1–N rows after XYZ mapping:

| Class | ofx / ofy at kick | Example | Collapse |
|-------|-------------------|---------|----------|
| **ofx=0** | `XYOFFSET=0` | GoW Path2 WAVE-12B | `(0,0)+(512,0)` → 512×1 → px≈512/prim |
| **ofx=0x8000** | retail center 2048.0 (12.4) | Whiplash, BO2 | Y=0 both corners → h=1 strip |
| **retail-center band** | ofx,ofy ∈ `[0x6000,0x9000]` | B3-class offsets | partial logo bands |

Without expand, Soft-GS reports near-zero chrome (`px=1026` residual class) even though the game submitted real Path2/PRIM color. Expand scales the strip to the Soft-GS title FB so MENU-class claims hold **while** Path/DISPFB/IMAGE work lands.

**Expand is not permanent strategy.** See `NEXT_PLAN.md` / G-GFX-5/6.

---

## 2. Legal conditions (must all hold)

Implemented in `Gs.DrawSprite` (`src/DetPS2.Core/Gs.cs`):

```text
retailOfs =
    (ofx == 0 && ofy == 0)
 || (ofx == 0x8000 && ofy == 0x8000)
 || (ofx ∈ [0x6000,0x9000] && ofy ∈ [0x6000,0x9000])

titleStrip = retailOfs && w ≥ FB_WIDTH/2 && h < FB_HEIGHT/2
```

When `titleStrip`:

| Case | Action | `ExpandHits` |
|------|--------|--------------|
| Sprite fully off-FB after clip | Clamp origin; height → `FB_HEIGHT` | +1 |
| Sprite partially on-FB thin strip | Expand rect to full `640×448` Soft-GS FB | +1 |

**Preserved from the real prim:** color, UV/ST, fog, Z.  
**Not done:** invent GIF PATH3, plant host logos, force DISPFB, change prim type.

**Not expand (no `ExpandHits`):** off-FB clamp when `!titleStrip` (generic commercial rescue only).

---

## 3. Telemetry

| Field | Where | Meaning |
|-------|-------|---------|
| `Gs.ExpandHits` | Soft-GS counter | Number of SPRITEs that fired the expand path |
| `softgs: … expandHits=N` | `blocker-trace` claim lines | Per-run total |
| `expandHits` | `scoreboard-metrics` JSON | Same |

**Target (G-GFX-6):** claim-window `expandHits=0` on ≥6 titles with px floor held via retail XYOFFSET + natural PRIM size (or DISPFB/IMAGE).

---

## 4. Forbidden / freezes

- Do **not** broaden ofx classes without S9 review + smoke + fleet diagnose.
- Do **not** treat expand px as natural DISPFB/IMAGE progress (P2/P3 still require imgBytes/dispfb/prims growth).
- Title seats (S1–S7) must **not** invent Soft-GS PATH3 plants or ofx hacks without S9.
- FLOAT_POLICY and SEMA_OFF claims unchanged.

---

## 5. Smoke

- `Gs_Path2_Ofx0_Y0_Sprite_ExpandsTitleSurface` — ofx=0 Y=0 Path2 strip → title floor + `ExpandHits≥1`.

---

## 6. Related

- Gate table: [GRAPHICS_PIPELINE_PHASE_PLAN.md](../GRAPHICS_PIPELINE_PHASE_PLAN.md) G-GFX-6, GX-004, GX-043…048  
- XYZ ofx mapping: `Gs.MapVertex` (0x8000 origin rescue; separate from strip expand)  
- Discovery: [DISCOVERY_LOG.md](DISCOVERY_LOG.md)
