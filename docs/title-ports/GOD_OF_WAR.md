# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w10` |
| **Branch** | `agent/menu-gow-w10` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-10 residual:** real 0x27DBF0 hang-escape + no 989snd stomp of LoadWad streamObj; GIF DMA tag builders (0x13F5xx) allowed; **MENU NO** — Soft-GS **px=0 gifPath3=0 FRAME_1=0** (Path3MaskedByVif held; shell decode→FRAME+PRIM wall) |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-10 evidence (agent/menu-gow-w10)

#### Claim 100M (SEMA_STALL_YIELD OFF) — Soft-GS

```
@100M: PC=0x17ED70 px=0 gifPath2=1082 gifPath3=0 dmac=28 spu2Samples=32552
       cdvdSectors=1202 (full R_SHELL+TIT1 host × pre-type2)
       type-2: force 0x27DBF0 @40.0M → hang-escape n=1 @40.55M → complete success
               dbf0Esc=1 shellOk=True resWas=0 (no permanent soft-ok plant)
       R_SHELL.WAD full 0xBAA95 @0x01E00000 Fedo magic 0x4665646F OK (pre-type2)
       TIT1E1_2.VPK @0x01D00000; LoadWad bind seed shellOk=True *obj=0x100 magic
       softgs-regs: FRAME_1=0 DISPFB1=0x800005090D0 SCISSOR full XYOFFSET=0 TEST=0
       prims=0 fragTest=0 — Path2 setup only (no XYZ kick / no FRAME write)
       *0x310384 posts after type-2: none (cmd stays 0; no invented type-3/4)
       989snd done-magic: refused on streamObj/Fedo (pending paints real recv only)
       threads all Started; final PC list-walk 0x17EDxx (not mid-pack 0x26C3A4)
       Path3MaskedByVif + high-TADR END held
```

Wave-10 assist changes:

1. **Disasm 0x26C3A4** = stream-work mid-pack **byte-copy** (not GIF). Size gate s0&lt;513; *obj is counter starting at magic 0x100, incremented by 0x26C478.
2. **Stop rehoming 0x13F540..0x13F6A8** as thrash — retail GIF/VIF **DMA tag builder** (QWC patch + END 0x70000000). Prior "sleep-cmd" escape killed FRAME chain finalize.
3. **Do not RefreshLoadWadStreamTable mid pack-producer** 0x26C150..0x26C470 (was resetting *obj counter → re-entry thrash).
4. **Refuse 989snd done-magic** on LoadWad streamObj / host Fedo/TIT1 windows (w9b regress restored).
5. **Real 0x27DBF0 force-entry** with a1/s6 status (not 0x27E234 with s6=0 which **skips** jal 0x27DBF0) + hang-escape after 50k → post-jal 0x27E258.
6. **No invent type-3/4**; post-type2 SignalSema wake only. Retail posters at 0x27C4xx still never re-arm *0x310384.
7. Path3MaskedByVif **held**.

Rejected:

- Force-post worker type-3/4 (w7 thrash).
- Inventing PATH3 GIF packets / fake Soft-GS pixels / FRAME plant.
- Ungating Path3MaskedByVif.
- Permanent soft-ok 0x27DBF0 at gate plant (skips real DMA arm).
- Painting 989snd done-magic onto streamObj 0x01CFE000.

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** (binds=10) |
| CDVD IRX-only 142 | **Broken** → **cdvd=1202** |
| Worker cmd type=2 soft-success | **Yes** (force dbf0 + hang-escape) |
| PART1.PAK / TOC FILEIO open | **Yes** (pre+post type-2) |
| R_SHELL Fedo host extract (full) | **Yes** (`shellOk=True`) |
| LoadWad bind seed (*obj=0x100) | **Yes** (not stomped by 989snd) |
| Real 0x27DBF0 entry + hang-escape | **Yes** (dbf0Esc=1) |
| GIF DMA tag builders 0x13F5xx live | **Yes** (post-type2 PC samples) |
| Post-type-2 *0x310384 next cmd | **No** (cmd=0 forever) |
| **gifPath3** | **No** (gifPath2=1082) |
| Soft-GS px>0 | **No** (FRAME_1=0, prims=0) |
| Interactive title surface | **No** |

### Wall / next

1. **Shell decode → FRAME+PRIM:** Fedo R_SHELL is compressed (no raw A+D FRAME in host buffer). Hang-escape leaves follow without full DMA graph arm. Need natural consumer past 0x27D7C8 / LoadWad('R_Shell') expand path.
2. **Post-type-2 cmd posters** at 0x27C4xx..0x27C8xx (type 3/5/6/7) require empty queue + SignalSema — queue stays 0; main list-walk residual 0x17EDxx never reaches posters.
3. **Path2 @ gifP2=1082:** DISPFB1 + SCISSOR set; **FRAME_1=0**, **TEST=0**, **XYOFFSET=0**, **prims=0** — setup only. Soft-GS cannot paint.
4. Path3MaskedByVif held — do not ungate as MENU shortcut.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w10
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w10/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
