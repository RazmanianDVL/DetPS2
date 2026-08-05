# M5-a S1 sample — Haven `DETPS2_TRACE_DMAC=1` (diagnose 20M)

**Date:** 2026-08-04  
**Tip:** `aaf3294`  
**Budget:** **diagnose (20M)** via `blocker-trace` + `--host-present`  
**Env:** `DETPS2_TRACE_DMAC=1` (print only; zero DMA behavior change)  
**Media:** `user-media-haven.json` → `C:/Users/user/Downloads/HavenCalloftheKing(USA).iso` (**present**); serial **SLUS_205.17**  
**Build:** Release → `out/scoreboard-build/DetPS2.Core.dll`  
**Design:** `docs/infra-audits/m5a-dmac-vif-gif-completion-design.md` §1.2 Haven oracle / §8 Q2  
**B3 peer:** `docs/infra-audits/m5a-b3-trace-dmac-sample.md`  
**Scope:** measurement only. **No Core code changes. No GameQuirks / WhiplashAssist. No push.**

---

## 1. Command (repro)

```powershell
# Repo root; tip aaf3294; Release Core already at out/scoreboard-build
$env:DETPS2_TRACE_DMAC = "1"
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
Remove-Item Env:DETPS2_DISABLE_JRGUARD64 -ErrorAction SilentlyContinue
$dll = "out/scoreboard-build/DetPS2.Core.dll"
dotnet exec $dll blocker-trace user-media-haven.json --cycles=20000000 --host-present 2> out/canaries/m5a-haven-trace-dmac/err.txt
# End summary is stderr: lines prefixed [DMAC-TRACE] end
Remove-Item Env:DETPS2_TRACE_DMAC -ErrorAction SilentlyContinue
```

**Why blocker-trace:** S1 end dump (`DumpTraceSummary` prefix `[DMAC-TRACE] end`) is wired on the `blocker-trace` path (`Program.cs`). Per-channel lines are omitted when all counters are zero (see `Dmac.cs` dump filter).

**Artifacts:**

```text
out/canaries/m5a-haven-trace-dmac/
  haven-diagnose-20260804-115941-out.txt
  haven-diagnose-20260804-115941-err.txt   # [DMAC-TRACE] end summary
  last-stamp.txt
```

Wall ~2.7 s, exit 0.

---

## 2. Run floor (blocker-trace claim)

| Field | Value |
|-------|-------|
| serial | `SLUS_205.17` |
| entry | `0x01000008` (high-VA ELF) |
| PC @20M | **`0x010003F0`** (CRT0 / bit-stream decompress band) |
| px / prims | **0 / 0** |
| gifP1 / gifP2 / gifP3 | 0 / 0 / 0 |
| dmac transfers | **0** |
| cdvd / syscalls / sifBytes | 0 / 0 / 0 |
| gifCompleted / aborted | 0 / 0 |
| imgBytes | 0 |
| RealSifRpc | binds=0 calls=0 liveRpcHits=0 |
| gif-path | all zeros; m3p=False |
| softgs | compositeSource=None; lit=0/286720 mostlyBlack=1 |
| IOP | pc=`0x00018638` |

Matches Haven title-port budget honesty: fleet **50M CRT0 black expected**; Soft-GS residual chrome is a **≥100M** claim class (`docs/title-ports/HAVEN.md`). TRACE print-only did not change trajectory.

---

## 3. End-of-run `[DMAC-TRACE]` summary (verbatim)

```text
[DMAC-TRACE] end total finish=0 raise=0 transfersCompleted=0 active=0
```

**No per-channel lines.** Dump skips channels when `finish/owedInc/preEnable/credit/w1c/tryTake/raise` are all zero — so VIF0/VIF1/GIF/SPR_* are **silent**, not “missing TRACE.”

**No ring.** Ring prints only when `TraceDmac && _telemRingCount > 0`; zero finishes/credits/enables/takes ⇒ empty ring.

---

## 4. Counter summary

| ch | finish | owedInc | owedPeak | preEnableInc | preEnablePromote | creditAssist | w1cWhileOwed | tryTakeCis | tryTakeOwed | raise | owedNow | preNow |
|----|-------:|--------:|---------:|-------------:|-----------------:|------------:|-------------:|-----------:|------------:|------:|--------:|-------:|
| *(all 0–9)* | 0 | 0 | 0 | 0 | 0 | **0** | 0 | 0 | 0 | 0 | 0 | 0 |
| **totals** | **0** | — | — | — | — | — | — | — | — | **0** | — | — |

### 4.1 Finish vs take vs owed

N/A — **no DMAC FinishChannel** in the 20M window. Cannot compute take/finish ratios or owed backlog for VIF1/GIF.

### 4.2 Pre-enable / assist

| Signal | Haven @20M |
|--------|------------|
| preEnableInc / promote | 0 / 0 |
| creditAssist (any ch) | **0** |
| w1cWhileOwed | 0 |

`TeamIcoAssist` VIF busy clear + `CreditOwedHandlerCall(VIF1)` residual is **not live** at diagnose: game has not left CRT0, so busy/pending RAM and VIF1 CHCR kicks are not yet in play. This is **not** proof that creditAssist stays 0 at claim; only that diagnose is **pre-oracle**.

---

## 5. Brief compare to B3 sample (`m5a-b3-trace-dmac-sample.md`)

