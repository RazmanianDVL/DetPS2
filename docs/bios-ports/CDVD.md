# CDVD family gap analysis (CDVDMAN / CDVDFSV)

Agent: CDVD  
Date: 2026-07-30  
Authority: `tools/bios-decomp/CDVDFSV_ALL.txt` (main tree; gitignored), `docs/BIOS_DISSECTION.md` §6.1, ps2sdk `ee/rpc/cdvd/src/{ncmd,scmd}.c` + `libcdvd-common.h`, `src/DetPS2.Core/{RealSifRpc,Cdvd,DiscImage,Iso9660,BiosBootHost}.cs`  
Scope: generic CDVDMAN/CDVDFSV contracts only — **no** per-game / MidwayBootAssist work.

## 1. ROMDIR surface

| Romdir | Role | SID(s) registered (decomp) | DetPS2 HLE |
|--------|------|----------------------------|------------|
| CDVDMAN | mechacon/drive manager | used by CDVDFSV imports | `Cdvd.cs` + thin `SidCdBase` |
| CDVDFSV | EE-facing RPC file service | `0x80000592`, `0x80000593`, `0x80000595`, `0x80000597`, `0x8000059a` (+ `0x8000059c` on IOPRP 2.8+) | `RealSifRpc` SCMD/NCMD + siblings |
| NCDVDMAN | newer CDVDMAN (X) | NCMD chain extras | listed in `BiosBootHost`, not literal port |
| CDVDSTM | disc-side stream IRX | (game IOP, not BIOS ROMDIR) | out of ROMDIR campaign |

CDVDFSV init (`FUN_000044ac` / `FUN_0000457c`) registers:

| SID | Handler | Purpose |
|-----|---------|---------|
| `0x80000592` | raw `0x204` (`FUN_00000204`) | `sceCdInit` |
| `0x80000593` | `FUN_000041b8` | **SCMD** (25 cases `1`–`0x19`) |
| `0x80000595` | `FUN_00003f3c` | **NCMD** (14 cases `1`–`0xe`) |
| `0x80000597` | raw `0x2f0` (`FUN_000002f0`) | `sceCdSearchFile` |
| `0x8000059a` | `FUN_000032d8` | `sceCdDiskReady` (blocking wait mode) |
| `0x8000059c` | **same as 0x59a** (IOPRP 2.8 vaddr `0x30D8`) | DiskReady twin — newer libcdvd after CdInit version probe (Burnout 3; CDVDMANIA RPS list) |

## 2. SCMD (`0x80000593`) — pre-work status

Already ported (2026-07-29) into `HandleCdScmd` with **result word first, payload at +4**. Structural shape is decomp-correct for all 25 cases.

| fno | Real name (decomp / ps2sdk) | Pre-work DetPS2 | Gap |
|-----|----------------------------|-----------------|-----|
| 1 | READ RTC | synthetic BCD clock @ +4 | shape OK; RTC not mechacon-real |
| 2 | WRITE RTC | echoes 2 words @ +4/+8 | OK |
| 3 | GetDiskType | `cdvd.DiscType` | OK (needs stable type on mount) |
| 4 | GetError | always `0` | **needs `LastError`** |
| 5 | TrayReq | tray open report only | **needs open/close/check modes** |
| 6–9 | iLink/NVM R/W | synthetic zeros / echo | shape OK; no real NVM store |
| 0xA–0xB | DEC SET | result / 4-word | shape OK |
| 0xC | Status | `ReadPending?0x80:MechaconStatus` | **map to SCECdStat\*** |
| 0xD–0x13 | HD mode / Config / Console ID | stubs | shape OK |
| 0x14 | Mecacon version | synthetic `0x00020101` | shape OK |
| 0x15–0x19 | ADout / Abort / SubQ / ForbidDVDP / AutoAdjust | Abort real; others stub | Abort OK |

## 3. NCMD (`0x80000595`) — pre-work status

ps2sdk `enum CD_NCMD_CMDS` matches decomp cases 1–0xe (0xf = READCHAIN X-only).

