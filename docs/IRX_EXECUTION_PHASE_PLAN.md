# Phase plan: Literal BIOS / disc IRX execution (IRX-first pivot)

**Status:** ACTIVE — architectural pivot (2026-07-30)  
**Supersedes:** HLE-first commercial campaign as the *primary* path to playability  
**Related:** GitHub **#12** (literal IOP IRX), G0 HLE remains a *fallback / bootstrap aid*, not the product  
**Doctrine conflict resolution:** Playability via **real module code** wins over inventing C# service clones. Soft-GS remains presentation truth. Host FFmpeg logos stay banned. Title `jr ra` plants are **debt to delete**, not strategy.

---

## 0. Why this plan exists

### What we did wrong

| Approach | Result |
|----------|--------|
| Re-implement IOP services in C# (FILEIO, PAD, CRI-ish, GTFS, MWFILE, …) | Endless trial-and-error; every title dialect is a new plant |
| GameQuirks thrash escapes / path plants / sector credit | Metrics move; games still not playable |
| Treat “BIOS G0 HLE complete” as “platform done” | False confidence — games run **on top of** IOP modules + EE + GS |

### What every other PS2 emulator does

```text
EE game ELF  →  SIF  →  real IRX on IOP R3000  →  devices (CDVD, SIO2, …)
```

They do **not** re-write Sofdec, PADMAN, and FILEIO from scratch for each commercial title as the default path.

### What DetPS2 already has (do not rebuild from zero)

| Asset | State | Use |
|-------|--------|-----|
| `Iop` R3000A interpreter | Exists, stepped, savestate-aware | **Execute** module entry points |
| `IrxLoader` | Real MIPS REL + export/import link | Load BIOS + disc IRX into IOP RAM |
| `RomdirExtractor` / BIOS bind | SCPH70008 ROMDIR inventory | Feed real module bytes |
| `BiosBootHost` IOPBTCONF order | Known | Boot order for real loads |
| SIF / CDVD / SIO2 / DMAC (EE side) | Partial but real | Device model IRX talks to |
| EE + Soft-GS | Commercial bring-up baseline | Keep; improve as needed |
| GameQuirks / RealSifRpc mega-HLE | Large | **Demote** to fallback / delete over phases |

**Critical quote (from `IrxLoader.cs`):** real modules were relocated but  
**“nothing ever executed loaded IOP module code.”**  
That single fact is the pivot: **stop inventing services; run the modules.**

### What this project is for (restated)

1. **Deterministic** EE+IOP+devices (state sync / netplay).  
2. **C#** for controlled, known host code (EE JIT, scheduler, Soft-GS, device models).  
3. **Playable commercial software** by running **the same IRX the console runs**, not a parallel fantasy OS.

C# is **not** an excuse to reimplement Sony’s IOP stack. C# hosts the machine; **IRX is the software**.

### Repo size note

Rough workspace sizes (this tree): `src+docs+tools` ~**225 MB**; `out/` build artifacts ~**1.2 GB**; whole worktree multi‑GB with traces/dumps.  
**Phase 0 hygiene** reclaims most of the “5 GB balloon” without deleting emulator capability.

---

## 1. Success definition

### North-star gate (end of plan)

| Gate | Criteria |
|------|----------|
| **IRX-primary boot** | With operator BIOS + one commercial ISO, IOPBTCONF modules **and** disc IOPRP chain **execute** on `Iop` (instruction stream non-zero in module text), not only HLE `HandleBind` |
| **SIF path live** | EE `sceSif*` / LOADFILE / FILEIO go through **real module code** ↔ SIF ↔ EE for at least one title’s pad + one file open |
| **Playability slice** | At least **one** commercial title: Soft-GS **non-black interactive surface** (title’s real gate — menu or first GS) **without** new title-local thrash plants |
| **Determinism** | Same ISO + same inputs + same cycle budget → identical MasterCycles + Soft-GS hash (smokes + one commercial replay tape) |
| **Debt direction** | Net **deletion** of GameQuirk plants / FILEIO soft-success sprawl; no new FFmpeg-class host media cheats |

