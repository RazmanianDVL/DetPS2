# THREADMAN — gap analysis (DetPS2 HLE vs Ghidra)

**Authority:** `tools/bios-decomp/THREADMAN_ALL.txt` (IOP Multi_Thread_Manager / IOP Realtime Kernel Ver.0.9.1, 80 functions), `docs/BIOS_DISSECTION.md` §4 + §6.4, `KernelHle.KernelState`, `SonyKernelHle` syscalls 0x20–0x48.

**Scope rule:** generic EE/IOP THREADMAN contracts only. No per-game assists, no Midway PC patches.

**Architecture note (BIOS_DISSECTION §6.4):** DetPS2’s `KernelState` is a working **contract-level** HLE (round-robin + sema count/waiter flags), **not** a literal port of IOP priority ready-queues / Mbx / Vpl / Fpl. This document tracks contract parity, not a full IRX rewrite.

---

## 1. Decomp map (thsemap / core thread waits)

| Decomp | Role | Count / wake rule |
|--------|------|-------------------|
| `FUN_00003060` | **CreateSema** | Magic `0x7f02`; `count=init`, `max=max`, init saved at `+0x28`; attr/option stored |
| `FUN_00003164` | **DeleteSema** | Walk wait queue; each waiter return `0xfffffe57`; mark READY(2); free object |
| `FUN_0000328c` | **SignalSema** | If waiters (`+0x10≠0`): dequeue **one**, READY(2), **no** count++; else if `count < max`: count++; else `0xfffffe5c` (OVF) |
| `FUN_00003374` | **iSignalSema** | Same count/wake; interrupt-context entry |
| `FUN_00003444` | **WaitSema** | If `count < 1`: state WAIT(4), wait-type SEMA(3), enqueue, yield; else count--, return 0 |
| `FUN_000035b4` | **PollSema** | If `count < 1`: `0xfffffe5d` (no sleep); else count--, return 0 |
| `FUN_0000365c` / `036a4` / `0373c` | **ReferSemaStatus** | attr, option, init, max, **current count**, **num waiters** |
| `FUN_0000200c` | **SleepThread** | If wakeup-count `+0x1e > 0`: decrement, return 0; else WAIT(4) type SLEEP(1), yield |
| `FUN_000020e4` / `02204` | **WakeupThread** / **iWakeup** | Only wakes WAIT+type==SLEEP; else **increments** wakeup-count. **Does not** clear WaitSema |
| `FUN_000022dc` / `02378` | **CancelWakeupThread** | Returns old wakeup-count, clears to 0 |
| `FUN_00001da8` / `01f00` | **ReferThreadStatus** | status byte at `+0xc` (1 RUN / 2 READY / 4 WAIT / 0x10 DORMANT) + wait type/id |
| `FUN_00001cfc` | **CheckThreadStack** | SP vs stack-base margin 0xA8 — panic path |

Thread status bits used by **EE** kernel (ps2sdk, written by DetPS2 `ReferThreadStatus`):

| Bit | Name | Meaning |
|-----|------|---------|
| 0x01 | THS_RUN | Currently running |
| 0x02 | THS_READY | Runnable |
| 0x04 | THS_WAIT | Sleeping / WaitSema / WaitVblank / event flag |
| 0x08 | THS_SUSPEND | Suspend nest / SoftSuspended |
| 0x10 | THS_DORMANT | Never-started or ExitThread’d |

Combinable: WAIT\|SUSPEND = 0x0C.

---

## 2. Current DetPS2 surface

