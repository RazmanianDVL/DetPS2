# M6-a — WaitSema / SwitchTo / JREXIT vs `MaybeRescueGenericStarvedSema`

**Date:** 2026-08-04  
**Mode:** read-only classification. **No Core code changes** in this pass.  
**Scope:** `GameQuirks/*`, `MidwayBootAssist.cs`, vs shared  
`KernelState.MaybeRescueGenericStarvedSema` (`KernelHle.cs`) + related WaitSema paths in  
`SonyKernelHle` / `EmotionEngine`.

**Goal:** which title-local WaitSema / SwitchTo / JREXIT assists are already subsumed by the  
generic starved-sema rescue (or by shared WaitSema syscall policy), and which are  
**genuinely different gaps** that still need title code or a *different* shared fix.

---

## 1. What the generic rescue actually does

**Site:** `src/DetPS2.Core/KernelHle.cs` — `KernelState.MaybeRescueGenericStarvedSema`  
**Call site:** `src/DetPS2.Core/Ps2System.cs` — after `ActiveQuirk?.Step(this)` every ambient tick.

| Property | Value |
|----------|--------|
| Grace | Default **1_500_000** master cycles on the *same* `(tid, WaitSemaId)` pair |
| Preconditions | Thread `Alive && Sleeping && WaitSemaId != 0` |
| **Extra gate (key)** | **Whole-system deadlock only:** every *other* thread must be non-`IsRunnable`. If any peer is runnable, timer resets and **no** force-signal. |
| Before force-signal | `Sony.DrainRealRpcQueue(SchedulerGeneration + 1)` |
| Action | `SignalSema(WaitSemaId)` only; no PC rewrite, no SwitchTo, no SleepThread wake, no `$ra` repair |
| Counter | `GenericStarvedSemaRescues` |
| Titles | **All** (not quirk-gated) |

Doc comment origin: generalizes Midway’s starved WaitSema for **SotC-class** “every thread asleep, producer also Sleeping” deadlocks. It deliberately does **not** fire when main (or any peer) remains runnable while a worker WaitSema’s forever.

### Related shared paths (not the generic rescue, but same family)

| Path | File | Role |
|------|------|------|
| WaitSema syscall 0x44 | `SonyKernelHle.cs` | Block; if `QueueMaySignalSema` → `RequestSemaStall`; else try `TryYieldToOtherRunnable`; else **FABRICATE** `SignalSema` + `WakeupThread` (alone). **WHIP-only** branch: fabricate + `RequestImmediatePreempt` always. |
| `_pendingSemaStall` | `EmotionEngine.cs` | Hold EE while matching RPC queued; on empty queue → peer yield or SignalSema (no SwitchTo auto-wake undo) |
| SwitchToNext self-wake | `KernelHle.cs` | If nobody else runnable, may clear Sleeping — why genuine RPC stall cannot call SwitchToNext |

---

## 2. Classification legend

| Tag | Meaning |
|-----|---------|
| **COVERED** | Same shape as generic rescue (or shared WaitSema fabricate/stall); assist is redundant *for that scenario* once generic runs. May still fire earlier/more often in title code. |
| **PARTIAL** | Overlaps force-`SignalSema` but **different gate/cadence** (runs while peers runnable, shorter grace, id filters, or post-event only). Generic does **not** replace it. |
| **GAP** | Different mechanism: JREXIT / `$ra`/PC salvage, explicit SwitchTo worker, SleepThread wake, PC soft-leave of WaitSema leaf, main `Started=false` revive, PC-band thrash. Needs different shared infra or stays title-local. |

---

## 3. Per-pattern inventory

### 3.1 MidwayBootAssist (`SLUS_210.87` Shaolin Monks)

