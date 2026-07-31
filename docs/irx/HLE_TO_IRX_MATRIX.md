# HLE → IRX matrix (WP-03 / Track T10)

**Status:** inventory — every known `RealSifRpc` service id (SID) and major soft-success path  
**Policy:** under `DETPS2_LITERAL_IRX=1`, IRX-owned SIDs must eventually execute real IOP module code; HLE remains **DEBT** until then  
**Sources:** `src/DetPS2.Core/RealSifRpc.cs`, `docs/bios-ports/*`, `docs/IRX_EXECUTION_PHASE_PLAN.md`  
**Do not edit GameQuirk sources from this track** — list only (demolition = WP-40/46–48).

Legend:

| Status | Meaning |
|--------|---------|
| **DEBT** | Handled in C# HLE / soft-success; target is real IRX (or thin device under IRX) |
| **DEVICE** | Host device surface is OK long-term; IRX should drive it (not reimplemented in EE plants) |
| **N/A** | Not an IOP RPC SID (SIFCMD cid / EE-only); keep for transport |
| **TITLE** | Proprietary game IRX — load from disc IOPRP / MODULES, not BIOS ROMDIR |

---

## 1. BIOS / SCE RPC SIDs

| SID | Const | Current HLE path | Target IRX / stack | Status | Notes / soft-success |
|-----|-------|------------------|--------------------|--------|----------------------|
| `0x80000001` | `SidFileIo` | `HandleFileIo` | **FILEIO.IRX** (+ **IOMAN**) | **DEBT** | Unknown fno 17–64 soft-success 0 (XFILEIO-class); G3 gate |
| `0x80000003` | `SidSysmem` | `HandleSysmem` (iopheap) | **SYSMEM** (+ iopheap client) | **DEBT** | Unknown fno soft-success **1**; LoadIopHeap soft-0 without media |
| `0x80000006` | `SidLoadFile` | `HandleLoadFile` | **LOADFILE.IRX** (+ **MODLOAD**) | **DEBT** | MOD_LOAD path loads bytes; `_start` not executed yet |
| `0x80000100` | `SidPad1` | `HandlePad` (NEW) | **PADMAN** (disc/X) | **DEBT** | G4 gate — OPEN port0 via live IRX |
| `0x80000101` | `SidPad2` | `HandlePad` (NEW) | **PADMAN** secondary | **DEBT** | |
| `0x8000010F` | `SidPadOld1` | `HandlePad` (OLD) | **rom0:PADMAN** | **DEBT** | BIOS old bind IDs |
| `0x8000011F` | `SidPadOld2` | bind-only / reject path | **rom0:PADMAN** extend | **DEBT** | Real IRX logs "not support" |
| `0x80000220` | `SidPl2303Usb` | soft-success 0 | **PL2303.IRX** / **USBD** | **DEBT** | SotC bind after load; calls return 0 |
| `0x80000400` | `SidMcServ` | `HandleMcServ` | **MCSERV** (+ **MCMAN**) | **DEBT** | XMCSERV `0x80000480` partial elsewhere |
| `0x80000592` | `SidCdBase` | `HandleCdInit` | **CDVDFSV** / **CDVDMAN** | **DEBT** | DEVICE: `Cdvd.cs` underneath |
| `0x80000593` | `SidCdScmd` | `HandleCdScmd` | **CDVDFSV** SCMD | **DEBT** | DEVICE bridge |
| `0x80000595` | `SidCdNcmd` | `HandleCdNcmd` | **CDVDFSV** NCMD | **DEBT** | Real disc data path |
| `0x80000597` | `SidCdSearchFile` | `HandleCdSearchFile` | **CDVDFSV** | **DEBT** | ISO9660 host path |
| `0x8000059A` | `SidCdDiskReady` | DiskReady 2/6 | **CDVDFSV** | **DEBT** | |
| `0x8000059C` | `SidCdDiskReady2` | same as 0x59a | **CDVDFSV** (IOPRP 2.8+) | **DEBT** | B3 binds twin |
| `0x80000701` | `SidSdReg` | soft-success 0 | **SDRDRV.IRX** (title) / SPU2 | **DEBT** | Midway raw SPU2 register RPC |
| `0x80001300` | `SidDbcMan` | `HandleDbcMan` | **DBCMAN.IRX** | **DEBT** | libdbc 3.10 version probe |
| `0x8000131B` | (sibling) | DbcMan sibling soft path | **DBCMAN** / DS2O family | **DEBT** | B3 after GTFSCDVD |
| `0x8000131C` | (sibling) | same | same | **DEBT** | |
| `0x8000131E` | (sibling) | same | same | **DEBT** | |
| `0x8000131F` | (sibling) | same | same | **DEBT** | |

### SIFCMD command IDs (not RPC SIDs)

