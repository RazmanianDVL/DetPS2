# M5-a design — DMAC → VIF/GIF handler IRQ completion fidelity

**Status:** S1 telemetry **landed** (Phase 0, zero behavior change) — Q0 ACK; S6 still blocked on Q1–Q5  
**Date:** 2026-08-04  
**Tip ref:** `9f312bd` (windows-detps2 / detps2)  
**Mode:** read-only investigation → flag-gated PRs only after ACK.  
**Priority source:** `docs/infra-audits/gamequirks-infra-debt.md` § priority #3  
**Seed:** `docs/infra-audits/m5a-dmac-vif-gif-completion-seed.md`  
**Owned code (future):** `src/DetPS2.Core/Dmac.cs`, `SonyKernelHle.cs` (AddDmacHandler / EnableDmac), `EmotionEngine.cs` (DmaController dispatch), optional Intc edge hygiene  
**Related (not owned here):** VIF/GIF **payload** delivery is G-GFX / M7-a; this workstream owns **completion → CIS → IRQ → handler** only  
**Hard bans for implement turn:** no `GameQuirks/*` growth; no title-named branches in Core; no `RealSifRpc` edits; no invent GIF packets; no force out←in

---

## 1. Problem class + multi-title evidence

### 1.1 Problem class

Commercial titles **arm `AddDmacHandler(VIF1/GIF)`** and treat DMA completion as a **software event stream**, not a pure MMIO poll:

```text
DMAC FinishChannel
  → D_STAT CIS[ch] sticky
  → (if CIM[ch] live) INTC DmaController (src 14)
  → EE HLE walks AddDmacHandler table (a0 = channel)
  → game handler body clears pending / busy / drains flip or path-sync queue
```

When any link is incomplete or lost, titles observe **software state stuck after hardware STR already cleared**:

| Symptom class | What software sees | Who papers it today |
|---------------|--------------------|---------------------|
| Flip / path-sync park | pending-count never hits 0; out≠in never drains | B3 `CreditOwedHandlerCall` re-arm |
| VIF software busy stuck | CHCR.STR clear / channel idle, game busy flag still set | Haven flag poke + VIF1 credit |
| DMA tag builder sticky | QWC+END never finalized; main never reaches pad/worker posters | GoW force-finish END + IRQ credit |

**Class:** shared **DMAC completion → IRQ → AddDmacHandler fidelity** debt. Assists re-credit IRQs or clear game RAM because the **shared event stream under-delivers**, not because FPS is low.

### 1.2 Multi-title evidence (symptoms only — read-only)

#### Burnout 3 — flip pending via `CreditOwedHandlerCall`

**File:** `src/DetPS2.Core/GameQuirks/Burnout3Assist.cs`

| Item | Detail |
|------|--------|
| Consumer | `0x001F1778` — decrements **pending** only on IRQ (`a0` = VIF1/GIF); drains out→in only when pending hits 0 |
| Queue addrs | pending `0x004E2830`, out `0x004E2838`, in `0x004E283C` |
| Park | GS flip/watermark `0x001F24E0` / callback `0x00228040` |
| Assist action | `ArmFlipConsumer` → INTC mask bit 14 + `EnableChannelIrq(VIF1/GIF)` then `CreditOwedHandlerCall(VIF1/GIF, need)` with `need = min(pending+1, 6)` (or 1 when pending already 0 but out≠in) |
| Hard ban observed | **must not** force out←in (early-outs drain → infinite gifP3 with stuck FILEIO) |
| Audit tag | *“GS flip pending via CreditOwedHandlerCall (DMA IRQ timing)”* (`gamequirks-infra-debt.md`) |

Sketch (assist re-arm body):

```text
// Burnout3Assist.cs ~302–333
pending = mem[0x4E2830]; out/in = mem[0x4E2838/3C]
if !flipHealthy && !gifMoving && stable && ActiveChannelCount==0:
  if out!=in: CreditOwedHandlerCall(VIF1, need); CreditOwedHandlerCall(GIF, need)
  if out==in && pending>0: prefer credits first; only soft-clear pending after several fails
```

#### God of War — sticky GIF/VIF DMA tag builders + IRQ credit

**File:** `src/DetPS2.Core/GameQuirks/GodOfWarAssist.cs`

