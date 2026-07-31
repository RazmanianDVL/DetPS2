# Soft-GS title-strip expand policy (GX-004 / GX-021)

**Owner seat:** S9 GFX-RASTER (`Gs.cs` `DrawSprite`, `GsRegisters.GetXyOffset`)  
**Status:** ACTIVE temporary crutch with telemetry (`ExpandHits`)  
**Demotion gate:** G-GFX-6 — expand hits → 0 on ≥6 titles while px floor held  
**Doctrine:** Soft-GS truth · no invent PATH3 · no planted host pixels · FLOAT_POLICY · SEMA_OFF claims

---

## 1. Why expand exists

Commercial title menus often submit **full-width thin SPRITEs** (logo clear / title strip) whose screen-space height collapses to 1–N rows after XYZ mapping:

| Class | ofx / ofy at kick | Example | Collapse |
|-------|-------------------|---------|----------|
| **ofx=0** | `XYOFFSET=0` | GoW Path2 WAVE-12B | `(0,0)+(512,0)` → 512×1 → px≈512/prim |
| **ofx=0x8000** | retail center 2048.0 (12.4) | Whiplash, BO2 | pure Y=−2048 → Y-rescue → h=1 strip |
| **retail-center band** | ofx,ofy ∈ `[0x6000,0x9000]` | B3-class offsets | partial logo bands / collapse only |

Without expand, Soft-GS reports near-zero chrome (`px=1026` residual class) even though the game submitted real Path2/PRIM color. Expand scales the **collapsed** strip to the Soft-GS title FB so MENU-class claims hold **while** Path/DISPFB/IMAGE work lands.

**Expand is not permanent strategy.** See `NEXT_PLAN.md` / G-GFX-5/6.

---

## 2. XYOFFSET truth (GX-021)

Sony/Play! `XYOFFSET_1` (reg `0x18`):

| Field | Bits | Format |
|-------|------|--------|
| OFX | 15:0 | unsigned 16-bit **12.4** fixed (same unit as XYZ X) |
| OFY | 47:32 | unsigned 16-bit **12.4** fixed (same unit as XYZ Y) |

**Pure screen map** (`GsRegisters.MapScreenXy12_4`):

```text
x = (xRaw - ofx) >> 4
y = (yRaw - ofy) >> 4
```

Soft-GS may apply **rescues** only when pure map is off-FB or OFX/OFY are unprogrammed (`0/0`). Rescues do not invent PATH3 or plant pixels; they re-interpret XYZ packing so commercial strips can reach `DrawSprite`.

Helpers:

- `GsRegisters.IsRetailCenterOffset(ofx, ofy)` — both ∈ `[0x6000,0x9000]` (includes `0x8000`)
- `GsRegisters.IsCollapseOffsetClass(ofx, ofy)` — `0/0`, exact `0x8000/0x8000`, or retail-center band

---

## 3. Legal expand conditions (must all hold)

Implemented in `Gs.DrawSprite` (`src/DetPS2.Core/Gs.cs`):

```text
collapseOfs = IsCollapseOffsetClass(ofx, ofy)
titleStrip  = collapseOfs && w ≥ FB_WIDTH/2 && h < FB_HEIGHT/2

// GX-021: kill illegal expand when ofx is retail-center and strip is already natural on-FB
if titleStrip && IsRetailCenterOffset(ofx, ofy)
   && !fullyOffFb && h >= ExpandRetailNaturalMinH (2):
    titleStrip = false   // do NOT expand; ExpandHits unchanged
```

When `titleStrip` remains true:

| Case | Action | `ExpandHits` |
|------|--------|--------------|
| Sprite fully off-FB after clip | Clamp origin; height → `FB_HEIGHT` | +1 |
| Sprite partially on-FB collapsed strip (h=1 class) | Expand rect to full `640×448` Soft-GS FB | +1 only if w/h actually grow |

**Preserved from the real prim:** color, UV/ST, fog, Z.  
**Not done:** invent GIF PATH3, plant host logos, force DISPFB, change prim type.

**Not expand (no `ExpandHits`):**

