# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w8b` |
| **Branch** | `agent/menu-gow-w8b` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-8b residual:** Haven `StartAndMaybeSwitch` $ra-resume **reverted** (restored boot); type-2 **FULL stream path natural success** (`resWas=0` epi=True streamPast=True). Still **MENU NO** — Soft-GS **px=0 gifPath3=0** (FRAME_1=0; Path3MaskedByVif held) |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-8b evidence (agent/menu-gow-w8b)

#### Claim 100M (SEMA_STALL_YIELD OFF) — Soft-GS

```
@100M: PC=0x176FAC..0x26C1xx residual px=0 gifPath2=962 gifPath3=0 dmac=11
       cdvdSectors=555
       type-2 complete NATURAL success @40M (resWas=0; mid=True epi=True streamPast=True)
       FILEIO force-open TOC fd=0 PART1 fd=1
       PART1 members: R_SHELL.WAD @0x01E00000, TIT1E1_2.VPK @0x01D00000
       DISPFB1 programmed; FRAME_1=0
       Path3MaskedByVif + high-TADR END held
```

Wave-8b changes:

1. **KernelHle.StartAndMaybeSwitch** — revert Haven broad `$ra` resume. Classic `fromSyscall` PC+4 only.
   - Haven plant broke GoW at first StartThread (~274k): main `Started=false`, forever WaitSema thrash, **cdvd=0 gifP2=0**.
   - Ground truth: SaveOut pin `$ra` → SwitchToFull desync → JREXIT; wave-7 metrics dead on tip after haven-w6 merge.
2. **Type-2 FULL stream path** — `0x281568` returns **v0=2** (not 1) so body sets `+0x888=1` and runs fill helpers (`0x27DED8`/`0x282208`/`0x281E30`/`0x27DCC8`) instead of early-exit `0x27E208` (no stream).
3. **Digit gate** — force continue past `0x81010086` (`a2<11` at `0x27E0B8`); pre-seed stream slot digits/ptrs at `0x2A3318`.
4. **Stream helper soft-ok** + **`0x27DBF0` follow soft-ok** — natural epilogue `resWas=0` (without soft-ok follow, body hangs mid-`0x27DBF0`).
5. **Complete-once delay** until epilogue / stream-past / fill window so full path executes.
6. **Death bands** — `0x292Cxx`, `0x2A14xx`, `0x2A6Dxx`, high RDRAM data-PC; post-type-2 resume prefers `0x26C0EC`.
7. Path3MaskedByVif + high-TADR END **not** ungated. No invented PATH3 / no planted FB pixels.

Rejected:

- Broad `$ra` resume for all StartThread (Haven-only; kills GoW boot).
- Force-post worker type-3/4 (claim100e thrash).
- Real `0x27DBF0` without soft-ok (streamPast=False hang before epi).
- Early ungating Path3MaskedByVif.

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** (binds=10) |
| CDVD IRX-only 142 | **Broken** → **cdvd=555** |
| Worker cmd type=2 soft-success | **Yes** → **natural resWas=0** (w8b) |
| Type-2 FULL stream body (fill path) | **Yes** (epi+streamPast) |
| PART1.PAK / TOC FILEIO open | **Yes** (host force) |
| R_SHELL + TIT1 title member extract | **Yes** (host) |
| **gifPath3** | **No** (gifPath2=962) |
| Soft-GS px>0 | **No** (FRAME_1=0; DISPFB1 set) |
| Interactive title surface | **No** |

### Wall / next

1. Type-2 natural success still leaves **no later worker cmds** (`*0x310384=0`); shell decode/draw not reached → FRAME_1 stays 0.
2. Real `0x27DBF0` follow hangs — soft-ok publishes status without arming shell DMA; need real follow with healthy stream graph.
3. Host R_SHELL/TIT1 bytes alone do not fire PATH3; game must open/decode via natural FILEIO+draw.
4. Soft-GS px>0 non-black then pad. Keep Path3MaskedByVif + high-TADR END.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w8b
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w8b/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
