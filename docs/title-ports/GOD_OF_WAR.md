# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w7` |
| **Branch** | `agent/menu-gow-w7` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-7 residual:** type-2 soft-success (`+0x888=0` epilogue); broke **cdvd 142→555** via GODOFWAR.TOC + PART1.PAK FILEIO force-open + TOC-member host extract (R_SHELL.WAD / TIT1E1_2.VPK). Still **MENU NO** — Soft-GS **px=0 gifPath3=0** (FRAME_1=0; Path3MaskedByVif held) |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-7 evidence (agent/menu-gow-w7)

#### Claim 100M (SEMA_STALL_YIELD OFF) — Soft-GS

```
@100M: PC varies (heap/data residual) px=0 gifPath2=962 gifPath3=0 dmac=11
       cdvdSectors=555 (was 142 IRX-only)
       type-2 complete soft-success @40M (resWas=0x8101002F → res=0; +0x888=0)
       FILEIO force-open TOC fd=0 PART1 fd=1
       PART1 members: R_SHELL.WAD @0x01E00000, TIT1E1_2.VPK @0x01D00000
       Path3MaskedByVif + high-TADR END held
```

Wave-7 assist changes:

1. **Type-2 table `+0x888=0`** — disasm `0x27E280` requires flag clear for `v0=0` success (wave-6 planted `1` → `0x81019003`).
2. **Complete-once success publish** — clear stale `0x8101*` result, write status block, clear `*0x310384`.
3. **GODOFWAR.TOC + PART1.PAK FILEIO force-open** after type-2.
4. **TOC 24-byte records** (`name[16]+off+size`) → host-extract R_SHELL / TIT1E1 from PART1.PAK LBA 1547 with honest sector credit.
5. **Virtual FILEIO** member FDs for shell/title.
6. **Heap free residual** `0x13DE80..0x13DF20` + object dispatch widen `0x233A50`.
7. Path3MaskedByVif + high-TADR END **not** ungated.

Rejected:

- Fail→success epilogue plants mid type-2 body (0x401A storms — wave-6).
- Force-post worker type-3/4 (claim100e: 3.3M WaitSema thrash, no PATH3).
- Protect-only Step return while type-2 pending (EE PC freeze at PostWait).
- Host-load R_*.WAD as ISO root files (they are PART1.PAK members only).

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** (binds=10) |
| CDVD IRX-only 142 | **Broken** → **cdvd=555** |
| Worker cmd type=2 soft-success | **Yes** |
| PART1.PAK / TOC FILEIO open | **Yes** (host force) |
| R_SHELL + TIT1 title member extract | **Yes** (host) |
| **gifPath3** | **No** (gifPath2=962) |
| Soft-GS px>0 | **No** (FRAME_1=0) |
| Interactive title surface | **No** |

### Wall / next

1. Game must **decode/draw** TIT1E1_2.VPK / R_SHELL (compressed) via natural stream path after type-2 — host bytes alone do not fire PATH3.
2. Real worker type-2 body still fails early (`0x8101002F` before gates); soft-success advances main but stream graph stays empty → FRAME_1=0.
3. Soft-GS px>0 non-black then pad. Keep Path3MaskedByVif + high-TADR END.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w7
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w7/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
