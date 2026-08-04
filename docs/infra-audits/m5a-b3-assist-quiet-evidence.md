# M5-a — Burnout 3 credit-assist quiet evidence (claim 100M)

**Date:** 2026-08-04
**Mode:** ops evidence only — **no Core changes**, **no product default change**, **no push**.
**Scope:** throwaway env-gated probe (`DETPS2_M5A_B3_NO_CREDIT_ASSIST=1`), added then fully reverted in this seat. `git status` clean after.
**Related:** `docs/infra-audits/m5a-b3-trace-dmac-claim-sample.md` (baseline creditAssist=391 finding, flagged "assist-quiet A/B" as the next measurement), `docs/infra-audits/m5a-dmac-vif-gif-completion-design.md` (M5-a design, Q1/Q6/Q7), `docs/infra-audits/m5a-q1-q5-evidence-rollup.md`

## 1. What was tested

`GameQuirks/Burnout3Assist.cs` calls `sys.Dmac.CreditOwedHandlerCall(VIF1, need)` / `(GIF, need)` at three sites: (1) the flip-consumer re-arm path (fires when DMAC is fully idle and the software queue has unprocessed entries, rate-limited to one credit burst per 100k cycles, capped at 512 total rearms), (2) a residual-pending clear path (out==in but a stale pending counter remains, rate-limited per 500k cycles, first 4 hits use real credit before falling back to a direct memory write), and (3) a flip-wait-stub-leave path keyed on specific EE PC ranges (rate-limited per 50k cycles). All three exist to compensate for exactly the kind of real DMAC completion under-take this session's M5-a telemetry already found for B3's VIF1/GIF channels.

Added a throwaway env-gated boolean (`NoCreditAssistProbe`, `DETPS2_M5A_B3_NO_CREDIT_ASSIST=1`) wrapping only the three `CreditOwedHandlerCall` call pairs — left the residual-pending soft-clear fallback (a separate mechanism, a direct memory write) untouched, since suppressing that too would have conflated two different compensations.

Ran `burnout-only.json` (`C:/Users/xxraz/Downloads/Burnout3Takedown.iso`) at claim tier (100M cycles, `--host-present`, `DETPS2_TRACE_DMAC=1`), Release build, twice: once with the probe unset (product baseline) and once with it set (assist fully silenced).

## 2. Result — surprising, not what "creditAssist=391" alone suggested

| Field | Baseline (assist ON) | Assist OFF | Δ |
|---|---:|---:|---|
| exitRequested / exitCode | False / 0 | False / 0 | = (no crash, no hang) |
| DMAC total finish | **574** | **1005** | **+431 (+75%)** |
| VIF1 finish / owedPeak / owedNow / creditAssist | 104 / 64 / 62 / 391 | 226 / **2** / **0** | owed backlog nearly **eliminated** |
| GIF finish / owedPeak / owedNow / creditAssist | 208 / 64 / 63 / 391 | 317 / **3** / **0** | owed backlog nearly **eliminated** |
| gifPath2 / gifPath3 | 412 / 620 | 890 / 1197 | roughly **doubled** |
| imgBytes | 3,352,128 | 7,230,080 | roughly **doubled** |
| prims | 6373 | 11159 | +75% |
| dmac (scoreboard transfer count) | 574 | 1005 | +75% |
| cdvdSectors | 6584 | 6584 | **= identical** (boot/disc progress unaffected) |
| RealSifRpc calls | 304 | 482 | +59% |
| softgs-present `lit` | 0/286720 (mostlyBlack=1) | 0/286720 (mostlyBlack=1) | **= identical** (no visible-output change either way) |
| stderr errors/exceptions | none | none | = |

**Neither run stalls or hangs — both complete claim tier cleanly.** With the assist silenced, real DMAC completions (`finish`) increase substantially rather than the pipeline starving, and the owed-credit backlog (`owedPeak`/`owedNow`) that sat pinned near the 64-depth cap in the baseline drops to near zero. GIF/VIF1 path activity, real IMAGE bytes, and primitive counts all roughly double. The one thing that does **not** change either way is the visible composite output (`lit=0/286720` both runs) — this is the same DISPFB-empty residual class M7's A0 inventory already classified as an orthogonal (Slice 3) gap, not something either DMAC state touches.

## 3. Interpretation

This is the opposite of what a naive read of "creditAssist=391" would predict. The credit-assist mechanism is not simply "filling a starvation gap that would otherwise stall the title" — at claim tier, with it OFF, the real DMAC pipeline processes **more** work, not less. Two plausible (not mutually exclusive, not confirmed here) explanations:

