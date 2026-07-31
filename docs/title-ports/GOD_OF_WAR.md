# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w9b` |
| **Branch** | `agent/menu-gow-w9b` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-9b residual:** pre-type-2 full Fedo R_SHELL + LoadWad bind; real 0x27DBF0 hang-guard (no permanent soft-ok); force epi + epi-hold; **MENU NO** — Soft-GS **px=0 gifPath3=0 FRAME_1=0** (Path3MaskedByVif held) |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-9b evidence (agent/menu-gow-w9b)

#### Claim 100M (SEMA_STALL_YIELD OFF) — Soft-GS

```
@100M: PC=0x295004 px=0 gifPath2=1082 gifPath3=0 dmac=28 spu2Samples=32552
       cdvdSectors=1202 (full R_SHELL+TIT1 host ×2 windows)
       type-2: force epi 0x27E234 @38.65M → complete @39.85M
               dbf0Seen=True dbf0Esc=1 (mid-body 0x27DC7C hang-escape, shellOk)
       R_SHELL.WAD full 0xBAA95 @0x01E00000 Fedo magic 0x4665646F OK (pre-type2)
       TIT1E1_2.VPK @0x01D00000; LoadWad bind seed shellOk=True
       softgs-regs: FRAME_1=0 DISPFB1=0x800005090D0 SCISSOR full XYOFFSET=0 TEST=0
       prims=0 fragTest=0 — Path2 setup only (no XYZ kick / no FRAME write)
       *0x310384 posts after type-2: none (cmd stays 0; no invented type-3/4)
       threads Started=True; worker WaitSema(32) idle
       Path3MaskedByVif + high-TADR END held
```

Wave-9b assist changes:

1. **Pre-type-2 full R_SHELL/TIT1 host extract** + **LoadWad bind seed** (restored from w8 regress in w8b).
2. **No permanent soft-ok of 0x27DBF0** — real stream-follow hang-guard after host shell lands; GIF/VIF1 handler credit only (no invented PRIM).
3. **Force type-2 success epi 0x27E234** + **epi-hold** (SwitchTo worker, re-enter 0x27DBF0) for ≥1.2M before cmd clear.
4. **Post-type-2 SignalSema wakes** for sleeping game threads — **never invent type-3/4** (w7 thrash rejected).
5. **Do not paint 989snd done-magic onto LoadWad streamObj / Fedo buffers** (w9b claim: pending=0x01CFE000 destroyed payload).
6. **Residual thrash escape** 0x13F5 sleep-cmd / MMI → stream-poll 0x26C0EC after type-2.
7. Path3MaskedByVif + high-TADR END **not** ungated.

Rejected:

- Force-post worker type-3/4 (w7 claim100e thrash).
- Inventing PATH3 GIF packets / fake Soft-GS pixels / FRAME plant.
- Ungating Path3MaskedByVif.
- Permanent soft-ok 0x27DBF0 at gate plant (skips real DMA arm).

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** (binds=10) |
| CDVD IRX-only 142 | **Broken** → **cdvd=1202** |
| Worker cmd type=2 soft-success | **Yes** (force epi + dbf0Seen) |
| PART1.PAK / TOC FILEIO open | **Yes** (pre+post type-2) |
| R_SHELL Fedo host extract (full) | **Yes** (`shellOk=True`) |
| LoadWad bind seed | **Yes** (host state) |
| Real 0x27DBF0 entry | **Yes** (hang-escape mid-body) |
| Post-type-2 *0x310384 next cmd | **No** (cmd=0 forever) |
| **gifPath3** | **No** (gifPath2=1082) |
| Soft-GS px>0 | **No** (FRAME_1=0, prims=0) |
| Interactive title surface | **No** |

### Wall / next

1. **Who posts to 0x310384 after type-2?** Retail main (or peer) — live post-type-2 cmd stays 0; SignalSema wakes run but poster never rewrites the cmd word. Do **not** invent type-3/4.
2. **0x27DBF0 hang mid-body** (0x27DC7x) with shellOk — hang-escape leaves success + GIF credit; does **not** produce FRAME/PRIM (real follow still incomplete graph).
3. **Path2 @ gifP2=1082:** DISPFB1 + SCISSOR set; **FRAME_1=0**, **TEST=0**, **XYOFFSET=0**, **prims=0 / fragTest=0** — setup packets only, no XYZ kick. Soft-GS cannot paint.
4. Need natural **shell decode → GIF FRAME+PRIM** (LoadWad('R_Shell') / VPK consumer) — Path3MaskedByVif held.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w9b
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w9b/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
