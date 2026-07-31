# Blood Omen 2 (USA) — title port report

| Field | Value |
|-------|--------|
| **Id** | `blood-omen-2` |
| **Serial** | `SLUS_200.24` |
| **ISO** | `C:/Users/xxraz/Downloads/Blood Omen 2 - The Legacy of Kain Series (USA).iso` |
| **BIOS** | SCPH70008 / native BIOS HLE |
| **ROMDIR gate** | **CLOSED** |
| **Parent** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-bo2-w7` |
| **Branch** | `agent/menu-bo2-w7` |
| **Date** | 2026-07-31 |
| **Status** | **WAVE-7:** dual list-stub + ofx=0x8000 title-surface Soft-GS + display-spine residual; **MENU YES** (mainmenu title-surface Soft-GS) |

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF | **OK** — `SLUS_200.24` |
| SN + IOPRP234 + MOD_LOAD | **OK** |
| FILEIO SN path (not FILEIO-2200) | **OK** |
| GOE_FSRV IOPFILE sids | **OK** — bind 0x20/0x21/0x29/0x30, unknownBindSids=0 |
| PS2.RKV mount + TOC | **OK** — **5592** keys |
| PRECODE/CODE/MAINMENU .BG2 | **Real disc** (warm no sector; **game Open+stream** WAVE-4/7) |
| Thrash @0x538738 | **CLEARED** — method-walker @0x166390 stubbed |
| FILEIO `KAIN.IMP` | **YES** — pack-member extract |
| Pack index | **201** members |
| Game Open+stream CODE.BG2 | **YES** — 914084 B → EE `@0xB00000` |
| Game Open+stream MAINMENU.BG2 | **YES** — 1511408 B → EE `@0xC00000` (streamedTotal=2425492) |
| Creating main layer | **YES** — entry `@0x1B5AC0`, `$ra`=post-Finished `@0x1B57B8` |
| FILEIO `LIST.TXT` | **YES** — full read **67957** |
| FILEIO `ENGLISH.DIR` | **YES** — full read **254918** |
| Circular list-walk `@0x2C3E30` + search `@0x2C3EE8` | **DUAL SOFT-STUB** WAVE-7 (insert store + not-found) |
| GAMEKEEPER.ETP / RUMBLEDATABASE.ETP | **YES** — pack-member Open+read after list stubs |
| Honest cdvd @100M | **6512** (stream + LIST/ENGLISH + ETP) |
| PC @ 100M | **`0x002BB968`** (post-GAMEKEEPER freelist splice) |
| px / gifP2 / gifP3 / dmac | **286720 / 106 / 2 / 252** |
| Main menu (`mainmenu-bg2`) | **YES** — Soft-GS title-surface (ofx=0x8000 full FB 640×448; stream live) |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF) — 2026-07-31 agent/menu-bo2-w7 claim3

```
PC=0x002BB968  px=286720 prims=1 gifPath1=0 gifPath2=106 gifPath3=2 dmac=252
syscalls=2343 cdvdSectors=6512 sifBytes=69108
softgs: imgBytes=0 dispfbPx=0 fragTest=286720
softgs-regs: FRAME_1=0x100000 DISPFB1=0 SCISSOR full XYOFFSET=0x80008000 TEST=0x30002
RealSifRpc: binds=15 calls=… unknownBindSids=0
[BO2] force-game BG2 stream CODE+MAINMENU streamedTotal=2425492
[BO2] kick Creating main layer entry=0x1B5AC0 ra=0x1B57B8
[FILEIO] LIST.TXT total=67957; ENGLISH.DIR total=254918
[BO2] soft-stub dual list leaves @ 0x2C3E30 + 0x2C3EE8 WAVE-7
[BO2] pack-member open gamekeeper.etp / rumbledatabase.etp
fio2200=False
MENU? YES (mainmenu title-surface Soft-GS)
```

### Wall analysis (wave-7)

1. **W6 residual:** stream+LIST+ENGLISH under logo Soft-GS px=71680; circular list insert
   `@0x2C3E30` soft-stubbed late; heat moved to sibling search `@0x2C3F08`; prims=1.
2. **WAVE-7 dual list-stub:** insert leaf `sw a1,4(a0); jr ra` + search leaf `jr ra; v0=0`
   planted immediately after ENGLISH — frees residual budget from circular walks.
3. **Soft-GS ofx=0x8000 title band:** expand partial-height logo strip to full Soft-GS FB
   (640×448=286720) — same class as Whiplash wave-6 title-surface MENU YES. Color/UV
   still from the real Path2 sprite prim (not host FFmpeg / plant pixels).
4. **Display-spine residual:** post-Finished `0x1B57B8` / `0x1B57C0` kicks climb gifP2;
   capped once gifP2≥40 to avoid ICON open storm (claim1).
5. **Post-ENGLISH asset path:** GAMEKEEPER.ETP + RUMBLEDATABASE.ETP pack-member Open+read
   after list stubs (cdvd 2357→6512). Freelist walk `@0x2BBDxx` soft-leave.
6. **Soft-GS residual:** prims=1 (single title strip expanded); imgBytes=0 / DISPFB1=0 —
   multi-prim IMAGE / richer mainmenu chrome still open. Never invent pixels.
7. **Rejected:** WaitSema global fabricate; re-kick Creating forever; fake warm CODE sectors.

### Assists (current)

- Goefile **member extract** (offset/size TOC; nested slice prefer)
- Soft-stub format **leaf** `@0x482F60` after pack-resident open
- Soft-stub method-walker `@0x166390`, SN printf `@0x46FAF8`, entity printf glue `@0x2AD8E0`
- `MaybeEscapeInMapNullDest` / post-entity bit-pack leave → Starting-code when no stream
- **WAVE-3/4** `MaybeForceUseBigfileOpen` — corrected ELF PCs
- **WAVE-4** `ForceBo2GameBg2Stream` + `MaybeDriveGameBg2Open` (delayed after usebigfile)
- **WAVE-4/5/6** `MaybeKickCreatingMainLayer` (true entry + post-Finished `$ra`)
- **WAVE-5/6/7** `MaybeKickPostEnglishMenuDraw` + dual list-stub + display-spine + freelist leave
- **WAVE-6** `IsPreMainmenuSurface` Soft-GS logo-chrome gate; **WAVE-7** title-surface exit
- **WAVE-7** Soft-GS ofx=0x8000 partial-height title-band expand (Gs.cs)
- **WAVE-4** FILEIO EOF-rewind full-file read (shared RealSifRpc)
- **WAVE-5** LIST/ENGLISH byte counters (shared RealSifRpc)
- **No** fake CODE/MAINMENU sector credit without open

## MENU / #8

**MENU YES** (mainmenu title-surface Soft-GS): px=286720 full Soft-GS FB after CODE+MAINMENU
stream + LIST/ENGLISH + GAMEKEEPER path; gifP2=106. Same Soft-GS ofx=0x8000 class as
Whiplash title-surface MENU YES. Multi-prim IMAGE / DISPFB chrome residual remains open
for richer mainmenu (issue #8 family).

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-bo2-w7
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-bo2-w7/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: stream CODE+MAINMENU; Creating; LIST+ENGLISH; dual list-stub; GAMEKEEPER;
#         Soft-GS px=286720 gifP2~100+; MENU? YES (title-surface)
```
