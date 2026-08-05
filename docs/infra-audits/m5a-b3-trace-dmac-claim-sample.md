# M5-a S3 claim sample — Burnout 3 `DETPS2_TRACE_DMAC=1` (claim 100M)

**Date:** 2026-08-04  
**Tip (docs):** `6676317` (Core binary pre-docs; TRACE print-only)  
**Budget:** **claim 100M** via `blocker-trace` + `--host-present`  
**Env:** `DETPS2_TRACE_DMAC=1` (zero DMA behavior change)  
**Media:** `burnout-only.json` → `C:/Users/user/Downloads/Burnout3Takedown.iso` (**present**)  
**Build:** Release → `out/scoreboard-build/DetPS2.Core.dll`  
**Design:** `docs/infra-audits/m5a-dmac-vif-gif-completion-design.md` §8 Q1  
**Diagnose peer:** `docs/infra-audits/m5a-b3-trace-dmac-sample.md` (20M)  
**Rollup peer:** `docs/infra-audits/m5a-q1-q5-evidence-rollup.md`  
**Scope:** measurement only. **No Core. No GameQuirks. No push of binaries.**

---

## 1. Command

```powershell
$env:DETPS2_TRACE_DMAC = "1"
dotnet exec out/scoreboard-build/DetPS2.Core.dll blocker-trace burnout-only.json `
  --cycles=100000000 --host-present `
  1> out/canaries/m5a-b3-trace-dmac-claim/out.txt `
  2> out/canaries/m5a-b3-trace-dmac-claim/err.txt
Remove-Item Env:DETPS2_TRACE_DMAC
```

Wall **~15.6 s**, EXIT=0.

---

## 2. Floor (claim 100M)

| Field | Value |
|-------|-------|
| px / prims | **30249654 / 6373** |
| gifP2 / gifP3 | **412 / 620** |
| dmac transfers | **574** |
| imgBytes | **3352128** (real large IMAGE) |
| gifCompleted / aborted | **3392 / 206** |
| softgs | `naturalDispfbPx=0` `residualDispfbPx=6515` `compositeSource=LastImageTrx` `dispfbPx=0` |
| lit | **0/286720** (mostly black present — R3 class residual) |

Commercial DMA live far past diagnose 20M. Soft-GS still DISPFB-empty residual class (M7 A0 R3), orthogonal to M5 completion.

---

## 3. `[DMAC-TRACE] end` (verbatim counters)

```text
[DMAC-TRACE] end total finish=574 raise=1345 transfersCompleted=574 active=0
[DMAC-TRACE] end ch=VIF1(1) finish=104 owedInc=103 owedPeak=64 preEnableInc=1 preEnablePromote=1 creditAssist=391 w1cWhileOwed=0 tryTakeCis=297 tryTakeOwed=2 raise=621 owedNow=62 preNow=0
[DMAC-TRACE] end ch=GIF(2) finish=208 owedInc=32 owedPeak=64 preEnableInc=2 preEnablePromote=2 creditAssist=391 w1cWhileOwed=0 tryTakeCis=95 tryTakeOwed=0 raise=724 owedNow=63 preNow=0
[DMAC-TRACE] end ch=SPR_FROM(8) finish=262 owedInc=0 owedPeak=0 preEnableInc=64 preEnablePromote=0 creditAssist=0 w1cWhileOwed=0 tryTakeCis=0 tryTakeOwed=0 raise=0 owedNow=0 preNow=64
```

---

## 4. Counter summary

| ch | finish | tryTakeCis | tryTakeOwed | take≈ | take/finish | owedPeak | owedNow | creditAssist | w1c |
|----|-------:|-----------:|------------:|------:|------------:|---------:|--------:|-------------:|----:|
| **VIF1** | 104 | 297 | 2 | ~299 | **≫1** (assist-inflated) | **64** | **62** | **391** | **0** |
| **GIF** | 208 | 95 | 0 | 95 | **0.46** | **64** | **63** | **391** | **0** |
| **SPR_FROM** | 262 | 0 | 0 | 0 | 0 | 0 | 0 (preNow=64) | 0 | 0 |

### 4.1 vs diagnose 20M

| Axis | Diagnose 20M | **Claim 100M** |
|------|--------------|----------------|
| total finish | 20 | **574** |
| GIF take/finish | 0.50 (4/8) | **0.46** (95/208) — **still under-take** |
| GIF owedPeak / owedNow | 8 / 7 | **64 / 63** — depth-64 **binding** |
| VIF1 creditAssist | 3 | **391** |
| GIF creditAssist | 3 | **391** |
| w1cWhileOwed | 0 | **0** both channels |
| pre-enable cap binding | no | no (promote==inc small) |

**Q1 claim confirmation:** GIF under-take **persists** at claim with real commercial traffic. Lost-CIS still **unsupported** (`w1c=0`). Pre-enable cap still **not** the story. **creditAssist is now dominant** (391 per channel) — Burnout3Assist flip re-arm is flooding owed/raise; pure Core under-delivery cannot be isolated without assist-quiet A/B.

---

## 5. Implications for S6

| Decision | Claim evidence |
|----------|----------------|
| S6 invent credits in Core? | **No** — assists already invent massively (creditAssist=391) |
| S6 raise depth caps? | **No** without A/B — both VIF1/GIF hit **64** |
| S6 level catch-up opt-in? | **Still plausible** for GIF take lag, but **confounded** until assist-quiet or creditAssist-normalized comparison |
| Lost CIS fix class? | **Still unsupported** (w1c=0 at claim) |

**Next measurement (not this seat):** B3 claim with flip-credit silence if env exists; else document that B3 is **assist-dominated** at claim and Haven remains the cleaner Core under-take oracle (creditAssist=1).

---

## 6. Sign-off

```text
M5-a S3 B3 TRACE_DMAC claim @100M
  finish=574 raise=1345
  VIF1: finish=104 takeCis=297 owedPeak=64 owedNow=62 creditAssist=391 w1c=0
  GIF:  finish=208 takeCis=95  owedPeak=64 owedNow=63 creditAssist=391 w1c=0
  Q1: GIF under-take persists; assist-dominated; not lost-CIS; not pre-enable cap
  No Core. No GameQuirks.
```
