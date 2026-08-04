# M5-a S1 sample — Burnout 3 `DETPS2_TRACE_DMAC=1` (diagnose 20M)

**Date:** 2026-08-04  
**Tip:** `f19144e`  
**Budget:** **diagnose (20M)** via `blocker-trace` + `--host-present`  
**Env:** `DETPS2_TRACE_DMAC=1` (print only; zero DMA behavior change)  
**Media:** `burnout-only.json` → `C:/Users/xxraz/Downloads/Burnout3Takedown.iso` (**present**)  
**Build:** Release → `out/scoreboard-build/DetPS2.Core.dll`  
**Design:** `docs/infra-audits/m5a-dmac-vif-gif-completion-design.md` §4.2 Phase 0 / §8 Q1–Q5  
**Scope:** measurement only. **No Core code changes. No push.**

---

## 1. Command (repro)

```powershell
# Repo root; tip f19144e; Release Core already at out/scoreboard-build
$env:DETPS2_TRACE_DMAC = "1"
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
Remove-Item Env:DETPS2_DISABLE_JRGUARD64 -ErrorAction SilentlyContinue
$dll = "out/scoreboard-build/DetPS2.Core.dll"
dotnet exec $dll blocker-trace burnout-only.json --cycles=20000000 --host-present 2> out/canaries/m5a-b3-trace-dmac/err.txt
# End summary is stderr: lines prefixed [DMAC-TRACE] end
Remove-Item Env:DETPS2_TRACE_DMAC -ErrorAction SilentlyContinue
```

**Why blocker-trace:** S1 end dump (`DumpTraceSummary` prefix `[DMAC-TRACE] end`) is wired on the `blocker-trace` path (`Program.cs`). Interval dumps also fire every 4096 finishes when TRACE is set (this run never hit the interval threshold — total finish=20).

**Artifacts:**

```text
out/canaries/m5a-b3-trace-dmac/
  burnout-3-diagnose-20260804-110636-out.txt
  burnout-3-diagnose-20260804-110636-err.txt   # [DMAC-TRACE] end summary
```

Wall ~4.2 s, exit 0.

---

## 2. Run floor (blocker-trace claim)

| Field | Value |
|-------|-------|
| serial | `SLUS_210.50` |
| PC @20M | `0x00123E84` |
| px / prims | 877187 / 172 |
| gifP2 / gifP3 | 12 / 20 |
| dmac transfers | 20 |
| cdvd / syscalls / sifBytes | 425 / 806 / 22780 |
| gifCompleted / aborted | 92 / 6 |
| imgBytes | 65728 |
| RealSifRpc | binds=11 calls=42 liveRpcHits=0 |
| gif-path | m3p=True heldP3n=5 heldP3qwc=2124 mskPath3=10 |

Matches prior M8-a B3 diagnose floor (scoreboard identity class) — TRACE print-only did not change trajectory.

---

## 3. End-of-run `[DMAC-TRACE]` summary (verbatim)

```text
[DMAC-TRACE] end total finish=20 raise=19 transfersCompleted=20 active=0
[DMAC-TRACE] end ch=VIF1(1) finish=4 owedInc=3 owedPeak=3 preEnableInc=1 preEnablePromote=1 creditAssist=3 w1cWhileOwed=0 tryTakeCis=5 tryTakeOwed=0 raise=8 owedNow=2 preNow=0
[DMAC-TRACE] end ch=GIF(2) finish=8 owedInc=6 owedPeak=8 preEnableInc=2 preEnablePromote=2 creditAssist=3 w1cWhileOwed=0 tryTakeCis=4 tryTakeOwed=0 raise=11 owedNow=7 preNow=0
[DMAC-TRACE] end ch=SPR_FROM(8) finish=8 owedInc=0 owedPeak=0 preEnableInc=8 preEnablePromote=0 creditAssist=0 w1cWhileOwed=0 tryTakeCis=0 tryTakeOwed=0 raise=0 owedNow=0 preNow=8
```

Ring (newest last; last 32 events only — full seq goes higher):

