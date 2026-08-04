# M3-d — LiveRpc counters status (`LiveRpcHits` / `LiveRpcFallbacks` / `UnknownServiceCalls`)

**Date:** 2026-08-04  
**Mode:** read-only grep / call-site inventory.  
**Owned code:** `src/DetPS2.Core/RealSifRpc.cs`  
**Related:** `docs/infra-audits/real-sif-rpc-dual-path.md` §6 item 4 (design asked for these counters for scoreboard).

---

## 1. Already present?

| Counter | Property | Increment sites | Reset | Savestate |
|---------|----------|-----------------|-------|-----------|
| **`UnknownServiceCalls`** | Yes — `RealSifRpc.UnknownServiceCalls` (~L551) | HLE unknown SID / unmatched CALL path (~L3777) | `Reset` / ctor path (~L644) | **Yes** — `WriteState` / `ReadState` (~L763, L787) |
| **`LiveRpcHits`** | Yes — (~L557–558), doc “C1.4” | Successful live `TryDispatchRealRegisteredRpc` (~L1346–1348) | (~L645) | **No** — explicit “Scoreboard / diagnostics only — not part of savestate” |
| **`LiveRpcFallbacks`** | Yes — (~L559–562) | Live registry hit but dispatch failed → HLE fall-through (~L1350–1351) | (~L645) | **No** — same as hits |

**Verdict:** all three counters **exist and are wired on the live/HLE CALL path**. Dual-path design §6.4 (“add LiveRpcHits / LiveRpcFallbacks next to UnknownServiceCalls”) is **implemented in Core**, not still a sketch-only gap.

Related env / gate already present: `LiveRpcDispatchEnabled()` (`DETPS2_NO_REAL_RPC`, `DETPS2_IOP_REAL_RPC`).

---

## 2. Scoreboard / trace wiring?

| Surface | `UnknownServiceCalls` | `LiveRpcHits` | `LiveRpcFallbacks` |
|---------|----------------------|---------------|---------------------|
| `blocker-trace` summary (`Program.cs` ~L595) | **Yes** — `unknownServiceCalls=` on `RealSifRpc:` line | **No** | **No** |
| `scoreboard-metrics` JSON (`Program.cs` ~L880+) | **No** (only `binds` / `calls`) | **No** | **No** |
| `tools/scoreboard.ps1` scrape | Parses existing blocker-trace fields; no LiveRpc keys | — | — |
| `tools/SCOREBOARD_SCHEMA.md` | No LiveRpc fields found | — | — |
| Unit / IOP smokes | Asserted in places (`Tests/SmokeTests.cs` UnknownServiceCalls; `Tests/IopExecSmokes.cs` Hits/Fallbacks/Unknown for synthetic live register) | **Yes** (M3-e-style smokes) | **Yes** |

**Verdict:** counters are **partially** scoreboard-wired.

- `UnknownServiceCalls` is visible on **blocker-trace text** only.  
- `LiveRpcHits` / `LiveRpcFallbacks` are **not** printed on blocker-trace and **not** in scoreboard-metrics JSON.  
- Smokes already depend on the properties directly (good for CI; weak for fleet scrape).

---

## 3. Gap for M3-d?

**M3-d remaining work is observability packaging, not inventing counters.**

| Gap | Priority | Touch |
|-----|----------|--------|
| Extend `blocker-trace` `RealSifRpc:` line with `liveRpcHits=` / `liveRpcFallbacks=` | **P0 for M3-d** | `src/DetPS2.Core/Program.cs` ~L595 |
| Add same fields to `scoreboard-metrics` JSON next to `binds`/`calls` | **P0/P1** | `Program.cs` metrics dict ~L880; optional schema note in `tools/SCOREBOARD_SCHEMA.md` |
| Optional: scrape helpers in `tools/scoreboard.ps1` / compare scripts | P2 | tools only |
| Persist LiveRpc counters in savestate | **Out of scope** unless rollback needs them — code intentionally excludes them today | — |
| WP-49 fail-fast when IRX-owned SID hits HLE | Separate dual-path item; not a counter gap | `RealSifRpc` + matrix |

**Not a gap:** presence of the three properties; increment on live hit / live fallback / unknown HLE; smoke coverage for synthetic live BIND+CALL (`IopExecSmokes.RealRpc_SyntheticLiveRegister_BindCallHits`).

---

## 4. Suggested M3-d acceptance (minimal)

1. Fresh blocker-trace after a live-hit smoke or IRX session shows non-zero `liveRpcHits` on the summary line when appropriate.  
2. `DETPS2_NO_REAL_RPC=1` run: hits stay 0; unknown path still increments as today.  
3. scoreboard-metrics JSON includes the three fields (or at least Hits/Fallbacks + keep text for Unknown).  
4. No savestate format bump required.

---

## 5. File index

| File | Finding |
|------|---------|
| `src/DetPS2.Core/RealSifRpc.cs` | Properties + increments + reset; Live* not in WriteState |
| `src/DetPS2.Core/Program.cs` | Prints Unknown only; metrics JSON omits all three Live/Unknown detail |
| `Tests/IopExecSmokes.cs` | Asserts LiveRpcHits / Fallbacks / UnknownServiceCalls |
| `Tests/SmokeTests.cs` | UnknownServiceCalls usage |
| `docs/infra-audits/real-sif-rpc-dual-path.md` | Original request for counters |

---

*Status report only. No Core changes in this note.*
