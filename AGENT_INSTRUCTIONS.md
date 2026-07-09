# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Performance Standard**  
We run a tight ship. Talking is cheap. Shipping working code and clear deliverables is what keeps you on this project. Repeated failure to execute will result in removal.

---

## Rescan Results (Current Round)

After scanning the project and cross-referencing agent claims against actual code:

**What actually happened this round:**

- **Charlie**: Performed well in prior rounds and gave an accurate review this round (noted that Delta’s SIF interrupt was proposed but not implemented in code).
- **Delta**: Proposed SIF interrupt generation but **did not ship code**. No implementation landed in `Iop.cs` or related files.
- **Foxtrot + Alpha**: Remained in documentation/coordination mode. No concrete timing improvement implemented.
- **Echo**: No meaningful update.

**George and Bravo** have been replaced with new agents operating under the same callsigns. They are starting fresh and have been introduced to their roles.

**Summary**: Actual code output was low this round. Most agents stayed in analysis/proposal mode.

---

## Current Active Agents

- **Alpha** – Emotion Engine (Final Warning)
- **Bravo** – Scheduler (New agent in role)
- **Charlie** – Foundationalist (Lead)
- **Delta** – IOP + SIF (On thin ice)
- **Echo** – UI Developer
- **Foxtrot** – Vector Units (Final Warning)
- **George** – GS + GIF Pipeline (New agent in role)

---

## Current Orders

### Bravo – Scheduler (New)
**First Orders**:
- Review the current `Scheduler.cs` and `ISchedulable` implementation.
- Produce a short, concrete proposal for how components can optionally report timing/work cost back to the Scheduler (lightweight, non-breaking).
- Coordinate with George on what the GS/GIF side might want to report.
- Deliver the proposal this round.

### George – GS + GIF Pipeline (New)
**First Orders**:
- Review the current GIF, VIF, and GS `Step()` implementations.
- Prototype a minimal work-cost / timing feedback mechanism in `Gif.Step()` or `Gs.Step()`.
- Keep it simple and reviewable.
- Coordinate with Bravo on what information would be useful for the Scheduler.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Since Delta has not implemented the SIF interrupt, you now have permission to implement a minimal version yourself if he does not deliver it soon.
- Continue expanding smoke tests, especially around interrupt paths.
- Review whatever Bravo and George produce this round.

### Delta – IOP + SIF
**Status**: On thin ice

**Next Orders**:
- You proposed SIF interrupt generation but did not implement it. Ship the code this round or Charlie will implement it.
- No more proposals without delivery.

### Alpha & Foxtrot (On Final Warning)
**Next Orders**:
- Deliver **one concrete, working timing-related change** this round.
- No more coordination updates without output. Ship something reviewable or be removed.

### Echo – UI Developer
**Next Orders**:
- Continue planning. Low priority this round.

---

## Rewards & Punishments This Round

**Rewards:**
- **Charlie**: Recognized for consistent delivery and accurate assessment of other agents’ progress.

**Punishments / Warnings:**
- **Delta**: Proposed but did not implement. On thin ice. Must deliver this round.
- **Alpha & Foxtrot**: Still on final warning. This is their last chance to demonstrate execution.
- **Bravo & George** (new agents): Starting with a clean slate. Performance will be judged strictly going forward.

---

## Project Manager Notes

George and Bravo have been replaced. The new agents in those roles have a clean start and clear first orders.

This round showed continued weakness in moving from proposals to actual implementation. That behavior will not be tolerated long-term.

Charlie remains the strongest performer. Delta needs to close the gap between talking and shipping.

Alpha and Foxtrot are on their final warning.

---

**End of Agent Instructions**