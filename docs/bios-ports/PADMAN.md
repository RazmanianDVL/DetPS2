# PADMAN service surface — gap analysis (Phase 0)

**Authority:** Ghidra decomp of BIOS `rom0:PADMAN` (`tools/bios-decomp/PADMAN_ALL.txt` / `PADMAN_ALL2.txt`,
string `"Pad driver. (99/11/22)"`) + ps2sdk `ee/rpc/pad/src/libpad.c` + existing DetPS2 smokes.
**Not authority:** commercial title PCs, MidwayBootAssist, per-game pad hacks.

---

## 1. Real BIOS PADMAN contracts (decomp ground truth)

### 1.1 RPC service registration (`FUN_000066b0` / `FUN_00006774`)

| SID | Role | Real handler |
|-----|------|--------------|
| **`0x8000010f`** | Primary PADMAN (OLD) | `FUN_0000655c` — switch on **arg word 0** (not fno) |
| **`0x8000011f`** | "Extend Service" | `FUN_00006744` — always logs *"not support"* and returns buffer |

ps2sdk names these `PAD_BIND_RPC_ID1_OLD` / `PAD_BIND_RPC_ID2_OLD`.  
**Newer** disc PADMAN IRX uses `0x80000100` / `0x80000101` (`PAD_BIND_RPC_ID*_NEW`) with
`PAD_RPCCMD_*_NEW` command words. EE libpad tries NEW first, then OLD.

### 1.2 OLD command codes (`FUN_0000655c` switch on `*param_2`)

| Cmd | Name (ps2sdk) | Wrapper | Result field | Semantics (summary) |
|-----|---------------|---------|--------------|---------------------|
| `0x80000100` | OPEN | `FUN_0000626c` → `FUN_00002fa8(port,slot,padArea)` | `+0x0C` | Open slot; fail if already open |
| `0x80000102` | INFO_ACT | `FUN_000062e0` | `+0x14` | Actuator query |
| `0x80000103` | INFO_COMB | `FUN_00006320` | `+0x14` | Combo query |
| `0x80000104` | INFO_MODE | `FUN_00006360` | `+0x14` | Mode table / cur id |
| `0x80000105` | SET_MMODE | `FUN_000063a0` | `+0x14` | Set main mode + lock |
| `0x80000106` | SET_ACTDIR | `FUN_000063e0` | `+0x14` | Actuator direct |
| `0x80000107` | SET_ACTALIGN | `FUN_00006418` | `+0x14` | Actuator align |
| `0x80000108` | GET_BTNMASK | `FUN_00006450` | `+0x0C` | Button mask |
| `0x80000109` | SET_BTNINFO | `FUN_00006488` | `+0x10` | Press-mode button info |
| `0x8000010a` | SET_VREF | `FUN_000064c4` | `+0x1c` | Vref params |
| `0x8000010b` | GET_PORTMAX | `FUN_000064fc` → returns **2** | `+0x0C` | Fixed |
| `0x8000010c` | GET_SLOTMAX | `FUN_00006528` → returns **1** | `+0x0C` | Fixed (no multitap in rom0) |
| `0x8000010d` | CLOSE | `FUN_000062a8` → `FUN_00003274` | `+0x0C` | Close port/slot |
| `0x8000010e` | END | `FUN_00006240` → `FUN_00002f18` | `+0x0C` | Tear down vblank + state |

RPC number from EE is always `1` (`sceSifCallRpc(..., 1, ...)`); command is buffer word 0.

### 1.3 DMA pad buffer — OLD (`pad_data_old`, ~64 B × 2)

rom0:PADMAN writes **old** layout (libpad.c `struct pad_data_old`):

| Off | Field | padGetState / padRead use |
|-----|-------|---------------------------|
| `+0x00` | `frame` (u32) | Higher frame wins double-buffer pick |
| `+0x04` | `state` (u8) | `PAD_STATE_*` (STABLE=6) |
| `+0x05` | `reqState` (u8) | COMPLETE=0 / BUSY=2 |
| `+0x06` | `ok` | non-zero when data good |
| `+0x08` | `data[32]` | `padButtonStatus` (btns **active-low**) |
| `+0x28` | `length` | bytes valid in `data` |
| `+0x2D` | `CTP` | 1=no config / 2=config |
| `+0x2E` | `model` | 1/2/3 |

