# SSBUSC + EECONF — gap analysis & port notes (DetPS2 HLE)

**Agent:** SSBUSC+EECONF (generic BIOS HLE only — zero title PCs / commercial hacks)  
**Date:** 2026-07-30  
**Worktree:** `subagent-019fb1c8-c40c-7eb3-80f9-af0bd7648fbe`

## Authority

| Source | Status |
|--------|--------|
| `docs/BIOS_DISSECTION.md` §1–2 (ROMDIR / IOPBTCONF) | Present — SSBUSC #6, EECONF #12 |
| `docs/bios-ports/ROMDIR_GATE.md` wave-4 rows | OPEN→this port |
| `tools/bios-extract/SSBUSC.bin` / `EECONF.bin` | **Absent** — not extracted |
| `tools/bios-decomp/SSBUSC_ALL.txt` / `EECONF_ALL.txt` | **Absent** |
| ps2sdk `iop/system/ssbusc` (`ssbusc.h` + `ssbusc.c`) | Fetched — export table, delay/base tables, common delay |
| Wisi SSBUSC delay bit-field research (cited in ps2sdk header) | Ground-truth for PIO/DMA fields |
| ps2homebrew / Woon Yung OSD init notes (EECONF roles) | MAC, SPEED, ROM version, PS1 EEPROM zero-fill |
| Existing `MmioBus` `0x1000F100` stub, `BiosBootHost`, `IopSystemHost` | Present |

**Scope rule:** generic BIOS HLE only. No MidwayBootAssist, no title PCs, no commercial game timing hacks.

---

## IOPBTCONF placement

```
… → INTRMANP → INTRMANI → SSBUSC → DMACMAN → TIMEMAN* → SYSCLIB → HEAPLIB → EECONF → THREADMAN → …
```

| Module | RequiredForCommercialFastPath | Role |
|--------|-------------------------------|------|
| **SSBUSC** | **true** | SSBUS controller — chip-select base + delay windows |
| **EECONF** | **false** (optional) | EE/peripheral config after HEAPLIB |

Both still implement contracts so name registration + post-init state match retail bring-up.

---

## 1. SSBUSC — contracts

### Real behavior (ps2sdk recreation of retail `ssbusc` v1.1)

Export library `ssbusc` (imports 4–17):

| Export | Contract |
|--------|----------|
| `SetDelay(device, value)` | Write delay/config MMIO for device; return `value` or **-1** |
| `GetDelay(device)` | Read delay/config; **-1** if unwired |
| `SetBaseAddress(device, value)` | Write base; return `value` or **-1** |
| `GetBaseAddress(device)` | Read base; **-1** if unwired |
| `Set/GetRecoveryTime` | Common delay bits 3:0 |
| `Set/GetHoldTime` | bits 7:4 |
| `Set/GetFloatTime` | bits 11:8 |
| `Set/GetStrobeTime` | bits 15:12 |
| `Set/GetCommonDelay` | Full `0xBF801020` |

Device slots (`SSBUSC_DEV` 0..12):

| Id | Name | Delay reg | Base reg |
|----|------|-----------|----------|
| 0 | Exp1 / DEV0 | `0xBF801008` | `0xBF801000` |
| 1 | DVDROM | `0xBF80100C` | `0xBF801400` |
| 2 | BOOTROM | `0xBF801010` | — |
| 3 | (none) | — | — |
| 4 | SPU | `0xBF801014` | `0xBF801404` |
| 5 | CDVD | `0xBF801018` | `0xBF801408` |
| 6–7 | (none) | — | — |
| 8 | Exp2 | `0xBF80101C` | `0xBF801004` |
| 9 | SPU2 | `0xBF801414` | `0xBF80140C` |
| 10 | DEV9I | `0xBF801418` | — |
| 11 | DEV9M | `0xBF80141C` | `0xBF801410` |
| 12 | DEV9C | `0xBF801420` | — |

Delay word bit-fields (Wisi): WRDL/RDDL, RECV/HOLD/FLOT/STRB enables, ATYP (8/16), AINC, IOIS16, EXDL, DECR (2^n decode range), DMAT, ADER, DMAF, WDMA, WAIT.

Module `_start`: `RegisterLibraryEntries` only — **no** hardware poke in the open recreation; retail BIOS may pre-program windows before/while loading SSBUSC. DetPS2 **plants post-boot defaults** so later modules see configured windows.

### What other modules expect after init

| Consumer | Expectation |
|----------|-------------|
| CDVDMAN / CD hardware path | CDVD window base/delay non-zero / ready |
| SPU / SPU2 / LIBSD | SPU(2) base in the `0x1F8xxxxx` / `0x1F9xxxxx` class |
| DEV9 / SPEED / HDD stack | DEV9I/M/C delay programmed |
| ROMDRV / rom0 | BOOTROM delay configured (base fixed in hardware) |
| Any IRX that imports `ssbusc` | Set/Get succeed for wired devices, -1 for holes |

### DetPS2 surface (this port)

| API | Location | Behavior |
|-----|----------|----------|
| Host | `IopSsbuscHost` | Full Set/Get Delay/Base + common-delay helpers |
| Defaults | `ApplyBiosDefaults()` | Plant retail-class bases/delays for all wired slots |
| Boot | `BiosBootHost.FinishIopServices` | Reset + ApplyBiosDefaults |
| Window query | `IsWindowReady(device)` | delay≠0 and base≠0 when base reg exists |
| Ps2System | `IopSsbusc` | Reset with system |

