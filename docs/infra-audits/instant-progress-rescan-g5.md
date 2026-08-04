# Instant multi-round progress — G5 residual re-scan

**Date:** 2026-08-04  
**Tip:** `e76f0a0` (`test(M3-e): synthetic live SIF RPC register/dispatch smoke`)  
**Scope:** read-only grep + site re-read of `src/DetPS2.Core` vs `docs/infra-audits/instant-progress-audit.md`  
**No Core code changes** in this pass.

## Method

Grep in `src/DetPS2.Core` for:

- `Step(256)`, `Step(128)`, `for (...512`, multi-Step loops
- `force-pump`, `poll-pump`, `OneRoundNudge`, `MaxChcrForce`
- `Sif.Step` / `StepFromSyscall`, `MaxQwPerReceive`, FQC fabricate

Compared against the original audit findings table (pre A3 / M1 wave).

---

## Status of previously audited items

| Prior finding | Risk then | Status at tip | Notes |
|---------------|-----------|---------------|-------|
| `MmioBus` GIF_STAT multi-round poll-pump (16× `Step(128)`) | High→Low after A1 | **Fixed residual Low** | Still single `OneRoundNudgeCycles=128` when FQC==0 (`MmioBus.cs` ~109–113). No multi-round restore. |
| `Dmac` CHCR STR force-pump `for i<512 Step(256)` | **High** | **Mitigated → Med residual** | A3: `MaxChcrForceSteps=16` default; `DETPS2_DISABLE_A3_CHCR_CAP=1` restores 512 (`Dmac.cs` ~45–52, 737–739). Same path3Hold/daDisplayVif gate. Bound ≈16×256=4096 cycle-units vs prior 131072. |
| `Dmac` `DoNormalTransfer` cycle QW cap (A1) | Fixed/Low | **Held** | `DrainCyclesPerQw` still load-bearing. |
| `Gif.ReadStat` unconditional FQC=1 under mask | Med | **Mitigated → Low residual (M1-a)** | Fabricate only with `_path3RaceEvidencePolls` (or kill-switch `DETPS2_DISABLE_M1A_HONEST_FQC=1`). |
| `Gif` instant full `ProcessTransfer` on Receive* | Med | **Mitigated → Low residual (M1-b)** | `MaxQwPerReceiveCall=256`; residual re-enters via `Step()` (`Gif.cs` ~23–35, 915–940, 1215+). |
| `Gif` FIFO instant empty | Low | **Mitigated (M1-c)** | Real GIFtag path via inline QW; kill-switch available. |
| `Sif.Step` up to 16 HLE RPC packets | Low–Med | **Mitigated → Low (M1-d)** | Default `HleRpcBatchPerStep=1`; legacy 16 via kill-switch. Real-RPC generation gate unchanged. |
| Syscall `Sif.Step(N)` mid-instruction | Med | **Mitigated → Low (M1-e)** | `StepFromSyscall`: default defers (or `Step(1)` only for homebrew sync result). Kill-switch restores legacy bulk. |
| `Sif` MMIO / real-RPC generation gate | Clean | **Clean** | — |
| `Pcrtc` sticky VBlank | Low | **Clean/Low** | No multi-Step. |
| `EmotionEngine` COP2 `_vu0.Step(1)` | Clean | **Clean** | — |
| `Scheduler.StepComponents` | N/A correct | **Clean** | — |

---

## NEW residuals only (post-wave; still open)

These are **not** full-strength restorations of the old High GIF_STAT / 512-force-pump class. Highest open multi-round site is the **capped** CHCR force-pump.

### R1 — `Dmac` CHCR force-pump still multi-round (capped)

| Field | Value |
|-------|--------|
| **Location** | `src/DetPS2.Core/Dmac.cs:730–739` |
| **Code** | Under `path3Hold \|\| daDisplayVif` on VIF0/VIF1/GIF CHCR STR: `for (i < maxSteps && Active) Step(256)` with `maxSteps = 16` (or 512 if kill-switch). |
| **Risk** | **Med** (was **High** at 512) |
| **Why residual** | Still manufactures up to 16 scheduler-equivalent DMAC rounds inside one MMIO store; channel finish + IRQ can still land mid-store relative to real hardware. Owed-handler soft queue collateral remains relevant at lower rate. |
| **Suggested next** | Single-round kick or scheduler-driven drain; retire force-step entirely once path-sync titles tolerate honest STR stick. |

