# SYSMEM port — gap analysis (Phase 0 → contract HLE)

**Agent:** SYSMEM (generic BIOS HLE only — zero title PCs / commercial hacks)  
**Date:** 2026-07-30  
**Worktree:** `subagent-019fb1b9-b0c3-71d0-841d-6402b768f693`

## Authority

| Source | Status |
|--------|--------|
| `docs/BIOS_DISSECTION.md` §1 SYSMEM, §2 IOPBTCONF, §7 | Present |
| Sibling `detps2/tools/bios-extract/SYSMEM.bin` | 4625 B, export table `sysmem` v1.1 (16 funcs) |
| Sibling `detps2/tools/bios-decomp/SYSMEM_ALL.txt` | **Absent** (no Ghidra dump yet) |
| ps2sdk `iop/system/sysmem/include/sysmem.h` | Fetched (Alloc modes, Query*, imports) |
| ps2sdk `iop/system/sysmem/src/sysmem.c` | Fetched (256-byte page freelist; SCE SDK 3.1.0-based) |
| ps2sdk `ee/kernel/src/iopheap.c` | Fetched (EE client wire shapes) |
| ps2sdk `common/include/iopheap-common.h` | Fetched (`_iop_load_heap_arg`) |
| Existing `RealSifRpc` SidSysmem bump allocator | Deepened this pass |

**Namespace note:** EE RPC **sid** `0x80000003` (iopheap) shares the numeric value with SIFCMD **CID** `RESET_CMD` (`0x80000003`). Different namespaces — do not conflate with `SonyKernelHle` RESET_CMD handling.

SYSMEM is **IOPBTCONF entry #1** (`System_Memory_Manager`). EE games almost never call its exports directly; they use **SifAllocIopHeap / SifFreeIopHeap / SifLoadIopHeap**, which bind RPC sid `0x80000003` and call into an IOP heap server that sits on top of `AllocSysMemory` / `FreeSysMemory`.

| Side | Owner | Surface |
|------|-------|---------|
| IOP page freelist / Query* / Kprintf | **SYSMEM IRX** (not executed) | Documented; HLE approximates via EE RPC |
| EE RPC wire (fno 1/2/3) | **This agent** | `RealSifRpc.HandleSysmem` |
| IRX placement in low IOP RAM | MODLOAD / `IopModuleHost` | Stays below heap base `0x180000` |

## Real contracts (ground truth)

### Export library (`sysmem` v1.1 — `SYSMEM.bin` @ magic `0x41C00000`)

| Ordinal | Symbol (ps2sdk) | Notes |
|--------:|-----------------|-------|
| 3 | `GetSysmemInternalData` | Internal control block |
| 4 | `AllocSysMemory(mode, size, ptr)` | Modes: FIRST=0, LAST=1, ADDRESS=2 |
| 5 | `FreeSysMemory(ptr)` | 0 ok / −1 fail |
| 6 | `QueryMemSize` | Total managed size |
| 7 | `QueryMaxFreeMemSize` | Largest free block |
| 8 | `QueryTotalFreeMemSize` | Sum of free |
| 9 | `QueryBlockTopAddress` | Top + FREE flag `0x80000000` |
| 10 | `QueryBlockSize` | Size + FREE flag |
| 14 | `Kprintf` | Optional debug |
| 15 | `KprintfSet` | Handler install |

### AllocSysMemory page rule (sysmem.c)

```
pages = (size + 255) >> 8;
if (pages == 0) return NULL;          // size 0 → NULL
// blocks tracked in 256-byte quanta; FREE bit in low info word
```

- Success → IOP **physical** pointer (page aligned).  
- Failure → **NULL** (`0`).  
- Free of non-page-aligned / non-owned / free block → **−1**.  
- Free success → **0**; adjacent free blocks coalesce.

### EE iopheap RPC (`iopheap.c` / `iopheap-common.h`)

| fno | Client API | Send | Recv | Result |
|----:|------------|------|------|--------|
| 1 | `SifAllocIopHeap(size)` | 4B size | 4B addr | `addr` or **NULL** |
| 2 | `SifFreeIopHeap(addr)` | 4B addr | 4B result | **0** / **−1** |
| 3 | `SifLoadIopHeap(path, addr)` | `struct _iop_load_heap_arg` (addr + path[252]) | 4B result | **0** / negative |

