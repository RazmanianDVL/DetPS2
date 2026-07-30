# BIOS HLE completion verdict — SCPH70008 commercial fast-path gate

| Field | Value |
|-------|--------|
| **Executive verdict** | **GATE_COMPLETE** |
| Date | **2026-07-30** |
| Scope | Project gate criteria (`ROMDIR_GATE.md` + `BiosBootHost.BootCriticalContracts`), **not** literal full PS2 BIOS universe |
| BIOS target | SCPH70008 ROMDIR / IOPBTCONF contracts |
| Verifier | Independent read of authority docs + code inventory + Release smoke suite |

---

## Executive summary

Against the **declared project gate** (“full BIOS HLE before commercial title work”), DetPS2 is **complete**:

1. All **26** `RequiredForCommercialFastPath` modules are registered by `BiosBootHost.StartCommercialIop`.
2. Every required row has gate tag **OK**, **PARTIAL**, or **NONPORT** — **no OPEN**.
3. Port docs exist for every required module (enforced by smoke).
4. `BiosBootHost_IopBtConfContracts` and `BiosRomdirGate_PortDocsForRequiredModules` are green.
5. Full `Tests` smoke suite: **`=== ALL SMOKE TESTS PASSED (Phase 56 + media) ===`**.

This is **not** a claim that every PS2 BIOS feature is fully ported. Intentional residuals (R3000 IRX exec, THREADMAN Mbx/Vpl/Fpl, MCMAN ECC, etc.) remain and **do not block** the gate.

Authority cross-check: `docs/bios-ports/ROMDIR_GATE.md` already records Status **CLOSED** (2026-07-30). This document re-verifies that claim from code + live smokes.

---

## Gate acceptance criteria (checklist)

| # | Criterion | Result |
|---|-----------|--------|
| 1 | `BiosBootHost_IopBtConfContracts` — all required names registered | **PASS** — `required=26`, `svcs=77` |
| 2 | `BiosRomdirGate_PortDocsForRequiredModules` | **PASS** — `checked=26` |
| 3 | Full `Tests` smoke suite green | **PASS** — Phase 56 + media |
| 4 | No title-specific PCs required for these contracts | **PASS** — generic HLE hosts / `RealSifRpc` / `KernelHle` |
| 5 | Every required row OK / PARTIAL / NONPORT (no OPEN) | **PASS** — see inventory |

---

## Required module inventory (`RequiredForCommercialFastPath = true`)

Source: `src/DetPS2.Core/BiosBootHost.cs` `BootCriticalContracts`.  
Gate tags: `docs/bios-ports/ROMDIR_GATE.md`.  
Smoke primary: `BiosBootHost_IopBtConfContracts` (name load) + module-specific smokes below.

