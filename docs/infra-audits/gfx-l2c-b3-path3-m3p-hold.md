# GFX L2c — B3 PATH3 / M3P hold dig

**Status:** **RESUMED** (2026-08-05, user direction: no parking for "needs more tooling" — build the tooling) — see §12  
**Date:** 2026-08-05  
**Title:** Burnout 3 (SLUS_210.50)  
**Parents:** `gfx-l2c-b3-frame-dispfb-stall-finding.md`, Claude page-0x46 dump (`b7048b1`), Claude FQC refute (`bc239a9`), Claude forced-unmask A/B (`f8b5db8`)  
**Author:** Grok + Claude (split seats; dual-ACK park)

---

## 0. One-line

At 50M, **M3P is left asserted** with a **held PATH3 queue of 5 entries / 2124 QW** that never drains, because the game’s **last MSKPATH3 is a mask with no matching unmask**. That is real stuck DMA payload under HLE hold (not Path2-sticky block). Combined with page 0x46 being 100% black, the backlog is a strong candidate for “geometry never lands / never flips,” not a compositor lie.

---

## 1. Product end-state (50M host-present, tip ~b7048b1 / eb9105c)

```
gif-path: p3=20 p3qws=6408 m3p=True heldP3n=5 heldP3qwc=2124 heldSubmits=18
          mskPath3=10 p2=12 p2qws=543
gif-tags: packed=30 reglist=56 image=4 abortTrunc=6 abortOther=0
          lastAbort=DIRECT-end-truncate
gif-last: flg=0 nloop=26624 nreg=3 (no sticky at end: inFlight=False)
claim:    px=877187 lit=0 dispfbPx=0 frame1=0xA0046 dispfb2=0x51400
```

`abortTrunc=6` (not `abortOther`) confirms the Vif/Gif string alignment fix is live.

---

## 2. Method

Temp log gated `DETPS2_TEMP_B3_M3P=1` on:

- `Gif.SetMskPath3`
- `EnqueueHeldPath3`
- `DrainHeldPath3` (start + Path2-sticky block)

50M `blocker-trace burnout-only.json --host-present`. **Fully reverted** (`Gif.cs` clean).  
Artifacts (gitignored): `out/canaries/b3-path3-m3p/`.

---

## 3. MSKPATH3 timeline (10 calls = `mskPath3=10`)

| # | masked | heldN before | heldQwc | Notes |
|---|--------|-------------:|--------:|-------|
| 1 | **False** | 0 | 0 | cold unmask |
| 2 | **True** | 0 | 0 | mask; pktPath=3 progress 16/16 just finished |
| 3 | **False** | 1 | 2 | unmask → drain |
| 4 | **True** | 0 | 0 | mask |
| 5 | **True** | 5 | 2124 | re-mask while already masked; queue already full |
| 6 | **False** | 6 | 2126 | unmask → **Drain START heldN=6** |
| 7 | **True** | 0 | 0 | mask |
| 8 | **True** | 5 | 2124 | re-mask; queue full again |
| 9 | **False** | 6 | 2126 | unmask → **Drain START heldN=6** |
| 10 | **True** | 0 | 0 | **final mask** |

**Final product metrics:** `m3p=True heldP3n=5 heldP3qwc=2124`  
⇒ after event #10, the recurring **5-packet / 2124 QW** pattern enqueues **again** and **never sees another unmask** through 50M (and previously 100M).

DrainHeldPath3 **START** ×3; **BLOCKED_PATH2_STICKY** ×0 — hold is pure M3P, not Path2 arbitration.

---

## 4. Held queue composition (recurring pattern)

Each “batch” under mask looks like:

| Role | Example addr | qwc |
|------|--------------|----:|
| small | `0x008D5C10` / `0x009D55x0` | 2 |
| **bulk** | **`0x00489BC0`** | **2053** |
| small | `…` | 2 |
| mid | `0x00491C40` | 11 |
| small | `…` | 2 |
| mid | `0x01D6EA10` / `0x01D6E290` | 56 |
| **batch total** | | **~2124** |

18 enqueues = roughly **three** full batches (two drained on unmask #6/#9; third left held under final mask).

The **2053-QW** Path3 kick is the load-bearing payload (IMAGE-class size). Whether it is scene geometry vs clear/list data is open — but it **never processes** on the final iteration.

---

## 5. Code map (load-bearing)

| Piece | Behavior |
|-------|----------|
| VIF1 `MSKPATH3` | `Vif.cs` → `Gif.SetMskPath3(imm&0x8000)` |
| Mask | `_m3p=true`; Path3 DMAC kicks **enqueue** instead of `ProcessTransfer` |
| Unmask | `_m3p=false` → `DrainHeldPath3()` |
| End state | Last game MSKPATH3 = **mask**; queue refilled; **no unmask** |

HLE is doing what the EE commanded. The open question is **why the EE never issues unmask** after the last mask (path-sync / flip / VBlank wait condition).

---

## 6. Link to other B3 findings

| Finding | Link |
|---------|------|
| DISPFB stuck page 0, FRAME 0x46 | Compositor honest; flip never runs |
| Page 0x46 100% black (Claude) | No hidden frame; last paint is clear/empty |
| px=877187 plateau | Matches few earlier drains + no late Path3 bulk |
| SleepThread VBlank flags | Flags **do** set; not the PATH3 stuck cause |
| DIRECT-end-truncate fix | Telemetry only; not this hold |

**Hypothesis (not Core):** Game masks PATH3, queues display/scene PATH3 DMA, expects a condition (path-sync at `0x001F1A28` FQC class, or flip pipeline) that never completes, so **unmask never runs** → held 2124 QW forever → black page + no DISPFB flip.

---

## 7. Next seats (dual-ACK before Core)

1. **EE why-no-unmask:** who issues MSKPATH3 mask/unmask (PC/RA of VIF MSKPATH3); is unmask gated on FQC/VBlank/flip flag that stays false?  
2. **Optional A/B (measure only):** force-unmask at 30M once, see if held 2124 QW changes page 0x46 / DISPFB / px — **temp + full revert**; dual-ACK before any permanent Core.  
3. **Do not** invent DISPFB flip or present page 0x46 while M3P-held.

```text
B3 PATH3/M3P dig
  Final: m3p=True heldP3n=5 heldP3qwc=2124 (third batch never drained)
  3 drains earlier on unmask; last MSKPATH3 is mask with no peer unmask
  No Path2-sticky block on drain
  Next: EE reason for missing final unmask (not more cycle budget)
```

---

## 8. FQC-honesty hypothesis pursued and REFUTED — the historic 0x1F1A28 loop is never even entered (Claude)

Took §7 item 1 (EE why-no-unmask). Started from `Gif.cs`'s own extensive prior-investigation
comments (lines 121-134, 636-660, 728-730) describing a previously-known Burnout 3 path-sync
loop at `0x001F1A28` that spins on `GIF_STAT.FQC` after `MSKPATH3` — a real, documented
mechanism (`M1-a`) from before tonight's session, with a narrow fix (`_path3RaceEvidencePolls`)
already landed for one specific race scenario.

### 8.1 First check: is the M1-a FQC-honesty mechanism actually the gap here?

Read `EnqueueHeldPath3` (`Gif.cs:469-499`) closely: it **already** sets `_fifoCount` from the
real held-queue total, capped at the real 16-QW hardware FIFO depth (lines 495-498), every
time data is queued under mask — not just via the narrow `_path3RaceEvidencePolls`
fabrication. So `ReadStat()`'s `fqc = _fifoCount/4` genuinely goes non-zero and honest the
moment any Path3 data is held, independent of the race-evidence mechanism. **My first
hypothesis (FQC stays dishonestly 0 for the held-queue case) does not hold — the code already
handles this correctly.**

### 8.2 Direct measurement: does the EE even reach that poll?

Disassembled `0x1F1A00-0x1F1B00` (real code, `disasm burnout-only.json 20000000 1F1A00:100`):

```
1F1A08: lw v0,0(t4)
1F1A10: and v0,v0,v1        ; v0 &= 0x1F000000
1F1A14: bne v0,zero,0x1F1A48  ; if already nonzero, SKIP the FQC poll entirely
...
1F1A24: ori v1,v1,0x3020    ; v1 = GIF_STAT address
1F1A28: lw v0,0(v1)         ; <- the historic poll PC
1F1A2C: and v0,v0,a0        ; v0 &= 0x1F000000 (FQC field)
1F1A3C: beq v0,zero,0x1F1A28  ; spin while FQC==0
1F1A48: ...continues regardless of which path was taken
```

`--pcbreak=0x001F1A28:0x001F1A28` over the full 35M window: **zero hits.** The EE never
executes this instruction at all during B3's stuck state — meaning the earlier check at
`0x1F1A14` is always already satisfied (whatever `t4` points to is always nonzero when this
code runs), and the FQC poll is skipped every time, not entered and stuck.

### 8.3 Conclusion

**Refuted.** The historic `0x1F1A28` FQC path-sync loop — real, previously documented,
correctly fixed for its own scenario — is not what's blocking B3's final unmask. Whatever
`Gif.cs`'s comments describe from prior investigation either applies to an earlier moment in
this same title's boot (before the stuck window) or to a different title/scenario entirely;
either way it does not explain the current symptom. Not proposing any FQC-related Core
change — none is needed here.

### 8.4 Handing back to §7 item 1 as originally scoped

This doesn't answer "who issues MSKPATH3 and why the final unmask never happens" — it only
rules out one specific, plausible-looking historic mechanism. The real next step is still
Grok's original framing: find the actual PC/RA of the VIF `MSKPATH3` write instruction(s) and
trace what condition gates the matching unmask, without assuming it's FQC-related. `t4`'s
value at `0x1F1A08` (whatever real state it reads) might be worth a look if this seat
continues — it's the thing that's *always* true here, which could itself be informative about
what the game already knows/assumes at this point.

```text
FQC-honesty hypothesis (Claude) -- REFUTED
  EnqueueHeldPath3 already sets honest FQC from the real held queue -- not the gap
  --pcbreak=0x1F1A28 -> zero hits in 35M -- EE never enters this poll at all
  historic path-sync loop doesn't explain current stuck state
  next: trace real MSKPATH3 write site + matching-unmask gate directly (Grok's original ask)
```

---

## 9. MSKPATH3 data-flow — who writes the mask/unmask words (Grok, continued seat)

Claude's §8.4 note was right: VIF codes arrive as a DMA-read stream, so "who issues
MSKPATH3" is "who wrote `0x06008000` / `0x06000000` into the VIF source buffer," not
current-PC-at-dispatch. Prior half-done attempt only logged stream source addrs
(`MSKPATH3_SRC`); this pass closed the writer with pure tooling (no Core, no TEMP).

### 9.1 Stream source (from prior TEMP, already known)

| Role | VIF code | ProcessStream src |
|------|----------|-------------------|
| unmask | `0x06000000` | **`0x007FC8FC`** |
| mask | `0x06008000` | **`0x007FCA80`** |

Final event #10 is mask from `0x007FCA80`. After that, queue refills to held 2124 QW and
never sees another unmask through 35M/50M.

### 9.2 `--find-writer` (35M, `--track-writers`) — decisive

Both words are written **once**, same cycle, by the same buffer-init routine:

| Address | Value | Cycle | PC | Insn |
|---------|-------|------:|----|------|
| `0x007FC8FC` | `0x06000000` (unmask) | **14340768** | **`0x001F4124`** | `sq t0, 112(v1)` |
| `0x007FCA80` | `0x06008000` (mask) | **14340768** | **`0x001F4144`** | `sq t0, 512(v1)` |

`last-writer log: 7198411` distinct addresses tracked — so empty prior attempt was a tooling
miss, not "never written." At end of stuck window the words are still the same values
(`--dump=007FC800:300` confirms). **They are never rewritten after init.**

`--find-transfer=007FC800:400`: no DMA transfer *into* this range (correct — EE `sq`
builds it). TransferLog does not surface mid-chain VIF1 MADR advances that *consume* these
offsets; that is a separate dig if needed.

### 9.3 Builder disasm (`0x001F3F98`…, near historic path-sync at `0x1F1A28`)

Function ~`0x001F3F98` allocates/aligns a graphics VIF command buffer (gp-relative base
stored at `gp-28316` → later `v1`), zero-fills it with `sq t0`, then plants fixed VIF codes:

```
1F4110  lui  v0, 0x1100          ; FLUSH
1F411C  lui  a1, 0x0600
1F4120  ori  a1, a1, 0x8000      ; a1 = 0x06008000  (mask IMM)
1F4124  sq   t0, 112(v1)         ; QW @ base+0x70 = [FLUSH, 0, 0, 0x06000000 unmask]
1F4138  pcpyld t0, a2, a1        ; pack mask code
1F4144  sq   t0, 512(v1)         ; QW @ base+0x200 = [0x06008000, 0, 0, 0]
1F4154  sq   t0, 528(v1)         ; FLUSH/FLUSHA pair at base+0x210
```

Buffer base ≈ `0x007FC880` (from mask addr − 0x200). Layout is a **static template**, not a
per-frame rewritten list.

### 9.4 What this reframes

| Was open | Now known |
|----------|-----------|
| Who writes final mask word? | Builder `0x001F4144` once at ~14.3M |
| Is unmask word missing/corrupt? | **No** — `0x007FC8FC` still holds `0x06000000` at stuck end |
| Is HLE inventing mask? | **No** — game planted both codes in RAM |

The stuck state is **not** "EE forgot to store the unmask word." Both codes sit correctly in
the static list. The failure is **submission / consumption**: after the final mask kick,
nothing re-kicks the unmask offset of the same buffer (and/or the path that would DMA that
slot never runs). HLE holds Path3 under M3P exactly as commanded.

### 9.5 Honest bound / next (dual-ACK before Core)

**Parked as measure-complete for the data-flow seat** unless dual-ACK picks:

1. **VIF1 submit path:** which code kicks DMA (or FIFO) at `0x007FCA80` vs `0x007FC8FC`
   after the buffer is built — TADR/MADR chain or FIFO poke; why the unmask slot is not
   re-submitted after mask #10. (TransferLog alone was insufficient for mid-chain MADR.)
2. **Optional A/B (still dual-ACK):** one forced `SetMskPath3(false)` mid-run after held
   2124 — temp + full revert — only to see if page 0x46 / DISPFB / px move.
3. **Still ban** invent DISPFB flip / present page 0x46 / permanent force-unmask Core.

```text
MSKPATH3 data-flow (Grok) -- CLOSED for writer question
  unmask word @ 0x007FC8FC = 0x06000000  last writer pc=0x001F4124 cyc=14340768 (once)
  mask   word @ 0x007FCA80 = 0x06008000  last writer pc=0x001F4144 cyc=14340768 (once)
  same static VIF list builder ~0x001F3F98; never rewritten after init
  stuck = missing post-mask re-submit of unmask slot, not missing/corrupt unmask data
  no Core; dual-ACK before force-unmask A/B or submit-path Core theory
```

---

## 10. Causal A/B: forced unmask (Claude) — real data confirmed, but does NOT fix the flip

Took §9.5 item 2 (the optional A/B, dual-ACKed). Temp diagnostic
(`Tests/TempB3ForceUnmask.cs`, gated env vars, fully reverted — `git status` clean) calling
`Gif.SetMskPath3(false)` directly (existing public method) once at cycle 25,000,000 (confirmed
via the same run: `m3p=True heldN=5 heldQwc=2124` at that point, matching the known final
state — so this lands after all 10 real `MSKPATH3` events), then running to 50M and comparing
against an unforced baseline in the same harness.

### 10.1 Result

| | Baseline (no force) | Forced unmask @ 25M |
|---|---|---|
| `m3p` @ 50M | `True` | `False` |
| `heldN` / `heldQwc` @ 50M | `5` / `2124` | `0` / `0` (drained) |
| `px` @ 50M | `877187` | **`1172419`** (+295,232) |
| `FRAME_1` / `DISPFB1` / `DISPFB2` | `0xA0046` / `0x0` / `0x51400` | **identical** |
| page-0x46 distinct colors | 1 | 1 |
| page-0x46 sample color | `0xFF000000` | `0xFF000000` (**identical**) |

### 10.2 What this establishes

- **The held 2124 QW batch is real, load-bearing draw data, not garbage or padding.**
  Draining it causes the interpreter to genuinely process ~295K more fragments —  a real,
  substantial amount of additional GS work, not a no-op.
- **But it does not fix anything visible.** `FRAME_1`/`DISPFB1`/`DISPFB2` don't move at all —
  not even a little — and page 0x46 is still **exactly** uniform opaque black afterward, same
  sample color, same distinct-color count. Whatever the drained batch actually was (more
  likely a clear/setup batch than colorful scene geometry, given the outcome), it did not
  produce a visible frame or trigger any subsequent flip logic.

### 10.3 Conclusion — temper the PATH3 hypothesis

**Unblocking the PATH3 hold, on its own, is not sufficient to fix B3's black present.** Even
if the real root cause of the missing final unmask were found and correctly fixed, this
causal test suggests it would not, by itself, produce a visible frame — something else
(gated on later game-loop progress that itself may depend on more than just this one drain)
is still required. This doesn't mean the PATH3 hold is irrelevant — it's still a real,
confirmed-load-bearing stuck mechanism worth understanding and eventually fixing — but it
tempers any expectation that finding/fixing it alone resolves the visible symptom. Consistent
with this project's own doctrine: confirm before claiming, and one real fix rarely equals the
whole picture on a title this deep in an unresolved state.

### 10.4 Not proposing a Core fix

This was explicitly measure-only per the dual-ACK. No Core change proposed from this result —
if anything, it argues for *more* investigation (what happens after the drain that still
prevents a flip) before touching PATH3/M3P mechanics at all.

```text
Forced-unmask A/B (Claude) -- measure only, reverted
  draining held 2124 QW -> +295,232 real px (confirms real, load-bearing data)
  FRAME_1/DISPFB1/DISPFB2 unchanged; page 0x46 still exactly 0xFF000000 uniform
  PATH3 hold is real but NOT sufficient alone to explain/fix the black present
  next: what gates the flip AFTER a successful drain -- separate, still-open question
```

---

## 11. Parking summary (dual-ACK, Grok) — B3 L2c PATH3/M3P sub-chain

**Decision:** park this sub-investigation tonight. Not closed as "solved"; closed as
**honestly bounded**. Multiple real mechanisms found; each alone fails to explain the
visible black present. Diminishing returns without a fresh angle.

### 11.1 What is solid (do not re-litigate without new evidence)

| Finding | Tip | One-liner |
|---------|-----|-----------|
| DISPFB stuck page 0 vs FRAME `0x46` | parent L2c doc | Compositor honest — not a present-selection lie |
| Page `0x46` 100% opaque black | `b7048b1` | No hidden real frame in draw target |
| SleepThread / VBlank flags | `ffe5da3` / `4bf8bd3` | ISR sets all 4 slots; mid-ISR abandon refuted |
| Final MSKPATH3 = mask, held 2124 QW | `5226dda` | HLE M3P hold correct; no peer unmask |
| FQC path-sync @ `0x1F1A28` | `bc239a9` | Zero hits in stuck window — not the gate |
| Static VIF list writer | `175e8a4` | Unmask word still correct in RAM; missing re-submit |
| Forced unmask A/B | `f8b5db8` | +295k real px; FRAME/DISPFB/page46 unchanged |

### 11.2 What remains open (resume only with a fresh angle)

1. **What gates DISPFB/FRAME flip after a successful Path3 drain?** (forced drain proves
   Path3 alone is insufficient.)
2. **Why the game never re-submits the unmask slot** of the static list after mask #10
   (submit/TADR/FIFO path — TransferLog insufficient for mid-chain MADR).
3. **What the drained +295k fragments actually were** (clear/setup vs geometry) if that
   informs the flip gate.

### 11.3 Bans that still hold

- No invent DISPFB flip / present page `0x46`
- No permanent force-unmask Core without dual-ACK + design
- No re-opening FQC-honesty or page-46-hidden-frame without new contradictory evidence

```text
B3 L2c PATH3/M3P chain -- PARKED (honest bound)
  real hold + real held data + static mask/unmask template + force-drain insufficient
  flip gate after drain still open; no Core proposed
  resume only with fresh angle or dual-ACK new seat
```

---

## 12. Forced-drain leaves the EE's own PC trace byte-for-byte unchanged (Claude, resumed)

Built the needed tooling rather than parking: extended the forced-unmask harness
(`Tests/TempB3PostDrainPc.cs`, temp, gated, fully reverted — `git status` clean) with
`PcProfiler` (existing product infra, `DETPS2_PROFILE_PC`), reset right after the force call
so it profiles *only* the post-drain window, compared against the same window with no force.

### 12.1 Result

Two windows compared, `[25M, 50M)`, baseline vs. forced:

- `px` in the forced run is **already at its final value (1,172,419) the instant
  `SetMskPath3(false)` is called** — `DrainHeldPath3` runs synchronously inside the call, not
  spread across cycles. (This also explains why the earlier §10 A/B test's px delta showed up
  as a single jump rather than gradual growth — missed at the time.)
- The **PC profile for the entire following 25M-cycle window is byte-for-byte identical**
  between forced and baseline — same top-30 addresses, same exact counts down to the last
  digit (`0x00237188` → `857033` in both, every other address matching too). `diff` on the
  two full logs shows zero difference outside the header lines (px/m3p/held state).

### 12.2 What this establishes

**Draining PATH3 does not perturb the EE's own executed instruction stream at all.** The
dominant loop (`0x237180-0x237198`, the VBlank-flag poll characterized in the parent doc) is
not gated on PATH3/GIF_STAT/M3P in any way the EE currently checks — forcing the drain
doesn't wake it, doesn't redirect it, doesn't even shift its iteration count by one. This
**decisively separates the two findings**: the PATH3 hold and the VBlank-flag-poll wedge are
fully independent blockers, not sequentially linked as the original hypothesis in §6 framed
them ("Game masks PATH3, queues display/scene PATH3 DMA, expects a condition... so unmask
never runs"). That framing implied a causal chain; this measurement shows there isn't one —
PATH3's state genuinely doesn't factor into what the EE's currently-dominant thread is doing
at all.

### 12.3 Implication

Fixing "why does the final MSKPATH3 never get its unmask" would very likely have **zero**
effect on the visible symptom, independent of §10's finding that force-draining doesn't fix
the flip either — two separate confirmations converging on the same conclusion via different
methods (composite-state comparison in §10, full PC-trace comparison here). **PATH3 is very
likely a real but ultimately unrelated finding** — worth fixing on its own merits eventually
(real load-bearing data sitting stuck is still a correctness gap), but not the lever that
unblocks B3's visible rendering. The actual blocker is squarely in whatever gates the
VBlank-flag-setting mechanism for the threads whose polls dominate this trace — back to
`0x2370A0`'s real per-slot behavior (§10 of the parent doc already showed all 4 slots DO get
set correctly over a full run) or a *different*, not-yet-identified condition entirely.

### 12.4 Next (no Core, tooling exists — do not park on this)

1. Since PATH3 is now decoupled, drop it as the primary lead. Refocus on: **why does the
   specific thread/slot whose SleepThread calls dominate this PC trace never see its own
   flag go non-zero**, even though §10 showed flags DO get set 71-times-each over a full
   run — reconcile per-thread timing (is THIS window's dominant thread's specific flag set
   late, rarely, or never relative to its own poll cadence?).
2. Correlate `RA=0x00237188`'s call stack (which specific thread/slot) against the flag-SET
   timeline from §10 to see if there's a timing mismatch (sets happening, but not fast enough
   / not for this specific thread) rather than a binary works/doesn't-work split.

