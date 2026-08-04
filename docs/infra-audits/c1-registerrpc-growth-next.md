# C1 residual — next step for live `sceSifRegisterRpc` growth (audit)

**Date:** 2026-08-04  
**Mode:** read-only audit — **no Core changes**  
**Tip:** `6d70561`  
**Parent designs:** `docs/IOP_MULTITHREAD_AND_REAL_RPC.md`, roadmap M2 C1.1–C1.5  
**Related:** `docs/infra-audits/c1-5-fleet-ab-results.md`, `docs/infra-audits/m3d-liverpc-counters-status.md`

---

## 0. Where C1 stands

| Item | Status |
|------|--------|
| C1.1 IOP thread context table | done (`DETPS2_IOP_THREADS`) |
| C1.2 unique module-entry stacks | done |
| C1.3 yield/park/ready hooks | done |
| C1.4 LiveRpcDispatchEnabled + multi-thread compose | done |
| C1.5 fleet A/B harness | done; **LiveRpcHits not observed** on diagnose paths |
| Live registry population | **still empty in practice** — HLE remains product path |

North star unchanged: IRX is the product; HLE is debt.

---

## 1. Gap (honest)

Scaffolding can switch contexts and prefer a live registry when non-empty.  
**The registry stays empty** because real module `_start` still does not complete `sceSifSetRpcQueue` / `sceSifRegisterRpc` under DetPS2 for commercial titles exercised so far.

C1.5 result language (prior): LiveRpcHits not observed; later M3-d counters for future runs.

---

## 2. Code map (pointers, not a full re-audit)

| Area | Location |
|------|----------|
| Live hit / fallback counters | `RealSifRpc.cs` — `LiveRpcHits`, `LiveRpcFallbacks`, `LiveRpcDispatchEnabled()` |
| Prefer live before HLE | `RealSifRpc.HandleCall` / `TryHandleCallLive` path (~C1.4 / M3-a) |
| IOP multi-context | `Iop.cs` — `DETPS2_IOP_THREADS` |
| Module `_start` entry | `IopModuleHost` / `StartLoadedModule` / literal IRX path |

---

## 3. Recommended next seats (ordered)

| # | Seat | Type | Why |
|---|------|------|-----|
| **1** | Pick one commercial title with disc IRX that *should* register (e.g. FILEIO/LOADFILE worker class) and TRACE `_start` progress under `IOP_THREADS=1` + `IOP_REAL_RPC=1` | measure | Find first yield/park or missing import that prevents registerRpc |
| **2** | If TRACE shows WaitSema/Sleep without C1.3 intercept: wire auto-park for that import only (flag-gated) | implement | Design-first, dual-ACK |
| **3** | Synthetic IRX smoke: parent creates worker → registerRpc → EE CALL hits LiveRpcHits | smoke | Exit criterion from parent design §2 |
| **4** | Only then: M3 denylist/policy polish | design/impl | Registry growth first |

---

## 4. Non-goals

- Expand per-title GameQuirks to fake registerRpc  
- Default-on IOP_THREADS fleet-wide without A/B  
- Touch Intc.cs / EmotionEngine.cs for C1 (locked to other streams)

---

## 5. Dual-ACK open

| ID | Question | Bias |
|----|----------|------|
| C1-N1 | Is seat #1 (TRACE one title to first registerRpc barrier) the correct next C1 implement-prep? | **Yes** |
| C1-N2 | Preferred first title oracle? | Midway / BO2 / SM — whoever has easiest disc IRX + existing media |

---

```text
C1 registerRpc growth next (audit only)
  scaffolding done; registry still empty
  next: TRACE one title to first barrier under IOP_THREADS+REAL_RPC
  no Core this seat
```
