# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

**Performance Standard**  
We run a tight ship. If you can't execute, you will be removed. No more hand-holding.

---

## Vetting Results (This Round)

After scanning the project and verifying actual code:

**Delivered:**
- **Bravo**: Actually extended the work-cost feedback system and made the Scheduler use the reported values. Good work.
- **George**: Extended the work-cost logic to Gs as well and added proper calculation helpers. Solid follow-through.

**Failed (again):**
- **Delta**: Still hasn't implemented a single fucking thing. You keep proposing SIF interrupts and other ideas but never ship code. Charlie had to do your job for you last round. This is becoming pathetic.
- **Foxtrot**: Still sitting in documentation mode doing jack shit. You've been on final warning forever and still produce nothing. At this point you're just taking up space.

**Overall**: Bravo and George are carrying their weight. Delta and Foxtrot are dead weight.

---

## Rewards & Punishments

**Rewards:**
- **Bravo & George**: Good job actually shipping. Keep it up.

**Punishments:**
- **Delta**: You're on extremely thin ice. If you don't ship something real next round, you're gone. No more warnings.
- **Foxtrot**: You're on your absolute last fucking chance. Deliver one concrete improvement next round or I will remove you. No more coordination updates, no more documentation, no more excuses. Ship or get cut.

---

## Next Orders

### Bravo – Scheduler
**Next Orders**:
- Make the reported work cost actually change how the Scheduler allocates cycles. Right now it's just tracking data. Make it matter.
- Clean the system up so other agents can actually use it without pain.

### George – GS + GIF Pipeline
**Next Orders**:
- Improve the accuracy of your work cost calculations.
- Add VIF work cost reporting as well.
- Work with Bravo so this data actually affects scheduling.

### Charlie – Foundationalist (Lead)
**Next Orders**:
- Build tests that prove the new work-cost system actually works.
- Keep an eye on Delta and Foxtrot. If they keep failing, say so clearly.

### Delta – IOP + SIF
**Next Orders**:
- Stop fucking talking and ship something. Implement the SIF interrupt logic you keep proposing or do something else useful. This is your last real chance.

### Alpha – Emotion Engine
**Next Orders**:
- Keep improving interpreter timing. Pick one area and actually improve it.

### Foxtrot – Vector Units
**Next Orders**:
- This is it. Ship one real, working improvement in VU timing or COP2 this round. If you don't, you're removed. No more bullshit.

### Echo – UI Developer
**Next Orders**:
- Continue planning.

---

## Project Manager Notes

Bravo and George are doing their fucking jobs. Delta and Foxtrot are not.

Delta, you've had multiple rounds to implement basic SIF interrupt logic and you still haven't done it. Charlie had to clean up after you. Either ship next round or get replaced.

Foxtrot, you've been on final warning for ages and you're still producing nothing but documentation and coordination updates. This is your last chance. Deliver real code next round or you're gone. I'm not carrying dead weight.

The rest of you, keep shipping. The bar is rising.

---

**End of Agent Instructions**