| # | Module | Gate | Port doc | HLE host / surface | Smoke evidence |
|---|--------|------|----------|--------------------|----------------|
| 1 | SYSMEM | **OK** | SYSMEM.md | `RealSifRpc` sid `0x80000003` (`HandleSysmem`) | `RealSifRpc_SysmemAllocFreeLoadContracts` |
| 2 | LOADCORE | **OK** | ROMDIR_GATE + BIOS_DISSECTION §6.5 | `IrxLoader.ScanExports` / `LinkImports` | `IrxLoader_LinkImports_*`, `Irx_LoadMinimal_*` |
| 3 | EXCEPMAN | **OK** | ROMDIR_GATE (§6.6) | `IopExcepManHost` | `IopExcepMan_PriorityOrderedRegistration` |
| 4 | INTRMANP | **PARTIAL** | VBLANK_INTRMAN.md | `IopSystemHost` IRQ register/enable | `BiosHle_IopSystemIntrAndTime` |
| 5 | INTRMANI | **PARTIAL** | VBLANK_INTRMAN.md | `IopSystemHost` (secondary) | `BiosHle_IopSystemIntrAndTime` |
| 6 | SSBUSC | **PARTIAL** | SSBUSC_EECONF.md | `IopSsbuscHost` | `Ssbusc_BusWindowContracts` |
| 7 | DMACMAN | **PARTIAL** | DMACMAN.md | `IopDmacManHost` | `BiosHle_IopDmacManContracts` |
| 8 | TIMEMANP | **PARTIAL** | ROMDRV_TIMEMAN.md | `IopSystemHost` hard timers | `Timeman_HardTimerAndSysClockContracts` |
| 9 | TIMEMANI | **PARTIAL** | ROMDRV_TIMEMAN.md | `IopSystemHost` (boot uses MANI=6 slots) | `Timeman_HardTimerAndSysClockContracts` |
| 10 | SYSCLIB | **OK** | SYSCLIB_HEAPLIB.md | `IopSysclibHeaplibHost` | `SysclibHeaplib_ExportTablesAndLinkImports` |
| 11 | HEAPLIB | **OK** | SYSCLIB_HEAPLIB.md | `IopSysclibHeaplibHost` | `SysclibHeaplib_HeapCreateAllocFreeContracts` |
| 12 | THREADMAN | **PARTIAL** | THREADMAN.md | `KernelHle` / `SonyKernelHle` (semas, threads, sleep/wakeup) | `KernelHle_ThreadmanSemaWakeAndReferStatus`, `KernelHle_ThreadmanSleepWakeupCount`, `KernelHle_ThreadsSemasEventFlags` |
| 13 | VBLANK | **OK** | VBLANK_INTRMAN.md | `IopVblankHost` + PCRTC dispatch | `BiosHle_IopVblankEventFlag`, `BiosHle_IopVblankRegisterContracts` |
| 14 | IOMAN | **OK** | FILEIO.md + REBOOT_STDIO_IOMAN.md | `IopSystemHost` AddDrv + `IopModuleHost` | `BiosHle_RebootStdioIgreetingIomanContracts` |
| 15 | MODLOAD | **OK** | MODLOAD.md | `IopModuleHost` lifecycle | `Modload_ModuleTableStartOrderSearchStopUnload` |
| 16 | ROMDRV | **OK** | ROMDRV_TIMEMAN.md | `IopModuleHost.BindRomBios` / rom0 FILEIO | `Romdrv_Rom0ContentServingThroughFileIo` |
| 17 | SIFMAN | **NONPORT** | SIFINIT_EESYNC.md + gate §6.3 | `Sif.cs` DMA / flags (functional stand-in) | `BiosHle_SifInitEeSyncContracts`, SIF DMA smokes |
| 18 | SIFCMD | **OK** | SIFINIT_EESYNC.md / RealSifRpc | BIND/CALL/RDATA → RPC_END `0x80000008` | `BiosHle_SifcmdRdataAndFileIoSid`, `BiosBootHost_IopBtConfContracts` CID checks |
| 19 | LOADFILE | **OK** | LOADFILE.md | `RealSifRpc` sid `0x80000006` | `RealSifRpc_LoadFile_SearchStopUnloadContracts`, `RealSifRpc_LoadFileModuleElfSetGetSearch` |
| 20 | CDVDMAN | **PARTIAL** | CDVD.md | `Cdvd.cs` + mechacon stand-in | `Cdvd_*`, NCMD/SCMD RPC smokes |
| 21 | CDVDFSV | **OK** | CDVD.md | `RealSifRpc` SIDs `0x592`/`0x593`/`0x595`/`0x597`/… | `RealSifRpc_CdSiblingSids*`, `RealSifRpc_CdvdNcmd*`, `RealSifRpc_CdScmd*` |
| 22 | SIFINIT | **OK** | SIFINIT_EESYNC.md | `Sif.ApplySifInit` (idempotent) | `BiosHle_SifInitEeSyncContracts` |
| 23 | FILEIO | **OK** | FILEIO.md | `RealSifRpc` sid `0x80000001` + `IopModuleHost` | `RealSifRpc_FileIoOpenReadLseekCloseAndDir`, `BiosHle_FileIoGetstatAndCdvdSectors` |
| 24 | EESYNC | **OK** | SIFINIT_EESYNC.md | `Sif.PostBootEnd` / BOOTEND | `BiosHle_SifInitEeSyncContracts` |
| 25 | PADMAN | **OK** | PADMAN.md | `RealSifRpc` OLD `0x8000010f` / NEW `0x80000100` | `RealSifRpc_PadmanOldSid*`, `RealSifRpc_PadmanNewSid*`, `RealSifRpc_PadmanCloseEndAndPortMax` |
| 26 | SIO2MAN | **OK** | SIO2MAN.md | `Sio2.cs` MMIO + pad/MC attach | `Sio2_PadmanConfigSequenceHelper`, `Sio2_DualShockConfigFsmAndActiveLow`, `Sio2_PadPoll` |

**Count:** 26 required — matches smoke `required=26` / `checked=26`.

### Optional / not RequiredForCommercialFastPath (closed for inventory; do not inflate “required=26”)

