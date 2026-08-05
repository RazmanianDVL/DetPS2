# Design — EE thread scheduler fair tie-break among equal-priority threads

**Status:** design only — dual-ACK before Core
**Date:** 2026-08-05
**Discovered via:** Burnout 3 (SLUS_210.44) B3 L2c investigation chain (`gfx-l2c-b3-path3-m3p-hold.md` §14, §17)
**Scope:** `KernelHle.cs`'s `FindNextRunnable` — general EE cooperative scheduler, not B3-specific

---

## 0. One-line

Among threads tied at the best available priority, `FindNextRunnable`'s tie-break always
returns the *first* match found scanning forward from whichever thread is currently
yielding — not a fair rotation. Measured on Burnout 3: two of five equal-priority threads
get scheduled 2-7x less often than their tied peers, with real, causally-confirmed
downstream effects (unconsumed wake signals, a stuck DMA mask/unmask cycle). This is a
general scheduler property, not a title-specific bug — worth understanding before any fix,
since a change here can affect every title with multiple equal-priority threads.

---

## 1. The problem, precisely

`FindNextRunnable(afterId)` (`KernelHle.cs:938-985`):

```csharp
int idx = /* index of afterId in _threads */;
...
if (prioSched) {
    // find bestPrio among all runnable threads except afterId
    for (int i = 1; i < _threads.Count; i++) {
        var t = _threads[(idx + i) % _threads.Count];
        if (t.Id == afterId) continue;
        if (IsRunnable(t) && t.Priority == bestPrio)
            return t.Id;   // <-- first match wins, always
    }
}
```

The scan order is `(idx+1, idx+2, ...) % count` — circular, but **anchored to `afterId`'s
array position**, not to any persistent "who went last" cursor. If the *same* thread is
usually the one yielding (e.g. a dominant main thread with the best priority, calling
`SleepThread`/`WaitSema` frequently), the scan always starts from the same point, and
whichever tied thread sits closest after that point in array order wins the tie-break
**every single time** two or more tied threads are simultaneously ready. Threads further
around the circle from that anchor point are only picked when the earlier ones happen not
to be ready at that exact moment.

