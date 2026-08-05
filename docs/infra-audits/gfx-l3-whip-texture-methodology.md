# GFX L3 — Whiplash real texture path methodology (investigation only)

**Status:** investigation only — **no Core this seat**
**Plan:** `gfx-plan-v0.md`
**Title:** Whiplash (SLUS_206.84)
**Scope:** why real EE code never emits genuine GIF IMAGE texture data; what would confirm the actual blocker

---

## 0. One-line

Prior investigation (2026-08-02, `docs/TITLE_HACKS.md`) already **located** the real texture/geometry format
and already **fixed** the GOE stream-delivery mechanism to be genuinely real (not an assist). The remaining
blocker is narrower than "find the missing mechanism from scratch": a specific fabricated semaphore signal
(`WHIP_SEMA_FIX_V3`) produces a confirmed 627-iteration livelock on one low-priority thread over a 50M-cycle
trace, and whether this livelock is starving the real stream-producer thread is the open, testable question.

---

## 1. What the two disabled assists actually did (mechanical)

Both are dead code as of 2026-08-02 — calls commented out in `WhiplashAssist.Step` (lines 344–371 at time of
writing), left in place only because the BITBLT plumbing is reusable once real texture bytes are located.

| Assist | What it did | Why disabled |
|--------|-------------|---------------|
| **PL-033** `MaybeFillTitleRing` | Copied bytes from a fixed-order GOE dump at `0x01C00000 + streamIdx*640KiB` into a ring buffer at EE `0x45BC94`, guessing the ring pointer by scanning candidate addresses | Superseded by a real fix (see §2) — RealSifRpc now delivers into the game's own real per-request ring pointer, not a guess |
| **MENU-WHIP-2** `TryFeedTitleChromeHostToLocal` | BITBLT Host→Local painted PS2.RKV `firstscreen`/`frontend`/`Code` bytes directly as PSMCT32 pixels | Ghidra RE of the real ELF + byte inspection proved these are NOT pixels — they are Crystal Dynamics `goefile` containers (magic `goefile`/`symlist`) holding ASCII scripting symbol tables and int16/float32 parameter records (entropy ~4.2–4.6 bits/byte). Painting them as pixels produced exactly the reported "random lines and colors and shapes" — fabricated noise, not a real image. Banned per `CORRECTNESS.md` ("no pretty lies"). |

`TitleRingBase` (`0x45BC94`) is a real, game-owned pointer — not an invented address — confirmed live: the
comment says "live stream-table setup packet +0x1C" and the 2026-08-02 fix traced the actual request packet's
`w2`/`+0x1C` fields to find it, rather than guessing.

---

## 2. Where the real graphics data actually lives (already reverse-engineered)

Per `docs/TITLE_HACKS.md` (2026-08-02 entry, Whiplash row):

- **PS2.RKV** is confirmed **audio-only** via full TOC dump — 356+ `vo/*` / `streams/wav/*` entries dominate
  the 1.29 GiB file. It was never going to contain menu/title graphics.
- Real per-level graphics geometry lives in **`WHIPLASH/MAP/*.MP2`**: `goefile` → `MAP0` → `MPGM` chunks,
  described as VU1-microcode-packed vertex blobs. **Not yet decoded.**
- Materials reference textures **by name** via `MPIM` chunks, not embedded pixels. The **shared texture
  resource pool itself is still unlocated** — i.e. even if MP2's geometry chunks were decoded, the actual
  texture pixel data lives somewhere else entirely and hasn't been found.

**This means the L3 gap is not (only) a code-execution or HLE problem — it is partly a genuinely undecoded
proprietary file format problem.** Real per-level textures cannot be emitted by any code path, real or
assisted, until MP2's chunk format (and wherever the shared texture pool actually lives) is understood. This
is qualitatively different from tonight's C1 IOP work (find a missing *mechanism*) — this is closer to format
reverse-engineering.

