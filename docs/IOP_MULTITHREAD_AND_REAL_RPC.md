# IOP multi-thread contexts + real `sceSifRegisterRpc` dual-path

**Status:** design only (no Core `.cs` changes in this doc’s authoring pass)  
**Audience:** T1 (IOP core) / T2 (module runtime) / T4 (SIF bridge)  
**Related:** `docs/DEVELOPER_GUIDE.md` §5.3–5.4, `docs/irx/MODULE_RUNTIME.md`, `docs/bios-ports/THREADMAN.md`, `docs/TITLE_HACKS.md` (Real RPC dispatch), `docs/IRX_EXECUTION_PHASE_PLAN.md`  
**Locks:** **do not** edit `Intc.cs` or `EmotionEngine.cs` for this work (A2-owned / frozen).

---

## 1. Current reality

| Piece | State |
|-------|--------|
| **`Iop.cs`** | Real **R3000A** interpreter: single flat register file (`PC` + 32 GPRs + LO/HI + minimal COP0). `Step` / `RunInstructions` retire real insns; AdEL on unknown fetch; exception vector at phys `0x80` can be overwritten by live `EXCEPMAN`/`INTRMAN*`. |
| **`LoadIrx` / `IopModuleHost`** | Places + links real ELF IRX into IOP RAM; HLE `StartModule` flips registry state; **`StartLoadedModule` / pending-literal FIFO** can run real **`_start`** (PC/GP/RA sentinel/SP). Boot path: `BiosBootHost.BootIopBtConfLiteral`. Disc path: `RealSifRpc` MOD_LOAD → `LoadIrx` + `TryStartLoadedModule`. |
| **Placement / queue bugs** | Same-name reload slot reuse + multi-load pending-entry FIFO fixed (2026-08-03). Not the remaining gap. |
| **Remaining gap A — multi-thread contexts** | Real modules (e.g. disc `IOPFILE.IRX`) **spawn worker threads and yield** inside `_start` rather than returning under a single-context budget. One GPR set cannot preserve `$ra`/`$sp`/callee-saves across THREADMAN-style switches. Larger `_start` budgets do **not** fix this (`DETPS2_LOADFILE_START_INSNS` 100× still no `sceSifRegisterRpc`). |
| **Remaining gap B — RealSifRpc dual-path vs real register** | `HandleCall` already **prefers** a live IOP registry walk (`TryFindRealRpcServer` → `TryDispatchRealRegisteredRpc`) before per-sid HLE. In practice the live table is often empty because module `_start` never reaches `sceSifSetRpcQueue` / `sceSifRegisterRpc`. HLE remains the path that keeps boots alive. Opt-out today: `DETPS2_NO_REAL_RPC=1`; trace: `DETPS2_TRACE_REALRPC=1`. |

North star (unchanged): **IRX is the product**; C# owns machines/devices; HLE is fallback debt, not a second OS.

```text
EE  →  SIF  →  IOP R3000 (multi-thread contexts + real registered handlers)
                    ↓ miss / flag off
              RealSifRpc per-sid HLE
```

---

## 2. First vertical slice — per-thread GPRs / PC / SP (THREADMAN-shaped)

**Goal:** enough cooperative multi-context on the IOP core that a real `_start` can create a worker, yield, and resume without corrupting the parent context — so registration and long-lived RPC loops can complete.

### Shape (minimal, not a full decomp port)

Mirror the **contract** already documented for THREADMAN / `KernelState` on the EE side, scoped to **IOP R3000 only**:

| Field | Notes |
|-------|--------|
| Thread id | Dense small table (e.g. 32–64 slots), id 0 = boot / current single-context behavior |
| State | RUN / READY / WAIT / DORMANT (bits match ps2sdk-style status where cheap) |
| **Saved PC** | Resume address after yield / switch |
| **Saved GPRs** | Full 32 words (or 1–31 if r0 forced zero on restore) |
| **Saved SP** | Explicit; stacks must not all share `DefaultModuleStack` forever |
| Optional later | LO/HI, COP0 subset, wait-object id (sema/event) — only when a live IRX needs it |

**Scheduler policy (v1):** round-robin among READY threads on IOP quantum boundaries and on explicit yield/sleep hooks already hit by real IRX (e.g. WaitSema / SleepThread via existing IOP syscall / import stubs). No priority queues in the first slice unless a smoke proves them required.

**Integration points:**

1. **`Iop`**: “current” context is still the active PC/GPR arrays used by the decode loop; switch = save active → load target. Savestate must dump the whole table (extend existing GPR/PC write path).
2. **`IopModuleHost.PrepareModuleEntry` / `StartLoadedModule`**: create or bind a thread for each literal `_start` with a **unique stack**; stop assuming one shared stack zero-wipe is safe for concurrent residents (today’s THREADMAN-only rotating slot is a stopgap, not the model).
3. **No EE THREADMAN rewrite** — this is IOP register context only. EE `KernelState` stays as-is.

### Feature flag

| Env | Behavior |
|-----|----------|
| **`DETPS2_IOP_THREADS=1`** | Enable multi-context save/restore + READY RR (or equivalent) |
| unset / `0` | **Default:** single flat context (today’s behavior) for determinism bisect |

Exit criteria for the slice:

- Synthetic IRX: parent `_start` creates worker, yields, worker runs, parent resumes with intact `$s*` / `$ra`.
- At least one real disc module that previously stalled before registerRpc advances past that point under the flag (metric: non-zero queue chain / server list, or `[REALRPC]` hit).
- Flag-off telemetry byte-identical to pre-change baseline on a short fleet smoke.

