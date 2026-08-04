# M6-b+ backlog — WaitSema / thread-starvation infra (from M6-a)

**Date:** 2026-08-04  
**Source:** `docs/infra-audits/m6a-waitsema-audit.md`  
**Mode:** infra-only proposals. **No title-quirk surgery** in these items (no “pulse harder on title X”).  
**Constraint:** do **not** globalize “SignalSema while peers runnable” — M6-a §6 warns WAD/GoW/Dec/B3 thrash history.

---

## Proposed backlog (priority order)

| Pri | ID | Item | Why (from M6-a) | Primary files | Notes / acceptance sketch |
|-----|-----|------|-----------------|---------------|---------------------------|
| **P0** | **M6-b1** | **Shared SleepThread / Suspend starve rescue** (grace + peer-aware gates) | Generic `MaybeRescueGenericStarvedSema` only handles `WaitSemaId != 0`. Midway `MaybeUnblockStarvedSleep`, B3 post-GTFS Sleep wakes, BO2 pure SleepThread remain **GAP**. | `src/DetPS2.Core/KernelHle.cs` (`KernelState`), call site `src/DetPS2.Core/Ps2System.cs` (ambient tick next to generic WaitSema rescue); policy edges in `src/DetPS2.Core/SonyKernelHle.cs` if WakeupThread path needs shared helpers | New counter e.g. `GenericStarvedSleepRescues`. Opt-in or tight default grace; **never** force SignalSema on RPC ids as part of this path. Pass: Midway/B3-class multi-thread sleep parks clear without title-local Wakeup spam; SM/B3 A/B no CreateSema thrash. |
| **P1** | **M6-b2** | **Scoreboard / blocker-trace counters for starvation family** | M6-a invents classification but fleet runs cannot see `GenericStarvedSemaRescues`, fabricate-vs-stall rates, or WHIP-gated 0x44 path usage. Infra observability first. | `src/DetPS2.Core/KernelHle.cs` (expose counters), `src/DetPS2.Core/SonyKernelHle.cs` (WaitSema fabricate / stall hits if not already public), `src/DetPS2.Core/Program.cs` (`blocker-trace` + `scoreboard-metrics` JSON fields), optionally `tools/SCOREBOARD_SCHEMA.md` | Print: `genericStarvedSemaRescues`, optional `waitSemaFabricates`, `semaStalls`. Pass: diagnose run on SotC/SM/GoW shows stable scrapeable lines; no behavior change. |
| **P2** | **M6-b3** | **Sticky yield / schedule fairness after SignalSema** (wake the waiter that was signaled) | GoW residual is **SwitchTo worker + frame integrity**, not whole-system deadlock. Shared `TryYieldToOtherRunnable` / fabricate help alone-thread cases; they do not replace “run the thread that owns WaitSema(id)”. | `src/DetPS2.Core/KernelHle.cs` (context switch / yield), `src/DetPS2.Core/SonyKernelHle.cs` (post-`SignalSema` / WaitSema leave), maybe `src/DetPS2.Core/EmotionEngine.cs` if preempt flags couple | Prefer **generic** “after successful SignalSema, prefer runnable waiter of that id” over title PC plants. Pass: GoW MOD_LOAD / empty-SIF path improves **without** deleting `SwitchToWorkerThread` yet; measure then soft-disable title SwitchTo in a later T10 seat. |
| **P3** | **M6-b4** | **Post-JREXIT main-revive scaffold** (`Started=false` + peer WaitSema, opt-in safe resume PC) | Whip/Haven **GAP**: `ra==0` / open-bus stack wipe → ExitThread class main death. Generic WaitSema rescue never touches PC/`$ra`/Started. Full stack integrity is hard; a **shared, env-gated** revive when tid1 dead and a worker is WaitSema-blocked is the M6-a §6 “safe future promotion” with the lowest false-positive surface if resume PC is constrained (e.g. only when LastGood/PC already valid .text). | `src/DetPS2.Core/KernelHle.cs`, `src/DetPS2.Core/EmotionEngine.cs` (jr/`$ra` edges if any shared guard), **not** title LastGood tables in Core | Env off by default. Pass: synthetic or Whip/Haven A/B with title JREXIT assist soft-off shows fewer main deaths **or** documents why resume PC cannot be shared yet. Do not merge title-specific PC constants into KernelHle. |

---

## Explicit non-items (stay out of M6-b+)

| Reject | Reason |
|--------|--------|
| Global “force SignalSema while peers runnable” | M6-a **PARTIAL** for Midway/BO2/Whip/Haven pulses — unsafe for WAD/Dec/B3 thrash |
| Delete Midway `MaybeUnblockStarvedSema` because generic exists | Generic is **stricter** (whole-system deadlock only); SM worker-vs-runnable-main remains required until producers real |
| Globalize WHIP WaitSema V3 fabricate+preempt | Title-gated shared path; M6-a: do not globalize |
| Title-local pulse cadence tweaks | Quirk debt, not infra promotion |

---

## Suggested sequence

1. **M6-b2** (counters) — cheap, unblocks evidence for b1/b3.  
2. **M6-b1** (SleepThread rescue) — largest clean GAP with clear mechanism.  
3. **M6-b3** (post-SignalSema fairness) — enables later GoW SwitchTo soft-disable.  
4. **M6-b4** (JREXIT revive) — only after resume-PC policy is design-reviewed.

---

## Related docs

| Doc | Use |
|-----|-----|
| `docs/infra-audits/m6a-waitsema-audit.md` | Full COVERED / PARTIAL / GAP inventory |
| `docs/infra-audits/gamequirks-infra-debt.md` §3 | EE thread / WaitSema theme |
| `src/DetPS2.Core/KernelHle.cs` | `MaybeRescueGenericStarvedSema` |
| `src/DetPS2.Core/SonyKernelHle.cs` | WaitSema syscall 0x44 fabricate / stall |

---

*Backlog proposal only. No Core changes in this note.*
