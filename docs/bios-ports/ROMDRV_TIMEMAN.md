# ROMDRV + TIMEMAN — gap analysis & port notes (DetPS2 HLE)

**Agent:** ROMDRV+TIMEMAN (generic BIOS HLE only — zero title PCs / commercial hacks)  
**Date:** 2026-07-30  
**Worktree:** `subagent-019fb1b9-b0c7-7272-a17a-a94cfe8110e4`

## Authority

| Source | Status |
|--------|--------|
| `docs/BIOS_DISSECTION.md` §1–2 (ROMDIR / IOPBTCONF), §7 INTRMAN/TIMEMAN row | Present |
| `docs/bios-ports/FILEIO.md` gap: *ROMDRV `rom0:` content serving* | Closed by this port |
| `docs/bios-ports/VBLANK_INTRMAN.md` gap: *TIMEMAN / hard timers* | Closed (contract HLE) by this port |
| `tools/bios-extract/TIMEMAN*.bin` / `TIMEMAN*_ALL.txt` | **Absent** — no Ghidra dump yet |
| `tools/bios-extract` ROMDRV.bin | **Absent** — not extracted |
| ps2sdk `iop/system/timrman` (`timrman.h` + `timrman.c`) | Fetched — TIMEMANI/P table, timid encoding, KE_* |
| ps2sdk `iop/system/threadman` `thbase.h` / `thbase.c` | Fetched — `iop_sys_clock_t`, SetAlarm/GetSystemTime/USec2SysClock |
| ps2sdk `iop/kernel/include/kerr.h` | Fetched — `-150..-156` timer errors |
| Existing `RomdirExtractor`, `IopModuleHost` FILEIO, `IopSystemHost` | Present |

ROMDRV is the IOMAN device driver for `rom0:` / `rom:` that exposes BIOS ROMDIR entries as files. FILEIO is only the EE↔IOP RPC shell; once OPEN reaches IOMAN, the path device is `rom` and the open is served by ROMDRV.

TIMEMAN (ROMDIR names **TIMEMANP** / **TIMEMANI**) is the hard-timer manager (`timrman` export library). **thbase** (THREADMAN) owns soft `GetSystemTime` / `SetAlarm` / `USec2SysClock` on top of a TIMEMAN-backed RTC; DetPS2 HLE colocates both surfaces in `IopSystemHost` because the project does not yet run THREADMAN IRX on R3000.

---

## 1. ROMDRV — contracts

### Real behavior (from ROMDIR role + IOMAN path model)

- Registered after IOMAN/MODLOAD in IOPBTCONF (`ROMDRV` line).
- Device names: `rom` / `rom0` (and sometimes `rom1` for DVD-player ROM variants).
- Path form: `rom0:PADMAN`, `rom0:\SIO2MAN;1`, `rom0:FOO.IRX` → module name = bare ROMDIR entry.
- Open/read/getstat return the **raw ROMDIR payload** for that name (ELF for IRX modules, text for `IOPBTCONF`, etc.).
- Missing name → ENOENT (`-2`).
- Read-only; no write/mkdir.

### DetPS2 surface (this port)

| API | Location | Behavior |
|-----|----------|----------|
| Bind BIOS image | `IopModuleHost.BindRomBios` | Called from `BiosBootHost.StartCommercialIop` |
| Path parse | `IopModuleHost.TryResolveRom0Path` | Strips `rom0:`/`rom:`/`rom1:`, `;ver`, `.IRX`/`.IMG` |
| Content extract | `RomdirExtractor.ExtractModuleContent` | ELF-magic offset first; naive offset fallback for non-ELF |
| `FileOpen("rom0:…")` | `IopModuleHost.FileOpen` | Real bytes when bound; **ENOENT** if bound+missing; empty stub if unbound |
| `FileRead` / `FileSeek` | existing host-file path | Served from bound byte[] |
| `FileGetStat` | `IopModuleHost.FileGetStat` | `io_stat_t.size` = ROMDIR entry size |
| `DirOpen("rom0:")` | `IopModuleHost.DirOpen` | Lists ROMDIR names when bound |
| Counters | `RomBiosBound`, `RomdirEntryCount`, `Rom0BytesServed` | Smoke / diagnostics |

