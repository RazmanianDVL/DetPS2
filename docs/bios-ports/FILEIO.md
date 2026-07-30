# FILEIO port — gap analysis (Phase 0)

**Agent:** FILEIO (generic BIOS HLE only — zero title PCs / commercial hacks)  
**Date:** 2026-07-30  
**Worktree:** `subagent-019fb19d-45ee-7821-b4b0-3821c144c98e`

## Authority

| Source | Status in this worktree |
|--------|-------------------------|
| `docs/BIOS_DISSECTION.md` §1 FILEIO, §6.2 IOMAN, §7 | Present |
| `tools/bios-decomp/FILEIO_ALL.txt` | **Absent** (only `FILEIO.bin` 8437 B in sibling `detps2/tools/bios-extract/`) |
| `tools/bios-decomp/IOMAN_ALL.txt` | Available under sibling `detps2/tools/bios-decomp/` (gitignored) |
| `tools/bios-decomp/LOADFILE_ALL.txt` | Available (path loads only; not FILEIO RPC) |
| ps2sdk `common/include/fileio-common.h` | Fetched (FIO_F_* + arg structs) |
| ps2sdk `ee/kernel/src/fileio.c` | Fetched (client wire shapes) |
| ps2sdk `common/include/io_common.h` | Fetched (`io_stat_t` / `io_dirent_t`) |
| Existing smokes `BiosHle_FileIo*`, `IopModules_FileDescriptorTableRealBound` | Present |

FILEIO.IRX strings (from `FILEIO.bin`): `FILEIO_service`, `open name %s flag %x`, `dopen name %s`, `sce_fileio: unrecognized code %x`, imports `ioman` / `sifcmd` / `sysmem`. FILEIO is a thin EE↔IOP RPC shell over IOMAN — **IOMAN fd/error contracts are the ground truth** when FILEIO decomp is missing.

## Real contracts (ground truth)

### RPC service

- **sid** `0x80000001` (`RealSifRpc.SidFileIo`) — also SIFCMD *command* namespace for SET_SREG; distinct spaces.
- **fno** (`enum _fio_functions`): OPEN=0 … FORMAT=14 (+ ADDDRV/DELDRV 15/16 in header, not required for disc boot).

### Arg layouts (`fileio-common.h` + `fileio.c`)

| fno | Struct | Layout |
|-----|--------|--------|
| OPEN | `_fio_open_arg` | `int mode` @+0; `char name[256]` @+4 |
| CLOSE | `int fd` | @+0 |
| READ | `_fio_read_arg` | `fd` @+0; `void *ptr` @+4; `int size` @+8; `read_data*` @+12 |
| WRITE | `_fio_write_arg` | `fd`; `ptr`; `size`; mis + aligned[16] |
| LSEEK | `_fio_lseek_arg` | `fd` @+0; `offset` @+4; `whence` @+8 |
| GETSTAT | `_fio_getstat_arg` | `io_stat_t *buf` @+0; `char name[256]` @+4 |
| DOPEN | path inline | `char name[256]` @+0 |
| DCLOSE | `int fd` | @+0 |
| DREAD | `_fio_dread_arg` | `fd` @+0; `io_dirent_t *buf` @+4 |
| REMOVE/MKDIR/RMDIR/FORMAT | path @+0 | |

Reply: **one result s32** in recvbuf (client often reuses arg buffer for 4-byte result).

### `io_stat_t` / `io_dirent_t`

```
io_stat_t: mode u32, attr u32, size u32, ctime[8], atime[8], mtime[8], hisize u32  → 0x28 bytes
io_dirent_t: io_stat_t + name[256] + privdata*  → name at +0x28
```

### IOMAN errnos (Ghidra `IOMAN_ALL.txt`)

| Code | Value | When |
|------|-------|------|
| EMFILE | **-24** (`-0x18`) | 16-slot table full (`FUN_00000b98`) |
| ENODEV | **-19** (`-0x13`) | path device parse fail (`FUN_00000d28`) |
| EBADF | **-9** | invalid fd (`FUN_00000c3c`: `fd > 0xF` or free slot) |
| EINVAL | **-22** (`-0x16`) | lseek whence ∉ {0,1,2} |
| ENOENT | **-2** | common missing-path convention (device driver) |

- Fixed **16-slot** table; **file + dir share** the same pool (`sceOpen`/`sceDopen` both call `FUN_00000b98`).
- Successful open returns **slot index 0..15**, not an unbounded counter.
- `sceClose` zeros the slot; invalid → -9.

## Current DetPS2 surface (pre-this-port)

| Area | Status | Gap |
|------|--------|-----|
| sid bind/call | OK | `SidFileIo` known; `HandleFileIo` wired |
| fno table | OK numbers | OPEN/GETSTAT **arg layout wrong** vs `_fio_open_arg` / `_fio_getstat_arg` |
| ISO open/read | Partial | Works via `IopModuleHost` when disc bound; large files stream by LBA |
| lseek | Partial | OK for SEEK_*; no EINVAL on bad whence; invalid fd → -1 not -9 |
| close | Weak | Always 0; no EBADF |
| getstat | Partial | path-first parsing; size/mode OK when found |
| dopen/dread | Partial | dir fd pool starts at 1000 (not 0..15); dread name at +0x40/+0x20 not **+0x28**; return = index not 1 |
| EMFILE | Partial | files-only count for `FileOpen` (dirs not counted); numbers unbounded |
| host/mc/rom paths | Weak | with disc mounted, non-cdrom missing paths fail as -1; host: probes need O_CREAT |
| ADDDRV/DELDRV / device registry | Deferred | same as BIOS_DISSECTION §6.2 |
| ROMDIR `rom0:` file content via FILEIO | **Gap** | ROMDRV not a real backing store |
| FILEIO literal decomp port | **Gap** | no `FILEIO_ALL.txt` yet |
| r/w staging buffer (`_fio_read_data`) | Partial | HLE writes dest directly (functionally OK for sync reads) |

## Scope for THIS agent — **DONE 2026-07-30**

1. Correct **HandleFileIo** arg decoding to match `fileio-common.h` (with safe fallbacks). ✅
2. Deepen **IopModuleHost** open/read/lseek/close/getstat/dopen/dread:
   - real **0..15 shared fd slots** + EMFILE; ✅
   - **EBADF / EINVAL / ENOENT** where IOMAN-backed; ✅
   - **io_dirent_t** name @ +0x28; dread success = 1; ✅
   - ISO-backed bytes when disc mounted; no fake success for missing disc files. ✅
3. Smokes: `RealSifRpc_FileIoOpenReadLseekCloseAndDir`, extended `BiosHle_FileIoGetstatAndCdvdSectors`,
   extended `IopModules_FileDescriptorTableRealBound`. Full suite green. ✅
4. Documented remaining ROMDIR / device-registry gaps; **zero game hacks**. ✅

## Out of scope (orchestrator / later)

- Per-game MidwayBootAssist / title PCs.
- Full MCMAN/PFS/host0 TCP.
- Literal FILEIO.IRX bytecode execution / `FILEIO_ALL.txt` decomp.
- IOMAN AddDrv/DelDrv registry until multiple real backends exist.
- XFILEIO extended service (same sid HLE is enough for now).
- ROMDRV `rom0:` content serving through FILEIO.
