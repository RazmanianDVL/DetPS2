# M7 residual honesty rollup — PATH2/3 IMAGE + DISPFB (2026-08-04)

**Status:** docs only — dual-orch next after dual-idle standing-order fix  
**Tip at write:** `ca5d916`  
**Purpose:** one place for **accepted residual vs still load-bearing Core** so we do not re-open closed M7 threads without a named bar  

---

## 0. One-line

Most M7 “chrome residual” stories on the six-title wall are **classified and partially closed**: IMAGE stall myths debunked, composite preference code already correct, several titles **accepted residual**. Remaining playability gaps are **mostly R1 (natural IMAGE never reaches GIF)** or **oracle-class LastImageTrx** — not more untargeted Gif/Gs thrash.

---

## 1. Residual classes (A0)

| Code | Meaning |
|------|---------|
| **R0** | No IMAGE bytes at all (pre-spine / budget) |
| **R1** | IMAGE not delivered from game path (assist or Path2 strip only) |
| **R2** | IMAGE wrong page / residual Frame vs empty natural DISPFB |
| **R3** | DISPFB unset — honest FRAME/FBP0 residual (A4) |
| **R4** | Composite skip / expand stamp |
| **R5** | Path3 masked starve |

Source: `m7a-a0-residual-inventory.md` / `m7-a0-residual-inventory.md`.

---

## 2. Six-title honesty table (current doctrine)

| Title | Primary class | Status | Core load-bearing? |
|-------|---------------|--------|--------------------|
| **GoW** | R2 (+ assist shell) | Residual present path documented; gifP3 often 0 | **Maybe** Slice 2 game IMAGE if assist-free bar wanted; plant is M4 not M7 |
| **Haven** | R1 assist IMAGE | Natural tags≈0; plant lights present | **Yes if** product bar is assist-free IMAGE |
| **Whip** | R1 GOE→GIF IMAGE gap | Path2 strip / assist; natural DISPFB OK once IMAGE lands | **Yes if** GOE ring → Path2 IMAGE |
| **Dec / DA** | R1 (+ DA LastImageTrx) | Midway “stall” = **telemetry artifact** (`m7c-2b`) | IMAGE **delivered**; MK:DA LastImageTrx **accepted** (`m7-slice3-mkda-residual-accepted.md`) |
| **BO2** | R1 MAINMENU stream | EE has BG2; natural IMAGE missing without assist | **Yes if** game IMAGE from streamed BG2 |
| **B3** | **R3** DISPFB=0 | A4 honest residual; game IMAGE works | **No** plant DISPFB; optional only if retail ever writes DISPFB |

---

## 3. Closed / do-not-reopen without new evidence

| Thread | Verdict | Doc |
|--------|---------|-----|
| Midway 5888/6144 IMAGE stall | **Not a bug** — stale progress telemetry | `m7c-2b-midway-image-stall-rootcause.md` |
| MK:DA Slice 3 composite selector | Code already prefers natural → residual; outcome **accepted residual** | `m7-slice3-mkda-residual-accepted.md` + `m7c-slice3-dispfb-composite-design.md` |
| M7-c “fully closed” framing | Re-scoped as labels + bisect, not one shared mechanism | `m7c-gif-bisect-4title.md` |
| B3 DISPFB plant | **Banned** — A4 residual OK | A0 / design A4 |

---

## 4. Still load-bearing (named bars only)

| Seat | Bar | Notes |
|------|-----|-------|
| **M7-L1** Whip/BO2/Haven **R1 game IMAGE** | Assist-off: `imgBytes>0` from game path **or** honest still 0 with TRACE | Highest playability leverage if chrome is still assist-only |
| **M7-L2** GoW natural Path3 IMAGE | gifP3 / image tags without assist plant | Separate from M4 plant |
| **M7-L3** LastImageTrx oracle | Retail/reference shows natural page empty or not | Only if oracle exists; else stay accepted |
| **M7-L4** Path3 mask starve (R5) | Title with held Path3 + zero IMAGE | None primary on six-title wall |

**Bias after C1/M5/GoW plant parks:** **M7-L1** only if a live title’s menu is still assist-chrome; else M7 is **documentation-closed** for this dual-orch cycle.

---

## 5. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **MH-Q1** | Accept this rollup as M7 standing status (no Core this seat)? | **Yes** |
| **MH-Q2** | Next M7 Core: only M7-L1 with named title + bar? | **Yes** |
| **MH-Q3** | Park MK:DA / B3 residual permanently (oracle optional later)? | **Yes** |
| **MH-Q4** | Re-open Gif.cs drain for Midway IMAGE? | **No** |

---

## 6. Definition of done (this seat)

- [x] Classes + six-title honesty  
- [x] Closed threads listed  
- [x] Load-bearing seats with named bars  
- [x] Dual-ACK MH-Q1..Q4  
- [x] **No Core**  

---

```text
M7 residual honesty rollup
  stall myth closed; MK:DA LastImageTrx accepted; B3 R3 A4
  open only R1 game IMAGE with named title+bar
```
