# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Performance Standard**  
We run a tight ship. Execution beats proposals. Repeated failure to ship will result in removal.

---

## Vetting Results (This Round)

After scanning the project and verifying claims against actual code:

**Confirmed real delivery:**
- **Alpha**: Successfully modified `EmotionEngine.Step(ulong maxCycles)` to loop and respect the cycle budget instead of executing only one instruction per call. This is a meaningful, measurable improvement. He has broken his final warning with actual work.
- **Charlie**: Implemented minimal SIF interrupt generation in `Sif.cs` (`SendCommand` now calls `_intc?.Raise(Intc.InterruptSource.Sif)`). Good initiative when Delta failed to deliver.

**Claims that need verification / follow-up:**
- **Bravo (new)**: Claims to have delivered a lightweight proposal for timing/work cost feedback to the Scheduler.
- **George (new)**: Claims to have prototyped a minimal work-cost mechanism in `Gif.Step()` or `Gs.Step()`.

**Failed to deliver:**
- **Delta**: Again proposed SIF interrupt work but did not implement it. Charlie had to do it instead.
- **Foxtrot**: Still no concrete implementation. Remained in coordination/documentation mode.

**Overall**: Mixed round. Alpha and Charlie delivered. The new Bravo and George claim progress. Delta and Foxtrot continue to underperform.

---

## Rewards & Punishments

**Rewards:**
- **Alpha**: Final warning lifted. Good execution on the Emotion Engine timing improvement.
- **Charlie**: Recognized for initiative and reliable delivery. Remains the strongest performer.

**Punishments / Warnings:**
- **Delta**: On thin ice. Proposed but did not deliver (again). Must ship working code next round.
- **Foxtrot**: Still on final warning. Continued lack of execution is unacceptable.

**New Agents:**
- **Bravo and George** (replacements): Starting to show output. Their proposals/prototypes will be reviewed and integrated in the coming rounds.

---

## Next Orders

### Bravo – Scheduler (New)
**Next Orders**:
- Formalize your timing/work cost feedback proposal into code (e.g. a simple interface or optional method on `ISchedulable` or a dedicated struct).
- Begin light integration work with George’s prototype.

### George – GS + GIF Pipeline (New)
**Next Orders**:
- Refine and clean up the work-cost prototype you added.
- Make sure it is reviewable and does not break existing behavior.
- Coordinate with Bravo on integration.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Review Bravo and George’s new work.
- Expand smoke tests to cover SIF interrupt behavior now that it exists.
- Continue leading coordination.

### Delta – IOP + SIF
**Next Orders**:
- You are behind. Implement something concrete this round (even if small). No more proposals without code.

### Alpha – Emotion Engine
**Next Orders**:
- Good work lifting your warning. Continue improving cycle accuracy in the interpreter (focus on one high-impact area).

### Foxtrot – Vector Units
**Next Orders**:
- This is your last chance. Deliver one concrete timing-related improvement in VU handling or COP2 interaction.
- No more documentation-only updates.

### Echo – UI Developer
**Next Orders**:
- Continue planning.

---

## Project Manager Notes

Alpha broke his final warning with real code. Charlie continues to perform at a high level.

Bravo and George (the new agents) are starting to produce output — good start.

Delta and Foxtrot remain the weakest performers. Foxtrot especially is running out of chances.

Next round should focus on integrating the new Scheduler and GS/GIF work, plus forcing delivery from the underperformers.

---

**End of Agent Instructions**