```text
[DMAC-TRACE] end ring (newest last, reason 0=finish 1=credit 2=enable 3=take):
  … enable VIF1/GIF early; interleave SPR_FROM finish; VIF1/GIF finish+take+credit …
  seq=26 ch=VIF1(1) credit
  seq=27 ch=GIF(2) credit
  seq=28–35 ch=GIF finish/take pairs (late window)
```

Full ring is in the err log; pattern is **enable → finish → take**, with **assist credit** pulses (seq 13–14, 26–27) matching `Burnout3Assist` flip re-arm.

---

## 4. Counter summary (interesting channels)

| ch | finish | owedInc | owedPeak | preEnableInc | preEnablePromote | creditAssist | w1cWhileOwed | tryTakeCis | tryTakeOwed | raise | owedNow | preNow |
|----|-------:|--------:|---------:|-------------:|-----------------:|------------:|-------------:|-----------:|------------:|------:|--------:|-------:|
| **VIF1 (1)** | 4 | 3 | **3** | 1 | 1 | **3** | **0** | **5** | **0** | 8 | **2** | 0 |
| **GIF (2)** | 8 | 6 | **8** | 2 | 2 | **3** | **0** | **4** | **0** | 11 | **7** | 0 |
| **SPR_FROM (8)** | 8 | 0 | 0 | 8 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | **8** |
| **totals** | 20 | — | — | — | — | — | — | — | — | 19 | — | — |

Other channels (0, 3–7, 9): silent (all zeros; omitted by dump).

### 4.1 Finish vs take vs owed peaks

| Channel | finish | tryTake (CIS+owed) | take/finish | owedPeak | owedNow (end) |
|---------|-------:|-------------------:|------------:|---------:|--------------:|
| VIF1 | 4 | 5 | **1.25** (takes ≥ finishes; assist credits) | 3 | 2 |
| GIF | 8 | 4 | **0.50** (under-take) | **8** | **7** |
| SPR_FROM | 8 | 0 | 0 | 0 | 0 (preNow=8) |

**GIF under-delivery is the primary B3 signal at 20M:** half of finishes never produce a handler take; owed queue peaks at 8 and still holds 7 at end-of-run.

**VIF1** is closer to balanced on takes (even slightly over due to `creditAssist`), but still leaves `owedNow=2`.

### 4.2 Pre-enable path

| Channel | preEnableInc | preEnablePromote | note |
|---------|-------------:|-----------------:|------|
| VIF1 | 1 | 1 | full promote (cap 4 not binding) |
| GIF | 2 | 2 | full promote |
| SPR_FROM | 8 | **0** | finishes while CIM off; **no handler / no promote** → `preNow=8` residual |

Pre-enable **cap (4) is not the bottleneck** on VIF1/GIF at this budget. SPR_FROM is expected noise if no `AddDmacHandler(SPR_FROM)` is registered.

### 4.3 Credit assist (GameQuirks residual)

`creditAssist=3` on **both** VIF1 and GIF → `Burnout3Assist` flip re-arm (`CreditOwedHandlerCall`) fired in this window. Ring shows paired credit events (VIF1+GIF). Assists are **live residual** at diagnose; Core TRACE alone cannot claim A1 silence.

---

## 5. Signals: lost CIS vs owed backlog

| Hypothesis (design Q1) | Evidence @20M B3 | Read |
|------------------------|------------------|------|
| **Lost CIS before dispatch** (D_STAT W1C race) | `w1cWhileOwed=0` on VIF1 **and** GIF | **No support** at this budget — W1C-while-owed never fired |
| **Handler not scheduled often enough** / catch-up | GIF: finish=8 → tryTakeCis=4, **owedPeak=8, owedNow=7**; `tryTakeOwed=0` (CIS-path only); raise=11 but still backlog | **Strong support** — completions queue owed faster than EE takes; level re-arm may under-deliver |
| **Pre-enable promote cap (4) under-count** | preEnableInc ≤ 2 on VIF1/GIF; promote == inc | **No support** — cap not hit |
| **Assist inventing owed** | creditAssist=3 each on VIF1/GIF | Confirmed residual paper; confounds pure Core under-delivery measurement (credits **add** to owed, not only replace missing takes) |