---

## 3. Dual registry — prefer real `sceSifRegisterRpc` table over HLE

Already partially implemented; this work **completes the preference chain** once threads make registration real:

1. **Primary:** walk live SIFCMD queue chain → `SifRpcServerData_t` list (`sid` / `func` / `buff` / `next`), ground-truthed layouts in `RealSifRpc` (`TryFindRealRpcServer`). Handler address must fall inside a loaded module image.
2. **Dispatch:** run handler on IOP R3000 with request DMA’d into registered buff; reply from handler return / buff (`TryDispatchRealRegisteredRpc`). Context save/restore around mid-quantum call **must compose with multi-thread** (dispatch may run on the RPC worker thread once THREADMAN is live).
3. **Fallback:** existing per-sid HLE in `RealSifRpc.HandleCall` when no live server, handler not in-image, or real path disabled.

Do **not** invent a second C# sid table that shadows the live list. HLE remains keyed by sid only as debt when the real table has no entry.

---

## 4. A/B strategy — `DETPS2_IOP_REAL_RPC=1`

| Mode | Env | Behavior |
|------|-----|----------|
| **A — prefer real** | `DETPS2_IOP_REAL_RPC=1` | Always attempt live registry + R3000 handler first; HLE only on miss / unsafe handler |
| **B — HLE-first (bisect)** | unset / `0`, or force | Skip real walk (or treat as miss); existing HLE only |

**Compatibility with current knobs:**

- Today’s `DETPS2_NO_REAL_RPC=1` is an opt-**out** of the already-wired prefer-real path. Design target is a clear **opt-in A** flag for experiments once multi-thread lands; implementors should map or supersede `DETPS2_NO_REAL_RPC` so there is one documented switch (prefer: `DETPS2_IOP_REAL_RPC` primary, keep `NO_REAL_RPC` as alias for HLE-only during transition).
- Trace: keep / extend `DETPS2_TRACE_REALRPC=1` (hit/miss, sid, func, module name).
- Pair with `DETPS2_IOP_THREADS=1` for meaningful A runs; real RPC without threads will still often miss.

**A/B procedure:** same title, same cycle budget, flag 0 vs 1; compare boot progress (binds/calls, realrpc hits, module `EntryExecuted`, crash absence). Prefer real only when A ≥ B and no new stalls.

---

## 5. Non-goals

- **No new `GameQuirks` / title assists** for “make this IRX register” or fake sid replies. Fix IOP exec + dual-path; quirks stay last resort under existing SOP.
- **No per-title RPC protocol reverse-engineering** as the primary plan once real handlers can run.
- **No full THREADMAN decomp port** (priority ready-queues, Mbx/Vpl/Fpl, stack-overflow panic) in this slice — contract-level multi-context only.
- **No EE kernel rewrite**, no Soft-GS / GIF changes.
- **No `Intc.cs` / `EmotionEngine.cs` edits** (A2 lock). IOP IRQs that already work stay; new interrupt policy is out of scope unless already owned by `Iop.cs` alone.
- **No plant waves** or host-decoded assets.

---

## 6. File touch list

| File / area | Role | Notes |
|-------------|------|--------|
| **`src/DetPS2.Core/Iop.cs`** | Multi-context store, switch, savestate | Primary T1 surface |
| **`src/DetPS2.Core/IrxLoader.cs`** | Only if load metadata must tag entry thread / stack | Prefer minimal |
| **`IopModuleHost` / `LoadedIrx` (today in `SifRpc.cs`)** | Per-module entry thread, unique SP, literal start + resume | T2 |
| **`src/DetPS2.Core/RealSifRpc.cs`** | Dual-path flag wiring, compose dispatch with thread contexts | Touch only as needed; one owner in multi-agent waves |
| Tests (`Tests/IopExecSmokes.cs` or adjacent) | Synthetic multi-thread + realrpc prefer smoke | |
| This doc / `DEVELOPER_GUIDE` §5.3 cross-link | After impl | |

### Explicitly out of touch

| File | Why |
|------|-----|
| **`Intc.cs`** | Locked for A2 |
| **`EmotionEngine.cs`** | Locked for A2 |
| `GameQuirks/*`, title assists | Non-goals |
| Soft-GS / VIF / GIF / present | Unrelated tracks |

---

## 7. Suggested implementation order

1. Flag-gated **thread context table** in `Iop` (save/restore only; still single-threaded if only one READY).
2. **Unique stacks** + entry binds from `PrepareModuleEntry` / `StartLoadedModule`.
3. Yield hooks (minimal) so a waiting `_start` parks and another READY runs within the same IOP quantum budget.
4. Wire **`DETPS2_IOP_REAL_RPC`** preference + metrics; keep HLE fallback.
5. Fleet A/B smoke; only then expand wait-object fidelity.

---

## 8. Success definition (done enough to merge)

- `DETPS2_IOP_THREADS=0` + real-RPC prefer off: no behavior change vs baseline.
- `DETPS2_IOP_THREADS=1`: multi-context smoke green; at least one commercial path shows real registry growth or real handler dispatch under `DETPS2_IOP_REAL_RPC=1`.
- HLE fallback still boots the 9-title roster when real path misses.
- Zero edits to `Intc.cs` / `EmotionEngine.cs`.
