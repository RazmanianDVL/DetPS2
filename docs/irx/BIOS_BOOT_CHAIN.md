# BIOS IOPBTCONF boot chain (Track T3 / WP-15–16)

**Owned:** `BiosBootHost.cs`, `RomdirExtractor.cs`  
**Authority:** SCPH70008 ROMDIR + `IOPBTCONF` text (`docs/BIOS_DISSECTION.md` §2), plan WP-15/WP-16.

## IOPBTCONF ordered list (SCPH70008 `@800`)

Canonical order (also `BiosBootHost.Scph70008IopBtConfOrder`):

```
SYSMEM, LOADCORE, EXCEPMAN, INTRMANP, INTRMANI, SSBUSC, DMACMAN,
TIMEMANP, TIMEMANI, SYSCLIB, HEAPLIB, EECONF, THREADMAN, VBLANK,
IOMAN, MODLOAD, ROMDRV, STDIO, SIFMAN, IGREETING, SIFCMD, REBOOT,
LOADFILE, CDVDMAN, CDVDFSV, SIFINIT, FILEIO
```

**27** module names. Parse APIs:

| API | Role |
|-----|------|
| `ParseIopBtConfText(string)` | Pure text → ordered names (unit-testable) |
| `ExtractIopBtConfNames(byte[])` | ROMDIR `IOPBTCONF` payload → ordered names |
| `BindBios` → `IopBtConfNames` / `GetBoundIopBtConfNames()` | Cached after bind |
| `InventoryIopBtConfElfs(bios, order)` | Which names have extractable ELF blobs |

Directives (`@800`, …) are skipped. Max name length 16 (ROMDIR field).

## Current `StartCommercialIop` — LoadIrx vs RegisterModule

Historical commercial path is **HLE destinations first**, with **best-effort ELF load only for RPC owners**.

### 1) Name-only `RegisterModule` (bulk)

| Path | What gets registered |
|------|----------------------|
| `InstallIopBtConfOrder` | IOPBTCONF names (or required-contract fallback when no image) |
| Full ROMDIR walk | Every ROMDIR name so `sceSifLoadModule("rom0:…")` probes succeed |
| `InstallContractModules` | Full `BootCriticalContracts` + `rom0:` / `ROM0:` aliases + disc-side names (`CDVDSTM`, `IOPRP*`, `PADMAN`, …) |
| `FinishIopServices` | STDIO / IGREETING / REBOOT re-register + HLE hosts (INTRMAN, SYSCLIB, SSBUSC, EECONF, extended SECRMAN/UDNL/…) |

**No IRX bytes** are placed for these paths alone — module table entry only (`IopModuleState.Registered`, `HasImage=false`).

### 2) `LoadIrx` (real ELF extract + relocate + link)

Only inside `StartCommercialIop` step 4, for contracts with **`RpcSid != 0`**:

| Module | Typical sid | Notes |
|--------|-------------|--------|
| SYSMEM | `0x80000003` | EE heap RPC |
| LOADFILE / XLOADFILE | `0x80000006` | |
| CDVDMAN / XCDVDMAN | CD base | |
| CDVDFSV / XCDVDFSV | SCMD | |
| FILEIO / XFILEIO | `0x80000001` | |
| PADMAN / XPADMAN | pad | Commercial-fast-path sibling, not always in `@800` text |
| MCMAN / MCSERV / XM* | MC | Optional contracts |
| …other `RpcSid != 0` rows | | Same loop |

Extract via `RomdirExtractor.ExtractModule` (ELF magic window). On failure → fall back to `RegisterModule`.

**Not LoadIrx’d by current HLE boot** (name-only + separate C# HLE hosts):  
`LOADCORE`, `EXCEPMAN`, `INTRMAN*`, `SSBUSC`, `DMACMAN`, `TIMEMAN*`, `SYSCLIB`, `HEAPLIB`, `EECONF`, `THREADMAN`, `VBLANK`, `IOMAN`, `MODLOAD`, `ROMDRV`, `STDIO`, `SIFMAN`, `IGREETING`, `SIFCMD`, `REBOOT`, `SIFINIT`, `EESYNC`, `SIO2MAN`, `LIBSD`, `UDNL`, …

`IopModuleHost.LoadIrx` also HLE-marks **Started** (`MODULE_RESIDENT_END`) — it does **not** run R3000 `_start` yet.

## Literal path (WP-15 prep / WP-16)

```text
BootIopBtConfLiteral(Ps2System)
  for name in IOPBTCONF order:
    if ExtractModule is ELF → LoadIrx(name)
      if DETPS2_LITERAL_IRX=1 → StartModule (HLE mark today;
          TODO real R3000 _start via IopModuleHost / IOP step — T1/T2)
    else → RegisterModule name-only
```

- Prefers **real extract+load**; does not invent HLE plants.
- Does **not** replace `StartCommercialIop` (HLE surface still required until G1).
- Result: `LiteralIopBtConfBootResult` / `LastLiteralBoot` (order, extractable, loaded, started, name-only).

Env:

| Variable | Effect |
|----------|--------|
| `DETPS2_LITERAL_IRX=1` | After LoadIrx, attempt start path (HLE mark + TODO real exec) |
| `DETPS2_TRACE_LITERAL_IRX=1` / `DETPS2_TRACE_BIOS=1` | Log literal boot summary |

## Exit criteria (WP-15)

- [x] Parse IOPBTCONF → ordered list (unit vs known SCPH70008 order)
- [x] Document LoadIrx vs RegisterModule (this file)
- [x] `BootIopBtConfLiteral` prep API
- [ ] WP-16: sequential exec first 5 modules (T1/T2 start API)

## Related

- `docs/BIOS_DISSECTION.md` §2  
- `docs/bios-ports/ROMDIR_GATE.md`  
- `docs/IRX_EXECUTION_PHASE_PLAN.md` Block C  
- `tools/README.md` — `DETPS2_LITERAL_IRX`
