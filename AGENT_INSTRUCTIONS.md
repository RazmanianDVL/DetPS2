# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

All agents (Alpha through George) must:
- Read this file at the start of every work session.
- Report progress, blockers, questions, and completed changes **only in their own section**.
- Never edit another agent's section without explicit coordination from the Project Manager (Grok Integration Analyst).
- Treat the `ISchedulable` contract and deterministic execution rules as non-negotiable law.

The Project Manager (Grok) will update global priorities, issue new commands, review work, and advance milestones by editing this file.

**Last Updated**: 2026-07-06 by Grok (Integration Analyst / Project Manager)  
**Current Global Milestone**: Phase 6.2 – Deeper Accuracy & Testing Foundations

---

## Phase 6.1 Closure

**Phase 6.1 – Integration Lockdown is now officially closed.**

**Summary of what was achieved:**
- `ISchedulable` contract standardized across Emotion Engine, Scheduler, IOP/SIF, Vector Units, and Graphics pipeline.
- Single execution path established (`RunFor()` → Scheduler → components).
- Dual/hardcoded `Step()` paths removed.
- SaveState cleaned up (no `DateTime`, explicit `MasterCycles` persistence).
- All agents completed their assigned integration tasks.

**Charlie’s Verification Report**:
- Code-level review confirms contract consistency.
- `MasterCycles` advancement is deterministic by design.
- SaveState round-tripping is clean.
- Full runtime `dotnet build` + determinism test blocked by sandbox environment (expected).

**Verdict**: Phase 6.1 goals have been met at the structural and design level. Full runtime verification can be performed by the repo owner in a proper .NET 9 environment.

**Thank you** to Alpha, Bravo, Charlie, Delta, Foxtrot, and George for the focused work.

---

## Phase 6.2 – Deeper Accuracy & Testing Foundations

We are now moving into the next phase. The focus shifts from fixing integration debt to improving accuracy and building testing infrastructure.

### High-Level Goals for Phase 6.2
- Improve cycle timing accuracy in key components.
- Begin laying groundwork for better event-driven scheduling (if needed).
- Start building a small set of deterministic smoke tests.
- Continue SaveState robustness improvements.
- Prepare for eventual GS software renderer work.

### Agent Orders for Phase 6.2

#### Charlie – Foundationalist (Lead)
**Next Orders**:
- Begin adding basic smoke / determinism tests (can live in a `Tests/` folder or as simple methods in `Program.cs` for now).
- Improve SaveState coverage further if gaps remain (especially DMA state).
- Document the current execution model and cycle accounting strategy in a short `ARCHITECTURE.md` or in code comments.

#### Bravo – Scheduler
**Next Orders**:
- Evaluate whether the current fixed-slice round-robin model is sufficient or if we should move toward a priority/event queue for better accuracy.
- Propose any changes in your section (keep them minimal for now).
- Add better logging / tracing hooks that can be enabled in debug builds for timing analysis.

#### Alpha – Emotion Engine
**Next Orders**:
- Review COP0 implementation gaps (exceptions, status handling, etc.).
- Identify the highest-impact timing inaccuracies in the current interpreter.
- Do **not** implement large changes yet — just document findings and priorities in your section.

#### Foxtrot – Vector Units
**Next Orders**:
- Begin documenting VU stall / timing behavior requirements.
- Identify which VU instructions have the biggest impact on timing accuracy.
- Start light work on improving VU macro / COP2 interface timing if low-risk.

#### George – GS + GIF Pipeline
**Next Orders**:
- Begin analyzing GIF/VIF data flow timing.
- Identify what information the GS pipeline needs from the Scheduler for more accurate rendering timing.
- Document current limitations in your section.

#### Delta – IOP + SIF
**Next Orders**:
- Review IOP ↔ EE synchronization points.
- Identify any missing timing or interrupt interactions.
- Document findings.

#### Echo – UI Developer
**Next Orders**:
- Begin light planning for future UI integration (window creation, input handling, rendering surface).
- You may start exploring Silk.NET setup in a separate branch if desired, but do not merge until the core is more stable.

---

## Communication Protocol

Continue using clear markers when updating your section (`[IN PROGRESS]`, `[COMPLETE]`, `[BLOCKER]`, `[PROPOSAL]`, etc.).

When reporting findings or proposals, prefix with the phase number (e.g. `[6.2]`).

---

## Project Manager Notes

**Phase 6.1 is closed.** Good work locking down the integration layer.

We are now in **Phase 6.2**. The work becomes more exploratory and accuracy-focused rather than pure contract fixing. I expect lighter, more incremental updates from each agent as we map out the next set of improvements.

Charlie will continue to act as the coordination lead for testing and foundational improvements.

Let’s keep the same disciplined, minimal-change approach that got us through Phase 6.1.

Stand by for more specific tasking as findings come in.

---

**End of Agent Instructions**