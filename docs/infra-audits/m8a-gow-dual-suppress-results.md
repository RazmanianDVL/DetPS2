# M8-a God of War — dual-suppress Prefer×plant results

**Date:** 2026-08-04  
**Tip:** `e68ed88` (+ local `GodOfWarAssist` / `SonyKernelHle` gates; **no push**)  
**Budget:** **diagnose (20M)** via `scoreboard-metrics` + `--host-present`  
**Media:** `user-media-god-of-war.json` → `C:/Users/xxraz/Downloads/GodofWar(USA).iso` (**present**)  
**Seed:** `docs/infra-audits/m8a-b3-gow-evidence-seed.md`  
**Build:** Release → `out/scoreboard-build`  
**Related:** `docs/infra-audits/m8a-b3-dual-suppress-results.md` (B3 plant soft-off; Prefer already off)

---

## 1. Scope

| Title | Fleet id | Serial | Assist | Pre-canary Prefer | Pre-canary plant |
|-------|----------|--------|--------|-------------------|------------------|
| God of War (USA) | `god-of-war` | `SCUS_973.99` | `GodOfWarAssist` | **ON** (mount + Ensure) | **ON** (`"3000"` @ `0x002C6D30`) |

**Dual-suppress meaning for GoW** (Prefer + plant both product-on axes):

| Axis | G0 baseline | G-dual |
|------|-------------|--------|
| PreferIopRp | ON | OFF |
| RAM plant `"3000"` | ON | OFF |
| `SetIopRpVersionAscii("3000")` / forced UDNL | product (empty-reboot only) | **not** suppressed in dual (Stage C separate) |

---

## 2. Gates landed

### 2.1 PreferIopRp — product soft-off (Stage A identity)

| Env | Evidence canary | After canary (product) |
|-----|-----------------|------------------------|
| `DETPS2_M8A_GOW_NO_PREFER_IOPRP` | `=1` skip Prefer; unset = Prefer ON | **soft-off default:** unset / any value except `0`/`false` → **skip Prefer**; `=0` or `false` → **opt back in** |

Sites gated:

- `OnDiscMounted` Prefer assign
- `EnsureIopRpGetVersion` Prefer assign
- Step tail re-clear `PreferIopRpGetVersion = false` when skip active (LITERAL_IRX neutralization)

### 2.2 Plant `"3000"` — evidence only (load-bearing; **not** soft-off)

| Env | Effect |
|-----|--------|
| `DETPS2_M8A_GOW_NO_VERSION_PLANT=1` | Skip all `PlantIopRpVersion` call sites (gate inside static method: mount, post-ELF 500k, reboot-gen, FreezeCache re-plant) |
| unset / other | **product plant ON** |

**Why not soft-off:** dual and Stage B plant-only both diverge from baseline (cdvd 136→0, calls 21→12). Plant is load-bearing at diagnose under current M4-b/M4-g GetVersion surface.

### 2.3 Stage C force axes — evidence only

| Env | Effect | Product default |
|-----|--------|-----------------|
| `DETPS2_M8A_GOW_NO_FORCE_TAG=1` | Skip `SetIopRpVersionAscii("3000")` inside Ensure | ON (force remains) |
| `DETPS2_M8A_GOW_NO_FORCE_UDNL=1` | Skip `ApplyUdnlHandoff(IOPRP300)` on empty reboot | ON |

Not exercised at diagnose: empty-reboot window is ~61M live (seed); 20M does not hit Ensure/UDNL force paths in a way that changes metrics independently of plant.

### 2.4 LITERAL_IRX Prefer auto-set (`SonyKernelHle.OnIopRebootCompleted`)

| Gate | Semantics |
|------|-----------|
| `DETPS2_M8A_B3_HOLD_PREFER_OFF=1` | skip auto-set (existing) |
| `DETPS2_M8A_GOW_NO_PREFER_IOPRP=1` | skip auto-set (**explicit =1 only**) |

**Important:** Prefer product soft-off must **not** use soft-off semantics in SonyKernelHle (env unset is global — would hold Prefer off for all titles). Soft-off Prefer isolation for GoW is via `GodOfWarAssist.Step` re-clear when `SkipPreferIopRp` is true.

### 2.5 FreezeCache residual (not gated)