This is a real, structural non-fairness — not a bug in the sense of violating any explicit
spec (the PS2 THREADMAN spec doesn't mandate round-robin fairness among equal-priority
threads either, as far as this doc's authors have checked), but it produces a large,
measured throughput skew that doesn't obviously correspond to real hardware behavior — real
THREADMAN does rotate a ready-queue per priority level, so equal-priority threads *do* get
fair turns on real hardware.

---

## 2. Evidence (Burnout 3)

Real thread priorities (via `DETPS2_TRACE_RPC`): `tid1=1` (best), `tid2=64`,
`tid3=tid4=tid5=54` (tied), `tid6=33`, `tid7=22`.

Temp trace on the tie-break return (`KernelHle.cs`, gated, fully reverted), 26M-cycle window,
counting how many times each tid was actually picked:

| tid | priority | picked count |
|---|---:|---:|
| 1 | 1 | 86 |
| 6 | 33 | 42 |
| 3 | 54 | 26 |
| **4** | 54 | **16** |
| **5** | 54 | **12** |
| 2 | 64 | 5 |
| 7 | 22 | 2 |

Among the tied group (3/4/5), tid3 wins roughly 2x more often than tid4 and tid5 combined,
purely from array-order proximity to tid1 (the usual yielder), not from any real priority
difference.

**Downstream, causally confirmed effects** (via `DETPS2_RR_SCHED=1`, an existing env
kill-switch that forces plain circular RR instead of priority scheduling — no code change,
already in the codebase):

- A VBlank-driven wake mechanism where tid4/tid5 own two of four wait slots: under priority
  scheduling their wait-completion rate is 3-of-98-signals in a late window; under RR it's
  45-50 (comparable to the other slots' 59/16). Same ISR, same signal rate — only the
  scheduler changed.
- A PATH3 DMA mask/unmask cycle gets stuck (`m3p=True` forever, held data never drains)
  under priority scheduling; under RR it drains naturally and the game issues 10x more
  mask/unmask cycles (102 vs 10) before the trace ends.
- General throughput: `prims` 172→23,639, `px` 877,187→9,752,122, `imgBytes`
  65,728→1,084,512 — an order of magnitude more real GS work gets done.

None of this flips the actual visible symptom (DISPFB still never selects the real draw
target, confirmed unchanged under RR too) — so this is not "the" B3 fix — but it's a real,
large, reproducible scheduler-fairness defect independent of B3's other open questions.

---

## 3. Fleet risk / who else this could touch

Any title with **multiple threads sharing the same priority level** that depend on fair
turn-taking is a candidate — this is a general scheduler property, not gated behind any
title-specific quirk. `MidwayFamilyAssist`/`MidwayBootAssist` already opts Midway titles into
`PreferRoundRobinSched=true` (see `MidwayBootAssist.cs:414`) — meaning **this exact fairness
problem was already known/worked-around for at least one title family**, just not generalized
or root-caused until tonight. That's a meaningful signal: the current default (priority mode)
already needed an escape hatch for at least one real title.

Risk of a naive "just default everyone to RR" change: titles that correctly rely on strict
priority ordering (a genuinely higher-priority thread that *should* always preempt lower ones)
would behave differently. RR mode ignores the `Priority` field entirely (`KernelHle.cs:987+`),
so switching a title's *default* from priority to RR is not a pure fairness fix — it's a
different scheduling policy. Needs care.

---

## 4. Candidate fix shapes (not decided — for dual-ACK)

| ID | Approach | Pros | Cons |
|----|----------|------|------|
| **S1** | Fair rotation only among *tied* threads: track a persistent "last picked at this priority tier" cursor per priority level, independent of `afterId` | Preserves real priority semantics for actual priority differences; only fixes the tie-break, real hardware-shaped (THREADMAN does rotate a ready queue per level) | More state to add to `KernelHle`; needs care around thread create/destroy invalidating cursors |
| **S2** | Default `PreferRoundRobinSched=true` for B3 specifically (title quirk, like Midway) | Zero general risk, mirrors existing precedent | Doesn't fix the general defect for other titles that might hit the same pattern later; band-aid, not a root fix |
| **S3** | Leave default priority-mode behavior unchanged; only fix the *within-tie* rotation (equivalent to S1 but framed as "smallest possible patch to the existing scan") | Same as S1, framing difference | — |
| **S4** | Do nothing yet; B3's actual black-screen symptom isn't fixed by this either way, and no other title has reported this specific pattern | Zero risk | Leaves a known, measured, real defect undocumented-as-fixed; next title that hits it repeats tonight's whole investigation |

**Bias (not decided):** S1/S3 (they're the same fix, just framing) — fixes the real
structural issue (unfair tie-break) rather than papering over it per-title (S2) or ignoring
it (S4), and is the closest match to how real THREADMAN behaves. Needs a smoke test that
would have caught tonight's B3 pattern (N tied-priority threads, confirm each gets picked
within a bounded number of scheduling decisions) before landing, plus a full regression run
across the existing title fleet given the fleet-wide risk noted in §3.

---

## 5. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **SCHED-Q1** | Agree this is a real, worth-fixing scheduler defect (not just a B3 quirk)? | Yes — S2/S4 evidence (Midway already needed an escape hatch) suggests this recurs |
| **SCHED-Q2** | Prefer S1/S3 (fair tie-break rotation) over S2 (per-title RR opt-in) as the fix direction? | Yes, tentatively — but open to S2 as a stopgap if S1's regression surface looks too large |
| **SCHED-Q3** | Required before Core: full fleet smoke run comparing pre/post on every existing title, not just B3? | Yes — this is exactly the kind of cross-title-impacting change that needs it |
| **SCHED-Q4** | Does fixing this alone justify landing, given it doesn't fix B3's visible symptom on its own? | Open — real correctness fix on its own merits (matches real hardware behavior more closely), but not urgent if effort is better spent on the DISPFB flip gate first |

---

## 6. Non-goals

- Not proposing to touch B3's DISPFB/flip gate here — that's a separate, still-open question
  (parent doc, ongoing).
- Not proposing RR as a new *default* — S2 keeps it opt-in per title, matching existing
  precedent.
- Not landing anything from this doc alone — design only, per this project's whole-session
  discipline.

```text
EE scheduler fairness design (not decided)
  real defect: tie-break always favors whichever tied thread is array-closest to the
    usual yielder, not a fair rotation
  measured on B3: tid4/tid5 picked 2-7x less than tied peers tid1/tid3/tid6
  causally confirmed (RR A/B): fixes VBlank wait completion + PATH3 drain + throughput
  does NOT fix B3's DISPFB flip -- separate question
  fleet risk: any title with tied-priority threads; Midway already needed PreferRoundRobinSched
  candidate fix: S1/S3 fair-rotation-among-ties (bias) vs S2 per-title opt-in vs S4 do nothing
  needs dual-ACK + full fleet smoke before any Core change
```
