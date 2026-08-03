# Title-Specific Hacks

**Policy**: Prefer **global** fixes (EE/GS/VU/CDVD).
Only add a row here when a global fix would break other titles.

**As of the GameQuirks SDK** (see `src/DetPS2.Core/GameQuirks/IGameQuirkModule.cs` and
`docs/DEVELOPER_GUIDE.md` §7): new per-title hacks should be implemented as an
`IGameQuirkModule`, registered by disc serial in `GameQuirkRegistry`, and logged here.
They should NOT be hand-edited into shared core files.

## Real SIF RPC dispatch (2026-08-02) — the thing this whole file exists to make unnecessary

Every PS2 title, however different its own engine, registers its IOP-side services through the
exact same standard BIOS mechanism: `sceSifSetRpcQueue` + `sceSifRegisterRpc`, building a real
linked list of `SifRpcServerData_t` entries in IOP memory that the real SIFCMD dispatcher walks
by `sid`. This was ground-truthed by extracting the real `SIFCMD.IRX` straight from the BIOS ROM
(`romdir-extract`) and decompiling it in Ghidra (project `SIFCMD`):

- `sceSifSetRpcQueue` == `FUN_00001088` — appends a queue to a global chain (module-relative
  `.data` offset `0x2a60` holds the chain head; each queue's `+0x14` is "next queue").
- `sceSifRegisterRpc` == `FUN_00001130` — appends a `SifRpcServerData_t` to a queue's server list
  (`+0x00` sid, `+0x04` func, `+0x08` buff, `+0x38` "next server", queue's `+0x08` is the list
  head). Verified the offset convention is correct: SIFCMD's real live entry point
  (`0x1C1580D0`) minus its real live load base (`0x1C158000`) is exactly `0xD0`, matching the
  Ghidra-analyzed `module_start` function address.

`RealSifRpc.HandleCall` now walks this REAL, live registry first (`TryFindRealRpcServer`) before
falling through to any hardcoded per-sid HLE branch below. If a genuinely loaded, genuinely
running module has actually registered a handler for the sid being called, its real handler runs
on the IOP R3000 core with the real request bytes (`TryDispatchRealRegisteredRpc` — full IOP
context save/restore around the call, since it runs mid-quantum from EE-side call handling), and
its real reply is used — no guessing. Bounds-checked (a matched handler address must land inside
some genuinely loaded module's real image, guarding against a partially-initialized entry while a
module's own `_start` is still mid-registration) and fully backward-compatible: whenever nothing
is really registered yet (module hasn't reached that call, or the service is one of the small set
BIOS-stack modules intentionally never run for real, e.g. LOADFILE/CDVDFSV), it falls straight
through to the existing HLE below, unchanged. Verified safe across the whole roster (all 9 titles
in `user-media.json` boot cleanly, no regressions) and opt-out via `DETPS2_NO_REAL_RPC=1` for
bisection. Trace via `DETPS2_TRACE_REALRPC=1`.

**Current status — not yet firing in practice**: for Whiplash, `IOPFILE.IRX`'s real `_start` now
genuinely executes (see its row below), but its queue-chain head reads `0x00000000` even after
2,000,000 real instructions — meaning its real init hasn't reached `sceSifSetRpcQueue` yet within
that budget, or is blocked on something else earlier in its own init that isn't yet correctly
emulated. That's the next real question: not a per-title hack, but IOP-execution correctness —
what real primitive is `IOPFILE.IRX` waiting on early in its own `_start` that we don't yet
service correctly. Once that's solved (for this or any other title's own IOP driver), this
mechanism activates automatically with zero further per-title work.

**2026-08-03 investigation — Ghidra-verified the C# BIOS conversion against the real BIOS ROM
(`Documents/PCSX2/bios/...SCPH70008.bin`), per direct instruction not to keep guessing at
per-title protocols when the underlying infrastructure itself might be the gap.** Findings:
- **The IOP kernel dispatch is more correct than assumed, not less.** `EXCEPMAN`/`INTRMANP`/
  `INTRMANI` all genuinely complete their real `_start` during the standard boot's IOPBTCONF walk
  (`BiosBootHost.BootIopBtConfLiteral`, which runs unconditionally — no skip-list applies there,
  that only gates *later*, game-requested re-`MOD_LOAD` calls). Verified live: IOP RAM `0x80`
  (the BEV=0 general exception vector) genuinely changes from the emulator's placeholder stub to
  real `SW`-opcode dispatcher instructions after they run — real code is installing a real
  handler chain, exactly as on hardware.
- **Real IOP syscalls do fire** (confirmed via new `[IOP-EXC]` tracing showing the real Sony
  kernel convention: syscall number in `$v0` at trap time, e.g. 1, 8, 0x10, 0x14, 0x20 observed
  live) and mostly return without crashing.
- **Root-caused the actual blocker**: `SDRDRV.IRX`'s real `_start` (the sound hardware driver,
  genuinely running per the 2026-08-02 module-loading fix) eventually calls what is almost
  certainly its own real `SifRpcFunc_t` RPC handler (`FUN_00000410` in the real disc `SDRDRV.IRX`
  — signature and body are an unmistakable `int fno, void *buf` dispatch across ~40 real SPU2
  register operations). Its epilogue is completely ordinary MIPS (`lw ra,0x38(sp); jr ra`) — the
  bug is not in this function. `$ra` reads back as **zero** from the stack at that point, meaning
  something earlier in the real call chain never wrote a real return address there. Confirmed via
  new `[IOP-BADJUMP]` tracing (any `JR`/`JALR` landing under `0x1000`) that the CPU then free-falls
  into the shared, zero-initialized `_start` stack region and infinite-loops on `jr $0` (the raw
  encoding of an all-zero word) for the rest of that module's instruction budget — which is very
  likely why `IOPFILE.IRX` (sharing the same per-module execution budget/scheduling model) never
  gets far enough to reach its own `sceSifSetRpcQueue`/`sceSifRegisterRpc` calls.
