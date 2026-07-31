# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w8` |
| **Branch** | `agent/menu-gow-w8` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-8 residual:** type-2 forced success epilogue `0x27E234` + full Fedo **R_SHELL.WAD** host load (cdvd **692**); LoadWad bind seed; still **MENU NO** — Soft-GS **px=0 gifPath3=0** (FRAME_1=0; Path3MaskedByVif held) |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-8 evidence (agent/menu-gow-w8)

#### Claim 100M (SEMA_STALL_YIELD OFF) — Soft-GS

```
@100M: PC=0x28BFF0 px=0 gifPath2=1082 gifPath3=0 dmac=28 spu2Writes=512
       cdvdSectors=692 (was 555 w7 / 142 IRX-only)
       type-2 force epilogue 0x27E234 @38.25M → complete @38.6M (epi=True resWas=0)
       R_SHELL.WAD full 0xBAA95 @0x01E00000 Fedo magic 0x4665646F OK
       TIT1E1_2.VPK @0x01D00000; LoadWad bind + streamObj seeded
       Path3MaskedByVif + high-TADR END held
```

Wave-8 assist changes:

1. **Pre-type-2 FILEIO** open TOC+PART1 at gate plant (before soft-complete).
2. **Force type-2 success epilogue** `0x27E234` on worker + protect from sleep-cmd rewind 2M.
3. **0x27DBF0 soft-success stub** (`*a1=0`) so epilogue does not re-enter `0x8101002F`.
4. **Full R_SHELL** host extract (`maxBytes=0xC0000`, TOC size `0xBAA95`) — Fedo `0x4665646F` verified.
5. **LoadWad bind seed** — table `+0x800` payload ptrs, stream slot `*0x2A1358`, name scratch, `*0x2AC7D0` kick flag.
6. **0x26BFB0 hang** escape from 40M (size≥0x201 assert nop-sled) → `0x26C0EC`.
7. **Death-band** widen: soft-float `0x292Cxx`, MMI `0x289Axx`, sleep-cmd `0x13F5xx`, stack-as-PC, flip-lock spin.
8. Path3MaskedByVif + high-TADR END **not** ungated.

Rejected:

- Mid flip-kick jump `0x140A04` → `0x1838A4` spin (claim100e).
- Force-post worker type-3/4 (w7 claim100e thrash).
- Inventing PATH3 GIF packets / fake Soft-GS pixels.
- Ungating Path3MaskedByVif.

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** (binds=10) |
| CDVD IRX-only 142 | **Broken** → **cdvd=692** |
| Worker cmd type=2 soft-success | **Yes** (forced epi, resWas=0) |
| PART1.PAK / TOC FILEIO open | **Yes** (pre+post type-2) |
| R_SHELL Fedo host extract (full) | **Yes** (`shellOk=True`) |
| LoadWad bind seed | **Yes** (host state only) |
| SPU2 activity | **Yes** (spu2Writes=512) |
| **gifPath3** | **No** (gifPath2=1082) |
| Soft-GS px>0 | **No** (FRAME_1=0) |
| Interactive title surface | **No** |

### Wall / next

1. Host Fedo R_SHELL / TIT1 bytes land but game **decode → GIF PRIM** path never runs (stream graph still empty after soft type-2).
2. Forced epi + `0x27DBF0` stub publish res=0; natural type-2 body still does not build draw graph / FRAME.
3. Need natural **LoadWad('R_Shell')** / title VPK decode that issues PATH2/PATH3 with FRAME+PRIM — keep Path3MaskedByVif.
4. Soft-GS px>0 non-black then pad.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w8
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w8/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
