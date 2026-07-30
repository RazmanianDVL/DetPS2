# SYSCLIB + HEAPLIB — gap analysis & port notes (DetPS2 HLE)

**Agent:** SYSCLIB+HEAPLIB (generic BIOS HLE only — zero title PCs / commercial hacks)  
**Date:** 2026-07-30  
**Worktree:** `subagent-019fb1c8-c40c-7eb3-80f9-aef55ca04357`

## Authority

| Source | Status |
|--------|--------|
| `docs/BIOS_DISSECTION.md` §2 IOPBTCONF (SYSCLIB then HEAPLIB after TIMEMAN*) | Present |
| `docs/BIOS_DISSECTION.md` §6.5 LOADCORE `ScanExports`/`LinkImports` | Present — linking surface |
| Sibling `detps2/tools/bios-extract/SYSCLIB.bin` / `HEAPLIB.bin` | **Absent** — not extracted |
| Sibling `detps2/tools/bios-decomp/SYSCLIB*_ALL.txt` / `HEAPLIB*` | **Absent** — no Ghidra dump |
| ps2sdk `iop/system/sysclib` (`sysclib.h`, `exports.tab`) | Fetched — ordinal map v1.1 |
| ps2sdk `iop/system/heaplib` (`heaplib.h`, `heaplib.c`, `exports.tab`) | Fetched — SCE SDK 1.3.4 recreation |
| Existing `IrxLoader` export/import format, `IopModuleHost`, `BiosBootHost` | Present |
| Existing SYSMEM EE RPC freelist (`RealSifRpc` sid `0x80000003`) | Present — parallel contract |

SYSCLIB and HEAPLIB are **IOPBTCONF early residents** (before THREADMAN). They export LOADCORE libraries that nearly every later IRX imports (`memcpy` / `sprintf` / `CreateHeap` family). There is **no EE SIF RPC sid** — pure IOP export tables.

| Side | Owner | Surface |
|------|-------|---------|
| `sysclib` export table + MIPS bodies | Real SYSCLIB.IRX (not executed) | HLE: non-null stubs + registry |
| `heaplib` export table + freelist | Real HEAPLIB.IRX (not executed) | HLE: stubs + C# freelist |
| CreateHeap → AllocSysMemory | Real HEAPLIB → SYSMEM | HLE private page pool (SYSMEM-shaped) |
| LinkImports consumers | LOADCORE / `IopModuleHost.LoadIrx` | Registry lookup by lib name |

---

## 1. Real contracts (ground truth)

### 1.1 SYSCLIB — library `sysclib` v1.1

From ps2sdk `exports.tab` / `sysclib.h` `DECLARE_IMPORT` ordinals:

| Ord | Symbol | Notes |
|----:|--------|-------|
| 0–3 | `_start` / `_retonly` ×3 | IRX entry boilerplate |
| 4–5 | `setjmp` / `longjmp` | |
| 6–9 | `_toupper` / `_tolower` / ctype table | |
| 10–17 | `memchr`…`bzero` | mem* + BSD b* |
| 18–19 | `prnt` / `sprintf` | |
| 20–38 | `strcat`…`strtoul` | string + parse (`atob` @37) |
| 39 | `_retonly` | gap |
| 40–41 | `_wmemcopy` / `_wmemset` | 32-bit word mem ops |
| 42–43 | `vsprintf` / `strtok_r` | later SDK |
| 44 | `_retnegativeone` | returns −1 |

**45 function pointers**, NUL-terminated after last entry. Import major version **1**.

### 1.2 HEAPLIB — library `heaplib` v1.1

| Ord | Symbol | Notes |
|----:|--------|-------|
| 0–3 | `_start` / `_retonly` ×3 | |
| 4 | `CreateHeap(size, flag)` | `AllocSysMemory` backend; NULL on fail |
| 5 | `DeleteHeap(heap)` | free backend + sub-chunks |
| 6 | `AllocHeapMemory(heap, n)` | first-fit in heap; NULL on fail |
| 7 | `FreeHeapMemory(heap, ptr)` | 0 ok; −1/−2/−3/−4 errors |
| 8 | `HeapTotalFreeSize(heap)` | free bytes or −4 |
| 9–10 | `_retonly` | |
| 11 | `HeapPrepare(mem, size)` | init chunk over raw memory |
| 12–14 | internal chunk helpers | `is_valid` / `do_allocate` / `do_iterate` |
| 15 | `HeapChunkSize(chunk)` | free bytes in prepared chunk |
| 16–17 | `_retonly` | |

**18 function pointers.** CreateHeap uses SYSMEM `AllocSysMemory((flag&2)!=0, rounded, 0)` — DetPS2 HLE approximates with a dedicated 256-byte page pool (same quanta as `RealSifRpc` iopheap).

