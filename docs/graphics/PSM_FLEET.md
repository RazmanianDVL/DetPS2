# Soft-GS PSM fleet matrix (GX-025…035)

**Owner seat:** S9 GFX-RASTER  
**Status:** G2 ACTIVE — Host→Local + sample paths for commercial IMAGE/TEX  
**Code paths:** `Gs.SampleTexel`, `Gs.WriteImageTransfer`, `Gs.MaybeLoadClut`, `SwizzleOffset32` / `16` / `16S` / `8`

---

## 1. Formats known in Soft-GS code

| PSM | Code | Sample path | Host→Local | Swizzle | Notes |
|-----|------|-------------|------------|---------|-------|
| PSMCT32 | `0x00` | `SampleTexel` default | yes | page/block 32 | primary FB/tex |
| PSMCT24 | `0x01` | RGB + TEXA.TA0 | yes (3 bpp) | page/block 32 | GX-030 |
| PSMCT16 | `0x02` | RGB555 + TEXA | yes | page/block 16 (`SwizzleOffset16`) | GX-029 |
| PSMCT16S | `0x0A` | same as 16 | yes | page/block **16S** (`SwizzleOffset16S`, PCSX2 `blockTable16S`) | GX-029 / GS-1 present (Dec DISPFB FBW=832) |
| PSMT8 | `0x13` | index→CLUT | yes | page/block 8 | GX-031 |
| PSMT8H | `0x1B` | index→CLUT | yes | page/block 8 | high twin |
| PSMT4 | `0x14` | 4-bit index→CLUT | yes (nibble pack) | linear residual | GX-032 partial |

CLUT: loaded from local mem at TEX0.CBP when **CLD≠0** (CPSM32 linear palette or CPSM16 RGB555). Soft-GS also accepts `UploadTexture8` host palette.

**GX-035:** any programmed TEX0 (including **TBP0=0**) disables procedural checker; sample local mem.

**GX-026:** Local→Local (TRXDIR.XDIR=2) same-PSM RRW×RRH blit.

---

## 2. Per-title matrix (fill at claim)

| Title | Menu TEX0.PSM | IMAGE TRX PSM | FRAME PSM | imgBytes@claim | Notes |
|-------|---------------|---------------|-----------|----------------|-------|
| God of War | (no TEX0 @20M) | — | — | 0 | expand title strip; gif image=0 |
| Whiplash | TBD | TBD | TBD | 0 | expand title strip |
| Blood Omen 2 | TBD | TBD | TBD | 0 | expand logo band |
| Burnout 3 | mixed | PSMCT32 | TBD | >0 | merge composite residual |
| MK Shaolin Monks | TBD | TBD | TBD | TBD | assist PATH3 chrome |
| MK Deadly Alliance | TBD | PSMCT32 | TBD | >0 | Midway keep-alive IMAGE |
| MK Deception | TBD | residual gameart | TBD | residual | gameart IMAGE (G-GFX-3) |
| Vexx | TBD | TBD | TBD | TBD | STREE0 surface |
| Haven | TBD | TBD | TBD | residual | IMAGE chrome |

---

## 3. How to sample

1. Claim / diagnose with SEMA_OFF; scrape `softgs-regs` + TEX0 from reg dump when GX-022 lands.  
2. Optional: `DETPS2_TRACE_GIF=1` (S8) for TRXDIR/BITBLTBUF DPSM.  
3. Oracle: Play! / PCSX2+PINE FRAME/TEX/DISPFB (see PLAY_HLE_ORACLE.md).  
4. Unit: `Gs_HostToLocal_*`, `Gs_Tex0_Cld_*`, `Gs_LocalToLocal_Blit`, `Gs_Texa_*`.

---

## 4. Related

- Expand policy: [EXPAND_POLICY.md](EXPAND_POLICY.md) (expandHits **held** for collapse strips; not demoted this WP)  
- Graphics plan: [GRAPHICS_PIPELINE_PHASE_PLAN.md](../GRAPHICS_PIPELINE_PHASE_PLAN.md) GX-025…035, G-GFX-3/4  
- Implementation: `src/DetPS2.Core/Gs.cs`, `GsRegisters.cs`