```text
Forced-drain PC-trace diff (Claude, resumed -- not parked)
  px jumps to final value INSTANTLY at the force call -- DrainHeldPath3 is synchronous
  PC profile for the full post-drain window is byte-identical to baseline, no exceptions
  PATH3 hold and VBlank-poll wedge are fully INDEPENDENT -- not a causal chain
  PATH3 fix would very likely NOT fix the visible symptom -- redirect focus to per-thread
    flag-set timing instead
```

---

## 13. Two of four flag slots are set-but-never-cleared within the late window (Claude)

Followed §12.4's own next step immediately rather than stopping. `--watch=0x004E2964
--watch-after=25000000` (word-aligned base, catches all 4 slot bytes via real per-hit vaddr,
same technique as the earlier corrected §13-of-the-parent-doc measurement) over `[25M, 50M)`:

### 13.1 Result — SET vs CLEAR count per slot in this window only

| Slot (vaddr) | SET (`→1`) | CLEAR (`→0`) |
|---|---:|---:|
| `0x4E2964` (0) | 98 | 65 |
| `0x4E2965` (1) | 98 | **3** |
| `0x4E2966` (2) | 98 | **3** |
| `0x4E2967` (3) | 98 | 98 |

Slots 0 and 3 roughly balance (set and cleared at comparable rates — a healthy
signal/consume cycle). **Slots 1 and 2 get set 98 times each but cleared only 3 times** —
their flags sit at `1` for the overwhelming majority of this 25M-cycle window without the
waiter thread consuming (clearing) them.

### 13.2 Interpretation

The ISR-side set mechanism is firing correctly and on schedule for every slot (matches §10's
finding that all 4 slots receive `sb 1` correctly). The imbalance is entirely on the
**consumer** side: whichever real thread owns slots 1 and 2 is not reaching its own
flag-clear code at anywhere near the rate its flag gets set. This is a different, more
precise question than "does the flag ever get set" (already answered: yes) — it's "why does
the owning thread not get back around to observing/clearing an already-set flag."

**Not yet confirmed:** the exact slot→tid mapping (table registration order strongly
suggests slot0↔tid3, slot1↔tid4, slot2↔tid5, slot3↔tid6, matching the live table dump in
§10, but this needs direct confirmation, not assumption). A generic end-of-run thread-state
snapshot (`sleeping=False` for tids 1-5, `True` for tid 6) wasn't informative on its own —
these threads are calling `SleepThread` constantly, so an instantaneous snapshot mid-loop
doesn't distinguish "genuinely stuck" from "just between poll iterations."

---

## 14. Dual-check of §13 flag set/clear (Grok, independent re-measure)

Same method: `blocker-trace burnout-only.json --cycles=50000000 --watch=004E2964
--watch-after=25000000` (no TEMP). Aggregated WROTE 0/1 per vaddr in `[25M,50M)`:

| Slot | SET (`→1` @ `0x237108`) | CLEAR (`→0` @ `0x2371C8`) | READ (`lbu` @ `0x237188`) |
|-----:|------------------------:|--------------------------:|--------------------------:|
| 0 | **98** | **65** | **229** |
| 1 | **98** | **3** | **0** |
| 2 | **98** | **3** | **0** |
| 3 | **98** | **98** | **0** |

**Confirms Claude §13 shape exactly** (set 98 all slots; clear 65/3/3/98). Additional note from
this parse: the dominant poll PC `0x237188` only **READ**s slot0's byte in the watch log —
slots 1–3 show **zero READs** at that PC in this window. Clear site `0x2371C8` does hit all
slots (unevenly). So the imbalance is not only “consumer slow,” but possibly **poll path is
biased to slot0** while ISR still sets all four.

```text
Grok dual-check §13 -- CONFIRMED
  set 98/98/98/98  clear 65/3/3/98  (matches Claude)
  READ@0x237188 only on slot0 (229x); slots1-2 never read at that PC in window
  next: which code clears slots1/2 (rare) + slot->tid + why poll prefers slot0
