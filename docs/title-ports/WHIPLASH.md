# Whiplash (USA) — residual + draw-graph charter (S7 / PL-005 · GX-008)

| Field | Value |
|-------|--------|
| **Title** | Whiplash (USA) |
| **user-media id** | `whiplash` |
| **Serial / BOOT2** | `SLUS_206.84` |
| **ISO** | `user-media-whiplash.json` → operator ISO (never commit path) |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-whiplash.json` |
| **Seat / branch** | **MENU-WHIP-2** · tip `detps2` (WhiplashAssist owned) |
| **Build** | `src/DetPS2.Core/bin/Release/net9.0` (live-queue) |
| **Assist** | `WhiplashAssist.cs` (owned) + shared GOE in `RealSifRpc.cs` |
| **Status** | Soft-GS lit>0 + natural present DISPFB — **MENU-WHIP-2 Host→Local residual** title chrome @100M (**not natural MENU YES**). LIVE re-verify lit=5189 imgBytes=262144 |
| **Last updated** | 2026-07-31 residual honesty |
| **WP** | MENU-WHIP-2 Host→Local residual (done) · natural EE GIF IMAGE residual |

### MENU gate

**title-surface Soft-GS** = full Soft-GS FB chrome after firstscreen/Code/frontend GOE Start.
Not MK MAINMENU language. **lit>0** required for host-present proof (px alone ≠ chrome).

---

## Claim 100M (SEMA_STALL_YIELD OFF) — MENU-WHIP-2 · 2026-07-31 live-queue

**Baseline (SliceSize=64, pre-fix):** `slice64-whip-20m-host` px=286720 lit=**0** prims=1 imgBytes=0 expandHits=1 mostlyBlack=1

```
@100M: PC=0x0035A254  px=610373  prims=4  gifPath1=0  gifPath2=0  gifPath3=4  dmac=38
       sifBytes=48208 syscalls=3170 cdvdSectors=2547 exitRequested=False
       softgs: imgBytes=262144 dispfbPx=36933 naturalDispfbPx=36933 residual=0
               compositeSource=NaturalDispfb expandHits=2 fragTest=573440
       softgs-present: lit=5189/286720 s0=0xFF000000 mostlyBlack=0
       softgs-regs: FRAME_1=0x100000 DISPFB1=0 DISPFB2=0x9000
                    SCISSOR=0x03FF000003FF0000 XYOFFSET=0x80008000 TEST=0x30002
       softgs-circuit: pmode=0x66 circ=2 FBP=0 FBW=512 PSM=1 out=512x448
       gif-pkts: completed=6 aborted=0 tags=6 p2qws=0
       RealSifRpc: binds=13
       chrome: Host→Local GOE firstscreen (256 KiB) → Soft-GS IMAGE + ForceRefreshPresentComposite
       spine: UsingCD + IOPRP255 · GOE IOPFILE 0x31/0x40 · WaitSema WHIP-gated only
