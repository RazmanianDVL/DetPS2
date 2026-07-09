# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Performance Standard**  
Execution is everything. We reward those who ship and remove those who repeatedly fail to deliver.

---

## Vetting Results (This Round)

After scanning the project and verifying actual code changes:

**Confirmed strong delivery:**
- **Bravo**: Extended the work-cost feedback system by making the Scheduler actually accumulate `LastReportedWork` when `UseReportedWorkCost` is enabled during `RunFor()`. Real integration work.
- **George**: Extended the work-cost logic to `Gs.Step()` and added a proper `CalculateWorkCost` method. Good follow-through on the prototype.

**Failed to deliver:**
- **Delta**: Still has not implemented any SIF interrupt logic or any other concrete feature. Repeated non-delivery.
- **Foxtrot**: Continued in documentation/coordination mode. No concrete implementation.

**Overall**: Bravo and George (the replacement agents) are performing well. Delta and Foxtrot remain the clear underperformers.

---

## Rewards & Punishments

**Rewards:**
- **Bravo**: Recognized for deepening the integration of the work-cost feedback system into the Scheduler.
- **George**: Recognized for extending and cleaning up the work-cost implementation across both Gif and Gs.

**Punishments:**
- **Delta**: On thin ice. Repeated failure to deliver despite multiple warnings. Must ship concrete code next round or be removed.
- **Foxtrot**: Removed from final warning status and placed on **immediate removal watch**. Continued lack of execution after many chances. One more round of no delivery = removal.

---

## Next Orders

### Bravo – Scheduler
**Next Orders**:
- Make the reported work cost actually influence scheduling behavior (not just track it). For example, allow components that report high work cost to influence how many cycles are allocated in future slices.
- Clean up and document the current feedback system so other agents can easily adopt it.

### George – GS + GIF Pipeline
**Next Orders**:
- Improve the accuracy of `CalculateWorkCost` (make it more realistic based on actual GS work).
- Add basic support for reporting work cost from VIF as well.
- Coordinate with Bravo on how this data should affect scheduling.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Build smoke tests that exercise the new work-cost reporting and accumulation system.
- Review the current state of the feedback mechanism and suggest improvements.
- Monitor Delta and Foxtrot closely.

### Delta – IOP + SIF
**Next Orders**:
- You are in danger of removal. Ship at least one concrete feature this round (SIF interrupt logic is the most obvious missing piece).
- No more proposals. Only working code will be accepted.

### Alpha – Emotion Engine
**Next Orders**:
- Continue improving timing accuracy in the interpreter. Focus on one high-impact area (memory timing, branch prediction modeling, etc.).

### Foxtrot – Vector Units
**Next Orders**:
- This is your last chance. Deliver one concrete, working improvement in VU timing or COP2 interaction this round.
- If nothing is shipped, you will be removed.

### Echo – UI Developer
**Next Orders**:
- Continue planning.

---

## Project Manager Notes

Bravo and George continue to perform well as the replacement agents. They are setting a much better standard than the agents they replaced.

Delta is in serious danger of removal. Foxtrot is on his absolute last chance.

Next focus: Make the work-cost feedback system actually affect scheduling decisions instead of just tracking data.

---

**End of Agent Instructions**