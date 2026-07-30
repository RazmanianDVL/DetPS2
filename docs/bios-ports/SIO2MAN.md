# SIO2MAN service surface — gap analysis + contract HLE

**Agent:** SIO2MAN  
**Date:** 2026-07-30  
**Authority:** PCSX2 `SIO/Sio2.{h,cpp}` + `SioTypes.h` register/FIFO model; BlueRetro DualShock SPI
notes (scanlime / hackaday log); ps2sdk `libpad` / PADMAN open path expectations; existing
DetPS2 `Sio2.cs`, `PadInput.cs`, `MemoryCard.cs`, `RealSifRpc` PADMAN/MCSERV HLE;
`docs/bios-ports/PADMAN.md`.  
**Not authority:** commercial title PCs, MidwayBootAssist, per-game pad/MC hacks.

---

## 1. ROMDIR surface

| Romdir | Role | EE RPC SID | DetPS2 HLE |
|--------|------|------------|------------|
| **SIO2MAN** | IOP SIO2 bus manager (exports for PADMAN/MCMAN) | **none** (`RpcSid=0`) | `Sio2.cs` transfer/ctrl + module register |
| PADMAN | pad RPC over SIF; **imports SIO2MAN** for wire I/O | `0x8000010f` / `0x80000100` | `RealSifRpc.HandlePad` (DMA HLE; bus optional) |
| MCMAN / MCSERV | memory-card manager + EE RPC | `0x80000400` | `HandleMcServ` + `MemoryCard`; bus probe path |

SIO2MAN is **not** an SIF RPC service. Retail PADMAN/MCMAN call SIO2MAN **export library**
functions (`sceSio2*` family) which program IOP MMIO at **`0x1F808200`** and run transfers.
DetPS2 presents the same **contract** via:

1. Module table: `IopModuleHost.RegisterModule("SIO2MAN")` from `BiosBootHost` / soft LOADFILE
   `rom0:SIO2MAN`.
2. Bus HLE: `Sio2` class (FIFO, CTRL/STAT, DualShock config FSM, MC probe/specs stubs).
3. Compact EE/test MMIO alias **`0x1000F600`** (Phase-31 smokes) plus real-relative offsets.

No Ghidra dump of BIOS `SIO2MAN` is in-tree yet (`tools/bios-decomp/` has PADMAN/MCMAN/MCSERV
but not SIO2MAN). Ground truth for registers is the PCSX2 model of the same hardware block.

---

## 2. Hardware / SIO2MAN contracts

### 2.1 IOP register map (real base `0x1F808200`)

| Off | Name | Role |
|-----|------|------|
| `+0x00..0x3C` | SEND3 / CmdQueue[16] | Per-slot command descriptors (port + length) |
| `+0x40..0x4C` | PORT_CTRL0[4] | Port timing / select |
| `+0x50..0x5C` | PORT_CTRL1[4] | Port timing |
| `+0x60` | DATA_IN | TX FIFO write |
| `+0x64` | DATA_OUT | RX FIFO read |
| `+0x68` | CTRL | `START_TRANSFER=1`, `RESET=0xC`, SIO2MAN post-reset `0x3BC` |
| `+0x6C` | CMD_STAT | Connected `0x1100` / disconnected `0x1D100` + port-open nibble |
| `+0x70` | PORT_STAT | default `0xF` |
| `+0x74` | FIFO_STAT | memcard phase hints (`0x83` specs, `0x8B` terminator) |

### 2.2 DetPS2 compact alias (`Sio2.MmioBase = 0x1000F600`)

| Off | R/W | Role (compat + deepen) |
|-----|-----|-------------------------|
| `+0x00` | R/W | DATA out / DATA in |
| `+0x04` | R/W | STAT (TX ready `0x1000`, RX ready `0x2000`) / CTRL start bit0 |
| `+0x08` | R/W | Multitap flag read; port/slot write `(port&1)\|((slot&3)<<1)` |
| `+0x60..0x7C` | R/W | Real-relative DATA_IN/OUT, CTRL, CMD_STAT, PORT_STAT, FIFO_STAT |

CTRL write with bit0 set → `ProcessTransfer()` (models SIO2MAN start).

### 2.3 Device framing (full duplex SPI)

First TX byte selects device:

| Addr | Device |
|------|--------|
| `0x01` | Controller |
| `0x21` | Multitap |
| `0x81` | Memory card |

Header (RX): `FF` (or `00` on short legacy paths) · **mode ID** · **`0x5A`** · payload.

### 2.4 DualShock commands (PADMAN config path)

| Cmd | Name | Config required | HLE |
|-----|------|-----------------|-----|
| `0x42` | Poll | no | Digital / analog / DS2 pressure; active-low buttons |
| `0x43` | Enter/exit config | no | Sets/clears `InConfig`; mode id `0xF3` while in |
| `0x44` | Mode switch + lock | yes | Analog on/off; lock `0x03` |
| `0x45` | Status / identity | yes | DS2: `03 02 AL 02 01 00` |
| `0x46`/`0x47`/`0x4C` | Constants | yes | BlueRetro fixed tables |
| `0x4D` | Vibration map | yes | Stores map; poll consumes motor bytes |
| `0x4F` | Response bytes / poll mask | yes | Enables DS2 (`0x79`) when mask full |
| `0x40`/`0x41` | Pressure / button query | yes | Stub success shapes |

