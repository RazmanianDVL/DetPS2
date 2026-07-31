# Graphics pipeline mega phase plan — Soft-GS truth path past MENU

**Status:** ACTIVE — **required** to get past commercial menus and discover real blockers  
**Tip anchor:** post–MENU YES 9/9 (`649846b` / later)  
**Doctrine:** Soft-GS = ground truth · no FFmpeg · no planted FB logos · SEMA_OFF claims  
**Parent orchestration:** [POST_MENU_PHASE_PLAN.md](POST_MENU_PHASE_PLAN.md) (10 seats, T0)  
**Oracle:** Play! GS/GIF/VIF · PCSX2+PINE for live FRAME/TEX/DISPFB · [PLAY_HLE_ORACLE.md](PLAY_HLE_ORACLE.md)

---

## 0. Why this plan exists

We hit **Soft-GS MENU YES 9/9**, but most surfaces are:

| Crutch | Examples |
|--------|----------|
| ofx=0 / 0x8000 **title-strip expand** | GoW, Whip, BO2 |
| Assist **PATH3 SPRITE** kicks | SM second chrome |
| **Merge composite** without natural DISPFB | B3 |
| **prims=1–4** logo clears | Haven, Vexx early, BO2 |
| Path2 **setup-only** (DISPFB+SCISSOR) without full TEX/IMAGE | GoW pre-W11C |
| **imgBytes=0** / no textured UI | Dec, Haven residual |

**Past the menu**, titles submit **real** Path1/2/3 graphs: textures (PSMT8/4/CT16), CLUT, multi-FB, Z, alpha, local→local blits, VU1 XgKick. Without a **faithful graphics pipeline**, pad will “work” and Soft-GS will stay a flat color — **hiding** the next bugs (VU, DMA, FILEIO, gameplay state).

```text
MENU (done) ──► honest pixels ──► pad past logo ──► NEW draw graphs appear ──► new walls visible
                     ▲
                     └── THIS PLAN (GFX stack)
```

---

## 1. Graphics north-star gates (G-GFX-0 … G-GFX-9)

| Gate | Name | Criteria (Soft-GS, SEMA_OFF) |
|------|------|------------------------------|
| **G-GFX-0** | Baseline | MENU 9/9 hold after every GFX merge |
| **G-GFX-1** | Path fidelity | Path1/2/3 complete tags; sticky Path2; no silent drop (telemetry: completed/aborted/inFlight) |
| **G-GFX-2** | Register truth | FRAME/ZBUF/TEX0/TEX1/TEXA/CLAMP/TEST/ALPHA/XYOFFSET/SCISSOR/DIMX match Play! semantics for used fields |
| **G-GFX-3** | IMAGE path | Host↔Local + Local↔Local BITBLT; imgBytes>0 on ≥5 titles @ claim |
| **G-GFX-4** | Texture sample | PSMCT32/16 + PSMT8+CLUT correct swizzle; procedural tex **off** when TEX0.TBP set |
| **G-GFX-5** | DISPFB present | Natural DISPFB→output on ≥4 titles without composite-only plant |
| **G-GFX-6** | Expand demotion | ofx title-strip expand hits → 0 on ≥6 titles while px floor held (retail XYOFFSET/PRIM size) |
| **G-GFX-7** | Path3 natural | ≥4 titles gifP3>0 from **game** DMA with Path3MaskedByVif policy (not assist plants) |
| **G-GFX-8** | Path1 commercial | ≥1 title Path1/VU1 XgKick contributes Soft-GS prims (not zero forever) |
| **G-GFX-9** | Gameplay surface | ≥3 titles T5 gameplay Soft-GS shows **textured** multi-prim scene (not logo expand) |

**Plan complete when G-GFX-0…9 green** (or deferred with issue + owner).  
**Blocks commercial P2–P6** in the post-menu plan when GFX residual is the wall.

---

## 2. Pipeline architecture (target)

```text
 EE / VU0          VU1 micro
     │                │
     │         XgKick │
     ▼                ▼
  GIF Path2/3      GIF Path1
     │                │
     └───────┬────────┘
             ▼
        GIF tag parser (PACKED / REGLIST / IMAGE)
             ▼
        GS register file (GsRegisters)
             ▼
        Primitive assembly (PRIM + XYZ2/3 + ST/UV + RGBA)
             ▼
        Raster (scissor, Z, ATEST, AFAIL, blend, fog, tex sample)
             ▼
        Local GS mem (swizzled FB / Z / TEX / CLUT)
             ▼
        PCRTC / DISPFB / DISPLAY readback
             ▼
        Soft-GS framebuffer (determinism source) → optional host present
```

