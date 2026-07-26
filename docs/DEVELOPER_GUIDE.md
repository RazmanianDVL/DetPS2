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
