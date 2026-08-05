# Design — B3 STAGEHED iovec escape: plant a full multi-entry chain instead of one 64KiB slice

**Status:** design only, dual-ACK pending (no Core landed)
**Date:** 2026-08-05
**Discovered via:** Burnout 3 (SLUS_210.50) B3 L2c investigation chain (`gfx-l2c-b3-path3-m3p-hold.md` §49, §49.1)
**Scope:** `src/DetPS2.Core/GameQuirks/Burnout3Assist.cs`'s `MaybeEscapeEmptyIoQueue` — B3-only (serial-gated quirk module), not shared Core infrastructure

---

## 0. One-line

B3's own empty-iovec-queue escape assist already reads the real `DATA/STAGEHED.BIN` (374,784
real bytes off the mounted ISO) into RDRAM, but only ever exposes the **first 64KiB** to the
game's own consume loop via a single iovec entry immediately followed by a `{0,0}`
terminator. The real consumer wants to walk a multi-entry list until its read budget is
satisfied. Confirmed live: this single-shot plant produces exactly one escape event, jumps
`cdvdSectors` 609→1865, then goes permanently silent — B3 sits one bucket (`stagePlantOnly`,
600-2000) short of the next threshold (`postTxd`, ≥2000) for the rest of any run length tested
(30M and 50M cycles, flat in both).

---

## 1. The problem, precisely

`MaybeEscapeEmptyIoQueue` (`Burnout3Assist.cs:1933+`), on detecting the game's real iovec-walk
loop stuck on an empty/absurd entry, plants a single real entry pointing at the already-loaded
`STAGEHED.BIN` data:

```csharp
uint plantSize = Math.Min(_stageHedSize, 0x10000u); // first 64KiB slice
sys.Memory.Write32(s4 + 0, _stageHedEeAddr);
sys.Memory.Write32(s4 + 4, plantSize);
// Terminator after one entry.
sys.Memory.Write32(s4 + 8, 0);
sys.Memory.Write32(s4 + 12, 0);
```

The `0x10000` (64KiB) cap is intentional and load-bearing — the same function has a separate
`hugeCopy` guard (`pc in memcpy tail && a2 > 0x10000` → forces an empty-queue escape instead of
letting the real consume path run) that a larger single entry would trip. But the terminator
right after the one entry is not: the real walker (disassembled at `0x122988`, confirmed live
by Grok) explicitly supports and expects a multi-entry list —

```text
if s2 != 0:  consume_chunk (callback ~0x123F58, plain memcpy, ≤1024 bytes/call)
else:        load next {ptr, size} at s4; s4 += 8 (delay slot, unconditional)
if size == 0: skip empty entries
after a chunk: s2 -= n; budget -= n; loop while budget remains
```

When the current entry is fully consumed and the caller's read budget isn't yet satisfied, it
loads the **next** iovec entry. Our terminator stops it dead after the first 64KiB (~17.5% of
the real 374,784-byte asset), well before the real budget the game is asking to satisfy.

The escape function's own design already anticipates multiple firings — `_ioQueueEscapes` is
capped at 256, and the PC-range re-entry check (`inScan`) is structured to fire again on a
later empty-queue hit. In practice it doesn't: after `n=1`, the `!empty` early-return
(`Burnout3Assist.cs:1981`, `if (!empty && !absurdS4 && !hugeCopy) return;`) means the scan
never gets a second chance to plant entry #2 — nothing re-drives it back into the empty/absurd
state the escape condition requires.

---

## 2. Evidence

`DETPS2_TRACE_BIOS=1` (existing flag, no new instrumentation), standard 30M- and 50M-cycle
`blocker-trace burnout-only.json --host-present` runs:

```text
cyc=28.00M   plant STAGEHED @ 0x01900000 size=374784 (real DATA/STAGEHED.BIN, off ISO)
cyc=28.85M   escape empty iovec  n=1  cdvd=609  -> jumps to cdvd=1865 shortly after
cyc=34.7M    (next B3 trace line: unrelated boot-wait-flag plant, n=32) -- cdvd still 1865
cyc=47.5M    (boot-wait-flag plant n=64) -- cdvd still 1865
```

Final state, both 30M and 50M cycle budgets: `cdvdSectors=1865`, flat, no further
`escape empty iovec` lines at any `n` beyond 1. `1865` sits inside the file's own
`stagePlantOnly` bucket (`sys.Cdvd.SectorsRead is >= 600 and < 2000`); the next threshold,
`postTxd` (`>= 2000`, gates `MaybePlantFrontendTxd`), is never crossed.

`374,784 / 65,536 = 5.72` → a full real-asset plant needs **6 entries** (5 full 64KiB + one
~29,952-byte tail) to cover the whole file.

---

## 3. Fleet risk / who else this could touch

