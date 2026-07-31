# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-gow-w3` |
| **Branch** | `agent/menu-gow-w4` |
| **ROMDIR gate** | **CLOSED** |
| **Status** | **Dmac Path3MaskedByVif gate kept.** 20M stable binds=10 cdvd=142. Wave-4: worker cmd type=2 at `*0x310384` **processed** (→0); no wrong-thread PC-stomp to `0x27CC08`. Claim residual PC=`0x27CC18` (historical first-gifPath3 PC) with WaitSema thrash — still **px=0 gifPath3=0** LoadWad open |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-4 evidence (agent/menu-gow-w4)

#### Diagnose 20M (SEMA_STALL_YIELD OFF) — Dmac gate held

```
@20M: PC=0x283EF4 px=0 gifPath3=0 dmac=2 sif=8116 binds=10 calls=21 cdvd=142
      rescue nop-sled 0x2200F0 -> 0x2200FC (stable; not heap 0x13D9C8)
```

#### Claim 100M / 150M — worker cmd drain + historical residual PC

```
@100M: PC=0x27CE60 px=0 gifPath3=0 dmac=32 sif=13k binds=10 calls=57 cdvd=142
       *0x310384: type 2 → 0 (worker jump-table ran; cmd slot cleared)
       WaitSema/SignalSema thrash ~1.4M each (shape of historical gifPath3=1 tip)
@150M: PC=0x27CC18 (same residual PC as campaign first gifPath3=1) still gifPath3=0
```

Wave-4 assist changes:

1. **No PC-stomp** of `0x27CC08` onto non-worker threads (poisoned SP / UnknownSpecial 0x2Axxxx).
2. **SignalSema(32) + TryYieldToOtherRunnable** when worker sleeps with pending cmd type∈[2,100] — no `RestoreContext` rewind.
3. **Post-worker copy/hang escape** at `0x26BF50..0x26BFC8` (huge memcpy / size≥513 hang) when that residual lands.
4. **gowHot** extended for worker handlers + post-worker band.

Rejected (unstable): `RestoreContext` force to `0x27CC08` every Step — rewound dispatch; one thrash path painted 989snd-done but lost dmac/binds shape.

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** (binds=10) |
| CDVD IRX-only | **Yes** (142) |
| Worker cmd type=2 | **Yes** (cleared `*0x310384`) |
| **gifPath3** | **No** residual WaitSema thrash @0x27CC18 |
| LoadWad / FILEIO past IRX | **No** |
| Soft-GS px>0 | **No** |
| Interactive title surface | **No** |

### Wall / next

1. Historical tip had **gifPath3=1** at same PC `0x27CC18` with dmac~121 — recover PATH3 DMA without reopening ungated VIF/GIF STR drain (kills early boot).
2. FILEIO / NCMD / ATHN*.WAD past cdvd=142 after first GS packet.
3. Soft-GS px>0 non-black, then pad.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w4
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w4/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
