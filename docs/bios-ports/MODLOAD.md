# MODLOAD port — gap analysis (Phase 0 → contract HLE)

**Agent:** MODLOAD (generic BIOS HLE only — zero title PCs / commercial hacks)  
**Date:** 2026-07-30  
**Worktree:** `subagent-019fb1ac-1006-7032-912f-7135234c6f8b`

## Authority

| Source | Status |
|--------|--------|
| `docs/BIOS_DISSECTION.md` §1 MODLOAD, §2 IOPBTCONF, §6.5 LOADCORE | Present |
| Sibling `detps2/tools/bios-decomp/MODLOAD_ALL.txt` | Available (gitignored decomp) |
| Sibling `detps2/tools/bios-extract/MODLOAD.bin` | 9025 B, export table `modload` v1.1 (16 funcs) |
| ps2sdk `iop/system/modload/include/modload.h` | Fetched |
| ps2sdk `iop/system/loadcore/include/loadcore.h` (`ModuleInfo_t`) | Fetched |
| ps2sdk `common/include/loadfile-common.h` (EE LOADFILE RPC fnos) | Fetched |
| Existing LOADCORE port (`IrxLoader.ScanExports`/`LinkImports`) | **Do not regress** |

MODLOAD sits in IOPBTCONF **after IOMAN**. It is the IOP-side module loader; EE games usually talk through **LOADFILE** RPC `sid=0x80000006`, which calls into MODLOAD (`LoadStartModule` etc.). DetPS2 split:

| Side | Owner | Surface |
|------|-------|---------|
| IOP module table / load / start / stop / unload / search | **MODLOAD agent** | `IopModuleHost` |
| EE RPC wire decode (`_lf_*_arg`) | LOADFILE agent | `RealSifRpc.HandleLoadFile` |
| Cross-module export/import linking | LOADCORE (already ported) | `IrxLoader` + `LoadIrx` |

## Real contracts (ground truth)

### Export library (`modload` v1.1, 16 funcs — real `MODLOAD.bin` @ export magic `0x41C00000`)

| Ordinal | Symbol (ps2sdk / decomp) | Decomp entry |
|--------:|--------------------------|--------------|
| 4 | `ReBootStart` | `FUN_00001518` → `FUN_000012e0` |
| 5 | `LoadModuleAddress` | `FUN_000001bc` |
| 6 | `LoadModule` | `FUN_000001f0` |
| 7 | `LoadStartModule` | `FUN_0000026c` |
| 8 | `StartModule` | `FUN_00000358` |
| 9 | `LoadModuleBufferAddress` | `FUN_00000214` |
| 10 | `LoadModuleBuffer` | `FUN_00000248` |
| 11 | `LoadStartKelfModule` | `FUN_00000440` |
| 15 | `IsIllegalBootDevice` | `FUN_00000bb8` (mc/hd/net/dev) |

Dispatcher `FUN_000005a0`: cmd 1=load, 2=start-by-id, 3=loadstart, 4=buffer-load.  
Search-by-id walks LOADCORE `image_info` list (`FUN_0000070c`, id at `ModuleInfo_t +0x0C`).

### `ModuleInfo_t` (loadcore.h) — fields DetPS2 tracks

| Offset | Field | HLE field |
|--------|-------|-----------|
| +0x00 | next | table order by id |
| +0x04 | name* | `LoadedIrx.Name` |
| +0x0C | id (u16) | `LoadedIrx.Id` |
| +0x10 | entry | `LoadedIrx.Entry` |
| +0x18 | text_start | `LoadedIrx.LoadBase` |
| sizes | text/data/bss | `LoadedIrx.Size` (extent) |

### `_start` return codes (loadcore.h)

| Value | Name | Unload |
|------:|------|--------|
| 0 | `MODULE_RESIDENT_END` | refuse |
| 1 | `MODULE_NO_RESIDENT_END` | ok after stop |
| 2 | `MODULE_REMOVABLE_END` | ok after stop |

### Error codes (decomp / LOADFILE)

