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

## Round 5 Review

**Good progress this round:**

- **Delta**: Successfully implemented basic SIF interrupt generation (`Iop.cs` now triggers `Intc.Raise` on SIF mailbox write). This is a meaningful, low-risk improvement that adds the first real IOP → EE interrupt path. Excellent work.
- **Charlie**: Expanded the smoke test suite with three new deterministic scenarios (`Determinism_MasterCycles`, `SaveState_MasterCyclesRoundTrip`, `Reset_MasterCycles`). Solid foundational testing work.

**Still in progress:**
- George, Foxtrot + Alpha, and Bravo have not yet delivered concrete implementations or proposals from the previous round.

---

## Next Orders (Round 6)

### Delta – IOP + SIF
**Status**: `[COMPLETE]` (SIF interrupt generation)

**Next Orders**:
- Stand by. Your SIF interrupt change is a good foundation. Be ready to extend it (e.g. more interrupt sources or status flag handling) once we validate the current implementation.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Review Delta’s SIF interrupt implementation and provide feedback (correctness, edge cases, test coverage).
- Review George’s GIF/GS work-cost proposal and recommend whether we should pursue it now or later.
- Continue expanding smoke tests if new synchronization behavior (like SIF interrupts) should be covered.

### George – GS + GIF Pipeline
**Next Orders**:
- Move from proposal to a small prototype.
- Implement a minimal work-cost return in `Gif.Step()` or `Gs.Step()` (even if approximate).
- Coordinate with Bravo on how this information could eventually influence scheduling.
- Keep the change small and isolated.

### Foxtrot + Alpha (VU / EE Timing)
**Next Orders**:
- Move from coordination to a concrete, low-risk change.
- Pick one small area (e.g. better COP2 timing feedback or simple EFU latency modeling) and implement a minimal version.
- Coordinate so changes in one don’t break the other.

### Bravo – Scheduler
**Next Orders**:
- Produce a short proposal for how the Scheduler could accept timing feedback from components (e.g. GIF/GS work cost or VU stalls).
- Keep it lightweight — we are not rewriting the scheduler yet. A simple extension point or optional callback is enough for now.
- Review Delta’s SIF interrupt work for any scheduler implications.

### Echo – UI Developer
**Next Orders**:
- Continue UI planning. You may begin light prototyping on a separate branch if ready.

---

## Project Manager Notes

Delta delivered a real, useful improvement this round. Charlie is building good test coverage.

The next focus is turning the remaining proposals into small implementations:
- George: GIF/GS work cost prototype
- Foxtrot + Alpha: One small VU/EE timing improvement
- Bravo: Lightweight scheduler feedback proposal

Charlie will act as the reviewer/coordinator for the above items.

Keep changes minimal and reviewable. We are still building confidence in the accuracy layer.

---

**End of Agent Instructions**