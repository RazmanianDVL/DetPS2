# Whiplash (USA) — residual + draw-graph charter (S7 / PL-005 · GX-008)

| Field | Value |
|-------|--------|
| **Title** | Whiplash (USA) |
| **user-media id** | `whiplash` |
| **Serial / BOOT2** | `SLUS_206.84` |
| **ISO** | `user-media-whiplash.json` → operator ISO (never commit path) |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-whiplash.json` |
| **Seat / branch** | **S7 STREAM** (secondary queue) · `agent/seat-s7/s1-g1` |
| **Build** | `out/seat-s7` |
| **Assist** | `WhiplashAssist.cs` (owned) + shared GOE in `RealSifRpc.cs` |
| **Status** | **MENU YES** + **pad inject live** (PL-018) — T2 INTERACTIVE residual (no state/prim advance @100M) |
| **Last updated** | 2026-07-31 |
| **WP** | PL-018 pad title-surface → T2; residual **texture ring + ofx expand** |

### MENU gate

**title-surface Soft-GS** = full Soft-GS FB chrome after firstscreen/Code/frontend GOE Start.
Not MK MAINMENU language.

---

## Claim 100M (SEMA_STALL_YIELD OFF) — S1 PL-018 pad · 2026-07-31 seat-s7

```
@100M: PC=0x00314F80  px=286720  prims=1  gifPath1=0  gifPath2=0  gifPath3=2  dmac=26
       sifBytes=78924 syscalls=3477 cdvdSectors=1904 exitRequested=False
       softgs: imgBytes=0 dispfbPx=0 fragTest=286720 rejBounds=0 rejScissor=0 rejDepth=0 rejAlpha=0
       softgs-regs: FRAME_1=0x100000 DISPFB1=0
                    SCISSOR=0x03FF000003FF0000 XYOFFSET=0x80008000 TEST=0x30002
       softgs-writes: total=12 PRIM=1 XYZ2=0 XYZ3=0 XYZF2=2 FRAME=1 SCISSOR=1 TEST=1 XYOFF=1
       gif-pkts: completed=3 aborted=0 tags=3 p2qws=0
       RealSifRpc: binds=13 calls=571 unknownBindSids=0
       pad: inject ≥1536 START/CROSS edges + ForceRefreshPad post PADMAN OPEN
       spine: UsingCD + IOPRP255 retail · GOE IOPFILE 0x31/0x40 · WaitSema WHIP-gated only