```

**CLAIM LINE (Whip / MENU-WHIP-2):**  
`Whip SEMA_OFF @100M lit=5189/286720 mostlyBlack=0 px=610373 prims=4 gifP3=4 imgBytes=262144 dispfbPx=36933 natural | Host→Local firstscreen 256KiB | vs Slice64 baseline px=286720 lit=0 imgBytes=0`

Live job: `out/live-queue/done/menu-whip-2-100m-host-20260731-131039.json`  
Trace: `out/traces/whiplash-claim100-menu-whip2-20260731-131039-{out,err}.txt`  
PL-033 prior (lit residual): px=573440 prims=2 imgBytes=0

Reproduce:

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release
# via live-queue:
@{ id="menu-whip-2-100m"; media="user-media-whiplash.json"; cycles=100000000; hostPresent=$true; priority=0 } |
  ConvertTo-Json | Set-Content out/live-queue/inbox/menu-whip-2-100m.json -Encoding utf8
# or direct:
dotnet exec src/DetPS2.Core/bin/Release/net9.0/DetPS2.Core.dll blocker-trace user-media-whiplash.json --cycles=100000000 --host-present
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
| Progressive texture **ring** fill EE `0x45BC94` | **Yes** (PL-033) |
| Soft-GS title surface px | **Yes** (**px=610373**, expandHits=2) |
| Soft-GS **lit** present | **Yes** (**lit=5189/286720**, mostlyBlack=0) |
| Host→Local IMAGE (MENU-WHIP-2) | **Yes** (**imgBytes=262144**) |
| Natural DISPFB composite | **Yes** (dispfbPx=**36933**, src=NaturalDispfb) |
| Natural Path2 multi-prim / EE GIF IMAGE | **Partial** (gifPath2=0; assist IMAGE residual) |
| Pad inject START/CROSS (PL-018) | **Yes** |
| T2 INTERACTIVE | **Residual** (PC=0x35A254; no frontend accept yet) |

---

## Draw-graph charter (menu / title-surface)

| Path | Count / signal | Notes |
|------|----------------|-------|
| **Path1 (VU1)** | gifPath1=**0** | None |
| **Path2 (VIF1/DIRECT)** | gifPath2=**0** | No Path2 tags at claim |
| **Path3** | gifPath3=**4** | PATH3 packed tags |
| **PRIM/XYZ** | prims=**4**, XYZF2=**4** | Title sprites + expand |
| **IMAGE / TEX** | imgBytes=**262144** | MENU-WHIP-2 Host→Local GOE firstscreen (not EE GIF IMAGE) |
| **DISPFB / PCRTC** | dispfbPx=**36933** natural | NaturalDispfb composite → lit |
| **Present lit** | lit=**5189**/286720 | mostlyBlack=0 (host-present proof) |
| **FRAME / XYOFFSET** | ofx=ofy=**`0x8000`** | Classic retail origin |
| **Expand policy** | expandHits=**2** | Dual full-FB title strips |
| **Rejects** | all **0** | Expand lands on FB |

**Draw class:** ofx=0x8000 expand title surface + Host→Local firstscreen IMAGE under natural DISPFB.
Honest GOE Start dump bytes — not invent PATH3 / not synthetic chrome color.

### Expected next draw graphs

1. ~~Soft-GS IMAGE sample → imgBytes>0~~ **Done (assist Host→Local)**.
2. Natural EE GIF IMAGE / multi-prim Path2 when expand demoted (PL-042 / S9).
3. Richer lit fraction (lit still sparse vs full chrome).
4. Pad accept → frontend chrome change.

---

## Residual truth — **texture ring + ofx expand + lit chrome**

### 1. Texture ring (PL-033) + MENU-WHIP-2 IMAGE

| Item | State |
|------|--------|
| GOE Open firstscreen / Code / frontend | Working (shared BridgeWhipGoeOpenStart) |
| Multi-chunk Start (≤1.5 MiB class) | Working |
| Stream-table FULL paint | Working |
| Progressive ring into `0x45BC94` | **Yes** — assist copies GOE high-RDRAM dump → ring |
| Host→Local firstscreen → Soft-GS IMAGE | **Yes** — imgBytes=**262144**, lit=**5189** (MENU-WHIP-2) |
| Natural EE GIF IMAGE / TEX0 sample | **Residual** — gif image tags=0; assist residual only |

### 2. ofx expand (G-GFX residual)

| Item | State |
|------|--------|
| Live sprite | ofx/ofy=`0x8000` → expand full 640×448 |
| Soft-GS | **px=610373**, expandHits=2; black expand alone was lit=0 |
| Natural retail draw | **Not yet** — prims=4 expand class + assist IMAGE |
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
- **MENU-WHIP-2** Host→Local GOE firstscreen/frontend → Soft-GS IMAGE + OnHostPresent composite refresh

**Shared (`RealSifRpc` / `SonyKernelHle` / `Gs`) — do not edit without ownership:**

- IOPFILE 0x31/0x40 GOE stream-table + BridgeWhipGoeOpenStart + ring service
- WHIP_SEMA_FIX_V3 fabricate (serial-gated)
- Gs ofx titleStrip expand

## Debt class

`WhiplashAssist` TITLE · sparse lit (assist IMAGE not full chrome) · natural EE GIF IMAGE residual · ofx expand demotion (S9) · WHIP WaitSema stays WHIP

## Next WPs

| WP | Goal |
|----|------|
| PL-018 | **Done (pad live)** |
| PL-033 | **Done** — ring fill + px/prims↑ |
| MENU-WHIP-2 | **Done** — lit=5189 imgBytes=262144 natural DISPFB @100M |
| PL-042 | Expand demotion / natural multi-prim (S9 co-review) |
| PL-062 | Start run / first gameplay |

## Related

- Scoreboard row: [`SCOREBOARD.md`](SCOREBOARD.md)
- Plan: [`POST_MENU_PHASE_PLAN.md`](../POST_MENU_PHASE_PLAN.md) S7 · [`GRAPHICS_PIPELINE_PHASE_PLAN.md`](../GRAPHICS_PIPELINE_PHASE_PLAN.md) GX-008 / GX-043
- Issue family: #17 GOE Open / stream
- Freezes: Soft-GS truth · SEMA_OFF · **no global WaitSema fabricate**
