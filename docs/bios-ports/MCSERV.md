# MCSERV / MCMAN RPC surface — gap analysis

**Authority:** Ghidra decomp of BIOS `rom0:MCSERV` (`tools/bios-decomp/MCSERV_ALL.txt`,
string `"PsIImcserv 1.30"`) + ps2sdk `ee/rpc/memorycard/src/libmc.c` (`mcRpcCmd[MC_TYPE_MC]`)
+ `common/include/libmc-common.h` (`mcDescParam_t`, `mcEndParam_t`, result codes).
**Not authority:** commercial title PCs, per-game save hacks, full MCMAN FAT rewrite.

**Related:** `docs/BIOS_DISSECTION.md` §6.7 (fno range bug) / §6.8 (MCMAN scoped out).

---

## 1. Real BIOS MCSERV contracts

### 1.1 Service registration (`FUN_000000c0`)

| Field | Value |
|-------|--------|
| **sid** | `0x80000400` (`RealSifRpc.SidMcServ`) |
| **RPC handler** | `FUN_00000144` — switch on **rpc_number (fno)** |
| **Arg buffer** | server static `DAT_00003248` (bind-time buffer EE fills via SIF) |
| **Result** | single `s32` at `DAT_00001240` → EE `recvbuf` |

XMCSERV (extended) uses **different** fnos (`0xFE` init, `0x01` getInfo, …) on the same or
alternate sid (`0x80000480` for DEV9/xfrom). rom0 MCSERV only implements **0x70–0x80**.

### 1.2 Real fno map (`FUN_00000144` ↔ ps2sdk `mcRpcCmd[0]`)

| fno | Name (ps2sdk) | Handler | Arg shape | Result (summary) |
|-----|---------------|---------|-----------|------------------|
| `0x70` | INIT | `FUN_00000320` | `mcDescParam_t` (offset magic −217) | 0 |
| `0x71` | OPEN | `FUN_00000360` | **name param** | fd ≥ 0 / error |
| `0x72` | CLOSE | `FUN_00000390` | desc.fd | 0 / error |
| `0x73` | READ | `FUN_000003e4` | desc (size, buffer, param) | bytes read |
| `0x74` | WRITE | `FUN_00000624` | desc (size, origin, buffer, data[16]) | bytes written |
| `0x75` | SEEK | `FUN_000003b4` | desc (fd, offset, origin) | new position |
| `0x76` | GET_DIR | `FUN_00000730` | **name param** (maxent, table, pattern) | entry count |
| `0x77` | FORMAT | `FUN_000008fc` | desc port/slot | 0 |
| `0x78` | GET_INFO | `FUN_00000954` | desc + endParam via param ptr | 0 / −1 / −2 … |
| `0x79` | DELETE | `FUN_00000a64` | **name param** | 0 / −4 |
| `0x7A` | FLUSH | `FUN_00000a94` | desc.fd | 0; **bad fd → −5** |
| `0x7B` | CH_DIR | `FUN_0000085c` | **name param** + curdir out | 0 |
| `0x7C` | SET_INFO | `FUN_00000f5c` | **name param** + file info | 0 |
| `0x7D` | ERASE_BLOCK | `FUN_00000ab8` | desc (port/slot/block/mode) | 0 / error |
| `0x7E` | READ_PAGE | `FUN_00000bf8` | desc (page in fd field, buffer) | 0 |
| `0x7F` | WRITE_PAGE | `FUN_00000df8` | desc (page, buffer, data[16] align) | 0 |
| `0x80` | UNFORMAT | `FUN_00000928` | desc port/slot | 0 |

Default / unknown fno: real code does **not** update the result word (stale). DetPS2 returns
`sceMcResDeniedPermit` (**−5**) so libmc’s XMCSERV probe (`fno=0xFE`, flush with `fd=−1`)
falls back to MCSERV correctly.

### 1.3 Wire layouts (`libmc-common.h` + `libmc.c`)

**`mcDescParam_t` (48 B)** — INIT/CLOSE/SEEK/READ/WRITE/FLUSH/FORMAT/GET_INFO/pages:

| Off | Field | Used by |
|-----|-------|---------|
| `+0` | `fd` (or **page** for READ/WRITE_PAGE) | close/seek/read/write/flush/pages |
| `+4` | `port` | format/getInfo/erase/pages |
| `+8` | `slot` | format/getInfo/erase/pages |
| `+12` | `size` | read/write; **GET_INFO want-type flag** (old MCSERV) |
| `+16` | `offset` | seek; **GET_INFO want-free flag** |
| `+20` | `origin` | seek whence; write unaligned head len; **GET_INFO want-format flag** |
| `+24` | `buffer` | EE data pointer (read/write/page) |
| `+28` | `param` | EE `mcEndParam_t` / align fixup / getInfo out |
| `+32` | `data[16]` | write unaligned head / writePage misalign |

