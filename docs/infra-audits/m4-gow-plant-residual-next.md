# M4 GoW plant residual — next status (after C1 park)

**Status:** docs only — **no Core this seat**  
**Date:** 2026-08-04  
**Tip:** `8da8a0e`  

---

## 0. One-line

GoW product still depends on **EE RAM plant** `"3000"` @ `0x002C6D30`. S4 **EE mirror** cannot retire it alone: `_lastIopRpVersionAscii` is empty early (no parseable reboot-gen arg in plant window). Plant-off + mirror **fails** claim class.

---

## 1. Evidence (already landed)

| Doc | Result |
|-----|--------|
| `GOW-PLANT-closed-summary` (UNC) | Plant load-bearing; Prefer-off alone OK; plant-off = dual fail |
| `m4-s4-gow-claim-canary.md` | Plant-off + `DETPS2_MIRROR_IOPRP_CELLS` **FAIL** vs baseline |
| `m4-s0-gow-udnl-arg-design.md` | S0 gap = retail-shaped reboot arg; not mirror-only |
| `m4-s4-ee-mirror-design.md` | Mirror mechanism correct; GoW timing/upstream empty tag |

---

## 2. Fix classes (infra, dual-ACK before Core)

| ID | Approach | Notes |
|----|----------|-------|
| **G1** | S0: make `LastIopRebootArg` / tag populate early enough for mirror | Preferred long-term; needs RESET_CMD / arg path ground-truth |
| **G2** | Accept plant as **documented permanent residual** until S0 proven | Default if no new ground-truth |
| **G3** | Title PC escape for freeze constructor | **Rejected** as primary (prior dual-ACK) |
| **G4** | M4-g FILEIO GetVersion tag-if-applied | Unrelated coupler for Whip/FILEIO; not GoW plant path |

---

## 3. Recommended next seats

| # | Seat | Type |
|---|------|------|
| 1 | TRACE: GoW RESET_CMD / reboot-gen arg window (when plant fires vs first real tag) | measure |
| 2 | Dual-ACK G1 vs G2 after TRACE | design |
| 3 | **No** plant soft-off flip without claim green | ops |

**Bias:** measurement seat (1) only if playability still blocked by plant debt; else leave plant on, park M4-GoW, work other titles.

---

## 4. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **GP-Q1** | Keep plant product-ON for GoW until S0 evidence? | **Yes** |
| **GP-Q2** | Next Core for GoW plant this week? | **No** without TRACE |
| **GP-Q3** | Accept G2 permanent residual if TRACE shows no retail arg path? | **Yes** default |

---

```text
GoW plant residual next
  mirror alone FAIL; plant load-bearing
  bias: plant stays ON; TRACE optional; no Core without dual-ACK
```
