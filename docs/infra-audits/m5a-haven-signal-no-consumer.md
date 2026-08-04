# M5-a — Haven S6.2 "signal without consumer" root cause

**Date:** 2026-08-04
**Scope:** investigate only. No Core code changes landed (temp debug prints added, traced, then fully reverted — `git status` clean).
**Input:** `docs/infra-audits/m5a-s6.2-claim-ab-results.md` §1 — Haven `catchupRaise=130` fires but `tryTakeCis` stays flat (VIF1 67→67, GIF 68→67).

**Verdict: not an edge-latch bug, not an orphan-channel bug. VIF1's own real-completion rate structurally outpaces the interrupt-dispatch loop's shared one-take-per-pass servicing rate. The level-catchup mechanism is firing exactly as designed; no amount of more-accurate re-signaling can close a throughput gap.**

---

## 1. Method

Added temporary stderr instrumentation (reverted before writing this doc, zero net diff):
- `Dmac.MaybeLevelCatchupRaise()`: on fire, print which channel(s) satisfy `HasLevelSensitiveDmacWork()` and their `dstat`/`owed` state.
- `SonyKernelHle.TryTakePendingDmacHandler()`: on `found=false`, print any channel with a pending DStat/owed signal but no registered `_dmacHandlers` entry (tests the "orphan channel" hypothesis).
- `EmotionEngine.TryDispatchRegisteredIntcHandler()`: at the `viaDmacFallback` + `src==DmaController` call site (the exact line that invokes `MaybeLevelCatchupAfterDmacDispatch()`), print which channel (`handlerArg`) was actually serviced in that same dispatch pass, plus `CurrentCycle()`.

All three gated behind a new, temporary `DETPS2_TRACE_CATCHUP_DEBUG=1` env var (removed on revert — no flag survives in the tree).

Ran the same claim-tier repro as S6.2: `blocker-trace user-media-haven.json --cycles=100000000 --host-present`, `DETPS2_TRACE_DMAC=1 DETPS2_DMAC_LEVEL_CATCHUP=1 DETPS2_TRACE_CATCHUP_DEBUG=1`.

## 2. Two live hypotheses from S6.2, both ruled out

**H1 — A2 minimum-dispatch-latency gate (`Intc.MinDispatchLatencyCycles=16`) re-triggering off the re-Raise's fresh `CpuLatched` edge, starving dispatch.** Ruled out: successive `DmaController` dispatch passes in the trace are spaced ~512 cycles (VIF1→GIF pair) to ~33,500 cycles (VIF1→VIF1) apart — both far above the 16-cycle floor. The gate is never the limiting factor here.

**H2 — `HasLevelSensitiveDmacWork()` (DStat-sticky OR owed>0) can fire true for a channel with no registered `AddDmacHandler`, so the re-Raise chases a signal `TryTakePendingDmacHandler` can never satisfy.** Ruled out: zero `signal-no-handler` debug lines across the full 100M-cycle run (130/130 `MaybeLevelCatchupRaise` fires). Every time `TryTakePendingDmacHandler` is consulted here, it finds a real, registered handler.

## 3. What actually happens

Chronological trace of the `DmaController` dispatch/catchup interleave (representative excerpt, `cyc` in EE cycles):

```
dispatch-pass servicedChannel=1 cyc=86475344
fire levelSensitive=[ch1(dstat=False,owed=1)]      <- catchupRaise re-Raises
dispatch-pass servicedChannel=2 cyc=86475856        (+512 cyc)
fire levelSensitive=[ch1(dstat=False,owed=1)]      <- same owed value, unchanged
dispatch-pass servicedChannel=1 cyc=86508832        (+32976 cyc)
fire levelSensitive=[ch1(dstat=False,owed=2)]      <- owed climbed +1
dispatch-pass servicedChannel=2 cyc=86509344        (+512 cyc)
fire levelSensitive=[ch1(dstat=False,owed=2)]
dispatch-pass servicedChannel=1 cyc=86542368        (+33024 cyc)
fire levelSensitive=[ch1(dstat=False,owed=3)]
...
```

This pattern holds for essentially the entire post-boot window: `servicedChannel=1` (VIF1) and `servicedChannel=2` (GIF) dispatch passes alternate almost perfectly 1:1 (**68 vs 67** over the 130-fire window), each one a genuine `TryTakePendingDmacHandler` take (`found=true`, real `AddDmacHandler` callback dispatched — this matches `tryTakeCis`+`tryTakeOwed` = 67+1=68 for VIF1 and 67+0=67 for GIF in the final `DMAC-TRACE` summary). The dispatch loop is not stalling and not skipping DmaController — it is *fairly alternating* between the two active channels on almost every attempt.

