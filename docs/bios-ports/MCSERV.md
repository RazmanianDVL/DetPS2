# MCSERV / MCMAN RPC surface — gap analysis

**Authority:** Ghidra decomp of BIOS `rom0:MCSERV` (`tools/bios-decomp/MCSERV_ALL.txt`,
string `"PsIImcserv 1.30"`) + ps2sdk `ee/rpc/memorycard/src/libmc.c` (`mcRpcCmd[MC_TYPE_MC]`)
+ `common/include/libmc-common.h` (`mcDescParam_t`, `mcEndParam_t`, result codes)
+ Ghidra decomp of BIOS `rom0:MCMAN` (`tools/bios-decomp/MCMAN_ALL.txt`, `"PsIImcman 130"`,
`"1.1.0.0"` superblock) + Ross Ridge PS2 MCFS (mymc) for on-disk layout.
**Not authority:** commercial title PCs, per-game save hacks, full ECC/wear-leveling port.

**Related:** `docs/BIOS_DISSECTION.md` §6.7 (fno range bug) / §6.8 (MCMAN dual-format).

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
| `sceMcTypePS1` | 1 | getInfo type |
| `sceMcTypePS2` | 2 | getInfo type |

---

## 2. MCMAN dual-format FAT (Phase 4)

### 2.1 Real MCMAN dual-type probe (`FUN_000005ac`)

Per-port state is a `0x180`-byte stride. Card type byte `DAT_0001ee80`:

| Type | Meaning | Probe path |
|------|---------|------------|
| `0` | unknown / absent | fallback |
| `1` | **PS1** format | `FUN_0000714c` succeeds |
| `2` | **PS2** MCFS | `FUN_00002374` superblock path |
| `3` | intermediate | `FUN_00007380` branch |

PS2 format writes superblock version literal **`"1.1.0.0"`** (`FUN_00002e10` / format path).
Page size field often `0x200` or derived; cluster I/O uses `0x2000 / page_len` sector loops.

### 2.2 DetPS2 `MemoryCard` layouts

| `McImageKind` | Magic / detect | `CardType` | Free units | Notes |
|---------------|----------------|------------|------------|-------|
| `DetPs2Native` | `"DETPS2MC"` | PS2 (2) | remaining pages | Default HLE save path; Desktop UX |
| `SonyPs2` | `"Sony PS2 Memory Card Format "` | PS2 (2) | FAT free clusters | Superblock + IFC + FAT + root dir |
| `SonyPs1` | `"MC"` @0 | PS1 (1) | free frames/slots | Classic 128 KB / 128-byte frames |

**Sony PS2 MCFS (subset of mymc layout):**

| Off | Field | HLE value |
|-----|-------|-----------|
| `0x00` | magic[28] | `Sony PS2 Memory Card Format ` |
| `0x1C` | version[12] | `1.1.0.0` |
| `0x28` | page_len | 512 |
| `0x2A` | pages_per_cluster | 2 |
| `0x2C` | pages_per_block | 16 |
| `0x30` | clusters_per_card | size-derived |
| `0x34` | alloc_offset | after IFC+FAT |
| `0x38` | alloc_end | allocatable span |
| `0x3C` | rootdir_cluster | 0 |
| `0x50` | ifc_list[0] | cluster 8 |
| `0x150` | card_type | 2 |
| `0x151` | card_flags | `0x52` |

FAT entries: MSB set = used; low 31 bits = next relative cluster; `0xFFFFFFFF` = end.
Directory entries: 512 B, mode/length/cluster/name — enough for named save I/O and free-count.

**PS1:** header `"MC"`, directory frames 1–15, data from frame 16; usage `0xA0` free / `0x51` first.

### 2.3 MCSERV integration