**Law:** Host GPU / VulkanPresent is **display only**. Claims always Soft-GS metrics.

### 2.1 Owned code (current)

| Area | Files |
|------|--------|
| Raster + tex + FB | `Gs.cs`, `GsRegisters.cs` |
| Orchestration | `GsPipeline.cs`, `GsCommandBuffer.cs` |
| GIF tags | `Gif.cs` |
| VIF → Path2 | `Vif.cs`, `Vif1.cs`, `Vif1CommandProcessor.cs`, `VifUnpacker.cs` |
| DMA | `Dmac.cs` (GIF/VIF channels, Path3 mask) |
| VU Path1 | `Vu1.cs`, `VectorUnit.cs` (XgKick) |
| Present | `Pcrtc.cs`, `FramePresenter.cs`, `VulkanPresent.cs` |

---

## 3. Permanent GFX agent seats (3 of 10)

T0 always keeps **three** subagents on graphics (see parent seat map):

| Seat | Codename | Owns (write) | Focus |
|------|----------|--------------|--------|
| **S8** | **GFX-PATH** | `Gif.cs`, `Vif.cs`, `Vif1*.cs`, `VifUnpacker.cs`, `Dmac.cs` (GIF/VIF only) | Path1/2/3 delivery, DIRECT, M3P, PATH3 mask, nTAG |
| **S9** | **GFX-RASTER** | `Gs.cs`, `GsRegisters.cs`, `GsCommandBuffer.cs` | PRIM, XYZ, tex, blend, Z, AFAIL, swizzle, ofx policy |
| **S10** | **GFX-DISPLAY** | `Pcrtc.cs`, `GsPipeline.cs`, `FramePresenter.cs`, present hooks; `tools/*` GS metrics; smoke GS | DISPFB, DISPLAY, composite, PPM/claim telemetry, smokes |

**Title seats S1–S7** must **not** invent Soft-GS PATH3 plants or ofx hacks without S9 review. They **consume** pipeline fixes and report title draw graphs.

### 3.1 Handoff protocol

| If wall is… | Owner |
|-------------|--------|
| gif completed/aborted, DIRECT, QW pad, Path3 mask | **S8** |
| prims=0 with gif>0, wrong color, Z reject, tex black | **S9** |
| black despite prims, DISPFB=0, wrong output rect | **S10** |
| title never submits GIF | title seat (stream/EE), not GFX |

---

## 4. Seasons (G0–G6) interleaved with post-menu S0–S9

| GFX season | Parallel post-menu | Theme | Primary gates |
|------------|--------------------|-------|---------------|
| **G0** | S0 | Inventory + telemetry | G-GFX-0 hold |
| **G1** | S1–S2 | Path + tag fidelity | G-GFX-1, G-GFX-2 |
| **G2** | S2–S3 | IMAGE + textures | G-GFX-3, G-GFX-4 |
| **G3** | S2–S3 | DISPFB + expand demotion | G-GFX-5, G-GFX-6 |
| **G4** | S3–S4 | Path3 natural + Path1 | G-GFX-7, G-GFX-8 |
| **G5** | S4–S6 | Gameplay textured surfaces | G-GFX-9 |
| **G6** | S8–S9 | Perf/soaks + debt (no expand crutches) | all G-GFX hold |

**Critical path for “past menu”:** G0 → G1 → G2 → G3 **before** expecting P4 FIRST-GAMEPLAY without lying pixels.

---

## 5. Work packages (WP-GX-000 … WP-GX-079)

### G0 — Inventory & instrumentation (GX-000 … GX-009)