**2026-08-03 continued — traced the actual call chain and found two more real, general gaps
(not per-title), per direct instruction that a non-response means the infrastructure, not the
game, is at fault:**
- Traced who calls `SDRDRV.IRX`'s `FUN_00000410`: a new `DETPS2_TRACE_IOP_CALLWATCH` diagnostic
  (traps any `J`/`JAL`/`JALR`/`JR` targeting a chosen physical address) caught a single real call
  from real `LOADCORE` code (`FUN_0000069c` in `tools/bios-decomp/LOADCORE_ALL.txt` — a generic
  "invoke a registered callback now, or queue it if the registry isn't ready" mechanism, not SIF
  RPC dispatch or an interrupt table), at `n=77516`, with a real, correctly-set return address.
  That call **returns correctly** — so the corruption isn't at the call site.
- Ruled out the leading hypothesis directly instead of assuming it: added `DETPS2_TRACE_SPU2REG`
  and confirmed SDRDRV makes only ~106 real SPU2 register reads total in the 5.6M-instruction
  window before the crash — sparse, not a busy-poll. **This wasn't a starved hardware register.**
  (Fixed anyway, since it's real and general regardless: `IopRead8`/`IopRead32`/`IopWrite8`/
  `IopWrite32` never routed IOP-side SPU2 access — `0x1F900000`-`0x1F9007FF` — to the `Spu2` object
  at all; every real access silently read `0`/dropped. Now wired through to the same `Spu2` the
  EE side already uses.)
