# EE kernel syscall surface (0x00–0x7F)

**Phase 5 · AGENT-E**  
**Authority:** [ps2sdk `ee/kernel/include/syscallnr.h`](https://github.com/ps2dev/ps2sdk/blob/master/ee/kernel/include/syscallnr.h), [ps2sdk `kernel.h`](https://github.com/ps2dev/ps2sdk/blob/master/ee/kernel/include/kernel.h), [psdevwiki EE Syscalls](https://www.psdevwiki.com/ps2/EE_Syscalls), live DetPS2 `SonyKernelHle.TryHandle`.

**Implementation:** `src/DetPS2.Core/SonyKernelHle.cs`  
**Mode:** commercial / native BIOS path (`BiosHle.SonyKernelMode`). Negative `v1` (i\* variants) is absolute-valued before dispatch (`BiosHle.HandleSyscall` + `TryHandle`).

## Status legend

| Status | Meaning |
|--------|---------|
| **Implemented** | Functional HLE sufficient for CRT0/libkernel + observed retail use |
| **Stub** | Handled (returns fixed success/error); body intentionally thin |
| **Missing** | Falls through to `default` → `Unknown++`, unhandled |
| **N/A** | Not a defined primary number in ps2sdk (gap) |

Interrupt-safe **i\*** forms that absolute to a *different* primary (e.g. `-0x1a` → `0x1a`) are listed under the absolute index.

---

## Inventory table

| # | Name (ps2sdk) | Status | Notes |
|---|---------------|--------|-------|
| 0x00 | RFU000_FullReset | Stub | Soft accept; no full EE rebuild mid-title |
| 0x01 | ResetEE | Stub | Accept `init_bitfield`; HLE peripherals already live |
| 0x02 | SetGsCrt | Implemented | Sets CRT mode + GS PMODE enable |
| 0x03 | *(gap / RFU)* | Stub | Intentional no-op |
| 0x04 | KExit / Exit | Implemented | `RequestExit` |
| 0x05 | ResumeIntrDispatch | Stub | Intentional no-op |
| 0x06 | _LoadExecPS2 | Stub | Returns **-1** (not supported under HLE) |
| 0x07 | _ExecPS2 | Stub | Returns **-1** |
| 0x08 | ResumeT3IntrDispatch | Stub | Alarm hard-timer path; soft alarms do not need it |
| 0x09 | RFU009 | Stub | Intentional no-op |
| 0x0A | AddSbusIntcHandler | Stub | Success 0 |
| 0x0B | RemoveSbusIntcHandler | Stub | Success 0 |
| 0x0C | Interrupt2Iop | Stub | Success 0 |
| 0x0D | SetVTLBRefillHandler | Implemented | Store + return previous / default sentinel |
| 0x0E | SetVCommonHandler | Implemented | Same pattern |
| 0x0F | SetVInterruptHandler | Implemented | Same pattern |
| 0x10 | AddIntcHandler | Implemented | Per-cause chain; enables `TakeExceptions`; rearms sticky STAT |
| 0x11 | RemoveIntcHandler | Implemented | Clears cause chain |
| 0x12 | AddDmacHandler | Implemented | Channel → handler map |
| 0x13 | RemoveDmacHandler | Implemented | |
| 0x14 | _EnableIntc | Implemented | INTC_MASK OR + rearm |
| 0x15 | _DisableIntc | Implemented | |
| 0x16 | _EnableDmac | Implemented | Channel IRQ + DmaController mask |
| 0x17 | _DisableDmac | Implemented | |
| 0x18 | _SetAlarm | Implemented | Soft alarm table; time = H-SYNC ticks; fires from VBlank |
| 0x19 | _ReleaseAlarm | Implemented | Returns remaining H-SYNC or -1 |
| 0x1A | (abs iEnableIntc) | Implemented | Same as 0x14 |
| 0x1B | (abs iDisableIntc) | Implemented | Same as 0x15 |
| 0x1C | (abs iEnableDmac) | Implemented | Same as 0x16 |
| 0x1D | (abs iDisableDmac) | Implemented | Same as 0x17 |
| 0x1E | (abs _iSetAlarm) | Implemented | Same as 0x18 |
| 0x1F | (abs _iReleaseAlarm) | Implemented | Same as 0x19 |
| 0x20 | CreateThread | Implemented | `ee_thread_t` parse → `KernelState` |
| 0x21 | DeleteThread | Implemented | |
| 0x22 | StartThread | Implemented | Switch-now cooperative start |
| 0x23 | ExitThread | Implemented | Permanent DORMANT + yield |
| 0x24 | ExitDeleteThread | Implemented | Same as ExitThread path |
| 0x25 | TerminateThread | Implemented | DeleteThread |
| 0x26 | (abs iTerminateThread) | Implemented | Same as 0x25 |
| 0x27 | DisableDispatchThread | Stub | **Not supported** on retail EE (ps2sdk); no-op 0 |
| 0x28 | EnableDispatchThread | Stub | Same |
| 0x29 | ChangeThreadPriority | Stub | Priority not stored in `KernelState` yet; returns 0 |
| 0x2A | (abs iChangeThreadPriority) | Stub | Same |
| 0x2B | RotateThreadReadyQueue | Implemented | `SwitchToNext` |
| 0x2C | (abs iRotateThreadReadyQueue) | Implemented | Same |
| 0x2D | ReleaseWaitThread | Implemented | Via `WakeupThread` |
| 0x2E | (abs iReleaseWaitThread) | Implemented | Same |
| 0x2F | GetThreadId | Implemented | |
| 0x30 | ReferThreadStatus | Implemented | Fills `ee_thread_status_t` RUN/READY/WAIT/SUSPEND/DORMANT |
| 0x31 | (abs iReferThreadStatus) | Implemented | Same (case 0x31 in switch) |
| 0x32 | SleepThread | Implemented | WakeupCount consume / park / yield |
| 0x33 | WakeupThread | Implemented | |
| 0x34 | (abs _iWakeupThread) | Implemented | Same |
| 0x35 | CancelWakeupThread | Implemented | Return + clear WakeupCount |
| 0x36 | (abs iCancelWakeupThread) | Implemented | Same |
| 0x37 | SuspendThread | Implemented | Nest count + self-yield / deadlock break |
| 0x38 | (abs _iSuspendThread) | Implemented | |
| 0x39 | ResumeThread | Implemented | |
| 0x3A | (abs iResumeThread) | Implemented | |
| 0x3B | **RFU059** | Stub | **Not JoinThread.** EE kernel has no JoinThread syscall. Returns 0 |
| 0x3C | SetupThread (RFU060) | Implemented | Returns SP top |
| 0x3D | SetupHeap (RFU061) | Stub | Returns 0 (CRT0 accepts; see source comment) |
| 0x3E | EndOfHeap | Implemented | Returns `HeapTop` (0x01FFF000) |
| 0x3F | *(gap)* | Missing | Not in syscallnr.h |
| 0x40 | CreateSema | Implemented | `ee_sema_t` |
| 0x41 | DeleteSema | Implemented | Wakes waiters |
| 0x42 | SignalSema | Implemented | Returns **sema id** on success |
| 0x43 | (abs iSignalSema) | Implemented | |
| 0x44 | WaitSema | Implemented | Block + RPC-aware stall / yield / fabricate |
| 0x45 | PollSema | Implemented | Returns id on success |
| 0x46 | (abs iPollSema) | Implemented | |
| 0x47 | ReferSemaStatus | Implemented | count/max/init/waiters |
| 0x48 | (abs iReferSemaStatus) | Implemented | |
| 0x49 | (abs iDeleteSema) | Missing | Falls through; rare |
| 0x4A | SetOsdConfigParam | Stub | 0 |
| 0x4B | GetOsdConfigParam | Stub | 0 |
| 0x4C | GetGsHParam | Stub | 0 |
| 0x4D | GetGsVParam | Stub | 0 |
| 0x4E | SetGsHParam | Stub | 0 |
| 0x4F | SetGsVParam | Stub | 0 |
| 0x50 | CreateEventFlag | Implemented | |
| 0x51 | DeleteEventFlag | Stub | Success 0; object not removed from table (residual) |
| 0x52 | SetEventFlag | Implemented | Wakes WaitEventFlag parkers |
| 0x53 | iSetEventFlag | Implemented | Same |
| 0x54–0x58 | **See ABI note** | Implemented* | DetPS2: Clear/Wait/Poll EventFlag. ps2sdk: TLB / xlaunch — **DetPS2 keeps event-flag path** (load-bearing) |
| 0x59 | ExpandScratchPad | Stub | No TLB remap; return 0 |
| 0x5A | Copy | Implemented | Best-effort memcpy; may be SetSyscall-hooked |
| 0x5B | GetEntryAddress | Implemented | Stub trampoline pool |
| 0x5C | EnableIntcHandler | Stub | Always-on once registered |
| 0x5D | DisableIntcHandler | Stub | Chain kept |
| 0x5E | EnableDmacHandler | Stub | |
| 0x5F | DisableDmacHandler | Stub | |
| 0x60 | KSeg0 | Stub | 0 |
| 0x61 | EnableCache | Stub | 0 |
| 0x62 | DisableCache | Stub | 0 |
| 0x63 | GetCop0 | Implemented | `ReadCop0Public` |
| 0x64 | FlushCache | Stub | Accept any op |
| 0x65 | *(gap)* | Missing | Not in syscallnr.h |
| 0x66 | CpuConfig | Stub | 0 |
| 0x67 | (abs iGetCop0) | Implemented | Same as 0x63 |
| 0x68 | (abs iFlushCache) | Stub | 0 |
| 0x69 | RFU105 | Stub | Intentional no-op |
| 0x6A | (abs iCpuConfig) | Stub | 0 |
| 0x6B | sceSifStopDma | Stub | 0 |
| 0x6C | SetCPUTimerHandler | Stub | COP0 Compare timer not fired yet |
| 0x6D | SetCPUTimer | Stub | Same residual |
| 0x6E | SetOsdConfigParam2 | Stub | 0 |
| 0x6F | GetOsdConfigParam2 | Stub | 0 |
| 0x70 | GsGetIMR | Implemented | Shadow + GS |
| 0x71 | GsPutIMR | Implemented | |
| 0x72 | SetPgifHandler | Stub | 0 (grouped with SetVSyncFlag historically; **0x72 alone is stub**) |
| 0x73 | SetVSyncFlag | Implemented | Pointers written every VBlank |
| 0x74 | SetSyscall | Implemented | Custom hook table; return previous |
| 0x75 | _print | Stub | 0 (InitDebug not required) |
| 0x76 | sceSifDmaStat | Implemented | Always -1 (completed/idle) |
| 0x77 | sceSifSetDma | Implemented | Real RPC + SIFCMD HLE |
| 0x78 | sceSifSetDChain | Stub | 0 |
| 0x79 | sceSifSetReg | Implemented | Hardware + virtual 0x80000000 namespace; SMFLAG W1C |
| 0x7A | sceSifGetReg | Implemented | SMFLAG / SUBADDR / reboot complete |
| 0x7B | _ExecOSD | Stub | Returns **-1** |
| 0x7C | Deci2Call | Implemented | Open/Send/Poll/kPuts sub-dispatch |
| 0x7D | PSMode | Stub | 0 |
| 0x7E | MachineType | Stub | 0 (consumer) |
| 0x7F | GetMemorySize | Implemented | `RDRAM_SIZE` |

### Extended (above 0x7F, still in switch)

| # | Name | Status | Notes |
|---|------|--------|-------|
| 0x80 | _GetGsDxDyOffset | Stub | 0 |
| 0x82 | _InitTLB | Stub | 0 |
| 0x83 | FindAddress | Implemented | Memory scan + Midway CRT0 plant |
| 0x85 | SetMemoryMode | Stub | DESR; 0 |
| 0x86 | GetMemoryMode | Stub | 0 |
| 0x87 | ExecPSX | Stub | -1 |
| 0xFC | SetAlarm | Implemented | Public number (same as 0x18) |
| 0xFD | (abs iSetAlarm) | Implemented | |
| 0xFE | ReleaseAlarm | Implemented | |
| 0xFF | (abs iReleaseAlarm) | Implemented | |

---

## ABI note: 0x54–0x58 EventFlag vs TLB

ps2sdk `syscallnr.h` maps:

- `0x54` xlaunch, `0x55` PutTLBEntry, `0x56` _SetTLBEntry, `0x57` GetTLBEntry, `0x58` ProbeTLBEntry

DetPS2 historically (and currently) maps these to **ClearEventFlag / WaitEventFlag / PollEventFlag** family, matching observed commercial / SN ProDG wait patterns already green in smokes. **Do not renumber** without a multi-title audit. TLB helpers remain residual (InitTLB stub only).

---

## Alarm contract (new in Phase 5)

| Item | Behavior |
|------|----------|
| API | `SetAlarm(u16 time, cb, common)` / `ReleaseAlarm(id)` |
| Time unit | H-SYNC ticks (ps2sdk `kernel.h`) |
| Table | 64 slots (`MAX_ALARMS`) |
| Advance | Each `OnVblankTick`: subtract **262** H-SYNC (NTSC field approx) |
| Fire | `void cb(s32 id, u16 time, void *common)` mini-run (≤512 EE steps) |
| Release | Returns remaining H-SYNC; -1 if id missing |
| Residual | Not wired to real INTC Timer3 / H-SYNC edge; soft VBlank quanta only |

Smokes: `SonyKernelHle_SetAlarmReleaseAndFire`, `SonyKernelHle_Rfu059AndIEnableIntc`.

---

## Intentional no-ops (platform / HLE)

Documented stubs that **must not** be “filled” with fake hardware unless a title proves need:

- Disable/EnableDispatchThread (unsupported on EE)
- RFU009, RFU059, RFU105, ResumeIntrDispatch, ResumeT3IntrDispatch
- Cache enable/disable / KSeg0 / CpuConfig (host has no EE cache)
- OSD config param get/set (no OSDSYS dependency for G0)
- SetCPUTimer* (no COP0 Compare fire path)
- LoadExecPS2 / ExecPS2 / ExecOSD / ExecPSX (return failure)
- SBus INTC handlers / Interrupt2Iop (no SBUS guest)
- Enable/Disable Intc/Dmac **handler** flags (registration implies armed)

---

## Residual list (post Phase 5)

1. **ChangeThreadPriority** — needs `Thread.Priority` in `KernelState` (Phase 1 THREADMAN ownership).
2. **DeleteEventFlag** — remove object + wake waiters (KernelState API).
3. **iDeleteSema (0x49 after abs)** — wire to DeleteSema.
4. **EventFlag vs TLB dual ABI** — optional SetSyscall / ROMVER branch if a title hits PutTLBEntry at 0x55.
5. **SetCPUTimer / Compare IRQ** — hard EE timer path.
6. **Alarm** — H-SYNC-accurate Timer3 instead of 262/VBlank quanta; optional queue callbacks without nested `EE.Step`.
7. **JoinThread** — **does not exist** on EE; IOP THREADMAN only. Callers must use Exit + poll / event flags.
8. **Mbx / Vpl / Fpl** — Phase 1; EE has no CreateMbx syscalls in this range.
9. **SetupHeap** return value / newlib heap base — previously A/B tested; left stub.
10. **0x3F / 0x65** gaps — remain Missing unless a ROM defines them.

---

## Summary counts (primary 0x00–0x7F)

| Status | Approx count |
|--------|----------------|
| Implemented | ~70 (incl. i\* aliases sharing bodies) |
| Stub (handled no-op / fixed return) | ~35 |
| Missing (default) | 0x3F, 0x49, 0x65 (+ any undefined) |

CRT0 / libkernel critical path (**Alarm, RFU059, INTC/DMAC i\*, SetupThread/Heap/EndOfHeap, threads/semas, SIF, FlushCache, GetMemorySize**) is covered for G0 BIOS-core.
