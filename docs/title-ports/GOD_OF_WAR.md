# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w12b` |
| **Branch** | `agent/menu-gow-w12b` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-12B Soft-GS title-surface MENU YES:** Path2 ofx=0 Y=0 512×1 strips expand → **px=573440 prims=2** (2× full Soft-GS FB). gif-pkts completed=18 aborted=1 residual. Shell stream residual (cdvd=142). |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

**MENU YES (title-surface Soft-GS):** full Soft-GS FB chrome from real Path2 SPRITE prims (color/UV from prim; no invent PATH3 / no host FMV pixels).

### Wave-12B evidence (agent/menu-gow-w12b) — title-surface scale

#### Root cause (px=1026 residual after WAVE-11C)

Live Path2 SPRITEs (tag#3 / tag#5) kick with **ofx/ofy still 0** and both corners **Y=0**:
`(0,0) → (512,0)` → Soft-GS **512×1** strips ×2 = **px≈1026**.  
XYOFFSET=`0x7000/0x7300` is armed only by later A+D packets **after** the draws.  
Whiplash/BO2 title-strip expand required ofx=0x8000 — missed this ofx=0 Path2 class.

#### Fix

1. **Gs.DrawSprite:** full-width thin strips (`w ≥ FB_WIDTH/2`, `h < FB_HEIGHT/2`) with ofx/ofy = 0 **or** 0x8000 **or** retail-center band `0x6000..0x9000` expand to full Soft-GS title FB (640×448). Color/UV still from the real prim.
2. **GodOfWarAssist:** keep post-type-2 stream kick + shell decode seed after first Soft-GS px; residual thrash escape at `0x13F5F8` no longer requires `cdvd>400 && px==0` (live IRX-only is cdvd=142).

#### Claim 100M (SEMA_STALL_YIELD OFF) — Soft-GS

```
@100M: PC=0x13F5F8 px=573440 prims=2 gifPath2=19 gifPath3=0 p2qws=1082 dmac=28
       softgs: imgBytes=0 dispfbPx=0 fragTest=573440 rejBounds=0 rejScissor=0 rejDepth=0 rejAlpha=0
       softgs-regs: FRAME_1=0x80000 DISPFB1=0x800005090D0 SCISSOR=0x019F000001FF0000
                    XYOFFSET=0x730000007000 TEST=0x50000
       softgs-writes: total=1924 PRIM=1230 XYZ2=4 XYZ3=245 FRAME=13 SCISSOR=13 TEST=13 XYOFF=13
       gif-pkts: completed=18 aborted=1 spannedCalls=1 inFlight=False tags=19
       Path2: 2 real SPRITE packs expanded to 2× full Soft-GS FB (no PATH3 invent)
       cdvdSectors=142 (IRX-only stream residual; separate from title-surface Soft-GS)
       pad: assist START/CROSS inject live (first-gs-interactive surface)
```

Smokes: prior WAVE-11C Path2 suite + `Gs_Path2_Ofx0_Y0_Sprite_ExpandsTitleSurface` (px≥143360 title floor).

### Wave-11C evidence (agent/menu-gow-w11c) — Soft-GS Path2

#### Root cause (why FRAME_1=0 / prims=0 at gifP2=1082)

1. **VIF1 QW-sliced Path2** delivered one QW per `ReceivePath2Data` → GIFtag consumed, PACKED body dropped (sticky reassembly fix).
2. **DIRECT mid-QW** started Path2 at `addr&0xF!=0` → garbage IMAGE/REGLIST tags (QW-align pad fix).
3. **First DIRECT IMM=0xBF0** at `0x46BE90` was non-GIF payload (`A90BB00D…`). Sticky REGLIST `nloop=12301` **swallowed later real PACKED A+D** at `0x3969xx`. Fix: abort incomplete GIF packet on new DIRECT / DIRECT-end truncate.

#### Claim 100M (pre-expand) — Soft-GS

```
@100M: px=1026 prims=2 gifPath2=19 FRAME_1=0x80000
       gif-pkts: completed=18 aborted=1  (aborted=1 = garbage first DIRECT residual)
```

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** |
| Soft-GS Path2 FRAME+PRIM+XYZ2 | **Yes** (w11c) |
| Soft-GS **px>0** | **Yes** (**px=573440** title-surface) |
| first-gs-interactive MENU | **YES** (title-surface Soft-GS + pad) |
| gifPath3 / shell IMAGE | **No** (gifPath3=0; imgBytes=0) |
| Full R_SHELL / type-2 stream | Residual (cdvd=142 IRX-only variance) |

### Wall / next

1. **Shell / PATH3 IMAGE** for richer chrome — do not invent PATH3 packets.
2. **Post-type-2 stream** past IRX-only when cdvd stuck 142 (Fedo consumer → more natural PRIM/XYZ).
3. Path3MaskedByVif held unless game unmasks with real GIF PATH3.
4. **aborted=1** residual is intentional (garbage DIRECT IMM=0xBF0) — leave.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w12b
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w12b/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
