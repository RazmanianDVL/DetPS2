# Path2 sticky residual inventory (WAVE-11C / GX-010 prep)

**Seat:** S8 · **WP:** GX-010 inventory · **Regression title:** God of War (`user-media-god-of-war.json`)

---

## 1. What sticky reassembly fixes

VIF1 often feeds Path2 as **one QW per** `ReceivePath2Data` (or mid-DIRECT pad). Without mid-packet sticky state in `Gif`, the GIFtag alone was consumed and PACKED A+D (FRAME/PRIM/XYZ2) never reached Soft-GS → GoW `gifP2` high / `FRAME_1=0` / `prims=0`.

| Fix | Location | Smoke |
|-----|----------|-------|
| Sticky mid-packet across Receive* | `Gif.ProcessTransfer` | `Gif_Path2_QwSliced_PackedSprite_WritesPixels` |
| Mid-QW DIRECT pad to 16B | `Vif.ProcessStream` | `Vif_Direct_MidQw_PadsBeforePath2` |
| New DIRECT aborts incomplete sticky | `Vif` DIRECT + `Gif.AbortIncompletePacket` | `Vif_Direct_Supersede_AbortsStickyGarbage` |
| DIRECT-end truncate abort | same | (covered by supersede + GoW claim aborted=1) |
| END ADDR=0 inline DIRECT Path2 | `Dmac` | `Dmac_Vif1EndAddr0_InlineDirectPath2` |

---

## 2. Residual that is **intentional**

| Metric | GoW @100M (typ.) | Meaning |
|--------|------------------|---------|
| `aborted=1` | yes | First DIRECT IMM=0xBF0 at ~`0x46BE90` is **non-GIF** payload; parses as huge REGLIST; next DIRECT supersedes → `abortNewDir=1` |
| `lastAbort=new-DIRECT` | yes | Same residual |
| `spannedCalls≥1` | yes | Sticky reassembly did real multi-call work |
| `inFlight=False` at claim | yes | No stuck sticky after stream |

**Do not** try to drive `aborted→0` by inventing GIF for that DIRECT — it is garbage payload, not a missing Path2 feature.

---

## 3. Residual that is **not** Path2 sticky

| Symptom | Owner |
|---------|--------|
| `px` strip 512×1 before ofx expand | S9 ofx policy (W12B) |
| `gifPath3=0` shell IMAGE | title stream / PATH3 natural (not invent) |
| `imgBytes=0` | S9 IMAGE / title bind |
| M3P stuck with held PATH3 | `PATH3_MASK_MATRIX.md` |

---

## 4. Safe hardens landed (G0)

1. **Zero-QWC Path2/Path1 no longer inflate transfer counters** (match Path3 early-return).  
2. **Abort reason split** on claim: `abortNewDir` / `abortTrunc` / `abortOther` + `lastAbort`.  
3. **DETPS2_TRACE_GIF ring** records Path1/2/3 xfer, tags, completes, aborts; Path2 huge nloop (`>4096` REGLIST/IMAGE) logs `WARN=path2-huge-nloop` for inventory (no auto-abort mid-DIRECT — supersede remains the safe boundary).

---

## 5. Safe hardens landed (G1 / GX-010 + GX-011)

| Fix | Location | Smoke |
|-----|----------|-------|
| Path-owned sticky (`_pktPath`); VIF DIRECT boundary **does not abort Path3 sticky** | `Gif.AbortIncompletePacket` | `Gif_Path2_DoesNotAbort_Path3Sticky` |
| Path2↔Path3 stall (Play! arb): Path2 stalls on Path3 sticky; Path3 held on Path2 sticky + drain | `ReceivePath2/3Data` | same + hold |
| EOP multi-packet continue in same transfer (Play! ProcessMultiplePackets) | `Gif.ProcessTransfer` | `Gif_Path2_MultiPacket_EopContinuesInTransfer` |
| GIF_CTRL RST clears sticky mid-packet | `Gif.WriteRegister` | (covered by Reset/CTRL) |
| DIRECT IMM=0 → 65536 QWs (not empty) | `Vif` (hold) | `Vif_Direct_Imm0_Means65536_NotEmpty` |
| FIFO `FeedData` mid-QW pad + 4-word QW assembly → `ReceivePath2Quadword` | `Vif.FeedData` + `Gif` | `Vif_FeedData_Direct_MidQwPad_Path2Frame` |

**Not done (still deferred):** auto-abort huge nloop mid-DIRECT; abort-on-FLUSH; inventing PATH3 packets; wholesale `Path3MaskedByVif` clear.

---

## 6. Deferred (needs crash/regress-clear proof)

| Idea | Why deferred |
|------|----------------|
| Auto-abort Path2 mid-DIRECT when nloop huge | Can clip legitimate large IMAGE DIRECT |
| Abort sticky on FLUSH/FLUSHA | HW waits for GIF idle; abort ≠ flush |
| Full APATH exclusive model + stall queues | Partial arb landed; deeper stall/resume later |

---

## 7. Exit for GX-010/011 full

- Smokes above green (W11C suite + G1 four)  
- GoW claim: `aborted` only from garbage DIRECT class (`abortNewDir`, not random trunc storms)  
- No MENU px regress on Path2 titles (GoW, BO2, Dec/DA Path2 pumps)  
- `abortTrunc` / `abortOther` storms = 0 on diagnose claims
