# Whiplash (USA) — residual + draw-graph charter (S7 / PL-005 · GX-008)

| Field | Value |
|-------|--------|
| **Title** | Whiplash (USA) |
| **user-media id** | `whiplash` |
| **Serial / BOOT2** | `SLUS_206.84` |
| **ISO** | `user-media-whiplash.json` → operator ISO (never commit path) |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-whiplash.json` |
| **Seat / branch** | **S7 STREAM** (secondary) · `agent/seat-s7/s2-g2` |
| **Build** | `out/seat-s7` |
| **Assist** | `WhiplashAssist.cs` (owned) + shared GOE in `RealSifRpc.cs` |
| **Status** | **MENU YES** + **PL-033 ring fill live** — px/prims↑ @100M; pad inject live |
| **Last updated** | 2026-07-31 |
| **WP** | PL-033 full texture ring path toward richer Soft-GS |

### MENU gate

**title-surface Soft-GS** = full Soft-GS FB chrome after firstscreen/Code/frontend GOE Start.
Not MK MAINMENU language.

---

## Claim 100M (SEMA_STALL_YIELD OFF) — S2 PL-033 · 2026-07-31 seat-s7

```
@100M: PC=0x0035A254  px=573440  prims=2  gifPath1=0  gifPath2=0  gifPath3=4  dmac=38
       sifBytes=48208 syscalls=3170 cdvdSectors=2547 exitRequested=False
       softgs: imgBytes=0 dispfbPx=0 expandHits=2 fragTest=573440
       softgs-regs: FRAME_1=0x100000 DISPFB1=0
                    SCISSOR=0x03FF000003FF0000 XYOFFSET=0x80008000 TEST=0x30002
       softgs-writes: total=24 PRIM=2 XYZ2=0 XYZF2=4 FRAME=2 SCISSOR=2 TEST=2 XYOFF=2
       gif-pkts: completed=6 aborted=0 tags=6 p2qws=0
       RealSifRpc: binds=13 calls=… unknownBindSids=0
       pad: inject ≥1536 START/CROSS edges + ForceRefreshPad post PADMAN OPEN
       ring: assist fill firstscreen+frontend → EE 0x45BC94 (~780 KiB total) from GOE Start dump
       spine: UsingCD + IOPRP255 · GOE IOPFILE 0x31/0x40 · WaitSema WHIP-gated only
```

**CLAIM LINE (Whip / PL-033):**  
`Whip SEMA_OFF @100M MENU hold px=573440 prims=2 gifP3=4 expandHits=2 cdvd=2547 | ring fill ~780KiB→0x45BC94 | pad inject live (≥1536) | T3 partial (px↑ prims↑ vs S1 px=286720 prims=1; imgBytes=0 residual)`

Trace: `out/traces/whiplash-claim100-pl033-20260731-094200-{out,err}.txt`  
S1 baseline: px=286720 prims=1 imgBytes=0 PC=0x314F80

Reproduce:

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/seat-s7
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_WHIP='1'
dotnet exec out/seat-s7/DetPS2.Core.dll blocker-trace user-media-whiplash.json --cycles=100000000 --host-present
```

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF `SLUS_206.84` | **Yes** |
| UsingCD force + IOPRP255 retail reboot | **Yes** |
| FlushCache ra=0 rescue + tid1 revive | **Yes** (optional on tip) |
| SN PreferSnFileIo + IOPFILE 0x31/0x40 | **Yes** |
| PS2.RKV surface + stream-table paint | **Yes** (shared RealSifRpc) |
| GOE Open+Start firstscreen/Code/frontend | **Yes** |
| Progressive texture **ring** fill EE `0x45BC94` | **Yes↑** (assist PL-033 ~780 KiB from GOE dump) |
| Soft-GS title surface | **Yes** (**px=573440** = 2× full-FB class) |
| Natural Path2 multi-prim / IMAGE tex | **Partial** (prims=**2**, imgBytes=**0**, gifPath2=0) |
| DISPFB present | **No** (dispfbPx=0) |
| Pad inject START/CROSS (PL-018) | **Yes** (≥1536) |
| T2 INTERACTIVE | **Residual** (PC moved 0x314F80→0x35A254; no frontend accept yet) |

---

## Draw-graph charter (menu / title-surface)

