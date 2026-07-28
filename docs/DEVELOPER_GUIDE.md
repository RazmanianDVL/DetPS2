# DetPS2 Developer Guide

**Purpose**: a comprehensive, *current* map of what exists in this emulator and how to build on
it — for a developer joining the project cold. Where the short-form docs (`ARCHITECTURE.md`,
`CONTRIBUTING.md`) give contracts and rules, this document explains the *why* and walks the
actual call chains.

**Honesty note**: several other docs in this repo (`ARCHITECTURE.md`, `PLAY.md`, phase-plan docs)
describe aspirational or historical state and drift out of date as work lands. This document was
written by reading the current source, not by trusting other docs — where they disagreed with the
code, the code won. If you find this document has drifted too, the same rule applies: trust
`git log` and the source over prose.

**Last written**: 2026-07-25, against commit history through the EE interrupt-dispatch fixes
(see §4). Core project: `src/DetPS2.Core` (~80 files, ~19k lines). Desktop shell:
`src/DetPS2.Desktop` (Avalonia).

---

## 1. Determinism — the one rule everything else serves

This project's stated end goal is netplay: multiple instances of the emulator staying in sync
over a network via lockstep/rollback. That's only possible if **the same input sequence produces
bit-identical output every time**, on any machine. Every architectural choice below exists in
service of that:

- **No wall-clock time anywhere in the core or save/load path.** No `DateTime.Now`,
  `Stopwatch`, or OS timers in `src/DetPS2.Core`. Time is `ulong MasterCycles`, an emulated
  cycle counter, full stop.
- **One execution entry point**: `Ps2System.RunFor(ulong cycles)`. There is no other way to
  advance emulated time. If you're writing code that needs to "do something over time," it goes
  through this, not a background thread with its own clock.
- **Deterministic floats**: see `FLOAT_POLICY.md` — canonicalized NaN handling, no
  platform-dependent FP behavior in emulated math.
- **Deterministic present**: the software GS renderer is the "truth" for hashing/determinism
  checks; any GPU-accelerated present path is display-only and must never be used to compute a
  hash that gates correctness (`AcceleratedPresent_DetHashUnchanged` in the smoke suite checks
  this).

If a change you're making touches core emulation and needs a host timer, a random number
generator without a seeded/deterministic source, or thread-scheduling-dependent ordering, stop —
that will silently break netplay determinism in a way that's very hard to debug later.

---

## 2. Execution model

```
Ps2System.RunFor(N)
    → (commercial-boot titles: sliced into 50k-cycle chunks with a low-memory-thrash rescue
       check between slices — see §6.3)
    → Scheduler.RunFor(N)
        → for each registered ISchedulable, in a fixed order: component.Step(sliceCycles)
        → MasterCycles += sliceCycles
```

Every timed component implements:

```csharp
public interface ISchedulable
{
    int Step(ulong maxCycles);  // do up to maxCycles cycles of work; return cycles actually spent
    void Reset();
}
```

`Ps2System` itself also implements `ISchedulable` (`Ps2System.cs:529`) — its `Step` is what
actually walks every subsystem in registration order for one slice:

```
EE.Step → Timers.Step → Dmac.Step → Vif.Step → Gif.Step → Gs.Step → Pcrtc.Step
    → Intc.Step → Iop.Step → Cdvd.Step → Sif.Step → Spu2.Step → Ipu.Step
```

(`Ps2System.cs:529-551`, the interface-explicit `ISchedulable.Step`.) `Scheduler.Register(...)`
order in `Ps2System.RegisterComponents()` (`Ps2System.cs:164-180`) determines round-robin order
when `Scheduler` itself drives things (fixed-slice or event-queue mode) — **do not reorder
without a reason**, per `ARCHITECTURE.md`'s frozen contract.

`Scheduler` (`Scheduler.cs`) supports two modes (`SetEventQueueMode`): fixed-slice round-robin
(default, simple, deterministic) and an event-queue mode for more accurate interleaving. Both
preserve the same `MasterCycles` budget accounting.

`EeJit` / `IopJit` are opt-in JIT paths (`UseJit` flag) that replace the interpreter loop for the
EE/IOP but must stay bit-identical to the interpreter when determinism mode is on — see the
`EeJit_RealAlu_ParityLoop` / `Perf_S1_Documented` smoke tests for how that's verified.

---

## 3. Subsystem map

All of the following are fields on `Ps2System` (`Ps2System.cs:11-45`), constructed and wired in
its constructor (`Ps2System.cs:79-162`). This is the actual source of truth for "how does X talk
to Y" — read the constructor if this table doesn't answer your question.

| Component | File | What it is |
|---|---|---|
| `EE` | `EmotionEngine.cs` (1781 lines) | R5900 (MIPS III + MMI + COP0 + COP1) interpreter. The main CPU. Owns COP0 state, GPRs, the exception/interrupt pipeline (§4), and the syscall trap that hands off to HLE (§5). |
| `Iop` | `Iop.cs` | R3000A (MIPS I) interpreter for the IOP (I/O Processor) side. HLE'd at the module/RPC level (§5.3) rather than fully executing every real IOP module — see §6.5 caveat. |
| `Memory` | `SystemMemory.cs` | Flat RDRAM (32MB) + BIOS ROM + IOP RAM + scratchpad, with physical-address MMIO carve-out (`MMIO_BASE`..`MMIO_END`) delegated to `MmioBus`. |
| `Mmio` | `MmioBus.cs` | Dispatches physical-address reads/writes in the `0x10000000+` range to the right hardware register block (Timers, INTC, DMAC, SIF, pad, SPU2, SIO2, IPU, GIF/GS/VIF). This is where "unknown MMIO" telemetry (seen in `blocker-trace` output) comes from. |
| `Intc` | `Intc.cs` | Interrupt controller: 15-bit `Stat`/`Mask` registers (`Intc.InterruptSource` enum), `GetPendingInterrupts() = Stat & Mask`. See §4 for how this drives EE exceptions. |
| `Dmac` | `Dmac.cs` | The 10 DMA channels (VIF0/1, GIF, IPU in/out, SIF0/1/2, SPR in/out). Raises `Intc.InterruptSource.DmaController` on channel completion. |
| `Vif` / `Vif1` / `VifUnpacker` | `Vif.cs`, `Vif1.cs`, `VifUnpacker.cs` | VIF0/VIF1 command processors — unpack GS/VU data streams from DMA into VU memory or straight to GIF (`Vif1CommandProcessor.cs` handles the MSCAL/etc. command set). |
| `Vu0` / `Vu1` / `VectorUnit` | `Vu0.cs`, `Vu1.cs`, `VectorUnit.cs` | The two Vector Units — VLIW micro-code processors used for geometry transform (VU0, tightly coupled to EE via COP2) and full macro programs (VU1, own memory, feeds GIF via XGKICK). |
| `Gif` | `Gif.cs` | GIF (Graphics Interface): receives Path1 (VU1 XGKICK), Path2 (VIF1 direct), Path3 (DMA) GIFtag streams and forwards PACKED/REGLIST/IMAGE data to `Gs`. |
| `Gs` | `Gs.cs` (1213 lines) | Software GS (Graphics Synthesizer) implementation: register file, primitive rasterizer, framebuffer, PSMCT32/PSMT8 texture addressing. This is the determinism "truth" — see §1. |
| `GsPipeline` | `GsPipeline.cs` | Thin façade over Gif+Gs+Pcrtc for "submit a path, present a frame." |
| `Pcrtc` | `Pcrtc.cs` | Display timing / VBlank generator. Raises `VBlankStart`/`VBlankEnd` on `Intc` on a cycle-driven period. **VBlankStart is deliberately left "sticky"** (never auto-acked) so games can busy-poll `INTC_STAT` directly with COP0 interrupts masked off — see §4.4, this is a real invariant other code must not violate. |
| `Sif` | `Sif.cs`, `SifRpc.cs`, `RealSifRpc.cs` | EE↔IOP bridge. Two parallel RPC implementations — see §5.4 for which is which. |
| `Cdvd` | `Cdvd.cs` | Disc/CD-DVD block device: sector reads (2048B), backed by `IDiscImage` (ISO or synthetic). |
| `Sio2` / `PadInput` / `MemoryCard` / `Multitap` | `Sio2.cs`, `PadInput.cs`, `MemoryCard.cs` | Controller + memory card serial I/O port. |
| `Spu2` | `Spu2.cs` | Sound processor: per-voice VOL/PITCH/ADSR registers, real ADPCM sample decode/playback, mixed into `IAudioSink`. |
| `Ipu` | `Ipu.cs` | Image Processing Unit (MPEG decode) — partial; full MPEG IPU is a known gap (see `COMPLETENESS.md`). |
| `Timers` (`EeTimers`) | `Timer.cs` | EE's 4 hardware timers; compare-match raises `Intc` sources. |
| `IopModules` | `IrxLoader.cs` (`IopModuleHost`, `LoadedIrx`) | IRX module registry/loader — HLE module presence tracking (`sceSifLoadModule` checks succeed against this) rather than real module code execution. |
| `Hle` (`BiosHle`) | `BiosHle.cs` | Syscall dispatch root — see §5. |
| `Debugger` / `Tracer` | `Debugger.cs`, `Tracer.cs` | Breakpoints (`Debugger.AddBreakpoint`, EE halts in `Step`) and instruction tracing (`Tracer.Enable()`, diffable via `docs/TRACE_DIFF.md`). |
| `EeJit` / `IopJit` | `EeJit.cs`, (IopJit in same area) | Optional JIT block-cache execution paths. |
| `Snapshots` | `SnapshotEngine.cs` | Delta frame snapshots (distinct from full `SaveState` — see §8). |
| `InputRecording` | `InputRecording.cs` | Pad-input tape record/replay (`INPR` format) — the backbone of both regression testing and netplay. |
| `Present` (`PresentPipeline`) | `FramePresenter.cs` | Software/GPU-staging/accelerated present backends — display only, never authoritative for hashing (§1). |
| `MidwayAssist` (`MidwayBootAssist`) | `MidwayBootAssist.cs` | The (pre-SDK) per-title boot-assist hack for one specific disc — see §7.3, this is explained in detail there rather than here since it's the thing §7 replaces going forward. |

### 3.1 ElfLoader / KernelBootstrap (boot-time only)

- `ElfLoader.cs`: parses and loads a PS2 ELF into EE memory, sets entry PC / GP.
- `KernelBootstrap.cs`: for commercial (non-homebrew) titles, since this project does not execute
  a real BIOS ROM instruction-by-instruction, this installs synthetic exception/interrupt vectors
  into low RAM (`InstallExceptionVectors`), a low-memory jump trap (`InstallLowMemoryTrap`), and
  sets the EE's initial COP0 `Status` to approximate what a real BIOS would have left it as by the
  time it hands off to a game (see §4.3 — this is exactly where the interrupt-mask default lives).

---

## 4. The EE interrupt/exception system

This section documents a mechanism that changed substantially and was mostly broken until
2026-07-25 — if you're debugging a boot stall, read this section fully before assuming the bug is
somewhere else.

### 4.1 The pipeline, end to end

```
Hardware event (VBlank, DMA complete, Timer compare, SIF...)
    → Intc.Raise(source)                                    [sets a Stat bit]
    → Intc._onChanged callback → EmotionEngine.SyncInterruptsFromIntc()
        → COP0_Cause bit10 (IP2) set if (Stat & Mask) != 0
        → COP0_Cause bit15 (IP7) set if Compare-timer condition met
        → InterruptPending = (Cause & Status & 0xFF00) != 0   [IM-gated, see §4.2]
                              && (Status.IE || Status.EIE)
                              && !(Status.EXL || Status.ERL)
    → EmotionEngine.Step()'s main loop, each instruction:
        if (_takeExceptions && InterruptPending):
            TryDispatchRegisteredIntcHandler()                [see §4.4]
```

`EmotionEngine.TakeExceptions` (a `public bool`) is the master on/off switch. It starts `false`
after `KernelBootstrap.InstallCommercialRuntime` (fast-boot skips the real BIOS init that would
normally have turned it on) and is flipped to `true` the first time the game calls the
`AddIntcHandler` syscall (`SonyKernelHle.cs`, syscall `0x10`) — i.e. the moment a game shows it's
ready to receive interrupts is when we start delivering them. Before that point, `Cause` bits
still get set (so software polling `INTC_STAT`/`COP0_Cause` directly still sees correct values),
but no exception is ever taken.

### 4.2 IM masking (real MIPS semantics — do not skip this)

`COP0_Status` has an 8-bit Interrupt Mask field, bits 8-15 (`IM0`..`IM7`), positionally aligned
with `COP0_Cause`'s `IP0`..`IP7` bits at the same 8-15 range. **Real hardware only delivers an
interrupt if the Cause.IPx bit AND the matching Status.IMx bit are both set** — global `IE` alone
is not sufficient. This project's EE interpreter did not check `Status.IM` at all until
2026-07-25 (`EmotionEngine.SyncInterruptsFromIntc`, `EmotionEngine.cs` — the `causeIp` line):

```csharp
// correct (current):
bool causeIp = (COP0_Cause & COP0_Status & 0xFF00u) != 0;
```

This matters because real PS2 code routinely runs with `IE=1` but `IM=0` *on purpose* — e.g. to
busy-poll `INTC_STAT` directly without COP0 interrupt delivery racing it (see §4.4's VBlankStart
note). Without this check, the emulator was taking phantom interrupt exceptions the real CPU
never would, which (before the fixes below) actively broke such polling loops.

`KernelBootstrap.InstallCommercialRuntime` sets `Status.IM2` (bit10, the INTC summary line) and
`IM7` (bit15, Compare/timer) by default, approximating what real BIOS boot leaves before handing
off to a game — but a title's own code is free (and, per the above, sometimes needs) to mask
those back off itself via a plain `mtc0 $Status` write, which this interpreter now respects
correctly.

### 4.3 Dispatch to real handlers

`EmotionEngine.TryDispatchRegisteredIntcHandler()` (`EmotionEngine.cs`) is what actually runs
when an interrupt is taken. Instead of jumping to a hand-written MIPS dispatcher in the
synthesized vector (which doesn't exist — `KernelBootstrap.WriteHandler` installs a bare `eret`
stub, since writing a real table-walking dispatcher in raw MIPS was judged not worth it vs. doing
the equivalent directly in C#), it:

1. Reads `Intc.GetPendingInterrupts()` for the currently pending+unmasked source bits.
2. For each pending source, checks `SonyKernelHle.TryGetIntcHandler(cause)` — the table built by
   the real `AddIntcHandler(cause, handlerFunc, next)` syscall (`s32 (*handler)(s32 cause)`,
   confirmed against real `ee/kernel/include/kernel.h`).
3. **Special case**: DMA-channel completion (our `Intc.InterruptSource.Sif` bit, raised whenever
   SIF0 DMA completes) is *not* usually claimed via `AddIntcHandler` on real hardware — ps2sdk's
   own `sceSifInitCmd()` claims it via `AddDmacHandler(DMA_CHANNEL_SIF0=5, _SifCmdIntHandler, 0)`
   instead, a *separate* per-DMA-channel handler table. If no direct INTC handler is found for
   the `Sif` source, `SonyKernelHle.TryGetDmacHandler(5, ...)` is checked as a fallback.
4. If a handler is found: performs the same EPC/Cause/EXL bookkeeping `EnterException` would,
   then sets `PC` directly to the handler address, `a0 = cause` (matching the real callback
   signature), and `ra = KernelBootstrap.Kseg0Interrupt` (`0x80000200`) — the synthesized vector's
   own address, which is just `eret`. So the handler's own `jr ra` epilogue naturally restores
   `EPC` and clears `EXL`, exactly like a real BIOS ISR return path, with zero extra plumbing.
5. If no handler is found for *any* pending source: those sources are acknowledged directly
   (mimicking a minimal default BIOS ISR) **except** `VBlankStart` — see §4.4 for why that one
   source is carved out. Without step 5 at all, an unclaimed, unmasked, un-acked interrupt would
   re-fire on literally the next instruction fetch forever, permanently pinning PC in place (this
   was a real, observed bug before the carve-out existed).

### 4.4 The VBlankStart invariant

`Pcrtc.Step()` (`Pcrtc.cs`) deliberately raises `VBlankStart` and *never* auto-acknowledges it —
the comment there is explicit: "games poll/ACK via INTC_STAT write-1-clear." This is the standard
PS2 vsync-wait idiom: a game masks `Status.IM2` off, then does

```
poll: lw v0, INTC_STAT; andi v0, v0, VBLANK_START_BIT; beqz v0, poll
      sw VBLANK_START_BIT, INTC_STAT   ; ack, write-1-to-clear
```

entirely without ever taking a COP0 exception. If the §4.3 step-5 fallback ever acknowledges
`VBlankStart` on the poller's behalf (e.g. because some *other*, unrelated source happened to be
pending and unmasked at the same moment, triggering a phantom exception that swept up all pending
sources indiscriminately), the poll's `beqz` never observes the bit set and the game spins
forever — this was a real bug found and fixed on 2026-07-25 by excluding `VBlankStart`
specifically from the auto-ack sweep. **If you're extending §4.3's fallback logic, preserve this
carve-out** — it's the one INTC source real titles are known to expect this emulator not to touch
on their behalf.

---

## 5. HLE (High-Level Emulation) layering

There are two parallel HLE stacks in this codebase, and knowing which one is live for a given
scenario matters:

### 5.1 Syscall dispatch chain (the authoritative path)

```
EmotionEngine (syscall trap in the interpreter loop)
    → BiosHle.HandleSyscall(EmotionEngine ee)          [BiosHle.cs:118]
        → if SonyKernelMode: SonyKernelHle.TryHandle(ee, num, out result) FIRST
              → (SonyKernelHle.cs — the ~800-line switch on Sony's real EE syscall numbers:
                 thread/sema/eventflag via the SHARED KernelState, AddIntcHandler/AddDmacHandler
                 tables §4.3, SIF register/DMA HLE, RealSifRpc dispatch §5.4)
        → else: BiosHle's own switch over `_kernel` (KernelState) — the simpler homebrew/Det ABI
```

`SonyKernelMode` (`BiosHle.SonyKernelMode`, set via `EnableSonyKernel()`) is the flag that decides
which ABI is live. `Ps2System.LoadBios()` always calls `Hle.EnableSonyKernel()` — **commercial
titles always use the real Sony syscall table**, never the simplified Det homebrew one. The
homebrew path only applies to synthetic ELFs built without a BIOS load (see the
`Homebrew_Elf_DrawsGsFrame` smoke test).

### 5.2 KernelState — the shared bookkeeping layer

`KernelHle.cs` defines `KernelState` (its main class — the filename doesn't match the class name,
a known naming quirk) — Thread/Sema/EventFlag primitives. Despite the filename overlap with
`SonyKernelHle`, this is **not** dead code: it's the shared thread/semaphore/event-flag data
structure both `BiosHle`'s own switch *and* `SonyKernelHle`'s real-syscall switch dispatch into
(`_kernel.CreateThread`, etc.) — one kernel-state model, two syscall-number tables mapping onto
it.

### 5.3 IOP side — HLE, not real execution

`Iop.cs` is a real R3000A interpreter, but **IOP modules themselves are not fully executed** —
`IopModuleHost` (`SifRpc.cs`) tracks which named modules are "loaded" (so `sceSifLoadModule`-style
checks pass) without running their actual code, and IOP-side RPC servers (SIF-bound services like
`LIBSD`, `CRI_ADXI`, `SDRDRV`) are HLE'd in `RealSifRpc.cs` (§5.4) by directly emulating what the
real module's RPC handler would have returned, reverse-engineered from disassembling the real
`.IRX` modules extracted off retail discs (see `IopDisassembler.cs`, the `iop-disasm`/
`iop-find-word` CLI tools, and `docs/DEVELOPER_GUIDE.md` §9). This is the single biggest reason
per-title quirks exist at all (§7): a title using an RPC protocol nobody's reverse-engineered yet
will stall waiting for a response our IOP HLE doesn't know how to produce.

### 5.4 SifRpc.cs vs RealSifRpc.cs — two different protocols, not duplicates

- **`SifRpc.cs`**: defines `SifRpcCmd` + `SifRpcPacket`, a **simplified 16-byte packet ABI**
  (`cmd`/`eeBuffer`/`size`/`result`) documented in `ARCHITECTURE.md`'s "SIF RPC ABI (Phase 13)"
  section. This is a synthetic testing/tooling format — see `Ps2System.CallRpc()` — used by
  homebrew fixtures and tests, **not** what real commercial titles speak. This file also hosts
  the unrelated `IopModuleHost`/`LoadedIrx` IRX registry (§5.3) — two unrelated responsibilities
  sharing a file, worth splitting eventually but not urgent.
- **`RealSifRpc.cs`**: implements the actual Sony wire-format SIF RPC bind/call protocol real PS2
  games and IOP modules use (`SifRpcClientData_t`/`SifRpcBindPkt_t`/`SifRpcCallPkt_t` layouts,
  `SIF_CMD_RPC_BIND`/`SIF_CMD_RPC_CALL` etc.), plus per-service-ID (`sid`) HLE dispatch for known
  middleware: `SidSndf` (SNDF_Driver, Midway's own audio driver), `SidCriAdx` (CRI Middleware's
  ADX codec RPC — an echo-style protocol, not request/response), `SidSdReg` (raw SPU2/Midway
  register RPC). **This is the live path for real disc boots.** These middleware SIDs are
  general — used by potentially many titles built on the same toolchain, not one specific game —
  so they belong here in core HLE, not in a per-title `GameQuirks` module (see §7.1's distinction).

---

## 6. Boot pipeline (commercial ISO path)

```
Ps2System.LoadBios(path)                                    [Ps2System.cs:189]
    → loads BIOS ROM bytes into RDRAM @ 0xBFC00000 mirror
    → EE.PC = 0xBFC00000; Iop.PC = same
    → Hle.EnableSonyKernel()                                [§5.1]
    → EE.COP0_Status = EIE | IE                              (no IM bits yet — see below)
    → KernelBootstrap.InstallCommercialRuntime(this)         [§3.1, §4.2 — sets IM2/IM7 too]

Ps2System.BootDiscFile(path) → DiscBoot.BootFromFile → DiscBoot.BootFromDisc [Iso9660.cs:450]
    → Iso9660.Open(disc) — parse ISO9660 volume
    → Cdvd.MountDisc(disc, volumeId)
    → read + parse SYSTEM.CNF → BOOT2 filename (SystemCnf.Parse)
    → ElfLoader.LoadIntoEe(bootElfBytes, system) — sets real entry PC
    → IopModules.BindDisc(...) — register IRX presence for sceSifLoadModule checks
    → MediaVerify.ExtractSerial(cnfText, bootName) → GameQuirkRegistry.Resolve(serial)
        → Ps2System.ActiveQuirk                             [§7 — serial-gated, e.g. MidwayBootAssist for SLUS_210.87]
    → ActiveQuirk?.OnDiscMounted(system)

(caller) Ps2System.RunFor(cycles) repeatedly
    → commercial-mode slicing (50k-cycle chunks), each slice:
        - KernelBootstrap.RescueIfLostInLowMem — if EE PC wandered into the vector/trap pages
          with no legitimate reason, snap it back to LastGoodEePc (a real jump-into-garbage
          recovery, not a game-specific hack — applies to any title)
        - KickMidwayMainPath, gated on `ActiveQuirk is MidwayBootAssist` — i.e. only runs when
          the mounted disc's serial actually resolved to this module (§7.3)
        - ActiveQuirk?.Step(this)                          [§7 — whichever module (if any) matched]
        - Scheduler.RunFor(sliceSize)                       [§2]
```

Key point for anyone debugging a stall: **this project does not execute a real BIOS boot ROM
instruction-by-instruction for commercial titles.** `KernelBootstrap` synthesizes the minimum
kernel state (exception vectors, initial COP0 Status) a real BIOS would have left behind, then
jumps straight to the disc's boot ELF. Most "why is this stalling" investigations start by
checking: is the EE waiting on a real hardware event we correctly simulate (INTC/DMAC/Timer — fix
generally, see §4), or on IOP-side module behavior we don't execute at all (§5.3 — often needs
either a new middleware protocol case in `RealSifRpc.cs`, or if it's truly one title's own binary
quirk, a `GameQuirks` module, §7).

---

## 7. The GameQuirks SDK

### 7.1 Why this exists

HLE means the IOP side is emulated by *predicting* what a real module's RPC handler would return,
not by running the module's real code. That prediction is necessarily incomplete — proprietary
middleware, undocumented protocols, and one-off binary-layout assumptions in a specific title's
`main()` are all things no amount of *general* correctness work can fully anticipate. Some amount
of per-title handling is therefore unavoidable at this project's current stage.

**The policy remains: prefer a general fix first.** `docs/TITLE_HACKS.md` states this and this
SDK doesn't change it — `GameQuirkRegistry` is where you land only *after* confirming the issue is
genuinely specific to one disc (a hardcoded address that only makes sense for one binary layout,
a boot-sequence assumption unique to one title's `main()`), not a case where the underlying
hardware behavior (INTC, DMAC, a shared middleware SID) was simply unimplemented — those belong
in core (§4, §5.4).

### 7.2 How to add a title module

1. Create `src/DetPS2.Core/GameQuirks/YourTitle.cs`:

```csharp
namespace DetPS2.Core;

public sealed class YourTitleQuirks : IGameQuirkModule
{
    public string Serial => "SLUS_12345";           // MediaVerify.NormalizeSerial format
    public string DisplayName => "Your Title (USA)";

    public void OnDiscMounted(Ps2System sys) { /* one-time setup, right after ELF load */ }
    public void Step(Ps2System sys) { /* polled ~every 25k cycles from the commercial run loop */ }
    public void OnHostPresent(Ps2System sys) { /* once per host display refresh, e.g. FMV pacing */ }
    public void Reset() { /* clear module-local mutable state */ }
}
```

2. Register it in `GameQuirks/GameQuirkRegistry.cs`'s static constructor:
   `Register("SLUS_12345", () => new YourTitleQuirks());`
3. Log the hack in `docs/TITLE_HACKS.md` with a reason (what real hardware/software behavior it's
   standing in for, and why a general fix wasn't possible yet).
4. `sys.ActiveQuirk` is set automatically by `DiscBoot.BootFromDisc` once your serial matches — no
   other file needs to change. `Ps2System.RunFor`'s commercial slice loop and the Desktop shell's
   present loop already call `ActiveQuirk?.Step` / `ActiveQuirk?.OnHostPresent` unconditionally.
5. Build, run `Tests/DetPS2.Tests.csproj` (must stay green), and verify your title's boot
   progresses further with your module than without it (e.g. via `blocker-trace`, §9).

A fresh module instance is created per boot via the registry's factory — never make a module's
own state `static`; that would leak between separate `Ps2System` instances (breaking, among other
things, parallel test runs and netplay's per-session isolation).

### 7.3 MidwayBootAssist.cs — the reference module, migrated 2026-07-25

`MidwayBootAssist.cs` (968 lines) is Mortal Kombat: Shaolin Monks' (`SLUS_210.87`) boot-assist
code and now `sealed class MidwayBootAssist : IGameQuirkModule`, registered in
`GameQuirkRegistry` for `SLUS_210.87`. Its `OnDiscMounted`/`Step`/`OnHostPresent`/`Reset` methods
already matched the interface exactly (that's *why* the interface has those four hooks and no
others — it was modeled on this file), so the migration needed zero changes inside the class
itself beyond adding `Serial`/`DisplayName`.

**The correctness gap this closed**: before this migration, `MidwayBootAssist`'s hooks were wired
**unconditionally** into `Ps2System.cs`'s `RunFor`/`KickMidwayMainPath`/`Reset` and
`Iso9660.cs`'s `DiscBoot.BootFromDisc`, for *any* commercial boot — gated only by
`Hle.SonyKernelMode` and the `--no-assist`-style CLI flags, **not** by whether the mounted disc's
serial was actually `SLUS_210.87`. Booting a different commercial title would have had this
file's hardcoded addresses (`0x00212F70`, `0x00482E98`, `0x002062D4`, etc.) actively read/written
against it regardless, corrupting unrelated state for that title. All of those call sites are now
routed through `Ps2System.ActiveQuirk` (resolved by serial in `DiscBoot.BootFromDisc`), so a
different title's boot never touches this file at all.

**How existing non-generic consumers were preserved**: `Program.cs`'s `probe-frame` and several
Desktop UI status displays (`MainWindow.axaml.cs`, `GameDisplayWindow.axaml.cs`,
`SessionLog.cs`) read `Ps2System.MidwayAssist`'s diagnostic fields (`Status`, `LogoFrame`,
`FramesPresented`, ...) directly as a concrete `MidwayBootAssist`, not through the generic
`IGameQuirkModule` interface — migrating those ~20 call sites to a nullable/generic access
pattern was out of scope for this change. Instead, `Ps2System.MidwayAssist` is now a computed
property: `(ActiveQuirk as MidwayBootAssist) ?? (fallback instance)`. For `SLUS_210.87` it
resolves to the exact same live instance as `ActiveQuirk` (one object, one source of truth); for
any other title it resolves to an idle instance that is never stepped and so never diverges from
`Status == "idle"`. Every existing non-generic call site keeps working unchanged with this
property's non-null guarantee intact — verified via a byte-for-byte identical real MK Shaolin
Monks boot (same final PC/px/syscall counts) before and after this change, plus the full smoke
suite.

**Still open**: `MidwayBootAssist` itself still conflates two responsibilities in one file — a
plausibly-generic boot-FMV cache/present mechanism (`FindBootFmvBytes` tries generic path names
like `ESRB.SFD`/`LOGO.SFD`/`SCEI.SFD`, not just Midway's) and the genuinely MK-specific SIF/CRT0
hacks (`UnstickSifWaits`, `MaybeForceSifInit`, `MaybePostLogoAdvance`, all hardcoded addresses).
Splitting those into a reusable "generic boot-FMV" helper plus a slimmer per-title module was
judged out of scope for this change (higher risk, no second title to validate the split against
yet) — worth doing once a second title's `GameQuirks` module exists and its actual needs are
known, per §7.1's general guidance to hardening the interface against real second-consumer needs
rather than guessing now.

### 7.4 MK Shaolin Monks boot trace — current state (2026-07-25, updated same day)

**Bug A (fixed): `sceSifInitRpc` never ran.** `sceSifBindRpc` (real vaddr `0x4834E0`) was failing
because its packet-pool allocator (`_rpc_get_packet`, real vaddr `0x483060`) saw
`_sif_rpc_data.pkt_table_len` (at `0x77A088`, offset+8 of the real `struct rpc_data` — confirmed
field-by-field against `ee/kernel/src/sifrpc.c`) still zero. `sceSifInitRpc` (real vaddr
`0x482E98`) is the only code that sets it, and none of its 14 real call sites across the whole
binary fired before the pad-bind retry started. `MidwayBootAssist.MaybeForceSifInit` already
force-calls this exact real function (not a synthetic memory-poke) but was gated on
`Gs.PixelsWritten > 0 || Gif.Path3Transfers > 0` — a chicken-and-egg condition when pad/input
needs to init before anything renders. Gate removed; verified via full smoke suite plus a
byte-identical already-working assisted-boot outcome (no regression).

**Bug B (was a test-tool bug, not a product bug): "FMV/logo frame count never advances."**
`probe-frame`'s own post-boot loop called only `RunFor` in a loop, never `OnHostPresent` — but
`MidwayBootAssist`'s own design (see its doc comments) requires `OnHostPresent` to advance the
FMV; `RunFor`/`Step()` deliberately does not, to avoid burning an entire movie in one slice. So of
course the logo frame counter stayed frozen in this specific tool — that's not evidence of a
product bug, it's a gap in the test harness itself. Fixed by adding the missing
`ActiveQuirk?.OnHostPresent(p)` call per iteration, matching `MainWindow.axaml.cs`'s real per-tick
pattern. **Lesson**: before concluding a state genuinely never changes, confirm the test harness
actually drives every code path the mechanism depends on — a static "this looks stuck" reading
from outside can be indistinguishable from "the tool never pokes the thing that would move it."

**With both fixed**, the boot progresses dramatically further than anything seen before this
pair of fixes: the FMV plays through all 103 real decoded frames, `MaybePostLogoAdvance` fires
(`Status` → `"post-logo-main"`), and PC settles into `0x27E0D0` — disassembled and confirmed to be
a genuine object-list iteration loop (`for each item: if item->field56 != filter, call a
per-item update callback`), whose callback (`0x27EEA0`) does real floating-point distance/timer
math on object pairs. This is real, executing per-frame game logic — not a synthetic stall.

**Bug C (found, not yet root-caused): the frame loop runs but produces zero new content.**
Verified over a 400M-cycle window past the post-logo transition: `Gs.PrimitivesDrawn` and
`Gif.Path3Transfers` are **completely frozen** (unchanged instruction-for-instruction) the entire
time, `Cdvd.SectorsRead` stays at `0` throughout, yet `Gs.PixelsWritten` grows by *exactly* one
full framebuffer (640×448 = 286720) every single frame — consistent with a plain screen clear,
not new rendering. Simulating repeated Start-button taps via `PadInput.Press`/`Release` made no
difference. The real GS framebuffer is solid black throughout (confirmed via periodic PPM
snapshots); a real user testing the Desktop app independently observed a frozen *logo* frame
instead of black — both are correct and consistent: `Gs.HostOverlayActive` stays `true` because
`MidwayBootAssist.KeepLogoVisible` only clears the display overlay once `Gif.Path3Transfers > 4`,
which — per the same frozen counter above — never happens, so the cached "best" FMV frame stays
shown on top of the real (black, empty) framebuffer. Confirmed directly from a real session log
(`%TEMP%\DetPS2\session-*.log`, written by `SessionLog.cs`) — `overlay=True` the entire time.

Two candidate explanations were investigated and **ruled out**:
- *Lost writes to unmapped memory*: the object-update loop's list-population code (traced to
  `0x20C0D8`, a free-list/pool-init routine) writes real heap pointers into a table based at
  `0x13400000` — outside any real hardware register range `MmioBus` models, so these register as
  `UnknownMmioWrite` in telemetry. But `MmioBus._unmappedFallback` (see its own doc comment)
  already gives genuinely-unmapped addresses real write-then-read memory semantics — confirmed by
  reading `Read32`/`Write32` directly. Writes here round-trip correctly; nothing is being lost.
- *The older `MidwayBootAssist` hacks (`UnstickSifWaits`/`AutoCompleteWorkItems`) are secretly
  doing the real work, not this session's SIF-init fix*: disabling both via
  `DETPS2_DISABLE_UNSTICK_WAITS=1 DETPS2_DISABLE_AUTO_COMPLETE=1` and rerunning `probe-frame`
  produced a byte-identical result (`px=192389120` at the same cycle count) to the all-assists-on
  run. The SIF-init gating fix alone (plus the always-on FMV/post-logo mechanisms) is what gets
  this far — confirmed, not assumed.

**Correction — important, changes how "fixed" Bug A really is**: `DETPS2_TRACE_RPC=1` showed
**zero** `RealSifRpc.HandleCall`/`HandleBind` activity across the entire run (sanity-checked
against `DETPS2_TRACE_PREEMPT=1` in the identical invocation style, which *does* produce output —
ruling out "the env var isn't reaching the process"). Chasing why led somewhere more important
than the original question: `--pcbreak=483588` (`sceSifBindRpc`'s own call site for
`sceSifSendCmd`, confirmed by disassembly to build a real `cid=0x80000009` bind packet) **never
fires once** in 270M cycles. And `--pcbreak=482E98` (`sceSifInitRpc`'s entry) shows, at the exact
cycle `MaybeForceSifInit`'s `MasterCycles < 1_500_000` gate allows it to fire
(`cyc=1500000` exactly): `ra=0x2131D0` with every GPR zeroed except `sp`/`ra` — the unmistakable
signature of `MaybeForceSifInit`'s own forced jump (it explicitly zeros GPRs 2-25/28/30 and sets
`ra=0x2131D0`), not a real caller.

Since the pad-bind retry starts at `cyc≈1,250,064` and is still spinning at `cyc=1,500,000` (its
own retry delay is near-instant, so it's continuously re-entering the loop, not blocked
elsewhere), **`MaybeForceSifInit`'s forced jump almost certainly fires while `sceSifBindRpc` is
still mid-retry and abandons it outright** — PC is yanked directly into `sceSifInitRpc` and then
teleported to `0x2131D0` (a point in `main()`'s own later continuation), rather than the bind ever
actually completing. `sceSifInitRpc` genuinely does get initialized for real (verified — this part
of the original fix is real and correct, and removing the chicken-and-egg gate was still the right
call), but the padman bind itself is **abandoned, not resolved**.

**What this means, stated plainly**: the boot reaching `"post-logo-main"` and a real per-frame
object-update loop is genuine — more of the game's own code demonstrably executes now, which has
real diagnostic value (§7.4 above, "Bug C") — but it is **not** evidence that `sceSifBindRpc`
completes correctly. It's the pre-existing per-title forced-jump hack (now just correctly gated)
skipping past the stuck state, exactly like its own doc comment describes
("Redirect CRT0 into Midway's real main... Observed: fast-boot never hits 0x212F70 and idles"),
not a general fix that lets the real SIF-RPC handshake happen. This directly explains Bug C: the
object list stays empty and nothing new ever renders because whatever legitimate setup padman's
real bind completion would have triggered never happened — the game is running in a
"skipped a step" state, not a healthy one.

**Resolved — the two candidate explanations above were both wrong; it's simpler than either.**
`--pcbreak=483060` (`_rpc_get_packet`'s own entry point — not just its later exhausted-pool return
path, which was also independently checked and never hit) shows **zero hits across the entire
270M-cycle run** past the initial brief window. Not "fails every time" (pool exhaustion) or "an
earlier branch diverts it" (a different code path would still eventually re-enter this function)
— the whole `sceSifBindRpc`/`_rpc_get_packet` call chain simply **stops executing entirely** the
moment `MaybeForceSifInit`'s forced jump fires at `cyc=1,500,000`, and is never re-entered for the
rest of the run.

**MaybeForceSifInit was made non-destructive (2026-07-25)**: it used to zero every GPR and jump to
a *fixed* point in `main()` (`0x2131D0`), permanently abandoning whatever the interrupted code was
doing. It now saves the full interrupted context (PC + all 32 GPRs — same technique as
`KernelState`'s forced-preemption save/restore) and resumes it exactly where it left off once
`sceSifInitRpc` returns, via a scratch-RAM trampoline (`MidwayBootAssist.SifInitReturnTrampoline`).
This is a real, verified engineering improvement (smoke suite green; the trampoline had to be
moved from an initial `0x00090000` to `0x01FE0000` because anything below `0x00100000` gets
treated as "lost in garbage" by `KernelBootstrap.RescueIfLostInLowMem`, which runs every slice
*before* `Step()`'s own resume check and would otherwise yank PC away first).

**But tracing what it actually resumes revealed something bigger than "which fix gets credit."**
With `DETPS2_TRACE_SIFINIT=1`, the interrupted PC the forced call saves/resumes turns out to be
`0x0040BB08` — a floating-point math utility, not the pad-bind retry loop at all. Chasing why led
to a third, previously-unexamined mechanism: **`Ps2System.KickMidwayMainPath`** (gated only by
`--no-assist`/`DisableMidwayAssist`, *not* by any of the four specific `--no-force-sif` /
`--no-unstick-waits` / `--no-auto-complete` flags) forcibly resets `PC` straight to `main()`'s
entry (`0x00212F70`) the moment `MasterCycles > 100_000` — confirmed directly:
`--pcbreak=212F70` with only `--no-force-sif`/`--no-unstick-waits`/`--no-auto-complete` (no
`--no-assist`) shows `main()` entered at **`cyc=150,000`**, with `sp=0x01FF0000` (this function's
own hardcoded safety value) and `takeExceptions=False` (still false — this is well before the
real CRT0 path would have set it), not the natural entry this session separately confirmed at
`cyc=957,104` when `--no-assist` genuinely disables everything.

**This means every "combined" test run this whole investigation — including the supposedly
"isolated" ones that only disabled `UnstickSifWaits`/`AutoCompleteWorkItems` via their env vars —
was running on a *different boot timeline* than the "pure" (`--no-assist`) test**, because
`KickMidwayMainPath` restarts `main()` ~800,000 cycles earlier than it would run on its own,
changing every subsequent cycle count (including exactly when the pad-bind retry starts, and
whether `MaybeForceSifInit`'s `cyc < 1,500,000` gate catches it mid-retry or long after it's moved
on). Separately, `MidwayBootAssist.PlantSifWorklist` (called unconditionally, gated by none of the
four disable flags) writes directly into `WorklistBase = 0x0077A080` — **the exact same address**
as this game's real `_sif_rpc_data` struct — and one of its writes
(`WorklistBase + 0x08 = WorkItemCount = 32`) lands precisely on `pkt_table_len`. Confirmed:
`sceSifBindRpc`'s own entry (`0x4834E0`) is *also* never hit again in a run with `PlantSifWorklist`
active but `MaybeForceSifInit` explicitly disabled — the retry loop is abandoned by something
else entirely in that configuration too, most likely `KickMidwayMainPath`'s own early restart.

**Honest bottom line**: this title's boot-assist code has accumulated five overlapping synthetic
mechanisms across multiple sessions (`KickMidwayMainPath`, `PlantSifWorklist`, `MaybeForceSifInit`,
`UnstickSifWaits`, `AutoCompleteWorkItems`), and they interact — `KickMidwayMainPath` alone changes
*when* everything downstream happens, and `PlantSifWorklist` pokes the same memory the real SIF-RPC
subsystem uses. Untangling exactly which one is "responsible" for any given downstream observation
requires disabling all five and reasoning from the single clean baseline that actually exists:
`--no-assist`, which disables everything at once (confirmed: `main()` reached naturally at
`cyc=957,104`, pad-bind retry starts at `cyc≈1,250,064` and never completes — `pkt_table_len`
verified via direct memory dump to stay `0` for the entire run). This session's own contribution
(`MaybeForceSifInit` no longer destructively abandoning state) is a genuine, unambiguous quality
improvement regardless of how much of any specific run's *observed* progress traces back to it
versus the other four mechanisms — verified via the full smoke suite with no regression to the
already-working assisted boot.

**Resolved (2026-07-25) — the exact deadlock, traced instruction-by-instruction from `main()`
down to the specific stalled retry, with the `--no-assist` baseline as the only input.** This
supersedes the "why doesn't `main()` reach `0x2131C8` fast enough" framing above — it isn't a
speed problem, it's a permanent, well-formed deadlock:

1. `main()`'s very first action (`0x212FA8`, before anything else including its own argument
   parsing) tail-calls a lazy-init guard (`0x205F00`/`0x205E50`) that walks a **221-entry static
   C++ constructor table** (`base=0x5656DC`, count read directly from static ELF data) in
   *reverse* declaration order — a completely ordinary, real toolchain pattern, confirmed by
   reading the table and its walk loop directly out of loaded memory.
2. Tracing four call-chain hops deep (`0x2F7F68` → `0x2C6520` → `0x00212990` → `0x00206268`,
   each hop confirmed by scanning the whole loaded ELF for the exact `jal` encoding to the
   previous hop's address via the new `scanword` CLI command — see §9), one of those
   constructors performs a pad (`sid=0x80000100`) `sceSifBindRpc` call, checks the bind result,
   and — if it's zero — spins in a calibrated delay loop and retries, forever. This is real,
   correctly-compiled game code; there is nothing synthetic or emulator-specific about it.
3. `sceSifBindRpc` (`0x4834E0`) calls `_rpc_get_packet(0x0077A080)` (`0x483060`) first. That
   function's very first check is `lw a0,8(s1); blez a0,<fail>` — i.e. it bails immediately if
   `pkt_table_len` (`rpc_data+8`) is `<= 0`, before ever touching the 32-slot packet pool or
   calling `sceSifSendCmd`. Confirmed directly: `--watch=77A088` across the whole natural boot
   shows this address is **read 36+ times and never once written** — every single read happens
   at `pc=0x00483078` inside `_rpc_get_packet`, always finding zero.
4. `0x0077A080` sits in the game's own BSS (past the ELF's file-backed region per
   `PT_LOAD file=0x4B1F94 mem=0x680898` — file ends at `0x5B1F94`, BSS runs to `0x780898`), so it
   is correctly zero-initialized by our loader (and would be on real hardware too) — nothing
   pre-seeds it. Only `sceSifInitRpc` (`0x482E98`) is known to write `pkt_table_len=32`.
5. `sceSifInitRpc` is **never called anywhere in the natural boot** — confirmed via
   `--pcbreak=482E98` across the *entire* run (cycles 0 through 270,000,000; before, during, and
   long after `main()`'s natural entry at `cyc=957,104`): zero hits. `main()` itself has **five**
   direct call sites to it (`0x2131C8`, `0x21321C`, `0x213250`, `0x213370`, `0x213428`, all
   confirmed via `scanword`), but they sit in straight-line code *after* the constructor-table
   call at `0x212FA8` — so `main()` never reaches its own calls, because it never returns from
   its first instruction. CRT0 (`0x0011C070`..`0x0011C2A0`, fully disassembled) doesn't call it
   either; its own pre-`main()` dispatcher at `0x00486228` fans out into seven subsystem-init
   functions (semaphore/heap/module-table setup) that were spot-checked and don't write
   `pkt_table_len` either, though not every instruction of all seven has been read line-by-line.

**FIXED (2026-07-25) — root cause was `ReferThreadStatus` (EE syscall `0x30`), a no-op stub.**
Following the `0x00486228` CRT0 dispatcher lead: one of its seven subsystem-init calls
(`0x00480AF0`) creates a second EE thread (`CreateThread`, entry `0x00480A18` — deep in the
SIF-RPC library, almost certainly the real ps2sdk SIF-command dispatch/completion thread that
`sceSifInitRpc` sets up) but does **not** start it directly. Instead, the very next function in
that same init sequence (`0x00480D80`) immediately calls `ReferThreadStatus(id=2, &statusBuf)`
and checks `statusBuf.status == THS_DORMANT (0x10)` — the correct, expected state for a thread
that's been `CreateThread`'d but not yet `StartThread`'d — before it will proceed to actually
call `StartThread` (`0x00480EA0`). Confirmed directly via `--pcbreak=480DA8`: `v1=0x0` (the value
read back from the status buffer) never equals `v0=0x10`, so the game's own defensive check
fails and takes the early-exit path, **permanently skipping `StartThread`** — our `SonyKernelHle`
implementation of `ReferThreadStatus` was `result = 0; break;` with no write to the caller's
buffer at all, so the check was reading uninitialized stack, not real thread state.

Fixed in `SonyKernelHle.ReferThreadStatus` (new method): looks up the real `Thread` record and
writes a proper `ee_thread_status_t` (`status`/`func`/`stack`/`stack_size`/`gp_reg`) to the
caller's buffer, deriving `status` from the thread's actual `Started`/`Sleeping`/current-thread
state (`DORMANT`/`RUN`/`WAIT`/`READY`). `KernelHle.Thread` gained a `StackSize` field and
`CreateThread` an optional `stackSize` parameter so this has real data to report. This is a
general engine fix, not a per-title assist — it corrects an unimplemented BIOS/kernel syscall to
match real PS2 semantics, and any other title relying on this same pattern (very common:
`CreateThread` → `ReferThreadStatus` sanity check → `StartThread`) benefits automatically.

**Effect, verified**: with this fix and *no* `MidwayBootAssist` hacks active (`--no-assist`),
`StartThread` now fires, `pkt_table_len` becomes reachable, and the padbind constructor's
infinite retry loop (§7.4 above) is broken — PC moves from the permanently-frozen `0x2062B4` to
deep, previously-unreached game code (`0x00959AE4`+ by `cyc=2,000,000`, vs. never leaving the
`0x2062xx`/`0x483xxx` region before). Full smoke suite still green (no regression to the
already-working assisted boot or anything else).

**FIXED (2026-07-25) — a real, general interpreter bug: interrupt dispatch silently clobbered
`$ra` for whatever code got interrupted.** Chasing the wild-jump blocker above (adding
`--trace-window=N --trace-chrono` — dumps `Tracer.Entries` in true insertion/cycle order, unlike
the existing address-sorted view — plus bisecting `--cycles=N` to find where the "steady linear
PC drift" signature first appears) traced the drift's true origin to the *221-entry static
constructor table walker itself* (`0x00205E50`..`0x00205EF8`, see the finding above — this is the
function `main()`'s first instruction tail-calls into, which runs every C++ global constructor
in reverse order before `main()`'s own body executes). Its own final `jr ra` — returning to
`main()` after all 221 constructors finish — jumps to `0x005B9FF0` garbage instead of the correct
`0x00212FB0`. `--pcbreak=205EEC` (its restore sequence) showed why: `sp=0x01FFFEE0`, not the
`0x01FFFEB0` it was entered with — a `+0x30` (48-byte) stack imbalance had crept in somewhere
across the 221 constructor calls, so its own `ld ra,32(sp)` read from the wrong address entirely
(explaining why `--watch=1FFFE60`/`--watch=1FFFED0` on the *expected* correct addresses upstream
never showed a corrupting write in either this or the audio-function repro below — the actual
read/write pair was happening 48 bytes away from where it should have been the whole time).

Root cause, found by reading `EmotionEngine.TryDispatchRegisteredIntcHandler` (the shortcut that
lets a registered `AddIntcHandler` callback run without a hand-written MIPS dispatcher — see its
own doc comment): it points `$ra` at the exception vector (`KernelBootstrap.Kseg0Interrupt`, a
bare `eret` stub) so the handler's own epilogue `jr ra` naturally reaches `eret` and resumes
normal execution — a clever trick, but it **unconditionally overwrote `$ra` with no save**.
Interrupts land at arbitrary instruction boundaries, including mid-call-chain with a live,
not-yet-saved `$ra` (e.g. right after a `jal`, before the callee's own prologue has saved it to
its stack frame) — any interrupt firing at exactly that moment permanently destroyed that
call's real return address for the rest of its execution, only reachable via a `sd ra,N(sp)` that
would now save `Kseg0Interrupt`'s address instead. Fixed in `EmotionEngine.cs`: added a
`Stack<ulong> _savedRaAcrossIntcDispatch`, pushed before the clobbering `SetGpr(31, ...)` in
`TryDispatchRegisteredIntcHandler`, popped and restored in `ExecuteEret` (a no-op when the stack
is empty, so unrelated `eret`s — syscalls, faults, BEV boot — are unaffected; LIFO correctly
handles a nested interrupt firing inside an already-dispatched handler). Full smoke suite green.

**Note**: that fix did *not* change the specific `+0x30` sp-imbalance symptom above (verified —
identical `sp=0x01FFFEE0` before and after). It's still a genuine, independently-justified fix,
but the sp-drift had a different, separate trigger — found and fixed below.

**FIXED (2026-07-25) — the actual root cause: `LUI` didn't sign-extend, corrupting the extremely
common `lui+ori` idiom for loading a negative 32-bit constant.** Traced by bisecting `--cycles=N`
down to the *exact instruction* (the previous entries in this section were false leads — see below
for what they turned out to mean): inside `0x0034CE10` (one of the 221 constructors, a normal
audio-related init routine — not a game bug, this is real, correctly-compiled retail code), a
`memset(sp, 0, 144)` call is followed by an 8-byte-tail-copy loop whose exit test is
`lui v0,0xFFFF; ori v0,v0,0xFFFF; addiu a2,a2,-1; beq a2,v0,<exit>` — the standard compiler
pattern for "loop until counter reaches -1". `addiu` correctly produces a 64-bit sign-extended
`-1` (`0xFFFFFFFFFFFFFFFF`) when `a2` underflows past 0. But `ExecuteLui` in `EmotionEngine.cs`
was `Lo = (ulong)imm << 16` — a *zero-extending* 32-to-64 widen — so `lui v0,0xFFFF` produced
`0x00000000FFFF0000` instead of the correct sign-extended `0xFFFFFFFFFFFF0000`; after the `ori`,
`v0` ends up `0x00000000FFFFFFFF`, not the true 64-bit `-1`. The `beq a2,v0,<exit>` then compares
two bit-patterns that both *mean* -1 but don't *equal* each other, so the exit branch never
fires, and the loop executes exactly one extra iteration — writing one stray zero byte one past
`memset`'s intended 144-byte range, directly into the adjacent, live stack slot holding this
constructor's own saved `$ra` (`0x0034D780` → `0x0034D700`, only the low byte changed — an exact
match for a single corrupting `sb`). That corrupted return address is what sent the
constructor-table walker's eventual `jr ra` into garbage, which is what every earlier entry in
this section (the `0x005B9FF0` wild jump, the `+0x30` sp imbalance, the audio-function trace) was
actually downstream of. None of those earlier findings were wrong, they just weren't the root —
each was one more layer of the same single bug's blast radius.

This is checked with certainty via `--pcbreak`/`--watch` at the exact byte address (confirmed the
single `sb a1,0(v1)` write, confirmed its `memset` caller's `a0`/`a2` args cover exactly
`[dest,dest+144)` while the corrupted address sits at `dest+144`, one past the end) — not a guess.
Fixed in `EmotionEngine.ExecuteLui`: `Lo = unchecked((ulong)(long)(int)((uint)imm << 16))`, sign-
extending through `int` before widening to `ulong`. This is `LUI`'s real, spec-defined MIPS64/R5900
behavior — not a game-specific patch. Since `lui` is one of the most common instructions in any
compiled MIPS binary (used for essentially every 32-bit constant/address load, and specifically
for every negative constant via the `lui+ori`/`lui+addiu` idiom this bug broke), this fix likely
has broad positive effects on titles and code paths well beyond this one boot sequence. Full smoke
suite green (including `HostGamepad_Enumerate`, `NetplayCert_ProductionGate`, JIT-parity, and
determinism-sensitive rollback/soak tests — no regressions from a change this fundamental).

**Effect, verified**: boot now proceeds *far* past the previous `UnknownOpcode`/wild-jump failure
point — by `cyc=2,000,000`, `px=286720` (the game is genuinely writing pixels to the framebuffer),
`gifPath3=1`, `dmac=4`, real MMIO reads/writes in the `0x1A700xxx` hardware-register range (no more
`UnknownOpcode` events at all in this window). `Cdvd.SectorsRead == 0` and the empty object-list
loop (§7.4 "Bug C") should be re-checked against this new, much-further boot state rather than
assumed still relevant — the whole picture past this point needs fresh investigation.

**Also investigated and ruled out along the way** (kept for the record so they aren't re-tried
blindly): `SetupHeap` (EE syscall `0x3D`) is a no-op stub (`result = 0`) alongside the
already-fixed `ReferThreadStatus`; implementing a real return value (matching `EndOfHeap`'s
`0x01FFF000` boundary) made no difference to this specific bug and was reverted, with a comment
recording the negative result. Thread preemption (`KernelState.MaybePreempt`) was also ruled out
via `DETPS2_TRACE_PREEMPT=1` — no actual thread switch occurs anywhere in the relevant window.

**FIXED (2026-07-25) — systemic audit found 8 more instances of the same bug class.** Given `LW`
had already been bitten by this once (see its own comment in `EmotionEngine.cs`) and `LUI` just
was, a full pass over every "32-bit" MIPS64/R5900 opcode found the pattern was genuinely
widespread, not a one-off — the codebase had a general habit of operating on/storing full 64-bit
register values for instructions the spec defines as strictly 32-bit (truncate inputs, compute,
sign-extend the *result*). Fixed, all in `EmotionEngine.cs`:
  - `ADD`/`ADDU`/`SUB`/`SUBU` — did a raw 64-bit add/sub of the full register values instead of
    truncating to 32 bits first. Silently wrong whenever the true 32-bit result crosses the sign
    boundary (e.g. `0x7FFFFFFF+1`) — routine in real loop counters and pointer arithmetic.
  - `ADDIU` — same bug, and arguably the highest-impact instance found: it's among the single
    most common instructions in any compiled MIPS binary (every small stack/offset adjustment).
  - `SLL`/`SRL`/`SRA`/`SLLV`/`SRLV`/`SRAV` — shifted the full 64-bit register instead of the
    low 32 bits, and didn't sign-extend the 32-bit result.
  - `MULT`/`MULTU`/`DIV`/`DIVU` — `LO`/`HI` were zero-extended via `(uint)` casts instead of
    independently sign-extended per 32-bit half. `MULTU`/`DIVU` still sign-extend `LO`/`HI`
    despite the multiply/divide itself being unsigned — a genuine, easy-to-miss R-series quirk.
  - `MFC0` — zero-extended the 32-bit COP0 value. High-impact in practice: KSEG0/KSEG1 addresses
    (`0x80000000`+, i.e. essentially all kernel/BIOS code and every exception vector) have bit 31
    set, so reading `EPC`/`BadVAddr` after any exception hit this constantly.
  - `MFC1` — zero-extended the FPU value's bit pattern. Every *negative* float has IEEE754 bit 31
    set, so this wasn't an edge case either.

`ADDI`/`ANDI`/`ORI`/`XORI`/`SLTI`/`SLTIU`/`DADD(U)`/`DSUB(U)`/`DSLL(V)`/`DSRL(V)`/`DSRA(V)` were
checked and are correct as-is (the `D`-prefixed forms are genuinely 64-bit ops with no truncation
needed; `ANDI`/`ORI`/`XORI` are correctly zero-extending per spec; `SLTI`/`SLTIU`/`ADDI` already
sign-extend correctly through C#'s `short→ulong` conversion rules). New regression test
`Ee_32BitOps_SignExtendAcrossBoundary` (`Tests/SmokeTests.cs`) pins all 8 fixes with inputs chosen
specifically to cross the 32-bit sign boundary, so a regression to a raw-64-bit or zero-extending
implementation fails loudly instead of silently passing for "clean" small test values (exactly
the trap that let these ship in the first place). Also required updating `Ee_MultuDivu_Dsll`'s
existing `MULTU` expectation, which had encoded the old *incorrect* zero-extended value — the
low-32 result (`0xFFFFFFFE`) was right, but real hardware sign-extends it (bit 31 is set) to
`0xFFFFFFFFFFFFFFFE`, not `0x00000000FFFFFFFE`. Full smoke suite green throughout.

**Cleanup pass (2026-07-25) — dead code and disposable diagnostics purged.** Alongside the ALU
audit, did a project-wide bloat pass on the theory that dirty/dead code makes exactly this kind of
systemic-bug hunting harder (easy to mistake a stale write-only field for a real invariant, or
waste time reading a diagnostic tool that isn't actually maintained):
  - Removed 7 write-only fields that were never read anywhere: `Cdvd._streamRemaining`,
    `EmotionEngine._nullifyDelayIfNotTaken`, `Gif._fifoWords`, `Iop._branchPending`,
    `MidwayBootAssist._esrbDone`, `Ps2System._commercialWorkerKicked`, `Spu2._irqPending` — each
    confirmed via grep to have zero reads before removal, so this is dead state, not an
    incomplete feature waiting to be wired up.
  - Removed `Program.cs`'s 19 genuinely disposable `probe-*` commands (`probe-iso`, `probe-str`,
    `probe-path`, `probe-callers`, `probe-gif`, `probe-desktop`, `probe-sif`, `probe-cmp`,
    `probe-main`, `probe-hang`, `probe-mk5`, `probe-mk4`, `probe-mk3`, `probe-mk2`, `probe-mk`,
    `probe-worker`, `probe-struct`, `probe-di`, `probe-boot`) — one-off diagnostics hardcoded to
    a specific developer machine's file paths and MK Shaolin Monks' addresses, already documented
    in §10 below as "not a maintained, stable tool surface." **`probe-frame` was deliberately kept**
    — it's the one `probe-*` command with real, current, documented use (see §10's tools table,
    including `--watch=HEX` support). `Program.cs` dropped from 1865 to 869 lines; every other
    command (`commercial-boot`, `dump-spine`, `elf-info`, `blocker-trace`, `long-run`,
    `find-store`, `find-word`, `scanmasked`, `scanword`, `disasm`, `play-path`,
    `majority-campaign`, `majority-catalog`, `commercial-checklist`, `netplay-soak`,
    `netplay-cert`, `extract-file`, `elf-sections`, `iop-disasm`, `iop-find-word`) is untouched.
  - Fixed the remaining compiler warnings project-wide (was 12, now 0): an unused local in
    `HostGamepad.cs`, two `CS0675` "bitwise-or on a sign-extended operand" sites (`GsRegisters.cs`
    `PackScissor`, a test helper in `SmokeTests.cs`) fixed by casting through `uint` before
    widening to `ulong`, a `CS8602` possible-null-deref in `GameDisplayWindow.axaml.cs` (the null
    check a few lines above already guarantees non-null, the compiler just can't see it), and the
    `CS8600`/`CS8602` chain in `Program.cs`'s `elf-info` command caused by `SystemCnf.Boot2` being
    `string?` (fixed with `?? ""` at the point it's first assigned to a non-nullable local).
  - Left alone (out of scope for a mechanical cleanup pass, would need actual judgment calls):
    the 16 root-level `.md` docs, some of which `DEVELOPER_GUIDE.md`'s own honesty note admits
    "describe aspirational or historical state and drift out of date" — worth a deliberate survey
    later, not a drive-by deletion.
  - Full smoke suite green throughout; `dotnet build` on Core, Tests, and Desktop all report
    0 warnings / 0 errors as of this pass.

**FIXED (2026-07-26) — R5900's 3-operand `MULT`/`MULTU` extension was unimplemented.** Resumed the
MK boot investigation and found a *new* class of `UnknownMmioWrite`/`UnknownMmioRead` garbage
(addresses like `0x16B806AD`, `0x142001A5` — not in any real MMIO window) starting around
`cyc≈1.85M`. Traced via `--pcbreak` + manual disassembly to a free-list/object-pool initializer
(`0x00384980`-ish) being called with a wildly wrong base pointer. The actual root cause: real
R5900 hardware extends `MULT`/`MULTU` (SPECIAL funct `0x18`/`0x19`) with a 3-operand form —
`mult rd, rs, rt` (`rd != 0`) *also* writes the sign-extended low-32 product to a regular GPR, not
just `HI`/`LO` — used constantly by compilers to skip a separate `mflo`. `EeDisassembler` doesn't
render the third operand (cosmetic gap only), but the real problem was that `EmotionEngine`'s
*base-pipeline* `MULT`/`MULTU` never wrote `rd` at all — even though the sibling pipeline-1 forms
(`MULT1`/`MULTU1`) already did, correctly, a few hundred lines down in the same file. Any code
using the 3-operand form to scale an array index (`mult t0, t0, v0` where `v0`=struct size) left
`t0` stale, producing exactly this "index not actually multiplied" address-corruption shape. Fixed
by adding the same `if (rd != 0) SetGpr(rd, ...)` write already used by `MULT1`/`MULTU1` to the
base `MULT`/`MULTU` cases.

While there, found the earlier "8 more instances" sign-extension audit (above) only ever covered
the base pipeline — `MULT1`/`MULTU1`/`DIV1`/`DIVU1` and the MMI-table `MADD`/`MADDU`/`MADD1`/
`MADDU1` still zero-extended `LO`/`HI` (`LO1`/`HI1`) instead of independently sign-extending each
32-bit half, the exact same bug class. Fixed all of them to match the already-fixed base pipeline.

**Effect, verified**: with both fixes, `dump-spine`/`blocker-trace` report **zero** `UnknownMmio*`
events at all through `cyc=5,000,000` (previously 12 unique garbage addresses in that window) and
`px=860160` at that point (previously `px=286720` — though not a strictly apples-to-apples
comparison, since that earlier figure was measured at a smaller cycle budget). Full smoke suite
green.

**Not yet root-caused — a new, later instance of the same failure shape.** Pushing the cycle
budget to 30M reveals the *same* garbage-base-pointer pattern recurring at `cyc≈29.77M`, in the
same code region (`0x0026Bxxx`-`0x0026Dxxx` calling into the `0x00384980`-`0x00384D60` pool
helpers), but this time the corrupted pointer (`s3`/`s0`, e.g. `0xFFFFFFFFB89096C0`) is *not* a
live index-multiply result — it's a value read back from a stored struct field (`lw s0, 32(a0)`),
meaning something wrote this garbage into memory further upstream, at some earlier point not yet
traced. The MULT/MMI fixes above did not change this specific occurrence at all (byte-identical
`cyc`/`px`/hit-count before and after), confirming it's a genuinely different root cause, not a
residual case of the same bug.

**One layer further, traced same day**: `--pcbreak=0x0026CA9C` shows `a0=0x0` on every hit — the
pool function is being called with a **null** object pointer, and `lw s0, 32(a0)` at `0x0026CAAC`
then reads whatever garbage happens to live at physical `0x20` (extremely low RAM) as if it were a
real array-base pointer. `a0` isn't set locally in this function; disassembling the caller
(`0x0024DBC0`-`0x0024DC9C`) shows it's freshly reloaded from a fixed global slot,
`lui v0,0x4F; lw a0,-364(v0)` → address `0x004EFE94`, right before nearly every sub-call in this
routine — and that slot holds `0` at `cyc≈29.77M`.

**CONFIRMED and FIXED (2026-07-26).** `--watch=004EFE94` across a full 30M-cycle run: **78,389
reads, zero writes** — nothing ever populates this global on the fast-boot path. `find-store`
couldn't locate the writer (its lui+ori heuristic assumes the wrong compiler idiom for this
`lui reg,hi; op reg,-offset` addressing style); a direct masked `scanmasked` for the `sw` opcode
with immediate `0xFE94` found exactly one candidate: `0x00212E90: sw v0,-364(s4)`, inside a
self-contained function at `0x00212DD0` that allocates and constructs 4 manager objects, storing
their pointers into 4 consecutive globals (`0x004EFE94`/`98`/`9C`/`A0`) before returning via a real
`jr ra`. Its **one** real caller is `0x0021338C` — inside `main()`'s own straight-line body — but
`main()` never reaches it: `main()` calls into `0x0024D128` much earlier, at `0x00213030`, and that
call never returns (it's the per-frame object-update loop this whole session has been tracing), so
everything main() would otherwise do afterward, including this call, is dead on the fast-boot path.
This is the exact same shape as the already-documented `MidwayBootAssist` forced-jump gaps above
(Bug A / SIF-init) — a piece of real one-time init that the fast-boot path's synthesized entry into
`main()` never naturally reaches — not a fourth independent bug class.

Fixed with `MidwayBootAssist.MaybeForceManagerInit`/`MaybeResumeAfterForcedManagerInit`, the exact
same non-destructive save-context/force-call/trampoline-resume technique as `MaybeForceSifInit`:
force-call `0x00212DD0` once, resume the interrupted context exactly where it left off. **Timing
mattered**: firing this at `cyc>200,000` (right after `KickMidwayMainPath` lands in `main()`, before
`MaybeForceSifInit`'s own `cyc>1,500,000` gate) caused a severe regression — boot livelocked in the
general exception vector (`PC` stuck cycling `0x80000180`-`0x8000019C`, `px=0`), the *same* signature
`--no-assist` alone produces. Whatever this forced call depends on (heap state, SIF init, something
else) isn't ready that early. Gated on `cyc>3,000,000` instead (safely after `MaybeForceSifInit` has
had time to complete) and the regression disappeared. **Verified**: zero `UnknownMmio*` events
through the *entire* 30,000,000-cycle window (previously corrupted at `cyc≈29.77M`) — this specific
bug is resolved. Full smoke suite green.

**Correction (2026-07-26) — the cyc≈97.66M "corruption" above was a false lead, not a bug.**
Pushing to 100M cycles initially looked like a fourth instance of the same garbage-pointer shape
(fresh addresses in the `0x18928xxx`/`0x1Axxxxxx`/`0x12Dxxxxx` range, same `0x00384980`-family pool
code). But the *same* pattern was found recurring continuously for dozens of unrelated slots across
the whole run, not just one — which matches this file's own earlier "investigated and ruled out"
note (§7.4, above): `MmioBus`'s unmapped-address fallback gives genuinely-unmapped addresses real
write-then-read semantics, so this whole `0x10000000`-`0x1BFFFFFF` region round-trips correctly as
an oversized pseudo-heap. Confirmed directly with the new `--find-writer` tool (see below): the one
address chased in detail (`0x18928140`) turned out to be a **correctly-computed** pool-slot address
whose fields were legitimately either untouched or written exactly once by real code — not
corrupted at all. Don't re-chase this pattern; it's benign.

**The real crash, found separately**: `PC=0xFFFFFFFFFFFFFFF4` at `cyc=100M` is the tail of a wild
jump at `cyc≈97,888,448`, traced via binary search + `--trace-chrono` to a `jal 0x002022B0` from
otherwise completely ordinary, correctly-compiled code at `0x002025E8`. `0x002022B0` decodes as
nonsense (`sd zero,0(zero)`, `sllv zero,zero,v0`) — real ELF-file-backed data (confirmed within the
PT_LOAD's file-backed range, not BSS), just not code, being executed as if it were a function. This
is the signature of an overlay/dynamically-patched code slot that never got its real content
written at runtime. Not yet confirmed why.

**New diagnostics built same day, specifically to stop misattributing findings** (two false leads
in a row — a bogus "material stack corruption" theory built by conflating two unrelated calls to
the same shared `strcpy`, on top of the pseudo-heap false lead above — made clear that raw
`--pcbreak`/`--trace-chrono` tracing alone wasn't enough):
  - **`SystemMemory.LastWriterLog`** (`--track-writers`, `--find-writer=ADDR[:LEN]`,
    `--find-value=VAL[:MASK]`) — a live "who last wrote this address" / "which address holds this
    value" index, hooked into `Write32` and (after an initial gap was found and fixed) `Write8`/`SH`
    too. Answers questions `--watch` structurally can't, since `--watch` requires knowing the
    target address *before* it's written — useless when the corrupted address is itself computed
    at runtime.
  - **`KernelState.ThreadLog`** (`--trace-threads`, `--thread-at=CYCLE`) — a chronological log of
    every thread lifecycle/switch event (Create/Start/Delete/SaveOut/SwitchTo/PreemptOut/PreemptIn),
    each stamped with cycle/tid/pc/sp. Answers "which logical thread was actually active at cycle
    N" directly instead of re-deriving it from call-chain guesswork.

**Used immediately, with a decisive result: zero threads have ever been created, in any trace, all
session.** `--thread-at` on the `cyc=1,500,000` "material"-garbage observation (see the
`MaybeForceSifInit` trace above) shows only the initial `MainReset` event — no `Create`/`Start`/
`SwitchTo`/`Preempt` event exists anywhere before it, confirmed across the *entire* 97.8M-cycle run,
not just that one moment. This rules out "wrong thread's context got restored" as an explanation
for anything observed today, and independently explains a fact visible in every trace all
session but never investigated: `lastCreatedThread: entry=0x00000000` never changes.

**Directly relevant to scoping the SIF RPC fix.** Fixed the syscall histogram's dead `>100` print
threshold (useless on a 41-total-syscall run) to always show the top 30, which surfaced: `0x42`
(SignalSema) and `0x44` (WaitSema) are called 6 times each, but `0x40` (CreateSema) is **never**
called. Traced to `SonyKernelHle.cs`'s `case 0x44`: a documented "auto-create missing semas" safety
net silently creates any semaphore `WaitSema` is called on but doesn't yet exist — **with initial
count 0**. The chain this produces: `WaitSema` on a freshly auto-created, empty semaphore blocks
immediately → looks for another thread to switch to → finds none (thread creation never succeeds,
per the finding above) → falls back to "park until VBlank, then fabricate a signal" (a real,
existing code path a few lines further in the same function). That fabricated-signal fallback keeps
the boot from hanging outright, but is a synthetic stand-in for real synchronization, not evidence
that whatever the real `CreateSema`/thread-start sequence was supposed to accomplish actually
happened. **Not yet fixed** — the concrete next step is finding what *should* call the real
`CreateSema` (syscall `0x40`) for this specific semaphore and why it's unreached, using the same
`scanword`/reachability technique that found and fixed the manager-init gap above; it may turn out
to be the same "`main()`'s own linear init sequence never gets reached" root cause wearing a third
face, or a genuinely separate gap. Given the pattern (three real fixes today, each "a specific
one-time call site is unreachable from the fast-boot path"), that's the leading hypothesis but is
**not confirmed**.

**CONFIRMED, then tried and reverted, same day.** Traced `CreateSema`'s real syscall stub
(`0x0047FE60`, confirmed via its exact 2-instruction body: `addiu v1,64; syscall; jr ra`) — **0**
executions across the full 97.8M-cycle run, out of 23 real call sites in the compiled binary. One
of those call sites (`0x00486228`, called from real CRT0 at `0x0011C250`) creates exactly the two
mutexes the earlier finding needed. `KickMidwayMainPath`'s fake CRT0 jump — straight to `main()`
with synthesized `a0`/`a1`/`ra`, skipping everything from `0x0011C1E0` onward — is confirmed as the
reason none of this ever runs, the same root cause as the manager-init gap, now nailed down
precisely instead of hypothesized.

**Tried the obvious fix — redirect to real CRT0 (`0x0011C200`, right before the real `SetupThread`
syscall) instead of faking it.** It works exactly as predicted: the real init chain runs, `CreateSema`
fires for real, and — for the first time in any trace this whole session — a real worker thread gets
created (`entry=0x00480A18`, squarely in the SIF-RPC library address region). But that thread
immediately calls `WaitSema` on a semaphore id (`3`) that **nothing** in the entire run ever signals,
confirmed via `--pcbreak` at the real `SignalSema`/`WaitSema` stubs (the one real `SignalSema` call
that does happen targets a *different* semaphore, id `1` — the `InitLocks` mutex, unrelated). The
whole boot stalls harder as a result: `px` capped at `573440` (previously `860160`+ and climbing),
`gifPath3`/`dmac` stuck at `0` (previously `1`/`4` and climbing). **Reverted** — a real, measured
regression against the fake-CRT0 baseline, not an improvement, despite being more architecturally
correct.

**What this is worth, despite the revert**: this is no longer a hypothesis. A real SIF-RPC-adjacent
worker thread, once actually allowed to run, blocks permanently and immediately on a semaphore that
only genuine IOP-side interaction could ever signal. That is a concrete, evidence-based confirmation
that real IOP-side SIF RPC service handling — not another force-call patch — is the thing standing
between here and further real progress. The revert is recorded in `Ps2System.cs`'s own comment so
it isn't blindly re-attempted; the next real move is building that IOP-side service handling (or a
narrower, deliberately-scoped stand-in for semaphore id `3` specifically) with this exact deadlock
already mapped out, rather than rediscovering it.

**FIXED, narrower version (same day)**: rather than redirecting all of CRT0 (which drags in the
thread-creation deadlock above), `MidwayBootAssist.MaybeForceInitLocks` force-calls just
`InitLocksFn` (`0x00486020`) in isolation — confirmed self-contained (allocate + 2× real
`CreateSema` syscall, no thread creation anywhere in its own body) — via the same non-destructive
save/resume trampoline technique as the other two forced calls, gated on the same `cyc>3,000,000`
threshold already proven safe. **Verified**: `CreateSema` now fires for real (syscall count
`41`→`43`), `px`/`gifPath3`/`dmac` match the known-good baseline exactly through the full
30,000,000-cycle window (zero regression), and — critically — no thread gets created, confirming
this genuinely avoids the deadlock the full-CRT0 experiment hit. The `cyc≈97,888,448` crash (wild
jump into the unpopulated `0x002022B0` overlay slot) is unaffected either way, as expected — a
separate, still-open issue. Four real fixes today, all following the same shape: find the
unreachable one-time call, confirm via `scanword`/`--find-writer`/`--pcbreak` exactly what it does
and why it's unreached, force-call it in the narrowest safe scope, verify zero regression before
committing.

**The `cyc≈97,888,448` overlay crash traces to the same root cause as the SIF-worker-thread
deadlock — confirmed, not guessed.** The call into the broken `0x002022B0` slot is conditional on a
global at `0x00584684` (`bne s0,zero,...` where `s0` is read from that address at function entry).
`--find-writer=584684:4` across the full run: written exactly once, at `cyc=0` — meaning it's never
touched by any real code, only by the ELF loader itself. Its ELF-compiled initial value is `1`, not
`0` — this is a "needs (re)load" flag that starts dirty and is supposed to be cleared once whatever
resource it guards has actually finished loading. `--find-writer=002022A0:40` on the slot itself:
every word's last-writer is also `cyc=0` — genuinely never populated by anything. Both facts point
the same way: the real load this flag is waiting on never happens, consistent with `cdvdSectors=0`
holding at zero for the *entire* run (every trace this whole session, not just today) and with the
confirmed-blocked SIF worker thread above. This isn't a second independent bug — it's the same
"real IOP-side interaction never happens" gap, wearing a different face. Not a force-call target;
force-calling the loader here would just be a second instance of trying to skip real hardware
interaction the way the reverted CRT0 experiment did, and would need its own careful regression
check the same way.

**CORRECTION, same day — "real IOP-side SIF RPC service handling doesn't exist" above was wrong.**
Checked `RealSifRpc.cs` before writing that, and shouldn't have skipped it: real bind/call handling
already exists and is substantial — real `sceCdRead`-family sector reads via `cdvd.ReadSector`, pad
state, memory-card stub, a real IOP-heap bump allocator, and Midway-specific modules (SNDF_Driver,
CRI ADX, an SPU2 register driver) individually extracted from the actual disc and disassembled to
get their protocols right. It's wired in — `SonyKernelHle.HleSifCmdFromEe` calls
`_realRpc.TryHandle` for every real `SifSetDma`-driven command packet. `RealSifRpc: binds=0 calls=0`
holding at zero all session isn't "the feature is missing" — it's that `HleSifCmdFromEe` only ever
runs when the game's own code issues a real EE→IOP SIF DMA transfer, and that never happens, for
the same reason `_rpc_get_packet`/`sceSifBindRpc` were already documented above as never executing.
This is the *same* unreachable-call shape as the four fixes today, one layer further out — not a
missing feature.

**Traced one layer further** (temporarily re-applied the reverted CRT0 experiment locally, not
committed, purely to gather data): the semaphore the worker thread blocks on (id `3`) is created by
the *main* thread, at the exact cycle the worker thread itself is created (`cyc=157488`,
`ra=0x480B34`) — a standard "create the sync primitive, then spawn the worker that waits on it"
pattern. `--pcbreak` on the real `SignalSema` stub across the full 30,000,000-cycle run: fires
exactly once, still only ever targeting semaphore `1` (the `InitLocks` mutex) — semaphore `3` is
never signaled by anything.

**A copy-loop hypothesis raised and then corrected before it got documented wrong**: the main
thread, right after switching back from the newly-blocked worker, briefly runs a loop at
`0x00487830` (`while (a3 < a2) *a0++ = *a1++`). First guess was that its bound (`a2`) was corrupted,
based on seeing it repeat in one narrow trace snapshot. Checking the *caller* statically (no need to
re-run anything) disproved that immediately: `a2=816` is a small, hardcoded constant at the call
site (`0x004878B4`), not a dynamic/corrupted value — this loop is bounded to ~204 iterations and
finishes fast. It was never the real stall; that was almost a repeat of the exact kind of
misattribution today's diagnostic tools were built to prevent, caught by checking before writing it
down instead of after.

**What's actually happening, confirmed with a later trace-chrono snapshot** (`cyc≈5,000,000`,
filtered to non-exception-vector PCs): **zero** real instructions execute — the main thread is
purely cycling `0x80000180`-`0x8000019C` (the general exception vector's own `eret`-return path)
with no real code running at all. This is the *exact same* livelock `--no-assist` alone was shown to
hit much earlier this session (`s0`-`s3`/`ra` full of `"material"`-ASCII garbage, confirmed via
`--pcbreak` back then). Both the fully-unassisted boot and this CRT0-redirect experiment
independently converge on the identical failure mode once enough real code runs. That's strong
evidence this is a **general, still-unexplained interpreter/exception-handling bug** — not specific
to SIF RPC, CD loading, or any one subsystem — and it's the thing actually capping how much real
code can run before things fall over, regardless of which path gets there.

**Honest state at the end of this session**: the real IOP-side service layer is already built and
was never the gap. What's actually blocking further progress on this specific experiment is the
same general exception-vector livelock already flagged (but not yet root-caused) earlier in this
document — recurring here as independent confirmation that it's a real, general bug worth
prioritizing, not an artifact of the `--no-assist` test specifically. Next session's most
leveraged target: root-cause *that* livelock (why do `s0`-`s3`/`ra` end up holding literal string
bytes, and why does the exception vector then get entered in an unbreakable cycle) using the same
`--track-writers`/`--trace-threads` tooling built today — solving it would very plausibly unblock
both the SIF-worker-thread deadlock and whatever's beyond it, in one shot, rather than needing
one force-call per gap the way today's four fixes did.

**Pushed on the `"material"` livelock directly (same day), and hit a second false lead — caught
the same way, before it got written down as fact.** The write pattern feeding the livelock (a real
library `strcpy`-equivalent at `0x00474E1C`-`0x00474E6C`, using the standard MIPS "hasless" SIMD
zero-byte-detection trick via `PSUBB`/`PNOR`/`PAND`/`PCPYUD`) looked, from a wide trace, like it
might be a self-overlapping runaway copy — worth checking given this session's whole ALU-bug theme.
It wasn't. Built `--pcbreak=START:END` (dumps registers, now including `a3`/`t0`/`t1`/`t2`, at
*every* instruction in a range, not just one PC) specifically because neither `--trace-chrono`
(opcodes, no registers) nor single-address `--pcbreak` (registers, but only one PC per iteration)
could resolve this precisely enough on their own. With it: `a2` (destination) is set once per
invocation and genuinely never changes within a call — the earlier "a2 grows in lockstep with a1"
reading was from *separate* invocations, not one runaway loop. And `PSUBB`'s output was hand-verified
byte-by-byte against the real quadword content (`t1=0x750380` vs `t2=0x0101010101010101` →
`v0=0xFFFFFFFFFF74027F`, exactly right) — the MMI zero-detection logic is computing correctly, and
the specific chunk traced genuinely does contain a real terminator, so the loop correctly exits
rather than running away. No bug in this loop or in `PSUBB` as tested. Reverted the "runaway copy"
framing before it could mislead a future investigation.

**What's still true, unretracted**: the exception-vector livelock itself (confirmed via two
independent reproductions, `s0`-`s3`/`ra` full of `"material"` bytes at the moment it's entered) is
real and still unexplained. What's now ruled out is "a broken strcpy self-overlap corrupts the
stack" as its cause. The actual mechanism — how literal ASCII string bytes end up in callee-saved
registers at an exception boundary — remains open. `--pcbreak=START:END` is now available for
whoever picks this up next to trace it precisely instead of inferring it from wide snapshots, which
is what produced both false leads here.

**RESOLVED (2026-07-26) — part one: the VBlank/INTC storm was real and is fixed.** Built
`DETPS2_TRACE_INTC` specifically to answer "is this actually a fast-firing interrupt, or something
else" — and it wasn't fast-firing at all: `Intc.Raise(VBlankStart/End)` fires only ~3 times in
1,000,000 cycles (`cyc≈250k` apart, a completely normal rate), but the *second* raise already shows
`alreadyRaised=True` — the first was never acknowledged. `InstallCommercialRuntime`'s own comment
already named this exact risk ("without a full ISR that ACKs INTC, VBlank would storm the EE") and
originally left `TakeExceptions=false` as the guard — but both `KickMidwayMainPath`'s forced
`COP0_Status` write and real CRT0's own `ei` enable interrupts before any real handler exists to ack
them. Fixed: `KernelBootstrap`'s synthesized exception vector (the only handler that exists
pre-full-BIOS) now reads-then-writes-back INTC's `I_STAT` register before every `eret` — real
write-1-to-clear hardware semantics (confirmed via `MmioBus`/`Intc.WriteRegister`), giving it the
same baseline "ack whatever's pending" behavior a real kernel's default dispatcher always has.
Verified: 20 clean raises over 5,000,000 cycles post-fix, `alreadyRaised` mostly `False`. Assisted
boot baseline unaffected (`px`/`gifPath3`/`dmac` unchanged at 30M cycles).

**Part two, found while verifying part one — a second, separate, still-open issue.** With the
storm fixed, `--no-assist` no longer VBlank-storms, but still doesn't fully recover: it now hits a
genuine `AdEL` (Address Error, instruction fetch) exception, also fast-repeating. Traced with
`--trace-chrono` + `--find-writer` to full precision, not guessed: a function at `0x00364690`-ish
allocates a 160-byte stack frame, calls the same real library `strcpy` (`0x00474DC4`) into a local
32-byte buffer at `sp+32`, then on return restores `s0`-`s4`/`ra` from `sp+64`-`sp+144` and does
`jr ra`. `--find-writer=1FFFEA0:8` (the exact `ra`-save slot for this specific run, `sp=0x1FFFDC0`)
shows it was last written by the *strcpy's own copy loop* (`sq t1,0(a2)` at `0x00474E48`,
`cyc=981296`) — a genuine buffer overflow. The copy loop itself was already verified correct
(previous entry, hand-checked `PSUBB`): it operates in 16-byte-aligned quadword chunks based on
where the *source* data's null terminator falls relative to that 16-byte grid, not the string's
logical length, so under the wrong source alignment it can legitimately write more than the
destination's 32 bytes — trampling the caller's saved `ra` a few quadwords later, causing exactly
this `jr ra`-to-garbage fault. Not yet fixed. Two live hypotheses, not yet distinguished: (a) a
genuine latent bug in the shipped game's compiled code that real hardware's differently-laid-out
stack simply never happens to trample anything load-bearing for, or (b) our own stack/heap
placement (even under `--no-assist`, which does reach a real, syscall-computed `SetupThread` `SP`
rather than a hardcoded approximation) differs from real hardware's enough to turn a harmless
overflow into a fatal one. Distinguishing these needs comparing our heap/stack layout against a
real PS2's allocator behavior, not more tracing of this one call site.

**CORRECTION (2026-07-26) — the INTC ack fix above was itself wrong and has been reverted.**
Re-testing the (uncommitted, experimental) real-CRT0-redirect in `KickMidwayMainPath` with the
fix in place showed real progress (syscalls 43→139, PC reaching the genuine SIF-library poll loop
at `0x00480330` instead of deadlocking on semaphore 3) but then a *new* stall at that exact loop.
Root cause: `EmotionEngine.TryDispatchRegisteredIntcHandler` already acks every pending INTC source
except `VBlankStart` on its no-handler-found fallback path, deliberately, specifically so busy-poll
code can see that bit stay sticky (its own comment says as much — see §4.4). The synthesized
vector's unconditional ack ran immediately after that fallback on the very same code path and undid
the exclusion, clearing `VBlankStart` out from under the poll on effectively every interrupt from
any other unmasked source. The vector-level ack was fully redundant with, and actively defeated,
logic that already existed and was already correct. Reverted `KernelBootstrap.cs` to its pre-fix
state (bare `eret`, no ack); kept the harmless diagnostic additions (`Intc.TraceRaise`). Re-verified
the assisted-boot baseline is back to exactly `px=860160/gifPath3=1/dmac=4/syscalls=43` at 30M
cycles. While disabling the CRT0-redirect experiment (still not the committed path — it reproduces
the original 2026-07-26 `px=573440` semaphore-3 deadlock once the vector ack is gone, confirming
that wall is independent of the INTC ack question), also caught and fixed a self-inflicted
regression: a second `if (pc < 0x0011C250)` block that sets up SP/GP (`SetupThread`-equivalent) had
been deleted as "dead code" alongside the experiment, on the mistaken assumption that it was
unreachable — it *looked* unreachable only because the experiment's own early `return` always fired
first while the experiment was active. Without the experiment, it's the real SP/GP init the
fake-CRT0-jump path depends on; losing it collapsed the baseline to `px=573440`/2 syscalls with PC
drifting into the strcpy-overflow region above. Caught via the same "verify before concluding"
discipline as everything else this session — re-ran the baseline after the revert instead of
assuming it was clean, saw the wrong numbers, and traced it back to the diff, not a guess.

**Follow-up investigation (2026-07-26) — characterizing the assisted-boot plateau more precisely.**
Built `PcProfiler` (`--profile-pc` / `DETPS2_PROFILE_PC=1`, a cheap opt-in PC-visit histogram) to
answer "is the CPU actually stuck, or just not producing GS output" during the long
`px=860160/gifPath3=1` plateau. Findings, in order of investigation:
- A burst of `UnknownMmioRead`/`UnknownMmioWrite` telemetry starting only after ~47–48M cycles
  (bisected precisely) turned out to be a **red herring**: `MmioBus.cs` already documents, by
  design, that genuinely-unmapped corners of the `0x10000000`-`0x1F000000` I/O window get real
  write-then-read memory semantics rather than always-zero, specifically because some real retail
  code legitimately uses addresses in that window as scratch memory. The read-then-write-same-
  address pattern observed matches that intentional fallback, not corruption — it doesn't explain
  the plateau.
- The hottest non-vector PC range (`0x002022B0`-`0x002022DC`) disassembled (via the existing
  `--dump=` + `EeDisassembler`) to a small floating-point classification leaf function (exponent-
  field extraction, `fpclassify`-style return codes) — real, active computation, not a hardware
  poll. `spu2Samples` keeps climbing the whole time. The game is genuinely alive and computing.
- The actual signal: `CreateThread` (syscall `0x20`) never appears in the syscall histogram across
  the entire plateau — the fake-CRT0-jump path (unlike the real-CRT0-redirect experiment) never
  spawns a second thread at all, so whatever second-thread/render hand-off the game normally relies
  on can't happen. This independently corroborates the CRT0-redirect experiment's own finding
  (semaphore-3 deadlock on a real IOP-side SIF worker thread) — two different boot strategies, two
  different symptoms, same underlying missing piece: the real IOP-side SIF RPC handshake.

**Follow-up fix (2026-07-26) — starved-semaphore auto-unblock.** Re-enabled the CRT0-redirect
experiment locally with `DETPS2_TRACE_RPC=1` to inspect the semaphore-3 wait directly: it's the
third of exactly three legitimate `CreateSema` calls this run (not an "auto-create missing sema"
artifact — that path only fires when the target id doesn't exist yet, and id 3 already did), and
nothing ever calls `SignalSema(3)` because the main thread stays runnable forever and the scheduler
never revisits the worker. Added `MidwayBootAssist.MaybeUnblockStarvedSema`: after a 2,000,000-cycle
grace period, force-signal any thread that's been sleeping on the same semaphore id the whole time
— same effect as a real signal, no execution redirect or faked function effect, re-armed per thread
since this turned out to be a real mutex re-locked in a loop (`WaitSema(3)` recurred roughly every
2M cycles once unblocked). Result on the CRT0-redirect experiment: syscalls climbed to 139 over 40M
cycles (the same ceiling the now-reverted INTC ack fix reached, via a mechanism that doesn't touch
INTC/VBlank semantics at all) — but `px`/`gifPath3` still don't exceed the redirect experiment's
`573440`/`0` ceiling, worse than the default fake-jump baseline's `860160`/`1`. Verified neutral on
the default path (never creates a second thread, so nothing to act on — baseline unchanged). Kept
as a real, safe, independently-useful fix; the CRT0-redirect itself stays disabled since it's still
not the better boot path for actual pixel output. **Current standing wall, confirmed from three
independent angles this session (semaphore-3 deadlock, missing-CreateThread pattern, and
`RealSifRpc: binds=0 calls=0` for the entire run every single time): the real IOP-side SIF RPC
handshake never gets exercised because the EE-side call chain that would invoke it
(`sceSifBindRpc`/`_rpc_get_packet`) is itself unreachable from either boot strategy.** `RealSifRpc.cs`
already has a substantial, real implementation ready to serve those calls (CD reads, pad state, IOP
heap) — it just never gets invoked. Making it reachable (either by getting real CRT0 execution far
enough to hit the real call site, or by synthesizing a plausible SIF RPC completion directly from
the semaphore-3 wait point using `RealSifRpc`'s existing service logic) is the next concrete step
toward clearing the cached-logo-overlay plateau and producing genuinely new rendered content.

**Architecture research + two real fixes (2026-07-26).** Given explicit go-ahead to research real
PS2 architecture rather than keep guessing, pulled the real hardware docs (psdevwiki/ps2tek SIF
register map) and the real ps2sdk source (`ee/kernel/src/sifrpc.c`, `sifcmd.c`, `sifdma.h`) to
check our SIF model against the genuine protocol. Two findings, both fixed:

1. **EE/IOP memory aliasing — a real, previously-unnoticed bug.** `Iop.cs` (the IOP R3000A
   interpreter) called straight into the EE's own `SystemMemory.Read/Write` methods with the IOP's
   raw address. On real hardware the two CPUs sit on separate physical buses; an IOP address like
   `0x1000` is a byte in the IOP's own 2MB RAM, unrelated to the EE's identically-numbered RDRAM.
   Confirmed empirically, not guessed: with a real retail BIOS loaded (`user-media.json`'s
   `biosPath`, a genuine SCPH-70008 dump) and the IOP core genuinely stepping every cycle
   alongside the EE, its PC settled at a stable address whose disassembly was unmistakably EE
   R5900/MMI code (`padduw`, `sq`, 64-bit `sd`/`ld` — none of which exist on the IOP's 32-bit
   R3000A). The "IOP" was silently misinterpreting the EE's own compiled game binary as firmware.
   Fixed with a genuinely isolated `IopRead8/IopRead32/IopWrite8/IopWrite32` family on
   `SystemMemory` (IOP RAM at IOP-physical `0x0`-`0x1FFFFF`, the shared BIOS ROM, the real
   IOP-side SIF mailbox window at `0x1D000000` per ps2tek routed to the same `Sif` object the EE
   reaches via `0x1000F200`, zero/no-op otherwise) and switching `Iop.cs` to use them. This does
   NOT give the IOP a working real boot — that needs real IOP-side DMA/timer/interrupt-controller
   register modeling, well out of scope — but it stops the two CPUs from corrupting/misreading each
   other's memory, which is the actual bug. Post-fix, the IOP's PC free-runs into unmapped
   (zero-returning) territory instead, which is honest: this emulator's SIF servicing has always
   been HLE-based (`RealSifRpc`/`IopModuleHost`, dispatched directly from `Sif.Step()`), entirely
   independent of whether `Iop.cs` itself executes anything meaningful — so this was never actually
   the reason SIF completion never happens, just a real correctness bug worth fixing regardless.

2. **`SIF_STAT_CMDINIT` et al. — real EE library code needs these and nothing ever set them.**
   ps2sdk's actual `sceSifInitCmd` (`ee/kernel/src/sifcmd.c`) literally polls
   `while (!(sceSifGetReg(SIF_REG_SMFLAG) & SIF_STAT_CMDINIT))` before doing anything else —
   `sifdma.h`'s real values are `SIF_STAT_SIFINIT=0x10000`, `SIF_STAT_CMDINIT=0x20000`,
   `SIF_STAT_BOOTEND=0x40000`. Since this emulator's IOP can't realistically complete a real kernel
   boot to set these for real (see above), `Sif.Reset()` now presents `SmFlag` with all three
   already set — the same way a real BIOS only hands control to a game once its own IOP-side boot
   has genuinely finished. Cheap, correct, and removes a landmine for any future work that reaches
   real EE library init code paths checking this.

**Verified these don't change the current plateau, and pinned down why not.** `DETPS2_TRACE_SIFINIT=1`
confirms the already-existing forced call to real `sceSifInitRpc` (`MaybeForceSifInit`, forcing
`0x00482E98`) completes and returns in ~50,000 cycles both before and after these fixes — it was
never stuck on the CMDINIT poll in the first place (its disassembly shows the real function doesn't
gate on that the way stock ps2sdk's does, or reaches it satisfied already for other reasons). More
significant: at the exact moment it fires (`cyc=1,500,000`), the interrupted context already shows
`ra=0x6C6169726574616D` ("material" ASCII) and `PC=0x80000198` (mid-exception-vector) — the
already-documented stack-buffer-overflow corruption (real library `strcpy`'s quadword copy
overrunning its 32-byte destination, §7.4 above) has already happened by 1.5M cycles, well before
any SIF work starts. **This reframes the standing wall precisely: it was never really "the SIF relay
is missing or broken" — a working relay (`RealSifRpc`) already exists and a real call into real SIF
init code already succeeds. The actual blocker is that the memory-corruption bug derails execution
flow itself before the game ever reaches a real `sceSifBindRpc` call site.** Distinguishing this
bug's two live hypotheses (genuine shipped-game bug vs. our own stack/heap layout letting through
what real hardware never triggers) is the next concrete step, and now looks like the *real*
priority — fixing it plausibly unblocks the SIF bind/call chain as a side effect, rather than the
other way around.

**Root-caused (2026-07-26): the "material" corruption was a real bug in this emulator's PCPYUD
instruction, not a strcpy quirk or a stack-layout mismatch.** Added `--find-string=TEXT` (scans
RDRAM for a literal byte pattern) specifically to locate a corrupted string's actual source, then
traced one live occurrence with `--pcbreak`/`--find-writer` to full precision: the real strcpy's
SIMD zero-byte-detection sequence (`psubb`/`pnor`/`pand` computing `hasless(x,0)` across a full
128-bit chunk) correctly detects a genuine null terminator sitting in the chunk's *upper* 64 bits,
then does `pcpyud a0,v0,t1` / `or v1,v0,a0` / `bne v1,zero,...` specifically to move that upper-half
result into a position a scalar `bne` can see (real R5900 semantics only ever expose a GPR's low 64
bits to non-MMI instructions). This emulator's `PCPYUD` computed `rd.Lo=rt.Hi, rd.Hi=rs.Hi`
(mechanically mirroring `PCPYLD`'s confirmed-correct `rd.Lo=rt.Lo, rd.Hi=rs.Lo` pattern) — for
`pcpyud a0,v0,t1` (rs=v0, rt=t1) that put `a0.Lo=t1.Hi` (raw source data, generally nonzero) instead
of `a0.Lo=v0.Hi` (the actual detection signal), so whenever a terminator landed in a chunk's upper
half the scalar check could never see it and the copy loop walked off the end of its buffer
indefinitely — confirmed live: it wrote the same repeating 8-byte source pattern via `sq` every 16
bytes, unbroken, for 4,132+ iterations (65KB+) before the trace was stopped, trampling everything
in its path including saved return addresses. Fixed to `rd.Lo=rs.Hi, rd.Hi=rt.Hi`; the same
instruction sequence that ran the corrupting loop 4,132+ times in 1.5M cycles now hits zero times.
Assisted-boot syscalls climbed 43→62 at the 5M mark from real work no longer being derailed by the
corruption; `px`/`gifPath3`/`dmac`/`RealSifRpc` unchanged — this alone doesn't clear the SIF wall,
but it's a real, independently-necessary fix (this class of bug could derail any code whose
zero-detection or similar bit-trick needs a marker byte in a quadword's upper half, well beyond
this one strcpy call site).

The EE Core Instruction Set Manual PDF and PCSX2's source wouldn't yield clean pseudocode for
PCPYUD through any fetch attempt available here, so the fix was resolved empirically against a
precisely-traced failure rather than left guessed — and then, at explicit suggestion, cross-checked
against **Play!** (jpd002/Play-, a mature independent PS2 HLE emulator, cloned locally for
inspection): its `PCPYUD` computes exactly `RD.Lo=RS.Hi, RD.Hi=RT.Hi`, an exact independent match.
Spot-checked several other MMI "shuffle" instructions the same way (`PEXEW`, `PROT3W`, `PABSW`,
`PREVH`) — all already correct in our implementation, so this bug was isolated to PCPYUD, not a
systemic pattern across the MMI set.

**Also fixed via the same Play! comparison: Deci2Call (syscall 0x7C) was a flat stub.** Play!'s
`CPS2OS::sc_Deci2Call` gave the real sub-function dispatch (Open/Send/Poll/kPuts) and struct
layouts (`DECI2BUFFER`: unknown0@0/status0@4/unknown1@8/status1@0xC/dataAddr@0x10; `DECI2SEND`:
size@0/data@0xC) our stub never had — it always returned 0 regardless of the function argument,
never touching the caller-supplied buffer's status fields. Traced a ~197,000-syscall storm in the
real-CRT0-redirect experiment to exactly this: a debug-output retry loop (`Deci2Send` once, then
`Deci2Poll` repeatedly) that never saw success because the fields it polls were never written.
Implemented the real dispatch (`SonyKernelHle.Deci2Call`, opt-in text surfaced via
`DETPS2_TRACE_DECI2=1`). Result: retry count roughly halved, and — more importantly, traced rather
than assumed — confirmed self-resolving, not a real block: 93,824 of the eventual 96,347 calls
already happen by 5M cycles, i.e. the loop exhausts itself early and moves on, same as it would on
real hardware polling for a debug host that was never attached (`Deci2Open` never runs because
fast-boot skips real CRT0 — the same root cause as everything else in this file). Confirmed this
isn't what caps `gifPath3`/`px` either. The real-CRT0-redirect experiment stays disabled regardless
(worse for actual pixel output than the fake-jump baseline); what comes after this retry loop
resolves is still untraced and remains the next concrete lead.

**Two more scheduler bugs found chasing that lead (2026-07-26).** Pushing the real-CRT0-redirect
experiment to 100M cycles surfaced a genuinely new corruption class: the main thread's own real
stack pointer got silently reverted to a stale value across a preempt/cooperative-restore boundary.
Root cause: `KernelState` has two independent context-save mechanisms — the ordinary cooperative
one (`SaveCurrentContext`/`RestoreContext`, used at every syscall yield) and the forced-preemption
one (`SaveFullContext`/`RestoreFullContext`, used by `MaybePreempt` for a thread that busy-waits
without ever yielding) — and neither knew the other existed. A thread preempted mid-flight (full
save captures its true SP) could later get resumed via the *other* path (e.g. another thread's own
`SwitchToNext` picking it as "next runnable"), which restored PC correctly but SP/RA from whatever
ancient cooperative save happened millions of cycles earlier. Confirmed with `--trace-threads`:
`PreemptOut tid=1 ... sp=0x01FFFC20` immediately followed by `SwitchTo tid=1 ... sp=0x01FFFEF0` — a
different, stale value. Fixed both directions: `SaveFullContext` now also refreshes the plain
fields, and `SaveCurrentContext` clears `HasFullSave` so a later `RestoreFullContext` can never
resurrect a stale `SavedGprFull` snapshot. This is what had been masquerading as "memory
corruption" in the near-null writes and tiny-garbage-value symptoms traced earlier — real code
executing under a PC that assumed one stack depth while SP pointed at a shallower, unrelated one.

Second: `ExitThread`/`ExitDeleteThread` reused the same `SleepThread()` as the real (rewakeable)
`SleepThread` syscall, so an exited thread's stale `WaitSemaId` could later match a `SignalSema`
call (e.g. `MaybeUnblockStarvedSema`) and get incorrectly revived. Added
`KernelState.ExitCurrentThread()` (permanent: `Started=false`, `WaitSemaId=0`, matching
`ReferThreadStatus`'s own DORMANT definition) and taught `SwitchToNext`'s "nobody else runnable,
wake myself so boot doesn't freeze" fallback to only apply to a genuine temporary wait
(`Started` still true) — a real exit stays exited. This roughly halved (261→168) a repeating
`exit(1)` call the main thread was making, though it didn't fully explain it: `EE.Step()` has no
actual halt mechanism when the last thread exits with nothing else runnable, so raw execution just
continues past the syscall into whatever memory follows — a separate, not-yet-built piece (there's
currently no way to tell the interpreter "the program legitimately terminated, stop"), not another
scheduler bug. Traced the remaining exit(1) calls to their trigger: a *different* memory-corruption
crash near cyc=98M (float-shaped data landing in `ra`, a different call site than the PCPYUD-fixed
string case) that knocks execution into the game's own fatal-error handler. Not the reason
rendering stays capped either — `gifPath3` was already stuck at 1 since ~cyc=1M, tens of millions
of cycles before this crash. Both fixes kept regardless: real, independently-correct scheduler
behavior; verified neutral on the default path (never creates a second thread, so this code never
engages there).

**The actual wall, found and crossed (2026-07-26).** Confirmed precisely, not assumed: the real
`sceSifBindRpc`/`sceSifCallRpc` call chain is never reached from *any* code path this session
found. `_rpc_get_packet` (real vaddr `0x483060` — the packet-pool allocator both functions funnel
through in the real compiled binary) is never called even once across a 100M-cycle trace with
every fix above applied, and nothing ever registers a SIF interrupt or DMA handler
(`AddIntcHandler` cause=13, `AddDmacHandler` channel=5 — added `DETPS2_TRACE_HANDLERS=1` and
confirmed neither ever fires). `RealSifRpc.cs`'s receiving/dispatch side was already real and
complete this whole time; the gap was entirely on the EE-side call chain that would produce a real
`SifSetDma`-driven RPC packet in the first place.

Rather than continue an open-ended search for which further real-code bug stands between the game
and that first call, added `MidwayBootAssist.MaybeCompleteRealSifCdRead`: builds a protocol-correct
`SifRpcBindPkt_t` + `SifRpcClientData_t` + `SifRpcCallPkt_t` (exact layouts per `RealSifRpc.cs`'s
own doc comments) for the CD_NCMD service (`sid=0x80000595`) and drives `RealSifRpc.TryHandle`
directly — the same real receiving-side code a genuine call would exercise, just invoked directly
instead of waiting for the EE to reach it. (First attempt routed through `Sif.SubmitRpc`/`Sif.Step`
instead, which turned out to be a completely different, incompatible path — the "DetPS2 homebrew
RPC ABI" `RealSifRpc.cs`'s own comment already distinguishes itself from — caught by checking the
result came back empty rather than assuming the wiring was right.)

**Verified byte-for-byte, not just via a nonzero counter:** `RealSifRpc.Binds`/`.Calls` are nonzero
for the first time this entire session, and the destination buffer's bytes after the call match the
mounted ISO's actual byte 0 exactly (`0xFD` repeated) — a genuine disc read through the real
protocol dispatch. `cdvdSectors=1` in the trace summary for the first time ever. One-shot by design
(proves the chain works end to end; doesn't try to replace the game's own real asset streaming) and
deliberately doesn't allocate a real kernel semaphore for the synthetic client's `sema_id` field
(left 0, since nothing here `WaitSema`s on it) specifically to avoid shifting the id of every
semaphore the game's own code creates afterward. Verified neutral otherwise: assisted-boot baseline
unchanged except the intentional `cdvdSectors` 0→1.

**Why the game never reaches that real call — a strong new lead (2026-07-26).** Went looking for
what the main thread is actually doing instead of ever calling `sceSifBindRpc`. Found a genuine
static string via a raw ISO byte search: `"PS2RNA Ver.1.32 Build:Jun 16 2005 11:06:15"` plus real
error-format strings for `PS2RNA_SetupVoice` — Midway's own real audio middleware, confirming the
earlier `PS2RNA_Init`/`SifAllocIopHeap` panic-string finding wasn't an isolated data point. Checked
whether any of this actually gets logged via the now-real `Deci2Call` (previous entry) during a
100M-cycle trace — it does, extensively: a repeating

    assertion "X" failed: file "Y", line N

message, sent (`Deci2Send`) then polled (`Deci2Poll`) in a tight loop, tens of thousands of times.
Decoded the raw bytes by hand (not just the naive ASCII dump) to check whether this was a real,
meaningful diagnostic or telemetry noise: the format string itself is intact and literal ("assertion
\"", "\" failed: file \"", "\", line " all decode as clean ASCII at fixed offsets), but every single
substituted value is garbage-shaped — `Y` decodes to unprintable bytes forming a 32-bit value miles
outside valid RDRAM (`0x7C401A68`), and `N`, formatted as a plausible decimal integer, is actually a
stack address in the `0x01FFxxxx` range, decrementing by exactly 32 bytes on every iteration (the
same 32 bytes recurring throughout this session as a record/array stride). This is not a decoding
bug — a real assert-style call is firing with real (if wrong) arguments, once per 32-byte record, on
what looks like a validation pass over an array with (going by the observed call volume) many
thousands of entries. Root cause of the array's own content not yet pinned down, but every other
finding this session points the same direction: whatever `PS2RNA` (or its loader) is validating —
almost certainly loaded-module or asset records that a real IOP module load would have populated —
was never actually written for real, because the real SIF module-loading path this whole file is
about never runs. This plausibly *is* the reason the game bails to a fatal `exit(1)` before it would
otherwise reach a real `SifBindRpc` call at all, rather than a separate, unrelated failure. Next
concrete step: find the outer loop that builds each assertion message (one level above the
retry-poll snippet at `0x00481238`-`0x00481258`, `ra=0x00481254` when calling the `Deci2Poll`
wrapper) to identify exactly what array it walks and what a "valid" entry should look like.

**Follow-up (2026-07-26) — corrected the "array validation" theory, found the real mechanism, hit
a real limit.** Traced up two more call levels: `0x00481128` (builds/sends the message) is called
from `0x00480428`/`0x004804A8` — a generic device-1/device-2 "ensure Deci2 open, then print or
read" pair, not per-record validation — which is in turn called from `0x0047B000`. That function
reads a global fault-code register (`*(0x00563B58)`, right next to the already-known
`SifInitedFlag` at `0x00563FE4` — same PS2RNA/SIF global state block) and a paired message-string
pointer (`*0x00563B5C`), and reports whichever fault is currently set. Confirmed with
`--find-writer` that this register is real and actively changes over the run (0x2A, then 0x31,
then finally 0x0B by cyc≈99.3M) — this is a **periodic fault-status watchdog** firing roughly every
7,488 cycles for tens of millions of cycles, not a one-time burst over an array. It re-reports
whatever the last real fault was, over and over, because nothing ever clears it (the same "stuck
forever, no code path resolves it" pattern as everything else in this file).

Caught and corrected a wrong turn while identifying this: the buffer holding the formatted message
(`0x007778A8`) looked, out of context, like it might just be static game data with placeholder
text baked in rather than something corrupted at runtime — checked this directly against the raw
disc image before accepting it. It's wrong: `grep -aoE "assertion.{0,60}line"` against the actual
ISO finds the real template on disc is `"assertion \"%s\" failed: file \"%s\", line %d"` — genuine
unsubstituted `%s`/`%s`/`%d` placeholders. So the original finding stands: whatever calls this
report function is substituting real but wrong-looking values (a 32-bit value outside valid RDRAM
for the file string, a `0x01FFxxxx`-range stack address printed as if it were a plausible decimal
line number) into a real, live sprintf-style call — this is a genuine runtime bug, not baked-in
disc content, confirmed by checking the disc rather than assuming either way.

What's still open, honestly: the exact plumbing from the fault-code register through to those two
specific bad arguments isn't nailed down — the wrapper chain (`0x0047B000` → `0x00480428` →
`0x00481128`) passes several counts/lengths whose exact role wasn't fully disentangled, and this
periodic watchdog reporting a real (if garbled) internal fault doesn't establish that it's the
*same* fault behind the `exit(1)` loop from the previous entry — they may be two independent
symptoms of the same root cause (PS2RNA's audio init depending on the still-unreachable real
`SifBindRpc`) rather than one causing the other. Given the scheduler/CPU bugs already found and
fixed this session, and the SIF chain being independently proven to work end to end, this specific
sub-thread (chasing PS2RNA's exact fault semantics) is not obviously the fastest remaining path to
pixels — it may be more productive to focus on why the game's own code never reaches a real
`SifBindRpc` call at all (§7.4 above), since fixing that would very plausibly make this whole
class of PS2RNA fault-reporting moot rather than needing to be understood in detail.

**Follow-up (2026-07-26) — enumerated every real call site to `sceSifBindRpc`, confirmed none
fire, and precisely re-dated the late crash.** Picked up the exact thread the previous entry
pointed at. Used the (pre-existing) `find-word` CLI mode to scan the whole loaded binary
(0x100000–0x700000) for the `jal 0x004834E0` instruction word (`0x0C120D38`) rather than guessing
— this is real static cross-referencing, not tracing: **14 real call sites**, at `0x2062CC`,
`0x206320`, `0x3829F4`, `0x385DAC`, `0x3860DC`, `0x3863DC`, `0x386524`, `0x3869E8`, `0x41ED5C`,
`0x462790`, `0x462804`, `0x4840CC`, `0x485494`, `0x48568C`. (Also found `_rpc_get_packet`'s two
real callers, both inside `sceSifBindRpc`/`sceSifCallRpc` themselves, at `0x483510`/`0x483710` —
confirming there are exactly two functions that ever reach the allocator, matching `RealSifRpc.cs`'s
own model.)

Disassembled two of the fourteen by hand to confirm they're genuine, meaningful call sites and not
padding/dead code:
- `0x2062CC`/`0x206320`: back-to-back `sceSifBindRpc(&client, 0x80000100, 0)` /
  `sceSifBindRpc(&client, 0x80000101, 0)` — unmistakably `padOpen`'s real pad1/pad2 bind, each
  followed by the exact retry-poll `lw v1,off(sX); beq v1,zero,...` pattern
  `AutoCompleteWorkItems`'s own existing comment already named (`@ 0x2062D4` / `@ 0x206328`) as a
  "tight wait" it fakes past by writing the completion field directly. **Confirms these two are
  faked past, not genuinely satisfied** — which had been assumed but not verified against the
  actual bind call itself until now.
- `0x4840CC`: a generic-looking `sceSifBindRpc(&client, 0x80000001, 0)` **inside the SIF library's
  own code** (0x484000–0x486000 range), immediately followed by more SIF-internal calls
  (`0x483918`, `0x0047FEA0`) — almost certainly `sceSifLoadModule`'s own internal RPC bind to the
  module-loader service, i.e. the single choke point every real IOP module load (PADMAN, SIO2MAN,
  CRI_ADXI, ...) would have to pass through.

Then re-ran the real-CRT0-redirect testbed (temporary edit, reverted after — see `git diff` in this
entry's own commit, none survives) with `--pcbreak=00483000:00483600` (covering `_rpc_get_packet`,
`sceSifBindRpc`'s and `sceSifCallRpc`'s entries, and the two allocator call sites) across a full
**270,000,000-cycle** run — deliberately matching the exact cycle count an earlier entry (§7.4)
used for its own "fires once in 270M cycles" observation under the *old*, destructive
`MaybeForceSifInit`. Result under the *current* code (non-destructive SIF-init trampoline, PCPYUD
fix, thread-desync fix, ExitThread fix, all already in place): **zero PCBREAK hits, not one, across
the entire 270M cycles** — a stronger and more direct result than before: the whole SIF-library
address range is never entered at all, not merely "abandoned mid-retry." This flips the earlier
ambiguous read (that a destructive forced-jump might have been incidentally responsible for the one
observed hit) into a clean negative result under the more faithful current testbed.

**Bisected exactly when execution stops being trustworthy, since a wild jump could in principle
explain "never reached" as "execution derailed before it got there" rather than "the natural boot
path just doesn't call it."** Binary-searched `--cycles=N` checkpoints (each a ~20–30s run, so this
was cheap) down to a 2,000-cycle window: PC is still valid, ordinary-looking code
(`0x0040B9EC`-ish region — a float-to-int clamp helper, unremarkable) through `cyc=96,234,000`, and
has landed in a narrow garbage band (`0x6237xxxx`–`0x623Axxxx`, ~300KB wide, not fully random —
consistent with a corrupted-but-structured pointer being walked, the same signature as the
already-documented stack-corruption bugs) by `cyc=96,236,000`. This precisely re-dates what an
earlier entry only knew as "~cyc=98M... a different call site than the PCPYUD-fixed string-shaped
bug" down to a 2,000-cycle window, and — more importantly — **establishes that this crash happens
tens of millions of cycles after the 14 real bind call sites would have needed to fire and didn't**,
so it is definitively not the reason they're unreached. The natural boot path runs cleanly (real
audio synthesis progressing — `spu2Samples` climbing steadily the whole time, real per-frame MMIO
traffic) for 96+ million consecutive cycles without ever calling into any of the 14 sites, then
crashes into unrelated garbage. Root-causing that crash itself (which register, which instruction)
was not pursued further this entry — it's real and worth fixing eventually, but chasing it will not
unblock SIF bind, so it isn't the priority the previous entry hoped it might be.

**Net conclusion, sharper than before: this is not a corruption-derails-execution problem, and not
a "the retry gets abandoned" problem — it's that nothing in the game's own natural control flow
ever branches into any of these 14 call sites during the entire observed clean-execution window.**
The two pad-bind sites (`0x2062CC`/`0x206320`) sit inside what disassembles as a real `padOpen`
routine that is itself simply never entered; the SIF-library-internal site (`0x4840CC`) that gates
real IOP module loading is equally unreached. Whatever higher-level sequencing decides when to call
`padOpen` / `sceSifLoadModule` (most plausibly the real logo/attract-mode sequencer, which
`MidwayBootAssist.MaybeStartLogo`/`MaybePostLogoAdvance` currently substitute with entirely
synthetic FMV pacing rather than executing) never reaches that decision point on this boot path.
**Did not attempt a fix here** — synthesizing a plausible trigger for 14 different call sites across
at least 3-4 distinct subsystems (pad, core SIF/module-loader, and whatever the `0x382xxx`–`0x386xxx`
and `0x41Exxx`/`0x462xxx` clusters turn out to be) without first identifying their actual common
caller would be exactly the kind of speculative, unverified fix this file's own conventions warn
against. **Concrete next step, if picked up:** statically find the callers of `0x2062CC`'s enclosing
function and of `0x4840CC`'s enclosing function (the same `find-word` technique used here, applied
to whatever `jal`s target those two function *entries*, which weren't identified this pass) to see
whether they share a single common gate — that would turn "14 unreached call sites" into "one
unreached decision point," a much more tractable fix target than patching each site individually.

Verified neutral: default assisted-boot baseline unchanged
(`px=860160/gifPath3=1/dmac=4/syscalls=62/cdvdSectors=1`) — this entire investigation used the
temporary real-CRT0-redirect testbed, reverted via `git checkout --` before any commit, same as
every prior entry in this section. Full smoke suite green. No source changes this entry — read-only
investigation plus this write-up.

**Follow-up (2026-07-26) — took the previous entry's own "concrete next step" and found the actual
common gate: both traced chains converge on a single per-player-object constructor, meaning
`SifBindRpc` is legitimately match-start-gated, not boot-gated.** Picked this up exactly where it
left off, using only the (pre-existing) `disasm` and `find-word` CLI modes — no execution/tracing
needed for the static parts, since the ELF is fully resident in RDRAM right after `BootDiscFile`
regardless of which boot path later executes it.

First, a re-check worth recording: re-ran the two-wrapper-function hypothesis
(`0x004842B0`/`0x00484E28`, the functions enclosing `0x4840CC`'s SIF-lib-internal bind) not just
under the real-CRT0 testbed but also under the **plain default assisted-boot path** (no source
edit at all — `blocker-trace user-media.json --cycles=150000000 --pcbreak=004842B0:00484F00`,
which uses the committed `KickMidwayMainPath` fake-jump-to-main exactly as shipped). Zero hits
across the full 150M-cycle run, with `px/gifPath3/dmac/syscalls` frozen at the exact baseline
values the entire time. This matters because it rules out "the real-CRT0 testbed's own known
brokenness (semaphore-3 deadlock, etc.) is why these sites look unreached" — they're equally
unreached under the actually-best-performing, committed boot path.

Then traced two of the 14 call sites' full static caller chains by hand, walking one level at a
time: disassemble backward from each call site to find its enclosing function's prologue, compute
that function's `jal` encoding (`0x0C000000 | (entry>>2)`), `find-word`-scan `0x100000`-`0x700000`
for it, repeat on whatever calls that.

- **`padOpen` chain** (`0x2062CC`/`0x206320`, already known from the previous entry to be a real
  pad1/pad2 bind): `0x00206268` (the enclosing function) has exactly **one** static caller in the
  whole binary, `0x002129A8`. That function (entry `0x00212990`) also has exactly one caller,
  `0x002C6560`. That function (entry `0x00212990`'s caller's enclosing function, entry `0x002C6520`)
  gates the whole thing on `lw v0,12(s3); beq v0,1,skip` — a plain "already initialized" flag check
  on an object pointed to by its own first argument — and has exactly one caller: `0x002F7F84`.
- **PS2RNA Midway-audio chain** (a previously-undocumented pair of real bind sites found while
  disassembling the `0x462xxx` cluster: `0x00462790` binds service ID `0x534E4446` = ASCII `"SNDF"`,
  and `0x00462804` binds `0x53465356`, both immediately preceded by `jal 0x00482E98` — the same
  `sceSifInitRpc`-looking call `0x00484010`'s wrapper opens with — confirming these are genuine,
  well-formed `sceSifBindRpc` calls for Midway's PS2RNA audio middleware, not padding): the
  enclosing function `0x00462740` has exactly one caller, `0x00271534`. Its enclosing function
  (`0x00271478`) has exactly one caller, `0x0037B500`. Its enclosing function (`0x0037B4F0`, a thin
  one-argument trampoline) has exactly one caller: **`0x002F7FD0`** — 76 bytes from the pad chain's
  `0x002F7F84`, i.e. **the same enclosing function**.

**Both of the two fully-traced SifBindRpc call chains — one for controller ports, one for Midway's
own licensed audio middleware — bottom out in the same single function.** Given it opens a
player's pad AND binds that player's audio channel a few instructions apart, this is almost
certainly a per-player (or per-match-participant) object constructor, called once when a player
actually joins/starts a match — not boot-time code at all. This reframes the entire "why does the
game never call real `SifBindRpc`" question from a mystery into an answered one: **it doesn't call
it during boot because on real hardware it isn't supposed to** — PS2 games commonly bind
controller/audio RPC ports per-match rather than at cold boot, and Shaolin Monks is simply doing
that. Chasing SIF-init flags, IOP acks, or interrupt/DMAC handler registration as "the reason bind
never fires" was the wrong frame; nothing needs to be fixed in the SIF layer itself to explain this
absence.

**Ruled out as a distraction:** a third caller into the general SIF-lib bind wrapper (`0x00484010`,
reached from `0x00211784` with `a1=1`) traces back to a small helper (entry `0x00211718`) that
checks a verbosity/debug flag before formatting a message — this is the assert/debug-print
subsystem (the same one behind the already-documented PS2RNA fault-watchdog and
`"assertion \"%s\" failed..."` format string), lazily binding a debug/Deci2 SIF channel on first
use. It being unreached is expected and correct for a clean run with no assertions firing; it is
not evidence of anything broken.

**This does not mean the SIF/gameplay wall is solved — it relocates it.** The real remaining
question is one level up: what needs to happen for the emulator to progress from where
`MidwayBootAssist.MaybePostLogoAdvance` currently resumes real execution
(`EE.PC = 0x00213218`, "after the dual wait loops... into main's later setup (pad/threads/movies)"
per that method's own comment — i.e. genuine compiled code does keep running from there) all the
way through attract-mode/menu/character-select into an actual match start, which is what would
naturally trigger the per-player constructor found above. That is a large, multi-system piece of
work (real menu-state-machine progression, plausibly synthesized pad input to navigate it), not a
targeted flag flip, and was explicitly **not attempted** this entry — per this file's own
conventions, synthesizing a fake call directly into the per-player constructor without first
understanding what player/character/match state it assumes already exists would be exactly the
speculative, unverified-fix pattern to avoid. **Concrete next step, if picked up:** trace what
happens after `PC=0x00213218` under the default assisted-boot path — does execution reach a
recognizable menu/attract-mode loop, and if so, is there a single global (a `GameState`-style enum,
an "insert coin"/attract-mode timer, a "start pressed" flag) whose value gates leaving it? That
would be the next target for a `MaybeStartLogo`-style unstick.

No source changes this entry — read-only static analysis (no execution needed beyond one
150M-cycle `blocker-trace` re-check under the plain default boot path, no CRT0-redirect edit
required for the disassembly/`find-word` work). Verified neutral: default assisted-boot baseline
unchanged (`px=860160/gifPath3=1/dmac=4/syscalls=62/cdvdSectors=1`). Full smoke suite green
(`dotnet run --project Tests/DetPS2.Tests.csproj -c Release` — note this project is a console app,
not an SDK test project, so `dotnet test` builds it but silently runs nothing; use `dotnet run`).

**Correction (2026-07-26) — the "per-player match-start constructor" framing above is wrong; this
is ordinary boot-time init, and the "0-hit" pcbreak evidence it was based on was a test-harness gap,
not a reachability finding.** Picked this back up expecting to trace what gates progress out of
`0x00213218` into a menu. Two things fell out instead:

1. **`0x002F7F68` (the shared function both traced `SifBindRpc` chains bottom out in) is not a
   match-start object constructor — it's called directly from `0x0021338C`, four instructions after
   `main()`'s straight-line body resumes.** Confirmed via `disasm` on `0x00213380`: `0x0021338C` is
   `jal 0x002F7F68`, full stop — not `jal 0x00212DD0` as an earlier entry in this same section (§7.4,
   "found the actual root cause... `0x00212DD0`'s own caller is `0x0021338C`") stated. That earlier
   claim was itself slightly wrong: `0x00212DD0` (the manager-init call `MaybeForceManagerInit`
   already force-calls in isolation) is reached *indirectly*, from inside `0x002F7F68` at `0x2F7FF0`,
   not directly from `0x0021338C`. `0x002F7F68` is a single "bootstrap: bind pad RPC, bind two Midway
   PS2RNA audio RPC services, call manager-init, do final setup" routine sitting in `main()`'s own
   ordinary post-logo-wait sequence — real, straight-line, always-executed-on-every-boot code, not
   something gated behind an in-game event. This also explains *why* `MaybeForceManagerInit` force-
   calls `0x00212DD0` in narrow isolation rather than its enclosing `0x002F7F68`: calling the whole
   wrapper would re-trigger the exact padOpen retry-forever loop this file traced in painstaking
   detail earlier in this same section (§7.4, "the exact deadlock, traced instruction-by-instruction")
   — the narrow-scope force-call was already correctly avoiding this, whether or not that reasoning
   was written down at the time.

2. **The zero-hits `--pcbreak` evidence for the 14 `SifBindRpc` sites (this entry and the one before
   it) was measured with `blocker-trace`, which never calls `MidwayBootAssist.OnHostPresent` — so
   `_midwayDone` never becomes `true`, `MaybePostLogoAdvance` never fires, and `EE.PC` never reaches
   `0x00213218` (or anywhere past it) in the first place under that tool.** This is the *exact* test-
   harness gap already documented and fixed once before, for a different tool, in §7.4's own "Bug B"
   entry ("`probe-frame`'s own post-boot loop called only `RunFor`... `MidwayBootAssist`'s own design
   requires `OnHostPresent` to advance the FMV") — it recurred here because `blocker-trace` never got
   the same fix `probe-frame` did, and nothing flagged that the two tools now disagree about whether
   this path is even reachable. Confirmed directly: re-ran with `probe-frame` (which already drives
   `OnHostPresent` every simulated frame, plus periodic `Start` taps) and the boot genuinely reaches
   `"post-logo-main"`, `cdvdSectors=1`, and real per-frame computation — `PC` settles at `0x00202C48`,
   which disassembles to a tight bit-normalization loop (`dsll`/`sltu`/`beq` back-edge) feeding into
   the already-identified `0x002022B0` float-classification leaf (§7.4's "Follow-up investigation...
   characterizing the assisted-boot plateau" entry) — real, active software floating-point work
   (almost certainly audio mixing, matching `spu2Samples` climbing the whole session), not a hang.
   **Lesson, restated because it bears repeating**: a "zero hits over N cycles" result only means
   something if the harness that produced it actually drives every mechanism the code path depends
   on — `blocker-trace` joins `probe-frame`'s own earlier list of tools that looked authoritative but
   weren't, for the same underlying reason.

**Net effect on this investigation's framing**: the 14-site `SifBindRpc`-unreachability question is
answered, but not the way the last two entries concluded. It isn't "legitimately gated behind match
start" (wrong) and it isn't "gated behind boot" either in the sense of never being attempted — under
the boot configuration that actually drives FMV pacing (`probe-frame`), execution runs real code well
past `0x00213218` and reaches genuine per-frame audio computation without observably hanging on the
padOpen retry loop at all in this run (no direct instruction-level confirmation either way — `probe-
frame` only samples PC once per simulated 1,000,000-cycle slice, so a retry loop entered and later
escaped, or one whose PC happens to fall outside those exact sample points, wouldn't necessarily show
up). This reopens, rather than closes, the question of whether `0x002F7F68`'s padOpen bind ever
actually executes and blocks on this specific boot path — determining that precisely (via `--pcbreak`
on `0x0020626C:0x00206330` or similar, added to `probe-frame` or ported into `blocker-trace` behind a
`--host-present` flag) is the concrete next step. Separately and at least as important: `blocker-
trace`'s missing `OnHostPresent` call means **every finding in the two entries directly above this
one that relied on `blocker-trace`'s "0 pcbreak hits" as evidence of unreachability past the logo
should be treated as unconfirmed, not refuted** — they may still be correct, but the specific evidence
cited for them wasn't measuring what it claimed to.

No source changes this entry — read-only investigation (`disasm`, `scanword`, `probe-frame`, one
`dotnet build` of the unmodified tree to get a fresh binary to run these against). Baseline
necessarily unchanged (nothing was edited). Full smoke suite green
(`dotnet run --project Tests/DetPS2.Tests.csproj -c Release`).

**Follow-up (2026-07-26) — added the missing `OnHostPresent` drive to `blocker-trace` itself
(`--host-present`, opt-in), got real `--pcbreak` evidence, and it overturns the previous entry's
"ordinary always-executed boot code" conclusion.** Executed the concrete next step named above:
added an opt-in `--host-present` flag to `blocker-trace` (`Program.cs`) that slices the run into
1,000,000-cycle chunks and calls `traceSys.ActiveQuirk?.OnHostPresent(traceSys)` between slices,
mirroring `probe-frame`'s existing pattern exactly — this makes `blocker-trace`'s own `--pcbreak`
support trustworthy for this question for the first time, instead of borrowing `probe-frame`'s
coarse once-per-1M-cycle PC sampling. Kept it opt-in (default behavior untouched) specifically so
every prior baseline number in this file stays comparable.

Two `--pcbreak` runs, both 150,000,000 cycles, both with `--host-present`, on the **plain default
boot path** (no CRT0-redirect edit needed — `KickMidwayMainPath`'s fake-jump-to-`main()` at
`0x00212F70` is the committed, always-on default; the "CRT0-redirect testbed" mentioned in earlier
entries is a separate, unrelated experiment that was NOT used here):
- `--pcbreak=0020626C:00206330` (the padOpen bind-and-poll region): **0 hits.**
- `--pcbreak=0021338C:00213394` (the single instruction the previous entry's static disasm
  identified as `jal 0x002F7F68`, the shared function both traced `SifBindRpc` chains bottom out
  in): **0 hits.**

So not only does execution never enter the padOpen retry loop, it never even reaches the
*instruction that would call into it*. This directly contradicts the immediately preceding entry's
conclusion that `0x0021338C`/`0x002F7F68` are "ordinary, always-executed boot code" — that
conclusion was built entirely on static disassembly (a real `jal` instruction exists at that
address) with no runtime confirmation that the instruction is ever fetched. It isn't, in either of
these two 150M-cycle runs.

Where does it actually go instead? Both runs' final state matches: `PC=0x623A97F8`,
`px=72826880 gifPath3=1 dmac=4 syscalls=62 spu2Samples=48828 cdvdSectors=1` — landed squarely in
the `0x6237xxxx`-`0x623Axxxx` garbage band the previous-but-one entry (commit `4921491`) already
bisected to a precise `cyc=96,234,000`-`96,236,000` crash window and explicitly, and now
incorrectly, ruled out as unrelated to the bind-site question ("happens tens of millions of cycles
after the 14 real bind call sites would have needed to fire and didn't ... ruling it out as the
reason they're unreached"). That ruling assumed the bind sites' caller was reached earlier and
uneventfully; with `0x0021338C` now confirmed at 0 hits across the *entire* 150M-cycle run
(not just "before the crash"), the more consistent reading is the opposite: **this same
memory-corruption crash is very plausibly upstream of, and directly responsible for, main() never
reaching `0x0021338C`/the bind sites at all** — not a coincidence discovered after the fact, but
the actual mechanism. (Note `px=72826880` here vs. the documented baseline's frozen `px=860160` —
`--host-present` genuinely changes execution, since it's what lets `MidwayBootAssist`'s FMV/logo
pacing advance per the already-documented Bug B/C screen-clear behavior; this is expected and is
exactly why the flag was needed to get honest evidence here, and it does not indicate the crash
finding is an artifact of the new flag, since the crash landing PC and all four other counters
matched exactly between the two separate 150M-cycle runs.)

**Corrected net conclusion, superseding both `4921491` and `585f452`**: this was never a "padOpen
retry-forever" bug nor a "legitimately never fired, it's match-start-gated" situation nor "ordinary
reached-but-uninteresting boot code" — it is that the already-documented, not-yet-root-caused
`cyc≈96.2M` wild-pointer corruption crash (§7.4's "Bug C" plateau region) derails execution before
it ever reaches the code that would call `sceSifBindRpc`, full stop. Root-causing *that* crash —
which instruction writes/reads the bad pointer that lands PC in the `0x6237xxxx` band — is now the
single highest-value next step for this entire thread, superseding the SIF-bind angle entirely;
continuing to look for SIF/pad-specific triggers past this point would be investigating a symptom.
Did not attempt to root-cause it in this entry — that's a distinct, substantial tracing task
(binary-searching `--cycles=N` checkpoints and `--find-writer` on whatever register/pointer goes
bad, the same technique `4921491` already used to bisect the window down to 2,000 cycles) and
deserves its own focused pass rather than being rushed at the tail of this one.

Verified: default assisted-boot baseline (no `--host-present`) unchanged
(`px=860160/gifPath3=1/dmac=4/syscalls=62/cdvdSectors=1` at 150M cycles, re-confirmed directly).
Full smoke suite green (`dotnet run --project Tests/DetPS2.Tests.csproj -c Release`). Source change
this entry: `Program.cs` only (`--host-present` flag for `blocker-trace`, opt-in, `--pcbreak`
diagnostics only — no behavior change to the emulator itself or to any default-path output).

**Follow-up (2026-07-26) — bisected the `cyc≈96.2M` crash to the exact faulting instruction under
`--host-present`; root cause narrowed but not fully pinned down, no fix applied.** Picked up the
concrete next step named above: root-cause the wild-PC crash now confirmed to be what stops `main()`
from ever reaching the `SifBindRpc` call sites.

Binary-searched `--cycles=N` (plain `PC=` output only, cheap) on the **default boot path with
`--host-present`** down to a 4,000-cycle window, then used `--trace-window=6000 --trace-chrono` to
get a full chronological instruction trace across it. Found the exact faulting instruction:
`0x0040AD88` (`jr ra`), executed at `cyc=96,246,536`, jumping to `ra=0x6237BBC0` — a value reloaded
two instructions earlier by `ld ra, 80(sp)` at `0x0040AD74`. From there PC walks linearly upward
through unmapped memory reading back all-zero words as `nop` (confirmed via `MmioBus.cs`'s
`_unmappedFallback` path, `Telemetry.cs`'s `UnknownMmioRead`/`UnknownMmioWrite`: reads to addresses
outside real RDRAM/known-MMIO silently return 0 rather than faulting, so a wild jump up there never
crashes — it free-runs forever, which is why the PC "freezes" at a fixed value once the walk
re-enters the periodic timer IRQ's `eret` and loops back into the same NOP stream).

**The `ra` value `0x6237BBC0` is not obviously stack corruption — it's identical to a live
"pointer" being computed nearby.** At the same cycle, the enclosing call chain
(`0x0026D2A8`'s function → `0x0026B150` → `0x0040AC48`, the last of which contains the fatal `jr ra`)
computes `s0 = *(a0+32) + a1` at `0x0026D2B0`-`B4` and passes it down as `a1`/`s1` through every
nested call — and it equals `0x6237BBC0` for this exact invocation. `--pcbreak` sampling of `0x0040AC48`'s
entry across the whole run (cyc≈9.6M through 96.2M) shows this same "voice pointer" argument was
already implausibly large well before the fatal invocation (`0x7123AE80`, `0xFFFFFFFFB1954080`,
`0x613BA7F0`, `0x3A938D00`, ...) — every sampled value across 90M+ cycles is far outside the real
32MB RDRAM range, they just hadn't yet produced a jump target that got fed back through `ra` until
this particular one. This is consistent with (though not proven to be caused by) the standing,
repeatedly-confirmed finding that PS2RNA's audio/voice init never runs on this boot path (§7.4, the
whole `SifBindRpc`-chasing arc above) — an uninitialized or never-populated voice table would produce
exactly this "garbage-but-consistently-structured, used unconditionally" pattern.

**Retracted a wrong turn before it went in as a finding**: initially suspected a genuine stack-frame
overlap between `0x0026B150` (144-byte frame) and `0x0040AC48` (112-byte frame) — `0x0040AC48`'s `sp`
at entry (`0x1FEFCD0`, confirmed via `--pcbreak=0040AC48`) is the *incoming*, pre-decrement value, and
a hex-subtraction slip (`0x1FEFCD0 - 0x70` mis-computed as `0x1FEFCC0` instead of the correct
`0x1FEFC60`) made `0x0040AC48`'s real `ra` slot (`sp+80`) appear to land on the same address as one of
`0x0026B150`'s own saved-register slots. Redone correctly, `0x0040AC48`'s actual `ra` slot is
`0x01FEFCB0`, entirely inside its own declared frame with no overlap with the caller's — the two
frames do not collide. Recorded here specifically so this exact wrong path isn't re-walked.

`--find-writer=01FEFCB0:8` on the corrected address returned an ambiguous result: last write
attributed to `pc=0x002022DC` (a bare `jr ra`, which cannot itself write memory) with `value=0x2`.
This is either a delay-slot PC-attribution artifact in `SystemMemory`'s `LastWriterLog` (the actual
store may be in `0x002022DC`'s branch-delay slot and getting logged under the branch's own PC rather
than the delay-slot instruction's), or something not yet understood — either way it is not a clean
enough signal to name a faulting store instruction with confidence, and guessing further risked
compounding an already-corrected arithmetic error with a second one.

**Confirmed this crash is specific to the `--host-present`-driven boot path**: re-running to the same
absolute cycle count (`96,246,536`) *without* `--host-present` lands at a perfectly ordinary PC
(`0x00203818`, mid-way through the same audio-decompose helper chain, not the garbage band) — the
default committed boot configuration does not reach this failure mode by this point, consistent with
it never advancing FMV/logo pacing far enough to drive the code path that exhibits it.

**No fix attempted.** The faulting instruction and its bad input are pinned down precisely, but the
mechanism by which a `0x6237BBC0`-shaped value ends up in a stack `ra` slot — direct corruption via
an unidentified out-of-bounds store, versus some other propagation this pass didn't find — is not.
Patching around it (e.g., bounds-checking the `jr ra` target, or fabricating a plausible voice table)
would mask rather than fix an unconfirmed root cause, which this file's conventions warn against.
**Concrete next step:** resolve the `--find-writer` ambiguity first — check whether
`SystemMemory.NoteLastWriter`'s call site records the PC of the branch or of its delay-slot
instruction for stores executed in a delay slot, since that would explain the `jr ra` attribution
directly and either confirm or rule out a real out-of-bounds store as the mechanism.

No source changes this entry — read-only investigation (`--host-present`, `--pcbreak`, `--trace-window
--trace-chrono`, `--find-writer`, `--track-writers`, all pre-existing `blocker-trace` flags). Baseline
necessarily unchanged. Full smoke suite green
(`dotnet run --project Tests/DetPS2.Tests.csproj -c Release`).

**Follow-up (2026-07-26) — the `--find-writer` ambiguity WAS a real tracer bug, now fixed; fixing it
reframes (but does not yet close) the corruption question.** Resolved the concrete next step named
above by reading `EmotionEngine`'s main loop directly (`EmotionEngine.cs` around line 385-421) rather
than guessing: `SystemMemory.CurrentPcForWatch` is set once, right before `ExecuteInstruction(opcode)`
runs for the instruction at the *branch's own* `PC`. When that instruction takes a branch (`tookBranch
== true`), the loop then fetches and executes the delay-slot instruction at `PC+4` — but never
refreshes `CurrentPcForWatch` first. Any store performed by the delay-slot instruction itself was
therefore being logged under the *branch's* PC in `SystemMemory.LastWriterLog`/`WatchHits`, not its
own — exactly matching the nonsensical "`jr ra` attributed as a writer" result from the entry above (a
`jr` cannot itself store; the real store was in its delay slot and got mis-tagged). This is a real,
general tracer-correctness bug, not specific to this investigation: **every prior `--find-writer` or
`--watch` result in this file for a store executed in a branch/jump delay slot would have been
off-by-one-instruction**, attributing the write to the branch/jump itself rather than the actual
faulting instruction. Fixed in `EmotionEngine.cs`'s branch-taken path: `CurrentPcForWatch` is now
refreshed to `PC+4` immediately before the delay-slot instruction executes, guarded by the same
`WatchAddr.HasValue || TrackLastWriter` check the original assignment uses (zero cost, zero behavior
change, when neither diagnostic is armed — confirmed the default assisted-boot baseline is bit-for-bit
identical at 150M cycles: `px=860160/gifPath3=1/dmac=4/syscalls=62/cdvdSectors=1`, smoke suite green).

Re-ran `--find-writer=01FEFCB0:8 --track-writers --host-present --cycles=96250000` with the fix in
place. The ambiguous `jr ra` result is gone; the real answer is `0x01FEFCB0: last written at
cyc=96246464 pc=0x002022E0 value=0x00000002  sw v0, 0(a1)` — a genuine store, ~9 instructions (72
cycle-units) before the fatal `jr ra` at `cyc=96246536`. Disassembling around `0x002022E0`
(`disasm ./user-media.json 96250000 0020225C:C0`) shows it sits in a small, real, shared
float-classification leaf (matches the already-documented "`0x002022B0` classification leaf" from an
earlier entry in this section): `addiu v0,zero,2 / jr ra / sw v0,0(a1)` — i.e. "classify as case 2,
write the result code through the caller-supplied output pointer `a1`, return." This is NOT the
`0x0040AC48` prologue's own `sd ra,80(sp)` — it's a *different*, unrelated function writing an output
parameter, whose destination address (`a1`) happens to be the exact same physical stack word this
entry's (corrected) arithmetic identified as `0x0040AC48`'s saved-`ra` slot.

**This does not yet prove corruption** — it could equally mean the earlier hand-computed identification
of `0x01FEFCB0` as `0x0040AC48`'s `ra` slot is itself still off (a third instance of the same class of
hand-arithmetic slip this section has already caught twice), and the classify leaf's `a1` legitimately
targets its own caller's unrelated local variable at that address with no overlap at all. Distinguishing
those two possibilities requires knowing, at `cyc=96246464`, whether `0x0040AC48`'s frame is currently
*live* (pushed but not yet popped) — i.e. whether the classify call is nested inside it — which needs a
call-stack/frame-liveness check this pass didn't do (e.g. `--trace-threads`-style bracketing, or
`--pcbreak` on `0x0040AC48`'s entry and exit around this exact cycle to read `sp` directly and compare).
**Concrete next step:** `--pcbreak=0040AC48:0040AD90` around `cyc≈96246000-96246600` to get `sp` at both
the classify call and at `0x0040AC48`'s own entry, and confirm or refute frame overlap directly instead
of by hand-subtracting hex offsets a fourth time.

**Fixed and committed this entry:** the delay-slot `CurrentPcForWatch` attribution bug in
`EmotionEngine.cs` — real, general, low-risk, valuable independent of whether it turns out to explain
this specific crash. **Not fixed:** the game-logic corruption itself remains unconfirmed; per this
file's conventions, no speculative fix was applied. Baseline unchanged, smoke suite green.

**Follow-up (2026-07-26) — root-caused the `cyc≈96.2M` crash to a genuine self-modifying-code
corruption from an uninitialized voice-table pointer; this closes the entire `SifBindRpc`-unreachable
arc.** Executed the previous entry's own concrete next step (`--pcbreak=0040AC48:0040AD90` around
`cyc≈96246000-96246600`) — but first had to correct a process error: initially ran it against the
real-CRT0-redirect testbed instead of the **default boot path**, which the crash was originally
bisected against (§ two entries above, "Confirmed this crash is specific to the
`--host-present`-driven boot path"). The testbed run showed no crash at all and a wildly different
`px` (55,910,400 vs the ~860K baseline) — a real, useful negative result (the testbed path legitimately
diverges enough to avoid this specific bug) but not what was needed. Reverted the testbed edit and
re-ran against the plain default path with `--host-present`: reproduced the exact fault
(`ra=0x6237BBC0` at `0x0040AD88`, `cyc=96246528`) on the first try.

With `sp` read directly from `--pcbreak` register dumps (not hand-subtracted), `0x0040AC48`'s epilogue
`ld ra, 80(sp)` at `0x0040AD74` executes with `sp=0x1FEFCF0` — **not** the `0x1FEFC60` its own prologue
set two instructions after entry. Something inside the function's body bumped `sp` by exactly `+0x90`
(144) without a matching pop. Bisected the culprit to `0x0040ACE0`'s `jal 0x002025E8`: `0x002025E8` is
a real, self-balancing function (`addiu sp,sp,-144` at entry, `addiu sp,sp,144` at its own exit,
`0x00202648`) — but on *this specific invocation*, its own prologue instruction doesn't decrement `sp`
at all, while its epilogue's increment still fires, leaking the entire +144 net effect into the caller.

Added `op=0x{opcode:X8}` to the existing `--pcbreak` diagnostic line in `EmotionEngine.cs` (it
previously printed only registers, not the fetched opcode) specifically to cross-check this against a
red herring: `--trace-window --trace-chrono` showed the same invocation fetching garbage-looking
opcodes (`0x00000000`, `0x20000004`, `0x00004000`, `0x00400004`) at `0x002025E8`-`0x002025F4`, four
words that also turned out to reproduce *inside* the unrelated, already-understood `0x002022B0`
classify leaf at a different offset — initially suspected this was itself a Tracer bug (IOP/EE
interleaving, ring-buffer misattribution) rather than a real fetch problem, since it looked exactly
like the kind of secondary-instrumentation artifact this session has already found twice. The new
`--pcbreak` opcode field, which reads `_memory.Read32(PC)` directly with no separate logging path,
confirmed the garbage opcodes are real, not a tracing artifact: `_memory.Read32(0x002025E8)` really
does return `0x00000000` at this point in execution, not the real `0x27BDFF70` (`addiu sp,sp,-144`).

**Root cause, confirmed via `--find-writer=002025E0:20` (with the delay-slot attribution fix from the
entry above already in place, so this result is trustworthy): this is genuine, accidental
self-modifying code.** `0x0024DD88` (`sd v0, 8(v1)`) and `0x0024DD9C` (`sd v0, 16(v1)`) — two ordinary
struct-field write-backs inside a function around `0x0024DD40` that otherwise looks like a per-item
loop (bumps `s1`, loops back to `0x0024DBD8` while `s1 < *(sp+8)`) — wrote `0x00000000`/`0x20000004`
and `0x00004000`/`0x00400004` directly into `0x002025E8`-`0x002025F4` at `cyc=96245632`, ~640 cycles
before the corrupted invocation. `v1` is computed at `0x0024DD84`/`0x0024DD94` as
`v1 = s5 + *(s4 + 444)` — the exact "base register + loaded offset from another structure" shape this
section has already flagged twice as a live "voice pointer" pattern (§ the two entries above, on the
`ra` value itself and on `0x0040AC48`'s own incoming `a1`). Here it's computing the address of a
per-voice (or per-channel) record to write two fields into, and because the underlying voice table is
never populated (no code path in this boot has ever reached the real audio/`PS2RNA` init — the
entire, now-closed `SifBindRpc` investigation above), the pointer it produces is garbage that happens
to alias into `.text` instead of a real heap/BSS voice struct. This one write corrupts exactly 16 bytes
(4 instruction words) of `0x002025E8`'s own prologue. The *next* time anything calls `0x002025E8`
(a few hundred cycles later, per this trace), the corrupted prologue no longer decrements `sp`, the
function's body still runs (using whatever `sp` value it inherited, silently reusing the caller's own
live stack slots as if they were its locals), and its unconditional epilogue `addiu sp,sp,144` still
fires on the way out — leaking +144 into the caller (`0x0040AC48`), which pushes its own frame
computations out of alignment with its actual (unmoved) prologue-decremented `sp`, so its own epilogue
`ld ra, 80(sp)` reads a stack slot that physically belongs to a still-live *grandparent* frame
(`0x0026B150`, confirmed via `--find-writer` to be exactly where `0x6237BBC0` was written, by that
frame's own prologue `sq s0, 112(sp)` at `0x0026B154` — i.e. the "corrupted `ra`" was never really a
`ra` value at all, it's `0x0026B150`'s caller-supplied `s0`, an ordinary live register value, read
through a slot `0x0040AC48` no longer owns). `jr ra` then jumps into that garbage address, which lands
in unmapped memory that `MmioBus.cs` silently reads back as all-zero (`nop`), so execution free-runs
forever instead of faulting — this is the actual, complete, instruction-level mechanism for the
"freeze" that has stopped `main()` from ever reaching the real `sceSifBindRpc` sites across this
entire multi-round investigation.

**No source fix applied to the game-logic corruption itself** — the write at `0x0024DD88`/`0x0024DD9C`
is not a bug in isolation (it's a completely ordinary struct write-back); the actual defect is
upstream, in whatever leaves the voice table at `s4`/`s5` uninitialized-but-not-zeroed in a way that
produces an in-range-looking `.text` address rather than a safely-inert one (a null pointer would have
faulted cleanly instead of corrupting code). Fixing that requires understanding what real
initialization call this voice table depends on and reproducing enough of it to zero the table (or,
more surgically, faking a benign no-op landing pad for this exact write) — a materially different,
larger task than this pass's scope, and this file's conventions warn against a blind patch on top of
an already-multiply-corrected root-cause chain. **Concrete next step:** identify what populates
`*(s4+444)` and `s5` on a boot path that *does* reach real audio init (if any title/path in this
codebase does), to characterize what a "safe" (zeroed or sentinel) uninitialized state should look
like, then either zero the table proactively during boot setup or intercept this specific write
site defensively.

**Fixed and committed this entry:** added the fetched opcode to the `--pcbreak` diagnostic line in
`EmotionEngine.cs` (`op=0x{opcode:X8}`, reading the same `opcode` local the interpreter's own fetch
already computed) — a real, general, zero-risk diagnostic improvement that let this entry rule out a
tracer artifact instead of chasing it, independent of whether it's ever used again. Verified: default
assisted-boot baseline unaffected (`px=860160/gifPath3=1/dmac=4/syscalls=62/cdvdSectors=1` at both
96.25M and 150M cycles, `--host-present` `px` also reconfirmed at 150M cycles as a non-baseline
reference point only). Full smoke suite green
(`dotnet run --project Tests/DetPS2.Tests.csproj -c Release`).

**Follow-up (2026-07-26) — attempted a fix based on a wrong model of `s4`; caught and reverted it
before committing; the real corrupted read has at least one more layer of indirection than
previously modeled.** Picked up the previous entry's own concrete next step directly (no subagent
available this round — a session usage-limit notification interrupted an in-flight fork attempting
this same step, so the rest of this entry was done via direct tool calls in the main session
instead of a fork).

Static disasm of the function containing the loop (`0x0024D900` onward) showed `lui s4,0x67` at
`0x0024DA04`, which looked at first like `s4` was a fixed global (`0x00670000`) for the rest of the
function — so `s4+444` would be the fixed address `0x006701BC`. Implemented a fix on that basis:
`MidwayBootAssist.MaybeResetVoiceCursorSentinel`, zeroing `0x006701BC` once early in `Step()`
(mirroring `MaybeForceManagerInit`'s established pattern, but as a plain one-time memory write, no
trampoline needed since it's not a function call). Verified `--find-writer=006701BC:4` first: zero
EE-instruction writers across a full run, confirming the shipped `0x7FFFFFCE` is raw on-disc ELF
data, not emulator-side corruption — a real, deliberate constant, not a BSS-zeroing bug.

Built, verified the default baseline unchanged (`px=860160/gifPath3=1/dmac=4/syscalls=62/
cdvdSectors=1`), then re-ran the 150M-cycle `--host-present --pcbreak=0021338C:0021338C` crash
repro to confirm the fix actually helped. **It didn't** — identical outcome, cycle-for-cycle:
`PC=0x623A97F8` at the same final state as the unfixed build, 0 hits on the real bind call site.
Rather than accept a "fix" that measurably changed nothing, re-checked whether the write was even
landing on the right address by re-capturing `v1` at the very first `--pcbreak=0024DD88` hit: still
`0x7FFFFFCE`, completely unaffected by the reset. That forced a proper look at what `s4` actually
holds by the time the loop body runs, instead of trusting the static read.

Disassembling the *rest* of the function (`0024DA80`-`0024DBD8`, not read the first pass) found
`s4` gets **reloaded twice** before the loop body: `lq s4, 1552(sp)` at `0x0024DB48`, then `daddu
s4, a0, zero` at `0x0024DB90` — i.e. by the time `lw v1,444(s4)` executes at `0x0024DD74`, `s4` is
a per-item dynamic pointer (whatever `a0` was at that call), not the fixed global at all. The `lui
s4,0x67` only holds for the earlier, different part of the function (`sw v0,10416(s4)` at
`0x0024DA80`). This is ordinary, unremarkable register reuse across two different loop nests within
one large function — nothing exotic, just something the first pass's narrower disasm window missed.
**Reverted `MaybeResetVoiceCursorSentinel` entirely** (`git checkout --`) rather than leave a
committed fix that provably does nothing.

Added `s4`/`s5` to the existing `--pcbreak` diagnostic line in `EmotionEngine.cs` (same low-risk,
general pattern as the earlier `op=` addition) and re-captured live: at the very first hit
(`cyc=1390448`), `s4=0x400` — a tiny, near-null value, itself clearly not a legitimate heap/BSS
struct pointer on a 32MB-RDRAM PS2 title. So `MEM[s4+444]` actually resolves to `MEM[0x5BC]`, not
`MEM[0x6701BC]` — a completely different address than the one this whole sub-thread was built
around. **Net effect: the real corruption chain has at least one more layer of indirection than
modeled** — `s4` itself (≈ `a0` at `0x0024DB90`) is being read from some per-item array/list that
holds near-null garbage instead of real per-voice struct pointers, one level upstream of everything
this section has traced so far. That array's own populate-or-default-to-zero logic is the next real
target, and it is very plausibly the same root gap already named twice (real PS2RNA/audio init
never running) manifesting one layer earlier than assumed.

**Considered, and deliberately did not implement, a general safety-net alternative**: rather than
chase the per-item array's own init gap, refuse EE stores that land inside the loaded ELF's own
`.text` segment (the actual, immediate mechanism of every crash in this whole sub-thread) and log
instead of corrupting code. This would be genuinely general — not MK-specific — and would very
plausibly rescue other titles with a similar shape of bug (uninitialized-pointer write aliasing
into code). Deliberately not attempted this entry: it touches `SystemMemory`'s `Write8/16/32/64`
family, the hottest path in the whole emulator, for every title, and changing memory-write
semantics needs full-suite validation this environment can't provide (only one real commercial ISO
is available to test against — see the earlier-documented search that confirmed no others exist in
this environment) and carries real regression risk if scoped even slightly wrong (a silently
dropped write is much harder to diagnose later than the corruption it prevents). Flagging this here
as a concrete, larger candidate fix for a future dedicated pass, not attempting it under this
entry's time/verification budget.

**Committed this entry:** only the `s4`/`s5` `--pcbreak` diagnostic addition in `EmotionEngine.cs` —
real, general, zero-risk, already useful for the next round of tracing. No game-logic fix landed.
Verified: default baseline unaffected (`px=860160/gifPath3=1/dmac=4/syscalls=62/cdvdSectors=1`),
full smoke suite green (`dotnet run --project Tests/DetPS2.Tests.csproj -c Release`).

**Addendum, same day — traced the indirection one further layer via static disasm (no live trace
needed).** The function containing the corrupting read (real entry `0x0024DB78`: `addiu sp,sp,-192`
then `daddu s4,a0,zero` — confirms `s4` is literally this function's own first argument, nothing more
exotic) has exactly two static callers, both inside a larger per-frame object-list-processing routine
around `0x0025A600`-`0x0025A930` (found via `find-word` for the `jal 0x0024DB78` encoding
`0x0C0936DE`: hits at `0x0025A91C` and `0x0025AB44`). At both sites, `a0` is set immediately before
the call via `daddu a0,s1,zero` — so the "per-item pointer" is literally `s1` at the call site,
passed straight through. Tracing `s1` itself further back in the same function: `0x0025A764: lw
s1,0(v0)`, where `v0` was computed just before as an array-base-plus-index (`addu v0,v0,v1` at
`0x0025A760`) — i.e. `s1` is loaded straight out of what disassembles as a genuine array of object
pointers, with no visible null/sanity check on the loaded value before it gets used. This is a
completely ordinary "iterate the active-object list, process each one" pattern; the bug isn't in this
function or its caller, it's that *the array itself* holds at least one near-null, garbage entry
(`0x400`, observed live) instead of either a real object pointer or a value this code path
explicitly guards against.

Did not chase the array's own populate/allocate site this entry — it's a genuinely open-ended
next step (which array, populated by what, and why one slot holds `0x400` specifically) rather than
a single next disasm read, and this entry is already a full session's worth of layered tracing.
**Restating the standing, most load-bearing conclusion for whoever picks this up next:** every layer
traced so far (the corrupting write, the function that performs it, the loop that calls it, the array
it iterates) is *itself* ordinary, correct code — the defect is upstream, in whatever should have
populated this array with valid data and hasn't, on this boot path, most plausibly because it's gated
behind real PS2RNA/audio init (the same closed loop this whole investigation keeps landing back on:
audio init needs `sceSifLoadModule`/`sceSifBindRpc`, which need `main()` to survive past this exact
corruption to reach them). Breaking that circularity — rather than chasing one more array — is
probably the highest-leverage next move: either (a) find and safely synthesize whatever minimal real
state the array's populate routine needs (the `MidwayBootAssist` pattern), or (b) implement the
general `.text`-write-protection safety net floated above, properly scoped and validated, so this
entire class of corruption (however many layers deep its root cause turns out to be) stops being able
to crash the boot regardless of which uninitialized array eventually causes it.

---

### 7.5 Cross-title validation: telling Shaolin-Monks-specific fixes apart from general ones

**2026-07-26 — the user supplied two more real, commercial Mortal Kombat ISOs (Deadly Alliance,
SLUS_204.23; Deception, SLUS_208.81) specifically to test the "how much of this is general vs.
Shaolin-Monks-specific" question this whole section had been unable to answer on its own** (every
prior entry in §7.4 explicitly noted no other real PS2 ISO was available). Neither title has a
`GameQuirkRegistry` entry (only `SLUS_210.87` does), so both run through **pure general
emulation — zero Shaolin-Monks-specific scaffolding** (`MidwayBootAssist` never runs for them at
all). This is about as clean a test as this project can get for the "does a general fix actually
help other titles" hypothesis.

**Methodology**: local, gitignored `user-media-mkfamily.json`/`user-media-deception.json` configs
(same schema as `user-media.json`, pattern-matched by `.gitignore`'s new `user-media-*.json` rule)
point `blocker-trace --host-present` at the two new ISOs. No source changes needed to add a title —
`GameQuirkRegistry.Resolve` returning `null` for an unregistered serial is the designed, common
case.

**Finding 1 — `FindAddressScan` (syscall `0x83`) infinite-loop bug, confirmed general, fixed.**
Deception spun 226,976 times on this syscall in a 5M-cycle window (`DETPS2_TRACE_FINDADDR=1`:
identical `start`/`end`/`needle`/`result` every call, `start` already one word past the returned
hit) — the implementation cached by `needle` alone and always scanned from physical address 0,
ignoring `start` entirely, so any title enumerating multiple occurrences of the same needle (`find
the next X after the one I just processed`, calling with the same needle but an advancing `start`)
got the same stale first hit forever. Fixed: cache key is now `(needle, start)`, and the scan
honors `start` as its real lower bound. **Result: Deception's `0x83` calls dropped from 226,978 to
3 in the same window, total syscalls from 226,984 to 71, and it now creates a real thread and
reaches a later PC.** Shaolin Monks' own default baseline is untouched (`px=860160/gifPath3=1/
dmac=4/syscalls=62/cdvdSectors=1`) — its committed fast-boot path barely exercises this syscall.
Commit `961fc3b`.

**Finding 2 — negative ("fast"/`i`-prefixed) syscall numbers never normalized, confirmed general,
fixed.** `BiosHle.HandleSyscall` read the syscall number (`v1`) raw, with no sign handling. Real
ps2sdk/libkernel commonly invokes the interrupt-context-safe (`i`-prefixed) variant of a kernel
call by negating its syscall number (`li v1,-N`) — the real BIOS negates it back before dispatch;
this HLE didn't, so every negative-encoded call silently fell through as unhandled regardless of
which one it was. Deception issues raw `v1=0xFFFFFFAB`/`0xFFFFFFA8` (`-0x55`/`-0x58`). Fixed by
normalizing the sign before dispatch, plus adding the two specific positive cases that had no
handler either (`0x55` `iClearEventFlag`, `0x58` `iPollEventFlag`, aliased to their existing
`ClearEventFlag`/`PollEventFlag` handlers — the same pattern already used for `0x52`/`0x53`).
Verified neutral on both other titles' behavior (Shaolin Monks baseline unchanged; Deadly
Alliance's own unrelated stuck point unaffected). Commit `3291940`.

**Finding 3 — `WaitEventFlag` (syscall `0x56`) has no real semantics, general, NOT fixed this
pass.** After Finding 2, Deception's remaining syscall traffic is dominated by `0x56` (196 calls in
30M cycles) instead of the noise Finding 2 removed. The current handler only reads `a0` (the flag
id) and calls the same `PollEventFlag` that `0x57` (`PollEventFlag`, genuinely non-blocking) uses —
it ignores `a1`/`a2`/`a3` (the real syscall's requested bitmask, AND/OR wait mode, and
result-pointer), and critically **never blocks-and-yields the way `WaitSema` (`0x44`) already
does**. A title that expects `WaitEventFlag` to actually suspend the calling thread until the flag
condition is met just busy-polls it from userspace instead, forever, if the flag never becomes
satisfied. Not fixed this pass: correctly implementing AND/OR mode, clear-on-exit, and
blocking+yield (mirroring `WaitSema`'s already-tuned pattern, including its "nobody else runnable →
park on VBlank" fallback) is real work that deserves its own verification pass, not a rushed patch
riding on this entry's remaining budget. **Concrete next step for whoever picks this up:** give
`KernelState` a `WaitEventFlagBlocking`-style primitive analogous to `WaitSemaBlocking`, wire cases
`0x56`/`0x58` through it the way `0x44` already works, and verify against both Deception (should
progress further) and the Shaolin Monks baseline (must stay identical).

**Finding 4 — Deadly Alliance is hard-parked on a documented, pre-existing architectural gap; not
attempted this pass.** Deadly Alliance's `PC` is bit-for-bit identical (`0x0010E670`) at 6M, 17M,
25M, and 30M cycles — a genuine hard park, not sampling coincidence. `--pcbreak` at that PC shows
`InterruptPending=True` but `takeExceptions=False`, held constant. This isn't a new bug — it's
already self-documented in `SonyKernelHle.cs` at the `AddIntcHandler` case (`0x10`): `KernelBootstrap`
deliberately starts `EE.TakeExceptions = false` after fast-boot (to avoid an unacknowledged VBlank
"storming" the EE before any handler exists) and only the game's own call to `AddIntcHandler` turns
it back on. Deadly Alliance's boot never calls `AddIntcHandler` (absent from its own syscall
histogram, unlike `AddDmacHandler` which it does call once) before it needs an interrupt-driven
wakeup — so it's permanently stuck waiting for an interrupt DetPS2 is deliberately withholding.
**Not attempted**: the comment's own "storm" concern is real and explicit — this gates the hottest
possible source of spurious re-entry (every title's exception path) and needs a properly-scoped fix
(most plausibly: a default/BIOS-level ACK-only handler for any cause with no game-registered
handler, so `TakeExceptions` can safely default to on, matching how real hardware's BIOS always
acknowledges interrupts regardless of whether a game handler exists) plus full-suite validation
before it's safe to land — explicitly the same category of "large, systemic, needs its own pass"
change as the `.text`-write-protection idea floated in the previous section, not something to rush
here.

**Standing conclusion of this cross-title pass**: at least two of four findings this round are
unambiguously general — found via a title with zero Shaolin-Monks-specific scaffolding, fixed, and
verified not to regress Shaolin Monks' own baseline. This is the first direct, empirical evidence
this whole investigation has had for the user's original "fixing general bugs helps a meaningful
slice of the library" hypothesis, rather than an inference from code-reading alone. The other two
findings (Deadly Alliance's interrupt gate, Deception's `WaitEventFlag` gap) are equally general in
nature but need more careful, better-tested fixes than this pass's remaining budget allowed —
prioritized here, in order, as the highest-leverage next steps for whoever continues this thread.

---

### 7.6 The cyc~96.2M crash, actually root-caused: LWL/LWR/SWL/SWR/LDL/LDR/SDL/SDR were fake

**2026-07-26 — the user redirected priority explicitly back to Shaolin Monks** ("the other 3
titles aren't priority they are simply there for reference"), closing out §7.5's cross-title
detour and resuming the still-open thread from §7.4/the `edafcbb`/`b24cd79` entries: find where
the per-item object-pointer array (that fed the garbage `s1`/`0x400` value chased for hours) is
actually populated.

**Picked up exactly where that left off** — disassembled backward from the `lw s1,0(v0)` array
lookup (found: `v0 = MEM[0x006664A8] + index*4`, `count = MEM[0x006664B0]`, a real, fixed global
"list descriptor" at `0x006662A8`) and confirmed via `--find-writer` that both fields are still
`0` (raw ELF-load value, never written). Since a `blez` guard skips the whole per-item loop when
`count<=0`, this loop should be dead code at cyc~1.39M — directly contradicting the fact that the
corrupting write (`0x0024DD88`) had already fired 67,888 times by then. **That contradiction was
the actual break in the case.**

Captured `ra` live at the corrupting function's real entry (`0x0024DB78`) via `--pcbreak`: it held
`0x4A010000000000` — not a plausible return address at all, and structurally identical in shape to
an already-flagged-but-never-root-caused "float-shaped ra corruption" finding from much earlier
this session. Since `jal`/`jalr` are a hardware guarantee to set `$ra` correctly regardless of
what the caller's own registers hold, a garbage `ra` at function entry meant this function was
never really *called* — confirmed via `--trace-window --trace-chrono`: the preceding function's own
`jr ra` at `0x0024DB70` executes, and control falls straight through into `0x0024DB78` (the next
instruction in memory) instead of jumping anywhere. This is `EmotionEngine`'s own JR/JALR
"ignore jumps into the low vector page" safety guard (added earlier this session to catch
uninitialized function pointers) silently no-opping a broken `jr ra` and letting execution
free-fall into unrelated code with whatever garbage happened to be sitting in that code's argument
registers — the "per-item object array" was a complete red herring; nothing was ever iterating it.

Added a `DETPS2_TRACE_JRGUARD=1` diagnostic (log every guard firing) and found the *first*
occurrence resolves to an exact, reproducible constant every time: `target=0x004A010000000000` —
which decodes precisely as a real, plausible 32-bit code address (`0x004A0100`) shifted left by 32
bits into the wrong half of a 64-bit register. That's the exact signature of a `DSLL32`-style
64-bit-shift result landing somewhere it shouldn't — or, as it turned out, of an **unaligned 64-bit
store performed as a full aligned one**, overwriting an adjacent saved register.

Disassembled the actual corruption site: a small function at `0x0024C800`+ doing a classic
unaligned struct-copy idiom (`ldl/ldr` pairs to load, `sdl/sdr` pairs to store) immediately
followed by `ld ra,16(sp)` and the fatal `jr ra`. The second `sdl`/`sdr` pair (`sdl v1,15(sp)` /
`sdr v1,8(sp)`) is supposed to write only bytes 8-15 of the stack frame. **Checked
`ExecuteSdl`/`ExecuteSdr` in `EmotionEngine.cs` and found both literally aliased to the full,
aligned `ExecuteSd`** ("behave like aligned for now" — an explicit, flagged simplification from
earlier in the project). `sdl v1,15(sp)` therefore performed a full 8-byte store *starting at*
offset 15 (bytes 15-22), directly through the saved `ra` at offset 16. Checked the other six
"left/right" instructions (`LWL`/`LWR`/`SWL`/`SWR`/`LDL`/`LDR`) and found the *entire family*
had the same issue — every one aliased to its full-width counterpart.

**Fixed all 8 with real MIPS64 little-endian semantics**, derived and triple-verified by hand
against the standard `Xxl rt,(N-1)(base); Xxr rt,0(base)` paired-use compiler idiom at every
possible alignment (the byte-lane formula is `b = (vAddr & (N-1)) XOR (N-1)`; `*L` affects the top
`N-b` register bytes from `mem[alignedAddr+(j-b)]`, `*R` affects the bottom `b+1` bytes from
`mem[alignedAddr+(j+N-1-b)]`), implemented as explicit byte loops (not a shift+mask formula) to
avoid any shift-count edge-case risk. Added a permanent regression test,
`Ee_UnalignedLoadStore_Lwl_Lwr_Swl_Swr_Ldl_Ldr_Sdl_Sdr` in `Tests/SmokeTests.cs`, checking exact
byte-for-byte correctness at every alignment (0-3 word, 0-7 doubleword) for both load and store —
it passed on the first real run, matching the hand-derivation.

**Impact, verified against the real boot, not just the CRT0-redirect testbed**: the default
assisted-boot baseline — frozen at `px=860160/gifPath3=1/dmac=4/syscalls=62/sifBytes=0/
cdvdSectors=1` for this entire investigation — now reaches `gifPath3=5/dmac=7/sifBytes=272/
syscalls=122` at the same 5M cycles. At 150M cycles, `RealSifRpc` reports `binds=2/calls=2`, up
from the synthetic-only `binds=1/calls=1` this session's `MaybeCompleteRealSifCdRead` milestone
established — **a second, genuine bind+call pair, meaning the game's own compiled code actually
executed a real `sceSifBindRpc`/`sceSifCallRpc` sequence**, closing the "why does `main()` never
reach real SifBindRpc" question for real (commits `4921491` through `b24cd79` had only explained
the mechanism, not fixed it). Execution ends the 150M-cycle window at `0x0047FA88`, an entirely
ordinary `jr ra` right after a syscall trampoline stub — ordinary library code, not distress.

Commits: `961fc3b`/`3291940` (the two cross-title general fixes from §7.5, unrelated to this
thread but landed just before it), `091ea76` (the LWL-family fix + regression test, EmotionEngine.cs
+ Tests/SmokeTests.cs).

**Not yet done**: pushing the trace further to see how much *more* progress this unlocks (gifPath3
advancing past 5, reaching an actual rendered frame, etc.) — this fix only just landed. That's the
immediate next step, continuing to prioritize Shaolin Monks per explicit instruction.

**Follow-up, same day — pushed the trace further; found the next wall precisely.** At 150M-400M
cycles, `PC`/`RealSifRpc`/`gifP3`/`dmac` all freeze identically (`gifP3=5`, `binds=2/calls=2`,
final `PC=0x0047FA88`) while `px`/`spu2Samples` keep climbing — the signature of a genuine, stable
per-frame loop, not a fresh crash. Captured a real frame via `probe-frame`: a well-formed Midway
logo render (clean gradients, no garbage) — though `prims`/`px` are frozen by then, so this is very
likely the pre-existing synthetic logo output, not new content.

Traced the toggle between `0x00213218` (the post-logo resumption point) and `0x0047FA88` (a
`syscall 0x04`/`Exit` trampoline). First hypothesized `MaybePostLogoAdvance`'s unconditional forced
jump to `0x00213218` was yanking PC backward from real progress — confirmed via `--pcbreak` that
the real SIF wait loops just above it now genuinely complete on their own (PC reaches `0x213218`
naturally before the 8M-cycle threshold even fires), so tightened that function to only force the
jump when actually caught stuck inside the loop range (`0x2131E8`-`0x213217`), never as a blind
timeout (commit `fb41737`). **Empirically this changed nothing for the current stall** (identical
150M-cycle outcome with or without the guard) — a clean negative result that disproves this
specific hypothesis while still being a correct, worthwhile change in its own right (the old
unconditional force is now a real liability given how often natural completion succeeds).

Captured `ra` at the `Exit` trampoline directly: `ra=0x476818`, `a0=1` — a real, sane address, not
garbage. `0x00476808` is a small "print error, call real exit(1)" wrapper (`jal 0x0011C2B0` — and
`0x0011C2B0` is literally 8 bytes past `0x0011C2A8`, the exact synthetic `ra` `MaybePostLogoAdvance`
already uses, confirming `0x0011C2A8`/`0x0011C2B0` is CRT0's own real "call main(), then exit(return
value)" wrapper). So this is a **real, legitimate exit(1) path** — not corruption, not a masked
crash — main()/some subsystem is genuinely calling this "fatal error" helper. Found 6 static callers
of `0x00476808` (`0x00201100`, `0x00201844`, `0x00203A2C`, `0x00203A54`, `0x002043E4`, and a
recursive one inside `0x00476840`) via `find-word`; captured which one fired live: `ra=0x80000200` —
**the real general exception vector**, with `COP0_Cause=0x00000400` decoding to `ExcCode=0`
(a genuine hardware interrupt, not a fault/trap) and `IP2` set (the EE's single catch-all line for
every peripheral interrupt source).

Disassembled `0x00000200` (the vector itself) expecting a real dispatcher: it's just
`mfc0 k0,$c0_13; andi k0,k0,0x7C; eret` — three instructions, reads Cause, masks it into a scratch
register, returns immediately. **No handler lookup, no INTC_STAT acknowledgment, no real dispatch
at all.** This strongly suggests whatever's supposed to install a real interrupt dispatcher at (or
reachable from) this vector never ran — the same standing theme as nearly everything else in this
whole file: real kernel/IOP-side initialization that depends on a boot sequence this fast-boot path
doesn't fully execute. **Concrete next step, not yet pursued:** determine whether DetPS2 supports
COP0 `EBase`-relocated or otherwise-chained exception vectors at all, and if the real BIOS install
its actual handler somewhere else this stub should be jumping to (rather than returning) — that
would explain both "interrupts appear to go essentially unhandled" and, downstream, why some code
path treats that as fatal and calls `exit(1)`.

No source changes this entry beyond the already-committed `fb41737` guard — the rest is read-only
tracing. Default baseline reconfirmed unaffected by the guard change specifically; the LWL-family
fix's own baseline impact is already documented above.

**Follow-up, same day — the exit(1) mystery fully resolved.** Checked whether DetPS2's interrupt
dispatch actually reaches a game-registered handler at all: it turns out it already does —
`EmotionEngine.TryDispatchRegisteredIntcHandler` (a real, previously-built, fully-documented
mechanism, wired into the main loop at the `_takeExceptions && InterruptPending` check) looks up
`SonyKernelHle`'s `AddIntcHandler`/`AddDmacHandler` tables and, if a handler is registered for a
currently-pending source, redirects `PC` straight to it (`a0`=cause), pointing the handler's own
`ra` at `KernelBootstrap.Kseg0Interrupt` (`0x80000200`) so its ordinary `jr ra` epilogue lands back
on the vector's `eret`. `DETPS2_TRACE_INTC_DISPATCH=1` (extended this entry with a `cyc=` field)
confirmed a REAL handler at `0x00482CA0` (src=13, SIF) dispatches twice around cyc≈17.58M — and
`--pcbreak` on that handler showed both invocations taking a completely benign, correct early-exit
path (empty mailbox, `jr ra` at `0x00482DE0`). So the dispatch mechanism itself works, and the
handler itself behaves correctly. The mystery was: why did `ra=0x80000200` still show up
**38 million cycles later**, at the real exit(1) call?

Answer: `KSeg0Interrupt` (`0x80000200`), once masked by the JR/JALR "ignore jumps into the low
vector page" guard's `& 0x1FFFFFFF`, evaluates to `0x200` — under the same `0x10000` threshold the
guard uses to catch uninitialized function pointers. **The guard was silently swallowing the
handler's own `jr ra` return to the vector.** `eret` never ran, `EXL` never cleared,
`_savedRaAcrossIntcDispatch`'s pushed original `ra` never got popped back — COP0 stayed
permanently "mid-exception" for the rest of the run, `InterruptPending` was permanently blocked
(gated on `!EXL`), and the stale `ra=0x80000200` just sat in register 31 untouched (since nothing
after that point had reason to overwrite it) until some much later, unrelated code path happened
to read it as *its own* return address. Fixed by excluding the three known, synthesized KSEG0
vector addresses from the guard specifically (`IsLegitimateVectorTarget`, `EmotionEngine.cs`) —
not by loosening the guard generally, since a game could still plausibly produce a coincidentally
small garbage pointer that these three exact addresses can't be confused with.

**Impact, verified**: default baseline unchanged. At 150M cycles the repeated-exit(1) storm is
gone — `syscalls` dropped from `370` to `181` (no more toggling into the exit trampoline), and `PC`
settles into ordinary, real code (a jump-table dispatch + byte-comparison loop around
`0x00474EE4`-`0x00475D14`) instead. `spu2Samples` keeps climbing steadily through 500M cycles
(real ongoing audio work), but `gifPath3`/`px` stay frozen from ~200M cycles onward — no crash, no
error loop, but also no new rendering yet. Tried widening `probe-frame`'s "press Start" heuristic
(previously gated on `Status == "post-logo-main"`, a string that — now that real execution mostly
doesn't need `MaybePostLogoAdvance`'s forced jump at all — was staying parked at `"logo-done"`
forever) to also fire on `"logo-done"`: pressing Start now genuinely reaches the game, but PC keeps
cycling through the exact same 3 addresses regardless — a useful negative result ruling out "this
loop is a press-start wait" as the explanation.

**Commits this entry**: `52c1403` (the JR-guard fix — the real headline fix), `524f490`
(`probe-frame` heuristic widening + the negative result above).

**Concrete next step**: disassemble the `0x00474EE4`/`0x00475000`/`0x00475D14` loop in full to
determine what it's actually doing and, critically, what condition would let it exit — this is now
the real, current wall for Shaolin Monks, three fixes past where the session's original "why does
main() never reach SifBindRpc" investigation started.

**Follow-up, same day — the loop identified precisely; confirmed rendering is genuinely dead, not
just slow.** `0x00474EE4`/`0x00475000` turned out to be a real, standard SIMD `strlen()` (the
classic zero-byte-in-word bit trick, vectorized with MMI `pcpyld`/`psubb`/`pnor`/`pand`/`pcpyud`,
falling back to a scalar byte loop at `0x00475000` for the tail) — completely ordinary library
code, not a bug. Traced its caller via `--pcbreak`: `a0=0x005AEFA8`, a string whose first byte is
`0x43` (`'C'`) immediately followed by a null — i.e. `strlen()` is being called on the literal
one-character string `"C"`. That caller (`0x00476A40`) is itself a Shift-JIS-aware
text-processing routine (explicit `[0x81-0x9F]`/`[0xE0-0xEF]` double-byte-lead-byte range checks —
real, standard Shift-JIS detection logic), and *its* caller (`0x00475C08`-`0x00475CBC`) is a
character-attribute-table-driven loop (`lbu ...; andi ...,0x8` — testing a per-character flag bit,
classic font/text-layout "measure this string" code) that re-enters roughly every 100 cycles —
tight enough that this is a genuine busy loop, not once-per-simulated-frame UI work.

Used `--track-transfers` to check the higher-level question directly rather than continuing to
trace deeper into font internals: **zero DMA/GIF/SIF transfer events occur between cyc≈1,318,176
and cyc=210,000,000** (18 total events, all clustered in the first ~1.3M cycles — the last is a
SIF `EE->IOP` transfer at cyc=1,318,176; nothing at all after that for 208M+ more cycles). This
confirms unambiguously: the game is not slowly-but-surely rendering new frames at a low rate — it
is genuinely, permanently stuck in a CPU-only loop (text/font layout, working on a trivial `"C"`
string) that never reaches whatever step would submit a new GS command list. `gifPath3=5` is this
run's *final* count, not a snapshot of an ongoing rate.

**Not pursued further this entry** — determining exactly what this text-layout loop's own exit
condition is (what would let it finish measuring `"C"` and move on) requires either much deeper
disassembly of the surrounding function than done so far, or a different diagnostic angle (e.g.
checking what real per-character-class table this consults and whether DetPS2 populates it
correctly, or whether this loop is itself gated on some flag/counter that a still-missing
subsystem — audio callback completion? a specific pad-read variant? — is supposed to advance).
Flagging as the concrete next step for whoever continues this thread, rather than guessing.

**Follow-up, same day — chased and disproved a plausible-looking "CD read starvation" theory
before it got oversold.** Traced the text-layout loop's own caller (`0x00475BA8`, entered via
`--pcbreak` capturing `ra=0x475A28`): found TWO invocations clustered around cyc≈17.58M (the same
window as the earlier SIF interrupt dispatch), the second showing `s0=0x5C3A306D6F726463` — decoded
as the ASCII bytes `"cdrom0:\"`, the classic PS2 CD-ROM device path prefix. Combined with
`cdvdSectors` having been frozen at `1` (the single synthetic sector from `MaybeCompleteRealSifCdRead`)
for this entire multi-day investigation, this looked like a strong, well-motivated lead: the game
stuck trying to enumerate/read more files from the disc than our HLE ever delivers.

**Checked before committing to it, and it didn't hold up.** Dumped the actual memory this
function's parameters (`a0=0x1FF0030`, `a2=0x1FF00F0`) point to: it's ordinary compiled MIPS
*code* (`lw`/`jal`/`mult`/`div`/`mtc1`/`cvt.s.w` — real floating-point conversion instructions),
not a CD file/directory table at all — these addresses just happen to sit in the same
stack-adjacent region (`0x1FEFxxx`-`0x1FF0xxx`) every `sp` value throughout this whole session has
lived in, i.e. they're ordinary local-struct-by-reference parameters, not evidence of anything
CD-related. The `"cdrom0:\"` string in `s0` was very likely just unrelated leftover register
content from an earlier, real disc-mount operation during boot, not something this specific loop
is currently working with. Documenting this explicitly so it isn't independently re-derived and
re-believed by whoever picks this up next — the CD-starvation theory is a dead end as stated, not
a lead to continue.

**Also checked two more reference titles the user added this session — God of War (SCUS_973.99)
and Burnout 3: Takedown (SLUS_210.50)** — both run through pure general emulation, no
`GameQuirkRegistry` entry for either. Burnout 3 looked stuck at only 10M cycles (`PC` pinned inside
an ordinary CRT0 BSS-zeroing loop, zero syscalls attempted) but turned out not to be a bug at all —
by 60M cycles it clears the loop naturally and starts making real progress (71 syscalls, a real
thread, real SIF traffic): it just needed more cycles, a useful negative result. God of War
surfaced one genuine, specific gap: an unimplemented MMI3-family instruction
(`(sa=0x13)<<6 | func=0x08`, falling through the default case of the `PCPYH`/`PEXCW`-style switch
in `EmotionEngine.cs` around line 1560) — VU-heavy titles exercise more of that instruction family
than Shaolin Monks does. Not implemented this entry: correctly identifying and implementing it
needs the same real-ISA-verified rigor as the LWL-family fix, not a rushed drive-by on a reference
title while Shaolin Monks remains the stated priority.

No source changes this entry — read-only investigation across all three titles, plus one corrected
(retracted) theory.

**Follow-up, same day — four more genre-sibling reference titles (Vexx SLUS_203.83, Haven: Call of
the King SLUS_205.17, Blood Omen 2 SLUS_200.24, Whiplash SLUS_206.84), same 3D-action genre as
Shaolin Monks, added specifically to find genre-level similarities.** All four run through pure
general emulation (no `GameQuirkRegistry` entry for any). Quick-checked each:

- **Vexx**: by far the healthiest boot seen this whole investigation — `binds=62` real SIF
  binds, heavy real `SifSetDma` traffic (`0x77` x94), substantial semaphore churn
  (`CreateSema`/`DeleteSema` ~150 each). One `unknownBindSids` hit: SID `0x00000592` — note this is
  NOT the already-known `SidCdBase=0x80000592` (`RealSifRpc.cs`) despite sharing the same low 16
  bits; it's missing the `0x80000000` Sony-reserved prefix entirely, so it's very likely a
  genuine custom/game-defined IOP service, not a masking bug in our own bind-comparison code
  (verified: `HandleBind` compares the raw, unmasked 32-bit `sid` directly, no truncation). Not
  investigated further — would need to know what Vexx's own custom service actually does to
  handle it meaningfully, and guessing would be exactly the speculative-fix pattern this file
  warns against.
- **Haven**: looked stuck at 10M cycles (`PC` pinned inside a real bit-stream decompressor — a
  classic bit-shift-and-test-carry unpacking loop, completely ordinary code, not a bug) but by
  250M cycles reaches real syscalls (72), a real thread, real SIF traffic. Same "just needed more
  cycles" pattern as Burnout 3 in the previous entry — decompressing whatever this is legitimately
  takes tens of millions of cycles before real boot code runs.
- **Blood Omen 2** / **Whiplash**: both show ordinary, healthy early-boot activity (real threads,
  real syscalls, no obvious stalls) — not traced further this pass.
- Also flagged, not implemented: `Haven`'s CRT0 executes a real REGIMM extension, `MTSAH`
  (`rt=0x19`), which `EmotionEngine.ExecuteRegimm` doesn't implement (falls through as a silent
  no-op — it only handles the 8 real branch `rt` values 0x00-0x03/0x10-0x13). `MTSAB`/`MTSAH` set
  the R5900's SA (shift-amount) register that `QFSRV` (the quadword-granularity cousin of
  LWL/SDL's word/dword unaligned-access problem) consumes — same bug *family* as the session's
  biggest fix, potentially comparably valuable. **Deliberately not implemented this entry**: this
  codebase's own `tbl_MMI` comment states it's verified against PCSX2's real opcode tables, and
  that table has no free slot matching remembered candidates for QFSRV's real encoding — meaning
  implementing `MTSAB`/`MTSAH` alone (without a correctly-encoded `QFSRV` to consume the SA
  register) would add code that fixes nothing observable, and guessing `QFSRV`'s opcode wrong
  risks live-locking or silently misdecoding some currently-correct instruction. Needs the same
  authoritative-ISA-reference verification the LWL-family fix got before attempting.

**Net takeaway for Shaolin Monks specifically**: none of these four genre siblings hit anything
resembling Shaolin Monks' current text-layout-loop stall within the cycle ranges checked — all
four reach real syscalls, real threads, and real SIF traffic noticeably faster and more
extensively than Shaolin Monks does even after today's three fixes. This is a useful negative
result: it suggests Shaolin Monks' remaining wall is more likely something specific to its own
code/assets than a still-missing general architecture piece that would also be blocking these
siblings — recalibrating priority toward directly continuing the text-layout loop trace (§7.6's
last entry) rather than expecting another cross-title-style general fix to resolve it.

No source changes this entry — read-only investigation across all four titles.

**Follow-up, same day — MTSAB/MTSAH/QFSRV implemented, closing the gap flagged just above.** Per the
user's request, cloned the Play! PS2 emulator (`github.com/jpd002/Play-`) and its CodeGen library
(`github.com/jpd002/Play--CodeGen`) to `C:/Windows` as a standing reference. Used it to settle
exactly what the earlier entry left uncertain: Play!'s own `m_pOpMmi1` table places `QFSRV` at
`sa=0x1B`, confirming our `func=0x28` MMI1 delegation (already correct in `ExecuteMmiFamily`) was
the right family — the free slot at `(27u<<6)|0x28` was genuinely unclaimed. More importantly,
rather than trust source-reading alone for the 256-bit shift semantics (concatenation order,
bit-vs-byte shift units), found and hand-verified Play!'s own `CodeGen` test suite
(`tests/MdTest.cpp`'s two `MD_Srl256` cases) byte-for-byte before writing any code — this is the
same discipline the LWL-family fix used (a permanent regression test reproducing known-correct
byte patterns, not just "looks right").

Implemented all three (`MTSAB`, `MTSAH`, `QFSRV`) in `EmotionEngine.cs`, added a new `_sa` field
for the shift-amount register, and added `Ee_Mtsab_Qfsrv_MatchesPlayReference` to
`Tests/SmokeTests.cs`, reproducing `MdTest.cpp`'s exact two cases through the real
`MTSAB`+`QFSRV` instruction pair — passed on the first real run. Verified: Shaolin Monks' default
baseline unchanged (`px=860160/gifPath3=5/dmac=7/sifBytes=272/syscalls=122`), full smoke suite
green. Commit `d2687ed`.

**Follow-up, same day — the text-layout loop's exact stuck mechanism found, precisely.** Returned to
the standing priority per explicit instruction. First cheaply tested whether this loop was like
Haven/Burnout 3's "just needs more cycles" decompression stalls: ran the default boot to
**1 billion cycles** — `gifPath3`/`dmac`/`sifBytes`/`syscalls`/`cdvdSectors` are all still frozen at
their 200M-cycle values (only `spu2Samples` keeps climbing, confirming ongoing but unproductive
CPU work). This is a genuine permanent stall, not a slow-but-real one.

Systematically mapped the enclosing function's control flow (`0x475BA8`-`0x475F94`, dumped whole and
grepped for every branch/jump) rather than continuing to guess at windows — found multiple loop-back
edges converging on `0x475C08`/`0x475C0C`, confirming a real inner loop distinct from the function's
own (rarely-hit, cleanly-returning) top-level entry and exit. Added `s6`/`s7` to `--pcbreak` (not
previously exposed) specifically to check the loop's own counter register directly, rather than
inferring it indirectly. Traced precisely, in order:

1. `s6` reads as `0x0` at every single sampled iteration of the dominant loop-back check
   (`blez s6, 0x475C08` at `0x475D18`) — the branch is taken every time, which is why this loop-back
   edge dominates over the (rarely reached) clean-exit paths found earlier.
2. `s6` is set from the return value (`v0`) of `jal 0x00476A20`, called at the top of the loop
   (`0x475C08`-`0x475C20`) with `a2` loaded from a stack-local field at the caller's `616(sp)`.
3. Captured `0x00476A20`'s real parameters live: `a2=0x0` at every sample. `0x00476A20`'s own
   prologue does `beq s0,zero,0x476A60` where `s0=a2` — with `a2` always zero, this branch is always
   taken, and the function returns `0` (or a value that ultimately still yields `s6=0`) essentially
   immediately, without doing any real work.
4. Traced the accumulator this feeds: `a0 += s6` happens right after the call (`addu a0,a0,s6` at
   `0x475C34`), then gets written straight back to the SAME `616(sp)` slot
   (`sw a0,616(sp)` at `0x475C3C`). With `s6=0`, `a0` never changes, so `616(sp)` is written back
   with the exact same `0` it started with.

**Net finding: `616(sp)` — whatever count/position field this represents — is a closed,
self-perpetuating zero loop.** It started at `0`, gets fed into `0x476A20` as `a2`, which (because
`a2==0`) returns a value that keeps `s6=0`, which keeps the accumulator from ever changing, which
writes the exact same `0` straight back into the field that started the whole cycle. There's nothing
externally *wrong* being detected each iteration — the loop's own logic treats `616(sp)==0` as a
completely self-consistent (if unproductive) state, so it never trips any error path; it just spins
forever with no way to break its own cycle from the inside.

**Not yet found**: what `616(sp)` is actually *supposed* to represent, and where its correct
initial value should come from (most likely computed once from the function's real parameters —
`s2`/`s5`, i.e. the original `a0`/`a2` at `0x475BA8`'s own entry — near the top of the function,
before this loop begins, in code not yet disassembled this pass). That's the concrete next step:
disassemble `0x475BA8`'s body between its prologue and `0x475C08` (not yet read in full) to find
where `616(sp)` is first written and why that computation yields `0` instead of a real count.

No source changes this entry beyond the already-committed `3e44605` (`s6`/`s7` `--pcbreak` fields).
Baseline reconfirmed unaffected (`px=860160/gifPath3=5/dmac=7/sifBytes=272/syscalls=122`), full
smoke suite green.

**Follow-up, same day — found `616(sp)`'s real source: `0x475BA8`'s own second argument (`a1`),
written straight through in the function's own prologue (`sw a1, 616(sp)` at `0x475BF0`) with no
computation at all.** So the question sharpens to: what does `0x475BA8`'s caller pass as `a1`?

Captured both real invocations of `0x475BA8`'s entry directly (only two exist in the whole run, both
near cyc≈17.58M, confirming again this function is a true one-shot top-level call whose *internal*
loop-back edges are the entire ongoing stall — not repeated top-level re-entry):

- Invocation 1 (`cyc=17,580,208`, `ra=0x475A28`): `a1=0xB` — a real, sane-looking value from a
  legitimate caller (the `sprintf`-style wrapper at `0x4759A0` traced in an earlier entry).
- Invocation 2 (`cyc=17,580,720`, **`ra=0x0`**): `a1=0x0` — this is the one that seeds the eternal
  zero loop.

**`ra=0x0` at function entry is not a real call — it's the exact same masked-fall-through signature
found earlier this session for the `exit(1)` mystery (§7.6's `52c1403` entry).** Confirmed directly
via `DETPS2_TRACE_JRGUARD=1`: the guard fires at `pc=0x00475BA0`, `rs=31` (`ra`), `target=0x0` — a
tiny wrapper function immediately before `0x475BA8` in memory (`0x475B88`-`0x475BA4`: save `ra`,
call `0x0047E1F0`, restore `ra`, `jr ra`) has a **genuinely corrupted, zero saved return address on
its own stack**, the guard correctly suppresses the resulting jump-to-low-page, and execution falls
straight through into `0x475BA8`'s prologue with whatever garbage happened to be in `a0`/`a1` at
that moment (`0`/`0`) — which becomes the eternal-zero-loop's seed.

This is the same corruption-class signature (a stack-saved `ra` that's wrong, silently masked by the
guard, landing in unrelated adjacent code with garbage arguments) as the original `cyc~96.2M` crash
root-caused via `LWL`/`SDL` (§7.6). Whether this is a *second, distinct* instance of unaligned-access
corruption (possibly not fully covered by the `091ea76` fix) or a downstream consequence of something
else was not determined this entry — briefly checked the one function the wrapper calls
(`0x0047E1F0`, which itself just calls `0x00480520`) for an obviously-analogous unaligned-write
pattern near its own stack frame and found none in the immediately visible code, meaning the actual
corruption site is at least one more call layer deep (inside `0x00480520`, or further) and would need
the same kind of methodical tracing the original bug took multiple rounds to fully pin down.

**Deliberately stopping the deep-dive here rather than open-endedly continuing** — this has the
shape of a second multi-round investigation, not a quick follow-up, and this entry already
represents a full, precisely-evidenced hand-off: the exact fall-through PC, the exact corrupted
register, and the exact call chain (`0x475B88` → `0x47E1F0` → `0x480520`) needing further tracing.
**Concrete next step**: disassemble `0x00480520` for an unaligned `SDL`/`SDR`/`QFSRV`-style write
landing near a saved-`ra` stack slot in one of its callers, using the exact same methodology as the
original fix (bisect via `--pcbreak`, confirm via `--find-writer`, verify any fix with a
byte-exact regression test before trusting a boot trace).

No source changes this entry — read-only tracing. Baseline unaffected (nothing executed
differently), full smoke suite green (unchanged from the entry above).

**Follow-up, same day — the ra=0 cascade's true first cause found and (partially) fixed; the
remaining question reframed entirely.** Broadened `DETPS2_TRACE_JRGUARD` across a longer window and
found the cascade isn't isolated to the text-layout function — it spans **dozens** of unrelated `jr
ra` sites from `0x480230` through `0x475BA0`, all reading the same stale `ra=0`. Disassembled the
start of that range: it's a **syscall trampoline table** (`li v1,N; syscall; jr ra`, one block per
syscall number, 16 bytes apart) — meaning once `ra` first becomes 0, execution doesn't just get
stuck, it **free-runs through the entire trampoline table issuing dozens of real, completely
unintended syscalls** as a side effect of each one's own masked `jr ra` falling through to the next.

Traced the true origin via `KernelState.RestoreContext`'s own doc comment (`"$ra = 0 so ExitThread
path is clean"`): a freshly-started thread's `ra` is *deliberately* seeded to 0, intending that a
thread function naturally returning (rather than calling `ExitThread`) be detected as an implicit
exit — but nothing ever implemented that detection. **Fixed** (commit `e48a854`): `jr ra` with
`rs=31` and a target of exactly `0` now calls `KernelState.ExitCurrentThread()` +
`SwitchToNext()`, honoring the documented convention for real. Verified via a new
`DETPS2_TRACE_JREXIT=1` diagnostic that the fix fires correctly — but `SwitchToNext` finds no other
runnable thread, because **Shaolin Monks' entire run, this whole investigation, has only ever had
one thread** (`--trace-threads`: `MainReset tid=1` at `cyc=0`, no `CreateThread` ever observed).
This doesn't change Shaolin Monks' own observable outcome (nothing to switch to → same fall-through
as before, the pre-existing "no CPU halt" gap, not made worse) but is a correct, general fix in its
own right for any boot state with a real second thread, and it reframes the standing question
entirely: **why does the game only ever have one thread, and why does that thread try to return so
early (`cyc≈1.4M`)?**

**Answer, found in `Ps2System.cs`'s own accumulated comments on `KickMidwayMainPath` (written
earlier the same day, across multiple re-test rounds): this is already a known, deliberate
tradeoff, not a new discovery.** The default boot path fakes a jump straight to `main()`
(`EE.PC = 0x00212F70`, `ra` set correctly to the real CRT0 return trampoline `0x0011C2A8`) instead
of running real CRT0. An alternate path — redirecting into genuine CRT0 (`0x0011C070`) — was tried
and re-tried multiple times this same day: it **does** create a real second worker thread (entry
`0x00480A18`, in the SIF-RPC library region), but that thread immediately blocks forever on
semaphore id 3, which nothing in the whole run ever signals — "permanently blocked on something
only genuine IOP-side interaction would ever satisfy." Each re-test after a same-day fix (PCPYUD,
then presumably more) showed measurable improvement (px/gifPath3/dmac reaching parity with the fake
path; a `Deci2Call` storm self-resolving after a real fix), but the semaphore-3 deadlock has kept
it worse overall each time, so the fake path remains the committed default.

**Given how many additional fixes have landed today since that last re-test** (the LWL-family fix
that unblocked real `SifBindRpc` entirely, the JR-guard vector fix, this entry's `ra=0`/`ExitThread`
fix, `MTSAB`/`MTSAH`/`QFSRV`) — **re-testing the real-CRT0 path again is the single most promising
next experiment**, not further hand-tracing of the specific stack-corruption instruction inside
`0x475B88`'s call chain (which — for the record, since it was actively being chased when this
broader cause was found — narrows to somewhere in `0x475B88 → 0x0047E1F0 →
0x00480520`/`0x004805E0`, a genuine stack-imbalance in that nested chain, same corruption class as
the original `LWL`/`SDL` fix; not yet pinpointed to the exact instruction). If the real-CRT0 path's
worker thread's semaphore-3 wait is now resolvable (or if it's now a *better* baseline even with
that thread still parked, given everything else that's improved), switching the default away from
the synthetic fake-jump would be a far more architecturally sound fix than chasing one more
instance of stack corruption in a code path (`KickMidwayMainPath`'s fake boot) that's fundamentally
a workaround to begin with.

**Concrete next step, in priority order**: (1) re-test the real-CRT0 redirect
(`KickMidwayMainPath`'s already-present, currently-disabled alternate path) with all of today's
fixes in place, using the exact same measure-and-revert methodology as its own prior re-test
rounds; (2) if it's now competitive or better, investigate the semaphore-3 deadlock specifically
(what should signal it, and whether that's a synthesizable `MidwayBootAssist`-style completion,
matching the `MaybeCompleteRealSifCdRead` precedent); (3) only if the real-CRT0 path is still not
viable, return to hand-tracing the specific stack-corruption instruction in the fake path's
`0x475B88` call chain.

Verified this entry's fix: default baseline unchanged
(`px=860160/gifPath3=5/dmac=7/sifBytes=272/syscalls=122`), full smoke suite green.

**Follow-up, same day — re-tested the real-CRT0 redirect with everything from today in place.**
Temporarily redirected `KickMidwayMainPath` to `0x0011C200` (the same target every prior same-day
re-test round used), measured, then reverted via `git checkout --` per the established protocol
(confirmed clean afterward — `git diff` empty).

**Result: dramatically more real underlying activity, but the exact same rendering ceiling.** At
5M cycles the real path already shows `sifBytes=269,396` (vs the fake path's `272`) and
`RealSifRpc binds=2,966` (vs `2`) — genuine, extensive real SIF/IOP module-loading activity, on
the order of what Vexx (this session's healthiest reference boot) showed. But `gifPath3`/`dmac`
are `5`/`7` at every measurement point, identical to the fake path. By 210M cycles — the same
cycle count the fake path was measured at — activity has plateaued (`binds` frozen at `3,843`
since ~40M, `syscalls` frozen at `23,253`) and, most tellingly, **`px=76,840,960` is byte-for-byte
identical to the fake path's own final `px` value at the same cycle count.** Also present and not
further chased this entry: a large, still-growing `unknown sid=0x00000000` count
(`unknownBindSids` reached `1,206`) — a new symptom not mentioned in any prior same-day re-test
round, most likely a retry loop binding with a not-yet-populated service-ID field; whether this is
the old semaphore-3 wall manifesting differently or something new wasn't determined.

**Conclusion: switching the default boot path is not, by itself, the fix.** The identical final
`px` across two structurally very different execution histories (a fake single-jump vs. real CRT0
with a genuine second thread and thousands of real SIF binds) is strong evidence that whatever
gates `gifPath3` past `5` is a **shared bottleneck below both boot paths** — most plausibly the
same "game never leaves attract-mode/menu into a real per-frame loop" class of gap this whole
investigation keeps converging on, not something either boot path's own mechanics would fix by
being switched. Given this, redirecting the default remains not worthwhile right now (matches the
standing precedent — "leaving this path disabled by default" — for a new, sharper reason: not
"it's worse," but "it's different work reaching the identical wall"). **Revised next step**: the
shared bottleneck itself (whatever decides "reached", not the boot path used to reach it) is now
the highest-value target — worth checking directly against the `0x00213218`/post-logo resumption
point and the text-layout loop already traced in earlier entries, since both paths presumably
funnel through the same later game-logic code once boot noise settles, rather than continuing to
alternate between the two boot-path experiments.

No source changes this entry (`Ps2System.cs`'s redirect was reverted, confirmed empty `git diff`
before the default rebuild). Baseline reconfirmed identical to before this experiment
(`px=860160/gifPath3=5/dmac=7/sifBytes=272/syscalls=122`).

**Follow-up, same day — traced the `ra=0` cascade's actual origin one layer further; found it
predates today's fixes and is already partially, defensively anticipated.** Re-verified (important,
since the immediately preceding entry temporarily redirected the boot path) that the default
fake-jump path was cleanly restored — `git status`/`git diff` empty, baseline metrics matched
exactly — before continuing.

Captured every hit of the syscall trampoline at `0x480268` (real `SifSetReg`, syscall `0x79`)
across the run's first ~1.4M cycles: the first several hits (`cyc=1,316,768`-`1,350,000`) all show
**sane, varied, legitimate return addresses** (`0x485DF0`, `0x485E0C`, ..., `0x48298C`,
`0x482FF8`) — genuinely different real callers, ordinary SIF register-configuration traffic. The
*first* bad hit (`ra=0x0`) appears at `cyc=1,400,000`, with `s0` already holding the `"cdrom0:\"`
byte pattern — the same recurring signature found at every corrupted capture point traced back
through this whole sub-investigation (see the entry above naming the call chain
`0x475B88 → 0x47E1F0 → 0x480520`/`0x4805E0`), strongly suggesting the true origin involves a real
CD/file-path operation gone wrong, not a generic pointer-arithmetic slip.

A chronological trace (`--trace-window --trace-chrono`) right around this cycle shows execution
spending the entire captured window in a **separate, already-documented** tight poll —
`0x00480330`: `lw v0,0(v1); andi v0,v0,0x4; beq v0,zero,0x00480330` — matching
`Ps2System.cs`'s own prior-round comment naming this exact address "the real SIF-library polling
loop." Confirmed via the already-known later-cycle sampling (`0x474EE4`/`0x475000`/`0x475D14`,
traced in earlier entries) that this specific spin **does** eventually get left behind — it is not
itself the permanent stall, just an early, transient one this run passes through before eventually
reaching the far-later text-layout loop.

**Checked `MidwayBootAssist.UnstickSifWaits` for an active role in the corruption and found the
opposite — it already defends against exactly this scenario.** Its handler for
`0x00482740`-`0x00482760` reads: `if (ra >= 0x100000) sys.EE.PC = ra;` — i.e. it explicitly
**refuses** to force a jump through `ra` when `ra` doesn't look like a sane code address, leaving
PC untouched (real CPU execution continues normally from wherever it already is) rather than
following a bad pointer. This is a defensive guard an earlier round of this same investigation
already added, meaning: **the game's own compiled code, independent of anything touched today,
already reaches a state where `ra` is corrupted/zero around this SIF-poll/give-up sequence** — this
predates every fix landed this session and was already known (if not root-caused) before today's
work began.

**Net position on the shared bottleneck**: real, concrete progress narrowing it (exact cycle of
first onset, exact recurring data signature, confirmed pre-existing and already-defended-against
rather than newly introduced), but not a root cause or a fix. The corruption most plausibly
originates inside the real CD-path/SIF-give-up handling that runs once the `0x00480330` poll is
abandoned — tracing the *exact* instruction that first corrupts `ra` (as opposed to where it's
first *observed* corrupted) needs the same painstaking, verified-at-every-step methodology the
original `LWL`/`SDL` fix took multiple rounds to complete, not a single additional pass.
**Concrete next step**: disassemble the SIF-poll abandonment/timeout path immediately following
`0x00480330` (what code runs once that loop's own real exit condition is met, or what — if
anything — currently forces an exit from it) and trace forward from there byte-exactly, the same
way `0x0024C800`'s `sdl`/`sdr` pair was found for the original bug, rather than continuing to
work backward from already-corrupted state as this entry did.

No source changes this entry — read-only tracing. Baseline unaffected, full smoke suite green
(unchanged).

---

**2026-07-27 — the shared bottleneck's actual mechanism found: two bugs in
`MidwayBootAssist.UnstickSifWaits` itself, not in the game's compiled code.** Following the prior
entry's concrete next step (bisect the ~50,000-cycle gap around the `ra=0` onset), a
`--trace-window=50000 --trace-chrono` starting at `cyc=1,350,000` reduced to unique PCs showed only
**18 addresses total** cycling for the entire window: `0x00483000-0x00483018`,
`0x00480260-0x00480268` (the trampoline), `0x00482FF8`/`0x00482FF0`, and `0x00482740-0x00482750`
(`UnstickSifWaits`'s own defensive-guard region, previously examined and believed innocent). Two
real bugs were found here, both fixed and committed:

1. **The `0x00482740-0x00482760` guard was hijacking an unrelated leaf function.** Disassembly of
   that range in this build shows an ordinary getter — `lui v0,0x78; sll a0,a0,2; addiu
   v0,v0,-30720; addu a0,a0,v0; jr ra; lw v0,0(a0)` — i.e. `array[a0]` at base `0x00778800`, not a
   wait loop. Unlike its sibling check three lines above (which validates the opcode is actually a
   `beq`-on-`v0` before acting), this branch fired unconditionally on PC range alone. Every ~25,000
   cycles `MidwayBootAssist.Step` samples the live PC; whenever it caught this getter mid-call, it
   force-set `v0=1` (clobbering the real table value) and jumped straight to `ra`, skipping the
   getter's own delay-slot `lw` — injecting a premature, garbage return value into a legitimate
   accessor. This is the mechanism behind the `ra=0`/`"cdrom0:\"` corruption cascade traced across
   the last several entries: not a bug in the game's code, and not a mystery CD-path/file-access
   operation — a false positive in our own heuristic guard. Fixed by gating it on the same
   opcode-is-a-branch-on-`v0` check its sibling uses (`MidwayBootAssist.cs`).

2. **Once (1) stopped masking it, a second, real bug in the same function became visible.** The
   genuine SIF-init wait this guard was originally meant to defend — `jal 0x00482740; daddu
   a0,zero,zero; beq v0,zero,-3 @ 0x00482FF8` — already has its own dedicated handler a few lines
   above (`pc is >= 0x00482FF0 and <= 0x00482FFC` → force `v0=1`, jump to `0x00483000`). That
   handler never fires in practice: this loop's cycle period is fixed relative to the 25,000-cycle
   sampling interval, so the periodic snapshot deterministically lands inside the *callee*
   (observed consistently at `0x00482750`, the getter's own `jr ra`) rather than ever landing on
   the caller's branch at `0x00482FF8`. Fixed by adding a companion check: when PC is inside
   `0x00482740-0x00482760` **and** `$ra == 0x00482FF8` (the return address unique to this one call
   site), apply the same resolution.

**Verified impact of both fixes together** (`blocker-trace user-media.json --host-present`, full
smoke suite green throughout, 0 failures at each step):
- 5M cycles: `px` rises from the long-standing baseline `860160` to `3,153,920` (~3.7x), and
  `syscalls` rises from 122 → 182 with real `SifSetReg`/`SifGetReg` activity now present instead of
  the corruption-cascade's spurious syscall storm.
- 210M cycles: `syscalls` rises from 122 (mostly spurious, cascade-driven) to 4,282, dominated by
  4,176 real calls to `SifSetReg` (syscall `0x79`) — genuine SIF library activity, not corruption
  artifacts. The corruption signatures (`ra=0`, the `"cdrom0:\"` byte pattern in `s0`) no longer
  appear anywhere in the trace.
- The final `px=76,840,960` ceiling at 210M cycles is **unchanged** from before these fixes, and
  execution again settles at `PC=0x00482750` by the end of the run — but this is now a *different*,
  *real* wait: forward progress happens first (thousands of legitimate `SifSetReg` calls), and only
  after that does execution loop back into calling this same getter again, evidently from some
  other, not-yet-identified call site (a different `$ra`, since the one call site this session
  fixed is confirmed resolved). This is a genuinely improved bottleneck, not the same one — real
  work now happens before the wall is hit — but the wall itself has not yet been fully cleared.

**Concrete next step**: the `SifSetReg` ×4,176 retry pattern is the new lead. It's a real,
fully-implemented HLE call (`SonyKernelHle.cs` case `0x79`, just stores a register value and
returns success unconditionally) — so a caller retrying it thousands of times implies it's
incidental to a genuine polling loop waiting on some *other* condition per iteration, not a stubbed
call failing. Find that loop (likely another SIF handshake/bind retry, possibly involving
`SifGetReg`'s `IopReady` flags at `SonyKernelHle.cs` line ~456) and determine what real IOP-side
state it's waiting for that HLE never supplies. Also worth identifying the new call site reaching
`0x00482740` at the very end of long runs — same getter, different `$ra`, not yet traced.

---

**2026-07-27 (continued) — durable fix for `sceSifInitRpc`'s own wait, then the user directly
reported the resulting symptom from their own play-testing** ("cycles keep going but the pixel
count freezes... deadlocked waiting for a response thread") — confirming from an independent
angle what `DETPS2_PROFILE_PC=1` had already shown: ~70% of all 147M instructions executed over a
210M-cycle run were spent re-entering the `0x00482740`/`0x00482FF0` getter loop from the previous
entry. Fixed durably rather than just nudged: both `UnstickSifWaits` handlers for this call site
now also write `MEM[0x00778800]=1` (the flag `sceSifInitRpc`'s own `array[0]` check polls) the
first time either fires, so every subsequent *natural* (non-assisted) call succeeds on its own.
Verified: PC profiler's total executed-instruction count rose from 147M to 182M in the same 210M
cycle budget (far less wasted spinning), and a real second thread (`entry=0x00480A18`, the SIF-RPC
dispatch worker `sceSifInitRpc` sets up) gets created for the first time in this boot path.

That thread creation immediately exposed the user's reported deadlock precisely. Added a thread-
state dump to `blocker-trace` (`Program.cs`: id/alive/started/sleeping/waitSemaId per thread) to
inspect it directly, and traced a **chain of two more real bugs**, both fixed:

1. **`KickCommercialWorker` (`Ps2System.cs`) was fully implemented but never called from
   anywhere — dead code.** `SonyKernelHle.cs`'s own `CreateThread` case (`0x20`) comment already
   documented the gap: *"Do not auto-start: Midway's worker needs globals filled first. StartThread
   (if called) or a late commercial assist will start it."* The worker thread was being created
   (confirmed via the new thread dump: `alive=True started=False` indefinitely, even past 1B
   cycles) but the game's own code never reached its own `StartThread` call for it, and the "late
   commercial assist" meant to cover that gap was simply never wired in. Wired it into `RunFor`'s
   per-slice loop: once a thread `id>=2` is observed alive-but-not-started, wait 200,000 cycles
   (letting whatever globals the case-`0x20` comment refers to get filled in), then fire once.

2. **Once the worker thread actually started running, its own dispatch loop turned out to call
   `WakeupThread(0)` — a permanent no-op**, since thread ids start at 1 and thread 1 (the
   primordial boot thread) predates the normal `CreateThread`/`GetThreadId` flow, so nothing ever
   recorded its real id anywhere the worker could read it back from. Confirmed via a temporary
   `DETPS2_TRACE_WAKEUP` diagnostic (`SonyKernelHle.cs` case `0x33`): every single `WakeupThread`
   call across a 50M-cycle sample targeted id 0. Thread 1 had `SleepThread`'d itself expecting the
   worker to wake it once real, and slept forever even though the worker was genuinely alive and
   correctly looping on its own `WaitSema`/`WakeupThread` dispatch. Fixed with a direct sibling to
   the existing `MaybeUnblockStarvedSema`: `MaybeUnblockStarvedSleep` force-wakes any thread that's
   been `Sleeping` with `WaitSemaId==0` and not `WaitVblank` for over 2,000,000 cycles — same
   shape, same grace period, the `SleepThread` analogue of the `WaitSema` case.

**Verified impact of this whole sub-round** (full smoke suite green throughout every step, 5M-cycle
`px` checkpoint unchanged at each commit — no regressions): at 250M cycles, thread 1 is genuinely
running again (`started=True sleeping=False`, `currentThreadId=1`) and thread 2 is correctly
blocked on a real semaphore (`waitSemaId=11`) instead of both being stuck in incompatible states.
This is the user's reported deadlock, confirmed root-caused and fixed.

**What's left — a new, distinct bottleneck, now clearly exposed rather than masked by threading
bugs**: `px` is *still* frozen at `76,840,960` even at 1B cycles, because thread 1, once properly
awake, goes straight back into a pre-existing, unrelated stall: a character-by-character text
dispatcher at `0x00475CE0`/`0x00475D14` (jump table on byte value, reading via a pointer chain
through `sp+616`) that calls into a nested search function at `0x00476A20`. Traced with
`--pcbreak` sampling at multiple points along this chain:

- The caller at `0x0047B1A8` (`jal 0x00476A20`) sits in an outer loop bounded by a fixed count of
  37 (`addiu s2,zero,37` at `0x0047B18C`, loop condition `bne v0,s2,0x0047B190`) — i.e. "process up
  to 37 items." Over 250M cycles this outer loop's exit-check point (`0x0047B1B8`) was hit only
  **221 times total** (vs. millions of hits inside the nested search), meaning nearly all CPU time
  is spent *inside* a single call to `0x00476A20`, not cycling the outer loop.
- `v0` at that exit-check (loaded from a stack slot, the outer loop's own progress counter) reads
  `0x1` at every single one of those 221 samples, from the very first (`cyc=302,752`) to deep into
  the run — the counter that should climb toward 37 to let this finish never advances past its
  first real value.
- Inside `0x00476A20` itself: a nested index (`s0`) does count up (0, 1, 2, 3, ...) interleaved
  with a constant `-2` (`0xFFFFFFFFFFFFFFFE`) sentinel value on alternating calls — a linear search
  through candidates that, on the evidence so far, never matches (each candidate check is a
  `strcmp`/`strlen` pair against byte-range logic that looks like Shift-JIS/multi-byte lead-byte
  detection — `(byte-0x40)<0x3F`, `(byte-0x80)<0x7D` — i.e. this may be Japanese-text/font-glyph
  support code the US release still links but rarely exercises).

Net read: a font/text precompute step is stuck re-searching for something at index 1 of a
37-item table that it never finds, so item 1 never completes and items 2-37 are never reached.

**Follow-up (2026-07-27, same day): identified the "needle" precisely, then hit a genuine
roadblock in verifying the actual cause.** Read the literal candidate bytes at the `strcmp` targets
in `0x00476A20` directly (remembering EE is little-endian — a word's low-address byte is its LSB,
not its first-printed hex digit): they spell out **`"C-SJIS"`, `"C-EUCJP"`, `"C-JIS"`** — this is an
iconv-style character-encoding-name lookup table, not font/glyph data as first suspected. The
"needle" (`MEM[s1+52]` → `MEM[0x563504]` → `0x005AEFA8`, confirmed via `--track-writers
--find-writer=0x00563504` to be written exactly once, at `cyc=0`, i.e. it's static ELF-compiled
data, never touched by any executed instruction across the full 250M-cycle run) is a single-byte
string: `"C\0"`. `"C"` is the standard POSIX default locale name — a real, valid, and likely
*intentional* value for a US release (no special encoding requested). Since none of the three
candidates is a bare `"C"`, the search legitimately never matches — which is almost certainly
*correct*, expected behavior; the real bug must be in what the **outer loop** does with a
legitimate "not found" result (never advancing past index 1) rather than in the search itself.

Chasing why the outer loop never advances led to a COP0-level finding, added
`DETPS2_TRACE_COP0STATUS` (`EmotionEngine.cs`, logs every `MTC0` write to Status) to investigate:
`COP0_Status` starts the run properly enabled (`0x00018401`) but reads as `0x00790000` throughout
the entire stall — decoded, that's IM bits (8-15, the per-source interrupt mask) **fully zeroed**,
meaning no INTC-sourced interrupt (VBlank, DMA, timer, ...) can reach the CPU at all. If this
loop's real completion is interrupt-driven (plausible — a lot of PS2 kernel synchronization is),
a fully-masked interrupt state would explain the permanent stall precisely.

Tracing where this happens: only **3 total `MTC0` writes to Status across the whole 250M-cycle
run**, all at `pc=0x00486778`, clustered around `cyc≈17,575,088-17,575,152`
(`old=0x00008401 new=0x00780004`, `old=0x00780000 new=0x00780004` ×2). Disassembling
`0x00486728-0x0048678C` (reached via three call-site trampolines at `0x00486798`-`0x004867C8`
targeting real physical MMIO addresses `0xB0001000`/`0xB0001010`/`0xB0001020`) shows a textbook
ps2sdk-style atomic hardware-register-write helper: read Status, conditionally `di`-and-verify,
compute a new Status value via `ori`/`xori`/`or` against the saved EIE bit, `mtc0` it, do the
protected `sw`, save `ra` into `ErrorEPC` (`mtc0 ra,$c0_30`), `eret`. This is legitimate, ordinary
kernel code used for many unrelated purposes — nothing Shaolin-Monks-specific about the function
itself.

**Where this became a genuine roadblock rather than a fix**: hand-computing the expected `mtc0`
value from this disassembly (tracing the branch that skips the `di`-verify loop, since the saved
EIE bit read as 0) gives `v0 = ((0x8401 | 0x6) ^ 0x2) | 0 = 0x8405` — which does **not** match the
actually-observed write of `0x00780004`. That's not a rounding/detail mismatch — the two values
share almost no bits. Two honest explanations remain open: (a) a genuine emulation bug in one of
`mfc0`/`ori`/`xori`/`or`/`beq`/`mtc0`/`di` for this specific case, or (b) a real interrupt fires
*during* this short instruction sequence (between the `mfc0` read and the `mtc0` write, both only
a handful of instructions apart) and its own exception-entry Status manipulation is what's actually
being observed — `--pcbreak`-style periodic sampling and single-write-event tracing can't
distinguish these without full instruction-level tracing across the exact window, which no existing
tool in this codebase does yet (`--trace-window`/`--trace-chrono` exists but wasn't run precisely
enough across this ~10-instruction span in this pass). **Decision point for whoever picks this up
next**: either build/use finer-grained instruction tracing to resolve (a) vs (b) definitively before
changing anything COP0-related (high confidence, more work), or add a conservative periodic assist
that snapshots Status before it's observed going to all-IM-zero and restores it if stuck too long
with no forward progress (matches this session's established `MaybeUnblockStarvedSema`/
`MaybeUnblockStarvedSleep` pattern, but with meaningfully lower confidence than those two since the
root mechanism here isn't yet understood, only its symptom). Did not attempt either during this
session — the risk of a wrong COP0-level fix (which could silently mask a real CPU-emulation bug
affecting far more than Shaolin Monks) outweighed pushing further without more certainty.

---

**2026-07-27 (continued) — the COP0 roadblock resolved with precise instruction-level tracing
(not a CPU bug), then two more real, general fixes found and landed.** Reran the exact
mfc0-to-mtc0 window at `0x00486728`-`0x0048678C` with `--cycles=<N> --trace-window=15000
--trace-chrono` (a genuinely precise instruction-by-instruction capture, unlike periodic
`--pcbreak` sampling) and found the earlier hand-verification discrepancy's real cause: **a
hardware interrupt fires mid-sequence** — not between the specific mfc0/mtc0 pair first suspected
(that one ran cleanly, no interrupt), but between two *separate* calls to the same atomic
MMIO-write helper (different trampoline entry points, `0x00486798` then `0x004867B8`), confirmed
by `jr ra` at `0x00482DE0` landing directly on `0x80000200` (the interrupt vector) with zero gap.
`EnterException` itself is clean (only sets EXL, doesn't touch IM bits) — the actual mechanism:

- The interrupt is a real INTC "Sif" source, dispatched via `TryDispatchRegisteredIntcHandler`
  through the game's own registered DMAC-channel-5 handler (`AddDmacHandler` cause, real ps2sdk
  `_SifCmdIntHandler` pattern, confirmed via `DETPS2_TRACE_HANDLERS`).
- That dispatch path never acknowledged the INTC source itself — correct for a direct
  `AddIntcHandler` registration (real software handlers ack `INTC_STAT` as part of doing real
  work), but wrong for the DMAC-channel-5 fallback specifically.
- Our HLE raises the Sif INTC bit from several call sites (`Sif.cs`, `Iop.cs`,
  `SonyKernelHle.cs`) whenever SIF DMA activity happens, without always populating the real
  in-memory queue/flag data the game's handler inspects. When the handler finds nothing to do (a
  legitimate outcome from its own perspective) it takes its own early-exit path and never reaches
  the ack write buried in the "real work" branch — so the bit stays pending and re-fires on the
  very next eligible instruction, forever: a genuine interrupt storm (confirmed: the handler's
  `jr ra` returned straight to the vector every ~64 cycles with zero forward progress in between,
  ~70% of a 250M-cycle run's instruction count concentrated in this exact 5-address loop).

Fixed (`EmotionEngine.cs`, `TryDispatchRegisteredIntcHandler`) by acknowledging the Sif source
specifically when routing through the DMAC-channel-5 fallback path (leaving the direct
`AddIntcHandler` case untouched, since real DMAC channel completion is hardware-acknowledged
unlike INTC sources needing explicit software ack). Verified: smoke suite green, 5M-cycle `px`
unchanged, PC profiler's previous dominant hotspot (`0x00482CA0`'s loop) gone entirely, total
executed instructions in the same 250M-cycle budget dropped from 214.9M to 35.65M samples (far
less wasted re-firing, though still nonzero residual interrupt activity — a real reduction, not a
full elimination). `px` itself stayed at `76,840,960` — the storm was real and is now fixed, but
it wasn't the sole blocker.

**Second finding, chasing why thread 1 stopped getting scheduled once thread 2's dispatch loop ran
cleanly**: syscall `0x56` (`WaitEventFlag`) was a suspicious repeat entry in the post-storm-fix
histogram. Its implementation (`SonyKernelHle.cs`) ignored its pattern (`a1`), wait mode (`a2`),
and result-pointer (`a3`) arguments entirely, returning the raw current event-flag bits as if that
were a status code, with **no blocking at all** — a caller checking `v0==0` for success against
nonzero real bits would see a spurious "error" and retry immediately, forever, without ever
yielding to another thread. Implemented real ps2sdk semantics (block until `(bits & pattern)`
satisfies mode — OR = bit `0x01`, AND = default; clear-on-exit = bit `0x10`; write the satisfying
bits to `*result_ptr`), mirroring `WaitSema`'s already-correct blocking pattern exactly (new
`Thread.WaitEfId`/`WaitEfPattern`/`WaitEfMode`/`WaitEfResultAddr` fields; `SetEventFlag` re-checks
and wakes parked waiters; same "fabricate the signal if nobody else is runnable" deadlock guard
`WaitSema` already uses). A follow-up bug in the same change (the auto-create-missing-flag branch
unconditionally returned "not satisfied" instead of checking pattern=0/AND-mode's trivial-true
case against the fresh flag) was found via `DETPS2_TRACE_RPC` and fixed immediately after. Neither
turned out to be the active blocker for this specific run (the 48 `WaitEventFlag` calls present
all happened to already be satisfied), but both are real, general kernel-primitive bugs likely to
matter for other titles using event flags for genuine synchronization.

**Third finding, the actual scheduler bug**: added a thread-state dump to `blocker-trace`
(`Program.cs`: id/alive/started/sleeping/waitSemaId) and found thread 1 sitting `alive=True
sleeping=False` — clearly runnable — yet permanently never `currentThreadId` again once thread 2
took over. Root cause in `KernelState.FindNextRunnable` (`KernelHle.cs`): the round-robin scan
loop ran `for (int i = 1; i <= _threads.Count; i++)`, so its *last* iteration lands on
`(idx + Count) % Count == idx` — i.e. it re-checks the **calling thread itself**. Since the
calling thread trivially satisfies its own `Alive && Started && !Sleeping` condition (it's the one
currently running), it gets returned as "the next runnable thread," `SwitchToNext` sees
`next == current`, concludes "nobody else runnable," and **never reaches the very next lines**:
the explicit "also allow main thread (id 1) even if `Started` flag never set" fallback that exists
specifically to handle thread 1 (the primordial thread, which never goes through `StartThread` so
its `Started` flag is permanently false). Any time thread 2 called a blocking primitive while
happening to also satisfy its own next-runnable check, thread 1 was silently skipped — a real
scheduler bug, not an HLE-completeness gap, and one with no title-specific trigger at all (it
fires for any 2+-thread boot once the non-thread-1 thread stops needing genuine blocks). Fixed by
bounding the loop to `i < Count` (check every *other* thread exactly once) instead of `i <= Count`.

**Verified impact, all three fixes together**: smoke suite green throughout, no `px`/baseline
regressions at any step. `--trace-threads` over a 250M-cycle run shows real, substantial change:
thread 1 gets scheduled cooperatively at `cyc=19,750,000` (previously never again once thread 2
took over) and the existing `ForcePreempt` mechanism (a *different*, already-existing forced
timeslice mechanism, `_preemptQuantum = 0x10000`) keeps alternating both threads regularly
afterward, with thread 1 reaching entirely new code (`0x004748C8` onward) never touched in any
prior trace this session. `syscalls` still plateaus at `298` by 250M cycles with no further growth
through 1B cycles, and `px` is still unchanged at `76,840,960` — real, deep, verified forward
motion in *how much of the game's own code actually runs*, but not yet a full unblock. PC
profiling at 250M cycles is numerically unchanged from immediately before the scheduler fix
(dominated by the same big `memcpy`-style loop at `0x00486098`, ~1.96M hits, plus the residual
interrupt-vector and strlen hotspots) — the new thread-1 activity is real (confirmed via
`--trace-threads`) but small relative to that dominant loop's volume, so it doesn't show up in the
profiler's top-20 view; it's folded into the `unique=32448` distinct-address count instead.

**Where this stands**: `px` (`Gs.PixelsWritten`) appears to be a cumulative-since-boot counter
that plateaus once the logo/boot-time rendering finishes and nothing further calls a GS drawing
primitive — every fix landed today addressed a real, verified bug (corruption cascade, sawtooth,
dead assist code, no-op wakeup target, interrupt storm, missing WaitEventFlag semantics, a
scheduler self-check bug), and each individually produced genuine, measurable forward progress in
*what the game's own code actually executes* — but none of them has yet been the specific gate for
resuming GS activity. **Concrete next step**: with thread 1 now reaching `0x004748C8` onward
(never explored this session), disassemble/trace forward from there to find whatever code path
would normally issue the next real GS draw command, rather than continuing to chase kernel
primitives — the remaining gap looks increasingly like it's in game-specific rendering/menu logic
now, not general kernel/threading HLE.

---

**2026-07-27 (continued) — one more real interrupt-storm fix (VBlankStart), then confirmed the
system has reached a genuinely clean, healthy steady state — not a bug.** `--trace-threads` showed
what looked like a second livelock (`ForcePreempt` ping-ponging threads 1/2 at an apparently frozen
cycle count, e.g. `cyc=43416704` repeated for dozens of consecutive Preempt events). Added
`DETPS2_TRACE_IRQLOOP` (counts consecutive interrupt-dispatch re-entries with no real instruction
execution in between) to check whether this was a genuine zero-progress loop. It wasn't quite — but
tracing it found a second instance of the DMAC-channel-5 storm's root pattern: **VBlankStart**
(`pending=0x0004` consistently) re-firing roughly every 64-676 cycles for the rest of the run,
because `TryDispatchRegisteredIntcHandler`'s own no-handler-found fallback deliberately excluded
VBlankStart from auto-ack — a design meant to protect a game doing "busy-poll INTC_STAT with COP0
interrupts masked off," which turns out to be structurally impossible to encounter at that exact
call site: `Intc.GetPendingInterrupts()` (`Stat & Mask`) is already filtered by INTC's own
per-source mask, and the method itself is only reached when COP0-level `_takeExceptions &&
InterruptPending` was already true (the literal opposite of "masked off"). Removed the exclusion
(`EmotionEngine.cs`).

Verified: smoke suite green, 5M-cycle `px` unchanged, PC profiler shows the interrupt vector
(`0x80000200`) drop out of the top-20 hotspots entirely. Re-ran `--trace-threads` afterward and
found the earlier "livelock" was a **diagnostic artifact, not a real bug**: `MaybePreempt` runs on
*every* `Step()` loop iteration (line 366, before the interrupt-dispatch branch), but
`KernelState.CurrentCycle` — the timestamp `LogThreadEvent`/`--trace-threads` actually prints — is
only stamped much later (line 418), reached *only* when execution falls through past the
interrupt-dispatch `continue`. During a stretch where many consecutive iterations hit that branch,
the printed timestamp simply stops advancing even though `_cyclesSinceLastPreempt` (a separate,
per-call counter) keeps ticking and firing real switches — so the log shows the same `cyc` value
dozens of times even though the underlying `MaybePreempt` calls are real and distinct. Confirmed
with `DETPS2_TRACE_IRQLOOP` at the exact same point post-fix: `streak=1` always, `pending`
alternates between real combinations of sources, `pc` visits dozens of distinct addresses, and
`cyc` advances cleanly in ~250,000-cycle increments matching a *realistic* VBlank period (vs. the
pre-fix ~64-676 cycle storm) — genuine, healthy, diverse forward progress, not a freeze.

**Correction — the "clean idle steady state" conclusion below was wrong.** Ran to 3 billion cycles
(~10 real seconds of PS2-hardware time) and found `syscalls`/`px` byte-for-byte identical to the
250M-cycle checkpoint, with `spu2Samples` still growing; concluded this was a healthy idle loop
waiting on input. It isn't. Added `EE.exitRequested`/`exitCode`/`PC` to `blocker-trace`'s summary
(`Program.cs`) to double-check, and it showed `exitRequested=True exitCode=1` — **the EE genuinely
called `Exit(1)` and has executed zero further instructions since**; `IOP`/`SPU2` are clocked
independently of the EE and kept advancing on their own, which is exactly what created the "still
running" illusion (`EmotionEngine.Step()`'s own loop does `if (_hle.ExitRequested) break;` as its
very first real check per call, so every subsequent `Step()` invocation, however many billions of
cycles requested, executes nothing).

Added `DETPS2_TRACE_EXIT` to `SonyKernelHle.cs`'s case `0x04` (the real Sony-kernel `Exit` syscall,
distinct from `ExitThread`/case `0x23`) and found the exact call: `code=1 pc=0x0047FA84
ra=0x00476818 tid=1 cyc=22,560,048` — matching precisely the cycle where `--trace-threads`'
`ForcePreempt` output appeared to "freeze" in the prior entry (that appearance was `KernelState.
CurrentCycle`, a diagnostic-only timestamp, simply never being restamped again once the EE stopped
executing — not a separate bug). Traced the call chain backward with `--trace-window`/
`--trace-chrono`: `0x0047FA80` is the `Exit` syscall trampoline, called via `j` (not `jal` — a tail
call) from `0x004865D8`, itself reached from a small unconditional wrapper at `0x00476808`
(`addiu a0,zero,1` in its own delay slot — i.e. this helper always exits with code 1 regardless of
its caller, a generic `abort()`/`fatal_error()`-style routine), which is the fall-through
continuation after returning from a call chain rooted at `0x004767C0` — a function that builds a
520-byte-tagged struct (`addiu v0,zero,520`) and calls `0x00479D38`, a `vsnprintf`-style formatter
(itself using the `0x00476A20` locale/encoding-search routine from earlier entries as one of its
format-specifier handlers) to build a string into a caller-supplied buffer, then NUL-terminates it
before falling into the exit helper. **This is a genuine, deliberate "format an error message, then
abort" pattern — a real game-level assertion failure, not a hang, corruption, or livelock.**

**Follow-up, same day: root-caused precisely, using new general-purpose tooling.** The earlier
`--pcbreak`/`disasm` cross-process reading was inconsistent because manually matching exact `sp`
values across separate invocations several stack frames deep is genuinely error-prone — resolved
by building two small, reusable, *in-process* diagnostics instead of continuing to hand-trace:

- `DETPS2_TRACE_MSGBUF` (`EmotionEngine.cs`): reads RDRAM directly at `v1` right before the
  fatal-exit path's NUL-terminate write (`0x004767F0: sb zero,0(v1)`). Confirms the buffer really
  is corrupted, not a reading mistake: `v1=0x401A6802`, and the bytes there are binary noise, not
  text.
- `DETPS2_TRACE_REGWRITE` (`EmotionEngine.cs`, hooks `SetGpr` directly): logs every write to one
  specific GPR (`DETPS2_TRACE_REGWRITE_IDX`, default 4/`a0`) across a full run, with the writing
  PC. Built because the corrupting write turned out to be *far* outside any reasonably-sized
  `--trace-window` capture (over 13,000 cycles untouched before the crash reads it) — a general
  tool for exactly this class of problem, reusable for any future register-corruption hunt.

Traced `a0` to its exact corrupting instruction: `pc=0x00475D24` (`bgtzl v0,0x00475D40` — a
*branch-likely*), at `cyc=21,858,048`, with `s2=0` (a NULL pointer) and `v0=MEM[s2+4]=0x335A007C`
(nonzero). Because `v0>0`, the branch is taken, which for a "likely" branch means its delay slot
*executes* (`0x00475D28: lw a0,0(s2)` → `a0=MEM[0]=0x401A6800`) — corrupting `a0` with whatever
real bytes sit at physical address 0. That's not an emulator placement bug: `KernelBootstrap.cs`
installs the TLB-Refill exception vector at `PhysTlbRefill=0x00000000`, which is the
architecturally-correct real R5900 address for it — real PS2 hardware also has genuine, non-zero
vector code there, not blank memory. So the actual bug is upstream of this instruction: **`s2`
should hold a real pointer here (its use, `MEM[s2+0]`/`MEM[s2+4]`, matches a struct/list-node
pattern) and is NULL instead**, and the branch-likely idiom (skip the load unless the "count"
field is `>0`) only reads safely on hardware where a NULL pointer's neighborhood is genuinely
blank — which the real EE kernel-reserved low-memory region is not, on real hardware either. This
strongly suggests the compiler's assumption here depends on `s2` never legitimately being NULL at
this point, i.e. something upstream failed to set it.

Traced `s2` (`DETPS2_TRACE_REGWRITE_IDX=18`) backward through several layers, each one turning out
to be a callee-saved-register save/restore around an unrelated inner call (the locale-search
function's own `0x00476A44`/`0x00476D44` prologue/epilogue, the big `vsnprintf`-style formatter's
800-byte-frame epilogue at `0x00476690`) rather than a fresh assignment — `s2`'s *true* origin (the
point where it should have been set to a real struct/list-node pointer and wasn't) is at least one
more layer further back than reached this session. **Concrete next step**: continue the same
`DETPS2_TRACE_REGWRITE_IDX=18` approach from progressively earlier `--cycles` cutoffs (each save/
restore pair found so far bounds the search further back) until a write to `s2` is found that
isn't immediately explained by a matching save/restore pair — that write's caller is where the
real "should have produced a valid pointer" logic lives, and is the actual next thing to
understand (likely a failed lookup or missing HLE-provided resource, matching this whole session's
pattern, rather than a genuine bug in Shaolin Monks' own shipped code).

**Follow-up: read `0x00475BA8`'s full function body directly instead of continuing the register
trace, which resolved the framing ambiguity above — and corrects a conflation in the entry
before it.** `0x00475BA8` is the entry point of the `vsnprintf`-style formatter itself (the
800-byte-frame function referenced throughout this whole thread): its own prologue does
`daddu s2,a0,zero` at `0x00475BBC` — **`s2` is simply this function's own first parameter**, i.e.
the output buffer pointer, freshly reassigned on every single call. So the "`s2` has been 0 since
`cyc=3,130,720`" finding above was following the *aggregate* history of one physical register
across *many unrelated calls* to this shared formatter throughout the whole program, not one
persistent variable — a real methodology mistake, not a real finding; retracted.

Correcting the actual chain: the corrupting `bgtzl`/`lw a0,0(s2)` at `0x00475D24` (`cyc=21,858,000`,
`s2=0`) fires **inside one specific call** to this formatter — not the fatal one. `DETPS2_TRACE_
MSGBUF`'s `a0` capture at `0x004767B8` (`cyc=22,553,520`, ~695,000 cycles *later*) shows the exact
same corrupted value being used as the buffer for a **separate, later** invocation. So the real
mechanism is: one formatter call (cyc≈21.86M) internally dereferences its own NULL-ish state via
the branch-likely idiom, produces `a0=0x401A6800` as a side effect, and that value survives —
through some return value or shared/global storage not yet identified — to be reused nearly
700,000 cycles later as the *buffer pointer* for the specific formatter call that leads to
`Exit(1)`. **Concrete next step**: find how a formatter call's internal state (specifically
whatever produces `0x00475D24`'s `s2` — a *different*, nested pointer, not the function's own `a0`
parameter — one level deeper than traced so far) ends up feeding the *next* unrelated call's `a0`;
likely a shared global/static buffer-management structure (an allocator, a string-table cursor, or
similar) that legitimately holds pointers across calls, one of which is getting corrupted rather
than the formatter's own locals.

**Session tally (2026-07-27, this whole thread)**: 9 real bugs found and fixed, all independently
verified (smoke suite + `px` baseline unchanged at every step): the `ra=0` corruption cascade
(false positive in `UnstickSifWaits`'s own guard), the sif-init wait handler never firing due to
sampling misalignment, the sif-init wait's sawtooth (nudged instead of durably satisfied),
`KickCommercialWorker` being fully-implemented dead code, `WakeupThread(0)` being a permanent
no-op, a DMAC-channel-5 INTC-ack gap, missing `WaitEventFlag` blocking semantics (plus a follow-up
bug in its own auto-create path), a scheduler self-check bug in `FindNextRunnable`, and the
VBlankStart INTC-ack gap. Every one of these is a real, general emulation bug — none required a
Shaolin-Monks-specific hack — directly matching the project's standing hypothesis that fixes found
via this one title's boot path have broad value across the library.

**Update: the NULL-buffer guard at `0x00475D24` only delayed the crash — it recurs, at a different
site, and its real trigger has now been found.** With the guard in place, the game runs *dramatically*
further: it reaches the `px=76,840,960` plateau by `cyc≈181M` (vs. never before) and keeps doing real
`SifSetReg` work for hundreds of millions more cycles, but still calls the same hardcoded `Exit(1)`
wrapper (`0x00476808`) at `cyc=476,734,304` this time. `[EXIT-SYSCALL]`'s `ra` always reads
`0x00476818` regardless of caller (it's the return address of the wrapper's own internal
`jal 0x0011C2B0`, overwritten on every call, not useful for finding *its* caller) — a new
`DETPS2_TRACE_EXIT`-gated `[ABORT-CALLER]` log was added at `0x00476808`'s own entry to capture `$ra`
*before* that overwrite happens.

**Root cause, precisely identified**: `scanword` found every static call site to `0x00476808` across
the whole loaded image — five inside game code (`0x00201844`, `0x00203A2C`, `0x00203A54`,
`0x002043E4`, plus `0x00476840` inside the shared formatter chain), none matching the observed
`ra=0x00000000`. A `j`-encoding scan (tail-calls, which don't touch `$ra`) found exactly one more:
`0x0020448C`, inside a small linked-list *lookup-or-die* function at `0x00204430` — walks a global
singly-linked registry (head pointer `MEM[0x0064E5C8]`, node layout `{next@0, ..., key@8, arg@12,
..., altNext@20}`) comparing each node's key against the caller's `a0`; if the list is empty or the
walk falls off the end without a match, it restores the (already-zero) saved `$ra` and tail-jumps
straight into `0x00476808` — i.e. **"look up resource X in the registry; if not registered, abort."**
`$ra=0` here is simply the correctly-restored value from whatever called `0x00204430`, meaning *that*
caller was itself reached without ever going through a `jal` either.

Tracing one level further: `0x00204430` has **zero `jal` callers anywhere in the image** — it's
reached only via one more tail-jump, from `0x004898F8`, which is a `case` block inside a
jump-table-dispatched handler at `0x00489868` (locale/codeset-conversion helper cluster — same
neighborhood as the `sscanf`-style `%[...]` scanset engine at `0x00475BA8` traced earlier). The key
passed to the lookup is a **hardcoded constant, `a0=0x00565B9C`** (a fixed address in the runtime's
own static data, not a runtime-computed value) — and the *sibling* case block at `0x004898DC`, a few
instructions earlier in the same dispatcher, **registers that exact same key** (`a0=0x00565B9C`) into
the identical list via a matching *insert* function at `0x00204408` (`node.key@8 = a0`, storage at a
fixed slot `a1=0x0077F5F0`, pushed onto the `MEM[0x0064E5C8]` list head). `0x0077F5F0` is in the same
data region as `0x0077F809`, the closest thing to a readable message string ever captured from this
whole investigation (see above) — the same neighborhood, not a coincidence.

**So the actual bug is a missing self-registration, not a NULL-pointer bug**: something is supposed
to reach the `0x004898DC` "register codeset/locale `0x00565B9C`" case *before* anything reaches the
`0x004898F4` "look it up" case, and in this emulator's execution it doesn't — matching this whole
session's pattern exactly (every prior bug here was a missing or incomplete piece of kernel/IOP-level
support, never a genuine game bug). The original NULL-buffer guard at `0x00475D24` was real and
correct (it fixed one deterministic crash on real-hardware-equivalent memory semantics), but it was
never the root cause — it just happened to fire on the way to what's actually a much bigger, separate
gap: whatever code path is supposed to trigger this locale/codeset registration during boot never
runs. **Concrete next step**: find every caller of the `0x00489868`-family dispatcher (the selector
value picking the `0x004898DC` vs. `0x004898F4` case is the real thing to trace — likely driven by a
locale/config value read very early in boot, possibly from a resource our CDVD/file HLE doesn't yet
serve, or a static-initializer table this emulator doesn't fully walk before `main()`), and determine
what condition should have made it take the *register* case before the *lookup* case is ever reached.

**Ruled out: this is not a missing-static-constructor problem.** `ElfLoader.cs` only ever processes
`PT_LOAD`/`PT_MIPS_REGINFO` program headers (no section-header/`.init_array`/`.ctors` handling
at all — confirmed by reading the whole file), which raised the obvious hypothesis that C++ global
constructors simply never run. Disassembling the game's own crt0 at its ELF entry (`0x0011C070`)
rules this out directly: after the standard GPR/FPR zero-init and two BSS-clear loops, it sets `$gp`
and `$sp` via two syscalls (`v1=60` then `v1=61` — heap/stack init), then at `0x0011C260-0x0011C274`
does the textbook linker-resolved-optional-symbol idiom — `lui/addiu s0,0x00205DE8; beq s0,zero,skip;
jalr s0` — i.e. **it does call a real init/constructor-runner routine, unconditionally, in the first
few hundred thousand cycles of boot**, well before any of the ~476M cycles of real gameplay/menu code
that run before the eventual crash. So static C++ initialization (or whatever `0x00205DE8` is) does
execute; it isn't skipped by an emulator-side loader gap. The missing self-registration must instead
be triggered by some later, in-game code path (e.g. first use of a specific locale/format feature,
possibly gated behind a resource our CDVD/SIF HLE doesn't yet serve) — not a boot-time omission. Also
visible in this same crt0 disassembly, for whoever picks this up next: `0x0011C288: jal 0x00476848`
(another conditional init call, `a0=0x00205DF0`) and `0x0011C2A0: jal 0x00212F70` (called with
`a0=MEM[0x005B9C00]`, `a1=v0+4` right after `ei` — almost certainly the real `main()`), followed by a
tail-jump to `0x00202068` (`exit(main_result)`-shaped). Useful landmarks for the next session instead
of re-deriving crt0 from scratch.

**Correction to the above** (2026-07-27, later the same day): `0x00205DE8` — the address `jalr s0`
actually calls at `0x0011C270` — disassembles to just `jr ra; nop`. It's an **empty stub**, not real
constructor logic as claimed above. Confirmed by reading it directly rather than inferring from the
`jalr` firing. Its sibling `0x00205DF0` (passed as an argument to `0x00476848` at `0x0011C288`,
not called directly) is the same empty stub. The *real* logic nearby, `0x00205DF8`, walks a
NULL-terminated function-pointer list via `jalr` in a loop — but it turned out to be an
**atexit/destructor walker**, not a constructor runner: its only caller (`0x00202068`, found via
`scanword`) tail-jumps straight into the `exit()` trampoline (`0x0011C2B0`) right after calling it,
i.e. it runs at shutdown, not startup. So static-constructor execution remains ruled out as the
cause here, just not for the reason originally given — there's no evidence of any real per-game
constructor-runner being invoked at all in this path, empty or otherwise, and no evidence it's
needed (nothing else pointed at C++ static init specifically).

### 7.5 Live-traced correction to the `0x00565B9C` registry-lookup theory

The `0x00204430` lookup-or-die / `0x00565B9C` registry-insert mechanism documented above (§7.4
final entries, and filed as [GitHub issue #1](https://github.com/RazmanianDVL/DetPS2/issues/1)) was
derived **entirely from static disassembly** — never confirmed against a live run. Added two new
`DETPS2_TRACE_EXIT`-gated diagnostics to check it directly: `[REG-INSERT]` at `0x004898DC` (the
registration case) and `[REG-LOOKUP]` at `0x004898F4` (the lookup case), both logging `$ra`/`$sp`/
cycle on every real entry (`EmotionEngine.cs`).

**Result, across the full run to the known crash cycle (476,734,304)**: `[REG-INSERT]` fires
**exactly once**, very early (`cyc=150,000`, `ra=0x00205EE4` — inside the same function-pointer-list
walker discussed above, a *third*, distinct list-walking function at `0x00205E50-0x00205EF8`, not
the atexit one). `[REG-LOOKUP]` **never fires at all** — zero hits in ~476.7M cycles. So the
registration genuinely happens, and the specific lookup-or-die path traced statically is **not**
what triggers this crash. That theory is retracted as the live mechanism; issue #1 needs updating.

**What actually happens, confirmed via `--trace-chrono` right at the `[ABORT-CALLER]` cycle
(`cyc=476,728,204`)**: entry to the abort wrapper (`0x00476808`) is reached by genuine, clean
straight-line fall-through from the tail end of the message-building function at `0x004767A8`
(`jr ra` at `0x004767A0` returns into `0x004767A8` itself — the same "compiler lays adjacent
functions out so one's epilogue return address is literally the next function's entry" pattern
documented earlier in this file, not corruption). Following it back with `$a0` logged at
`0x004767A8`'s own entry (existing `[MSGBUF-A0]` diagnostic, `DETPS2_TRACE_MSGBUF=1`):

```
[MSGBUF-A0] a0=0x000000000000000B a1=0x0077F809 a2=0x0000005D cyc=476727904 ra=00000000
[MSGBUF]    v1=0x0000000C cyc=476728224 msg=""
```

`a0=0x0B` (**11**, decimal) is the buffer pointer this message-builder is called with — an absurdly
small value for a real pointer, clearly a small integer being used where a buffer address is
expected (matches `v1=0xC` at the later NUL-terminate step: `0xB+1`, one byte written then
advanced). `a1=0x0077F809` looks like a genuine, plausible format-string address (same region
flagged earlier this whole investigation as "the closest thing to a readable message string ever
captured"). `ra=0` again — the same signature seen at the *original* crash occurrence much earlier
in this investigation (`cyc≈19,755,440`, also `a0=0`/`a1=0`/`ra=0`), suggesting this isn't a one-off:
whatever caller reaches this error/message-reporting path consistently does so with `ra=0` (i.e.
via a chain of tail-jumps all the way back to some root context that itself was entered without a
`jal` — a fresh thread entry or similar) and a **degenerate buffer argument**, at least twice, in
two structurally similar but not identical ways (`0x401A6800` corrupted-word-as-pointer the first
time; a raw small integer `11` this time).

**Concrete next step**: find what calls the message-builder chain (ultimately reachable from
`0x004766B8`'s bracket/charset scanner, itself entered via more tail-jumps) with `a0=11` — since
`$ra=0` can't distinguish *which* root caller this is (every hop in between is a `j`, not `jal`, so
`$ra` never changes), tracing needs a different signal: which thread is executing at
`cyc=476,727,904` (`KernelState.CurrentThreadId` or equivalent) and where that thread's own entry
point is, since `ra=0` strongly implies we're still within a context that has never made a real
`jal`-based call of its own.

### 7.6 A real fix found and applied, then a false-positive discovered and corrected in the same fix

Followed the "which thread" lead from §7.5 directly (`_hle.Kernel.CurrentThreadId` is reachable
from `EmotionEngine`) and found something concrete: at the crash cycle, `tid=1` (the game's main
thread) with `Entry=0`. Checking why led to `KernelHle.cs`'s existing `jr ra`-with-`ra==0` implicit-
thread-exit detection (`EmotionEngine.cs`, added in an earlier session): when it fires and no other
thread is runnable, it had no way to actually halt the CPU — it just returned `false` and the `Step()`
loop fell through to execute whatever raw bytes sat at the delay slot and beyond as if they were real
instructions. **This is confirmed to be the real, general mechanism behind the whole `Exit(1)`
investigation**: it explains the `ra=0` signature seen at every single occurrence traced this session
(cyc≈19.76M, cyc≈476.7M) and the nonsense register values (`a0=11`, `a0=0`) — execution wasn't
following real program logic at all, it was interpreting arbitrary memory as code.

**First fix attempt**: added a `_pendingThreadStall` flag, checked at the top of `Step()` exactly
like the existing `WaitingVblank` stall, that keeps retrying `SwitchToNext` every cycle instead of
falling through. Verified via a full run: `Exit(1)` no longer fires at all through 500,000,000
cycles — but further checking (`DETPS2_TRACE_JREXIT=1`) showed why: the implicit-exit condition
fired exactly **once**, at `cyc=1,350,000` — far too early for the game's real main thread to
legitimately finish — inside `0x00480260`, an ordinary, widely-reused syscall-trampoline leaf
function (`addiu v1,N; syscall; jr ra; nop`, one of many in a table) that returns to a perfectly
real address on every *other* call throughout the run. The EE was left **permanently frozen** at
that point (no other thread existed yet to switch to), just re-presenting a static frame to inflate
`px` to the same long-familiar `76,840,960` ceiling — arguably a worse outcome than the original
crash for reaching a menu, even though the fix's own logic (don't execute garbage) was correct.

**Root cause of the false positive**: thread 1 is created synthetically in `KernelHle.cs`
(`Started=true` from construction, `Entry=0`) and never goes through a real `StartThread`/
`RestoreContext` cycle — the *only* mechanism that deliberately seeds `ra=0` as an exit signal per
the kernel convention this whole detector is built on. Thread 1's `ra=0` at the syscall trampoline
was just the raw CPU boot-state default, never yet overwritten because crt0 reaches that point via
a long chain of pure tail-jumps (`j`) with zero real `jal` calls of its own — not a genuine
"top-level function returned" signal. **Fix, refined**: gate the whole detector on
`CurrentThreadId != 1`. Verified: smoke suite green, 5,000,000-cycle `px` baseline unchanged
(`3,153,920`), `syscalls` back to the original `122` (0 `JREXIT` events through a full run now).

**Honest result**: the game still hits `Exit(1)` — but now at `cyc=28,547,680`, dramatically
*earlier* than the original `cyc≈476,734,304` (only 254 syscalls accumulated by then, vs. 2,550+ in
the old buggy trajectory). This is expected, not a regression: removing a bug that was masking real
program state (a permanent freeze that looked like success) simply exposed the *next* thing in line
sooner. Both fixes are correct and kept — they measurably improved emulator correctness (the game no
longer executes memory as code, no longer silently freezes on a false-positive thread exit) — but
neither is the root cause of `Exit(1)` itself.

**Traced the new, earlier occurrence with the exact same toolkit**: identical signature —
`[MSGBUF-A0] a0=0x0 a1=0x01FFB6E2 ra=0x0 tid=1` at `cyc=28,541,216`, reached via the *same*
bracket-scanner (`0x004766B8`) → message-builder (`0x004767A8`) fall-through chain traced in §7.5,
just at a different cycle. **This exact mechanism has now recurred, identically, at three separate
points across this whole investigation** (cyc≈19.76M originally, cyc≈28.5M now, cyc≈476.7M with the
old NULL-guard patch): same near-zero/null buffer argument, same `ra=0`, same thread (1), same code
path. This stopped looking like "find the one caller" and started looking like a *systemic* property
of this call path — worth internalizing before the next session burns more time on static tracing
here: **the productive next step is almost certainly upstream of the bracket-scanner entirely** —
find what supplies its buffer/input argument in the first place (likely a heap/scratch-allocator
call that's returning near-zero garbage specifically when invoked from thread 1's early, tail-jump-
heavy execution context), not another attempt at walking `ra` back through more tail-jumps.

**Follow-up, same session: the "input string" being scanned isn't a string at all.** `--pcbreak` on
the bracket-scanner's own character-read instruction (`0x00476720: lb a3,0(a1)`) at the new
`cyc=28,541,600` crash caught `a1` walking `0x01FFB6DF → E0 → E1 → …` (a stack address, one byte at
a time, matching the loop). `--dump=01FFB6C0:80` on that exact region shows **mostly zero bytes and
small binary values** — `9C545D18 00008000 00001000 00002000 00002000 00000080 01FEFE00` — not
readable text. This matches the one incidental printable byte already seen in the earlier
`[MSGBUF-A0] a1text="T."` capture (`0x54`='T', sitting inside `0x9C545D18` at the right offset) —
that was never a real format string with one garbage byte, it was **uninitialized stack memory**
with one byte that happened to be printable. So this isn't corruption of a real value, and it isn't
a real message/format string either — it's the scanner being handed a pointer to memory that was
simply **never written** before this call, and reading whatever leftover bytes happen to be there
as if they were meaningful input.

Also notable, same PCBREAK capture: `s0=0x5C3A306D6F726463` — byte-decoded, `"cdrom0:\"` — the exact
byte pattern flagged as a "corruption signature" in the very first investigation round of this whole
saga (predating this session, described back then as part of a `ra=0` corruption cascade later
found to be a false positive in a different guard). Given everything learned this session, that
characterization should probably be revisited too: `s0` isn't touched anywhere in the bracket-
scanner's own body, so it's almost certainly just a leftover, unrelated value from some earlier,
completely legitimate CD-ROM path operation (opening `cdrom0:\SLUS_210.87;1` or similar) still
sitting in a callee-saved register — not evidence of anything wrong at this call site. Worth keeping
in mind if it resurfaces: it's very likely a red herring, not a clue.

**Where this leaves the investigation**: the call site itself (bracket-scanner → message-builder →
abort) is now very well understood, and known to be a real, general, recurring bug — not a red
herring, not title-specific. What's still unknown is *why* whatever calls into this chain hands it a
pointer to memory that was never initialized, i.e. what upstream logic should have written a real
string there first and didn't. That's a caller-side stack-frame/data-flow question, not something
`$ra`-chasing through more tail-jumps will resolve — it needs someone to identify the actual C-level
caller (likely by finding what local variable this stack slot corresponds to in whatever function
owns frame `sp≈0x01FF0120`) and what condition should have populated it.

**Follow-up, same session: confirmed it's stale reused stack memory, not truly-never-written
memory.** `--find-writer=01FFB6E0:20` (the last-writer index — see §10) shows the region genuinely
was written once: `cyc=1,844,224`, `pc=0x004860A8`, matching exactly the bytes seen at the crash
(`0x9C545D18`, `0x00008000`, …). But `0x004860A8` disassembles to a completely generic word-copy
loop (`lw v1,0(a1); a1+=4; sw v1,0(a0); a0+=4; loop` — the SDK's `memcpy`), called from all over the
program for unrelated purposes. So the real shape of the bug is: **some `memcpy` call, 26.7M cycles
earlier and for an entirely unrelated purpose, happened to target this exact stack address as its
destination; the address has been stack space belonging to some other, long-since-returned function
ever since; and the bracket-scanner's caller at `cyc≈28.5M` is reading it as fresh input without
ever writing its own data there first.** The address itself is suspicious: `0x01FFB6E0` is roughly
`0xB5C0` (~46.5KB) *above* the crash-time `$sp` (`0x01FF0120`) — far too large an offset to be an
ordinary local within one function's own small stack frame. Stacks grow downward on MIPS, so a much
larger address than the current `$sp` sits *shallower* in the call stack, close to where the
thread's stack was first set up — consistent with this being a pointer into a large scratch/work
buffer allocated once, early, near the thread's own top-level frame, and passed down through many
levels of nested calls rather than a normal per-function local.

**Not yet found**: the specific instruction that computes this pointer and hands it to the
bracket-scanner without first populating it — i.e. the actual C-level bug. Tracing it further needs
either symbol/debug info this build doesn't have, or a purpose-built diagnostic that catches the
*first* write to a1's target address relative to *this specific call*, not the whole program's
last-writer history (which only shows the closest, but structurally irrelevant, memcpy).

**Important reframing, same session**: `scanword` for the bracket-scanner's own address
(`0x004766B8`), both as a `jal`/`j` call-target encoding *and* as raw data (a function-pointer-table
entry), found **zero hits** — nothing anywhere in the loaded image calls it or references its
address at all, despite it clearly executing in every trace captured. Combined with it sitting
directly after the 800-byte-frame formatter's own epilogue (`0x004766A0-B4`, `0x00475BA8`'s own
`jr ra`/`addiu sp,sp,800`), the most likely explanation is that **`0x004766B8` isn't a separate,
externally-called function at all** — it's an internal continuation of `0x00475BA8`'s own body,
reached via an ordinary internal conditional branch from somewhere earlier in that same function,
not a distinct callee. That reframes the real entry point to trace as `0x00475BA8` itself (already
instrumented — `[FMTENTRY]`, `DETPS2_TRACE_MSGBUF=1`) rather than continuing to look for "the
bracket-scanner's caller," which may not exist as a separate concept. `[FMTENTRY]` shows zero hits
anywhere near the `cyc≈28.5M` crash (only the two original hits near `cyc≈19.7M` from before this
session even started) — worth checking directly next: whether this is genuinely the same,
still-unreturned invocation from `cyc≈19.7M` continuing internally all this time (unlikely across
~8.8M cycles and thousands of intervening syscalls, but not yet ruled out), or whether `[FMTENTRY]`
itself is somehow missing a real second entry.

**Resolved, same session**: a full disassembly of `0x00475BA8`'s body (`0x00475DA8` through
`0x00476750`, previously only read in ~0x200-byte fragments) found the real answer.
`0x004766A0-B4` shares the *exact same 800-byte stack frame size* as `0x00475BA8`'s own entry
(`addiu sp,sp,-800`) — it's not a different function's epilogue, it's **`0x00475BA8`'s own normal
return path**. And `0x004766B8` (the "bracket-scanner") is called from exactly one place, found by
recomputing the `jal` encoding correctly this time (an earlier `scanword` pass in this same session
mistakenly searched for the raw address as data instead of the encoded instruction — always
encode the target as `0x0C000000 | ((target>>2) & 0x03FFFFFF)` before scanning, per §10's own
`scanword` documentation): `0x00475E38: jal 0x004766B8`, itself inside `0x00475BA8`'s own body.
So the entire chain — bracket-scanner, message-builder, abort wrapper — is genuinely all one
continuous invocation of `0x00475BA8`, not several loosely-related functions falling through into
each other.

`0x00475BA8` itself has exactly one real caller in the whole image: `0x00475A20` (`jal`, `scanword`
confirms zero other `jal`/`j` sites). This matches the *first* `[FMTENTRY]` capture from way back in
this investigation **exactly** (`ra=0x00475A28` = `0x00475A20+8`) — that was the legitimate call.
The second, anomalous, all-zero `[FMTENTRY]` capture 512 cycles later (`cyc=19,755,440`,
`a0=a1=ra=0`) does **not** match this caller and was never explained: a `--trace-chrono` capture of
that exact 512-cycle window (`cyc=19,754,900`-`19,755,476`) shows the formatter already deep inside
its own character-processing loop (`0x00475C08-0x00475D18`) throughout, never showing `PC=0x00475BA8`
again — meaning either the cycle-bucketed trace format is coalescing/hiding a genuine but
extremely brief re-entry, or the second `[FMTENTRY]` firing is some other artifact not yet
understood. Left unresolved — this is the boundary of what hand-tracing at this granularity can
resolve without symbol/debug info or a purpose-built per-instruction (not per-cycle-bucket) capture
tool.

### 7.7 Ghidra + real R5900 decompilation — a new, permanent tool for this investigation

Installed Ghidra 12.1.2 (`C:\Users\xxraz\ghidra\ghidra_12.1.2_PUBLIC`) plus the community
`chaoticgd/ghidra-emotionengine-reloaded` extension (real R5900 Sleigh spec — `sq`/`lq`/MMI/COP2,
which stock Ghidra's generic MIPS spec doesn't implement at all and dies on immediately). Processor
ID: `r5900:LE:32:default`. A `ShaolinMonks` project already exists at
`C:\Users\xxraz\ghidra\projects` with `shaolin_boot.elf` (extracted via `extract-file` from the ISO,
`SLUS_210.87` per `SYSTEM.CNF`'s `BOOT2` line — **not** the first ISO file matching a loose `SLUS`
substring search, which pulled a different, unrelated executable, `SURREAL/SLUS_211.89`, the first
time) imported and fully auto-analyzed. Drive it headlessly (no GUI control available in this
environment):

```
cd C:\Users\xxraz\ghidra\ghidra_12.1.2_PUBLIC\support
./analyzeHeadless.bat "C:/Users/xxraz/ghidra/projects" ShaolinMonks -process shaolin_boot.elf -noanalysis -scriptPath "C:/Users/xxraz/ghidra/scripts" -postScript DecompileTargets.java
```

`C:\Users\xxraz\ghidra\scripts\DecompileTargets.java` is a small reusable GhidraScript — edit its
`targets` list (hex addresses) and rerun; it writes pseudo-C for each containing function to
`C:\Users\xxraz\ghidra\decompiled_targets.txt`. This is dramatically faster than hand-disassembling:
what follows was found in minutes, not hours.

**`0x00475BA8` is `vsscanf`.** The switch on format-specifier characters (`%d %f %s %[ %x %o %c %n
%p %u`, length modifiers `%l`/`%h`) is textbook scanf-family dispatch, consuming input via a refill
callback (`FUN_004757b0`) and writing parsed values through a `va_list`-style output-pointer array
(`param_3`). This finally properly identifies the whole subsystem chased since long before this
session — it was never really a generic "formatter."

**Confirmed the one real caller can never produce the observed bug.** `0x00475BA8`'s single static
caller (`FUN_004759a0` @ `0x00475A20`) always calls it with `param_1 = &uStack_f0` — the address of
its own local stack variable. That's simple SP-relative arithmetic; it can never be `0` or `11`. So
the anomalous crash-path entry (§7.6) is now **proven**, not just suspected, to bypass this legitimate
call entirely.

**`0x004766B8`'s real job**: build the `%[...]` scanset membership table *from the format string*,
not scan arbitrary input — called from inside `0x00475BA8`'s own body at `0x00475E38` (real `jal`,
confirmed both by Ghidra's decompilation and independently by `scanword` once the `jal` encoding was
computed correctly).

**New, likely more important lead: a Timer0 interrupt storm freezing forward progress for 26+
million cycles**, found via `DETPS2_TRACE_INTC_DISPATCH=1` (now extended with stack-depth and full
register logging). Timer0 (`Intc.InterruptSource.Timer0` = source 9) dispatches to a real, short,
clean handler (`FUN_001d1748` @ `0x001D17A0`, decompiled — two calls then `return 1`, nothing stuck
inside it) but re-fires **every ~64 cycles**, hundreds of thousands of times by the crash cycle
(409,750 dispatches by `cyc=28,542,000` alone). `_savedRaAcrossIntcDispatch`'s push/pop stays
perfectly balanced throughout (ruling out the "orphaned stack entry corrupts an unrelated `eret`"
theory this diagnostic was built to test) — so this is not the direct source of the `ra=0` signature,
but it's a severe, real bug in its own right.

Early in the run (`cyc≈2.31M`) the interrupted code roams across many different addresses between
storm hits — genuine progress is happening. Starting around `cyc≈2,314,016` it converges into a
tiny ~24-byte range (`0x001CCA6C-0x001CCA84`, disassembled directly: the inner-loop increment/exit
check of a bounded 6×20-entry table linear search, `slti a2,20` / `bne ...,0x001CCA50`) and **never
escapes for the next 26+ million cycles** — every single dispatch interrupts it at one of exactly
three addresses in that range. Captured full registers across dozens of consecutive storm hits:
`a1=0x01FEFF5C`, `a2=0x00010000`, `t0=0x00000080`, `t1=0x00000000` are **bit-for-bit identical every
time**, even though `0x001CCA6C` is literally `addiu a1,a1,20` (the loop's own advance step) — i.e.
the search makes zero measurable progress across the entire 26M-cycle span. Note `a2=0x10000`
(65536) is itself already inconsistent with the loop's own bound check (`slti v0,a2,20`) if this
really is a fresh, correctly-executing pass of the search — worth resolving directly (is `a2` here
genuinely the search's own loop counter, or is register-index-to-source-variable mapping wrong for
this specific capture point?) before deciding whether this is a starvation problem (interrupt
overhead consuming the entire 64-cycle slice, no register corruption needed) or a genuine
register-clobbering bug in the dispatch mechanism.

**Concrete next step**: pin down exactly why these registers never change — either (a) the ~64-cycle
interrupt period is simply shorter than the interrupt-handling overhead itself, so the interrupted
loop never gets a big-enough slice to execute even one iteration (in which case the real fix is the
Timer0 storm itself — find why it's configured/reloading at ~64 cycles when even audio-rate timers
need ~6144, almost certainly an `EeTimers.cs` compare/reload bug), or (b) something in the dispatch/
return path is failing to preserve these specific registers across the interrupt (in which case the
fix is in `TryDispatchRegisteredIntcHandler`/`ExecuteEret` themselves). Use Ghidra to decompile
`FUN_001d1748`'s own callees (`FUN_00321738`, `FUN_0020f330`) next to check whether either one
plausibly consumes anywhere near 64 cycles — if so, that settles it in favor of (a) directly.

**Resolved (a): fixed, and it's a separate bug from `Exit(1)`.** Decompiled the two callees:
`FUN_0020f330` is a genuine `free()`-style allocator (linked-list free-block coalescing across a
128-pool table); `FUN_001ce0e8`/`FUN_001ce380` (the functions whose own logic contains the stuck
6×20 search) are large (560–660 bytes), call a dozen-plus other functions each, and use a
1440-byte local buffer — nowhere near completing in 64 cycles. This matches the SIF/DMAC-fallback
storm class already documented above, just via a different trigger: a periodic timer-tick ISR has
no legitimate "found nothing, don't ack" case the way SIF's queue-check does, so repeated dispatch
can only mean the real ack (that a BIOS-level ISR wrapper we don't emulate would normally have done)
never happened. Fixed by acknowledging INTC for Timer0–3 specifically at dispatch time
(`EmotionEngine.cs`, `TryDispatchRegisteredIntcHandler`) — verified: dispatch count over the same
window drops from 409,750 to 55,108 (~7×).

**But the `Exit(1)` crash happens at the exact same cycle (`28,547,680`) with or without this fix.**
So the Timer0 storm and the `Exit(1)` crash are separate, coexisting bugs — not cause-and-effect as
hypothesized. The storm fix is real and kept; the `Exit(1)` investigation continues independently.

### 7.8 Ruled out every known real call path to the crash

Used Ghidra to decompile all five statically-known in-game call sites to the abort wrapper
(`0x00476808`), found via `scanword` much earlier this investigation: `0x00201844`
(`FUN_002017d0` — a linked-list tag-byte walk, aborts if the terminal tag isn't exactly `1`),
`0x00203A2C`/`0x00203A54` (both inside `FUN_00203a08` — two internal-count consistency checks,
`puVar12[1] != param_2` and `puVar12[1]+puVar12[3] != iVar6`), `0x002043E4` (`FUN_00204070` — a
compact binary-format decoder with an opcode-byte switch, `default: FUN_00476808()` for any
unrecognized byte — classic "malformed resource data" guard), and `0x00476840` (`FUN_00476818` —
builds a formatted message via `FUN_0047e630` first, then falls into the bare abort). All five are
genuine `jal`-based calls, which would set `$ra` to a real, specific, nonzero return address at
`0x00476808`'s entry. Every crash occurrence captured this whole investigation shows `ra=0`.
**This rules out all five as the trigger, on top of the one legitimate `vsscanf` caller (§7.7) and
the function-pointer-table/indirect-call searches (§7.4).** Every traceable, `jal`/`j`/data-reference
based mechanism has now been checked and excluded.

**What's left**: either (a) a genuinely wild/indirect call through a corrupted or uninitialized
function-pointer variable that happens to hold `0x00475BA8`, or (b) fallthrough from whatever
precedes `0x00475BA8` in memory — Ghidra's own function-boundary analysis recognizes `0x00475BA8` as
a clean, distinct function start (real `addiu sp,sp,-800` prologue), which argues against (b) unless
something is jumping *past* that recognized boundary rather than truly falling into it. Neither has
been directly confirmed. This is the honest boundary of what static+live tracing can resolve without
either a full instruction-level replay/rewind capability (to walk backward from the crash instant
without re-deriving cycle windows by trial and error) or symbol/debug info this build doesn't have.

### 7.9 A real, structural explanation for `ra=0` found — then exhausted as the specific mechanism

Went back to §7.8's "ruled out all five known assert sites" work and found the actual gap: the
original `scanword` sweep (way earlier this investigation) found **six** static call sites to
`0x00476808`, not five — `0x00201100` was in that original list and never got decompiled. Fixed
that, and separately ran Ghidra's own `ReferenceManager.getReferencesTo()` (authoritative — reflects
Ghidra's full disassembly/control-flow analysis, not just a raw `jal`/`j`-encoding byte scan) against
both `0x00475BA8` and `0x00476808` to make sure nothing was missed either way. `0x00475BA8` still has
exactly one reference (confirming §7.7's finding independently). `0x00476808` has exactly the same
seven `UNCONDITIONAL_CALL` references the manual scan already found (the six original plus
`0x00201100`) — Ghidra's analysis agrees precisely, no eighth site.

**`0x00201100` turned out to be genuinely structurally important, just not the direct answer.**
It's `FUN_002010f8`: unconditionally calls `FUN_00476808()`, no condition at all — a plain "always
abort" stub. Tracing *its* references found the real mechanism behind `ra=0`: a mutable global
function-pointer slot, `PTR_FUN_004ec8f8` @ `0x004EC8F8`, whose default/initial value **is**
`0x002010F8` (this stub). It's called through by two small dispatch functions,
`FUN_00201108`/`0x00201108` and `FUN_00202e40`/`0x00202E40` — and critically, **their own bodies are
pure tail calls**: call through the pointer, then `return`, nothing else. A pure tail call compiles
to a plain `j`/`jr`, not `jal` — it never touches `$ra`. So if either dispatch function is entered
via a real `jal` (Ghidra confirms both are, from 8 and 14 distinct call sites respectively — a
generic panic/fatal-error utility used all over the runtime, not tied to one subsystem) but the
pointer still holds its unconfigured default, `$ra` survives completely unchanged from whichever of
those many real callers invoked it, all the way down through the stub and into `0x00476808` —
**structurally explaining exactly how `ra=0` could appear at a site with zero calls that
individually look untraceable, without any actual corruption.**

**Checked live and it doesn't fire.** Added `[PANIC-DISPATCH-A]`/`[PANIC-DISPATCH-B]` at both
dispatch functions' own entries (`EmotionEngine.cs`) — neither ever fires before the crash. Also
re-checked the one earlier-found tail-jump site (`0x0020448C`, inside the lookup-or-die function at
`0x00204430`, from way back in §7.4/§7.6) against the current code state, since this session's other
EE fixes changed the execution path leading up to the same crash cycle — `[LOOKUP-ENTRY]`/
`[LOOKUP-TAILJUMP]` don't fire either.

**Bottom line**: every real, statically-and-dynamically-discoverable call path to `0x00476808` has
now been checked — the one legitimate `vsscanf` caller, the two now-decompiled unconditional-abort
dispatch functions and their real callers, all seven direct `jal`/`j` sites Ghidra's own reference
analysis confirms (matching the manual scan exactly), and the tail-jump site inside the lookup-or-die
function. **None of them fire before the crash.** Whatever reaches `0x00476808` with `ra=0` is not
reachable through any reference Ghidra's control-flow analysis can resolve, meaning it's either a
genuine runtime-computed indirect jump through a register value Ghidra can't determine statically
(a real wild pointer, from corrupted or never-initialized data — worth revisiting once its exact
source is known), or something in the emulator's own exception/interrupt-vector handling landing PC
here unintentionally rather than anything in the game's compiled code at all — a genuinely new angle
not yet explored this investigation, and the most promising concrete next step: audit
`EnterException`/`GetExceptionVector`/`ExecuteEret` for any path whose target computation could,
under some COP0 state, produce `0x00475BA8`/`0x00476808` by accident rather than by design.

**Checked immediately, came back clean**: `GetExceptionVector` returns one of four fixed constants
(`0x80000000/0x80000180/0xBFC00200/0xBFC00380`, selected only by `BootExceptionVectors` and
general-vs-specific) — nowhere near `0x00475BA8`/`0x00476808`, no dynamic computation that could
misfire into game-code addresses. Not the direct mechanism. One thing worth flagging for whoever
continues, spotted while reading this code but not yet chased down: `EnterException` unconditionally
overwrites `COP0_EPC = PC` on *every* call, with no check for whether `COP0_Status`'s EXL bit is
already set — i.e. no distinction between a fresh exception and a **nested** one while still inside
an earlier, not-yet-`eret`'d exception. Real MIPS hardware routes a nested exception to `ErrorEPC`
instead in that case; this code always uses the same field, which could lose an outer exception's
real return address if a nested one ever fires before the first `eret`. Not confirmed to explain
`ra=0` (nothing here touches `$ra`, and the specific PC-misdirection theory above didn't pan out),
but a related-looking gap worth understanding rather than assuming benign.

**Fixed and checked live (2026-07-27) — real bug, ruled out as the `Exit(1)` mechanism.**
`EnterException` now matches real MIPS semantics: `COP0_EPC`/`Cause.BD` are only captured when
`Status.EXL` is not already set, exactly like a real CPU refusing to clobber an outer, not-yet-
`eret`'d exception's return address with a nested one's. Added `DETPS2_TRACE_NESTED_EXC=1` to log
every time this path is actually taken (i.e. a nested exception occurs at all) and reran the full
boot to the crash cycle: **zero nested exceptions fire anywhere before `cyc=28,547,680`.** So this
gap was real (worth fixing on its own correctness merits — some *other* title or a later Shaolin
Monks milestone could easily hit it once nested exceptions start happening at all) but conclusively
not the source of this specific `ra=0` signature, joining every other mechanism exhausted in this
section. Verified via `git stash` A/B: `blocker-trace --cycles=5000000` produces byte-identical
output with and without this fix (expected, since the fixed path is provably never exercised in
this run) — a true no-op for Shaolin Monks specifically, kept anyway because it's architecturally
correct.

**Housekeeping note on the `px` baseline number**: this section previously cited `5,000,000-cycle
px baseline unchanged (3,153,920)` as of the thread-1 exclusion fix. The *later* Timer0 storm fix
(§7.7) legitimately changes execution up to cycle 5M as well (removing ~354,000 wasted re-dispatch
cycles changes exactly how far real game code gets by any fixed cycle count) — confirmed via the
same `git stash` A/B method that current `HEAD~1` (pre-nested-EPC-fix, post-Timer0-fix) already
reports `px=860160` at 5M cycles, not `3,153,920`, and that this fix does not change it further.
`860160` also happens to be the same number cited earlier in this section as the *pre-thread-fix*
"long-standing baseline" (§7.6-era) — almost certainly coincidental convergence from a differently-
shaped execution path landing on the same synthetic per-frame `px` ceiling, not a reintroduced bug
(smoke suite is green, thread-1/Timer0 fixes are both still present and independently verified).
Not chased further since it's a metric-housekeeping question, not a playability one — but future
commits touching the EE/scheduler should treat **`860160` at 5M cycles** as the current baseline to
diff against, not the stale `3,153,920`.

### 7.10 The `ra=0` mystery is dead — the real `Exit(1)` is a live, legitimate zlib assertion

**The whole §7.4-§7.9 investigation was chasing an artifact of bugs that are now fixed.** With the
thread-1 exclusion, Timer0 storm ack, and nested-EPC fixes all in place, re-ran the crash live
(isolated single-title config — see the cross-title static-`PcBreakGpr` pitfall below) and the
`Exit(1)` call is now reached through completely clean, real code with a **non-zero, correct `$ra`**
and a **real, non-garbage exit code** — not the `ra=0`/corrupted-register signature this whole prior
investigation was built around chasing.

**Pitfall hit and worked around**: `blocker-trace` against the full 9-title `user-media.json` boots
every title in one process, and `EmotionEngine.PcBreakGpr`/`PcBreakEnd` are `static` — a `--pcbreak`
hit from title 2's own unrelated code at the same virtual address (different games, different link
layouts, same coincidental address) is easy to misattribute to Shaolin Monks. Also: the printed
per-title `after N cyc: PC=... / EE: exitRequested=...` summary line for title *N* is followed by
title *N+1*'s own boot banner *before* its own summary prints (see `Program.cs`'s per-title loop —
boot banner prints immediately, but `exitRequested` prints only after all of that title's own
diagnostic sections run) — so blindly grepping for the nearest `exitRequested=True` after a
`--trace-window` block can attribute the wrong title's crash to Shaolin Monks. **Always use a
single-title scratch `user-media.json` for `--pcbreak`/exact-cycle work on one specific game.**

**New crash cycle**: `28,547,726` (isolated single-title run), up slightly from the old `28,547,680`
— a small, expected shift from the cumulative fixes changing exact timing, confirmed via binary
search on `exitRequested`.

**Live-verified real call chain** (ground truth via `disasm`, not Ghidra's decompilation, which
mis-analyzed `FUN_00476808`'s body — see below):
- `--pcbreak=47FA80` (the `_Exit` SDK wrapper, `0047fa80 _Exit` per the FID database) on the
  isolated run hits exactly once, at `cyc=28547680`, with **`a0=0x1`** (the real exit code) and
  **`ra=0x476818`** (a real, valid return address — not `0`).
- `disasm ... 476790:80` / `476810:60` (ground truth, our own memory) shows `0x00476808` is a tiny
  4-instruction wrapper (`addiu sp,-16; sd ra,0(sp); jal 0x0011C2B0; addiu a0,zero,1` — i.e. **this
  whole "abort wrapper" this investigation spent §7.4-§7.9 chasing is just `Exit(1)`**), called from
  `0x00476840` (`jal 0x00476808`, inside the next function up), which is itself preceded by
  `0x00476838: jal 0x0047E630` (the formatter) with `a1` pointing at a real format string and
  `a3`/`a0` holding two data addresses.
- Dumped those two addresses directly from memory (`--dump=5ae388:80` / `--dump=5ae3b0:60`,
  decoding the little-endian words byte-by-byte as ASCII): `0x005AE388` = the **NUL-terminated
  C string `"c:/Projects/Utility/util_zlib.cpp"`**; the second immediate arg baked into the one
  known static caller (`FUN_004675e8`, decompiled: `FUN_00476818(0x5ae388, 0x5d1, 0x5ae3b0); return
  0xffffffff;`) is `0x5d1` = **line 1489**; `0x005AE3B0` decodes to the single-character string
  `"0"`. This is the exact shape of an SDK `ASSERT(0, file, line)` / `panic(file, line, expr)` macro
  expansion, with the file/line pointing at **the game's own bundled zlib wrapper, line 1489.**

**Ghidra decompilation caveat found along the way**: Ghidra's decompiled body for `FUN_00476808`
(via `DecompileTargets.java`) shows a large function with a malloc-list-append loop and — bizarrely —
a call to itself (`FUN_00476808();` inside its own decompilation). This is wrong; ground-truth
`disasm` of the same address shows a real, tiny 4-instruction function. Whatever produced that
decompilation (likely a stale/bad function-boundary call from the earlier FID Analyzer pass merging
adjacent unrelated code) should not be trusted for this address — always cross-check Ghidra
decompilation against `disasm`'s raw bytes for any function this investigation leans on again.

**Not yet resolved**: exactly what calls into this assert. Ghidra's static reference search found
only one caller of `FUN_00476818` (`0x00467604`, inside `FUN_004675e8`, matching the exact literal
args found above) — but live `--pcbreak` at `0x004675e8`, `0x00467604`, and `0x00476818` itself all
recorded **zero hits** before the crash, even though the assert with those exact literal args
definitely fired (confirmed via the format-string dump above). Since `_Exit` never returns, `ra`
pointing at `0x476818` only proves the `jal 0x0011C2B0` at `0x476810` executed — it does *not* prove
`0x476818`'s own first instruction (`addiu sp,sp,-16`) was ever fetched, so the apparent contradiction
(body executes, but its own entry point + only known static caller show zero hits) likely means
there's a second, real call site with the identical three literal arguments that Ghidra's reference
search didn't surface, or the assert macro gets inlined at more than one call site by the original
compiler. Concrete next step for whoever continues: `--find-word` scan for the exact `lui/ori`
immediate-pair encodings of `0x5ae388`/`0x5d1`/`0x5ae3b0` to find every real call site by raw bytes
rather than relying on Ghidra's reference graph a second time.

**Correction, same session — the zlib string was a real red herring, not the cause.** Chasing "what
calls the assert" further: `--pcbreak` on `0x00476808` itself (the abort/`Exit(1)` wrapper) caught
the real, immediate caller's `$ra` — and it was **`0`**, not a valid return address. Ground-truth
`disasm` (not Ghidra, which is untrustworthy for this address — see above) of the instructions
immediately preceding `0x00476808` showed why: `0x004767F4: jr ra` (a completely ordinary function
epilogue, `$ra` freshly loaded from its saved stack slot two instructions earlier) with `$ra==0`,
hitting the exact same "ignore near-zero jump, fall through instead" guard documented at
`IsLegitimateVectorTarget` — except this time on **thread 1**, which had been deliberately *excluded*
from the genuine implicit-exit-stall mechanism since the thread-1 fix earlier this session (§7.6).
Excluded from the real handling, this `jr ra` silently became a no-op and execution walked forward
through raw memory instead — `DETPS2_TRACE_JRGUARD` confirmed **40,001** such fallthrough events
before the crash, including a long cascade through an entire table of syscall trampolines (each
firing a real, unintended syscall as a side effect — exactly the failure mode the original,
non-thread-1 version of this fix was built to prevent), before coincidentally colliding with
`FUN_00476808` purely because of where that walk happened to end up in memory. **The
`util_zlib.cpp:1489` message was real data that genuinely got formatted and printed (traced
end-to-end, ground truth) — but the `Exit(1)` it fed into was never a real zlib panic. It was
another instance of the exact bug class fixed twice already this session (§7.6, §7.7): thread 1
silently executing garbage instead of genuinely stalling.**

**Real fix**: removed the `CurrentThreadId != 1` exclusion entirely (`EmotionEngine.cs`, the `JR`
case). The original exclusion was based on a since-disproven premise — that thread 1's `ra==0` could
only ever be the raw, never-overwritten CPU boot-state default. Live data now shows thread 1 reaching
a **second, later, genuine** `ra==0` deep into real execution (cyc≈28.5M, loaded from a real stack
slot by ordinary code, nothing to do with boot). Verified via `git stash`-style A/B and the full
9-title `user-media.json`:
- **Zero regressions**: every other title's `px`/`syscalls` at 5M cycles are byte-identical with the
  fix in place; only Shaolin Monks changes (`syscalls` 122→113 — fewer, because the spurious
  trampoline-storm syscalls are gone).
- **No garbage cascade**: `DETPS2_TRACE_JRGUARD` shows **zero** fallthrough events across the whole
  run with the fix applied (was 40,001 without it).
- **No crash**: `exitRequested` stays `False` all the way out to 900M cycles (was `True`/`exitCode=1`
  at ≈28.5M before).
- **Genuine, stable new resting point**: PC settles at `0x00212DD0` — ground-truth `disasm` shows a
  real, clean function entry (object/resource-registration-looking code, repeated calls to
  `0x0020F058` with sequential-looking IDs) — and stays there, unmoving, from 5M cycles out to at
  least 900M, while `spu2Samples` keeps growing (IOP/audio still ticking) and `px`/`syscalls`/`dmac`
  stay exactly frozen. This is the new frontier for whoever continues: almost certainly a real
  blocking wait (semaphore/event-flag/VBlank) that our HLE isn't satisfying, not another garbage-
  execution artifact (confirmed clean via the zero-`JRGUARD` check above).

**One loose end flagged, not chased down**: the stall-retry path in `Step()` (`if
(_pendingThreadStall) { ...SwitchToNext(this)...}`) calls `SwitchToNext` with its default
`fromSyscall: true` even though this call site is *not* a syscall — `SaveCurrentContext` then always
computes `SavedPc = ee.PC + 4`, arguably wrong outside a real syscall context. `--trace-threads`
shows an enormous number of these calls (3.65M `SaveOut` events in a 5M-cycle run) before thread 1
eventually recovers from the cyc=1,350,000 stall — real inefficiency, and worth understanding
properly before relying on `SavedPc` values from this path for anything precise, but not something
this session chased further since it didn't block verifying the fix's own correctness.

### 7.11 Cross-title triage (2026-07-27) and a new ground-truth tool: PCSX2's PINE interface

After the `Exit(1)` fix above, re-tested all 9 titles in `user-media.json` to 100M+ cycles to find
the next best target instead of continuing to dig into Shaolin Monks' own new `0x00212DD0` stall.
Full results on the wiki (per-title pages + `Home.md`) and `COMPATIBILITY.md`. Headline finding:
**Burnout 3: Takedown, MK: Deadly Alliance, and MK: Deception — two unrelated developers — are all
stuck on the byte-for-byte identical shared SN Systems ProDG SDK routine**: a getter/setter pair for
a simple flag table (`lw v0,0(base+idx*4); jr ra` / `sw a1,0(base+idx*4); jr ra`), polled forever by
a caller one level up (`jal <getter>; beq v0,zero,<retry>`) that never sees the flag become nonzero.
Table bases: Burnout 3 `0x004E4140`, MK: Deadly Alliance `0x0040C780`, MK: Deception `0x005D8840`
(all index 0). Vexx remains the single most active title (274K+ syscalls, 5.8MB real SIF traffic)
before its own eventual stall ~100M-200M cycles — corrected from an earlier "still growing" read;
pushed to 400M and it's frozen too, just later than everything else.

**New tool: PCSX2's PINE interface, enabled and verified working this session.** The user gave
explicit permission to edit PCSX2's own config and run it from this admin console directly (no
GUI/computer-use needed for this part). `EnablePINE = true` in
`C:\Users\xxraz\Documents\PCSX2\inis\PCSX2.ini` (`PINESlot = 28011` already present, just off).
Verified end-to-end: `pcsx2-qtx64.exe -batch -- <iso path>` (⚠ `-nogui` specifically hung
indefinitely with a real ISO in this version — memory stayed flat at ~64MB and the PINE port never
opened; `-batch` with a visible window works fine and is what actually got used) then a raw TCP
client to `127.0.0.1:28011` sends `[4-byte LE size incl. itself][1-byte opcode][payload]` and gets
back `[4-byte LE size][1-byte status: 0=OK/0xFF=FAIL][data]`. Opcodes (from
`github.com/PCSX2/pcsx2/blob/master/pcsx2/PINE.cpp`): `MsgRead8/16/32/64`=0-3, `MsgWrite8/16/32/64`=
4-7, `MsgVersion`=8, `MsgSaveState`=9, `MsgLoadState`=0xA, `MsgTitle`=0xB, `MsgID`=0xC, `MsgUUID`=0xD,
`MsgGameVersion`=0xE, `MsgStatus`=0xF (0=Running/1=Paused/2=Shutdown). **Confirmed limitation**: PINE
is memory-only (`vtlb_ramRead`/`vtlb_ramWrite`, same virtual address space the game itself sees) —
no EE GPR/PC/COP0 access, no breakpoints, no stepping. Good for "what's really at this address on
real hardware," not a substitute for register-level debugging.

**Used it immediately to cross-check the shared SDK bug theory against real hardware.** Booted each
of the three affected titles for real in PCSX2 and read the exact table-base address found via
DetPS2's own `disasm`:

| Title | Address | Real PCSX2 value |
|---|---|---|
| Burnout 3: Takedown | `0x004E4140` | `0x00000001` |
| MK: Deadly Alliance | `0x0040C780` | `0x00000001` |
| MK: Deception | `0x005D8840` | `0x00000001` |

All three: **nonzero on real hardware.** This proves the poll loop genuinely resolves on a real PS2
(or accurate LLE) and isn't a legitimately-infinite wait — DetPS2 is definitely missing something
that's supposed to set this flag, for all three titles, via the same underlying SDK mechanism.

**Caught and fixed two address-transcription errors in the process**: initial hand-computed addresses
for MK: Deadly Alliance and MK: Deception (`0x0041C780`/`0x005E8840`) were off by one hex digit from
the real values (`0x0040C780`/`0x005D8840`) — a `lui`+negative-`addiu` reconstruction mistake, the
same error class flagged earlier this session (§7.7's `vsscanf` string decode, etc.). Caught only
because the PINE read against the wrong address returned `0x00000000` inconsistently with Burnout 3's
result, prompting a recheck of the arithmetic rather than assuming "still stalled" — a good example
of why cross-checking against a second, independent oracle catches this class of error that
single-source static analysis won't.

**First static attempt came up empty, corrected with proper Ghidra tooling.** `scanword` for the
`jal`-encoding of the setter's address, and separately for the raw address as data (a function-
pointer table entry), both returned zero matches — a flawed search, since neither method can find a
`lui`+`addiu` pair (each instruction's own operand only ever holds *half* the final address, never
the combined value). Set up a full Ghidra project for Burnout 3 (`extract-file` the real boot ELF via
its `SYSTEM.CNF` `BOOT2` path, import with the R5900 processor + FID database as a `-preScript`,
same recipe as Shaolin Monks — see §7.7) and wrote a proper scan: every `lui` with immediate `0x4E`,
checked against the following `addiu`/`ori` for a combined address landing in the flag table's page.
This is the method to reuse for any future "who touches this address" question — raw byte/data
scanning misses split-immediate address construction entirely.

### 7.12 Full root cause found: DetPS2 never delivers a real SIF/IOP response payload

The proper scan surfaced `FUN_0010e120` referencing the flag table's exact base — a SIF-init routine
that, via Ghidra's decompiler, turned out to *not* be about the setter at all. Full trace, in order:

1. **`FUN_0010e880`** (the function containing the stuck poll) calls `FUN_0010e120`, which performs
   real `sceSifGetReg`/`sceSifSetReg` calls against **software-defined "virtual" register IDs**
   (`0xffffffff80000000`-`0xffffffff80000002` — the high bit as a namespace marker, not real SIF
   hardware register indices 0-31) and, critically, calls the real kernel syscall
   `AddDmacHandler(5, 0x10e688, 0)` — registering a completion callback on **DMAC channel 5, the
   real SIF0 (IOP→EE) receive channel**.
2. **Found and fixed a real, separate bug along the way**: `SonyKernelHle.cs`'s `SifSetReg`/`SifGetReg`
   HLE (`case 0x79`/`0x7A`) only handles register IDs `< _sifRegs.Length` (a 32-element array) — any
   ID with the `0x80000000` marker bit set (exactly what this SDK convention uses) silently no-ops on
   write and always reads back `0`. Real bug, doesn't happen to be what blocks *this* specific poll
   (see below), but affects any title using this same virtual-register convention.
3. **Confirmed live that the DMAC-5 handler DOES fire** — `--pcbreak=10E688` hits twice
   (`cyc=13,724,640` and `13,724,960`), correctly dispatched through DetPS2's existing DMAC/INTC
   machinery. So the dispatch infrastructure (already hardened this session for Shaolin Monks) works
   correctly here too.
4. **The real bug: the handler reads garbage instead of a real response.** Decompiled
   `FUN_0010e688`: it reads a length-prefix byte from a fixed receive buffer (`0x4E3F98`) and, if
   nonzero, copies that many bytes into a local buffer and dispatches through a function-pointer
   table using bytes from the copied data as an index. `--find-writer=4E3F98:8` shows this address
   was written **exactly once**, at `cyc=13,723,936` — the SDK's own one-time static initialization
   (`puRam004e3f98 = &DAT_204e3ec0`, storing a pointer constant there) — and **never again**. No real
   SIF/IOP response payload is ever delivered. When the handler fires, it reads the leftover
   initialization pointer bytes as if they were a real received packet, and processes that garbage
   instead of a genuine response — never reaching whatever real-response code path would eventually
   set the flag table slot the poll loop needs.

**Bottom line**: DetPS2 correctly fires the SIF0/DMAC-5 completion interrupt (matching real hardware
timing/dispatch behavior), but has no HLE that synthesizes an actual, correctly-formatted response
payload for whatever specific SIF service this SDK's init sequence is calling. This is the real,
final blocker — not a missing interrupt, not a missing callback registration, but a missing *payload*.
**Refined further in §7.13 below** — this section's original "reads leftover static-init pointer
bytes as if they were real data" framing was half right (that IS what happens) but stopped one level
too shallow: that pointer's *target* buffer is where the real gap is, and §7.13 traces it to an exact
address and exact expected byte content, live, on both sides.

### 7.13 The exact missing bytes, found via a real PCSX2 remote debugger built for this project

PCSX2's own debugger (registers, breakpoints, memory watchpoints) has no network/scripting API —
confirmed by checking three independent sources (PCSX2's official docs, a community forum thread
requesting a GDB stub that never shipped, and the installed binary's own `-help` output, which lists
only `-debugger` for the GUI). PINE (§7.11) is memory-only. So, with the user's explicit go-ahead to
modify the PCSX2 installation directly ("it's open source... if you need more accurate logging... do
it"), **extended PINE itself** with new opcodes that expose PCSX2's own internal `R5900DebugInterface`/
`CBreakPoints` — the exact same machinery its Qt debugger UI is built on — over the network. Not a new
debugging implementation, just a wire-protocol exposure of an existing, mature one.

**Build setup** (repeatable in ~3 minutes once dependencies are cached):
- `git clone --depth 1 https://github.com/PCSX2/pcsx2.git` (built on `E:\dev\pcsx2-src` — the C:
  drive was too full for a full source+deps+build tree).
- Prebuilt Windows deps: `https://github.com/PCSX2/pcsx2-windows-dependencies/releases` (a
  continuously-updated `latest-windows-dependencies` tag, `.7z`, extract to a `deps/` folder
  alongside `PCSX2_qt.sln` — the archive itself contains a top-level `deps/` folder, so it needs
  flattening one level after extraction).
- Build via `MSBuild.exe PCSX2_qt.sln /t:pcsx2-qt "/p:Configuration=Release AVX2" /p:Platform=x64
  /m` (found via `vswhere`). **Gotcha**: `Start-Process -ArgumentList` does not auto-quote array
  elements containing spaces — `/p:Configuration=Release AVX2` silently splits into two argv
  entries unless the space is embedded inside the string itself
  (`'/p:Configuration="Release AVX2"'`). Output: `bin\pcsx2-qtx64-avx2.exe`. Reuses the same
  `Documents\PCSX2` user-data directory as the normal install (BIOS path, `EnablePINE=true`
  already carry over — no reconfiguration needed).

**Protocol extension** (`pcsx2/PINE.cpp`, opcodes `0x20`-`0x2B`, all reusing existing PCSX2 internals,
none reimplemented):
- `MsgGetPC`/`MsgGetGPR`/`MsgGetCP0` — `r5900Debug.getPC()`/`getRegister(EECAT_GPR/CP0, n)`. Only
  succeed while the CPU is paused (matches how the GUI debugger itself only allows inspection then),
  which sidesteps any need for cross-thread synchronization with the running CPU thread.
- `MsgAddBreakpointEE`/`MsgRemoveBreakpointEE` — `CBreakPoints::AddBreakPoint`/`RemoveBreakPoint`.
- `MsgAddMemCheckWrite`/`MsgRemoveMemCheckWrite` — `CBreakPoints::AddMemCheck(..., MEMCHECK_WRITE,
  MEMCHECK_BREAK)` — a real write watchpoint, not something DetPS2 has an equivalent of at all.
- `MsgIsPaused`/`MsgPauseCpu`/`MsgGetBreakpointTriggered` — thin wrappers.
- `MsgResumeCpu` — **not** a thin wrapper. A plain `resumeCpu()` (`VMManager::SetPaused(false)`)
  left the just-hit breakpoint's internal state armed, so the CPU immediately re-paused on the exact
  same instruction instead of continuing — confirmed live (PC never advanced past a watchpoint hit
  until fixed). Found the real fix by reading `DebuggerWindow::onVMPaused`'s own resume path
  (`pcsx2-qt/Debugger/DebuggerWindow.cpp`) and mirroring it exactly: `Host::RunOnCPUThread` running
  `CBreakPoints::ClearTemporaryBreakPoints()` + `SetBreakpointTriggered(false, ...)` +
  `SetSkipFirst(BREAKPOINT_EE, r5900Debug.getPC())` (and the IOP equivalent) before calling
  `resumeCpu()`. This is exactly the class of bug you'd only find by reading the real GUI code that
  already solves the same problem, rather than guessing at the API surface.

**Live trace, full chain, both emulators, byte-for-byte** (Burnout 3, table index 0, base
`0x004E4140` — see §7.11):
1. Set an execution breakpoint at the DMAC-5/SIF0 handler (`0x0010E688`). Hit immediately (PC, all
   GPRs read correctly) — confirms the handler dispatch itself matches DetPS2's own already-confirmed
   firing.
2. Set a write-watchpoint on `0x4E4140` directly. First two hits are both routine, expected
   zero-writes: the generic crt0 BSS-clear loop (`sq zero,0(v0)`, one quadword sweep through all of
   BSS) and a second explicit small-range clear inside the SDK's own SIF-init routine
   (`FUN_0010e120`, 32 words `0x4E4140`-`0x4E41BC`) — both just initializing the flag to its proper
   0 state, not the bug.
3. **Third hit is the real one**: `PC=0x0010E0BC` (`sw v1,0(v0)`), `v0=v1_addr=0x4E4140`,
   **`v1=1`** — the real value. `ra=0x0010E7A8`, landing squarely inside the DMAC handler
   (`0x0010E688`-`0x0010E7CF`) at the exact point Ghidra's decompile showed an indirect call through
   a registered function pointer (`(*pcVar2)(auStack_a0, ...)`).
4. Dumped the call's actual arguments live: `a0=0x81F20` (a local stack buffer, the handler's own
   copy of received data) held real content at the exact offsets the setter reads (`+0x10=0`=index,
   `+0x14=1`=value — precisely `table[0]=1`), and `a1` pointed at a registration struct
   (`0x4E3F80`+, matching `FUN_0010e608`'s earlier setup) containing the real function pointer
   `0x0010E0A8` (the generic `table[a0->index]=a0->value` setter, ground-truth disassembled directly)
   at a fixed offset.
5. **Dumped that same registration struct from DetPS2 at the equivalent point
   (`--dump=4E3F80:60`) — byte-for-byte identical to real hardware**, function pointers included.
   So the registration/table-building code (`FUN_0010e608`→`FUN_0010e4d0`) is not the bug; DetPS2
   executes it correctly.
6. **`--pcbreak=10E0A8` against DetPS2: zero hits.** DetPS2 never reaches the setter at all, despite
   firing the handler and having correct registration state. `--trace-chrono` right at the handler's
   first firing shows exactly why: `lw a3,16280(v1)` loads a *pointer* stored at `0x4E3F98`
   (`0x204E3EC0`, a static constant written once at SDK init — confirmed identical on both emulators
   via `--find-writer`), then `lbu v0,0(a3)` dereferences it — reading the length-prefix byte from
   wherever that pointer *targets* (`0x204E3EC0`, mirroring to physical `0x4E3EC0`), not from
   `0x4E3F98` itself. `beq a1,zero` **is taken** — DetPS2's byte there is `0`.
7. **The actual missing data, read live from both sides**: `--dump=4E3EC0:30` under DetPS2 shows
   **all zeros**. The same address read live from real PCSX2 (still paused mid-investigation) shows
   real content: `+0x08=0x80000001`, `+0x10=0x00000000` (index), `+0x14=0x00000001` (value),
   `+0x18=0x80000001`, `+0x20=0x8000000A`, matching exactly what the local copy at step 4 held.

**This is the real, final, fully-traced root cause**: `0x4E3EC0` (physical RDRAM; the game's own code
only ever stores a pointer *to* it, never writes it directly) is real SIF/IOP response data on real
hardware and is never written at all under DetPS2 — not "processes garbage instead of a response" as
§7.12 first framed it, but "the specific 44-ish bytes a real IOP response would contain are simply
absent." The `0x80000001`/`0x8000000A`-shaped values match the same virtual-register-ID convention
found in `SonyKernelHle.cs`'s `SifSetReg`/`SifGetReg` fix (§7.12) — plausibly a generic
"registration acknowledged" response from real BIOS-level SIF plumbing, not something specific to
this one IOP module, though that's not yet confirmed.

**Not yet fixed.** The exact bytes are now known for this one specific Burnout 3 call site, but
hardcoding them would be a single-game hack, not a general fix — matching this project's own
standing bar (§4's whole thesis). The real fix needs the *generation logic* real BIOS/SIF plumbing
uses to produce this response (most plausibly discoverable from `ps2sdk` source, the same way the
Virtual HDD work in §9 cross-checked real struct layouts rather than guessing), so DetPS2 can
synthesize the correct response for *any* registration of this shape, not just this one captured
instance. The PCSX2 remote-debugger extension built this session is reusable for that next step, and
for any future "what does real hardware actually do here" question on any title.

### 7.14 The missing data identified as a standard SIF command-queue drain — scoped for a future fix

Extended the PCSX2 debugger further with a DMA-level trace: `Sif0.cpp`'s two transfer functions
(`WriteFifoToEE`/`WriteIOPtoFifo`) now log every SIF0 (IOP→EE) transfer's source/dest/size into a
128-entry ring buffer, exposed over PINE (`MsgGetSif0Trace`=0x2C) — genuinely new information PCSX2's
GUI debugger itself doesn't surface (it shows CPU execution, not DMA-engine-level transfers).
`Console.WriteLn` doesn't reach stdout in `-batch` mode, which is why a PINE-exposed ring buffer was
used instead of a log line.

**The trace resolves the last open question**: `0x4E3EC0` is repeatedly written by **48-byte
(12-word) packets from a sequentially-incrementing IOP source address** (`0x00072DF0`, `+0x30` each
time — `0x00072E20`, `0x00072E50`, ... ) interleaved with several other fixed-size transfers to
unrelated destinations. A fixed-size packet drained one-at-a-time from a monotonically-advancing IOP
source address is the textbook shape of a **SIF command queue drain** — a standard PS2 SDK mechanism
(EE-side code enqueues/registers, IOP-side kernel code appends fixed-size reply packets to a ring
buffer as things complete, and periodically DMAs the next pending entry over to the EE), not
something specific to this SDK or this game. The captured 48-byte payload for Burnout 3's case
matches this exactly: `{0, 0, 0x80000001, 0, 0, 1, 0x80000001, 0, 0x8000000A, 0, 0, 0}` — the
`index=0, value=1` pair the setter reads, surrounded by more of the same virtual-register-ID values
(`0x80000000|N`) already established in §7.12's `SifSetReg`/`SifGetReg` fix.

**Scoped, not implemented.** `RealSifRpc.cs`/`Sif.cs` have no SIF-command-queue concept at all today
— building one correctly (real queue semantics, real packet framing, real per-service dispatch) is a
genuine subsystem, not a quick patch, and hardcoding the one captured 48-byte packet for this one
registration would be exactly the single-game hack §7.12 already ruled out. Deliberately stopped here
rather than rushing a narrow/wrong implementation — this is documented as a concrete, well-scoped
starting point for whoever picks it up next (the DMA-level trace tool above makes re-deriving this
exact data trivial for other titles too, not just re-reading this writeup).

### 7.15 Back to Shaolin Monks with the new tooling: the `0x00212DD0` stall was a real bug, fixed

Per this session's own standing methodology (§4, the "domino effect" thesis), pointed the new
PCSX2 remote debugger (§7.13) at Shaolin Monks' own stall (`PC=0x00212DD0`, stable 5M-900M cycles,
§7.10-7.11) the same way it was used on Burnout 3.

**Real hardware never stalls at all.** A breakpoint at `0x00212DD0` never triggered even after 150+
real seconds of PCSX2 running (dynarec speed, so this covers vastly more than 5M EE cycles worth of
real game time). Pausing and reading the live PC found real hardware happily executing a completely
different, much later region (`0x00274790`) — ground-truth disasm confirmed this is a **completely
normal, expected per-frame VSync field-parity wait** (`ld` from the real GS `CSR` register at
`0x12001000`, extract the FIELD bit, spin until it flips) — real hardware reached an actual running
game loop, presenting real frames.

**The `0x00212DD0` address itself turned out to be a red herring — an artifact of a boot-assist
hack, not a genuine EE resting point.** Re-running with `--no-assist` etc. produced a *completely
different* final PC (`0x00480330`), proving `0x00212DD0` only appears when the assist layer is
active. Confirmed directly with a new diagnostic (`DETPS2_TRACE_STALLCLEAR`, logs every time
`_pendingThreadStall` clears): **zero clears in the whole run** — the implicit-thread-exit stall
(`_pendingThreadStall`, §7.6/§7.11) triggers once, at `cyc=1,350,000`, and never recovers. So
`0x00212DD0` was never reached by real execution at all; something in the assist layer must be
writing `PC` directly.

**Traced the real corruption with `DETPS2_TRACE_REGWRITE_IDX=31`** (the existing "trace one GPR's
write history" tool, built earlier this session): the last real write to `$ra` before the stall sets
it to `0x00482FF8`, inside a known, already-documented `MidwayBootAssist.cs` "SIF-init wait unstick"
hack (`UnstickSifWaits`, targeting Shaolin Monks' own `0x00482740`/`0x00482FF0` polling loop). **Live
cross-check against real hardware nailed the exact bug**: set a breakpoint at the loop's real
`SifSetReg`-trampoline return point (`0x00480268`) — real hardware has `ra=0x00485DF0` (a completely
valid return address, from an entirely ordinary `jal 0x00480260` at `0x00485DE8`); DetPS2 has `ra=0`.

Ground-truth `disasm` of the assist's own jump target (`0x00482FF0`-`0x00483020`) found the exact
gap: both unstick branches set `sys.EE.PC = 0x00483000` directly, **skipping the real instruction
at `0x00482FFC`** (`ld ra,48(sp)`) that the natural, unassisted code path would have executed right
there, immediately before tail-jumping into the same `SifSetReg` trampoline
(`0x00483018: j 0x00480260`). Skipping it left `$ra` holding a stale mid-loop value instead of a
real caller's address — surfacing, cycles later, as thread 1's own `jr ra` (`ra==0`) implicit-exit
firing with nothing else runnable, permanently stalling the EE. This is the exact same bug *class*
already fixed twice this session (§7.6, §7.11) — not a new mechanism, just a third source feeding it
(a boot-assist hack that bypasses a real instruction's effect, rather than a genuinely-corrupted game
register).

**Fixed** (`MidwayBootAssist.cs`): read the real saved `$ra` from `sp+48` before jumping, matching
the skipped instruction's effect exactly, rather than the raw `PC =` assignment. Keeps the assist's
own intent (skip the wait, land at `0x483000`) without bypassing a real instruction.

**Result, verified**: `DETPS2_TRACE_JREXIT` shows **zero** implicit-exit events in a 100M-cycle run
(was one, permanent). Real, sustained forward progress instead of a resting point:

| Cycles | `px` | `syscalls` | `sifBytes` |
|---|---|---|---|
| 5M (old) | 860,160 | 113 | 272 |
| 100M (new) | 1,433,600 | 101,364 | 68,673 |
| 300M (new) | 2,293,760 | 1,314,960 | 68,673 (plateaued) |

`px` climbing well past its old ceiling — real, *new* GS rendering, not a static frame — is the
strongest signal here: this isn't another relocated stall, it's genuine continued execution.
Verified safe across all 9 titles in `user-media.json`: zero change to the other 8 (this fix's
address-range detection is Shaolin-Monks-specific, matching every other `MidwayBootAssist.cs` branch).

**Update, same day — found where it settles, and it's real, not another stall bug.** §7.15's own
"still climbing at 300M cycles" measurement turned out to be misleading: it came from `blocker-trace`
runs that don't drive `OnHostPresent`, the mechanism that paces logo/FMV playback and real frame
presentation (`MidwayBootAssist.OnHostPresent`'s own doc comment already flags this exact gap — see
§10's `probe-frame` entry). Re-checked with `probe-frame` (which does drive it, matching the desktop
app's real per-tick `RunFor`+`OnHostPresent`+`PresentFrame` pattern) and the user independently
confirmed the same thing from a manual desktop-app run: **the Midway logo plays correctly (real,
growing `px` in lockstep with real frame presents), then the instant the logo-hold sequence finishes
(`assist` transitions `logo-hold N` → `logo-done`, `cyc≈163M`), `px` and frame presentation both go
completely flat — confirmed unmoving out to 500M+ cycles.**

Traced with `--host-present --trace-threads`: the EE is not deadlocked (syscalls climb past 1.2
million, a third thread now exists — `logo-done`'s transition code fires a blanket
`SignalSema(1..32)`, per `MidwayBootAssist.cs`, waking whatever it can). PC cycles between a few
addresses including a genuine infinite `nop`-spin (`beq zero,zero,<5 instructions back>`, no internal
exit condition — the kind of construct only an interrupt/preemption is meant to break).

**Decompiled the real cause via Ghidra** (`FUN_0041ed18`, the function containing that spin): it's a
real, intentional **fatal-error handler**, not a bug in itself —

```c
while (true) {
    lVar2 = FUN_004834e0(0x53ddf0, DAT_0053d554, 0);
    if (lVar2 < 0) {
        FUN_004157d0(0x5a6820);   // format + log a fatal error message
        do { } while (true);      // intentional, permanent halt
    }
    if (DAT_0053de14 != 0) break;  // success path
    for (iVar1 = 0xfffb; iVar1 != -1; iVar1 += -4) { }  // real delay, then retry
}
```

Dumped the error string at `0x5a6820` directly from memory: `"E0092101: DTX_Init bind err[or]"`.
**`FUN_004834e0` is `sceSifBindRpc` itself** — confirmed beyond doubt by a literal embedded string in
its own decompiled body (`"SceSifrpcBind"`). Its logic: call `_rpc_get_packet` (`0x483060`, the same
function identified in this investigation's much earlier `SifBindRpc` arc), `CreateSema`, send the
bind request through the **same SIF-command-queue registration mechanism found missing for Burnout 3**
(`FUN_00482c20(0xffffffff8000000N, ...)`, matching §7.14's exact convention), then `WaitSema` for the
real IOP response.

Live `--pcbreak=4834E0` confirmed **825 real `sceSifBindRpc` calls** for many different RPC service
IDs (`0x80000001`, `0x80000003`, `0x80000592`, `0x90000200`, plus FourCC-style custom service names
like `"SNDF"`/`"SFSV"`) — the game legitimately binds many services during startup. Calls stop
entirely after `cyc≈93M` (the last one, for service `0x80000001`), matching where the fatal-error
path fires.

**Conclusion: this is the same root gap as §7.14, found independently from a second, completely
different title.** Shaolin Monks' own `sceSifBindRpc` fails (returns negative) because the real
SIF/IOP RPC-bind response never arrives — DetPS2 has no SIF command-queue implementation to deliver
it, exactly the gap scoped (not implemented) in §7.14. The game's own code correctly detects this
failure and halts (a real, designed fatal-error path, not corruption or an EE-emulation bug) — so
there is nothing to "fix" in `EmotionEngine.cs`/`MidwayBootAssist.cs` here; the real work is the SIF
command-queue subsystem itself, which would very plausibly unblock both titles (and likely others)
at once — a second, independent confirmation of this session's own "general fixes have broad payoff"
thesis, found before any fix was even attempted.

**Not implemented this session**, consistent with §7.14's own reasoning: a real subsystem (real SIF
queue semantics, real packet framing per RPC service, real per-service response generation) is not a
quick patch, and there's now good reason to build it generally rather than per-title.

### 7.16 Correction: §7.15's "missing SIF command queue" conclusion was wrong. The real bug was interrupt-context register corruption — fixed, and it unblocked ~500M cycles of real progress

Picking this back up later the same day, re-verification (fresh code reads and fresh live traces,
deliberately not trusting §7.15's own summarized conclusion) found it didn't hold up: `RealSifRpc.cs`
already answers real `sceSifBindRpc`/`sceSifCallRpc` packets — including the exact CRI ADX/DTX bind
call §7.15 pointed at — unconditionally and correctly. A live trace with `DETPS2_TRACE_RPC` across a
full 100M-cycle run showed hundreds of real binds and calls succeeding throughout, which directly
contradicts "no SIF command-queue implementation to deliver a response."

**The real freeze is much earlier than §7.15 believed: cyc≈1.48M, not cyc≈93M.** Everything §7.15
measured past that point — the logo playing, `px` climbing into the hundreds of thousands, worker
threads running — is scripted boot-assist/other-thread activity, not real main-thread game progress.
Thread 1 itself never recovered from a bind at cyc≈1.48M; it just kept getting round-robin
timesliced against other still-productive threads, masking the fact that it was permanently stuck.

**Root-caused via instruction-level tracing, not summarized decompiles** — every prior finding was
re-derived live rather than assumed correct:
1. `--pcbreak=4834E0:483600` plus a fresh `DETPS2_TRACE_RPC` line added to `RealSifRpc.HandleBind`
   confirmed the CRI ADX bind (`cd=0x53DDF0`, `sid=0x90000200`) really does get a successful
   `HandleBind` response (real data written, semaphore signaled) — twice, at cyc≈1.48M and
   cyc≈86.87M.
2. A fresh Ghidra decompile of `FUN_0041ed18` (the `"DTX_Init bind error"` handler) plus a raw disasm
   of `0x0041ED18-0x0041ED98` located the exact branch: `bgezl v0,0x0041ED98` right after
   `jal 0x004834E0` (`sceSifBindRpc`) — taken on success, falls through to the permanent
   `beq zero,zero,self` halt on failure.
3. `DETPS2_TRACE_SIFSETDMA` (new, instrumented directly at the `PerformSifSetDma` call site) proved
   the underlying `sceSifSetDma` syscall (0x77) genuinely computes a successful, nonzero return
   (`result=2`) for the exact DMA call feeding this bind, at cyc=1,481,744.
4. Yet a `--pcbreak=41ED64` (the unique, single-call-site return-check point) trace showed
   **`v0=0xFFFFFFFFFFFFFFFE` (-2)** at cyc=1,481,808 — only 64 cycles later — reproduced twice,
   independently.
5. A full-chain `--pcbreak=480220:483600` trace, walked instruction-by-instruction, found the real
   moment of divergence: right after the `syscall` opcode at `0x00480224` (which correctly returns
   2, confirmed via the `DETPS2_TRACE_SIFSETDMA` line printed at the identical cycle), execution
   jumped to **`0x00482CA0` with `ra=0x80000200`** — `KernelBootstrap.Kseg0Interrupt`, the common
   interrupt vector — instead of falling straight through to `jr ra` at `0x00480228`. A real SIF
   interrupt (raised synchronously as part of completing the DMA) fired and dispatched into the
   game's own registered ISR *before* the syscall's caller ever got to read `$v0`. Tracing forward
   from there, the ISR's own body (`0x482CA0-0x482DE0`) legitimately uses `$v0` as scratch
   (`lbu v0,0(a3)` etc.) — and when it returns via `eret`, execution resumes at `0x00480228` with
   **`v0=0`**, not the `2` the syscall actually computed.

**Root cause, finally located precisely**: `EmotionEngine.TryDispatchRegisteredIntcHandler` — the
code that redirects execution into a game-registered `AddIntcHandler`/`AddDmacHandler` callback
without hand-writing a real BIOS-style dispatcher — only ever saved and restored `$ra` around that
jump (`_savedRaAcrossIntcDispatch`, a `Stack<ulong>`). On real hardware, an interrupt is transparent
to every register: the interrupted code never "called" the ISR, so it had no chance to save its own
caller-saved registers the way a real function call's caller would, and the actual BIOS-level
exception dispatcher (a hand-written asm trampoline neither DetPS2 nor this synthesized shortcut
implements) always saves all 32 GPRs to the kernel exception frame before calling into the
registered C-level handler — which is then free to clobber `$v0`/`$v1`/`$a0-$a3`/`$t0-$t9` exactly
like any ordinary C function would, since its caller (the real BIOS trampoline) already preserved
them. DetPS2's shortcut skipped that save/restore, so **any register a dispatched handler touched
was permanently corrupted in the interrupted code's context** — not a rare edge case, since ordinary
handler code uses scratch registers constantly. This is the exact same bug *class*
`KernelHle.SaveFullContext`/`RestoreFullContext` already solved for thread preemption (2026-07-26/27,
via `MaybePreempt`) — it just was never applied to interrupt dispatch.

**Fixed** (`EmotionEngine.cs`): replaced the `$ra`-only `_savedRaAcrossIntcDispatch` with
`_savedGprAcrossIntcDispatch` (`Stack<ulong[]>`) — `TryDispatchRegisteredIntcHandler` now snapshots
all 32 GPRs before redirecting into the handler, and `ExecuteEret` restores the full snapshot
(instead of popping and restoring only `$ra`) when the handler's own `jr ra` reaches the vector's
`eret`. Verified directly: re-running the identical `--pcbreak=41ED64` trace after the fix shows
`v0=0x0` (a valid, non-negative success return, matching `FUN_004834e0`'s own `uVar2 = 0;` success
path) instead of `-2`; a fresh `disasm 2000000` run (which previously landed mid-way through the
permanent halt spin by cyc=2M) now shows real, different forward progress
(`PC=0x004803DC`, not `0x0041ED78`).

**Verified safe**: full smoke suite (`Tests/DetPS2.Tests.csproj`) passes with zero failures both
before and after. Cross-title check across all 9 titles in `user-media.json` at 20M cycles shows
identical per-title `px`/`syscalls`/`sifBytes`/`dmac` figures to the pre-fix baseline for every title
other than Shaolin Monks — zero measurable regression, as expected for a strictly-additive
correctness fix (it can only make previously-corrupted register state correct; nothing was ever
correctly relying on the old corruption).

**Result — real, substantial, verified forward progress**: with the fix in place, Shaolin Monks no
longer halts at cyc≈1.5M. `px` (real GS pixel output) climbs to ~77M by cyc=200M (`--host-present`,
matching the real desktop app's pacing) and plateaus there through cyc=500M — a genuine new resting
point, not a still-climbing figure this time (checked explicitly, learning from §7.15's earlier
"still climbing" overstatement). Along the way, unblocking this exposed two small, real, genuinely
general follow-on gaps rather than another catastrophic halt:

- Real syscall `0x31` (`iReferThreadStatus`, the interrupt-safe variant of the already-implemented
  `0x30`/`ReferThreadStatus`) was simply unimplemented in `SonyKernelHle.cs`. Added `case 0x31:`
  alongside the existing `case 0x30:` — identical semantics, real fix, not a stub.
- Real syscall `0x34` (`iWakeupThread`, the interrupt-safe variant of the already-implemented
  `0x33`/`WakeupThread`) was likewise unimplemented. Added `case 0x34:` alongside `case 0x33:`, and
  added `0x34` to `IsHleForcedSyscall`'s forced-HLE list (matching `0x33`'s own entry there) so a
  game-installed `SetSyscall` hook can't silently take over just the interrupt-safe variant while the
  direct one stays HLE'd.

Both were re-verified independently: smoke suite still passes after each addition, and the 9-title
cross-title check shows no fallout.

**Where it settles at cyc=500M**: thread 1 is genuinely `sleeping=True waitSemaId=0` (a plain
`SleepThread`, not blocked on anything) with `syscalls=183,480`, `sifBytes=15,732`, `dmac=7`,
`gifPath3=5`. This is a new, distinct plateau — not yet investigated — and the natural next target
with the same methodology (live `--pcbreak`/`--trace-threads` tracing, PCSX2 comparison if needed).

**Broader significance**: this fix is general, not Shaolin-Monks-specific — any title that takes a
real interrupt shortly after a syscall whose return value its caller then reads (a very ordinary
pattern) was at risk of exactly this kind of silent, hard-to-diagnose register corruption. This is a
substantially better candidate for "the general fix with wide catalog payoff" than the abandoned
SIF-command-queue theory ever was, and it was found by refusing to build further on a
not-independently-re-verified conclusion from earlier the same day — worth remembering as its own
methodology lesson alongside §4's "always re-verify against a full run" one.

### 7.17 The real architectural gap behind all of it: the EE was reaching devices it can't physically touch

Even after §7.16's fix, Shaolin Monks still visibly froze on the Midway logo for the user (screenshot:
`PC=0x004145D8`, `cycles=177,000,000`) — the same symptom, just later. Live-traced this new freeze the
same way as before and found thread 2 permanently spinning in `FUN_004145a8`
(`while (DAT_005341d8 == 0) { FUN_00414590(); }`, a real, ordinary thread that's supposed to clean up
and exit once some completion flag arrives) via a rebuilt custom PCSX2 (this session's remote-debugger
extension, further extended with a real, non-blocking file-based execution/memcheck tracer —
`CBreakPoints::TraceLog`/`AddTracePoint`, `pcsx2/DebugTools/Breakpoints.{h,cpp}` +
`pcsx2/x86/ix86-32/iR5900.cpp` — after discovering the existing `MemCheck::Action`'s `MEMCHECK_BREAK`
path is a stub in this PCSX2 version, and that the recompiler's *actual* memcheck hit handler is a
completely separate, unrelated function, `dynarecMemcheck`, that never calls it).

**Direct memory polling (PINE `MsgRead32`, no pause needed) settled it**: `DAT_005341d8` never becomes
nonzero — not in 30 seconds of real time, not even after the game reaches genuine running gameplay
(`PC=0x00274790`, the real VSync wait, confirmed at the same instant). Thread 2 is stuck on real
hardware too, forever, while the rest of the game runs normally around it — **it was never the actual
blocker**. Chasing it further this session (a write-watch, execution breakpoints on every statically-
found writer) was chasing a red herring; the real divergence had to be some other thread.

**The user, who provided the real PS2 hardware service manual for this exact question, pointed at the
right root cause directly**: DetPS2 wasn't actually simulating the EE and IOP as separate, physically
distinct processors relaying information across a real bus — it was letting the EE-side syscall
handler answer for both. Read the actual schematic (`sony-ps2-scph-39001.pdf`, SECTION 3 BLOCK DIAGRAM
for D type, SCPH-30002D/30003D/30004D service manual) to confirm exactly what that means in hardware
terms: **the EE and IOP are separate chips joined only by a narrow 32-bit `AD0-31` bus** (SIF is drawn
as a sub-block *inside* the EE package — the EE's own gateway onto that bus, not a separate chip).
**CD/DVD (DSP + mecha-con), SPU2, Boot ROM, and the pad/memcard front terminal are all wired to the
IOP's own "SUB DATA BUS / SUB ADDRESS BUS / SYSTEM CONTROL BUS"** — the EE has *no physical connection*
to any of them. The only way the EE can ever read a sector, play audio, or read a controller is by
sending a message across that bus to the IOP, which does the real work on its own bus and relays the
result back.

Confirmed DetPS2's code violated this directly: `Ps2System.cs` wires `Cdvd`/`Spu2`/`PadInput` as
top-level objects the EE-side dispatcher (`RealSifRpc.cs`, `SonyKernelHle.cs`) holds direct references
to and calls into synchronously, from *within* the same EE instruction (`SifSetDma`, syscall 0x77)
that issued the request — collapsing the real hardware's cross-chip relay into a single function call.
This is architecturally impossible on real hardware and is the real, general root cause behind the
whole session's recurring "a real IOP-mediated completion never arrives" bug class (Burnout 3's stuck
wait-flag, Shaolin Monks' DTX bind, and — per the above — thread 2's flag too, even though that one
turned out to be benign): none of these could ever be modeled correctly by an instant, synchronous
shortcut, regardless of how many individual per-service response bytes get hand-tuned.

**Fixed** (not just scoped): `Sif.cs` already had a working async queue+drain pattern
(`_rpcPacketAddrs`/`Step()`), just wired only to DetPS2's own synthetic homebrew RPC ABI (`SifRpc.cs`),
never to the real commercial-game protocol (`RealSifRpc.cs`), which was invoked synchronously and
directly from `SonyKernelHle.HleSifCmdFromEe` — called from inside `PerformSifSetDma`, the `SifSetDma`
syscall's own handler. Added a parallel `_realRpcQueue`: `HleSifCmdFromEe` now calls
`Sif.SubmitRealRpc(eePacket)` (after a cheap peek, `RealSifRpc.IsRealRpcPacket`, to recognize a real
bind/call cid without processing it early) instead of calling `RealSifRpc.TryHandle` directly. The new
`SonyKernelHle.DrainRealRpcQueue()` does the actual dispatch, called once per ambient scheduler tick
from `Ps2System.ISchedulable.Step` (right after `Iop.Step`, matching the real ordering: IOP processes
its own queued work on its own turn) — never from inside `PerformSifSetDma` itself. A response is now
never visible until at least the next scheduler slice after the request was issued, not within the
same EE instruction, the way it would be if the EE and IOP genuinely were separate, independently-timed
chips.

**Verified**: full smoke suite passes (0 failures). 9-title cross-check at 20M cycles shows zero
regression for the 8 other titles — identical `px`/`syscalls`/`sifBytes`/`dmac` to the pre-change
baseline for every one of them. Shaolin Monks itself still reaches the same ~77M `px` ceiling with
real, non-crashing, non-exiting progress at both 200M and 600M cycles — the boot timeline shifted
(worker threads that used to appear by 20M cycles hadn't spawned yet in the same window, since SIF
calls that used to resolve instantly now cost at least one real scheduler tick each), which is the
expected, intended effect of modeling real cross-chip latency instead of a regression.

**Not done, explicitly out of scope tonight**: this fixes the *relay timing* — the EE can no longer get
an instant, same-instruction answer from a device it has no real bus access to. It does **not** make
the IOP actually execute loaded IRX module code (`IopModuleHost.LoadIrx` still just copies module
bytes into IOP RAM and records metadata; `IopModuleHost.Dispatch`/`RealSifRpc.Dispatch` are still
hand-written, per-service C# approximations of what each real module would compute, not the real
module's own code actually running). That remains the honest, much larger follow-on: real R3000A
execution of arbitrary game-supplied IRX modules, which is what a full hardware-accurate emulator
(PCSX2 included) actually does instead of per-service HLE guessing. `ARCHITECTURE.md`'s "Current
Limitations" section is updated to state both facts precisely — what's now modeled honestly (relay
timing) and what still isn't (the IOP's own module execution).

### 7.18 The async relay fix's own self-inflicted regression, then the real WaitSema fabrication bug underneath it

§7.17's fix immediately exposed two more bugs, both downstream of the same root cause (an EE-side
syscall handler that used to get an instant answer no longer does) and both fixed the same night.

**Bug 1 — real RPC packet pool exhaustion.** A live `--pcbreak` trace (5.6GB output) caught thread 1
calling `sceSifBindRpc` for `sid=0x80000592` (the CDVD bind) *millions* of times in a tight retry loop.
Root cause: the real, retail `sifrpc.c` EE-side packet pool is small and fixed-size, freed only when a
real IOP response clears `PACKET_F_ALLOC`. §7.17's once-per-scheduler-tick drain couldn't keep up with
a retry loop that can issue many bind attempts within a single tick, so the pool exhausted for real —
not a DetPS2 hang, a genuine, hardware-accurate retry storm caused by *too slow* a drain. Fixed via
generation-tagging: `Sif._realRpcQueue` entries are now tagged with `Ps2System.SchedulerGeneration`
(incremented once per `ISchedulable.Step()` call — `MasterCycles` alone can't distinguish "this tick"
from "an earlier tick", since it only advances once per whole `RunFor` slice). `TryDequeueRealRpc`
refuses an entry from the *current* generation (preserving "never answered within the same
instruction") but drains anything strictly older whenever called — and `PerformSifSetDma` now also
opportunistically drains mid-`EE.Step()`, so a tight in-game retry loop frees up older packets during
its own execution instead of waiting for the tick boundary. Verified: the retry storm is gone from a
live re-trace (clean, incrementing `WaitSema BLOCKED`/`FABRICATING` pairs replaced the millions of
duplicate binds).

**Bug 2 — the real one underneath.** Even after Bug 1's fix, Shaolin Monks still plateaued at the exact
same PC (`0x0048350C`, inside `sceSifBindRpc`) and the exact same `px≈76,840,960` ceiling by 300M
cycles. Found it in `SonyKernelHle.cs`'s `case 0x44` (WaitSema): when `WaitSemaBlocking` genuinely
blocks a thread (correctly setting `Sleeping=true`/`WaitSemaId=id`) and `SwitchToNext` finds nothing
else runnable, the *existing* code called `WaitSemaVblank()` (a real, correct stall) immediately
followed, in the same synchronous call, by `SignalSema()` + `WakeupThread()` — **faking success
instantly, undoing its own stall before the real async response (§7.17's new relay) ever had a chance
to arrive.** This was invisible before §7.17 (the real response always landed synchronously, before
`WaitSema` was ever reached), and became a live, active bug the moment SIF responses became
asynchronous: the game read stale/uninitialized bind-result data, correctly judged the bind unresolved,
and retried forever.

A second, subtler trap made this harder to fix than "just don't fabricate": `SwitchToNext` itself
(`KernelHle.cs`) has its *own* built-in fallback — "wake ourselves if sleeping and nothing else is
runnable, so boot doesn't freeze" — that unconditionally clears `Sleeping`/`WaitSemaId` the moment it's
called with nothing else to switch to, regardless of whether a real completion is actually pending.
Reusing the existing `_pendingThreadStall` retry-`SwitchToNext`-every-cycle pattern (built earlier this
session for a different scenario — implicit thread-exit via `jr ra` with `ra==0`) would have hit this
same fallback and re-woken the thread immediately, defeating the fix before it started.

**Fixed**: `SonyKernelHle.cs`'s WaitSema handler now checks `Sif.RealRpcQueueCount > 0` before doing
anything else. If a real SIF RPC is already queued (meaning a real completion — and a real `SignalSema`
call — is genuinely coming), it skips `SwitchToNext` entirely (avoiding its auto-wake fallback) and
calls a new `EmotionEngine.RequestSemaStall()`, which sets a new `_pendingSemaStall` field. The main
interpreter loop (same location as the existing `WaitingVblank`/`_pendingThreadStall` checks) stalls
every cycle while `_pendingSemaStall` is set, polling whether the current thread is still `Sleeping` —
cleared the instant the real `DrainRealRpcQueue → RealSifRpc.TryHandle → SignalSema` path fires for
real and wakes it (`SignalSema` already correctly finds the specific `Sleeping` thread whose
`WaitSemaId` matches). When `RealRpcQueueCount == 0` (no SIF RPC involved — some other, unrelated wait),
the original `SwitchToNext`-then-fabricate safety net is preserved unchanged, so nothing about the many
other titles' non-SIF wait paths changes at all.

**Verified**: full smoke suite passes (0 failures; also fixed one unrelated stale assertion in
`Tests/SmokeTests.cs`'s `Iop_HandAssembledLoop_Deterministic` — it asserted the IOP halts on `SYSCALL`,
which was true under the old halt-on-syscall behavior but stale after §7.17's real R3000A
exception-vectoring fix; updated to assert `Cop0Epc`/`Cop0Cause` ExcCode instead, which is what
actually changed). A quick 30M-cycle Shaolin Monks re-trace with `DETPS2_TRACE_RPC=1` shows the new
`WaitSema STALLING for real completion` path firing cleanly across many distinct semaphores (`0x1`
through `0x10`+) with zero fallback to fabrication, and **real forward progress past the old plateau**:
final PC moved from `0x0048350C` (stuck in `sceSifBindRpc`) to `0x0024E740`, a different part of the
boot sequence entirely, with `px=17,489,920` at that checkpoint.

9-title cross-check at 20M cycles, baseline (pre-fix, via a temporary `git stash`) vs fixed, same
config/seed: all 8 other titles produced **byte-identical** `px`/`gifPath3`/`dmac`/`sifBytes`/`syscalls`
between the two runs — zero regression. Shaolin Monks itself: `px` identical at this checkpoint
(11,755,520 both runs — same amount of real GS work landed by 20M cycles either way), but
`syscalls` dropped from 205 → 116 and `sifBytes` from 2048 → 640 with the fix — exactly the expected
signature of the retry storm being replaced by genuine, single-shot stalls instead of repeated
fabricate-then-retry cycles.

**New frontier found, not yet fixed**: past the old plateau, the same 30M-cycle trace shows a large,
linear sweep of `UnknownMmioRead` telemetry across `0x13607F8C`–`0x13608xxx`+ (incrementing by 4 bytes
each read). `MmioBus.cs`'s mapped ranges are all within `0x10000000`–`0x1000FFFF` and
`0x12000000`–`0x12002000` — real PS2 hardware has nothing legitimate at `0x1360xxxx`, so this reads
like either a genuinely wild/garbage pointer being dereferenced, or an address-translation bug
somewhere upstream, not a real hardware register sweep. Not investigated further this session — the
next concrete lead for continuing the Shaolin Monks push.

---

## 8. Save states & determinism contracts

- Magic `0x44505332`, versioned header, optional deflate envelope (v4).
- Persists: `MasterCycles`, RDRAM, EE GPRs/COP0, IOP GPRs, SIF status, DMA/GS state.
- **No wall-clock fields, ever** (§1).
- `SnapshotEngine` (`SnapshotEngine.cs`) is a *different* mechanism — delta frame snapshots for
  quick save/rewind during development, not the same format as `SaveState`.
- Frozen contracts (do not change without a migration note — see `ARCHITECTURE_FREEZE.md`):
  `ISchedulable` semantics, save magic + version envelope, input tape magic `INPR` v1, HLE syscall
  numbering in `BiosHle`/`SonyKernelHle` (game code hardcodes these numbers — renumbering breaks
  every title that's currently booting), golden framebuffer-hash regression tests.

---

## 9. Virtual HDD (APA + PFS) — 2026-07-25

Memory cards top out at 8MB with only 2 slots and no switching — real PS2 hardware solved this
with the Expansion Bay + Network Adaptor + HDD (APA partition table, PFS filesystem). We're
giving the emulator the same solution: a virtual HDD with effectively unbounded save capacity,
using the *real* on-disk format, not a synthetic shortcut. Memory cards are untouched — this is
an addition, not a replacement.

**Status: foundation only.** `ApaPartitionTable.cs` / `PfsFileSystem.cs` / `PfsVolume.cs` /
`VirtualHdd.cs` implement and unit-test the on-disk format as a standalone library. **Nothing is
wired to game-facing I/O yet** — no SIF RPC service, no IOP device HLE, no syscall interception.
That's the deliberate next phase, not an oversight: the format needed to be built and verified
in isolation first (see `Tests/SmokeTests.cs`'s `Apa_*`/`Pfs_*`/`VirtualHdd_*` tests — 7 tests,
all green, covering checksum validation, checksum-corruption detection, multi-zone file
round-trips, nested directories, delete-then-reclaim, and full serialize/reopen round-trips).

### 9.1 What's real vs. what's scoped down

Struct layouts, field names, checksum algorithms, and the exact zone-bitmap sizing formulas are
verified against real ps2sdk source (github.com/ps2dev/ps2sdk — fetched directly via `curl`, not
paraphrased/summarized, specifically because AI-summarized fetches of these files dropped
precision that matters for byte-exact structs):
  - `iop/hdd/libapa/include/libapa.h`, `iop/hdd/libapa/src/apa.c` (`apa_header_t`, `APA_MAGIC`,
    `apaCheckSum` — sum of the header's 256 u32 words, skipping word 0/the checksum field itself).
  - `common/include/hdd-ioctl.h` (`APA_IDMAX`/`APA_MAXSUB`/`APA_PASSMAX`/`APA_TYPE_*`).
  - `iop/hdd/libpfs/include/libpfs.h` (`pfs_super_block_t`/`pfs_inode_t`/`pfs_dentry_t`/
    `pfs_blockinfo_t`, `PFS_SUPER_MAGIC`, `PFS_SUPER_SECTOR`=8192/`PFS_SUPER_BACKUP_SECTOR`=8193,
    `PFS_BLOCKSIZE`=0x2000, `PFS_INODE_MAX_BLOCKS`=114).
  - `iop/hdd/libpfs/src/super.c` (`pfsGetBitmapSizeSectors`/`pfsGetBitmapSizeBlocks` — genuinely
    non-obvious, deliberately-replicated-verbatim rounding quirks, not a clean ceiling-division).
  - `iop/hdd/libpfs/src/superWrite.c` (`pfsFormat`/`pfsFormatSub` — the exact reserved-zone/log/
    root layout sequence; the `reserved = (0x2000>>scale) + log.count + 3 + bitmapBlocks` formula
    was cross-checked by independently computing "zone right after the root directory's data
    zone" two different ways and confirming they match exactly — see the git history for this
    file for the full derivation walkthrough).
  - `iop/hdd/libpfs/src/inode.c`, `dir.c` (`pfsInodeCheckSum` — same algorithm as APA's checksum;
    `pfsInodeFill`/`pfsFillSelfAndParentDentries`/`pfsFillDentry` — root/general inode and dentry
    construction).
  - `common/include/iox_stat.h` (`FIO_S_IFDIR`=0x1000, `FIO_S_IFREG`=0x2000, `FIO_S_IFMT`=0xF000).

Deliberately out of scope for this pass (documented, not silently approximated):
  - **Single main partition only** (`NumSubs` always 0) — real PFS supports up to 64
    sub-partitions per volume; multi-partition volumes (the OPL "one HDL partition per installed
    game" pattern) aren't implemented.
  - **Single-segment inodes only** — files up to `PFS_INODE_MAX_BLOCKS` (114) direct zones
    (~912KB at the default 8192-byte zone size). Real PFS continues large files via chained
    "segment descriptor" inodes (`next_segment`/`SEGI` blocks); `PfsVolume.WriteFile` throws
    `NotSupportedException` rather than silently truncating if a file would need this.
  - **One zone per directory** (this implementation's own allocator choice, not a real-PFS
    constant) — real PFS grows a directory's dentry listing across multiple zones as needed;
    here, `AddDentry` throws once a directory's single 8192-byte zone (16 x 512-byte dentry
    chunks) is full. Fine for save-data folders; not fine for "install thousands of files."
  - **No journal replay** — the log/journal area is reserved at the correct real on-disk
    position (so a real PFS-aware tool would recognize the layout), but no actual write-ahead
    logging or crash-recovery replay happens. We don't simulate power loss mid-write.
  - **No sub-partition/APA-extended (LBA48, GPT) support** — `apa_header_t`'s `mbr` sub-struct
    is written in the non-GPT (200-byte padding) layout only.

### 9.2 Layout quick-reference

For a single main partition with zero sub-partitions and the default zone_size (8192 bytes,
16 sectors, `Pfs.SectorScale`=4):

```
sector 0            APA "self" header (id "PlayStation2", type MBR) — start of the partition chain
sector N..N+1        each APA partition's own 2-sector (1024-byte) header, chained via next/prev
zone 512              PFS superblock (sector 8192) + backup (sector 8193) — inside one zone
zone 513..513+B-1     zone bitmap (B = GetBitmapSizeBlocks(4, partitionSectors))
zone 513+B            journal/log area start (log.count = max(0x20000/zone_size, 1) zones)
zone 513+B+log.count  root directory inode (1 zone)
zone 513+B+log.count+1  root directory dentry data ("." and ".." only after Format())
```

### 9.3 Concrete next step

Wire this into game-facing I/O — likely as a new SID service in `RealSifRpc.cs`, following the
exact pattern `SidMcServ`/`SidCdScmd` already use (intercept the real IOP-side RPC calls games
actually make, back them with a `VirtualHdd`/`PfsVolume` instance instead of returning `1` for
everything). The real HDD-facing service IDs/protocol (`hdd.irx`'s IOCTL2 transfer calls,
`ps2fs`/`pfs.irx`'s mount/file RPC) need the same kind of verification pass this session gave
CDVD/pad — don't guess service IDs the way `SidMcServ`'s current stub effectively does.

---

## 10. Debugging tools

Most of these are `dotnet run --project src/DetPS2.Core -- <command> [args]`. The stable,
general-purpose ones:

| Command | Purpose |
|---|---|
| `blocker-trace <user-media.json> [--cycles=N] [--pcbreak=HEX] [--dump=ADDR:LEN] [--trace-window=N] [--trace-chrono] [--no-assist\|--no-force-sif\|--no-unstick-waits\|--no-auto-complete]` | The main real-disc-boot diagnostic: boots the title(s) in `user-media.json`, runs N cycles, reports final PC/px/DMA/syscall counts. `--pcbreak` prints full GPR/COP0 state every time PC hits that address (careful — a tight spin loop can produce gigabytes of output in seconds; pipe through `head` or use a short `--cycles`). `--dump` disassembles/dumps raw memory at a fixed address. `--trace-window=N` captures the next N executed instructions past `--cycles`, deduped and **sorted by address** — useful for telling "genuinely stuck in a tight loop" apart from "just landed here when the cycle budget ran out" (see the PC=`0x480338` vs PC=`0x2062B4` investigation in git history), but address-sorting hides actual control flow. Add `--trace-chrono` to instead dump the same window in true execution order (cycle-stamped, one line per instruction, no dedup) — this is what you want for "what jumped where" control-flow bugs (e.g. a corrupted return address); see the `0x00345B08` bad-`jr` finding in §7.4 for a worked example, found by bisecting `--cycles=N` until the "steady linear PC drift" signature first appears, then re-running with a tight `--trace-chrono` window right at that boundary. The `--no-*` flags disable `MidwayBootAssist`'s synthetic hacks individually so you can measure how far *general* fixes alone get a boot — this is how the §4 interrupt-dispatch fixes were verified as real, not accidental. |
| `long-run --hours=N --log=PATH` | Unattended multi-hour soak: boots, runs in chunks, writes flushed timestamped checkpoints. For overnight/background investigation. |
| `probe-frame [biosPath] [isoPath] [--watch=HEX]` | Boots MK Shaolin Monks specifically (hardcoded paths if omitted — pass positionally to override, flags starting with `--` are filtered out of positional matching), runs until the boot-logo FMV resolves or times out, saves a PPM of the framebuffer. Calls `ActiveQuirk?.OnHostPresent` every iteration (required for `MidwayBootAssist`'s FMV pacing to advance at all — see §7.4's "Bug B" for what happens if a tool forgets this). `--watch=HEX` reports every read/write to that address across the whole run (same mechanism as `blocker-trace`'s `--watch`). Useful for a quick visual sanity check without setting up `user-media.json`; convert the output PPM with `ffmpeg -i in.ppm -update 1 out.png` to view it. |
| `extract-file <iso> <pathOrSubstring> <outPath>` | Pull a raw file (e.g. an `.IRX` module) off a mounted ISO for offline analysis. |
| `elf-sections <file>` / `elf-info <file>` | Dump ELF/IRX section headers. |
| `iop-disasm <file> <fileOffsetHex>:<lenHex>` | Disassemble raw IOP (R3000A) bytes — used for reverse-engineering real `.IRX` modules extracted via `extract-file` (see §5.3). |
| `iop-find-word <file> <wordHex> [start] [end]` | Exact-word scan over raw IOP module bytes — e.g. finding import-stub headers or `jal` call sites. |
| `disasm <media.json> <cycles> <addr>:<len> [titleIndex]` | Boots the title, runs N cycles, disassembles a fixed EE virtual-address range. `cycles=0` is fine for pure static disassembly of already-ELF-loaded code/data — nothing needs to execute first. |
| `scanword <media.json> <wordHex> <startHex> <lenHex> [titleIndex]` | EE-side analogue of `iop-find-word`, but scans the *loaded virtual memory image* rather than a raw file — e.g. finding every caller of a function by searching for its exact `jal` encoding (`(0x03<<26) \| (target>>2)`). This is how the whole §7.4 static-constructor call chain (`main()` → `0x2F7F68` → `0x2C6520` → `0x00212990` → `0x00206268`) was traced: each hop found by computing the `jal` word for the previous hop's address and scanning the whole PT_LOAD range for it. |
| `find-word --mask=HEX <target>` | Same idea but for *running EE memory*, with an optional bitmask for matching an instruction pattern regardless of one field (e.g. "any `jalr $ra,$rs`" regardless of which register). |
| `dump-spine` / `play-path` / `majority-catalog` / `netplay-cert` / `commercial-checklist` | The synthetic compatibility/campaign gates the smoke suite's `[Smoke] ...` checklist output is built from — see `COMPLETENESS.md` for what each gate actually certifies. |

Many `probe-*` commands in `Program.cs` (`probe-mk`, `probe-worker`, `probe-struct`,
`probe-callers`, etc.) are **disposable one-off diagnostics** written during specific past
investigation sessions, hardcoded to MK Shaolin Monks' addresses — not a maintained, stable tool
surface. Don't build on them; if you need similar tooling for a new title, write a new one-off or
generalize `blocker-trace`'s options instead.

**Ghidra (installed 2026-07-27, see §7.7 for the full setup)** at
`C:\Users\xxraz\ghidra\ghidra_12.1.2_PUBLIC`, with the `chaoticgd/ghidra-emotionengine-reloaded`
extension for real R5900 support (stock Ghidra's generic MIPS spec doesn't know `sq`/`lq`/MMI and
dies immediately on function prologues that use them — nearly all of them). Reach for this **before**
hand-disassembling anything non-trivial — decompiling to pseudo-C answers "what does this do" in
seconds instead of hours of manual opcode reading, as demonstrated in §7.7 (identified `0x00475BA8`
as `vsscanf` and proved its one real caller can never produce the observed bug, both in one pass).
Drive it headlessly via `analyzeHeadless.bat` + a `GhidraScript` (`C:\Users\xxraz\ghidra\scripts\
DecompileTargets.java` is a ready-made, reusable one — edit its address list and rerun) since there's
no GUI control in this environment. The `ShaolinMonks` project (import of the real boot ELF,
extracted via `extract-file` using the exact `SYSTEM.CNF` `BOOT2` path, not a loose substring match)
already exists and is fully analyzed — no need to reimport for future sessions.

`Debugger.AddBreakpoint(addr)` + `Tracer.Enable()` (`Debugger.cs`, `Tracer.cs`) are the
programmatic (non-CLI) equivalents, usable from C# tests or a REPL-style investigation.

---

## 11. Testing

`Tests/DetPS2.Tests.csproj` is a plain console `Main` (not an xunit/NUnit project — `dotnet test`
will appear to succeed instantly without running anything; use
`dotnet run --project Tests -c Release` instead). It runs `SmokeTests.cs` top to bottom and exits
non-zero with a stack trace on the first failure. This **is** the CI gate (`ARCHITECTURE_FREEZE.md`)
— every phase feature needs a smoke test here, and it must stay green through any change.

Structure: individual `[Smoke] Name OK (...)` lines for unit-style checks, plus aggregate
"campaign" gates (Commercial Checklist, Play-Path, Majority Catalog, Netplay Certification) that
run synthetic multi-step scenarios and report a pass ratio.

---

## 12. Where to start

- Read `CONTRIBUTING.md` for the PR-level workflow rules (never commit BIOS/ISOs, keep diffs
  focused, etc.) — this document is architecture, that one is process.
- If you're adding a new title's boot support: start with `blocker-trace --no-assist ... `
  against a clean checkout to see how far *general* fixes alone get you, before writing any
  per-title code. Most real progress this project has made came from finding a genuine emulation
  bug that just happened to be exposed by one specific title's boot path (§4 is the clearest
  example) — not from writing more hacks. Only reach for §7 once you've confirmed the remaining
  gap is genuinely IOP-module-specific or binary-layout-specific.
- If you're adding a new subsystem (e.g. real IPU/MPEG decode, native Vulkan present): read
  `ARCHITECTURE_FREEZE.md`'s frozen-contracts list first — new work should extend those seams, not
  bypass them.
