# M7-a A0 inventory — residual class table (evidence-sourced, no Core change)

**Date:** 2026-08-04
**Mode:** read-only classification per `docs/infra-audits/m7a-path23-image-dispfb-design.md` §4.4/§7.2 (A0 gate). No code changes. All numbers pulled from existing `docs/title-ports/*.md` claim-tier records; no fresh canaries were needed — the fleet already has rich session history.

## Method

Classification follows the design doc's own pseudocode (§4.4): R1 (no real GIF IMAGE), R2 (IMAGE wrong page), R3 (DISPFB unset), R4 (composite skip despite IMAGE+DISPFB set), R5 (Path3 masked/starved), R0 (upstream bytes never bound — not M7-a's problem). Where the design doc's own §7.3 table already guessed an "expected class today," I note whether real evidence **confirms** or **corrects** that guess.

## Per-title table

| Title | Serial | imgBytes | natural/residual DISPFB | compositeSource | gifP1/P2/P3 | Class | Doc's own guess (§7.3) | Evidence source |
|-------|--------|----------|---------------------------|------------------|-------------|-------|--------------------------|------------------|
| God of War | SCUS_973.99 | 262144 (assist Host→Local; real gifP3=0 elsewhere) | natural=0, residual=60866 | Frame | 0/31/0 | **R1** (corrected from doc's R2 guess — see note) | `docs/title-ports/GOD_OF_WAR.md` |
| Haven | SLUS_205.17 | 194560 | natural=43132, residual=0 | **NaturalDispfb** | 0/65/68 | **near-natural, not R1-R5** (major correction — doc guessed R1/imgBytes=0) | `docs/title-ports/HAVEN.md` |
| Blood Omen 2 | SLUS_200.24 | 0 | natural=0, residual=0 | — | 0/54/0 | **R1** (Path2 setup-only, qwc traffic but no IMAGE tag drain) | `docs/title-ports/BLOOD_OMEN_2.md` |
| Burnout 3 | SLUS_210.50 | 2.4M–2.7M (real, large) | naturalDispfbPx nonzero but DISPFB1 register reads 0 | merge/Frame | 0/296–340/438–491 | **R3** (confirmed — doc guessed R3) | `docs/title-ports/BURNOUT_3.md` |
| Whiplash | SLUS_206.84 | 262144 (explicitly Host→Local GOE firstscreen, **not** EE GIF IMAGE per doc's own note) | natural=36933, residual=0 | NaturalDispfb | 0/?/4 | **R1** (confirmed — doc guessed R1) | `docs/title-ports/WHIPLASH.md` |
| Vexx | SLUS_203.83 | 38912 (small, "texture crumbs") | natural flag=1, dispfbPx=644–6534 (small/partial) | — | not GIF-blocked; residual PC stuck at actor-stub vtable (EE code-side stall) | **R0-adjacent / out of M7-a scope** — real blocker is EE code execution stall, not Path2/3/DISPFB fidelity | `docs/title-ports/VEXX.md` |
| Shadow of the Colossus | SCUS_974.72 | 524288 (header explicitly calls this Host→Local MANAGER/NICO/KERNEL residual) | natural=120153, residual=0 | **NaturalDispfb** | 0/0/17 | **R1** (IMAGE delivery is residual-fed despite DISPFB circuit being natural — mixed case, primary gap is IMAGE not DISPFB) | `docs/title-ports/SHADOW_OF_THE_COLOSSUS.md` |
| MK: Deadly Alliance | SLUS_204.23 | 360448 (now gated on real gifP2≥2, "honest Host→Local" per doc) | natural=224016 | lit=75656 (non-black) | 0/240–606/— | **R1** (confirmed, but closest to graduating — imgBytes feed is already gifP2-gated, not pure invention) | `docs/title-ports/MK_DEADLY_ALLIANCE.md` |
| MK: Deception | SLUS_208.81 | 557056 (explicitly "natural EE IMAGE residual" — Host→Local SEC tiles from real gameart.ssf bytes) | DISPFB1=0 unset, dispfbPx=153405 | — | 0/145 (p2qws=5988)/6 | **R1 + secondary R3** (residual-fed IMAGE, DISPFB1 also never programmed) | `docs/title-ports/MK_DECEPTION.md` |

## Split (per doc §11 step 2)

- **R1/R5 majority (Slice 2 — Path2/3 IMAGE delivery candidates):** GoW, BO2, Whiplash, SotC, MK:DA, MK:Dec — **6 of 8 classified titles**.
- **R2/R3/R4 (Slice 3 — DISPFB/composite candidates):** Burnout 3 (clean R3), MK:Dec (secondary R3 alongside its primary R1).
- **R0 / out of scope:** Vexx — real blocker is an EE-side code stall (actor-stub vtable), not a Path2/3/DISPFB pipeline gap. Recommend NOT scoping Vexx into M7-a; it needs its own EE-execution investigation (different milestone).
- **Near-natural / not fitting R0-R5 at all:** Haven — see correction note below, this is the most consequential finding of this pass.

**Conclusion: R1/R5 dominates 6-of-8.** Per the design doc's own implement checklist (§11 step 3/4), this means **Slice 2 (Path2/3 IMAGE delivery)** is the higher-leverage next Core work, not Slice 3 — most titles' primary gap is that real game-path GIF IMAGE tags never complete, not that DISPFB/composite mis-selects a page. Slice 3 (DISPFB) work is lower priority right now, relevant mainly for Burnout 3 and as a secondary concern for MK:Deception.

## Correction to the design doc's own §7.3 guess table

Two real findings diverge from what the design doc guessed before this inventory ran:

1. **GoW: R1, not R2.** The doc guessed "R2 / residual Frame + LastImageTrx; natural DISPFB 0 lit." Real evidence shows `gifP3=0` (title-ports doc explicitly states "no shell IMAGE texture path yet, G-GFX-3") — there is no real Path3 GIF IMAGE delivery happening at all, which is R1's defining condition, not R2's ("IMAGE wrong page" implies IMAGE *is* delivered, just to the wrong place). GoW's residual DISPFB (Frame compositeSource) is a downstream symptom of the upstream IMAGE gap, not an independent DISPFB-stage bug.

2. **Haven does not fit the R0-R5 scheme at all — it's the most important finding here.** The doc guessed "R1 / imgBytes=0 natural." Real claim-tier (100M) evidence shows `imgBytes=194560` (nonzero), `naturalDispfbPx=43132` (nonzero), `compositeSource=NaturalDispfb`, and **both** `gifP2=65` and `gifP3=68` active. This is a natural, working pipeline at this budget — not a residual-driven present. Haven may need little or no M7-a Slice 2/3 work; if it's still showing any residual chrome elsewhere in a fuller playthrough, that's likely a different, narrower gap than what this doc's Path2/3/DISPFB program targets.

## M5-a / VIF1 completion dependency (design doc's own open Q6)

The design doc explicitly asks: "Does missing VIF1/GIF IRQ completion prevent games from submitting the next IMAGE chain, making M7-a Slice 2 blocked on M5-a for **B3/Haven class**?" Grok's parallel M5-TRACE work found a real VIF1 DMAC completion under-take for Haven (`finish=134, take=67, owed~64, creditAssist=1`) via `DETPS2_TRACE_DMAC`.

**Honest answer: NOT confirmed by the Soft-GS evidence gathered here, for either title named in the hypothesis.**

- **Haven** shows a fully natural DISPFB composite with real nonzero `imgBytes` and both `gifP2`/`gifP3` active at 100M claim — i.e., whatever the VIF1 under-take is doing at the DMAC level, it does not appear to be preventing Haven's Soft-GS pipeline from reaching a natural, non-residual present by claim tier. Either the game recovers/retries sufficiently within budget, the under-take affects timing/cadence rather than final pixel state, or the M5-TRACE sample and the HAVEN.md claim-tier record come from different run conditions that aren't directly comparable — I can't distinguish these from the data on hand.
- **B3** shows large real `imgBytes` (millions of bytes, genuine game-path IMAGE) with its actual gap being purely that `DISPFB1` is never programmed (R3) — not a stalled/starved IMAGE delivery pipeline. B3 is not showing the "never submitting next IMAGE chain" symptom the M5-a hypothesis describes.

**Recommendation:** do not block M7-a Slice 2 work on M5-a landing for Haven or B3 based on current evidence. The VIF1/DMAC completion gap Grok found is real and worth fixing on its own merits (M5-a), but this inventory pass found no direct causal link to either title's current Soft-GS IMAGE/DISPFB state at claim tier. If a future Slice 2 implementation attempt on Haven/B3 stalls in a way that traces back to VIF1/GIF IRQ starvation, revisit this — but don't gate the design on an unconfirmed dependency.

## Titles not covered (would need fresh evidence, not blocking this pass)

MK: Armageddon (same assist class as DA/Deception, not separately re-verified), Shaolin Monks (out of M4/M8 version scope but not yet checked for M7-a purposes). Neither blocks the 6-title minimum this A0 gate requires (8 were classified).

## Next step recommendation (not started here — needs its own design-first ACK, same as M4-S4-MIRROR got)

Per the R1/R5-majority split, the natural next design pick is **Slice 2 (Path2/3 IMAGE delivery hardening in `Gif.cs`)**, targeting the shared gap across GoW/BO2/Whiplash/SotC/MK-DA/MK-Dec: real Path2/3 GIF IMAGE tags not completing/delivering, forcing every one of those titles onto Host→Local assist residual. This is genuinely the "one-fix-many-titles" shape the mission wants — 6 titles share the same primary gap class. Recommend this be scoped as its own design doc (mirroring M4-S4-MIRROR's dual-ACK process) before any Core implementation, per this design doc's own hard ban: "Edit GameQuirks / RealSifRpc in M7-a implement turns without explicit ACK" and its general design-first posture for all Core GFX changes.