**Name param** — OPEN/GET_DIR/DELETE/CH_DIR/SET_INFO:

| Off | Field |
|-----|-------|
| `+0` | port |
| `+4` | slot |
| `+8` | flags (open mode / getdir mode / setinfo mask) |
| `+12` | maxent (getdir) |
| `+16` | `sceMcTblGetDir*` or `curdir` out ptr |
| `+20` | `name[1024]` |

**`mcEndParam_t` (64 B)** — GET_INFO / READ align fixup (old MCSERV):

| Off | Field |
|-----|-------|
| `+0` | type (union size1) |
| `+4` | free (union size2) |
| `+8`/`+12` | dest1/dest2 |
| `+16`/`+32` | src1[16]/src2[16] |

**`sceMcTblGetDir` (64 B)** — GET_DIR table entries: `_Create`@0, `_Modify`@8,
`FileSizeByte`@16, `AttrFile`@20, `EntryName[32]`@32.

### 1.4 Result codes (libmc-common.h)

| Code | Value | Notes |
|------|-------|-------|
| `sceMcResSucceed` | 0 | |
| `sceMcResChangedCard` | −1 | getInfo sync |
| `sceMcResNoFormat` | −2 | |
| `sceMcResNoEntry` | −4 | missing file |
| `sceMcResDeniedPermit` | −5 | bad fd / unhandled RPC |
| `sceMcTypePS2` | 2 | getInfo type |

---

## 2. Current DetPS2 surface (this port)

| Piece | Location | Status |
|-------|----------|--------|
| sid `0x80000400` bind | `RealSifRpc.SidMcServ` | OK |
| fno **0x70–0x80** full map | `HandleMcServ` | **Mapped** (this agent) |
| `mcDescParam_t` / name layouts | `HandleMcServ` helpers | **Corrected** (was size@+8/buf@+4) |
| OPEN/CLOSE/SEEK/READ/WRITE/FLUSH | fd table + `MemoryCard` | Lightweight HLE |
| GET_INFO endParam type/free | writes `param` | OK |
| GET_DIR list | `MemoryCard.FileNames` → table | Flat names only |
| FORMAT / DELETE / UNFORMAT | `MemoryCard` | OK |
| CH_DIR / SET_INFO | success stubs (+ cwd string) | Partial |
| ERASE_BLOCK | success stub | No real erase semantics |
| READ_PAGE / WRITE_PAGE | `MemoryCard.ReadPage/WritePage` | DetPS2 image pages, not Sony FAT |
| Unknown fno / bad flush fd | `−5` | libmc probe-safe |
| Full MCMAN FAT / ECC / dual PS1 | — | **Scoped out** (§6.8) |
| XMCSERV / sid `0x80000480` | — | Not implemented |

Backend is DetPS2’s own `MemoryCard` image (directory table after superblock), **not** a
byte-exact port of MCMAN’s 512-byte cluster FAT. Sufficient for titles that only need
RPC success + named save I/O through libmc; not for titles that dig into raw Sony pages.

---

## 3. Gaps remaining (out of this slice)

1. **Full MCMAN filesystem** — cluster chains, `"1.1.0.0"` superblock, ECC, PS1 dual format
   (`MCMAN_ALL.txt` 151 functions). Revisit if a title stalls on real card layout, not RPC.
2. **XMCMAN/XMCSERV** — fno table `0xFE`/`0x01`…; getEnt/rename/chgPriority; DEV9 xfrom sid.
3. **Card change detection** — getInfo sync −1/−2 when media swapped (no hotplug model yet).
4. **GET_DIR** — wildcards beyond simple `*`/prefix/suffix; subdirectory trees; dates.
5. **SET_INFO** — create/modify timestamps + attr bits not persisted on HLE entries.
6. **READ/WRITE alignment DMA** — real MCSERV SIF-DMAs head/tail fixup into `mcEndParam_t`;
   HLE writes aligned buffer only (libmc fixup becomes no-op when size1/size2=0).
7. **ERASE_BLOCK** real semantics (block erase before write-page).
8. **Multitap MC slots** — port/slot accepted but single `MemoryCard` instance.
9. **SIO2 low-level path** — separate from MCSERV RPC; `Sio2.EmitMemcard` remains stub-level.

---

## 4. Smokes

| Test | Checks |
|------|--------|
| `RealSifRpc_McservRealFunctionNumbers` | bind; open/write/seek/read/flush/close; getInfo type=2; flush fd=−1 → −5; fno 0x06 → −5; getDir ≥1 |

Run: `dotnet run --project Tests` (or filter via smoke runner).

---

## 5. Non-goals

- No MidwayBootAssist / game PC patches.
- No commit/push/merge from this worktree.
- No wholesale MCMAN C→C# transliteration.