Bind: `sceSifBindRpc(&_ih_cd, 0x80000003, 0)` after `sceSifInitRpc`.

## Pre-port DetPS2 surface

| Area | Status | Gap |
|------|--------|-----|
| sid bind | OK | `SidSysmem` known; not counted as unknown bind |
| Alloc | Partial | 16-byte bump only; no free reuse; min size forced to 16 |
| Free | Stub | Always 0 (no tracking) |
| Load | Stub | Always 0 (no disc copy) |
| size 0 | Wrong | Returned a 16-byte block instead of NULL |
| SaveState | Partial | Only bump watermark |
| Literal IOP freelist / Query* | Missing | No R3000 SYSMEM execution |
| ALLOC_LAST / ALLOC_ADDRESS | Missing | EE RPC only exposes FIRST-shaped alloc |

## Landed this agent (2026-07-30)

1. **`HandleSysmem`** dedicated CALL path (sid `0x80000003`) with disc-aware Load.  
2. **256-byte page alignment** matching AllocSysMemory; **size 0 → NULL**.  
3. **First-fit free list + bump** in `[0x180000, 0x1F0000)` (above IRX load area, below RPC scratch).  
4. **Free** returns 0/−1; double-free / misaligned / unknown → −1; coalesce + watermark retract.  
5. **Load** copies disc file bytes into the heap buffer; missing `cdrom*` → `LfErrFileNotFound` (−203); soft-0 without disc.  
6. **SaveState** serializes live map + holes + `SysmemOps`.  
7. **Smoke** `RealSifRpc_SysmemAllocFreeLoadContracts` (bind, alloc/free/reuse, load, missing path).  
8. **Zero game hacks** / no Midway / no title PCs.

### Intentional HLE divergences

| Divergence | Why |
|------------|-----|
| Return **EE-mapped** `0x1Cxxxxxx` (not bare IOP phys) | Existing MOD_BUF_LOAD / DMA helpers already normalize both; EE-mapped is readable via `SystemMemory` without extra translation in title code that peeks the window |
| Fixed heap window 448 KiB | Full IOP RAM freelist needs real module layout + SYSMEM init (`0x401100` tables); window sits above `IopModuleHost` IRX bases |
| Only ALLOC_FIRST | EE iopheap RPC has no mode argument |
| Query* / Kprintf not on EE RPC | No sid fno for them; IOP-export-only until R3000 IRX runs |
| Soft-0 Load without disc / rom0 miss | Avoid panicking boots that probe paths before media is bound |

## Remaining gaps for full ROMDIR SYSMEM completeness

Ordered by contract value:

1. **Ghidra `SYSMEM_ALL.txt`** — literal transliteration of retail freelist vs ps2sdk recreation.  
2. **Full IOP RAM ownership map** — share one freelist with `IopModuleHost` / LOADCORE so Alloc cannot overlap live IRX images.  
3. **ALLOC_LAST / ALLOC_ADDRESS** for IOP-native callers (not EE iopheap).  
4. **QueryMemSize / QueryMaxFreeMemSize / QueryTotalFreeMemSize / QueryBlock*** HLE surface if any title imports them via LOADCORE stubs.  
5. **Kprintf / KprintfSet** routing to host log (debug only).  
6. **Real IOP execution of SYSMEM.IRX** — retires this freelist when R3000 BIOS modules run.  
7. **SifLoadIopHeap** host0:/rom0: raw bytes (today soft-0 without disc match).  
8. **Return bare IOP physical** option if a title hard-requires low pointers without 0x1C map (none observed; ResolveIopPointer already dual-maps).

## Acceptance for this slice

- Bind sid `0x80000003` not unknown.  
- Alloc(0)=NULL; Alloc(n) 256-aligned EE-IOP pointer; OOM=NULL.  
- Free success 0; bad free −1; hole reuse on next Alloc.  
- Load copies real disc bytes; missing cdrom → −203.  
- Smokes green; no commercial title hacks.  
- Remaining ROMDIR SYSMEM work listed above.
