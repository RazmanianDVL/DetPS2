# SIO2 / pad device surface for PADMAN IRX (WP-21 · Track T6)

**Status:** device contract ready for IRX handoff  
**Date:** 2026-07-30  
**Owned code:** `Sio2.cs`, `PadInput.cs`, `MemoryCard.cs` (device surface)  
**Authority:** PCSX2 `SIO/Sio2.{h,cpp}` + `SioTypes.h`; BlueRetro DualShock SPI; ps2sdk libpad; BIOS `rom0:PADMAN` decomp notes in `docs/bios-ports/PADMAN.md`  
**Not authority:** title assists, MidwayBootAssist, GameQuirks  

---

## 1. What PADMAN IRX needs (end-to-end path)

Retail stack when **literal IRX** is running:

```text
EE libpad (scePad*)
    │  SIF RPC  (SID 0x80000100 NEW  or  0x8000010f OLD)
    ▼
PADMAN.IRX  (R3000 on IOP)          ← WP-28 / G4; needs T2 exec
    │  import library calls
    ▼
SIO2MAN.IRX  (sceSio2* exports)     ← WP-28; needs T2 link+exec
    │  MMIO + DMA11/12
    ▼
SIO2 hardware @ 0x1F808200          ← THIS WP (T6 device HLE)
    │  full-duplex SPI
    ▼
Pad / Multitap / MemoryCard
```

### 1.1 SIO2MAN → hardware (what the device must accept)

| Step | SIO2MAN / PADMAN action | DetPS2 device API |
|------|-------------------------|-------------------|
| 1 | Soft reset CTRL `0xC` → idle `0x3BC` | `WriteRegister(…+0x68, 0xC)` / `Reset` |
| 2 | Program **SEND3[0..N]** descriptors (port + length) | `ProgramSend3` / write `IopPhysBase+0x00` |
| 3 | Program PORT_CTRL0/1 timing (often ignored by HLE) | `+0x40…0x5C` |
| 4 | Push TX bytes to DATA_IN (`+0x60`) or DMA11 | FIFO push / `Transact` / `TransactIop` |
| 5 | CTRL `START_TRANSFER` bit0 | `ProcessTransfer` |
| 6 | Poll / IRQ: transfer complete | **iStat bit0** + `OnTransferComplete` → IOP INTC **line 17** |
| 7 | Drain DATA_OUT (`+0x64`) or DMA12 | FIFO read |
| 8 | Read CMD_STAT for connected/missing | `CmdStat` / `+0x6C` |

### 1.2 Pad wire protocol (PADMAN find + config + poll)

Device address byte `0x01` (pad), full-duplex:

| TX (host) | RX (pad) |
|-----------|----------|
| `01` addr | `FF` hi-Z |
| `42` poll / `43` config / … | mode id (`41`/`73`/`79`/`F3`) |
| dummy | `5A` |
| params / motors | payload (buttons **active-low**, sticks, pressure) |

Config sequence after open (generic DualShock 2):  
`0x43 enter → 0x45 status → 0x46/47/4C constants → 0x44 mode+lock → 0x4D vib map → 0x43 exit`  
Helper: `Sio2.RunPadmanConfigSequence(port)`.

Poll after config: mode `0x79` (DS2) or `0x73` (analog), buttons active-low, then RX/RY/LX/LY (+ 12 pressure bytes for `0x79`).

### 1.3 Memcard wire subset (MCSERV residual; WP-29)

Address `0x81`: short presence, probe `0x11`, specs `0x26`, terminator `0x27`/`0x28`, auth stub `0xF0`.  
Full sector FAT I/O remains on MCSERV RPC + `MemoryCard` image — not required for pad OPEN.

---

## 2. DetPS2 device surface (landed WP-21)

### 2.1 Address windows

