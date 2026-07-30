# DetPS2 — BIOS completion plan (core component)

| Field | Value |
|-------|--------|
| **Status** | **ACTIVE — BIOS-ONLY CAMPAIGN** |
| **Authority** | SCPH70008 ROMDIR (101 entries) + IOPBTCONF + Ghidra IRX decomp + ps2sdk |
| **BIOS image** | Operator `user-media*.json` → SCPH70008 4 MiB (never commit) |
| **Rule** | **No per-title work, no GameQuirks, no title PCs, no commercial scoreboard pushes** until Phase Gate G0 is COMPLETE (all PARTIAL → OK/NONPORT for IOPBTCONF + extended service modules listed below). |
| **Architecture** | Contract HLE in C# (service map), not cycle-accurate guest BIOS OS. Literal IRX exec is Phase L (optional path #12), not a substitute for contract completeness. |

---

## Why this plan exists

Every PS2 title assumes the BIOS/IOP service surface (THREADMAN, SIF, LOADFILE, CDVD, FILEIO, PAD, MC, …) already exists. Per-game assists that paper over missing BIOS contracts **do not scale** and **must stop** until the core HLE map is complete.

**Definition of done for “BIOS complete” in DetPS2:**

1. Every IOPBTCONF @800 module and commercial-fast-path ROMDIR sibling is **OK** or intentional **NONPORT** (no **PARTIAL** left on the required set).
2. Extended service modules that retail software loads from `rom0:` / UDNL / IOPRP (SECRMAN, CLEARSPU, LIBSD, UDNL, X*, MCMAN depth) have **documented contracts + smokes**.
3. EE kernel syscall surface used by CRT0/libkernel is complete enough that generic homebrew + retail CRT0 do not require title plants for kernel primitives.
4. Full smoke suite green; port docs updated; gate doc has **zero PARTIAL** on required rows.
5. **No** `GameQuirks/*` changes in this campaign.

---

## Phase map (G0)

```
  Phase 0  Inventory lock + plan (this doc)                    [ORCHESTRATOR]
      │
  Phase 1  THREADMAN completion (Mbx/Vpl/Fpl, Delay, readyq)   [AGENT-T]
      │
  Phase 2  INTRMAN + TIMEMAN + DMACMAN + SSBUSC deepen           [AGENT-I]
      │
  Phase 3  UDNL IOPRP image apply + SECRMAN/LOADFILE MG path    [AGENT-U]
      │
  Phase 4  MCMAN dual-format FAT + MCSERV residual              [AGENT-M]
      │
  Phase 5  EE kernel syscall audit + missing primitives         [AGENT-E]
      │
  Phase 6  CDVDMAN mechacon residual + CD sibling parity        [AGENT-C]
      │
  Phase 7  LIBSD functional core (not full SPU mixer)           [AGENT-S]
      │
  Phase 8  Integration gate: PARTIAL→OK, docs, smokes, G0 close [ORCHESTRATOR]
      │
  Phase L  (OPTIONAL later) Literal IOP R3000 IRX execution     [#12 — not G0]
```

Phases **1–7** may run in parallel when file ownership is respected. Orchestrator merges only.

---

## Phase 0 — Inventory lock (orchestrator)

| Item | Action |
|------|--------|
| ROMDIR 101 | Already parsed; `ROMDIR_FULL_AUDIT.md` |
| Gate rows | `ROMDIR_GATE.md` — track PARTIAL→OK |
| Open issues | Prefer #14 THREADMAN, #10 MCMAN, #12 IRX (defer L), CDVD residuals |
| Deliverable | This plan committed; agents spawned |

---

## Phase 1 — THREADMAN completion  `AGENT-T`  (#14)

**Files (OWN):**
- `src/DetPS2.Core/KernelHle.cs`
- `src/DetPS2.Core/SonyKernelHle.cs` (THREADMAN-related syscalls only)
- `docs/bios-ports/THREADMAN.md`
- `Tests/SmokeTests.cs` (new THREADMAN smokes only)

**Do:**
1. Message boxes: CreateMbx / DeleteMbx / SendMbx / ReceiveMbx / PollMbx / ReferMbxStatus (decomp `tools/bios-decomp/THREADMAN_ALL.txt` + ps2sdk thmsgbx). Wire EE if syscalls exist; else host API + export intercept for IOP.
2. Variable pools: CreateVpl / DeleteVpl / AllocateVpl / FreeVpl / ReferVpl.
3. Fixed pools: CreateFpl / DeleteFpl / AllocateFpl / FreeFpl / ReferFpl.
4. DelayThread / alarm path (FUN_00002444).
5. Priority-aware ready selection (at least priority bands; full RotateThreadReadyQueue semantics).
6. DeleteSema / ReleaseWaitThread waiter return codes (`0xfffffe57`, `0xfffffe5e`).
7. Smokes for all of the above.
8. Promote THREADMAN row PARTIAL → **OK** in ROMDIR_GATE.md when smokes pass.

