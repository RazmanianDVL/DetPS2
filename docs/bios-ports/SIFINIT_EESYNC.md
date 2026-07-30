# SIFINIT + EESYNC port — gap analysis (Phase 0 + landed)

**Agent:** SIFINIT+EESYNC (generic BIOS HLE only — zero title PCs / commercial game assists)  
**Date:** 2026-07-30  
**Worktree:** `subagent-019fb1ac-1008-73a2-aba6-fb6e47099e57`

## Authority

| Source | Status |
|--------|--------|
| `docs/BIOS_DISSECTION.md` §1–2 (ROMDIR / IOPBTCONF), §3 SIFCMD, §6.3 SIFMAN, §6.6 SIFINIT/EESYNC note | Present |
| `tools/bios-decomp/SIFINIT_ALL.txt` | Sibling `detps2/tools/bios-decomp/` (gitignored binaries); 3 funcs, import stubs |
| `tools/bios-decomp/EESYNC_ALL.txt` | Same; 5 funcs, SyncEE posts `0x40000` |
| `tools/bios-decomp/SIFCMD_ALL.txt` | INIT/SET_SREG/handlers |
| `tools/bios-decomp/SIFMAN_ALL.txt` | Ground-truthed only — **not** a literal port (§6.3) |
| `tools/bios-extract/SIFINIT.bin` / `EESYNC.bin` | Strings + raw MIPS entry (imports to loadcore/sifman/stdio) |
| ps2sdk `ee/kernel/include/sifdma.h` | `SIF_STAT_*`, `SIF_REG_*`, `SIF_SYSREG_*` |
| ps2sdk `ee/kernel/src/sifcmd.c` | `sceSifInitCmd` CMDINIT wait + SUBADDR |
| ps2sdk `ee/kernel/src/sifrpc.c` | `sceSifInitRpc` INIT_CMD + `SIF_SREG_RPCINIT` / `SIF_SYSREG_RPCINIT` |
| ps2sdk `ee/kernel/src/iopcontrol.c` | `SifIopReset` W1C SMFLAG + `SifIopSync` BOOTEND |

**BIOS_DISSECTION §6.6** correctly notes SIFINIT/EESYNC are tiny bootstrap wrappers — not full IRX ports. This agent deepens **init/sync contracts** only (EE-visible SMFLAG / SUBADDR / RPCINIT / ready slots / reboot sequencing). `Sif.cs` DMA transport remains the functional stand-in for SIFMAN.

## IOPBTCONF placement

```
… → SIFMAN → IGREETING → SIFCMD → REBOOT → LOADFILE → CDVDMAN → CDVDFSV → SIFINIT → FILEIO
```

`EESYNC` is a ROMDIR sibling (export library `eesync` / `SyncEE`); not always a line in the `@800` IOPBTCONF text blob, but commercial handoff requires its **BOOTEND post** before EE `SifIopSync` / late boot waiters succeed.

## Decomp contracts (ground truth)

### SIFINIT.IRX (`SIFINIT.bin` 1041 B)

Strings: `Skip SIF init`, `Skip SIF init (it is DECI1)`, imports `loadcore` / `stdio` / `sifman`.

Entry (raw MIPS @ text):

1. Query boot-mode / library state (`a0=3` import) — if already marked init → printf skip, return 1.
2. DECI1 check (`a0=1`) — if DECI1 → skip string, return 1.
3. Else call sifman init import → return 1 (resident).

**HLE meaning:** idempotent ensure of `SIF_STAT_SIFINIT` (`0x10000`). No independent RPC sid.

### EESYNC.IRX (`EESYNC.bin` 1177 B)

- Library name `eesync`, export label `SyncEE`.
- `FUN_0000007c` / SyncEE: `FUN_000000f4(0x40000)` then return 0 → **posts `SIF_STAT_BOOTEND`** through sifman.
- Module start: boot-mode flags; register library; register reboot/post callback to SyncEE.

**HLE meaning:** `PostBootEnd()` ORs `0x40000` onto SMFLAG. After IOP reboot, SyncEE must run again so `SifIopSync` observes BOOTEND.

### SIFMAN (not ported)

`FUN_00000148` sets `*(0xBD000030) = 0x10000` (SMFLAG SIFINIT) after SBUS/DMA bring-up. DetPS2 keeps abstract `Sif.SmFlag` instead of raw IOP DMAC pokes (§6.3).

### SIFCMD residual init (in scope only as ready-slot gaps)

| CID | Name | EE-visible effect needed |
|-----|------|---------------------------|
| `0x80000000` | CHANGE_SADDR | Store EE/IOP cmd buffer pointer |
| `0x80000001` | SET_SREG | Software sreg; index 0 = `SIF_SREG_RPCINIT` |
| `0x80000002` | INIT_CMD | opt==0 → CMDINIT + SUBADDR; opt!=0 → RPCINIT |
| `0x80000003` | RESET_CMD | IOP reboot; EE then W1C clears flags |

`sceSifInitCmd` (ps2sdk): wait `SMFLAG & CMDINIT`, read `SIF_REG_SUBADDR`.  
`sceSifInitRpc`: if `SIF_SYSREG_RPCINIT` already set, return; else INIT_CMD and wait `SifGetSreg(RPCINIT)`.