| Pattern | Location (approx) | Tag | Why vs generic |
|---------|-------------------|-----|----------------|
| `MaybeUnblockStarvedSema` | `MidwayBootAssist.cs` ~2193–2225 | **PARTIAL** | Same grace drain+SignalSema skeleton, but **no whole-system-deadlock gate**. Historical case: worker WaitSema(3) while **main stays runnable** and never signals. Generic **explicitly refuses** this. Grace also tightens to **250k** after resource force. |
| `MaybeUnblockStarvedSleep` | ~2275–2318 | **GAP** | SleepThread / Suspend (`WaitSemaId==0`); generic only handles WaitSema. |
| `MaybeResumeAllAfterResource` | ~2245–2272 | **GAP** | Resume SoftSuspended + Signal any WaitSema + Wakeup workers + `YieldToWorker` at fixed PC — multi-axis unpark, not deadlock-only. |
| Menu / pad / stream `SignalSema` pulses | ~974–978, 3180+, 4181+, 4336+, … | **PARTIAL** | Periodic low-id SignalSema while peers live; not grace+deadlock. |
| SIF force-init / CRT0 / CRI plants | elsewhere | **GAP** | Not WaitSema at all (producers missing). |

**Verdict:** SM’s original `MaybeUnblockStarvedSema` is **not** covered by generic. Generic covers the *stricter* “all deadlocked” subset only. Deleting Midway’s helper would re-break “main busy, worker WaitSema(3)” unless a new shared policy allows force-signal with runnable peers (high risk for WAD/RPC races).

---

### 3.2 MidwayFamilyAssist (DA / Deception / Armageddon)

| Pattern | Tag | Why |
|---------|-----|-----|
| Policy: **no SignalSema(3)** / no WaitSema fabricate on SIF poll | **GAP (by design)** | Family intentionally avoids the Generic/Midway force-signal class on SIF-cmd mutex. |
| Dec `TryBreakDecCdPowerOffStorm` | **GAP** | Soft-complete WaitSema leaf → CallRpc `$ra` / idle park; clear `Sleeping`/`WaitSemaId` **without** SignalSema(3). PC-band storm, not starved producer. |
| Sparse pure-sleeper `WakeupThread` | **GAP** | SleepThread, not WaitSema. |
| Occasional low-id / high-id filtered SignalSema (DA stream paths) | **PARTIAL** | Peer-runnable, id-filtered; not whole-system deadlock. |

**Verdict:** Dec PowerOff WaitSema storm and DA keep-alive are **orthogonal** to generic rescue.

---

### 3.3 GodOfWarAssist (`SCUS_973.99`)

| Pattern | Tag | Why |
|---------|-----|-----|
| `SwitchToWorkerThread` + optional SignalSema | **GAP** | Explicit `SaveCurrentContext` / `RestoreContext(worker)` + callee-saved repair + force PC `WorkerPostWait`. Generic only SignalSema; never switches tid. |
| Empty SIF poll WaitSema(3) / 0x20 / ≥32 pulses | **PARTIAL** | Force-signal **while main often runnable**; PC-band soft-return via `$ra`. Shared WaitSema may fabricate when alone, but residual needs title pulse + SwitchTo. |
| Poison WaitSemaId clear (`>256`) | **GAP** | Bad `a0` on trampoline — state repair, not starvation. |
| StartThread / dormant main re-start on WaitSema trampoline | **GAP** | Lifecycle, not sema count. |
| Soft-return empty-SIF leaf | **GAP** | PC/`$ra` leave without SignalSema. |

**Verdict:** GoW’s core residual is **worker SwitchTo + frame integrity**, not generic whole-system WaitSema deadlock. `TryYieldToOtherRunnable` / fabricate help MOD_LOAD; they do not replace `SwitchToWorkerThread`.

---

### 3.4 WhiplashAssist (`SLUS_206.84`)

