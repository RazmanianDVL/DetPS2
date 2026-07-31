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
| **Branch** | `agent/menu-gow-w2` (base tip `3748553`) |
| **ROMDIR gate** | **CLOSED** |
| **Status** | WHIP WaitSema thrash **fixed** (SHARED); dmac≈93 binds=10; **px=0 gifPath3=0**; residual post-table 0x156324; LoadWad still open |
| **Last updated** | 2026-07-31 |

### MENU gate

**first-gs-interactive** = Soft-GS **px>0 non-black** + pad interactive surface — **not** MK MAINMENU.

### Wave-2 evidence (agent/menu-gow-w2)

#### Tip 3748553 regression (pre-fix)

WHIP_SEMA_FIX_V2 always-fabricate (merge agent/menu-whip) on empty SIF WaitSema(3):

```
@20M: PC=0x293B50 px=0 gifPath3=0 dmac=1 binds=0 cdvd=0 WaitSema 0x44 x537k
FABRICATING signal for sema=0x3 (WHIP_SEMA_FIX_V2)  // forever
```

#### After SHARED WaitSema gate (WhiplashAssist-only fabricate; else TryYield-first)

```
@20M:  PC=0x283EF4 px=0 gifPath3=0 dmac=2 binds=10 calls=21 cdvd=142 WaitSema x36
@100M: PC=0x156324 px=0 gifPath3=0 dmac=93 sifBytes=19080 binds=10 calls=88 cdvd=142
```

No early SetIopRpVersionAscii. Post-reboot EnsureIopRp only.

CRT0 AdEL death (binds=16 gifPath3=0 PC=0x100140) also closed via post-progress CRT0 re-home + null-ra prefer 0x27CC08.

### Prior tip gifPath3=1 (wave-1 / gow-safe @ c423c4f-class)

```
PC=0x0027CC18  px=0 gifPath3=1 dmac=121 sifBytes=21420 syscalls=2299166
cdvdSectors=142
RealSifRpc: binds=10 calls=101
top syscalls: WaitSema 0x44 x1.14M, SignalSema 0x42 x1.14M
```

Wave-2 has not yet restored gifPath3=1 (residual post-table 0x156324 after freelist/cache-wb).

### Disc MOD_LOAD

IRX path restored (sio2man/dbcman/sio2d/mc2_d/ds2u_d/libsd/989nomid/smpd_iop). **No FILEIO** RPC. **No game NCMD** past IRX cdvd=142.

### What was tried (wave-2)

1. **Gate WHIP always-fabricate to WhiplashAssist** + restore TryYield-first for other titles — **YES** (SHARED, unblocks GoW boot).
2. **CRT0 band rescue** after AdEL re-entry — **YES** (avoids PC=0x100140 freeze).
3. **Null-ra empty-SIF prefer worker 0x27CC08** — **YES** (avoid AdEL chain).
4. **Post-table residual 0x155BA0–0x156400 soft-return** — in flight for gifPath3 path.
5. **Early GetVersion=3000** — **NOT used** (regressed gifPath3 historically).

### How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / PollSema-id | **Yes** |
| Disc MOD_LOAD IOP_MOD | **Yes** |
| 989snd sentinel HLE | **Partial** (binds path) |
| FreezeCache escape | **Yes** |
| BST + HERO_HEAP_SIZE | **Yes** |
| freelist / list / table-index | **Partial** — soft escapes |
| **CDVD** | **Yes** — 142 IRX-only |
| **gifPath3** | **No** this wave residual (prior tip had 1) |
| GS / px | **No** (`px=0`) |
| LoadWad / FILEIO past IRX | **No** |
| Interactive title surface | **No** |

### Wall / next

1. Post-table residual **PC=0x156324** after freelist/cache-wb (~40M–100M) — leave toward stream/PATH3 (gifPath3) then LoadWad.
2. FILEIO / NCMD / LoadWad past IRX-only cdvd=142 (game WAD: ATHN*.WAD / ARENA*.WAD).
3. First Soft-GS **px>0 non-black**, then pad inject.
4. Prefer real IOPRP300 GetVersion **after** IRX binds / reboot, never early.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
```
