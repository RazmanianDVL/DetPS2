# REBOOT + STDIO + IGREETING + IOMAN (remaining) — port notes

**Agent:** REBOOT+STDIO+IGREETING+IOMAN-rest (generic BIOS HLE only — zero title PCs / commercial hacks)  
**Date:** 2026-07-30  
**Worktree:** `subagent-019fb1c8-c40e-71b0-b07d-8f18ed6028f2`

## Authority

| Source | Status |
|--------|--------|
| `docs/BIOS_DISSECTION.md` §1–2 (ROMDIR / IOPBTCONF), §6.2 IOMAN | Present |
| `docs/bios-ports/SIFINIT_EESYNC.md` | Present — EE SMFLAG / RESET_CMD deferred complete |
| `docs/bios-ports/FILEIO.md` | Present — fd table done; AddDrv deferred **closed here** |
| `tools/bios-decomp/IOMAN_ALL.txt` | Sibling `detps2/tools/bios-decomp/` — FUN_00000d28 / e8c / f44 |
| `tools/bios-extract/IOMAN.bin` | Strings: `Unknown device`, `tty00:`, `CONSOLE`, `AddDrv` table |
| REBOOT / STDIO / IGREETING `.bin` | **Not extracted** in-tree — contracts from IOPBTCONF order + ps2sdk |
| ps2sdk `ee/kernel/src/iopcontrol.c` | `SifIopReset` / `SifIopReboot` / `SifIopSync` |
| ps2sdk `common/include/fileio-common.h` | `FIO_F_ADDDRV=15`, `FIO_F_DELDRV=16` |
| ps2sdk `iop/system/ioman/include/ioman.h` | `iop_device_t`, `AddDrv` / `DelDrv` |
| Existing `Sif.cs`, `SonyKernelHle`, `BiosBootHost`, `IopSystemHost`, `IopModuleHost` | Present |

## IOPBTCONF placement

```
… → IOMAN → MODLOAD → ROMDRV → STDIO → SIFMAN → IGREETING → SIFCMD → REBOOT → LOADFILE → …
```

| Module | Role in HLE |
|--------|-------------|
| **IOMAN** | 16-slot fd table (prior port) + **AddDrv/DelDrv registry + path parse** (this port) |
| **STDIO** | `printf` / `puts` / tty write → non-fatal log sink |
| **IGREETING** | Early IOP greeting init stub (one-shot banner via STDIO) |
| **REBOOT** | IOP-side reboot helper: capture RESET_CMD arg/mode; complete with SIFINIT+EESYNC handoff |

---

## 1. REBOOT — contracts

### Real behavior (ps2sdk + SIFINIT_EESYNC)

`SifIopReset(arg, mode)` (EE):

1. Build `SifCmdResetData_t`: header `cid=SIF_CMD_RESET_CMD (0x80000003)`, `arglen`, `mode`, `arg[≤80]`.
2. W1C `SMFLAG` BOOTEND, DMA RESET_CMD to IOP SUBADDR.
3. W1C SIFINIT + CMDINIT; clear SYSREG_RPCINIT / SUBADDR.
4. Poll `SifIopSync` → wait `SMFLAG & BOOTEND` (EESYNC re-post after IOP reload).

Empty `arg` = default IOPBTCONF reload. `SifIopReboot(path)` wraps as `"rom0:UDNL …"`.

**REBOOT.IRX** is the IOPBTCONF module that participates in that reload path. DetPS2 does not run R3000 IRX; HLE deepens the **EE-visible contract** already started in SIFINIT_EESYNC:

| API | Location | Behavior |
|-----|----------|----------|
| `MarkIopRebootPending(arg, mode, argLen)` | `Sif.cs` | Store `LastIopRebootArg` / Mode / ArgLen; clear BOOTEND; set pending |
| RESET_CMD packet parse | `SonyKernelHle` | Read arglen@+0x10, mode@+0x14, arg@+0x18 |
| `TryCompletePendingIopReboot` | `Sif.cs` | On SMFLAG GetReg: gen++, re-assert SIFINIT\|CMDINIT\|BOOTEND |
| `OnIopRebootCompleted` | `SonyKernelHle` | Re-publish SUBADDR/RPCINIT; call `BiosBootHost.ApplyPostIopRebootContracts` |
| `ApplyPostIopRebootContracts` | `BiosBootHost` | Re-seed IOMAN devices, STDIO, IGREETING; count `IopRebootHandoffs` |