- Added a coarse `DETPS2_TRACE_IOP_HEARTBEAT` (PC every ~1M instructions) and found the real
  answer: PC advances by exactly `0x400000` between heartbeats spaced `0x100000` instructions
  apart — precisely 4 bytes per instruction. **The CPU isn't looping or waiting on anything — it's
  walking forward through raw memory as NOPs, unbounded, because `IopRead32`'s fallback for a
  genuinely unmapped address silently returns `0` (which decodes as a real NOP) instead of
  faulting.** Real R3000A hardware raises an Address Error (AdEL) the instant PC leaves mapped
  memory; this emulator didn't, so one real, still-unidentified bug (whatever puts `0` in `$ra`
  before `SDRDRV.IRX`'s `jr ra`) turned into an *undetectable, unbounded* runaway that silently
  burned the rest of that module's entire execution budget walking through arbitrary memory
  (confirmed reaching literal ASCII string data and executing it as code) instead of trapping
  immediately into the real, already-correctly-installed exception handler chain.
  - **Fixed**: `Iop.Step` now checks `SystemMemory.IsKnownIopAddress(PC)` before every fetch and
    raises a real AdEL (`EnterException(4, PC)`) when it's false, exactly like real hardware.
    Verified live: the fault now fires and is handled instead of free-running (3 faults in a
    20M-cycle Whiplash trace, each recovering in one step). Verified safe across the full title
    roster — all 9 titles in `user-media.json` produce byte-identical telemetry at 10M cycles
    before and after this change; zero regressions.
  - **Still open**: this makes the *symptom* (unbounded runaway) recoverable and bounded, but the
    original bug — what writes `0` into `$ra` before `SDRDRV.IRX` reaches its own `jr ra` — is not
    yet identified. `IOPFILE.IRX`'s queue-chain head is still `0x00000000` after this fix in the
    same trace window, i.e. real registration still hasn't happened yet in the time available;
    with the runaway now bounded rather than budget-exhausting, this is a more tractable next
    target (single confirmed fault site, `pc=0x00040BE4`, real function `FUN_00000410`) than it
    was before this investigation started.

**2026-08-03 correction — the crashing module is `THREADMAN`, not `SDRDRV.IRX`; root cause is real
IOP thread/context-switch dispatch, not modeled:**
- Continuing "find the gap between our system and real hardware": added `DETPS2_TRACE_IOP_CALLWATCH_AFTER=N`
  (trace N retired instructions after a callwatch hit) and `DETPS2_TRACE_IOP_ADDR_ONESHOT=0xADDR`
  (full GPR + stack dump + a 256-instruction approach-path ring buffer, first hit only) to watch the
  crash site directly instead of inferring it from static decompile alone.
- The one-shot fired at `pc=0x00040BE4 n=5681324`, confirming `$ra` (r31) really is `0x00000000` at
  the "jr ra" — matches the prior finding. But the approach-path ring buffer showed the *actual*
  live call chain reaching it: a real caller at `pc=0x00021178` does a linked call (`ra` set to the
  correct return address `0x00021180`) landing not at the documented function entry `0x00040410`
  but at `0x00040940` — a label deep *inside* the same function body. From there: two more internal
  branches (`0x00040940→0x00040B78→0x00040BD0`) before `$ra` flips to `0` at `0x00040BD8` and the
  shared epilogue at `0x00040BE4` faults.