**However**, the title-surface / menu chrome (the thing MENU-WHIP-2 was trying to fake) is a *separate,
smaller* target: firstscreen/frontend/Code are `goefile` script/param containers, and the real texture bytes
for menu chrome — if GIF IMAGE is ever emitted for it at all — would come from wherever the game's own script
interpreter directs it, which requires the interpreter to actually run far enough. That's where §3 is relevant.

---

## 3. The real (non-assist) GOE stream-delivery fix, already landed

Separately from the two disabled assists, `RealSifRpc`'s GOE stream-table relay was **rewritten** 2026-08-02
as genuine infra (applies generally, not a per-title plant): the real request packet's `w2` field is a client
poll cursor (not a stream selector as previously assumed), and it carries the EE's own real ring-buffer
pointer at `+0x1C`. Streams now open lazily by real TOC name the instant the game asks, and bytes deliver only
into the real per-request pointer. `MaybeFillTitleRing`'s address-guessing scanner was removed as redundant.

**Verified live (2026-08-02, prior investigation):** `Code` (574,216B), `firstscreen` (184,708B, **100%
delivered**), `frontend` (1,240,220B) stream real bytes progressively; EE PC visibly advances into new code
once firstscreen completes (was static before this fix). This is real, confirmed forward progress — not an
assist artifact.

**But:** by 400M cycles, no natural visible render. The game **stops issuing stream-table polls** after ~1MB
total delivered (Code 75%, frontend 35%) — the real producer thread appears to give up mid-stream. At the same
time, **thread 2** (described as "an unrelated SN-runtime scheduler-helper") spins forever on a **fabricated**
`WaitSema(3)` signal. Prior investigation's own conclusion: "likely starves the real producer" — flagged but
not confirmed. Next lead named explicitly: `WHIP_SEMA_FIX_V3` in `SonyKernelHle.cs`.

---

## 4. WHIP_SEMA_FIX_V3 — confirmed livelock signature (new this seat)

Read `SonyKernelHle.cs:1191-1220` (WaitSema HLE dispatch). When a WaitSema blocks with no real queued RPC that
will signal it, the generic path yields to another runnable thread first and only fabricates a signal if truly
alone. But there's a **Whiplash-specific carve-out**: `else if (_system.ActiveQuirk is WhiplashAssist)` skips
the yield-first check entirely and unconditionally does `SignalSema(a0); WakeupThread(CurrentThreadId);
RequestImmediatePreempt();` — i.e. it always fabricates a signal for *whichever thread just blocked*, wakes
that same thread immediately, and forces the EE scheduler to re-decide who runs next right now
(`RequestImmediatePreempt` just sets `_cyclesSinceLastPreempt = _preemptQuantum`, per `KernelHle.cs:1246`).
The code comment for V2→V3 already documents this fabricate mechanism previously caused a measured starvation
regression once (~333k WaitSema/Wakeup pairs, `px 3→0`) that V3's `RequestImmediatePreempt` add was meant to
fix — i.e. this exact class of bug (fabricated signal → starvation) has bitten this title before.

**Fresh trace this seat** (`DETPS2_TRACE_RPC=1`, `blocker-trace`, `user-media-whiplash.json`, 50M cycles,
`--host-present`):

```
WHIP_SEMA_FIX_V3 fabricate count: 627 (over 50M cycles)
```

Every single one of the 627 fabricate events shows the **identical** state:

```
[RPC] WaitSema BLOCKED a0(sema)=0x3 tid=2 pc=0x00365464 ra=0x00365EE8 sp=0x00469770 gp=0x00429F70
[RPC]   thread id=1 alive=True started=True sleeping=False waitVblank=False suspend=0 waitSemaId=0 priority=1
[RPC]   thread id=2 alive=True started=True sleeping=True waitVblank=False suspend=0 waitSemaId=3 priority=64
```

**Confirmed facts:**
- It is *always* thread 2, *always* at the *same PC* (`0x00365464`), blocking on the *same* semaphore
  (`id=3`) — a textbook livelock: fabricate-wake, immediately re-block at the identical instruction, repeat.
  627 times in 50M cycles with zero apparent progress past this PC for thread 2.