Diagnostics: `DETPS2_TRACE_REBOOT=1`.

### Intentional HLE divergences

| Divergence | Why |
|------------|-----|
| No real UDNL / IOPRP image parse from reboot arg | No IOP R3000 / no IOPRP loader; arg is captured for probes/tests |
| Full service table not wiped on reboot | Only SMFLAG + device re-seed + SIF contracts; IRX images stay registered |
| REBOOT is name-registered, not executed | Same pattern as SIFINIT/EESYNC contracts |

---

## 2. STDIO — contracts

### Real behavior

STDIO.IRX sits after ROMDRV in IOPBTCONF. Provides IOP `printf` / `puts` / console write, typically backed by the IOMAN `tty` / CONSOLE device (IOMAN itself opens `tty00:` at init — decomp `FUN_00000098`).

### DetPS2 surface

| API | Location | Behavior |
|-----|----------|----------|
| `EnsureStdioDevices` | `IopSystemHost` | Register `tty`, `stderr`, `tty00` |
| `Printf` / `Puts` | `IopSystemHost` | Append to `StdioLog` (256-line ring); never throws |
| `StdioWriteBytes` | `IopSystemHost` | FILEIO write path for tty fds |
| `FileWrite` tty routing | `IopModuleHost` | If path is tty/stderr → sink |
| `ApplyStdioContract` | `BiosBootHost` | Called from commercial bring-up + post-reboot |

Mirror to host stderr only when `DETPS2_IOP_STDIO=1` (default silent — non-fatal log sink).

---

## 3. IGREETING — contracts

### Real behavior

Tiny resident after SIFMAN. Prints an early IOP greeting during boot; no EE RPC sid.

### DetPS2 surface

| API | Location | Behavior |
|-----|----------|----------|
| `ApplyIgreetingContract` | `BiosBootHost` | Register module; one-shot `Printf("IOP: IGREETING ready…")` |
| `IgreetingDone` | `BiosBootHost` | Idempotent flag (stays true across reboot handoff) |

---

## 4. IOMAN remaining — AddDrv / DelDrv / path parse

### Real contracts (`IOMAN_ALL.txt`)

| Function | Role |
|----------|------|
| `FUN_00000d28` | Path parse: skip spaces; require `:`; copy device token; strip trailing digits → unit; lookup device; return path after colon or -1 → open returns **ENODEV (-19)** |
| `FUN_00000e8c` | **AddDrv**: first free slot in device pointer table; call device `init`; fail → free slot, return -1 |
| `FUN_00000f44` | **DelDrv(name)**: match name (`strcmp`); call `deinit`; free slot |
| Device table | Classic IOMAN = **16** slots (iomanX steals 16); HLE capacity **32** (iomanX `MAX_DEVICES`) |
| Fd table | Unchanged **16** shared file+dir slots (prior FILEIO port) |

Built-in: IOMAN adds CONSOLE/`tty` at start (`FUN_000010ac`). Other devices (cdrom, rom, mc, …) are added by their drivers via AddDrv during IOPBTCONF.

### DetPS2 surface

| API | Location | Behavior |
|-----|----------|----------|
| `AddDrv(name, desc?, type, ver)` | `IopSystemHost` | Free slot + name map; idempotent success if name exists |
| `DelDrv(name)` | `IopSystemHost` | Remove map + slot; -1 if missing |
| `TryParseDevicePath` | `IopSystemHost` | Colon parse + unit digits + registry lookup |
| `IsKnownDevicePath` | `IopSystemHost` | Relative (no colon) → true; else parse |
| `InstallBiosDevices` | `IopSystemHost` | Base names + unit aliases + STDIO devices |
| `FileOpen` device check | `IopModuleHost` | Colon path + unknown device → **ENODEV (-19)** when IopSystem bound |
| `FioAddDrv` / `FioDelDrv` | `RealSifRpc` fno 15/16 | Name string → `IopModuleHost.AddDrv/DelDrv` |
| `BindIopSystem` | `IopModuleHost` ← `Ps2System` | Shares live registry + STDIO write |