| ID | Seat | Deliverable | Exit test | Est |
|----|------|-------------|-----------|-----|
| **GX-000** | T0 | Link this plan from NEXT_PLAN + post-menu | docs on main | 0.25d |
| **GX-001** | S10 | Claim lines: gif completed/aborted, reg write counts, expandHits, imgBytes, dispfbPx, path1/2/3 | always in blocker-trace | 1d |
| **GX-002** | S10 | PPM dump helper `--dump-softgs=path` @ claim | file non-black when px>0 | 0.5d |
| **GX-003** | S8 | Path2/3/1 transfer log ring (DETPS2_TRACE_GIF=1) | readable trace | 1d |
| **GX-004** | S9 | Document ofx expand sites + legal conditions | policy in Soft-GS section | 0.5d |
| **GX-005** | S8 | Matrix: Path3MaskedByVif truth table vs titles | `docs/graphics/PATH3_MASK_MATRIX.md` | 1d |
| **GX-006** | S9 | Matrix: PSM formats used by fleet (oracle sample) | `docs/graphics/PSM_FLEET.md` | 1d |
| **GX-007** | S10 | Smoke inventory list for pipeline | Tests index | 0.5d |
| **GX-008** | S1–S7 | Per-title **draw graph charter** (which paths/formats at menu) | 7 docs | 0.5d each |
| **GX-009** | T0 | G0 merge; baseline matrix JSON | artifact | 0.5d |

---

### G1 — Path & GIF fidelity (GX-010 … GX-024) → G-GFX-1/2

| ID | Seat | Deliverable | Exit test | Est |
|----|------|-------------|-----------|-----|
| **GX-010** | S8 | Path2 sticky reassembly harden (W11C residual) | aborted only garbage DIRECT | 2d |
| **GX-011** | S8 | VIF DIRECT IMM=0 / mid-QW pad complete | smoke + GoW hold | 2d |
| **GX-012** | S8 | VIF UNPACK → VU1 mem correctness for Path1 prelude | unit | 2d |
| **GX-013** | S8 | DMAC GIF nTAG / TTE edge cases | smoke | 2d |
| **GX-014** | S8 | DMAC VIF1 batch vs QW parity tests | no px regress | 1d |
| **GX-015** | S8 | PATH3 mask: clear conditions when VIF M3P off | matrix soak 5 titles | 3d |
| **GX-016** | S8 | GIF REGLIST NREG/REGS full matrix | smoke each REGS | 2d |
| **GX-017** | S8 | GIF PACKED A+D continuous | smoke | 1d |
| **GX-018** | S9 | XYZ2 kick / XYZ3 no-kick (Play! map) hold | smoke + DA/GoW | 0.5d |
| **GX-019** | S9 | PRIM types: tri/strip/fan/sprite/line complete | smokes | 2d |
| **GX-020** | S9 | ST vs UV enable; TEX perspective | smoke | 2d |
| **GX-021** | S9 | XYOFFSET full 16.4 fixed; kill illegal expand when ofx retail | expandHits↓ | 2d |
| **GX-022** | S10 | GS register dump in claim (`FRAME/ZBUF/TEX0/…`) | claim lines | 1d |
| **GX-023** | S8+S9 | Integration: gif>0 ⇒ prim attempt or explicit reject reason | reject counters | 2d |
| **GX-024** | T0 | **G-GFX-1/2 assert** | gates | 0.5d |

---

### G2 — IMAGE + textures (GX-025 … GX-039) → G-GFX-3/4

| ID | Seat | Deliverable | Exit test | Est |
|----|------|-------------|-----------|-----|
| **GX-025** | S9 | Host→Local BITBLT all used PSM | smoke | 2d |
| **GX-026** | S9 | Local→Local blit | smoke | 2d |
| **GX-027** | S9 | Local→Host (readback) if titles use | smoke/opt | 2d |
| **GX-028** | S9 | PSMCT32 swizzle page/block truth vs Play! | oracle test | 3d |
| **GX-029** | S9 | PSMCT16 / PSMCT16S | smoke + 1 title | 2d |
| **GX-030** | S9 | PSMCT24 | smoke | 1d |
| **GX-031** | S9 | PSMT8 + CLUT8 PSMCT32 | smoke + Dec/SM | 3d |
| **GX-032** | S9 | PSMT4 + CLUT4 | smoke | 2d |
| **GX-033** | S9 | TEXA / AEM / TA0/TA1 | smoke | 1d |
| **GX-034** | S9 | CLAMP region / repeat / region_repeat | smoke | 2d |
| **GX-035** | S9 | Disable procedural tex when TEX0 valid | fleet img/tex | 1d |
| **GX-036** | S9 | Bilinear optional + deterministic | FLOAT_POLICY | 1d |
| **GX-037** | S2/S3/S1 | Title consume: Midway textures | imgBytes>0 | 3d |
| **GX-038** | S9 Haven/Dec docs | IMAGE residual close | T3 | 2d |
| **GX-039** | T0 | **G-GFX-3/4 assert** imgBytes>0 on ≥5 | scoreboard | 0.5d |

