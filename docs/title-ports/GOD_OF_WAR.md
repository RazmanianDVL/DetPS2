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
| **Status** | CDVD 142; RPC binds=45 calls=207; sifBytes↑; list thrash escaped; still `px=0` |
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

### Evidence @ 100M (host-present) — post list/WaitSema/PickSafeResume fix

```
PC=0x00284694  px=0 gifPath3=0 dmac=87 sifBytes=46768 syscalls=29509
cdvdSectors=142
RealSifRpc: binds=30 calls=153
threads: all started, not sleeping
```

### Evidence @ 150M (host-present)

```
PC=0x00284650  px=0 gifPath3=0 dmac=90 sifBytes=68656 syscalls=35827
cdvdSectors=142
RealSifRpc: binds=45 calls=207
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

1. Break residual list-cmp thrash (`0x2847xx`) + avoid CRT0 BSS re-entry.  
2. FILEIO / NCMD asset load past cdvd=142.  
3. First GS (`px>0` / `gifPath3>0`, non-black Soft-GS).  
4. Pad-inject once presentable title/in-engine surface exists.
