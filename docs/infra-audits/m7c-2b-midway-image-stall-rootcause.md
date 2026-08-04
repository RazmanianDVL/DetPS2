# M7-c-2b root-cause — Midway "5888/6144 IMAGE stall" is a telemetry artifact, not a real bug

**Date:** 2026-08-04
**Mode:** investigation only — **no Core changes landed** (throwaway trace edits added, verified, and reverted; `git status` clean).
**Tracks:** follow-up to `docs/infra-audits/m7c-gif-bisect-4title.md`'s "Plateau + IMAGE partial" bucket (MK: Deadly Alliance, MK: Deception).

---

## 0. One-line summary

**There is no stall.** MK: Deadly Alliance's Path3 IMAGE transfer (`nloop=6144`) completes successfully — `_pktLoop` reaches `6144`, `_pktActive` goes `false`. The `lastStallReason=image-partial progress=5888/6144` field that the bisect summary reported is **stale telemetry**: it's the second-to-last progress checkpoint (23 of 24 budgeted 256-QW chunks), never overwritten or cleared once the 24th chunk actually finishes the packet one tick later. `path3ImageCompleted=1` in the same bisect run already said this correctly — the "stall" label was a misleading read of a counter that just means "not-yet-finished-as-of-this-particular-call," which is true and expected for 23 of 24 chunks of any large, correctly-budgeted multi-tick transfer, successful or not.

**M7-c-2b is not a promising, fixable `Gif.cs` drain bug.** The real remaining gap for MK: Deadly Alliance's residual chrome (`compositeSource=LastImageTrx`, `naturalDispfbPx=0`, `residualDispfbPx=46080` in the same run) is downstream of IMAGE delivery — a **Slice 3** (DISPFB/composite selection) question, not Slice 2 (IMAGE delivery), or something else entirely. Reclassifying, not fixing, is this doc's contribution.

---

## 1. What I found

### 1.1 Reproduced the exact scenario

Rebuilt from the tip Grok's bisect ran at, added three throwaway trace lines (`DETPS2_TRACE_GIFCHAIN=1`, all reverted after):
- `Dmac.cs` `DeliverSegment`'s GIF case — log each `_gif.ReceivePath3Data(madr, qwc)` call.
- `Gif.cs` `ProcessTransferBudgeted` — log entry and exit state (`_pktLoop`/`_pktNloop`/`_pktActive`/pending-residual fields) around every call.

Ran `user-media-da.json` at claim tier (100M cycles, `--host-present`), same as the original bisect. Confirmed **byte-identical** scoreboard to the original bisect result before trusting the trace: `imgBytes=98304`, `gifCompleted=9`, `gifP3=6` — same deterministic run, not a different scenario.

### 1.2 The Path3 chain, kick by kick

Six `DeliverSegment` → `ReceivePath3Data` calls total for the whole 100M run (matches `path3Kicks=6`):

| # | madr | qwc | Note |
|---|------|-----|------|
| 1 | `0x00588DE0` | 13 | unrelated small Path3 (PACKED, completes in 1 call) |
| 2 | `0x00588C00` | 15 | unrelated small Path3 |
| 3 | `0x01FFFEF0` | 6 | starts a new packet, `_pktNloop=6144` set here (tag header parsed) |
| 4 | `0x00589320` | **6144** | **the IMAGE payload — one single DMA delivery of the full nloop, not split across multiple DMA segments** |
| 5 | `0x00538B40` | 13 | unrelated, after the IMAGE packet already completed |
| 6 | `0x005383C0` | 17 | unrelated |

Kick #4 is the one Slice 2a's bisect flagged as "stalled." It is delivered to `Gif.cs` as **one call with the entire 6144-QW payload already in memory** — this is not a DMAC chain-tag-splitting issue; the DMAC side already did its job (all real data was in place before the call). Everything after this point is purely `Gif.cs`'s own M1-b budgeted-continuation mechanism.

### 1.3 The M1-b continuation, chunk by chunk

`Gif.cs`'s `MaxQwPerReceiveCall = 256` caps the first synchronous chunk, then `Step()`'s pending-residual resume (`_pendingBudgetPath`/`_pendingBudgetAddr`/`_pendingBudgetQwc`) re-enters `ReceivePath3Data` on each subsequent Step() tick to drain 256 more, exactly as designed. Trace of every `ProcessTransferBudgeted` call for this one packet (24 total):

```
chunk 1:  pktLoop 0    -> 256   (initial unmasked call, isCont=False)
chunk 2:  pktLoop 256  -> 512   (isCont=True, continuation)
chunk 3:  pktLoop 512  -> 768
...
chunk 23: pktLoop 5632 -> 5888  <- this is the exact checkpoint _lastImageStallReason froze on
chunk 24: pktLoop 5888 -> 6144  pktActive=False   <-- PACKET COMPLETES HERE
```

