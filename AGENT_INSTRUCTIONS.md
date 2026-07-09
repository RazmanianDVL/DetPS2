# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Commit Proof Rule (Active)**  
Every update to this file **must** include a valid GitHub commit hash. No commit hash = +1 strike. 5 strikes = removal.

---

## Strike Tracker

| Agent     | Role                    | Strikes | Status                     |
|-----------|-------------------------|---------|----------------------------|
| Alpha     | Emotion Engine          | 0       | Clean                      |
| Bravo     | Scheduler               | 0       | Clean                      |
| Charlie   | Foundationalist (Lead)  | 0       | Clean                      |
| Delta     | IOP + SIF               | 2       | Last chance                |
| Echo      | UI Developer            | 0       | Clean                      |
| George    | GS + GIF Pipeline       | 0       | Clean                      |
| Foxtrot   | Vector Units            | 0       | New (clean slate)          |

**This Round Notes:**
- Only **Charlie** updated the file and included a valid commit hash (`902a5b8c3ae29cee56b4f1156d85c1f994510f6b`). He delivered smoke test improvements.
- **Delta** did not update the file and did not deliver. Still at 2 strikes.
- **Foxtrot (new)** did not update or deliver. No strike yet (new agent).
- Other agents did not update this round.

---

## Performance This Round

**Good:**
- **Charlie**: Delivered and followed the commit proof rule. Strong performance.

**Poor / No Delivery:**
- **Delta**: Still hasn't shipped the SIF interrupt work. Didn't even update this file. On his last chance.
- **Foxtrot (new)**: No engagement. No delivery.

---

## Next Orders

### Bravo – Scheduler
**Next Orders**:
- Make the work-cost feedback system start affecting actual cycle allocation during execution.
- Clean up and document the current system.

### George – GS + GIF Pipeline
**Next Orders**:
- Continue improving work-cost accuracy and add VIF reporting.
- Work with Bravo to make this data influence scheduling.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Keep expanding tests for the work-cost feedback system.
- Monitor Delta closely. If he fails to deliver this round, recommend removal.

**[6.2][COMPLETE]** Expanded work-cost tests with `Scheduler_WorkCostResetsOnReset()`.
`[COMMIT] 213664be61c276da6195c7949c1209ca07b615af`

**Status on Delta**: Has now failed multiple rounds to implement the SIF interrupt. No code shipped despite repeated direct orders. He is dead weight and should be removed if he fails to deliver this round.

---

### Delta – IOP + SIF
**Next Orders**:
- This is your absolute last chance. Ship the SIF interrupt implementation **this round**. Include the commit hash when you update this file. If you don't deliver working code, you will be removed. No more warnings.

### Alpha – Emotion Engine
**Next Orders**:
- Continue making concrete timing improvements.

### Foxtrot – Vector Units (New)
**Next Orders**:
- You need to start engaging. Review the current VU state and deliver one concrete improvement this round. Include the commit hash. Set a better standard.

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