### 1.3 LOADCORE linking requirement

Unresolved import stubs are `addiu zero,zero,ORDINAL`. Resolution patches word0 to `J exports[ordinal]` when the library is registered with matching major version; otherwise **`jr ra`** (safe no-op).

Without SYSCLIB/HEAPLIB in the export registry, every importer of `memcpy`/`CreateHeap` etc. silently links to no-ops even though boot “registered” the module **name**.

---

## 2. DetPS2 surface (this port)

| API / surface | Location | Behavior |
|---------------|----------|----------|
| Host | `IopSysclibHeaplibHost` | Plant + HEAPLIB freelist |
| Export registration | `IopModuleHost.RegisterExportLibrary` | Last-wins by name |
| Lookup | `IopModuleHost.LookupExportLibrary` | Version-major filter |
| Boot plant | `BiosBootHost.FinishIopServices` | After INTRMAN/VBLANK plant |
| Module names | `RegisterModule("SYSCLIB"/"HEAPLIB", systemResident: true)` | MODLOAD search |
| Stub region | IOP phys `0x4000`..`0x6000` (EE `0x1C004000`) | `jr ra; nop` per ordinal + `0x41C00000` tables |
| HEAPLIB pool | IOP phys `0x170000`..`0x180000` (64 KiB) | SYSMEM-shaped pages for CreateHeap |
| Create/Alloc/Free | C# methods on host | Host-side / smoke / future intercept |
| SYSCLIB MIPS bodies | Shared retonly stubs | Not R3000-executed yet |

### Intentional HLE divergences

| Divergence | Why |
|------------|-----|
| SYSCLIB is stubs, not real libc | Project does not execute BIOS IRX on R3000; linking needs non-null targets only |
| HEAPLIB pool separate from `RealSifRpc` iopheap | Avoid private freelist coupling; same 256-byte page contract |
| No shared freelist with live IRX layout | Full SYSMEM ownership map still a SYSMEM remaining item |
| `stdio` table from sysclib module not planted | Optional; STDIO is separate IOPBTCONF entry / low priority |
| Internal heaplib ordinals 12–14 are retonly stubs | C# path covers public Create/Alloc/Free/Prepare |

---

## 3. Landed this agent (2026-07-30)

1. **`IopSysclibHeaplibHost`** — plant 45 `sysclib` + 18 `heaplib` non-null exports; in-RAM `0x41C00000` tables for `ScanExports`.
2. **`IopModuleHost.RegisterExportLibrary` / `LookupExportLibrary`** — HLE path for LOADCORE registry without loading IRX bytes.
3. **`BiosBootHost.FinishIopServices`** installs SYSCLIB/HEAPLIB after commercial IOP bring-up (with or without BIOS image).
4. **HEAPLIB freelist HLE** — CreateHeap / AllocHeapMemory / FreeHeapMemory / HeapTotalFreeSize / DeleteHeap / HeapPrepare with first-fit, hole reuse, error codes.
5. **Smokes** — `SysclibHeaplib_ExportTablesAndLinkImports`, `SysclibHeaplib_HeapCreateAllocFreeContracts`.
6. **Zero game hacks** / no Midway / no title PCs.

---

## 4. Remaining gaps

Ordered by contract value:

1. **Extract + Ghidra** retail `SYSCLIB.bin` / `HEAPLIB.bin` — confirm ordinal counts/versions vs ps2sdk recreation (esp. later `vsprintf`/`strtok_r` presence on SCPH70008).
2. **Real SYSCLIB MIPS** (or host intercept) for hot paths: `memcpy`/`memset`/`sprintf`/`strcmp`/`strlen` if R3000 IRX execution begins calling through linked stubs.
3. **Unify HEAPLIB CreateHeap with `RealSifRpc` AllocSysMemory** freelist so EE iopheap + IOP heaplib share one ownership map with IRX placement.
4. **Plant optional `stdio` export table** (sysclib module also registers a stub stdio in ps2sdk).
5. **R3000 execution of real SYSCLIB/HEAPLIB IRX** — retires stub bodies and private pool when BIOS modules run for real.
6. **SaveState** for HEAPLIB live heaps (not required until a title depends on IOP-side heap across save).

---

## 5. Acceptance for this slice

- `StartCommercialIop` → `ExportRegistry` contains `sysclib` v1.x (45 ptrs) and `heaplib` v1.x (18 ptrs), all non-null.
- `LinkImports` of synthetic importers patches `J` to those pointers (not unresolved `jr ra`).
- `ScanExports` over the stub region finds both planted tables.
- CreateHeap/Alloc/Free/reuse/double-free contracts green in smoke.
- No commercial title hacks; worktree left uncommitted for orchestrator merge.
