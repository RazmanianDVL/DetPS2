# M7-c Slice 2a — four-title `DETPS2_TRACE_GIF_BISECT` canary

**Date:** 2026-08-04  
**Mode:** observation only — **no Core changes.**  
**Tip:** `d25ec3a` (includes `540a7c8` TRACE_GIF_BISECT)  
**Build:** `out/gif-bisect-build/DetPS2.Core.dll` (Release)  
**Env:** `DETPS2_TRACE_GIF_BISECT=1` · `DETPS2_TRACE_BIOS=0` · host-present via `tools/run-title.ps1`  
**Budget:** **claim 100M** (all four titles finished in ~16–28 s wall; no diagnose fallback needed)  
**Fleet ids:** `god-of-war`, `blood-omen-2`, `mk-deadly-alliance`, `mk-deception` (`tools/scoreboard-fleet.json`)  
**ISOs:** all present (local Downloads paths in `user-media-*.json`) — **none skipped**  
**Output root:** local `out/traces/` only (no UNC emulator writes)

## Method

Per title: `pwsh ./tools/run-title.ps1 -Media <user-media> -Budget claim -BuildOut out/gif-bisect-build -SkipBuild -HostPresent` with `DETPS2_TRACE_GIF_BISECT=1`. Capture stderr `[GIF-BISECT]` line (`Gif.DumpBisectSummary`) plus claim-line scoreboard fields.

Bucket rules from `docs/infra-audits/m7c-path23-image-delivery-design.md` §3–4 / validation §6.1:

| Bucket | Signature |
|--------|-----------|
| **DMA never submitted (zero Path3 IMAGE)** | `path3Kicks≈0` and/or `path3ImageTags=0` with no stalls |
| **Traffic exists but plateaued** | `path3ImageTags>0` with completed plateau and/or `path3ImageStalled` climbing |
| **A0 prior** | GoW/BO2 → zero Path3 · MK:DA/Dec → gifP3 plateau ~6 |

## Summary table

| Title | fleet id | serial | wall s | path3Kicks | path3ImageTags | path3ImageCompleted | path3ImageStalled | lastStallReason | gifP2 | gifP3 | imgBytes | gifCompleted | Bucket | vs A0 guess |
|-------|----------|--------|--------|------------|----------------|---------------------|-------------------|-----------------|-------|-------|----------|--------------|--------|-------------|
| God of War | god-of-war | SCUS_973.99 | 27.7 | **0** | **0** | **0** | **0** | `-` | 17 | **0** | 266288 | 2541 | **zero Path3 IMAGE** (kicks=0) | **Confirms** A0 (GoW gifP3=0 / no Path3 DMA) |
| Blood Omen 2 | blood-omen-2 | SLUS_200.24 | 15.9 | **2** | **0** | **0** | **0** | `-` | 54 | 2 | **0** | 11 | **Path3 kicks, zero IMAGE tags** | **Mostly confirms** zero-IMAGE; mild surprise: `path3Kicks=2`/`gifP3=2` (A0 residual often listed gifP3=0) — still **not** MK plateau |
| MK Deadly Alliance | mk-deadly-alliance | SLUS_204.23 | 19.8 | **6** | **1** | **1** | **24** | `image-partial progress=5888/6144` | 1 | **6** | 98304 | 9 | **plateau + IMAGE partial stall** | **Confirms** A0 MK plateau (`gifP3=6`); stall counters show multi-DMA IMAGE underfill |
| MK Deception | mk-deception | SLUS_208.81 | 23.9 | **4** | **1** | **1** | **24** | `image-partial progress=5888/6144` | 0 | **4** | 557056 | 4 | **plateau + IMAGE partial stall** | **Confirms** same Midway stall class as DA (A0 gifP3≈6 shape; this run gifP3=4) |

## Per-title detail

### 1. God of War (`god-of-war`)

```text
[GIF-BISECT] path3Kicks=0 path3ImageTags=0 path3ImageCompleted=0 path3ImageStalled=0 lastStallReason=-
```

| Field | Value |
|-------|------:|
| claim | `px=1646610 prims=6 gifP1=0 gifP2=17 gifP3=0 imgBytes=266288 … gifCompleted=2541 gifAborted=2` |
| gif-path | `p3=0 p3qws=0 m3p=False mskPath3=8` |
| gif-tags | `packed=2540 image=1` (IMAGE is **not** Path3 — bisect Path3 IMAGE tags=0) |
| PC | `0x0017A0DC` |
| logs | `out/traces/user-media-god-of-war-claim-20260804-130624-{out,err}.txt` · `…json` |

**Classification:** **zero Path3** — DMA for Path3 is never submitted (`path3Kicks=0`). Nonzero `imgBytes` / residual present are assist / Path2 class, not Path3 IMAGE completion. Matches design-doc expectation for Slice 2a GoW arm.

### 2. Blood Omen 2 (`blood-omen-2`)

```text
[GIF-BISECT] path3Kicks=2 path3ImageTags=0 path3ImageCompleted=0 path3ImageStalled=0 lastStallReason=-
```

| Field | Value |
|-------|------:|
| claim | `px=286720 prims=1 gifP1=0 gifP2=54 gifP3=2 imgBytes=0 … gifCompleted=11 gifAborted=1` |
| gif-path | `p3=2 p3qws=14 m3p=False mskPath3=2` |
| gif-tags | `packed=11 image=0` |
| PC | `0x0011C23C` |
| logs | `out/traces/user-media-bloodomen2-claim-20260804-130652-{out,err}.txt` · `…json` |