### Does not break 16-slot fds

AddDrv/DelDrv only touch the **device** table. File/dir opens still use `AllocIoManFd` over 0..15 with EMFILE=-24. Smoke asserts EMFILE still holds after registry ops.

### Intentional HLE divergences

| Divergence | Why |
|------------|-----|
| No real `iop_device_ops` function pointers | No R3000; open/read still routed by path prefix in `IopModuleHost` |
| Max 32 device slots (not 16) | `InstallBiosDevices` seeds many aliases; matches iomanX headroom |
| AddDrv idempotent if name exists | Real may reject duplicate; soft success avoids IRX re-init thrash |
| Relative paths (no `:`) skip ENODEV | Preserves disc-relative probes / existing smokes |
| EE FILEIO AddDrv is name-only | Real AddDrv takes `iop_device_t*`; EE rarely calls fno 15 |

---

## Landed this pass

### Code

- `Sif.cs` — reboot arg/mode capture; trace
- `SonyKernelHle.cs` — RESET_CMD `SifCmdResetData_t` parse; post-reboot `ApplyPostIopRebootContracts`
- `BiosBootHost.cs` — STDIO / IGREETING / REBOOT register; post-reboot handoff counters
- `IopSystemHost.cs` — device slots, AddDrv/DelDrv, path parse, STDIO sink
- `IopModuleHost` (`SifRpc.cs`) — BindIopSystem, ENODEV on open, tty write, AddDrv/DelDrv proxies
- `RealSifRpc.cs` — FIO_F_ADDDRV / DELDRV
- `Ps2System.cs` — bind IOMAN host to FILEIO

### Smoke

- `BiosHle_RebootStdioIgreetingIomanContracts` — modules, STDIO/IGREETING, path parse, ENODEV, AddDrv/DelDrv, EMFILE still 16, RESET_CMD arg + handoff

### Docs

- This file
- Cross-refs: closes FILEIO.md “AddDrv deferred” and SIFINIT_EESYNC.md “REBOOT.IRX” gap at contract level

---

## Remaining (not this agent)

| Gap | Notes |
|-----|--------|
| Literal REBOOT/STDIO/IGREETING IRX execution | Still no R3000 module run; contracts only |
| UDNL / IOPRP image apply on reboot arg | Needs IOP image loader |
| Full `iop_device_ops` dispatch | Open/read still prefix-routed in FILEIO host |
| Classic 16-slot device hard cap | HLE uses 32; can tighten if a title depends on EMFILE-for-devices |
| ROMDIR extract of REBOOT/STDIO/IGREETING bins | Not in `tools/bios-extract/` yet |
| DECI2 / host TTY passthrough | Stdio is log sink only |
| SIFMAN literal / EE `_SifCmdIntHandler` | Still prior SIFINIT_EESYNC remaining list |

## Acceptance checklist

- [x] Full project scan (BIOS_DISSECTION, SIFINIT_EESYNC, FILEIO, Sif reboot path, SonyKernelHle, SifRpc IOMAN, BiosBootHost)
- [x] Generic BIOS HLE only (no MidwayBootAssist / title PCs)
- [x] REBOOT RESET_CMD arg + deferred complete + post handoff
- [x] STDIO non-fatal log sink + tty devices
- [x] IGREETING init stub
- [x] IOMAN AddDrv/DelDrv + path parse + ENODEV without breaking 16-slot fds
- [x] Smoke `BiosHle_RebootStdioIgreetingIomanContracts`
- [ ] Worktree left uncommitted for orchestrator merge
