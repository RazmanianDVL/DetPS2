# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w11b` |
| **Branch** | `agent/menu-gow-w11b` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-11B:** main post-type-2 poster path — restore W10 0x13F5xx protect; finish stuck DMA END tags; escape 0x26C288 size≥513 hang; refuse 989snd stomp of LoadWad table; **Soft-GS px=2 gifP3=2** first breakthrough. **MENU NO** residual (FRAME_1=0 dispfbPx=0; *0x310384 still no retail re-arm). Path3MaskedByVif held. |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-11B evidence (agent/menu-gow-w11b)

#### Claim 100M (SEMA_STALL_YIELD OFF) — Soft-GS

```
@100M: PC=0x26D3BC px=2 prims=3 gifPath2=1082 gifPath3=2 dmac=32 spu2Samples=32552
       cdvdSectors=1202 (full R_SHELL+TIT1 host × pre-type2)
       type-2: complete success @39.85M (dbf0Seen; dbf0Esc=0 this run)
       DMA tag finish n≥1 (cursor *0x32F168 was poison → END 0x70000000 scratch)
       stream size-hang escape 0x26C288 (s0≥513 intentional hang) → 0x26C2A4 healthy
       softgs: fragTest=2 rejBounds=1 imgBytes=0 dispfbPx=0
       softgs-regs: FRAME_1=0 DISPFB1=0x800000001400 SCISSOR full XYOFFSET=0 TEST=0
       *0x310384 posts after type-2: none (cmd=0; no invented type-3/4)
       989snd done-magic: refused on streamObj + table 0x2A1300..0x2A1400 + slots
       Path3MaskedByVif held — gifP3=2 natural (not ungated)
```

Wave-11B root cause (why main never reached posters after type-2):

1. **W10B merge regress:** rehomed 0x13F5xx DMA tag builders as thrash + always resumed 0x26C0EC (killed W10 poster-path alternate to 0x185FAC).
2. **Stuck DMA tag END** at 0x13F670 with poison `*0x32F168` — FRAME chain never finalized (60M cycles mid-align-pad).
3. **Stream size hang 0x26C288:** `slti s0,513` fails on host size 0xBAA95 / poison s0 → intentional `beq self` hang. W10 residual PC was 0x17ED70 (list-unlink); after DMA finish residual moved here.
4. **989snd done-magic** painted onto `0x2A1370` (stream table companion holding streamObj) — stomped LoadWad graph.
5. **List-unlink 0x17ED70** circular walk when links never hit sentinel (w10 final PC) — escape added.

Wave-11B assist changes:

1. Restore W10: **do not rehome 0x13F540..0x13F6A8**; alternate residual resume 0x185FAC / 0x26C0EC; no mid-pack Refresh 0x26C150..0x26C470.
2. **TryFinishDmaTagBuilder** — force END 0x70000000 + advance cursor when sticky/poison (not thrash rehome).
3. **Escape 0x26C288 size-hang** → s0=512 + 0x26C2A4 healthy pack path.
4. **TryFinishStreamPackCopy** at 0x26C3A4; **TryEscapeListUnlink** at 0x17ED28..80.
5. Poster gates: queue word 0 (not 0xFFFFFFFF); `*0x2A3310` non-zero; refuse 989snd paint on LoadWad table/slots/Fedo.
6. No invent type-3/4. Path3MaskedByVif **held**.

Rejected:

- Force-post worker type-3/4.
- Inventing PATH3 GIF packets / fake Soft-GS pixels / FRAME plant.
- Ungating Path3MaskedByVif.
- Rehoming healthy 0x13F5xx as sleep-cmd thrash.

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** |
| CDVD IRX-only 142 | **Broken** → **cdvd=1202** |
| Worker cmd type=2 soft-success | **Yes** |
| PART1.PAK / TOC FILEIO open | **Yes** |
| R_SHELL Fedo host extract (full) | **Yes** |
| LoadWad bind seed (*obj=0x100) | **Yes** |
| GIF DMA tag builders 0x13F5xx live | **Yes** (finish END when sticky) |
| Stream size-hang 0x26C288 leave | **Yes** |
| **gifPath3** | **Yes (2)** first |
| Soft-GS px>0 | **Yes (px=2)** first |
| Post-type-2 *0x310384 next cmd | **No** (cmd=0) |
| FRAME_1 / title surface | **No** (FRAME_1=0 dispfbPx=0) |
| Interactive title surface | **No** |

### Wall / next

1. **FRAME_1=0 / dispfb empty:** prims=3 fragTest=2 but no title framebuffer. Need natural FRAME+XYZ after pack producer past 0x26C2A4 / LoadWad expand.
2. **Post-type-2 cmd posters** at 0x27C4xx still never re-arm `*0x310384` (queue empty; main Exit residual).
3. Path3MaskedByVif held — do not ungate as MENU shortcut.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w11b
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w11b/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