| Item | Detail |
|------|--------|
| Builder band | `0x13F540..0x13F6A8` writes QWC + END `0x70000000` (retail; not thrash) |
| Sticky park | mid-align-pad `0x13F5F8` / `0x13F670` ≥200k cycles; poison cursor `*0x32F168` |
| Assist action | Force-align cursor, write END tag, advance cursor, leave via `$ra` / post-FreezeCache; then `EnableChannelIrq(GIF)` + `CreditOwedHandlerCall(GIF/VIF1, 4)` |
| Stream/world kick | Additional `CreditOwedHandlerCall(GIF/VIF1)` + mask arm so queued work can drain — **no invented GIF packets** |
| Audit tag | *“sticky GIF DMA tags; heap escapes SECONDARY”* |

**Open classification:** tag-builder sticky may be **EE thrash / poison cursor** (SECONDARY) with IRQ credit as a side-effect, **or** missing END/completion delivery from DMAC. Design must bisect before promoting a shared DMAC fix (see §8 Q3).

#### Haven (Team Ico) — VIF1 software-busy while channel idle

**File:** `src/DetPS2.Core/GameQuirks/TeamIcoAssist.cs`

| Item | Detail |
|------|--------|
| Wait | `0x188AE0` (`bne v0,0` after jal `0x1883C8`) |
| Busy flag | `*(0x39C0C4)`; pending `*(0x39C0DC)` |
| Set path | VIF1 chain kick CHCR=`0x1C5`; **cleared by DMA completion path** (game handler) |
| Assist action | When STR clear / `!IsActive(VIF1)` but busy/pending set → clear flags + `EnableChannelIrq(VIF1)` + `CreditOwedHandlerCall(VIF1, 1)` |
| Audit tag | *“Haven VIF busy/IRQ”* |

Sketch:

```text
// TeamIcoAssist.MaybeClearHavenVifBusy ~353–375
if busy==0 && pending==0: return
if IsActive(VIF1) || (CHCR & STR): return   // hardware still live — do not poke
mem busy/pending = 0
EnableChannelIrq(VIF1); CreditOwedHandlerCall(VIF1, 1)
```

#### Adjacent (not primary oracles, same class)

| Title | Signal |
|-------|--------|
| **DA / Midway** | Display-chain END+IRQ; handlers check `CHCR & 0xF0000000 ∈ {0x8,0xF}` (REFE/END+IRQ nTAG). Core already latches nTAG in `DoChainTransfer` (`Dmac.cs` ~549–553). |
| **Blood Omen 2** | Also calls `CreditOwedHandlerCall(GIF/VIF1)` in places — same debt class, not a fourth independent design. |

### 1.3 Shared pattern

All three primary titles are **completion side-effect missing or lost** relative to hardware-ish expectations, papered by title-local re-credit or flag poke. **Success metric** for M5-a: quirks go **quiet under env-off**, not “assist deleted first.”

---

## 2. Current Core completion path (accurate file:line sketches)

All line numbers refer to tip layout under `src/DetPS2.Core/` as of design date. Treat as sketches if lines drift; symbol names are stable.

### 2.1 End-to-end flow

```text
CHCR.STR write / StartTransfer
  → Dmac.Step: DoNormalTransfer / DoChainTransfer → DeliverSegment (GIF/VIF payload)
  → FinishChannel(ch)
       Active=false; CHCR &= ~STR
       DStat |= CIS[ch]
       if IsChannelIrqEnabled(ch):
         _owedHandlerCalls[ch]++ (cap 64)
         RaiseDmacIrq()  // force INTC MASK bit 14 if dropped
       else:
         _preEnableCompletions[ch]++ (cap 64)
  → EnableDmac / EnableChannelIrq:
       promote pre-enable → owed (cap 4 per arm)
       level-sensitive: if CIS|owed → RaiseDmacIrq
  → EmotionEngine.TryDispatchRegisteredIntcHandler
       src==DmaController && TryTakePendingDmacHandler → handler PC, a0=channel
       viaDmacFallback: Acknowledge + ClearCpuLatch (anti-storm)
  → game AddDmacHandler body runs with a0 = ch
```

### 2.2 `Dmac.cs` — finish / credit / arm