```

**CLAIM LINE (Whip / PL-018):**  
`Whip SEMA_OFF @100M MENU hold px=286720 prims=1 ofx=0x8000 cdvd=1904 | pad inject live (≥1536) | T2 INTERACTIVE residual (no PC/prim delta vs S0; WaitSema WHIP-only)`

Trace: `out/traces/whiplash-claim100-pad-20260731-090439-{out,err}.txt`  
S0 baseline (no pad): `out/traces/whiplash-claim100-20260731-085231-{out,err}.txt`  
Matches wave-6 title-surface class: **px=286720** = 640×448 full Soft-GS FB; ofx/ofy=`0x8000`.

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
| FlushCache ra=0 rescue + tid1 revive | **Yes** |
| SN PreferSnFileIo + IOPFILE 0x31/0x40 | **Yes** |
| PS2.RKV surface + stream-table paint | **Yes** (shared RealSifRpc) |
| GOE Open+Start firstscreen/Code/frontend | **Yes** (bridge + multi-chunk Start) |
| Progressive texture **ring** fill EE `0x45BC94` | **Partial** (served in wave-6; richer ring residual) |
| Soft-GS title surface | **Yes** (**px=286720** full FB) |
| Natural Path2 multi-prim / IMAGE tex | **No** (prims=1, imgBytes=0, gifPath2=0) |
| DISPFB present | **No** (dispfbPx=0) |
| Pad inject START/CROSS (PL-018) | **Yes** (≥1536 pulses @100M; ForceRefreshPad; post PADMAN OPEN) |
| T2 INTERACTIVE (state/prim delta) | **Residual** (same PC/prims as S0; expand strip + texture ring) |

---

## Draw-graph charter (menu / title-surface)

What Soft-GS actually sees at MENU claim (for S8/S9/S10 handoff):

| Path | Count / signal | Notes |
|------|----------------|-------|
| **Path1 (VU1)** | gifPath1=**0** | None |
| **Path2 (VIF1/DIRECT)** | gifPath2=**0**, p2qws=**0** | No Path2 tags at claim |
| **Path3** | gifPath3=**2** | Minimal PATH3 traffic |
| **PRIM/XYZ** | PRIM=**1**, XYZF2=**2**, XYZ2=**0** | Single title sprite class |
| **IMAGE / TEX** | imgBytes=**0** | No GIF IMAGE BITBLT; textures via GOE ring residual |
| **DISPFB / PCRTC** | dispfbPx=**0**, DISPFB1=**0** | Composite/present unset — S10 |
| **FRAME / XYOFFSET** | FRAME_1=`0x100000`, ofx=ofy=**`0x8000`** | Classic retail 2048.0 origin |
| **Expand policy** | **ofx expand hit** | Degenerate Y=0 full-width strip → 640×448 Soft-GS FB (`Gs.DrawSprite` titleStrip) |
| **Rejects** | all **0** | Expand lands fully on FB |

**Draw class:** **ofx=0x8000 single-strip expand** title surface (same family as BO2 WAVE-7 / related GoW expand policy). Color/UV from real prim — not invent PATH3 / not host pixels.

**Natural-draw gap (P3 / G-GFX-5/6):** expandHits-class; demote only with S9 review (PL-042 / GX-043). Do not invent multi-prim chrome.

### Expected next draw graphs

1. **Texture ring complete** — progressive fill from GOE Start into EE ring (`0x45BC94`) → TEXTURE sample / imgBytes>0 (PL-033 / G2).
2. Natural multi-prim Path2 when ofx expand demoted and retail XYOFFSET/PRIM sizes correct (S9).
3. DISPFB/DISPLAY arm for present (S10).
4. Pad accept → frontend chrome change (PL-018).

---

## Residual truth (S0 baseline) — **texture ring + ofx expand**

### 1. Texture ring (primary T3 residual)

| Item | State |
|------|--------|
| GOE Open firstscreen / Code / frontend | Working (shared BridgeWhipGoeOpenStart) |
| Multi-chunk Start (≤1.5 MiB class) | Working (wave-6 bytesStarted class ~2 MiB total) |
| Stream-table FULL paint | Working (ends infinite w2 walk) |
| Progressive ring into `0x45BC94` | **Residual** — partial ringBytesServed; not full texture residency for rich chrome |
| GIF IMAGE / TEX0 sample | **imgBytes=0** — Soft-GS never samples ring as textured prims yet |

Backlog: **PL-033** full texture ring path; handoff textured sample to S9 (G-GFX-3/4).

### 2. ofx expand (primary T4 / G-GFX residual)

| Item | State |
|------|--------|
| Live sprite | ofx/ofy=`0x8000`, raw Y≈0 both corners → 1-row strip without expand |
| Soft-GS rescue | `titleStrip` expand → full 640×448 (**px=286720**) |
| Natural retail draw | **Not yet** — prims stays 1; no multi-prim frontend |
| Policy | Expand legal for MENU claim; **demote** under PL-042 / GX-043 with S9 (require thin strip **and** !retailOfx evidence) |

### 3. WaitSema (title-local freeze)

| Item | Rule |
|------|------|
| WHIP_SEMA_FIX_V3 | **Whiplash-gated only** in `SonyKernelHle` |
| Global WaitSema fabricate | **FORBIDDEN** (seat freeze) |
| Assist pulses | Post-IOPRP255 SignalSema for sleepers until real cdvd≥50 |

### Working spine (do not regress)

1. UsingCD EE patches → `cdrom0:` / `/whiplash/bin/`
2. IOPRP255 plant `"2550"` + retail UDNL arg rewrite
3. FlushCache JREXIT ra=0 rescue @ `0x400000` → `0x24D8F4`
4. PreferSnFileIo + IOPFILE SIDs 0x31/0x40
5. Stream-table paint + GOE Open+Start bridge (shared)
6. Soft-GS ofx title-band expand (shared Gs)

---

## Assists (current)

**Title-local (`WhiplashAssist`):**

- UsingCD force + IOPRP255 version cells
- Reboot arg host→cdrom rewrite
- FlushCache/JREXIT rescue + data-thrash escape
- Post-reboot WaitSema pulse (title) + PS2.RKV / title-name warm tokens
- **PL-018** dense START/CROSS/Circle/D-pad inject + `ForceRefreshPad` post-PADMAN (no global WaitSema)

**Shared (`RealSifRpc` / `SonyKernelHle` / `Gs`) — do not edit without ownership:**

- IOPFILE 0x31/0x40 GOE stream-table + BridgeWhipGoeOpenStart + ring service
- WHIP_SEMA_FIX_V3 fabricate (serial-gated)
- Gs ofx titleStrip expand

## Debt class

`WhiplashAssist` TITLE · GOE ring shared DEBT · ofx expand shared GFX (S9) · WHIP WaitSema stays WHIP · pad live but frontend not consuming edges yet.

## Next WPs (seat S7 secondary)

| WP | Goal |
|----|------|
| PL-018 | **Done (pad live)** — T2 state advance residual; WHIP WaitSema only |
| PL-033 | Full texture ring path |
| PL-042 | Expand demotion attempt (S9 co-review) |
| PL-062 | Start run / first gameplay |

## Related

- Scoreboard row: [`SCOREBOARD.md`](SCOREBOARD.md)
- Plan: [`POST_MENU_PHASE_PLAN.md`](../POST_MENU_PHASE_PLAN.md) S7 · [`GRAPHICS_PIPELINE_PHASE_PLAN.md`](../GRAPHICS_PIPELINE_PHASE_PLAN.md) GX-008 / GX-043
- Issue family: #17 GOE Open / stream
- Freezes: Soft-GS truth · SEMA_OFF · **no global WaitSema fabricate**