- Thread 1 (main, priority=1 — numerically higher priority than thread 2's priority=64 under standard PS2/IOP
  convention) is shown `started=True sleeping=False` in every sample — nominally runnable, not blocked on
  anything. Whether it is actually *receiving* CPU time between each of the 627 fabricate events, or is being
  crowded out by `RequestImmediatePreempt()` repeatedly favoring thread 2's immediate re-wake, is **not yet
  established by this trace** — the RPC log only captures WaitSema dispatch events, not general scheduler
  occupancy.

**Confirmed vs. speculative:**
- **Confirmed:** the livelock exists, is title-scoped (only fires for `WhiplashAssist`), is real (not a
  trace artifact — same PC/tid/sema every time), and the fabricate mechanism itself is a hand-synthesized
  signal for a semaphore whose real signal source was never implemented (this is the class of thing the
  project's own doctrine says to replace with the real mechanism, not tune further).
- **Speculative:** that this livelock is the reason the *real* stream-producer thread stops polling after
  ~1MB. Plausible and consistent with prior investigation's own hypothesis, but not yet directly measured.
  Also unconfirmed: what thread 2 (`pc=0x00365464`) is actually doing — is it the "SN-runtime scheduler
  helper" named in the 2026-08-02 note, and does semaphore 3 have a *real* owner anywhere in the game's own
  code that should legitimately signal it (meaning the fabricate is standing in for a missing real trigger),
  or is semaphore 3 something the emulator itself should never need to synthesize at all for this thread.

---

## 5. Recommended next measurement (not Core)

1. **Scheduler occupancy trace**: instrument (temporarily, or via existing `DETPS2_TRACE_*` if something already
   exists) how many EE instructions thread 1 vs thread 2 actually execute in the same 50M-cycle window, to
   directly confirm or refute starvation rather than inferring it from WaitSema dispatch events alone.
2. **Identify semaphore 3's intended real signaler**: decompile/inspect around `pc=0x00365464` (thread 2) to
   determine what real event should release this wait — is it plausibly the same GOE stream-table completion
   that `RealSifRpc`'s real relay (§3) already drives correctly for the *producer* thread, meaning the fix
   might be as small as making that real completion also signal sema 3 for thread 2 (a real mechanism), rather
   than continuing to fabricate?
3. **Re-run the §3 stream-delivery trace (Code/firstscreen/frontend byte counts) with and without
   WHIP_SEMA_FIX_V3 disabled** (temporarily, measurement only) to see whether the real producer's stall point
   (~1MB, Code 75%/frontend 35%) shifts — a direct causal test of the starvation hypothesis.
4. Only **after** the above narrows H1 (livelock genuinely starves producer) vs. an alternative explanation
   (producer stalls for an unrelated reason and the sema-3 livelock is a red herring) should any Core design
   be proposed — per GFX-PLAN-v0's dual-ACK-before-Core discipline.

**Not proposing a fix this seat.** MP2 texture decode (§2) remains a separate, larger, format-reverse-
engineering problem out of scope for a quick mechanism fix — even if the WHIP_SEMA_FIX_V3 starvation is
resolved and the real GOE stream producer completes fully, title-surface graphics may still not render if the
menu chrome path also depends on MP2/shared-texture-pool decode. The sema-3 investigation is the tractable
near-term lever; MP2 decode is the larger, separate effort.

---

## 6. Non-goals (carried from plan + title doctrine)

- Do **not** re-enable MENU-WHIP-2 / PL-033 Host→Local paint.
- Do **not** invent Path2 IMAGE or fabricate additional semaphore signals as a "fix."
- Do **not** attempt MP2 format decode in this seat — that's a distinct, larger effort.

```text
GFX L3 Whiplash methodology
  MP2 texture format = separate undecoded-format problem (out of scope this seat)
  WHIP_SEMA_FIX_V3 = confirmed 627x livelock, tid=2, same PC, same sema — starvation of real
    producer thread is the live hypothesis, not yet directly measured
  next: scheduler occupancy trace + semaphore-3 real-owner identification, no Core yet
```
