# M8-a canary — Whiplash + Blood Omen 2 soft-off vs opt-back-in

**Date:** 2026-08-04  
**Tip:** `def77d8` (worktree `windows-detps2/detps2`)  
**Mode:** ops canary only — **no Core changes**, no push.  
**Budget:** **diagnose (20M)** via `scoreboard-metrics` + `--host-present`  
**Build:** Release → `out/scoreboard-build`

---

## 1. Scope

| Title | Fleet id | Serial | Media | Assist |
|-------|----------|--------|-------|--------|
| Whiplash | `whiplash` | `SLUS_206.84` | `user-media-whiplash.json` | `WhiplashAssist` |
| Blood Omen 2 | `blood-omen-2` | `SLUS_200.24` | `user-media-bloodomen2.json` | `BloodOmen2SnAssist` |

**Env semantics (product default = M8 soft-off):**

| Arm | Envs |
|-----|------|
| **soft-off** (product default) | `DETPS2_M8A_*_NO_PREFER_IOPRP` **unset**, `DETPS2_M8A_*_NO_VERSION_PLANT` **unset** → PreferIopRp + version plant **skipped** |
| **opt-back-in** (both axes) | `…_NO_PREFER_IOPRP=0` **and** `…_NO_VERSION_PLANT=0` → PreferIopRp + plant **restored** |

| Title | Prefer env | Plant env |
|-------|------------|-----------|
| Whip | `DETPS2_M8A_WHIP_NO_PREFER_IOPRP` | `DETPS2_M8A_WHIP_NO_VERSION_PLANT` |
| BO2 | `DETPS2_M8A_BO2_NO_PREFER_IOPRP` | `DETPS2_M8A_BO2_NO_VERSION_PLANT` |

ISO paths (both **present** this host):

- Whip: `C:/Users/user/Downloads/Whiplash(USA).iso`
- BO2: `C:/Users/user/Downloads/Blood Omen 2 - The Legacy of Kain Series (USA).iso`

---

## 2. How invoked

Same scoreboard path as fleet tooling:

```text
dotnet exec out/scoreboard-build/DetPS2.Core.dll scoreboard-metrics <media> --cycles=20000000 --out=<metrics.json> --host-present
```

Fleet keys from `tools/scoreboard-fleet.json` (`whiplash`, `blood-omen-2`).  
Harness pattern mirrors `tools/canary-c1-5-fleet-ab.ps1` (build once, two arms, native metrics JSON).

Artifacts:

```text
out/canaries/m8a-whip-bo2-softoff/20260804-104519/
  soft-off/{whiplash,blood-omen-2}-{metrics.json,out.txt,err.txt}
  opt-back-in/{whiplash,blood-omen-2}-{metrics.json,out.txt,err.txt}
  summary.json
```

---

## 3. Verdict

| Field | Value |
|-------|-------|
| ISO availability | **Both present** — no SKIP |
| Soft-off status | **RAN** (exit 0, `exitRequested=false`) both titles |
| Opt-back-in status | **RAN** (exit 0, `exitRequested=false`) both titles |
| Scoreboard-metrics identity | **Byte-identical** soft-off ↔ opt-back-in for **both** titles on all compared metric fields @ **20M diagnose** |
| MENU claim? | **No** — diagnose only; heuristic `GS?` both arms (px class, not MENU YES) |

**Honest read:** at diagnose budget, opting back into PreferIopRp + version plant does **not** change observed scoreboard metrics vs product soft-off. Soft-off is not a diagnose-tier crash/exit regression vs legacy plants.

**Caveat (prior evidence, not re-run here):** M4-e/M4-f recorded a **claim (100M)** dual-suppress drift for Whip (syscalls/calls +169, cdvd −2) when plants were product-on and dual-off was the experimental arm. This canary is the **inverted product default** (soft-off default, opt-back-in experimental) at **20M only** — do not claim claim-budget identity from this run.

---

## 4. Summary table (soft-off → opt-back-in)

| Title | status | exitReq | syscalls | calls | binds | cdvd | px | menuHeuristic | PC | identity |
|-------|--------|---------|----------|-------|-------|------|-----|---------------|-----|----------|
| Whiplash | RAN → RAN | F → F | 921 = | 114 = | 13 = | 916 = | 286720 = | GS? = | `0x003145A8` = | **byte-identical** |
| Blood Omen 2 | RAN → RAN | F → F | 701 = | 62 = | 14 = | 2211 = | 286720 = | GS? = | `0x00488898` = | **byte-identical** |

Also identical on both titles: `prims=1`, `gifP3=2`, `dmac=8`, `sifBytes` (Whip 20428 / BO2 18872), `expandHits=1`, `gifCompleted=3`, tiers `T0=Y T1=NEAR? T3=N G1=Y?`, `exitCode=0`.

Wall times ~1.7–3.4 s per arm (informational only).

---

## 5. Commands (repro)

```powershell
# Repo root; Release Core once
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/scoreboard-build --nologo -v q
$dll = "out/scoreboard-build/DetPS2.Core.dll"
$cycles = 20000000

# --- Soft-off (product default) ---
Remove-Item Env:DETPS2_M8A_WHIP_NO_PREFER_IOPRP,Env:DETPS2_M8A_WHIP_NO_VERSION_PLANT,
  Env:DETPS2_M8A_BO2_NO_PREFER_IOPRP,Env:DETPS2_M8A_BO2_NO_VERSION_PLANT -ErrorAction SilentlyContinue
dotnet exec $dll scoreboard-metrics user-media-whiplash.json --cycles=$cycles --out=out/whip-soft.json --host-present
dotnet exec $dll scoreboard-metrics user-media-bloodomen2.json --cycles=$cycles --out=out/bo2-soft.json --host-present

# --- Opt-back-in both ---
$env:DETPS2_M8A_WHIP_NO_PREFER_IOPRP = "0"
$env:DETPS2_M8A_WHIP_NO_VERSION_PLANT = "0"
dotnet exec $dll scoreboard-metrics user-media-whiplash.json --cycles=$cycles --out=out/whip-opt.json --host-present
Remove-Item Env:DETPS2_M8A_WHIP_NO_PREFER_IOPRP,Env:DETPS2_M8A_WHIP_NO_VERSION_PLANT -ErrorAction SilentlyContinue

$env:DETPS2_M8A_BO2_NO_PREFER_IOPRP = "0"
$env:DETPS2_M8A_BO2_NO_VERSION_PLANT = "0"
dotnet exec $dll scoreboard-metrics user-media-bloodomen2.json --cycles=$cycles --out=out/bo2-opt.json --host-present
Remove-Item Env:DETPS2_M8A_BO2_NO_PREFER_IOPRP,Env:DETPS2_M8A_BO2_NO_VERSION_PLANT -ErrorAction SilentlyContinue
```

---

## 6. Follow-ups (not done)

1. Optional **claim (100M)** A/B for Whip to re-check M4-e dual-axis drift under inverted product default.  
2. Residual assists (PreferSnFileIo, host0 rewrite, UsingCD, BO2 SN stubs / Host→Local) stay on — out of M8-a version soft-off.  
3. No mass-delete of plant code until claim-class evidence is accepted for each title.
