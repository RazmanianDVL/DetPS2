# M8 Prefer / plant quiet — fleet audit (2026-08-04)

**Status:** docs only — dual-orch seat B while Claude on C1 boot-walk  
**Tip at write:** `869fcdb`  
**Purpose:** one table of PreferIopRp + version-plant debt; retirement readiness  

---

## 0. One-line

Most fleet Prefer/plant axes are already **soft-off default** or **assist-off**; **GoW plant remains the only load-bearing RAM plant** proven at diagnose/claim. Do not mass-delete `PlantIopRpVersion` or Prefer property until claim-class A/B green per title.

---

## 1. Fleet table

| Title | Prefer product | Plant product | Soft-off status | Evidence | Next |
|-------|----------------|---------------|-----------------|----------|------|
| **Whip** | soft-off default | soft-off default | diagnose **byte-identical** soft-off ↔ opt-in | `m8a-whip-bo2-softoff-canary.md` | Optional claim 100M if residual cadence questioned |
| **BO2** | soft-off default | soft-off default | diagnose **byte-identical** | same | Optional claim 100M |
| **B3** | assist OFF | plant **soft-off** default | diagnose dual-suppress **byte-identical** | `m8a-b3-dual-suppress-results.md` | Optional claim LGDEV residual cadence |
| **GoW** | Prefer **soft-off** | plant **ON** (load-bearing) | plant-off dual **FAIL** (cdvd→0) | `m8a-gow-dual-suppress-results.md` + `m4-gow-plant-residual-next.md` | Plant stays ON; S0 TRACE only with dual-ACK |
| **Haven** | Prefer assign (checklist) | no RAM plant | M8-a checklist; Prefer quiet if M4-b tag live | `m8a-haven-vexx-retirement-checklist.md` | Prefer soft-off canary if not already product |
| **Vexx** | Prefer + plant `"2520"` | plant sites | checklist staged Prefer then plant | same | Prefer A/B then plant A/B |
| **SotC / Ico** | TeamIco Prefer | — | optional after Haven green | checklist | Separate A/B |

Env naming pattern: `DETPS2_M8A_<TITLE>_NO_PREFER_IOPRP` / `_NO_VERSION_PLANT` with soft-off = unset skip, `=0` opt-back-in (Vexx/Whip/BO2 style).

---

## 2. Standing decisions

1. **GoW plant stays product-ON** until S0 retail-arg evidence (parked M4 residual).  
2. **No** global PreferIopRp property delete.  
3. **No** mass GameQuirks plant body delete — soft-off is enough for quiet titles.  
4. M4-b/M4-g tag-if-applied is the **version gate** for soft-off titles; Prefer is debt, not authority.  

---

## 3. Next seats (named bar)

| ID | Seat | Bar |
|----|------|-----|
| **M8-N1** | Whip/BO2 claim 100M soft-off A/B | scoreboard non-worse vs opt-in (or document claim drift) |
| **M8-N2** | Haven Prefer soft-off canary | diagnose identity then claim |
| **M8-N3** | Vexx Prefer then plant A/B | staged; keep CRT plants |
| **M8-N4** | Park M8 until title demand | default if no live Prefer crash |

**Bias:** **M8-N4 park** or **M8-N1** if fleet scoreboard noise still cites Prefer. Not GoW Core.

---

## 4. Dual-ACK

| ID | Question | Bias |
|----|----------|------|
| **MQ-Q1** | Accept this fleet audit as M8 standing status? | **Yes** |
| **MQ-Q2** | Next Core only with named title A/B? | **Yes** |
| **MQ-Q3** | GoW plant soft-off this week? | **No** |

---

```text
M8 Prefer quiet fleet audit
  Whip/BO2/B3 soft-off OK diagnose; GoW plant load-bearing
  no mass delete; claim A/B only with named bar
```
