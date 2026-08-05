# M7-c Slice 3 design — DISPFB/composite preference (oracle-first, not a guessed fix)

**Status:** design only (ready for dual ACK) — **no Core implement this turn**
**Date:** 2026-08-04
**Mode:** infra-only. **No `Gs.cs` edits. No `Gif.cs` edits. No GameQuirks edits. No push.**
**Tracks:** M7-a Slice 3 (`docs/infra-audits/m7a-path23-image-dispfb-design.md` §4.2 "Slice 3 — Natural DISPFB circuit + composite preference"), forced by `docs/infra-audits/m7c-2b-midway-image-stall-rootcause.md`'s finding that MK: Deadly Alliance's real Path3 IMAGE delivery is byte-correct yet `compositeSource=LastImageTrx`.
**Related:**
`docs/infra-audits/m7a-path23-image-dispfb-design.md` (parent design, §4.2 Slice 3 candidates, §12 Q4 — the exact open question this doc engages)
`docs/infra-audits/m7-a0-residual-inventory.md` (A0 classification — flags a numeric discrepancy for MK:DA, see §1.3)
`docs/infra-audits/m7c-2b-midway-image-stall-rootcause.md` (the forcing evidence: IMAGE delivery proven correct, composite still residual)
`src/DetPS2.Core/Gs.cs` (`CompositeDispfbToFramebuffer`, read in full for this design)

**Owned code (future, not this seat):** none proposed — see §2.

---

## 0. One-line summary

I read `Gs.cs`'s `CompositeDispfbToFramebuffer` in full expecting to find a composite-selection bug (the parent doc's Slice 3 framing: "composite must prefer Natural over Frame/FBP0/LastImageTrx when natural page has real RGB"). **I did not find one.** The code already implements exactly that preference order, correctly detects that MK:DA's natural DISPFB page has zero real RGB under it, and only *then* falls back through FRAME → FBP0 → `LastImageTrx` — the same fallback chain the code comments say was built for GoW's PSMT4-vs-empty-DISPFB case, and the same code path has an inline comment citing **MK Deception by name** as a prior tuning case for exactly this composite/PSM-mismatch class. This is not an undiscovered bug waiting for a fix; it's a documented, deliberately-built residual policy, already exercising correctly on MK:DA.

**The real open question is not "why does the composite selector pick the wrong source" — it's "is landing on `LastImageTrx` here honest-and-correct or honest-but-avoidable."** That's a judgment call this codebase's own doctrine (`CORRECTNESS.md`, cited throughout M7-a) says needs real evidence (a reference emulator or hardware oracle), not more code-reading — the parent M7-a design already posed this exact question as its own **unresolved Q4** ("When is sampling the texture page as display acceptable residual vs always wrong?") months before this seat existed, and this investigation doesn't resolve it either. **Recommended shape: an oracle-check investigation seat, not a Core PR.**

---

## 1. What I found

### 1.1 The composite algorithm, as it actually runs for MK:DA

`CompositeDispfbToFramebuffer()` (`Gs.cs:2270-2484`), traced against MK:DA's known state (`compositeSource=LastImageTrx`, `naturalDispfbPx=0`, `residualDispfbPx=46080`, real `imgBytes=98304` delivered per m7c-2b):

1. `circuit.HasNaturalDispfb` is checked first (`Gs.cs:2315`) — if true, `fromDispfb=true`, `source=NaturalDispfb` **is set as the starting assumption**. This is the "prefer natural" step the parent doc asked for; it already exists.
2. `CompositeLocalToFb(fb, fromDispfb: true, ...)` (`Gs.cs:2366`) then actually scans the local-memory page DISPFB points at, pixel by pixel, and returns the count of non-black pixels found there (`Gs.cs:2539-2546`: `if ((pixel & 0x00FFFFFF) == 0) continue;`).
3. For MK:DA, this scan returns **0** (`naturalDispfbPx=0` in the evidence) — DISPFB *is* programmed (natural path was attempted), but the page it points at has no real RGB in local memory.
4. Because `written == 0`, `ImageBytesWritten > 0` (98304, the real delivered IMAGE), and the present buffer is mostly black, the GX-041 residual branch fires (`Gs.cs:2386-2426`) — tries FRAME, then FBP0, then finally `CompositeLastImageTransfer` (`Gs.cs:2416-2425`), which samples the actual last real BITBLT destination (the page the 98304 real IMAGE bytes actually landed at) with its correct PSM. This is where `LastImageTrx` and `residualDispfbPx=46080` come from.