### Intentional HLE divergences

| Divergence | Why |
|------------|-----|
| No separate `RomdrvHost` IRX | FILEIO already routes through `IopModuleHost`; binding ROMDIR there is the behavioral payoff |
| Unbound BIOS → empty success for any `rom0:` name | Matches prior host/mc probe policy so no-BIOS bring-up still works |
| No real IOMAN `AddDrv("rom")` registry object | Device **names** already in `IopSystemHost.InstallBiosDevices`; full AddDrv/DelDrv still deferred (FILEIO.md) |
| Extension strip only `.IRX`/`.IMG` | Avoids turning `FOO.BAR` into wrong names |

---

## 2. TIMEMAN — contracts

### Hard timers (timrman / TIMEMANI)

From ps2sdk recreation of SCE TIMEMAN (6 slots; TIMEMANP is first 3):

| Idx | Count addr | Sources | Width | Max prescale | IRQ | Notes |
|-----|------------|---------|-------|--------------|-----|-------|
| 0 | `0xBF801100` | SYSCLOCK\|PIXEL\|HOLD (`0xB`) | 16 | 1 | 4 | PADMAN RTC0 |
| 1 | `0xBF801110` | SYSCLOCK\|HLINE\|HOLD (`0xD`) | 16 | 1 | 5 | PADMAN RTC1 |
| 2 | `0xBF801120` | SYSCLOCK | 16 | 8 | 6 | Preferred first alloc |
| 3 | `0xBF801480` | SYSCLOCK\|HLINE | 32 | 1 | `0xE` | TIMEMANI only |
| 4 | `0xBF801490` | SYSCLOCK | 32 | 256 | `0xF` | TIMEMANI only |
| 5 | `0xBF8014A0` | SYSCLOCK | 32 | 256 | `0x10` | TIMEMANI only |

- **timid** = `((index + 1) << 28) | (countAddr >> 4)`
- **AllocHardTimer(source, size, prescale)** — free slot matching source mask / width / max prescale; preference order `2,5,4,3,0,1`
- Errors: `KE_ILLEGAL_CONTEXT` (-100), `KE_NO_TIMER` (-150), `KE_ILLEGAL_TIMERID` (-151), `KE_ILLEGAL_SOURCE` (-152), `KE_ILLEGAL_PRESCALE` (-153), `KE_TIMER_BUSY` (-154), `KE_TIMER_NOT_SETUP` (-155), `KE_TIMER_NOT_INUSE` (-156)

### Soft clock / alarms (thbase)

| API | Contract |
|-----|----------|
| `iop_sys_clock_t { u32 lo, hi }` | 64-bit SysClock |
| `GetSystemTime` | Current ticks |
| `SetAlarm(delta, cb, arg)` | Relative schedule; **duplicate (cb,arg) → KE_FOUND_HANDLER** |
| `CancelAlarm(cb, arg)` | **KE_NOTFOUND_HANDLER** if missing |
| `USec2SysClock` / `SysClock2USec` | mul=**36864**, div=**1000** (≈ 36.864 MHz SYSCLOCK) |

### DetPS2 surface (this port)

