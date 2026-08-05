# GFX-PLAN-v0 — Soft-GS present / graphics correctness

**Status:** **CP0 dual-ACKed** (Claude seq0227 + Grok seq0226)  
**Tip at plan freeze:** `a4171c3`  
**Process:** plan dual-ACK → baseline → L1 design dual-ACK → Core. No Core before CP2.

---

## Exit bars

| Tier | Bar |
|------|-----|
| **A (minimum)** | ≥1 commercial title @50–100M: present PPM **color lit ≥5% of FB**, not pure black / single gray strip; Desktop HostPresent uses same `GetPresentSpan` path as scoreboard; `exitRequested=false`. **Visual dual-check of PPM required** — numeric lit% alone is not enough. |
| **B (honest natural)** | Tier A driven by EE GIF Path2/3 IMAGE and/or prims without new Host→Local invent of non-pixel containers (goefile ban). |
| **C (fleet)** | Tier A on ≥3 titles (prefer B3 + Dec + one of Whip/BO2/GoW). |

**Not the bar:** scoreboard `imgBytes` / G2 alone; Host→Local residual MENU YES claims.

---

## Taxonomy

| Layer | Symptom | Hypotheses |
|-------|---------|------------|
| **L1 Present** | Metrics high, present black | Black full-FB prims wipe Soft-GS FB; merge cache skips re-composite; scoreboard dump ≠ Desktop `GetPresentSpan`; wrong DISPFB/FRAME page |
| **L2 Delivery** | gif/imgBytes but wrong page/PSM | CompositeLastImageTrx / PSM / `IsPageMismatched`; FRAME FBP ≠ DISPFB FBP |
| **L3 EE path** | imgBytes=0 | Game never submits texture (Whip/BO2); assist was fake paint |
| **L4 Raster** | prims reject | rejDepth/Alpha massive |

---

## Title order

1. **Burnout 3** — L1/L2 (high Soft-GS activity, black present)  
2. **MK Deception** — L2 (art-scale IMAGE, gray residual strip)  
3. **Whiplash** (then BO2) — L3 EE texture path  
4. **GoW** — later (residual DISPFB class)

---

## Checkpoints

| CP | Gate | Owner bias |
|----|------|------------|
| **CP0** | Dual-ACK this plan | **done** |
| **CP1** | GFX-BASELINE canary + doc (read-only) | Grok script; both visual PPMs |
| **CP2** | L1 design dual-ACK → Core | Grok design; Claude review |
| **CP3** | B3 present Tier A or next hypothesis | Grok Core |
| **CP4** | Dec Tier A or escalate L2 | Grok |
| **CP5** | Whip L3 investigation dual-ACK → dig | Claude docs/TRACE |

---

## Split (after CP0)

| Agent | Work |
|-------|------|
| **Grok** | L1/L2 Core present + composite; B3 primary; scoreboard/Desktop parity |
| **Claude** | L3 methodology + Whip EE texture path map (docs/TRACE only until CP5); review L1 design at CP2 |

---

## Bans

- Re-enable MENU-WHIP-2 / MENU-BO2 goefile Host→Local paint  
- Invent Path2 IMAGE without real texture path  
- Claim MENU YES from residual Host→Local  
- Parallel thrash M1 CHCR / C1-TP while GFX open  

---

## Related

- Baseline: `docs/infra-audits/gfx-baseline-2026-08-04.md`  
- Canary: `tools/canary-gfx-baseline.ps1`  
- L1 design (when ready): `docs/infra-audits/gfx-l1-present-black-design.md`  
