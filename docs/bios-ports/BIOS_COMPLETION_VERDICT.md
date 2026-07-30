# BIOS HLE completion verdict — G0 core complete

| Field | Value |
|-------|--------|
| **Executive verdict** | **BIOS_CORE_COMPLETE** (G0) |
| Date | **2026-07-30** |
| Scope | SCPH70008 contract HLE — IOPBTCONF @800 + commercial-fast-path + extended services in plan |
| Plan | `docs/bios-ports/BIOS_COMPLETION_PLAN.md` |
| Tracking | GitHub **#29** |
| BIOS target | SCPH70008 ROMDIR / IOPBTCONF / EE kernel syscall surface |

---

## Executive summary

Against the **G0 BIOS-only campaign** definition of done:

1. **All IOPBTCONF @800 + commercial-fast-path required rows** are **OK** or intentional **NONPORT** — **zero PARTIAL** on the required set.
2. Extended services deepened: UDNL image apply **OK**, MCMAN dual-format **OK**, LIBSD core **OK**, SECRMAN plain path **PARTIAL** (MagicGate residual only).
3. THREADMAN Mbx/Vpl/Fpl/priority/DelayThread **OK** with smokes.
4. EE kernel syscall inventory published (`EE_KERNEL_SYSCALLS.md`) + Alarm HLE.
5. Full `Tests` suite: **`=== ALL SMOKE TESTS PASSED (Phase 56 + media) ===`**.

This is **contract HLE completeness**, not literal R3000 execution of every BIOS IRX (#12 / Phase L remains optional).

**Commercial multi-title work may resume** under the standing rule: prefer shared BIOS/HLE fixes over GameQuirks when a bug is generic.

---

## G0 checklist

| # | Criterion | Result |
|---|-----------|--------|
| 1 | Required gate rows: no PARTIAL | **PASS** — see `ROMDIR_GATE.md` |
| 2 | `BiosBootHost_IopBtConfContracts` | **PASS** (`required=26`) |
| 3 | `BiosRomdirGate_PortDocsForRequiredModules` | **PASS** |
| 4 | Extended smokes (UDNL/SECR/LIBSD/THREADMAN/MC/CDVD/Alarm) | **PASS** |
| 5 | Full suite green | **PASS** |
| 6 | No GameQuirks in G0 commits | **PASS** (BIOS hosts/docs/tests only in phase commits) |

---

## Phase roll-up

| Phase | Agent | Result |
|-------|-------|--------|
| 0 Plan | Orchestrator | `BIOS_COMPLETION_PLAN.md` |
| 1 THREADMAN | AGENT-T | Mbx/Vpl/Fpl/priority/delay → **OK** |
| 2 INTR/TIME/DMAC/SSBUSC | AGENT-I | All **OK** |
| 3 UDNL/SECR/MG | AGENT-U | UDNL **OK**; SECRMAN **PARTIAL** (crypto) |
| 4 MCMAN | AGENT-M | Dual-format FAT → **OK** |
| 5 EE syscalls | AGENT-E | Inventory + Alarm |
| 6–7 CDVD/LIBSD | AGENT-CS | CDVDMAN **OK**; LIBSD **OK (core)** |
| 8 Integration | Orchestrator | This verdict + suite green |
| L IRX exec | deferred | #12 |

---

## Intentional residuals (do not reopen G0)

| Residual | Issue / note |
|----------|----------------|
| MagicGate crypto | SECRMAN PARTIAL — no console secrets |
| ADDDRV / RMRESET / XMTAPMAN depth | Optional PARTIAL names |
| Literal IOP R3000 IRX execution | #12 Phase L |
| LIBSD full dual-core mixer / DSP | Documented in LIBSD.md |
| MCMAN ECC / XMCSERV full table | MCSERV.md residuals |
| Event-flag wait-queue priority fairness | THREADMAN §5 residual |
| Generation-bit IOP object IDs | Only if IRX exec lands |

---

## Code anchors

| Piece | Path |
|-------|------|
| Plan | `docs/bios-ports/BIOS_COMPLETION_PLAN.md` |
| Gate | `docs/bios-ports/ROMDIR_GATE.md` |
| Full ROMDIR audit | `docs/bios-ports/ROMDIR_FULL_AUDIT.md` |
| EE syscalls | `docs/bios-ports/EE_KERNEL_SYSCALLS.md` |
| Extended host | `IopExtendedBiosHost.cs`, `IopLibSdHost.cs` |
| THREADMAN | `KernelHle.cs` Mbx/Vpl/Fpl/Delay |

---

*G0 closed by orchestrator after multi-agent phase campaign 2026-07-30.*
