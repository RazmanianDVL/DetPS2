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
| **Branch** | `agent/menu-gow-w3` (base tip `8da8267`) |
| **ROMDIR gate** | **CLOSED** |
| **Status** | WaitSema WHIP-gated (tip). **Dmac Path3MaskedByVif gate restored** — ungated VIF/GIF STR drain killed GoW (nop-sled 0x2200F0→heap 0x13D9C8, binds=0/cdvd=0). 20M stable binds=10 cdvd=142. Residual empty-SIF→worker leave; **px=0 gifPath3=0** LoadWad still open |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-3 evidence (agent/menu-gow-w3)

#### Tip main thrash (pre-fix, after DA/B3 Dmac ungated drain)

```
@20M: PC=0x13DEA0 px=0 gifPath3=0 dmac=1 binds=0 cdvd=0 WaitSema x1
[BIOS] rescue nop-sled 0x002200F0 -> 0x0013D9C8 cyc=582000
```

File bisect: **tip `Dmac.cs` alone** on gow-w2 Core base regressed GoW (ungated VIF0/VIF1/GIF drain on every CHCR.STR).

#### After Dmac Path3MaskedByVif gate + nop-sled heap reject + empty-SIF worker leave

```
@20M x3: PC=0x283EF4 px=0 gifPath3=0 dmac=2 sif=8116 binds=10 calls=21 cdvd=142
         rescue 0x2200F0 -> 0x2200FC (stable)
@100M:   PC=0x293C68 px=0 gifPath3=0 dmac=93 sif=19080 binds=10 calls=88 cdvd=142
         empty-sif soft-return ra=0x27CC08 (worker) after poll loops; still no PATH3
```

WaitSema stays WHIP-gated (title-local fabricate). No early GetVersion=3000.

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / MOD_LOAD IRX | **Yes** (binds=10) |
| CDVD IRX-only | **Yes** (142) |
| **gifPath3** | **No** residual empty-SIF / worker WaitSema |
| LoadWad / FILEIO past IRX | **No** |
| Soft-GS px>0 | **No** |
| Interactive title surface | **No** |

### Wall / next

1. Worker `0x27CC` WaitSema residual after empty-SIF leave — need stream/PATH3 (gifPath3) then LoadWad.
2. FILEIO / NCMD / ATHN*.WAD past cdvd=142.
3. Soft-GS px>0 non-black, then pad.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow-w3
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow-w3/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