| Pattern | Tag | Why |
|---------|-----|-----|
| `MaybeRescueFlushCacheJrExit` (`ra==0` @ FlushCache epi) | **GAP** | JREXIT class: PC/`$ra`/SP salvage after CD_NCMD; revive tid1. Generic never touches PC. |
| `ReviveMainInPlace` / `EnsureMainThreadRunning` | **GAP** | `Started=false` after ExitThread/JREXIT; worker may still WaitSema. |
| `PulseWaiters` (SignalSema sleepers post-reboot) | **PARTIAL** | Rate-limited; runs with peers; silenced after real CDVD≥50. Not deadlock-gated. |
| Sony `case 0x44` **WHIP_SEMA_FIX_V3** | **PARTIAL / shared-title** | Title-gated fabricate+preempt **inside syscall** — different from ambient generic rescue (and intentionally not global). |

**Verdict:** WHIP wall is **JREXIT + main dead + post-reboot waiter pulse**. Generic deadlocked-WaitSema may help side cases; it does not replace FlushCache/`$ra=0` rescue.

---

### 3.5 TeamIcoAssist — Haven residual (`SLUS_212.97` class)

| Pattern | Tag | Why |
|---------|-----|-----|
| `MaybeRescueHavenJrExit` + CallRpc frame complete | **GAP** | Same JREXIT family as Whip; reconstruct 192-byte CallRpc frame, resume NUSOUND bulk. |
| `MaybeReviveHavenMain` / `ReviveHavenMain` | **GAP** | tid1 `Started=false` while worker WaitSema. |
| `MaybeRepairHavenPoisonRa` / `MaybeEscapeHavenBadPc` | **GAP** | `$ra`/PC thrash — not sema. |
| `MaybePulseHavenWaiters` | **PARTIAL** | Only after JREXIT/main/bad-PC events; Signal all WaitSema sleepers with short interval; peers may be runnable. |

**Verdict:** Haven post-NUSOUND is **JREXIT / poison-`$ra` / bad-PC**, with WaitSema pulse as secondary. Generic covers none of the primary walls.

**SotC / Ico:** PreferIopRp only — no WaitSema assist. SotC was the **motivating title** for generic whole-system deadlock rescue (per KernelHle doc). That path is **COVERED** by generic for all-asleep cases; no title-local WaitSema helper remains to delete there.

---

### 3.6 BloodOmen2SnAssist (`SLUS_200.24`)

| Pattern | Tag | Why |
|---------|-----|-----|
| `PulseWaiters` SignalSema | **PARTIAL** | Pre-GOE only (`cdvd < 350`); then CompleteRpcEnd owns leave. Peer-runnable, short interval. |
| Pure SleepThread Wakeup | **GAP** | Not WaitSema. |
| Explicit **do not** yank WaitSema leaf @0x488894 | **GAP (policy)** | Opposite of force-signal — protect FILEIO CompleteRpcEnd. |

**Verdict:** Early SN/boot producer missing → periodic SignalSema. After GOE, shared RPC path. Generic only overlaps if boot hits full multi-thread deadlock (uncommon for BO2 single-waiter parks).

---

### 3.7 Burnout3Assist (`SLUS_210.50`)

| Pattern | Tag | Why |
|---------|-----|-----|
| Post-GTFS: **only** SleepThread / Suspend wake — **never** SignalSema on RPC ids | **GAP (policy)** | Blind SignalSema caused CreateSema/WaitSema thrash (ids past 0x500). |
| High WaitSemaId (≥32) pulses in residual/post-TXD paths | **PARTIAL** | Filtered SignalSema while peers runnable; PC-gated residual. |
| Post-LGDEV WaitSema leaf soft-leave / flag plants | **GAP** | PC/flag, not generic deadlock. |
| WaitSema(3) residual: “fabricate already kernel” | **COVERED (syscall)** | Relies on shared 0x44 fabricate when alone; title avoids extra SignalSema. |

**Verdict:** B3 deliberately **diverges** from Midway-style force-SignalSema. Generic does not enable the banned thrash path; residual is Sleep/flags/LGDEV PC leave.

---

