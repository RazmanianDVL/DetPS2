# GFX L2c — B3 PATH3 / M3P hold dig

**Status:** measure complete (hold dig + FQC refute + data-flow writer) — **no Core this seat**; dual-ACK before any fix  
**Date:** 2026-08-05  
**Title:** Burnout 3 (SLUS_210.50)  
**Parents:** `gfx-l2c-b3-frame-dispfb-stall-finding.md`, Claude page-0x46 dump (`b7048b1`), Claude FQC refute (`bc239a9`)  
**Author:** Grok (claimed seat after Claude seq0290 split; §9 continues after Claude seq0292)

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
