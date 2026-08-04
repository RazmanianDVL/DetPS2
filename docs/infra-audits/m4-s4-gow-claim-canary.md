# M4-S4 GoW claim canary — product plant ON vs plant-off + EE mirror

**Date:** 2026-08-04  
**Tip:** `d74c6b2` (`d74c6b2277281fd0346cff0da18b67c5c2ddb780`)  
**Mode:** ops claim A/B only — **no Core changes**, **no plant default flip**, **no push**.  
**Budget:** **claim (100M)** via `scoreboard-metrics` + `--host-present`  
**Fleet id:** `god-of-war` (`tools/scoreboard-fleet.json`)  
**Media:** `user-media-god-of-war.json` → `C:/Users/xxraz/Downloads/GodofWar(USA).iso` (**present**)  
**Build:** Release → `out/scoreboard-build`  
**Related:**  
- `docs/infra-audits/m4-s4-ee-mirror-design.md` §6.2 G-S4 / Q9  
- `docs/infra-audits/m8a-gow-dual-suppress-results.md` (plant load-bearing @ diagnose)  
- `docs/UDNL_GETVERSION_UNIFICATION.md` §3.1 S4 / §5.2 mirror env  

---

## 1. Scope

| Title | Fleet id | Serial | Assist |
|-------|----------|--------|--------|
| God of War (USA) | `god-of-war` | `SCUS_973.99` | `GodOfWarAssist` |

**A/B arms (Prefer soft-off already product default — left unset):**

| Arm | PreferIopRp | RAM plant `"3000"` @ `0x002C6D30` | EE mirror |
|-----|-------------|-------------------------------------|-----------|
| **Baseline (product)** | soft-off (unset) | **ON** (product) | **off** (unset) |
| **Plant-off + mirror** | soft-off (unset) | **OFF** `DETPS2_M8A_GOW_NO_VERSION_PLANT=1` | **ON** `DETPS2_MIRROR_IOPRP_CELLS=1` |

**Pass bar (Claude Q9 / design §6.2):** plant-off + mirror **non-worse** vs product baseline at claim on load-bearing gates (cdvd, calls, sifBytes, PC not stuck early freeze constructor class). Byte-identical preferred for any product plant soft-off flip; this seat **reports only** — does not flip plant default even on pass.

---

## 2. How invoked

```text
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/scoreboard-build --nologo -v q
dotnet exec out/scoreboard-build/DetPS2.Core.dll scoreboard-metrics user-media-god-of-war.json \
  --cycles=100000000 --out=<metrics.json> --host-present
```

| Arm | Env |
|-----|-----|
| Baseline | no special `DETPS2_*` (plant ON, mirror off, Prefer soft-off default) |
| Plant-off+mirror | `DETPS2_M8A_GOW_NO_VERSION_PLANT=1` + `DETPS2_MIRROR_IOPRP_CELLS=1` |

Artifacts:

```text
out/canaries/m4-s4-gow-claim/20260804-124456/
  baseline/{god-of-war-metrics.json,out.txt,err.txt,metrics.sha256}
  plant-off-mirror-on/{god-of-war-metrics.json,out.txt,err.txt,metrics.sha256}
  summary.json
```

| Arm | metrics SHA256 |
|-----|----------------|
| Baseline | `2BDED68014BCE34D38E7F3DE3F0FF3AF844B13DC41EAF9DE103E70DDE4EFA75D` |
| Plant-off+mirror | `6FFF071C3D69279C5D4C3C2795BB1B9289F19449758ECDC2AA82E88318155015` |

---

## 3. Metrics table (claim 100M)

| Field | Baseline (plant ON, mirror off) | Plant-off + mirror ON | Δ / identity |
|-------|----------------------------------|------------------------|--------------|
| status | RAN exit 0 | RAN exit 0 | both ran |
| exitRequested | false | false | = |
| exitCode | 0 | 0 | = |
| **cdvd** (`cdvdSectors`) | **646** | **510** | **−136 worse** |
| **calls** | **42** | **19** | **−23 worse** |
| **binds** | 10 | 10 | = |
| **pc** | `0x0017A0DC` | `0x0026C4B4` | **diverge** |
| **dmac** | 2 | 2 | = |
| syscalls | 11639 | 5354 | −6285 worse |
| sifBytes | 10028 | 3556 | −6472 worse |
| px | 1646610 | 1646610 | = |
| prims | 6 | 6 | = |
| gifP2 / gifP3 | 17 / 0 | 17 / 0 | = |
| imgBytes | 266288 | 266288 | = |
| expandHits | 5 | 5 | = |
| gifCompleted | 2541 | 2541 | = |
| **menuHeuristic** (tiers) | `T0=Y T1=Y? T3=Y? G1=Y? G2=Y` | same | = (not MENU YES) |
| metrics JSON | SHA above | SHA above | **not byte-identical** |