**This is the exact fallback chain `Gs.cs:2381-2385`'s own comment describes**: *"natural DISPFB is programmed but local RGB under that FBP is empty (IMAGE BITBLT lives at FBP0 / FRAME / high-page PSMT4)... try FRAME, FBP0, then largest Host→Local BITBLT dest."* It was written for GoW's PSMT4-at-high-DBP case; MK:DA is hitting the identical code path, not a different, unhandled one.

### 1.2 This exact class was already tuned once, citing MK Deception specifically

`Gs.cs:2547-2553` (inside `CompositeLocalToFb`'s pixel-read helper) has a dated comment: *"Don't paint a pixel whose backing page was demonstrably last written in a different format or stride than this composite is decoding it as... verified 2026-08-02 against two distinct cases: MK Deception, DISPFB2 declared PSM=0x0A/832 over a page whose only real writes were PSM=0x01/640 boot chrome; Whiplash, same PSM but a BITBLT write at stride 256 read back at stride 1024."* This confirms the Midway-family DISPFB/PSM-mismatch pattern is a known, previously-investigated class — not new territory this design doc is the first to touch.

### 1.3 A0's numbers for MK:DA don't match this investigation's — flagging, not resolving

`docs/infra-audits/m7-a0-residual-inventory.md` (line 21) records MK:DA as `natural=224016` (nonzero), `lit=75656 (non-black)`, sourced from `docs/title-ports/MK_DEADLY_ALLIANCE.md`. My reference evidence (via `m7c-2b`'s GIF_BISECT-4 run, current tip) shows `naturalDispfbPx=0`, `compositeSource=LastImageTrx`. **These are not the same run** — A0 pulled from a historical title-port doc; GIF_BISECT-4/m7c-2b generated a fresh trace at the current tip with M1-b/M7-c Slice 2a telemetry landed in between. I have not reconciled which reflects current behavior, whether MK:DA's natural-DISPFB pixel count is genuinely budget/timing-sensitive (a different claim-tier sample could land at a different point in a boot sequence with different DISPFB state), or whether something in the intervening landings changed this title's composite behavior. **This needs a fresh, single canonical trace before any design proceeds** — same "verify before trusting the number" discipline the M4-h/M4-S4 threads already established this session.

---

## 2. Why I'm not proposing a mechanism

Per this session's own established discipline (M7-c's Slice 2a found two of four candidate fixes already landed; M4-S0-GOW's design correctly refused to guess a fix without disassembly evidence; my own m7c-2b found the "Midway stall" was a telemetry artifact, not a real bug): **proposing a `Gs.cs` composite-selection change right now would be fixing something that isn't demonstrably broken.** The code already implements the parent doc's own stated preference order (natural → residual fallback chain), already handles this specific PSM/page-mismatch class (tuned against MK Deception before), and already respects every hard ban (never plants DISPFB, only samples real transferred bytes). Writing a "proposed mechanism" section here would mean inventing a plausible-sounding change to code that is arguably already correct — the same trap M4-g's own pre-check avoided by testing before scoping.

**What would actually move this forward is not a code change, but evidence**: does real PS2 hardware (or a trusted reference emulator) show MK:DA's natural DISPFB page briefly empty at this exact point in its boot/menu sequence too (in which case DetPS2's residual fallback is the CORRECT behavior, matching retail, and this is not a bug at all — just an accurately-reproduced transient), or does real hardware show the natural page already populated by this point (in which case something upstream — BITBLT destination-page calculation, or timing of when the IMAGE transfer's destination gets promoted to the DISPFB-visible page — is genuinely wrong, and Slice 3 needs a different, more specific fix than "prefer natural more")?

---

## 3. Proposed next seat (investigation/oracle, not implementation)

### 3.1 Intent

1. **Reconcile the A0-vs-m7c-2b discrepancy first** (§1.3) — re-run MK:DA at claim tier with `DETPS2_TRACE_GIF=1` and whatever DISPFB/PCRTC trace exists, get one current, trusted number for `naturalDispfbPx`/`compositeSource` before doing anything else.
2. **If MK:DA is confirmed at `naturalDispfbPx=0` / `LastImageTrx`**: determine whether the DISPFB page and the real IMAGE BITBLT destination page are *supposed* to be the same page at this point in MK:DA's real boot sequence (oracle question — Play!/PCSX2 comparison if available, or documented reasoning from the retail SDK/library behavior pattern already partially understood from BO2/Whiplash's S0 investigations this session), or whether they're expected to diverge briefly (in which case the current residual fallback is honest and correct, matching the B3 DISPFB=0 precedent that's explicitly documented as "honest residual, not always wrong" in the parent doc).
3. **Only if the oracle says DetPS2 diverges from retail** (natural DISPFB should already be populated and isn't) does this become a real Slice 3 implementation ticket — and even then, the fix target would likely be *upstream* of `Gs.cs`'s composite selector (e.g., BITBLT destination-page resolution, or a page-promotion/copy step DetPS2 is missing), not the composite-preference logic itself, which this investigation found is already doing the documented-correct thing with the data it's given.

### 3.2 What this is NOT

- Not a request to plant DISPFB, force natural composite, or invent pixels — explicitly banned by the parent doc and this doc's own analysis found no evidence the composite logic needs that kind of change anyway.
- Not a claim that MK:DA (or MK:Dec) is definitely fine as-is — the residual could be genuinely wrong; this doc just didn't find evidence either way, because that evidence has to come from outside this codebase (a reference oracle), not from re-reading `Gs.cs` again.

---

## 4. Flag-gated / kill-switch strategy (if a future Slice 3 fix does land)

Not applicable to this seat (no mechanism proposed), but recorded for whatever `Slice 3b` design eventually follows the oracle check, to keep parity with every other landing this session:

| Control | Default | Purpose |
|---|---|---|
| Any future DISPFB/composite behavior change | **OFF** first | Matches A1-A3, M1-a/b/c, M4-S4-MIRROR, M7-c Slice 2a — no exception |
| Kill-switch | Required | Same convention, e.g. `DETPS2_DISABLE_M7_SLICE3=1` |
| Product-default flip | **Claim-tier (100M) byte-identical/non-worse required**, not diagnose-only | Per M4-S4-MIRROR's Q9 amendment, standing bar for the whole M4/M7 program |

---

## 5. Non-goals

| Non-goal | Why |
|---|---|
| Any `Gs.cs` code change in this seat | Investigation found the existing logic already matches the documented policy; no demonstrated bug to fix |
| Plant/force DISPFB | Hard ban, parent doc §4.3, GX-040/041 law |
| Resolve MK:Dec's identical-looking case | Not independently traced in this pass or in m7c-2b; needs its own confirmation, don't assume identical without checking (same caution m7c-2b already flagged for MK:Dec's IMAGE-completion state) |
| Answer the parent doc's Q4 (LastImageTrx acceptable-residual question) definitively | That's exactly what the oracle-check seat in §3 is for; this doc surfaces the question with a concrete forcing case, doesn't answer it |
| Touch `Gif.cs` | Different file/milestone thread (Grok's Slice 2a work); this doc is Slice 3/`Gs.cs` only |

---

## 6. Validation plan (for the proposed investigation seat, §3)

| Check | Expect |
|---|---|
| Fresh MK:DA claim-tier trace, `DETPS2_TRACE_GIF=1` | Reconciles the A0-vs-m7c-2b `naturalDispfbPx` discrepancy with one trusted current number |
| Oracle comparison (Play!/PCSX2 or documented retail behavior) | Answers whether DISPFB-page-empty-at-this-point is retail-accurate or a DetPS2 gap |
| If a fix is later designed: diagnose tier | Proof of concept only |
| If a fix is later designed: claim tier (100M) | **Required before any product-default change**, byte-identical/non-worse, same bar as every prior landing |

---

## 7. Open questions for dual-ACK

| ID | Question | Options | Design bias |
|---|---|---|---|
| **Q1** | Is the oracle-check investigation (§3) an acceptable next seat, or does dual-ACK prefer a different Slice 3 target (e.g., B3's clean R3/DISPFB1=0 case, which the parent doc already calls "honest residual" and may not need any further work at all) instead of re-opening the harder GoW/MK-class `LastImageTrx` question? | (a) MK:DA oracle-check next (b) treat B3 as already-closed and pick a different milestone entirely (c) park Slice 3 until an oracle tool/reference exists | **(a)**, but flagging that without real oracle access this seat may stall on "no reference to compare against" — worth confirming such a reference is actually available before claiming it |
| **Q2** | Does either of us have (or can build) an actual Play!/PCSX2-based oracle-comparison capability, or is "oracle check" aspirational without concrete tooling? | check tooling availability first / treat as blocked without it | Genuinely don't know — **first sub-step of §3 should be confirming this is answerable at all**, not assuming it is |
| **Q3** | If no oracle is available: should MK:DA's `LastImageTrx` residual just be **documented as accepted, honest residual** (matching B3's precedent) rather than pursued further, closing this thread without a fix? | accept as documented residual / keep open pending future oracle access | Leaning toward **accept + document** if Q2 comes back "no oracle available" — matches this session's precedent of accepting real, evidenced residuals (GoW's S4 plant, B3's DISPFB=0) rather than chasing unfalsifiable questions |
| **Q4** | Should the A0 inventory doc be corrected/annotated with this doc's discrepancy note (§1.3) now, or left until the fresh trace in §3.1 produces a reconciled number? | correct now (flag discrepancy) / wait for fresh data | **Flag now, reconcile after** — avoids anyone building on A0's possibly-stale MK:DA row in the meantime |

---

## 8. Definition of done (this design seat)

- [ ] Dual ACK on Q1-Q4 (or recorded deferrals).
- [ ] If Q2 confirms oracle tooling exists: investigation seat scoped and claimed.
- [ ] If Q2 confirms no oracle: MK:DA's residual documented as accepted per Q3, thread closed without a code change.
- [ ] A0 inventory's MK:DA row gets a discrepancy note per Q4.
- [ ] **This design seat:** document only — no Core implement, no mechanism proposed (none was warranted).

---

## 9. References (absolute paths)

| Artifact | Path |
|---|---|
| Parent M7-a design (Slice 3 spec, Q4) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m7a-path23-image-dispfb-design.md` |
| A0 residual inventory (MK:DA discrepancy source) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m7-a0-residual-inventory.md` |
| Forcing evidence (IMAGE delivery proven correct) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m7c-2b-midway-image-stall-rootcause.md` |
| Composite selector (read in full) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\Gs.cs` (`CompositeDispfbToFramebuffer`, `CompositeLocalToFb`, ~lines 2270-2560) |
| MK:DA title-port historical data | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\title-ports\MK_DEADLY_ALLIANCE.md` |
| Correctness doctrine | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\CORRECTNESS.md` |

---

*Design only. No Core code changes in this note. Investigation, not a guessed fix — the composite-selection code already implements the documented residual policy correctly for the evidence available; the open question is whether that policy's outcome matches real hardware, which needs an oracle, not another read of `Gs.cs`.*