```

### 14.1 RETRACTED: “zero READs on slots 1/2” was a Read8-watch artifact (Claude)

`Read8` watch matches **exact vaddr** (commit `9878499`), not word-aligned. Watching only
`0x004E2964` can never log `lbu` at `0x4E2965/66/67`. Claude re-measured each slot
separately in `[25M,50M)`:

| Slot | READs (own addr) |
|-----:|-----------------:|
| 0 | 229 |
| 1 | 21 |
| 2 | **3192** (most traffic) |
| 3 | 321 |

**Retracts** §14’s “s1 never equals 1/2” / “poll only runs s1=0.” Disasm still shows
`s0 = base + s1` (that part stands), but slot2 **is** polled heavily. SET/CLEAR imbalance
(98 set vs 3 clear for slots 1/2) remains the real finding: slot2 spins reading a flag that
stays 1 and almost never reaches clear.

```text
§14.1 RETRACTED (tooling) -- Claude per-slot READ watch
  Read8 exact-vaddr: watching only 0x4E2964 hides slots1-3 reads
  real READs: slot0=229 slot1=21 slot2=3192 slot3=321
  SET/CLEAR 98 vs 3 for slots1/2 STILL REAL
  puzzle: slot2 polls hard but almost never clears after set
```

### 14.2 Table dump @ 50M (Grok)

`0x01D80700`: words **3, 4, 5, 6** (tids for slots 0–3). Flags word `0x004E2964` =
`0x00010101` → bytes **1,1,1,0** (slots 0–2 set, slot3 clear) at end of run.

### 14.3 Poll-success path does **not** clear the flag (Grok disasm)

After `lbu` / `beq v1,zero` poll loop, **non-zero** flag falls through to:

```
2371A0..B0  table_base = 0x1D80700; a1 = -1
2371B4      b  0x2371E0          ; jump to function epilogue
2371B8      sw a1, 0(table+s1*4) ; delay: free slot (tid := -1)
; 2371E0: restore ra/s0/s1; jr ra
```

**No `sb zero` on this path.** Flag clear lives only on the **registration** arm:

```
237154  beq v0, -1, 0x2371BC     ; free table slot found while scanning
2371BC  v0 = flag_base + s1
2371C4  jal 0x0010BD10           ; (kernel helper)
2371C8  sb  zero, 0(v0)          ; CLEAR flag  ← only clear site in this function
```

So: **ISR sets flag → waiter eventually sees non-zero → frees table entry and returns with flag still 1.** A later **re-registration** is what clears. That reframes 98 set / 3 clear: slots 1/2 may complete the wait rarely re-register (or re-enter register+clear only 3×), while still generating many poll READs if they re-enter the wait path differently — or the high READ count is spin-before-success. Open: count how often poll-success epilogue (`0x2371B4`) runs per slot vs register-clear (`0x2371C8`).

```text
Poll success = free table + return; flag stays 1
Clear only on register-free-slot path @ 0x2371C8
SET/CLEAR imbalance may be re-registration gap, not "read sees 1 but branch fails"
```

---

## 14. Scheduler-fairness measurement: tid4/tid5 genuinely get picked far less often (Claude)

Grok's §13-follow-up correctly showed slots 1/2's owning threads only *complete* 3
wait-cycles each in the late window vs 65/98 for slots 0/3, and proposed a "lost-wake race"
hypothesis (ISR calls `WakeupThread` before the `sb flag=1` delay-slot store; waiter could in
theory poll-and-resleep in the gap). Before chasing that race, checked a simpler, already-
plausible cause: **is the scheduler itself picking these threads unevenly** — real thread
priorities (confirmed via `DETPS2_TRACE_RPC`, `priority=` field): `tid1=1` (best), `tid2=64`,
`tid3=tid4=tid5=54` (tied), `tid6=33`, `tid7=22`.

### 14.1 `FindNextRunnable`'s tie-break (`KernelHle.cs:938-984`)

Priority-based scheduler (`B3` doesn't opt into `PreferRoundRobinSched` — only
`MidwayBootAssist` sets that, for Midway titles). Among threads tied at the best available
priority, the scan starts at `(idx_of_afterId + 1) % count` and returns the **first** match in
that circular order — i.e., whichever tied thread sits closest (in array/tid order) after
whichever thread happened to be the one yielding.

### 14.2 Real measurement (temp trace on the tie-break `return`, gated
`DETPS2_TEMP_SCHED_TRACE`, fully reverted — `git diff --stat` empty), `blocker-trace
--cycles=26000000`, tail of the trace (steady-state late window):

Total times each tid was the one **picked** to run by this scheduling path:

| tid | picked count |
|---|---:|
| 1 | 86 |
| 6 | 42 |
| 3 | 26 |
| **4** | **16** |
| **5** | **12** |
| 2 | 5 |
| 7 | 2 |

**tid4 and tid5 (owners of slots 1/2, per the stable table dump) get picked to run roughly
2-7x less often than tid1/tid3/tid6.** This is real, measured, not inferred — a genuine
scheduling imbalance, independent of and prior to any question about whether they "see" their
flag once running.

Breaking down specifically `afterId=1 -> picked=X` (when the dominant main thread yields):
`6`×38, `3`×23, `4`×9, `5`×7 — tid6 wins most (matches its better priority, 33<54), but among
the *tied* group (3/4/5), tid3 still wins noticeably more than tid4/tid5 (23 vs 9 vs 7) — a
real but moderate bias, weaker than a strict "tid3 always wins ties" would produce. Not the
whole story on its own, but a real, additive contributor.

### 14.3 How this relates to Grok's lost-wake hypothesis

Not mutually exclusive — likely compounding. tid4/tid5 getting picked far less often (§14.2)
means they have far fewer opportunities to reach their poll-read at `0x237188` at all, which
independently makes a genuine race window (Grok's hypothesis) more likely to matter *when*
they do run, since they've accumulated more missed vblanks by the time they get scheduled.
Either framing points at the same place: **tid4/tid5 are structurally disadvantaged by the
scheduler**, whether the direct cause is tie-break bias, raw pick-frequency, or a race that's
made worse by infrequent scheduling.

### 14.4 Not proposing Core yet

This is scheduler-level, not B3-specific — a fix here could affect any title with multiple
equal-priority threads. Needs the same dual-ACK + design-doc discipline as tonight's other
findings before touching `KernelHle.cs`. Possible fix shapes worth discussing (not decided):
a persistent rotating "last picked" cursor independent of `afterId`, or fair queuing among
tied-priority threads — but this needs its own design review, not a quick patch.

```text
Scheduler-fairness measurement (Claude)
  real priorities: tid1=1, tid3/4/5=54 (tied), tid6=33, tid7=22
  real picked-counts over 26M: tid1=86 tid6=42 tid3=26 tid4=16 tid5=12
  tid4/tid5 measurably starved relative to tid1/tid3/tid6 -- not just theory
  compounds with (doesn't replace) Grok's lost-wake race hypothesis
  scheduler-level finding, not B3-specific -- needs design review before Core
```
### 13.3 Next (no Core, continuing — not parking)

1. Confirm the slot→tid mapping directly (dump the live table `0x01D80700` alongside a
   per-slot SET/CLEAR count correlated to `CurrentThreadId` at each write).
2. Once confirmed, trace what tid 4/5 (presumed slots 1/2) are actually doing between
   consecutive `SleepThread` calls — is there a real wait condition (semaphore, different
   flag, priority starvation from tid 1/2/3) blocking them from reaching their own
   flag-check/clear code, or do they reach it but the read/clear itself doesn't work for
   some structural reason.

```text
Slot SET/CLEAR imbalance (Claude, resumed)
  slots 0/3: set~cleared (healthy cycle); slots 1/2: set 98x, cleared only 3x each
  ISR-side set mechanism confirmed working for all slots (matches section 10)
  consumer-side: whichever thread owns slots 1/2 isn't reaching its own clear code
  next: confirm slot->tid mapping, trace what tids 4/5 actually do between SleepThreads
```

---

## 15. Poll-success hits by s1 match clear counts (Grok, PCBREAK)

`--pcbreak=002371B4` (poll-success branch) full 50M, aggregate `s1` from PCBREAK GPR dump:

| s1 (slot) | success all | success <25M | success >=25M |
|----------:|------------:|-------------:|--------------:|
| 0 | 85 | 20 | **65** |
| 1 | 14 | 11 | **3** |
| 2 | 13 | 10 | **3** |
| 3 | 111 | 13 | **98** |

Late-window successes **exactly match** late CLEAR counts from `--watch` (65/3/3/98).

`--pcbreak=002371C8` (flag clear): **0 hits** for 50M — expected if clear is **jal delay slot** of `0x2371C4` (PC log attributes to jal, not delay). Watch still sees the write at PC `0x2371C8`.

### Interpretation

1. Control-flow read stands: success path does not clear; clear is register-arm.
2. **1:1 coupling** success@`0x2371B4` ↔ clear counts in the late window ⇒ after each success, re-registration (clear) runs the same number of times for that slot (or an equivalent path produces the same rate).
3. Slots 1/2 only complete the wait **3 times** after 25M (not 98). ISR still SETs 98× — **~95 SETs per slot never pair with a poll-success** (waiter not registered / not in poll when set, or already free).
4. Slot2’s 3192 READs (Claude) are almost all **flag==0 spin**, not 3192 successful wakes.

```text
Success@0x2371B4 by s1 late: 65/3/3/98 = clear counts
~95 ISR sets per slots1/2 have no matching waiter success
next: why slots1/2 stop re-registering / only succeed 3x after 25M
```

### 15.1 ISR order: WakeupThread **before** flag set (possible lost-wake)

ISR loop at `0x2370A0` for each live table slot:

```
2370F0  jal  WakeupThread     ; a0 = table[s0] (tid) in delay slot
2370F8  v0 = flag_base
237100  v0 += s0
237104  b    continue_loop
237108  sb   1, 0(v0)         ; SET flag in delay slot of branch
```

**Wakeup first, set flag second.** If the waiter is scheduled and re-enters `lbu`/`beq` **before** `sb 1` commits, it samples 0 and `SleepThread`s again — a classic lost-wakeup window. That would produce many ISR SETs (and Wakeups) with few poll-successes, matching slots 1/2 (98 set / 3 success) under scheduling pressure. Slot0/3 may win the race more often (65/98 success).

**Not yet proven** (needs ordered SET vs READ vs SleepThread chronology on one slot). Hypothesis only; dual-ACK before any Core reorder.

```text
ISR race hypothesis (not Core)
  WakeupThread(tid) then sb flag=1
  waiter can poll 0 and sleep again before set lands
  fits 98 sets / 3 successes if lost-wake is frequent for slots1/2
  next: ordered log SET vs poll READ vs SleepThread for slot2
```

---

## 17. A/B: `DETPS2_RR_SCHED=1` equalizes poll-success (Grok, measure-only)

Existing env forces circular RR in `FindNextRunnable` (no new Core). Same PCBREAK
`0x2371B4` success census, late window `cyc≥25M`:

| s1 | PRIO late success (baseline) | **RR late success** |
|---:|-----------------------------:|--------------------:|
| 0 | 65 | 59 |
| 1 | **3** | **45** |
| 2 | **3** | **50** |
| 3 | 98 | 16 |

Slots 1/2 leave the 3-success cliff under RR. Product metrics also move:

| Metric | PRIO baseline | RR |
|--------|--------------:|---:|
| px | 877 187 | **9 752 122** |
| prims | 172 | **23 639** |
| gifP3 | 20 | **200** |
| imgBytes | 65 728 | **1 084 512** |
| lit / dispfbPx | 0 | still **0** |

**Still black present** (no DISPFB flip), but the VBlank-waiter imbalance is **causally
linked** to priority tie-break order: fair RR among runnables restores slots1/2 wait
completions and unlocks ~11× prims / more Path3. Complements Claude’s pick-count
starvation measure (`f5caae9`). **No permanent Core** — env A/B only. Dual-ACK + design
before defaulting RR or changing tie-break (fleet impact: Midway already uses
`PreferRoundRobinSched`; B3 does not).

```text
RR A/B (existing DETPS2_RR_SCHED)
  late success 65/3/3/98 -> 59/45/50/16
  px 877k -> 9.7M prims 172 -> 23k; still lit=0 dispfb=0
  scheduler tie-break is a real lever for B3 waiters; not a full flip fix
  dual-ACK design before any default Core sched change
```

---

## 18. Page 0x46 under RR: numerically "varied" but visually noise, not real content (Claude)

Followed up §17 directly: if RR unlocks 11x more `prims`/`px`, does the real draw target
(page `0x46`, per §14 of the parent doc — same page confirmed 100% uniform black under
priority scheduling) now show real content? Added a temp `--dump-gs-page=FBP:W:H[:path]`
flag to `blocker-trace` (`Program.cs`, gated, fully reverted after — `git diff --stat`
empty) reusing the existing `Gs.ReadLocalMem` accessor, read-only, bypasses present/composite
entirely — same method as the original page-0x46 dump (`b7048b1`).

### 18.1 Numeric result (looked promising)

`DETPS2_RR_SCHED=1`, same 50M run as §17: `nonZeroRgbPixels=6283/286720
distinctColors(cap20000)=4087` — a dramatic change from the baseline's "1 distinct color,
100% black."

### 18.2 Visual result (the numbers were misleading)

Converted the dump to PNG and looked at it directly, per this project's own repeated lesson
tonight (and historically — `3bcedb2`'s stripe noise, Dec's pre-coherence-check RGB static)
that numeric variety alone does not prove real content. **The image is a thin horizontal
band of high-frequency, randomly-colored speckle noise** — red/green/blue/white static
concentrated in a narrow strip near the top of the frame, not a coherent scene, sprite, or
UI element. This is the same *class* of fabricated-looking noise this session has explicitly
banned and rejected twice already (visually, not just by pattern-matching the description).

### 18.3 Corrected conclusion

**Retracting the implied "RR unlocks real visible content" reading.** RR scheduling
genuinely unlocks more real GS *activity* (§17's px/prims/imgBytes numbers are real —
independently reproduced via the actual product CLI, not fabricated), but what lands at
page `0x46` under this specific dump is not recognizable graphics. Plausible explanations,
none confirmed: (a) this is genuine but still-early/partial rasterization — real triangles
being drawn with wrong/uninitialized vertex or color data because some other prerequisite
(the still-never-flipped DISPFB pipeline, or data the still-mostly-inert PATH3 hold would
have supplied) hasn't run yet; (b) the dump is reading a boundary/stride mismatch — my
640-width assumption may not match whatever real FBW the game is now using under the
RR-unlocked code path, so this could be reading a genuinely different, unrelated memory
region as if it were image data; (c) real but format-shaped noise from a partially-completed
z-buffer or stencil operation, not a color buffer at all.

### 18.4 Not proposing anything

No Core, no claim of progress toward a visible frame from this specific sub-result. §17's
throughput numbers stand as real; this page-46 visual does not support "graphics are close."
`FRAME_1=0xA0046` is identical between the baseline and RR runs (confirmed in both traces'
output), so (b) a changed real FBW is unlikely — the register itself didn't move. Leans
more toward (a) partial/early rasterization or (c) non-color-buffer data than a stride
mismatch, but none of the three is confirmed.

```text
Page-0x46 under RR (Claude) -- visual correction
  numeric result looked like real content: 4087 distinct colors, 6283 nonzero pixels
  VISUAL result: thin band of high-frequency random-color noise, not real graphics
  same fabricated-looking-noise class already banned twice tonight (3bcedb2, Dec pre-fix)
  retracts "RR unlocks real content" -- throughput numbers (px/prims) still stand as real
  open: is FBW=640 even still correct under the RR-unlocked code path -- check before re-dump
```
```

### 18.5 Grok dual-check RR product mix (no page dump)

`DETPS2_RR_SCHED=1` 50M host-present (product CLI, not custom harness):

| | PRIO baseline | RR |
|--|--------------:|---:|
| heldP3n / m3p | 5 / True | **0 / False** |
| mskPath3 | 10 | **102** |
| PRIM / XYZ2 (softgs-writes) | small | **65271 / 45866** |
| gif image tags | 4 | **66** |
| abortTrunc | 6 | 67 |
| lit / dispfbPx | 0 | 0 |

**PATH3 hold clears under RR** — game issues many natural mask/unmask cycles (mskPath3=102)
and drains held queue. Throughput + natural PATH3 unmask both track scheduler fairness.
Visual noise on page 0x46 (Claude §18) still means this is not “menu/scene ready,” but
the black present is no longer explained by stuck M3P alone when RR is on.

---

## 19. Soft-GS under RR: real geo flood, depth-reject dominated (Grok)

Seat: softgs prim/XYZ/frag under `DETPS2_RR_SCHED=1` (Claude owns page-byte noise decode).

### 19.1 Product CLI 50M RR (host-present)

| Metric | Value |
|--------|------:|
| PRIM / XYZ2 writes | 65 271 / 45 866 |
| px / prims (claim) | 9 752 122 / 23 639 |
| fragTest / rejDepth / rejAlpha | 31 188 876 / **21 436 754** / 885 827 |
| imgBytes / image tags | 1 084 512 / 66 |
| path3 qws / held / mskPath3 | ~10.8M / 0 / 102 |
| gif abortTrunc / lastAbort | 67 / new-DIRECT |
| gif-last | mid-packet nloop=29184 progress=4817 |
| TEST | `0x5140B` (ZTE on) |
| lit / dispfbPx | 0 |

Depth reject is **~69% of fragments**. So most of the “geo flood” never becomes color pixels — consistent with a **sparse noise band** on page 0x46 (Claude visual) while claim px is still multi-million (many fragments pass some tests or write elsewhere before reject accounting).

### 19.2 vs PRIO baseline (same 50M)

| | PRIO | RR |
|--|-----:|---:|
| prims | 172 | 23 639 |
| heldP3 | 5 | 0 |
| fragTest | ~2.8M | ~31M |
| rejDepth | ~1.9M | ~21M |

RR multiplies work ~10×; reject mix stays depth-heavy. Not inventing a Core depth fix from this alone.

### 19.3 Hand-off

Noise-band **structure** (stride / garbage color vs wrong memory) stays Claude’s dump seat. This seat: **commands are real PRIM/XYZ2 at scale; depth test kills most fragments; PATH3 unmasked; present still DISPFB≠FRAME.**

```text
Soft-GS RR mix
  PRIM/XYZ flood real; ~69% frag depth-reject
  PATH3 drained; still black present (DISPFB page0 vs FRAME 0x46)
  noise band = thin survivors of reject storm, not full FB scene
```

---

## 20. Noise-band structure: isolated single-pixel survivors, not coherent fills (Claude)

Took the handed-off structural question. Temp analysis (`Tests/TempNoiseAnalyze.cs`, gated,
fully reverted — `git status` clean) reading the already-dumped `b3-page46-rr.ppm` directly,
no new run needed.

### 20.1 Result

- **Nonzero row range: [117, 128]** — an 11-row band out of 448 (~2.5% of frame height),
  matching the visual location exactly.
- **5,734 same-color runs, 6,283 total nonzero pixels, average run length 1.10.**
  Run-length histogram: **5,402 of 5,734 runs (94%) are a single isolated pixel** — only 231
  runs of length 2, dropping off fast (59 of length 4, 26 of length 3, a handful longer).

### 20.2 Interpretation

An average run length of 1.10 is **not** what coherent triangle rasterization looks like,
even a mostly-successful one — real scanline-coherent fills produce runs of many adjacent
pixels sharing a triangle's interpolated color. What this pattern **does** match, combined
with Grok's §19 finding (`rejDepth` ≈ 69% of all fragment tests): **many overlapping/
z-fighting triangles competing for the same thin band of screen space**, where the depth
test's per-pixel winner effectively looks random at this scale — a real, if currently
degenerate, rendering outcome, not a memory-format misread. Confined to an 11-row band out
of 448 is consistent with (not proof of) a camera/projection setup that hasn't reached its
real final state yet, collapsing 3D geometry into a near-flat sliver.

### 20.3 Still not established

Whether this specific hypothesis (badly-conditioned/incomplete camera transform) is correct,
versus some other explanation for why real geometry commands land in such a narrow vertical
band. Would need real vertex/transform data inspection (VU1 output, not just fragment
counts) to confirm — a further step, not attempted here.

### 20.4 Combined conclusion (with §19)

Real command flood (PRIM/XYZ2) + heavy depth-reject + isolated-pixel survivor pattern in a
narrow band, together, are consistent with **real but incomplete/degenerate rendering**
(most likely a transform/projection issue collapsing geometry into a thin strip) rather than
either "finished frame" or "reading unrelated memory as pixels." Not proposing any Core
change — this needs real transform-data inspection before any fix hypothesis, and is squarely
still "no Core until dual-ACK on a design," same as every other B3 finding tonight.

```text
Noise-band structure (Claude)
  rows [117,128] only (11 of 448) -- matches visual band location exactly
  94% of runs are single isolated pixels, avg run length 1.10
  NOT coherent triangle fills -- consistent with many z-fighting triangles in a thin band
  combined with rejDepth~69% (Grok): real but incomplete/degenerate rendering, not garbage memory
  open: is this a collapsed/incomplete camera transform -- needs real vertex data, not attempted
```

---

## 21. DISPFB flip under RR: still stuck (Grok)

With `DETPS2_RR_SCHED=1` 50M (product CLI), privileged circuit end-state is unchanged vs PRIO:

| Reg | Value |
|-----|-------|
| FRAME_1 | `0xA0046` (draw page 0x46) |
| DISPFB1 | `0` |
| DISPFB2 | `0x51400` (display page 0, PSMCT16S) |
| naturalDispfbPx / lit | 0 |

RR unlocks geo + PATH3 unmask + waiter fairness but **does not** cause the game to re-point
DISPFB at 0x46 in this window. Parent `gfx-l2c-b3-frame-dispfb-stall-finding.md` still holds
under high-throughput RR. Flip remains a separate open dig (who writes DISPFB late / what gate).

```text
RR does not fix DISPFB stall
  FRAME 0x46 / DISPFB2 page0 still
  next: DISPFB write timeline under RR (temp) or EE code that programs DISPFB
```

### 21.1 DISPFB write log under RR (temp, reverted)

`DETPS2_TEMP_B3_DISPFB=1` + `DETPS2_RR_SCHED=1` 50M. All privileged DISPFB1/2/PMODE writes logged.

- **DISPFB2** only ever `raw=0x51400` → fbp=**0**, psm=0xA (PSMCT16S). Never fbp=0x46.
- **DISPFB1** only ever `0`.
- **PMODE** sticky `0x66` after early setup (plus a few dual-half write artifacts at cyc=0).
- No write in the log selects draw page 0x46 for display.

Confirms: under high-throughput RR the game still **never programs a DISPFB flip** to the FRAME target in 50M. Stall is not “waiting for more frames of geo.”

```text
DISPFB write census RR 50M
  only page0 / 0x51400 + DISPFB1=0
  zero writes with fbp=0x46
  flip code path not reached (or not writing privileged DISPFB)
```

---

## 22. Flip-helper EE location + runtime PC census (Grok)

**Method (no Core):** static disassembly of `out/SLUS_210.50` for GS privileged address
construction + `blocker-trace --pcbreak=ADDR --cycles=30000000 --host-present` hit counts
on tip **e529238** (S1 product, no RR). Temp instrumentation not required — product
`--pcbreak` only. Canaries under `out/canaries/b3-flip-pc/` (gitignored).

### 22.1 Static map

| Site | VA | Role |
|------|-----|------|
| PutDispEnv-like | `0x001029B0` | **Sole** DISPFB2 writer path: `ld val, +0x10(env); sd val, DISPFB2`. Circuit1 writes DISPFB1. |
| DISPFB2 `sd` | `0x00102A48` | Circuit-2 store (after mode check via global halfword at `0x00483F20+6`). |
| Early put wrapper | `0x00103B68` | Init path; `jal PutDispEnv` then optional draw-env put. |
| **Flip ISR** | `0x001F1CE8` | VBlank handler: gated PutDispEnv + optional DISPFB1 direct writes. |
| Register ISR | `0x001F3C08` | `AddIntcHandler(cause=2 /*VBLANK_START*/, handler=0x001F1CE8, arg=0)` via syscall 16 stub `0x0010BB00`. Also registers DMAC handlers. |
| Flip-ready **set** | `0x001F1BF4`, `0x001F1C0C` | `sb s6, gp-0xA15F` — only non-zero writers of the ISR gate flag. |
| Flip-ready **clear** | `0x001F1D4C` | ISR clears `gp-0xA15F` immediately before PutDispEnv. |

Call graph:

```text
AddIntcHandler(VBLANK_START) @ 0x1F3C08
        |
        v
  ISR 0x1F1CE8  (every VBlank once registered)
        |  if (gp-0xA15F != 0)  // flip-ready
        |    clear flag; jal PutDispEnv(env*)
        v
  PutDispEnv 0x1029B0
        |
        v
  DISPFB2 = env->dispfb   // observed always 0x51400 (fbp=0)
```

**Zero** other `lui 0x1200` + `ori 0x90` DISPFB2 builders in the ELF — all privileged
display rebinds go through PutDispEnv.

### 22.2 Runtime PC census (30M, host-present, S1 default)

| PC | Hits | Notes |
|----|------|-------|
| `0x001F3C08` register | **1** | Handler installed once mid-boot. |
| `0x001F1CE8` flip ISR | **48** | First hit cyc≈14.5M; all `ra=0x80000200` (kernel INTC return). a0=2 (VBLANK_START). |
| `0x001F1BF4` set-flag A | **1** | |
| `0x001F1C0C` set-flag B | **2** | |
| `0x001029B0` PutDispEnv | **4** | ra: `0x1F1D8C`×3 (ISR), `0x103B90`×1 (early). |
| `0x00102A48` DISPFB2 sd | **4** | **v0=0x51400 every time** (fbp=0, psm=0xA). Never fbp=0x46. |

End-state (unchanged): `FRAME_1=0xA0046`, `DISPFB2=0x51400`, `px=877187`,
`m3p=True heldP3qwc=2124`, present black.

### 22.3 What this establishes

1. **Flip machinery is live, not missing.** VBlank ISR is registered and fires (~48 times
   in 30M). This is not an AddIntcHandler / VBlank-delivery gap for this handler.
2. **PutDispEnv is heavily gated.** ISR runs 48× but only enters PutDispEnv 3× — exactly
   matching the 3 flip-ready flag sets. 45/48 VBlanks find the flag already clear and skip.
3. **When PutDispEnv does run, it re-programs page 0** (`0x51400`), never draw page `0x46`.
   So even the few completed "flip" requests advertise the wrong (empty) display target.
4. **Prior census ("never fbp=0x46") was correct** and is now explained: the only writer
   exists, runs rarely, and always stores the init value.

### 22.4 What this does NOT establish (open, no Core)

- **Why flip-ready is set only 3×** — the set sites sit in a large path/completion handler
  around `0x001F1Axx` (DMAC/GIF status polling flavor). Likely tied to draw-path completion
  that rarely finishes under the PATH3 hold / plateau (same 877k px ceiling as pre-S1).
- **Why env→DISPFB field stays 0x51400** — who *should* write `env+0x10` with fbp=0x46 (or
  the double-buffer sibling) before setting flip-ready. Separate dig: stores into the live
  env blobs seen at PutDispEnv a0 (`0x6754C0` / `0x675810` / `0x675838`).
- **RR interaction** — under RR, geo + PATH3 drain improve but prior DISPFB census still
  never fbp=0x46. Re-measure set-flag/PutDispEnv counts under `DETPS2_RR_SCHED=1` would
  show whether more completions raise flip-ready without fixing the fbp field.

### 22.5 Non-goals (still banned)

- Inventing a synthetic DISPFB flip or present of page `0x46`.
- Forcing flip-ready from assist without a dual-ACK'd design.

```text
Flip dig (Grok)
  sole DISPFB2 writer = PutDispEnv 0x1029B0
  VBlank ISR 0x1F1CE8 registered + fires (48/30M)
  PutDispEnv only 4x (1 init + 3 gated); always stores 0x51400
  gate = gp-0xA15F set only 3x at 0x1F1BF4/0x1F1C0C
  next: who writes env.dispfb / why set-flag so rare (PATH3 plateau link?)
  no Core
```

### 22.6 env.dispfb field is sticky-init (Grok follow-up)

`--watch` on the three PutDispEnv env bases' `+0x10` DISPFB fields over 30M:

| Addr (env+0x10) | Writes of DISPFB value | Writer PC |
|-----------------|------------------------|-----------|
| `0x6754D0` (env0) | **1×** `0x51400` (plus early zero clear) | `0x0010273C` `sd v1/r3, 16(s3)` inside SetDefDispEnv-like `0x00102638` |
| `0x675820` (env1) | **1×** `0x51400` (byte-wise sdl/sdr) | `0x001FDFB8` game init copy |
| `0x675848` (env2) | **1×** `0x51400` | `0x001FE008` game init copy |

**Zero** subsequent stores change fbp away from 0. So PutDispEnv's 4 runs are not "failing to flip" —
they faithfully re-bind a **struct that was never updated** after boot-time SetDefDispEnv.

`0x00102638` is called from game init at `0x001FD994` / `0x001FD9C4` only (static jal census).
There is also a generic `sd r2, 16(s0)` at `0x00102C10` (SetDispEnv-shaped) but it does not
appear among the watch hits on these live envs in the 30M window.

```text
env.dispfb sticky-init
  written once to 0x51400 at SetDefDispEnv / game copy
  never rewritten with fbp=0x46 (or any other page)
  PutDispEnv correctly re-applies stale page0
  next: who *should* update env.dispfb (or L2L blit 0x46→page0) before set-flag
```

---

## 23. L2L / blit-path reachability (Grok, split seat)

**Working model (dual-ACK with Claude, not Core):** DISPFB sticky page0 may be correct by
design (PSMCT16S present). Real draw at FRAME `0x46`. Missing piece = local→local
downconvert (or equivalent) never executes. Claude: zero L2L all night; set-flag cluster
only ~15.17–15.50M then silence.

### 23.1 Static

| Item | Finding |
|------|---------|
| DISPFB fbp=0x46 constants (`0x1446` / `0x51446`) | **Zero** in ELF — game never packs "display page 0x46" |
| libgraph `SetDefLoadImage`-like `0x001031B8` | Builds AD regs BITBLTBUF/TRXPOS/TRXREG/TRXDIR (`0x50..0x53`). Callers `0x1FB2B8`, `0x1FB514` |
| `sceGsExecStoreImage` / SyncPath error strings | Present (libgraph), StoreImage path exists |
| Data words looking like `TRXDIR=2` AD at `0x4CB708`/`0x4CBA28` | Embedded in UI/resource tables with float mesh ptrs — **not** a live FB blit path |

### 23.2 Runtime 30M S1 host-present

| PC | Hits | Notes |
|----|------|-------|
| `0x001031B8` SetDefLoadImage | **0** | Standard load-image helper never entered |
| `0x001FB2B8` its caller | **0** | |
| `0x0021A304` `addiu r5,r0,0x53` (TRXDIR reg-id into packet buf) | **1** | **cyc=14429376**, `a1=0x46`, `ra=0x0019EE64` |

Sole hit sits in the same early window as flip-ready (≈14.4–15.5M). GPR snapshot:
`a1=0x46` (draw page), large BITBLT-shaped immediates in a0/a2/t0, writing reg-id
sequence into a packet buffer under `0x68xxxx`. **Builder runs once then never again.**

Combined with zero L2L BITBLT events for the whole night: either the packet is built but
**never DMA'd/submitted** to GIF, or TRXDIR.XDIR is not 2 / path aborts before
`BeginTrxFromDir`. Soft-GS L2L also same-PSM-only (`RunLocalToLocalBlit`); cross-PSM
32→16S would still need a real TRXDIR=2 arrival first (never observed).

### 23.3 Convergence with Claude stall seat

```text
~14.4M  TRXDIR packet builder once (a1=0x46)
~15.2M  flip-ready set ×3 + PutDispEnv ×3 (still env.dispfb=0x51400)
~15.5M  set-flag / blit-arm activity ends
  later  VBlank ISR keeps firing; no more set-flag; no L2L; px plateau
```

Same stalled control flow after ~15.5M starves both flip-ready re-arm and any further
blit/submit. Claude's main-thread heatmap after 15.5M should name the wait; this seat
says the **blit/load-image arming code is reachable once then abandoned**, not missing
from the binary.

### 23.4 Next (this seat)

1. Disasm `0x0021A284` / caller `0x0019EE64` — confirm XDIR=2 vs 0/1 and submit path.
2. Optional: GIF/DMAC submit pcbreak on that packet buffer.
3. Still ban: invent L2L, present page 0x46, force flip-ready.

```text
Blit dig
  no DISPFB fbp=0x46 packing in ELF (page0 present likely by design)
  libgraph LoadImage helper 0 hits / 30M
  custom TRXDIR packet builder 0x21A304 hits 1× @14.4M with a1=0x46 then never
  zero L2L all night still holds
  next: submit path after builder; dual-read Claude post-15.5M heatmap
```

---

## 24. Post-15.5M PC heatmap: real ongoing simulation, not obviously the stall itself (Claude)

Profiled `[15.6M, 25.6M)` (temp `PcProfiler`-based tool, gated, fully reverted — `git status`
clean; replicated blocker-trace's `--host-present` 1M-cycle `OnHostPresent` slicing exactly,
since an earlier attempt without it silently missed B3's quirk-driven activity).

### 24.1 Result

Dominant hotspot is **not** the earlier-characterized VBlank-poll loop (`0x237xxx`) — a
different region entirely:

- `0x00123E10-0x00123E9C`: a standard PS2 SIMD-shaped `memcmp` (quadword `lq`/`pxor`/
  `pcpyud` fast path, byte-wise `lbu` tail).
- `0x00293A60-0x00293AD8`: walks a linked list from a global head (`gp-23416`), calling the
  memcmp wrapper per node — a real hash-bucket/name-lookup pattern.
- `0x00293F80-0x00294008`: a nested loop calling the same memcmp wrapper — outer bound `s6`,
  inner bound `s7`. **Both confirmed via `--pcbreak` register dump: `s6=s7=0x12=18`** — max
  324 total comparisons, far too small to be a genuinely slow O(n²) computation on its own.

### 24.2 Interpretation

This reads as **real, ongoing, bounded simulation work** (plausibly collision/object matching
among a small set of ~18 nearby items, called very frequently — once per game-logic tick) —
not an infinite or runaway loop. It's evidence the game is genuinely alive and doing
legitimate per-frame processing in this window, which is a mildly positive sign, but it
doesn't by itself explain why the flip/blit machinery (§22-23) never re-arms after ~15.5M.

### 24.3 Reframing with §23

Given §23's blit-builder fires exactly once at cyc≈14.4M and this doc's flag-cluster is
15.17M-15.50M, the actually decisive window is likely **narrower and earlier** than what was
profiled here (14.4M-15.5M, the gap between blit-build and flag-cluster) rather than 15.6M+
which is probably already past the critical moment — this section's finding is honest
background context, not the answer.

```text
Post-15.5M heatmap (Claude)
  NOT the VBlank-poll loop -- different hotspot: memcmp + linked-list lookup + small
    nested search (bounds 18x18, confirmed via pcbreak -- not a slow O(n^2), too small)
  reads as real ongoing simulation (collision/object matching), not obviously the stall
  real decisive window is narrower: 14.4M-15.5M (blit-build to flag-cluster gap), not this one
```

### 23.5 Submit path: built, never DMA'd (Grok)

Call chain all 1× @ cyc=**14429376**:

```text
0x228328 → 0x19EE40(a0=0x665EC0) → 0x21A290 builder
                                  → 0x365880 stub
                                  → 0x251840 ×3 (object float init, not DMA)
```

| Probe | Result |
|-------|--------|
| `--watch=67CDD0` (TRXDIR reg-id slot) | 1 write `0x53` @ `0x21A318`; **0 later EE reads** |
| `--find-transfer=67C000:4000` | **no transfer touched this range** (214 total transfer events logged) |
| `--watch=665EC0` (object) | few post-build EE reads (`0x19E8EC`, `0x2271B4`×4) — object still touched; packet slot not |

**Verdict:** the one-shot "TRXDIR-shaped" build is **not submitted** via DMAC/GIF. Zero L2L is explained by non-submit, not by Soft-GS rejecting a real L2L. Whether this buffer is even a GIF packet vs an internal Midway command list still open — either way nothing bulk-moves it after build.

```text
builder once @14.4M → write 0x53 to 0x67CDD0 → never read, never DMA
object 0x665EC0 still lightly touched later
next: who should walk/submit (starved after 15.5M?); Claude 14.4-15.5M heatmap
```

### 23.6 Template field consumers: TRXDIR path dead, siblings live

Static readers of the builder's `0x68xxxx` table (lui 0x68 + large negative offs):

| Slot | Addr | Reader site | Fn entry | 30M hits |
|------|------|-------------|----------|----------|
| first field `-12864` | `0x67CDC0` | `0x0021990C` | `0x00219830` | **28** |
| mid field `-12856` | `0x67CDC8` | `0x00219668` | `0x00219530` | (live callers exist) |
| **TRXDIR `-12848`** | `0x67CDD0` | `0x00219298` | `0x00219150` | **0** entry; caller `0x1A7750` also **0** |

So the global GS-template table **is** read for some fields (28×), but the **TRXDIR-specific consumer is never entered**. That is sharper than "buffer never touched": siblings consume; transfer-arm path is dead code in this window.

Matches zero L2L / zero DMA of the range: nothing walks the TRXDIR slot into a GIF/DMAC submit.

```text
template table partially live (28× non-TRX fields)
TRXDIR consumer 0x219150 / caller 0x1A7750: never entered
next: what dispatches to 0x1A7750 vs live sibling callers (type gate?)
```

### 23.7 Dead TRXDIR path sits in unreferenced mega-fn

Enclosing function of the only `jal 0x219150` (TRXDIR consumer):

| Item | Value |
|------|-------|
| Entry | `0x001A6290` (`addiu sp, -3472`) |
| Contains | loop @ `0x1A6F00`…`0x1A7770` with `jal 0x219150` @ `0x1A7750` |
| Static `jal`/`j`/data ptrs to entry | **none found** |
| Runtime entry hits | (implied 0; call site 0x1A7750 already 0) |

Contrast: live field copiers are `jal`'d from many sites including `0x19DFxx` (same object-init neighborhood as the 14.4M builder chain).

**Interpretation:** TRXDIR arming is not merely type-gated inside a live frame loop — the **whole consumer pipeline for that slot lives in a function with no static callers** and zero dynamic hits. Either:
1. intended to be registered via a function-pointer table we have not found, and never registered; or
2. leftover / alternate render path not wired for this boot path.

Either way: builder fills TRXDIR template once; nothing in the live graph reads it into a submit.

```text
0x1A6290 mega-fn: no static callers, contains only TRXDIR consumer jal
live siblings called from 0x19DFxx / many others
TRXDIR path structurally unreachable in this binary wiring
```

---

## 25. 14.4M-15.5M heatmap: real bounded work, not a hang — plus a refuted pad-input test (Claude)

### 25.1 Narrow-window heatmap

Profiled `[14.4M, 15.6M)` (temp `PcProfiler` tool, gated, fully reverted — the exact gap
between §23's blit-builder firing once and this doc's flag-cluster start). Dominant hotspots,
all disassembled directly:

- `0x0012409C-0x001240B0`: standard PS2 SIMD `memset` (128-bit `sq` fast path), called from
  `0x00167C04` with `a1=0` (zero-fill), `a2=20320` bytes (~20 KB).
- `0x00167BC0-0x00167C3C` (the caller): a real, bounded init routine — loops **exactly 254
  times** (`slti v0,s0,254`) calling a 128-byte-stride per-item initializer, then the 20 KB
  memset, then loops **254 more times** calling a 64-byte-stride per-item initializer at the
  same base the memset just cleared. Reads as legitimate object/particle-pool
  (re)initialization — 254 is a specific, finite bound, not runaway.
- `0x0010C680-0x0010C704`: real R5900 cache-management code (`sync`/`cache`/`mfc0 $c0_28`
  TagLo reads) — a standard "invalidate this memory range from cache" routine, iterating a
  4096-byte range in 64-byte (cache-line) steps.

**None of this is a hang or spin.** It's real, bounded, purposeful low-level work (pool init,
memset, cache flush) that completes in this window — refutes "the stall is literally inside
14.4-15.5M." The setup work here finishes normally; whatever prevents further progress must
be about what does or doesn't happen *after* this point, consistent with Grok's §23-§24
finding that the actual consumer pipeline is structurally unreachable rather than merely
slow.

### 25.2 Pad-input test — refuted

Given the above, tried a cheap, different hypothesis: is the game legitimately parked on a
menu/attract screen waiting for real player input our automated harness never provides
(matching this project's own established need for `--pad-script` on interactive titles)?
Built a pad-script pressing `Start`/`Cross` at cyc 16M/17M/18M/20M (product `--pad-script=`,
confirmed applied: "4 event(s)... applied 8 press/release action(s)"), re-ran the full 50M
trace.

**Result: `px=877187`, byte-identical to the no-input baseline.** Simulated Start/Cross
presses at these specific times produce zero observable effect. Doesn't rule out a different
button, different timing, or a held-vs-tapped distinction, but refutes the simple "just
needs a Start press" version of this hypothesis.

### 25.3 Combined read

Setup work in the critical window is real and completes; simulated player input doesn't
unstick anything; Grok's static analysis independently shows the actual blit-consumer
pipeline has no static callers anywhere in the binary. Converging picture: this isn't a
timing race or a missing player action — it's structurally disconnected code, consistent
with Grok's own two hypotheses (unregistered via an undiscovered fptr table, or a genuinely
unused alternate render path for this boot configuration).

```text
14.4-15.5M heatmap + pad test (Claude)
  real bounded work in the gap window (254-item pool init x2, 20KB memset, cache flush) --
    not a hang, refutes "stuck inside this window"
  pad-input test (Start/Cross @ 16-20M): REFUTED, px identical to baseline
  combined with Grok's static findings: structurally disconnected code, not a timing/input gap
```

---

## 25. Flip-ready re-arm: DMAC handler dies after setup (Grok)

**Seat:** why flip-ready only 3× then never re-arms post-15.5M (Claude: 14.4–15.5M is real setup, not hang).

### 25.1 Static

Set-flag sites `0x1F1BF4` / `0x1F1C0C` live inside **`0x001F1778`** (frame −288).

That entry is registered at boot (`0x1F3C08`) as **AddDmacHandler** (alongside VBlank ISR `0x1F1CE8`):

```text
AddIntcHandler(VBLANK_START, 0x1F1CE8)
AddDmacHandler(ch=1, 0x1F1778)   # and ch=2 variant nearby
```

So flip-ready is **DMA-completion-driven**, not main-thread polling.

### 25.2 Runtime 30M S1

| PC | Hits | Cycle window |
|----|------|----------------|
| `0x001F1778` DMAC handler entry | **13** | **15,167,216 – 15,750,256 only** |
| set-flag path (range break) | **3 events** | 15,171,952 / 15,252,752 / 15,502,752 |

Handler a0 mix: `2`×6, `-1`×4, `1`×3 (channel / sentinel style).

**After ~15.75M: zero further entries to `0x1F1778`.** Not merely "set-flag branch skipped" — the **handler is never invoked again**. VBlank ISR continues (48× through 30M); DMA-completion path does not.

### 25.3 Reading

```text
14.4M   TRXDIR template builder once
15.17–15.75M  DMAC handler 13×; set-flag 3×; PutDispEnv 3×
15.75M+  DMAC handler silent; flip-ready never re-armed; px plateau; PATH3 held (S1)
```

Setup completes (Claude heatmap). Ongoing frame production would need repeated DMA completions into this handler. Those stop. Aligns with gif/px plateau and structural blit-consumer disconnect — even if fptr wire for `0x1A6290` were fixed, **re-arm never gets fresh completion events** under current S1 run shape.

Optional next: which DMAC channel(s) fire the 13 handler entries; why channel activity ends (PATH3/M3P hold vs CHCR). No Core.

```text
re-arm dig
  set-flag inside AddDmacHandler 0x1F1778
  handler 13 hits all in 15.17-15.75M then permanent silence
  VBlank keeps firing; DMA-completion path does not
  flip-ready cannot re-arm without those completions
```

### 25.4 Channel map + RR A/B

Registration (`0x1F3C40`…): **AddDmacHandler(ch=1)** and **AddDmacHandler(ch=2)** both point at `0x1F1778`.

PS2 DMAC: ch1=**VIF1**, ch2=**GIF**. Handler a0 mix (`1` / `2` / `-1`) matches.

| Config | Handler hits | First–last cyc | End px (30M) |
|--------|--------------|----------------|--------------|
| S1 product | 13 | 15.17M – **15.75M** | 877187, m3p held |
| `DETPS2_RR_SCHED=1` | 14 | 15.65M – **22.2M** | 877187, m3p held (30M still plateau) |

RR stretches the last handler fire later (~22M) but does **not** produce ongoing per-frame completions in this 30M window (still ~14 total). Re-arm remains starved; not fixed by fair/RR scheduling alone at this budget.

```text
DMAC handler = VIF1+GIF completions
~14 fires clustered in setup; RR spreads slightly later, not continuous
re-arm needs sustained GIF/VIF1 completion stream
```

---

## 26. DMAC completion-interrupt logic is unconditional (Claude) — refutes an HLE-hold hypothesis, redirects to game-side issuance

Independently confirmed the same channel registration (`DETPS2_TRACE_HANDLERS=1`:
`[ADDDMAC] channel=1 handler=0x1F1778` / `channel=2 handler=0x1F1778` — VIF1 and GIF,
matching Grok's finding exactly). Before accepting "GIF completions stop because PATH3 is
held" as the mechanism, checked whether our own HLE might be *suppressing* the DMAC
completion interrupt for held Path3 transfers (a real, plausible-looking Core bug candidate,
similar in shape to tonight's earlier findings).

**Read `Dmac.cs`'s `Step`/`DeliverSegment`/`FinishChannel` directly:** `FinishChannel` (which
calls `RaiseDmacIrq`) is invoked **unconditionally** right after `DeliverSegment` whenever a
channel's `QWC` reaches 0, regardless of what `DeliverSegment` did internally — including the
GIF case, where `DeliverSegment` calls `Gif.ReceivePath3Data`, which internally holds the
data (`EnqueueHeldPath3`) when Path3 is masked. **The DMAC layer doesn't know or care whether
GIF held or processed the payload — it raises its own completion interrupt either way**,
correctly matching real hardware semantics (DMAC completion = "I finished moving these
bytes," not "the downstream consumer finished processing them").

**This refutes the hypothesis that HLE is suppressing completion interrupts for held
transfers.** The interrupt-raising code is correct. So the reason handler `0x1F1778` stops
being entered after ~15.75M isn't a missing/suppressed interrupt for existing work — it's
that **the DMAC channels (VIF1/GIF) simply have no new segments to complete**, i.e. the game
stops *issuing new* DMA kicks on these channels at all. Consistent with everything else
found tonight: PATH3's own held-queue pattern shows the same shape (three real batches, the
third left held forever, no fourth batch ever submitted).

### 26.1 Redirects the real question

Not "why doesn't the completion interrupt fire" (it would, if there were new work) — but
**"why does the game's own code stop submitting new VIF1/GIF DMA transfers after
~15.5-15.75M."** That's a game-logic-side question, the same shape as the MSKPATH3
write-site tracing already done successfully tonight (§9 of this doc) — find the real code
that issues new DMA kicks (writes `D2_CHCR`/`D2_QWC`/`D2_MADR` or the VIF1 equivalent with
the STR/start bit) and check whether/how often it's reached after the critical window,
mirroring the exact methodology that already worked for finding the MSKPATH3 builder.

```text
DMAC completion-interrupt check (Claude) -- refutes HLE-suppression hypothesis
  FinishChannel/RaiseDmacIrq fires unconditionally, regardless of GIF hold state
  interrupt-raising logic itself is correct, matches real hardware semantics
  real question redirects to: why does the GAME stop issuing new VIF1/GIF kicks
  next: find the real DMA-kick-issuing code (same method as the MSKPATH3 write-site trace)
```

---

## 27. fptr hunt (empty, strengthens dead-code read) + real DMA-kick trace pinpoints the exact re-arm gate (Claude)

### 27.1 fptr/indirect-call hunt for 0x1A6290 / 0x219150 — empty

Seat assigned by Grok (ACK seq0355): find a *computed* (not literal-word) construction of
either target address, since Grok's static census already found zero literal occurrences of
`0x001A6290` anywhere in the ELF (aligned or unaligned) and zero direct `jal`/`j`.

Built two temp `scanmasked` sweeps over the full code range (`0x00100000-0x00700000`):
- `lui $rt, 0x001A` (7 hits) — checked every one's next few instructions by hand for a
  matching `addiu`/`ori $rt,$rt,0x6290`. One coincidental hit: `0x001A53CC: addiu t0,t0,25232`
  (25232 = `0x6290`) does construct `t0 = 0x001A6290` exactly — but `t0` is **never read
  again** anywhere in that function (checked forward to `0x001A57C0`, well past the next `jr
  ra`). Dead/coincidental, not a call target.
- `lui $rt, 0x0021` (21 hits) and `lui $rt, 0x0022` (8 hits) for `0x00219150` (upper half
  needs `+1` adjustment for `addiu`-style construction, or direct `0x0021` for `ori`-style) —
  cross-referenced against a masked scan for `ori $rt,$rt,0x9150` (0 hits) and
  `addiu $rt,$rt,0xF150`/-3760 (8 hits, none adjacent to any of the 29 `lui` candidates).

**No live computed construction of either address found.** Combined with Grok's literal-word
and direct-call census, this is now three independent techniques (literal word scan, direct
`jal`/`j` scan, adjacent-instruction computed-address scan) all coming up empty for
`0x1A6290`. Strengthens the "genuinely unlinked/dead code" read over "we're failing to find a
live pointer." Not stopping here — see 27.2, which made the fptr question less central anyway.

### 27.2 Real DMA-kick trace (new tool, temp + reverted)

Rather than keep guessing at how kicks might be issued, instrumented the actual write site:
`Dmac.WriteRegister`'s CHCR case, right where `StartTransfer` fires on the STR bit (temp
`DETPS2_TRACE_DMAC_KICK=1`, wired a `(cyc, pc)` source into `Dmac` mirroring
`EE.SetCycleSource`'s existing pattern; also temp `DETPS2_DUMP_DMAC_STATE=1` printing
`IsActive`/`IsStalled` at end-of-run). Full revert confirmed via `git status`/`git diff
--stat` after use — see the exact trace below, this is the ground truth, not a guess:

```text
[DMACKICK] cyc=14335392 pc=0x00104118 ch=GIF  chcr=0x00000101 madr=0x00675E40 qwc=0x0D tadr=0x00000000
[DMACKICK] cyc=14338272 pc=0x001040A8 ch=VIF1 chcr=0x00000105 madr=0x00000000 qwc=0x00 tadr=0x004B1400
[DMACKICK] cyc=14340192 pc=0x00102DF8 ch=GIF  chcr=0x00000101 madr=0x00675510 qwc=0x11 tadr=0x00000000
[DMACKICK] cyc=15169584 pc=0x001F19F8 ch=GIF  chcr=0x00000104 madr=0x00675620 qwc=0x00 tadr=0x008D5C00
[DMACKICK] cyc=15169584 pc=0x001F1A4C ch=VIF1 chcr=0x00000145 madr=0x00000000 qwc=0x00 tadr=0x007FD100
[DMACKICK] cyc=15250192 pc=0x001F1F00 ch=GIF  chcr=0x00000104 madr=0x00000000 qwc=0x00 tadr=0x01D6EA00
[DMACKICK] cyc=15250384 pc=0x001F19F8 ch=GIF  chcr=0x00000104 madr=0x01D6ED90 qwc=0x00 tadr=0x009D5500
[DMACKICK] cyc=15250384 pc=0x001F1A4C ch=VIF1 chcr=0x00000145 madr=0x00000030 qwc=0x00 tadr=0x008FCA00
[DMACKICK] cyc=15500192 pc=0x001F1F00 ch=GIF  chcr=0x00000104 madr=0x00000000 qwc=0x00 tadr=0x01D6E280
[DMACKICK] cyc=15500384 pc=0x001F19F8 ch=GIF  chcr=0x00000104 madr=0x01D6E610 qwc=0x00 tadr=0x008D5C00
[DMACKICK] cyc=15500384 pc=0x001F1A4C ch=VIF1 chcr=0x00000145 madr=0x00000030 qwc=0x00 tadr=0x007FD100
[DMACKICK] cyc=15750192 pc=0x001F1F00 ch=GIF  chcr=0x00000104 madr=0x00000000 qwc=0x00 tadr=0x01D6E280
  [DMACSTATE] GIF active=False stalled=False VIF1 active=False stalled=False  (at cyc=30,000,000)
```

**Exactly 12 kicks total, all in the 14.3M-15.75M window, zero after — through 30M.** This is
the game's own code, directly confirmed at the MMIO write site (not inferred from completion
counts). Matches Grok's 13-14 handler-hit count almost exactly (kicks → completions →
handler entries, 1:1-ish).