### Intentional HLE divergences

| Divergence | Why |
|------------|-----|
| No real `0xBF8010xx` IOP MMIO | Project has no IOP SSBUS bus-cycle model; software table is the contract |
| Default delay constants approximate | No SSBUSC.bin decomp; values are non-zero / class-correct |
| No cycle-accurate WAIT/DMA timing | Not required for name-level + import ABI HLE |
| EE `0x1000F100` SBUS window stays in MmioBus | That is **SBUS/SIF**, not SSBUSC |

---

## 2. EECONF — contracts

### Real behavior (community / OSD init notes; no ps2sdk module)

EECONF runs on the IOPBTCONF path (optional flag in DetPS2 but present in retail `@800` text):

1. **Initialize peripherals** into a usable post-boot state.
2. **MAC address** — plant Ethernet MAC from MECHACON / board data for SPEED/SMAP consumers.
3. **SPEED capabilities** — expose DEV9/SPEED feature bits (present, 100M, duplex, HDD-related).
4. **ROM version** — manipulate/publish ROM version data (DECKARD collaboration on SCPH-75000+).
5. **PS1 config EEPROM block** — open MECHACON config block used by the browser PS1 driver options and **zero-fill** it on every IOP boot (disc speed / texture options never survive reboot).

No EE SIF RPC sid. No public export table in ps2sdk (BIOS-private).

### DetPS2 surface (this port)

| API | Location | Behavior |
|-----|----------|----------|
| Host | `IopEeconfHost` | Init + MAC/SPEED/ROM version + PS1 block |
| `ApplyBiosInit()` | FinishIopServices | Clear PS1 block, plant MAC/SPEED/ROMVER, ready |
| `ClearPs1ConfigBlock()` | host | 64-byte zero-fill; counter |
| `SetMac` / `GetMac` | host | 6-byte; default `02:00:00:00:00:01` (LAA) |
| `SpeedCaps` | host | Present \| 100M \| FullDuplex by default |
| `RomVersion` | host | Default `0160EC20040614` (SCPH-class plant) |
| `ContractsReady` | host | Aggregate post-init check |
| Ps2System | `IopEeconf` | Reset with system |

### Intentional HLE divergences

| Divergence | Why |
|------------|-----|
| No mechacon serial / real EEPROM | No hardware model; behavioral zero-fill + synthetic MAC |
| No DECKARD emulator hooks | Late slim-only; not needed for generic IOPBTCONF HLE |
| No full peripheral driver init | Drivers have their own HLE (CDVD, SIO2, …); EECONF only marks ready |
| Optional required flag remains false | Matches `BiosBootHost` contract table |

---

## 3. Landed (waves + Phase 2 deepen)

1. **`IopSsbuscHost`** — 13-device tables, Set/Get Delay/Base, common-delay fields, `ApplyBiosDefaults`, `IsWindowReady`.
2. **`IopEeconfHost`** — `ApplyBiosInit`, PS1 block clear, MAC/SPEED/ROM version, `ContractsReady`.
3. **`Ps2System`** — `IopSsbusc` / `IopEeconf` properties + Reset.
4. **`BiosBootHost.FinishIopServices`** — plants both after INTRMAN/TIMEMAN.
5. **Phase 2 (AGENT-I):** `AllWindowsReady` / `ReadyWindowCount`; EECONF `DirtyPs1ConfigBlock` / `IsPs1ConfigAllZero` for re-clear proof.
6. **Docs** — this file; `ROMDIR_GATE.md` SSBUSC/EECONF → **OK**.
7. **Smokes** — `Ssbusc_BusWindowContracts`, `Eeconf_InitContracts` (deepened); boot path asserts windows ready.

**Gate:** SSBUSC → **OK**; EECONF → **OK** (optional required flag remains false in boot table; contracts still complete).

---

## 4. Remaining gaps (non-blocking)

| Gap | Notes |
|-----|-------|
| **Extract SSBUSC.bin + Ghidra** | Confirm export ordinals, any `_start` hardware pokes, exact default delays |
| **Extract EECONF.bin + Ghidra** | Line-for-line MAC/SPEED/ROMVER/EEPROM offsets |
| **IOP SSBUS MMIO** | Map `0xBF8010xx` so R3000 IRX can poke real regs if IOP exec lands |
| **DEV9 / SPEED HLE** | Consumers of SpeedCaps still thin; HDD path optional |
| **EE SBUS 0x1000F100** | Keep as MmioBus ready stubs; do not conflate with SSBUSC |
| **Re-init on SifIopReset** | Today FinishIopServices runs at StartCommercialIop; wire EECONF/SSBUSC re-plant on deferred reboot if titles re-query |

---

## 5. Smokes

```text
Ssbusc_BusWindowContracts
Eeconf_InitContracts
BiosBootHost_IopBtConfContracts   (pre-existing; SSBUSC still required name)
BiosRomdirGate_PortDocsForRequiredModules  (SSBUSC_EECONF.md listed)
```

Run: `dotnet run --project Tests`

---

## 6. Acceptance checklist

- [x] Full project scan (dissection + bios-ports + MmioBus + BiosBoot + IopSystem + ps2sdk ssbusc)
- [x] Generic BIOS HLE only
- [x] SSBUSC bus/window contracts after init
- [x] EECONF optional-but-implemented contracts
- [x] docs/bios-ports/SSBUSC_EECONF.md
- [x] Smokes
- [ ] Worktree left uncommitted for orchestrator merge
