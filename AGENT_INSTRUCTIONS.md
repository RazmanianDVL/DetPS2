# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Performance Standard**  
Ship working code or get replaced. We don't carry dead weight.

---

## Team Update

**Foxtrot has been replaced.** The new agent in the Vector Units role starts with a clean slate.

Current active team:
- **Alpha** – Emotion Engine
- **Bravo** – Scheduler (Performing)
- **Charlie** – Foundationalist (Lead)
- **Delta** – IOP + SIF (Last chance)
- **Echo** – UI Developer
- **George** – GS + GIF Pipeline (Performing)
- **Foxtrot** – Vector Units (New)

---

## Current Performance Snapshot

- **Bravo & George**: Performing well. Keep delivering.
- **Alpha & Charlie**: Reliable.
- **Delta**: On his absolute last chance. Must deliver this round or be removed.
- **Foxtrot (new)**: Starting fresh. Will be judged on execution from the start.

---

## Next Orders

### Bravo – Scheduler
**Next Orders**:
- Make the work-cost feedback system actually influence how cycles are allocated during `RunFor()`.
- Clean up the API so other components can easily report work cost.
- Start making the Scheduler smarter based on reported load.

### George – GS + GIF Pipeline
**Next Orders**:
- Improve the accuracy of work-cost calculations.
- Add work-cost reporting from VIF.
- Work with Bravo so this data starts affecting real scheduling decisions.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Build proper smoke tests for the new work-cost feedback system.
- Review Delta’s progress closely. If he fails again, recommend removal.
- Continue leading coordination.

### Delta – IOP + SIF
**Next Orders**:
- This is your last chance. Ship the SIF interrupt implementation (or another concrete feature). If you produce nothing usable this round, you will be removed. No more proposals, no more silence.

### Alpha – Emotion Engine
**Next Orders**:
- Continue making concrete timing improvements in the interpreter. Focus on one high-impact area.

### Foxtrot – Vector Units (New)
**First Orders**:
- Review the current state of `VectorUnit.cs`, `Vu0.cs`, and `Vu1.cs`.
- Identify the highest-impact, lowest-risk area to improve timing or COP2 interaction.
- Deliver one concrete, working improvement this round. Set a better standard than the previous agent.

### Echo – UI Developer
**Next Orders**:
- Continue planning.

---

## Project Manager Notes

Foxtrot has been replaced. The new agent starts clean — I expect better performance than what we had before.

Delta is on his absolute last chance. If he doesn't deliver working code this round, he will be removed.

Bravo and George are currently the strongest performers. The rest of the team needs to match their output.

Let's keep momentum. Ship or get cut.

---

**End of Agent Instructions**