| Symbol | Approx lines | Role |
|--------|--------------|------|
| `RaiseDmacIrq` | 67–79 | Force INTC MASK bit 14 if clear; `Intc.Raise(DmaController)`. B3 comment: EnableDmac then later SetMask/DisableIntc drops bit 14 while D_STAT CIM stays live → handlers never run. |
| `Step` → `FinishChannel` call sites | 198–255 | QWC drain / chain TADR=0 → finish |
| `DeliverSegment` | 262–285 | GIF Path3 / VIF `ProcessStream` on completed segment **before** finish on QWC==0 path |
| `_owedHandlerCalls` / `_preEnableCompletions` | 333–342 | Soft queues surviving D_STAT W1C / pre-mask completions |
| `CreditOwedHandlerCall` | 356–371 | Assist/public API: add ≤8 to owed (cap 64), sticky CIS, Raise if CIM live |
| `FinishChannel` | 373–399 | CIS + owed or pre-enable + Raise |
| `IsChannelIrqEnabled` | 401–408 | D_STAT bit 16+ch **or** `DMask` bit |
| `EnableChannelIrq` | 410–443 | EnableDmac body: set CIM, promote pre-enable (≤4), level-sensitive Raise |
| `ClearChannelStatus` | 454–460 | W1C CIS after dispatcher hands channel |
| `HasPendingChannelIrq` | 462–471 | Any CIS & CIM |
| `DoChainTransfer` nTAG latch | 512–628 | CHCR high half = tagLow>>16; REFE/END handling; CIS **not** at tag-fetch (comment ~625) |
| `WriteRegister` D_STAT / CHCR | 661–748 | D_STAT low W1C + high XOR mask; CHCR STR start + path3Hold/daDisplayVif force-pump (max 16 steps) |

**Caps today:**

| Cap | Value | Where |
|-----|-------|-------|
| Credit add per `CreditOwedHandlerCall` | ≤8 | `CreditOwedHandlerCall` |
| Owed queue depth | ≤64 | Finish / Credit / Enable promote |
| Pre-enable promote on Enable | ≤4 | `EnableChannelIrq` |
| CHCR force-pump steps | 16 (512 if `DETPS2_DISABLE_A3_CHCR_CAP=1`) | `WriteRegister` CHCR |

**Save-state gap:** `WriteState`/`ReadState` (~94–131) persist DStat/DMask/channels but **do not** serialize `_owedHandlerCalls` / `_preEnableCompletions`. Mid-IRQ save/load can drop owed credits (note for later; not v1 unless A2 savestate workstream cares).

### 2.3 `SonyKernelHle.cs` — registration + take

| Symbol | Approx lines | Role |
|--------|--------------|------|
| `_dmacHandlers` dict | ~87, 880–884 | `AddDmacHandler` syscall 0x12: channel → handler VA |
| `TryGetDmacHandler` | 539–543 | Lookup |
| `TryTakePendingDmacHandler` | 550–594 | Prefer CIS+CIM+registered; clear CIS + consume one owed; else owed-only fallback; re-Raise if more work |
| `EnableDmac` / `iEnableDmac` | 906–923 | `EnableChannelIrq` + force INTC mask bit 14 + `TakeExceptions=true` + Raise/Rearm |

### 2.4 `EmotionEngine.cs` — DmaController dispatch

| Symbol | Approx lines | Role |
|--------|--------------|------|
| `TryDispatchRegisteredIntcHandler` DmaController path | 1242–1267 | If no direct AddIntcHandler for src 14: `TryTakePendingDmacHandler` → `handlerArg = chNum`, `viaDmacFallback` ack |
| a0 install | 1335 | `SetGpr(4, handlerArg)` — channel for DMAC path, cause for INTC path |
| Min dispatch latency | ~1197–1199 | `Intc.MinDispatchLatencyCycles` (16) after fresh CpuLatched edge |

**B3-specific comment in EE (~1242–1248):** without this fallback, handler at `0x001F1778` never ran; only software `a0=-1` poll early-outs while pending≠0 → park at `0x001F24E0`.

### 2.5 `Intc.cs` — summary bit

| Item | Detail |
|------|--------|
| `InterruptSource.DmaController = 14` | Summary for **all** DMAC channels |
| STAT sticky vs `CpuLatched` edge | Documented in `Intc` header (~9–18); bare-eret must not storm |
| `Raise` / `Acknowledge` / `ClearCpuLatch` | viaDmacFallback acks to break empty-handler storms (same class as SIF0 DMAC fallback) |

### 2.6 VIF / GIF payload vs completion

| Unit | Role in M5-a |
|------|----------------|
| `Dmac.DeliverSegment` → `Gif.ReceivePath3Data` / `Vif.ProcessStream` | Payload fidelity (M7-a / G-GFX). **Not** the completion IRQ path. |
| `Vif` MSKPATH3 → `Gif.Path3MaskedByVif` | Gates CHCR force-pump (`path3Hold`) so B3 path-sync drains under M3P. Adjacent; keep behavior unless telemetry proves over/under-pump. |
| Game software busy flags | **Not** Core VIF STAT mirrors today. Haven busy is pure game RAM updated by the title’s handler (or assist poke). |