**Owed backlog (real):** GIF ends with **owedNow=7** after only 4 CIS takes; peak depth 8. VIF1 **owedNow=2**.  
**Lost CIS (not seen):** zero W1C-while-owed; all takes recorded as `tryTakeCis` (owed-only fallback never used).

Caveat: diagnose 20M is **pre** LGDEV residual force window (~22M) and far below claim/flip soak. Signal is directional for Q1, not a MENU or A1 proof.

---

## 6. Q1–Q5 status after this sample

| ID | Question (short) | Status after B3 TRACE |
|----|------------------|------------------------|
| **Q1** | B3 wedge: lost CIS vs cadence vs pre-enable cap? | **Narrowed, not closed.** Data favor **handler cadence / owed backlog** over lost CIS (w1c=0) and over pre-enable cap (not binding). Still need: assist-credit A/B (env-off if available), longer budget, and/or catch-up experiment before S6 class is locked. |
| **Q2** | VIF1 software busy = game RAM only (Haven)? | **Still open.** This seat is B3 flip/IRQ, not Haven busy oracle. No Core busy-mirror evidence requested here. |
| **Q3** | GoW `0x13F5xx` DMAC vs EE thrash? | **Still open.** No GoW TRACE run in this seat. |
| **Q4** | Keep caps 8/64/4; save-state owed? | **Partially informed.** Cap-4 pre-enable not binding @20M B3. GIF **owedPeak=8** touches common depth-8 territory — do **not** raise caps without A/B. Save-state of owed: still **out of v1** (no new evidence demanding it). |
| **Q5** | Play!/PCSX2 oracle snapshot required before S6? | **Still open / optional preference.** TRACE narrowed Q1 without external oracle; a single flip IRQ snapshot (D_STAT, CHCR.nTAG, INTC, handler PC, pending) remains useful before any S6 behavior PR but is **not** required to continue S3 measurement. |

### Still open (explicit)

1. **Q1 final root class** under assist-quiet (creditAssist forced 0) — does GIF tryTake catch finish without invent credits?  
2. **Q2 Haven** diagnose TRACE_DMAC + busy flag correlation.  
3. **Q3 GoW** TRACE during sticky `0x13F5xx` (finish/owed vs force-finish).  
4. **Q4** formal ACK: keep defaults; no save-state in v1.  
5. **Q5** ACK: oracle optional vs required for S6.  
6. **Q6–Q7** (assist env-off ownership; default-on vs opt-in catch-up) unchanged — no new data.

---

## 7. Findings (5 bullets)

1. **GIF is under-taken at 20M:** finish=8 vs tryTakeCis=4; **owedPeak=8 / owedNow=7** — clear owed backlog, not a silent “no DMA” story.  
2. **Lost-CIS race not observed:** `w1cWhileOwed=0`; all takes are CIS-path (`tryTakeOwed=0`).  
3. **Pre-enable cap is not the B3 bottleneck here:** VIF1/GIF preEnable fully promoted (1/1, 2/2); SPR_FROM preNow=8 is unhandled-channel noise.  
4. **Assist residual is live:** `creditAssist=3` on VIF1 and GIF — flip re-arm still papering completion; confounds pure Core under-delivery counts.  
5. **Q1 leans cadence/backlog; Q2–Q5 still need their oracles** (Haven, GoW, cap ACK, optional Play! snapshot) before any S6 Core behavior PR.

---

## 8. Sign-off

```text
M5-a S1 B3 TRACE_DMAC sample @diagnose(20M) tip f19144e
  media: burnout-only.json ISO present
  [DMAC-TRACE] end: finish=20 raise=19
  VIF1: finish=4 takeCis=5 owedPeak=3 owedNow=2 creditAssist=3 w1c=0
  GIF:  finish=8 takeCis=4 owedPeak=8 owedNow=7 creditAssist=3 w1c=0
  SPR_FROM: finish=8 preNow=8 (no promote/take)
  Q1: favors handler cadence / owed backlog; not W1C-lost-CIS; not pre-enable cap
  Q2–Q5: still open (this seat B3-only)
  No Core changes. No push.
```

---

*Measurement sample for M5-a S3 (B3 oracle). Supersedes nothing in design/seed; feeds Q1 evidence only.*
