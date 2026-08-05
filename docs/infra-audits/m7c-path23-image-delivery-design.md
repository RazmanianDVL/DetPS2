# M7-c design — Slice 2: Path2/3 IMAGE delivery (bisect-first)

**Status:** design only (ready for dual ACK) — **no Core implement this turn**
**Date:** 2026-08-04
**Mode:** infra-only. **No GameQuirks edits. No RealSifRpc edits. No invent PATH3. No wholesale M3P clear. No push.**
**Tracks:** M7-a Slice 2 (`docs/infra-audits/m7a-path23-image-dispfb-design.md` §4.2), driven by A0 inventory's 6-title R1/R5 majority (`docs/infra-audits/m7-a0-residual-inventory.md`)
**Related:**
`docs/infra-audits/m7a-path23-image-dispfb-design.md` (parent design, hard bans, slice program)
`docs/infra-audits/m7-a0-residual-inventory.md` (evidence: GoW/BO2/Whip/SotC/MK-DA/MK-Dec = R1/R5)
`docs/graphics/PATH3_MASK_MATRIX.md` (M3P unmask soak gate — explicitly NOT this seat)
`docs/infra-audits/m4-s4-ee-mirror-design.md` (structural template + dual-ACK process this doc mirrors)
`src/DetPS2.Core/Gif.cs` (packet state machine — read in full for this design)

**Owned code (future PR, not this seat):** `src/DetPS2.Core/Gif.cs` telemetry additions only (Slice 2a); no `Gs.cs`/`Dmac.cs`/`Vif1*.cs` edits until Slice 2a data exists.
**Out of scope:** M3P auto-unmask (soak-gated, separate seat per `PATH3_MASK_MATRIX.md` §4), DISPFB/composite (Slice 3), assist edits, GoW-only or any single-title hardcoded fix.

---

## 0. One-line summary

A0 inventory found 6 of 8 titles (GoW, BO2, Whiplash, SotC, MK:DA, MK:Dec) sitting at R1 — real game-path GIF IMAGE tags aren't completing, forcing every one onto Host→Local assist residual. Before writing any `Gif.cs` fix, I read the actual packet state machine in full. Two of the parent doc's four candidate fix classes turn out to be **already correctly implemented** (see §2), which narrows the real gap and means jumping straight to a speculative "harden Gif.cs" PR risks fixing nothing (or worse, risks becoming exactly the kind of per-title tinkering the mission wants avoided, since we wouldn't actually know *why* IMAGE isn't completing for these 6 titles). **Recommended shape: Slice 2a is a small, cheap, telemetry-only bisect landing (not a behavior change) to find out precisely where in the pipeline each title's IMAGE delivery breaks down — before proposing any Slice 2b behavior fix.**

---

## 1. Problem class vs what's already landed

### 1.1 Re-checking the parent doc's four Slice 2 candidates against live code

| Candidate (parent doc §4.2) | Status found by reading `Gif.cs` in full |
|---|---|
| **Multi-DMA IMAGE reassembly / sticky across budgeted chunks** (G-GFX-3) — "ensure `_pktActive` IMAGE survives M1-b re-entry; no silent abort" | **Already correct.** `DrainImage` (`Gif.cs:1155-1186`) is a proper resumable state machine: `_pktActive`/`_pktLoop`/`_pktNloop`/`_pktFlg` are instance fields, not locals. `Step()` (`Gif.cs:1215-1252`) drains `_pendingBudgetPath`/`_pendingBudgetAddr`/`_pendingBudgetQwc` on the *next* tick via the real `ReceivePath1/2/3Data` entry points (not a shortcut), and `ProcessTransferBudgeted` (`Gif.cs:929-941`) only sets a new pending slot if `_pendingBudgetPath == 0`, so a still-oversized residual correctly re-arms across as many ticks as needed (the `Step()` comment at `Gif.cs:1217-1222` documents this explicitly — this was hardened during M1-b this session). No truncation, no silent restart. **Not the gap.** |
| **Path2 held under Path3 sticky soak** (G-GFX-1) | **Already landed**, per the parent doc's own §2.1: "Hardening already landed (do not re-litigate as missing primitives)." Confirmed present in code (`_heldPath2Count`/`_heldPath2InlineCount` checks around `Gif.cs:428, 812, 815`). **Not the gap.** |
| **M3P matrix soak (no wholesale clear)** (G-GFX-7) | **Explicitly NOT ready for this seat.** `PATH3_MASK_MATRIX.md` §4 lists hypotheses H1-H4 for a core-level unmask condition, and says each "needs multi-title soak (≥5, include GoW + B3 + Dec/SM) before core change" — current state is H0 (do nothing in core). Landing any of H1-H4 here would violate the parent doc's own hard ban ("Wholesale M3P clear without matrix soak") since that soak hasn't happened. **Correctly out of scope for M7-c; needs its own future soak-gated seat.** |
| **TRX setup order / incomplete BITBLT before IMAGE data** (G-GFX-3) | **Genuinely unverified** — BITBLTBUF/TRXDIR register writes route through `Gs.cs` (`WriteGsRegister`), not `Gif.cs`; I have not read `Gs.cs`'s BITBLT setup path in this seat. This is a real open question, not resolved by my `Gif.cs` read. |