### 2.7 Existing smoke

`Tests/SmokeTests.cs` ~1245–1279 — `Dmac_EnableDmac_DispatchesAddDmacHandler`: EnableChannelIrq(GIF) → Start → Step → DmaController raised → `TryTakePendingDmacHandler` returns registered `0x001F1778` and clears CIS. **Happy-path only** — no W1C race, no pre-enable promote, no multi-channel owed flood, no EXL nesting.

---

## 3. Gap analysis vs hardware-ish expectation

### 3.1 Expected (Play! / ps2tek-ish)

```text
1. Transfer ends → STR clear, CIS[ch] set (level-sensitive with CIM)
2. If CIM[ch] & CIS[ch] → INTC DMAC summary pending
3. BIOS/ISR walks AddDmacHandler table; game ISR body runs with channel identity
4. Software W1C D_STAT CIS and/or relies on STR/CIS observation
5. Side-effects (pending--, busy clear, drain) happen **only** in game code after (3)
```

Real DMAC is **level-sensitive** on (CIS & CIM). Completions before EnableDmac should still fire when the mask is later armed. Racey software W1C before the ISR runs should not permanently drop a completion that already raised CIS (or the ISR must still see work).

### 3.2 What Core already does well

| Layer | Status |
|-------|--------|
| Finish → CIS | Implemented (`FinishChannel`) |
| Pre-enable catch-up | `_preEnableCompletions` promote on Enable (B3-motivated) |
| CIS → soft owed queue | `_owedHandlerCalls` survives W1C before take |
| INTC mask bit 14 force | `RaiseDmacIrq` (B3 SetMask drop) |
| HLE table dispatch | `TryTakePendingDmacHandler` + EE DmaController fallback |
| CHCR nTAG latch | DA REFE/END+IRQ checks |
| Anti-storm ack on DMAC fallback | `viaDmacFallback` Acknowledge |

### 3.3 Remaining gaps (hypotheses)

| Layer | Suspected gap | Why assists still fire |
|-------|---------------|------------------------|
| **Handler cadence** | Owed calls exist but EE does not drain often enough under EXL / multi-handler / MinDispatchLatency / other INTC sources monopolizing dispatch | B3 re-arms with multi-credit after quiet GIF |
| **Lost CIS before take** | Still possible if Raise + game W1C + no owed increment (e.g. Finish while CIM off and Enable never re-promotes enough) | B3 pre-enable cap 4 vs many path-sync completes |
| **INTC mask thrash** | Bit 14 forced on Raise, but DisableIntc / SetMask can still create windows where latch is cleared without take | B3 `ArmFlipConsumer` re-ORs mask every re-arm |
| **Handler identity / a0** | Multi-handler per channel? Dict is **single slot** per channel — last AddDmacHandler wins (same class as old Intc single-slot before multi-handler chain) | Unproven for VIF1/GIF; B3 appears single consumer |
| **VIF/GIF status mirror** | Games that clear busy **only in handler** never clear if handler never runs; Core does not mirror busy from CHCR | Haven poke busy then credit — chicken/egg |
| **Chain-end / tag builder** | Incomplete END write is EE-side, not FinishChannel | GoW force END is PC repair, not DMAC |
| **Owed caps** | Cap 8 credit / 64 queue / 4 pre-enable may under-credit bursts or over-fire after promote | Needs telemetry |
| **Save-state** | Owed/pre-enable not serialized | Rare mid-IRQ load drop |
| **Force-pump vs scheduler** | CHCR force-pump finishes transfers synchronously (good for path-sync) but IRQ may still land before handler is registered / IE armed | Timing races |

### 3.4 One-sentence hypothesis

Default-safe DMAC already queues some owed calls and dispatches AddDmacHandler for src 14, but **handler dispatch cadence, level-sensitive catch-up caps, and game-observed completion side-effects** still diverge from Play!/hw enough that multiple titles re-credit IRQs or clear busy in GameQuirks.

---

## 4. Proposed mechanism (flag-gated, kill-switch, default-safe)

### 4.1 Policy

| Principle | Detail |
|-----------|--------|
| **Default-safe** | Fleet roster green with flags **off** or with kill-switch restoring pre-change behavior |
| **No title branches** | No `if (serial == B3)`, no PC-band gates in Core |
| **No GameQuirks growth** | Assists stay as-is until env-off quiet; do not add more `CreditOwedHandlerCall` plants |
| **Prefer real path** | Finish → CIS → level-sensitive IRQ → durable owed → dispatcher drain → game handler |
| **Kill-switch** | Single env restores pre-M5-a behavior for instant A/B |