Wall times ~17.3 s baseline / ~15.9 s plant-off+mirror (informational).

---

## 4. Honest verdict

| Field | Value |
|-------|-------|
| ISO availability | **Present** — ran |
| Baseline product | **RAN** |
| Plant-off + mirror | **RAN** (no crash / no exitRequested) |
| Scoreboard identity | **NOT byte-identical** |
| Non-worse on claim gates? | **NO** — cdvd, calls, sifBytes, syscalls, PC all worse/diverge |
| Plant soft-off product flip? | **FAIL — do not flip** (this seat left plant product-ON) |
| MENU claim? | **No** — heuristic tiers only; not MENU YES |

**Verdict line:**

```text
M4-S4 GoW claim(100M): FAIL plant soft-off product flip —
  plant-off + DETPS2_MIRROR_IOPRP_CELLS=1 is WORSE than product plant-ON baseline
  (cdvd 646→510, calls 42→19, sif 10028→3556, pc 0x0017A0DC→0x0026C4B4).
  Soft-GS surface (px/prims/gif/dmac/binds) held; boot/RPC progress did not.
  Plant default remains ON. Mirror remains opt-in. No push.
```

### Context vs diagnose (20M plant-off alone)

From `m8a-gow-dual-suppress-results.md` @ diagnose, plant-off **without** mirror collapsed **cdvd 136→0**, **calls 21→12**.  
At claim with mirror, cdvd **510** (not zero) and Soft-GS floors match product — so mirror is **partially active / not a no-op**, but it does **not** restore product plant progress. That is still **fail** for quiet-retiring the plant under Q9.

Likely residual classes (not proven here): tag publish timing before memcmp, force-tag/UDNL empty-reboot path (~61M live window historically), FreezeCache non-version residual, or single-shot mirror missing a later scrub.

---

## 5. Product follow-through (this seat)

| Axis | Decision |
|------|----------|
| GoW PreferIopRp | unchanged (product soft-off) |
| GoW plant `"3000"` | **stay product ON** — claim A/B failed non-worse bar |
| `DETPS2_MIRROR_IOPRP_CELLS` | **stay default off** (opt-in only) |
| Core / plant body delete | **none** |
| Push | **none** |

---

## 6. Repro commands

```powershell
# Repo root; Release Core
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/scoreboard-build --nologo -v q
$dll = "out/scoreboard-build/DetPS2.Core.dll"
$cycles = 100000000
$media = "user-media-god-of-war.json"

# Baseline product
Remove-Item Env:DETPS2_M8A_GOW_NO_VERSION_PLANT,Env:DETPS2_MIRROR_IOPRP_CELLS,
  Env:DETPS2_M8A_GOW_NO_PREFER_IOPRP -ErrorAction SilentlyContinue
dotnet exec $dll scoreboard-metrics $media --cycles=$cycles --out=out/gow-claim-baseline.json --host-present

# Plant OFF + mirror ON
$env:DETPS2_M8A_GOW_NO_VERSION_PLANT = "1"
$env:DETPS2_MIRROR_IOPRP_CELLS = "1"
dotnet exec $dll scoreboard-metrics $media --cycles=$cycles --out=out/gow-claim-s4.json --host-present
Remove-Item Env:DETPS2_M8A_GOW_NO_VERSION_PLANT,Env:DETPS2_MIRROR_IOPRP_CELLS -ErrorAction SilentlyContinue
```

---

## 7. Follow-ups (not done)

1. TRACE_RPC / TRACE_MIRROR at claim to confirm `[S4-MIRROR]` write timing vs FreezeCache memcmp.  
2. Stage C force-tag / force-UDNL interactions with plant-off + mirror (empty reboot).  
3. If tag never lands before first cell consumer: S0 arg / Ensure order before another S4 canary.  
4. Parent may re-run after mirror engine fixes — **do not** soft-off plant on this evidence.

---

*Claim canary results only. Plant remains product-on. No push.*
