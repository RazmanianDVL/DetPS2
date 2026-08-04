# M5-a seed — DMAC → VIF/GIF handler IRQ completion fidelity

**Status:** design **SEED** (implementation-ready sketch) — not a full final design  
**Date:** 2026-08-04  
**Mode:** read-only. **No Core code changes** in this note.  
**Priority source:** `docs/infra-audits/gamequirks-infra-debt.md` §4 / priority #3  
**Owned code (future):** `src/DetPS2.Core/Dmac.cs`, EE DMAC handler dispatch (Kernel HLE / INTC), VIF1 status paths, GIF chain completion side-effects  
**Related:** B3 flip residual, GoW sticky GIF DMA tags, Haven VIF busy; `docs/TITLE_HACKS.md`, `docs/bios-ports/DMACMAN.md`

---

## 1. Problem class

Commercial titles **arm AddDmacHandler(VIF1/GIF)** and treat DMA completion as a **software event stream**:

```text
DMAC FinishChannel → D_STAT CIS + (if unmasked) DmaController IRQ
  → EE AddDmacHandler(ch) body
  → game clears pending / busy / drains flip or path-sync queue
```

When that chain is incomplete, titles observe:

| Symptom class | What software sees |
|---------------|--------------------|
| Flip / path-sync park | pending-count never hits 0; out≠in never drains |
| VIF software busy stuck | CHCR.STR already clear, but game flag still set |
| DMA tag builder sticky | QWC+END never finalized; main never reaches pad/worker posters |

Assists today **credit owed handler calls** or **clear busy flags** instead of fixing the shared completion → IRQ → handler fidelity. That is INFRA debt, not FPS.

---

## 2. Evidence from assists (symptoms only)

### 2.1 Burnout 3 — `CreditOwedHandlerCall` flip residual

**File:** `src/DetPS2.Core/GameQuirks/Burnout3Assist.cs`

- Consumer at `0x001F1778` only decrements **pending** on IRQ (`a0` = VIF1/GIF) and only drains out→in when pending hits 0.
- Assist re-arms via `Dmac.CreditOwedHandlerCall(VIF1/GIF, need)` + `EnableChannelIrq`; **must not** force out←in (that early-outs drain → infinite gifP3 with stuck calls).
- Audit roll-up: *“GS flip pending via CreditOwedHandlerCall (DMA IRQ timing)”*.

### 2.2 God of War — sticky GIF DMA tag builders + IRQ credit

**File:** `src/DetPS2.Core/GameQuirks/GodOfWarAssist.cs`

- PL-023 / WAVE-11B: force-finish sticky GIF/VIF DMA tag builders at `0x13F5xx` (QWC + END `0x70000000`) when park mid-align-pad with poison cursor `*0x32F168`.
- Stream-follow hang / world-kick paths also `CreditOwedHandlerCall(GIF/VIF1)` and enable channel IRQs so queued work can drain — **no invented GIF packets**.
- Audit: *“sticky GIF DMA tags; heap escapes SECONDARY”*.

### 2.3 Haven (Team Ico) — VIF1 software-busy while channel idle

**File:** `src/DetPS2.Core/GameQuirks/TeamIcoAssist.cs`

- Wait at `0x188AE0`: callee returns `*(0x39C0C4)` (busy) set when VIF1 chain kicked (`CHCR=0x1C5`), **cleared by DMA completion path**.
- When STR clear / channel idle but busy/pending still set → assist clears flags + `CreditOwedHandlerCall(VIF1, 1)`.
- Audit: *“Haven VIF busy/IRQ”*.

### Shared pattern

All three are **completion side-effect missing or lost** relative to hardware expectations, papered by title-local re-credit or flag poke.

---

## 3. Hypothesized general infra gap

Not “one missing IRQ.” A **stack** of fidelity gaps:

| Layer | Suspected gap | Existing partial mitigation in Core |
|-------|---------------|-------------------------------------|
| **Finish → CIS** | Completions before mask arm lose handler calls | `_preEnableCompletions` + promote on `EnableChannelIrq` (B3-motivated) |
| **CIS → handler** | Racey D_STAT W1C before EE dispatch drops the event | `_owedHandlerCalls` queue + `CreditOwedHandlerCall` (assist API + Finish path) |
| **INTC mask** | DmaController MASK bit 14 dropped while channel CIM live | `RaiseDmacIrq` forces MASK bit 14 (B3 comment in `Dmac.cs`) |
| **Handler fidelity** | AddDmacHandler body must see correct `a0`/channel and CHCR.nTAG (END/REFE+IRQ) | CHCR high half latches tag nTAG (DA REFE/END checks) — still title-sensitive |
| **VIF/GIF status** | Software busy / path-sync pending only clear when **handler runs**, not merely STR clear | No shared “status mirror”; Haven/B3 poke game RAM or credit handlers |
| **Tag builder / chain end** | Incomplete chain or missing END delivery leaves builder mid-body | GoW force-finish is **title PC repair**, not DMAC |

**Hypothesis (one sentence):**  
Default-safe DMAC already queues some owed calls, but **handler dispatch cadence, level-sensitive catch-up, and VIF/GIF completion status observed by game code** still diverge from Play!/hw enough that multiple titles re-credit IRQs or clear busy in GameQuirks.

