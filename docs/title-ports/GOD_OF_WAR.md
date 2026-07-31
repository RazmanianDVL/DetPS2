# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w11c` |
| **Branch** | `agent/menu-gow-w11c` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-11C Soft-GS:** Path2 sticky reassembly + DIRECT QW pad + DIRECT supersede abort → **px=1026 prims=2 FRAME_1≠0** (Path2 truth). MENU NO — shell/PATH3 residual. |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-11C evidence (agent/menu-gow-w11c) — Soft-GS Path2

#### Root cause (why FRAME_1=0 / prims=0 at gifP2=1082)

1. **VIF1 QW-sliced Path2** delivered one QW per `ReceivePath2Data` → GIFtag consumed, PACKED body dropped (sticky reassembly fix).
2. **DIRECT mid-QW** started Path2 at `addr&0xF!=0` → garbage IMAGE/REGLIST tags (QW-align pad fix).
3. **First DIRECT IMM=0xBF0** at `0x46BE90` was non-GIF payload (`A90BB00D…`). Sticky REGLIST `nloop=12301` **swallowed later real PACKED A+D** at `0x3969xx` (`NLOOP=13 REGS=A+D`, FRAME/SCISSOR/TEST/XYOFFSET/XYZ2). Fix: abort incomplete GIF packet on new DIRECT / DIRECT-end truncate.

XYZ2 kick map already global (DA WAVE-5). **No invent PATH3.** Rejection counters: `fragTest=1026 rejBounds=0 rejScissor=0 rejDepth=0 rejAlpha=0` — paint not AFAIL/TEST blocked.

#### Claim 100M (SEMA_STALL_YIELD OFF) — Soft-GS

```
@100M: PC=0x13F5F8 px=1026 prims=2 gifPath2=19 gifPath3=0 p2qws=1082 dmac=28
       softgs: imgBytes=0 dispfbPx=0 fragTest=1026 rejBounds=0 rejScissor=0 rejDepth=0 rejAlpha=0
       softgs-regs: FRAME_1=0x80000 DISPFB1=0x800005090D0 SCISSOR=0x019F000001FF0000
                    XYOFFSET=0x730000007000 TEST=0x50000
       softgs-writes: total=1924 PRIM=1230 XYZ2=4 XYZ3=245 FRAME=13 SCISSOR=13 TEST=13 XYOFF=13
       gif-pkts: completed=18 aborted=1 spannedCalls=1 inFlight=False tags=19
       Path2: real PACKED A+D setup + XYZ2 kicks (no PATH3 invent)
       cdvdSectors=142 (IRX-only class this run; stream residual separate from Soft-GS px)
```

Wave-11C core changes (`Gif.cs` / `Vif.cs` / `Dmac.cs` / `Gs.cs`):

1. GIF sticky mid-packet reassembly across Path2 `Receive*` calls.
2. VIF DIRECT pad to next QW before Path2 feed.
3. New DIRECT / DIRECT-end aborts truncated sticky packet.
4. Dmac VIF segment → single `ProcessStream` (batch DIRECT).
5. Soft-GS reg-write + rejection + gif-pkts telemetry in blocker-trace.

Smokes: `Gif_Path2_QwSliced_PackedSprite_WritesPixels`, `Vif_Direct_MidQw_PadsBeforePath2`,
`Vif_Direct_Supersede_AbortsStickyGarbage`, `Dmac_Vif1EndAddr0_InlineDirectPath2` (FRAME assert).

### Wave-10 evidence (agent/menu-gow-w10) — residual before Path2 fix

```
@100M: px=0 gifPath2=1082 gifPath3=0 FRAME_1=0 prims=0 fragTest=0
       Path2 setup only (DISPFB+SCISSOR default) — QW-slice / sticky garbage (see w11c)
```

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** |
| Soft-GS Path2 FRAME+PRIM+XYZ2 | **Yes** (w11c) |
| Soft-GS **px>0** | **Yes** (**px=1026**) |
| gifPath3 / shell IMAGE | **No** (gifPath3=0) |
| Interactive title surface | **No** (MENU NO) |
| Full R_SHELL / type-2 stream | Residual (cdvd variance) |

### Wall / next

1. **Shell / PATH3 IMAGE** for title chrome — do not invent PATH3 packets.
2. **Post-type-2 cmd posters** / stream past IRX-only when cdvd stuck 142.
3. Path3MaskedByVif held unless game unmasks with real GIF PATH3.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w11c
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w11c/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
