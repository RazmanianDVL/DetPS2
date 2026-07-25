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

### 7.4 MK Shaolin Monks boot trace — current state (2026-07-25)

Neither the "pure" boot (all `MidwayBootAssist` hacks disabled via `--no-assist` etc., testing
whether general fixes alone are sufficient) nor the assisted boot currently reaches a real menu
or gameplay. Traced precisely, instruction-by-instruction, to two distinct, now-understood bugs:

**Bug A (root-caused, partially mitigated): `sceSifInitRpc` never runs.**
`sceSifBindRpc` (real vaddr `0x4834E0`) fails because its packet-pool allocator
(`_rpc_get_packet`, real vaddr `0x483060`) sees `_sif_rpc_data.pkt_table_len` (at `0x77A088`,
offset+8 of the real `struct rpc_data` — confirmed field-by-field against
`ee/kernel/src/sifrpc.c`) still zero. `sceSifInitRpc` (real vaddr `0x482E98`) is the only code
that sets it (`= 32`, confirmed by disassembling its body) — and it never runs: every one of its
14 real call sites across the whole binary was checked with `find-word`/`--pcbreak`, and none
fire before the pad-bind retry starts (`main()` itself reaches this point directly, single
thread — an earlier "separate thread starvation" hypothesis this session was empirically
disproven via `DETPS2_TRACE_PREEMPT=1`). `MidwayBootAssist.MaybeForceSifInit` already force-calls
this exact real function (not a synthetic memory-poke) — but was gated on
`Gs.PixelsWritten > 0 || Gif.Path3Transfers > 0`, a chicken-and-egg condition when pad/input needs
to init before anything renders. That gate was removed 2026-07-25 (see the method's comment) —
verified: full smoke suite green, zero change to the already-working assisted-boot outcome
(confirming no regression), and it's the correct fix for the pure-path ordering gap even though
the pure path still doesn't reach further on its own (see Bug B).

**Bug B (found, not yet root-caused): FMV/logo frame count never advances.**
Even with `MaybeForceSifInit` firing successfully (confirmed: `dmac`/`sifBytes` counters go
nonzero), the boot plateaus — verified flat from 500M cycles all the way to 3B cycles (identical
PC/pixel/syscall counts) via `blocker-trace`, and separately via `probe-frame` (which, unlike
`blocker-trace`, does call `OnHostPresent` every iteration — ruling out "the CLI harness just
never drives the present loop" as the explanation). `MidwayBootAssist.LogoFrame` gets set to `1`
exactly once (confirms `MaybeStartLogo`'s first-frame path ran) but never increments across 80+
subsequent `OnHostPresent`/`AdvanceLogoOneFrame` calls, and `Status` stays `"sif-forced"` rather
than ever showing `"logo-playing"` again — meaning `AdvanceLogoOneFrame` itself, or the
`_logoActive`/pacing state it depends on, isn't behaving the way a straight reading of the code
implies. Root cause not yet found — the likely next step is instrumenting
`OnHostPresent`/`AdvanceLogoOneFrame` directly (a `DETPS2_TRACE_*`-style env-gated trace, matching
the pattern already used for RPC/preemption) rather than continuing to infer state from the
outside, since static reasoning about this one hit a contradiction (the code path that would
explain the observed `LogoFrame`/`Status` combination isn't obvious from inspection alone).

PC ends up oscillating among three addresses (`0x474F94`/`0x476A28`/`0x476D44`) inside a
real MMI-based memory/string-scan routine (`psubb`/`pnor`/`pand`/`pcpyld` — a classic SWAR
`strlen`-style byte scan) — this is very likely *not itself* the bug (it's plausibly a hot,
frequently-called utility function, not a spin loop), just where the periodic 1M-cycle samples
happen to land while whatever outer loop calls it repeats without completing.

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
| `blocker-trace <user-media.json> [--cycles=N] [--pcbreak=HEX] [--dump=ADDR:LEN] [--trace-window=N] [--no-assist\|--no-force-sif\|--no-unstick-waits\|--no-auto-complete]` | The main real-disc-boot diagnostic: boots the title(s) in `user-media.json`, runs N cycles, reports final PC/px/DMA/syscall counts. `--pcbreak` prints full GPR/COP0 state every time PC hits that address (careful — a tight spin loop can produce gigabytes of output in seconds; pipe through `head` or use a short `--cycles`). `--dump` disassembles/dumps raw memory at a fixed address. `--trace-window=N` captures the last N executed instructions once the run completes, useful for telling "genuinely stuck in a tight loop" apart from "just landed here when the cycle budget ran out" (see the PC=`0x480338` vs PC=`0x2062B4` investigation in git history for a worked example of this distinction mattering). The `--no-*` flags disable `MidwayBootAssist`'s synthetic hacks individually so you can measure how far *general* fixes alone get a boot — this is how the §4 interrupt-dispatch fixes were verified as real, not accidental. |
| `long-run --hours=N --log=PATH` | Unattended multi-hour soak: boots, runs in chunks, writes flushed timestamped checkpoints. For overnight/background investigation. |
| `probe-frame` | Boots MK Shaolin Monks specifically (hardcoded paths), runs until the boot-logo FMV resolves or times out, saves a PPM of the framebuffer. Useful for a quick visual sanity check without setting up `user-media.json`. |
| `extract-file <iso> <pathOrSubstring> <outPath>` | Pull a raw file (e.g. an `.IRX` module) off a mounted ISO for offline analysis. |
| `elf-sections <file>` / `elf-info <file>` | Dump ELF/IRX section headers. |
| `iop-disasm <file> <fileOffsetHex>:<lenHex>` | Disassemble raw IOP (R3000A) bytes — used for reverse-engineering real `.IRX` modules extracted via `extract-file` (see §5.3). |
| `iop-find-word <file> <wordHex> [start] [end]` | Exact-word scan over raw IOP module bytes — e.g. finding import-stub headers or `jal` call sites. |
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
