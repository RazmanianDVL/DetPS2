# VBLANK + INTRMAN — gap analysis (DetPS2 HLE vs BIOS)

**Authority:** `tools/bios-decomp/VBLANK_ALL.txt` (SCPH70008 `Vblank_service` v1.1, 26 functions), export table in `tools/bios-extract/VBLANK.bin`, open-source recreation `ps2sdk/iop/system/vblank/src/vblank.c` (SCE SDK 1.3.4-based, matches decomp), `docs/BIOS_DISSECTION.md` §5 + §7. INTRMAN: export table + KE_* immediates in `tools/bios-extract/INTRMANP.bin` / `INTRMANI.bin` (lib `intrman` v1.2, 32 exports) — **no full Ghidra dump yet**; contracts grounded in binary error codes + `ps2sdk/iop/system/intrman/include/intrman.h`.

**Scope rule:** generic BIOS HLE only. No MidwayBootAssist, no title PCs, no commercial game frame-pacing hacks.

**Architecture note:** EE `Intc` cause 2 (`VBlankStart`) is **not** IOP VBLANK.IRX. Games that `AddIntcHandler(2, …)` or busy-poll `INTC_STAT` bit 2 use EE KERNEL + PCRTC. IOP VBLANK still matters for CDVDFSV / FILEIO / drivers that `WaitEventFlag` on the service event flag.

---

## 1. VBLANK decomp map

| Decomp / export | Role | Result codes |
|-----------------|------|--------------|
| `FUN_00000164` export[8] **RegisterVblankHandler**(which, priority, cb, arg) | which 0=start list, 1=end; insert by priority (lower first); 16-slot shared free pool | `0` / `-100` ILLEGAL_CONTEXT / `-104` FOUND_HANDLER / `-400` NO_MEMORY |
| `FUN_000002ac` export[9] **ReleaseVblankHandler**(which, cb) | Unlink by callback pointer | `0` / `-100` / `-105` NOTFOUND_HANDLER |
| `FUN_00000374` irq VBLANK (IRQ 0) | Walk start list; `callback(arg)`; return 0 → free node; first frame sets system status bit `0x200` | returns 1 |
| `FUN_0000042c` irq EVBLANK (IRQ 11) | Walk end list (same auto-free rule) | returns 1 |
| `FUN_000004b4` base start handler | `iSetEventFlag(START\|VBLANK)` then `iClearEventFlag(~(START\|NON))` → residual **START** | returns 1 |
| `FUN_000004fc` base end handler | `iSetEventFlag(END\|NON)` then `iClearEventFlag(~(VBLANK\|END))` → residual **END** | returns 1 |
| export[4–7] WaitVblank{Start,End,} / WaitNonVblank | `WaitEventFlag(ef, bit, WEF_OR, NULL)` | blocks |
| `_start` | CreateEventFlag; Register base handlers prio 128; `RegisterIntrHandler(0/11)`; `EnableIntr(0/11)` | — |

### Event-flag bits (real)

| Bit | Name | When pulsed |
|-----|------|-------------|
| `0x1` | EF_VBLANK_START | Start edge (then residual after clear) |
| `0x2` | EF_VBLANK | Start edge (cleared by residual mask) |
| `0x4` | EF_VBLANK_END | End edge (then residual after clear) |
| `0x8` | EF_NON_VBLANK | End edge (cleared by residual mask) |

`ClearEventFlag(ef, bits)` semantics (THREADMAN / `KernelState`): `curr &= ~bits`. So `Clear(~9)` keeps only bits in `9` (START|NON).

---

## 2. INTRMAN contract map (no full decomp)

Export table `intrman` 1.2 (INTRMANP ordinals, high value for HLE):

| Ord | API | HLE surface |
|-----|-----|-------------|
| 4 | `RegisterIntrHandler(irq, mode, handler, arg)` | One handler per IRQ; `KE_FOUND_HANDLER` if occupied; `KE_ILLEGAL_INTRCODE` if irq &gt; 0x3F |
| 5 | `ReleaseIntrHandler(irq)` | Clear slot; `KE_NOTFOUND_HANDLER` if empty |
| 6 | `EnableIntr(irq)` | Unmask line |
| 7 | `DisableIntr(irq, int *res)` | Mask line |
| 8/9 | `CpuDisableIntr` / `CpuEnableIntr` | Global IE force |
| 17/18 | `CpuSuspendIntr` / `CpuResumeIntr` | Nestable suspend depth |
| 15/16 | `DisableDispatchIntr` / `EnableDispatchIntr` | Soft dispatch mask (DECI2) |
| 23 | `QueryIntrContext` | 0 thread / 1 IRQ |

**KE_* grounded in INTRMANP binary** (addiu immediates): `-100` ILLEGAL_CONTEXT, `-101` ILLEGAL_INTRCODE, `-102` CPUDI, `-103` INTRDISABLE, `-104` FOUND_HANDLER, `-105` NOTFOUND_HANDLER.

IOP IRQs used by VBLANK: `0` = VBLANK, `11` = EVBLANK (`ps2sdk iop_irq_list`).

---

## 3. Current DetPS2 surface

