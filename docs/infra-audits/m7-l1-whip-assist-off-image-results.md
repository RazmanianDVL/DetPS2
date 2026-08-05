# M7-L1 results — Whiplash natural IMAGE (assist chrome already soft-off)

**Status:** **measured** — honest residual; **no Core** this seat  
**Tip:** `de7f569` (+ this docs commit)  
**Parent plan:** `m7-l1-whip-assist-off-image-trace-plan.md`  
**Parent honesty:** `m7-residual-honesty-rollup-2026-08-04.md` (R1 class)

---

## 1. Bar (from plan)

| Arm | Expect |
|-----|--------|
| Product | `imgBytes>0` **or** honest 0 if assist chrome is already off |
| Assist soft-off | honest residual **or** unexpected natural IMAGE → Core dual-ACK |

**Pass** = document honest residual **or** natural IMAGE (then Core dual-ACK).

---

## 2. Product state of Whip IMAGE assists

| Assist | Status at tip |
|--------|----------------|
| **MENU-WHIP-2** Host→Local GOE BITBLT | **DISABLED** (2026-08-02) — goefile/audio mispaint; see `WhiplashAssist.cs` + `TITLE_HACKS.md` |
| PL-033 ring guess fill | **DISABLED** (RealSifRpc delivers real stream) |
| M8 PreferIopRp / version plant | product **soft-off** (M8-a) |
| UsingCD / CD_NCMD / WaitSema / pad | boot/path assists — **not** GIF IMAGE inject |

So the product arm **is** the IMAGE assist-off arm for Host→Local chrome. No separate `--no-assist` needed for this R1 bar (`--no-assist` only gates Midway PC-range, not Whip).

---

## 3. Measure (diagnose 20M, tip `de7f569`)

```text
dotnet exec out/scoreboard-build/DetPS2.Core.dll scoreboard-metrics user-media-whiplash.json `
  --cycles=20000000 --host-present --out=out/canaries/m7-l1-whip/<stamp>/product-metrics.json
```

Artifact: `out/canaries/m7-l1-whip/20260804-194206/product-metrics.json`  
Wall ~10 s. `exitRequested=false`, exit 0.

| Field | Value |
|-------|-------|
| **imgBytes** | **0** |
| gifP2 / gifP3 | 0 / 2 |
| gifCompleted | 3 |
| px / prims | 286720 / 1 |
| compositeSource | None |
| expandHits | 1 |
| naturalDispfb | true (DISPFB2 set; residual/dispfbPx still 0) |
| G2 (imgBytes heuristic) | **N** |
| T3 | N |
| PC | `0x003145A8` |
| binds / calls / cdvd | 13 / 114 / 916 |

Matches prior M8 soft-off + fleet flag-off Whip 20M identity rows (`imgBytes=0`).

---

## 4. Verdict

| Question | Answer |
|----------|--------|
| Natural game IMAGE at diagnose? | **No** — `imgBytes=0`, Path2=0 |
| Assist Host→Local chrome still inventing IMAGE? | **No** — MENU-WHIP-2 disabled |
| Class | **R1 honest residual** — EE has title surface / GOE/RKV path; Soft-GS never sees Host→Local IMAGE |
| Core this seat? | **No** — do not invent Path2 IMAGE or re-enable goefile paint |
| Reopen when? | Real **MAP/\*.MP2** (or other proven texture) decode + EE path that posts GIF IMAGE, **or** claim-budget (50–100M) evidence of natural tags |

---

## 5. Dual-idle notes

- Claude left M7-L1 shelf; dual-idle standing order = **propose/execute free seat**, not mutual-hold.
- This seat **closes Whip M7-L1 measure** as honesty doc; does **not** open Core Path2 invent.
- Optional later: BO2/Haven same assist-off IMAGE bar under their own env kills (separate claim).

```text
M7-L1 Whip IMAGE
  product=assist-off for Host→Local chrome
  imgBytes=0 @20M tip de7f569 — R1 honest residual
  no Core; reopen only on real texture path
```
