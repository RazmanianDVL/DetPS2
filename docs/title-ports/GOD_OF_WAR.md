# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w10b` |
| **Branch** | `agent/menu-gow-w10b` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-10B residual:** disasm-proven streamObj magic + 0x27DBF0 loop bound + SavedPc pin to fill/follow; **MENU NO** — Soft-GS **px=0 gifPath3=0 FRAME_1=0** (Path3MaskedByVif held) |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-10B evidence (agent/menu-gow-w10b)

#### Attack findings (disasm, not host plant)

1. **Who writes FRAME_1?** Not via permanent soft-ok of `0x27DBF0`. Real stream-follow:
   - `0x27DBF0` loops `jal 0x27D7C8` while `cursor(+0x890) < *(slot+0x170)`.
   - Early leave when count at **slot+0x170** is 0 (not +0x38 — w9b seeded wrong field).
   - Shell decode → GIF FRAME+PRIM is **post** type-2 consumer (`0x26C2BC` magic path / VPK consumer), not the follow loop alone.
2. **Why pack→FRAME incomplete?** w9b `streamObj+0 = payload` failed magic check:
   - Disasm `0x26C2C4`: `beql *obj, 0x100` → healthy path; else poison table `0x2A1360` (UnknownSyscall thrash).
   - Payload/size live at **+4/+8**. Fixed via `RefreshLoadWadStreamTable` magic `0x100`.
3. **Minimal real path to SPRITE/TRI:** fill `0x27E0CC` → epi → `0x27DBF0` (empty-ready leave) → stream-poll `0x26C0EC` with magic streamObj → retail decode → Path2/3 FRAME+PRIM. Path3MaskedByVif **held**.
4. **gifPath2 @ 1082:** DISPFB1+SCISSOR only; FRAME_1=0 TEST=0 XYOFFSET=0 prims=0 — setup, no XYZ kick. Soft-GS cannot paint without PRIM (not a FRAME plant).

#### Claim 100M (SEMA_STALL_YIELD OFF) — Soft-GS

```
@100M: px=0 prims=0 gifPath2=1082 gifPath3=0 dmac=28 spu2Samples=32552
       cdvdSectors=1202 (full R_SHELL+TIT1 host)
       type-2: force fill 0x27E0CC @38.65M (SavedPc pin; toPc=0x27E0CC real)
               epi-hold → toPc=0x27DBF0; complete @39.85M dbf0Seen=True
       R_SHELL.WAD full 0xBAA95 @0x01E00000 Fedo magic OK; streamObj magic=0x100
       softgs-regs: FRAME_1=0 DISPFB1=0x800005090D0 SCISSOR full XYOFFSET=0 TEST=0
       prims=0 fragTest=0 — Path2 setup only
       post-type2 *0x310384: cmd=0 (no invented type-3/4)
       Path3MaskedByVif + high-TADR END held
```

Wave-10B assist changes:

1. **Restore w9b hang-guard base** (main merge of w9 had re-soft-ok'd `0x27DBF0`).
2. **streamObj magic 0x100** + payload@+4 size@+8 + budget `0x2A1360` (`RefreshLoadWadStreamTable`).
3. **slot+0x170=0** (real 0x27DBF0 loop bound) + soft-ok `0x27D7C8` (`sw 0,0(a2)`).
4. **Force fill 0x27E0CC with SavedPc pin** (claim: SwitchTo restored WaitSema; fill never ran).
5. **Epi-hold pins 0x27DBF0** after ≥400k fill slice; clear Sleeping/WaitSemaId.
6. Path3MaskedByVif **not** ungated; no invented type-3/4 / GIF / FRAME plant.

Rejected:

- Inventing PATH3 GIF / fake Soft-GS pixels / FRAME plant.
- Ungating Path3MaskedByVif.
- Force-post worker type-3/4.
- Permanent soft-ok of `0x27DBF0` (skips real follow).

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** (binds=11) |
| CDVD past IRX-only | **Yes** (cdvd=1202) |
| Worker type-2 success | **Yes** (fill pin + dbf0Seen) |
| Real 0x27E0CC fill PC | **Yes** (SavedPc pin) |
| Real 0x27DBF0 entry | **Yes** (epi-hold toPc) |
| streamObj magic 0x100 | **Yes** |
| Post-type-2 *0x310384 next cmd | **No** |
| **gifPath3** | **No** (gifPath2=1082) |
| Soft-GS px>0 | **No** (FRAME_1=0, prims=0) |
| Interactive title surface | **No** |

### Wall / next

1. **Shell decode → GIF FRAME+PRIM** after magic streamObj is healthy — retail consumer still does not kick XYZ (prims=0).
2. **No post-type-2 worker cmds** (`*0x310384=0`) — poster thread idle; do not invent type-3/4.
3. Path2 setup-only (DISPFB+SCISSOR); Soft-GS truth: no paint without PRIM.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w10b
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w10b/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
