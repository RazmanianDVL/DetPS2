# M7-a seed — PATH2/3 IMAGE + DISPFB composite fidelity (Host→Local residual)

**Status:** design **SEED** (implementation-ready sketch) — not a full final design  
**Date:** 2026-08-04  
**Mode:** read-only. **No Core code changes** in this note.  
**Priority source:** `docs/infra-audits/gamequirks-infra-debt.md` §6 / priority #6  
**Owned code (future):** `Gif.cs`, `Gs.cs` / `GsDisplayCircuit.cs` / `GsPipeline.cs`, VIF1 DIRECT→Path2, DMAC GIF Path3, present composite  
**Related:** `docs/GRAPHICS_PIPELINE_PHASE_PLAN.md` G-GFX-3/5, `docs/graphics/PATH2_STICKY_W11C.md`, `docs/graphics/PATH3_MASK_MATRIX.md`, `docs/graphics/EXPAND_POLICY.md`, `docs/graphics/DISCOVERY_LOG.md`, `docs/CORRECTNESS.md`

---

## 1. Problem class

MENU YES 9/9 Soft-GS surfaces often **do not** come from a complete retail path:

```text
EE texture/stream bytes
  → DMA GIF Path2/3 (or VIF1 DIRECT → Path2)
  → GIFtag IMAGE (Host→Local BITBLT setup + data)
  → local GS mem @ TRX/DBP
  → software DISPFB1/2 + PMODE EN
  → CompositeDispfbToFramebuffer → Soft-GS present
```

When any stage fails, assists **Host→Local BITBLT honest disc/RDRAM bytes** into Soft-GS local and force composite refresh so present is non-black. That is tagged **PRESENT** (pipeline honesty), not FPS and not “cheating invent” — doctrine allows residual **only** when pixels are real disc/EE bytes (`CORRECTNESS.md`).

**Class:** end-to-end **PATH2/3 IMAGE + DISPFB composite** fidelity gap → Host→Local Soft-GS residual crutches.

---

## 2. Evidence from assists / fleet (symptoms)

| Title / assist | Residual mechanism | Soft-GS symptom class |
|----------------|--------------------|------------------------|
| **GoW** `GodOfWarAssist` | Host→Local R_SHELL/TIT1; `ForceRefreshPresentComposite` when expand black + imgBytes under floor | Path2 setup/expand paints; natural IMAGE weak; DISPFB empty CT24 vs high-DBP PSMT4 |
| **Haven / SotC** `TeamIcoAssist` | Host→Local SYSTEM.RW3 / CUBE / MANAGER / NICO; re-merge on host present | Logo clear prims; imgBytes=0; DISPFB garbage → natural composite None |
| **Whip** `WhiplashAssist` | Host→Local firstscreen/frontend bulk BITBLT | Ring/GOE bytes never reach GIF IMAGE |
| **BO2** `BloodOmen2SnAssist` | Host→Local MAINMENU | Multi-prim IMAGE/DISPFB chrome residual |
| **Midway family** | Host→Local gameart.ssf (DA/Dec) | EE texture upload path incomplete |
| **B3** (audit / scoreboard) | Merge composite without natural DISPFB; residual FRAME/FBP0 | DISPFB1 often 0 → residualDispfbPx honest path |
| **SM** `MidwayBootAssist` | Assist PATH3 / logo spine (adjacent PRESENT) | Natural gifP3 + NaturalDispfb without forced spine |

**Policy repeated in assists:** no invent PATH3 packets, no synthetic branded color — residual only.

Discovery log snapshots (illustrative):

- GoW: high gifP2 / imgBytes from residual feed; `naturalDispfb` still 0; residual `Frame` / `LastImageTrx`.
- Dec: Host→Local gameart tiles → large imgBytes; Path2-only menu.
- B3: residual composite when DISPFB unset is **documented honest** (GX-041).

---

## 3. Hypothesized general infra gap

Assists fire because **one or more** of the following shared stages do not match retail graphs (not because Host→Local BITBLT API is missing — Soft-GS already implements TRXDIR Host→Local):

| Stage | Gap hypothesis | Pointers already in tree |
|-------|----------------|---------------------------|
| **Stream / bind** | Texture bytes never reach EE in time (FILEIO/SIF/WAD) → nothing to IMAGE | Infra-debt #1–2, #5 (orthogonal; M7-a assumes bytes *can* exist) |
| **DMA → GIF Path2/3** | Incomplete sticky / arb / M3P mask holds Path3; Path2 setup-only | `PATH2_STICKY_W11C.md`, `PATH3_MASK_MATRIX.md`; G-GFX-1/7 |
| **GIFtag IMAGE** | Multi-DMA IMAGE slices, wrong TRX/BITBLTBUF, PSM swizzle, abort mid-stream | G-GFX-3; Gif IMAGE multi-DMA smokes landed; fleet still imgBytes=0 on several titles |
| **Local mem truth** | Draw FRAME ≠ display page; IMAGE at high DBP while DISPFB points empty | GoW PSMT4 vs DISPFB CT24; `LastImageTrx` residual |
| **DISPFB / PCRTC** | DISPFB1/2 never programmed or wrong circuit/PSM/FBW | GX-040 decode landed; naturalDispfbPx still sparse |
| **Composite policy** | Present samples expand/black FB before local IMAGE merge | `ForceRefreshPresentComposite`; expand vs natural race (`EXPAND_POLICY.md`) |

**Hypothesis (one sentence):**  
Host→Local residual is the **PRESENT escape hatch** for an incomplete **natural graph** (Path delivery → IMAGE into correct local pages → software DISPFB → composite), not a missing host blit primitive.

---

## 4. Non-goals

