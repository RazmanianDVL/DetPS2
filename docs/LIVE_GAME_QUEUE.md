# Live game queue

**Script:** [`tools/live-game-queue.ps1`](../tools/live-game-queue.ps1)  
**Root layout:** `out/live-queue/{inbox,running,done,logs}/`  
**SOP:** [`AGENT_SOP.md`](AGENT_SOP.md) · Soft-GS truth: [`CORRECTNESS.md`](CORRECTNESS.md) · Present: [`HOST_PRESENT.md`](HOST_PRESENT.md)

Concurrent **game runners** are capped so research agents can keep diagnosing titles without thrashing the host. Agents never call `blocker-trace` in a free-for-all; they **enqueue inbox JSON** and read **`done/*.json`**.

---

## 1. Concurrency model

| Role | Count | Responsibility |
|------|------:|----------------|
| **Game runners** | **max 3** | `dotnet exec DetPS2.Core.dll blocker-trace …` processes owned by the queue script |
| **Research agents** | **7** | Oracle / wall analysis; write one job JSON each into `inbox/` and poll `done/` |

- Default: `-MaxConcurrent 3` (override only for deliberate stress; do not raise casually).
- Poll interval: `-PollMs 1500`.
- Jobs start only while `running process count < MaxConcurrent`.
- Inbox sort: **lower `priority` first**, then older `LastWriteTime`.

```text
  7 research agents                    queue runner (1 process)
        │                                     │
        │  write inbox/<id>.json              │  poll inbox
        ▼                                     ▼
   out/live-queue/inbox/  ──claim──►  running/<id>.json
                                          │
                                          │  up to 3 × blocker-trace
                                          ▼
                                   done/<id>.json  (+ job archive)
                                   logs/<id>-out.txt / -err.txt
```

**Do not** start extra parallel `run-title.ps1` / manual `blocker-trace` waves against the same machine while the queue is draining capacity — use the inbox.

---

## 2. Directory layout

All paths are under the repo root (`detps2/`). Directories are created on first run.

| Path | Role |
|------|------|
| `out/live-queue/inbox/*.json` | Pending jobs (agents write here) |
| `out/live-queue/running/<id>.json` | Claimed job currently executing |
| `out/live-queue/done/<id>.json` | **Result** (metrics + exit) |
| `out/live-queue/done/<id>.job.json` | Archived original job file |
| `out/live-queue/logs/<id>-out.txt` | Captured stdout |
| `out/live-queue/logs/<id>-err.txt` | Captured stderr |

`out/live-queue/` is operator local (under gitignored `out/`).

---

## 3. Inbox job schema (research agents enqueue)

One JSON object per file. Filename should be unique; **`id`** is the stable key for results.

```json
{
  "id": "unique-id",
  "media": "user-media-deception.json",
  "cycles": 50000000,
  "hostPresent": true,
  "nativeBios": false,
  "priority": 0
}
```

| Field | Type | Default | Meaning |
|-------|------|---------|---------|
| `id` | string | new GUID | Result key; use a stable, human-readable id (`client-a-dec-50m`) |
| `media` | string | *(required)* | Path to media JSON (repo-relative or absolute). Must exist or job fails immediately |
| `cycles` | ulong | `50000000` | `blocker-trace --cycles=` budget (prefer diagnose **20M** first; verify **50M**) |
| `hostPresent` | bool | `true` | Passes `--host-present` when true |
| `nativeBios` | bool | `false` | Passes `--native-bios` when true |
| `priority` | number | `0` | Lower runs first |

### Enqueue (agent / operator)

```powershell
# From repo root (detps2/)
$inbox = "out/live-queue/inbox"
New-Item -ItemType Directory -Force -Path $inbox | Out-Null

@{
  id          = "dec-diagnose-20m"
  media       = "user-media-deception.json"
  cycles      = 20000000
  hostPresent = $true
  nativeBios  = $false
  priority    = 0
} | ConvertTo-Json | Set-Content (Join-Path $inbox "dec-diagnose-20m.json") -Encoding utf8
```

Atomic tip: write to `inbox/<id>.json.tmp` then `Move-Item` to `inbox/<id>.json` so the poller never reads a half-written file.

### What the runner executes

