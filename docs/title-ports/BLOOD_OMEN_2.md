# Blood Omen 2 (USA) — title port report

| Field | Value |
|-------|--------|
| **Id** | `blood-omen-2` |
| **Serial** | `SLUS_200.24` |
| **ISO** | `C:/Users/xxraz/Downloads/Blood Omen 2 - The Legacy of Kain Series (USA).iso` |
| **BIOS** | SCPH70008 / native BIOS HLE |
| **ROMDIR gate** | **CLOSED** |
| **Parent** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-bo2-w3` |
| **Branch** | `agent/menu-bo2-w4` |
| **Date** | 2026-07-31 |
| **Status** | **WAVE-4:** force Open+**stream** CODE/MAINMENU into EE; ELF PC fix (w3 +0x1000); Creating main layer; LIST.TXT+ENGLISH.DIR full FILEIO; **px=3**; **MENU? No.** |

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF | **OK** — `SLUS_200.24` |
| SN + IOPRP234 + MOD_LOAD | **OK** |
| FILEIO SN path (not FILEIO-2200) | **OK** |
| GOE_FSRV IOPFILE sids | **OK** — bind 0x20/0x21/0x29/0x30, unknownBindSids=0 |
| PS2.RKV mount + TOC | **OK** — **5592** keys |
| PRECODE/CODE/MAINMENU .BG2 | **Real disc** (warm no sector; **game Open+stream** WAVE-4) |
| Thrash @0x538738 | **CLEARED** — method-walker @0x166390 stubbed |
| FILEIO `KAIN.IMP` | **YES** — member extract → PRECODE.BG2 off=0x0 size=172028 |
| Pack index | **201** members |
| Game Open+stream CODE.BG2 | **YES** — 914084 B → EE `@0xB00000` (gameOpens=1) |
| Game Open+stream MAINMENU.BG2 | **YES** — 1511408 B → EE `@0xC00000` (gameOpens=2, streamedTotal=2425492) |
| Creating main layer | **YES** — force `@0x1B5AC4` (ELF ground-truth) |
| FILEIO `LIST.TXT` | **YES** — full read 67957 → `@0xA4EA90` |
| FILEIO `ENGLISH.DIR` | **YES** — full read 254918 → `@0xA62140` |
| Honest cdvd @100M | **2135** (stream + LIST/ENGLISH sector credit) |
| PC @ 100M | **`0x0048CF50`** (post-ENGLISH residual / path parse) |
| px / gifP3 / dmac | **3 / 2 / 323** |
| tid=1 | **started=True** |
| Main menu (`mainmenu-bg2`) | **Not reached** (px still logo-class; stream≠Soft-GS draw) |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF) — 2026-07-31 agent/menu-bo2-w4

```
PC=0x0048CF50  px=3 gifPath3=2 dmac=323 sifBytes=42216
syscalls=1191 cdvdSectors=2135
RealSifRpc: binds=15 calls=114 unknownBindSids=0
[BO2] pack-member open kain.imp parent=PRECODE.BG2 off=0 size=172028
[BO2] force-game BG2 stream token=CODE dest=0xB00000 n=914084 gameOpens=1
[BO2] force-game BG2 stream token=MAINMENU dest=0xC00000 n=1511408 gameOpens=2 streamedTotal=2425492
[BO2] kick Creating main layer planted=True n=1
[FILEIO] LIST.TXT read 67957; ENGLISH.DIR read 254918
fio2200=False
```

### Wall analysis (wave-4)

1. **WAVE-3 residual:** force-game Open + immediate Close never FileRead into EE; Soft-GS px=3.
2. **ELF PC bug (w3):** `StartingCodeBigFilePc=0x1B6798` / `PreCode=0x1B6708` were **off by 0x1000**.
   Ground-truth xrefs: PreCode `@0x1B5708`, Starting-code `@0x1B5798` → jal StartBigFile
   wrapper `0x346DF8` / body `0x346E48`; Creating main layer `@0x1B5AC4`.
3. **WAVE-4 stream:** `ForceBo2GameBg2Stream` Open+GOE-slot+FileRead CODE→`0xB00000`,
   MAINMENU→`0xC00000` (goefile magic planted). Honest sector credit retained.
4. **Creating main layer** kick once → natural **LIST.TXT** + **ENGLISH.DIR** full reads.
5. **FILEIO EOF-rewind (shared):** dual SEEK_END then full-size read returned 0; rewind
   when at EOF and request ≈ file size (LIST/ENGLISH). Not title-local invent.
6. **Soft-GS:** px=3 logo-class remains. Stream + entity list load ≠ GIF prim path.
   Residual post-ENGLISH path-parse thrash `@0x48CFxx` / asset-as-code storms.
7. **Rejected:** aggressive re-kick of Creating main layer (looped LIST.TXT open forever).

### Assists (current)

- Goefile **member extract** (offset/size TOC; nested slice prefer)
- Soft-stub format **leaf** `@0x482F60` after pack-resident open
- Soft-stub method-walker `@0x166390`, SN printf `@0x46FAF8`, entity printf glue `@0x2AD8E0`
- `MaybeEscapeInMapNullDest` / post-entity bit-pack leave
- **WAVE-3/4** `MaybeForceUseBigfileOpen` — **corrected ELF PCs**
- **WAVE-4** `ForceBo2GameBg2Stream` + `MaybeDriveGameBg2Open` (Open+stream, not Open+close)
- **WAVE-4** `MaybeKickCreatingMainLayer` (single kick + asset-as-code rescue)
- **WAVE-4** FILEIO EOF-rewind full-file read (shared RealSifRpc)
- **No** fake CODE/MAINMENU sector credit without open; **no** MENU claim

## MENU / #8 residual

**NOT REACHED** (px=3 ≪ menu; no claim). Game CODE/MAINMENU are Open+streamed and
LIST/ENGLISH load; Soft-GS still logo-class. Next: post-ENGLISH UI/draw path (GIF PATH3
prims from MAINMENU surface) without inventing pixels — or PINE at post-layer draw.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-bo2-w4
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-bo2-w4/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: stream CODE+MAINMENU; Creating main layer; LIST+ENGLISH full read; px=3; MENU? No
```