### SMFLAG write-1-to-clear

`SifIopReset` (ps2sdk):

```c
sceSifSetReg(SIF_REG_SMFLAG, SIF_STAT_BOOTEND);   // clear BOOTEND
sceSifSetDma(... RESET_CMD ...);
sceSifSetReg(SIF_REG_SMFLAG, SIF_STAT_SIFINIT);  // clear SIFINIT
sceSifSetReg(SIF_REG_SMFLAG, SIF_STAT_CMDINIT);  // clear CMDINIT
sceSifSetReg(SIF_SYSREG_RPCINIT, 0);
sceSifSetReg(SIF_SYSREG_SUBADDR, 0);
// later: SifIopSync → SMFLAG & BOOTEND  (EESYNC re-post)
```

HLE must **not** re-assert SIFINIT during RESET_CMD DMA completion before EE clears; re-post on the next SMFLAG **GetReg** (deferred reboot completion).

## Pre-this-port DetPS2 surface

| Area | Status | Gap |
|------|--------|-----|
| SMFLAG bits at `Sif.Reset` | Present (always OR on GetReg) | GetReg ignored real W1C; reboot sequencing wrong |
| `BiosBootHost` module names SIFINIT/EESYNC | Registered | Roles underspecified; no SyncEE API |
| EE ready slots `0x00778800` | Planted at boot + ack | Named only in Midway comments; not shared helper |
| SUBADDR (reg 2) | Often 0 | `sceSifInitCmd` needs non-zero IOP cmd buffer |
| SYSREG_RPCINIT | Unplanted | `sceSifInitRpc` may spin on INIT path |
| INIT_CMD | Set CMDINIT only | No opt==RPCINIT / SUBADDR publish |
| RESET_CMD | Almost no-op | No deferred EESYNC BOOTEND re-post |
| SIFMAN literal port | Out of scope | Remains §6.3 ground-truth only |

## Landed this pass

### `Sif.cs` — init/sync contract API

- `SifStatIopBootReady`, reg/sysreg constants, `DefaultIopSifCmdBufAddr`, `EeSifReadySlotBase`
- `ApplySifInit()` / `ApplyCmdInit()` / `PostBootEnd()` / `PresentIopBootReady()`
- `ClearSmFlagBits` (W1C)
- `MarkIopRebootPending` / `TryCompletePendingIopReboot` / `IopRebootGeneration`
- `PlantEeSifReadySlots(mem)`

### `SonyKernelHle.cs`

- `PlantSifInitSyncContracts()` — SMFLAG ready, SUBADDR, SYSREG_SUBADDR, SYSREG_RPCINIT, EE slots
- `SifSetReg(SMFLAG)` → write-1-to-clear
- `SifGetReg(SMFLAG)` → live `SmFlag` + deferred reboot complete (EESYNC)
- INIT_CMD opt-aware; RESET_CMD pending reboot; SET_SREG RPCINIT mirror
- `AcknowledgeEeSifCmdReady` respects pending reboot (does not thrash SMFLAG)

### `BiosBootHost.FinishIopServices`

- Explicit SIFINIT → CMDINIT → EESYNC PostBootEnd sequence + `PlantSifInitSyncContracts`

### `BiosHle` homebrew `SysSifInit`

- Applies the same contracts (no game PCs)

### Smoke

- `BiosHle_SifInitEeSyncContracts` — boot flags, skip-init, SUBADDR/RPCINIT, W1C, RESET→deferred BOOTEND, INIT_CMD RPCINIT

## Remaining SIF-stack ROMDIR gaps (not this agent)

| Module / gap | Notes |
|--------------|--------|
| **SIFMAN** | Still not a port target — needs real IOP DMAC/SBUS if ever literal |
| **SIFCMD** transport | BIND/CALL/RDATA/RPC_END already in `RealSifRpc` (other agents); EE software `sregs[]` table not fully modeled (SYSREG_RPCINIT stands in) |
| **REBOOT.IRX** | IOP-side reboot helper beyond RESET_CMD EE contract |
| EE `_SifCmdIntHandler` path | Still HLE side effects instead of real SIF0 DMAC → handler |
| Alternate EE ready-slot BSS bases | Only common `0x00778800` planted; other SDK layouts may differ |
| IOPBTCON2 short path | No SIFCMD/LOADFILE/FILEIO — separate bring-up profile |
| Literal IRX execution | SIFINIT/EESYNC R3000 still not run; contracts only |

## Acceptance checklist

- [x] Full project scan (dissection + decomp + Sif/Sony/BiosBoot + ps2sdk)
- [x] Generic BIOS HLE only (no MidwayBootAssist / title PCs)
- [x] SIFINIT idempotent + EESYNC BOOTEND post + reboot sequencing
- [x] EE ready slots + SUBADDR + RPCINIT for `sceSifInitRpc` handshake
- [x] Smoke `BiosHle_SifInitEeSyncContracts`
- [x] No RealSifRpc PAD/FILEIO/CDVD handler thrash
- [ ] Worktree left uncommitted for orchestrator merge
