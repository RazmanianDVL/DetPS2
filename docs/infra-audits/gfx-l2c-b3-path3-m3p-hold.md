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