| Module | `Required…` | Gate (ROMDIR_GATE) | Host / notes |
|--------|-------------|--------------------|--------------|
| EECONF | false | **PARTIAL** (optional REQ) | `IopEeconfHost` — smoke `Eeconf_InitContracts` |
| STDIO | false | **OK** | `BiosBootHost.ApplyStdioContract` / `IopSystemHost` |
| IGREETING | false | **OK** | `BiosBootHost.ApplyIgreetingContract` |
| REBOOT | false | **OK** | `Sif` RESET + `ApplyPostIopRebootContracts` |
| XLOADFILE / XFILEIO / NCDVDMAN | false | **PARTIAL** | Aliases share primary HLE |
| MCSERV | false | **OK** | `RealSifRpc` sid MC — `RealSifRpc_McservRealFunctionNumbers` |
| MCMAN | false | **OK** | Dual-format FAT (PS1/PS2) + MCSERV; ECC residual |
| LIBSD | false | **PARTIAL** (name only) | Sound; not boot-critical |

---

## Smokes run log summary

**Commands (2026-07-30):**

```text
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release
  → Build succeeded. 0 Warning(s). 0 Error(s).

dotnet run --project Tests -c Release --no-build
  → === ALL SMOKE TESTS PASSED (Phase 56 + media) ===
```

**BIOS gate smokes (excerpt):**

| Smoke | Result |
|-------|--------|
| `BiosBootHost_IopBtConfContracts` | OK (`svcs=77`, `required=26`) |
| `BiosRomdirGate_PortDocsForRequiredModules` | OK (`checked=26`) |
| `BiosHle_RebootStdioIgreetingIomanContracts` | OK |
| `BiosHle_SifInitEeSyncContracts` | OK (`smflag=0x70000`) |
| `Ps2System_LoadBiosNative_BootsWithoutRealBiosFile` | OK |
| Module-specific RealSifRpc / Iop* / KernelHle_* listed above | all OK |

**Suite:** commercial checklist 11/11, play-path gate, majority/netplay synthetic gates green. No BIOS-gate failures.

---

## Intentional residuals (do **not** block gate)

From `ROMDIR_GATE.md` and port docs — honest incomplete vs a literal BIOS:

| Residual | Why not gate-blocking |
|----------|------------------------|
| Literal R3000 execution of BIOS IRX | Architecture path optional; contract HLE presents destinations |
| THREADMAN Mbx / Vpl / Fpl + priority ready queues | Semas/threads/sleep cover commercial WaitSema/SignalSema fast path; PARTIAL documented |
| MCMAN ECC / wear-leveling | Dual-format FAT + MCSERV OK; ECC residual; not RequiredForCommercialFastPath |
| IOP DMAC/SSBUS MMIO shared with hosts | PARTIAL hosts usable for boot bookkeeping |
| INTRMAN full Ghidra dump parity | PARTIAL register/enable/dispatch enough for VBLANK plant |
| LIBSD deep audio | Optional name registration only |
| MagicGate MG_* decrypt on LOADFILE | Documented gap; plain module path works |
| Exact mechacon bit-for-bit CDVDMAN | PARTIAL stand-in; CDVDFSV RPC OK |

---

## Gaps that would block claiming “gate complete”

None observed at verification time. Hypothetical blockers (for future regressions):

| Blocker | How it would fail |
|---------|-------------------|
| Any required module missing from `IopModules` after `StartCommercialIop` | `BiosBootHost_IopBtConfContracts` throws |
| Missing port doc / gate mapping | `BiosRomdirGate_PortDocsForRequiredModules` throws |
| Gate tag **OPEN** on a required row | Violates `ROMDIR_GATE.md` criterion 5 |
| Required count drop below 20 or name de-registration | Same IopBtConf smoke |
| Full suite red after BIOS change | Criterion 3 |

---

## Distinction: gate vs literal full BIOS

| Claim | Verdict |
|-------|---------|
| **Gate COMPLETE** (project definition) | **YES** — all required rows closed + smokes green |
| **Literal full BIOS complete** | **NO** — residuals above remain; HLE is intentional contract surface, not cycle-accurate IRX |

Commercial multi-title troubleshooting may proceed under the project rule that shared BIOS bugs stay generic (prefer host fixes over title PCs).

---

## Code anchors

| Artifact | Path |
|----------|------|
| Contract table | `src/DetPS2.Core/BiosBootHost.cs` (`BootCriticalContracts`, `StartCommercialIop`) |
| Gate status doc | `docs/bios-ports/ROMDIR_GATE.md` |
| Dissection | `docs/BIOS_DISSECTION.md` §1–2 (ROMDIR / IOPBTCONF) |
| Smokes | `Tests/SmokeTests.cs` — `BiosBootHost_IopBtConfContracts`, `BiosRomdirGate_PortDocsForRequiredModules` |
| Port docs | `docs/bios-ports/*.md` |

---

*Verification date: 2026-07-30. No commits or pushes performed by verifier.*