### 4.2 Phased mechanism (implement only after ACK)

#### Phase 0 — Telemetry only (behavior-identical)

| Piece | Spec |
|-------|------|
| Env | `DETPS2_TRACE_DMAC=1` (extend existing TRACE_* style; stderr) |
| Counters (per channel 0–9, or at least VIF0/VIF1/GIF) | `FinishChannel`, `owedInc`, `owedPeak`, `preEnableInc`, `preEnablePromote`, `creditAssist` (CreditOwedHandlerCall), `W1C` (D_STAT clear while owed>0 or before take), `TryTake` hits (CIS path vs owed path), `RaiseDmacIrq`, `dispatch` (optional hook from EE when viaDmacFallback DMAC) |
| Optional ring | last N (ch, reason: finish\|credit\|enable\|take, cyc) when TRACE on |
| Default | counters always accumulate if cheap; **print only** when TRACE set — no behavior change |
| Kill-switch | N/A (telemetry only) |

#### Phase 1 — Cadence / level-sensitive catch-up (flag-gated behavior)

Only if Phase 0 proves under-delivery (e.g. Finish ≫ TryTake, or CIS W1C with owed starved).

| Candidate | Default | Kill-switch / opt-in |
|-----------|---------|----------------------|
| **A. Re-raise DMAC when owed remains after eret** | Already partially present in `TryTakePendingDmacHandler` re-Raise | Strengthen only under `DETPS2_DMAC_LEVEL_CATCHUP=1` if evidence shows dropped latch |
| **B. Pre-enable promote cap** | Keep 4 default | `DETPS2_DMAC_PREENABLE_PROMOTE=N` (dev) — **do not** raise fleet-wide without B3 A/B |
| **C. Ambient owed drain nudge** | **Off** | Forbidden as default: no ambient invent credits. If needed: only re-Raise Intc when `_owedHandlerCalls[ch]>0 && CIM && IE` and no recent take — still **no** invent count |
| **D. Strict handlers** | **Off** | `DETPS2_DMAC_STRICT_HANDLERS=1` fail-fast when Finish with CIM and no registered handler (dev only) |

**Recommended v1 behavior change (after ACK):** start with **telemetry + any proven latch re-arm bugfix** only. Do **not** invent owed credits in Core (that is what assists do).

#### Phase 2 — Status observation (only if Haven oracle demands it)

| Candidate | Policy |
|-----------|--------|
| Mirror VIF “busy” into Core | **No** title RAM poke in Core. Busy is game software. Fix is **make the handler run**. |
| Document CHCR.STR / IsActive for pollers | Already readable via MMIO; no new API required for games |

#### Phase 3 — GoW reclassification

If telemetry shows Finish/IRQ sufficient during sticky builder park → **reclassify GoW force-finish as SECONDARY EE thrash** (out of M5-a). If Finish/IRQ missing causal → Phase 1 only; never Core-write END tags.

### 4.3 Flag / kill-switch table

| Env | Default | Role |
|-----|---------|------|
| `DETPS2_TRACE_DMAC=1` | **off** | Telemetry print |
| `DETPS2_DISABLE_M5A_DMAC=1` | unset = feature slices **on** only if merged as default-on | Hard kill for any Phase-1 behavior PR (mirror M6-b style) |
| `DETPS2_DMAC_LEVEL_CATCHUP=1` | **off** until proven | Optional stronger level re-arm |
| `DETPS2_DMAC_STRICT_HANDLERS=1` | **off** | Dev fail-fast |
| `DETPS2_DMAC_PREENABLE_PROMOTE=N` | unset = keep 4 | Dev bisect only |

**Assist env-off (validation only; not Core):** document expected future assist silences e.g. `DETPS2_NO_B3_FLIP_CREDIT` / Haven busy-clear silence — **only if those env hooks already exist or land as assist-side opt-out in a separate PR**. Design does **not** require adding them in M5-a Core PRs; measurement can use TRACE creditAssist counter **with assists still loaded** first (creditAssist>0 proves residual), then env-off when available.

### 4.4 Hard bans (invariants)