| Base | Who uses it | Map |
|------|-------------|-----|
| **`Sio2.IopPhysBase` `0x1F808200`** | Real SIO2MAN/PADMAN IRX (KSEG1 `0xBF808200`) | **Real:** SEND3[16] @ +0x00…3C, PORT_CTRL @ +0x40…5C, DATA_IN +0x60, DATA_OUT +0x64, CTRL +0x68, CMD_STAT +0x6C, PORT_STAT +0x70, FIFO_STAT +0x74, **iStat +0x80** |
| **`Sio2.MmioBase` `0x1000F600`** | EE/test + Phase-31 smokes | Compact DATA/STAT/CTRL + real-relative aliases |

`Sio2.TryGetIopOffset(addr, out off)` / `IsIopAddress` accept phys or KSEG-masked addresses.

### 2.2 Pad / transfer contracts

| Feature | Status |
|---------|--------|
| DualShock poll `0x42` digital/analog/DS2 pressure | **OK** (active-low buttons) |
| Config FSM `0x43…0x4F` | **OK** |
| `RunPadmanConfigSequence` | **OK** |
| Multitap slot select + presence | **OK** (aggregate 4-pad packet residual) |
| SEND3 port + length on transfer | **OK** (single-descriptor HLE; multi-slot drain residual) |
| iStat bit0 + `OnTransferComplete` | **OK** (device-side; IOP INTC wire = handoff) |
| `TransactIop(port, cmd)` real-window helper | **OK** |
| CMD_STAT connected / missing shapes | **OK** |
| MC probe/specs/terminator | **OK** (page count aligned to `MemoryCard.DefaultPages`) |
| Infrared addr `0x61` silent bus | **OK** (disconnect) |

### 2.3 PadInput / MemoryCard

- **`PadInput`**: host digital+analog state; MMIO `0x1000F400` for EE tests; `Buttons` active-high in host API; wire path inverts via `Sio2` / `Sio2.ActiveLowButtons`.
- **`MemoryCard`**: image formats for MCSERV; SIO2 only needs present + probe/specs stubs for pad path.

### 2.4 Smokes (must stay green)

| Smoke | Covers |
|-------|--------|
| `Pad_InputReadable` | Host pad + EE MMIO |
| `Pad_Analog_MmioAndRpc` | Analog sticks + synthetic PadState RPC |
| `Sio2_PadPoll` | Analog poll mode `0x79` |
| `Multitap_FourPorts` | Slot select |
| `MemCard_ViaSio2` | Short `0x81` presence |
| `Sio2_DualShockConfigFsmAndActiveLow` | Config + active-low |
| `Sio2_MemcardProbeAndCtrlStat` | MC probe + CTRL start + STAT |
| `Sio2_PadmanConfigSequenceHelper` | Full open config |
| `Sio2_IopPhysSend3AndIstat` | **WP-21** real IOP map + SEND3 + iStat |
| `Sio2_Send3PortAndTransferIrqHook` | **WP-21** port from SEND3 + IRQ callback |

---

## 3. Handoffs (not T6)

| Need | Owner | Why |
|------|-------|-----|
| `SystemMemory.IopRead/Write*` route `0x1F808200` → `Sio2.Read/WriteRegister` | **T1** (IOP core / memory bus) | Today IOP unmapped writes are ignored; IRX cannot see the device until wired. Use `Sio2.TryGetIopOffset`. |
| IOP INTC line **17** on `OnTransferComplete` | **T1** | No IOP INTC model yet; device only exposes iStat + callback. |
| DMA11 (OUT to SIO2) / DMA12 (IN from SIO2) complete | **T2** / dmacman host | `IopDmacManHost` has ch 11/12 enable bits; slice→FIFO residual. |
| Load + **exec** SIO2MAN + PADMAN from ROMDIR/disc | **T2** (+ T3 boot chain) | Module context, imports link, `_start` |
| SIF RPC bind/call into live PADMAN (not only `RealSifRpc.HandlePad`) | **T4** + **T2** | WP-28 G4 |
| Prefer live pad DMA over HLE `TickPadDma` when IRX owns SID | **T4**/T10 later | Optional; HLE already STABLE+active-low |

---