| Axis | **B3** diagnose 20M | **Haven** diagnose 20M |
|------|---------------------|-------------------------|
| Serial | SLUS_210.50 | SLUS_205.17 |
| PC / phase | `0x00123E84` (in-game path-sync / flip window) | `0x010003F0` (**CRT0 decompress**) |
| total finish / raise | **20 / 19** | **0 / 0** |
| VIF1 finish / takeCis / owedNow / creditAssist | 4 / 5 / 2 / **3** | *(silent)* |
| GIF finish / takeCis / owedPeak / owedNow / creditAssist | 8 / 4 / **8** / **7** / **3** | *(silent)* |
| SPR_FROM | finish=8 preNow=8 | *(silent)* |
| w1cWhileOwed | 0 | 0 (vacuously) |
| px / gifP3 / dmac | 877187 / 20 / 20 | 0 / 0 / 0 |
| Q1 (lost CIS vs cadence) | **Directional:** GIF under-take + owed backlog; not W1C | **No data** — no finishes to classify |
| Q2 (Haven VIF software busy) | N/A (B3 seat) | **Still open** — oracle window not reached @20M |
| Assist residual @budget | B3 flip credit **live** (creditAssist=3×2) | TeamIco VIF credit **not yet** (creditAssist=0) |

**Read:** same TRACE harness, opposite boot phase. B3 at 20M is already exercising VIF1/GIF completion + assist re-arm; Haven at 20M is still decompressing the high-VA ELF and has **not started any commercial DMA stream**. A side-by-side of finish/owed/credit counters at diagnose is **not apples-to-apples** for Q2.

**Budget implication for Q2 (design §8 / S4):**

| Budget | Haven expected DMAC activity | TRACE usefulness for Q2 |
|--------|------------------------------|-------------------------|
| diagnose **20M** | none (this sample) | **negative only** — proves CRT0 pre-DMA; cannot score busy vs handler take |
| fleet **50M** | still CRT0 black expected (`PC≈0x01000450`) | likely still empty or near-empty |
| claim **≥100M** (Soft-GS residual class) | VIF1/GIF live; Host→Local residual chrome | **first budget where VIF busy / creditAssist can speak** |

Do **not** re-run Haven TRACE_DMAC at 20M expecting VIF1 under-delivery signals. Next Haven oracle seat: **≥100M** (or a mid window after CRT0 exit ~80–85M if a shorter custom budget is chosen), still print-only TRACE, still no Core behavior change.

---

## 6. Q1–Q5 status after this sample

| ID | Question (short) | Status after Haven TRACE @20M |
|----|------------------|-------------------------------|
| **Q1** | B3 wedge: lost CIS vs cadence vs pre-enable cap? | **Unchanged.** This seat is Haven; B3 peer still owns Q1 evidence. |
| **Q2** | VIF1 software busy = game RAM only (Haven)? | **Still open; budget gate clarified.** @20M: zero finish/credit; PC in CRT0; no busy-flag correlation possible. Design still holds: busy is game RAM cleared by handler (or assist poke) — **not** Core VIF STAT. Need TRACE + busy RAM sample **after** first VIF1 chain (claim-class budget). |
| **Q3** | GoW `0x13F5xx` DMAC vs EE thrash? | **Still open.** No GoW TRACE in this seat. |
| **Q4** | Keep caps 8/64/4; save-state owed? | **No new data** (no peaks). B3 peer remains the only cap-binding signal (GIF owedPeak=8). |
| **Q5** | Play!/PCSX2 oracle before S6? | **Still open / optional.** Haven negative at 20M does not force external oracle. |

### Still open (explicit)

1. **Q2 positive sample:** Haven TRACE_DMAC at **≥100M** (or post-decompress custom) with VIF1 finish / tryTake / creditAssist / ring, plus optional busy/pending RAM poke correlator (measure-only; **no** GameQuirks growth).  
2. Q1 assist-quiet A/B, Q3 GoW sticky TRACE, Q4/Q5 ACKs — unchanged from B3 sample.

---

## 7. Findings (5 bullets)

1. **Haven @diagnose is pre-DMA:** `[DMAC-TRACE] end total finish=0 raise=0 transfersCompleted=0 active=0` — no per-channel lines, no ring.  
2. **PC=`0x010003F0` CRT0 band** matches title-port honesty (50M still black; claim ≥100M for Soft-GS). Zero px/gif/dmac/syscalls/sif is expected, not a TRACE regression.  
3. **Q2 cannot be scored at 20M:** VIF software-busy residual (`TeamIcoAssist` + `CreditOwedHandlerCall(VIF1)`) never arms in this window (`creditAssist=0`).  
4. **vs B3:** B3 already shows GIF under-take (finish=8 takeCis=4 owedNow=7) + live assist credits; Haven is the **empty baseline** of the same harness — useful as a control that TRACE does not invent events.  
5. **Next measurement for Q2:** claim-class Haven TRACE_DMAC (not another diagnose), still measurement-only; keep B3 Q1 path independent.

---

## 8. Sign-off

```text
M5-a S1 Haven TRACE_DMAC sample @diagnose(20M) tip aaf3294
  media: user-media-haven.json ISO present SLUS_205.17
  [DMAC-TRACE] end: finish=0 raise=0 transfersCompleted=0 active=0
  per-ch / ring: empty (all silent)
  floor: PC=0x010003F0 px=0 dmac=0 gifP*=0 (CRT0 pre-DMA)
  vs B3 peer: B3 finish=20 GIF under-take+creditAssist; Haven empty control
  Q2: still open — need ≥100M / post-CRT0 TRACE for VIF busy oracle
  Q1/Q3–Q5: unchanged (this seat Haven-only negative)
  No Core changes. No GameQuirks/WhiplashAssist. No push.
```

---

*Measurement sample for M5-a S4 (Haven oracle budget gate). Supersedes nothing in design/seed; feeds Q2 “diagnose is too early” evidence only.*
