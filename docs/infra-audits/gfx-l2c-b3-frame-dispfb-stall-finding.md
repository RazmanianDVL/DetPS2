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