All 24 chunks run to completion, one per Step() tick, addresses advancing correctly (`0x00589320`, `0x0058A320`, `0x0058B320`, … in exact 256-QW/4096-byte strides), each carrying the correctly-shrinking residual QWC (`5888, 5632, 5376, …, 256, 0`). **Chunk 24 is the last one and it finishes the packet.** No hang, no dropped data, no clobbered state, no interaction with Path3 masking (checked directly — the `Path3Masked`-hold branch never fires for this packet at all; `mskPath3` observed nonzero in the bisect summary refers to state at a *different* point in the run, not a block on this specific packet's drain).

### 1.4 Why the bisect telemetry said "stalled" anyway

`Gif.cs`'s `_path3ImageStalled` counter and `_lastImageStallReason` string are updated **every time a call to the drain function ends with `_pktActive` still true** — which is true and correct for chunks 1–23 of any 24-chunk transfer, successful or not. There is no code path that clears or re-labels `_lastImageStallReason` once the packet subsequently *does* complete on the next call — the string is simply the last thing written to that field, and for this run that happened to be the 23rd-of-24 checkpoint. `path3ImageCompleted=1` (also present in the same bisect summary row) was the accurate signal all along; `lastStallReason`'s wording ("stall") is what caused the misread, not the completion counter itself being wrong.

**This is a genuine, if minor, telemetry-labeling issue** (not a functional bug) — worth a one-line fix later (e.g. clear `_lastImageStallReason` on `NotePacketCompleted` for the IMAGE case, or rename the field/counter to something like `_path3ImagePartialSamples` that doesn't imply failure) so a future bisect pass doesn't get misread the same way. **Not fixing it in this seat** — this is investigate-first, matching the S0-TRACE seat Grok is running in parallel; a naming/clearing fix is trivial enough that dual-ACK may want to just take it, but I'm reporting rather than landing per this session's default posture for anything touching `Gif.cs`.

---

## 2. What this means for M7-c Slice 2b

The `m7c-gif-bisect-4title.md` bucket split needs a correction:

| Bucket (as reported) | Titles | Correction |
|---|---|---|
| Plateau + IMAGE partial stall | MK:DA, MK:Dec | **MK:DA's IMAGE transfer actually completes** (verified this doc). The "stall" was `_lastImageStallReason` staleness, not a real drain failure. MK:Dec was not independently re-verified in this pass (same `image-partial progress=5888/6144` signature, same `nloop=6144`, same shared Midway assist class — very likely the identical telemetry artifact, but **not directly confirmed** here; flagging as a needed follow-up, not assuming).

**Given the IMAGE data itself is delivered successfully, MK:DA's residual chrome (`compositeSource=LastImageTrx`, `naturalDispfbPx=0`) is NOT an M7-a Slice 2 (Path2/3 IMAGE delivery) problem.** The gap is downstream — either a Slice 3 (DISPFB/composite preference) issue, or something else entirely (e.g. the delivered IMAGE data landing in the wrong GS buffer/page, a timing issue between IMAGE completion and the DISPFB write, or genuinely correct-but-insufficient data). This needs its own investigation, not a `Gif.cs` drain fix — there is nothing left to fix in the drain path for this specific packet.

**Recommendation:** do not scope a `Gif.cs` behavior change for the "Midway plateau" bucket based on the original bisect's stall reading. If dual-ACK wants to pursue MK:DA/MK:Dec further, the next seat should be a Slice 3 (DISPFB/composite) investigation on MK:DA specifically, or a one-line telemetry-clarity fix for `_lastImageStallReason` (trivial, could plausibly be dual-ACK'd as "trivial" per this session's `M4-S4-MIRROR`-style design docs' own escape hatch for small landings) — not a re-attempt at "fixing" the drain, which was never broken.

---

## 3. Non-claims / what I did not check

- Did not independently re-verify MK: Deception's packet also completes (same signature, same assist family, high confidence it's the identical artifact, but not directly traced in this pass).
- Did not investigate why `compositeSource=LastImageTrx` / `naturalDispfbPx=0` despite the IMAGE data landing correctly — that's the real open question now, out of scope for this doc.
- Did not check GoW or BO2 (the "zero Path3" bucket) — unaffected by this finding, still believed to be an upstream EE/DMA-submission gap per the original bisect.
- The Path3-masking hypothesis I initially suspected (before tracing) was directly checked and ruled out — worth recording so it isn't re-investigated: `Path3Masked`-hold never fires for this packet's continuation calls.

---

## 4. Repro

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/gifchain-trace-build --nologo -v q
# (temporary trace lines were added to Dmac.cs DeliverSegment's GIF case and
#  Gif.cs ProcessTransferBudgeted for this investigation, then reverted — not in tree)
$env:DETPS2_TRACE_GIFCHAIN = "1"
dotnet exec out/gifchain-trace-build/DetPS2.Core.dll scoreboard-metrics user-media-da.json `
  --cycles=100000000 --out=out/da-gifchain-metrics.json --host-present
```

---

## 5. References

- `docs/infra-audits/m7c-gif-bisect-4title.md` — original bisect, the finding this doc corrects.
- `docs/infra-audits/m7c-path23-image-delivery-design.md` — parent Slice 2/2a/2b design.
- `src/DetPS2.Core/Gif.cs` — `MaxQwPerReceiveCall`, `ProcessTransferBudgeted`, `_lastImageStallReason` (M1-b / M7-c Slice 2a telemetry).
- `src/DetPS2.Core/Dmac.cs` — `DeliverSegment`, `DoNormalTransfer` (confirmed not implicated — DMAC already delivers the full segment before `Gif.cs` sees it).
