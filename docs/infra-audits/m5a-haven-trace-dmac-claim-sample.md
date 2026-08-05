# M5-a S1 claim sample — Haven `DETPS2_TRACE_DMAC=1` (claim 100M)

**Date:** 2026-08-04  
**Tip:** `64184b7`  
**Budget:** **claim (100M)** via `blocker-trace` + `--host-present`  
**Env:** `DETPS2_TRACE_DMAC=1` (print only; zero DMA behavior change)  
**Fleet id:** `haven` (`tools/scoreboard-fleet.json`)  
**Serial:** `SLUS_205.17`  
**Media:** `user-media-haven.json` → `C:/Users/user/Downloads/HavenCalloftheKing(USA).iso` (**present**)  
**Assist class:** TeamIco / `TeamIcoAssist` (loaded by title path; not modified this seat)  
**Build:** Release → `out/scoreboard-build/DetPS2.Core.dll`  
**Design:** `docs/infra-audits/m5a-dmac-vif-gif-completion-design.md` §1.2 Haven oracle / §8 Q2  
**Diagnose peer (pre-DMA empty control):** `docs/infra-audits/m5a-haven-trace-dmac-sample.md`  
**B3 diagnose peer:** `docs/infra-audits/m5a-b3-trace-dmac-sample.md`  
**Scope:** measurement only. **No Core code changes. No GameQuirks / TeamIcoAssist / GodOfWarAssist edits. No push.**

---

## 0. Budget choice

| Budget | Used? | Why |
|--------|------:|-----|
| **claim 100M** | **Yes (primary)** | Prior diagnose 20M was pre-DMA empty; title-port honesty says Soft-GS residual chrome + first commercial DMA is **claim-class ≥100M** (`docs/title-ports/HAVEN.md`). Wall **~13.1 s** — feasible. |
| verify 50M | Not needed | 100M completed cleanly; no fallback required. |

---

## 1. Command (repro)

```powershell
# Repo root; tip 64184b7; Release Core at out/scoreboard-build
$env:DETPS2_TRACE_DMAC = "1"
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
Remove-Item Env:DETPS2_DISABLE_JRGUARD64 -ErrorAction SilentlyContinue
$dll = "out/scoreboard-build/DetPS2.Core.dll"
dotnet exec $dll blocker-trace user-media-haven.json --cycles=100000000 --host-present `
  2> out/canaries/m5a-haven-trace-dmac-claim/err.txt
# End summary is stderr: lines prefixed [DMAC-TRACE] end
Remove-Item Env:DETPS2_TRACE_DMAC -ErrorAction SilentlyContinue
```

**Why blocker-trace:** S1 end dump (`DumpTraceSummary` prefix `[DMAC-TRACE] end`) is wired on the `blocker-trace` path. Per-channel lines appear only when at least one counter is non-zero.

**Artifacts:**

```text
out/canaries/m5a-haven-trace-dmac-claim/
  haven-claim-20260804-122623-out.txt
  haven-claim-20260804-122623-err.txt   # [DMAC-TRACE] end summary + ring
  last-stamp.txt
  status.txt                            # EXIT=0 wallSec=13.1
