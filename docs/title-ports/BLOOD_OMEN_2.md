# Blood Omen 2 (USA) — title port report

| Field | Value |
|-------|--------|
| **Id** | `blood-omen-2` |
| **Serial** | `SLUS_200.24` |
| **ISO** | `C:/Users/xxraz/Downloads/Blood Omen 2 - The Legacy of Kain Series (USA).iso` |
| **BIOS** | SCPH70008 / native BIOS HLE |
| **ROMDIR gate** | **CLOSED** |
| **Parent** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2` |
| **Branch** | `agent/menu-bo2-w5` |
| **Date** | 2026-07-31 |
| **Status** | **WAVE-5:** post-ENGLISH residual — stop false thrash on low-ELF MMI; Creating entry fix; LIST/ENGLISH counters; Soft-GS residual path; **px=3**; **MENU? No.** |

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
| Creating main layer | **YES** — force **true entry** `@0x1B5AC0` (WAVE-5; w4 mid-prologue `@0x1B5AC4`) |
| FILEIO `LIST.TXT` | **YES** — full read **67957** → `@0xA4EA90` (`Bo2ListTxtBytesRead`) |
| FILEIO `ENGLISH.DIR` | **YES** — full read **254918** → `@0xA62140` (`Bo2EnglishDirBytesRead`) |
| Honest cdvd @100M | **2135** (stream + LIST/ENGLISH sector credit) |
| PC @ 100M | **`0x002CD884`** (post-ENGLISH real list-walk code; **not** w4 EI park `0x48CF50`) |
| px / gifP3 / dmac | **3 / 2 / 323** |
| tid=1 | **started=True** |
| Main menu (`mainmenu-bg2`) | **Not reached** (px still logo-class; stream≠Soft-GS draw) |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF) — 2026-07-31 agent/menu-bo2-w5

```
PC=0x002CD884  px=3 gifPath3=2 dmac=323 sifBytes=42216
syscalls=1179 cdvdSectors=2135
RealSifRpc: binds=15 calls=114 unknownBindSids=0
[BO2] pack-member open kain.imp parent=PRECODE.BG2 off=0 size=172028
[BO2] force-game BG2 stream token=CODE dest=0xB00000 n=914084 gameOpens=1
[BO2] force-game BG2 stream token=MAINMENU dest=0xC00000 n=1511408 gameOpens=2 streamedTotal=2425492
[BO2] kick Creating main layer entry=0x1B5AC0 planted=True n=1
[FILEIO] LIST.TXT total=67957; ENGLISH.DIR total=254918
fio2200=False
(no low-ELF "rescue data thrash" yank — WAVE-5)
```

### Wall analysis (wave-5)

1. **WAVE-4 residual:** after LIST+ENGLISH, EE ran real low-ELF C++ helpers (`0x100F48`
   list splice, `0x10CFD8` path parse, MMI epilogues `0x101Axx`). Assist treated
   **all PC&lt;0x120000 as data thrash** and yanked mid-helper → mid-function epilogues
   (`0x298F08`) / short-circuit `0x48A980` → **EI park `0x48CF50`** with px=3.
2. **WAVE-5 thrash gate:** data thrash = below image **or** past ELF `.text` (`≥0x4A0000`).
   Format thrash still via `inFmtFrame`. Do **not** use `IsLikelyEeCode` for thrash (rejects MMI).
3. **IsExecutingDataOrNopSled:** trust full PT_LOAD `0x100000..0x4A0000` except pure NOP sleds.
4. **Creating main layer entry:** true prologue `@0x1B5AC0` (stack alloc); w4 `@0x1B5AC4` skipped it.
5. **FILEIO counters:** `Bo2ListTxtBytesRead` / `Bo2EnglishDirBytesRead` (fd path-match; clear on close).
6. **Post-ENGLISH Soft-GS residual:** DISPFB/PATH3 arm; unstick EI/`0x48A980` only when stuck;
   leave natural low-ELF / main `.text` alone. Clear `*0x4AC108` so cold `0x48A980` can re-run body.
7. **Soft-GS:** px=3 logo-class remains. No IMAGE/DISPFB programmed (`imgBytes=0`). Stream + list
   parse progress ≠ GIF PATH3 prims from MAINMENU surface.
8. **Rejected:** invent pixels; re-kick Creating forever; cold mid-Creating complete site (jr-ra loop).

### Assists (current)

- Goefile **member extract** (offset/size TOC; nested slice prefer)
- Soft-stub format **leaf** `@0x482F60` after pack-resident open
- Soft-stub method-walker `@0x166390`, SN printf `@0x46FAF8`, entity printf glue `@0x2AD8E0`
- `MaybeEscapeInMapNullDest` / post-entity bit-pack leave
- **WAVE-3/4** `MaybeForceUseBigfileOpen` — **corrected ELF PCs**
- **WAVE-4** `ForceBo2GameBg2Stream` + `MaybeDriveGameBg2Open` (Open+stream, not Open+close)
- **WAVE-4/5** `MaybeKickCreatingMainLayer` (true entry + LIST/ENGLISH gate + residual)
- **WAVE-5** `MaybeKickPostEnglishMenuDraw` + low-ELF thrash fix
- **WAVE-4** FILEIO EOF-rewind full-file read (shared RealSifRpc)
- **WAVE-5** LIST/ENGLISH byte counters (shared RealSifRpc)
- **No** fake CODE/MAINMENU sector credit without open; **no** MENU claim

## MENU / #8 residual

**NOT REACHED** (px=3 ≪ menu; no claim). WAVE-5 unblocked post-ENGLISH low-ELF parse
(PC `0x2CD884` list-walk vs w4 EI park). Soft-GS still logo-class — need MAINMENU GIF PATH3
prims / FRAME+DISPFB setup without inventing pixels. Issues **#8**, **#17** stay open.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-bo2-w5
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-bo2-w5/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: stream CODE+MAINMENU; Creating @0x1B5AC0; LIST+ENGLISH; no low-ELF thrash yank; px=3; MENU? No
```