- Off-FB clamp when `!titleStrip` (generic commercial rescue only)
- Retail-center ofx + on-FB natural height (`h ≥ 2`) — **illegal expand killed (GX-021)**
- Normal quads / full-height sprites / non-collapse ofx classes

---

## 4. When retail ofx should disable expand (demotion prep)

**Do not remove expand on a title without MENU-hold proof** (px floor + claim graph). Prep rules for G-GFX-6 / GX-043:

| Condition | Expand? | Rationale |
|-----------|---------|-----------|
| `IsRetailCenterOffset` + pure 12.4 map places full-width sprite on-FB with **natural height** (logo/UI band already sized) | **No** | GX-021 illegal kill; paint natural size |
| `IsRetailCenterOffset` + pure map off-FB / Y-collapse to h=1 (Whip/BO2) | **Yes (temp)** | MENU chrome until natural PRIM/DISPFB/IMAGE holds px |
| ofx=ofy=0 + Path2 Y=0 strip (GoW) | **Yes (temp)** | Unprogrammed OFX; collapse class until retail XYOFFSET+PRIM |
| DISPFB/IMAGE/`imgBytes` already supply title surface | **Prefer off** | Expand px is not natural progress (P2/P3) |
| Gameplay multi-prim textured scene | **Must be 0** | G-GFX-6 / post-menu NATURAL |

**Demotion order (title seats consume; S9 owns gate):**

1. Fix Path / FRAME / DISPFB / IMAGE so px floor holds without expand  
2. Arm retail XYOFFSET + natural PRIM sizes in claim window  
3. Confirm `expandHits=0` with MENU hold (GX-043/046/048)  
4. Only then drop collapse-class expand for that title  

---

## 5. Telemetry

| Field | Where | Meaning |
|-------|-------|---------|
| `Gs.ExpandHits` | Soft-GS counter | SPRITEs that **actually** expanded (rect grew) |
| `softgs: … expandHits=N` | `blocker-trace` claim lines | Per-run total |
| `expandHits` | `scoreboard-metrics` JSON | Same |

**Target (G-GFX-6):** claim-window `expandHits=0` on ≥6 titles with px floor held via retail XYOFFSET + natural PRIM size (or DISPFB/IMAGE).

**S1 baseline (diagnose 20M, SEMA_OFF):** GoW `expandHits=2` (2× ofx=0 strip); Whip `expandHits=1` (0x8000 collapse). Accurate counters, not demoted yet.

**S2-G2 hold:** expandHits policy **unchanged** while G2 IMAGE/TEX lands (GX-025…035). Do **not** remove collapse-strip expand without MENU hold (forbidden: GoW/Whip demotion without proof).

---

## 6. Forbidden / freezes

- Do **not** broaden ofx classes without S9 review + smoke + fleet diagnose.
- Do **not** remove expand without MENU hold proof on the title.
- Do **not** treat expand px as natural DISPFB/IMAGE progress (P2/P3 still require imgBytes/dispfb/prims growth).
- Title seats (S1–S7) must **not** invent Soft-GS PATH3 plants or ofx hacks without S9.
- FLOAT_POLICY and SEMA_OFF claims unchanged.

---

## 7. Smoke

- `Gs_Xyz2_Kicks_Xyz3_DoesNot` — GX-018 Play! map hold (0x05 kick / 0x0D no-kick).
- `Gs_Path2_Ofx0_Y0_Sprite_ExpandsTitleSurface` — ofx=0 Y=0 Path2 strip → title floor + `ExpandHits≥1`.
- `Gs_RetailOfx_NaturalHeight_DoesNotExpand` — ofx=0x8000 full-width natural h → `ExpandHits=0`.
- `Gs_Ofx8000_CollapsedStrip_StillExpands` — ofx=0x8000 Y-collapse strip → expand + `ExpandHits≥1` (MENU hold).

---

## 8. Related

- Gate table: [GRAPHICS_PIPELINE_PHASE_PLAN.md](../GRAPHICS_PIPELINE_PHASE_PLAN.md) G-GFX-6, GX-004, GX-018, GX-021, GX-043…048  
- XYZ ofx mapping: `Gs.AddVertexFromXyz` (pure 12.4 first; rescues secondary)  
- Discovery: [DISCOVERY_LOG.md](DISCOVERY_LOG.md)
