# M7-a design — PATH2/3 IMAGE + DISPFB composite fidelity (Host→Local residual)

**Status:** design only (ready for implement ACK) — **no Core change in this note**  
**Date:** 2026-08-04  
**Pri / ID:** P1 / **M7-a** (`docs/infra-audits/gamequirks-infra-debt.md` §6 / priority #6)  
**Source seed:** `docs/infra-audits/m7a-path23-image-dispfb-seed.md`  
**Related:** `docs/GRAPHICS_PIPELINE_PHASE_PLAN.md` G-GFX-3/5 (and G-GFX-1/6/7 adjacency),  
`docs/graphics/PATH2_STICKY_W11C.md`, `docs/graphics/PATH3_MASK_MATRIX.md`,  
`docs/graphics/EXPAND_POLICY.md`, `docs/graphics/DISCOVERY_LOG.md`,  
`docs/CORRECTNESS.md`, `tools/SCOREBOARD_SCHEMA.md`  
**Mode:** infra / Soft-GS pipeline. **No GameQuirks edits. No RealSifRpc edits. No invent PATH3. No push.**

---

## 1. Problem (one paragraph)

MENU YES 9/9 Soft-GS surfaces often **do not** come from a complete retail graph. The honest end-to-end path is:

```text
EE texture/stream bytes
  → DMA GIF Path2/3 (or VIF1 DIRECT → Path2)
  → GIFtag IMAGE (Host→Local BITBLT setup + data)
  → local GS mem @ TRX/DBP
  → software DISPFB1/2 + PMODE EN
  → CompositeDispfbToFramebuffer → Soft-GS present
```

When any stage fails, **PRESENT** assists feed **Host→Local BITBLT of honest disc/RDRAM bytes** into Soft-GS local mem and force composite refresh so present is non-black (GoW R_SHELL/TIT1, Dec/DA gameart.ssf, Whip firstscreen, Haven SYSTEM.RW3/CUBE, BO2 MAINMENU, Midway family art). Soft-GS already implements TRXDIR Host→Local (`BeginTrxFromDir` / `WriteImageData`); the residual is **not** “missing host blit API.” It is incomplete **natural delivery + page bind + DISPFB composite**. Doctrine (`CORRECTNESS.md`): residual only when pixels are real disc/EE bytes; black + honest residual beats pretty lie; no FFmpeg logos / synthetic branded UI / invent PATH3.

**Class:** end-to-end **PATH2/3 IMAGE + DISPFB composite** fidelity gap → Host→Local Soft-GS residual crutches (tag **PRESENT**, not FPS).

---

## 2. Current path (code map)

### 2.1 GIF Path2 / Path3 delivery (`Gif.cs`)

| Entry | Source | Role for IMAGE |
|-------|--------|----------------|
| `ReceivePath3Data(addr, qwc)` | DMAC GIF channel | Full segment QWC; M3P/M3R hold queue; unmasked `ProcessTransferBudgeted` |
| `ReceivePath2Data(addr, qwc)` | VIF1 DIRECT | Often **qwc=1** sticky mid-packet; G2 holds under Path3 sticky |
| `ReceivePath2Quadword(...)` | VIF FIFO / GX-011 | Inline 1-QW Path2 without contiguous EE addr |
| `DrainImage` | GIFtag flg=IMAGE | Streams nloop QWs → `_gs.WriteImageData` (TRX cursor owns commercial path) |

Hardening already landed (do not re-litigate as “missing primitives”):

- **Sticky mid-packet** Path2 across QW slices (GoW class: gifP2 high / FRAME_1=0 was pre-sticky).
- **Path2 hold under Path3 sticky** (G2) — multi-DMA Host→Local IMAGE leaves sticky between GIF segments; drop → VIF debit desync → Midway/DA abort storms.
- **Path3 hold under M3P/M3R** with multi-kick queue (Burnout path-sync FQC).
- **M1-b budgeted Process** — large IMAGE segments drain across `Step()` ticks without inflating Path*Transfers.
- **M1-a honest FQC** — only when unmasked PATH3 race evidence exists (no invent FQC for never-ran Path3).
- Telemetry: `TagsCompletedImage`, `PacketsCompleted` / `PacketsAborted`, `DETPS2_TRACE_GIF=1` ring.

### 2.2 Soft-GS Host→Local + residual composite (`Gs.cs`, `GsDisplayCircuit.cs`, `GsPipeline.cs`)

| Piece | Behavior |
|-------|----------|
| BITBLT setup | `WriteGsRegister` BITBLTBUF / TRXPOS / TRXREG / TRXDIR → `BeginTrxFromDir` (XDIR=0 Host→Local) |
| IMAGE data | `WriteImageData` → TRX cursor `StoreLocalPixel`; bumps `ImageBytesWritten`, `_localMemHasImage` |
| Largest transfer note | `NoteLargestImageTransfer` for residual present sampling (GoW high-DBP PSMT4) |
| Present | `GetPresentSpan` → if IMAGE present, `CompositeDispfbToFramebuffer` (+ black-FB `ForceRefreshPresentComposite`) |
| Natural composite | Software-written DISPFB1/2 (GX-040/041; PMODE EN optional) → `naturalDispfbPx`, source `NaturalDispfb` |
| Residual sources | FRAME → SyntheticFbp0 → `LastImageTrx` (largest Host→Local window); metrics `residualDispfbPx` |
| Circuit gen | `DisplayCircuitGeneration` invalidates merge cache when DISPFB/DISPLAY/PMODE change |

**Composite residual policy (honest, already coded):**

1. Prefer natural DISPFB when programmed (even EN=0).  
2. If DISPFB unset → FRAME, then FBP0 IMAGE page (B3-class).  
3. If natural DISPFB programmed but local RGB empty / Soft-GS mostly black + IMAGE exists → FRAME, FBP0, then **LastImageTrx** (GoW PSMT4 @ high DBP vs DISPFB CT24 empty).  
4. Sparse natural vs different FRAME FBP → merge FRAME (Vexx-class).  
5. **Never plant DISPFB.** Never invent PATH3 SPRITE for composite.

### 2.3 PRESENT assists (GameQuirks — **do not edit this design turn**)

Assists program Soft-GS BITBLT Host→Local from real RDRAM/disc bytes and often call `ForceRefreshPresentComposite` when expand black + imgBytes under floor. Representative:

| Title / assist | Residual mechanism | Soft-GS symptom class |
|----------------|--------------------|------------------------|
| **GoW** `GodOfWarAssist` | Host→Local R_SHELL/TIT1; force composite when expand black | Path2 setup/expand; natural IMAGE weak; DISPFB empty CT24 vs high-DBP PSMT4 |
| **Haven / SotC** `TeamIcoAssist` | Host→Local SYSTEM.RW3 / CUBE / MANAGER / NICO; re-merge on present | Logo clear prims; imgBytes=0 natural; DISPFB garbage → composite None |
| **Whip** `WhiplashAssist` | Host→Local firstscreen/frontend bulk BITBLT | Ring/GOE bytes never reach GIF IMAGE |
| **BO2** `BloodOmen2SnAssist` | Host→Local MAINMENU | Multi-prim IMAGE/DISPFB chrome residual |
| **Midway family** | Host→Local gameart.ssf (DA/Dec) | EE texture upload path incomplete |
| **B3** | Merge composite without natural DISPFB; residual FRAME/FBP0 | DISPFB1 often 0 → residualDispfbPx honest path |
| **SM** `MidwayBootAssist` | Assist PATH3 / logo spine (adjacent PRESENT) | Natural gifP3 + NaturalDispfb without forced spine |

Policy repeated in assists: **no invent PATH3 packets, no synthetic branded color** — residual only.

### 2.4 Scoreboard truth (`tools/SCOREBOARD_SCHEMA.md`)

| Metric | Meaning for M7-a |
|--------|------------------|
| `imgBytes` | Host→local IMAGE/BITBLT bytes (**today includes assist Host→Local** — see open Q2) |
| `naturalDispfbPx` | Composited from software-programmed DISPFB only |
| `residualDispfbPx` | FRAME / FBP0 / LastImageTrx residual composite |
| `compositeSource` | `None` / `NaturalDispfb` / `Frame` / `SyntheticFbp0` / `LastImageTrx` |
| `naturalDispfb` / `enNaturalDispfb` | DISPFB programmed / EN + preferred |
| G2 / G3 heuristics | G-GFX-3 / G-GFX-5 shaped; residual FRAME/FBP0 is **Y?** not full natural Y |

---

## 3. Gap analysis

### 3.1 Why assists fire

Assists fire because **one or more** shared stages do not match retail graphs — not because Host→Local BITBLT is missing:

| Stage | Gap hypothesis | Owner seat | Pointers |
|-------|----------------|------------|----------|
| **Stream / bind** | Texture bytes never reach EE (FILEIO/SIF/WAD) → nothing to IMAGE | Title / SIF / M3 / media | Infra-debt #1–2, #5 (orthogonal; M7-a assumes bytes *can* exist for inventory split) |
| **DMA → GIF Path2/3** | Incomplete sticky / arb / M3P holds Path3; Path2 setup-only | **S8** | `PATH2_STICKY_W11C.md`, `PATH3_MASK_MATRIX.md`; G-GFX-1/7 |
| **GIFtag IMAGE** | Multi-DMA IMAGE slices, wrong TRX/BITBLTBUF, PSM swizzle, abort mid-stream, budgeted residual not completing | **S8** (+ **S9** PSM) | G-GFX-3; multi-DMA smokes landed; fleet still `gif image=0` on residual titles |
| **Local mem truth** | Draw FRAME ≠ display page; IMAGE at high DBP while DISPFB points empty | **S9** / **S10** | GoW PSMT4 vs DISPFB CT24; `LastImageTrx` residual |
| **DISPFB / PCRTC** | DISPFB1/2 never programmed or wrong circuit/PSM/FBW | **S10** | GX-040 decode landed; `naturalDispfbPx` still sparse |
| **Composite policy** | Present samples expand/black FB before local IMAGE merge | **S10** | `ForceRefreshPresentComposite`; expand race (`EXPAND_POLICY.md`) |

**One-sentence hypothesis:**  
Host→Local residual is the **PRESENT escape hatch** for an incomplete **natural graph** (Path delivery → IMAGE into correct local pages → software DISPFB → composite), not a missing host blit primitive.

### 3.2 Residual class taxonomy (inventory labels)

Per title @ claim, label residual as exactly one primary class (secondary notes allowed):

| Class id | Name | Evidence sketch |
|----------|------|-----------------|
| **R0** | Upstream no bytes | EE ring empty / FILEIO fail; assist Host→Local from disc still lights present |
| **R1** | No IMAGE | EE has tex buffer; `TagsCompletedImage≈0`, game gif IMAGE=0; Path2 setup-only |
| **R2** | IMAGE wrong page | `imgBytes>0` (game or residual), natural DISPFB programmed, natural lit=0, residual LastImageTrx/Frame |
| **R3** | DISPFB unset | DISPFB1/2=0; composite Frame/FBP0; B3 honest residual |
| **R4** | Composite skip | IMAGE + DISPFB set; merge cache / black expand stamp prevents natural composite |
| **R5** | Path3 masked starve | M3P sticky + held Path3; never unmask → IMAGE never drains (matrix title) |

Classification drives which slice / seat owns the fix. **R0 is not M7-a Core work** (media/SIF owners).

---

## 4. Proposed mechanism (flag-gated)

M7-a is a **multi-slice shared pipeline program**, not a single function. Default product path keeps PRESENT residual assists **on** until A-gates. All new Core behavior is **env kill-switchable**. Implement turn does **not** edit GameQuirks / RealSifRpc.

### 4.1 Placement (owned code — future implement)

| Piece | Site | Seat |
|-------|------|------|
| Path2/3 sticky / IMAGE drain / M3P hold | `src/DetPS2.Core/Gif.cs` | S8 |
| VIF1 DIRECT → Path2 feed | `Vif1*.cs` / DMA GIF channel only as needed | S8 |
| BITBLT / PSM / local truth | `src/DetPS2.Core/Gs.cs` | S9 |
| DISPFB decode / composite / present | `GsDisplayCircuit.cs`, `GsPipeline.cs`, `Pcrtc.cs` | S10 |
| Metrics split (optional) | `Gs.cs` counters + scoreboard scrape | S10 |
| Ambient present refresh (only if gap is R4) | existing `GetPresentSpan` / `ForceRefreshPresentComposite` | S10 |

**Out of this design’s implement scope (explicit):** `GameQuirks/*`, `MidwayBootAssist.cs`, RealSifRpc, invent PATH3, FFmpeg, synthetic chrome plants.

### 4.2 Investigation → fix order (slices)

Align with G-GFX gates; flag-gated; residual assists stay on until A3.

#### Slice 0 — Inventory (telemetry only; no behavior change)

- Per title @ claim budget (SEMA_OFF preferred for honesty):  
  `gifP2/P3`, `PacketsCompleted` / `PacketsAborted`, `TagsCompletedImage`, `imgBytes`, `naturalDispfb` / `enNaturalDispfb`, `dispfbPx` / `naturalDispfbPx` / `residualDispfbPx`, `LastCompositeSource` / `compositeSource`, `expandHits`, Path3MaskedByVif / held Path3, `LastImage*` telemetry if scraped.
- Emit residual class **R0–R5** table for ≥6 fleet titles (A0).
- Tooling: existing `scoreboard-metrics` / `blocker-trace` claim lines; optional `DETPS2_TRACE_GIF=1` for TRXDIR/BITBLTBUF/DPSM on suspect titles.

#### Slice 1 — Separate “bytes never submitted” vs “submitted but not presented”

| Observation | Branch |
|-------------|--------|
| EE has texture ring; gif IMAGE ≈ 0 | → Slice 2 (Path/VIF/GIF) |
| imgBytes>0 (game path) but naturalDispfb=0 / lit=0 | → Slice 3 (DISPFB + composite) |
| Neither; assist Host→Local lights lit | PRESENT residual only; root may be R0 stream (other infra) |

#### Slice 2 — Path2/3 IMAGE delivery (S8, G-GFX-1/3)

Flag-gated work candidates (pick by inventory; do not wholesale unmask Path3):

| Fix class | Gate | Notes |
|-----------|------|-------|
| Multi-DMA IMAGE reassembly / sticky across budgeted chunks | G-GFX-3 | Ensure `_pktActive` IMAGE survives M1-b re-entry; no silent abort |
| Path2 held under Path3 sticky soak | G-GFX-1 | Commercial: DA/Dec Path2 paint after Path3 IMAGE setup |
| M3P matrix soak (no wholesale clear) | G-GFX-7 | Per `PATH3_MASK_MATRIX.md`; title-safe unmask only when real MSKPATH3 |
| TRX setup order / incomplete BITBLT before IMAGE data | G-GFX-3 | TRACE_GIF prove BITBLTBUF+TRXDIR before DrainImage |

**Env (implement):**

| Env | Default | Role |
|-----|---------|------|
| `DETPS2_DISABLE_M7A_PATH_IMAGE=1` | unset = feature on (when landed) | Kill Path IMAGE delivery hardenings introduced under M7-a |
| `DETPS2_TRACE_GIF=1` | off | Existing; TRX/tag ring for bisect |

Do **not** invent PATH3 packets when gifP3=0.

#### Slice 3 — Natural DISPFB circuit + composite preference (S10, G-GFX-5)

When software writes DISPFB, composite must prefer **Natural** over Frame / FBP0 / LastImageTrx when natural page has real RGB.

| Fix class | Gate | Notes |
|-----------|------|-------|
| Circuit gen invalidation already present — fix residual races | G-GFX-5 | Expand stamp / black prim full-FB blocking natural merge (R4) |
| Prefer natural when lit under DISPFB > residual floor | G-GFX-5 | Do not plant DISPFB |
| B3 DISPFB=0 | A4 | Document residual FRAME/FBP0 as **honest residual**, not forced fake DISPFB |

**Env:**

| Env | Default | Role |
|-----|---------|------|
| `DETPS2_DISABLE_M7A_NATURAL_DISPFB=1` | unset = on | Kill M7-a composite preference changes |
| `DETPS2_NO_HOST_LOCAL_RESIDUAL=1` | **off** (campaign green) | Bisect: silence assist Host→Local **when assists honor it** (assist-side; not this design’s Core edit — open Q for assist flag ownership) |

#### Slice 4 — Demote residual assists under flags (post A1/A2)

- Once natural floor holds, fleet A/B with residual assist silenced on ≥3 titles (A3).
- **This design does not edit assists.** Implement checklist: open follow-on ticket for assist env-off hooks if missing; Core may only split telemetry so G-GFX-3 cannot pass on residual alone (open Q2).

#### Slice 5 — Expand demotion last (G-GFX-6)

Only after IMAGE/DISPFB hold px without ofx strip expand. **Not** M7-a v1 acceptance; adjacency only.

### 4.3 Hard bans (invariants)

| Ban | Why |
|-----|-----|
| Invent PATH3 / synthetic GIFtag SPRITE for chrome | `CORRECTNESS.md` / G-GFX-7 |
| Plant DISPFB1/2 from host | GX-040/041 law; B3 residual is FRAME/FBP0 |
| FFmpeg / host-decoded logos / branded UI paint | Correctness |
| Count assist-only Host→Local as G-GFX-3 pass without split (if Q2 accepts split) | Scoreboard honesty |
| Wholesale M3P clear without matrix soak | `PATH3_MASK_MATRIX.md` |
| Demote expand by zeroing px floor without Path/DISPFB replacement | G-GFX-6 order |
| Delete PRESENT assists before A3 | Campaign green / residual safety |
| Edit GameQuirks / RealSifRpc in M7-a implement turns without explicit ACK | Design boundary |

### 4.4 Pseudocode (inventory + branch — no Core invent)

```text
ClassifyResidual(title @ claim):
  if ee_texture_bound == false and assist_host_local_lit: return R0
  if TagsCompletedImage == 0 and gif_image_tags == 0: return R1
  if imgBytes > 0 and naturalDispfb and naturalDispfbPx == 0 and residualDispfbPx > 0:
    if source == LastImageTrx: return R2
    return R2  # wrong page / empty natural FBP
  if not naturalDispfb and residualDispfbPx > 0: return R3
  if imgBytes > 0 and naturalDispfb and present mostly black: return R4
  if Path3Masked and heldPath3 > 0 and TagsCompletedImage starved: return R5
  return OK_natural  # rare at MENU today
```

Fix branch: R1/R5 → Slice 2; R2/R3/R4 → Slice 3; R0 → defer non-M7-a.

---

## 5. Files to touch (implement turn — after ACK)

Per slice; **not all files in one PR**. Prefer small flag-gated PRs.

| File | Slice | Change class |
|------|-------|--------------|
| `src/DetPS2.Core/Gif.cs` | 2 | IMAGE multi-DMA / sticky / budgeted residual / hold drain (only if inventory proves gap) |
| `src/DetPS2.Core/Vif1*.cs` / DMA GIF path (S8 only) | 2 | DIRECT feed completeness if Path2 IMAGE never starts |
| `src/DetPS2.Core/Gs.cs` | 2–3 | BITBLT/PSM/local truth; optional `ImageBytesWritten` split (game vs residual); composite preference |
| `src/DetPS2.Core/GsDisplayCircuit.cs` | 3 | Circuit decode / preference if gap |
| `src/DetPS2.Core/GsPipeline.cs` / `Pcrtc.cs` | 3 | Present cadence / composite call sites if R4 |
| Scoreboard scrape / claim print | 0 / optional metrics | Expose split counters if landed |
| `docs/graphics/DISCOVERY_LOG.md` | all | Append-only wall rows when fleet moves |

**Out of scope this item:** `GameQuirks/*`, `MidwayBootAssist.cs`, RealSifRpc, assist deletion, expand demotion as primary goal.

---

## 6. Flag / kill-switch summary

| Env | Default | Role |
|-----|---------|------|
| `DETPS2_DISABLE_M7A_PATH_IMAGE=1` | unset = on (when Slice 2 lands) | Kill M7-a Path IMAGE hardenings |
| `DETPS2_DISABLE_M7A_NATURAL_DISPFB=1` | unset = on (when Slice 3 lands) | Kill M7-a composite preference changes |
| `DETPS2_TRACE_GIF=1` | off | Existing GIF/TRX trace |
| `DETPS2_NO_HOST_LOCAL_RESIDUAL=1` | **off** | Bisect residual silence (assist-side; Core optional mirror later) |
| Existing M1-a/b/c GIF kill-switches | as today | Do not regress budgeted IMAGE / honest FQC |

**Default product policy:** residual Host→Local assists **remain enabled** until A3. M7-a Core fixes default **on** with kill-switches for A/B (same risk class as other GFX WPs).

---

## 7. Validation (verify-tier — do not invent pixels)

### 7.1 Doctrine for metrics

- Soft-GS metrics are ground truth (`CORRECTNESS.md`).
- **Pass criteria must not invent pixels:** no PATH3 plant, no DISPFB plant, no synthetic branded fill, no FFmpeg overlay counting as `px` / `imgBytes`.
- Prefer SEMA_OFF claim budgets for G-GFX-shaped gates.
- `imgBytes` / `naturalDispfbPx` must reflect **game GIF / software DISPFB** for A1/A2 (if residual still feeds `imgBytes`, A1 requires independent proof — open Q2).

### 7.2 Acceptance gates

| Gate | Criteria | Pixel honesty |
|------|----------|---------------|
| **A0** | Inventory table ≥6 fleet titles: residual class R0–R5 labeled; **no behavior change** | N/A (telemetry) |
| **A1** | G-GFX-3-shaped: ≥5 titles `imgBytes>0` from **game** GIF IMAGE (not assist Host→Local alone) at claim with SEMA_OFF | Real EE→GIF→BITBLT bytes only |
| **A2** | G-GFX-5-shaped: ≥4 titles `naturalDispfbPx>0` / `compositeSource=NaturalDispfb` without assist DISPFB plant | Real software DISPFB bind |
| **A3** | Residual assist silenced (`NO_HOST_LOCAL_RESIDUAL` or assist env) on ≥3 titles that pass A1/A2: Soft-GS present non-black; MENU YES holds | Natural path owns present |
| **A4** | B3 (or DISPFB=0 retail) has **documented** residual FRAME/FBP0 — not forced fake DISPFB | Honest residual allowed |
| **A5** | No new invent-PATH3 / synthetic chrome; expandHits trend down only with px floor held | Expand demotion not forced if px would collapse |

**Success definition:** Host→Local assist residual becomes **optional**; natural Path + DISPFB owns present on the A3 set.

### 7.3 Fleet smoke sketch (A/B)

```text
# baseline (residual assists on — campaign default)
detps2 scoreboard-metrics <user-media.json> --cycles=N
detps2 blocker-trace <user-media.json> --cycles=N

# Path IMAGE kill-switch A/B (after Slice 2 lands)
DETPS2_DISABLE_M7A_PATH_IMAGE=1 detps2 blocker-trace <user-media.json> --cycles=N

# Natural DISPFB kill-switch A/B (after Slice 3 lands)
DETPS2_DISABLE_M7A_NATURAL_DISPFB=1 detps2 blocker-trace <user-media.json> --cycles=N

# Residual silence bisect (only after A1/A2 floor; assist must honor flag)
DETPS2_NO_HOST_LOCAL_RESIDUAL=1 detps2 scoreboard-metrics <user-media.json> --cycles=N
```

Compare claim lines:

```text
claim: px=… prims=… gifP1=… gifP2=… gifP3=… imgBytes=… dispfbPx=… naturalDispfbPx=… residualDispfbPx=… expandHits=… gifCompleted=… gifAborted=…
softgs: … compositeSource=…
gif-pkts: completed=… aborted=…
```

**Title classes for A/B (illustrative; media from operator fleet):**

| Title | Expect class today | A1/A2 target shape |
|-------|--------------------|--------------------|
| GoW | R2 / residual Frame + LastImageTrx; natural DISPFB 0 lit | Game IMAGE + natural or documented residual |
| Dec / DA | R1 residual gameart Host→Local; Path2 menu | Game IMAGE imgBytes without assist feed |
| Haven | R1 / imgBytes=0 natural | Game IMAGE or stream R0 defer |
| Whip | R1 / firstscreen residual | Game IMAGE from GOE ring |
| BO2 | Multi-prim + MAINMENU residual | Game IMAGE + natural composite |
| B3 | R3 DISPFB unset | A4 documented residual OK |
| SM | Adjacent PATH3/logo spine | Natural gifP3 + NaturalDispfb |

### 7.4 Unit / synthetic (optional)

Prefer fleet. Cheap Core smokes if already present: multi-DMA IMAGE sticky; DISPFB composite source selection (natural vs residual). Do not add invent-pixel tests that assert assist chrome.

### 7.5 Interaction with M5-a (DMA/VIF/GIF IRQ)

If inventory shows titles stuck with pending GIF/VIF busy and never submitting next IMAGE chain, **M7-a Slice 2 is blocked on M5-a completion IRQ** for those titles (B3/Haven class). Document as dependency, not invent completion inside GIF.

---

## 8. Non-goals

| Non-goal | Owner / later |
|----------|----------------|
| Per-title chrome plants as long-term fix (new gameart plants, invent PATH3 SPRITE) | Banned |
| FFmpeg / host-decoded logos / synthetic branded UI | `CORRECTNESS.md` |
| Treating residual Host→Local as MENU **natural** progress for G-GFX-3/5 | Scoreboard honesty |
| Demoting expand by zeroing px floor without Path/DISPFB replacement | G-GFX-6 later |
| Mass-delete PRESENT assists before A3 | Campaign residual |
| Core GameQuirks / RealSifRpc edits in M7-a | Explicit non-scope |
| Fixing R0 FILEIO/WAD/SIF bind under M7-a | Infra-debt #1–2, #5 |
| Full Path1 / VU1 XgKick gameplay (G-GFX-8/9) | Later GFX seasons |
| Wholesale M3P clear for all titles | `PATH3_MASK_MATRIX.md` |
| Soft-GS host GPU as claim truth | Soft-GS metrics only |

---

## 9. Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| A1 passes on assist `imgBytes` alone | High (ops/honesty) | Open Q2: split counters; inventory proves game IMAGE tags |
| Wholesale Path3 unmask regresses B3 path-sync | High | Matrix soak; no clear without MSKPATH3 evidence |
| Composite prefers wrong page → lit>0 but wrong logo | Med | Prefer natural only when natural RGB non-empty; residual metrics still honest |
| ForceRefresh thrash burns cycles | Low | Existing `_mergeBlackBypassArmed` |
| M5-a IRQ gap starves IMAGE submit | Med | Dependency callout; do not fake IRQ in M7-a |
| Silencing residual before natural floor → black MENU fail | High | A3 only after A1/A2; default residual on |
| Scope creep into title stream/WAD | Med | R0 taxonomy; defer non-GFX owners |
| Interaction with expand black stamp hides natural DISPFB | Med | Slice 3 R4; expand demotion only after px hold |

---

## 10. Relation to existing code (read-only map)

| Existing | Relation to M7-a |
|----------|------------------|
| `Gif.DrainImage` + sticky Path2/3 | Delivery spine; Slice 2 hardens residual gaps only |
| `Gif` G2 Path2 hold under Path3 | Already multi-DMA IMAGE safe; soak commercial still weak titles |
| `Gs.BeginTrxFromDir` / `WriteImageData` | Host→Local primitive **exists**; assists use same path |
| `Gs.CompositeDispfbToFramebuffer` residual ladder | Honest PRESENT residual; Slice 3 prefers natural when real |
| `ForceRefreshPresentComposite` | Assist + black-FB rescue; not long-term substitute for DISPFB |
| PRESENT GameQuirks Host→Local feeds | Escape hatch until A3; **do not edit this design** |
| G-GFX-3 / G-GFX-5 | North-star gates M7-a A1/A2 map onto |
| `PATH3_MASK_MATRIX.md` | Unmask policy for R5 titles |
| `EXPAND_POLICY.md` | Expand after IMAGE/DISPFB (Slice 5 adjacency) |
| M5-a DMAC/VIF/GIF completion | May block IMAGE chain submit (open Q6) |

---

## 11. Implement checklist (next turns after ACK)

Ordered; each step is its own ACK-sized PR unless inventory proves free:

1. **A0 inventory** — residual class table ≥6 titles from existing scoreboard/blocker-trace; append discovery log if new walls. **No Core behavior change.**
2. Split R0 vs R1–R5; open non-M7 tickets for pure R0 stream.
3. **Slice 2** (if R1/R5 majority): flag-gated Path IMAGE hardenings in `Gif.cs` (+ S8 VIF only if proven); kill-switch; TRACE_GIF bisect.
4. **Slice 3** (if R2/R3/R4 majority): flag-gated natural DISPFB / composite preference; no DISPFB plant; B3 A4 doc.
5. Optional metrics split (Q2 ACK): game vs residual `imgBytes` for scoreboard G2 honesty.
6. **A1/A2 fleet** SEMA_OFF; then residual-silence A3 on ≥3 titles (assist env — **separate** GameQuirks PR if flags missing).
7. Do **not** delete PRESENT assists; do **not** push without operator ACK; do **not** invent PATH3.

---

## 12. Open questions for ACK

1. **Per residual title primary owner:** For each of GoW / Haven / Whip / Dec / BO2 / B3, is the missing piece **Path IMAGE (R1/R5)**, **DISPFB/composite (R2–R4)**, or **upstream asset never bound (R0)**? A0 inventory answers; ACK should confirm A0 is required before any Core Slice 2/3 PR.
2. **Telemetry split:** Should assist Host→Local bytes continue to count toward `ImageBytesWritten` / scoreboard `imgBytes`, or be split (`imgBytesGame` vs `imgBytesResidual`) so G-GFX-3 / A1 cannot pass on residual alone?
3. **GoW path:** Is natural shell IMAGE expected on Path3 or Path2 DIRECT after Fedo decode — what does Play! show for TRX/DBP vs DISPFB FBP? (Oracle before large GoW-only Core work.)
4. **`LastImageTrx` residual long-term:** When is sampling the texture page as display **acceptable residual** (A4-style document) vs always wrong (must natural DISPFB own present)?
5. **PATH3 M3P:** Which titles need unmask policy vs held Path3 forever starving IMAGE (matrix soak list for R5)? Confirm no wholesale clear in M7-a.
6. **M5-a dependency:** Does missing VIF1/GIF **IRQ completion** prevent games from submitting the next IMAGE chain (pending/busy), making M7-a Slice 2 blocked on M5-a for B3/Haven?
7. **`DETPS2_NO_HOST_LOCAL_RESIDUAL` ownership:** Core-only ignore of residual composite sources vs assist-side skip of Host→Local feed? Design prefers assist-side silence for A3 (no Core GameQuirks edit this item) — ACK preferred owner.
8. **Slice priority if fleet is mixed:** If A0 shows half R1 and half R2, ship Slice 2 and Slice 3 as parallel seats (S8 + S10) or serial?

---

## 13. Ready for implement ACK?

**Yes — design is implement-ready as a multi-slice program** (inventory → Path IMAGE → natural DISPFB → residual demotion), with flag gates, hard bans, and verify-tier metrics that forbid inventing pixels.

**First implement after ACK should be A0 inventory only** (no Core behavior change) unless ACK explicitly prioritizes a single proven R1/R2 fix with kill-switch.

**Not a one-function rubber-stamp:** success is natural Path+DISPFB owning present; Host→Local assist residual becomes optional under A3.

---

## 14. Source map

| Artifact | Path |
|----------|------|
| Seed | `docs/infra-audits/m7a-path23-image-dispfb-seed.md` |
| Debt audit §6 / priority #6 | `docs/infra-audits/gamequirks-infra-debt.md` |
| GFX phase plan G-GFX-3/5 | `docs/GRAPHICS_PIPELINE_PHASE_PLAN.md` |
| Path2 sticky | `docs/graphics/PATH2_STICKY_W11C.md` |
| PATH3 mask | `docs/graphics/PATH3_MASK_MATRIX.md` |
| Expand policy | `docs/graphics/EXPAND_POLICY.md` |
| Discovery log | `docs/graphics/DISCOVERY_LOG.md` |
| Correctness | `docs/CORRECTNESS.md` |
| GIF IMAGE / Path2/3 | `src/DetPS2.Core/Gif.cs` |
| Composite circuit | `src/DetPS2.Core/Gs.cs`, `GsDisplayCircuit.cs`, `GsPipeline.cs` |
| Scoreboard metrics | `tools/SCOREBOARD_SCHEMA.md` |

---

*Design only. No Core / GameQuirks / RealSifRpc changes in this note. Prefer shared Path/IMAGE/DISPFB PRs that silence Host→Local residual under env-off over growing assist chrome. Correct black + honest residual beats pretty lie.*