FreezeCache error clear / spin escape remain product residual. Only the **version plant** half is env-gated. Without plant, the gate fails earlier (cdvd collapses); spin escape alone does not recover diagnose metrics.

---

## 3. Verdict

| Field | Value |
|-------|-------|
| ISO availability | **Present** — ran |
| Baseline (Prefer ON + plant ON) | **RAN** exit 0, `exitRequested=false` |
| Stage A (Prefer OFF, plant ON) | **RAN** — **byte-identical** to baseline |
| Stage B (Prefer ON, plant OFF) | **RAN** — **diverges** (plant load-bearing) |
| Dual (Prefer OFF + plant OFF) | **RAN** — **byte-identical to Stage B**, not to baseline |
| Scoreboard-metrics dual vs baseline | **NOT identical** |
| Prefer soft-off product post-land | **byte-identical** to evidence baseline SHA |
| MENU claim? | **No** — diagnose only |

**Honest read:**

1. **PreferIopRp is not load-bearing @ diagnose** after M4-b/M4-g tag-if-applied packing. Stage A Prefer suppress = baseline metrics. Prefer soft-off product is justified.
2. **RAM plant `"3000"` is load-bearing @ diagnose.** Suppressing plant (alone or dual) collapses **cdvd 136→0**, **calls 21→12**, **sifBytes 8116→2932**, **syscalls 2284→2249**, PC `0x002846A4` → `0x00283F08` (freeze-region constructor neighborhood). Quiet-retire plant is **blocked** until claim/verify proves title self-fills `0x2C6D30` or gate no longer needs the cell.
3. Dual failure is **plant-driven** (Prefer×plant dual == plant-only SHA). Prefer is free; plant is not.

---

## 4. Summary table (diagnose 20M)

| Arm | status | exitReq | syscalls | calls | binds | cdvd | px | prims | dmac | sifBytes | PC | identity vs G0 |
|-----|--------|---------|----------|-------|-------|------|-----|-------|------|----------|-----|----------------|
| **G0 baseline** Prefer ON plant ON | RAN | F | 2284 | 21 | 10 | 136 | 1433600 | 5 | 2 | 8116 | `0x002846A4` | — |
| **G-A** Prefer OFF plant ON | RAN | F | 2284 = | 21 = | 10 = | 136 = | 1433600 = | 5 = | 2 = | 8116 = | `0x002846A4` = | **byte-identical** SHA `5562B8F1…BB09C` |
| **G-B** Prefer ON plant OFF | RAN | F | 2249 | 12 | 10 | **0** | 1433600 = | 5 = | 2 = | 2932 | `0x00283F08` | **diverge** SHA `36D4CB20…B93D11` |
| **G-dual** Prefer OFF plant OFF | RAN | F | 2249 | 12 | 10 | **0** | 1433600 = | 5 = | 2 = | 2932 | `0x00283F08` | **== G-B** (not G0) |
| post soft-off product (Prefer soft-off, plant ON) | RAN | F | 2284 = | 21 = | 10 = | 136 = | … | … | … | 8116 = | `0x002846A4` = | **== G0/G-A** |
| Prefer rollback `=0` + plant ON | RAN | F | 2284 = | 21 = | 10 = | 136 = | … | … | … | 8116 = | `0x002846A4` = | **== G0** |

Also held across arms: `gifP2=17`, `gifP3=0`, `imgBytes=4144`, `expandHits=5`, `gifCompleted=2541`, tiers `T0=Y T1=Y? T3=Y? G1=Y? G2=Y`, `exitCode=0`, `liveRpcHits=0`.

Wall times ~3.5–3.8 s per arm (informational).

Artifacts:

```text
out/canaries/m8a-gow-dual-suppress/20260804-110117/
  baseline/{god-of-war-metrics.json,out.txt,err.txt,metrics.sha256}
  stage-a-prefer-off/{…}
  stage-b-plant-off/{…}
  dual-suppress/{…}
  post-softoff-product/{…}
  prefer-rollback0/{…}
  summary.json
```

---

## 5. Product follow-through (this seat)