### 1.2 What this leaves

Two of four candidates are done; one is correctly deferred (soak-gated); one is a genuine unknown. That's a thin remainder to justify a `Gif.cs` behavior PR on its own — and critically, **none of the four candidates directly explain GoW's `gifP3=0`** (not "IMAGE tags truncate," but "no Path3 GIF DMA activity registers at all" per `PATH3_MASK_MATRIX.md`'s own GoW row: "PATH3 not natural yet ... wait real MSKPATH3 unmask + PATH3 DMA"). If Path3 DMA is never being *submitted* for GoW at this budget, that's upstream of everything in `Gif.cs`'s tag-parsing logic — closer to Vexx's A0 finding (EE-side execution stall, out of M7-a scope) than to a fixable delivery-mechanics bug.

**Conclusion: we don't actually know why these 6 titles are R1 yet — we know two plausible causes are ruled out.** Proposing a specific `Gif.cs` behavior change now would be guessing. The honest next step, matching this session's own established discipline (A0 before Slice 2/3; M4-g's pre-check before scoping M4-g's fix; M4-h's re-isolation before trusting M4-g's regression report), is a **bisect pass first.**

---

## 2. Per-title signature differences (why one bisect table, not 6 separate investigations)

Pulled from `docs/infra-audits/m7-a0-residual-inventory.md` and `docs/graphics/PATH3_MASK_MATRIX.md`:

| Title | gifP3 signature | Likely bucket |
|---|---|---|
| GoW | **0**, flat | PATH3 DMA never submitted (upstream of `Gif.cs`) — matrix doc: "wait real MSKPATH3 unmask + PATH3 DMA" |
| BO2 | 0 in A0's Path2/3 columns (`0/54/0`) | Same bucket as GoW — no Path3 activity at all |
| Whiplash | Path2-driven per matrix doc ("Path2 firstscreen"); Path3 not the primary channel | Different bucket — may be an M7-a **Slice 3** (DISPFB) case wearing an R1 label, not a Path3 delivery gap; flagged for re-check, not assumed |
| SotC | `0/0/17` — some Path3 activity, low | Small but nonzero — different bucket from GoW/BO2 |
| MK:DA | **6**, flat across multiple independent runs (`docs/title-ports/MK_DEADLY_ALLIANCE.md` — same `gifP3=6` in at least 4 separate claim records) | Path3 DMA **is** submitting and completing a small, stable number of tags — plateaued, not absent. Most tractable signature of the six. |
| MK:Dec | `0/145/6` — same `gifP3=6` shape as MK:DA (Midway family, shared assist class) | Same bucket as MK:DA |

**This alone is a finding worth landing even before any fix:** "R1" as a single label is hiding at least two different underlying situations (zero Path3 activity vs. small-and-plateaued Path3 activity). A bisect pass that just labels which of the 6 titles is in which bucket is itself useful, cheap, and non-speculative.

---

## 3. Proposed mechanism — Slice 2a (bisect telemetry only)

### 3.1 Intent

Add read-only counters/trace lines to `Gif.cs` (and cite, not edit, `Gs.cs`/`Dmac.cs` call sites if a counter needs a hook there) that distinguish, per title, **where** in the pipeline Path3 IMAGE delivery stops:

```text
Was GIF Path3 DMA channel ever kicked (Dmac CHCR)?
        │
        ▼ no → bucket "DMA never submitted" (likely R0-adjacent / EE-side, like Vexx — defer, not M7-a Core work)
        ▼ yes
Did a GIFtag with flg=IMAGE (2) get parsed on Path3? (_tagsSeen / _lastTagFlg via existing trace)
        │
        ▼ no → bucket "Path3 traffic exists but never an IMAGE tag" (different gap — REGLIST/PACKED only?)
        ▼ yes
Did DrainImage reach _pktLoop >= _pktNloop (packet completed) or stall (_pktActive stays true across N ticks)?
        │
        ▼ stalls → bucket "IMAGE tag started, budget/re-entry issue" (would contradict §1.1's code read — needs re-verification)
        ▼ completes → bucket "IMAGE tag completes; check Gs.cs BITBLT ordering next" (Slice 2a hands off to a Gs.cs-focused follow-up, not this seat)
```

### 3.2 Concrete additions (illustrative; non-binding names)

| Piece | Change |
|---|---|
| `DETPS2_TRACE_GIF_BISECT=1` (new, separate from existing `DETPS2_TRACE_GIF=1` to avoid noise) | Emits one line per title run: `[GIF-BISECT] path3Kicks=N path3ImageTags=N path3ImageCompleted=N path3ImageStalled=N lastStallReason=<free text>` |
| Counter: DMA-channel-kicked-for-Path3 | Only if not already exposed — check `Dmac.cs` for an existing per-channel kick counter before adding a new one (avoid duplicate telemetry) |
| Counter: `_tagsCompletedImage` **specifically on Path3** vs Path1/2 | `_tagsCompletedImage` (`Gif.cs:1079`) already exists but is not currently split by path — cheap split, telemetry only |
| No new fields beyond counters | No behavior change — this must be a pure observation pass, same as A0 |

### 3.3 What Slice 2a explicitly does NOT do

- Does not change what `ProcessTransfer`/`DrainImage`/`DrainPacked`/`DrainReglist` actually do.
- Does not touch `Dmac.cs`, `Gs.cs`, `Vif1*.cs` behavior — read-only hooks into existing counters only, and only if a genuinely new counter is needed (prefer reusing what already exists first).
- Does not attempt any fix for GoW/BO2 specifically, even though they're likely R0-adjacent — that determination is Slice 2a's *output*, not something to presuppose going in.

---

## 4. Slice 2b (deferred — behavior fix, scope TBD by Slice 2a data)

**Not designed in this doc.** Per the bucket Slice 2a resolves each title into:

- **"DMA never submitted" bucket** (likely GoW, BO2): reclassify as R0-adjacent, hand off to a different milestone (EE-execution / stream-bind investigation, same class as Vexx's A0 finding) — **not** a `Gif.cs` Slice 2b fix at all.
- **"Traffic exists but plateaued" bucket** (likely MK:DA, MK:Dec, given the consistent `gifP3=6` signature across multiple independent runs): this is the genuinely promising Slice 2b target — something is capping Path3 IMAGE tag submission/completion at a small constant rather than scaling with game activity. Needs its own bisect-informed design (this doc does not speculate on the mechanism) once Slice 2a's `path3ImageStalled`/`lastStallReason` data exists.
- **Whiplash/SotC**: signatures don't clearly match either bucket from existing A0 data alone — Slice 2a's per-title run should resolve which bucket they fall into before scoping any fix.

---

## 5. Flag-gated, kill-switch, default-safe strategy

| Control | Default | Purpose |
|---|---|---|
| `DETPS2_TRACE_GIF_BISECT=1` | **off** | Opt-in diagnostic only; zero behavior/perf change when unset (matches `DETPS2_TRACE_DMAC`/`DETPS2_TRACE_MIRROR` precedent from M5-TRACE/M4-S4-MIRROR this session) |
| Counter split (`_tagsCompletedImage` by path) | Always accumulates (cheap), **printed only under trace flag** | Same "counters free, prints gated" pattern as M4-S4-MIRROR's Phase-0 telemetry and M5-a's Phase-0 DMAC telemetry |

No product-default behavior changes in this seat at all — Slice 2a is pure telemetry, so there is no plant/mechanism to soft-off or claim-tier-gate the way M4-S4-MIRROR's plant flip needed. The claim-tier bar from M4-S4-MIRROR's Q9 amendment applies to **Slice 2b** (whatever behavior fix eventually lands), not to this telemetry-only seat.

---

## 6. Validation plan

### 6.1 Slice 2a (this seat, if ACK'd trivial)

| Check | Expect |
|---|---|
| `DETPS2_TRACE_GIF_BISECT` unset | Byte-identical scoreboard vs current baseline on all 6 titles + control titles (Haven, B3) — pure telemetry must not perturb behavior |
| `DETPS2_TRACE_GIF_BISECT=1` on GoW/BO2 | Expect `path3Kicks=0` (confirms "DMA never submitted" bucket) or reveals a surprise (kicks>0 but tags=0 — different bucket than predicted, equally valuable) |
| `DETPS2_TRACE_GIF_BISECT=1` on MK:DA/MK:Dec | Expect `path3ImageTags>0` with either `path3ImageCompleted` plateauing or `path3ImageStalled` climbing — distinguishes "completes but doesn't grow" from "starts but never finishes" |
| Full smoke matrix | Green (telemetry-only change, same bar as M4-S4-MIRROR/M5-a Phase-0 landings) |

### 6.2 Slice 2b (future seat, not this doc's acceptance criteria)

Deferred. Whatever mechanism Slice 2a's data motivates will need its own diagnose-tier proof-of-concept **and claim-tier (100M) byte-identical/non-worse validation before any product-default change** — same bar M4-S4-MIRROR's Q9 amendment established for the whole M4/M7 program, not just S4.

---

## 7. Non-goals

| Non-goal | Why |
|---|---|
| Any `Gif.cs` behavior change in this seat | Slice 2a is telemetry-only; a behavior fix needs its own design once bisect data exists |
| M3P auto-unmask (H1-H4) | Soak-gated per `PATH3_MASK_MATRIX.md` §4; needs its own ≥5-title soak, not this seat |
| Fixing GoW/BO2's Path3-DMA-never-submitted symptom | Likely not an M7-a problem at all (R0-adjacent, EE-execution class like Vexx) — Slice 2a's job is to confirm this, not fix it |
| `Gs.cs` BITBLT/TRXDIR ordering investigation | Real open question (§1.1) but needs its own read-through of `Gs.cs`, out of scope for a `Gif.cs`-focused bisect pass |
| Assist edits / GameQuirks changes | Explicit parent-doc ban, unchanged here |
| Whiplash/SotC-specific investigation | Their bucket is unresolved by existing data; Slice 2a resolves it, this doc doesn't presuppose an answer |

---

## 8. Open questions for dual-ACK

| ID | Question | Options | Design bias |
|---|---|---|---|
| **Q1** | Is Slice 2a (bisect telemetry only) an acceptable first landing, or should the design instead attempt a direct behavior fix on the best-understood bucket (MK:DA's plateaued-but-nonzero signature) without a telemetry pass first? | (a) bisect first (b) skip to MK:DA behavior fix directly | **(a)** — we don't actually know the mechanism yet; jumping straight to a fix on a guess risks the same class of error M4-g's pre-check caught (a plausible-sounding hypothesis that didn't hold up under isolation) |
| **Q2** | Should the Path3-specific `_tagsCompletedImage` split be a genuinely new counter, or should Slice 2a first check whether `RingPush`'s existing ring buffer (`Gif.cs` — used elsewhere for tag/packet tracing) already carries enough data to answer the bisect questions via post-processing, avoiding new fields entirely? | new counter / ring-buffer post-process / both | **Ring-buffer-first** — cheaper, no new state; only add counters if the ring buffer genuinely can't answer the bucket question |
| **Q3** | Does a DMA-channel-kicked-for-Path3 counter already exist in `Dmac.cs`? (I did not read `Dmac.cs` in this seat — parent doc's owner map puts Path2/3 IMAGE drain in `Gif.cs`/S8 but the kick itself is DMAC's.) | check first / assume new counter needed | **Check first** — avoid duplicate telemetry; whoever implements should grep `Dmac.cs` before adding anything |
| **Q4** | Should Whiplash and SotC be included in the first Slice 2a bisect run, or deferred to a second pass after GoW/BO2/MK-DA/MK-Dec's buckets are confirmed (smaller first landing, matching M4-S4-MIRROR's GoW-only-v1 precedent)? | all 6 first pass / 4 first + 2 deferred | **4 first** (GoW, BO2, MK:DA, MK:Dec) — their existing A0 signatures already suggest which bucket they're likely in, making them cheaper to confirm; Whiplash/SotC's ambiguous fit is worth its own follow-up rather than diluting the first pass |
| **Q5** | If Slice 2a confirms GoW/BO2 are genuinely R0-adjacent (Path3 DMA never submitted), should that finding retroactively update A0's `m7-a0-residual-inventory.md` classification, or stay as a Slice 2a-specific note? | update A0 doc / separate note | **Update A0** — same append-only discovery-log spirit as `docs/graphics/DISCOVERY_LOG.md`; keeps one source of truth for classification rather than two documents disagreeing |

---

## 9. Implementation sketch (future PR only, after ACK)

### 9.1 Touch list

| Area | Change |
|---|---|
| `src/DetPS2.Core/Gif.cs` | `DETPS2_TRACE_GIF_BISECT` flag; per-path `_tagsCompletedImage` split (if Q2 doesn't resolve via ring-buffer post-process); trace line emission |
| `src/DetPS2.Core/Dmac.cs` | Read-only check for existing Path3-kick counter (Q3) — new counter only if genuinely absent |
| `docs/infra-audits/m7-a0-residual-inventory.md` | Append bisect results per Q5 if confirmed |
| `docs/graphics/DISCOVERY_LOG.md` | Append row if new wall/finding, per parent doc §5/file index |

### 9.2 Minimal first land (if dual ACK wants smallest PR)

1. Grep `Dmac.cs` for existing Path3-kick telemetry (resolve Q3) before writing any new counter.
2. Add `DETPS2_TRACE_GIF_BISECT=1` flag + per-path IMAGE-tag-completed split in `Gif.cs` only.
3. Run bisect on GoW, BO2, MK:DA, MK:Dec (Q4's 4-title first pass) at claim tier.
4. Write up findings; propose Slice 2b's actual mechanism as a follow-up design doc, scoped to whichever bucket(s) the data supports — do not pre-commit to a fix here.

### 9.3 Explicit non-diff

- No change to `ProcessTransfer`/`DrainImage`/`DrainPacked`/`DrainReglist`/`Step()` logic.
- No `Gs.cs`, `Vif1*.cs` behavior changes.
- No GameQuirks/RealSifRpc edits.
- No UNC / emulator write outside local `detps2` tree.

---

## 10. Definition of done (M7-c Slice 2a)

- [ ] Dual ACK on Q1-Q5 (or recorded deferrals).
- [ ] Bisect telemetry lands behind flag, default off, byte-identical scoreboard when unset.
- [ ] GoW/BO2/MK-DA/MK-Dec (or all 6 per Q4) each get a bucket classification with real trace evidence.
- [ ] A0 inventory doc updated if Q5 says so.
- [ ] Slice 2b's actual mechanism is a **separate, future** design doc informed by this data — not pre-decided here.
- [ ] **This design seat:** document only — no Core implement unless ACK marks trivial.

---

## 11. References (absolute paths)

| Artifact | Path |
|---|---|
| Parent M7-a design | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m7a-path23-image-dispfb-design.md` |
| A0 inventory | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m7-a0-residual-inventory.md` |
| PATH3 mask matrix | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\graphics\PATH3_MASK_MATRIX.md` |
| M4-S4-MIRROR (structural template) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m4-s4-ee-mirror-design.md` |
| Gif.cs packet state machine | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\Gif.cs` |
| MK:DA title port (gifP3=6 signature) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\title-ports\MK_DEADLY_ALLIANCE.md` |

---

*Design only. No Core code changes in this note. Bisect-first because two of the parent doc's four candidate fix classes turned out to already be landed, and none of the four directly explain GoW/BO2's zero-Path3-activity signature — proposing a specific Gif.cs behavior fix without knowing where the pipeline actually breaks would be guessing, not infra work.*