### Non-goals (explicit)

- Shipping BIOS/ISO in git (operator-provided only).  
- MagicGate / DRM crypto fidelity as a blocker.  
- Perfect GS accuracy before first playable frame.  
- Keeping every RealSifRpc HLE path forever.

---

## 2. Architecture target

```text
┌─────────────────────────────────────────────────────────────┐
│ EE (C# interp/JIT) — game ELF, kernel syscalls as needed    │
│ Soft-GS / GIF / VU / DMAC (C# devices)                      │
└───────────────────────────┬─────────────────────────────────┘
                            │ SIF (DMA + RPC mailbox) — real protocol
┌───────────────────────────▼─────────────────────────────────┐
│ IOP R3000 (C# interpreter) — **executes IRX text**            │
│ Loaded from: BIOS ROMDIR + disc IOPRP / IRX files             │
│ Devices: CDVD, SIO2, SPU2 stubs, INTC, timers (C# MMIO)     │
└─────────────────────────────────────────────────────────────┘
         optional thin HLE only when IRX cannot run (rare)
```

**Default:** `PreferLiteralIrx = true` (new).  
**HLE:** opt-in fallback per-module or global kill-switch for bisect.

---

## 3. Phase plan

### Phase 0 — Hygiene & freeze (1–2 days)

**Goal:** Stop the bleeding; make the tree workable.

| # | Work | Done when |
|---|------|-----------|
| 0.1 | Delete/gitignore bloated `out/**` rebuild artifacts; `tools/clean-traces`; refuse root `*.txt` dumps | Worktree size dominated by sources, not 1 GB+ `out/` |
| 0.2 | **Freeze** new GameQuirk thrash plants / path invents unless labeled `// DEBT-IRX` and linked to a device bug | PR policy |
| 0.3 | Flag `DETPS2_LITERAL_IRX=0/1` (default **1** for new boots once Phase 2 lands; 0 = old HLE path for bisect) | Env documented in `tools/README.md` |
| 0.4 | Inventory: list every `RealSifRpc` SID/fno that is pure soft-success → “replace by IRX” backlog | Table in this doc §5 |

**Exit:** Clean default build; policy frozen; bisect switch designed.

---

### Phase 1 — IOP execution substrate (3–7 days)

**Goal:** Loaded IRX **runs** on `Iop`, not just sits in RAM.

| # | Work | Done when |
|---|------|-----------|
| 1.1 | After `IrxLoader.Load` + `LinkImports`, **set IOP PC to module start** and schedule IOP quanta in `Ps2System` proportional to EE (tunable ratio) | `InstructionsExecuted` climbs inside module VA range |
| 1.2 | IOP exception / syscall / interrupt path good enough for LOADCORE-style modules (COP0, vectors) | Minimal IRX test module completes `_start` → register library |
| 1.3 | Export/import stubs: imports resolve to **real export addresses** of already-loaded modules (or device traps) | Link + call across two real BIOS modules |
| 1.4 | Savestate: IOP PC/GPR + module list + load bases | Round-trip smoke |
| 1.5 | Trace: `DETPS2_TRACE_IOP=1` dumps PC samples in module names | Debug usable |

**Fixture:** Synthetic IRX (existing smoke) **plus** one tiny real BIOS module (e.g. SYSMEM or heaplib) from operator BIOS extract.

**Exit:** “IRX loaded and **executed** end-to-end” smoke green.

---

### Phase 2 — BIOS IOPBTCONF chain (1–2 weeks)

**Goal:** Boot like a PS2: **real modules from ROMDIR in IOPBTCONF order**.

