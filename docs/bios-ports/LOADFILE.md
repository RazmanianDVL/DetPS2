# LOADFILE port — gap analysis (Phase 0)

**Agent:** LOADFILE (generic BIOS HLE only — zero title PCs / commercial hacks)  
**Date:** 2026-07-30  
**Worktree:** `subagent-019fb1ac-1005-75d3-8630-f2330f7bd862`

## Authority

| Source | Status in this worktree |
|--------|-------------------------|
| `docs/BIOS_DISSECTION.md` §1 LOADFILE, §6, §7 | Present |
| `tools/bios-decomp/LOADFILE_ALL.txt` | Available under sibling `detps2/tools/bios-decomp/` (gitignored; ~1022 lines) |
| `tools/bios-extract/LOADFILE.bin` | Available under sibling extract tree (10065 B) |
| ps2sdk `common/include/loadfile-common.h` | Fetched (enum `_lf_functions` + arg structs) |
| ps2sdk `ee/kernel/src/loadfile.c` | Fetched (client wire shapes / recv sizes) |
| ps2sdk `common/include/ps2lib_err.h` | Fetched (`E_LF_*` / `E_IOP_NO_MEMORY`) |
| Existing `HandleLoadFile` / `IrxLoader` / `IopModuleHost.LoadIrx` | Present |

LOADFILE.IRX strings (from decomp symbols): `Load File service.(99/11/05)`, `loadmodule:`, `loadelf:`, `LoadModuleByEE`. Registers RPC **sid=0x80000006** at init (`FUN_000000c8` → `sceSifRegisterRpc(..., 0x80000006, FUN_000004c4, ...)`).

## Real contracts (ground truth)

### RPC service

- **sid** `0x80000006` (`RealSifRpc.SidLoadFile`)
- BIOS dispatch table at module `DAT_00001bc8` (file-verified from `LOADFILE.bin`):

| fno | Handler | Role |
|-----|---------|------|
| 0 | `FUN_00000150` | `LF_F_MOD_LOAD` — path module load |
| 1 | `FUN_00000240` | `LF_F_ELF_LOAD` — EE ELF load → epc/gp |
| 2 | `FUN_00000420` | `LF_F_SET_ADDR` — IOP poke |
| 3 | `FUN_00000364` | `LF_F_GET_ADDR` — IOP peek |
| 4 | `FUN_000001fc` | `LF_F_MG_MOD_LOAD` |
| 5 | `FUN_000002fc` | `LF_F_MG_ELF_LOAD` |

BIOS table only covers **fno &lt; 6** (`FUN_000004c4`). Later fnos (6–10, 0xFF) are modern/XLOADFILE extensions still used by ps2sdk clients and retail EE libs — HLE implements them.

### Arg / reply layouts (`loadfile-common.h` + client)

| fno | Arg struct | Reply |
|-----|------------|-------|
| MOD_LOAD / MG_MOD_LOAD | `_lf_module_load_arg`: arg_len@+0, modres@+4, path[252]@+8, args[252]@+260 | **8B** `{ result, modres }` |
| ELF_LOAD / MG_ELF_LOAD | `_lf_elf_load_arg`: epc@+0, gp@+4, path@+8, secname@+260 | **16B** `t_ExecData` `{epc,gp,sp,dummy}`; client treats **epc==0** as miss |
| SET_ADDR / GET_ADDR | `_lf_iop_val_arg`: iop_addr@+0, type@+4, val@+8 | **4B** result (SET always 0; GET = value) |
| MOD_BUF_LOAD | `_lf_module_buffer_load_arg`: ptr@+0, arg_len/modres@+4, args@+260 | **8B** `{ result, modres }` |
| MOD_STOP | id@+0, arg_len@+4, args | **8B** |
| MOD_UNLOAD | id@+0 | **4B** |
| SEARCH_BY_NAME | name@+8 | **4B** id or −1 |
| SEARCH_BY_ADDRESS | ptr@+0 | **4B** id or −1 |
| GET_VERSION | (none) | **4B** version |

### Error codes (decomp + `ps2lib_err.h`)