| Ban | Why |
|-----|-----|
| Title serial / PC-band / GameQuirks calls in Core | Debt policy; “no title-named branches” |
| Invent GIF packets / PATH3 plants to “complete” | M7-a / PRESENT residual |
| Force out←in / fake pending=0 as product path | B3 telemetry wedge |
| Core poke of game busy/pending RAM | Haven busy is game state; fix handler path |
| Grow `CreditOwedHandlerCall` plants in assists as “the fix” | This workstream retires need for them |
| Wholesale delete B3/GoW/Haven assists | Success = quiet under env-off, not delete-first |
| Touch RealSifRpc | Different workstream (infra-debt #2) |

---

## 5. Files to touch, non-goals

### 5.1 Files to touch (implement turn — after ACK)

| File | Change class |
|------|----------------|
| `src/DetPS2.Core/Dmac.cs` | Telemetry counters; optional kill-switched catch-up; **no** title logic |
| `src/DetPS2.Core/SonyKernelHle.cs` | Optional take-path counters / re-Raise hygiene; EnableDmac only if proven |
| `src/DetPS2.Core/EmotionEngine.cs` | Optional dispatch counter when viaDmacFallback DMAC; **no** a0 semantics change without smoke |
| `src/DetPS2.Core/Intc.cs` | Only if latch/ack bug proven for src 14 |
| `Tests/SmokeTests.cs` | Extend: pre-enable promote; W1C before take still drains owed; multi-channel re-Raise |
| `docs/infra-audits/m5a-dmac-vif-gif-completion-design.md` | Update status after land |
| `docs/TITLE_HACKS.md` or debt audit | One-line pointer when shared path stabilizes |

### 5.2 Explicitly out of scope

| Non-goal | Owner |
|----------|--------|
| Edit `GameQuirks/*` (grow or strip) | Later residual env-off PRs after A1–A3 |
| `RealSifRpc` / SIFCMD | infra-debt #2 |
| Host→Local / PATH IMAGE / DISPFB | M7-a |
| IOP DMACMAN / `IopDmacManHost` | `docs/bios-ports/DMACMAN.md` (IOP side) |
| GoW poison cursor / heap SECONDARY | Separate; only reclassify after bisect |
| MENU campaign claims / chrome budgets | Out of M5-a validation |
| Save-state owed serialization | Optional follow-up (a2-savestate) |
| Multi-handler linked list per DMAC channel | Only if telemetry shows multi-register drop |

### 5.3 Explicit charter lines

- **No GameQuirks growth** for flip/VIF/tag completion.  
- **No title-named branches** in Core (no B3/GoW/Haven identifiers).  
- Assists remain loaded for MENU/fleet until env-off quiet is proven.

---

## 6. Implementation order (small slices)

| Slice | Name | Behavior change? | Exit criteria |
|------:|------|------------------|---------------|
| **S0** | Design ACK | No | This doc locked; open questions answered (§8) |
| **S1** | Telemetry PR | **No** | Counters + `DETPS2_TRACE_DMAC`; smokes green; diagnose canaries no worse |
| **S2** | Smoke expansion | **No** | Pre-enable, W1C-before-take, dual-channel owed |
| **S3** | B3 oracle A/B | Measure only | TRACE: Finish vs TryTake vs creditAssist during flip park; document root (lost CIS vs cadence vs EXL) |
| **S4** | Haven oracle A/B | Measure only | STR idle + busy set ⇒ handler take count before/after; distinguish missing IRQ vs never-entered clear |
| **S5** | GoW bisect | Measure only | During sticky `0x13F5xx`: was GIF/VIF Finish+owed firing? If yes → SECONDARY write-up; if no → feed S6 |
| **S6** | Minimal Core fix | **Yes, kill-switched** | Only the gap proven in S3–S5 (e.g. latch re-arm, promote cap, dispatch bug). Default-safe. |
| **S7** | Env-off residual | Assist-side opt-out if needed | A1–A3 quiet under assist silence; **no** MENU campaign |
| **S8** | Doc roll-up | No | Debt audit + TITLE_HACKS pointer; seed marked superseded by this design |

**Do not** jump to S6 without S1–S5 evidence. **Do not** expand assist credits as S6.

---

## 7. Validation: smokes + diagnose canaries (not MENU campaign)

### 7.1 Unit smokes (required for any Core PR)

| Smoke | Assert |
|-------|--------|
| Existing `Dmac_EnableDmac_DispatchesAddDmacHandler` | Still green |
| **New** pre-enable | Finish while CIM off → EnableChannelIrq → CIS/owed → TryTake returns handler |
| **New** W1C race | Finish → Raise → software D_STAT W1C CIS → owed still TryTake-able |
| **New** multi-channel | VIF1 + GIF both owed → take one → re-Raise → take other |
| **New** credit API | `CreditOwedHandlerCall` increments owed + Raise when CIM; caps respected |

### 7.2 Diagnose canaries (20M default — **not** MENU campaign)

Use existing tooling; **do not** open with 100M claim / menu chrome work.

| Canary | Tool / media | Budget | Watch |
|--------|--------------|--------|-------|
| **B3** | `tools/canary-path3-b3.ps1` or `run-title.ps1 -Media burnout-only.json` / `user-media` B3 | **diagnose 20M** (verify 50M only if diagnose green) | flip pending/out/in trajectory; `creditAssist` / re-arm spam; gifP3 not wedged; no out←in; Soft-GS floor not worse |
| **GoW** | `run-title.ps1` + `user-media-god-of-war.json` (or godwar media map) | diagnose 20M | sticky builder parks; Finish/owed vs force-finish rate (assist log if TRACE_BIOS); no invented PATH3 |
| **Haven** | `run-title.ps1` + `user-media-haven.json` | diagnose 20M | VIF busy clear count / handler takes; no new WaitSema thrash; SoftFloat residual orthogonal |

Fleet A/B helper (optional infra regression): `tools/canary-c1-5-fleet-ab.ps1` at diagnose — **infrastructure A/B**, not MENU YES scoring.

### 7.3 Acceptance gates (carry from seed; refined)

| Gate | Criteria |
|------|----------|
| **A0** | Telemetry PR merges; flags default-safe; B3/GoW/Haven diagnose trajectory **no worse** than tip baseline |
| **A1** | B3: flip pending drains toward 0 with **assist flip re-credit silenced** (when opt-out exists) for documented diagnose budget; Soft-GS floor not worse; **no** force out←in |
| **A2** | Haven: VIF busy wait exits with **assist busy-clear silenced** while CHCR.STR idle; no new WaitSema thrash |
| **A3** | GoW: sticky DMA-tag force-finish silent under env-off **or** reclassified SECONDARY with written root cause ≠ DMAC IRQ |
| **A4** | No title-local `CreditOwedHandlerCall` added for new titles; no title-named Core branches; shared path documented |

**Success = quirks go quiet under env-off**, not assist deleted first. **Not** a MENU campaign seat.

### 7.4 A/B sketch

```text
# baseline
pwsh ./tools/run-title.ps1 -Media <b3|gow|haven media> -Budget diagnose

# telemetry
$env:DETPS2_TRACE_DMAC="1"
pwsh ./tools/run-title.ps1 -Media ... -Budget diagnose

# after Phase-1 feature lands
$env:DETPS2_DISABLE_M5A_DMAC="1"   # kill-switch A/B
pwsh ./tools/run-title.ps1 -Media ... -Budget diagnose
```

Compare: exit PC class, cdvd/FILEIO, gifP3, DMAC counters (Finish/TryTake/creditAssist), absence of CreateSema thrash (B3 history).

---

## 8. Open questions needing ACK before code

**Claude (or implement owner) must answer these before any Core behavior PR (S6). S1 telemetry can land with only Q0 ACK.**

| ID | Question | Why it blocks |
|----|----------|---------------|
| **Q0** | Approve S1 telemetry surface (`DETPS2_TRACE_DMAC` + per-channel counters listed in §4.2 Phase 0) as first PR with **zero** behavior change? | Unblocks measurement without risk |
| **Q1** | Is B3 pending wedge primarily **lost CIS before dispatch**, **handler not scheduled often enough** under EE IRQ nesting / EXL, or **pre-enable promote cap (4)** under-count? | Chooses S6 fix class (none invent credits) |
| **Q2** | Should VIF1 **software busy** remain purely game RAM (handler-only clear), with Core only guaranteeing IRQ delivery — **ACK: no Core busy mirror / no game-RAM poke**? | Confirms Haven strategy |
| **Q3** | Are GoW `0x13F5xx` parks caused by missing END delivery from DMAC, or pure EE state corruption (poison cursor / worker gaps, infra-debt #2/#4)? Promote to shared DMAC only if Finish/IRQ absence is causal. | Keeps SECONDARY out of M5-a |
| **Q4** | Cap policy: keep 8/64/4 defaults; change only with B3 A/B proof? Any appetite for save-state serialization of owed queues in v1? | Avoid silent over-fire / under-credit |
| **Q5** | Play!/PCSX2 oracle: minimum snapshot for one B3 flip IRQ (D_STAT, CHCR.nTAG, INTC STAT/MASK, handler PC, pending byte) — required before S6 or optional? | Scope of external oracle |
| **Q6** | Assist env-off hooks (`DETPS2_NO_B3_FLIP_CREDIT` etc.): land as **separate assist PR** after Core quiet, or require before claiming A1–A2? | Validation ownership |
| **Q7** | Default-on vs opt-in for any S6 behavior: recommend **kill-switched default-on only if smokes + three diagnose canaries hold**; otherwise opt-in `DETPS2_DMAC_LEVEL_CATCHUP=1` until proven. ACK preferred policy. | Fleet safety |

### Seed questions (carried)

1. ↔ **Q1**  
2. ↔ **Q2**  
3. ↔ **Q3**  
4. ↔ **Q4**  
5. ↔ **Q5**

---

## 9. Explicit: no GameQuirks growth; no title-named branches

| Rule | Apply to |
|------|----------|
| **No GameQuirks growth** for this problem class | Do not add new `CreditOwedHandlerCall` sites, new flip plants, new VIF busy pokes, or new sticky-tag force-finishes as the M5-a “fix.” Assists may later **shrink** under env-off after A1–A3. |
| **No title-named branches in Core** | No `SLUS_210.50`, `SCUS_973.99`, Haven serials, no PC constants from B3/GoW/Haven in `Dmac` / `SonyKernelHle` / `EmotionEngine`. Oracles are **diagnose canaries**, not Core `if`s. |
| **No RealSifRpc** | Out of charter for this design. |
| **No invent pixels / GIF** | Completion IRQs only. |

---

## 10. Source map

| Artifact | Path |
|----------|------|
| Seed | `docs/infra-audits/m5a-dmac-vif-gif-completion-seed.md` |
| Debt audit § priority #3 | `docs/infra-audits/gamequirks-infra-debt.md` |
| DMAC Core | `src/DetPS2.Core/Dmac.cs` |
| HLE handlers | `src/DetPS2.Core/SonyKernelHle.cs` |
| EE dispatch | `src/DetPS2.Core/EmotionEngine.cs` |
| INTC | `src/DetPS2.Core/Intc.cs` |
| B3 assist | `src/DetPS2.Core/GameQuirks/Burnout3Assist.cs` |
| GoW assist | `src/DetPS2.Core/GameQuirks/GodOfWarAssist.cs` |
| Haven assist | `src/DetPS2.Core/GameQuirks/TeamIcoAssist.cs` |
| Smoke | `Tests/SmokeTests.cs` (`Dmac_EnableDmac_DispatchesAddDmacHandler`) |
| IOP DMACMAN (not EE) | `docs/bios-ports/DMACMAN.md` |
| Graphics seat (not IRQ) | `docs/GRAPHICS_PIPELINE_PHASE_PLAN.md`, M7-a seed |
| Style peer design | `docs/infra-audits/m6b-sleepthread-rescue-design.md` |

---

## 11. Ready for implement ACK?

**Partial.**  

- **S1 telemetry:** ready after **Q0** ACK (safe, no behavior).  
- **S6 behavior:** **not** ready until **Q1–Q5** (and preferred **Q6–Q7**) answered from telemetry/oracle.  

Implement only after explicit ACK. This note contains **no Core changes**.

---

*Design only. Expand of seed `m5a-dmac-vif-gif-completion-seed.md`. Flag-gated investigation first; no Core behavior change until A0 telemetry and open-question ACK.*

## 12. S1 implementation note (2026-08-04)

**Landed:** Phase 0 / S1 telemetry only — zero DMA completion behavior change.

| Item | Detail |
|------|--------|
| Env | `DETPS2_TRACE_DMAC=1` — print only (interval every 4096 finishes + blocker-trace end dump via `DumpTraceSummary`) |
| Counters | Always accumulate per channel 0-9 (cheap ulong/int bumps) |
| Names | `finish`, `owedInc`, `owedPeak`, `preEnableInc`, `preEnablePromote`, `creditAssist`, `w1cWhileOwed`, `tryTakeCis`, `tryTakeOwed`, `raise` (+ totals finish/raise) |
| Ring | Last 32 (ch, reason, seq) when TRACE on; reason finish/credit/enable/take |
| Files | `Dmac.cs` (counters + dump), `SonyKernelHle.cs` (`NoteHandlerTake` only), `Program.cs` (end dump if TRACE) |
| Scoreboard | **Not** wired — TRACE-only first |
| Not done | EE dispatch optional hook; Phase 1 kill-switch / catch-up |

