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
| **Status** | CDVD 142; tag empty-exit only; RPC calls=386; PC=`0x26C0E0`; still `px=0` |
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
| Tag-list `0x170BBx` | **Escaped** → empty epilogue `0x170BFC` |
| **CDVD** | **Yes** — `cdvdSectors=142` |
| GS / px | **No** (`px=0`, `gifPath3=0`) |
| Main menu | **No** |

### Evidence @ 100M (host-present)

```
PC=0x0026C0E0  px=0 gifPath3=0 dmac=23 sifBytes=42920 syscalls=436112
cdvdSectors=142
RealSifRpc: binds=10 calls=386
```

### Assists

- World kick after CDVD: list-walk re-escape, peer wake, dense pad  
- Tag-list walk force-empty at `0x170BFC` when `a1` bad / periodic  
- Avoid kick into unknown-opcode band `0x2A0xxx`  
- Exception-vector rescue held  
- Policy: no SEMA_STALL_YIELD, PollSema-id, no global DMAC force-finish  

## MENU REACHED?

**No.**

> Past DualInfo, 989snd, FreezeCache, BST, **HERO_HEAP_SIZE**, freelist, **CDVD 142**, tag empty-exit.  
> RPC calls climbed to **386**. Still **px=0** — no GS frame.

### Next

1. Populate world object lists for real draw.  
2. First GS (`px>0` / `gifPath3>0`).  
3. Pad-inject once presentable surface exists.
