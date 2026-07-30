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
| **ROMDIR gate** | **CLOSED** |
| **Status** | CDVD 142; RPC 16/443; dmac=463 sif=95k; tick-wait + free-search escaped; still `px=0` / `gifPath3=0` |
| **Last updated** | 2026-07-30 |

### Bring-up note (agent 2026-07-30)

- **Diagnose wall** `PC=0x2849C4` is soft-float decode prologue (jal `0x284618`), not a list hang.
  Band `0x2847xx` is IEEE754 mantissa rotate — PcProfiler heat is expected. Removing the
  residual `TryEscapeListCompareWalk` force-exit **regressed** dmac 463→5; keep until a
  soft-float-aware gate exists.
- **Claim residual** `PC=0x293C68` = WaitSema trampoline (syscall 0x44); worker empty SIF-cmd
  poll at `0x294810`. Main often `started=False` at 100M.
- **Hot secondary** flag countdown `0x17A32C` (`*0x29C7D0==1`) and freelist `0x23A978`.
- **Play!** GameConfig: no SCUS_973.99 entry. Walls FILEIO/LOADFILE/SIF → generic IOP HLE.
- **First real GS: No** (px=0). Gate is first Soft-GS px>0 non-black, not MK MAINMENU.

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / PollSema-id | **Yes** |
| 989snd sentinel HLE | **Yes** |
| FreezeCache escape | **Yes** |
| BST + HERO_HEAP_SIZE | **Yes** |
| Heap freelist / list walk | **Partial** — soft escapes |
| Soft-tick wait `0x17A1D0` | **Escaped** — advance `*0x29C7D4`; jr ra without zeroing tick |
| Global free-search `0x13E1C8` | **Escaped** — plant null-terminated head @ `*0x29BEB0` (in-RDRAM arena) |
| Tag-list `0x170BBx` | **Escaped** → empty epilogue `0x170BFC` |
| **CDVD** | **Yes** — `cdvdSectors=142` (IRX-only; no game FILEIO) |
| GS / px | **No** (`px=0`, `gifPath3=0`) |
| Interactive title surface | **No** (not MK MAINMENU gate) |

### Evidence @ 100M (host-present) — tick-wait + free-head

```
PC=0x00293C68  px=0 gifPath3=0 dmac=463 sifBytes=95684 syscalls=24796
cdvdSectors=142
RealSifRpc: binds=16 calls=443
[GOW] plant global free-head node=0x01FF3600 size=0x00000000 @0x0029BEB0
PcProfiler: 0x17A32C flag-countdown residual; freelist/list-cmp secondary
```

### Evidence @ 150M (host-present)

```
PC=0x00293C68  px=0 gifPath3=0 dmac=463 sifBytes=95684 syscalls=25337
cdvdSectors=142
RealSifRpc: binds=16 calls=443  (metrics frozen after ~60M — WaitSema residual)
```

### Assists

- IOPRP `"3000"` + FreezeCache unlock
- BST HERO/SLOT/UPGRADE_HEAP_SIZE + freelist bump-arena soft escapes
- List/flag/parent/link-search soft escapes; cache-wb leaf stub
- **Soft-tick wait** — `*0x29C7D4` advance + `0x17A1D0` escape (jr ra @ `0x17A294`, no tick zero)
- **Global free-search plant** — `*0x29BEB0` null-terminated node; arena hard-clamped under 32 MiB RDRAM
- World kick after CDVD: list re-escape, peer wake, dense pad, tick advance
- Policy: no SEMA_STALL_YIELD, PollSema-id, no global DMAC force-finish
- Prefer shared HLE; title thrash only in `GodOfWarAssist`

## First real GS / interactive?

**First real GS: No** (`px=0`, `gifPath3=0`)  
**Interactive: No**

Gate is **first real GS** (`px>0` non-black) then pad-interactive — **not** MK-style MAINMENU.

> Past DualInfo, 989snd, FreezeCache, BST, freelist, **CDVD 142**, free-search plant, **tick-wait escape**.  
> RPC calls **443** / dmac **463** / sif **95k** @100M (was 153/87/46k). Still **px=0** — no GS frame. cdvd IRX-only.

### Next

1. Leave WaitSema residual (`0x293C68`) + re-start dormant main after thrash.  
2. FILEIO / NCMD asset load past `cdvd=142` (game data, not more IRX).  
3. Drain residual flag-countdown (`0x17A32C`) / freelist (`0x23A978`) / list-cmp (`0x2847xx`).  
4. First GS (`px>0` / `gifPath3>0`, non-black Soft-GS) then pad-inject.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
dotnet exec out/game-gow/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=150000000 --host-present
```