### 3.8 VexxAssist (`SLUS_203.83`)

| Pattern | Tag | Why |
|---------|-----|-----|
| Explicit **no WaitSema fabricate** | **N/A** | Hang class is freelist/list/FILEIO, not WaitSema. |

**Verdict:** No M6-a WaitSema debt surface.

---

## 4. Mechanism map (covered vs gap)

```
                    ┌─────────────────────────────────────┐
                    │  WaitSema blocked, count==0          │
                    └─────────────────────────────────────┘
                                      │
           ┌──────────────────────────┼──────────────────────────┐
           ▼                          ▼                          ▼
  Matching RPC queued         No matching RPC            Long grace elapsed
  Sony: RequestSemaStall      TryYield peer /            on same (tid,id)
  EE: _pendingSemaStall       FABRICATE if alone
           │                  (WHIP: always fab+preempt)
           │                          │
           ▼                          ▼
  CompleteRpcEnd              Shared 0x44 leave
  SignalSema (real)           (not ambient rescue)
                                      │
                    ┌─────────────────┴─────────────────┐
                    ▼                                   ▼
         Anyone else runnable?                 All peers non-runnable?
         YES → Midway/BO2/Whip/                YES → MaybeRescueGeneric
               Haven/GoW title pulses                StarvedSema  [COVERED
               still needed [PARTIAL]                 whole-system case]
                    │
                    ▼
         Different walls (not SignalSema count):
           • JREXIT / ra==0 / frame complete     [GAP Whip/Haven]
           • SwitchTo worker + SP repair         [GAP GoW]
           • SleepThread / Suspend wake          [GAP Midway/B3/BO2]
           • PC soft-leave WaitSema leaf         [GAP Dec/GoW/B3]
           • tid1 Started=false revive           [GAP Whip/Haven]
```

---

## 5. Summary table (audit roll-up)

| ID | Pattern / assist | Primary title(s) | Mechanism | vs `MaybeRescueGenericStarvedSema` | Residual shared fix (if any) |
|----|------------------|------------------|-----------|------------------------------------|------------------------------|
| G0 | `MaybeRescueGenericStarvedSema` | All (motivated by SotC) | Grace + drain RPC + SignalSema iff **no peer runnable** | — | Keep; do not loosen gate lightly |
| M1 | Midway `MaybeUnblockStarvedSema` | SM | Force SignalSema **even if main runnable** | **PARTIAL** — stricter peer-runnable case | Optional shared “orphan WaitSema with idle peer that never signals” is **unsafe** without producer model; prefer real SIF/worker progress |
| M2 | Midway `MaybeUnblockStarvedSleep` | SM | WakeupThread / Resume after grace | **GAP** | Shared SleepThread starve rescue (peer-aware) |
| M3 | Midway post-resource resume-all | SM | Resume + Signal + YieldToWorker | **GAP** | Real Suspend/Resume + ADX mutex |
| F1 | Dec PowerOff WaitSema storm | Deception | PC → CallRpc `$ra` / idle; clear wait **no SignalSema(3)** | **GAP** | CD_SCMD PowerOff HLE / don’t re-enter thrash |
| F2 | DA pure-sleeper keep-alive | DA | Wakeup SleepThread only | **GAP** | Sleep producer / pad/menu scheduling |
| W1 | GoW `SwitchToWorkerThread` | GoW | Context switch + frame repair + optional SignalSema | **GAP** | EE multi-thread fairness + sticky worker after SignalSema |
| W2 | GoW empty SIF WaitSema pulse | GoW | Filtered SignalSema + soft-return | **PARTIAL** | Honest SIF-cmd queue / empty-poll path |
| H1 | Whip FlushCache JREXIT | Whip | `ra==0` → resume PostCd; revive main | **GAP** | Stack/`$ra` integrity after CD_NCMD / FlushCache |
| H2 | Whip `PulseWaiters` | Whip | Periodic SignalSema pre-real-CDVD | **PARTIAL** | LOADFILE/SIF cadence post-IOPRP reboot |
| H3 | Sony WHIP WaitSema V3 | Whip | Syscall fabricate + immediate preempt | **PARTIAL** (title-gated shared) | Do **not** globalize (GoW/Dec residual risk) |
| T1 | Haven JREXIT / poison `$ra` / bad-PC | Haven | Frame reconstruct + PC rehome | **GAP** | Stack integrity after CallRpc / open-bus |
| T2 | Haven `MaybePulseHavenWaiters` | Haven | Event-gated SignalSema all waiters | **PARTIAL** | Secondary to JREXIT; real peer after main revive |
| B1 | BO2 `PulseWaiters` | BO2 | Pre-GOE SignalSema + Sleep wake | **PARTIAL** | SN/IOP producer; post-GOE CompleteRpcEnd |
| C1 | B3 Sleep-only post-GTFS | B3 | **Avoid** SignalSema on RPC ids | **GAP** (policy) | LGDEV skip + FILEIO without thrash |
| C2 | B3 high-id residual SignalSema | B3 | WaitSemaId ≥ 32 pulses | **PARTIAL** | Residual only after LGDEV/TXD |
| V0 | Vexx | Vexx | No WaitSema assist | **N/A** | — |
| S0 | Shared 0x44 fabricate / stall | All | Syscall-time leave | **COVERED (syscall)** — orthogonal ambient | Already shared |

