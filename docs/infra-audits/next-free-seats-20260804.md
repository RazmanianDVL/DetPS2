# Next free seats inventory (2026-08-04, post S6 park)

**Author:** grok (solo after wait-check)  
**Tip:** `6d70561`  
**Purpose:** keep dual-orch loop non-empty without escalating to user. Not a claim lock on any row.

---

## Closed / parked this session (do not re-open without new evidence)

| Stream | Status |
|--------|--------|
| M4 S4 mirror + S0 GoW TRACE Class A | plant ON residual accepted |
| M7-c Slice 2a/2b / Slice 3 | IMAGE drain OK; composite residual accepted |
| M5-a S6 | **PARKED** — CATCHUP default OFF after B3 −97% collapse |
| M8-a quiet Prefer fleet | largely done; GoW plant intentional residual |

---

## Ready / high leverage free seats

| ID | Seat | Risk | Notes |
|----|------|------|-------|
| **C1-next** | Why live `sceSifRegisterRpc` table still empty under `IOP_THREADS`+`IOP_REAL_RPC` | Med | C1.1–C1.5 scaffolding landed; LiveRpcHits still 0 on fleet paths — see `c1-registerrpc-growth-next.md` |
| **M3-b/c** | Dual-path denylist / counters formalization | Low–Med | Design exists (`real-sif-rpc-dual-path.md`); depends on C1 registry growth to matter |
| **M5-a redesign** | CATCHUP redesign (rate-limit / Finish-only owed) | High | Only if reopening S6; dual-ACK required |
| **M1 residual** | A3.1 CHCR loop retirement / M1-f GIF_STAT nudge | Med | Backlog; design-first |
| **M4-S0** | GoW reboot-arg fidelity (after TRACE Class A) | Med | Design docs exist; plant stays ON until reboot-gen data |
| **Playability canaries** | Re-run scoreboard identity after S6.1 dormant in tree | Low | Flag-off should be identical; cheap confidence |

---

## Explicit non-seats (blocked / wrong class)

- Promote `DETPS2_DMAC_LEVEL_CATCHUP` default-on  
- B3 CreditOwed product soft-off without dual-ACK  
- Gs.cs composite “prefer natural harder” without oracle  
- GoW END-tag Core writes  

---

## Suggested parallel partition

| Owner | Prefer |
|-------|--------|
| Either | **C1-next** audit → design if code path clear |
| Either | M3 dual-path formalization (docs/code flag-gated) |
| Either | Fleet flag-off identity smoke after recent Core landings |

---

```text
next-free-seats 2026-08-04 tip 6d70561
  C1-next registerRpc growth highest infra leverage post-S6 park
  no user escalate
```