Mode IDs: digital `0x41`, analog DS `0x73`, DS2 pressure `0x79`, config `0xF3`.

Helper: `Sio2.RunPadmanConfigSequence(port)` runs the generic find→config→exit sequence
PADMAN performs after open (no title-specific ordering).

### 2.5 Memory card commands (MCSERV/MCMAN wire subset)

| Cmd | Name | HLE |
|-----|------|-----|
| (short `{0x81}`) | Legacy presence | `00 5A 5D fileCount` (Phase-31 smoke) |
| `0x11` | Probe | present + terminator |
| `0x26` | Get specs | page size 512 + page count stub |
| `0x27`/`0x28` | Set/get terminator | default `0x55` |
| `0xF0` | Auth XOR | success-shaped stub |

Full sector R/W / erase / auth chain remains on the **MCSERV RPC** + `MemoryCard` image path,
not byte-exact PS2 card FAT over SIO2.

### 2.6 SIO2MAN export surface (library, not RPC)

Typical ps2sdk / retail names (ordinals vary by IRX revision):

| Concept | DetPS2 stand-in |
|---------|-----------------|
| `sceSio2Init` / reset | `Sio2.Reset`, CTRL `0xC` |
| `sceSio2Transfer` | `Sio2.Transact` / `ProcessTransfer` |
| Ctrl set / stat get | `WriteRegister` CTRL, `CmdStat` / STAT |
| Port ctrl | PORT_CTRL0/1 + compact `+0x08` slot select |

`BiosBootHost` lists SIO2MAN as **required** commercial fast-path so `sceSifLoadModule("rom0:SIO2MAN")`
and disc-side loads resolve as already resident.

---

## 3. Pre-work DetPS2 surface

| Piece | Status before this port |
|-------|-------------------------|
| FIFO pad poll `0x42` | Stub (treated first byte as cmd; no config FSM) |
| Multitap slot select | Present |
| Memcard short `0x81` | Present (simplified) |
| CTRL start bit | Present |
| DualShock config `0x43..0x4F` | **Missing** |
| CMD_STAT connected | **Missing** |
| Real-relative regs | **Missing** |
| MC probe/specs | **Missing** |
| `RunPadmanConfigSequence` | **Missing** |
| Module register / LOADFILE rom0 | Present |
| Wire coupling into `HandlePad` DMA | Not required (PADMAN HLE fills DMA directly) |

---

## 4. Landed this agent (2026-07-30) — generic, protocol-backed

1. **`Sio2.cs`**: full-duplex device framing; DualShock config FSM; poll digital/analog/DS2
   pressure; MC probe/specs/terminator/auth stub; CTRL reset/start; CMD_STAT;
   real-relative register aliases; `RunPadmanConfigSequence`; multitap/pad select preserved.
2. **`docs/bios-ports/SIO2MAN.md`** (this file).
3. **Smokes**: config enter/status/exit; poll active-low; MC probe; CTRL→STAT; PADMAN config
   sequence helper; keep Phase-31 `Sio2_PadPoll` / `Multitap_FourPorts` / `MemCard_ViaSio2`.
4. **`BiosBootHost`** SIO2MAN role string clarified (no EE RPC).
5. Short note in `BIOS_DISSECTION.md` + PADMAN remaining-gap cross-link.
6. **Zero game / Midway hacks.**

---

## 5. Remaining gaps (out of slice / later)

- Extract + Ghidra decompile real BIOS `SIO2MAN` export table (ordinals / strings) into
  `tools/bios-decomp/SIO2MAN_ALL.txt` — not in current artifact set.
- Literal SEND3 multi-command queue drain (16 descriptors per packet) matching DMA11/12.
- IOP interrupt INTC line 17 on transfer complete.
- Full multitap aggregate packet (4 pads @ 1 MHz) and MC-bus multitap slot switching.
- Byte-exact PS2 memory-card FAT/cluster protocol over SIO2 (page R/W/erase/auth F3/F7).
- Wire `RealSifRpc.TickPadDma` through live `Sio2.Transact` (optional; current DMA HLE is
  already STABLE + active-low and is what libpad polls).
- Disc XSIO2MAN / newer multitap IRX divergences.
- Host rumble actuators → OS gamepad (motor bytes are stored only).

---

## 6. Non-goals

- No MidwayBootAssist / game PC patches.
- No title-specific pad or memcard quirks.
- No commit/push/merge from this worktree.
- No inventing SIF RPC SID for SIO2MAN (retail has none).

---

## 7. Relationship to PADMAN / MCSERV

```
EE libpad / libmc
    │ SIF RPC
    ▼
PADMAN / MCSERV  ──imports──►  SIO2MAN  ──MMIO──►  SIO2 HW  ──SPI──►  pad / MC
    │                              │
    │ DetPS2: HandlePad /          │ DetPS2: Sio2.cs contract HLE
    │ HandleMcServ DMA/RPC         │ (+ module name resident)
    ▼
pad_data_* / MC image
```

PADMAN agent already lands OLD/NEW SIDs and DMA layouts (`docs/bios-ports/PADMAN.md`).
This port supplies the **bus** contracts that a real PADMAN would use for find-pad and
config, and that MCSERV uses for presence/probe — without regressing RPC HLE.
