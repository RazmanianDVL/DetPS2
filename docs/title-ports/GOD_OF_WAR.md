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
| **Status** | CDVD 142; RPC binds=45 calls=207; sifBytes↑; free-search circular thrash escaped; still `px=0` |
| **Last updated** | 2026-07-30 |

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| DualInfo / PollSema-id | **Yes** |
| 989snd sentinel HLE | **Yes** |
| FreezeCache escape | **Yes** |
| BST + HERO_HEAP_SIZE | **Yes** |
| Heap freelist / list walk | **Partial** — capped escapes |
| Global free-search `0x13E1C8` | **Escaped** — plant null-terminated head @ `*0x29BEB0` |
| Tag-list `0x170BBx` | **Escaped** → empty epilogue `0x170BFC` |
| **CDVD** | **Yes** — `cdvdSectors=142` (IRX-only; no game FILEIO) |
| GS / px | **No** (`px=0`, `gifPath3=0`) |
| Main menu | **No** (not MK MAINMENU gate) |

### Evidence @ 100M (host-present) — free-head plant

```
PC=0x00284694  px=0 gifPath3=0 dmac=87 sifBytes=46768 syscalls=29509
cdvdSectors=142
RealSifRpc: binds=30 calls=153
threads: all started, not sleeping
[GOW] plant global free-head node=0x01FEC200 size=0x00000000 @0x0029BEB0
```

### Evidence @ 150M (host-present)

```
PC=0x00284650  px=0 gifPath3=0 dmac=90 sifBytes=68656 syscalls=35827
cdvdSectors=142
RealSifRpc: binds=45 calls=207
```

### Assists

- IOPRP `"3000"` + FreezeCache unlock
- BST HERO/SLOT/UPGRADE_HEAP_SIZE + freelist bump-arena soft escapes
- List/flag/parent/link-search soft escapes; cache-wb leaf stub
- **Global free-search plant** — `*0x29BEB0` null-terminated node (size=0, field=~0) so `0x13E1C8` cannot circular-walk forever
- World kick after CDVD: list re-escape, peer wake, dense pad
- Policy: no SEMA_STALL_YIELD, PollSema-id, no global DMAC force-finish
- Prefer shared HLE; title thrash only in `GodOfWarAssist`

## MENU REACHED?

**No.** Gate is **first real GS** (`px>0` non-black) then pad-interactive — **not** MK-style MAINMENU.

> Past DualInfo, 989snd, FreezeCache, BST, freelist, **CDVD 142**, free-search plant, tag empty-exit.  
> RPC calls **207** @150M. Still **px=0** / **gifPath3=0** — no GS frame. cdvd stuck IRX-only.

### Next

1. FILEIO / NCMD asset load past `cdvd=142` (game data, not more IRX).  
2. Break residual list-cmp thrash (`0x2847xx`) + float band residual (`0x284650`).  
3. First GS (`px>0` / `gifPath3>0`, non-black Soft-GS).  
4. Pad-inject once presentable title/in-engine surface exists.

### Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-gow
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-gow/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
dotnet exec out/game-gow/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=150000000 --host-present
```