But `ch1`'s (`VIF1`) own `owed` count climbs monotonically from 1 to 63 across the run regardless — incrementing by +1 specifically on the cycle *following* a `servicedChannel=1` pass, and staying flat after `servicedChannel=2` passes. VIF1 generates real completions (`FinishChannel`) roughly **twice as fast** as GIF does (final `finish`: VIF1=132, GIF=68), yet the two channels are serviced through `TryTakePendingDmacHandler` at a roughly equal 1:1 rate. GIF's completion rate matches its dispatch share exactly (`owedNow` stays ≈0-1 the whole run); VIF1's does not, so its backlog piles up steadily and is never drained.

## 4. Root cause

**This is a throughput mismatch, not a lost/dropped signal.** `TryTakePendingDmacHandler` (`SonyKernelHle.cs`) consumes exactly one channel's completion per call — the lowest-index channel with a pending DStat or owed signal — then returns. Across this run it alternates fairly between VIF1 and GIF because both keep having *something* pending each time it's consulted. `MaybeLevelCatchupRaise` (`Dmac.cs`) is working precisely as its own design intends: it re-Raises the IRQ exactly when real, non-invented backlog remains (`HasLevelSensitiveDmacWork()` correctly reflects genuine state, confirmed — never an orphan signal), and every one of its 130 fires this run is immediately followed by a real dispatch pass that takes a real handler.

The reason `tryTakeCis` doesn't improve under `DETPS2_DMAC_LEVEL_CATCHUP=1` is that **the loop was already dispatching on essentially every opportunity before the flag was ever added** — level-catchup's re-Raise doesn't unlock any *additional* dispatch throughput, it just makes the "there's still work" signal more honest. VIF1's real completion rate structurally exceeds what a strict one-take-per-dispatch-pass, alternating-fairly-with-GIF loop can drain in the same window. No re-signaling mechanism, however accurate, can close a throughput deficit — only servicing more than one pending channel per dispatch pass (or otherwise raising per-pass throughput) could, and that is a materially different, higher-risk Core change than S6's scope, out of this seat's remit.

This reframes the S6.2 "signal without consumer" finding precisely: the signal *does* have a consumer, and that consumer *is* running — VIF1 simply produces work faster than the shared consumer can drain across two competing channels.

## 5. Relationship to Burnout 3's collapse (out of scope here)

Not investigated in this seat (B3's regression is already root-caused separately in `m5a-s6.2b-b3-catchup-rootcause.md` as an early-plateau/thrash pattern, a different mechanism from Haven's flat-non-improvement). Nothing here contradicts or revises that finding.

## 6. Recommendation

No Core change proposed by this seat. This is evidence supporting the existing `m5a-s6-residual-parked.md` decision to park the whole S6 stream (S6.1 dormant, flag stays default-off) — confirms parking was the right call for Haven specifically, for a different underlying reason than B3's (throughput cap vs. thrash/regression), not merely "unconfirmed, out of scope" as the S6.2 doc's honest-assessment section had left it. If a future seat wants to pursue this further, the concrete follow-on would be a design (dual-ACK required, touches `SonyKernelHle.TryTakePendingDmacHandler`) for servicing more than one pending channel per `DmaController` dispatch pass — flagged as a real design question, not attempted here.

## 7. Repro

```powershell
$dll = "src/DetPS2.Core/bin/Release/net9.0/DetPS2.Core.dll"  # local Release build, tip at time of this doc
$env:DETPS2_TRACE_DMAC = "1"
$env:DETPS2_DMAC_LEVEL_CATCHUP = "1"
dotnet exec $dll blocker-trace user-media-haven.json --cycles=100000000 --host-present 2> haven-trace.txt
```
(Debug channel-attribution prints used to produce §3's excerpt were temporary and are not in the tree — reproducing the exact `servicedChannel=`/`fire levelSensitive=` lines requires re-adding them per §1; the `DMAC-TRACE` summary counters alone (`tryTakeCis`, `tryTakeOwed`, `finish`, `owedNow` per channel) are sufficient to confirm the throughput-gap conclusion without them.)
