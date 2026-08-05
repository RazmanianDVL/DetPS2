# GFX L2c — B3 PATH3 / M3P hold dig

**Status:** **IN PROGRESS** (2026-08-05 night) — executive scoreboard below; G1/G2/G3 all confirmed real, G3 answered NO (§56.4) — real gap is B3's ongoing per-frame render path (VU1/Path1), never located tonight  
**Date:** 2026-08-05  
**Title:** Burnout 3 (SLUS_210.50)  
**Parents:** `gfx-l2c-b3-frame-dispfb-stall-finding.md`, Claude page-0x46 dump (`b7048b1`), Claude FQC refute (`bc239a9`), Claude forced-unmask A/B (`f8b5db8`)  
**Author:** Grok + Claude (split seats; dual-ACK before Core)  
**Related design:** `b3-iovec-multi-entry-design.md` (S1 landed + hugeCopy tighten; not the 1865 limiter)

---

## 0. Executive scoreboard (2026-08-05 night)

### 0.1 One-line (updated)

B3 finishes a **correct one-shot display-env + GTFS open** of `Global.txd`, then **never dispatches the fno=5 DMA read**. Sector counter plateaus at **1865** (`stagePlantOnly`), so the existing post-TXD PATH3-unmask assist never arms; **held PATH3 stays 5/2124** and the present stays **black**. Completing “cdvd≥2000” alone unlocks more boot activity but **does not** drain the hold or light the framebuffer.

### 0.2 Causal chain (load-bearing)

```text
display-env setup (0x1FFAF4 ×4 on 0x6754C0) ──OK, finished──►
GTFS fno=3 open Global.txd (0x1D36E0) ──OK, state=1──►
vtable sibling fno=5 DMA (0x1D3280) ──NEVER DISPATCHED (0 hits)──►
cdvd stuck 1865 (<2000) ──blocks──► MaybeEscapePostTxdHang
  (needs cdvd≥2000, cyc≥40M, M3P, px==0, gifP3≥30)
held PATH3 5/2124 never drains ──► lit=0 / mostlyBlack
```

### 0.3 What is proven correct (not the bug)

| Area | Verdict | Refs |
|------|---------|------|
| Held-queue **drain policy** | Unmask fully drains sync; Path2 sticky idle | §3, §46 |
| Mask/unmask buffer | 3× real VIF delivery; layout re-masks each round | §45 |
| Display-env / DMAC / scheduler chain | One-shot setup, finishes clean | §25–§31, §47 |
| Boot-table 6-of-28 | Install-all dense vtable; specialized callers only | §38–§39 |
| 5 “dead islands” (blit/alarm/id2/pipeline/gate) | Unlinked / unarmed, not HLE broken | §41–§43 |
| Object creators | Bounded one-shots, complete | §44 |
| HLE `SetMskPath3` / `DrainHeldPath3` | Matches EE commands | §5, §46 |
| iovec multi-entry S1 + hugeCopy tighten | Correct bugs fixed; **not** 1865 limiter | §49–§51 |
| Empty-path Global default | Ruled out (real path in TRACE) | §54 |

### 0.4 Open gaps (fix priority)

| # | Gap | Evidence | Status |
|---|-----|----------|--------|
| **G1** | **fno=5 never dispatched** after successful open | Open `0x1D36E0` ×1; read `0x1D3280` ×0; same vtable `0x4DDFC8` | Open — need dispatcher / re-tick |
| **G2** | **postTxd unmask heuristic never arms** even when cdvd forced ≥2000 | gifP3 caps **26&lt;30**; px becomes ≠0 → self-defeating | Open — heuristic rewrite or alternate unmask |
| **G3** | Does draining held 2124 QW light anything? | Forced unmask: **fully drains (heldP3n/qwc 5/2124→0/0), lit stays 0** | **Answered: NO — see §56.4** |

### 0.5 Force-cdvd A/B (S56) — negative for “single missing link”

At cyc=30M forced `NoteHostReadSectors` 1865→2065 (temp, reverted):

| Metric | Before | After force |
|--------|--------|-------------|
| cdvd | 1865 | **→6784** (FRONTEND plant + natural climb) |
| px / prims | 877187 / 172 | 1172419 / 234 |
| gifP2 / gifP3 / msk | 12 / 20 / 10 | 16 / 26 / 13 |
| **heldP3n / qwc** | 5 / 2124 | **5 / 2124** (unchanged) |
| lit / mostlyBlack | 0 / 1 | **0 / 1** |
| post-TXD unmask TRACE | — | **0 lines** |

**Read:** sector gate unlocks real cascade; **does not** drain hold or present. Completing G1 alone is **not proven** sufficient for lit chrome.

### 0.6 Key PCs / objects (quick index)

| Item | Address |
|------|---------|
| Display-env object | `0x6754C0` |
| Stage runner (×4) | `0x1FFAF4` → … → PutDispEnv |
| GTFS open (fno=3) | `0x001D36E0` (vtable `0x4DDFC8`) |
| GTFS close (fno=4) | `0x001D3670` |
| GTFS DMA read (fno=5) | `0x001D3280` (vtable `0x4DDFD0`) — **0 hits** |
| Open state field | struct **+24** (=1 on success); handle **+40** |
| State getter | `0x001D3824` (×1) |
| One-shot container tick | `0x00212A24` (s0=`0x66E100`) |
| Recv buffer (open) | `0x0066E080` |
| postTxd unmask assist | `Burnout3Assist.MaybeEscapePostTxdHang` (cdvd≥2000) |
| Dense boot registry | `0x49AC58` {id,fn} → dense `0x670C18` |

### 0.7 Product end-state (unchanged plateau shape)

```text
gif-path: p3=20 p3qws=6408 m3p=True heldP3n=5 heldP3qwc=2124  (force-cdvd: p3=26, held same)
claim:    px~877k (force: ~1.17M) lit=0 dispfbPx=0 frame1=0xA0046 dispfb2=0x51400
cdvd:     1865 natural plateau (force: 6784)
```

---

## 0b. Original one-line (session start)

At 50M, **M3P is left asserted** with a **held PATH3 queue of 5 entries / 2124 QW** that never drains, because the game’s **last MSKPATH3 is a mask with no matching unmask**. That is real stuck DMA payload under HLE hold (not Path2-sticky block). Combined with page 0x46 being 100% black, the backlog is a strong candidate for “geometry never lands / never flips,” not a compositor lie. *(Still true; mechanism of “why no later unmask” is now G1+G2 above, not broken drain HLE.)*

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

---

## 35. Sema 104's designated producer traced and found unreachable — same dead-code shape as §27, new subsystem (Claude)

### 35.1 Sema 104 is never signaled — full system census, not just this one ID

