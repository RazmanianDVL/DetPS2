# M5-a Q3 sample — God of War `DETPS2_TRACE_DMAC=1` (claim 100M)

**Date:** 2026-08-04  
**Tip (docs):** `2dab89b`+  
**Budget:** **claim 100M** via `blocker-trace` + `--host-present`  
**Env:** `DETPS2_TRACE_DMAC=1` (print only; zero DMA behavior change)  
**Media:** `user-media-god-of-war.json` → `C:/Users/user/Downloads/GodofWar(USA).iso` (**present**)  
**Build:** Release → `out/scoreboard-build/DetPS2.Core.dll`  
**Design:** `docs/infra-audits/m5a-dmac-vif-gif-completion-design.md` §8 **Q3**  
**Rollup:** `docs/infra-audits/m5a-q1-q5-evidence-rollup.md`  
**Scope:** measurement only. **No Core. No GodOfWarAssist product edits.**

---

## 1. Command

```powershell
$env:DETPS2_TRACE_DMAC = "1"
dotnet exec out/scoreboard-build/DetPS2.Core.dll blocker-trace user-media-god-of-war.json `
  --cycles=100000000 --host-present `
  1> out/canaries/m5a-gow-trace-dmac-claim/out.txt `
  2> out/canaries/m5a-gow-trace-dmac-claim/err.txt
Remove-Item Env:DETPS2_TRACE_DMAC
```

Wall **~26.3 s**, EXIT=0.

---

## 2. Floor (claim 100M)

| Field | Value |
|-------|-------|
| PC | `0x0017A0DC` |
| px / prims | **1646610 / 6** |
| gifP1 / gifP2 / gifP3 | 0 / **17** / **0** |
| dmac transfers | **2** |
| imgBytes | **266288** (assist / Host→Local class residual, not Path3) |
| gifCompleted / aborted | **2541 / 2** |
| softgs | `naturalDispfbPx=0` `residualDispfbPx=213010` `compositeSource=LastImageTrx` |
| lit | **213010/286720** |

Matches long-standing GoW claim identity (gifP3=0, residual DISPFB, large lit residual). TRACE print-only.

---

## 3. `[DMAC-TRACE] end` (verbatim)

```text
[DMAC-TRACE] end total finish=2 raise=0 transfersCompleted=2 active=0
[DMAC-TRACE] end ch=VIF1(1) finish=2 owedInc=0 owedPeak=0 preEnableInc=2 preEnablePromote=0 creditAssist=0 w1cWhileOwed=0 tryTakeCis=0 tryTakeOwed=0 raise=0 owedNow=0 preNow=2
[DMAC-TRACE] end ring (newest last, reason 0=finish 1=credit 2=enable 3=take):
[DMAC-TRACE] end   seq=1 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=2 ch=VIF1(1) finish
```

**GIF channel:** silent (all zeros; omitted by dump).  
**Other channels:** silent.

---

## 4. Counter summary

| ch | finish | raise | tryTake | owedPeak | owedNow | creditAssist | preEnablePromote | preNow |
|----|-------:|------:|--------:|---------:|--------:|-------------:|-----------------:|-------:|
| **VIF1** | **2** | **0** | **0** | 0 | 0 | **0** | **0** | **2** |
| **GIF** | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

### 4.1 Read for Q3

| Hypothesis | Evidence @claim 100M | Read |
|------------|----------------------|------|
| Sticky `0x13F5xx` park caused by **missing GIF/VIF Finish+IRQ** | GIF finish=0 entire claim; VIF1 finish=2 only, **raise=0**, **tryTake=0** | **Cannot confirm sticky-window causal** from end-of-run alone — there is almost **no** DMAC completion event stream at all. Not the B3/Haven “finish≫take” under-take pattern. |
| Assist **CreditOwed** papering IRQ | **creditAssist=0** | Assist IRQ credit **not** active in this window |
| Pre-enable without handler | VIF1 `preEnableInc=2` `preEnablePromote=0` `preNow=2` | Finishes while CIM/handler path never promotes — residual pre-enable queue only |
| Promote GoW force-finish END tags into **M5-a Core** | No evidence Finish/IRQ absence during proven sticky park | **Do not** promote. Bias remains **SECONDARY EE thrash / residual assist** until a sticky-window-targeted TRACE exists |

**Honest Q3 status after this seat:** **still open for sticky-window causality**, but **closed against** “S6 DMAC catch-up will fix GoW force-finish.” Claim-class GoW is **DMA-starved / gifP3=0 / residual Soft-GS**, not “handlers under-taking a busy completion stream.” That is closer to **M7 R1 / EE-side** than M5 under-take.

---

## 5. Compare vs B3 claim / Haven claim

| Axis | B3 claim | Haven claim | **GoW claim** |
|------|----------|-------------|---------------|
| total finish | 574 | 202 | **2** |
| Under-take pattern | GIF 0.46 + creditAssist=391 | VIF1 0.50 + creditAssist=1 | **N/A** (no takes, no raises) |
| creditAssist | 391/ch | 1 VIF1 | **0** |
| Primary class | assist-dominated under-take | Core under-take (cleaner) | **pre-DMA / residual pipeline** |

Do **not** fold GoW into the same S6 catch-up experiment as B3/Haven without a different oracle (sticky builder PC/cursor TRACE).

---

## 6. Recommended next (not this seat)

1. Optional: sticky-window probe (`0x13F5xx` cursor / park duration counters) — **assist/telemetry design**, dual-ACK before any Core.  
2. Keep **Q3 default:** force-finish END tags stay **GameQuirks SECONDARY** until causal Finish/IRQ gap proven.  
3. M5 S6 still driven by **Haven** (clean under-take) + **B3** only after assist-quiet.

---

## 7. Sign-off

```text
M5-a Q3 GoW TRACE_DMAC claim @100M
  finish=2 raise=0 GIF silent creditAssist=0
  floor: gifP3=0 imgBytes=266288 residual LastImageTrx lit=213010
  Q3: not B3/Haven under-take class; no S6 promotion for GoW force-finish
  sticky-window causality still open (needs targeted probe)
  No Core. No GodOfWarAssist edits.
```