| # | Work | Done when |
|---|------|-----------|
| 2.1 | `BiosBootHost`: for each IOPBTCONF entry, **extract ELF bytes → IrxLoader → start IOP thread/module** | Chain advances past first N modules without HLE `LoadIrx` no-exec |
| 2.2 | Device MMIO: whatever SYSMEM/LOADCORE/IODMAN/SIFMAN need to not die (implement **devices**, not fake RPC) | Module `_start` returns / registers |
| 2.3 | SIFMAN path: prefer **executing SIFMAN** over pure C# SIF where possible; keep C# DMA engine under it | EE can sifcmd to a live IOP service |
| 2.4 | EE RESET / reboot protocol matches enough for game `SifIopReset` | Reboot generation increments; modules reloaded |
| 2.5 | HLE dual-path: if `LITERAL_IRX=0`, old path remains for regression | Bisect works |

**Oracle:** PCSX2+PINE optional; primarily **IOP PC trace vs expected entry** and no immediate exception storm.

**Exit:** Operator BIOS alone: IOPBTCONF chain executes; EE can complete a **minimal** LOADFILE-style handshake with **live** IOP code (even if game not yet).

---

### Phase 3 — Disc IOPRP + game IRX (1–2 weeks)

**Goal:** Commercial discs load **their** IOP image the normal way.

| # | Work | Done when |
|---|------|-----------|
| 3.1 | `SifLoadModule` / UDNL path: apply **disc IOPRPxxx.IMG** modules via real load+exec | GetVersion ASCII from **real** module behavior where possible |
| 3.2 | FILEIO / PADMAN / SIO2MAN / MCMAN from **BIOS or disc IRX**, not only `RealSifRpc` | At least one title: pad OPEN via executing PADMAN |
| 3.3 | CDVDMAN/CDVDFSV or HLE-CDVD **device** that IRX can call (device accuracy > RPC soft-success) | `sceOpen`/`sceRead` through IRX stack reads ISO bytes |
| 3.4 | Strip or gate `PreferIopRp` RAM plants when real GetVersion works | Plants not required for Haven/GoW version checks |
| 3.5 | Title matrix smoke: SM, B3, BO2, GoW, Dec, DA, Vexx, Whip, Haven — **diagnose 20M** under `LITERAL_IRX=1` | Scoreboard of *execution* metrics (IOP insn in module, binds via real sif) |

**Exit:** One title opens a real game file through **IRX FILEIO** (not host warm + credit). Soft-GS may still be black.

---

### Phase 4 — First commercial playable surface (2–4 weeks)

**Goal:** Playability, not scoreboard theatre.

| # | Work | Done when |
|---|------|-----------|
| 4.1 | Pick **one** title (recommend **SM** or simplest pad+file path). PCSX2+PINE map: threads, waits, first GS. | Written oracle notes in `docs/title-ports/` |
| 4.2 | Fix **only** device/EE/GS bugs blocking that path (no new permanent thrash stubs) | Soft-GS px>0 non-black **or** interactive pad surface per title gate |
| 4.3 | Delete DEBT plants for that title as IRX covers them | Diff removes assist code |
| 4.4 | Determinism: input tape replay hash stable | Smoke + tape |
| 4.5 | Repeat for 2nd title free-riding same IRX stack | Proves generality |

**Exit:** **≥1 commercial title playable surface** under literal IRX path. Celebrate; then widen.

---

### Phase 5 — Demote HLE / reclaim complexity (ongoing)

| # | Work | Done when |
|---|------|-----------|
| 5.1 | `RealSifRpc`: mark paths **IRX-owned**; HLE only if module missing | Document matrix SID → IRX name |
| 5.2 | Delete Midway/B3/BO2/GoW assist stubs replaced by IRX | File size / LOC down |
| 5.3 | Optional: keep thin HLE for modules we refuse (SECRMAN stubs returning “no MagicGate”) | Explicit NONPORT list |
| 5.4 | Netplay/rollback: include IOP module memory + PC in state | Sync test 2P synthetic |

**Exit:** HLE is the exception; IRX is the default story in README/COMPLETENESS.

---

### Phase 6 — Performance & sync polish (after playability)