- Cross-checked address ownership directly instead of trusting the earlier static Ghidra project's
  labels: a new `[LOADIRX-BASE]` trace print in `IopModuleHost.LoadIrx` (every real module load,
  name + assigned physical base) shows the *entire* generic BIOS/stack module set — `SYSMEM`,
  `LOADCORE`, …, up through `XCDVDFSV` — loads through the **same shared sequential allocator**
  (`_nextIopBase`, starting at `IrxLoader.DefaultLoadBase = 0x00010000`) as later game-requested
  loads, each placed contiguously with zero overlap. That trace shows `THREADMAN` — not `SDRDRV` —
  assigned physical base `0x00040000`. `SDRDRV` never appears in `[LOADIRX-BASE]` output at all
  within a 1,000,000-cycle Whiplash trace (i.e. it had not been real-loaded yet at the point the
  earlier session's crash trace was captured). **The earlier session's `sdrdrv_all.txt`/
  `sdrdrv_crash.txt` Ghidra project was decompiling the wrong extracted file** — its `FUN_00000410`
  is really `THREADMAN`'s real internal dispatcher, not `SDRDRV`'s RPC handler; the "~40 real SPU2
  register operations" read in that decompile were misread, not SPU2 calls at all.
- This reframes the bug from a Whiplash-specific audio-driver quirk into a **general, universal
  kernel-module gap**: the real caller at `0x00021178` sits inside `INTRMANI`'s resident range
  (`0x00020000`–`0x00024000`) and makes what looks like an ordinary linked call, but lands on an
  internal `THREADMAN` label reached only via that module's own switch/dispatch logic — consistent
  with a **thread reschedule/context-switch dispatch** (`THREADMAN`'s actual job), not a plain
  function call. Real hardware's context switch saves and restores a *complete, separate* register
  file per thread (including `$ra`) when switching stacks; this emulator's `Iop` class has exactly
  one flat set of 32 GPRs with no per-thread save/restore, so any real switch-triggering call
  necessarily corrupts registers relative to what a genuinely multi-threaded IOP would preserve.
  This matches, and sharpens, the already-documented "real IOP threads spawning/yielding not
  modeled" gap in `docs/DEVELOPER_GUIDE.md` §5.3 — same root cause, now traced to an exact call
  site and exact corrupted register, not a general suspicion.
- **Not fixed this pass**: modeling real per-thread IOP register contexts (multiple GPR sets +
  THREADMAN-aware scheduling) is a substantial feature, not a targeted bug fix — scoped out of this
  investigation. The AdEL fix already bounds the *symptom* (this fault now recovers in one step
  instead of free-running for millions of instructions); the corrected diagnosis here is the
  concrete next engineering target for anyone picking this up.

**2026-08-03 resolved — the "context-switch dispatch" hypothesis above was itself wrong; actual
root cause is a real, general IOP module-placement collision, now fixed:**
- The "internal label reached via a real dispatch" story didn't survive a direct check against
  the pristine module. Extracted the real BIOS `THREADMAN.IRX` (via `romdir-extract`, not a
  possibly-mislabeled prior Ghidra project this time) and Ghidra-decompiled its actual bytes at
  the exact live addresses from the trace. The real disassembly shows `0x00040940` is a normal
  function's own prologue (`addiu sp,sp,-0x30`, straight-line for 40+ instructions, no branch to
  `0x40B78` anywhere nearby), and — decisively — `0x00040BE4` in the real file is `addiu sp,sp,
  -0x20`, the *start of a completely different function*, not `jr ra` at all. The real epilogue
  for the `0x940` function is at `0x40BBC`. **The live trace's control flow (`0x940→0xB78→0xBD0`
  in ~17 instructions, ending in a "jr ra" that isn't really there in the pristine file) could not
  possibly be executing THREADMAN's real code** — something had overwritten it.
- Used the existing `--track-writers --find-writer=ADDR:LEN` diagnostic (already in the codebase
  from an earlier session, built for exactly this) on `0x1C040BE4`. Every word in that range had
  been overwritten by a single write at `cyc=4867856` with values that are themselves real,
  correctly-formed MIPS instructions (`0x03E00008`="jr ra", `0x27BD0040`="addiu sp,sp,0x40", …) —
  i.e. **another real module's own code, landing directly on top of THREADMAN's resident memory.**
- Extended the `[LOADIRX-BASE]` trace (name + assigned base for every real `LoadIrx` call) across
  the *entire* trace window instead of stopping early, and found the actual mechanism: after the
  generic BIOS module set loads once (`SYSMEM`…`FILEIO`, `XLOADFILE`…`XCDVDFSV`), a **second,
  entirely legitimate wave** reloads `LOADCORE`/`SIFCMD`/`SIFMAN`/`THREADMAN`/`IOMAN`/`MODLOAD`/
  `FILEIO`/`CDVDMAN`/`CDVDFSV` under their own names — genuine PS2 behavior (a disc-provided IOPRP
  image swapping in a custom IOP kernel stack after a real IOP reset). The bug: `LoadIrx`'s
  placement allocator (`_nextIopBase`) **never reclaimed a reloaded module's old slot** — every
  same-name reload consumed a *brand new* address instead of reusing its own now-abandoned one —
  which raced the allocator past its `0x00180000` ceiling far faster than real cumulative module
  size alone would. Once past the ceiling, the allocator **blindly wrapped back to
  `DefaultLoadBase = 0x00010000`** with zero check for whether that address range was still
  occupied by a live, still-resident module. The very next load (Whiplash's real `SDRDRV.IRX`)
  landed at physical `0x00040000` — identical to where `THREADMAN` had already been placed —
  silently overwriting it mid-run. This is general and universal (not Whiplash- or SDRDRV-
  specific): any title whose cumulative real IOP module loading exceeds ~1.47MB across its
  lifetime (entirely plausible on a 2MB IOP given a mid-run IOP reset re-loads its whole kernel
  stack again) would hit the same silent corruption, just at a different pair of modules.
- **Fixed** (`IopModuleHost.LoadIrx`, `SifRpc.cs`): replaced the blind bump-then-wrap allocator
  with one that (1) reuses a same-name reload's own prior slot when the new image still fits in
  it, instead of always burning a fresh one, and (2) for any placement that isn't a same-name
  reuse, skips forward past the real footprint of every other currently-registered module instead
  of trusting the raw bump position — including right after a wraparound, so the wraparound itself
  is now safe rather than needing to be removed. (Caught and fixed a follow-on bug in the same
  change: `LoadedIrx.LoadBase` is stored EE-mapped, `0x1C000000`-based, while the allocator's
  candidate/placement values are local/physical — comparing or reusing them without normalizing
  through the existing `ToIopPhys` helper would have silently never detected real overlaps.)
- **Verified live**: re-ran the same 8,000,000-cycle Whiplash trace that previously hit the crash
  at `n=5,681,324` — the `DETPS2_TRACE_IOP_ADDR_ONESHOT=40BE4` breakpoint never fires at all now.
  `--find-writer=1C040BE4` confirms the address holds `0x27BDFFE0` ("addiu sp,sp,-0x20") — the
  real, correct, pristine THREADMAN byte — written once at `cyc=0` (initial load) and never again
  for the rest of the run. `SDRDRV` now lands at a genuinely free address (`0x00088000` in this
  run) with zero overlap against any other loaded module. Full 9-title roster at 2,000,000 cycles:
  zero exceptions, telemetry identical except each title's final IOP `pc` (expected — modules now
  legitimately execute different, no-longer-corrupted code than before).

**2026-08-03 continued — a second general infra bug in the same family (single-slot state silently
overwritten), and conclusive proof of the real remaining blocker:**
- With the placement collision fixed, checked whether real SIF RPC registration
  (`sceSifSetRpcQueue`/`sceSifRegisterRpc`) finally happens. It still never did —
  `TryFindRealRpcServer`'s `firstQueue` stayed `0x00000000` across every one of 114 real RPC calls
  in a 20,000,000-cycle trace. Traced why with `DETPS2_TRACE_IOP_CALLWATCH=6D620` (IOPFILE's real
  entry physical address): zero hits, with **and** without `--host-present` (ruling out "just a
  test-harness pacing artifact" — confirmed to reproduce under the same per-tick `RunFor` pattern
  the real Desktop app uses).
- Root cause: `IopModuleHost`'s "pending literal entry" arming state (`_pendingLiteralId` and
  friends) was a **single overwritable field, not a queue** — the same class of bug as the
  placement-allocator fix above, just in a different subsystem. `LoadIrx` recorded whichever
  module loaded *last* as "the one to arm next"; if several modules loaded in quick succession
  (confirmed live: `IOPFILE`→`SDRDRV`→`IOPSND` load back to back during Whiplash's real disc IOPRP
  handoff, all within the same tick), every entry but the final one was silently discarded before
  ever getting its real `_start` armed. Fixed (`SifRpc.cs`): converted to an actual FIFO queue
  (`_pendingLiteralQueue`); `TryArmPendingLiteralEntry` now dequeues-and-arms one entry per call
  instead of idempotently re-reading a single slot, so every queued module eventually gets its
  turn across successive `RunFor` calls instead of all but the last being lost. New
  `DETPS2_TRACE_STARTMOD=1` output: `[LITQUEUE] enqueue/dequeue+arm/removed` shows the exact
  sequence live.