| Code | Value | When (decomp) |
|------|-------|----------------|
| E_LF_NOT_IRX | **−201** (`0xffffff37`) | path check fail / not ELF |
| E_LF_FILE_NOT_FOUND | **−203** (`0xffffff35`) | open fail (`Cannot openfile`) |
| E_LF_FILE_IO_ERROR | **−204** (`0xffffff34`) | read/path I/O fail |
| E_IOP_NO_MEMORY | **−400** (`0xfffffe70`) | heap/read buffer alloc fail |

## Current DetPS2 surface (post-this-port)

| Area | Status | Notes |
|------|--------|-------|
| sid bind | OK | `SidLoadFile` known; not counted as unknown bind |
| MOD_LOAD path | OK | disc IRX → `LoadIrx` + export/import link; rom0 soft register; empty → −201; missing cdrom → −203 |
| MOD_BUF_LOAD | OK | IOP RAM window copy → `IrxLoader`; bad magic → −201 |
| ELF_LOAD | OK | disc EE ELF via `ElfLoader.LoadElfDetailed`; reply `{epc,gp,sp,0}` |
| SET/GET_ADDR | OK | byte/half/word on IOP physical / 0x1C mapped |
| SEARCH_BY_NAME | OK | name@+8 |
| SEARCH_BY_ADDRESS | OK | match `LoadedIrx.LoadBase` window |
| MOD_STOP / UNLOAD | Partial | stop soft-0; unload drops registry (no IOP RAM reclaim) |
| GET_VERSION | OK | `0x00020000` placeholder (or IOPRP ASCII when PreferIopRp) |
| MG_* plain ELF | OK | Shares plain path loader; SECRMAN classify passthrough (Phase 3) |
| MG_* encrypted | Partial | Non-ELF disc bytes → clear fail (`LfErrNotIrx` / epc=0); **no MagicGate decode** |
| IOPRP/DNAS `.IMG` | OK | ROMDIR-in-IMG parse + IOPBTCONF + LoadIrx (Phase 3) |
| Module `_start` / modres | Gap | HLE does not execute IOP module start; **modres always 0** |
| Literal LOADFILE ELF loader | Gap | EE load uses `ElfLoader`, not decomp `FUN_000010dc` transliteration |
| XLOADFILE extended surface | Gap | only ps2sdk-documented fnos |
| ROMDIR `rom0:` file bytes via LOADFILE | Gap | presence via `RegisterModule` / BiosBootHost, not raw ROM content |

## Phase 3 (AGENT-U) — **DONE 2026-07-30**

1. `LF_F_MG_MOD_LOAD` / `LF_F_MG_ELF_LOAD` share plain path load. ✅  
2. When disc bytes present: `IopExtendedBiosHost.ClassifySecrBoot` — plain ELF OK; non-ELF → clear reject. ✅  
3. IOPRP/DNAS image MOD_LOAD → `ApplyIopRpImageBytes`. ✅  
4. Smoke: `BiosUdnl_IopRpImageApplyAndSecrMgPath`. ✅  
5. **No fake MagicGate secrets.** ✅  

## Scope for Phase 0 agent — **DONE 2026-07-30**

1. Full fno constants + error codes from decomp/ps2sdk. ✅  
2. Deepen `HandleLoadFile`: correct arg/reply shapes for MOD/ELF/SET/GET/BUF/SEARCH/UNLOAD/VERSION. ✅  
3. Coordinate with existing `IopModuleHost.LoadIrx` / `IrxLoader` export-import linking (no break). ✅  
4. Smoke: `RealSifRpc_LoadFileModuleElfSetGetSearch`. Full suite green. ✅  
5. Document remaining gaps; **zero game hacks**. ✅  

## Out of scope (orchestrator / later)

- Per-game MidwayBootAssist / title PCs.  
- Real IOP execution of loaded IRX `_start` (modres from live start).  
- MagicGate decrypt path for MG_MOD/MG_ELF (residual — honest fail only).  
- Full ROMDIR byte serving for `rom0:` path loads.