| API | Location | Status vs decomp |
|-----|----------|------------------|
| CreateSema | `KernelState.CreateSema` / `CreateSemaFromStruct` | Count/max/InitCount ✓; no magic/generation ids (EE flat ints) |
| DeleteSema | `KernelState.DeleteSema` | Wakes all waiters ✓; waiter return `KeWaitDelete` (`0xfffffe57`) ✓; Suspend nest preserved ✓ |
| SignalSema | `KernelState.SignalSema` | One waiter OR count++ OR OVF ✓; Suspend nest preserved ✓ |
| iSignalSema | `ISignalSema` → SignalSema | Same rules ✓ |
| WaitSema / WaitSemaBlocking | `KernelState` + `SonyKernelHle` 0x44 | Count-- / park ✓; stall path when SIF RPC queued ✓ |
| PollSema | `PollSema` | Non-blocking consume ✓ |
| ReferSemaStatus | `SonyKernelHle` 0x47/0x48 | Fills ee_sema_t (count/max/init/waiters) ✓ |
| SuspendThread / ResumeThread | `KernelState` + 0x37–0x3A | Nestable SuspendCount ✓; SoftSuspended sticky for exited peers ✓ (intentional) |
| ReferThreadStatus | `SonyKernelHle` 0x30/0x31 | RUN/READY/WAIT/SUSPEND/DORMANT + priority + wakeupCount ✓ |
| WakeupThread | `KernelState.WakeupThread` | Sleep waiters ✓; WaitSema routed through **SignalSema** ✓ intentional |
| SleepThread | `SleepThread` | Wakeup-count consume without park ✓ |
| CancelWakeupThread | 0x35 | Return+clear wakeup count ✓ |
| WaitSema auto-create | Sony 0x44 | `EnsureSema(id)` materializes requested id ✓ |
| SaveState | `WriteState`/`ReadState` | Threads/semas/flags/Mbx/Vpl/Fpl + priority/delay/wait returns ✓ |
| Priority ready selection | `FindNextRunnable` / `ChangeThreadPriority` (0x29) | Lower priority value runs first; RR within band ✓ |
| Message boxes | `KernelState` Create/Delete/Send/Receive/Poll/ReferMbx | Contract HLE ✓ (host API; no EE syscall) |
| Variable pools | `KernelState` Create/Delete/Allocate/Free/ReferVpl | Host freelist + synthetic cookies ✓ |
| Fixed pools | `KernelState` Create/Delete/Allocate/Free/ReferFpl | Fixed block freelist ✓ |
| DelayThread | `KernelState.DelayThread` + `TickDelays` / `OnVblank` | Alarm-style park ✓ (host API; no EE syscall) |
| ReleaseWaitThread | 0x2D + `KernelState.ReleaseWaitThread` | `KeReleaseWait` (`0xfffffe5e`) ✓ |
| CheckThreadStack | — | Not ported (diagnostic candidate only) |

---

## 3. Bugs fixed in this agent pass

1. **SignalSema / DeleteSema wake must honor SuspendCount**  
   Decomp marks READY, but EE HLE models Suspend as an independent nest. Clearing `Sleeping` while `SuspendCount > 0` falsely makes a SUSPENDed peer runnable and thrash-resumes workers that games intended to stay parked. Mirror existing `WakeupThread` / `OnVblank` guards.

2. **WaitSema auto-create must materialize the requested id**  
   `CreateSema(0,1)` returns `_nextSema++`, not `a0`. Subsequent `WaitSemaBlocking(a0)` still misses → returns −1 with `LastWaitSemaBlocked=false` → Sony path treats as success. Add `EnsureSema(id, init, max)`.

3. **ReferSemaStatus must fill ee_sema_t**  
   From decomp `FUN_0000365c` + ps2sdk layout: count, max_count, init_count, wait_threads (+ attr/option 0). Track `InitCount` and count waiters with `WaitSemaId == id`.

4. **SleepThread / WakeupThread / CancelWakeupThread wakeup-count**  
   Decomp `+0x1e` counter: Wakeup while not SLEEP-waiting increments; Sleep consumes one pending wake without parking. Cancel returns-and-clears. Required for pure SleepThread rendezvous without WaitSema.

5. **SaveState completeness** for SoftSuspended / EverStarted / SuspendCount / WakeupCount / Sema.InitCount so mid-boot snapshots do not lose THREADMAN park state.

6. **Smoke coverage** for the above contracts (no game PCs).

---

## 4. Intentional HLE divergences (keep)