```text
dotnet exec src/DetPS2.Core/bin/Release/net9.0/DetPS2.Core.dll blocker-trace <mediaPath> --cycles=<N> [--host-present] [--native-bios]
```

If Core is missing, the script builds `DetPS2.Core` Release once at startup.

---

## 4. Result schema (`done/<id>.json`)

### Success / process finished

Scraped from blocker-trace stdout (`px=`, `lit=`, `PC=0x…`, `prims=`).

```json
{
  "id": "dec-diagnose-20m",
  "ok": true,
  "exitCode": 0,
  "px": 966000,
  "lit": 12000,
  "prims": 42,
  "pc": "0x00123456",
  "outLog": "C:\\…\\out\\live-queue\\logs\\dec-diagnose-20m-out.txt",
  "errLog": "C:\\…\\out\\live-queue\\logs\\dec-diagnose-20m-err.txt",
  "finishedUtc": "2026-07-31T12:34:56.7890123Z"
}
```

| Field | Type | Meaning |
|-------|------|---------|
| `id` | string | Job id |
| `ok` | bool | `exitCode == 0` |
| `exitCode` | int | Process exit code |
| `px` | long | Soft-GS `PixelsWritten` scrape (0 if absent) |
| `lit` | long | Non-black present sample scrape (0 if absent) |
| `prims` | int | Soft-GS primitives scrape |
| `pc` | string | EE PC hex (`""` if absent) |
| `outLog` / `errLog` | string | Full log paths |
| `finishedUtc` | string | ISO-8601 UTC finish time |

### Immediate failure (media missing)

No process is started. Job file is moved to `done/<id>.job.json`.

```json
{
  "id": "dec-diagnose-20m",
  "ok": false,
  "error": "media missing: C:\\…\\user-media-deception.json"
}
```

### Agent consumption

```powershell
$done = "out/live-queue/done"
# Wait for result
while (-not (Test-Path "$done/dec-diagnose-20m.json")) { Start-Sleep -Milliseconds 500 }
Get-Content "$done/dec-diagnose-20m.json" -Raw | ConvertFrom-Json
```

Use **`px` / `prims` / `pc`** (and full `outLog`) for wall reports. Heuristic alone is not a MENU claim — see [`AGENT_SOP.md`](AGENT_SOP.md) §4 and [`tools/SCOREBOARD_SCHEMA.md`](../tools/SCOREBOARD_SCHEMA.md).

---

## 5. How to start the queue runner

```powershell
# From repo root (detps2/)
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue

# Continuous drain (default: max 3 concurrent)
pwsh ./tools/live-game-queue.ps1

# One drain pass then exit when idle
pwsh ./tools/live-game-queue.ps1 -Once

# Optional overrides
pwsh ./tools/live-game-queue.ps1 -MaxConcurrent 3 -PollMs 1500
```

| Parameter | Default | Notes |
|-----------|---------|--------|
| `-MaxConcurrent` | `3` | Hard cap on simultaneous `blocker-trace` processes |
| `-PollMs` | `1500` | Sleep between claim/complete loops |
| `-Once` | off | Exit when no running jobs and inbox empty |

Typical operator flow:

1. Start `live-game-queue.ps1` in a dedicated terminal (or background).
2. Up to **7 research agents** drop jobs into `out/live-queue/inbox/`.
3. Runner claims ≤3 at a time → `running/` → results in `done/`.
4. Agents read `done/<id>.json` + logs; enqueue verify/claim budgets only after diagnose walls.

---

## 6. Stability doctrine — EE off the UI thread

Desktop and any interactive host **must not** run the Emotion Engine on the Avalonia/UI thread.

| Rule | Detail |
|------|--------|
| **EE on worker thread** | `EmulationWorker` (`DetPS2-EE`, background, above-normal) owns `Ps2System.RunFor` |
| **UI never blocks on RunFor** | UI timer only **snapshots** Soft-GS present; no long EE slices on `Dispatcher.UIThread` |
| **Double-buffer present** | Worker writes back buffer → flips front; UI reads latest frame without holding the EE lock during blit |
| **Headless queue path** | `live-game-queue` uses Core CLI (`blocker-trace`); no Avalonia — still one process per job, cap **3** |
| **Why** | Windows “Not Responding”, missed paints, and non-deterministic UI↔EE interleaving when EE runs on the UI thread |