| Axis | Decision | Rationale |
|------|----------|-----------|
| PreferIopRp | **Soft-off landed** (Vexx/Whip style; rollback `DETPS2_M8A_GOW_NO_PREFER_IOPRP=0`) | Stage A byte-identical |
| Plant `"3000"` | **Product ON**; evidence gate `=1` only | Dual/Stage B fail (cdvd=0) |
| Force tag / force UDNL | Product ON; evidence `=1` only | Not proven idle at 20M; empty reboot ~61M |
| FreezeCache non-version residual | **Stay on** | Not a version-plant retire |
| BST/heap/worker/stream/R_SHELL residual | **Stay on** | Non-version; out of seat |

---

## 6. Commands (repro)

```powershell
# Repo root; Release Core
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/scoreboard-build --nologo -v q
$dll = "out/scoreboard-build/DetPS2.Core.dll"
$cycles = 20000000
$media = "user-media-god-of-war.json"

# --- Evidence shape (pre Prefer soft-off): Prefer ON baseline ---
# With current soft-off Prefer product, opt Prefer back in for G0-classic:
$env:DETPS2_M8A_GOW_NO_PREFER_IOPRP = "0"
Remove-Item Env:DETPS2_M8A_GOW_NO_VERSION_PLANT -ErrorAction SilentlyContinue
dotnet exec $dll scoreboard-metrics $media --cycles=$cycles --out=out/gow-g0.json --host-present

# Stage A Prefer off (product soft-off path): unset Prefer env
Remove-Item Env:DETPS2_M8A_GOW_NO_PREFER_IOPRP -ErrorAction SilentlyContinue
dotnet exec $dll scoreboard-metrics $media --cycles=$cycles --out=out/gow-ga.json --host-present

# Dual / plant suppress (expect cdvd collapse @diagnose):
$env:DETPS2_M8A_GOW_NO_VERSION_PLANT = "1"
dotnet exec $dll scoreboard-metrics $media --cycles=$cycles --out=out/gow-dual.json --host-present
Remove-Item Env:DETPS2_M8A_GOW_NO_VERSION_PLANT -ErrorAction SilentlyContinue
```

Evidence canary stamp `20260804-110117` used **pre soft-off Prefer** gate semantics (`=1` suppress, unset Prefer ON).

---

## 7. Sign-off (diagnose)

```text
M8-a GoW: Prefer OFF + plant suppress @diagnose(20M)
  Stage A Prefer OFF plant ON: BYTE-IDENTICAL to Prefer-ON baseline
  Stage B / dual plant OFF: DIVERGE — cdvd 136→0 calls 21→12 sif 8116→2932
    pc 0x002846A4 → 0x00283F08 (freeze-region constructor class)
  Prefer soft-off product: LANDED (rollback =0)
  Plant soft-off product: NOT landed (load-bearing)
  Force tag/UDNL: not staged off product; empty reboot ~61M
  FreezeCache residual / non-version assists: still on
  PASS Prefer quiet half @diagnose — FAIL dual / plant quiet
  claim residual optional follow-up for plant re-prove
```

---

## 8. Open risks / follow-ups

1. **Plant load-bearing forever?** Until title stores GetVersion/`IOPRP300` into `0x2C6D30` (or UDNL image apply fills it) before the freeze-region memcmp, plant cannot quiet-retire. TRACE_RPC + claim-class (100M) recommended before any plant soft-off attempt.
2. **FreezeCache residual coupling:** Plant suppress may raise `0xFFFEFFFC` earlier; residual clear/spin still fire under plant-on product. After plant quiet (if ever), keep FreezeCache residual until proven unused.
3. **Stage C force tag/UDNL:** Not measured at 20M. Re-run dual + `NO_FORCE_TAG` / `NO_FORCE_UDNL` at claim if empty `SifIopReset` still live @ tip.
4. **FILEIO GetVersion open Q** (seed §3): TRACE_RPC counts still open for GoW.
5. **FILEIO-2200 arm:** Prefer soft-off should help avoid false Play! arm with tag `"3000"`; not separately scored here.
6. **Budget honesty:** diagnose proves Prefer free + plant needed for IRX/cdvd floor; claim not re-proven under Prefer soft-off (expected hold by Stage A identity).
7. No mass-delete of `PlantIopRpVersion` body. No push from this seat.

---

*Results + Prefer soft-off only. Plant remains product-on with evidence suppress env. No push.*
