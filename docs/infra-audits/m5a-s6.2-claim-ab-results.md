# M5-a S6.2 — claim-tier (100M) A/B: level catch-up (DETPS2_DMAC_LEVEL_CATCHUP)

**Date:** 2026-08-04
**Tip:** `3fa29b3` (S6.1 `b36dfd1` + doc amend)
**Build:** Release → `out/s62-build/DetPS2.Core.dll`
**Scope:** measurement only. **No Core code changes. No push.**
**Design ref:** `docs/infra-audits/m5a-s6-level-catchup-design.md` §2.4 acceptance table

**Verdict: MIXED-TO-NEGATIVE. Haven flat (does not meet the "improves" bar). B3 severely regresses (fails the "not worse" bar). Recommend against any further promotion (S6.4/S6.5) until root-caused.**

---

## 1. Haven (`SLUS_205.17`), claim 100M, `--host-present`

Command: `blocker-trace user-media-haven.json --cycles=100000000 --host-present`, `DETPS2_TRACE_DMAC=1`.

| Field | Baseline (flag unset) | `DETPS2_DMAC_LEVEL_CATCHUP=1` | Δ |
|---|---|---|---|
| total finish | 202 | 200 | -2 |
| total raise | 204 | 332 | +128 |
| catchupRaise | 0 | **130** | mechanism fires as designed |
| VIF1 finish | 134 | 132 | -2 |
| VIF1 tryTakeCis | 67 | 67 | **0 (no improvement)** |
| VIF1 owedNow | 63 | 62 | -1 (marginal) |
| GIF finish | 68 | 68 | 0 |
| GIF tryTakeCis | 68 | 67 | **-1 (slightly worse)** |
| GIF owedNow | 0 | 1 | **+1 (slightly worse)** |
| exit | 0 | 0 | same |

**Reading:** `catchupRaise=130` proves the mechanism is genuinely re-Raising 130 times — it is not a no-op. But the actual `tryTakeCis` (real handler-take) counts do not improve on either channel, and GIF's `owedNow`/`tryTakeCis` move slightly in the wrong direction. **Design §2.4's Haven acceptance gate ("take/finish improves or owedNow decreases") is not met** — the result is flat-to-marginally-worse, not the hoped-for improvement. The re-Raise signal is reaching the IRQ line (raise count jumps from 204→332) but is not translating into more real `AddDmacHandler` invocations.

## 2. Burnout 3 (`SLUS_210.50`), claim 100M, `--host-present`