## 4. What PADMAN IRX still needs from IOP exec (T2) — checklist

This is the **WP-28 / G4 residual list** from the device track’s point of view.

### 4.1 Module load / link

1. **Load** `rom0:SIO2MAN` (or disc `XSIO2MAN` / title `SIO2MAN.IRX`) into IOP RAM with real REL sections.  
2. **Link imports** of SIO2MAN: at least `INTRMAN`, `DMACMAN`, `THBASE`/`THREADMAN`, `HEAPLIB`/`SYSMEM`, `LOADCORE` export registration.  
3. **Run `_start`** so SIO2MAN registers its export table (`sceSio2*` family).  
4. **Load + link + start PADMAN** after SIO2MAN (PADMAN imports SIO2MAN ordinals).  
5. **Export resolver**: PADMAN calls must hit SIO2MAN text, not dead stubs.

### 4.2 Runtime services SIO2MAN/PADMAN import

| Import area | Why PADMAN dies without it |
|-------------|----------------------------|
| `CreateThread` / `StartThread` / `SleepThread` / `iWakeupThread` | PADMAN vblank/update thread after OPEN |
| `RegisterIntrHandler` + enable IOP IRQ (SIO2=17, VBlank) | Transfer complete + pad refresh |
| `SetEventFlag` / `WaitEventFlag` (or sema) | Sync open vs wire find |
| `sceSifSetDma` / sifcmd / rpc register | EE `scePadInit` / `scePadPortOpen` path |
| `dmacman` SetSlice/Start ch 11–12 | Bulk SEND3 transfers (not only PIO DATA_IN) |
| Heap alloc (`AllocSysMemory` / heaplib) | Pad work buffers, DMA areas |

### 4.3 Device wiring once IRX runs

6. IOP load/store to **`0xBF808200`** must reach `Sio2` (T1 SystemMemory).  
7. On transfer complete, raise **IOP INTC 17** so SIO2MAN’s handler drains FIFO and unblocks PADMAN.  
8. Optional: DMA11 fill `_inFifo` from IOP RAM; DMA12 write `_outFifo` to IOP RAM (can start as “instant complete” like current dmacman HLE if PIO path works).

### 4.4 EE-visible success (G4)

9. EE `sceSifBindRpc` to PADMAN SID (NEW `0x80000100` or OLD `0x8000010f`) succeeds because **live** module registered RPC (not only HLE bind table).  
10. `scePadPortOpen(0,0,buf)` → IOP OPEN runs find-pad over SIO2 → DMA buffer reaches **STABLE** with active-low buttons.  
11. Under `DETPS2_LITERAL_IRX=1`, prefer IRX path; HLE `HandlePad` is fallback only (WP-49 fail-fast later).

### 4.5 Explicitly out of WP-21

- Multitap full aggregate packet, rumble host actuators, byte-exact MC FAT over SIO2.  
- Decomp of full SIO2MAN export ordinal table (still missing in `tools/bios-decomp/`).  
- Replacing `RealSifRpc.HandlePad` (stays until WP-28 proves live OPEN).

---

## 5. Exit criteria (WP-21)

| # | Criterion | Evidence |
|---|-----------|----------|
| 1 | SIO2 MMIO + pad bytes documented for PADMAN | This file |
| 2 | Transfer/poll strengthened (SEND3, iStat, IOP base, config FSM) | `Sio2.cs` |
| 3 | `Pad_*` / `Sio2_*` smokes green | Test run |
| 4 | T2 needs listed | §4 |

**Next track gate:** WP-28 (T6+T2) — PADMAN IRX executes; pad OPEN port0 → **G4**.

---

## 6. Cross-links

- `docs/bios-ports/SIO2MAN.md` — earlier HLE gap analysis  
- `docs/bios-ports/PADMAN.md` — RPC SIDs + DMA layouts  
- `docs/IRX_EXECUTION_PHASE_PLAN.md` — WP-21 / WP-28 / G4  
- `docs/bios-ports/DMACMAN.md` — ch 11–12  
