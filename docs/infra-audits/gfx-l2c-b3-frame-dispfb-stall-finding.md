# GFX L2c — B3 finding: FRAME/DISPFB registers stop updating entirely after early setup

**Status:** finding, code-verified via real trace — no Core, needs dual-ACK on next step
**Title:** Burnout 3 (SLUS_210.50)
**Parent:** `gfx-l2-frame-page-composite-design.md` (open problem, §2)

---

## 0. One-line

Present is black because the display circuit (DISPFB2) permanently points at a physical
page (0) that the real game **never writes to** — real draws land at a different page
(0x46) that DISPFB never points at. This is the honest, correct result given what the real
EE code does in our trace. The open question is **why the real game's own flip/blit never
fires** across a full 50M-cycle window, not whether our compositor is behaving correctly.

## 1. Method

Temp trace (gated `DETPS2_TEMP_B3_BLIT_TRACE=1`, three call sites: FRAME_1 register write,
DISPFB1/DISPFB2 register writes, local-to-local BITBLT dispatch) added directly to
`Gs.cs`, run against the real ISO via `scoreboard-metrics burnout-only.json`, fully
reverted after (`git diff --stat` empty, confirmed).

Two runs: `--cycles=20000000` and `--cycles=50000000` (the design doc's established
diagnose/verify budgets for this title), both `--host-present`.

## 2. Result

Both runs produced the **byte-identical** 19-line trace — nothing further happens between
20M and 50M cycles:

```
FRAME_1 write fbp=0x0  psm=0x0 raw=0x100000      <- initial, very early
DISPFB2 write fbp=0x0  psm=0xA raw=0x51400        (PSMCT16S)
DISPFB2 write fbp=0x0  psm=0xA raw=0x51400
FRAME_1 write fbp=0x46 psm=0x0 raw=0xA0046        <- switches to real draw target
FRAME_1 write fbp=0x46 psm=0x0 raw=0xA0046
DISPFB2 write fbp=0x0  psm=0xA raw=0x51400
DISPFB2 write fbp=0x0  psm=0xA raw=0x51400
DISPFB1 write fbp=0x0  psm=0x0 raw=0x0            (PSMCT32)
DISPFB1 write fbp=0x0  psm=0x0 raw=0x0
FRAME_1 write fbp=0x46 psm=0x0 raw=0xA0046
... (repeats 3 more times, same values)
```

**Zero** local-to-local BITBLT events in the entire 50M-cycle window (the `L2L blit` trace
line never fires once).

Meanwhile the scoreboard for the 50M run shows the game is genuinely alive and drawing:
`px=877187`, `fragTest=2825979`, `rejDepth=1948792`, `gifPath2=12 gifPath3=20`,
`syscalls=62716`, `exitRequested:false` — real, ongoing GS fragment activity, not a hang.
But `dispfbPx=0 naturalDispfbPx=0 residualDispfbPx=0 compositeSource:"None"
presentLit=0` — our own natural-DISPFB composite path correctly finds nothing at page 0,
because nothing is there.

## 3. What this establishes

- **The compositor's black present is honest, not a bug.** `IsPageMismatched` and the
  natural-DISPFB path are doing exactly what they should: DISPFB2 says "scan page 0 as
  PSMCT16S," and page 0 has never been written by anything (no FRAME target ever points
  there again after the first two writes, no BITBLT ever touches it) in this window.
- **Real draws are happening continuously**, just at page `0x46` (PSMCT32), which DISPFB
  never selects.
- **No flip and no format-converting blit ever occurs** between 20M and 50M cycles — the
  real EE code that would normally re-point DISPFB at the finished back buffer (or blit/
  downconvert 0x46 → 0) either hasn't reached that point yet, or depends on something that
  never completes in our emulation.

## 4. What this does NOT establish (open, not yet investigated)

- **Whether more cycles would eventually trigger a real flip.** 50M is this title's
  established "verify" budget in the design docs, not a proof of terminal stall — B3 could
  legitimately still be in an attract-mode/loading loop this deep in a real console too.
  Comparing against a longer budget (100M "claim") would help separate "just needs more
  time" from "actually stuck."
