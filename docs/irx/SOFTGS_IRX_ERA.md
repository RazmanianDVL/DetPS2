# Soft-GS in the IRX era (Track T9)

**Status:** binding for Soft-GS / GIF / present once IOP runs real IRX and assets stream  
**Owned:** `Gs.cs`, `Gif.cs` (minimal), this doc  
**Plan:** [`docs/IRX_EXECUTION_PHASE_PLAN.md`](../IRX_EXECUTION_PHASE_PLAN.md) — WP-37 / WP-39 / G5  
**Doctrine:** [`docs/CORRECTNESS.md`](../CORRECTNESS.md)

---

## 1. Role of Soft-GS under `LITERAL_IRX`

```text
EE game  →  GIF (Path1/2/3)  →  Soft-GS (registers → prims / IMAGE)  →  present span
                 ↑
         assets via IRX FILEIO / CDVD (not host paint)
```

| Layer | Owner | Soft-GS expectation |
|-------|--------|---------------------|
| IOP IRX (FILEIO, PADMAN, …) | T1–T8 | Streams real disc data into EE RAM |
| EE / DMA / VIF / GIF | core | Submits real GIFtags (PACKED / REGLIST / IMAGE) |
| **Soft-GS** | **T9** | Raster + local VRAM truth; metrics ground truth |
| Desktop / GPU present | optional | Blits Soft-GS span only; never invents pixels |

**North star (G5):** ≥1 commercial title shows a **non-black Soft-GS interactive surface** without thrash plants, FFmpeg, or synthetic logos.

---

## 2. PATH3 / M3P requirements (WP-37)

Commercial flip / path-sync loops (e.g. Burnout 3) program VIF1 **MSKPATH3** and poll **GIF_STAT**:

| Bit | Name | Soft-GS / GIF contract |
|-----|------|-------------------------|
| 1 | **M3P** | Mirrors VIF1 MSKPATH3 (`Gif.SetMskPath3`) |
| 0 | **M3R** | GIF_MODE permanent PATH3 mask |
| 24–28 | **FQC** | Non-zero while masked PATH3 data is held (FIFO full or synthetic ≥1 when masked empty race) |
| 10–12 | **APATH** | Active path while processing |

**Required behavior when the game submits under mask:**

1. `ReceivePath3Data` **does not** drain into GS while `M3P|M3R`.
2. Held transfer raises FQC; unmask (`SetMskPath3(false)` or clearing M3R) **drains** held PATH3 into GS (`ProcessTransfer`).
3. `gifPath3` (`Gif.Path3Transfers`) increments on submit (including held), so telemetry sees traffic even before unmask.
4. Soft-GS must **not** stick permanently with M3P=1 and no drain path when the game later unmasks.

**Residual (document, do not plant):** only the last held PATH3 QWC is retained while masked (no full hardware FIFO queue). If a title multi-kicks PATH3 under a long mask, extend FIFO hold — do not unmask from title assists as a substitute for correct GIF_STAT.

**Exit signal (WP-37):** `gifPath3` rises with **real prims / IMAGE** after unmask — not assist sticky `SetMskPath3(false)` alone.

---

## 3. Present path (WP-39 / G5)

### Ground truth

| Signal | Meaning |
|--------|---------|
| `Gs.PixelsWritten` (`px`) | Software **raster** fragments written to Soft-GS FB only |
| `Gs.PrimitivesDrawn` | Prim assembly kicks |
| `Gif.Path3Transfers` / Path1 / Path2 | DMA / VIF / VU1 submissions |
| `GetPresentSpan()` | **Always** software framebuffer (640×448 ARGB) |
| PPM / scoreboard | Soft-GS only; host GPU optional |

### Forbidden present shortcuts

| Shortcut | Status |
|----------|--------|
| Host FFmpeg → `SetHostOverlay` boot FMV | **Removed** (MidwayBootAssist `host-fmv-disabled`) |
| `BlitArgb8888` auto-installing host overlay | **Removed** (Gs no longer reintroduces overlay) |
| `SetHostOverlay` counting as `PixelsWritten` | **Removed** (metrics lie) |
| `GetPresentSpan` preferring host overlay over Soft-GS FB | **Removed** |
| Synthetic branded logos / chrome | Forbidden (CORRECTNESS) |

### Host overlay API (legacy)

