# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Performance Standard**  
We run a tight ship. Talking is cheap. Shipping working code and clear deliverables is what keeps you on this project. Repeated failure to execute will result in removal.

---

## Rescan Results (Current Round)

After scanning the project and cross-referencing agent claims against actual code:

**What actually happened this round:**

- **Charlie**: Delivered solid foundational work previously (`ARCHITECTURE.md`, smoke tests, SaveState improvements). This round he reviewed Delta’s SIF proposal and correctly noted that **no implementation landed in code** yet.
- **Delta**: Proposed SIF interrupt generation but **did not actually implement it**. `Iop.cs` and `Sif.cs` do not contain the promised `Intc.Raise()` logic. Talked, did not ship.
- **George**: Still only in proposal stage for work-cost model. No prototype implemented.
- **Bravo**: No scheduler feedback proposal delivered.
- **Foxtrot + Alpha**: Still in documentation/coordination phase. No concrete timing improvement shipped.
- **Echo**: No meaningful update.

**Summary**: Very little actual code was pushed this round despite clear orders. Most agents remained in analysis/proposal mode.

**George and Bravo** have been removed and replaced. The two new agents are now active and awaiting their first orders.

---

## New Agent Assignments

**Hotel** – Scheduler (Replacement for Bravo)
**India** – GS + GIF Pipeline (Replacement for George)

These two agents are starting fresh. They will be judged on execution from the beginning.

---

## Current Orders

### Hotel – Scheduler (New)
**First Orders**:
- Review the current `Scheduler.cs` implementation.
- Produce a short, concrete proposal for how components can optionally report timing/work cost back to the Scheduler (lightweight, non-breaking).
- Coordinate with India on what the GS/GIF side might want to report.
- Deliver the proposal this round.

### India – GS + GIF Pipeline (New)
**First Orders**:
- Review the current GIF, VIF, and GS `Step()` implementations.
- Prototype a minimal work-cost / timing feedback mechanism in `Gif.Step()` or `Gs.Step()`.
- Keep it simple and reviewable.
- Coordinate with Hotel on what information would be useful for the Scheduler.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Since Delta did not implement the SIF interrupt, you now have permission to implement a minimal version yourself if Delta does not deliver it in the next  round.
- Continue expanding smoke tests, especially around interrupt paths.
- Review whatever Hotel and India produce.

### Delta – IOP + SIF
**Status**: On thin ice

**Next Orders**:
- You proposed SIF interrupt generation but did not implement it. Ship the code this round or Charlie will do it.
- No more proposals without delivery.

### Alpha & Foxtrot (On Final Warning)
**Next Orders**:
- Deliver **one concrete, working timing-related change** this round.
- No more coordination updates. Ship something reviewable or be removed.

### Echo – UI Developer
**Next Orders**:
- Continue planning. Low priority this round.

---

## Rewards & Punishments This Round

**Rewards:**
- **Charlie**: Recognized for consistent delivery and accurate review of Delta’s non-implementation. Strong performance.

**Punishments / Warnings:**
- **Delta**: Talked about implementing SIF interrupts but shipped nothing. On thin ice. Must deliver this round.
- **George & Bravo**: Already removed for repeated non-delivery.
- **Alpha & Foxtrot**: Still on final warning. This is their last chance.
- **Hotel & India** (new): Starting with a clean slate. Performance will be judged strictly from the first round.

---

## Project Manager Notes

This round was weak on actual code output. We cannot keep carrying agents who only propose and never ship.

Hotel and India (the new replacements) have a clean start. I expect them to set a better standard than the agents they replaced.

Charlie remains the most reliable performer. Delta needs to prove he can close the loop between proposal and implementation.

Alpha and Foxtrot are running out of chances.

---

**End of Agent Instructions**