### R2 — `MmioBus` GIF_STAT single-round read-side nudge

| Field | Value |
|-------|--------|
| **Location** | `src/DetPS2.Core/MmioBus.cs:109–113` |
| **Code** | One `_dmac.Step(128)` when GIF_STAT read and FQC==0. |
| **Risk** | **Low** residual |
| **Why residual** | Non-idempotent STAT read; still device progress inside a load. Not multi-round. |
| **Suggested next** | Drop nudge once pure RR + A1 QW cap keep path-sync alive; never restore multi-round loop. |

### R3 — `Gif` bounded race-evidence FQC fabricate

| Field | Value |
|-------|--------|
| **Location** | `src/DetPS2.Core/Gif.cs:612–622` |
| **Code** | Masked + FQC==0 → FQC=1 only while `_path3RaceEvidencePolls > 0` (or kill-switch unconditional). |
| **Risk** | **Low** residual |
| **Class** | Fake status (not multi-Step). Honest held-QW FQC still preferred long-term. |

### R4 — `Gif` budgeted process still allows large per-call chunks + edge fallback

| Field | Value |
|-------|--------|
| **Location** | `ProcessTransferBudgeted` / `MaxQwPerReceiveCall=256` (`Gif.cs:23, 929–940`) |
| **Risk** | **Low** (edge **Low–Med** if residual already outstanding: new large transfer processed unbounded rather than drop/reorder) |
| **Class** | Instant-complete HLE class, time-sliced at 256 QW default. |

### R5 — Syscall `StepFromSyscall` homebrew immediate `Step(1)`

| Field | Value |
|-------|--------|
| **Location** | `Sif.cs:679–689`; call sites `BiosHle` SysSifRpcCall (`needImmediateResult: true`) |
| **Risk** | **Low** |
| **Class** | One packet max under M1-d; not multi-round bulk. Default non-immediate path defers entirely. |

### Non-findings (false positives from grep)

| Site | Why not residual multi-round DMAC/GIF |
|------|----------------------------------------|
| `EmotionEngine.cs:729` `for (i < 512)` | MSGBUF diagnostic string read under `DETPS2_TRACE_MSGBUF`, not device Step. |
| `SonyKernelHle.cs:457` `for (i < 512) ee.Step(1)` | RPC end-function EE run-to-sentinel (instruction loop), not `Dmac`/`Sif` multi-round pump on MMIO. Related “work inside syscall” class but different pattern. |
| `RomdirExtractor.cs:40` `for (i < 512)` | BIOS table scan only. |

---

## Grep hit summary (multi-round / pump keywords)

| Pattern | Live sites of interest |
|---------|------------------------|
| `Step(256)` | Only CHCR force-pump loop (`Dmac.cs:739`) |
| `force-pump` | Comments + CHCR write site (`Dmac.cs`) |
| `poll-pump` / `OneRoundNudge` | Comments + single GIF_STAT nudge (`MmioBus.cs`) |
| `for (...512` + Step | Force-pump **only under kill-switch** (cap 16 default); EE MSGBUF / RPC end-func / romdir as above |
| multi-Step on MMIO read | **None** beyond single GIF_STAT nudge |

---

## Verdict (G5-10)

1. **No new High-severity multi-round Step pumps** introduced since the audit.
2. **Primary High site (CHCR 512×Step(256)) is A3-mitigated to 16×** — still the **highest residual** (Med), not eliminated.
3. **A1 GIF_STAT multi-round remains fixed** (single nudge Low residual).
4. **M1-a/b/c/d/e hold**: honest/bounded FQC, budgeted GIF process/FIFO, SIF batch=1, syscall SIF defer.
5. **Recommended residual priority:** R1 CHCR force-pump retire → R2 read-side nudge drop → R3–R5 polish.

**Residual high findings: 0** (none at High; top open = **Med** CHCR force-pump ×16).