Command: `blocker-trace burnout-only.json --cycles=100000000 --host-present`, `DETPS2_TRACE_DMAC=1`. (Correct media file is `burnout-only.json`, not `user-media-*burnout*` — no such file exists; confirmed via `tools/scoreboard-fleet.json`'s `burnout-3` fleet entry.)

| Field | Baseline (flag unset) | `DETPS2_DMAC_LEVEL_CATCHUP=1` | Δ |
|---|---|---|---|
| **px** | 30,249,654 | **877,187** | **-97%** |
| **prims** | 6373 | **172** | **-97%** |
| gifP2 | 412 | 12 | -97% |
| gifP3 | 620 | 20 | -97% |
| imgBytes | 3,352,128 | 65,728 | -98% |
| **gifCompleted** | 3392 | **92** | **-97%** |
| gifAborted | 206 | 6 | -97% |
| residualDispfbPx | 6515 | 0 | -100% |
| RealSifRpc binds | 13 | 12 | -1 |
| **RealSifRpc calls** | 304 | **45** | **-85%** |
| total finish (DMAC) | 574 | 20 | -97% |
| catchupRaise | 0 | 14 | mechanism fires |
| exit code | 0 | 0 | both clean, no crash/exception |
| wall time | not captured exactly | ~21.7s (full 100M ran, not an early exit) | — |

**This is a severe, real regression, not a wash.** B3's actual game/boot progression collapses to roughly 3% of baseline across every real-work metric (pixels, primitives, GIF completions, RPC calls) with the flag on. It is **not** a crash or an early exit — the process runs the full 100M-cycle budget and exits cleanly (`EXIT=0`), it just does dramatically less real work in that budget. This directly fails the design's own acceptance gate ("Flag-on B3 | finish/take not worse").

**Verified, not assumed:**
- **Reproducible:** re-ran flag-on B3 a second time — identical `px=877187`, deterministic, not noise/a fluke.
- **Kill-switch confirmed causal:** `DETPS2_DMAC_LEVEL_CATCHUP=1` + `DETPS2_DISABLE_M5A_DMAC=1` together fully restores baseline (`px=30249654`, `calls=304`, exact match) — proves the level-catchup mechanism itself is the cause, not some other confound in this build.
- Baseline numbers in both titles match this session's earlier recorded values exactly (Haven VIF1 finish=134/owedNow=63; B3 GIF finish=208/creditAssist=391) — confirms this build is consistent with prior session canaries, not a stale/different tip artifact.

## 3. Flag-off no-op spot check

Both baseline runs above (Haven and B3, flag unset) match this session's already-recorded pre-S6.1 numbers exactly (Haven: VIF1 finish=134, owedNow=63, tryTakeCis=67; B3: GIF finish=208, tryTakeCis=95, owedPeak=64, creditAssist=391). **Confirmed: flag-off remains a true no-op** in this build, consistent with the existing `Dmac_LevelCatchup_DefaultOff_NoOp` smoke.

## 4. Honest assessment

Neither of the design's two Q6.4 acceptance rows holds:

- Haven does **not** show the hoped-for improvement — the mechanism fires (`catchupRaise=130`) but doesn't convert into more real handler takes.
- B3 shows a **severe** regression, not "not worse" — real game progression drops to ~3% of baseline.

**Two live hypotheses, neither confirmed here (root-causing this is out of this seat's scope — measure-only per directive):**
1. The re-Raise may be firing at a point in the IRQ/dispatch cycle that steals scheduler time or EE cycles away from real work without actually reaching a productive `AddDmacHandler` body — i.e., it re-asserts the IRQ line but something downstream (masking state, handler dispatch ordering, or interaction with B3's own `CreditOwedHandlerCall` assist, which is still active and unmodified in this pass) causes the EE to spend cycles servicing a storm of re-raised-but-unproductive interrupts instead of making forward progress.
2. B3 specifically has much higher baseline DMAC activity than Haven (574 vs 202 total finishes) and an active assist already manufacturing 391 credits/channel — the level-catchup mechanism may interact destructively with that existing assist-driven credit pressure in a way Haven (creditAssist=1, near-zero) never exercises. This would mean the mechanism might behave differently on titles without a heavy assist already active — untested here.

**Recommendation: do NOT proceed to S6.4 (ambient re-Raise) or any S6.5 default-on discussion until this B3 regression is root-caused.** This is exactly the kind of finding S6.2's claim-tier gate was designed to catch before further promotion — the design doc's own discipline worked as intended.

## 5. Repro commands

```powershell
$dll = "out/s62-build/DetPS2.Core.dll"
$env:DETPS2_TRACE_DMAC = "1"

# Haven baseline
Remove-Item Env:DETPS2_DMAC_LEVEL_CATCHUP -ErrorAction SilentlyContinue
dotnet exec $dll blocker-trace user-media-haven.json --cycles=100000000 --host-present 2> haven-baseline-err.txt

# Haven catchup=1
$env:DETPS2_DMAC_LEVEL_CATCHUP = "1"
dotnet exec $dll blocker-trace user-media-haven.json --cycles=100000000 --host-present 2> haven-catchup-err.txt

# B3 baseline
Remove-Item Env:DETPS2_DMAC_LEVEL_CATCHUP -ErrorAction SilentlyContinue
dotnet exec $dll blocker-trace burnout-only.json --cycles=100000000 --host-present 2> b3-baseline-err.txt

# B3 catchup=1
$env:DETPS2_DMAC_LEVEL_CATCHUP = "1"
dotnet exec $dll blocker-trace burnout-only.json --cycles=100000000 --host-present 2> b3-catchup-err.txt

# B3 catchup=1 + kill (confirms causal)
$env:DETPS2_DISABLE_M5A_DMAC = "1"
dotnet exec $dll blocker-trace burnout-only.json --cycles=100000000 --host-present 2> b3-catchup-killed-err.txt
```

## 6. Artifacts

```text
out/canaries/m5a-s62-haven/{baseline,catchup}-{out,err}.txt
out/canaries/m5a-s62-b3/{baseline,catchup,catchup-rerun,catchup-killed}-{out,err}.txt
```
