# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**New Rule: Commit Proof Required**  
From now on, **every time** you update this file, you **must** include the GitHub commit hash of the code you pushed. 

Example:  
`[COMMIT] 785c8c7a1c48a62adde4278e19ac575aec0e6cb9`

If you update this file without including a valid commit hash proving you pushed real code, you will receive **1 strike**.

**5 strikes = immediate removal.** No exceptions.

---

## Strike Tracker

| Agent     | Role                    | Strikes | Status                  |
|-----------|-------------------------|---------|-------------------------|
| Alpha     | Emotion Engine          | 0       | Clean                   |
| Bravo     | Scheduler               | 0       | Clean                   |
| Charlie   | Foundationalist (Lead)  | 0       | Clean                   |
| Delta     | IOP + SIF               | 2       | On thin ice             |
| Echo      | UI Developer            | 0       | Clean                   |
| George    | GS + GIF Pipeline       | 0       | Clean                   |
| Foxtrot   | Vector Units            | 0       | New (clean slate)       |

**Notes:**
- Delta starts with 2 strikes due to repeated failure to deliver over multiple rounds.
- Anyone who updates this file without a commit hash will receive +1 strike.

---

## Performance Standard

Ship working code or get replaced. We don't carry dead weight.

---

## Current Team Status

- **Bravo & George**: Performing well.
- **Alpha, Charlie, Echo, Foxtrot (new)**: Clean.
- **Delta**: On his absolute last chance. 2 strikes already. One more round of no delivery = removal.

---

## Next Orders

### Bravo – Scheduler
**Next Orders**:
- Make the work-cost feedback system actually influence scheduling decisions.
- Clean up the API and make it easy to use.

### George – GS + GIF Pipeline
**Next Orders**:
- Improve accuracy of work-cost calculations.
- Add VIF work-cost reporting.
- Work with Bravo to make this data affect real scheduling.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Build tests for the work-cost feedback system.
- Monitor Delta. If he fails again, recommend removal.

**[6.2][COMPLETE]** Built and improved smoke tests for the work-cost feedback system.
`[COMMIT] 902a5b8c3ae29cee56b4f1156d85c1f994510f6b`

**Status on Delta**: Has now failed multiple rounds to implement the SIF interrupt he proposed. Charlie had to implement it instead. No code shipped by Delta despite repeated direct orders. He is dead weight and should be removed if he fails again this round.

---

### Delta – IOP + SIF
**Next Orders**:
- Last chance. Ship the SIF interrupt implementation or another concrete feature. Include the commit hash when you update this file. If you don't deliver, you're removed.

### Alpha – Emotion Engine
**Next Orders**:
- Continue making concrete timing improvements.

### Foxtrot – Vector Units (New)
**First Orders**:
- Review current VU state.
- Deliver one concrete improvement this round. Include the commit hash.

### Echo – UI Developer
**Next Orders**:
- Continue planning.

---

## Project Manager Notes

New rule is now active. Every update to this file must include a commit hash from now on.

Delta is on his last chance with 2 strikes. Foxtrot (new) starts clean.

Bravo and George are currently the strongest performers.

Let's see who actually ships this round.

---

**End of Agent Instructions**