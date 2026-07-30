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
| CreateSema | `KernelState.CreateSema` / `CreateSemaFromStruct` | Count/max OK; **InitCount not stored**; no magic/generation ids |
| DeleteSema | `KernelState.DeleteSema` | Wakes all waiters ✓; does **not** set negative WaitSema return; **clears Sleeping even under SuspendCount** |
| SignalSema | `KernelState.SignalSema` | One waiter OR count++ OR OVF ✓; return is live count (not 0); **wake ignores SuspendCount** |
| iSignalSema | `ISignalSema` → SignalSema | Same rules ✓ |
| WaitSema / WaitSemaBlocking | `KernelState` + `SonyKernelHle` 0x44 | Count-- / park ✓; stall path when SIF RPC queued ✓ |
| PollSema | `PollSema` | Non-blocking consume ✓ |
| ReferSemaStatus | `SonyKernelHle` 0x47/0x48 | **Stub — always returns 0, writes nothing** |
| SuspendThread / ResumeThread | `KernelState` + 0x37–0x3A | Nestable SuspendCount ✓; SoftSuspended sticky for exited peers ✓ (intentional) |
| ReferThreadStatus | `SonyKernelHle` 0x30/0x31 | RUN/READY/WAIT/SUSPEND/DORMANT bits ✓ |
| WakeupThread | `KernelState.WakeupThread` | Sleep waiters ✓; WaitSema routed through **SignalSema** (not fake-clear Sleeping alone) ✓ intentional vs thrash |
| SleepThread | `SleepThread` | Always parks; **no wakeup-count** |
| CancelWakeupThread | 0x35 | **Stub** |
| WaitSema auto-create | Sony 0x44 | Calls `CreateSema(0,1)` which allocates a **new** id, then waits on the **old** id — broken |
| SaveState | `WriteState`/`ReadState` | Missing EverStarted, SoftSuspended, SuspendCount, InitCount, WakeupCount |
| Priority ready queues | — | Not ported (documented §6.4) |
| Mbx / Vpl / Fpl | — | Not ported |
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
| Round-robin instead of priority ready queues | §6.4 — large rewrite, low payoff while count/wake is correct |
| SignalSema/PollSema success return live count (not always 0) | Callers use `< 0` for errors; smokes assert count |

---

## 5. Remaining gaps for full ROMDIR THREADMAN completeness

Ordered by contract value (not game PCs):

1. **Priority ready queues** (`readyq`, ChangeThreadPriority, RotateThreadReadyQueue real semantics) — currently round-robin / SwitchToNext.
2. **Message boxes** (thmsgbx: CreateMbx / SendMbx / ReceiveMbx / PollMbx / ReferMbx) — full decomp present; zero HLE.
3. **Fixed / variable pools** (thfpool / thvpool: CreateFpl/Vpl, Allocate, Free) — decomp present; zero HLE.
4. **Event-flag wait-queue priority / multi-waiter fairness** — basic Set/Wait/Poll exists; not full thevent object model.
5. **DeleteSema / ReleaseWaitThread waiter return codes** (`0xfffffe57` deleted, `0xfffffe5e` released) — EE rarely checks; still incomplete ABI.
6. **Sema/thread generation-bit IDs** (IOP `id = ptr<<5 | gen<<1 | 1`) — EE uses flat ints; OK for EE syscall path, incomplete if real IOP THREADMAN IRX ever executes.
7. **CheckThreadStack** diagnostic (`FUN_00001cfc`, 168-byte margin) — optional, must not false-panic.
8. **DelayThread** alarm path (`FUN_00002444`) — not modeled.
9. **i-form context checks** (iSignal/iWakeup only from interrupt) — currently same as non-i.
10. **Full literal IOP THREADMAN** when/if R3000 BIOS IRX execution lands — then this HLE becomes a shim or is retired.

---

## 6. Acceptance for this slice

- Sema count vs single-waiter wake matches decomp (no double-count).
- PollSema never sleeps; DeleteSema wakes all waiters; iSignalSema shares SignalSema rules.
- Suspend nest + SoftSuspended sticky preserved; Signal/Delete wakes do not clear Suspend.
- ReferThreadStatus bits RUN/READY/WAIT/SUSPEND/DORMANT correct; ReferSemaStatus filled.
- WakeupThread does not fake-clear WaitSema without SignalSema.
- Smokes green; no commercial title hacks.
- Remaining ROMDIR THREADMAN work listed in §5.