| Path | Count / signal | Notes |
|------|----------------|-------|
| **Path1 (VU1)** | gifPath1=**0** | None |
| **Path2 (VIF1/DIRECT)** | gifPath2=**0** | No Path2 tags at claim |
| **Path3** | gifPath3=**4** | 2× S1 PATH3 |
| **PRIM/XYZ** | prims=**2**, XYZF2=**4** | Second title sprite / expand |
| **IMAGE / TEX** | imgBytes=**0** | Ring resident but Soft-GS IMAGE residual (S9) |
| **DISPFB / PCRTC** | dispfbPx=**0** | S10 |
| **FRAME / XYOFFSET** | ofx=ofy=**`0x8000`** | Classic retail origin |
| **Expand policy** | expandHits=**2** | Dual full-FB title strips |
| **Rejects** | all **0** | Expand lands on FB |

**Draw class:** ofx=0x8000 expand title surface (doubled). Ring bytes honest from GOE Start dump — not invent PATH3.

### Expected next draw graphs

1. Soft-GS IMAGE sample of ring → imgBytes>0 (S9 G-GFX-3/4).
2. Natural multi-prim Path2 when expand demoted (PL-042 / S9).
3. DISPFB arm (S10).
4. Pad accept → frontend chrome change.

---

## Residual truth — **texture ring + ofx expand**

### 1. Texture ring (PL-033 progress)

| Item | State |
|------|--------|
| GOE Open firstscreen / Code / frontend | Working (shared BridgeWhipGoeOpenStart) |
| Multi-chunk Start (≤1.5 MiB class) | Working |
| Stream-table FULL paint | Working |
| Progressive ring into `0x45BC94` | **Yes** — assist copies GOE high-RDRAM dump (0x1C00000+stride) → ring |
| GIF IMAGE / TEX0 sample | **imgBytes=0** — Soft-GS never samples ring as textured prims yet (S9) |

### 2. ofx expand (G-GFX residual)

| Item | State |
|------|--------|
| Live sprite | ofx/ofy=`0x8000` → expand full 640×448 |
| Soft-GS | **px=573440** (2× expandHits) |
| Natural retail draw | **Not yet** — prims=2 expand class |
| Policy | Expand legal for MENU; demote under PL-042 / S9 |

### 3. WaitSema (title-local freeze)

| Item | Rule |
|------|------|
| WHIP_SEMA_FIX_V3 | **Whiplash-gated only** |
| Global WaitSema fabricate | **FORBIDDEN** |

### Working spine (do not regress)

1. UsingCD EE patches → `cdrom0:` / `/whiplash/bin/`
2. IOPRP255 plant `"2550"` + retail UDNL arg rewrite
3. FlushCache JREXIT rescue (when hit) + post-reboot RKV warm without requiring rescue
4. PreferSnFileIo + IOPFILE SIDs 0x31/0x40
5. Stream-table paint + GOE Open+Start bridge (shared)
6. Assist ring fill from GOE Start dump → `0x45BC94`
7. Soft-GS ofx title-band expand (shared Gs)

---

## Assists (current)

**Title-local (`WhiplashAssist`):**

- UsingCD force + IOPRP255 version cells
- Reboot arg host→cdrom rewrite
- FlushCache/JREXIT rescue + data-thrash escape
- Post-reboot WaitSema pulse (title) + PS2.RKV warm
- **PL-033** progressive ring fill EE `0x45BC94` from GOE dump
- **PL-018** dense pad inject + ForceRefreshPad (no global WaitSema)

**Shared (`RealSifRpc` / `SonyKernelHle` / `Gs`) — do not edit without ownership:**

- IOPFILE 0x31/0x40 GOE stream-table + BridgeWhipGoeOpenStart + ring service
- WHIP_SEMA_FIX_V3 fabricate (serial-gated)
- Gs ofx titleStrip expand

## Debt class

`WhiplashAssist` TITLE · imgBytes=0 IMAGE sample (S9) · ofx expand demotion (S9) · WHIP WaitSema stays WHIP

## Next WPs (seat S7 secondary)

| WP | Goal |
|----|------|
| PL-018 | **Done (pad live)** |
| PL-033 | **Partial** — ring fill + px/prims↑; imgBytes=0 residual |
| PL-042 | Expand demotion attempt (S9 co-review) |
| PL-062 | Start run / first gameplay |

## Related

- Scoreboard row: [`SCOREBOARD.md`](SCOREBOARD.md)
- Plan: [`POST_MENU_PHASE_PLAN.md`](../POST_MENU_PHASE_PLAN.md) S7 · [`GRAPHICS_PIPELINE_PHASE_PLAN.md`](../GRAPHICS_PIPELINE_PHASE_PLAN.md) GX-008 / GX-043
- Issue family: #17 GOE Open / stream
- Freezes: Soft-GS truth · SEMA_OFF · **no global WaitSema fabricate**