`SetHostOverlay` / `ClearHostOverlay` / `HostOverlayActive` remain for ABI stability with dead assist call sites. They must:

- **not** drive present (`GetPresentSpan` ignores overlay),
- **not** inflate `PixelsWritten`,
- **never** be used for boot FMV or branded UI.

Missing logos/FMV are **IPU/CRI Soft-GS gaps**, not host-paint jobs.

### Present residuals (honest)

1. Soft-GS present is the **CPU raster FB**, not a full privileged **DISPFB1/2 → local VRAM composite**. Titles that only BITBLT into GS local memory and never draw prims into the software FB may still report `px=0` until DISPFB present is implemented.
2. IMAGE/BITBLT now tracks TRX cursor + PSMCT32/PSMT8 swizzle so **texture sample** matches `UploadTexture*`. Full GS block layout edge cases and Local→Local still incomplete.
3. Headless Soft-GS is the default success path on this operator machine (no iGPU).

---

## 4. GIF → GS pipeline checklist (when IRX streams assets)

Once FILEIO/CDVD IRX delivers real packs / TXDs / frontend blobs into EE RAM:

1. **DMA GIF (PATH3)** or VIF1 DIRECT (PATH2) / VU1 XGKICK (PATH1) carries GIFtags.
2. **IMAGE** after `BITBLTBUF` + `TRXPOS` + `TRXREG` + `TRXDIR=0` fills local VRAM (swizzled).
3. **PACKED/REGLIST** programs TEX0/FRAME/TEST/ALPHA and kicks PRIM/XYZ2.
4. Soft-GS raster increments `px` / `prims`.
5. Scoreboard / PPM shows non-black Soft-GS — claim only with metrics + capture, not host overlay.

If step 1–2 happen (`gifPath3↑`) but `px=0`: debug **prim path / FRAME / scissor / M3P stuck**, not paint a logo.

---

## 5. Metrics discipline

| Do | Don't |
|----|--------|
| Report `px`, `prims`, `gifPath1/2/3`, `dmac`, FB hash | Treat host overlay or Desktop as Soft-GS truth |
| Keep black + honest residual | Claim MENU / first GS without Soft-GS evidence |
| Fix Gs/Gif machine bugs | New GameQuirk thrash plants for presentation |

`px` was historically **unreliable** when logo-hold overlay inflated `PixelsWritten` every present tick. That path is disabled; prefer `gifPath3` + `prims` + FB hash when diagnosing stalls.

---

## 6. Smokes (T9 gate)

All GS/GIF-related smokes in `Tests/SmokeTests.cs` must stay green, including:

- `Gs_RenderTestSceneProducesPixels`, `Gs_Sprite_FillsRect`, `Gs_DepthTest_RejectsFar`
- `Gs_AlphaBlend_Mixes`, `Gs_TextureSample_NonUniform`, `Gs_TexturePsmct16_Samples`
- `Gs_Clut8_Samples`, `Gs_AlphaTest_Rejects`, `Gs_TexFlush_Counts`, `Gs_Bilinear_Samples`
- `Gif_PackedTriangle_WritesPixels`, `Gif_Paths_APIsExist`
- `Dmac_GifPath3_UsesStartMadr`
- `Present_HashAlwaysSoftwareGs`, `PresentPipeline_Software` / related present smokes

Commercial title progress is **not** a smoke substitute for these unit gates.

---

## 7. Track boundaries

| In scope (T9) | Out of scope |
|---------------|--------------|
| `Gs.cs`, `Gif.cs` (minimal), Desktop present consumers only if present-span contract | `GameQuirks`, Midway/Burnout assists, FFmpeg |
| PATH3 M3P / hold-drain, IMAGE BITBLT, raster truth | IOP / IRX / SIF / FILEIO |
| Docs: this file | Host logo overlays, title plants |

Cross-track: if IRX never delivers assets, Soft-GS cannot invent them. If assets land and GIF submits, Soft-GS bugs are T9.

---

## 8. WP mapping

| WP | Soft-GS deliverable |
|----|---------------------|
| **WP-37** | PATH3 not stuck M3P when game submits; `gifP3↑` with real transfer drain |
| **WP-39** | Non-black Soft-GS frame (+ pad if required) = **G5** |
| **WP-44** | Same for title #2 |

This document is the Soft-GS contract for those WPs; implement machine fixes, not presentation cheats.