- **No per-title chrome plants** as the long-term fix (no new gameart plants; no invent PATH3 SPRITE).
- **No FFmpeg / host-decoded logos** or synthetic branded UI (`CORRECTNESS.md`).
- **No treating residual Host→Local as MENU natural progress** for G-GFX-3/5 (scoreboard: `naturalDispfbPx`, `imgBytes` from **game** GIF, expandHits).
- **No demoting expand by zeroing px floor** without Path/DISPFB replacement (G-GFX-6 order).
- **No Core code in this seed**; flag-gated GFX WPs already exist — this seed only unblocks shared investigation order vs title seats.
- **Do not delete PRESENT assists** until natural path holds claim budgets under assist residual env-off.

---

## 5. Proposed investigation order

Align with G-GFX gates; flag-gated; default product path keeps residual assists **on** until A-gates.

1. **Inventory (telemetry only)**  
   - Per title @ claim: `gifP2/P3`, `PacketsCompleted`, IMAGE tags, `imgBytes`, `naturalDispfb` / `enNaturalDispfb`, `dispfbPx` / `naturalDispfbPx` / `residualDispfbPx`, `LastCompositeSource`, expandHits, Path3MaskedByVif.  
   - Classify residual as: **no IMAGE**, **IMAGE wrong page**, **DISPFB unset**, **composite skip**, **upstream no bytes**.

2. **Separate “bytes never submitted” vs “submitted but not presented”**  
   - If EE has texture ring but gif IMAGE=0 → S8 Path/VIF/GIF.  
   - If imgBytes>0 but naturalDispfb=0 / lit=0 → S10 DISPFB + composite.  
   - If neither and assist Host→Local lights lit → PRESENT residual only; root may be stream (other infra).

3. **Path2/3 IMAGE delivery (S8, G-GFX-1/3)**  
   - Reuse sticky/hold smokes; soak commercial: multi-DMA IMAGE, Path2 held under Path3 sticky, M3P matrix without wholesale unmask.  
   - Flag: `DETPS2_TRACE_GIF=1` for TRXDIR/BITBLTBUF/DPSM.

4. **Natural DISPFB circuit (S10, G-GFX-5)**  
   - When software writes DISPFB, composite must prefer Natural over Frame/FBP0/LastImageTrx.  
   - B3: document composite-only residual if retail never programs DISPFB (honest residual, not a plant).

5. **Demote residual assists under flags**  
   - Env sketch: `DETPS2_NO_HOST_LOCAL_RESIDUAL=1` (or per-title) for bisect once natural floor exists.  
   - Default remains residual **on** for campaign green.

6. **Expand demotion last (G-GFX-6)**  
   - Only after IMAGE/DISPFB hold px without ofx strip expand.

---

## 6. Acceptance sketch

| Gate | Criteria |
|------|----------|
| **A0** | Inventory table for ≥6 fleet titles: residual class labeled; no behavior change. |
| **A1** | G-GFX-3-shaped: ≥5 titles `imgBytes>0` from **game** GIF IMAGE (not assist Host→Local counters) at claim with SEMA_OFF. |
| **A2** | G-GFX-5-shaped: ≥4 titles `naturalDispfbPx>0` / Natural composite source without assist DISPFB plant. |
| **A3** | With residual assist silenced on ≥3 titles that pass A1/A2, Soft-GS present remains non-black and MENU YES holds. |
| **A4** | B3 (or any title with retail DISPFB=0) has **documented** residual FRAME/FBP0 path — not forced fake DISPFB. |
| **A5** | No new invent-PATH3 / synthetic chrome; expandHits trend down only with px floor held. |

Success = **Host→Local assist residual becomes optional**, natural Path+DISPFB owns present.

---

## 7. Open questions

1. For each residual title, is the missing piece **Path IMAGE**, **DISPFB programming**, or **upstream asset never bound** (FILEIO/WAD)? One seed cannot fix all three owners.
2. Should assist Host→Local bytes continue to count toward `ImageBytesWritten` telemetry, or be split so G-GFX-3 cannot pass on residual alone?
3. GoW: is natural shell IMAGE expected on Path3 or Path2 DIRECT after Fedo decode — what does Play! show for TRX/DBP vs DISPFB FBP?
4. When is `LastImageTrx` residual acceptable long-term vs always wrong (texture page as display)?
5. PATH3 M3P: which titles need unmask policy vs held Path3 forever starving IMAGE (matrix soak)?
6. Interaction with M5-a: does missing VIF1/GIF **IRQ completion** prevent games from submitting the next IMAGE chain (pending/busy), making M7-a blocked on M5-a for B3/Haven?

---

## 8. Source map

| Artifact | Path |
|----------|------|
| Debt audit §6 / priority #6 | `docs/infra-audits/gamequirks-infra-debt.md` |
| GFX phase plan G-GFX-3/5 | `docs/GRAPHICS_PIPELINE_PHASE_PLAN.md` |
| Path2 sticky | `docs/graphics/PATH2_STICKY_W11C.md` |
| PATH3 mask | `docs/graphics/PATH3_MASK_MATRIX.md` |
| Expand policy | `docs/graphics/EXPAND_POLICY.md` |
| Discovery log | `docs/graphics/DISCOVERY_LOG.md` |
| Composite circuit | `src/DetPS2.Core/GsDisplayCircuit.cs`, `Gs.cs` (`CompositeDispfbToFramebuffer`, residual sources) |
| GIF IMAGE | `src/DetPS2.Core/Gif.cs` |
| Scoreboard metrics | `tools/SCOREBOARD_SCHEMA.md` (`naturalDispfbPx`, `residualDispfbPx`, `imgBytes`) |

---

*Seed only. Prefer shared Path/IMAGE/DISPFB PRs that silence Host→Local residual under env-off over growing assist chrome. Correct black + honest residual beats pretty lie.*
