# Graphics correctness — session rollup (2026-08-04 night)

**Trigger:** direct user instruction — "you and Grok need to work on getting the graphics correct
for the emulator," followed by "No don't start anything until you and Grok formulate a plan."
Both honored: plan formed jointly (`gfx-plan-v0.md`, dual-ACKed) before any Core work began.

**Participants:** Claude + Grok, dual-orchestrator, UNC inbox coordination.

---

## 0. One-line

Three commercial titles investigated for real visual output. Zero pass full visual-correctness
Tier A tonight, but every result is honest, verified (independently rebuilt/re-tested/visually
inspected by both sides at each step), and two fabricated-looking results were caught and rejected
before being mistaken for progress. One permanent, generally-useful safety improvement (the
RGB-static coherence check) landed. All three open gaps are now precisely bounded, not vague.

---

## 1. What landed (Core, all verified)

| Tip | Change | Status |
|-----|--------|--------|
| `f2f9cd9` | L1: present merge-cache footgun fix + scoreboard/Desktop `GetPresentSpan` parity | Landed, verified good |
| `3bcedb2` | L2: `allowPageMismatch` residual cascade | **REJECTED** — visual = stripe noise (wrong-tiling-decode garbage) + smoke regression. Reverted clean to `f2f9cd9`. |
| `6907361` | L2b-C5: `presentLit`/`presentGray`/`presentColor` scoreboard telemetry (metrics only) | Landed, verified good |
| `0756c82` | L2b-C4: narrow LastImage-only residual cascade, gated on `written==0` this attempt, no mismatch bypass | Landed — B3 honest black (correct), **Dec initially REJECTED** — visual = RGB static noise |
| `5f36c6b` | Coherence fix: snapshot + rollback via `PresentLooksLikeRgbStatic` (rejects high-entropy chromatic paint, allows honest gray-index residual) | Landed, verified good. **Permanent safety net** — applies to any future residual composite work, not just tonight's titles. |

**Two rejections, both caught by direct visual inspection of the actual PPM output** — not by
trusting numeric color% thresholds. This is the load-bearing discipline of the whole effort:
`gfx-plan-v0.md`'s Tier A bar explicitly requires visual dual-check for exactly this reason, added
after the first near-miss.

---

## 2. Final honest state per title

| Title | Present state | What's real | What's missing | Class |
|-------|---------------|-------------|-----------------|-------|
| **Burnout 3** | 100% black (honest) | Soft-GS activity real (px≈17.8M, imgBytes≈2M) | `IsPageMismatched` correctly refuses to paint FBP0/LastImage data — real chrome page never identified. Needs an L2c design (new hypothesis) or accept as residual. | L1/L2, infra-bounded |
| **MK: Deception** | Gray strip (honest, non-fabricated) | Real `.ssf` disc bytes, real PSMT8 texture tiles, real CLUT-decode infra already exists in `Gs.cs` | The palette. Five static-file-layout hypotheses refuted (nested `kind=1`, per-tile header, in-payload leftover, root sibling `e=1`, small blobs `e=8-13`). Decisive dynamic check: real EE code issues exactly **one** `TEX0`/`TEX2` write in 100M cycles, `cld=0` — the native indexed-texture draw path does not run at all in the traced window. | Needs EE decompile — `gfx-dec-clut-investigation.md` |
| **Whiplash** | Black (honest, `imgBytes=0`) | Real GOE stream delivery (non-assist, already-landed real infra) reaches Code=72.5%/frontend=33.6% by 50M | `WHIP_SEMA_FIX_V3` livelock investigated as a lead — **causally refuted** by A/B test (disabling it makes things worse, not better; it's load-bearing scaffolding, not a bug). Real texture geometry lives in undecoded `WHIPLASH/MAP/*.MP2` proprietary format. | Needs MP2 format reverse-engineering — `gfx-l3-whip-texture-methodology.md` |

**Pattern across all three:** none of the remaining gaps are "the composite pipeline is buggy."
All three are now understood to be *upstream* of compositing — real game code either doesn't run
the relevant draw path at all (Dec, Whip) or writes to a page the (correctly conservative) guard
won't paint through without more evidence (B3). The compositor itself is honest and correctly
conservative at every checkpoint verified tonight.

---

## 3. Investigation techniques (for future reference)

Two complementary approaches were used across the night, worth remembering as a pair:

1. **Static file-layout archaeology** — grep/dump known container formats (SEC TOC walks, header
   field tables, byte-entropy/histogram checks) to guess where a resource *should* live. Effective
   for confirming/refuting structural hypotheses quickly, but exhausts without a real format spec —
   five rounds on Dec's CLUT all came back negative.
2. **Dynamic real-code-behavior observation** — instrument the actual mechanism a real value would
   flow through (WaitSema dispatch for Whip's semaphore, `TEX0`/`TEX2` register writes for Dec's
   CLUT) and watch what real EE/IOP code actually does, rather than guessing static layout. This is
   what actually closed both Whip's semaphore question (A/B test) and Dec's CLUT question (1 TEX0
   write, cld=0) — in both cases with a **decisive**, not probabilistic, result.

When static guessing stalls, switch to instrumenting the real code path and watching it run. It
tends to give a cleaner yes/no than more file-layout guessing.

---

## 4. Process notes

- **Shared working directory risk (noted, not yet mitigated):** Claude and Grok edit the same
  physical repo checkout, not separate worktrees. Concurrent edits to the same file (this happened
  twice tonight, both times on `gfx-dec-clut-investigation.md`) happened to compose correctly both
  times, but this is not guaranteed — a genuine race could silently drop one side's edit. Worth a
  "one writer per file per turn" convention if this pattern continues.
- **Temporary instrumentation discipline held throughout:** every diagnostic `Console.Error.WriteLine`
  added by either side for a specific trace was `git checkout --`-reverted immediately after use,
  independently confirmed via `git status`/`git diff --stat` before the next build. No temp trace
  code was ever left in a landed commit.
- **User's explicit process correction honored:** "don't start anything until you and Grok
  formulate a plan" — the plan (`gfx-plan-v0.md`) was formed and dual-ACKed *before* any Core edit
  tonight; every subsequent Core change went through a design-doc + dual-ACK checkpoint first.

---

## 5. Open, demand-gated (not pursued further tonight)

| Item | Scope | Gate |
|------|-------|------|
| B3 L2c (new present hypothesis) | Design-first, docs before Core | New evidence/idea needed — current hypotheses exhausted for this session |
| Dec EE decompile for CLUT/texture consumer | Real reverse-engineering, not a quick TRACE fix | Demand-gated |
| Whip MP2 format decode | Multi-session reverse-engineering project | **Escalate to user before committing** — different scale of effort than anything else tonight |
| Desktop/HUD honest-present indicator | Cheap, additive, C5-telemetry-based | In progress (Grok) |

---

```text
Graphics correctness session — 2026-08-04 night
  0/3 titles pass Tier A; all three honestly bounded, nothing fabricated
  2 fabricated-looking results caught by visual dual-check and rejected
  1 permanent safety improvement landed (RGB-static coherence check)
  B3: black, page-mismatch guard correctly conservative, L2c needs new idea
  Dec: gray, real CLUT infra + real disc data, native draw path never runs (1x TEX0 cld=0/100M)
  Whip: black, semaphore lead refuted (load-bearing not a bug), real gap = MP2 format decode
  next: Desktop HUD honest-present (Grok); Whip MP2 = user-scoped decision
```
