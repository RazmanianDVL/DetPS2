# CDVD device surface for literal IRX (WP-18 / Track T5)

**Owner:** Track T5 — `src/DetPS2.Core/Cdvd.cs`  
**Date:** 2026-07-30  
**Depends:** WP-11 (IOP quanta scheduled), existing HLE contracts in `docs/bios-ports/CDVD.md`  
**Exit:** CDVDMAN can poll Ready without hang (or logged trap); sector read deterministic smoke still green.

---

## 1. Why this document exists

Retail **CDVDMAN.IRX** does **not** talk to DetPS2 through SIF RPC. It pokes the **mechacon / CDVD register window** on the IOP bus:

| Window | Address |
|--------|---------|
| Physical | `0x1F402000`–`0x1F4020FF` |
| KSEG1 (uncached) | `0xBF402000`–`0xBF4020FF` |
| SSBUSC device 5 base default | `0x1F402000` (`IopSsbuscHost`) |

Decompiled CDVDFSV / CDVDMAN DiskReady paths gate on:

```text
(DAT_bf402005 & 0xc0) == 0x40   // Ready bit6 set, Busy bit7 clear
```

**Before WP-18:** `SystemMemory.IopRead*` returned **silent 0** for this window. That fails the Ready check forever → **IRX CDVDMAN hang** on every DiskReady / NCMD poll.

**After WP-18:** `Cdvd.ReadMmio8` / `WriteMmio8` are attached via `SystemMemory.AttachCdvd`. Unknown offsets log and return `0xFF` (PCSX2-class), not silent 0.

---

## 2. Register map DetPS2 implements

Ground truth: PCSX2 `cdvdRead` / `cdvdWrite`, ps2tek N-command status bits, DetPS2 decomp notes (`DAT_bf402005`, `DAT_bf40200f`).

| Off | Name | R/W | DetPS2 behavior |
|-----|------|-----|-----------------|
| `04` | NCOMMAND | R/W | Last NCMD; write issues command after params |
| `05` | N-READY / NDATAIN | R/W | **Read:** `ComposeReady()` (DiskReady poll). **Write:** NCMD param FIFO |
| `06` | ERROR | R | Clear-on-read latched error |
| `07` | BREAK | W | Abort in-flight busy NCMD |
| `08` | INTR_STAT | R/W | Bit0 = command complete; write-1-to-clear |
| `0A` | STATUS | R | `DriveState` (`SCECdStat*`) |
| `0B` | STATUS STICKY | R | Accumulated status bits |
| `0C`–`0E` | CRT MSF | R | BCD of `LastSector` (CD MSF shape) |
| `0F` | TYPE | R | `DiscType` (`0x14` PS2 DVD); 0 if tray open |
| `13` | SPEED | R | Stable DVD/CD class value when spinning |
| `15` | RSV | R | 0 |
| `16` | SCOMMAND | R/W | Issue SCMD after params on `17` |
| `17` | SREADY / SDATAIN | R/W | Bit6 set = no result bytes left; write = SCMD param FIFO |
| `18` | SDATAOUT | R | Drain SCMD result FIFO |
| `20`–`3A` | Key / DEC_SET | R/W | Stub 0 / accept write |
| other | — | R/W | **Log** + read returns `0xFF` |

### Ready register (`05`) bit layout (ps2tek / PCSX2)

| Bit | Meaning | DetPS2 |
|-----|---------|--------|
| 0 | Error | set on failed NCMD accept |
| 2 | DEV9 connected | always OR’d (`0x04`) |
| 3 | Mecha init | always OR’d (`0x08`) |
| 6 | Drive ready | `0x40` when idle |
| 7 | Busy executing NCMD | `0x80` while async read pending |

`Cdvd.DiskReady()` (HLE/RPC path) and MMIO Ready agree: ready iff not tray-open, not `ReadPending`, and `(MechaconStatus & 0xc0) == 0x40`.

### STATUS (`0A`) — same values as `SCECdvdDriveState`

| Value | Name |
|------:|------|
| `0x00` | Stop |
| `0x01` | Tray open |
| `0x02` | Spin |
| `0x06` | Read |
| `0x0A` | Pause |
| `0x12` | Seek |
| `0x20` | Emergency |

---

## 3. NCMD path (IOP-facing)

1. CDVDMAN writes parameters to `0x05` (FIFO, up to 16 bytes).  
2. Writes command byte to `0x04`.  
3. Polls `0x05` until `(ready & 0xc0) == 0x40`.  
4. Acks `0x08` (W1C).

