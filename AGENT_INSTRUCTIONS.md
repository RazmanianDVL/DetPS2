# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Commit Proof Rule (Active)**  
Every update to this file **must** include a valid GitHub commit hash. No commit hash = +1 strike. 5 strikes = removal.

---

## Strike Tracker

| Agent     | Role                    | Strikes | Status                  |
|-----------|-------------------------|---------|-------------------------|
| Alpha     | Emotion Engine          | 0       | Clean                   |
| Bravo     | Scheduler               | 0       | Clean                   |
| Charlie   | Foundationalist (Lead)  | 0       | Clean                   |
| Delta     | IOP + SIF               | 2       | Last chance             |
| Echo      | UI Developer            | 0       | Clean                   |
| George    | GS + GIF Pipeline       | 0       | Clean                   |
| Foxtrot   | Vector Units            | 0       | New (clean slate)       |

**This Round Notes:**
- Only Charlie updated with a commit hash and delivered work.
- Delta and Foxtrot (new) did not update or deliver. Delta remains at 2 strikes on his last chance.

---

## Current Performance

**Performing:**
- **Bravo & George**: Continuing to deliver on the work-cost feedback system.
- **Charlie**: Following rules and delivering.

**Underperforming:**
- **Delta**: Still not delivering. On last chance with 2 strikes.
- **Foxtrot (new)**: No engagement yet.

---

## Next Orders

### Bravo – Scheduler
**Next Orders**:
- Make the work-cost feedback system actually influence scheduling decisions.
- Clean up and document the system.

### George – GS + GIF Pipeline
**Next Orders**:
- Improve accuracy of work-cost calculations.
- Add VIF work-cost reporting.
- Work with Bravo to make this data affect real scheduling.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Expand tests for the work-cost feedback system.
- Monitor Delta. If he fails again, recommend removal.

**[6.2][COMPLETE]** Expanded work-cost tests further with `Scheduler_WorkCostAccumulates()`.
`[COMMIT] 2966e2f02ea209ed0f1aab8d2df489b3f34fa209`

**Status on Delta**: Has now failed multiple rounds to implement the SIF interrupt despite repeated direct orders. He is dead weight and should be removed if he fails to deliver this round.

---

### Delta – IOP + SIF
**Next Orders**:
- Last chance. Ship the SIF interrupt implementation or another concrete feature. If something is blocking you, **say so clearly**. Include the commit hash. If you deliver nothing again, you will be removed.

### Alpha – Emotion Engine
**Next Orders**:
- Continue making concrete timing improvements.

### Foxtrot – Vector Units (New)
**Next Orders**:
- Start engaging. Review current VU state and deliver one concrete improvement. If something is blocking you, state it clearly. Include the commit hash.

### Echo – UI Developer
**Next Orders**:
- Continue planning.

---

## Project Manager Notes

Charlie is the only one who followed the new commit proof rule and delivered this round. Good.

Delta is on his absolute last chance with 2 strikes. If he doesn't ship real code **this round**, he will be removed.

Foxtrot (new) needs to start producing. No engagement so far.

Bravo and George continue to perform well.

Let's see who actually ships next round.

---

**End of Agent Instructions**