---

### G3 — DISPFB / output / expand demotion (GX-040 … GX-049) → G-GFX-5/6

| ID | Seat | Deliverable | Exit test | Est |
|----|------|-------------|-----------|-----|
| **GX-040** | S10 | DISPFB1/2 + DISPLAY1/2 circuit | unit | 2d |
| **GX-041** | S10 | Composite DISPFB→FB only when retail programs DISPFB | B3 dispfbPx | 3d |
| **GX-042** | S10 | PCRTC circuit / field / magh/magv | smoke | 2d |
| **GX-043** | S9 | Demote ofx expand: require thin strip **and** !retailOfx | GoW/Whip/BO2 | 3d |
| **GX-044** | S9 | Full-height Y strip expand policy | smoke | 1d |
| **GX-045** | S4 | B3 natural DISPFB or documented FRAME-only present | T3 honest | 2d |
| **GX-046** | S6 | GoW expandHits=0 attempt | G-GFX-6 partial | 2d |
| **GX-047** | S10 | Output PPM + metrics CI helper | tool | 1d |
| **GX-048** | S1–S7 | Claim expandHits per title | matrix | 1d |
| **GX-049** | T0 | **G-GFX-5/6 assert** | gates | 0.5d |

---

### G4 — Path3 natural + Path1 (GX-050 … GX-059) → G-GFX-7/8

| ID | Seat | Deliverable | Exit test | Est |
|----|------|-------------|-----------|-----|
| **GX-050** | S8 | Path3MaskedByVif dynamic unmask when VIF stops masking | ≥2 titles gifP3↑ natural | 3d |
| **GX-051** | S8 | GIF PATH3 high-TADR END hold tests | no Dec/GoW regress | 2d |
| **GX-052** | S1 | SM remove assist PATH3 when natural FBB0 | gifP3 natural | 3d |
| **GX-053** | S8 | VU1 XgKick → Path1 buffer wire audit | unit | 2d |
| **GX-054** | S8 | Path1 PACKED from VU1 mem | smoke | 3d |
| **GX-055** | S6/S4 | Commercial Path1 first prims | **G-GFX-8** | 5d |
| **GX-056** | S9 | ZBUF / ZTST / ZTE commercial defaults | smoke | 2d |
| **GX-057** | S9 | Alpha blend Cs/Cd/As formulas + FIX | smoke | 2d |
| **GX-058** | S9 | ATEST/AFAIL FB_ONLY / RGB_ONLY hold | B3/DA | 1d |
| **GX-059** | T0 | **G-GFX-7/8 assert** | gates | 0.5d |

---

### G5 — Gameplay textured surfaces (GX-060 … GP-069) → G-GFX-9

| ID | Seat | Deliverable | Exit test | Est |
|----|------|-------------|-----------|-----|
| **GX-060** | S4 | B3 race/frontend multi-tex Soft-GS | G-GFX-9 partial | 4d |
| **GX-061** | S3 | DA match surface textured | partial | 4d |
| **GX-062** | S7 | Vexx first area textures | partial | 4d |
| **GX-063** | S9 | Mip / LOD if required by titles | smoke/opt | 3d |
| **GX-064** | S9 | Fog F / CLAMP fog | smoke | 1d |
| **GX-065** | S8 | Multi-context GS (2 contexts) if used | smoke | 3d |
| **GX-066** | S10 | Scene-change detector (FB hash delta) for T5 | tool | 1d |
| **GX-067** | S1–S7 | No new ofx/PATH3 plants in gameplay WPs | review | cont |
| **GX-068** | S8+S9 | Perf: Soft-GS hot path (span fills) without det break | PERF note | 3d |
| **GX-069** | T0 | **G-GFX-9 assert** ≥3 textured gameplay | gates | 0.5d |

---

### G6 — Hardening & debt (GX-070 … GX-079)

