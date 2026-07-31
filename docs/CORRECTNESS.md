# DetPS2 correctness doctrine

**We are here for correct, not for “working.”**

If a change makes a title *look* better or *get further* by lying about what the PS2 did, it is the wrong change — even when metrics or the Desktop window look improved.

---

## Platform strategy (2026-07-30)

**Default path to playability:** execute **real BIOS and disc IRX** on a deterministic IOP
(see [`IRX_EXECUTION_PHASE_PLAN.md`](IRX_EXECUTION_PHASE_PLAN.md)).  
C# owns EE, Soft-GS, devices, and scheduler — **not** a second invented IOP OS.  
HLE service clones and title thrash plants are debt, not the product.

## North star

DetPS2 is a **pure C#** PS2 emulator. Success is:

1. **Honest Soft-GS / subsystem state** (pixels, GIF paths, CDVD, RPC, PC, threads) that match real hardware/oracle behavior as far as we have implemented it.
2. **Shared HLE** that ports real ABI and side-effects (Play! / PCSX2 ground truth), not title-local theatre.
3. **Determinism** — same input → same MasterCycles and Soft-GS outcomes.

Success is **not**:

- A painted logo, fake frame, or host-decoded movie that never went through EE/IOP/IPU/GS.
- A MENU YES / “near menu” claim without Soft-GS evidence.
- Forcing semaphores, Exit stubs, or magic memory so the process “survives” without the real wait being satisfied.
- Third-party host tools standing in for hardware the console implements itself.

**A black screen with correct residual metrics is better than a pretty wrong screen.**

---

## Forbidden shortcut classes

These are **out of policy** unless an explicit design issue approves them as temporary, documented experiments (default: reject):

| Shortcut | Why forbidden |
|----------|----------------|
| **Host media decode for game video** (e.g. FFmpeg Sofdec → host overlay) | Logos/FMV must come from disc → IPU/CRI/path → Soft-GS. Missing video is an IPU/HLE gap. |
| **Synthetic branded UI** (host-drawn “MIDWAY”, fake chrome) | Not Soft-GS truth; confuses bring-up and players. |
| **Force-complete wait / status plants that cause Exit or skip real RPC** | Hides the real wall; DA/Dec lessons apply. |
| **Invent FILEIO/Open success without path/size ABI** | Inflates cdvd / “progress” without game-visible data. |
| **Claim MENU / first GS without claim-budget Soft-GS evidence** | Scoreboard heuristics are not claims. |
| **Copy Play!/PCSX2 wholesale or guess RPC layouts** | Port ABI + side-effects after oracle lookup; do not invent. |
| **Host clocks / nondeterminism on the core path** | See `FLOAT_POLICY.md`. |

Title-local `GameQuirks` are allowed only to **unstick a documented wall after shared HLE is insufficient**, and must not replace core subsystems with host cheats.

---

## Required orientation

1. **Soft-GS metrics are ground truth** for DetPS2 presentation (`px`, gifPath3, dmac, framebuffer). Host GPU present is optional display only.
2. **Oracles before invention** — DetPS2 traces → Play! (`play-lookup`) → PCSX2+PINE when ambiguous. See `AGENT_SOP.md`, `PLAY_HLE_ORACLE.md`.
3. **Prefer shared HLE** — one correct FILEIO/SIF/IPU fix beats ten title plants.
4. **Leave issues open** until fixed with evidence. Close only when the real gate is met.
5. **Document residuals honestly** — wiki and issues state what still does not work.

---

## When something is hard

If we cannot render a logo, open a pack member, or reach menu **correctly**:

- Say so.
- Keep the residual in the issue tracker.
- Deepen Soft-GS / HLE / kernel behavior.
- Do **not** ship a workaround that only makes the demo look alive.

Correct and incomplete beats incorrect and flashy.

---

## Agents and PRs

- Agents: treat this file as binding with `AGENT_SOP.md` §0.
- PRs: reviewers should reject host-cheat presentation and false claims.
- Historical shortcuts (e.g. host FFmpeg boot FMV) are **retired** — do not reintroduce.

See also: `CONTRIBUTING.md`, `AGENT_SOP.md`, `COMPLETENESS.md`, `FLOAT_POLICY.md`.
