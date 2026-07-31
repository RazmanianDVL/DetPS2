# IRX-first mega phase plan — orchestrator edition

**Status:** ACTIVE PIVOT — **ASAP playability** via literal BIOS/disc IRX  
**Commit anchor:** main (keep this file authoritative)  
**Epic:** [#12](https://github.com/RazmanianDVL/DetPS2/issues/12)  
**Orchestration model:** **10 concurrent agents** on **isolated tracks** (file ownership below). Orchestrator merges, smokes, pushes, updates issues/wiki.  
**Supersedes:** HLE plant waves as primary strategy; prior 6-phase sketch expanded here into **50 work packages (WP-00 … WP-49)** across **10 tracks**.

---

## Executive summary (read this first)

### The mistake

We spent enormous time **re-implementing IOP services in C#** and title plants.  
Other PS2 emulators **load and run the real IRX**. DetPS2 already has:

- `Iop` R3000 interpreter  
- `IrxLoader` with real REL + import/export link  
- ROMDIR / IOPBTCONF inventory  

…but historically **“nothing ever executed loaded IOP module code”** (`IrxLoader.cs`).

### The fix

```text
EE game (C#)  →  SIF  →  IOP R3000 **executing real IRX**  →  C# devices (CDVD/SIO2/…)
```

C# = machine (EE, Soft-GS, devices, determinism).  
**IRX = the software.** Stop inventing a second OS.

### North-star (when we stop saying “behind”)

| Gate | Criteria |
|------|----------|
| **G1** | IOPBTCONF modules from operator BIOS **execute** (non-zero insn in module text) |
| **G2** | Disc IOPRP chain **executes**; GetVersion comes from live stack where applicable |
| **G3** | ≥1 commercial title: **FILEIO open+read** of a real game file through **executing FILEIO IRX** |
| **G4** | ≥1 commercial title: **pad OPEN** through **executing PADMAN** |
| **G5** | ≥1 commercial title: **Soft-GS non-black interactive surface** (title’s real gate) **without new thrash plants** |
| **G6** | Determinism: tape replay hash stable under `LITERAL_IRX=1` |
| **G7** | Net deletion of GameQuirk plant LOC; HLE is fallback only |

### Absolute freezes (orchestrator enforces)

1. **No new multi-title HLE plant waves.**  
2. **No host FFmpeg / synthetic logos.**  
3. **No new GameQuirk** unless ticket says `blocks IRX WP-XX device X` and orchestrator approves.  
4. Agents **own tracks only** — do not edit foreign files without handoff.  
5. Every WP ends with: build + named smoke + comment on #12.

---

## 10 agent tracks (parallel ownership)

| Track | Agent role | **OWNED files / areas** | Forbidden |
|-------|------------|-------------------------|-----------|
| **T0** | **Orchestrator** (you + this agent) | Merge, smoke, push, #12, wiki, `docs/IRX_*`, `NEXT_PLAN` | Day-to-day IRX impl |
| **T1** | IOP core exec | `Iop.cs`, `IopDisassembler.cs`, exception/syscall vectors | RealSifRpc, Assists |
| **T2** | IRX loader / modules | `IrxLoader.cs`, `IopModuleHost` (or wherever LoadIrx lives), module list | EE quirks |
| **T3** | BIOS boot chain | `BiosBootHost.cs`, `RomdirExtractor.cs`, IOPBTCONF ordering | GameQuirks |
| **T4** | SIF bridge | `Sif.cs`, SIF parts of `RealSifRpc` only as **bridge**, `SifRpc.cs` | Title assists |
| **T5** | CDVD device | `Cdvd.cs`, ISO path, mechacon ready for IRX | Midway assists |
| **T6** | SIO2 / pad / MC devices | `Sio2.cs`, `PadInput.cs`, `MemoryCard.cs` device surface | FILEIO HLE sprawl |
| **T7** | EE kernel / LOADFILE surface | `SonyKernelHle.cs`, `KernelHle.cs`, LOADFILE EE side | IOP interpreter |
| **T8** | Disc IOPRP / UDNL | `IopExtendedBiosHost.cs` UDNL path, disc IRX load | Soft-GS |
| **T9** | Soft-GS / GIF / present | `Gs.cs`, `Gif.cs`, `Vif*.cs`, Desktop present only | IOP |
| **T10** | Debt demolition + tooling | Strip GameQuirks / soft-success; `tools/*` IRX traces; size hygiene | New plants |

> **Note:** 10 work agents = T1–T10; T0 is orchestrator. Fan-out max **10 parallel** when dependencies allow (see § Parallel waves).

---

## Work package catalog (WP-00 … WP-49)

Each WP: **ID · Track · Depends · Deliverable · Exit test · Est**

### Block A — Foundation & freeze (WP-00 … WP-04)

| ID | Track | Depends | Deliverable | Exit test | Est |
|----|-------|---------|-------------|-----------|-----|
| **WP-00** | T0/T10 | — | Document `DETPS2_LITERAL_IRX=0\|1` (default **1** after WP-08); bisect switch design | Doc in tools/README + this file | 0.5d |
| **WP-01** | T10 | — | Clean `out/`, traces, root dumps; `.gitignore` hardened; size report | Worktree rebuildable; sources dominate size | 0.5d |
| **WP-02** | T0 | — | Freeze PR policy: reject plant waves; label `debt-hle` | Written in CONTRIBUTING + #12 | 0.25d |
| **WP-03** | T10 | — | Inventory matrix: every RealSifRpc SID → “IRX name or DEBT” CSV/md | `docs/irx/HLE_TO_IRX_MATRIX.md` | 1d |
| **WP-04** | T2 | — | Module registry API audit: LoadIrx, RegisterModule, export tables, start hooks | Design note in `docs/irx/MODULE_RUNTIME.md` | 0.5d |

**Block A exit:** Policy + hygiene + matrix. Agents T1–T9 unblocked for B.

---

### Block B — Make IRX actually run (WP-05 … WP-14)

| ID | Track | Depends | Deliverable | Exit test | Est |
|----|-------|---------|-------------|-----------|-----|
| **WP-05** | T1 | WP-04 | IOP step API: run N insns; trap unknown COP/MMIO to log | Unit: 1k insns deterministic | 1d |
| **WP-06** | T1 | WP-05 | Exception/syscall vectors match R3000 enough for IRX `_start` | SYSCALL smoke | 1–2d |
| **WP-07** | T2 | WP-04 | After Load+Link: **create runnable module context** (entry, gp, resched) | Struct + tests | 1d |
| **WP-08** | T2+T1 | WP-05,07 | **First exec:** load synthetic IRX → set PC → run until return/halt | **Smoke `Irx_ExecutesMinimal`** | 1–2d |
| **WP-09** | T2 | WP-08 | Import stubs call **real export** of already-loaded module | Two-module link smoke | 1–2d |
| **WP-10** | T1 | WP-06 | IOP interrupts: VBlank/timer enough for module idle loops | IRX can WaitEventFlag-style loop without death | 1–2d |
| **WP-11** | T0 | WP-08 | `Ps2System.RunFor` schedules **IOP quanta** when `LITERAL_IRX=1` | MasterCycles + IOP insn both advance | 1d |
| **WP-12** | T2 | WP-08 | Savestate: module list + load bases + IOP PC/GPR | Round-trip smoke | 1d |
| **WP-13** | T10 | WP-08 | `DETPS2_TRACE_IOP=1` samples PC → module name map | Trace file readable | 0.5d |
| **WP-14** | T2+T3 | WP-08 | Load **one real BIOS IRX** (SYSMEM or HEAPLIB) from operator ROMDIR and run `_start` | Smoke `Irx_ExecutesBiosSysmem` (needs media) | 2d |

**Block B exit = G1 partial:** IRX execution is real. **Critical path.**

---

### Block C — BIOS IOPBTCONF chain (WP-15 … WP-24)

| ID | Track | Depends | Deliverable | Exit test | Est |
|----|-------|---------|-------------|-----------|-----|
| **WP-15** | T3 | WP-14 | Parse IOPBTCONF → ordered list of module names | Unit vs known SCPH70008 order | 0.5d |
| **WP-16** | T3 | WP-15,14 | Boot: sequential extract+load+exec for first **5** modules | Log chain; no exception storm | 2d |
| **WP-17** | T3 | WP-16 | Complete **@800 required** chain (G0 list) with exec | All required start or explicit trap | 3d |
| **WP-18** | T5 | WP-11 | CDVD MMIO surface IRX expects (status/ready/read) | CDVDMAN can poll without hang (or logged trap) | 2–3d |
| **WP-19** | T4 | WP-11 | SIF DMA/mailbox: EE write visible to IOP; IOP reply visible to EE | Round-trip smoke bytes | 2d |
| **WP-20** | T4 | WP-19,09 | Prefer **executing SIFMAN** for sifcmd path (HLE DMA engine underneath OK) | EE sifcmd completes with live IOP | 3d |
| **WP-21** | T6 | WP-11 | SIO2 MMIO + pad bytes for PADMAN | Device unit tests | 2d |
| **WP-22** | T7 | WP-19 | EE LOADFILE path talks to **live** IOP LOADFILE when present | GetVersion non-fake under LITERAL_IRX | 2d |
| **WP-23** | T3 | WP-17 | Reboot: `SifIopReset` tears down + reloads chain | Generation++ ; chain re-exec | 2d |
| **WP-24** | T0 | WP-17,19 | Integration: BIOS-only boot report JSON (modules exec counts) | CLI `irx-boot-report` | 1d |

**Block C exit = G1:** BIOS chain executes. EE can handshake.

---

### Block D — Disc IOPRP & commercial IRX (WP-25 … WP-34)

| ID | Track | Depends | Deliverable | Exit test | Est |
|----|-------|---------|-------------|-----------|-----|
| **WP-25** | T8 | WP-23 | UDNL applies **disc IOPRP image** with **LoadIrx+exec** (not name-only register) | Log IRX starts from disc image | 2–3d |
| **WP-26** | T8 | WP-25 | IOPRP version string path matches EE strcmp without RAM plant | Haven/GoW version check without plant | 2d |
| **WP-27** | T5+T2 | WP-25 | FILEIO IRX from disc/BIOS executes; open `cdrom0:` file | **G3** on one title | 3–5d |
| **WP-28** | T6+T2 | WP-25,21 | PADMAN IRX executes; pad OPEN port0 | **G4** on one title | 3–5d |
| **WP-29** | T6 | WP-28 | MCMAN/MCSERV via IRX or thin device | MC probe no Exit | 2d |
| **WP-30** | T4 | WP-27 | Demote FILEIO soft-success unknown fno when IRX owns sid | Diff removes soft-success | 1d |
| **WP-31** | T8 | WP-25 | MOD_LOAD non-empty paths for Whiplash-class `/bin/*.IRX` | Trace real paths | 2d |
| **WP-32** | T0 | WP-27,28 | Scoreboard `LITERAL_IRX=1` diagnose fleet (9 titles) metrics only | JSON artifacts | 1d |
| **WP-33** | T10 | WP-30 | Delete BO2 sector-credit / warm-as-game-Open confusion | No credit without Open | 1d |
| **WP-34** | T10 | WP-26 | Delete version RAM plants where IRX path works | Assists shrink | 1d |

**Block D exit = G2+G3+G4** on at least one title each.

---

### Block E — First playable commercial surface (WP-35 … WP-41)

| ID | Track | Depends | Deliverable | Exit test | Est |
|----|-------|---------|-------------|-----------|-----|
| **WP-35** | T0 | WP-32 | Pick title #1 (recommend simplest FILEIO+pad). Write oracle charter | Issue subtask | 0.25d |
| **WP-36** | T0+any | WP-35 | **PCSX2+PINE** dump: threads, waits, first GS PC | `docs/irx/oracle-<title>.md` | 1–2d |
| **WP-37** | T9 | WP-27 | Soft-GS: ensure GIF path not stuck M3P when game submits | gifP3↑ with real prims | 2d |
| **WP-38** | T1–T8 | WP-36 | Fix **only** device/EE bugs on critical path (no plants) | Compare DetPS2 vs PINE | 3–7d |
| **WP-39** | T9 | WP-38 | Non-black Soft-GS frame + pad interactive if required | **G5** | 2–5d |
| **WP-40** | T10 | WP-39 | Remove all DEBT plants for title #1 | LOC down | 1d |
| **WP-41** | T0 | WP-39 | Determinism tape replay | **G6** | 1d |

**Block E exit = G5+G6.** This is the “we’re not behind on a ghost” gate.

---

### Block F — Second title free-ride + widen (WP-42 … WP-45)

| ID | Track | Depends | Deliverable | Exit test | Est |
|----|-------|---------|-------------|-----------|-----|
| **WP-42** | T0 | WP-39 | Title #2 (different middleware: Midway vs Criterion vs SN) | Charter | 0.25d |
| **WP-43** | T2–T8 | WP-42 | Only missing IRX/devices for title #2 | Open+pad+GS progress | 3–7d |
| **WP-44** | T9 | WP-43 | Soft-GS surface title #2 | Non-black / interactive | 2–5d |
| **WP-45** | T0 | WP-44 | Fleet report: IRX-primary vs HLE residual | Wiki + #12 | 0.5d |

---

### Block G — Demolish HLE debt (WP-46 … WP-49)

| ID | Track | Depends | Deliverable | Exit test | Est |
|----|-------|---------|-------------|-----------|-----|
| **WP-46** | T10 | WP-39 | Strip MidwayBootAssist thrash/logo residue | Build green | 2d |
| **WP-47** | T10 | WP-39 | Strip B3 flip/LGDEV stubs replaced by IRX | Build green | 2d |
| **WP-48** | T10 | WP-39 | Strip BO2/GoW/Dec/DA plants replaced by IRX | Build green | 2d |
| **WP-49** | T4+T10 | WP-46–48 | RealSifRpc: IRX-owned SIDs throw if HLE hit under LITERAL_IRX | Fail-fast bisect | 2d |

**Block G exit = G7.**

---

## Parallel waves (10 agents ASAP)

Orchestrator launches waves when deps met. **Do not start E before B+C.**

### Wave 1 — NOW (max parallel after A)

| Agent | WP |
|-------|-----|
| T10 | WP-01, WP-03 (serial in track) |
| T0 | WP-00, WP-02 |
| T2 | WP-04 |
| T1 | WP-05 (start after WP-04 design note exists — can draft in parallel) |

### Wave 2 — Exec (critical path)

| Agent | WP |
|-------|-----|
| T1 | WP-05 → WP-06 → WP-10 |
| T2 | WP-07 → WP-08 → WP-09 → WP-12 |
| T0 | WP-11 (with T1/T2) |
| T10 | WP-13 |

### Wave 3 — BIOS chain + devices (wide parallel)

| Agent | WP |
|-------|-----|
| T3 | WP-15 → WP-16 → WP-17 → WP-23 |
| T5 | WP-18 |
| T4 | WP-19 → WP-20 |
| T6 | WP-21 |
| T7 | WP-22 |
| T0 | WP-24 |

### Wave 4 — Disc commercial

| Agent | WP |
|-------|-----|
| T8 | WP-25 → WP-26 → WP-31 |
| T5+T2 | WP-27 |
| T6+T2 | WP-28 → WP-29 |
| T4 | WP-30 |
| T0 | WP-32 |
| T10 | WP-33, WP-34 |

### Wave 5 — Playable

| Agent | WP |
|-------|-----|
| T0 | WP-35, WP-36, WP-41, WP-42, WP-45 |
| T9 | WP-37, WP-39, WP-44 |
| T1–T8 | WP-38, WP-43 (bugfix only) |
| T10 | WP-40, WP-46–49 |

---

## Agent prompt skeleton (orchestrator paste)

```text
You are DetPS2 IRX-first agent Track T# (see docs/IRX_EXECUTION_PHASE_PLAN.md).
OWNED: <files>
WP: WP-XX only. Depends satisfied: <ids>.
LITERAL_IRX path. No GameQuirk plants. No FFmpeg. No foreign files.
Done: build Release, smoke name <...>, report metrics, local commit only.
Orchestrator merges. Comment #12 with WP-XX DONE + SHA.
```

---

## Dependency DAG (compressed)

```text
A(00-04)
  └─► B(05-14) exec   ──► C(15-24) BIOS chain
                            ├─► D(25-34) disc IRX
                            │     └─► E(35-41) first playable  ──► F(42-45) second title
                            │                                      └─► G(46-49) demolish HLE
                            └─ devices 18/19/21 parallel under C
```

---

## Metrics dashboard (update every merge)

| Metric | How | Target |
|--------|-----|--------|
| `iop_insn_in_module` | Trace | >0 then climbing |
| `modules_started` | Boot log | ≥ IOPBTCONF count |
| `fileio_open_via_irx` | Flag | 1+ titles |
| `pad_open_via_irx` | Flag | 1+ titles |
| `softgs_px` / interactive | Scoreboard | G5 |
| `gamequirk_loc` | cloc | decreasing after E |
| `worktree_gb` | du | << multi-GB junk |

---

## Risk & mitigation

| Risk | Mitigation |
|------|------------|
| IOP dies on first real IRX | WP-08 synthetic first; MMIO trap log (T1) |
| 10 agents thrash same file | Track ownership; orchestrator rejects cross-track |
| “Just one plant” creeps back | WP-02 freeze; PR bot / human reject |
| Perf tank | Accept until G5; Phase F optional IOP cache later |
| MagicGate modules | NONPORT stub; never block chain |
| EE-only bugs | T7/T9; IRX won’t fix bad EE |

---

## Orchestrator daily loop

1. `git fetch`; list open WPs with deps met.  
2. Spawn ≤10 agents (worktree isolation) with track ownership.  
3. On completion: smoke → merge → push `main` → comment #12 checklist.  
4. Update this file’s dashboard if gates flip.  
5. **Never** schedule HLE plant waves.

---

## Immediate “do this hour” list

1. **WP-00/01/02** (T0+T10) — freeze + clean.  
2. **WP-04** (T2) — module runtime design (short).  
3. **WP-05+07+08** (T1+T2) — **first executed IRX smoke** — highest ROI in the entire plan.  
4. Everything else waits on WP-08 green.

---

## Relation to old HLE campaign

| Old artifact | Fate |
|--------------|------|
| GameQuirks assists | Debt → Block G delete |
| RealSifRpc mega tables | Bridge + demote → IRX-owned |
| Scoreboard NEAR plants | Ignore until G5 under LITERAL_IRX |
| BIOS G0 HLE | Fallback if `LITERAL_IRX=0` bisect |

---

## One-line strategy

**50 work packages, 10 tracks, one rule: run the IRX; fix the machine under them until Soft-GS shows the real game — no more ghost-chasing HLE.**
