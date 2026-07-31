# DetPS2 scoreboard / claim metrics schema

**Owners:** S10 GFX-DISPLAY (PL-001, GX-001, GX-002)  
**Truth:** Soft-GS only · SEMA_OFF for claims · heuristics ≠ MENU YES  
**Producers:**

| Source | Output |
|--------|--------|
| `detps2 scoreboard-metrics …` | JSON object per title (`--out=`) |
| `tools/scoreboard.ps1` | `out/traces/scoreboard-*.md` + `.json` |
| `detps2 blocker-trace …` | text lines incl. `claim:` scraper row |
| `tools/run-title.ps1` | per-title JSON from blocker-trace scrape |

---

## 1. Core Soft-GS fields (always)

| Field | Type | Meaning |
|-------|------|---------|
| `px` | long | Soft-GS `PixelsWritten` |
| `prims` | long | Soft-GS `PrimitivesDrawn` |
| `gifPath1` / `gifP1` | ulong | GIF Path1 transfers (VU1/XgKick) |
| `gifPath2` / `gifP2` | ulong | GIF Path2 transfers (VIF1 DIRECT) |
| `gifPath3` / `gifP3` | ulong | GIF Path3 transfers (DMAC GIF) |
| `imgBytes` | long | Host→local IMAGE/BITBLT bytes |
| `dispfbPx` | long | Pixels composited from DISPFB/FRAME local VRAM |
| `expandHits` | long | Title-strip ofx expand rescues (G4/T4 demotion target) |
| `gifCompleted` | ulong | Fully drained GIFtag packets (`Gif.PacketsCompleted`) |
| `gifAborted` | ulong | Aborted mid-packet GIFtags (`Gif.PacketsAborted`) |
| `dmac` | ulong | DMAC transfers completed |
| `cdvdSectors` / `cdvd` | long | CDVD sectors read |
| `syscalls` | long | EE HLE syscall count |
| `binds` / `calls` | ulong | RealSifRpc bind/call counts |
| `exitRequested` | bool | EE exited (run may look “alive” via IOP) |
| `pc` | string | EE PC hex |
| `serial` | string | Disc serial if known |
| `cycles` | ulong | Master cycles after run |

**Hooks (S8/S9 land refine semantics; fields already present):**

- `expandHits` — S9 PL-003/GX-004 policy for legal expand conditions  
- `gifCompleted` / `gifAborted` — S8 Path fidelity (G-GFX-1)

---

## 2. Play tiers T0–T7 (PL-001)

Heuristic codes emitted as strings. **`Y` / `Y?` / `NEAR?` / `N` / `?` / `WARN`**.

| Code | Name | Heuristic (current) | Formal claim needs |
|------|------|---------------------|--------------------|
| **T0** | Boot | Spine live (`cycles` advanced; not instant death) | ELF entry, no early Exit |
| **T1** | Menu | `px>0` and any gif path; `Y?` if px≫ + gifP3 | `menuKind` YES + claim budget |
| **T2** | Interactive | `?` until pad-inject mode (PL-002) | Pad changes sel/PC/prims |
| **T3** | Frontend | prims≥10 **or** imgBytes>0 **or** dispfbPx>0 **or** gifP3≥20 | Richer Soft-GS charter |
| **T4** | Natural | expandHits==0 and px>0 → `Y?` | No expand plant / assist PATH3 |
| **T5** | Gameplay | `?` stub | Scene change post–New Game |
| **T6** | Playable-slice | `?` stub | Deep budget + in-game pad |
| **T7** | IRX-honest | `?` stub | FILEIO/PAD via IRX telemetry |

`?` = not measured in this budget / needs another tool.  
`Y?` = metric-pass heuristic only — **not** a shipped claim.

---

## 3. GFX columns G1–G4 (PL-001 / graphics plan)

| Code | Name | Heuristic (current) | Gate |
|------|------|---------------------|------|
| **G1** | Path fidelity | any path or gifCompleted>0; WARN if aborted ≫ completed | G-GFX-1 |
| **G2** | Texture/IMAGE | imgBytes>0 → `Y` | G-GFX-3 |
| **G3** | DISPFB present | dispfbPx>0 → `Y` | G-GFX-5 |
| **G4** | Expand demotion | expandHits==0 and px>0 → `Y?` | G-GFX-6 |

---

## 4. PPM dump (GX-002)

```text
detps2 scoreboard-metrics <media> --cycles=N --dump-softgs=out/traces/softgs.ppm
detps2 blocker-trace <media> --cycles=N --dump-softgs=out/traces/softgs.ppm
```

- Writes **only when `px>0`** (no empty black claim file).
- Multi-title media: path becomes `stem-<titleId>.ppm`.
- JSON field: `dumpSoftGs` = path written or null.
- Helpers: `GsPipeline.DumpSoftGsIfDrawn`, `Pcrtc.DumpSoftGsIfDrawn`.

---

## 5. blocker-trace claim line

Always printed (scrapers / agents):

```text
claim: px=… prims=… gifP1=… gifP2=… gifP3=… imgBytes=… dispfbPx=… expandHits=… gifCompleted=… gifAborted=…
```

Also: `softgs: … expandHits=…` and `gif-pkts: completed=… aborted=…`.

---

## 6. scoreboard.ps1 markdown columns

| Title | Serial | Heur | T0 | T1 | T2 | T3 | T4 | T5 | T6 | T7 | G1 | G2 | G3 | G4 | PC | px | prims | gifP1 | gifP2 | gifP3 | img | dispfb | expand | dmac | cdvd | sec |

When using `-NativeMetrics`, tiers come from Core JSON.  
Fallback log-parse fills metrics it can find and recomputes the same heuristics in PowerShell.

---

## 7. Freezes

1. Soft-GS truth only — no FFmpeg / planted logos.  
2. Heuristic columns are **not** MENU YES / INTERACTIVE claims.  
3. `DETPS2_SEMA_STALL_YIELD` OFF for claim budgets.  
4. Prefer diagnose (20M) while iterating; claim (100M+) only when asserting.

See also: `docs/POST_MENU_PHASE_PLAN.md` §4, `docs/GRAPHICS_PIPELINE_PHASE_PLAN.md` GX-001/002, `docs/AGENT_SOP.md`.
