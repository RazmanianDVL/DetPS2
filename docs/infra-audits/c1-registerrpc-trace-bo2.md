# C1 TRACE — Blood Omen 2 under `IOP_THREADS` + `IOP_REAL_RPC` (registerRpc barrier)

**Date:** 2026-08-04  
**Tip:** `0171e55`+ (Core pre-docs; C1 scaffolding already on tip)  
**Budget:** verify **50M** (blocker-trace `--host-present`)  
**Media:** `user-media-bloodomen2.json` → Blood Omen 2 USA ISO (**present**)  
**Env:**  
`DETPS2_IOP_THREADS=1`  
`DETPS2_IOP_REAL_RPC=1`  
`DETPS2_TRACE_RPC=1`  
`DETPS2_TRACE_REALRPC=1`  
**Mode:** measurement only — **no Core changes**  
**Parent:** `docs/infra-audits/c1-registerrpc-growth-next.md`

---

## 0. One-line result

**Live SIFCMD RPC queue remains empty** for the entire 50M run: every `[REALRPC-DBG]` line shows `firstQueue=0x00000000`. Scoreboard: **`liveRpcHits=0` `liveRpcFallbacks=0`**, `binds=14` `calls=71`. HLE still carries product RPC. Multi-thread + prefer-live flags **do not** produce a live `sceSifRegisterRpc` table on BO2 at this budget.

---

## 1. Command

```powershell
$env:DETPS2_IOP_THREADS = "1"
$env:DETPS2_IOP_REAL_RPC = "1"
$env:DETPS2_TRACE_RPC = "1"
$env:DETPS2_TRACE_REALRPC = "1"
dotnet exec out/scoreboard-build/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json `
  --cycles=50000000 --host-present `
  1> out/canaries/c1-registerrpc-trace-bo2/out.txt `
  2> out/canaries/c1-registerrpc-trace-bo2/err.txt
```

Wall **~8.1 s**, EXIT=0.

---

## 2. Scoreboard floor

```text
claim: px=286720 prims=1 gifP1=0 gifP2=0 gifP3=2 imgBytes=0 …
RealSifRpc: binds=14 calls=71 unknownServiceCalls=0 unknownBindSids=0 liveRpcHits=0 liveRpcFallbacks=0
```

Matches long-standing BO2 soft-gs / HLE identity class (gifP3=2 residual path).

---

## 3. REALRPC-DBG pattern (err)

Representative lines (pattern identical for all sampled CALL attempts):

```text
[REALRPC-DBG] sid=0x80000592 sifcmdLoadBase=0x1C064000 chainHead=0x1C066A60 firstQueue=0x00000000
[REALRPC-DBG] sid=0x80000006 … firstQueue=0x00000000
[REALRPC-DBG] sid=0x80000001 … firstQueue=0x00000000
[REALRPC-DBG] sid=0x80000400 … firstQueue=0x00000000
[REALRPC-DBG] sid=0x00534E03 … firstQueue=0x00000000
```

| Observation | Implication |
|-------------|-------------|
| `sifcmdLoadBase` / `chainHead` non-zero | SIFCMD module image / chain head structure present in IOP RAM |
| **`firstQueue` always null** | No live `sceSifSetRpcQueue` / queue node linked — **register path never completed** |
| `liveRpcHits=0` | No real handler dispatch |
| Many distinct sids still HLE-served | Product path is HLE dual-path miss → fallback (expected when registry empty) |

---

## 4. Barrier class (this seat)

| Class | Supported? |
|-------|------------|
| Empty live queue at CALL time | **Yes — primary signal** |
| Which IRX `_start` should have registered | **Not isolated** — needs module-load / THREADMAN TRACE next |
| C1.3 WaitSema park missing | **Not proven** — no per-module `_start` progress counters this seat |
| Flag not armed | **Unlikely** — TRACE_REALRPC DBG lines fire; IOP_REAL_RPC prefer path reached |

**Next TRACE seat (recommended):** log IRX `StartLoadedModule` / `_start` entry+exit + thread create/yield under same flags; name the first disc IRX that never returns from `_start` before registerRpc.

---

## 5. Non-claims

- Does not prove IOP_THREADS broken — only that registry still empty on BO2@50M.  
- Does not re-run claim 100M (verify enough for empty-queue identity).  
- Does not touch Haven / Dmac / S6.

---

```text
C1 BO2 TRACE @50M IOP_THREADS+REAL_RPC tip 0171e55+
  liveRpcHits=0 firstQueue always 0
  barrier: live queue never populated (registerRpc not reached)
  next: _start / module TRACE to name the stuck IRX
  No Core.
```
