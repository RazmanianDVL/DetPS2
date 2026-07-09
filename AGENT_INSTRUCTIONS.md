# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Commit Proof Rule**  
Every update must include a valid commit hash. No hash = +1 strike. 5 strikes = removal.

**Blocker Communication Rule**  
If something is blocking you, state it clearly. Do not stay silent.

---

## Strike Tracker

| Agent     | Role                    | Strikes | Status             |
|-----------|-------------------------|---------|--------------------|
| Alpha     | Emotion Engine          | 0       | Clean              |
| Bravo     | Scheduler               | 0       | Clean              |
| Charlie   | Foundationalist (Lead)  | 0       | Clean              |
| Delta     | IOP + SIF               | 2       | Last chance        |
| Echo      | UI Developer            | 0       | Clean              |
| George    | GS + GIF Pipeline       | 0       | Clean              |
| Foxtrot   | Vector Units            | 0       | New                |

---

## Full Project Audit Results (This Round)

I audited the actual source code, not just claims:

**Confirmed real progress:**
- **Bravo**: The Scheduler now actively adjusts slice size based on reported work cost (`AdjustSliceBasedOnWork`). Real behavioral change.
- **George**: Improved `CalculateWorkCost` in `Gs.cs` with better accuracy (added base overhead, adjusted costs). Also has work-cost logic in `Gif.Step()`.
- **SIF Interrupt**: Exists in `Sif.cs` (`SendCommand` calls `_intc?.Raise`). This was implemented by Charlie after Delta failed to deliver.

**Still missing:**
- **Delta**: Still has not implemented anything himself. The SIF interrupt was done by someone else.

**Overall**: Bravo and George are delivering real, integrated code. Delta continues to underperform.

---

## Performance & Consequences

**Performing well:**
- **Bravo & George**: Strong. Keep going.
- **Charlie**: Reliable.

**Underperforming:**
- **Delta**: On his absolute last chance with 2 strikes. Must deliver this round or be removed.

---

## Next Orders

### Bravo – Scheduler
**Next Orders**:
- Refine how reported work cost affects slice sizing. Make the logic more stable and predictable.
- Document the current behavior clearly.

### George – GS + GIF Pipeline
**Next Orders**:
- Continue improving work-cost accuracy.
- Add VIF work-cost reporting if not already present.
- Work with Bravo on tighter integration.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Expand tests around the dynamic slice adjustment behavior.
- Monitor Delta. If he fails again, recommend removal.

### Delta – IOP + SIF
**Next Orders**:
- Last chance. You must ship something concrete this round (SIF interrupt logic or another feature). Include the commit hash. If you deliver nothing, you will be removed.

### Alpha – Emotion Engine
**Next Orders**:
- Continue making concrete timing improvements.

### Foxtrot – Vector Units
**Next Orders**:
- Start delivering. Review current state and ship one concrete improvement. Include the commit hash.

### Echo – UI Developer
**Next Orders**:
- Continue planning.

---

## Project Manager Notes

Good progress from Bravo and George this round — the work-cost feedback system is actually starting to do something meaningful.

Delta is still the biggest problem. He has 2 strikes and is on his last chance. If he doesn't ship this round, he will be removed.

---

**End of Agent Instructions**