| Divergence | Why |
|------------|-----|
| SoftSuspended sticky on exited (EverStarted && !Started) peers | Prevents Suspend/Resume thrash on ExitThread’d workers; documented on `KernelState.Thread` |
| WakeupThread(WaitSema waiter) → SignalSema | Real IOP only bumps wakeup-count; EE games that Refer WAIT then Wakeup need a real WaitSema release (not half-clear Sleeping). SignalSema is the correct release path |
| WakeupThread(0) wakes pure Sleep waiters | Primordial EE main is often addressed as id 0; real kernel would error |
| WaitSema fabricates VBlank park when nothing runnable and no RPC | Avoids whole-EE deadlock under incomplete producers |
| Priority selection without full multi-band readyq lists | `FindNextRunnable` picks min priority then RR within band — contract equivalent for EE cooperative + preemption |
| SignalSema/PollSema success return live count (not always 0) | Callers use `< 0` for errors; smokes assert count; Sony path returns id for SN ProDG |
| Mbx/Vpl/Fpl/DelayThread as host `KernelState` API | **No EE syscalls** in ps2sdk `kernel.h` for these (IOP thmsgbx/thvpool/thfpool exports only). Documented public API for IOP HLE / tests |
| Vpl/Fpl synthetic pointer cookies (`0x0E000000+`) | Contract freelist without mapping real RDRAM; Free matches cookies only |
| DelayThread advanced by `TickDelays` / ~16667 µs per `OnVblank` | No real TIMEMAN hard-timer coupling yet; sufficient for contract + smokes |

---

## 5. Remaining gaps (post Phase-1 completion)

Ordered by residual value:

1. ~~**Priority ready selection**~~ — **done** (min-priority + RR band; full multi-list readyq optional polish).
2. ~~**Message boxes**~~ — **done** (Create/Delete/Send/Receive/Poll/Refer).
3. ~~**Fixed / variable pools**~~ — **done** (Create/Delete/Allocate/Free/Refer + park-on-empty).
4. **Event-flag wait-queue priority / multi-waiter fairness** — basic Set/Wait/Poll exists; not full thevent object model.
5. ~~**DeleteSema / ReleaseWaitThread waiter return codes**~~ — **done** (`KeWaitDelete` / `KeReleaseWait` + `$v0` patch on restore).
6. **Sema/thread generation-bit IDs** (IOP `id = ptr<<5 | gen<<1 | 1`) — EE uses flat ints; OK for EE syscall path, incomplete if real IOP THREADMAN IRX ever executes.
7. **CheckThreadStack** diagnostic (`FUN_00001cfc`, 168-byte margin) — optional, must not false-panic.
8. ~~**DelayThread** alarm path~~ — **done** (`DelayThread` + `TickDelays` / `OnVblank`).
9. **i-form context checks** (iSignal/iWakeup only from interrupt) — currently same as non-i.
10. **Full literal IOP THREADMAN** when/if R3000 BIOS IRX execution lands — then this HLE becomes a shim or is retired.
11. **RotateThreadReadyQueue(priority)** full band-only rotate — currently yields via `SwitchToNext` (priority-aware).
12. **Vpl/Fpl real IOP heap backing** — synthetic cookies until SYSMEM/heap coupling needed.

---

## 6. Acceptance for Phase 1 (THREADMAN completion slice)

- Sema count vs single-waiter wake matches decomp (no double-count).
- PollSema never sleeps; DeleteSema wakes all waiters with `0xfffffe57`; iSignalSema shares SignalSema rules.
- Suspend nest + SoftSuspended sticky preserved; Signal/Delete wakes do not clear Suspend.
- ReferThreadStatus bits RUN/READY/WAIT/SUSPEND/DORMANT + priority/wakeupCount correct; ReferSemaStatus filled.
- WakeupThread does not fake-clear WaitSema without SignalSema.
- Mbx/Vpl/Fpl host APIs + smokes green.
- Priority-aware SwitchToNext; DelayThread tick path; ReleaseWaitThread `0xfffffe5e`.
- Smokes: `KernelHle_ThreadmanMbxVplFpl`, `KernelHle_ThreadmanPriorityAndDelay`, `KernelHle_ThreadmanReleaseWaitAndDeleteSemaCodes` (+ prior Sleep/Sema smokes).
- No commercial title hacks / GameQuirks.
- Remaining residual listed in §5 (items 4, 6, 7, 9–12).

## 7. EE syscall wiring note

| Feature | EE syscall (ps2sdk) | DetPS2 |
|---------|---------------------|--------|
| Threads / Semas / EventFlags | 0x20–0x58 family | `SonyKernelHle` ✓ |
| ChangeThreadPriority | 0x29 / 0x2A | ✓ stores priority |
| ReleaseWaitThread | 0x2D / 0x2E | ✓ `KeReleaseWait` |
| CreateMbx / Vpl / Fpl / DelayThread | **none on EE** | `KernelState` public API only |