| API | Location | Status vs decomp |
|-----|----------|------------------|
| Register / Unregister | `IopVblankHost` | Priority order, duplicate, 16-slot pool, ILLEGAL_CONTEXT ✓ |
| Dispatch start/end + base EF pulse | `IopVblankHost.DispatchStart/End` via `BiosHle.OnVblank` | Residual START then END bits ✓ |
| WaitVblank* | — | Not exported as HLE methods; waiters use `KernelState` EF on `EventFlagId` |
| Callback return-0 auto-free | — | **Not ported** (no R3000 callback exec) |
| System status flag `0x200` on first start | — | **Not ported** |
| RegisterIntrHandler / Release / Enable / Disable | `IopSystemHost` | One-per-IRQ + KE_* + ILLEGAL_CONTEXT ✓ |
| Query status (handler/mode/arg/pending/enable/dispatch) | `IopSystemHost` | `QueryIntrStatus` + getters ✓ |
| RaiseIntr / AcknowledgeIntr pending | `IopSystemHost` | Pending latch + clear ✓ |
| CpuSuspend/Resume/Enable/Disable | `IopSystemHost` | Nestable ✓ |
| OnVblankIrqPulse (IRQ 0/11) | `BiosHle.OnVblank` → `IopSystemHost` | Bookkeeping raise + pending when enabled ✓ |
| Boot plants VBLANK IRQs | `BiosBootHost.FinishIopServices` | Handlers + EnableIntr for 0/11 ✓ |
| TIMEMAN clock / SetAlarm | `IopSystemHost.Tick` / `SetAlarm` | Synthetic + hard-timer table ✓ |
| EE INTC sticky VBlankStart | `Intc` + `Pcrtc` + `EmotionEngine` | Sticky STAT + CpuLatched + hold window ✓ |
| EXCEPMAN | `IopExcepManHost` | Separate module — do not regress |

---

## 4. Landed (waves + Phase 2 deepen)

1. **Correct EF bit layout** — was conflating END with bit `0x2`; real END is `0x4`, VBLANK combined is `0x2`.
2. **Base-handler residual clear** after each edge (matches `FUN_000004b4` / `004fc`).
3. **16-slot free pool**, priority insert, KE_FOUND / NOTFOUND / NO_MEMORY / ILLEGAL_CONTEXT.
4. **INTRMAN one-handler-per-IRQ**, illegal irq, enable/disable, acknowledge/raise bookkeeping, CpuSuspend nest, dispatch soft-mask.
5. **Boot-time RegisterIntrHandler(0/11)+EnableIntr** so commercial IOP bring-up matches VBLANK._start.
6. **`KernelState.SetEventFlag` wakes WaitEventFlag waiters** so IOP producers (vblank pulse) release parkers without requiring the EE syscall path.
7. **EE INTC sticky VBlankStart smoke** (STAT vs CpuLatched + hold window).
8. **Phase 2 (AGENT-I):** pending latch on Raise/OnVblankIrqPulse; `AcknowledgeIntr` clears; `QueryIntrStatus` + mode/arg/pending/dispatch getters; `Register`/`Release` reject interrupt context; `TryDisableIntr` reports -1 on illegal irq.
9. **Smokes:** `BiosHle_IopVblankEventFlag`, `BiosHle_IopVblankRegisterContracts`, `BiosHle_IopSystemIntrAndTime` (deepened), `Intc_VBlankStartStickyForPollers`.

**Gate:** INTRMANP / INTRMANI → **OK** (contract HLE + smokes; KE_* from binary + ps2sdk; residual = no R3000 dispatch / no ICR MMIO / no full Ghidra dump).

---

## 5. Intentional HLE divergences (keep)

| Divergence | Why |
|------------|-----|
| Callbacks not R3000-executed | Project does not run BIOS IRX on IOP R3000 yet; flag pulse + register contracts are the waiter path |
| No auto-free on callback return 0 | Requires real callback return value |
| INTRMAN RaiseIntr is bookkeeping only | No real IOP exception vector dispatch yet; pending latch is the query surface |
| EE OnVblank fires start+end in one host call | PCRTC is a single edge today; residual after full pulse is END (real hardware separates IRQ 0 and 11 in time) |

---

## 6. Remaining ROMDIR / IRQ / vblank gaps (non-blocking)

| Gap | Notes |
|-----|-------|
| **Full INTRMAN Ghidra dump** | `INTRMANP.bin` / `INTRMANI.bin` extracted; no `INTRMAN*_ALL.txt` yet — mode-register save sets, multi-intr catch, ctx-switch callbacks when decomp lands |
| **IOP IRQ controller MMIO** | Real INTRMAN programs ICR; DetPS2 has no IOP INTC MMIO model |
| **Callback return-0 unlink** | Needs IOP exec or an explicit HLE “oneshot” register API |
| **System status event flag bit 0x200** | First start-list dispatch side effect (`iSetEventFlag(GetSystemStatusFlag(), 0x200)`) |
| **Separate PCRTC start/end host edges** | Would space EF residual START-visible window for mid-frame IOP waiters without a combined pulse |
| **QueryIntrStack / iCatchMultiIntr / SetNewCtxCb** | DECI2 / THREADMAN integration — low priority until real IOP exec |

---

## 7. EE INTC coherence (sticky VBlankStart)

Documented in `Intc.cs` and `BIOS_DISSECTION.md` §7:

- `INTC_STAT` is **sticky** until software write-1-clear.
- COP0 delivery uses a separate **edge latch** (`CpuLatched`); bare-`eret` HLE clears the latch only so the EE does not storm while pollers still see bit 2.
- Per-source **hold window** blocks early W1C so a busy-poller can observe VBlankStart.
- `Pcrtc` re-Raises Start on the end-of-period edge so a mid-frame software clear still gets a fresh sticky bit.

Do **not** auto-ack VBlankStart on PCRTC End, and do **not** add per-title vblank pumps.