---

## 6. Actionable conclusions

1. **Do not treat Midway `MaybeUnblockStarvedSema` as deleted by M6 generic.**  
   Generic added the **whole-system deadlock** subset (SotC). SM’s worker-vs-runnable-main case remains **PARTIAL / still required** until producers (SIF RPC / worker schedule) are real.

2. **JREXIT class (Whip, Haven) is a genuine different gap.**  
   Needs stack/`$ra` integrity or a shared “jr ra with ra==0 + revive main” policy — **not** SignalSema grace.

3. **GoW SwitchTo worker is a genuine different gap.**  
   Needs cooperative switch to the **signaled** waiter (or sticky yield-to-worker after SignalSema), not blind force-signal.

4. **SleepThread / Suspend starve (Midway, B3, BO2) is uncovered** by generic (WaitSemaId must be ≠ 0).

5. **Dec/DA “no SignalSema(3)” and B3 “no RPC SignalSema” are intentional anti-thrash policies** opposite of force-signal; generic must never be loosened to re-create those thrash modes.

6. **Safe future promotions (if measured live):**
   - Optional shared **SleepThread** starve rescue (grace + peer checks) — Midway M2 analogue.  
   - Optional shared **post-JREXIT main revive** when `Started=false` and another thread is WaitSema-blocked — only with safe resume PC (hard without title LastGood).  
   - **Do not** promote “SignalSema while peers runnable” to global without per-title measurement (WAD, GoW, Dec, B3 all regressed historically).

7. **Already adequately covered for their scenario:**
   - SotC-class all-asleep WaitSema deadlock → **G0 generic**.  
   - Alone-thread WaitSema with no matching RPC → **shared 0x44 fabricate**.  
   - Matching RPC WaitSema → **stall + CompleteRpcEnd**.

---

## 7. File index (absolute paths)

| File |
|------|
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\KernelHle.cs` |
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\Ps2System.cs` |
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\SonyKernelHle.cs` |
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\EmotionEngine.cs` |
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\MidwayBootAssist.cs` |
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\GodOfWarAssist.cs` |
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\WhiplashAssist.cs` |
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\TeamIcoAssist.cs` |
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\BloodOmen2SnAssist.cs` |
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\Burnout3Assist.cs` |
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\MidwayFamilyAssist.cs` |
| `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\VexxAssist.cs` |

**Related debt map:** `docs/infra-audits/gamequirks-infra-debt.md` §3 (EE thread / WaitSema theme).