`padGetState` / `padRead` are **EE-side DMA polls**, not RPC. IOP must keep refreshing open
buffers (vblank / continuous task).

### 1.4 DMA pad buffer — NEW (`pad_data_new`, 256 B × 2)

Used by later PADMAN + `PAD_RPCCMD_*_NEW` (`0x01` open, `0x10` init, …). Metadata at
`state@0x70`, `reqState@0x71`, `buttonDataReady@0x67`, `modeCurId@0x65`, button report at `data[0]`.

---

## 2. Current DetPS2 surface (pre-this-port)

| Piece | Location | Status |
|-------|----------|--------|
| NEW SIDs `0x80000100` / `0x101` | `RealSifRpc.SidPad1/2` | Bound + `HandlePad` |
| NEW cmds `0x01`/`0x06`–`0x12` | `HandlePad` | Present (success stubs + open/init) |
| OLD cmds `0x8000010x` in switch | `HandlePad` | Partially listed; **no** result-field variance |
| **OLD SID `0x8000010f`** | — | **Missing** from Dispatch (bind works for any SID, but calls hit unknown-service `return 1` without DMA open) |
| **OLD SID `0x8000011f`** | — | **Missing** (extend stub) |
| `pad_data_new` DMA | `WritePadDataNew` | Present |
| `pad_data_old` DMA | — | **Missing** |
| Button polarity in DMA | `WriteStatusBuffer` / `WritePadDataNew` | **Active-high** (wrong for `padButtonStatus`) |
| CLOSE removes open slot | — | **Missing** (areas leak forever) |
| END clears all | — | **Missing** |
| `TickPadDma` on VBlank | `BiosHle.OnVblank` | Present |
| `BiosBootHost` PADMAN contract | sid listed as `0x80000100` only | Incomplete vs rom0 |
| RealSifRpc pad smokes | SmokeTests | **None** (only synthetic `SifRpcCmd.PadState` + SIO2/MMIO) |

Docs (`BIOS_DISSECTION.md` §7) previously claimed “all 15 real `0x800001xx` cases; no gap” — that
referred to **command codes** on the NEW SID path, not the **rom0 service id** or **old DMA layout**.

---

## 3. Gaps → implement in this agent slice

1. Register **`SidPadOld1 = 0x8000010f`** / **`SidPadOld2 = 0x8000011f`** as known binds; dispatch
   old1 → `HandlePad` (old style), old2 → extend “not support” (return 0 / no-op).
2. Track open ports with **style** (old vs new); write **`pad_data_old`** or **`pad_data_new`**.
3. CLOSE removes key; END clears all areas.
4. OLD result field offsets for info/set cmds (`+0x14` / `+0x10` / `+0x1c` where decomp says so).
5. Active-low button report in DMA `data[]`.
6. Dualshock-shaped defaults for GET_BTNMASK / info-style queries (generic, not title-specific).
7. Smoke tests: bind old SID, OPEN + TickPadDma → STABLE + active-low buttons; CLOSE; PORTMAX=2.
8. Doc updates: this file + short §PADMAN note in `BIOS_DISSECTION.md`.

## 4. Remaining gaps (full ROMDIR completeness — out of slice)

- Full SIO2 config state machine (CTP find → dualshock config commands → real modeTable/actData).
- Real multitap slot max > 1 when multitap present.
- Per-actuator power budgeting (`"Over Max Consumpt"` path).
- Literal IOP vblank thread registration (rom0 creates threads on OPEN).
- Exact dualshock pressure/actuator byte tables from hardware.
- NEW PADMAN disc-module divergences beyond libpad.c.
- Wire-level SIO2man coupling (separate ROMDIR entry).

## 5. Non-goals

- No MidwayBootAssist / game PC patches.
- No title-specific pad quirks.
- No commit/push/merge from this worktree.