Implementation reference: `src/DetPS2.Desktop/EmulationWorker.cs` — *“Runs EE `Ps2System.RunFor` on a background thread so the Avalonia UI thread never blocks.”*

Queue stability follows the same spirit: **isolate heavy EE work** in dedicated processes, bound concurrency, and keep agent orchestration I/O (JSON drop/poll) off the emulator critical path.

---

## 7. Soft-GS doctrine — present correctness gates

Binding product rules (do not bypass for a prettier window):

1. **CPU Soft-GS is ground truth** for metrics and claims (`px`, prims, GIF paths, DISPFB, imgBytes, PC, …).  
2. **Host present is display only** — D3D/Vulkan/OpenGL/`--host-present` never replace Soft-GS hashes or raster math ([`HOST_PRESENT.md`](HOST_PRESENT.md)).  
3. **No host cheats** that fake console video/UI/I/O (no FFmpeg logos, synthetic branded overlays, invented I/O success) — [`CORRECTNESS.md`](CORRECTNESS.md).  
4. **SEMA_OFF** for claims; `DETPS2_SEMA_STALL_YIELD` stays off unless a documented experiment.  
5. Scoreboard heuristics (`NEAR?` / `GS?` / `Y?`) are **not** MENU YES.

### Present / Soft-GS gates (what queue results support)

Use `done/*.json` + scoreboard rows as **evidence inputs**, not automatic pass:

| Gate family | What “correct present” means | Queue signal |
|-------------|------------------------------|--------------|
| **Soft-GS drew** | Non-trivial `px` / `prims` from EE→GIF→GS, not host paint | `px`, `prims` in `done/<id>.json` |
| **G1 Path fidelity** | Path1/2/3 tags complete; no silent drop | Full `outLog` / scoreboard `gifP*`, completed/aborted |
| **G2 Texture / IMAGE** | Host↔Local / Local↔Local BITBLT when title submits | scoreboard `imgBytes` (not in thin queue scrape) |
| **G3 DISPFB** | Natural DISPFB→output preferred over residual FRAME/FBP0 | scoreboard `naturalDispfbPx` / `compositeSource` |
| **G4 Expand demotion** | Title-strip ofx expand → 0 while px floor held | scoreboard `expandHits` |
| **G-GFX-0…9** | Pipeline north-star ([`GRAPHICS_PIPELINE_PHASE_PLAN.md`](GRAPHICS_PIPELINE_PHASE_PLAN.md)) | claim-budget scoreboard + PPM when `px>0` |

**Black Soft-GS + honest residual > pretty wrong screen.**  
If host present shows chrome that Soft-GS does not, treat host as a bug or optional upscale — **never** as claim evidence.

---

## 8. Policy reminders

- Prefer **diagnose (20M)** inbox jobs before verify (50M) / claim (100M+).  
- After shared fixes, still run fleet regression (`scoreboard.ps1` / `regression-matrix.ps1`) — the live queue is for **targeted concurrent walls**, not a full scoreboard replacement.  
- One owner per shared hot file in multi-agent waves ([`AGENT_SOP.md`](AGENT_SOP.md) §3).  
- Never commit BIOS/ISO, private paths, or root-level trace dumps; keep artifacts under `out/`.

---

## 9. Related docs

| Doc | Topic |
|-----|--------|
| [`tools/live-game-queue.ps1`](../tools/live-game-queue.ps1) | Implementation |
| [`TOOLING.md`](TOOLING.md) | Operator tool index |
| [`tools/SCOREBOARD_SCHEMA.md`](../tools/SCOREBOARD_SCHEMA.md) | Full Soft-GS / T0–T7 / G1–G4 schema |
| [`CORRECTNESS.md`](CORRECTNESS.md) | Correct-over-working doctrine |
| [`HOST_PRESENT.md`](HOST_PRESENT.md) | Host swap present contracts |
| [`GRAPHICS_PIPELINE_PHASE_PLAN.md`](GRAPHICS_PIPELINE_PHASE_PLAN.md) | G-GFX gates |
| [`AGENT_SOP.md`](AGENT_SOP.md) | Agent budgets and claim bar |