**Critically: at cyc=30M both channels are `Active=False Stalled=False`.** Not stuck mid
transfer — the last kick's chain *did* finish cleanly. This rules out a DMAC-level hang or
deadlock as the mechanism. The handler ran its course and legitimately produced no further
kicks — this is a starved producer, not a stuck consumer.

### 27.3 The three re-arm kick sites, disassembled

All 9 of the post-setup kicks (cyc≥15.17M) come from just three fixed PCs inside handler
`0x1F1778`: `0x001F19F8`, `0x001F1A4C`, `0x001F1F00`. Disassembled `0x1F1980-0x1F1B00`
(`disasm burnout-only.json 20000000 001F1980:180`):

- Before each kick: polls **GIF_STAT** (`lui v1,0x1000; ori v1,v1,0x3020` = `0x10003020`,
  the real GIF_STAT MMIO address) spin-waiting on status bits, then reads a **queue cursor at
  `gp-24120`** (`lw v0,-24120(gp)`) that supplies the buffer/tag pointer for the next kick's
  `TADR`/`MADR`, then advances that cursor by 8 bytes and a companion counter byte at
  `gp-24128` by 2 (`addiu v0,v0,8; sw v0,-24120(gp)` / `addiu v1,v1,2; sb v1,-24128(gp)`) —
  a classic ring/queue-drain pattern, one descriptor entry consumed per kick.
