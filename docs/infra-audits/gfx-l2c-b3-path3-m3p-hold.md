# GFX L2c — B3 PATH3 / M3P hold dig

**Status:** measure complete — **no Core this seat**; dual-ACK before any fix  
**Date:** 2026-08-05  
**Title:** Burnout 3 (SLUS_210.50)  
**Parents:** `gfx-l2c-b3-frame-dispfb-stall-finding.md`, Claude page-0x46 dump (`b7048b1`)  
**Author:** Grok (claimed seat after Claude seq0290 split)

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
