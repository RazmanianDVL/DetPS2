# GFX baseline — present vs metrics (CP1)

**Status:** CP1 baseline (read-only ops)  
**Plan:** `gfx-plan-v0.md` (CP0 dual-ACKed)  
**Tip frozen:** `a4171c3`  
**Canary:** `pwsh ./tools/canary-gfx-baseline.ps1 -Budget verify`  
**Rule:** open `*-present.ppm` for visual dual-check before any Tier A claim.

---

## Method

```text
dotnet exec out/scoreboard-build/DetPS2.Core.dll scoreboard-metrics <media> \
  --cycles=50000000 --host-present --out=…-metrics.json --dump-softgs=…-present.ppm
```

PPM is Soft-GS present FB after scoreboard’s final composite (product tip; **no** L1 Core fix landed).  
Lit = any non-zero RGB. Gray = R=G=B>0. Color = chromatic RGB. Tier A numeric gate = color ≥5% of 640×448 — **plus visual**.

---

## Frozen numbers @ tip `a4171c3` (verify 50M)

| Title | Serial | px | prims | imgBytes | gifP2/P3 | compositeSource | residualDispfbPx | present lit% | present color% | Visual |
|-------|--------|---:|------:|---------:|----------|-----------------|-----------------:|-------------:|---------------:|--------|
| **Burnout 3** | SLUS_210.50 | 17849910 | 3769 | 1971840 | 244 / 367 | LastImageTrx | 6515 | **0** | **0** | **100% black** |
| **MK Deception** | SLUS_208.81 | 462848 | 3 | 557056 | 0 / 4 | LastImageTrx | 32768 | **11.43** | **0** | **Gray strip only** (128,128,128 × 32768); rest black |
| **Whiplash** | SLUS_206.84 | ~286720 | 1 | **0** | 0 / 2 | None | 0 | **0** | **0** | Black / expand residual (M7-L1 honesty) |

**Canary re-run frozen:** `out/canaries/gfx-baseline/20260804-200930/` (gitignored) — confirms table above at tip `a4171c3`.

### Register notes (B3)

- `FRAME_1` FBP=**70** (0x8C000), FBW=10, PSM=0  
- `DISPFB2` FBP=**0**, DBW=10, DPSM=0x0A (PSMCT16S)  
- Natural DISPFB programmed; residual path reports LastImageTrx — **present still black**

### Register notes (Dec)

- residual LastImageTrx fills 32768 present pixels as **uniform gray** (index/CLUT residual class), not gameart color

---

## Diagnosis (plan taxonomy — not Core yet)

| Layer | B3 | Dec | Whip |
|-------|----|-----|------|
| L1 Present | **Suspect** — activity vs black present | Partial (gray strip) | N/A (no IMAGE) |
| L2 Delivery | Suspect (wrong page/PSM vs FRAME 70) | Suspect (gray not art) | — |
| L3 EE path | Soft-GS already active | Host→Local residual gameart | **Primary** (imgBytes=0) |
| L4 Raster | rejDepth high but px huge | Thin prims | Expand only |

**Bias into CP2:** L1 design (black wipe + merge cache + present path parity) with B3 bar; L2 page selection if L1 alone insufficient.

---

## CP1 done criteria

- [x] Plan dual-ACK (CP0)  
- [x] Shared canary script  
- [x] Frozen baseline table  
- [ ] Dual visual of PPMs (Claude + Grok open files after canary re-run)  
- [ ] No Core  

```text
GFX CP1 baseline
  B3 present black despite 18M px / 2M img
  Dec gray strip only; Whip imgBytes=0
  next: L1 design dual-ACK (CP2)
```
