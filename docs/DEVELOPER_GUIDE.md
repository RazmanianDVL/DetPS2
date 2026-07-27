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