1. **The assist's own rate-limiting under-paces the real hardware's actual completion rate.** The 100k/500k/50k-cycle gates and per-burst caps (`need` maxes at 6) were tuned against the DMAC under-take symptom, but if real completions can actually arrive faster than that once nothing is artificially holding the queue's `pending` counter in a stale state, the assist's own cadence becomes the bottleneck instead of the DMAC.
2. **Manufactured credits and real completions may be competing for the same consumer-side queue slots.** Every `CreditOwedHandlerCall` invocation drains a `need`-sized chunk of the software queue exactly as a real completion would; if the assist "uses up" queue capacity with synthetic completions before real ones arrive, real completions that show up shortly after may have nothing left to drain until the queue refills — an artificial pacing ceiling, not a floor.

**This does not mean B3's assist should be removed** — this is a claim-tier, single-title, non-interactive probe with no evidence about menu/gameplay-visible behavior beyond the unchanged `lit` field, and the M5-a design's own hard rule (never silently change product behavior without dual-ACK + kill-switch + multi-title evidence) fully applies. What it does mean: **the "assist is load-bearing, don't touch M5-a" framing implied by creditAssist=391 alone is not supported by this evidence** — if anything, this is a data point in *favor* of prioritizing M5-a's real Core fix (the actual DMAC completion/handler-callback mechanism), since it suggests the real completion pipeline may already be capable of sustaining more throughput than the current assist-paced cadence allows, once a proper Core-level fix replaces ad hoc per-title credit injection with a correctly-paced generic mechanism.

## 4. Honest gaps / not done here

- Single run each arm, no repeat-for-determinism check (though DetPS2's cycle-stepped model is normally deterministic; not independently re-verified in this seat).
- No diagnose-tier (20M) comparison — this was a claim-tier-only probe, matching the M5-a doc's existing claim-sample precedent.
- No menu/interactive-tier check — `lit` staying unchanged at 0/286720 is a claim-tier snapshot only, not a claim about later gameplay frames.
- Did not investigate *why* real `finish` roughly doubles (the two hypotheses in §3 are plausible readings, not confirmed root cause) — that would need its own DMAC-side trace-timing investigation, out of scope for this evidence-gathering seat.

## 5. Recommendation for M5-a priority

Given B3 now shows an unexpected *positive* signal when its assist is silenced (more real completions, not fewer), combined with Haven's and B3's own already-documented under-take patterns (finish/take ratios well under 1.0), this strengthens rather than weakens the case for M5-a's S6 (real completion/handler-callback fix) being worth prioritizing — the current per-title assist-credit approach may be actively capping throughput that a correct generic fix could recover, on top of the debt-reduction benefit of retiring per-title `CreditOwedHandlerCall` call sites once a real fix lands (same T10-style retirement arc as M4's GetVersion unification).

## 6. Repro

```powershell
# Requires the throwaway DETPS2_M5A_B3_NO_CREDIT_ASSIST env-gate re-added to
# Burnout3Assist.cs (reverted after this seat) -- not present in product tree.
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/scoreboard-build --nologo -v q

# Baseline (assist on)
Remove-Item Env:DETPS2_M5A_B3_NO_CREDIT_ASSIST -ErrorAction SilentlyContinue
$env:DETPS2_TRACE_DMAC = "1"
dotnet exec out/scoreboard-build/DetPS2.Core.dll blocker-trace burnout-only.json --cycles=100000000 --host-present `
  1> out/canaries/m5a-b3-assist-quiet/baseline/out.txt 2> out/canaries/m5a-b3-assist-quiet/baseline/err.txt

# Assist off
$env:DETPS2_M5A_B3_NO_CREDIT_ASSIST = "1"
dotnet exec out/scoreboard-build/DetPS2.Core.dll blocker-trace burnout-only.json --cycles=100000000 --host-present `
  1> out/canaries/m5a-b3-assist-quiet/quiet/out.txt 2> out/canaries/m5a-b3-assist-quiet/quiet/err.txt
```

## 7. Sign-off

```text
M5-a B3 assist-quiet @claim 100M
  Baseline: finish=574 VIF1owedPeak=64 GIFowedPeak=64 creditAssist=391/391 gifP3=620 imgBytes=3352128
  Quiet:    finish=1005 VIF1owedPeak=2 GIFowedPeak=3 creditAssist=0/0 gifP3=1197 imgBytes=7230080
  Neither run crashes/hangs/exits early. cdvdSectors identical (6584). lit unchanged (0/286720).
  SURPRISE: assist OFF shows MORE real DMAC completion activity, not less -- opposite of naive
  "creditAssist=391 = load-bearing" reading. Strengthens case for M5-a S6 prioritization.
  No Core change. No GameQuirks change (reverted). No push.
```