| API | Location | Status |
|-----|----------|--------|
| RTC0–5 table + timid encode | `IopSystemHost` | ✓ |
| Alloc/Refer/FreeHardTimer | `IopSystemHost` | ✓ (bookkeeping; no MMIO) |
| Set/Get Timer Mode/Counter/Compare | `IopSystemHost` | ✓ synthetic counters |
| GetHardTimerIntrCode | `IopSystemHost` | ✓ (PADMAN IRQ 4/5) |
| Setup/Start/StopHardTimer | `IopSystemHost` | ✓ config flags only |
| ConfigureTimeMan(mani/p) | `IopSystemHost` | ✓; boot uses TIMEMANI (6) |
| GetSystemTime / struct write | `IopSystemHost` | ✓ |
| SetAlarm / CancelAlarm / fire on Tick | `IopSystemHost` | ✓ duplicate + NOTFOUND |
| USec2SysClock / SysClock2USec | static on `IopSystemHost` | ✓ |
| VBlank `Tick(1)` | `BiosHle.OnVblank` | Advances **SysClockPerVblank** (≈614400), not 1 |
| SetTimerHandler / SetOverflowHandler | `IopSystemHost` | ✓ compare-match → timeup + INTRMAN RaiseIntr bookkeeping |
| Get/ClearTimerTimeupFlags | `IopSystemHost` | ✓ |

### Intentional HLE divergences

| Divergence | Why |
|------------|-----|
| No `0xBF8011xx` MMIO timer hardware | Project has no IOP timer MMIO model; counters advance on host Tick |
| Alarm / timer callbacks not R3000-executed | Same class as VBLANK callbacks — count + INTRMAN pending only |
| thbase + timrman colocated | No IOP IRX execution; single host is the service surface |
| No full Ghidra TIMEMAN dump | Contracts from ps2sdk recreation + kerr.h; re-verify when `TIMEMAN*_ALL.txt` lands |

---

## 3. Landed (waves + Phase 2 deepen)

1. **`RomdirExtractor.ExtractModuleContent` / `TryFindEntry` / `HasModule`** — ELF or raw ROMDIR payload.
2. **`IopModuleHost.BindRomBios` + rom0 open/read/getstat/dopen** through FILEIO.
3. **`BiosBootHost.StartCommercialIop` binds ROM image** into FILEIO (and clears on no-image path).
4. **Hard-timer table TIMEMANI/P** with real timid encoding and KE_* family.
5. **SysClock units**, USec conversion, SetAlarm duplicate/cancel contracts, VBlank-scale Tick.
6. **Phase 2 (AGENT-I):** `SetTimerHandler` / `SetOverflowHandler`; Tick compare-match / overflow → timeup flags + INTRMAN pending raise on timer IRQ; strict timid encode check on lookup.
7. **Smokes:** `Romdrv_Rom0ContentServingThroughFileIo`, `Timeman_HardTimerAndSysClockContracts` (deepened); extended `BiosHle_IopSystemIntrAndTime`.

**Gate:** TIMEMANP / TIMEMANI → **OK** (contract HLE + smokes; residual = no MMIO / no Ghidra dump / no R3000 cb exec).

---

## 4. Remaining gaps (non-blocking)

| Gap | Notes |
|-----|-------|
| **TIMEMAN Ghidra dump** | Extract TIMEMANP/TIMEMANI bins + headless decomp; reconcile timid/order vs recreation |
| **ROMDRV.IRX decomp** | Confirm path parse / unit digit / error codes line-for-line |
| **IOP timer MMIO** | Real `BF801100` family so free-running hardware matches GetTimerCounter without Tick |
| **THREADMAN DelayThread** | Soft delay still KernelState/EE-side; not driven by these IOP alarms |
| **IOMAN AddDrv registry** | Still deferred until multiple real backends need a general dispatcher |
| **rom1: DVD-player ROM** | Name accepted; no second image binding |

---

## 5. Smokes

```text
Romdrv_Rom0ContentServingThroughFileIo
Timeman_HardTimerAndSysClockContracts
BiosHle_IopSystemIntrAndTime   (extended: SysClockPerVblank + HardTimerCount==6)
Romdir_ParseAndExtract_HandlesInterEntryPadding  (pre-existing; still green)
```

Run: `dotnet run --project Tests`

**Scope rule:** generic BIOS HLE only. No MidwayBootAssist, no title PCs, no commercial game timing hacks.