**Do NOT:** touch GameQuirks, RealSifRpc game paths, title docs.

**Done when:** smokes green; THREADMAN.md §5 items 1–3,5,8 closed; gate tag OK.

---

## Phase 2 — INTRMAN / TIMEMAN / DMACMAN / SSBUSC  `AGENT-I`

**Files (OWN):**
- `src/DetPS2.Core/IopSystemHost.cs`
- `src/DetPS2.Core/IopDmacManHost.cs`
- `src/DetPS2.Core/IopSsbuscHost.cs`
- `src/DetPS2.Core/IopEeconfHost.cs` (if needed)
- `docs/bios-ports/VBLANK_INTRMAN.md`, `DMACMAN.md`, `SSBUSC_EECONF.md`, `ROMDRV_TIMEMAN.md`
- Tests only for these hosts

**Do:**
1. INTRMAN: deepen RegisterIntrHandler / EnableIntr / DisableIntr / query status to match Ghidra + ps2sdk; document remaining gaps if any.
2. TIMEMAN: hard timer alloc/free/set/alarm contracts; SysClock completeness.
3. DMACMAN: channel request/release/set_handler complete; MMIO window if missing.
4. SSBUSC / EECONF: bus window defaults + init contracts full.
5. Promote PARTIAL → **OK** where contracts + smokes justify it.

**Do NOT:** RealSifRpc commercial paths, GameQuirks.

**Done when:** four PARTIAL rows OK (or documented NONPORT with evidence); smokes green.

---

## Phase 3 — UDNL + SECRMAN + MG LOADFILE  `AGENT-U`

**Files (OWN):**
- `src/DetPS2.Core/IopExtendedBiosHost.cs`
- `src/DetPS2.Core/RealSifRpc.cs` — **only** LOADFILE MG_* / reboot version / Secr-related
- `src/DetPS2.Core/BiosBootHost.cs` — only if wiring UDNL apply
- `docs/bios-ports/LOADFILE.md`, new `UDNL.md` / update `ROMDIR_FULL_AUDIT.md`
- Tests for UDNL/SECR

**Do:**
1. Parse IOPRP/DNAS image containers when path resolvable via FILEIO/ISO (common ROMDIR-in-IMG layouts).
2. Register modules listed in image IOPBTCONF; load ELF IRX into `IopModuleHost` when extractable.
3. SECRMAN: document + implement non-crypto SecrDiskBootFile/SecrCardBootFile success for plain ELF; refuse encrypted with clear errno if detectable.
4. LOADFILE MG_MOD_LOAD / MG_ELF_LOAD: share path loader; no fake MagicGate secrets.
5. Promote UDNL PARTIAL → OK (or PARTIAL with only crypto residual).

**Do NOT:** title version plants, GameQuirks.

**Done when:** smoke opens a synthetic IOPRP-like blob and registers modules; LOADFILE MG path tested.

---

## Phase 4 — MCMAN dual-format FAT  `AGENT-M`  (#10)

**Files (OWN):**
- `src/DetPS2.Core/MemoryCard.cs`, `MemCardManager.cs`
- `src/DetPS2.Core/RealSifRpc.cs` — **only** MCSERV/MCMAN handlers
- `src/DetPS2.Core/Sio2.cs` — only if MC attach contracts
- `docs/bios-ports/MCSERV.md` (+ MCMAN section)
- Tests for MC formats

**Do:**
1. Dual-format FAT (PS1/PS2 card layouts as documented in MCSERV.md residual).
2. MCSERV RPC completeness vs decomp `tools/bios-decomp/MCSERV_ALL.txt`.
3. Promote MCMAN PARTIAL → OK where justified.

**Do NOT:** pad game paths, title assists.

---

## Phase 5 — EE kernel syscall surface  `AGENT-E`

**Files (OWN):**
- `src/DetPS2.Core/SonyKernelHle.cs` — syscalls **not** owned by AGENT-T if Phase 1 done first; coordinate: prefer **after** Phase 1 merge, or only non-THREADMAN ranges (0x00–0x1F, 0x5A+, SIF/GS/OSD)
- `src/DetPS2.Core/BiosHle.cs` if needed
- New `docs/bios-ports/EE_KERNEL_SYSCALLS.md` inventory table
- Tests for newly filled stubs

