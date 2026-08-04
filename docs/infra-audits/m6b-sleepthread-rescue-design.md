# M6-b1 design — Shared SleepThread / Suspend starve rescue

**Status:** design only (ready for implement ACK) — **no Core change in this note**  
**Date:** 2026-08-04  
**Pri / ID:** P0 / **M6-b1** (`docs/infra-audits/m6b-next-items.md`)  
**Source audit:** `docs/infra-audits/m6a-waitsema-audit.md` §3.1 M2, §3.6–3.7, §5 G0 vs M2/C1, §6.4  
**Mode:** infra-only. **No GameQuirks edits. No title PC plants. No push.**

---

## 1. Problem (one paragraph)

`KernelState.MaybeRescueGenericStarvedSema` only rescues parks with `WaitSemaId != 0`. Pure **SleepThread** (`WaitSemaId == 0`, not VBlank) and **SuspendThread** (`SuspendCount > 0`) remain **GAP** (M6-a M2 / B3 post-GTFS sleep policy / BO2 pure-sleep pulse). Title code already papered this: Midway `MaybeUnblockStarvedSleep`, B3 post-GTFS Wakeup-only loops, BO2/DA sparse pure-sleeper wakes. Goal: one **title-independent** ambient rescue, peer-gated like generic WaitSema, that **never** calls `SignalSema` (B3 thrash history: CreateSema/WaitSema ids past 0x500).

---

## 2. Mechanism

### 2.1 Placement

| Piece | Site |
|-------|------|
| State + API | `src/DetPS2.Core/KernelHle.cs` — sibling of `MaybeRescueGenericStarvedSema` |
| Call site | `src/DetPS2.Core/Ps2System.cs` ambient tick, **immediately after** `MaybeRescueGenericStarvedSema(this)` (same “post-quirk, every commercial title” slot) |
| Policy helpers | Prefer existing `WakeupThread` / `ResumeThread` only; **do not** open a new Sony syscall path |

No `GameQuirks/*` and no Midway-only address logic.

### 2.2 Candidate parks (per thread)

A thread is a **sleep/suspend starve candidate** iff **all** of:

1. `Alive`
2. `WaitSemaId == 0` (hard — never touch WaitSema parks)
3. `!WaitVblank` (VBlank parks have a real producer; leave them)
4. One of:
   - **Pure sleep:** `Sleeping && SuspendCount == 0`
   - **Suspend nest:** `SuspendCount > 0` (whether or not `Sleeping` was set by Suspend)
5. **Lifecycle:** `Started || Id == 1` — do **not** wake never-started CreateThread shells
6. **ExitThread / SoftSuspended sticky:** if `SoftSuspended && EverStarted && !Started` → **skip** (ResumeThread already keeps DORMANT peers sticky to avoid ADX Suspend/Refer thrash). Do **not** clear `SoftSuspended` here.

### 2.3 Grace timers

Mirror Midway’s proven windows (not B3’s 100k pulse cadence):

| Kind | Default grace (master cycles) | Rationale |
|------|-------------------------------|-----------|
| Pure SleepThread | **2_000_000** | Midway `graceSleep` |
| Suspend nest | **400_000** | Midway `graceSuspend` (Resume missing under HLE is common) |

Tracking dict (KernelHle private, not save-state critical — same class as `_genericSemaWaitStart`):

```text
_genericSleepWaitStart: tid → (kind: sleep|suspend, sinceCycle)
```

Rules:

- Leave candidate set → remove entry.
- Kind changes (sleep ↔ suspend) or leave park → re-arm `sinceCycle = MasterCycles`.
- On successful rescue → remove entry (fresh grace if it re-parks). Same as Midway sleep path, not the “cooldown reset to now while still parked” WaitSema pattern.

### 2.4 Peer-aware gate (required)

**Default = whole-system deadlock only**, same spirit as `MaybeRescueGenericStarvedSema`:

```text
if any other thread IsRunnable → reset sinceCycle; do not wake
if every other thread non-runnable → allow rescue after grace
```

Use the same private `IsRunnable` definition (Alive, not Sleeping, not WaitVblank, SuspendCount==0, Started or tid1).

**Why not Midway’s ungated loop as default:** Midway wakes sleepers while peers remain runnable. That is correct for “worker alive, forgot to WakeupThread,” but as a **global** default it can thrash intentional multi-thread Sleep parks (pad/menu waiters, B3 flag-gated Sleep loops that expect a **memory flag**, not a blind Wakeup). Whole-system gate keeps the promotion SotC/generic-safe; residual peer-runnable pure-sleep stays title-local or later opt-in (below).

Optional **phase-2 / env-gated orphan** (implement only if default fails SM A/B and WakeupThread(0) is already fixed in Core):

| Env | Behavior |
|-----|----------|
| `DETPS2_STARVED_SLEEP_ORPHAN=1` | After **2×** pure-sleep grace, wake **pure Sleep only** even if a peer is runnable. Still **never** force-Resume Suspend under this mode; still never SignalSema. |

Default: **off**. Document in implement notes; do not enable fleet-wide without A/B.

### 2.5 Action (after grace + gate)