**Classification:** **not MK plateau.** Path3 kicks exist (2) but produce **no IMAGE tags** (`path3ImageTags=0`, global `image=0`, `imgBytes=0`). Stalls stay 0 — there is nothing partial to stall. Closest A0 bucket remains “no Path3 IMAGE delivery”; refine A0 “gifP3=0” wording to “no Path3 IMAGE” (small packed Path3 traffic can still increment `gifP3`).

### 3. MK Deadly Alliance (`mk-deadly-alliance`)

```text
[GIF-BISECT] path3Kicks=6 path3ImageTags=1 path3ImageCompleted=1 path3ImageStalled=24 lastStallReason=image-partial progress=5888/6144
```

| Field | Value |
|-------|------:|
| claim | `px=762880 prims=5 gifP1=0 gifP2=1 gifP3=6 imgBytes=98304 … gifCompleted=9 gifAborted=0` |
| gif-path | `p3=6 p3qws=76864 m3p=False mskPath3=2` |
| gif-tags | `packed=8 image=1` |
| PC | `0x00114F40` |
| logs | `out/traces/user-media-da-claim-20260804-130709-{out,err}.txt` · `…json` |

**Classification:** **plateaued Path3 with IMAGE partial stalls.** Exactly one Path3 IMAGE tag completed; 24 stall samples at `image-partial progress=5888/6144` (nloop=6144 Midway IMAGE multi-DMA size from matrix docs). `gifP3=6` matches the long-standing DA claim signature. Slice 2b target class.

### 4. MK Deception (`mk-deception`)

```text
[GIF-BISECT] path3Kicks=4 path3ImageTags=1 path3ImageCompleted=1 path3ImageStalled=24 lastStallReason=image-partial progress=5888/6144
```

| Field | Value |
|-------|------:|
| claim | `px=462848 prims=3 gifP1=0 gifP2=0 gifP3=4 imgBytes=557056 … gifCompleted=4 gifAborted=0` |
| gif-path | `p3=4 p3qws=76835 m3p=False mskPath3=2` |
| gif-tags | `packed=3 image=1` |
| PC | `0x001BF594` |
| logs | `out/traces/user-media-deception-claim-20260804-130730-{out,err}.txt` · `…json` |

**Classification:** **same plateau + partial-IMAGE stall class as DA** (identical `path3ImageTags/Completed/Stalled` and `lastStallReason`). `gifP3=4` is slightly under historical charter `gifP3≈6` but bisect shape is the Midway plateau, not zero-Path3.

## Bucket split (Slice 2a output)

| Bucket | Titles | Implication for Slice 2b |
|--------|--------|---------------------------|
| **Zero / no Path3 IMAGE** | GoW (kicks=0), BO2 (kicks>0 but tags=0) | **Not** a pure `Gif.cs` IMAGE-drain fix alone — upstream of Path3 IMAGE submit (EE stream / bind / path choice). Reclassify R0-adjacent / Path3-absent per design §4. |
| **Plateau + IMAGE partial** | MK:DA, MK:Dec | **Promising Slice 2b** — Path3 IMAGE starts (`nloop=6144`) and mostly drains (`5888/6144`) but stalls repeatedly; completed IMAGE tags stuck at 1 while kicks sit at 4–6. |

### vs prior A0 guess (one line)

| Prior A0 (`m7c-path23-image-delivery-design.md` / residual inventory) | This bisect |
|---------------------------------------------------------------------|-------------|
| GoW/BO2 **zero Path3** | **GoW:** full confirm (`path3Kicks=0`). **BO2:** confirm no Path3 IMAGE; small non-IMAGE Path3 kicks only. |
| MK:DA / MK:Dec **gifP3 plateau ~6** | **Confirm** plateau class; **new:** shared `image-partial 5888/6144` ×24 stalls with `path3ImageCompleted=1`. |

## Honest notes / non-claims

- No ISO missing — all four ran claim-tier.
- `imgBytes` nonzero on GoW/DA/Dec can be assist Host→Local and/or partial natural IMAGE; bisect Path3 counters are the Slice 2a truth for Path3 IMAGE.
- GoW `gif-tags image=1` is **global** IMAGE count (Path2-class), not Path3 — do not read as Path3 IMAGE delivery.
- No product-default or Core behavior change in this pass.
- Commit optional (parent may commit).

## Trace index (absolute under worktree)

| Title | out | err | json |
|-------|-----|-----|------|
| GoW | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\out\traces\user-media-god-of-war-claim-20260804-130624-out.txt` | `…-err.txt` | `…json` |
| BO2 | `…\user-media-bloodomen2-claim-20260804-130652-out.txt` | `…-err.txt` | `…json` |
| DA | `…\user-media-da-claim-20260804-130709-out.txt` | `…-err.txt` | `…json` |
| Dec | `…\user-media-deception-claim-20260804-130730-out.txt` | `…-err.txt` | `…json` |

Machine summary: `out/traces/m7c-gif-bisect-4title-summary.json` (re-scrape from logs if needed).

## References

- Telemetry: `src/DetPS2.Core/Gif.cs` (`TraceGifBisect`, `DumpBisectSummary`) · dump site `Program.cs` blocker-trace end  
- Design: `docs/infra-audits/m7c-path23-image-delivery-design.md`  
- A0 residual: `docs/infra-audits/m7-a0-residual-inventory.md` · `m7a-a0-residual-inventory.md`  
- Fleet: `tools/scoreboard-fleet.json`