| NCMD | Name | DetPS2 |
|------|------|--------|
| `00` | NOP | complete + IRQ |
| `01` | Reset | stop + ready + IRQ |
| `02` | Standby | `Standby()` + IRQ |
| `03` | Stop | `Stop()` + IRQ |
| `04` | Pause | `Pause()` + IRQ |
| `05` | Seek | `SeekTo(lsn)` + IRQ |
| `06`/`07`/`08` | Read / CDDA / DVD | `BeginAsyncReadN`; IRQ when `Step` completes (**no DMA3 fill** — see gaps) |
| `09` | GetToc | accept + IRQ (no IOP TOC DMA) |
| `0C` | ReadKey | accept + IRQ |
| `0F` | ChgSpdlCtrl | accept + IRQ |
| other | — | logged stub NOP + IRQ |

---

## 4. SCMD path (IOP-facing)

1. Params → `0x17`.  
2. Command → `0x16`.  
3. Poll `0x17` bit6 clear, drain `0x18`.

Implemented enough for boot poll: GetDiscType (`01`), SubQ stub, mecha version subcmd, tray, RTC shape, BootCertify, ForbidDVDP, AutoAdjust. Unknown SCMDs **log** and return result `0x00` (not hang).

Full NVM / MG / iLink crypto remains HLE residual (`docs/bios-ports/CDVD.md` §8).

---

## 5. HLE vs MMIO (two faces of one device)

| Face | Consumer | Entry |
|------|----------|-------|
| **C# HLE** | EE libcdvd via `RealSifRpc` CDVDFSV SIDs | `DiskReady()`, `ReadSectorsTo`, `SeekTo`, … |
| **IOP MMIO** | Literal CDVDMAN / CDVDFSV IRX | `ReadMmio8` / `WriteMmio8` via IOP bus |

Both share drive state (`DriveState`, `MechaconStatus`, tray, sector buffer, async `Step`). Do **not** invent a second drive model.

RPC contracts remain documented in `docs/bios-ports/CDVD.md`.

---

## 6. Gaps that **will hang** IRX CDVDMAN

These are intentional residual blockers for full literal CDVDMAN (not fixed in WP-18):

1. **IOP DMA channel 3 (CDVD)** — NCMD Read expects DMA into IOP RAM (`HW_DMA3_*`). DetPS2 has no IOP DMAC CDVD channel. Async read updates internal buffer + Ready/IRQ only; **sector payload never lands in IOP memory** for pure-MMIO reads.  
2. **IOP INTC cause 2 (CDVD)** — completion currently raises EE-side SIF as a stand-in (`Intc.InterruptSource.Sif`). Real CDVDMAN may `WaitSema` / poll IOP INTC bit 2 forever if that path is used exclusively.  
3. **INTRMAN / THREADMAN waiters** — CDVDFSV uses event flags / vblank; needs WP-10/WP-17 depth.  
4. **Full SCMD mechacon (NVM, MG auth, config blocks)** — stubs return success-shaped zeros; titles that *require* real NVM content fail later, not at Ready poll.  
5. **DVD 2064-byte raw sector layout / dual-layer optics** — HLE uses 2048 user data.  
6. **Stream ring / CDVDSTM** — game IRX, not BIOS ROMDIR CDVDMAN.  
7. **SSBUSC delay programming** — base is defaulted; if IRX reprograms delay to zero and depends on wait states, behavior is unmodeled.  
8. **Silent non-CDVD IOP MMIO** — other devices (SIO2, timers, IOP DMAC regs) still return 0 from `IopRead*` unless their track wires them; CDVDMAN specifically no longer silent-zeros its own window.

---

## 7. Tests

| Test | What it proves |
|------|----------------|
| `Cdvd_ReadSector_Deterministic` | Sector buffer + mount path unchanged |
| `Cdvd_MechaconDiskReadyAfterMount` | HLE DiskReady 2/6 after mount / tray |
| `Cdvd_MmioReadyAndDiskReady_IopBus` | IOP `IopRead8(0x1F402005)` / KSEG1 alias sees Ready; NCMD seek; unknown reg logs |

---

## 8. Files touched (WP-18)

- `src/DetPS2.Core/Cdvd.cs` — MMIO surface + compose Ready  
- `src/DetPS2.Core/SystemMemory.cs` — `AttachCdvd` + IOP route  
- `src/DetPS2.Core/Ps2System.cs` — attach  
- `docs/irx/CDVD_DEVICE.md` — this file  
- `Tests/SmokeTests.cs` — MMIO smoke  

**Forbidden for T5:** GameQuirks plants, wholesale `RealSifRpc` edits.
