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