| CID | Const | Role | Status |
|-----|-------|------|--------|
| `0x80000000` | `CidSifInit` | SIFCMD INIT family | **N/A** transport — target **SIFCMD** / **SIFMAN** IRX |
| `0x80000001` | `CidSifSetSreg` | SET_SREG (cmd ns) | **N/A** — numeric collide with FILEIO sid |
| `0x80000008` | `CidRpcEnd` | RPC_END reply | **N/A** |
| `0x80000009` | `CidRpcBind` | BIND | **N/A** |
| `0x8000000A` | `CidRpcCall` | CALL | **N/A** |
| `0x8000000C` | `CidRpcRdata` | RDATA | **N/A** |

---

## 2. Title / middleware RPC SIDs (proprietary IRX)

| SID | Const | Titles | Target IRX | Status | Soft-success / plant notes |
|-----|-------|--------|------------|--------|----------------------------|
| `0x534E4446` | `SidSndf` ("SNDF") | Midway family | **SNDFI.IRX** / SNDF_Driver | **DEBT** / TITLE | fno `0x1300` success=0 |
| `0x53465356` | `SidSfsv` ("SFSV") | Midway | same module as SNDF | **DEBT** / TITLE | soft 0 |
| `0x90000200` | `SidCriAdx` | Midway / CRI | **CRI_ADXI.IRX** | **DEBT** / TITLE | echo special-case in HandleCall |
| `0x00534E03` | `SidSnProdg` | BO2 / Crystal | SN ProDG residual (no public IRX) | **DEBT** | soft 0 so boot continues |
| `0x00123456` | `Sid989Snd` | 989 middleware | **989snd** / **989nomid** | **DEBT** / TITLE | soft bind+reply |
| `0x00123457` | `Sid989Snd2` | 989 stream | same | **DEBT** / TITLE | |
| `0x00012345` | `SidMsl` | MK:DA family | **MSL.IRX** | **DEBT** / TITLE | init fno `0xDADA` |
| `0x00012347` | `SidMslMfl` | MK:DA | MFL (MSL file link) | **DEBT** / TITLE | open/read for `MKDA.PAK` |
| `0x00000020` | `SidIopFile20` | BO2 / Whiplash | **IOPFILE.IRX** (GOE_FSRV) | **DEBT** / TITLE | archive / RKV |
| `0x00000021` | `SidIopFile21` | BO2 | **IOPFILE.IRX** | **DEBT** / TITLE | |
| `0x00000029` | `SidIopFile29` | BO2 | **IOPFILE.IRX** | **DEBT** / TITLE | |
| `0x00000030` | `SidIopFile30` | BO2 | **IOPFILE.IRX** | **DEBT** / TITLE | |
| `0x00000031` | `SidIopFile31` | Whiplash | **IOPFILE.IRX** | **DEBT** / TITLE | |
| `0x00000040` | `SidIopFile40` | Whiplash | **IOPFILE.IRX** | **DEBT** / TITLE | |
| `0x00475453` | `SidGtfsStg` ("STG") | Burnout 3 | **GTFSCDVD.IRX** / GTFS | **DEBT** / TITLE | soft-success stage RPC |
| `0x53465447` | ("GTFS" fourCC) | Burnout 3 | same | **DEBT** / TITLE | alt fourCC form |
| `0x00150276` | `SidB3Aux` | Burnout 3 | residual post-LGDEV | **DEBT** / TITLE | soft-OK only; no DMA into stack |
| `0x046D046D` | `SidLgDev` | Burnout 3 | **LGDEVW.IRX** / LGKBM / LGAUD | **DEBT** / TITLE | fno 12 version plant `0x010B1B00`; other fnos soft empty inventory |
| `0x000F0001` | `SidMwFileMain` | MK Dec/DA family | **MWFILEFR.IRX** | **DEBT** / TITLE | open/read → FILEIO bridge |
| `0x000F0002` | `SidMwFileAux` | same | **MWFILEFR.IRX** aux | **DEBT** / TITLE | fno `0xC8` |
| `0x000F1002` | `SidMwFileEeServer` | same | EE reverse server id | **N/A** | not IOP service |

Disc IRX samples (local extract, gitignored): `tools/b3-irx/` (B3ROUTE, DBCMAN, GTFSCDVD, LGAUD, LGDEVW, LGKBM, RWA).

---

## 3. Soft-success / debt hotspots (RealSifRpc)

| Location | Behavior | Replace with |
|----------|----------|--------------|
| FILEIO default fno **17–64** | return **0** + log `soft-success unknown fno` | Live **FILEIO** / XFILEIO IRX (WP-27, WP-30) |
| SYSMEM unknown fno | return **1** | Live **SYSMEM** / iopheap |
| SYSMEM `LoadIopHeap` no host / rom0 miss | soft **0** | Real media + IRX load path |
| PL2303 calls | return **0** | PL2303.IRX or fail-closed under LITERAL_IRX |
| SDRDRV / SFSV / SnProdg | return **0** | Disc IRX or documented stub IRX |
| LGDEV non-version fnos | empty inventory success | **LGDEV\***.IRX |
| GTFS / SidB3Aux | soft-OK | **GTFSCDVD** IRX |
| MWFILE invalid fno | soft 0 safer than −1 | **MWFILEFR** IRX |
| Generic unknown bind SID | `UnknownBindSids++` still completes bind | Fail-fast under LITERAL_IRX (WP-49) |
| Generic unknown call | often result **1** | Fail-fast / live IRX (WP-49) |

