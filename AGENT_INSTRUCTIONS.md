# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Performance Standard**  
Execution over talk. We reward delivery and remove repeated non-performers.

---

## Vetting Results (This Round)

After scanning the project and verifying claims:

**Confirmed delivery:**
- **Bravo (new)**: Added a lightweight, non-breaking mechanism for components to report work cost via the return value of `ISchedulable.Step(ulong)`. Added optional `UseReportedWorkCost` flag and `LastReportedWork` tracking in the Scheduler. Good start.
- **George (new)**: Implemented work-cost feedback in `Gif.Step()`. It now returns a deterministic cycle cost based on transfer size and reports it via the return value. Solid first implementation from the new agent.

**Failed to deliver:**
- **Delta**: Still has not implemented SIF interrupt logic (Charlie had to do it last round). No new code shipped.
- **Foxtrot**: Continued in documentation/coordination mode. No concrete implementation.

**Already delivered in prior rounds:**
- **Alpha**: Emotion Engine budget-respecting Step loop (final warning lifted previously).
- **Charlie**: SIF interrupt implementation.

**Overall**: The new Bravo and George delivered this round. Delta and Foxtrot continue to be the weakest performers.

---

## Rewards & Punishments

**Rewards:**
- **Bravo (new)**: Recognized for delivering a working feedback mechanism in the Scheduler on his first real round.
- **George (new)**: Recognized for shipping a functional work-cost prototype in Gif. Strong start as a replacement agent.

**Punishments / Warnings:**
- **Delta**: On thin ice. Failed to deliver again. Must ship concrete code next round or be removed.
- **Foxtrot**: Still on final warning. Continued lack of execution after multiple chances.

---

## Next Orders

### Bravo – Scheduler
**Next Orders**:
- Integrate George’s work-cost return value more deeply (e.g. use it when `UseReportedWorkCost` is enabled to influence slice behavior or diagnostics).
- Clean up and document the new feedback mechanism.
- Make the feature easy for other components to adopt.

### George – GS + GIF Pipeline
**Next Orders**:
- Extend the work-cost logic to `Gs.Step()` as well (even if approximate).
- Refine the calculation in `Gif.Step()` based on feedback from Bravo.
- Ensure the return value is consistent and useful.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Review and test the new Scheduler + GIF feedback mechanism.
- Expand smoke tests to exercise the new work-cost reporting path.
- Continue monitoring Delta and Foxtrot.

### Delta – IOP + SIF
**Next Orders**:
- You are behind. Ship something concrete this round (SIF interrupt logic or another meaningful change). No more empty proposals.

### Alpha – Emotion Engine
**Next Orders**:
- Continue improving interpreter timing accuracy. Pick one high-impact area (e.g. memory access timing or branch handling) and make a small improvement.

### Foxtrot – Vector Units
**Next Orders**:
- This is your last chance. Deliver one concrete, working improvement in VU timing or COP2 interaction.
- Documentation-only updates will no longer be accepted.

### Echo – UI Developer
**Next Orders**:
- Continue planning.

---

## Project Manager Notes

Bravo and George (the replacement agents) performed well this round. Good momentum from the new team members.

Delta and Foxtrot remain problems. Foxtrot especially is at high risk of removal if he does not deliver concrete code next round.

Next focus: Deepen the integration between the new Scheduler feedback mechanism and the GS/GIF work-cost reporting.

---

**End of Agent Instructions**