---

## 4. Non-goals

- **No per-title flip/VIF quirks as the fix.** Assists stay until shared infra is quiet under env-off; do not grow more `CreditOwedHandlerCall` plants.
- **No invent GIF packets / PATH3 plants** to “complete” graphics (PRESENT residual is M7-a).
- **No force out←in / fake pending=0** as a product path (B3 telemetry already proved that wedges drain).
- **No wholesale deletion of Burnout3Assist / GodOfWarAssist / TeamIcoAssist** in this workstream.
- **No Core code in this seed** — investigation and flag-gated PRs only after acceptance sketch below.

---

## 5. Proposed investigation order

Flag-gated, default-safe (opt-in diagnostics / opt-in behavior; roster green when flags off).

1. **Telemetry only (default-on counters, no behavior change)**  
   - Per channel: `FinishChannel` count, `_owedHandlerCalls` peak, `_preEnableCompletions` promote, W1C before take, `TryTakePendingDmacHandler` drain, handler entry with `a0`.  
   - Env sketch: `DETPS2_TRACE_DMAC=1` (extend existing style).

2. **B3 flip pending as primary oracle**  
   - Claim window: pending byte + out/in + gifP3/calls without assist re-credit.  
   - Bisect: mask arm timing vs completion order; INTC MASK 14 lifetime; handler call count vs game pending increments.

3. **Haven VIF busy as status oracle**  
   - When CHCR.STR clear and channel idle, confirm whether game’s busy flag should already have been cleared by a real handler side-effect (or by a missing VIF STAT path).  
   - Distinguish: missing IRQ vs missing **status bit** vs missing **game-side clear in handler** never entered.

4. **GoW DMA tag builder as chain-end oracle**  
   - Separate pure DMAC completion from **EE tag-builder thrash** (poison cursor). Only promote to shared fix if Finish/IRQ absence is causal; otherwise leave as SECONDARY thrash (different workstream).

5. **DA display-chain END+IRQ nTAG** (adjacent evidence)  
   - Handlers that check `CHCR & 0xF0000000 ∈ {0x8,0xF}` — verify nTAG latch + STR clear order matches Play! so DA-class locks clear without family assist.

6. **Shared policy sketch (after 1–5)**  
   - Prefer: correct Finish → CIS → level-sensitive IRQ → durable owed queue → dispatcher drain → game handler.  
   - Optional flag: `DETPS2_DMAC_STRICT_HANDLERS=1` for fail-fast when completion has no consumer (dev only).

---

## 6. Acceptance sketch

| Gate | Criteria |
|------|----------|
| **A0** | Telemetry PR merges with flags default-safe; B3/GoW/Haven fleet MENU YES holds. |
| **A1** | B3: flip pending drains to 0 with **assist flip re-credit silenced** (`DETPS2_NO_B3_FLIP_CREDIT` or equivalent env-off) for a documented claim budget; Soft-GS floor not worse. |
| **A2** | Haven: VIF busy wait exits with **assist busy-clear silenced** while CHCR.STR idle; no new WaitSema thrash. |
| **A3** | GoW: sticky DMA-tag force-finish either silent under env-off **or** reclassified SECONDARY with written root cause ≠ DMAC IRQ. |
| **A4** | No title-local `CreditOwedHandlerCall` added for new titles; shared path documented in `TITLE_HACKS` / this seed’s follow-up design. |

Success = **quirks go quiet under env-off**, not “assist deleted first.”

---

## 7. Open questions

1. Is B3 pending wedge still primarily **lost CIS before dispatch**, or **handler not scheduled often enough** under EE IRQ nesting / EXL?
2. Should VIF1 **software busy** be mirrored from CHCR/FBRST in Core, or must it remain purely game RAM updated only by the title’s handler?
3. Are GoW `0x13F5xx` parks caused by missing END delivery from DMAC, or pure EE state corruption after SIF/worker gaps (priority #2/#4 in infra-debt)?
4. Cap policy: current owed-call caps (8 credit / 64 queue / 4 pre-enable) — do they hide under-credit vs over-fire races?
5. Play!/PCSX2 oracle: minimum D_STAT + CHCR.nTAG + INTC snapshot needed to diff one B3 flip IRQ vs DetPS2?

---

## 8. Source map

| Artifact | Path |
|----------|------|
| Debt audit §4 | `docs/infra-audits/gamequirks-infra-debt.md` |
| DMAC Core | `src/DetPS2.Core/Dmac.cs` (`FinishChannel`, `CreditOwedHandlerCall`, `EnableChannelIrq`) |
| B3 assist | `src/DetPS2.Core/GameQuirks/Burnout3Assist.cs` |
| GoW assist | `src/DetPS2.Core/GameQuirks/GodOfWarAssist.cs` |
| Haven assist | `src/DetPS2.Core/GameQuirks/TeamIcoAssist.cs` |
| Graphics path seat | `docs/GRAPHICS_PIPELINE_PHASE_PLAN.md` (S8 owns GIF/VIF delivery; this seed owns **IRQ completion**, not pixels) |

---

*Seed only. Flag-gated investigation first; no Core behavior change until A0 telemetry and a follow-up design lock acceptance A1–A3.*