**Demotion rule (WP-30 / WP-49):** when `DETPS2_LITERAL_IRX=1` and module owns the SID, HLE hit should throw or hard-fail for bisect — not soft-success.

---

## 4. GameQuirk modules (DEBT inventory only — do not edit here)

Registered in `GameQuirkRegistry` (demolish after IRX path works — Block G):

| Serial | Module | Title | Debt class |
|--------|--------|-------|------------|
| `SLUS_210.87` | `MidwayBootAssist` | MK: Shaolin Monks | Boot/thrash plants, logo residue (WP-46) |
| `SLUS_204.23` | `MidwayFamilyAssist` | MK: Deadly Alliance | Version policy / soft-success window |
| `SLUS_208.81` | `MidwayFamilyAssist` | MK: Deception | same |
| `SLUS_215.50` | `MidwayFamilyAssist` | MK: Armageddon | same |
| `SLUS_215.43` | `MidwayFamilyAssist` | MK: Armageddon PE | same |
| `SLUS_200.24` | `BloodOmen2SnAssist` | Blood Omen 2 | SN / IOPRP / sector-credit (WP-33, WP-48) |
| `SCUS_973.99` | `GodOfWarAssist` | God of War | IOPRP version RAM plant class (WP-34, WP-48) |
| `SLUS_210.50` | `Burnout3Assist` | Burnout 3 | flip / LGDEV stubs (WP-47) |
| `SLUS_203.83` | `VexxAssist` | Vexx | title plants |
| `SLUS_206.84` | `WhiplashAssist` | Whiplash | MOD_LOAD paths / IOPFILE |
| `SCUS_974.72` | `TeamIcoAssist` | Shadow of the Colossus | IOPRP GetVersion policy |
| `SCUS_971.13` | `TeamIcoAssist` | Ico | same |
| `SLUS_205.17` | `TeamIcoAssist` | Haven: Call of the King | IOPRP250 / Exit before MOD_LOAD |

**Freeze:** no new GameQuirk thrash plants while epic #12 is active (`docs/IRX_EXECUTION_PHASE_PLAN.md`).

---

## 5. BIOS IOPBTCONF chain (execution targets, not all RPC SIDs)

Ordered boot modules (from plan / `docs/bios-ports/`) — HLE substitutes today; literal exec is Block B–C:

| Module | Role | RPC surface |
|--------|------|-------------|
| SYSMEM | heap | iopheap `0x80000003` |
| LOADCORE / MODLOAD | module table | via LOADFILE |
| INTRMAN / VBLANK / TIMEMAN | events | no EE RPC |
| SIFMAN / SIFCMD / SIFINIT / EESYNC | EE↔IOP | SIFCMD cids |
| IOMAN / FILEIO | fs | FILEIO `0x80000001` |
| LOADFILE | EE module load | `0x80000006` |
| CDVDMAN / CDVDFSV | disc | `0x8000059x` |
| MCMAN / MCSERV | memory card | `0x80000400` |
| PADMAN / SIO2MAN | pad | `0x8000010x` |
| LIBSD / … | audio | title-dependent |

---

## 6. Work package crosswalk

| Gate / WP | Matrix rows |
|-----------|-------------|
| G3 / WP-27 | FILEIO `0x80000001` |
| G4 / WP-28 | PADMAN `0x80000100` / `0x0f` |
| WP-29 | MCSERV |
| WP-30 | demote FILEIO soft-success when IRX owns SID |
| WP-33–34 | BO2 sector-credit / version plants (GameQuirk DEBT) |
| WP-40 / 46–48 | strip title GameQuirks after playable |
| WP-49 | RealSifRpc fail-fast on HLE hit under LITERAL_IRX |

---

## 7. Hygiene (WP-01)

| Item | Location |
|------|----------|
| Trace cleaner | `tools/clean-traces.ps1` |
| Trace output | `out/traces/` (gitignored) |
| This matrix | `docs/irx/HLE_TO_IRX_MATRIX.md` |
| Plan | `docs/IRX_EXECUTION_PHASE_PLAN.md` |

```powershell
# From repo root (detps2/)
pwsh ./tools/clean-traces.ps1 -DryRun
pwsh ./tools/clean-traces.ps1              # move root noise → out/traces/archive-YYYYMMDD/
pwsh ./tools/clean-traces.ps1 -ReportSize  # worktree size snapshot
```

**Update rule:** when adding a new SID constant or soft-success in `RealSifRpc.cs`, add a row here marked **DEBT** in the same PR (or T10 hygiene follow-up).
