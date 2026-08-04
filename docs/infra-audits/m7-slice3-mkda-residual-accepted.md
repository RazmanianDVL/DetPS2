# M7 Slice 3 — MK:DA LastImageTrx residual **accepted** (reconcile close)

**Date:** 2026-08-04  
**Tip at run:** docs-only `2dab3a7` (Core binary pre-docs tip; Soft-GS path unchanged by docs commits)  
**Mode:** measurement only — **no Core / Gs.cs / Gif.cs changes**  
**Closes:** dual-ACK path from `docs/infra-audits/m7c-slice3-dispfb-composite-design.md` Q1+Q3 after Q2 tooling survey + fresh claim

---

## 0. Verdict

**Accept** MK: Deadly Alliance claim-tier Soft-GS present residual as **honest documented residual**, same class as Burnout 3 DISPFB=0 and GoW plant residual:

| Field | Value @ claim 100M |
|-------|---------------------|
| `naturalDispfbPx` | **0** |
| `residualDispfbPx` | **46080** |
| `compositeSource` | **LastImageTrx** |
| `imgBytes` | **98304** (real Path3 IMAGE delivered) |
| `gifP3` / `gifCompleted` | **6** / **9** |
| circuit | `naturalDispfb=1` (DISPFB programmed) but local page under DISPFB has **no real RGB** → residual chain samples last BITBLT |

**No `Gs.cs` composite-selection PR.** Code already prefers natural then residual; outcome matches policy. Parent M7-a Q4 (“when is LastImageTrx acceptable”) → **acceptable here** without a live retail pixel oracle this session.

---

## 1. Reconcile command

```powershell
# Repo root; ISO present; Release Core at out/scoreboard-build
$env:DETPS2_TRACE_GIF_BISECT = "1"
dotnet exec out/scoreboard-build/DetPS2.Core.dll blocker-trace user-media-da.json `
  --cycles=100000000 --host-present `
  1> out/canaries/m7-slice3-mkda-reconcile/out.txt `
  2> out/canaries/m7-slice3-mkda-reconcile/err.txt
Remove-Item Env:DETPS2_TRACE_GIF_BISECT
```

**Media:** `user-media-da.json` → `C:/Users/xxraz/Downloads/MortalKombatDeadlyAlliance(USA).iso`  
**Wall:** ~19.4 s, EXIT=0

---

## 2. Soft-GS claim line (verbatim fields)

```text
softgs: imgBytes=98304 dispfbPx=46080 naturalDispfbPx=0 residualDispfbPx=46080 compositeSource=LastImageTrx
softgs-circuit: naturalDispfb=1 enNatural=0 dispfb1=0x148C FBP=0x118000 FBW=640 PSM=0
claim: px=762880 gifP1=0 gifP2=1 gifP3=6 imgBytes=98304 naturalDispfbPx=0 residualDispfbPx=46080 gifCompleted=9
```

Matches GIF_BISECT-4 / m7c-2b floor (`imgBytes=98304`, `gifP3=6`) — deterministic reconciling run, not a new trajectory.

---

## 3. A0 inventory

Historical A0 row `natural=224016` (from older title-port doc) is **stale**. Current tip claim is **`naturalDispfbPx=0` / LastImageTrx**. Annotated on `docs/infra-audits/m7-a0-residual-inventory.md`.

---

## 4. Oracle tooling (Q2) — why we do not wait forever

| Tool | Path | Useful for this Q? |
|------|------|--------------------|
| Play! source + `play-lookup.ps1` | `C:\Windows\Play` | HLE/GameConfig — **not** Soft-GS page RGB |
| PCSX2 tree | `C:\pcsx2` + `Documents\PCSX2` | Possible live ref, **no** DetPS2 DISPFB pixel harness built |
| Decision | — | Do not block Slice 3 close on building a new harness this session |

If a future seat builds DetPS2↔PCSX2 DISPFB page comparison and proves retail natural page is populated at this cycle, reopen as upstream BITBLT/page-promotion work — **not** composite preference flip.

---

## 5. Non-claims

- MK: **Deception** not re-run here (still “likely same class, not independently confirmed”).  
- Does not claim full commercial menu playability for MK:DA.  
- Does not retire Midway Host→Local assists.  
- Does not change B3 R3 residual documentation.

---

## 6. Sign-off

```text
M7 Slice 3 MK:DA residual ACCEPTED tip 2dab3a7+
  claim 100M: naturalDispfbPx=0 residual=46080 compositeSource=LastImageTrx imgBytes=98304 gifP3=6
  A0 natural=224016 SUPERSEDED for current tip
  No Gs.cs change. Dual-ACK Q3 path closed.
```
