# SCPH70008 ROMDIR / IOPBTCONF BIOS gate

**Purpose:** Close the “full BIOS HLE before any commercial title work” gate.  
**Authority:** `docs/BIOS_DISSECTION.md` §1–2, `BiosBootHost.BootCriticalContracts`, per-module port docs in this directory.  
**Rule:** No Midway/title-PC campaigns until every **RequiredForCommercialFastPath** row is **OK**, **PARTIAL**, or **NONPORT**.

## Status legend

| Tag | Meaning |
|-----|---------|
| **OK** | Contract HLE + smokes; decomp or ps2sdk ground-truthed |
| **PARTIAL** | Usable for boot; known residual gaps in module port doc |
| **NONPORT** | Intentional functional stand-in (not literal IRX port) |

---

## IOPBTCONF @800 order (required)

| # | Module | Port doc / surface | Gate |
|---|--------|-------------------|------|
| 1 | SYSMEM | SYSMEM.md · RealSifRpc sid 0x80000003 | **OK** |
| 2 | LOADCORE | IrxLoader ScanExports/LinkImports · BIOS_DISSECTION §6.5 | **OK** |
| 3 | EXCEPMAN | IopExcepManHost · §6.6 | **OK** (bookkeeping until IOP exec) |
| 4 | INTRMANP | VBLANK_INTRMAN.md · IopSystemHost | **OK** |
| 5 | INTRMANI | VBLANK_INTRMAN.md · IopSystemHost | **OK** |
| 6 | SSBUSC | SSBUSC_EECONF.md · IopSsbuscHost | **OK** |
| 7 | DMACMAN | DMACMAN.md · IopDmacManHost | **OK** |
| 8 | TIMEMANP | ROMDRV_TIMEMAN.md · IopSystemHost | **OK** |
| 9 | TIMEMANI | ROMDRV_TIMEMAN.md · IopSystemHost | **OK** |
| 10 | SYSCLIB | SYSCLIB_HEAPLIB.md · IopSysclibHeaplibHost | **OK** |
| 11 | HEAPLIB | SYSCLIB_HEAPLIB.md · IopSysclibHeaplibHost | **OK** |
| 12 | EECONF | SSBUSC_EECONF.md · IopEeconfHost | **OK** (optional REQ) |
| 13 | THREADMAN | THREADMAN.md · KernelHle/SonyKernelHle | **OK** (Mbx/Vpl/Fpl/priority/delay; residual §5.4/6/7/9–12) |
| 14 | VBLANK | VBLANK_INTRMAN.md · IopVblankHost | **OK** |
| 15 | IOMAN | FILEIO.md + REBOOT_STDIO_IOMAN.md · 16-slot + AddDrv | **OK** |
| 16 | MODLOAD | MODLOAD.md · IopModuleHost lifecycle | **OK** |
| 17 | ROMDRV | ROMDRV_TIMEMAN.md · rom0: FILEIO | **OK** |
| 18 | STDIO | REBOOT_STDIO_IOMAN.md · printf sink | **OK** |
| 19 | SIFMAN | SIFINIT_EESYNC.md · Sif.cs | **NONPORT** (§6.3) |
| 20 | IGREETING | REBOOT_STDIO_IOMAN.md | **OK** |
| 21 | SIFCMD | RealSifRpc BIND/CALL/RDATA/RPC_END | **OK** |
| 22 | REBOOT | REBOOT_STDIO_IOMAN.md · SifIopReset + handoff | **OK** |
| 23 | LOADFILE | LOADFILE.md · sid 0x80000006 | **OK** |
| 24 | CDVDMAN | CDVD.md · Cdvd.cs | **OK** (mechacon stand-in; DiskReady/tray/error/NCMD) |
| 25 | CDVDFSV | CDVD.md · SCMD/NCMD/siblings | **OK** |
| 26 | SIFINIT | SIFINIT_EESYNC.md | **OK** |
| 27 | FILEIO | FILEIO.md · sid 0x80000001 | **OK** |

## Extended ROMDIR (required for commercial fast path)

| Module | Port doc | Gate |
|--------|----------|------|
| EESYNC | SIFINIT_EESYNC.md | **OK** |
| PADMAN | PADMAN.md | **OK** |
| SIO2MAN | SIO2MAN.md | **OK** |

## Extended ROMDIR (optional / deferred)

| Module | Notes | Gate |
|--------|-------|------|
| XLOADFILE / XFILEIO / NCDVDMAN / XPADMAN / XSIO2MAN / … | Aliases / X paths share primary HLE (`IopExtendedBiosHost`) | **OK** (alias) |
| MCMAN / MCSERV | MCSERV.md full RPC + dual-format FAT (PS1/PS2) | MCSERV **OK**, MCMAN **OK** |
| LIBSD | LIBSD.md · IopLibSdHost (sceSdInit/param/key-on) | **OK** (core; mixer residual in LIBSD.md) |
| SECRMAN | Secr*BootFile plain ELF OK; encrypted clear fail; no MagicGate crypto | **PARTIAL** |
| CLEARSPU | Spu2 soft-reset on boot + UDNL handoff | **OK** |
| UDNL | IOPRP/DNAS ROMDIR+IOPBTCONF apply + LoadIrx + version (see UDNL.md) | **OK** |
| ADDDRV / RMRESET / XMTAPMAN | Name + related hosts | **PARTIAL** |

Full 101-entry map: **ROMDIR_FULL_AUDIT.md**.

## Gate acceptance criteria

1. `BiosBootHost_IopBtConfContracts` green — all **26** required names registered.  
2. `BiosRomdirGate_PortDocsForRequiredModules` green — port docs for every required module.  
3. Full `Tests` smoke suite green after merge.  
4. No title-specific PCs required for the above contracts.  
5. Every required row is OK / PARTIAL / NONPORT (no OPEN).

## Intentional residual (does **not** block gate)

These remain for later deepening but do **not** leave commercial boot without a destination:

- Literal R3000 execution of BIOS IRX (optional architecture path)
- THREADMAN event-flag wait-queue priority / generation-bit IDs / CheckThreadStack / i-form checks (Mbx/Vpl/Fpl/priority/delay **done** — see THREADMAN.md §5)
- MCMAN ECC spare / wear-leveling (dual-format FAT present; ECC residual)
- IOP DMAC/SSBUS MMIO shared with hosts (contract HLE is OK; MMIO is residual)
- INTRMAN full Ghidra dump (export contracts ground-truthed from binary KE_* + ps2sdk)
- LIBSD dual-core mixer / effect DSP (core contracts OK — see LIBSD.md)

## Gate decision

| Field | Value |
|-------|--------|
| Status | **CLOSED** |
| Closed date | 2026-07-30 |
| Closed by | orchestrator (waves 1–4 integrated; full smoke green) |
| Smoke evidence | `BiosBootHost_IopBtConfContracts` required=26; `BiosRomdirGate_PortDocsForRequiredModules`; `=== ALL SMOKE TESTS PASSED ===` |

**Commercial multi-title troubleshooting may begin** (orchestrator may now spawn per-game agents). Per-game work must still prefer generic BIOS fixes when a bug is shared.