**Do:**
1. Inventory all Sony EE syscall numbers 0x00–0x7F: implemented vs stub vs missing.
2. Fill critical stubs used by CRT0/libkernel (Alarm, RFU paths, JoinThread if incomplete, etc.) per ps2sdk `syscallnr.h`.
3. Document intentional no-ops (platform-specific).
4. Smokes for new contracts.

**Do NOT:** THREADMAN Mbx work if Phase 1 agent owns it; no GameQuirks.

---

## Phase 6 — CDVDMAN residual  `AGENT-C`

**Files (OWN):**
- `src/DetPS2.Core/Cdvd.cs`
- `src/DetPS2.Core/RealSifRpc.cs` — **only** CDVD SIDs / NCMD/SCMD
- `docs/bios-ports/CDVD.md`
- Tests CDVD

**Do:**
1. Mechacon stand-in depth for retail SCMD/NCMD remaining gaps in CDVD.md.
2. DiskReady / tray / error status parity.
3. Promote CDVDMAN PARTIAL → OK if contracts hold.

**Do NOT:** game FILEIO quirks, GTFS, Midway.

---

## Phase 7 — LIBSD functional core  `AGENT-S`

**Files (OWN):**
- `src/DetPS2.Core/IopExtendedBiosHost.cs` (libsd section) **or** new `IopLibSdHost.cs`
- `src/DetPS2.Core/Spu2.cs` only for LIBSD-facing init hooks
- `docs/bios-ports` new `LIBSD.md`
- Tests

**Do:**
1. Export ordinals that retail modules import (sceSdInit, sceSdSetParam family — from ps2sdk libsd).
2. Minimal functional HLE: init, voice key-on/off stubs calling Spu2 where safe.
3. Promote LIBSD PARTIAL → OK (core) or PARTIAL (mixer residual explicit).

**Do NOT:** full game audio quality; no Midway MSL.

---

## Phase 8 — G0 integration gate (orchestrator only)

1. Merge all phase branches/worktrees.
2. Re-run full Tests smoke suite.
3. Update `ROMDIR_GATE.md`: required set must have **no PARTIAL** (OK or NONPORT only).
4. Update `BIOS_COMPLETION_VERDICT.md` → **BIOS_CORE_COMPLETE** or list remaining PARTIAL with issue links.
5. Wiki BIOS-HLE page.
6. Close GitHub issues only when evidence matches (#14, #10, …).
7. **Only then** may commercial multi-title work resume.

---

## Phase L — Literal IOP IRX execution (NOT G0)

Tracked as #12. Optional architecture path. Does not replace Phases 1–7. Do not block G0 on L.

---

## File ownership matrix (collision avoidance)

| File / area | Owner phase |
|-------------|-------------|
| `KernelHle.cs` | Phase 1 |
| `SonyKernelHle.cs` THREADMAN cases | Phase 1 |
| `SonyKernelHle.cs` other syscalls | Phase 5 (after 1 or non-overlapping ranges) |
| `IopSystemHost`, `IopDmacMan`, `IopSsbusc`, `IopEeconf` | Phase 2 |
| `IopExtendedBiosHost`, UDNL | Phase 3 |
| `MemoryCard`, MCSERV RPC | Phase 4 |
| `Cdvd.cs`, CDVD RPC | Phase 6 |
| LIBSD host / Spu2 hooks | Phase 7 |
| `GameQuirks/**` | **FORBIDDEN entire campaign** |

---

## Agent SOP (mandatory)

1. BIOS / HLE / docs / smokes only.
2. Prefer Ghidra decomp in `tools/bios-decomp/` and ps2sdk over guessing.
3. Prefer SHARED host over plants.
4. Local commits OK; **no push/PR** — orchestrator integrates.
5. End with deliverable: files, smokes, gate tag recommendation, residual list.
6. If blocked on another phase’s file, stop and report — do not edit.

---

## Success metric

**G0 COMPLETE** when:

```text
ROMDIR_GATE required rows: zero PARTIAL
BiosBootHost_IopBtConfContracts OK
BiosRomdirGate_PortDocsForRequiredModules OK
BiosExtendedRomdir_* OK
New phase smokes OK
=== ALL SMOKE TESTS PASSED ===
BIOS_COMPLETION_VERDICT: BIOS_CORE_COMPLETE
```

Until then: **BIOS is the only campaign.**