- **What condition the real game is gating the flip on.** `syscalls=62716` over 50M cycles
  with essentially flat register state suggests the EE is looping in *something* real, but
  we haven't identified what — a real HLE gap (an interrupt, semaphore, or sceGs callback
  the game is waiting on that never resolves) is plausible given tonight's other findings
  (Whip's `WHIP_SEMA_FIX_V3` starvation was exactly this class of bug), but this trace alone
  doesn't point at a specific mechanism the way the Whip investigation's WaitSema evidence
  did. Needs its own targeted trace (which syscall/wait is the EE actually blocked in,
  repeatedly, during this window) before proposing anything.
- **Whether page 0x46 even contains a complete, correct frame** — px/fragTest counts show
  drawing is happening, but haven't visually verified what's actually at 0x46 (would need a
  raw dump of that page, not the present path, since DISPFB never selects it).

## 5. Recommended next step (not started, no Core)

1. **100M-cycle comparison run** (same trace) — cheapest next data point: does DISPFB ever
   move, even once, given more time?
2. If still static at 100M: **identify what the EE is actually doing** in its per-quantum
   loop during this window — a syscall/PC-histogram trace (same class of tool used for the
   Whip WaitSema investigation) rather than register-write trace, to find whether it's
   parked on a real wait condition or genuinely spinning in draw code with no exit gate we
   can see yet.
3. Only after narrowing "waiting on X" vs "just needs more real frames" should any Core
   design be proposed, per this project's dual-ACK-before-Core discipline.

## 6. Non-goals

- Do **not** treat page-0x46 content as presentable via any `allowPageMismatch`-style
  bypass — that's the exact fabrication pattern already rejected once this session
  (`3bcedb2`) for producing stripe noise on this same title.
- Do **not** invent a synthetic DISPFB flip or fabricate a blit as a "fix" — same class of
  thing banned for Whip's semaphore and Dec's CLUT investigations.

```text
B3 L2c finding
  FRAME_1 -> 0x46 (real draws, ongoing, px growing) never selected by DISPFB
  DISPFB2 -> page 0 (PSMCT16S) never written by anything -- honest black is correct
  zero register/blit activity for the entire 20M-50M window (byte-identical trace)
  open: does it ever flip past 50M? what is the EE actually waiting on?
  next: 100M comparison, then PC/syscall trace if still static -- no Core yet
```

---

## 7. 100M comparison (Grok, 2026-08-04) — still static

**Method:** same temp gate `DETPS2_TEMP_B3_BLIT_TRACE=1` (FRAME_1 GIF write, DISPFB1/2 privileged, L2L blit entry) on tip after C1; `scoreboard-metrics burnout-only.json --cycles=100000000 --host-present`; **fully reverted** (`git status` clean on `Gs.cs`).

**Trace (20 lines total, 4 unique patterns):**

```
FRAME_1 write fbp=0x0  psm=0x0 raw=0x100000     x1
FRAME_1 write fbp=0x46 psm=0x0 raw=0xA0046       x4
DISPFB2 write fbp=0x0  psm=0xA raw=0x51400       x8
DISPFB1 write fbp=0x0  psm=0x0 raw=0x0           x6
```

**Zero** `L2L blit` lines.

**Scoreboard 100M vs Claude 50M:**

| Metric | 50M (Claude) | 100M (Grok) |
|--------|--------------|-------------|
| px | 877187 | **877187** (flat) |
| fragTest | ~2.8M | 2825979 |
| gifP2/P3 | 12 / 20 | 12 / 20 |
| presentLit | 0 | 0 |
| frame1 | 0xA0046 | 0xA0046 |
| dispfb2 | 0x51400 | 0x51400 |
| exitRequested | false | false |
| syscalls | 62716 | 65639 (+~3k) |

### Verdict

**Not "just needs more time" through 100M.** DISPFB never moves; FRAME stays at 0x46; no local→local blit; **px stops growing after the early window** (identical 877187 at 50M and 100M). EE still alive (syscalls continue, no exit) but draw progress is plateaued.

Candidate (1) closed: escalate to candidate (2) **PC/syscall histogram** for the stall window — no Core.

Artifacts (gitignored): `out/canaries/b3-l2c-100m/`.

---

## 8. PC / syscall histogram (Grok, 50M, candidate 2)

**Method:** product `DETPS2_PROFILE_PC=1` + `blocker-trace burnout-only.json --cycles=50000000 --host-present`  
No Core, no temp instrumentation.

### 8.1 Top syscalls

| # | Name (HLE) | Count |
|---|------------|------:|
| **0x32** | **SleepThread** | **45437** |
| **0x64** | **FlushCache** | **14701** |
| 0x2F | (see kernel map) | 1141 |
| 0x34 | | 503 |
| 0x44 | WaitSema | 301 |
| 0x42 | SignalSema | 217 |

**Not** a WaitSema livelock (genericStarvedSemaRescues=0; WaitSema only 301). Dominant cost is **SleepThread thrash** + FlushCache.

### 8.2 PcProfiler top (39M samples, 50k unique)

| Band | Role (disasm) |
|------|----------------|
| **0x00100158–0x00100170** (~1.7M each) | **memset / bulk zero**: `sq zero,0(v0); addiu +16; j loop` until end |
| **0x0012DF90–0x0012DFB0** (~314k each) | IEEE754-style **double unpack** helpers (`ld`, `dsrl32`, branch on exp) |
| **0x0012DFC8+** | same helper family |
| Final PC **0x0012DF54** | same float helper prolog (`lui/ori/dsll` mask build) |

So most EE time is **memory clear + float decode**, not a single tight spin on a wait flag address — but **SleepThread×45k** means cooperative yield is the control-flow backbone of the plateau.

### 8.3 GIF / PATH3 (same run)

```
m3p=True heldP3n=5 heldP3qwc=2124 heldSubmits=18 mskPath3=10
gifCompleted=92 gifAborted=6 lastAbort=DIRECT-end-truncated
gif-last: nloop=26624 nreg=3 (large pending tag residual)
```

**PATH3 is held / M3P asserted** with multi-QW backlog while present stays black. This is a stronger “why no flip” lead than DISPFB alone: the display path may be waiting for PATH3 drain / unmask that never completes under our GIF model.

### 8.4 Next (still no Core)

1. Dual-ACK whether to prioritize **PATH3 hold / M3P / DIRECT-end-truncated** investigation vs SleepThread producer (what wakes the sleeper).  
2. Optional: dump page 0x46 contents for visual “is the backbuffer real chrome?” (read-only dump, not present bypass).  
3. Still ban: page-mismatch present of 0x46, synthetic DISPFB flip.

```text
B3 L2c candidate 2
  SleepThread 45k + FlushCache 14k dominate (not WaitSema)
  PC heat: memset 0x100158 + float helpers 0x12DFxx
  PATH3 held (m3p, heldP3qwc=2124) + DIRECT-end-truncated aborts
  flip still absent at 100M; next: PATH3 hold mechanism, not more cycle budget
```

---

## 9. DIRECT-end-truncated string-mismatch bug — real, but causally REFUTED for B3 (Claude)

Independently landed on the same `heldP3`/`DIRECT-end-truncated` lead as §8 before seeing
Grok's result. Traced it one step further: found and causally tested a concrete bug in
`Gif.cs`'s `AbortIncompletePacket`.

**The bug (still present in tree, not landed — see below):** `Gif.cs:374` and `:387` compare
the abort reason string against `"DIRECT-end-truncate"` (no trailing `d`), but the only
caller, `Vif.cs:333`, always passes `"DIRECT-end-truncated"` (with the `d`). The comparison
**never matches**. Concretely this means:

- The Path3-sticky protection at `Gif.cs:373-374` (comment: *"Do not clear Path3-owned
  sticky"*) never engages for the real string, so a held Path3 packet is NOT protected from
  this Path2-boundary abort the way the code's own comment says it should be.
- The telemetry miscategorizes: `abortOther` incremented instead of `abortDirectTruncate`
  (matches what both traces show: `abortTrunc=0 abortOther=6` before the fix).

**Causal test:** fixed both string literals to `"DIRECT-end-truncated"` (temp, in-tree only
for the test), rebuilt, reran `blocker-trace burnout-only.json --cycles=50000000
--host-present`. Result: **byte-identical** `px`, `FRAME_1`, `DISPFB2`, thread states, and
full syscall histogram to the unfixed run — the only change was telemetry classification
(`abortTrunc=6 abortOther=0` instead of the reverse). Reverted (`git diff --stat` empty,
confirmed).

**Conclusion: this typo is a real, independent bug worth fixing on its own merits (the
code's stated intent silently never fires), but it does NOT explain B3's plateau.** The 6
`DIRECT-end-truncated` events in this run are not `_pktPath==3`-owned at the moment they
fire, so the broken protection branch was never going to touch them either way — refuting
"the abort tears down the packet that would have flipped DISPFB" as the mechanism.

Answers Grok's §8.4 open question 1: **not** the PATH3-hold/DIRECT-end-truncated angle as
the SleepThread producer — that's now a dead end for B3 specifically. Worth landing as a
small separate correctness fix (dual-ACK, unrelated to B3), but the real "what wakes the
45k-deep SleepThread spin" question is still open and needs a different angle — likely: what
condition is thread 1 (or whichever thread executes `0x12DF54`'s float-helper family) actually
polling for between each `SleepThread` call, i.e. what does the code *between* consecutive
SleepThreads read/check.

```text
DIRECT-end-truncated bug
  Gif.cs compares "...truncate", Vif.cs passes "...truncated" -- never matches, real bug
  causally tested: fixing it changes ZERO B3 behavior (only telemetry bucket)
  PATH3-hold-abort is refuted as the SleepThread-plateau's cause
  open: what condition is polled between each of the 45k SleepThread calls
```

---

## 10. SleepThread call-site correlation (Grok) — **VBlank flag poll**

**Method:** temp `DETPS2_TEMP_B3_SLEEP_RA=1` RA histogram on syscall 0x32 only; fully reverted after measure.

### 10.1 RA histogram (50M, n=45340)

| RA | Count | Share |
|----|------:|------:|
| **0x00237188** | **45246** | **99.8%** |
| 0x0022E248 | 92 | 0.2% |
| 0x002A214C | 1 | — |
| 0x002B2590 | 1 | — |

Syscall PC always **0x0010BD44** (SleepThread trampoline). Threads **3/4/5/6** dominate early; tid 1 rarely.

### 10.2 What 0x237188 is doing (disasm)

Waiter function starts **0x00237120**:

1. Scan table **`0x01D80700`** for a free slot (word == `-1`); up to 4 slots.
2. `s0 = (gp - 23820) + slot` — flag byte array (same offset cited in `SonyKernelHle` AddIntcHandler comment).
3. Loop:
   - `jal 0x0010BD40` → SleepThread  
   - **RA = 0x00237188**: `lbu v1, 0(s0)`  
   - `beq v1, zero, sleep_again` — **spin until flag ≠ 0**

Producer side is the INTC handler at **0x002370A0** (the VBlankStart chain entry already named in HLE comments):

1. Scan same table `0x01D80700` for slots ≠ `-1`.
2. `jal 0x0010CCD0` (WakeupThread path) with that tid.
3. **`sb 1, (gp-23820)+slot`** — sets the flag the waiter polls.
4. EI / return.

Live dump at 25M: table holds **`3,4,5,6`** (registered waiter tids) — **not** cleared to `-1`. So waiters **are** registered; flags never go non-zero → 45k SleepThread.

### 10.3 Link to prior knowledge

`SonyKernelHle.cs` AddIntcHandler already documents this exact wedge:

> Burnout 3 registers three VBlankStart handlers; keeping only the last left the VBlank thread-wakeup at **0x2370A0** dead and wedged boot on a SleepThread flag poll at **0x23719x** (flags @ **gp-23820** never set).

Multi-handler chain is **already append** (not last-wins). Plateau persists ⇒ either **0x2370A0 is not running**, or it runs but **never reaches `sb flag=1`** (e.g. table scan always sees -1 under ISR GP/context, or Wakeup path fails before store).

### 10.4 Next (still no Core without dual-ACK)

1. **Measure:** does cause=2 (VBlankStart) ever dispatch **0x2370A0** during the plateau? (handler-entry temp counter / DETPS2_TRACE_HANDLERS-class).  
2. If yes: dump flag bytes + table under ISR GP when handler runs.  
3. If no: INTC STAT/mask / TakeExceptions / multi-handler walk bug for cause=2.  
4. Still ban: invent DISPFB flip; present page 0x46 mismatch.

---

## 11. CD/RPC-async flatness (Claude) — thread 1's RPC chain completes normally, not the blocker

**Method:** `DETPS2_TRACE_RPC=1` + `blocker-trace`/`scoreboard-metrics burnout-only.json`, real
product trace flag, no temp instrumentation. Cross-checked a timeline of intermediate cycle
budgets (8M/10M/15M/20M/30M/35M/40M/50M) to find exactly when forward progress stops.

### 11.1 Growth timeline

| cycles | px | cdvdSectors | binds | calls | syscalls |
|-------:|---:|------------:|------:|------:|---------:|
| 8-10M | 0 | 0 | 0 | 0 | 0 |
| 15M | 286720 | 0 | 5 | 6 | 424 |
| 20M | 877187 (final) | 425 | 11 | 59 | 42461 |
| 30M | 877187 | 609 (final) | 12 (final) | 62 (final) | 61548 |
| 40M-100M | 877187 | 609 | 12 | 62 | slowly climbing (spin only) |

**Everything** (`px`, `cdvdSectors`, `binds`, `calls`) reaches its exact terminal value by
~30M and then stays byte-identical through Grok's 100M run — only the syscall counter keeps
moving (pure SleepThread/FlushCache spin, matches §10).

### 11.2 What thread 1 was doing right at the freeze point

`DETPS2_TRACE_RPC=1` trace is byte-identical from a 35M run through a 50M run for the first
937 lines, then exactly **one more line** appears at 50M:

```
[RPC] HandleCall sid=CD_SCMD fno=0x1 recvBuf=0x00486A40 eePC=0x00000000
```

`fno=0x1` = `ScmdReadClock` (`RealSifRpc.cs:8676`) — a simple, synchronous handler
(`WriteCdClock`) with `CompleteRpcEnd` called in the same statement block right after. **This
call is not itself stuck** — it's an ordinary bookkeeping read that completes immediately,
same as the ~60 RPC calls before it in the chain (GTFS, SYSMEM, etc., all seen completing
normally in the trace leading up to this one).

### 11.3 Conclusion

**Refutes** "thread 1 is blocked on an unanswered CD/RPC reply" as the blocker — the RPC
chain runs cleanly through its last call (`ReadClock`) and then produces **zero further RPC
trace output** for the remaining ~15-65M cycles, not because a reply never arrives, but
because thread 1 apparently has nothing further to *ask* — consistent with §10's picture:
after this point the game's real control flow moves into worker threads 3-6 polling a
VBlank-set flag that never gets set, i.e. thread 1 finishes its init/RPC chain normally and
hands off to a steady-state loop gated on the same wedge Grok found. Not a second,
independent blocker — corroborates §10 rather than competing with it.

**No Core.** Agree with §10.4's proposed next measure (does cause=2 ever dispatch
`0x2370A0`) — that's the decisive test now, my angle came back clean.

```text
CD/RPC flatness (Claude)
  px/cdvdSectors/binds/calls all reach final value by ~30M, frozen through 100M
  last RPC event: CD_SCMD ReadClock (fno=1) -- completes normally, not stuck
  refutes "unanswered RPC reply" -- corroborates Grok's VBlank-ISR wedge (10.4) instead
```

```text
B3 SleepThread correlation
  99.8% RA=0x237188 — flag poll (gp-23820)+slot after SleepThread
  producer INTC 0x2370A0 should sb flag=1 + WakeupThread
  table 0x1D80700 has tids 3/4/5/6 live; flags never set
  next: prove whether 0x2370A0 runs on VBlank during plateau
```