| ID | Seat | Deliverable | Exit test | Est |
|----|------|-------------|-----------|-----|
| **GX-070** | S9 | Delete dead procedural-tex paths in commercial boot | code | 1d |
| **GX-071** | S9 | Expand-hit ban list in TITLE_HACKS when G-GFX-6 | docs | 0.5d |
| **GX-072** | S8 | PATH3 assist plant ban (SM) when natural | code | 1d |
| **GX-073** | S10 | Full GS smoke suite in CI entry | all pass | 1d |
| **GX-074** | S10 | Determinism: same GIF stream → same FB hash | test | 2d |
| **GX-075** | S8 | SaveState GS/GIF/VIF bodies completeness | round-trip | 2d |
| **GX-076** | S9 | Oracle: Play! GSHandler field map doc | `docs/graphics/PLAY_GS_MAP.md` | 2d |
| **GX-077** | S1–S7 | Fleet claim G-GFX hold | matrix | 2d |
| **GX-078** | T0 | Graphics chapter in RELEASE_NOTES v0.2 | notes | 0.5d |
| **GX-079** | T0 | Close G-GFX-0…9 on #12 | gates table | 0.5d |

---

## 6. Discovery loop (how GFX finds new issues)

```text
1. S8/S9/S10 land pipeline WP → T0 merge + smoke
2. Title seats re-claim 100M Soft-GS
3. If pad advances menu:
     - NEW gif patterns / TEX0 / IMAGE appear → file wall under S8/S9/S10
     - If still black → S10 DISPFB; if prims=0 gif>0 → S9; if gif=0 → title stream
4. Log wall in docs/graphics/DISCOVERY_LOG.md (append-only)
5. Open residual ticket or WP amendment
```

**Pad without GFX = wasted.** Title INTERACTIVE WPs (post-menu S1) **depend on** G1–G2 minimum for any title that only shows expand strips today (GoW, Whip, BO2).

### Dependency into post-menu gates

| Post-menu gate | Requires GFX |
|----------------|--------------|
| P1 INTERACTIVE | G-GFX-1 at least for GoW/Whip/BO2 class |
| P2 FRONTEND | G-GFX-3/4/5 |
| P3 NATURAL | G-GFX-6/7 |
| P4–P6 GAMEPLAY | G-GFX-9 |
| P10 Determinism | GX-074 |

---

## 7. Freezes specific to graphics

1. Soft-GS metrics only for YES claims.  
2. No host “screenshot replace” / FFmpeg.  
3. No invent PATH3 packets or prims to pass gates.  
4. Expand strips: **temporary** with telemetry; G-GFX-6 demotes.  
5. Assist PATH3: **temporary**; G-GFX-7 demotes.  
6. Path3MaskedByVif changes require **9-title soak**.  
7. FLOAT_POLICY for any bilinear / blend.  
8. Do not “fix” black frames by Clear(color) hacks.

---

## 8. First 72 hours (GFX)

| Hour | Action |
|------|--------|
| 0–4 | T0 land plan; assign S8/S9/S10 to GFX seats |
| 4–24 | GX-001 telemetry + GX-003 GIF trace + GX-004 expand policy |
| 24–48 | GX-010/011 Path2 harden; title draw charters GX-008 |
| 48–72 | First matrix with gif/reg/expand metrics; pick worst 3 titles for G1 focus |

---

## 9. Success metrics (dashboard)

| Metric | Menu now (typical) | Target post-G3 |
|--------|--------------------|----------------|
| imgBytes>0 titles | few | ≥5 |
| dispfbPx>0 titles | few | ≥4 |
| expandHits=0 titles | few | ≥6 |
| natural gifP3>0 | few | ≥4 |
| Path1 prims | 0 | ≥1 title |
| Textured gameplay | 0 | ≥3 |

---

## 10. References

| Doc | Role |
|-----|------|
| [POST_MENU_PHASE_PLAN.md](POST_MENU_PHASE_PLAN.md) | 10-seat orchestration |
| Play! `GSHandler` / GIF | Oracle |
| [FLOAT_POLICY.md](../FLOAT_POLICY.md) | Deterministic float |
| ROADMAP Phase 7 | Historical Soft-GS baseline |

---

*Graphics is not optional polish — it is the instrument that reveals post-menu bugs. T0 keeps S8–S10 on this plan until G-GFX-9.*