- Register dump at `0x1F1A4C` via `--pcbreak=001F1A48:001F1A50` across the three real
  invocations shows `v0` (the value just read from `gp-24120`) = `0x7FD080`, `0x8FC980`,
  `0x7FD080` — real RDRAM buffer addresses, matching the `tadr` values seen in the kick trace
  almost exactly (offset by a small header). **The third invocation re-visits the same buffer
  as the first** — small (2-3 entry) buffer pool, not an unbounded stream.

### 27.4 Where this leaves the investigation

The DMAC/interrupt/HLE layers are now cleared end-to-end: kicks are issued correctly, GIF_STAT
is polled correctly, completions fire correctly (§26), channels end cleanly finished (not
stuck). The entire remaining mystery is upstream of all of this: **why does whatever produces
new entries into the queue at `gp-24120` stop after exactly ~3 rounds**, when Claude's §24/§25
heatmaps already showed real, ordinary simulation code continuing to run for tens of millions
of cycles afterward without ever coming back to feed this queue.

This exactly matches the angle Grok proposed as option (C) in the seq0358 check-in ("GIF/VIF1
why completions stop after setup — CHCR/PATH3 link") — now sharpened to a precise target:
find what writes to `gp-24120`'s queue (the producer), and what condition gates whether a 4th
round ever gets enqueued. Same shape as the successful MSKPATH3 write-site trace (§9): watch
writes to the real address (`gp` resolved + `-24120`/`-24128`) across the full 30M run and see
whether the producer ever runs again, or is itself gated on something that only fires 3 times
(e.g. a fixed-size init loop rather than an ongoing per-frame call).

```text
DMA-kick trace (Claude) -- exact write-site instrumentation, temp + reverted
  fptr hunt (0x1A6290/0x219150): empty across 3 independent techniques -- dead code reinforced
  real kick trace: 12 kicks total, all 14.3M-15.75M, zero after through 30M
  channels end Active=False Stalled=False at 30M -- NOT stuck, cleanly finished
  all re-arm kicks come from 3 fixed PCs (0x1F19F8/0x1F1A4C/0x1F1F00) draining a queue
    cursor at gp-24120 (buffer ptr) / gp-24128 (count), 8 bytes/entry, small 2-3 buffer pool
  next: find the producer that writes gp-24120's queue -- why does it stop after ~3 rounds
```

---

## 26. Queue producer write-sites (Grok) — external fills 3× then silence

**Split:** producer for gp−24120 (Claude: gate shape). Static + pcbreak 30M S1.

### 26.1 Static writers of `sw/sd …, -24120(gp)` (imm `0xA1C8`)

| Site | Role |
|------|------|
| `0x001F1A58`, `0x001F1C74` | **Inside** DMAC handler `0x1F1778` (self-advance / recycle) |
| `0x001F2554` in fn `0x001F2408` | **External producer** (many static callers) |
| `0x004DADD8` | BSS init with base `0x4Dxxxx`, not runtime gp |

### 26.2 Runtime hits (30M)

| PC | Hits | Window | Notes |
|----|------|--------|-------|
| `0x001F2408` producer entry | **3** | 15.167–15.193M | a2=3; v0/a1 in `0x7FD080` pool (matches Claude) |
| `0x001F2554` `sw → gp-24120` | **3** | 15.167–15.252M | same 3 events |
| `0x001F1A58` handler internal | **3** | 15.169–15.500M | cursor advance |
| `0x001F1C74` handler path | **9** | 15.169–15.502M | more thrash on same pool |

**Zero external producer hits after ~15.25M.** Handler-only stores continue briefly while draining the 2–3 entry pool, then DMA kicks stop (Claude’s 12 kicks, all ≤15.75M).

### 26.3 Mechanism (grounded)

```text
external producer 0x1F2408  →  fills gp-24120 queue  (only 3× at setup)
        ↓
DMAC handler 0x1F1778       →  drains queue, kicks VIF1/GIF, may set flip-ready
        ↓
queue empty, no refill      →  handler has nothing to kick → silence forever
```

Not a DMAC hang (channels clean). Not missing fptr for blit consumer (dead code). **Refill of the kick queue stops after a fixed setup burst** — same shape as set-flag×3 / PutDispEnv×3.

### 26.4 Next

- Callers of `0x1F2408` (static many) — which are live, why only 3 fires (Claude’s gate seat).
- Optional: watch absolute gp−24120 once gp known; already have write PCs.

```text
producer dig
  external 0x1F2408/0x1F2554: 3 hits setup only
  handler self-stores while draining 2-3 entry pool
  no refill after 15.25M → no kicks → no re-arm
```

### 26.5 Only live producer path: scheduler `0x1F43B0` (setup-only)

Of 21 static `jal 0x1F2408` sites, only **two** fire at runtime:

| Caller site | Hits | Cycles |
|-------------|------|--------|
| `0x001F440C` | 1 | 15,167,536 |
| `0x001F4478` | 2 | 15,180,912 / 15,193,584 |

Both sit in **`0x001F43B0`** (producer *scheduler*):

```text
0x1F43B0:
  if (gp-24112 == 0) return;          // master enable
  // queue-empty / flip-ready (-24225) / cursor checks
  jal 0x1F2408                        // producer @ 0x1F440C
  ...
  jal 0x1F1778 (a0=-1)                // optional direct handler kick
  jal 0x1F2408                        // producer @ 0x1F4478
```

| PC | Hits 30M | Window |
|----|----------|--------|
| `0x1F43B0` scheduler entry | **8** | 15.155–15.256M **only** |
| Outer `0x1F5788` | 4 | setup |
| Outer `0x1F6128` | 4 | setup |
| Outer `0x132F38` / `0x1D3E24` / `0x26F89C` | 0 | |

**8 scheduler entries → 3 producer fills** (gates skip 5). After ~15.26M the **scheduler itself is never called** — not merely failing gates mid-function. Upstream outers `0x1F5788`/`0x1F6128` also go silent.

```text
outers 0x1F5788/0x1F6128 → scheduler 0x1F43B0 (8× setup)
  → gated → producer 0x1F2408 (3×) → queue → DMA kicks
after 15.26M: outers silent → no scheduler → no refill
```

Claude gate seat can focus on why `0x1F5788`/`0x1F6128` stop (or master enable gp−24112).

---

## 28. Gate seat closed: the whole chain traces to ONE call site, fired exactly 4 times as a bounded stage sequence — likely one-shot setup, not per-frame render (Claude)

Picked up exactly where §26.5 left off (independently converged on the same `0x1F43B0`
scheduler and `gp-24112` master-enable flag before reading Grok's write-up — see the matching
addresses throughout). Traced one level past the "outers" to their root.

### 28.1 The arm-flag setter's real function, and its one caller

`0x1F2A60` (`sb v0,-24112(gp)`, the master-enable set) lives inside a real function starting
at **`0x001F2960`** (`addiu sp,sp,-48` prologue at `0x1F2960`, params `a0`=target object
pointer, `a1`=stage counter byte). This function builds a GS/GIFtag-style packet (`pcpyld`
quadword construction matching a GIFtag NREG/NLOOP shape) into a fixed staging buffer, then
conditionally arms the producer chain via the `andi s1,a1,1` odd/even check found in §26.5,
then calls `0x1F2620`.

**`scanword` for `jal 0x1F2960` (`0x0C07CA58`) across the full code range: exactly ONE static
caller, `0x001FFAF4`.** Not "one of several live ones" — the literal only call site for this
function in the whole binary.

### 28.2 That one call site, exhaustively traced (pcbreak, full 30M)

`--pcbreak=001FFAF4:001FFAF4` across the entire 30M-cycle run:

```text
cyc=15,166,704  v1(counter)=2 a1(passed)=1 a0(target)=0x006754C0
cyc=15,180,144  v1(counter)=3 a1(passed)=2 a0(target)=0x006754C0
cyc=15,192,816  v1(counter)=4 a1(passed)=3 a0(target)=0x006754C0
cyc=15,264,592  v1(counter)=5 a1(passed)=4 a0(target)=0x006754C0
```

**Exactly 4 hits, total, across all 30M cycles.** All within a ~98,000-cycle window
(15.1667M-15.2646M). The target object address (`a0`/`s0` = `0x006754C0`) is **identical
every single time** — this is not iterating over a list of objects, it's 4 sequential stages
against the *same* target, driven by a simple incrementing counter (`1,2,3,4`) read from a
persistent byte at `gp-28132`. `ra=0x1FFADC` (the same return site) every time too.

### 28.3 Reading

This closes the causal chain end to end, address by address:

```text
0x1FFAF4 (ONLY caller, 4x total @ 15.1667-15.2646M, fixed target 0x6754C0, stage=1..4)
  -> 0x1F2960 (builds GS packet, arms gp-24112 on odd stages)
    -> 0x1F43B0 scheduler (8x, gated, only fires while gp-24112 set)
      -> 0x1F2408 producer (3x, fills gp-24120 queue)
        -> 0x1F1778 DMAC handler (13-14x, drains queue, kicks VIF1/GIF)
          -> 12 real DMACKICK events, all <=15.75M, channels finish clean
```

A single call site firing exactly 4 times against one fixed target, with a plain 1-2-3-4
stage counter, is the classic shape of a **bounded multi-stage setup/init sequence** (e.g.
"upload this object's N setup packets," N=4, done at load), not a per-frame or per-event
call that's failing to keep firing. Nothing here looks broken *at this level* — 4/4 stages
ran, the queue drained, the DMA channels finished cleanly (§27.2). This reads as **working
code that correctly does a fixed amount of one-time work and then correctly stops.**

### 28.4 Reframe for the team

If §28.1-28.3 holds up, the entire chain investigated across §25-§28 (DMAC handler /
producer / scheduler / arm-flag / this call site) is very likely a **one-shot boot or
level-load asset/object upload path**, not B3's ongoing per-frame render-submission
mechanism. That would mean we've been correctly and thoroughly characterizing a subsystem
that *isn't* the bug — the real "nothing new ever gets drawn" mechanism must be a **separate,
still-unfound code path** that's supposed to fire every frame/vblank and isn't (or is, but
through a channel/PATH we haven't instrumented yet — Path1/Path2, or a VIF1 chain that
doesn't route through this same `0x1F1778` handler at all).

Concrete next step: find who calls `0x1FFAF4`'s enclosing function (didn't chase the prologue
this pass) and what `0x006754C0` is — if that object is level/track-scoped (matches "one-shot
per level load"), the reframe is confirmed and the real search moves to finding B3's actual
per-frame VU1/GIF submission call (likely a completely different address range, possibly
routed through Path1 rather than Path3). No Core changes.

```text
gate-seat close (Claude)
  arm-flag setter (0x1F2A60) lives in fn 0x1F2960, sole static caller = 0x1FFAF4
  0x1FFAF4 fires EXACTLY 4x total (30M cyc), all ~15.17-15.26M, same fixed target 0x6754C0
  stage counter 1,2,3,4 -- bounded setup shape, not per-frame
  reframe: this whole chain (S25-S28) may be one-shot object/level upload, not the render loop
  next: find caller of 0x1FFAF4's function + identify object 0x6754C0; if level-scoped,
        redirect the whole search to find B3's real per-frame VU1/GIF submission path
```

---

## 29. Confirm one-shot setup: 0x6754C0 is display-env; 0x1FFAB8 is staged boot fptr (Grok)

### 29.1 Object `0x006754C0`

Already established earlier in §22 / env dig:

- PutDispEnv env0 base (DISPFB at `+0x10` sticky `0x51400`)
- SetDefDispEnv / game init writes target this blob
- Claude’s stage calls pass **hardcoded** `a0=0x6754C0`:

```text
0x1FFAE0: lui r2, 0x67
0x1FFAE4: addiu r16, r2, 21696   # 0x6754C0
0x1FFAEC: move a0, r16
0x1FFAF4: jal 0x1F2960           # arm/producer chain
```

**Not track/level geometry** — it is the **GS display-env object**. Strengthens “bounded display/GS circuit setup,” not missing per-frame world draw.

### 29.2 Who calls `0x1FFAB8`

No direct `jal`. **Data word** at `0x0049AC74` = `0x001FFAB8`.

Table (pairs fn / id):

```text
0x49AC5C: 0x001F5708  id=0x0A
0x49AC64: 0x001FFA00  id=0x15
0x49AC6C: 0x001F6108  id=0x14
0x49AC74: 0x001FFAB8  id=0x02   ← our 4× stage runner
0x49AC7C: 0x00207E30  id=0x03
... (more staged setup handlers)
```

Loaded via `0x001FE444` region (`lui 0x4A` + addiu into table). Classic **boot/state-machine dispatch table**, not a per-frame render loop.

### 29.3 Verdict on reframe

End-to-end chain S25–S28 is a **one-shot multi-stage GS/display setup** on fixed env `0x6754C0`:

```text
boot table @ 0x49ACxx → 0x1FFAB8 (stage id 2, internal 1..4)
  → 0x1F2960 → scheduler → producer → DMAC kicks → done clean
```

Stopping after stage 4 is **success of setup**, not a bug. Real per-frame submission is elsewhere (Claude Path1/VU1 hunt).

```text
0x6754C0 = display env (confirmed)
0x1FFAB8 = boot-table stage, not per-frame
reframe CONFIRMED
```

### 29.4 Post-setup transfer census (Grok, complementary to Path1 hunt)

`--track-transfers` 30M: **214 events**. After setup window, **only SIF EE→IOP** continues (RPC/IOP). Visible GIF/VIF lines are all ~14.3M (setup).

End metrics (unchanged plateau):

```text
gifPath1=0  gifPath2=12  gifPath3=20  m3p=True heldP3qwc=2124
px=877187  (flat after early window)
```

**Path1 is zero for the entire run** — not “Path1 exists but we mis-routed.” After GS display-env setup finishes, **no GIF path is fed at all**; EE stays alive (syscalls + SIF) without new GS submission.

So the open “per-frame path” is not merely Path1 vs Path3 preference — it is **absence of any post-setup GS kick stream** (Path1/2/3). Aligns with setup-chain reframe: setup did its job; ongoing draw submission never starts.

```text
post-setup: SIF only; gifP1=0 forever; P2/P3 only in setup
hunt = who should start continuous GS submit after boot table stage 2 completes
```

---

## 30. VIF1 command-type breakdown confirms: not a routing gap, VU1 is never fed at all (Claude)

Complementary angle to §29.4's transfer census — checked VIF1's own command-type counters
(`CommandsProcessed`/`UnpackWords`/`MpgWords`, `Vu1.MscalRuns`, all pre-existing fields, no new
Core code) rather than GIF-side transfer counts, to rule out "maybe VU1 gets its data some
other way we're not counting on the GIF side."

```text
at cyc=16,000,000 and cyc=30,000,000 (byte-identical, confirms the freeze is total):
  gif-path: p1=0 p1qws=0 p2=12 p3=20 ...
  vu1: mscalRuns=0
  vif1: cmds=745 unpackWords=0 mpgWords=0 mscal=0
```

**VIF1 processes 745 commands total (frozen by 16M, unchanged through 30M) — and not one of
them is UNPACK, MPG, or MSCAL.** Whatever those 745 commands are (STCYCL/STMASK/FLUSH/DIRECT-
class, consistent with the p2=12 Path2-passthrough count), VU1 never once receives a VU data
upload, a microcode upload, or a microcode-run trigger, in the entire 30M-cycle run. Combined
with `mscalRuns=0`: **VU1 never executes a single instruction this whole run.**

This rules out a Path1-specific routing bug (e.g. XGKICK firing but Gif.ReceivePath1Data
somehow dropping it) — the problem is upstream of GIF entirely: the EE-side code that should
feed VU1 real geometry/microcode via VIF1 UNPACK+MPG+MSCAL never runs those command types at
all, consistent with Grok's §29.4 finding that no post-setup GS kick stream exists on any path.
One unified freeze, not two separate bugs.

```text
VIF1 command-type check (Claude) -- rules out Path1-specific routing bug
  cmds=745 total, frozen identical at 16M and 30M -- confirms total freeze, not just GIF-side
  unpackWords=0 mpgWords=0 mscal=0 mscalRuns=0 -- VU1 never fed, never runs, entire run
  same root as S29: whatever should drive continuous per-frame submission never starts
```

---

## 31. VBlank ISR's own consume/gate logic disassembled — closes the loop back to the doc's original DISPFB-sticky finding (Claude)

Split with Grok: (a) am I reaching whatever should wake on VBlank post-15.5M, (b) if
reached, where does it bail. Disassembled the VBlank ISR itself first (`0x1F1CE8`,
`disasm burnout-only.json 20000000 001F1CE8:C0`) since it's the one thing we already know
keeps firing (48x, Grok's §25.2) — turns out its *own* body already answers most of (b).

### 31.1 The ISR's real gate

```text
0x1F1D38: bne a0,v0,0x1F1F24      # v0=2: only act on a specific interrupt sub-type
0x1F1D40: lbu v0,-24225(gp)       # THE SAME flip-ready flag from Grok's S25 (0x1F1BF4/0x1F1C0C)
0x1F1D44: beq v0,zero,0x1F1F24    # flag clear -> skip straight past the real work, no-op vblank
0x1F1D4C: sb zero,-24225(gp)      # flag set -> consume it (clear) ...
0x1F1D50: sb v0,-24111(gp)
...
0x1F1D84: jal 0x001029B0          # ... and call the PutDispEnv-like DISPFB writer
```

**The VBlank ISR itself is gated by `gp-24225`, the exact flip-ready flag the whole S25-S30
chain traces back to.** When set, it consumes (clears) the flag and calls `0x001029B0` — the
same "PutDispEnv-like writer" identified all the way back near the start of this document as
the thing that sets `env.dispfb`. When clear, it's a correct, cheap no-op.

### 31.2 Closes the loop to the document's very first finding

This directly explains `env.dispfb`'s original "sticky-initialized once at boot, never
changes" behavior (noted early in this investigation, long before §9): `0x1029B0` only ever
gets called from inside this ISR gate, and the flag that gates it (`gp-24225`) only gets set
by the boot-stage chain traced in §25-§29 — which fires a bounded, small number of times
during setup and never again. **The VBlank ISR has been working correctly the entire time.**
It fires every vblank (48x confirmed), checks for new work, finds none after the setup
window, and correctly does nothing. Not a flip-logic bug — a starved-input problem, same
root as everything else in §25-§30.

### 31.3 What's actually left

The single remaining open question across this entire very long investigation (§1-§31) is
now precisely: **what real game-loop code is supposed to set `gp-24225` (or otherwise drive
a new round of VIF1 UNPACK/MPG/MSCAL + GIF Path1/2/3 submission) once per frame during actual
gameplay, and why is it never reached (or reached but silently bails) after the one-shot
boot-stage-2 display-env setup completes?** Everything downstream of that point (scheduler,
producer, DMAC handler, VBlank consume-gate) has now been read, traced, and confirmed correct
by both of us independently. The bug, if there is one in our emulator (rather than a real
missing trigger condition we haven't provided — e.g. real input, a title-screen wait, a
disc-region check), lives upstream of all of it, in whatever should be calling into this
machinery every frame and isn't.

```text
VBlank ISR gate (Claude) -- closes the loop to the original DISPFB-sticky finding
  ISR checks gp-24225 (flip-ready) every vblank; only acts when set, else correct no-op
  when set: consumes flag, calls 0x1029B0 (the PutDispEnv-like DISPFB writer)
  gp-24225 only ever gets set by the S25-S29 boot-stage chain -- explains "DISPFB sticky once"
  VBlank ISR is NOT the bug -- everything downstream of gp-24225 is now confirmed correct
  remaining question: what should set gp-24225 every frame during real gameplay, and isn't
```

---

## 31. VBlank ISR gates (Grok seat b) — flip/DMA only, no VU1 submit

Full disasm of `0x001F1CE8` (fires 48×/30M). Control flow:

```text
restore gp
if (vblank_counter >= limit) early out
if (cause != 2) early out
if (flip-ready gp-24225 == 0) skip PutDispEnv   // post-setup: always true after 3 clears
  else PutDispEnv + DISPFB/DISPLAY privileged writes
optional display-env field tweaks
if (some env slot) program DMAC regs (0x1000_xxxx, CHCR-like 260)
gp-24128++   // queue count bump
jal 0x1F1778(a0=-1)   // try DMA handler drain every vblank
return
```

### 31.1 Bail conditions post-setup

| Gate | Post-15.75M behavior |
|------|----------------------|
| flip-ready `gp-24225` | **0** (only set 3× in setup) → PutDispEnv skipped |
| kick queue | **empty** (no producer refill) → handler no-ops |
| ISR itself | **still entered** 48× (not reachability-dead) |

### 31.2 What the ISR is *not*

No MSCAL/UNPACK/Path1 kick. No thread create. Display flip + opportunistic DMA-queue drain only.

So seat (b) answer: **consumer (this ISR) IS entered every VBlank**, but every useful side effect is gated off by **empty setup-only queues/flags**. It is not waiting on a mysterious mid-ISR bail for “frame submit” — frame submit was never this ISR’s job.

Continuous GS submit must be a **different** VBlank waiter / main-loop path (Claude seat a: other wake targets; or main thread not parked on VBlank at all).

```text
ISR 0x1F1CE8: entered 48x; only flip+DMA-drain
post-setup both drains are no-ops (no flip-ready, empty queue)
VU1/Path1 submit is elsewhere — not an ISR-internal gate we missed
```

---

## 32. CDVD/streaming state 15–50M (Grok)

`scoreboard-metrics` product path, host-present:

| cycles | cdvdSectors | sifBytes | px | gifP3 | syscalls |
|--------|-------------|----------|-----|-------|----------|
| 15M | **0** | 1,920 | 286,720 | 2 | 424 |
| 20M | **425** | 33,660 | **877,187** | **20** | 42,461 |
| 30M | **1,865** | **36,372** | 877,187 | 20 | 85,160 |
| 50M | **1,865** | **36,372** | 877,187 | 20 | **918,536** |

### Reading

1. **Disc is not frozen at setup.** Sectors 0→425→1865 across 15–30M — real streaming continues **after** the GS/DMA setup cliff (~15.75M).
2. **By 30M disc and SIF both go idle** (byte-identical at 50M). Not “blocked forever on pending CDVD.”
3. **EE keeps thrashing** (syscalls 85k→918k with no new I/O or GS) — alive but not loading and not drawing.
4. So post-30M black is **not** explained by unfinished disc streaming. 15–30M may still be load/attract with progressive sector reads; after that, no I/O and no GS.

```text
CDVD: streams 15-30M then idle
SIF: same
EE: spins without I/O or GS after 30M
not blocked on disc after 30M; may still be pre-gameplay state earlier
```

---

## 33. Thread census + one spin-loop traced and cleared (Claude, thread/RPC seat)

Split with Grok: Grok took CDVD/streaming state (§32, disc streams then idles by 30M — not
blocked on I/O), I took EE thread state.

### 33.1 Full thread snapshot at cyc=30M

Added a temp `SnapshotThreads()` accessor to `KernelState` (revert after use) — no new
behavior, just reads existing per-thread fields:

```text
tid=1 alive=T started=T sleeping=T  waitSema=0   prio=50
tid=2 alive=T started=T sleeping=T  waitSema=3   prio=64
tid=3 alive=T started=T sleeping=T  waitSema=0   prio=54
tid=4 alive=T started=T sleeping=T  waitSema=0   prio=54
tid=5 alive=T started=T sleeping=F  waitSema=0   prio=54   <- the running/current thread
tid=6 alive=T started=T sleeping=T  waitSema=0   prio=33
tid=7 alive=T started=F                          prio=22   (never started)
tid=8 alive=T started=T sleeping=T  waitSema=104 prio=1    (highest real priority)
currentThreadId=5
```

Live `EE.PC=0x0010BD48` matches `tid5`'s position exactly — confirms tid5 is genuinely the
one executing at end-of-run, not a stale snapshot artifact.

### 33.2 tid5 traced: a real, actively-progressing wait — not the stuck point

`0x0010BD48` is the return address of the generic BIOS syscall trampoline for syscall `0x32`
(`SleepThread`) — `0x32` is also the single largest entry in the syscall histogram (74,162
hits total, all threads combined). Register dump (`--pcbreak=0010BD48:0010BD48`) gives a
stable `ra=0x00237188`, i.e. the real caller loop is at `0x237180-0x237198`:

```text
0x237180: jal 0x0010BD40        # SleepThread()
0x237188: lbu v1, 0(s0)         # s0 = gp-23820 + index (index 0..3)
0x237198: beq v1, zero, 0x237180  # byte still 0 -> sleep again
```

A textbook 4-slot flag-wait: `while (flags[i]==0) SleepThread();` for `i` in `0..3`.
`gp-23820` resolves to `0x004E2964` (gp confirmed `0x4E8670` earlier this session) — matches
the `s0` values seen in the register dump exactly.

**Watched all 4 bytes (`0x4E2964..0x4E2967`) across the full 30M-cycle run: NOT a one-shot.**
Each slot gets SET (`0x237108`, `sb v1,0(v0)` → 1) and CONSUMED/cleared (`0x2371C8`,
`sb zero,0(v0)` → 0) **repeatedly — 22-48 times each, spread across the whole run**, unlike
every other flag traced tonight (`gp-24112`, `gp-24225`, the boot-stage chain) which fired a
small fixed number of times only during the 15.17-15.75M setup window and then went
permanently silent.

**This rules tid5's wait out as the stuck point.** Whatever sets these 4 bytes is alive and
producing work throughout the entire run — this is a real, correctly-functioning
worker-sync primitive (roughly one full 4-slot cycle every ~625K cycles, plausible per-frame-
ish cadence), not evidence of the freeze. My earlier read of the raw 74,162 SleepThread count
as "one thread spinning uselessly" was too hasty — that count is a whole-system syscall
histogram, most of it presumably normal idle-thread scheduling unrelated to this one loop.

### 33.3 What's still open

- `tid2` (`waitSema=3`) and `tid8` (`waitSema=104`, highest real priority in the system) are
  both parked on real semaphores that haven't been characterized yet — worth checking
  whether these are legitimate long-lived waits (e.g. an IOP-RPC completion sema that's
  correctly idle) or a starved producer, same shape as everything else tonight.
- `tid7` never started at all (`started=False`) — worth a quick check on whether real B3
  expects it to be started by this point.
- Given tid5's specific spin cleared, the search for "what should trigger continuous
  per-frame GS submission" is still open — this was one candidate thread, ruled out with
  real evidence rather than assumed.

```text
thread census (Claude) -- tid5's flag-wait spin traced and cleared
  8 threads: 6 sleeping (0 real wait-id), tid5 running (SleepThread spin), tid7 never started
  tid5's spin-wait (gp-23820, 4 flag bytes) IS actively serviced (22-48x each, whole run)
    -- NOT the stuck point, rules this thread out with evidence
  still open: tid2 waitSema=3, tid8 waitSema=104 (highest prio) -- not yet characterized
  still open: tid7 never started -- check if real B3 expects it running by now
```

---

## 34. tid7 is not "never started" — one-shot worker that **exited** (Grok)

`--trace-threads` 30M retracts the snapshot reading `started=False` as never-run.

| Event | cyc | notes |
|-------|-----|-------|
| Create+Start tid=7 | 25,011,264 | entry=`0x002A2110` prio=22 stack=0x800 |
| SwitchTo | same | runs entry |
| SaveOut | 25,011,328 | at `0x10BE64` (Sleep/Wait path) |
| brief preemption | ~25.25–25.32M | |
| SaveOut | 25,325,600 | at **`0x002A2168`** = epilogue after `jr ra` |

Entry `0x2A2110` is a **wrapper**: `jalr` callback from arg struct (`lw t9,4(a0)`), then branch to WaitSema / ExitDelete / Sleep stubs (`0x10BE40` / `0x10BD40` / `0x10BC60`). Final PC on epilogue = **function returned**.

PS2 THREADMAN: exited thread stays allocated → census `alive=True started=False` looks like "never started" but means **DORMANT after run**.

**tid7 cleared** as the continuous-draw blocker. High-prio tid8 @ sema 104 remains Claude's better lead.

```text
tid7: create/start @25M, runs once, returns, dormant
not never-started; one-shot worker finished
sema104 / tid8 still the open thread lead
```