| Value | Hex | When |
|------:|-----|------|
| -202 | `0xFFFFFF36` | StartModule: id not found |
| -201 | `0xFFFFFF37` | Illegal boot device; cannot unload resident/system |

### EE LOADFILE RPC fnos (loadfile-common.h) → IOP

| fno | Name | IOP action |
|----:|------|------------|
| 0 | `LF_F_MOD_LOAD` | LoadStartModule(path) |
| 6 | `LF_F_MOD_BUF_LOAD` | LoadModuleBuffer + start |
| 7 | `LF_F_MOD_STOP` | StopModule(id) |
| 8 | `LF_F_MOD_UNLOAD` | UnloadModule(id) |
| 9 | `LF_F_SEARCH_MOD_BY_NAME` | SearchModuleByName |
| 10 | `LF_F_SEARCH_MOD_BY_ADDRESS` | SearchModuleByAddress |

## Pre-port DetPS2 surface

| Area | Status | Gap |
|------|--------|-----|
| Name registry | OK | `RegisterModule` / `TryGetModule` only — no lifecycle |
| IRX load + LOADCORE link | OK | Full port §6.5; no start/stop state |
| LOADFILE load/search-name | Partial | Worked; stop/unload were fake `0` |
| Search by address | Missing | |
| Illegal boot device | Missing | |
| Module start order | Missing | |
| `modres` on load | Always 0 | Now reflects `LastModRes` |
| ReBootStart / KELF / Secr callbacks | Not ported | Out of scope |

## Landed this agent (2026-07-30)

1. **`LoadedIrx` module table** with `State` / `StartOrder` / `LastModRes` / `HasImage` / `SystemResident` / `Size`.
2. **`IopModuleHost` MODLOAD API:**
   - `RegisterModule` (optional `systemResident`) → Started HLE entry  
   - `LoadIrx` → load + LOADCORE link + **implicit Start** (LoadStartModule)  
   - `StartModule` / `StopModule` / `UnloadModule`  
   - `SearchModuleByName` / `SearchModuleByAddress` (EE-mapped or IOP phys)  
   - `GetModuleIdList` / `GetModuleTable`  
   - `IsIllegalBootDevice` (FUN_00000bb8)  
3. **`RealSifRpc.HandleLoadFile`** forwards stop/unload/search-by-address; refuses mc/hd/net/dev loads with `0xFFFFFF37`; fills `modres` from table.
4. **NormalizeName** strips `device:` and `;version` so `rom0:FOO` / `FOO.IRX` share one id.
5. **Smokes:** `Modload_ModuleTableStartOrderSearchStopUnload`, `RealSifRpc_LoadFile_SearchStopUnloadContracts`.
6. **InitDefaults** modules marked system-resident (unload refuses).

## Remaining gaps

| Item | Notes |
|------|-------|
| Literal IRX `_start` execution | HLE always returns `MODULE_RESIDENT_END` (0) — no R3000A module entry |
| ReBootStart / IOPBOOT | Decomp `FUN_000012e0` — not needed for commercial fast-path |
| KELF / SecrMan callbacks | Encrypted module path; soft-success only on EE MG fnos |
| Stop/Unload export ordinals on modload &gt;1.2 | BIOS MODLOAD is v1.1; newer games use xmodload — EE LOADFILE still covers stop/unload |
| ROMDIR full-ROM gate | Orchestrator-owned |
| LOADCORE image_info in IOP RAM | Bookkeeping only in C#; not a real linked list at 0x800 |
| Per-module version / flags from `.iopmod` | Partial via `IrxLoader.LoadResult`; not yet on `LoadedIrx` |

## Out of scope

- MidwayBootAssist / title PCs  
- Changing LOADCORE link algorithm  
- Full ROMDIR commercial BIOS execution  

## Files touched

- `src/DetPS2.Core/SifRpc.cs` — module table + MODLOAD contracts  
- `src/DetPS2.Core/RealSifRpc.cs` — LOADFILE stop/unload/search-addr/illegal device  
- `Tests/SmokeTests.cs` — two new smokes  
- `docs/bios-ports/MODLOAD.md` — this file  
- `docs/BIOS_DISSECTION.md` — §6.9 note  
