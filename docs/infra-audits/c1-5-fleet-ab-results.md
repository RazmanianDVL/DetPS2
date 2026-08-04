# C1.5 fleet A/B results (real run)

**Task:** G6-3  
**Date:** 2026-08-04  
**Command:** `pwsh ./tools/canary-c1-5-fleet-ab.ps1`  
**Budget:** diagnose (20M cycles)  
**Fleet:** default four — mk-shaolin-monks, burnout-3, blood-omen-2, god-of-war  
**Build:** once → `out/scoreboard-build` (Release)  
**TraceRealRpc:** off (default)  
**NativeMetrics:** scoreboard-metrics JSON

## Verdict

| Field | Value |
|-------|-------|
| **Harness verdict** | **STABLE** |
| Meaning | Both arms completed; no flag-on crash vs baseline; metric deltas informational only |
| MENU / C1.5-done claim? | **No** — infrastructure A/B only |

## Counts

| Metric | Count |
|--------|------:|
| Titles selected | 4 |
| Both RAN (pass for crash/exit honesty) | 4 |
| SKIP (media/ISO) | 0 |
| Baseline crash / non-zero exit | 0 |
| Flag-on crash / non-zero exit | 0 |
| Flag-on worse (hard REGRESS) | 0 |
| Flag-on recovered | 0 |
| Soft flags only | 1 title (`cdvd-drop>50%` on MK SM) |

**Pass/fail/regress (honest summary):**

- **Pass (both RAN, no crash):** 4 / 4  
- **Fail (flag-on crash / hard exit vs baseline):** 0  
- **Regress (harness `flagOnWorse`):** 0  
- **Skip:** 0 (all ISOs present under configured media paths)

## LiveRpcHits

| Surface | Result |
|---------|--------|
| `scoreboard-metrics` JSON keys | **No** `liveRpcHits` / `liveRpcFallbacks` / `unknownServiceCalls` (confirmed on metrics artifacts; fields are binds/calls only for RPC) |
| Console / out-err logs | **No** `[REALRPC` lines (`realrpcDbg` 0→0 all titles; `-TraceRealRpc` not set) |
| Counter existence in Core | Yes on `RealSifRpc` — not exported to this harness path (see `docs/infra-audits/m3d-liverpc-counters-status.md`) |

**Conclusion:** LiveRpcHits **did not appear** in this run’s traces/metrics. Absence is an **observability gap**, not evidence that live dispatch never fired.

## Per-title (baseline → flag-on)

| Title | base | flag-on | px | binds | calls | dmac | cdvd | honest | notes |
|-------|------|---------|----|-------|-------|------|------|--------|-------|
| MK Shaolin Monks | RAN | RAN | 286720 (=) | 17→13 | 292→41 | 7 (=) | 198842→1 | neutral | PC 0x00464B14→0x00474D94; soft `cdvd-drop>50%` |
| Burnout 3: Takedown | RAN | RAN | 877187 (=) | 11 (=) | 42 (=) | 20 (=) | 425 (=) | neutral | stable |
| Blood Omen 2 | RAN | RAN | 286720 (=) | 14 (=) | 62 (=) | 8 (=) | 2211 (=) | neutral | stable |
| God of War | RAN | RAN | 1433600 (=) | 10 (=) | 21 (=) | 2 (=) | 136 (=) | neutral | stable |

Wall times were short (~1.8–4.3 s per title per arm at diagnose budget).

## Env arms

| Arm | `DETPS2_IOP_THREADS` | `DETPS2_IOP_REAL_RPC` | `DETPS2_NO_REAL_RPC` |
|-----|----------------------|-----------------------|----------------------|
| baseline | unset | unset | unset |
| flag-on | `1` | `1` | cleared |

## Artifacts

| Path | Role |
|------|------|
| [`out/canaries/c1-5/20260804-085401/summary.md`](../../out/canaries/c1-5/20260804-085401/summary.md) | Human summary |
| [`out/canaries/c1-5/20260804-085401/summary.json`](../../out/canaries/c1-5/20260804-085401/summary.json) | Machine summary |
| `out/canaries/c1-5/20260804-085401/baseline/` | Per-title metrics + out/err |
| `out/canaries/c1-5/20260804-085401/flag-on/` | Per-title metrics + out/err |
| `out/canaries/c1-5-harness-console.log` | Full harness console capture |

## Follow-ups (not done in G6-3)

1. Wire `liveRpcHits` / `liveRpcFallbacks` into scoreboard-metrics (M3-d packaging).  
2. Optional re-run with `-TraceRealRpc` for `[REALRPC` line scrape.  
3. Investigate MK SM flag-on path divergence (binds/calls/cdvd/PC) under IOP threads + real-RPC — soft only at diagnose; recheck at verify if C1 claims depend on SM.

---

*Real harness execution. No Core code changes. No push.*
