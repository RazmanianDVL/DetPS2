# Soft-GS PSM fleet matrix (GX-006 skeleton)

**Owner seat:** S9 GFX-RASTER  
**Status:** SKELETON — fill from oracle / claim softgs-regs + live TEX0.PSM samples  
**Code paths:** `Gs.SampleTexel`, `Gs.BytesPerPixel`, TRX/BITBLT upload, `SwizzleOffset32` / `SwizzleOffset8`

---

## 1. Formats known in Soft-GS code

| PSM | Code | Sample path | Swizzle | Notes |
|-----|------|-------------|---------|-------|
| PSMCT32 | `0x00` | `SampleTexel` default | page/block 32 | primary FB/tex |
| PSMCT24 | `0x01` | bpp only | — | TRX size; sample TBD |
| PSMCT16 | `0x02` | RGB555 expand | linear (TODO swizzle) | logo/tex residual |
| PSMCT16S | `0x0A` | bpp only | — | TRX size |
| PSMT8 | `0x13` | index→CLUT | page/block 8 | Midway/Dec residual class |
| PSMT8H | `0x1B` | bpp only | — | high nibble twin |
| PSMT4 | `0x14` | 4-bit index→CLUT | linear | partial |

CLUT target assumed PSMCT32 palette in `_clut[256]`.

---

## 2. Per-title matrix (fill at claim)

| Title | Menu TEX0.PSM | IMAGE TRX PSM | FRAME PSM | imgBytes@claim | Notes |
|-------|---------------|---------------|-----------|----------------|-------|
| God of War | TBD | 0 (imgBytes=0) | TBD | 0 | expand title strip |
| Whiplash | TBD | TBD | TBD | TBD | expand title strip |
| Blood Omen 2 | TBD | TBD | TBD | 0 | expand logo band |
| Burnout 3 | TBD | TBD | TBD | >0 | merge composite residual |
| MK Shaolin Monks | TBD | TBD | TBD | TBD | assist PATH3 chrome |
| MK Deadly Alliance | TBD | TBD | TBD | TBD | Midway keep-alive |
| MK Deception | TBD | TBD | TBD | residual | gameart IMAGE |
| Vexx | TBD | TBD | TBD | TBD | STREE0 surface |
| Haven | TBD | TBD | TBD | residual | IMAGE chrome |

---

## 3. How to sample

1. Claim / diagnose with SEMA_OFF; scrape `softgs-regs` + TEX0 from reg dump when GX-022 lands.  
2. Optional: `DETPS2_TRACE_GIF=1` (S8) for TRXDIR/BITBLTBUF DPSM.  
3. Oracle: Play! / PCSX2+PINE FRAME/TEX/DISPFB (see PLAY_HLE_ORACLE.md).  
4. Update this table; link residuals to G-GFX-3/4 WPs (GX-025…039).

---

## 4. Related

- Expand policy: [EXPAND_POLICY.md](EXPAND_POLICY.md)  
- Graphics plan: [GRAPHICS_PIPELINE_PHASE_PLAN.md](../GRAPHICS_PIPELINE_PHASE_PLAN.md) GX-006, G-GFX-3/4  
- Implementation: `src/DetPS2.Core/Gs.cs`