- This fix *did* let `IOPFILE`'s real `_start` finally run (confirmed via `[LITQUEUE] removed
  id=100 (finished via StartLoadedModule)` — it turns out `StartLoadedModule`'s host-driven direct
  `PC` write, used by the synchronous disc-module-load path, was *also* already reaching it; the
  `CALLWATCH`-based "zero hits" check above was a false negative from watching only real jump/call
  *instructions*, which a host-side register poke doesn't go through). But real SIF RPC
  registration still never completes. Directly tested (not assumed) whether this is simply an
  instruction-budget problem: reran with `DETPS2_LOADFILE_START_INSNS=10000000` (100× the default
  100,000-instruction budget `TryStartLoadedModule` gives real disc IRX `_start` calls). Result:
  **`firstQueue` is still `0x00000000` at 10,000,000 instructions, identical to 100,000.** This
  conclusively rules out "just needs more budget" — `IOPFILE.IRX`'s real `_start` is genuinely
  blocked waiting on something this emulator's single-register-file `Iop` can never deliver, not
  merely slow. Matches, and now conclusively confirms rather than just suspects, the
  already-documented gap: real IOP `_start` routines that spawn a worker thread and cooperatively
  yield need actual per-thread register contexts and a real scheduler to resume; a single flat
  `Iop` GPR set with a bounded "run until sentinel or budget" model cannot represent that no matter
  how large the budget is. **Not fixed this pass** — modeling real per-thread IOP register
  contexts is a substantial feature (multiple GPR sets, real context-switch triggers, a real
  scheduler), scoped out of this investigation; this is the concrete next engineering target.
  Verified safe: full 9-title roster at 2,000,000 cycles, zero exceptions, telemetry unchanged
  except final IOP `pc` per title (expected, same reason as the placement fix above).

New diagnostics added and kept (zero cost when unset, same convention as existing `DETPS2_TRACE_*`
flags): `DETPS2_TRACE_BTCONF_STEP=1` (per-module IOPBTCONF boot step + IOP RAM `0x80`-`0x8F`
dump), `[IOP-EXC]` now includes `v0`/`v1`/`a0-a3`/`ra`, `[IOP-BADJUMP]` (any indirect jump to an
address `< 0x1000`), `DETPS2_TRACE_IOP_CALLWATCH=0xHEXADDR` (full call context to a chosen
address), `DETPS2_TRACE_IOP_CALLWATCH_AFTER=N` (trace N instructions after a callwatch hit),
`DETPS2_TRACE_IOP_ADDR_ONESHOT=0xHEXADDR` (full GPR/stack/approach-path dump on first PC hit,
then disarms), `DETPS2_TRACE_IOP_HEARTBEAT=1` (PC every ~1M instructions), `DETPS2_TRACE_SPU2REG=1`
(every real SPU2 register read), `[IOP-ADEL]` (real address-fault trace), `[LOADIRX-BASE]` under
`DETPS2_TRACE_STARTMOD=1` (name + candidate/final physical base + whether a prior slot was reused,
for every real `LoadIrx` call — the tool that caught the `THREADMAN`/`SDRDRV` collision above),
`[LITQUEUE]` also under `DETPS2_TRACE_STARTMOD=1` (enqueue/dequeue+arm/removed for the pending
real-`_start` queue — the tool that caught the single-slot overwrite bug), `DETPS2_LOADFILE_START_INSNS=N`
(override the real disc-IRX `_start` instruction budget, default 100,000 — used to conclusively
rule out "just needs more budget" for `IOPFILE.IRX`'s real registration stall).

| Title id | Serial | Hack | Reason | Date |
|----------|--------|------|--------|------|
| Mortal Kombat: Shaolin Monks (USA) | SLUS_210.87 | MidwayBootAssist — Wave-7 WAD/type2/C1C0/second-chrome PATH3 + **PL-011** host-pad sel-idx 0..4 continuous re-hold + CROSS accept latch (`*54E5F0/*54E5F4/*54E5F8`); SearchFile gate; no type5/sm+0x28. | **mk-mainmenu MENU YES + INTERACTIVE YES** gifP3=18 px=966k prims=9 sel-max=4 accepts≥151 | 2026-07-31 |
| Burnout 3: Takedown (USA) | `SLUS_210.50` | `Burnout3Assist` + DBC paint (`work=0x0067CCC0`); residual→STG; FRONTEND plant; Soft-GS; **MENU-B3-2** presentation leave dead-ra→`0x223228` + pad-script | STG+TXD+FRONTEND cdvd=6584 **px≈23.6M lit≈100k** **logo-frontend MENU YES**; **INTERACTIVE PARTIAL** PC left park `0x12DF84` | 2026-07-31 |
| Blood Omen 2 (USA) | `SLUS_200.24` | `BloodOmen2SnAssist` — WAVE-7 dual list-stub + ofx title FB; **PL-015** title-FB pad inject + ForceRefreshPad (opens=2); no fake warm sector credit. **PL-027/G-GFX-3 Host→Local MAINMENU/MAINSKY DISABLED 2026-08-02**: real disc bytes at `RESOUR~1/LEVELS/UI/MAINMENU.BG2` are a Crystal Dynamics "goefile" container (magic `goefile`/`symlist`) whose early bytes are ASCII scripting symbol names (`getstate`, `position`, `rotation`, `color`, …; entropy ~4.2 bits/byte), not pixels — painting them as raw PSMCT32 fabricated garbage, not real menu art. Real texture bytes (if present in BG2 raw form at all) are further in, behind an undecoded sub-section. | Host→Local residual now suppressed — honest framebuffer instead of fabricated garbage; T2 PARTIAL pad inject unaffected | 2026-08-02 |
| God of War (USA) | `SCUS_973.99` | `GodOfWarAssist` — Path2 expand + **Host→Local residual** R_SHELL/TIT1 (lit residual); PL-023 DMA END; pad-after-px; Path3MaskedByVif held | **Host→Local residual** lit=60866 residualDispfb=60866 natural=0 expandHits=2 — **not natural MENU YES** | 2026-07-31 |
| Haven: Call of the King (USA) | `SLUS_205.17` | `TeamIcoAssist` — PreferIopRp + SoftFloatBridge + VIF/JREXIT + MENU-HAVEN-3 Host→Local SYSTEM.RW3/CUBE + **MENU-HAVEN-4 poison-`$ra`** | Soft-GS lit=43132 @100M Host→Local residual; **fleet 50M CRT0 px=0 expected** | 2026-07-31 |
| Shadow of the Colossus (USA) | `SCUS_974.72` | `TeamIcoAssist` — PreferIopRp + MENU-SOTC-2 Host→Local MANAGER/NICO/KERNEL | Soft-GS lit=120153 Host→Local residual — **not natural MENU YES** | 2026-07-31 |
| Whiplash (USA) | `SLUS_206.84` | `WhiplashAssist` — MENU-WHIP-2 Host→Local GOE firstscreen + ofx expand **DISABLED 2026-08-02** (fabricated non-image RKV script/param bytes as PSMCT32 pixels; RKV confirmed genuinely audio-only via full TOC dump, 356+ `vo/*`/`streams/wav/*` entries dominate the 1.29 GiB). Real per-level graphics geometry lives in `WHIPLASH/MAP/*.MP2` (`goefile`→`MAP0`→`MPGM` chunks, VU1-microcode-packed vertex blobs — not yet decoded) and materials reference textures **by name** (`MPIM` chunks), not embedded pixels; the shared texture resource pool itself is still unlocated. **RealSifRpc GOE stream-table relay rewritten 2026-08-02**: the old bridge bulk-preloaded a fixed Code/firstscreen/frontend order into an invented `0x01C00000` scratch address after an arbitrary poll-count wait; traced the real request packet instead — its `w2` field is a client poll cursor, not a stream selector (counts 0x2..0xFF as one run), and it carries the EE's own real ring-buffer pointer at `+0x1C`. Streams now open lazily by real TOC name the instant the game asks, and bytes deliver only into the real per-request pointer, rotating fair-share (no fixed order, no guessed address). `MaybeFillTitleRing`'s address-guessing scanner removed as redundant. | Verified live: `Code` (574,216B), `firstscreen` (184,708B, **100% delivered**), `frontend` (1,240,220B) stream real bytes progressively; EE PC visibly advances into new code once firstscreen completes (was static before). Still no natural visible render by 400M cycles — game stops issuing stream-table polls after ~1 MB total (Code 75%, frontend 35%) while thread 2 (an unrelated SN-runtime scheduler-helper) spins forever on a fabricated `WaitSema(3)` signal; likely starves the real producer. Next: investigate the sema=3 fabricate loop (`WHIP_SEMA_FIX_V3` in `SonyKernelHle.cs`). **Separate, general infra fix landed the same day** (`RealSifRpc.LoadModuleByPath`, applies to *every* title, not just Whiplash): the real disc `IOPFILE.IRX` — Crystal Dynamics' actual compiled GOE_FSRV driver — was silently never loading. An earlier probe (e.g. an empty-path MOD_LOAD) pre-registered the module *name* with no image, and the real, later MOD_LOAD request with the true disc path was short-circuited by an "already registered" fast path that never checked whether the existing registration actually had a loaded image, so the real bytes were never read and `_start` never ran. Fixed to only take that fast path when the existing registration already has a real image or is deliberately HLE-owned (`PADMAN`/`MCMAN`/etc.); otherwise it falls through to the real disc load. Verified live: `IOPFILE.IRX`'s real `_start` now genuinely executes on the IOP R3000 interpreter (100,000+ real instructions, previously zero) for both Whiplash and Blood Omen 2, and the same fix also unblocked `SDRDRV`/`IOPSND`/`IOPMEM`/`IOPSNDS`. Its `_start` doesn't return within a 2M-instruction budget (likely spawns a worker thread and cooperatively yields rather than returning) — real cross-scheduler interleaving of a mid-`_start` IOP module is the next open question, not yet solved. | 2026-08-02 |
| Mortal Kombat: Deception (USA) | `SLUS_208.81` | `MidwayFamilyAssist` **DEC** — PL-012 INTERACTIVE + **PL-029** gameart Host->Local Soft-GS (imgBytes art-scale); Path3MaskedByVif held | **MENU+INTERACTIVE** + imgBytes=557056 @100M; residual EE BITBLT natural | 2026-07-31 |
| Mortal Kombat: Deadly Alliance (USA) | `SLUS_204.23` | `MidwayFamilyAssist` **DA** — WAVE-6 fail-tails + **PL-013** pad sel-idx + **PL-030** menu-band display drain + belt fail-tail demote (core 6) | **MENU YES** + **T2 YES** + FRONTEND PARTIAL gifCompleted=2980 px≈47.7M imgBytes=98304 @100M SEMA_OFF | 2026-07-31 |

Format: short description + link to issue/commit when available.