**None beyond this title.** `MaybeEscapeEmptyIoQueue`/`MaybePlantStageAssets` are private
methods on `Burnout3Assist`, which only ever runs when `GameQuirkRegistry` resolves the
mounted disc's serial to `SLUS_210.50` (`Burnout3Assist.Serial`). No other title's boot path
touches this code. This is materially different from the scheduler-fairness fix
(`b3-ee-sched-fairness-design.md`) — that touched shared `KernelHle.cs` scheduling logic used
by every title; this touches one title's own quirk-assist implementation.

---

## 4. Candidate fix shapes (not decided — for dual-ACK)

| ID | Approach | Pros | Cons |
|----|----------|------|------|
| **S1** | Plant the **full multi-entry chain** up front (all `ceil(374784/65536)=6` entries, each ≤0x10000, `ptr` stepped through the already-loaded RDRAM copy, terminator only after the real last chunk) in the same escape call that currently plants one | Single, simple change; matches what the real walker already expects; no dependency on the currently-broken re-escape path; real ISO bytes, no fabrication | Slightly larger single write (6 iovec entries = 48 bytes instead of 16) — negligible |
| **S2** | Fix the re-escape path instead (make the scan re-enter `MaybeEscapeEmptyIoQueue`'s empty/absurd branch after each entry is consumed, so entries get planted one at a time, `n=1..6`) | Closer to the function's apparent original intent (progressive re-plant, `_ioQueueEscapes` cap of 256 suggests many small escapes) | More invasive — requires understanding *why* the walker never re-enters the empty/absurd state after consuming a real entry (does it loop back to the same scan PC range at all, or move on?); higher risk of new edge cases per re-entry |
| **S3** | Do nothing yet; keep investigating whether `postTxd`/`frontendEra` are even reachable via this path at all before committing engineering effort | Zero risk | Leaves a concrete, well-understood, high-confidence lead unaddressed |

**Bias (not decided):** S1 — smallest, most direct change that matches the confirmed walker
behavior, doesn't depend on diagnosing why the re-escape path is currently dead, and is
easy to verify (one before/after `cdvdSectors` comparison). S2 is worth understanding for its
own sake (the re-escape path being dead is itself a small bug worth fixing eventually) but
isn't necessary to unblock progress past `stagePlantOnly`.

---

## 5. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **IOVEC-Q1** | Agree the single-64KiB-slice-then-terminate plant is the (or a) real limiter for B3's `cdvdSectors` plateau at 1865, given the walker's confirmed multi-entry design and the exact bucket-boundary fit? | Yes — both agents independently traced to the same conclusion; Grok confirmed the consumer disassembly directly |
| **IOVEC-Q2** | Prefer S1 (plant the full chain up front) over S2 (fix progressive re-escape) as the first thing to try? | Yes — S1 is strictly smaller and independently testable; S2 can follow later if S1 alone doesn't fully unblock progress |
| **IOVEC-Q3** | Required before landing: confirm via a real before/after run that `cdvdSectors` crosses 2000 (into `postTxd`) and ideally that `MaybePlantFrontendTxd` actually fires, not just that the plant code compiles/runs without crashing? | Yes |
| **IOVEC-Q4** | Given this is title-scoped (not shared Core), does it still need a full fleet smoke run, or is a B3-only before/after (plus the existing smoke suite passing unmodified) sufficient? | Lean: full existing smoke suite (cheap, already required for any Core-adjacent change) is enough — no other title's code path is touched, so a fleet-wide *behavioral* comparison isn't expected to show anything, but running the suite to confirm zero regressions elsewhere is still correct practice |

---

## 6. Non-goals

- Not claiming this fixes B3's ultimate black-screen symptom — crossing `postTxd`/`frontendEra`
  is a necessary step toward reaching a real menu/gameplay state, not a proven guarantee that
  VU1 execution, Path1 traffic, or continuous MSKPATH3 cycling (§30, §47) will then start on
  their own. Those remain open questions even if this lands cleanly.
- Not proposing to fix the dead re-escape path (S2) in this change — flagged as a follow-up,
  not required to unblock S1's benefit.
- Not landing anything from this doc alone — design only, per this project's whole-session
  discipline (dual-ACK + smoke suite required first).

```text
B3 iovec multi-entry design (not decided)
  real limiter: single 64KiB iovec entry + hard terminator, walker wants multi-entry (confirmed)
  evidence: DETPS2_TRACE_BIOS trace, escape n=1 -> cdvd 609->1865, then permanent silence
  fix (bias S1): plant full 6-entry chain (374784/65536) up front, terminate after real last chunk
  fleet risk: none -- serial-gated to SLUS_210.50 only, not shared Core
  needs dual-ACK + existing smoke suite green before landing; verify cdvd crosses 2000 after
```