| RPC | Dual-format behavior |
|-----|----------------------|
| `0x77` FORMAT | `MemoryCard.FormatSonyPs2()` — real superblock for MCMAN probes |
| `0x78` GET_INFO | `CardType` + `FreeUnits` from FAT/blocks |
| `0x7D` ERASE_BLOCK | zeros erase-block (16 pages PS2 / frame PS1) |
| `0x7E`/`0x7F` pages | raw page I/O on active image |
| open/read/write/delete | path through format-aware `WriteFile`/`ReadFile` |

---

## 3. Current DetPS2 surface (this port)

| Piece | Location | Status |
|-------|----------|--------|
| sid `0x80000400` bind | `RealSifRpc.SidMcServ` | OK |
| fno **0x70–0x80** full map | `HandleMcServ` | **Mapped** |
| `mcDescParam_t` / name layouts | `HandleMcServ` helpers | **Corrected** |
| OPEN/CLOSE/SEEK/READ/WRITE/FLUSH | fd table + `MemoryCard` | Dual-format HLE |
| GET_INFO endParam type/free | writes `param` from `CardType`/`FreeUnits` | OK |
| GET_DIR list | `MemoryCard.FileNames` → table | Flat names only |
| FORMAT / DELETE / UNFORMAT | Sony PS2 format path | OK |
| CH_DIR / SET_INFO | success (+ cwd string); SET_INFO accept | Partial |
| ERASE_BLOCK | real zero of erase block | OK (no wear map) |
| READ_PAGE / WRITE_PAGE | `MemoryCard.ReadPage/WritePage` | OK |
| Unknown fno / bad flush fd | `−5` | libmc probe-safe |
| Dual-format FAT (PS1/PS2) | `MemoryCard` + MCSERV | **OK** (Phase 4) |
| ECC spare / wear-leveling | — | **Residual** |
| XMCSERV / sid `0x80000480` | partial `0xFE`/`0x01` | Not full table |

---

## 4. Gaps remaining (residual)

1. **ECC spare area** — real cards store 12 B ECC per 528 B page; HLE uses 512 B data pages only.
2. **XMCMAN/XMCSERV** — full fno table `0xFE`/`0x01`…; getEnt/rename/chgPriority; DEV9 xfrom sid.
3. **Card change detection** — getInfo sync −1 when media swapped (no hotplug model yet).
4. **GET_DIR** — wildcards beyond simple `*`/prefix/suffix; subdirectory trees; dates.
5. **SET_INFO** — create/modify timestamps + attr bits not persisted on all image kinds.
6. **READ/WRITE alignment DMA** — real MCSERV SIF-DMAs head/tail fixup into `mcEndParam_t`;
   HLE writes aligned buffer only (libmc fixup is no-op when size1/size2=0).
7. **Multitap MC slots** — port/slot accepted but single `MemoryCard` instance.
8. **SIO2 low-level path** — separate from MCSERV RPC; `Sio2.EmitMemcard` remains stub-level.
9. **Full MCMAN C transliteration** — 151 functions; not required once dual-format I/O + RPC are green.

---

## 5. Smokes

| Test | Checks |
|------|--------|
| `RealSifRpc_McservRealFunctionNumbers` | bind; open/write/seek/read/flush/close; getInfo type=2; flush fd=−1 → −5; fno 0x06 → −5; getDir ≥1 |
| `MemoryCard_DualFormatFat_Ps1Ps2` | Sony PS2 superblock+FAT free drop/rise; PS1 128KB round-trip; DetPS2 native |
| `RealSifRpc_McservFormatSonyPs2AndPages` | FORMAT→Sony magic page0; getInfo free; write via FAT; ERASE_BLOCK |
| `MemCardManager_ExportImport` | DetPS2 native host save/load |

Run: `dotnet run --project Tests` (or filter via smoke runner).

---

## 6. Non-goals

- No MidwayBootAssist / game PC patches.
- No commit/push/merge from this worktree (local commits OK per campaign).
- No wholesale MCMAN C→C# transliteration of all 151 functions.
