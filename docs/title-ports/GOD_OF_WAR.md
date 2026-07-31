# God of War (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | God of War (USA) |
| **user-media id** | `god-of-war` |
| **Serial / BOOT2** | `SCUS_973.99` |
| **ISO** | `C:/Users/xxraz/Downloads/GodofWar(USA).iso` |
| **BIOS** | SCPH-70008 (E) v2.0 2004-06-14 |
| **Media config** | `user-media-god-of-war.json` |
| **Worktree** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2` |
| **Branch** | `agent/menu-gow` (base tip `c423c4f`) |
| **ROMDIR gate** | **CLOSED** |
| **Status** | first **gifPath3=1**; **px=0**; WaitSema residual after IOP_MOD MOD_LOAD |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Tip evidence @ 100M (host-present, SEMA_STALL_YIELD OFF) — `c423c4f`

```
PC=0x0027CC18  px=0 gifPath3=1 dmac=121 sifBytes=21420 syscalls=2299166
cdvdSectors=142
RealSifRpc: binds=10 calls=101
exitRequested=False
stream-arms=1
top syscalls: WaitSema 0x44 x1.14M, SignalSema 0x42 x1.14M
```

Diagnose @20M (same tip): `PC=0x002849C4` soft-float prologue heat, dmac=2, cdvd=142, binds=10.

### Disc MOD_LOAD (TRACE_RPC, tip)

Post-LOADFILE GetVersion (classic `0x00020000` while PreferIopRp=True but version empty until reboot):

| path | result | StartLoadedModule |
|------|--------|-------------------|
| IOP_MOD/sio2man.irx | 4 | (HLE/existing) |
| IOP_MOD/dbcman.irx | 100 | DBCMAN ok insns=30984 ret=sentinel |
| IOP_MOD/sio2d.irx | 101 | SIO2D ok |
| IOP_MOD/mc2_d.irx | 102 | MC2_D hit budget 50k |
| IOP_MOD/ds2u_d.irx | 103 | DS2U_D hit budget |
| IOP_MOD/libsd.irx | 7 | existing |
| IOP_MOD/989nomid.irx | 104 | 989NOMID hit budget v0=1 |
| IOP_MOD/smpd_iop.irx | 105 | SMPD_IOP hit budget |

989snd HLE answers sid=0x00123456 (fno 0 / 0x4D / 0xA / 0x68) with done-magic. **No FILEIO** RPC. No game NCMD past IRX cdvd=142.

### Play!

No SCUS_973.99 GameConfig entry — generic IOP HLE only.

### What was tried (agent 2026-07-31) and rejected

1. **Early SetIopRpVersionAscii("3000")** at cyc 500k so GetVersion returns ASCII 3000  
   → regressed **gifPath3 1→0**, metrics froze (dmac≈99, PC 0x1756xx).  
   Keep: EE plant at 0x2C6D30 + **post-reboot** EnsureIopRpGetVersion only.

2. **Thrash-aware WaitSema soft-return** (prefer soft-return over SignalSema; land on worker 0x27CC)  
   → when $ra was thrash-band, left=True spun; **gifPath3 1→0 dmac 121→3**, WaitSema 1.9M.

3. Shared StartLoadedModule budget bumps / gowHot expansions — thrash rewrites regressed tip gifPath3.

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / PollSema-id | **Yes** |
| Disc MOD_LOAD IOP_MOD (DBCMAN/989…) | **Yes** (StartLoadedModule on tip IRX path) |
| 989snd sentinel HLE | **Yes** |
| FreezeCache escape | **Yes** |
| BST + HERO_HEAP_SIZE | **Yes** |
| freelist / list / table-index / soft-tick | **Partial** — soft escapes |
| **CDVD** | **Yes** — 142 IRX-only |
| **gifPath3** | **Yes** — **1** (first PATH3) |
| GS / px | **No** (`px=0`) |
| Interactive title surface | **No** |

### Wall / next

1. Residual empty SIF WaitSema (0x293Cxx / worker 0x27CCxx) after gifPath3=1 — 1M+ fabricate thrash burns claim cycles; need real SIFCMD/FILEIO/LoadWad progress, not more SignalSema.
2. FILEIO / NCMD / LoadWad past IRX-only cdvd=142 (game WAD data).
3. First Soft-GS **px>0 non-black**, then pad inject.
4. Prefer real IOPRP300 GetVersion **after** IRX binds / reboot, never early.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
# RPC spine:
$env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-gow/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=50000000 --host-present
```
