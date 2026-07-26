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

**New blocker found (2026-07-25, not yet fixed) — a corrupted return address, traced to its
exact instruction.** `--trace-window` sorts by address, not execution order, which is misleading
for control-flow bugs — added `--trace-window=N --trace-chrono` (dumps `Tracer.Entries` in true
insertion/cycle order instead) specifically to unpick this. Using it plus a cycle-count bisection
(binary-searching `--cycles=N` for the PC value at increasing N until the "steady linear PC drift,
~3.75 bytes/cycle, no taken branches" signature starts) pinned the originating bad jump to a
single `jr ra` at `0x00345B08`, inside a small audio/volume-calc function
(`0x00345930`..`0x00345B08`, real prologue `sd ra,0(sp)` at `0x00345954`). Confirmed via
`--pcbreak=345954`: entered at `cyc=985200` with `sp=0x01FFFE60`, `ra=0x0034D730` (the correct,
legitimate caller) and `s0=0x0274C7C0` (its own "sound object" struct pointer — note this is
*already* past the 32MB RDRAM boundary as a raw KUSEG address, aliasing back into range only via
masking, which is at least unusual). By the matching `ld ra,0(sp)` / `jr ra` at
`cyc=986628` (confirmed via `--trace-window=600 --trace-chrono --cycles=986500`), the value
read back from that exact same stack slot (`0x01FFFE60`) is `0x005BA000` instead of the correct
`0x0034D730` — something wrote into this function's own saved-`ra` stack slot while it was
running. `0x01FFFE60` is a very commonly-reused address (confirmed via `--watch=1FFFE60`: dozens
of unrelated functions save/restore `ra` there across a 200,000-cycle window, since it's near the
top of the main thread's stack, reused every time call depth returns to the same level) — the
exact corrupting write wasn't isolated before time ran out this session; the next step is
`--watch=1FFFE60` scoped tightly to `cyc=985200..986628` (this function's own lifetime) to see
literally every access in between and find which one clobbers it, then trace that write's own
source register back to see whether it's an off-by-something in the same audio function's
`13204`-`13224(s0)` struct-field stores (all relative to `s0`, which — see above — already looks
suspicious) or something unrelated stomping the stack from a different call.

**Investigated and ruled out**: `SetupHeap` (EE syscall `0x3D`) was *also* a no-op stub
(`result = 0`) alongside the already-fixed `ReferThreadStatus`, and CRT0 calls it right after
`SetupThread` — a very plausible way for a bad heap-end value to eventually corrupt an unrelated
buffer's placement. Implemented a real return value (matching `EndOfHeap`'s existing
`0x01FFF000` boundary) and re-ran the exact same repro: **identical failure, same cycle, same
PC** — so this isn't what's consumed by whatever's going wrong here. Reverted to `result = 0`
(unverified either way, but changing it demonstrably didn't help) with a comment recording the
negative result so this doesn't get silently re-tried.
`Cdvd.SectorsRead == 0` and the empty object-list loop (§7.4 "Bug C") should be re-checked against
this new, much-further boot state rather than assumed still relevant.

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

## 9. Debugging tools

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

`Debugger.AddBreakpoint(addr)` + `Tracer.Enable()` (`Debugger.cs`, `Tracer.cs`) are the
programmatic (non-CLI) equivalents, usable from C# tests or a REPL-style investigation.

---

## 10. Testing

`Tests/DetPS2.Tests.csproj` is a plain console `Main` (not an xunit/NUnit project — `dotnet test`
will appear to succeed instantly without running anything; use
`dotnet run --project Tests -c Release` instead). It runs `SmokeTests.cs` top to bottom and exits
non-zero with a stack trace on the first failure. This **is** the CI gate (`ARCHITECTURE_FREEZE.md`)
— every phase feature needs a smoke test here, and it must stay green through any change.

Structure: individual `[Smoke] Name OK (...)` lines for unit-style checks, plus aggregate
"campaign" gates (Commercial Checklist, Play-Path, Majority Catalog, Netplay Certification) that
run synthetic multi-step scenarios and report a pass ratio.

---

## 11. Where to start

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
