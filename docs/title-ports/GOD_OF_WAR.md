# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w6` |
| **Branch** | `agent/menu-gow-w6` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-6:** post type-2 poison-SP word-scan (0x299354) escaped; type-2 gate stubs + complete-once; real-thread dispatch kept. Claim 100M: cmd soft-ok clear, all threads healthy, syscalls~13k (not 298k thrash) — still **px=0 gifPath3=0** cdvd=142 FILEIO/LoadWad open |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-6 evidence (agent/menu-gow-w6)

#### Diagnose 20M (SEMA_STALL_YIELD OFF) — Dmac Path3MaskedByVif gate held

```
@20M: PC=0x283F08 px=0 gifPath2=887 gifPath3=0 dmac=1 sif=8116 binds=10 calls=21 cdvd=142
      worker tid=3 Sleep WaitSema(32); SIF tid=2 Sleep WaitSema(3)
```

#### Claim 100M — type-2 gate stubs + complete-once + SP/word-scan

```
@100M: PC=0x1781F8 px=0 gifPath2=962 gifPath3=0 dmac=11 sif=11.5k binds=10 calls=50 cdvd=142
       plant type-2 gate stubs @37M; complete worker cmd=2 res=0x8101002F softOk
       threads 1/2/3 all Alive+Started; syscalls~13712 (idle healthy — not 2.1M/298k thrash)
```

Wave-6 assist changes:

1. **Repair poison SP** with plausible-stack check (never plant SP in .text — live 0x176BC0 bug).
2. **Escape word-scan** residual 0x299300 (live 0x299354 a2=0x400 multi-MiB + ra self + sp OOB).
3. **No force-rewind mid type-2 body** — forceDispatch only at WaitSema/idle gate.
4. **Type-2 gate stubs** 0x282DD0 / 0x281568 / 0x281548 / 0x2815A8 → li v0,1 (null-handle path);
   0x2F-path nops + strcmp soft-match + 0x27E220→0x27E234 (still residual res=0x8101002F once).
5. **Complete-once** after first dispatch: clear *0x310384 on 0x8101* error so main poll advances.
6. **Rehome wrong-tid** off worker text even when cmd clear; uncached 0x401A poison PC rescue.
7. **gowHot** 0x2993xx + 0x2893xx; host byte-copy accel for 0x289320 residual (capped).

Rejected: mid-body soft-retry to 0x27DFB4 (skipped prologue → UnknownOpcode 0x2032xxxx);
fail→success branch plants into type-2 epilogue (uninit stream → 0x401A storms);
type-2 no-op body stub (main continues without assets, metrics frozen).

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** (binds=10) |
| CDVD IRX-only | **Yes** (142) |
| Worker cmd type=2 | **Yes** (gate stubs; soft-ok clear) |
| Post type-2 poison SP / word-scan | **Escaped** (wave-6) |
| **gifPath3** | **No** (gifPath2=962; PATH3 still 0) |
| LoadWad / FILEIO past IRX | **No** |
| Soft-GS px>0 | **No** |
| Interactive title surface | **No** |

### Wall / next

1. Type-2 still returns 0x8101002F (stream bit/flag at sp+16 missing) after gate stubs — need real stream job / WAD open path, not fail→success epilogue plants.
2. FILEIO / NCMD / ATHN*.WAD past cdvd=142.
3. Soft-GS px>0 non-black, then pad. Path3MaskedByVif + high-TADR END gates stay.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w6
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w6/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