| # | Work |
|---|------|
| 6.1 | IOP dynarec / block cache **if** needed (still executes same IRX) |
| 6.2 | EE/IOP cycle ratio tuning for speed without desync |
| 6.3 | Soft-GS / present path only after IRX asset stream is real |

Do **not** start here. Playability first.

---

## 4. Phase dependency graph

```text
Phase 0 Hygiene ──► Phase 1 IOP exec ──► Phase 2 BIOS chain ──► Phase 3 Disc IOPRP
                                                                      │
                                                                      ▼
                                                              Phase 4 First playable
                                                                      │
                                                                      ▼
                                                              Phase 5 Demote HLE
                                                                      │
                                                                      ▼
                                                              Phase 6 Perf / netplay polish
```

No parallel “another 9-title plant wave.” Parallel only: device bugs (CDVD vs SIO2) once Phase 1 is green.

---

## 5. Backlog: HLE to replace with IRX (initial)

| Current HLE / plant | Replace with |
|---------------------|--------------|
| FILEIO soft-success unknown fno | Real FILEIO IRX + correct device |
| PADMAN ghost / major version plants | Real PADMAN + SIO2 device |
| LOADFILE GetVersion plants | Real LOADFILE/UDNL after IOPRP |
| GTFS / LGDEV stubs (B3) | Disc IRX + CDVD device |
| MWFILE / MFL path plants (Midway) | Real Midway IRX or correct FILEIO |
| BO2 pack warm + sector credit | Real IOPFILE/FILEIO + pack in EE as game does |
| GoW freelist soft escapes | Only if EE bug; else IRX won’t help — EE truth |
| Host FMV / overlays | Already removed; IPU later |

EE-only bugs (bad interpreters, Soft-GS) stay C# fixes — IRX does not fix a broken EE.

---

## 6. Tooling & oracle (mandatory practice)

| Tool | Role under IRX-first |
|------|----------------------|
| `DETPS2_TRACE_IOP` | Prove execution in module VA |
| `play-lookup` | Still useful for EE-side expectations |
| **PCSX2 + PINE** | Map EE waits / buffers when stuck; compare to DetPS2 PC |
| `scoreboard.ps1` | Track playability metrics — **not** plant success |
| `wall-save/load` | Optional IRX-era walls |

**Rule:** If you don’t know what a thread waits on → **PINE first**, not a new assist.

---

## 7. Risk register

| Risk | Mitigation |
|------|------------|
| IOP incomplete → IRX dies immediately | Phase 1 synthetic + one real module; MMIO trap log |
| SIF timing desync | Deterministic quanta; log mailbox depth |
| Perf regression | Accept slower until Phase 6; optional HLE bisect |
| Some IRX need hardware we stub | Explicit stub device with logged fno; no silent success |
| Team keeps adding plants | Phase 0 freeze; review rejects DEBT without device bug link |

---

## 8. Milestones & tracking

| Milestone | Approx | Artifact |
|-----------|--------|----------|
| M0 | Phase 0 done | Clean tree + policy |
| M1 | Phase 1 done | Smoke: IRX executes |
| M2 | Phase 2 done | BIOS chain executes |
| M3 | Phase 3 done | Disc FILEIO via IRX on 1 title |
| M4 | Phase 4 done | **First commercial playable surface** |
| M5 | Phase 5 ongoing | HLE LOC declining |

Track as GitHub issue epic (reactivate **#12**) with checklists per phase.

---

## 9. Immediate next actions (start now)

1. Merge this plan; update `NEXT_PLAN.md` pointer.  
2. Phase 0: clean `out/`, document `DETPS2_LITERAL_IRX`.  
3. Phase 1.1 spike: after load of minimal IRX, run IOP until module returns — **smallest possible PR**.  
4. No new 9-title HLE plant waves.

---

## 10. One-line strategy

**Stop rewriting the IOP operating system in C#. Load the IRX every other emulator runs, execute them on our deterministic IOP, and fix the device models until the game’s real code path lights Soft-GS.**