Order per candidate:

1. **No** `DrainRealRpcQueue` requirement for pure Sleep (RPC is not the Sleep producer). Optional cheap drain is **allowed** but **must not** be used as a SignalSema substitute — drain only, then re-check still parked.
2. If `SuspendCount > 0`: loop `ResumeThread(id)` until count 0 or a small safety cap (e.g. 16) — never infinite if Resume no-ops on SoftSuspended sticky.
3. Else pure sleep: `WakeupThread(id)` once (existing API already refuses WaitSema parks and Suspend-only).
4. Increment `GenericStarvedSleepRescues`.
5. Trace when `DETPS2_TRACE_RPC=1` (same channel as generic WaitSema):  
   `[RPC] generic force-waking starved sleep/suspend thread=… susp=… cyc=…`

### 2.6 Hard bans (invariants)

| Ban | Why |
|-----|-----|
| `SignalSema` / fabricate WaitSema on this path | B3 CreateSema thrash; Dec/DA anti-SIF-3 policy |
| Clear `WaitSemaId` without Signal | Corrupts waiter bookkeeping |
| Touch WaitVblank parks | Real VBlank producer |
| Force-clear SoftSuspended on ExitThread peers | ADX Suspend/Refer thrash |
| SwitchTo / PC / `$ra` rewrite | JREXIT / GoW are different M6-b items |
| Title serial / PC-band gates in Core | Quirk debt |

### 2.7 Pseudocode

```text
MaybeRescueGenericStarvedSleep(sys, graceSleep=2e6, graceSuspend=4e5):
  if DETPS2_DISABLE_M6B_SLEEP_RESCUE=1: return
  for t in _threads:
    if not Candidate(t): RemoveTimer(t); continue
    kind = t.SuspendCount > 0 ? suspend : sleep
    ArmOrContinueTimer(t, kind)
    if not GraceElapsed(t, kind): continue
    if PeerRunnableExists(excluding t) and not OrphanEnvAllowsPureSleep(t, kind):
      ResetTimer(t); continue
    if kind == suspend: DrainResume(t)
    else: WakeupThread(t.Id)
    RemoveTimer(t)
    GenericStarvedSleepRescues++
```

---

## 3. Files to touch (implement turn)

| File | Change |
|------|--------|
| `src/DetPS2.Core/KernelHle.cs` | `_genericSleepWaitStart`; `GenericStarvedSleepRescues`; `MaybeRescueGenericStarvedSleep`; clear dict on `Reset()` (optional but preferred) |
| `src/DetPS2.Core/Ps2System.cs` | Call after `MaybeRescueGenericStarvedSema` |
| `src/DetPS2.Core/Program.cs` | **Optional** print field if scraper already nearby — prefer defer full scoreboard fields to **M6-b2** unless one line is free |

**Out of scope this item:** `GameQuirks/*`, `MidwayBootAssist.MaybeUnblockStarvedSleep` deletion (title may still need peer-runnable / post-resource paths), `SonyKernelHle` WaitSema 0x44.

---

## 4. Flag / kill-switch

| Env | Default | Role |
|-----|---------|------|
| `DETPS2_DISABLE_M6B_SLEEP_RESCUE=1` | unset = **on** | Hard kill for A/B and fleet regression |
| `DETPS2_STARVED_SLEEP_ORPHAN=1` | **off** | Optional peer-runnable pure-sleep orphan (phase-2) |
| `DETPS2_TRACE_RPC=1` | off | Existing trace channel; log each rescue |

No CLI flag required for v1 (env matches A2/A3/M1 kill-switch style). Counter `GenericStarvedSleepRescues` is the runtime proof bit.

**Default policy recommendation:** rescue **enabled** by default with **whole-system gate + long grace** (same risk class as existing generic WaitSema rescue). Kill-switch for instant rollback.

---

## 5. Acceptance

### 5.1 Behavioral (not title-specific)

1. **Whole-system pure-sleep deadlock:** ≥2 alive threads, all non-runnable, at least one pure SleepThread park past grace → `WakeupThread` fires; counter increments; no `SignalSema` in the rescue stack.
2. **Peer runnable:** one pure sleeper + one busy/runnable peer → **no** rescue under default gate (timer resets); orphan env may change this later.
3. **WaitSema parks untouched:** thread with `WaitSemaId != 0` never appears in this path’s action (generic WaitSema path remains sole ambient SignalSema rescuer).
4. **Suspend:** whole-system all-non-runnable + `SuspendCount > 0` past suspend grace → Resume drain; SoftSuspended DORMANT peers not resurrected.
5. **Not title-specific:** no serial checks, no PC bands, no GameQuirks calls inside the helper.

### 5.2 Fleet smokes (A/B with kill-switch)

