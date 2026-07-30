# Play! HLE oracle (mandatory for all titles)

**Policy (user mandate, 2026-07-30):** For **every current and future title**, when DetPS2 hits a wall or behavior is unclear, agents **must** consult the [Play!](https://github.com/jpd002/Play-) HLE source (and PCSX2+PINE for live LLE). **Do not guess** boot flow, RPC shapes, FILEIO layouts, or menu type.

Play! is another **HLE** PS2 emulator (C++). We do **not** port the whole engine. We **re-host contracts** into DetPS2 C# (`RealSifRpc`, kernel, CDVD, quirks) the same way we already re-host BIOS ABI.

## Location (this machine)

| Path | Contents |
|------|----------|
| `C:\Windows\Play\` | Full purei Play! tree |
| `C:\Windows\Play\Source\iop\` | IOP HLE modules (`Iop_FileIo*.cpp`, `Iop_SifCmd.cpp`, `Iop_PadMan.cpp`, …) |
| `C:\Windows\Play\Source\ee\` | EE side |
| `C:\Windows\Play\GameConfig.xml` | **Per-title** patches / idle loops (sparse) |

If missing, clone: `https://github.com/jpd002/Play-` (prefer `E:\dev` if C: is tight).

## Oracle stack (all titles)

| Order | Tool | Use when |
|-------|------|----------|
| 1 | **DetPS2** traces (`blocker-trace`, `DETPS2_TRACE_*`) | Live wall PC / RPC / cdvd |
| 2 | **Play! source** | How another HLE implements that service or title |
| 3 | **PCSX2 + PINE** | Live LLE ground truth (memory/PC/flags) |
| 4 | **Elgato / soft-GS PPM** | Visual confirmation after assets actually draw |

## Per-title workflow (required)

For **each** title under test (fleet or new ISO):

1. **Identify serial** from `SYSTEM.CNF` / media JSON (e.g. `SLUS_200.24`).
2. **Scan Play! GameConfig** for that executable/title:
   ```powershell
   Select-String -Path "C:\Windows\Play\GameConfig.xml" -Pattern "SLUS_200.24|Blood Omen" -Context 0,8
   ```
3. If a **GameConfig** entry exists: treat patches as **TITLE_LOCAL candidates** only after confirming they are structural (not a substitute for missing HLE). Map addresses carefully (version skew).
4. If **no** GameConfig entry: still use **generic** Play IOP modules for the wall (FILEIO, SIF, PAD, CDVD, MC, threads).
5. Map wall → Play module (table below) → read C++ handler → port **ABI + side-effects** into DetPS2 SHARED HLE when transferable.
6. Prefer **SHARED** (`RealSifRpc` / `SonyKernelHle` / …) over `GameQuirks/*` unless Play itself only patches that title.
7. After merge: wiki Active/Fixed + issues (close only when the wall is truly done).

## Wall → Play! module map

| DetPS2 wall class | Play! source (start here) |
|-------------------|---------------------------|
| FILEIO open/read/seek/version | `Source/iop/Iop_FileIo.cpp`, `Iop_FileIoHandler*.cpp` |
| SIF bind/call/RPC end / WaitSema | `Iop_SifCmd.cpp`, `Iop_SifMan*.cpp`, `Iop_Thsema.cpp` |
| PADMAN open / GetModVer / DMA surface | `Iop_PadMan.cpp` |
| CDVD DualInfo / DiskReady / NCMD | `Iop_Cdvdfsv.cpp`, `Iop_Cdvdman.cpp` |
| MCSERV / XMCSERV | `Iop_McServ.cpp` |
| LoadModule / LOADCORE | `Iop_Loadcore.cpp`, `Iop_Modload.cpp` |
| IOMAN paths / host0 | `Iop_Ioman.cpp`, `Iop_PathUtils.cpp`, `ioman/*` |
| Threads / semas / mbx / vpl | `Iop_Thbase.cpp`, `Iop_Thsema.cpp`, `Iop_Thmsgbx.cpp`, `Iop_Thvpool.cpp` |
| VBlank IOP | `Iop_Vblank.cpp` |
| Title-only patches | `GameConfig.xml` |

## Fleet GameConfig snapshot (this machine, 2026-07-30)

| Title | Serial | Play! GameConfig? |
|-------|--------|-------------------|
| Blood Omen 2 | SLUS_200.24 | **YES** — nullify custom exception handler @ `0x00463018` / `0x0046301C` |
| Burnout (original) | SLUS_203.07 | YES (not B3 SLUS_210.50) |
| Burnout 3 | SLUS_210.50 | No entry — use generic IOP HLE only |
| God of War | SCUS_973.99 | No entry |
| MK Shaolin Monks | SLUS_210.87 | No entry |
| MK Deadly Alliance | SLUS_204.23 | No entry |
| MK Deception | SLUS_208.81 | No entry |
| Vexx | SLUS_203.83 | No entry |
| Whiplash | SLUS_206.84 | No entry |
| Haven | — | No entry |

**Re-scan GameConfig** whenever a new serial is added; Play! updates the XML over time.

### Blood Omen 2 (example — must re-check addresses on our ISO)

```xml
<GameConfig Executable="SLUS_200.24;1" Title="Blood Omen 2">
  <Patch Address="0x00463018" Value="0x03E00008" Description="Nullify custom exception handler." />
  <Patch Address="0x0046301C" Value="0x24020001" Description="Nullify custom exception handler." />
</GameConfig>
```

Before planting: confirm EE image still has that handler (disasm / find-writer). Prefer fixing HLE so the handler is not needed; use patch only if structural and version-matched.

## Conversion rules (C++ → C#)

| Do | Don't |
|----|--------|
| Port return tokens, packet layouts, who SignalSema’s whom | Copy whole classes / VU / GS |
| Match SIF RPC SID + fno tables | Blind-paste addresses from GameConfig without verify |
| Lift patterns that help 2+ titles into SHARED HLE | Fork a parallel “Play#” emulator |
| Document Play! file + function name in commit/issue | Assume Play! and DetPS2 memory maps are identical |

## Agent report requirement

Every title report that claims a wall must include one of:

- **Play! consulted:** `path` + what was learned, or  
- **Play! N/A:** service not present / no relevant module, with reason  

If the wall is **behavioral** (menu type, first boot flow) and Play! is silent, **PCSX2+PINE is mandatory**.

## Related

- [Tools wiki](https://github.com/RazmanianDVL/DetPS2/wiki/Tools) — PCSX2 PINE + Play!  
- `docs/DEVELOPER_GUIDE.md` — DetPS2 CLI  
- `docs/BIOS_DISSECTION.md` — BIOS ABI  
- Game quirks: `src/DetPS2.Core/GameQuirks/`  