```

Wall **~13.1 s**, exit **0**.

---

## 2. Run floor (blocker-trace claim @100M)

| Field | Value |
|-------|-------|
| serial | `SLUS_205.17` |
| entry | `0x01000008` (high-VA ELF) |
| PC @100M | **`0x00331CC8`** (past CRT0; game .text) |
| px / prims | **329852 / 2** |
| gifP1 / gifP2 / gifP3 | 0 / **66** / **68** |
| dmac transfers | **202** |
| cdvd / syscalls / sifBytes | **6400 / 6751 / 459224** |
| gifCompleted / aborted | **200 / 0** |
| imgBytes | **194560** |
| RealSifRpc | binds=**13** calls=**125** liveRpcHits=0 |
| gif-path | p2=66 p3=68 m3p=False mskPath3=1 heldP3n=0 |
| softgs | compositeSource=**LastImageTrx**; residualDispfbPx=**43132**; naturalDispfbPx=0 |
| softgs-present | lit=**43132**/286720 mostlyBlack=**0** |
| IOP | pc=`0x0000C0F4` |

Matches Haven title-port claim identity class (px≈329852 lit≈43132 imgBytes=194560 Soft-GS residual Host→Local chrome). TRACE print-only did not change trajectory vs residual seat.

**Phase vs diagnose 20M:** diagnose was CRT0 pre-DMA (`PC=0x010003F0`, finish=0). Claim is **post-decompress commercial DMA live** — first Haven TRACE seat where finish/take/owed/creditAssist can speak.

---

## 3. End-of-run `[DMAC-TRACE]` summary (verbatim)

```text
[DMAC-TRACE] end total finish=202 raise=204 transfersCompleted=202 active=0
[DMAC-TRACE] end ch=VIF1(1) finish=134 owedInc=130 owedPeak=64 preEnableInc=0 preEnablePromote=0 creditAssist=1 w1cWhileOwed=1 tryTakeCis=67 tryTakeOwed=0 raise=136 owedNow=63 preNow=0
[DMAC-TRACE] end ch=GIF(2) finish=68 owedInc=68 owedPeak=1 preEnableInc=0 preEnablePromote=0 creditAssist=0 w1cWhileOwed=0 tryTakeCis=68 tryTakeOwed=0 raise=68 owedNow=0 preNow=0
[DMAC-TRACE] end ring (newest last, reason 0=finish 1=credit 2=enable 3=take):
[DMAC-TRACE] end   seq=307 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=308 ch=VIF1(1) take
[DMAC-TRACE] end   seq=309 ch=GIF(2) finish
[DMAC-TRACE] end   seq=310 ch=GIF(2) take
[DMAC-TRACE] end   seq=311 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=312 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=313 ch=VIF1(1) take
[DMAC-TRACE] end   seq=314 ch=GIF(2) finish
[DMAC-TRACE] end   seq=315 ch=GIF(2) take
[DMAC-TRACE] end   seq=316 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=317 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=318 ch=VIF1(1) take
[DMAC-TRACE] end   seq=319 ch=GIF(2) finish
[DMAC-TRACE] end   seq=320 ch=GIF(2) take
[DMAC-TRACE] end   seq=321 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=322 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=323 ch=VIF1(1) take
[DMAC-TRACE] end   seq=324 ch=GIF(2) finish
[DMAC-TRACE] end   seq=325 ch=GIF(2) take
[DMAC-TRACE] end   seq=326 ch=GIF(2) finish
[DMAC-TRACE] end   seq=327 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=328 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=329 ch=VIF1(1) credit
[DMAC-TRACE] end   seq=330 ch=GIF(2) take
[DMAC-TRACE] end   seq=331 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=332 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=333 ch=VIF1(1) take
[DMAC-TRACE] end   seq=334 ch=GIF(2) finish
[DMAC-TRACE] end   seq=335 ch=GIF(2) take
[DMAC-TRACE] end   seq=336 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=337 ch=VIF1(1) finish
[DMAC-TRACE] end   seq=338 ch=VIF1(1) take
```

Ring pattern (late window): **paired VIF1 finish×2 → one take**, interleaved GIF finish→take 1:1; single **VIF1 credit** pulse near end (seq=329) matching `TeamIcoAssist` `CreditOwedHandlerCall(VIF1)`.

Other channels (0, 3–7, 9, SPR_*): silent (all zeros; omitted by dump).

---

## 4. Counter summary (live channels)

| ch | finish | owedInc | owedPeak | preEnableInc | preEnablePromote | creditAssist | w1cWhileOwed | tryTakeCis | tryTakeOwed | raise | owedNow | preNow |
|----|-------:|--------:|---------:|-------------:|-----------------:|------------:|-------------:|-----------:|------------:|------:|--------:|-------:|
| **VIF1 (1)** | **134** | **130** | **64** | 0 | 0 | **1** | **1** | **67** | 0 | 136 | **63** | 0 |
| **GIF (2)** | **68** | 68 | **1** | 0 | 0 | **0** | 0 | **68** | 0 | 68 | **0** | 0 |
| **totals** | **202** | — | — | — | — | — | — | — | — | **204** | — | — |

### 4.1 Finish vs take vs owed

| Channel | finish | tryTake (CIS+owed) | take/finish | owedPeak | owedNow (end) |
|---------|-------:|-------------------:|------------:|---------:|--------------:|
| **VIF1** | 134 | 67 | **0.50** (under-take) | **64** (depth cap) | **63** |
| **GIF** | 68 | 68 | **1.00** (balanced) | 1 | **0** |

**Primary Haven claim signal — VIF1 under-delivery:** half of finishes never produce a handler take; owed queue hits **peak 64** (common depth-64 cap territory) and still holds **63** at end-of-run. `tryTakeOwed=0` (all takes recorded as CIS path). Ring shows finish doublets with single take — cadence lag, not pure silence.

**GIF is healthy at claim:** finish==take, owed drained (`owedNow=0`), peak only 1. Opposite of B3 diagnose (where GIF under-took).

### 4.2 Pre-enable path

| Channel | preEnableInc | preEnablePromote | note |
|---------|-------------:|-----------------:|------|
| VIF1 | 0 | 0 | no pre-enable residual |
| GIF | 0 | 0 | no pre-enable residual |

Pre-enable **cap (4) is not in play** at this Haven claim window. All activity is post-enable finish/take/credit.

### 4.3 Credit assist (GameQuirks residual)

| Signal | Haven @100M claim |
|--------|-------------------|
| **creditAssist VIF1** | **1** |
| **creditAssist GIF** | **0** |
| ring credit events | 1× VIF1 (seq=329 late window) |

`TeamIcoAssist` VIF busy clear + `CreditOwedHandlerCall(VIF1, 1)` residual is **live but sparse** at claim (creditAssist=1), not the dominant driver of the owed backlog (owedPeak=64 vs only one invent credit). Assists remain paper residual; Core TRACE alone still cannot claim A1 silence — but Q2 oracle window **is open** (DMA + handler path exercised).

### 4.4 W1C-while-owed

| Channel | w1cWhileOwed |
|---------|-------------:|
| VIF1 | **1** |
| GIF | 0 |

Single VIF1 W1C-while-owed event — **weak** lost-CIS signal (not zero like B3 diagnose, but not a storm). Does **not** explain 67 missing takes / owedNow=63 by itself.

---

## 5. Compare: diagnose empty control → claim live DMA

| Axis | Haven **diagnose 20M** | Haven **claim 100M** (this seat) |
|------|------------------------|----------------------------------|
| PC | `0x010003F0` CRT0 | **`0x00331CC8`** game .text |
| total finish / raise | **0 / 0** | **202 / 204** |
| VIF1 finish / takeCis / owedPeak / owedNow / creditAssist | silent | **134 / 67 / 64 / 63 / 1** |
| GIF finish / takeCis / owedPeak / owedNow / creditAssist | silent | **68 / 68 / 1 / 0 / 0** |
| w1cWhileOwed | 0 | **1** (VIF1 only) |
| px / gifP3 / dmac | 0 / 0 / 0 | **329852 / 68 / 202** |
| softgs lit / composite | 0 / None | **43132 / LastImageTrx** |
| Q2 readiness | **No data** (pre-DMA) | **Open with positive counters** |

### vs B3 diagnose (different title, different phase)

| Axis | B3 diagnose 20M | Haven claim 100M |
|------|-----------------|------------------|
| Under-take channel | **GIF** (finish=8 take=4 owedNow=7) | **VIF1** (finish=134 take=67 owedNow=63) |
| Balanced channel | VIF1 (~1.25 take/finish, assist-inflated) | **GIF** (1.00 take/finish) |
| creditAssist | 3 on VIF1 **and** GIF | **1 on VIF1 only** |
| owedPeak binding | GIF peak **8** | VIF1 peak **64** (cap-class) |

**Read:** same TRACE harness, opposite channel stress. Haven claim is the **positive Q2 seat** the diagnose sample predicted; do not treat B3 GIF under-take as the Haven VIF busy root class without this split.

---

## 6. Q1–Q5 status after this sample

| ID | Question (short) | Status after Haven TRACE @claim 100M |
|----|------------------|--------------------------------------|
| **Q1** | B3 wedge: lost CIS vs cadence vs pre-enable cap? | **Unchanged for B3.** This seat is Haven; B3 peer still owns Q1. Haven’s own under-take is **VIF1 cadence/owed**, not GIF, and pre-enable is idle. |
| **Q2** | VIF1 software busy = game RAM only (Haven)? | **Narrowed, not closed.** Claim window proves: VIF1 finishes **do** fire; handler takes **lag** (0.5 ratio); owed hits depth **64** with **owedNow=63**; assist credit is **sparse (1)** not the backlog source. Supports design: keep busy as **game RAM / handler path** — problem looks like **handler take cadence / IRQ re-arm**, not missing FinishChannel and not Core busy-mirror. Still need: busy/pending RAM correlator (measure-only) + optional assist-quiet A/B before ACK. |
| **Q3** | GoW `0x13F5xx` DMAC vs EE thrash? | **Still open.** No GoW TRACE in this seat. **Did not touch GodOfWarAssist.** |
| **Q4** | Keep caps 8/64/4; save-state owed? | **Informed for Haven.** VIF1 **owedPeak=64** **touches depth-64 cap** — do **not** raise caps without A/B; backlog is real. Pre-enable cap-4 not binding. Save-state of owed still **out of v1**. |
| **Q5** | Play!/PCSX2 oracle before S6? | **Still open / optional.** Haven claim TRACE alone narrows Q2 without external oracle; a busy-flag RAM snapshot at first VIF1 under-take window remains useful. |

### Still open (explicit)

1. **Q2 close path:** measure-only busy/pending RAM correlator around VIF1 finish without take (no GameQuirks growth); optional `TeamIco` busy-clear silence A/B if env hook exists.  
2. **VIF1 catch-up experiment class** (design S6) — only after A1 assist-quiet posture; this sample is evidence, not a behavior PR.  
3. Q1 (B3), Q3 (GoW), Q5 — unchanged ownership.

---

## 7. Findings (5 bullets)

1. **Haven @claim is DMA-live (not pre-DMA):** `[DMAC-TRACE] end total finish=202 raise=204 transfersCompleted=202 active=0` — VIF1 + GIF channels speak; diagnose empty control is superseded for Q2.  
2. **VIF1 under-take is the Haven claim signal:** finish=134 / tryTakeCis=67 (**0.50**), **owedPeak=64**, **owedNow=63**; GIF is balanced (68/68, owedNow=0).  
3. **creditAssist is live but sparse:** VIF1 **creditAssist=1** (TeamIco residual), GIF **0** — assist is **not** inventing the depth-64 backlog; ring shows one late credit.  
4. **w1cWhileOwed=1 (VIF1 only)** — weak lost-CIS blip; does not explain half the takes missing. Pre-enable path silent.  
5. **Floor identity holds:** PC=`0x00331CC8`, px=329852, lit=43132 LastImageTrx residual Soft-GS — TRACE print-only; no Core/GameQuirks edits; no push.

---

## 8. Sign-off

```text
M5-a S1 Haven TRACE_DMAC claim sample @claim(100M) tip 64184b7
  fleet id: haven  serial: SLUS_205.17  media: user-media-haven.json ISO present
  wall ~13.1s EXIT=0  (50M fallback not needed)
  [DMAC-TRACE] end: finish=202 raise=204 transfersCompleted=202 active=0
  VIF1: finish=134 takeCis=67 owedPeak=64 owedNow=63 creditAssist=1 w1c=1
  GIF:  finish=68  takeCis=68 owedPeak=1  owedNow=0  creditAssist=0 w1c=0
  floor: PC=0x00331CC8 px=329852 gifP3=68 dmac=202 lit=43132 LastImageTrx
  vs diagnose 20M: empty→live (Q2 oracle window open)
  Q2: narrowed — VIF1 cadence/owed under-take; busy stays game-RAM hypothesis
  Q1/Q3–Q5: B3/GoW ownership unchanged; no GoWAssist / TeamIco edits
  No Core changes. No GameQuirks growth. No push.
```

---

*Measurement sample for M5-a Haven claim-class TRACE (Q2 positive seat). Supersedes diagnose-only emptiness for Q2 readiness; does not supersede design/seed or B3 Q1 evidence.*
