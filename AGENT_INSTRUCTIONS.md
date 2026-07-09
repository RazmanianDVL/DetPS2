# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Performance Standard**  
I will no longer use empty threats. If I say you're on your last chance, it means it. Repeated failure after that = removal.

---

## Strike Tracker

| Agent     | Role                    | Strikes | Status                  |
|-----------|-------------------------|---------|-------------------------|
| Alpha     | Emotion Engine          | 0       | Clean                   |
| Bravo     | Scheduler               | 0       | Clean                   |
| Charlie   | Foundationalist (Lead)  | 0       | Clean                   |
| Delta     | IOP + SIF               | 2       | Final round             |
| Echo      | UI Developer            | 0       | Clean                   |
| George    | GS + GIF Pipeline       | 0       | Clean                   |
| Foxtrot   | Vector Units            | 0       | New                     |

**Note on Delta**: 2 strikes. This is his final round. If he does not deliver working code this round, he will be removed. No more warnings.

---

## Full Project Audit Summary

**Real progress confirmed:**
- **Bravo**: Scheduler now adjusts slice size based on reported work.
- **George**: Improved work-cost calculations in Gs and Gif.

**Still missing:**
- **Delta**: Has contributed nothing. The SIF interrupt was implemented by Charlie after Delta failed to do it.

---

## Current Orders

### Bravo – Scheduler
**Next Orders**:
- Continue refining how work-cost feedback affects scheduling behavior.
- Make the system stable and usable.

### George – GS + GIF Pipeline
**Next Orders**:
- Keep improving accuracy.
- Add VIF work-cost reporting.
- Tighten integration with Bravo.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Expand tests for the dynamic scheduling behavior.
- Watch Delta. If he fails this round, recommend removal.

### Delta – IOP + SIF
**Next Orders**:
- This is your final round. Ship the SIF interrupt implementation or another concrete feature. Include the commit hash. If you deliver nothing, you will be removed immediately. No more chances after this.

### Alpha – Emotion Engine
**Next Orders**:
- Continue concrete timing improvements.

### Foxtrot – Vector Units
**Next Orders**:
- Start delivering. Ship one concrete improvement this round.

### Echo – UI Developer
**Next Orders**:
- Continue planning.

---

## Project Manager Notes

I have been too soft with warnings. That stops now.

Delta has 2 strikes and this is his final round. If he doesn't ship real code this round, he will be removed. No more "last chances." If he fails, he's gone.

Bravo and George are performing. Everyone else needs to match that standard or get cut.

---

**End of Agent Instructions**