| fno | Name | Decomp handler | Pre-work DetPS2 | Gap |
|-----|------|----------------|-----------------|-----|
| 1 | READ | `FUN_000004d8` → byte count | `ReadSectorsTo`, byte count | **sector pattern / error codes** |
| 2 | CDDA READ | `FUN_000015ac` | same path as 1 | CDDA size not modeled (2048 OK for HLE) |
| 3 | DVD READ | `FUN_00000d8c` | same path as 1 | 2064-byte pattern not modeled |
| 4 | GET TOC | `FUN_0000340c` result+isDvd | `WriteCdToc` | shape OK; TOC DMA buffer not IOP-real |
| 5 | SEEK | `FUN_00004808(lsn)` + sync(2) | bare `1` | **update `LastSector` / drive state** |
| 6 | STANDBY | `FUN_000047f8` + sync | bare `1` | **Mechacon ready / state** |
| 7 | STOP | `FUN_00004840` + sync | bare `1` | **stop spin / cancel async** |
| 8 | PAUSE | `FUN_000048d0` + sync | bare `1` | **pause state** |
| 9 | STREAM | `FUN_00001d5c` subcmd in arg[3] | `BeginStream(lba)` only | **full ST_CMD set** |
| 0xA | CDDA STREAM | `FUN_0000273c` | bare `1` | stub / bank-stat return |
| 0xB | READ KEY | `FUN_00003c90` multi-word | bare `1` | **payload shape (result+16B key)** |
| 0xC | APPLY NCMD | `FUN_00003e0c` + sync | bare `1` | passthrough stub OK |
| 0xD | READ IOP MEM | `FUN_00000380` real read | bare `1` | **must `ReadSectorsTo`** |
| 0xE | DISK READY | `FUN_00003ee0` → 2 or 6 | mixed 0/2 | **SCECdComplete=2 / NotReady=6** |
| 0xF | READCHAIN (X) | not in this ROM | treated as ready | document as X-only |

Bug: `NcmdDiskReady` constant was `0x0F` (READCHAIN); real is **`0x0E`**.

## 4. Sibling SIDs

| SID | Status | Notes |
|-----|--------|-------|
| `0x80000592` | **HLE** | `sceCdInit` (`FUN_00000204`) + init packet version words |
| `0x80000597` | **HLE** | SearchFile via ISO9660 |
| `0x8000059a` | **HLE** | DiskReady → 2/6 |
| `0x8000059c` | **HLE** (2026-07-30) | IOPRP 2.8 twin of 0x59a (same handler); Burnout 3 binds this |

## 5. `Cdvd.cs` contract gaps

- No `LastError` (SCMD GetError always 0).
- Drive state conflated with mechacon ready bits (`0x40`/`0x80`); `sceCdStatus` needs SCECdStat\*.
- Seek/Standby/Stop/Pause have no first-class methods.
- Stream: cursor only; no bank/stat/subcmd.
- Tray: toggle only; no TrayReq mode enum.
- Failed reads (tray open / no disc) don't set error codes.

## 6. CompleteRpcEnd / WaitSema

BIND + CALL already go through `CompleteRpcEnd` (SignalSema + pkt free + RPC_END cid). NCMD read fills data **inside** the call so EE `WaitSema` sees ready data — matches retail "sync inside RPC_END" path for blocking RPC and our HLE of NOWAIT+callback as completed. **Do not** invent multi-megabyte full-disc preloads.

## 7. Landed this agent (2026-07-30) — generic, decomp-backed

1. `Cdvd.cs`: SCECd error/drive-state constants, `LastError`/`DriveState`, Seek/Standby/Stop/Pause, `DiskReady` (2/6), TrayReq modes, stream ST_CMD_*, failed-read errors.
2. `RealSifRpc.cs`: `HandleCdNcmd` (all 1–0xe + 0xf stub), fixed `NcmdDiskReady=0x0E`, SCMD GetError/Status/TrayReq depth, SIDs `0x592`/`0x597`/`0x59a`, READ IOP MEM + READ KEY shapes, stream subcmds.
3. Smokes: `RealSifRpc_CdvdNcmdSeekSyncDiskReadyAndStream`, `RealSifRpc_CdScmdTrayErrorStatus`, `RealSifRpc_CdSiblingSidsInitSearchDiskReady`.
4. `BIOS_DISSECTION.md` §6.1 updated.
5. Full smoke suite green. **Zero game hacks.**

## 7b. Phase 6 residual close (AGENT-CS, 2026-07-30)

1. Save/restore `LastError` / `DriveState` / stream bank state with drive save-state.
2. Mount paths (`MountIso` / `MountImage` / `MountDisc`) call `SetMountedReady()` — tray closed, `ErNO`, `StatSpin`, mechacon `0x40` so `DiskReady` → SCECdComplete immediately after media insert.
3. Contracts hold for commercial DiskReady / tray / error / Seek-Stop-Stream paths (smokes above + `Cdvd_MechaconDiskReadyAfterMount`).
4. **Gate: CDVDMAN PARTIAL → OK** (mechacon stand-in is intentional NONPORT-of-binary, not a contract gap).

## 8. Remaining residuals (do **not** block CDVDMAN OK)

- Literal CDVDMAN binary port (mechacon register poke layer) — CDVDFSV imports remain stubs; `Cdvd.cs` is the functional stand-in.
- Real NVM / iLink / console ID secrets.
- Real CDDA/DVD-V sector sizes (2328/2340/2064) and DVD dual-layer optics.
- Full IOP-side stream ring buffer DMA (ST_CMD_READ fills EE dest when addressable; IOP ring not modeled).
- XCDVDFSV-only READCHAIN (fno 0xf accepted as success stub).
- Per-title disc hacks / MidwayBootAssist (**out of scope forever for this agent**).
- CDVDSTM.IRX (game IOP, not SCPH70008 ROMDIR).