Temp `DETPS2_TRACE_RPC` addition to `SignalSema` (reverted after use) gave a full census of
every `SignalSema` call across the 30M-cycle run: **220 total calls system-wide, 218 of them
target sema id=1, one targets id=7, one targets id=8. Zero target id=104 (or id=3, tid2's
wait target).** Sema 104 — the wait target of tid8, the single highest-real-priority thread
in the whole system (`priority=1`, lower = higher per this codebase's convention) — is never
signaled once in 30 million cycles. Essentially all system liveness funnels through a single
semaphore (id=1); everything else, including 104, is either starved or resolved through a
different mechanism (SIF RPC synchronous completion, observed separately in the trace for
other semas via `HandleBind`/`HandleCall`, but not present for 104's creation).

### 35.2 Found the designated producer, and it's unreachable

Sema 104's creation site (`ra=0x00248598`, `init=0 max=127`) creates a *pair* of semaphores
plus initializes a small table of records (28 bytes × 2, fields `0, 512, 16, 16384` — sizes
consistent with a job/DMA-buffer descriptor pool), the shape of a producer/consumer job
queue. Right above it in the same function block sits a second function
(`0x00248518-0x00248540`) that:

```text
0x248520: lw a0, 13444(v0)     # v0 = 0x1D90000; loads the SAME memory slot (0x1D93484)
                                #   that holds sema 104's id
0x248528: jal 0x0010BE50       # = iSignalSema (EE syscall -67, interrupt-safe SignalSema —
                                #   see SonyKernelHle.cs's own comment: "for SetAlarm callbacks")
0x248530-34: sync; ei          # interrupt-enable wrap, consistent with an alarm-callback body
```

**This is sema 104's designated producer** — an `iSignalSema`-wrapped one-liner, exactly the
shape real PS2 code uses for a periodic/one-shot `SetAlarm` callback that posts a semaphore.

Checked reachability with the same three independent techniques that established the
`0x1A6290`/`0x219150` dead-code read back in §27:

1. **Direct `jal`**: `scanword` for `jal 0x00248518` (`0x0C092146`) across the full code
   range — **zero matches**.
2. **Stored literal pointer** (e.g. a callback table entry): `find-word` for the raw address
   `0x00248518` across `0x100000-0x700000` post-20M-cycle live memory — **zero matches**.
3. **Computed `lui`+`addiu`/`ori` construction**: masked scan for `lui $rt,0x25` (17 hits) /
   `lui $rt,0x24` (4 hits), then directly for the completing `addiu $rt,$rt,0x8518` (9 hits,
   all `addiu t0,{s4,s5,s6,s7},-31464` — `0x250000-0x7AE8 = 0x248518`, confirmed real
   constructions, not false positives). **Checked the first one by hand**
   (`0x248968`, inside a retry-loop polling `jal 0x216BB8` until `v0==1`): `t0` is
   **clobbered** at `0x2489CC` (`addu t0,s0,s1`) a few instructions later, before any
   `jalr t0` or store — dead, same shape as §27's one coincidental `0x1A6290` match. The
   other 8 hits are structurally identical repeats of the same unrolled block (same
   `lui sX,0x25 ... addiu t0,sX,-31464` pattern at evenly-spaced addresses
   `0x248928..0x2495A0`), overwhelmingly likely the same dead pattern each time, not checked
   individually given the strong structural match and time budget — flagging this explicitly
   as a slightly weaker link than the other two techniques (worth a second pass if this
   thread becomes central).

**All three techniques: zero live reachability.** Same read as §27: this looks like
genuinely orphaned/unlinked code, not a bug in call-graph construction on our side.

### 35.3 Why this feels different from §25-§31's chain

Everything in §25-§31 traced a mechanism that *does* run (a handful of times, correctly,
during boot setup) and then correctly stops because its input dries up — a starved-producer
shape, several links deep, all confirmed live and correct up to the point they run out of
work. **This is different: a specific, plausible-looking producer function for the
highest-priority thread's wait condition that is never reachable at all**, not even once —
closer in shape to the §27 dead-code finding (the blit-consumer mega-fn) than to the
starved-producer chain. Two independent "this looks like real code with zero live callers"
findings in the same binary, in two unrelated subsystems (GS blit consumption, and an
alarm-driven semaphore producer), is a real pattern worth flagging on its own: possibly a
build/link configuration difference between what we loaded and how the real retail binary
resolves indirect calls (e.g. a fixed-up jump table we're not reproducing, or callbacks
registered through a BIOS/IOP-side mechanism our HLE doesn't model), rather than two
unrelated coincidences.

### 35.4 Open

- Confirm the remaining 8 `addiu t0,sX,-31464` sites are dead the same way (spot-checked
  one, structurally near-identical to the rest, but not individually verified).
- Find what *should* call `SetAlarm` (or equivalent) with `0x248518` as the callback —
  that's the real missing link, same shape as looking for what should call the boot-stage
  dispatcher an ongoing number of times instead of the bounded 4 we found in §28.
- Given both this and §27's dead-code finding share the "computed-but-clobbered" or
  "zero reachability across all 3 techniques" signature, worth comparing notes with Grok on
  whether our indirect-call/jump-table modeling has a systematic gap, rather than continuing
  to treat each as an isolated one-off.

```text
sema-104 producer hunt (Claude) -- found the designated producer, confirmed unreachable
  full SignalSema census: 220 calls total, 218->sema1, 1->sema7, 1->sema8, ZERO->sema104
  producer candidate: iSignalSema-wrapped fn @ 0x248518 (SetAlarm-callback shape per
    SonyKernelHle.cs's own comment), reads the exact same memory slot as sema104's id
  reachability: 0/3 techniques find a live call (direct jal, stored ptr, computed lui+addiu)
    -- one computed match found but clobbered before use, same shape as S27's dead 0x1A6290 hit
  pattern worth flagging to Grok: TWO unrelated subsystems now show "real code, zero live
    callers" -- possibly a systematic indirect-call/callback-table modeling gap, not
    coincidence
```

---

## 36. Correction: SetAlarm HLE is not the gap — the game never calls SetAlarm at all (Claude)

Follow-up on §35's "possible systemic gap" flag, before Grok duplicated the check. Traced
`SetEeAlarm` directly (temp trace on the shared implementation all four syscall aliases
`0x18`/`0x1E`/`0xFC`/`0xFD` route through, reverted after use): **zero `SetAlarm`/`iSetAlarm`
calls of any kind across the entire 30M-cycle run.** Cross-checked against the syscall
histogram already gathered — none of the alarm syscall numbers appear in the top entries.

Also read `InvokeAlarmCallback`/`TickEeAlarms` directly: the firing mechanism itself is
correctly implemented (real EE.PC redirect to the callback, real `id`/`time`/`common` arg
setup, driven off VBlank, sentinel-RA return handling). If B3 ever called
`SetAlarm(0x248518, ...)`, our HLE would actually run it.

**Revises §35.3's framing:** this isn't evidence of a gap in our indirect-call/callback-table
modeling — `SetAlarm` is simply never invoked by the game at all in this window, same as
`0x1A6290` never being called directly (§27). Softens "systemic call-graph bug" back toward
"multiple distinct subsystems (blit consumer, alarm-driven sema producer) all sit unarmed
because whatever's supposed to arm them hasn't run" — consistent with Grok's CDVD-idle
finding and §33's thread census. Doesn't answer *why* nothing arms them, but does rule out
"our alarm/callback HLE is buggy" as the cause.

```text
SetAlarm correction (Claude) -- zero SetAlarm/iSetAlarm calls across 30M cycles, any variant
  InvokeAlarmCallback/TickEeAlarms read directly -- HLE firing mechanism is correct
  revises S35.3: not a callback-modeling gap, the game just never calls SetAlarm at all
  consistent theme: several subsystems sit correctly-unarmed, not broken
```

---

## 35. EE SetAlarm: zero calls in 30M (Grok)

Systemic check after Claude's unreachable `0x248518` iSignalSema producer (sema 104).

### Method

Product `blocker-trace` 30M with `DETPS2_TRACE_ALARM=1` (logs every `SetEeAlarm` / syscalls `0x18/0x1E/0xFC/0xFD`).

### Result

**`[ALARM]` lines: 0.**  
Top-syscalls dump also has **no** alarm nums (`0xFC/0xFD/0x18/0x1E/0xFE/0xFF`) in the listed ranks.

HLE path `SetEeAlarm` / `TickEeAlarms` / `InvokeAlarmCallback` exists and is wired on VBlank — but **B3 never registers an EE soft alarm** in this window.

### Implication

- The dead `0x248518` producer is **not** "registered via SetAlarm but our fire is broken" — **registration never happens**.
- Same class as blit consumer: plausible callback body, **no live arm path** in the run we see.
- Does **not** yet prove a systematic HLE gap (we never got a SetAlarm to mishandle). Does prove B3's continuous path is not waiting on EE SetAlarm callbacks we failed to fire.

Next (if pursuing systemic): IOP SetAlarm / TIMEMAN; or who *should* call EE SetAlarm before gameplay and why that code is unreached (same shape as boot-table vs per-frame).

```text
EE SetAlarm: 0 registrations / 30M
0x248518 cannot fire via alarm HLE — never armed
not a TickEeAlarms bug for this title in this window
```

---

## 37. Boot-stage table dumped in full: a one-shot subsystem-init registry, not a sequential state machine (Claude)

Split with Grok: (A) mine — who should set a "gameplay ready" flag; (B) Grok's — static
registrar hunt for `0x248518` (result: zero refs anywhere, even stronger dead-code shape than
the alarm-HLE angle — matches).

Dumped the full boot table from §29 (`0x0049AC5C` onward, `disasm ... 0049AC50:C0`) rather
than just the handful of entries already sampled. It's **{id, fnptr} word pairs** covering a
wide, non-sequential range of ids: `5, 4, 0xC, 9, 8, 0xF, 0x10, 0x17, 0x18, 0xE, 0xD, 0x11,
0x12, 0x13, 0xB, ...` (alongside the earlier-known `0x0A, 0x15, 0x14, 0x02, 0x03`) — at least
~20 distinct subsystem-init entries, ids scattered, not incrementing in table order.

**This changes the framing for (A).** It's not a linear "stage N of M" sequence where
progress could stall at stage 2 — it reads as a **boot-time subsystem registry**, walked once
to initialize every listed subsystem (audio, physics, display-env [id=2, our known chain],
etc.), each running its own one-shot init. There is no "next stage" counter to get stuck on;
every entry plausibly already ran once during the ~15.17-15.75M setup window we've mapped
repeatedly tonight.

**Redirects (A):** "gameplay ready" is very unlikely to be a further position in *this* table
— it's more likely a separate flag/condition checked by the main loop *after* all boot-table
subsystems finish, or a per-subsystem "am I fully initialized" flag that one of these ~20
init functions itself sets (or fails to set). Given the volume of subsystems here, I'd rather
not fan out into per-entry static tracing without a sharper filter first. No Core.

```text
boot-table full dump (Claude) -- one-shot subsystem registry, not sequential stages
  ~20 {id, fnptr} entries, ids scattered (2,3,4,5,8,9,A-F,10-13,17,18...), not table-order
  redirects "gameplay ready" search away from "stuck at stage N in this table"
  toward: a flag checked by the main loop AFTER all boot-table init finishes,
    or set by one specific subsystem init among the ~20 -- needs a sharper filter
```

---

## 38. 14 of 20 boot-table subsystems never fire at all — this is request-driven, not "run everything once" (Claude)

Ran the census proposed in §37 (temp `--pc-census=` on all 20 fnptrs, reverted after use):

```text
fired:     0x1F5708(id=0xA)  4x   0x1FFA00(id=0x15) 4x   0x1F6108(id=0x14) 4x
           0x1FFAB8(id=0x02) 4x   0x1F7E08(id=0x04) 10x  0x1F9DC0(id=0x0C) 2x
never:     0x207E30(id=3) 0x1FA168(id=5) 0x205E68(id=9) 0x1FFB50(id=8)
           0x1FB0B0(id=0xF) 0x1FB960(id=0x10) 0x1FFB60(id=0x17) 0x1FFC18(id=0x18)
           0x205DE8(id=0xE) 0x200578(id=0xD) 0x201108(id=0x11) 0x202CC0(id=0x12)
           0x204320(id=0x13) 0x205E30(id=0xB)
```

**Only 6 of 20 registered subsystem-inits ever fire, all 4x/10x/2x during the same 15.17-
15.75M setup window already mapped repeatedly tonight. 14 never fire once, in 30M cycles.**

This settles §37's open question: the table is **not** "walk everything once at boot" — it's
genuinely request-driven, and only ids `{2, 4, 0xA, 0xC, 0x14, 0x15}` are ever requested. The
other 14 ids (`3, 5, 8, 9, 0xB, 0xD, 0xE, 0xF, 0x10, 0x11, 0x12, 0x13, 0x17, 0x18`) are simply
never asked for. If any of those 14 corresponds to "begin continuous rendering" / "enter
gameplay," that's a direct hit on the real question — worth checking which of these 14
functions look graphics/gameplay-shaped (vs audio/save/physics-only) before doing a deeper
static trace on all of them blind.

```text
boot-table fire census (Claude) -- 6/20 fire, 14/20 never fire, all in the 15.17-15.75M window
  confirms: request-driven registry, not run-everything-once
  fired ids: 2,4,0xA,0xC,0x14,0x15  |  never-fired ids: 3,5,8,9,B,D,E,F,10,11,12,13,17,18
  next: which of the 14 never-fired ids is graphics/gameplay-relevant (vs audio/save/physics)
```

### 38.1 Surface-level pass on the 14: no obvious graphics candidate popped out

Disassembled the first ~15-25 instructions of all 14 never-fired functions, looking for an
obvious tell (GS/VIF/DMAC MMIO `lui 0x1000/0x1200`, `MSCAL`, `XGKICK`-shaped code) versus
audio/save-only code. **No such signal — the opposite, in fact.** Most of the 14
(`0x1FA168, 0x205E68, 0x1FFB50, 0x1FFB60, 0x1FB0B0, 0x1FB960, 0x1FFC18, 0x205DE8, 0x200578,
0x201108, 0x202CC0, 0x204320, 0x205E30`) share a near-identical prologue shape: load a byte
flag from a fixed small offset (32-35 or 54) of a struct pointer, mask against small bit
patterns (`0x6, 0x7, 0x60, 0x80, 0xF8`), branch on state. This reads as **one homogeneous
family of per-object/entity message handlers** (consistent struct layout, generic
type/flags-byte dispatch) rather than 14 unrelated subsystems — plausibly a shared
actor/entity-component message dispatcher reused across many object *kinds*, not
graphics-vs-audio-vs-physics separated by id. `0x207E30` is the one outlier (dispatches on a
`(a2 & 0xF00)` "message type" field read from a raw buffer — looks like a lower-level RPC/
message-decode router, not an entity handler).

**No sharp filter emerged from surface inspection alone.** Given the homogeneity, picking one
of the 14 to deep-trace without a better signal risks being arbitrary. Leaving this exact
point open for whoever picks it up next — worth comparing against Grok's independent census
(in flight as of this write) in case a different angle (e.g. what argument/id each of the 14
*would* need to be requested with, or who holds the request queue that only ever asks for
`{2,4,0xA,0xC,0x14,0x15}`) sharpens it faster than reading 14 near-identical bodies cold.

```text
surface pass on the 14 never-fired (Claude) -- no sharp graphics/audio filter found
  13 of 14 share one homogeneous entity/object message-handler shape (type+flags byte dispatch)
  1 outlier (0x207E30) looks like a lower-level message-type router
  leaving open: better to find the REQUEST QUEUE that only ever asks for {2,4,A,C,14,15}
    than to keep reading near-identical handler bodies cold
```

### 38.2 Independent census dual-confirm (Grok)

Product `--pcbreak` each table fnptr, 30M host-present. Matches Claude §38:

| Result | Count | Entries |
|--------|-------|---------|
| **Fired** | **6** | `0x1F7E08`(10×@14.4M), `0x1F9DC0`(2×@14.4M), `0x1F6108`(4×), `0x1F5708`(4×), `0x1FFA00`(4×), `0x1FFAB8`(4×) — all ≤15.75M |
| **Never** | **22** | remaining table code ptrs including `0x207E30` (id 3), `0x207F28`, `0x208010`, … |

Fired set = Claude ids **{2,4,0xA,0xC,0x14,0x15}**. Request-driven registry confirmed twice.

```text
boot-table census dual-confirm
  6 fire / 22 no-fire (same 6 as Claude)
  all fires in setup window
  next: requester/dispatcher who selects those 6 ids only
```

---

## 39. Requester-side: no request queue — dense vtable install-all + specialized slot callers (Grok)

Claude handed requester-side (seq0386 ACK). Result: the "who selects only 6 ids" framing dissolves into a sharper architecture.

### 39.1 Table layout was misaligned (critical correction)

Prior census paired `{fn, next_word_as_id}` starting at `0x49AC5C`. The walker at switch case 11 uses base **`0x49AC58`** with pairs **`{id, fn}`**:

```text
0x49AC58: id=1    fn=0x001F5708
0x49AC60: id=0xA  fn=0x001FFA00
0x49AC68: id=0x15 fn=0x001F6108
0x49AC70: id=0x14 fn=0x001FFAB8   ← was mislabeled "id=2 display-env"
0x49AC78: id=2    fn=0x00207E30   ← never fires (matches census)
0x49AC80: id=3    fn=0x00207F28
... through id=0x1C (28 registry entries)
```

**Correct fired id set: `{1, 4, 0xA, 0xC, 0x14, 0x15}`** (not `{2,4,0xA,0xC,0x14,0x15}`).

Display-env stage `0x1FFAB8` is **id=0x14**, not id=2. Real id=2 (`0x207E30`) is among the never-fired set — Claude's surface-pass outlier message-router.

### 39.2 Switch `0x001FE1A0` is the message API; case 11 builds the dense vtable

```text
0x001FE1A0:  // a0=case (0..22), a1=buf, a2=?, a3=slot_count
  if (a0 >= 23) return;
  jr jump_table[a0];   // table @ 0x004B8EA0
```

| case (a0) | target | role |
|----------:|--------|------|
| 11 (0xB) | `0x001FE444` | **install registry → dense array** |
| others | various | object/msg helpers (not per-id handler calls) |

**Case 11 body (table builder):**

1. `t0 = 0x49AC58` (registry base)
2. Fill `a1[0 .. a3)` with default `0x001FFE10`
3. Walk 27 registry entries: if `id >= 0 && id < a3` then `a1[id] = fn`
4. Return

**Runtime (pcbreak `001FE1A0`, 20M host-present):** case 11 fires **once** @ cyc≈14.33M:

```text
a0=0xB  a1=0x670C18  a3=0x1D(=29)  ra=0x1E340C
```

⇒ dense vtable of **29 slots** at **`0x670C18`**, and **all 28 registry handlers are installed** (ids 1..0x1C all `< 29`). Defaults only pad empty indices.

Object root **`0x670BD0`**: dense base = `+0x48` → `0x670C18`. Slot `id` lives at `0x670BD0 + 0x48 + id*4`.

### 39.3 There is no pending-id request queue

Handlers are **never selected by scanning the registry at call time**. Call path is always:

```text
fn = *(0x670BD0 + 0x48 + id*4);   // or absolute lw of that address
jalr fn
```

hardcoded into specialized wrapper functions. Zero direct `jal` / external word-refs to any of the 6 fired fns outside the registry itself — pure `jalr` via the dense table.

### 39.4 Sole RA per fired id (pcbreak each entry, 16–20M)

| id | fn | hits | sole RA (after jalr) | slot load site | shape |
|---:|-----|-----:|----------------------|----------------|-------|
| **1** | `0x1F5708` | 4× @15.15–15.26M | **`0x1E2710`** | near `0x1E26xx` | fixed obj `a1=0x1ED0720` |
| **4** | `0x1F7E08` | 10× @14.42–14.43M | **`0x1E7588`** | `0x1E7550: lw v0,0x58(s2)` → slot[4] | **per-instance** `a1=0x1EDxxxx` varying; sizes in a2 |
| **0xA** | `0x1FFA00` | 4× @15.16–15.26M | **`0x1E26B0`** | `0x1E269C: lw v0,0x70(0x670BD0)` → slot[0xA] | fixed obj `0x1ED0720` |
| **0xC** | `0x1F9DC0` | 2× @14.44M | **`0x1E7738`** | `0x1E7708: lw v0,0xC48` → `0x670C48` slot[0xC] | instance pair a0/a1 |
| **0x14** | `0x1FFAB8` | 4× @15.16–15.26M | **`0x2222B0`** | `0x2222A8: jalr gp-23864` | **display-env** chain (known) |
| **0x15** | `0x1F6108` | 4× @15.15–15.19M | **`0x1E200C`** | `0x1E1FFC: lw v0,0xC6C` → `0x670C6C` slot[0x15] | fixed obj `0x1ED0720` |

**Asymmetry Claude flagged is free:** id=4's 10× comes from `0x1E74F0` being a **per-object-instance** factory (12 static jals into it); id=0xC's 2× is a tighter instance path. The uniform-4× group `{1,0xA,0x14,0x15}` is one coordinated setup sequence on object `0x1ED0720` / display-env, not a stage counter.

### 39.5 Switch callers (who *installs*, not who *fires* handlers)

pcbreak RAs into `0x1FE1A0`:

| RA | cases seen | role |
|----|------------|------|
| `0x1E340C` | 0, 4, **11** | main path via wrapper `0x1E33D0` (`jalr *(a0+4)` → switch) |
| `0x1E2AC0` | 7 | |
| `0x1E2D40` | 2 | |
| `0x1E2DB0` | 0x11 | |

Static jals to wrapper `0x1E33D0`: `0x1E03BC, 0x1E04E0, 0x1E2F14, 0x1E2F2C, 0x1E2F4C`.

### 39.6 Verdict and redirect

```text
requester-side RESULT
  registry @ 0x49AC58 = {id,fn} x28 (CORRECT pairing; prior id labels off-by-one)
  switch 0x1FE1A0 case11 = install-ALL into dense[29] @ 0x670C18 (once @14.33M)
  "6 of 28 fire" = 6 specialized jalr-slot call sites run; 22 slots never jalr'd
  NOT a request queue that omits 22 ids — all 28 are registered
  id labels CORRECTED: fired {1,4,A,C,14,15}; display-env is id=0x14 not 2
  asymmetry: id4=per-instance factory; {1,A,14,15}=coordinated 4x setup
```

**Redirects "gameplay ready / continuous GS":**

1. **Not** "missing registry request for a never-fired id" — those ids are installed; nothing calls them.
2. Continuous render is either (a) a call site into never-fired slots that never reaches runtime, or (b) **outside this entire object-message subsystem** (matches post-setup SIF-only / gifPath1=0 plateau).
3. Sharper next filters (pick one, no Core):
   - Static: find every `lw` from `0x670C18` range / `0x670BD0+0x48+n` and list which slot indices have *any* call site (map "who could fire the 22").
   - Or leave this subsystem: hunt post-15.75M who should kick Path1/VU1 (already cold), from main-loop / thread entries rather than boot registry.

No Core. Canaries under `out/canaries/b3-requester/` (gitignored).

---

## 40. Real id=2 (`0x207E30`) is a color-format pack/unpack routine — the sharpest never-fired candidate yet (Claude)

Grok's §39 correction (table is `{id,fn}` not `{fn,id}`) re-identifies the real id=2 as
`0x207E30` — the one function my §38.1 surface pass already flagged as structurally different
from the other 13 (a `(a2 & 0xF00)`-keyed dispatcher rather than a generic entity-handler
prologue). Read its full body (`disasm ... 00207E30:200`) instead of just the opening
instructions this time.

**It's a 16-bit packed-color pack/unpack routine, not a generic message handler:**

- Case `a2&0xF00==0x100`: takes 4 input bytes, does `sll v0,7 / andi 0x7C00`,
  `sll v1,2 / andi 0x3E0`, `srl a0,3`, then ORs them together — textbook 8-bit-per-channel
  RGB(A) → 16-bit `0555`/`1555`-style packed pixel format (5 bits red, 5 bits green, matching
  bit positions exactly). Stores the packed 32-bit result via `sw v0,0(t0)`.
- Case `0x500`: multiplies by `0x808081` then takes the high bits (`mult`/`mflo`) — the
  classic fixed-point "divide by 255" trick used for 8-bit color/alpha normalization.
- The sibling function right after it (`0x207F28`, same `(a2&0xF00)` dispatch shape) does the
  **inverse**: unpacks a 16-bit packed color back into separate bytes (`srl`/`andi` extracting
  5-bit fields, `sb` stores), plus its own `divu`-based case — a matched pack/unpack pair.

This is exactly the shape of code used to convert between game-side RGBA color data and the
GS's native pixel/vertex-color formats — the kind of routine you'd expect to sit directly in
a real per-frame (or per-vertex-color-update) render-submission path, not in an
audio/save/physics entity handler. It's also the most structurally distinct of all 14
never-fired functions from the surface pass in §38.1 — everything else in that set was the
homogeneous entity-message-handler shape; this one stands alone.

**Not proof by itself** — a color-packer could plausibly belong to a UI/HUD color-blend path
instead of the main 3D render path, and it still hasn't been shown to have *any* call site
(reached or not) rather than none at all. But given Grok's open question ("which of the 22
even has a call site"), this is the one candidate worth a static call-site search first rather
than picking blind — the functional signature is a real, on-topic match for "graphics id."

```text
id=2 body read (Claude) -- real id=2 = 0x207E30, a 16-bit packed-color pack/unpack routine
  case 0x100: 8-bit RGB(A) -> 0555/1555-style packed pixel, sll/andi bit-position match exact
  case 0x500: x*0x808081>>N divide-by-255 fixed-point trick (alpha/color normalize)
  sibling fn 0x207F28: inverse unpack, same dispatch shape, matched pair
  structurally the ONE outlier among 14 never-fired -- everything else was generic entity-handler
  sharpest concrete candidate yet for "graphics-relevant id that's never requested"
  next: static call-site search specifically for this id's vtable slot (Grok's open Q1)
```

---

## 41. id=2 (color-pack) callsite: real wrapper, dead-at-source one level up (Grok)

Claude S40: `0x207E30` is 16-bit 0555/1555 pack (case 0x100) / fixed-point normalize (case 0x500); sibling `0x207F28` (id=3) unpacks. Graphics-adjacent outlier among never-fired.

### 41.1 Proven dense-slot call site

```text
0x001E6890:  // thin wrapper — no static callers
  lui   v1, 0x67
  ...
0x001E68A0:  lw    v0, 0xC20(v1)   // 0x670C20 = dense[2] = id2 handler
0x001E68A8:  jalr  v0
0x001E68AC:  move  a0, sp          // stack buffer out

0x001E68C0:  // sibling wrapper for id=3
0x001E68D4:  lw    v0, 0xC24(v1)   // dense[3]
0x001E68DC:  jalr  v0
```

High-confidence scan (`lui 0x67` + `lw` into dense range + `jalr` same reg): **slot[2] has exactly this one wrapper**. Not "nothing ever reads vtable-slot[2]" — the wrapper does. (Noisy bare `lw off=0x50` hits are mostly stack/struct false positives.)

### 41.2 Runtime: zero hits

`--pcbreak=001E68A0:001E68A8` and `--pcbreak=00207E30` over 20M host-present: **0 PCBREAK lines** each.

### 41.3 Static: wrapper itself is unreferenced

| Query | id2 wrap `0x1E6890` | id3 wrap `0x1E68C0` | id4 factory `0x1E74F0` (fires) |
|-------|---------------------|---------------------|--------------------------------|
| `jal` | **0** | **0** | **12** |
| `j` / bal / branch | 0 | 0 | 0 |
| word ptr in ELF | 0 | 0 | 0 |
| `lui`+addiu construct | 0 | 0 | n/a (has jals) |

Same shape as dead `0x1A6290` / `0x248518`: **handler installed, thin wrapper exists, nothing in the binary ever targets the wrapper**. Dead one level above the color-pack body — not a missing runtime precondition on a live call edge.

### 41.4 Side note: many never-fired slots have similar wrappers

High-confidence dense-slot wrappers also exist for ids `{6,7,8,9,0xD,0xF–0x13,0x17–0x19,0x1C}` (mostly `0x1E7xxx`). Fired ids that appear in this scan: `{0x14,0x15}` (plus other non-`lui 0x67` paths for `{1,4,0xA,0xC}`). Full "which of 22 have any live call edge" still open if wanted; id=2 specifically is answered.

```text
id2 callsite RESULT
  real wrapper 0x1E6890 jalrs dense[2]; sibling 0x1E68C0 -> dense[3]
  runtime 20M: zero hits on wrapper and on 0x207E30
  static: wrapper has zero jal/j/bal/word/construct refs (dead-at-source)
  NOT "no one reads slot[2]"; IS "no one calls the only reader"
```

---

## 42. Shared-wrapper-caller hunt: dead bulk pipeline `0x1E9C10`, not a generic id dispatcher (Grok)

Claude S41 follow-up: many never-fired slots share dead wrappers in `0x1E7xxx` — propose find ONE shared caller into that family.

### 42.1 Not one generic dispatcher

High-confidence dense-slot wrappers (`lui 0x67` + `lw` dense + `jalr`): 20 entries. Their **static `jal` edges cluster**, they do not fan into a single `jalr table[id]` dispatcher.

| Wrapper entry | slot(s) | static jals | notes |
|---------------|---------|------------:|-------|
| `0x1E6890` | 2 (id2 pack) | **0** | fully unlinked |
| `0x1E68C0` | 3 (id3 unpack) | **0** | fully unlinked |
| `0x1E7138` | 7 | **11** | all from dead cluster |
| `0x1E7A80` | 16 | **16** | all from dead cluster |
| `0x1E70D0` | 6 | 1 | dead cluster |
| `0x1E1FF0` | 21 (id15, **fires**) | 4 | **live** edges |
| `0x1E74F0` | id4 factory (**fires**) | 12 | **live** (RAs `0x205Dxx`/`0x1FE7xx`/`0x211Exx`) |

No consecutive fnptr table of wrappers found in ELF data.

### 42.2 The cluster is bulk pipeline `0x001E9C10` — unrolled, unlinked

```text
0x001E9C10: addiu sp, sp, -1216   // huge frame
            saves s0-s6, ra; s2=a0 (object), s4=a1
...
0x001E9C68: jal 0x1E70D0   // slot6 wrap
...
0x001E9FC0: jal 0x1E7138   // slot7 wrap
0x001E9FC8: jal 0x1E7A80   // slot16 wrap
// repeated unrolled pairs in loop-ish control flow with a2=5 helpers
```

- **Static jals TO `0x1E9C10`: 0** (dead-at-source at the pipeline root)
- **Runtime `--pcbreak=001E9C10` 18M: 0 hits**
- Also 0 hits on `0x1E7138`, `0x1E7A80`, `0x1E9FC0` individually

So wrappers with "many jals" are only linked from this **unreached tree** — static edges, zero dynamic fire. Same unarmed shape as blit/alarm/id2, but one level higher: the whole bulk asset/object pipeline never runs.

### 42.3 Live fires are a *different* path family

id4 factory `0x1E74F0` **does** run (10× @14.4M, RAs from live object creators). id15 wrap `0x1E1FF0` has live jals. id14 uses `0x2222xx` + gp. The successful 6 are **not** fed by `0x1E9C10`.

### 42.4 Related (possibly gated) large pipelines

| Entry | frame | jals from | role guess |
|-------|------:|-----------|------------|
| `0x1E92B8` | −1696 | `0x1EB14C` | alt bulk path |
| `0x1E9530` | −1856 | `0x1EB13C` | alt bulk path |
| `0x1EB118` | −16 | ? | gp+`0x670BD0` gate: if `*(gp_off+0x670BD0+0x1C)!=0` then `1E9530` else `1E92B8` |

`0x1E9C10` itself is **not** behind that gate — it has zero callers of any kind.

### 42.5 Verdict

```text
shared-caller RESULT
  NOT a generic id→wrapper dispatcher
  IS a dead bulk unrolled pipeline @ 0x1E9C10 (0 static callers, 0 runtime hits)
  that would jal many never-fired wrappers (6,7,16,...) if ever entered
  id2/id3 not even on that pipeline (0 jals anywhere)
  live 6 use separate specialized factories (id4 etc.)
  next if pursuing: who should call 0x1E9C10 / arm 0x1EB118 gate
    — OR leave subsystem (post-setup GS still the product gap)
```

---

## 43. `0x1EB118` gate closed: also zero callers (Grok)

Quick close-out per Claude split (seq0392).

### Static

| Symbol | Addr | jals **to** it | notes |
|--------|------|---------------:|-------|
| gate | `0x001EB118` | **0** | also 0 word refs |
| pipe A | `0x001E92B8` | 1 (`0x1EB14C` = **inside gate**) | |
| pipe B | `0x001E9530` | 1 (`0x1EB13C` = **inside gate**) | |

Gate body (confirmed):

```text
0x1EB118:  // gp-relative + 0x670BD0
  v0 = *(gp-24248) + 0x670BD0
  v1 = *(v0 + 0x1C)
  if (v1 != 0) jal 0x1E9530; else jal 0x1E92B8
```

The "1 jal each" on the pipes are **only** from this unlinked gate — not external live edges. Same shape as `0x1E9C10`: internal wiring, zero entry from the rest of the binary.

### Runtime 18M host-present

`--pcbreak` on `0x1EB118`, `0x1E92B8`, `0x1E9530`: **0 hits** each.

### Verdict

```text
0x1EB118 CLOSED
  gate has 0 static callers / 0 runtime hits
  pipes only reachable via dead gate
  NOT a live loose thread — 5th dead-at-source island in this vein
  vein fully exhausted for static-unlinked bulk paths
```

Claude's mode-check / live-6-vs-dead-pipeline decision-point hunt is the right pivot. No Core.

---

## 44. id4's 10 creators are 6 disparate one-time call sites, not an ongoing spawn loop — proposes stepping back to Path3 itself (Claude)

Started the mode-check pivot by tracing id4's factory (`0x1E74F0`, fires 10x) to its real
callers, since it's the highest-frequency member of the "live 6." `scanword` for `jal
0x1E74F0` found **12 static call sites**; PC-hit census (temp, reverted) against all 12 across
the full 30M run:

```text
live (sum to exactly 10):  0x1FE7C8 x1   0x1FE9B0 x1   0x205D50 x1   0x205D6C x1
                            0x211E24 x3   0x211E3C x3
dead (0 hits, same family as the S42/S43 islands):
                            0x1E703C  0x1E71D8  0x1E72E4  0x1E9410  0x1E96C8  0x1FBF08
```

Four singleton call sites (one object each — plausibly player car / camera / HUD / similar
one-off) plus one pair (`0x211E24`/`0x211E3C`, a different code region entirely from the
`0x1FE7xx`/`0x205Dxx` cluster) that each fire exactly 3x — a small, fixed-count loop
(`for i in 0..3`), not a streaming/ongoing spawn mechanism. **None of the 6 live creators show
any sign of being a per-frame or continuously-re-entered call** — every one of them looks like
a genuine one-shot init, consistent with everything else traced across §25-§43 tonight.

### 44.1 Reframe

Combined with Grok's CDVD census (§32: exactly 1865 sectors read, flat after 30M — a fixed,
not truncated-looking, amount) and the boot-table fire census (§38: exactly 6/28 subsystems
requested, all bounded): **the object/entity-creation phase for this scene looks complete,
not broken or cut short.** A handful of singleton objects plus a 3-count loop is a plausible
exact object list for whatever scene B3 is in at this point (title screen / car-select /
attract — genuinely not yet in a race, matching Grok's earlier framing), not evidence of
truncated level data.

**If object creation is legitimately finished and correct, the missing piece isn't "more
objects should have been created" — it's squarely back to "what should run every frame/vblank
regardless of how many objects exist."** Five separate dead-code islands (§27, §35, §41,
§42, §43) all turned out to be unrelated to this — none of them were ever going to be the
per-frame render trigger, they were disconnected alternate/unused paths in an entity-message
subsystem that has nothing to do with why VU1 never executes an instruction (§30) or why
Path3's held queue never resolves (§0-§11, the very first finding in this document).

### 44.2 Proposal: revisit Path3's own held-queue data with fresh eyes

We've spent §12-§43 working outward from Path3/DMAC almost entirely through *adjacent*
subsystems (flip flags, boot dispatch, entity messages) without yet returning to the single
most direct piece of evidence this whole document opened with: **Path3 itself has a real,
non-empty held backlog (`heldP3n=5 heldP3qwc=2124`, §1) that never drains, because the last
`MSKPATH3` is a mask with no matching unmask (§0).** Every subsystem we've now ruled out
tonight was reached by working *away* from that fact. Proposing we go back to it directly:
find the code that issues the *unmask* — the write to VIF1's `MSKPATH3` bit that should follow
the game's last `MSKPATH3` mask and never comes — using the exact write-site-tracing method
that worked for finding the mask write in §9, applied to the (presumably nearby) unmask
write instead. That's a direct, close-to-the-metal target we haven't actually gone after yet,
as opposed to another hop through the entity-message subsystem.

```text
id4 creator census (Claude) -- 6 disparate one-time call sites, not an ongoing spawn loop
  4 singletons (1x each) + 1 pair (3x each, different code region) = 10, sums exactly
  none show per-frame/streaming shape -- object creation looks legitimately complete
  reframe: 5 dead-code islands tonight were all unrelated tangents from the entity-msg system
  proposal: stop working outward from Path3/DMAC, go back to Path3's OWN held-queue directly --
    find the missing VIF1 MSKPATH3 UNMASK write-site, mirroring the S9 mask-write methodology
```

---

## 45. §9's mask/unmask buffer IS consumed 3x, in lockstep, both codes together — correction, not a missing kick (Claude)

Started on my own §44.2 proposal directly: re-examined §9's static mask/unmask template
buffer (`0x007FC880`-ish, unmask QW at `0x7FC8FC`, mask QW at `0x7FCA80`). First pass (a plain
`--watch`, no cycle info) looked like each word was read only once — misleadingly suggested
the buffer might never really be DMA-consumed at all. Added a temporary cycle-stamped variant
of the watch tool (`WatchHitsCyc`, reverted after use) to check properly before reporting
anything, since the plain `--watch` output doesn't carry cycle numbers and a same-line-content
`uniq -c` silently collapses distinct events.

**Corrected result — both words are read 3 times each, at the identical 3 cycles, from the
identical 3 PCs:**

```text
mask   (0x7FCA80): cyc=15169648 pc=0x1F30B4 READ | cyc=15250448 pc=0x228048 READ | cyc=15500448 pc=0x113F38 READ
unmask (0x7FC8FC): cyc=15169648 pc=0x1F30B4 READ | cyc=15250448 pc=0x228048 READ | cyc=15500448 pc=0x113F38 READ
```

These three cycles line up almost exactly with §27.2's three real DMACKICK rounds
(15169584/15250192-384/15500192-384) — this buffer's segment **is** delivered via real
VIF1 DMA, once per kick round, and **both codes are read together as one contiguous segment
each time** (matches `Dmac.DeliverSegment`'s "batch the whole segment as one VIF stream"
comment — `ProcessStream` reads a contiguous MADR span, naturally covering both offsets in
one pass when they're both inside the same delivered segment).

### 45.1 Revises §9.5's framing

§9.5 asked "does anything re-kick the unmask offset of the same buffer" as if unmask might be
getting skipped while mask keeps firing. **It doesn't get skipped — it's delivered exactly as
often as mask, every time.** But the buffer lays unmask at the *lower* offset (`+0x70`) and
mask at the *higher* offset (`+0x200`); a single contiguous forward-order read naturally
processes unmask first, then mask second, **within the same delivery**. If VIF applies them
in the order it reads them, every one of these 3 rounds legitimately ends up re-masked
immediately after briefly unmasking — by the buffer's own layout, not a missing kick. That
would explain `m3p=True` persisting through all 3 rounds without any kick being dropped.

### 45.2 What's still actually open

This does **not** explain why the *pre-existing* held backlog (`heldP3n=5 heldP3qwc=2124`,
the document's very first finding, §1) never drains during the brief unmasked window each
round — if unmask really is processed (even briefly) 3 times, a real GS should have gotten at
least some chance to drain queued Path3 data each time, unless the window is too short, or
the held backlog itself was queued *before* this buffer's own 3 rounds and something else
governs whether held data specifically gets serviced during a transient unmask vs only new
incoming data. That distinction — held-queue drain policy during a transient unmask window —
is the sharper, still-unanswered question this correction leaves behind, closer to the
metal than anything in §12-§44's excursion through the entity-message subsystem.

```text
mask/unmask buffer re-check (Claude) -- corrects an over-hasty first read, real result:
  BOTH words read 3x each, IDENTICAL cycles/PCs, matching the 3 known DMACKICK rounds exactly
  buffer IS delivered via real VIF1 DMA each round -- not a missing kick, S9.5's framing revised
  unmask (lower offset) + mask (higher offset) delivered together, forward order -> re-masked
    by the buffer's own layout each round, not a dropped kick
  real open question: why doesn't the PRE-EXISTING held backlog drain during these transient
    unmask windows -- held-queue-during-transient-unmask policy, not a missing DMA event
```

---

## 46. Held-queue drain policy during transient unmask — already works; final hold is post-last-unmask (Grok)

Took Claude S45's drain-policy question (seq0395). Read `Gif.cs` + reconciled against §3 timeline. **No Core.**

### 46.1 What `Gif.cs` does on unmask (load-bearing)

```csharp
// Vif.cs MSKPATH3 → Gif.SetMskPath3((imm & 0x8000) != 0)
public void SetMskPath3(bool masked)
{
    _m3p = masked;
    if (!masked)
        DrainHeldPath3();   // synchronous, before VIF continues to next code
}

private void DrainHeldPath3()
{
    if (_heldPath3Count == 0) return;
    if (_pktActive && _pktPath == 2) return;  // Path2-sticky only gate
    // snapshot queue, clear held, ProcessTransfer(addr,qwc) each entry FULLY
    // (not ProcessTransferBudgeted — no mid-drain budget split)
}
```

Policy properties:

| Property | Behavior |
|----------|----------|
| Trigger | Every `SetMskPath3(false)` (MSKPATH3 unmask or M3R clear) |
| Timing | **Synchronous** inside the VIF command that unmasks — next VIF code (e.g. re-mask) cannot run until drain returns |
| Path2 gate | Only if `_pktActive && _pktPath==2`; §3 measured **BLOCKED_PATH2_STICKY ×0** |
| Completeness | Held entries use `ProcessTransfer` (full QWC), not budgeted residual |
| Incoming under mask | `ReceivePath3Data` → `EnqueueHeldPath3` while `Path3Masked` |

⇒ There is **no** HLE policy that "skips held backlog during a brief unmask" or only processes new data. Unmask drains **all** currently held entries, then VIF may re-mask from the same segment (S45 layout).

### 46.2 Reconcile S45 with §3 timeline

S45: unmask@`0x7FC8FC` + mask@`0x7FCA80` in one forward VIF segment, 3× lockstep with DMACKICK rounds → each round briefly unmasks then re-masks by buffer layout. Correct and important.

§3 already logged the drain effect of those unmasks:

| MSK event | masked | heldN before | Action |
|----------:|:------:|-------------:|--------|
| 6 | False | **6** | **Drain START** → empties |
| 7 | True | **0** | re-mask (held empty) |
| 8 | True | 5 | already masked; queue refilled under mask |
| 9 | False | **6** | **Drain START** → empties |
| 10 | True | **0** | **final mask** |
| after 10 | True | →5 / 2124 | third batch enqueued; **no event 11 unmask** |

**The three transient unmask windows do drain.** §3 `DrainHeldPath3 START ×3` + heldN=0 on the following mask events are the proof. End-state `heldP3n=5 heldP3qwc=2124` is a **new third batch** filled **after** the last unmask/re-mask pair — not a pre-existing backlog that survived the three windows.

Product metrics match: `p3=20 p3qws=6408` (earlier batches processed) + final held 2124 (third batch never unmasked) + `px=877187` plateau from prior drains.

### 46.3 What S45 still sharpens (without changing drain policy)

1. **Re-mask is by design of the template buffer**, not a dropped unmask kick — S9.5 framing correctly revised.
2. **Within each of the 3 rounds**, order is unmask → (sync drain) → mask → later PATH3 under M3P refills held.
3. **Missing piece is still EE-side:** after the third refill under final mask, **no 4th MSKPATH3 unmask** is ever issued (through 50M/100M). Not "drain policy ignores held during brief unmask."

### 46.4 Verdict

```text
drain-policy RESULT (Gif.cs + S3 reconcile)
  SetMskPath3(false) fully drains held queue synchronously (ProcessTransfer, no budget cut)
  Path2-sticky gate idle (S3 BLOCKED×0)
  S45 3× unmask windows DID drain (S3 Drain START×3; heldN=0 after)
  end-state held 5/2124 = third batch AFTER last re-mask; no 4th unmask
  NOT an HLE held-vs-new discrimination bug
  open: why EE never issues MSKPATH3 unmask after third refill (back to S7.1, sharpened)
```

Next (Claude's S44.2 still valid, re-scoped): find who should issue the **4th** unmask / what condition arms another MSKPATH3-unmask after the third DMACKICK round — not more entity-msg, not drain-policy Core. Optional measure-only force-unmask A/B remains dual-ACK (§7.2).

---

## 47. Synthesis: the "4th unmask" isn't missing — it's the same bounded chain from §25-§31, and it's supposed to stop (Claude)

Before starting a fresh static hunt for "who should issue the 4th unmask," checked whether we
already have the answer from earlier tonight — we do.

§28 already fully traced *the* single call site that arms this entire mask/unmask/kick chain:
`0x1FFAF4`, the **sole** static caller of `0x1F2960` (which builds the GS packet and arms
`gp-24112`). It fires **exactly 4 times**, cycles `15,166,704 / 15,180,144 / 15,192,816 /
15,264,592`, always against the **same fixed target object `0x006754C0`** — which §29
independently confirmed is the `PutDispEnv` display-env object, not track/level geometry —
with a plain incrementing stage counter (`a1 = 1, 2, 3, 4`). §37's boot-table read showed this
whole thing is dispatched from a **bounded, `id`-keyed one-shot registry**, not a per-frame
loop. Downstream, §46 shows the mask/unmask cycle drains correctly on unmask (twice, cleanly:
events 6→7 and 9→10 both empty the held queue via `Gif.cs`'s synchronous `DrainHeldPath3`).

**Putting these together: this whole complex — `0x1FFAF4`'s 4-stage display-env setup →
`0x1F2960` → `gp-24112` arm → scheduler `0x1F43B0` → producer `0x1F2408` → DMAC handler
`0x1F1778` → the mask/unmask template buffer (§9/§45) → real VIF1/GIF kicks (§27.2) — is one
single, bounded, correctly-functioning boot-time display-environment setup routine that is
*supposed* to run a fixed number of stages and then stop.** There is no missing "5th stage" or
"4th unmask" to find in this chain, any more than there's a missing 5th call to `0x1FFAF4`
itself — it's a 4-stage init, by design, and every layer downstream of it (drain policy,
buffer layout, DMAC completion, VBlank consume-gate) has now been read and confirmed correct.

### 47.1 What this settles, across the whole night

Every subsystem investigated from §12 through §46 — DMAC completion interrupts, the scheduler
tie-break, the boot-stage dispatch table, five independent dead-code islands, the entity
message subsystem, VIF1's command-type breakdown, thread/semaphore census, the VBlank ISR's
own gate, and now the mask/unmask buffer's drain behavior — has been **confirmed correct and
complete**, not buggy. The held Path3 backlog (`heldP3n=5 heldP3qwc=2124`, this document's
very first finding) is the honest, expected residue of a one-shot setup routine finishing its
bounded work while masked, not evidence of a stuck or dropped mechanism.

### 47.2 What's actually still missing

**A completely separate piece of code**, never yet located, that's supposed to run on an
ongoing (per-frame or per-vblank) basis during real gameplay: issuing its own fresh
MSKPATH3 unmask/mask cycles, driving VU1 execution (§30: `Vu1.MscalRuns=0` the entire run —
VU1 never executes a single instruction), and producing Path1 GIF traffic (§30:
`gifPath1=0` the entire run). Nothing in the `0x1FFAF4` chain, the entity-message subsystem,
or the boot-table registry was ever going to be that code — they're all one-shot,
already-confirmed-complete initialization. The real search target is B3's actual per-frame
render-submission entry point, structurally unrelated to everything traced tonight, most
likely reached from wherever the game's main loop decides "we are now in a race / gameplay
state" (still unconfirmed whether this run ever reaches that state at all — genuinely open,
not investigated with the same rigor as the setup-chain work).

```text
synthesis (Claude) -- the "missing 4th unmask" question is already answered, not open
  0x1FFAF4 (S28) -- sole caller, fires EXACTLY 4x, fixed target = display-env object (S29)
  boot-table (S37) -- bounded one-shot registry, not per-frame
  drain policy (S46) -- correct, empties held queue cleanly on every real unmask
  => the WHOLE S9/S25-S31/S45/S46 chain is one bounded, correct, COMPLETE setup routine
  => no missing 4th unmask to find -- it's a 4-stage init that's supposed to stop
  still missing: a SEPARATE per-frame/per-vblank mechanism, never located tonight, that
    should drive ongoing MSKPATH3 cycling + VU1 execution + Path1 traffic during real gameplay
```

---

## 48. State question: not lit frontend waiting for Start — black post-setup plateau; pad no-ops (Grok)

Claude S47 synthesis accepted: S9–S46 chain is one bounded correct display-env init. Open = game state / ongoing per-frame trigger.

### 48.1 Agree synthesis

- `0x1FFAF4` ×4 stages on fixed `0x6754C0` = finished setup, not starved
- Drain policy correct (S46); object creators one-shot complete (S44)
- Missing piece is **not** inside that chain

### 48.2 State probe (tip ~2346be1 / current main)

| Run | cyc | cdvd | px | gifP1 | heldP3qwc | lit | mostlyBlack | notes |
|-----|----:|-----:|---:|------:|----------:|----:|:-----------:|-------|
| baseline | 30M | **1865** | 877187 | 0 | 2124 | 0 | 1 | tid5 current; plateau |
| +`b3-menu-interactive.pad` | 50M | **1865** | 877187 | 0 | 2124 | 0 | 1 | Start/Cross @28–50M; **identical GS** |
| older 50M profile (prior tip) | 50M | 609 | 877187 | 0 | 2124 | 0 | 1 | same gfx plateau |

Pad script comment targets Soft-GS FRONTEND ~40M with **cdvd≈6584**. We plateau at **1865** (or 609 on older tip) — well short of that spine. Richer START/CROSS does **not** move cdvd, gif paths, held queue, or lit.

### 48.3 What state this is / is not

| Hypothesis | Verdict |
|------------|---------|
| Lit title/frontend chrome waiting for Start | **No** — `lit=0` / `mostlyBlack=1` / no DISPFB paint; Start/Cross no-op on GS |
| Race gameplay / VU1 active | **No** — `gifPath1=0`, `MscalRuns=0` (S30), forever |
| Mid-stream truncated disc load still progressing | **Unlikely at 30–50M** — cdvd flat after setup window; no further sectors |
| Post display-env setup, EE thrash, no continuous GS | **Yes** — matches S29.4 transfer census (SIF-only post-setup) |

So: **not** "sitting correctly on a real menu waiting for a button we haven't pressed." Closer to **post-one-shot-setup idle with black display-env**, where something that should both (a) pull more assets / advance mode and (b) arm per-frame MSKPATH3+VU1 never starts. Pad is not the missing trigger for *this* plateau.

### 48.4 Implications for next hunt

1. **State-first still right**, but the state is "black post-setup", not "frontend interactive."
2. Code-side target reframe: who advances **mode / load phase** after display-env stage 4 — the thing that would request more CDVD (toward 6584) **or** arm continuous render — not more of the finished S25–S31 chain.
3. Pad A/B at 100M with the interactive script is low value while cdvd/gif are frozen at 1865/held-2124; only re-try if a load/mode advance is restored first.

```text
state RESULT
  agree S47: setup chain finished correctly
  NOT lit menu waiting for Start (pad 50M no-ops; lit=0)
  IS black post-setup plateau: cdvd flat 1865, gifP1=0, held third batch, EE thrash
  next: mode/load phase after display-env stage4 (not 4th unmask inside finished chain)
```

---

## 49. Found existing prior-campaign machinery: an IOP/FILEIO escape chain, and a likely real limiter — a single 64KB iovec slice with a hard terminator (Claude)

Before starting a fresh static hunt for "what advances mode/load after stage 4," checked
whether this repo already has prior work on B3 specifically — it does, extensively.
`src/DetPS2.Core/GameQuirks/Burnout3Assist.cs` (2496 lines, dated 2026-07-30) is an entire
prior campaign's worth of named, staged IOP/IRX/LGDEV/CDVD boot-progress assists, already
wired into `OnHostPresent` and active during every run tonight. It already defines the exact
sector-count buckets we've been feeling around for blind: `irxOnly` (400-600),
`stagePlantOnly` (600-2000), `postTxd` (>=2000, triggers `MaybePlantFrontendTxd`),
`frontendEra` (>=6000) — our whole night's `cdvdSectors=1865` final state sits squarely in
`stagePlantOnly`, one bucket short of `postTxd`.

### 49.1 Live trace of this machinery (temp `DETPS2_TRACE_BIOS=1`, no Core, just enabling existing tracing)

Ran the standard 30M-cycle command with this **already-existing** trace flag on (not new
instrumentation — just reading output the codebase already produces):

```text
cyc=18.0M-20.0M   LGDEV entry/CallRpc stub plants
cyc=22.3M-26.5M   boot-wait-flag plants ×12 (cdvd still 425 throughout)
cyc=28.0M         plant STAGEHED @ 0x01900000 size=374784 (real DATA/STAGEHED.BIN off the ISO)
cyc=28.85M        escape empty iovec  n=1  cdvd=609 -> jumps to cdvd=1865 shortly after
cyc=34.7M-47.5M   boot-wait-flag plants continue (n=32, n=64) -- cdvd stays flat at 1865
```

**This confirms Grok's flat-1865 finding directly from the source, and narrows it to one
specific event: the single "escape empty iovec" firing (`n=1`) is what produces the whole
609→1865 jump, and it never fires a second time (`_ioQueueEscapes` caps at 256, but nothing
after `n=1` ever re-enters the scanning PC range that would trigger `n=2`).**

### 49.2 The likely limiter: a 64KB single-slice plant with an immediate terminator

Read `MaybeEscapeEmptyIoQueue`/`MaybePlantStageAssets` directly. `STAGEHED.BIN` is read in
full from the real mounted ISO (374,784 real bytes, genuinely off-disc — this is not
fabricated data) and placed correctly in RDRAM. But the iovec entry the escape assist plants
for the game's own iovec-walk loop is:

```csharp
uint plantSize = Math.Min(_stageHedSize, 0x10000u); // first 64KiB slice
sys.Memory.Write32(s4 + 0, _stageHedEeAddr);
sys.Memory.Write32(s4 + 4, plantSize);
// Terminator after one entry.
sys.Memory.Write32(s4 + 8, 0);
sys.Memory.Write32(s4 + 12, 0);
```

**Only the first 64KB of the 374,784-byte real asset is ever exposed to the game's own
consume loop (`jal 0x123F58` at the redirect target `0x122A18`/`0x122A40`), followed
immediately by a hard `{0,0}` terminator entry.** If the real game logic expects to walk a
multi-entry iovec list (one entry per chunk) until it has consumed the whole 374,784-byte
asset — which is the natural read of "iovec queue," and consistent with `_ioQueueEscapes`
being designed to support up to 256 separate escape events — then this single-shot,
hard-terminated plant would explain *exactly* the observed shape: one real unlock (609→1865)
then a permanent stop, because the loop is told "there is nothing more" after just the first
17.5% of the real asset.

### 49.3 Why this is worth prioritizing over more static tracing

This isn't a hypothesis built from scratch tonight — it's a concrete, already-identified
mechanism, already reading real ISO bytes, that stops one step short of what its own design
(`_ioQueueEscapes` cap of 256, multi-chunk framing) suggests it was meant to do. Per this
project's standing doctrine (find the missing real mechanism, don't hand-synthesize the
end state), the fix shape — if this is confirmed — would be: **plant the full iovec chain
across as many 64KB-capped entries as `STAGEHED.BIN`'s real size requires (ceil(374784/65536)
= 6 entries), terminating only after the last real chunk**, rather than always stopping at
one. Not yet implemented or even fully confirmed (haven't verified the consumer at `0x123F58`
actually re-enters the scan loop for a second entry rather than doing something else with a
multi-entry list) — flagging this now because it's the sharpest, most concrete lead of the
whole night and directly explains the exact plateau number (1865) rather than requiring more
guessing.

```text
existing B3 quirk machinery (Claude) -- found real prior work, a likely concrete limiter
  Burnout3Assist.cs (2026-07-30): full staged IOP/CDVD boot-assist chain, already active
  DETPS2_TRACE_BIOS=1 (existing flag) shows exactly ONE "escape empty iovec" firing (n=1)
    produces the whole 609->1865 jump, then permanent silence through 50M
  STAGEHED.BIN (374,784 real ISO bytes) loaded correctly, but iovec plant exposes only
    the FIRST 64KB with a hard terminator right after -- likely why n=2 never fires
  proposed fix shape (not yet implemented): plant the full 6-entry chain instead of 1,
    terminate only after the real last chunk -- matches the "find the real mechanism,
    don't hand-synthesize" doctrine
  next: confirm the 0x123F58 consumer actually wants to re-enter the scan for more entries
    before touching Core -- this needs dual-ACK + design doc like any other Core change
```

---

## 49.1 Consumer confirmation: multi-entry walk is real; 64KiB was intentional anti-hugeCopy (Grok)

Claude S49: STAGEHED fully in RDRAM; iovec plant exposes only first 64KiB + terminator; n=1 only. Proposed multi-entry plant. Grok took confirm (a), no Core.

### Walker at `0x00122988`–`0x001229EC` (confirmed wants multi-entry)

```text
0x122988: if s2!=0 goto consume_chunk   # residual size in current entry
0x122990: s2=size(s4); s3=ptr(s4)
0x1229A4: if s2==0 goto 0x122990        # skip empties
0x1229A8: s4 += 8                       # delay slot ALWAYS — after load, s4 already at NEXT entry
0x1229B0: jalr callback(a1=s3, a2=min(s2,1024))  # 0x123F58-class memcpy consume
0x1229D8: s3 += n; s2 -= n; budget -= n
0x1229E4: if budget!=0 goto 0x122988    # more budget → if s2==0, load NEXT iovec
0x1229EC: -> 0x122CBC success (budget exhausted)
```

**Yes — when current entry is fully consumed and stream budget remains, it loads the next `{ptr,size}` at `s4`.** Terminator `{0,0}` after a single 64KiB entry ends the useful walk (empty spin or success with partial STAGEHED). Multi-entry list is the natural shape.

`0x123F58` is a memcpy (a0=dst, a1=src, a2=len) — not a multi-iovec parser itself; the **walker** is.

### Why single 64KiB on 2026-07-30 (not unfinished by accident)

```csharp
uint plantSize = Math.Min(_stageHedSize, 0x10000u); // first 64KiB slice
// Terminator after one entry.
```

Same function guards:

```csharp
bool hugeCopy = pc is >= 0x00124020 and <= 0x00124050
    && a2 > 0x10000;
// ... if hugeCopy → empty-epi escape, skip natural consume
```

Planting **full 374784 in one `{ptr,size}`** would set `a2 > 0x10000` on the memcpy path and hit the **hugeCopy escape** (forces empty success instead of real consume). The 64KiB cap is **intentional** to stay under that threshold.

Design intent of `_ioQueueEscapes` cap 256 + "first 64KiB slice" comment: **progressive re-plant of successive slices** on later empty hits. Observed: **n=2 never fires** — after first plant, `!empty` early-return means no second slice; walk finishes/spins without presenting a clean empty+inScan window for slice 2.

### Plateau arithmetic

| Item | Value |
|------|------:|
| STAGEHED.BIN | 374 784 B |
| Planted iovec size | 65 536 B (1/6) |
| Full file as sectors | ≈183 |
| Host-credited plant (full file to RDRAM) | `NoteHostReadSectors` whole file → cdvd jump 609→1865 |
| Assist buckets | stagePlantOnly `[600,2000)` ← **we sit here**; postTxd `≥2000` never reached |

cdvd credits the **full** file into RDRAM, but the **game only consumes 64KiB** via iovec → mode never reaches postTxd/FRONTEND plants.

### Confirm verdict for Claude's split

| Question | Answer |
|----------|--------|
| (a) Does consumer want a second entry? | **Yes** — walker reloads next iovec when entry exhausted and budget remains |
| Why single slice? | **Intentional** anti-`hugeCopy` (a2≤0x10000); multi-escape progression **failed** (n=2 never) |
| Fix shape | Still dual-ACK / design-doc: plant **chain of ≤64KiB entries** covering full STAGEHED in **one** plant (or cursor-based re-plant on subsequent empties). Real ISO bytes only. No invent. |

```text
iovec consumer CONFIRM
  walker multi-entry: YES (s4 advances; residual s2==0 loads next)
  0x123F58 = memcpy only
  64KiB cap = intentional hugeCopy guard, not accidental unfinished
  n=2 never = progression broken after first plant
  design: multi-entry ≤64KiB chain in one plant (or working slice cursor)
```

---

## 50. hugeCopy "live blocker" is a false positive on memcpy a2=-1 sentinel (Grok)

Claude S1 implemented (chain plant) but never fired; pcbreak `0x124044` showed s2=0x3, a2 cycling 1/0/0xFFFFFFFF in tight loop. Asked for disasm of that range.

### 50.1 What `0x124020`–`0x124050` actually is

Plain **memcpy byte-tail** of `0x123F58` (after lq/sq and ld/sd bulk):

```text
0x124028: a2--
0x12402C: v0 = -1
0x124030: if a2 == -1 goto done     # exit when count underflows past 0
0x124038: lbu; a2--; a1++
0x124044: sb ...                    # Claude's hit
0x12404C: bne a2, a0, loop          # a0=-1; continue while a2 != -1
0x124054: jr ra
```

After a **small** legitimate copy finishes, `a2` goes `… → 1 → 0 → 0xFFFFFFFF`. That is **normal**, not a multi-megabyte request.

### 50.2 Assist mis-detect

```csharp
bool hugeCopy = pc is >= 0x00124020 and <= 0x00124050
    && (uint)a2 > 0x10000;
```

`(uint)(-1) == 0xFFFFFFFF > 0x10000` → **true on every normal memcpy completion** that ends in this tail. Combined with `!empty && !absurdS4 && !hugeCopy` early-return logic: when hugeCopy fires, assist does **not** return early, skips empty-plant (`empty` is false mid-memcpy), and falls through to **empty-epi snap `PC=0x122CBC`** — the TRACE line Claude still sees as `escape empty iovec n=1`.

So the "live n=1 escape" is **not** proof the empty-iovec plant path ran; it is often/always this **false-positive abort of a finishing small memcpy**.

### 50.3 Implications

| Claim | Revised |
|-------|---------|
| S1 chain plant wrong | **No** — correct for empty path; never reached live |
| Live blocker is hugeCopy of >64KiB STAGEHED | **No** — a2=-1 sentinel in byte tail |
| Why plant line never prints | Escape taken via hugeCopy→fallback, not empty→plant |
| cdvd 609→1865 | Still from `MaybePlantStageAssets` full-file `NoteHostReadSectors`, not iovec consume |

### 50.4 Fix shape (design, dual-ACK if touching Core/assist)

Tighten hugeCopy to **positive** sizes only, e.g.:

```csharp
uint a2u = (uint)(sys.EE.GetGpr(6).Lo & 0xFFFFFFFFUL);
bool hugeCopy = pc is >= 0x00124020 and <= 0x00124050
    && a2u > 0x10000u && a2u < 0x80000000u; // exclude a2=-1 and other negatives
```

Or drop PC-tail hugeCopy entirely and only gate **plant entry size** (already ≤0x10000 in S1). After fix: re-measure whether empty path plants, whether multi-entry chain is walked, whether natural I/O past 1865 appears.

```text
hugeCopy FALSE POSITIVE
  0x124044 = normal memcpy byte-tail; a2→0xFFFFFFFF is done-sentinel
  assist treats a2>0x10000 unsigned → aborts to empty-epi (n=1 TRACE)
  S1 empty-plant never reached; not a failed multi-entry test
  next: tighten hugeCopy (signed/positive bound) then re-run S1 before/after
```

---

## 51. Both fixes implemented, smoke green, zero regression — but B3's final state is unchanged (Claude)

Implemented both S1 (multi-entry iovec chain, `b3-iovec-multi-entry-design.md`, dual-ACKed)
and the `hugeCopy` tightening Grok proposed in §50 (exclude the top half of the unsigned
range so the memcpy tail-loop's `-1` termination sentinel can't false-positive the guard —
`a2Raw > 0x10000 && a2Raw < 0x80000000`), in the same pass since both touch the same function.

**Full existing smoke suite: all green, zero regressions** (`dotnet run --project Tests`,
`=== ALL SMOKE TESTS PASSED ===`).

**B3 before/after (30M and 50M cycles, `DETPS2_TRACE_BIOS=1`):**

- The false `escape empty iovec n=1` line is **gone** — confirms the `hugeCopy` false-positive
  fix works exactly as diagnosed.
- My new `plant iovec chain STAGEHED` trace line **still never fires** — S1's target branch
  (`empty == true`) is still never reached, now confirmed for a second, different reason: with
  the false-positive escape removed, execution simply doesn't hit *any* of this function's
  escape branches at all in this run.
- **`cdvdSectors` reaches the exact same 1865 by the exact same cycle (~30-34.7M) as every
  prior run tonight, with or without either fix.** Final state (`px`, `gif-path`, `held`,
  everything) is byte-identical to the original baseline.

### 51.1 Honest read

Both fixes are real, correct, worth keeping (a genuine false-positive guard bug fixed; a
genuine size-limiting bug fixed for the case it targets) — but **neither one is B3's actual
limiter.** `cdvdSectors` climbing from 609 to 1865 by ~34.7M happens through some other,
still-unidentified real code path entirely independent of the `STAGEHED.BIN` iovec-walk
mechanism this whole §49-§51 thread was built around. That mechanism turned out to be a red
herring for *this specific plateau* — real, worth fixing, but not load-bearing for why B3
stops at 1865.

**This doesn't invalidate §49's core observation** (the sector-count buckets are real, 1865
is genuinely inside `stagePlantOnly`, `postTxd` genuinely never triggers) — it means the
*mechanism* we guessed was gating that boundary (the STAGEHED iovec walk) isn't actually it.
The real gate is still open. Both fixes are being kept (correct, zero-risk, zero-regression
improvements) but this specific investigative thread doesn't yet explain the plateau.

```text
S1 + hugeCopy fix implemented (Claude) -- both correct, smoke green, B3 outcome UNCHANGED
  false escape event is gone (hugeCopy fix confirmed working)
  S1's multi-entry branch still never fires -- not what gates B3, even with the false-positive removed
  cdvdSectors: identical 1865 plateau, same cycle, byte-identical final state either way
  keeping both fixes (real, zero-regression) but the actual 1865 gate is still unidentified
  next: find what REALLY drives cdvd 609->1865 (not STAGEHED iovec) and what stops it there
```

---

## 52. Found the real 609→1865 driver: real GTFS FILEIO opens via RealSifRpc.cs — and exactly where it stops (Claude)

Added a temp caller-attributed trace on `Cdvd.NoteHostReadSectors` itself (`[CallerFilePath]`/
`[CallerLineNumber]`, reverted after use — cheap, no per-call-site edits needed) to see every
single sector credit across the run with its real source location.

### 52.1 The exact credit sequence

```text
cyc 15.5M-16.75M   ~20 small credits, all RealSifRpc.cs:3184 (early IRX-era opens) -> total 425
cyc 28.00M         +183 Burnout3Assist.cs:2084 (STAGEHED plant)        -> 608
cyc 28.00M         +1   Burnout3Assist.cs:2096 (HEADUS plant)          -> 609
cyc 29.15M         +183 RealSifRpc.cs:4199 (STAGEHED, real GTFS open)  -> 792
cyc 29.15M         +512 RealSifRpc.cs:4223 (FRONTEND.TXD, real open)   -> 1304
cyc 29.15M         +1   RealSifRpc.cs:4242 (HEADUS, real open)         -> 1305
cyc 29.40M         +560 RealSifRpc.cs:4390 (Global.txd, real open)     -> 1865
(nothing further through 40M/50M)
```

The 792→1865 jump is **not** the quirk assist at all — it's a **real, general** GTFS-TOC-open
routine (`RealSifRpc.cs`, shared FILEIO RPC infrastructure, not title-specific) opening
`STAGEHED.BIN`, `FRONTEND.TXD`, and `HEADUS.BIN` for real, plus a separate real RPC-driven open
of `Data\Global.txd`. Confirmed with the existing `DETPS2_TRACE_RPC=1` flag — only **7** total
`[GTFS]` events in the whole 40M-cycle run, ending with:

```text
[GTFS] fno=0x3 send=64 arg=0x1C1F6000: ...ASCII "Data\Global.txd"...
[GTFS] open path="Data\Global.txd" fd=4 size=1146112 fno=0x3
```

**That's the last GTFS event of the entire run.** The game's own real RPC call successfully
opens `Global.txd` (fd=4, real 1,146,112-byte size) — and then never issues a follow-up call
to actually read it. Confirmed this specific `fno=0x3` open call carried no `{dest,size}`
pointer pair in its 64-byte argument buffer (the buffer is fully consumed by the ASCII path
string), so this is a *bare* open — completely normal FILEIO usage expects a separate read()
RPC call afterward, which never comes.

### 52.2 Sharpened next question

Not "why does cdvd plateau at 1865" (answered: five real files opened, matching exactly) —
now precisely: **why doesn't the game issue a read (or lseek+read) RPC call for `Global.txd`
(fd=4) after successfully opening it?** Candidates: the open's response (fd value) isn't
reaching the game's own code in a form it recognizes as ready; the EE thread that issued
open() is blocked on something (semaphore/callback) before it can issue the follow-up read;
or the real game logic has its own gate before reading this specific file that isn't met.
This is the same "find the missing real mechanism" shape as everything else fixed tonight,
now one level closer to the metal than the STAGEHED-iovec detour in §49-§51.

```text
real cdvd driver found (Claude) -- GTFS FILEIO opens, not the STAGEHED iovec walk
  609->1865 comes from RealSifRpc.cs's real, general GTFS-open routine (5 real file opens)
  DETPS2_TRACE_RPC confirms: last of only 7 total GTFS events is a bare open of Global.txd
    (fd=4, real size 1146112) -- no read ever follows, for the rest of the run (40M-50M+)
  sharpened question: why no follow-up read() RPC call after the successful Global.txd open
  next: trace the open response delivery + whatever EE thread issued it, post-open
```

---

## 53. THE MISSING LINK: an existing assist already unmasks Path3 M3P, gated on cdvd>=2000 — ties §0-§52 into one causal chain (Claude)

While tracing the Global.txd open()'s caller (confirmed it's a standard, unremarkable
synchronous `SifCallRpc` wrapper chain — `0x0010C1E4` → `0x0010E5E4` → `0x0010E638` →
`0x0010F378`, the last a normal "wait for RPC completion" spin, all still inside
system-library territory, not yet real game code — 100M-cycle re-run also confirms the 7-event
`[GTFS]` count is a hard, permanent plateau, not a "just needs more cycles" artifact), searched
`Burnout3Assist.cs` for any other reference to `Global.txd` or the `>=2000` threshold. Found
this:

```csharp
/// After STG + full Global.txd (cdvd>=2000). Wave-9: sticky PATH3 M3P unmask,
/// host-plant FRONTEND.TXD slice, dead flip-watermark $ra rescue.
private void MaybeEscapePostTxdHang(Ps2System sys)
{
    ...
    // PATH3 M3P: transfers count while packets are held -> gifP3 climbs, px=0.
    if (sys.Gif.Path3MaskedByVif && sys.Gs.PixelsWritten == 0
        && sys.Gif.Path3Transfers >= 30)
    {
        sys.Gif.SetMskPath3(false);
        ...
    }
```

**An existing, already-implemented assist explicitly unmasks Path3 M3P** — the exact mechanism
this document's whole §0-§47 arc spent all night characterizing as correctly-behaving-but-
starved. Its call site (`Burnout3Assist.cs:339`):

```csharp
if (_lgDevFullyDone && sys.Cdvd.SectorsRead >= 2000 && sys.MasterCycles >= 40_000_000)
    MaybeEscapePostTxdHang(sys);
```

**Gated on `sys.Cdvd.SectorsRead >= 2000`.** We plateau at 1865. This function — and its
Path3-unmask logic specifically — has never once been invoked in any run tonight.

### 53.1 The full causal chain, now closed end to end

```text
Global.txd open() succeeds (fd=4, §52)
  -> no follow-up read() RPC call ever comes (§52, confirmed permanent to 100M cycles)
    -> cdvdSectors plateaus at 1865, never crosses 2000 (§49-§52)
      -> MaybeEscapePostTxdHang (the Path3-M3P-unmask assist) never gets invoked (§53)
        -> Path3 stays masked forever, held backlog never drains (§0-§1, this doc's original finding)
          -> VBlank ISR's own gate (§31) correctly no-ops every frame — nothing to flip
            -> DISPFB never selects new content -> black screen
```

Every other subsystem investigated across §9-§47 (DMAC completion, scheduler tie-break,
boot-stage dispatch table, five dead-code islands, entity-message subsystem, VIF1 command
breakdown, thread/semaphore census, VBlank ISR gate, mask/unmask buffer drain policy) is a
real, correct, and now fully-understood part of this same system — none of them were wrong,
they just weren't the *load-bearing* gap. The load-bearing gap is exactly one RPC call: the
game opens `Global.txd` and never reads it.

### 53.2 What's left, precisely

**Single remaining question: why doesn't the EE thread that successfully opened `Global.txd`
(fd=4) issue a follow-up read call?** Traced its call stack up through the standard
`SifCallRpc` wait-loop (`0x0010F378`, `bne v0,zero` spin — completes normally, this thread
isn't stuck *in* the RPC wrapper) — have not yet reached the real game-code caller above the
library layer (stack unwind was cut short by the loop's self-referential `ra` at the moment
sampled; needs catching the loop's entry/exit boundary instead, or a cleaner call-stack walk).
That's the concrete next step: find what real B3 code calls this open, and what it does (or
fails to do) immediately after — a stored/never-checked flag, a wrong fd interpretation, a
thread that goes to sleep and is never woken, etc. Same "find the missing real trigger" shape
as everything else tonight, now with a precise, single, well-defined target instead of an
open-ended search.

```text
THE MISSING LINK (Claude) -- ties the whole night together into one causal chain
  existing assist (MaybeEscapePostTxdHang, Burnout3Assist.cs:1443) ALREADY unmasks Path3 M3P
  gated on sys.Cdvd.SectorsRead >= 2000 -- we plateau at 1865, this assist has NEVER fired
  full chain: no Global.txd read() -> cdvd stuck at 1865 -> unmask-assist never runs ->
    Path3 stays masked -> held backlog never drains -> VBlank correctly no-ops -> black screen
  every other S9-S47 subsystem is real and correct -- none of them were the load-bearing gap
  single remaining question: why no read() RPC call for the already-opened Global.txd (fd=4)
  next: find the real game-code caller above the SifCallRpc library wrapper, and its continuation
```

---

## 54. Reached real game code: the open completes cleanly, state advances correctly — caller is table-dispatched (Claude)

Tested both remaining hyp3 sub-questions from §52/Grok's follow-up directly against the saved
trace: `grep "empty path"` on both the 40M and 100M-cycle `DETPS2_TRACE_RPC` logs — **zero
hits**. The observed open used a real, non-empty 64-byte send buffer with the genuine
`"Data\Global.txd"` path decoded from real bytes (`send=64 arg=0x1C1F6000: 61746144
6F6C475C...`) — not the HLE empty-path fallback. Rules out Grok's hypothesis 1 (HLE-invented
open) directly: the game genuinely intended this open.

Continued the stack walk with `--watch` on the real recv buffer (`0x0066E080`, taken from the
`[RPC] HandleCall ... recvBuf=0x0066E080` line) instead of guessing library `ra` chains — found
a **real, non-library** read at `pc=0x001D37B4: lw v0,0(a1)`, address `0x001D3xxx` being well
outside the `0x0010xxxx` system-library range that swallowed the earlier stack-walk attempts.

Disassembled the enclosing function (real entry at **`0x001D36E0`**, standard
`addiu sp,sp,-144` prologue) and traced its exact execution live via `--pcbreak`:

```text
0x1D3780: addiu a1,zero,3            # fno = 3 (open)
0x1D379C: jal 0x0010F1E8             # issue the RPC call (library)
0x1D37A4: jal 0x0010BE40             # wait for completion (library)
0x1D37B4: lw v0,0(a1)                # a1 = 0x66E080 (recvBuf) -- read status word
0x1D37B8: bltz v0,0x1D3800           # NOT taken (v0=0, success)
0x1D37D0: lwu a1,4(a1)               # read handle word -> a1=5 (matches fd=4, handle=fd+1)
          ... stores handle/state into a persistent struct at s1, sets state=1 ...
0x1D3804: jr ra                      # returns cleanly, v0=0
```

**This confirms end to end: the open() call is issued correctly, completes successfully
(status=0), the handle (5, matching real fd=4) is correctly extracted and stored, and the
function returns cleanly with no error.** Nothing is broken in this specific leg — the game's
own open-and-wait logic works exactly as it should.

The enclosing function (`0x1D36E0`) is itself gated by two conditions from its own caller-
supplied flags parameter (`a3`): a nonzero result from `jal 0x00212A80` and `(flags & 0xE) ==
0`, both satisfied in the observed run (confirmed by the fact we reached the open at all).

**`scanword` for `jal 0x001D36E0` (`0x0C074DB8`): zero static callers.** Same shape as
essentially everything else found tonight (§27, §35, §41-43) — this loader function is reached
via an **indirect call** (table/vtable dispatch), not a direct `jal`. Given how much of tonight
was spent characterizing exactly this kind of id-keyed dispatch table architecture (§37-§44),
this is very likely another instance of the same pattern: a resource-loader table where this
specific slot got dispatched exactly once (matching the single observed `fno=3` open) and
whatever should dispatch the *next* step (a second call into this same function, or a sibling
handling `fno=5`) never happens.

### 54.1 Where this leaves it

Not a broken open, not an HLE-fabricated call, not a reply-format mismatch, not fno=3↔4
thrashing. The open genuinely succeeds and the game's own state advances to "state=1,
handle=5" correctly. The gap is one level further out: **whatever should invoke this loader
function again (or invoke a sibling "issue the read" function) to advance past state=1 never
does.** Same "find the missing real trigger" shape as §25-§53's whole arc, now narrowed to a
single, real, well-identified function (`0x1D36E0`) and its still-unknown table/dispatch
caller.

```text
real game code confirmed (Claude) -- open() succeeds cleanly, gap is one level further out
  ruled out: HLE empty-path fallback (grep confirms real path both trace runs)
  reached real function 0x1D36E0 (real prologue, not library) via recvBuf watch, not blind stack walk
  traced its exact execution: fno=3 issued, RPC completes, status=0, handle=5 (=fd+1) captured,
    state advances to 1, returns cleanly -- nothing broken in this specific leg
  0x1D36E0 has ZERO static (jal) callers -- reached via indirect/table dispatch, same shape
    as S27/S35/S41-43's dead-code pattern
  next: find what SHOULD re-invoke this loader (or a read-issuing sibling) to advance past state=1
```

---

## 55. Struct-consumer side: found the "state" flag's sole reader and a candidate dispatch loop (Claude, split with Grok's vtable hunt)

Grok found the `fno=5` sibling function (`0x1D3280`, the real multi-chunk `Global.txd` DMA
reader) via the vtable at `0x4DDFC8`, confirmed `--pcbreak` shows **zero hits** — fully wired,
never called, same shape as §27/§35/§41-43. Took the complementary struct-consumer angle.

Watched the "state" field directly (`s1+24` from §54's trace, real address `0x0066E138`):
**exactly one read across the entire 40M-cycle run**, from a trivial getter at `0x001D3824`
(`lw v0,24(a0); jr ra`) — clearly meant to be polled repeatedly, called exactly once.

Traced that getter's caller context and found a real `jalr`-based dispatch loop nearby
(`0x00212A24-0x00212A4C`):

```text
0x212A24: lw t9, 0(s0)      # load vtable ptr from object s0
0x212A28: daddu a0, s0, zero
0x212A2C: lw t9, 16(t9)     # load method ptr from vtable+16
0x212A30: jalr t9           # call obj->vtable[16](obj, index=s1)
0x212A34: daddu a1, s1, zero
0x212A38: sw zero, 24(v0)   # clear offset+24 of the call's return value
0x212A3C: addiu s1, s1, 1
0x212A40: lw v0, 12(s0)     # count = *(s0+12)
0x212A44: sltu v0, s1, v0
0x212A48: bne v0, zero, 0x212A24  # loop while index < count
```

Classic indexed vtable-dispatch pattern (`for i in 0..count: obj->vtable[16](obj, i)`), and
the offset it clears after each call (`+24`) matches the loader's own "state" field exactly.
**Not yet confirmed** whether this loop's `s0` resolves to the same object family as Grok's
`0x4D5990` vtable-install site, or whether the item pointer it clears is actually our specific
loader's struct — flagged to Grok directly given they have the install-site context to check
this quickly. If confirmed, this would be a genuine race: the per-frame dispatch loop clearing
`state` back to 0 immediately after calling each item's handler, potentially before anything
else gets a chance to observe `state==1` and issue the `fno=5` read.

```text
struct-consumer side (Claude) -- state flag's sole reader found, candidate reset-race flagged
  state field (0x66E138) read exactly once in 40M cycles, by a trivial polled getter (0x1D3824)
  found a real jalr-t9 indexed dispatch loop (0x212A24-0x212A4C) that clears offset+24
    (matches state field exactly) immediately after calling each item's vtable[16] method
  not yet confirmed this is the SAME object/state -- handed to Grok (has install-site context)
    to check whether s0 here matches the 0x4DDFC8 vtable's owning object
  if confirmed: a per-frame reset race, not a missing call -- state never survives long enough
    for anything to see it and issue the fno=5 read
```

---

## 56. Forced-completion A/B: crossing postTxd unlocks real activity, but does NOT clear the black screen (Claude)

Ran the measure-only A/B proposed in §55's follow-up, matching tonight's established causal-A/B
discipline (RR sched, forced-unmask, etc.): temp env-gated hook in `blocker-trace` (reverted
after use) that credits enough sectors via the *same* `NoteHostReadSectors` mechanism already
used for every other credit tonight (no fabricated data) to push `cdvdSectors` from 1865 to
2065 once the plateau is observed, then let the run continue for 60M cycles and watch what
actually happens.

### 56.1 Result

```text
[FORCE] cdvd 1865->2065 cyc=30000000
[B3] plant FRONTEND.TXD @ 0x00A00000 planted=4194304/8517568 cdvd=6784 cyc=40000000
final: px=1172419 (was 877187) prims=234 (was 172) gifP2=16 (was 12) gifP3=26 (was 20)
       mskPath3=13 (was 10) cdvdSectors=6784 (crossed frontendEra>=6000 too)
       m3p=True heldP3n=5 heldP3qwc=2124 (UNCHANGED) lit=0/286720 mostlyBlack=1 (UNCHANGED)
```

**Crossing `postTxd` (2000) genuinely cascades:** `MaybePlantFrontendTxd` fires for real, and
`cdvdSectors` climbs on its own all the way to 6784 (past `frontendEra` too) — confirming the
rest of that assist chain is alive and working once its own gate is satisfied. Real internal
graphics activity increases measurably (`px`, `prims`, `gifP2/P3`, `mskPath3` all up).

**But the visible symptom is unchanged.** `heldP3n=5 heldP3qwc=2124` — byte-identical to the
very first measurement in this entire document (§1). `lit=0/286720` — the presented framebuffer
is still 100% black. `MaybeEscapePostTxdHang`'s own Path3-M3P-unmask branch **never fires**
(zero `post-TXD unmask` trace lines in the whole 60M-cycle run) despite its own outer gate
(`cdvd>=2000 && cyc>=40M`) being satisfied.

### 56.2 Why the unmask still doesn't fire

The unmask branch itself requires `Path3MaskedByVif && PixelsWritten==0 && Path3Transfers>=30`.
Neither sub-condition holds by the time the gate opens: `Path3Transfers` (`gifP3`) only reaches
**26** in the entire 60M-cycle run — never crosses the `>=30` threshold — and separately `px`
becomes nonzero once the newly-unlocked `FRONTEND.TXD`-adjacent activity starts drawing
something. Either alone is sufficient to explain why the unmask heuristic never triggers; can't
cleanly separate which one "the" blocker is without finer-grained timing.

### 56.3 What this settles

This is real, useful negative evidence, not a dead end: **completing the missing `fno=5`
dispatch (§52-§55's whole thread) would very likely *not* by itself clear B3's black screen.**
The `postTxd` assist's own Path3-unmask heuristic has its own separate, currently-unmet
preconditions that would need addressing too — and there's a real irony here: the heuristic's
`px==0` check (meant to detect "nothing is rendering, so Path3 must be why") gets defeated by
*other* real activity that starts rendering once `postTxd` unlocks, even though Path3 itself
stays masked with a real backlog the whole time.

Reframes the fix priority: finding/fixing the missing `fno=5` dispatch is still worth doing
(closes a real gap, matches the "find the real mechanism" doctrine), but shouldn't be assumed
to be *the* fix for the visible symptom without also addressing why `Path3Transfers` stalls
under 30 and/or revisiting whether `PixelsWritten==0` is still the right unmask heuristic once
other content can render independently of Path3.

```text
forced-completion A/B (Claude) -- crossing postTxd unlocks real activity, screen stays black
  temp NoteHostReadSectors force (real mechanism, no fabricated data), cdvd 1865->2065->6784
  real cascade: FRONTEND.TXD plants for real, px/prims/gifP2-3/mskPath3 all genuinely increase
  BUT: heldP3n/qwc unchanged (still 5/2124, same as S1's very first measurement)
  lit=0/286720 still 100% black -- visible symptom unchanged despite real internal progress
  postTxd's OWN Path3-unmask branch never fires: Path3Transfers caps at 26 (<30 threshold),
    AND px becomes nonzero from other activity, defeating its px==0 heuristic
  settles: fixing the fno=5 dispatch alone would likely NOT clear the black screen by itself --
    postTxd's unmask heuristic has its own separate unmet preconditions needing attention too
```

### 56.4 G3 answered: force-unmask A/B — the held payload itself is not visible either (Claude)

Ran the direct, independent test (bypassing `MaybeEscapePostTxdHang`'s heuristic entirely):
temp env-gated hook (reverted after use) that calls `sys.Gif.SetMskPath3(false)` directly the
moment the original `heldP3n>=5` backlog is observed (cyc≈16M, well before the postTxd gate
would ever open on its own).

```text
[FORCEUNMASK] heldP3n=5 heldP3qwc=2124 cyc=16000000 -> unmasking
final: m3p=False heldP3n=0 heldP3qwc=0   (fully drained, confirms §46's drain-policy finding again)
       lit=0/286720 mostlyBlack=1        (UNCHANGED)
       DISPFB1=0x0000000000000000        (UNCHANGED, still the sticky-zero value)
       FRAME_1=0xA0046                   (UNCHANGED)
```

**The unmask+drain mechanism works perfectly — the entire original 2124-QW held backlog fully
drains — and it changes literally nothing visible.** `DISPFB1` stays exactly zero, the
presented framebuffer stays exactly 100% black, pixel counters don't reflect any new visible
content from this specific drain.

**G3 is answered: no.** The held Path3 payload itself was never going to be the thing that
lights the screen, even under perfect conditions (immediate, clean unmask+drain, no other
preconditions in the way). This is consistent with the very early §18 finding (RR-mode
"unlocked" content turned out to be noise, not real geometry, on visual inspection) — the held
backlog is very likely GS state-setup/register packets rather than actual draw commands with
visible framebuffer effect, or targets a part of GS memory that isn't part of the displayed
buffer.

### 56.5 Updated picture: three real, independently-confirmed gaps, none sufficient alone

```text
G1 (fno=5 never dispatched)      -- real, open, root-cause-worthy
G2 (postTxd unmask never arms)   -- real, open, heuristic-level
G3 (drain the held backlog)      -- ANSWERED NO, doesn't light anything even when forced
```

None of G1, G2, or G3 individually explain the visible black screen — closing G1+G2 (the real
fix path) would let the *assist's own* unmask fire, but §56.4 now shows that even a perfect,
immediate unmask+drain of the specific backlog that's been sitting there all night produces zero
visible change. **The real per-frame render path (VU1 execution, Path1 traffic, continuous new
Path3 submissions) — never once observed running at all tonight (§30) — remains the actual
missing piece.** Everything in this document's G1/G2/G3 was about *unblocking a one-shot setup
backlog*, not about finding or arming the game's ongoing per-frame draw submission, which this
whole night converges on being a separate, still-unlocated mechanism.

**Triple-confirmed (Grok, seq0428, temp-only, canary `out/canaries/b3-requester/
force-global-dma-50m.txt`, not committed to Core):** a combined probe — real `GLOBAL.TXD`
bytes host-DMA'd into EE (not just credited), *plus* the direct `SetMskPath3(false)` unmask,
*plus* the natural `FRONTEND.TXD` plant cascade (cdvd→6160) — still produces `lit=0
mostlyBlack=1`, `gifPath1=0` (no VU1/Path1 activity), `interactive=False`/`scene=False` in the
PL-014 scene-delta fingerprint. Even the maximal combination of everything G1/G2/G3 could give
does not light the screen. This is the strongest form of the negative result: not just "one
link missing," but "even simulating every fix in this thread landing at once isn't sufficient."

```text
G3 answered (Claude) -- force-unmask A/B, independent of postTxd heuristic
  forced SetMskPath3(false) directly at cyc=16M when heldP3n=5 first observed
  drain confirmed perfect: heldP3n/qwc 5/2124 -> 0/0 (matches S46's drain-policy finding)
  lit/DISPFB1/FRAME_1: ALL UNCHANGED -- the held payload itself has zero visible effect
  G3 = NO: the original held backlog was never going to light the screen, even ideally drained
  updated picture: G1+G2+G3 all real but none alone (or together) explain the black screen --
    the actual missing piece is B3's ongoing per-frame render path (VU1/Path1), never observed
    running at all tonight, structurally separate from everything G1-G3 traced
```

---

## 57. Mode/state hunt, started: full-run PC profile shows the EE spends the overwhelming majority of its time idling, not simulating (Claude)

Not parking (user standing instruction: no "next session" framing) — picked up the mode/state
question directly. First cut: a full 90M-cycle `DETPS2_PROFILE_PC=1` run to see where the EE
actually spends its instruction budget, as a proxy for "is this a real gameplay/simulation
loop or mostly idle."

```text
0x00237180-0x00237198  (tid5's 4-slot flag-wait spin, §33)     ~2.57M samples EACH instruction
0x0010BD40-0x0010BD48  (SleepThread syscall trampoline)         ~2.57M samples EACH
0x00100158-0x00100170  (very early boot-region / dispatch)      ~1.70M samples EACH
0x00293A30-0x00293A50  (small compare/lookup helper)             122K samples EACH
(everything else falls off sharply from there — 51,800 unique PCs total, 43.7M samples)
```

**The single dominant hotspot, by roughly 20x over the next-largest, is tid5's own
`while(flags[i]==0) SleepThread()` wait loop from §33** — the SAME loop I earlier confirmed is
*actively serviced* (all 4 flags do get set/cleared, 22-48 times each across the run). Put
together: real progress on this specific synchronization primitive happens rarely (dozens of
transitions) relative to the sheer number of idle poll/yield iterations between each one (2.57M
samples). This isn't "the EE is stuck" — it's real, working code — but it clarifies the texture
of what's happening: **B3 spends the overwhelming majority of its CPU budget idling on this one
wait, not running gameplay simulation.** The much smaller secondary hotspot (`0x293A30`, a
plain equality-check/compare helper, ~122K samples) matches the earlier §24 "real hash/linked-
list lookup" background work — mundane, not simulation-shaped either.

### 57.1 What this contributes to the mode/state question

Doesn't yet answer "what mode is B3 in" directly, but rules out one framing: this is not a CPU
saturated with per-frame simulation work that happens to never call the render path — it's
mostly *waiting*, real code, real synchronization, but overwhelmingly idle. Consistent with
"B3 is sitting in a low-activity holding state" (a menu/attract/loading wait) rather than
"B3 is actively simulating a race and just failing to submit graphics for it." Next natural
step: identify what real external event the 4-slot wait's producer is gated on, and whether
that producer itself is waiting on something further upstream (the same "keep going up one
level" method that worked for §52-§55's GTFS chain).

```text
mode/state hunt started (Claude) -- full 90M-cycle PC profile
  overwhelming dominant hotspot: tid5's 4-slot flag-wait spin (S33), ~2.57M samples, ~20x
    the next-largest hotspot
  confirms: B3 spends the vast majority of its time idling/yielding on ONE real sync
    primitive, not running per-frame simulation code that fails to submit graphics
  secondary hotspot: a plain compare/lookup helper (~122K samples), matches earlier S24
    background-work finding, not simulation-shaped either
  redirects the mode/state hunt: find what the 4-slot wait's real PRODUCER is gated on
    (same "go one level up" method as S52-S55), rather than assuming active-but-silent simulation
```

---

## 58. Real mode/render-object identity check found — a singleton that's never constructed on either end (Grok + Claude)

Grok found a real "current mode/render object" identity check at `0x223224` (reached from the
presentation-continue path after §57's VBlank-park loop): loads a current-object pointer,
compares against a fixed constant, branches to `0x2243E0` on match (confirmed real VU0/`cop2`
matrix-transform code — chained `cop2`, `lwc1`/`swc1`, `sq`/`lq` pulling float fields at offsets
16-72 from a source object, matching a 4x4-matrix-shaped layout) or falls through otherwise.
(Both Grok's and Claude's independent disasm of the actual fall-through path, `0x223228`+,
also show real matrix/float work on a *different* source object — so this specific branch is
"use object A's transform vs object B's transform," not cleanly "render vs no-render"; noted as
an open caveat, not yet fully resolved which downstream effect matters more.)

**The real addresses (corrected after an initial off-by-`0x10000` sign-extend slip from a
`lui`+`addiu` pair — caught and fixed):**

- Current pointer: `*(0x0051BA88)`
- Expected/target singleton: `0x0051A688`

### 58.1 Both ends confirmed dead

**Current pointer, watched across the full 90M-cycle run (Claude):** exactly 4 accesses,
ever — boot zero-init, one syscall-context read, **one real write that's an explicit re-zero**
(`sw zero,-9656(v1)` at `0x133EBC` — deliberately clearing something already zero, not an
assignment), and one real consumer read (`0x131E54`). **The pointer is never assigned a
non-null value at any point in the entire run.**

**Target singleton, checked by Grok:** `0x51A688` sits as all-zero BSS — never constructed
(no writer found yet from Grok's static pass; likely a fixed singleton object that should be
placement-constructed once, then have its address written into `0x51BA88`).

### 58.2 Reading

This is now a clean "singleton never constructed" story on both ends, not a "pointing at the
wrong thing" story: the object that should represent whatever mode/state enables this specific
identity-gated branch never gets built, and the pointer that should reference it never gets
set. Same shape as everything else found tonight (registered-but-never-dispatched), one level
higher up — this may be *the* real gate for whatever downstream rendering difference the
`0x2243E0` branch represents, though the exact downstream consequence (given both branches do
real matrix work) still needs pinning down before claiming this explains the black screen.

```text
mode/render singleton (Grok+Claude) -- never constructed on either end
  current pointer *(0x51BA88): exactly 4 accesses in 90M cycles, NEVER assigned non-null
    (boot zero-init, one syscall read, one deliberate re-zero at 0x133EBC, one consumer read)
  target singleton 0x51A688: all-zero BSS, no constructor/writer found yet (Grok, in progress)
  identity check at 0x223224 (beq -> 0x2243E0 real VU0/cop2 matrix code) never matches -> always
    falls through to a DIFFERENT matrix path (0x223228+, also real work, not yet fully traced)
  open caveat: both branches do real float/matrix work -- exact downstream consequence of
    taking 0x2243E0 instead of the fall-through not yet confirmed
  next: who should construct 0x51A688 (Grok, in progress); what 0x133EBC's re-zero context is
```
