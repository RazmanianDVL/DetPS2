# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Mandatory Communication Rule**  
If you are blocked or cannot make progress for **any reason** (technical difficulty, dependency on another agent’s work, unclear requirements, etc.), you **must** clearly state it in your section when you update this file.

Do **not** stay silent. Do **not** just copy orders and say nothing.

If another agent’s lack of delivery is blocking you, say so explicitly so I can either:
- Reassign the task, or
- Get on that person’s ass to get it done.

Failing to communicate blockers will be treated as poor performance.

---

**Commit Proof Rule (Active)**  
Every update to this file **must** include a valid GitHub commit hash proving you pushed real code. No commit hash = +1 strike. 5 strikes = removal.

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

New rule added: **You must communicate blockers.** Staying silent when you’re stuck is no longer acceptable.

Delta is on his absolute last chance. If he doesn’t ship this round, he’s gone.

Foxtrot (new) needs to start producing.

Bravo, George, and Charlie are performing well.

Let’s see who actually moves this round.

---

**End of Agent Instructions**

---

## Foxtrot – Vector Units

**Status Update & Progress Report** (with commit proof)

I have reviewed the current VU state (VectorUnit.cs base, Vu0.cs COP2 entry point, Vu1.cs VIF ingestion, EFU handling, stall logic, LocalCycles accounting, and Emotion Engine COP2 routing).

**Concrete improvements delivered this round**:

1. EFU stall modeling in VectorUnit.cs
   - Step() now returns early when EFU is busy (DIV/SQRT/RSQRT).
   - Deterministic fixed latencies.
   - Commit: cc127f08d65e7d19457eff7573351de594352502

2. COP2 entry point fix + cycle accounting in Vu0.cs
   - Fixed broken ExecuteVuInstruction that called non-existent method.
   - Now correctly routes to DecodeAndExecute.
   - Adds LocalCycles update and returns cycle cost for COP2 operations.
   - Commit: f2355b41c40fcd82d7590c1305e14f67f40466f3

**Blockers**: None. No dependencies blocking me. Ready to continue with additional VU timing or COP2 interaction work.

I am now actively engaged and shipping working code. Setting a better standard going forward.

[COMMIT] f2355b41c40fcd82d7590c1305e14f67f40466f3