| Title / class | Pass criteria |
|---------------|---------------|
| **SM (Midway)** | Boot trajectory no worse; no CreateSema / WaitSema thrash spike; sleep parks that were whole-system deadlocks clear without new SignalSema spam. Title `MaybeUnblockStarvedSleep` may still fire earlier — OK. |
| **B3** | Post-GTFS path: **no** CreateSema/WaitSema id climb / 60k+ WaitSema thrash pattern historically tied to blind SignalSema. Pure sleep rescues OK; RPC WaitSema ids never forced by this helper. |
| **SotC / generic WaitSema** | `GenericStarvedSemaRescues` behavior unchanged; sleep rescue does not steal WaitSema cases. |
| **Dec / DA / WAD-class** | No new SIF-mutex (WaitSema 3) fabricate via this path (invariant — should be impossible if bans hold). |
| **BO2** | No post-GOE SignalSema reintroduction; pure-sleep whole-system cases may wake; peer-runnable PulseWaiters remain title-local. |

**Smoke commands (sketch):** existing `blocker-trace` / `scoreboard-metrics` A/B:

```text
# baseline / feature (default on)
detps2 blocker-trace <user-media.json> <cycles>

# kill-switch A/B
DETPS2_DISABLE_M6B_SLEEP_RESCUE=1 detps2 blocker-trace <user-media.json> <cycles>
```

Compare: exit trajectory, cdvd, syscall counts, CreateSema max id, and (once M6-b2 lands) `genericStarvedSleepRescues` / `genericStarvedSemaRescues`.

### 5.3 Unit / synthetic (if cheap)

Optional Core test: synthetic KernelHle two-thread all-sleep pure-sleep after grace → Wakeup; one runnable peer → no Wakeup. Prefer real fleet if harness cost is high.

---

## 6. Non-goals

| Non-goal | Owner / later |
|----------|----------------|
| Force SignalSema while peers runnable | Explicit M6-a reject / M6-b non-item |
| Delete Midway `MaybeUnblockStarvedSema` or peer-runnable sleep assist | PARTIAL residual until producers real |
| Delete B3 flag plants / post-GTFS pad kicks | Title progress producers, not pure schedule starve |
| GoW SwitchTo worker / sticky yield after SignalSema | **M6-b3** |
| JREXIT / `$ra` / tid1 `Started=false` revive | **M6-b4** |
| Globalize WHIP WaitSema V3 fabricate+preempt | Shared-title only; M6-a do-not-globalize |
| Scoreboard / blocker-trace full counter family | **M6-b2** (may add single print opportunistically) |
| SoftSuspended bulk clear / post-resource resume-all | Midway `MaybeResumeAllAfterResource` multi-axis — stays title |
| PC soft-leave WaitSema leaf | Dec / GoW / B3 residual |

---

## 7. Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| Blind Wakeup of intentional long Sleep (menu pad wait) under whole-system gate | Med if single-thread “all asleep” is normal | Long 2M grace; single-thread alone is already covered by other paths; multi-thread all-asleep is the intended rare deadlock |
| Suspend Resume spam ↔ re-Suspend thrash | Med | SoftSuspended Exit sticky skip; safety cap on Resume loop; long-ish suspend grace |
| Interaction with Midway title sleep assist (double wake) | Low | Double Wakeup is mostly no-op; counter may double-count only generic path |
| Operator confuses sleep rescue with WaitSema thrash | Med (ops) | Invariants + B3 A/B; kill-switch; never SignalSema |
| Orphan mode (`STARVED_SLEEP_ORPHAN`) wakes producer-waiting sleepers while main runs | High if defaulted on | **Off by default**; measure SM only before any default flip |
| Save-state timer dict not serialized | Low | Same as `_genericSemaWaitStart` today — re-arm after load |
| Performance | Low | O(threads²) peer scan once per ambient tick after grace candidates only; tiny thread counts |

---

## 8. Relation to existing code (read-only map)

| Existing | Relation to M6-b1 |
|----------|-------------------|
| `MaybeRescueGenericStarvedSema` | Template for grace + whole-system `IsRunnable` gate + ambient call site |
| Midway `MaybeUnblockStarvedSleep` | Prototype for sleep/suspend candidate + grace constants; **ungated** — Core generalizes the **gated** subset |
| Core `WakeupThread` (incl. id 0 broadcast) | Already fixed Midway WakeupThread(0) no-op class; sleep rescue covers remaining “no wake ever” cases |
| B3 post-GTFS Wakeup-only | Policy precedent: wake sleep/suspend, **never** SignalSema on RPC ids |
| BO2 / DA pure-sleeper pulses | Peer-runnable, short interval — **not** replaced by default gate |

---

## 9. Implement checklist (next turn after ACK)

1. Add `MaybeRescueGenericStarvedSleep` + counter + dict in `KernelHle.cs`.
2. Wire Ps2System ambient call; honor `DETPS2_DISABLE_M6B_SLEEP_RESCUE`.
3. Build Core; optional synthetic or SM/B3 short blocker-trace A/B.
4. Do **not** edit GameQuirks; do **not** delete Midway helpers; do **not** push.

---

## 10. Ready for implement ACK?

**Yes — design is non-trivial (gates + Suspend sticky + anti-SignalSema invariants) but fully specified.**  
Implement only after explicit ACK. Not a one-function rubber-stamp of Midway (peer gate + SoftSuspended skip + default-on kill-switch differ from title code).

---

*Design only. No Core changes in this note.*
