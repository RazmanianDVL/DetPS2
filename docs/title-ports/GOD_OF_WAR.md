# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w5` |
| **Branch** | `agent/menu-gow-w5` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Wave-5:** worker type=2 at `*0x310384` clears on real tid with s1=0x310000 / sp=0x31C660 (`RestoreContext` + frame repair). Wrong-thread worker-text thrash (s1=0 sp=OOB) rehomed. Claim 100M: cmd=0, worker Sleep WaitSema(32), gifPath2=962 — still **px=0 gifPath3=0** LoadWad/FILEIO open |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-5 evidence (agent/menu-gow-w5)

#### Diagnose 20M (SEMA_STALL_YIELD OFF) — Dmac Path3MaskedByVif gate held

```
@20M: PC=0x283F08 px=0 gifPath2=887 gifPath3=0 dmac=1 sif=8116 binds=10 calls=21 cdvd=142
      worker tid=3 Sleep WaitSema(32); SIF tid=2 Sleep WaitSema(3)
```

#### Claim 100M — real-thread type-2 dispatch

```
@100M: PC=0x299354 px=0 gifPath2=962 gifPath3=0 dmac=11 sif=12k binds=10 cdvd=142
       *0x310384: type 2 → 0 (jump table on worker tid; s1/sp healthy)
       worker tid=3 Started Sleep WaitSema(32)
       WaitSema/SignalSema ~143k (idle after cmd clear — not 2.1M poison thrash)
```

Wave-5 assist changes:

1. **`RestoreContext(worker.Id)`** instead of `TryYieldToOtherRunnable` (peer WaitSema trampoline steal).
2. **Repair worker frame** s1=s3=0x310000, s4=0x2C0000, sp=StackTop-608 when poison / force-dispatch.
3. **Park main** at `0x185FAC` when executing worker .text on wrong tid (live: s1=0 sp=OOB).
4. **`PickSafeResume` death-band** includes worker body + WaitSema leaf + post-type-2 `0x26B9xx` jalr data thrash.
5. Cache-wb residual widened to `0x2943C0`; escape bad `jalr a2` at `0x26B9F4` (a2=0xD40).

Rejected: arm-once-only force (left cmd stuck); 1M-only throttle (cmd stuck); wrong-tid rehome via freelist `$ra` bounce.

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** (binds=10) |
| CDVD IRX-only | **Yes** (142) |
| Worker cmd type=2 | **Yes** (cleared `*0x310384` on real worker tid) |
| **gifPath3** | **No** (gifPath2=962; PATH3 still 0) |
| LoadWad / FILEIO past IRX | **No** |
| Soft-GS px>0 | **No** |
| Interactive title surface | **No** |

### Wall / next

1. After type-2 idle WaitSema(32): post more worker cmds / stream path so PATH3 DMA fires (`gifPath3>0`) without ungating early VIF/GIF STR drain.
2. FILEIO / NCMD / ATHN*.WAD past cdvd=142.
3. Soft-GS px>0 non-black, then pad.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w5
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w5/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
