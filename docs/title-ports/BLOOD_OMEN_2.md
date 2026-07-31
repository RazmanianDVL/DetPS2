# Blood Omen 2 (USA) — title port report

| Field | Value |
|-------|--------|
| **Id** | `blood-omen-2` |
| **Serial** | `SLUS_200.24` |
| **ISO** | `C:/Users/xxraz/Downloads/Blood Omen 2 - The Legacy of Kain Series (USA).iso` |
| **BIOS** | SCPH70008 / native BIOS HLE |
| **ROMDIR gate** | **CLOSED** |
| **Parent** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-bo2-w6` |
| **Branch** | `agent/menu-bo2-w6` |
| **Date** | 2026-07-31 |
| **Status** | **WAVE-6:** Soft-GS logo-chrome gate restore (`IsPreMainmenuSurface`); stream+Creating+LIST+ENGLISH under main Soft-GS; list-walk soft-stub; **gifP2=111**; **px=71680 logo**; **MENU? No.** |

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF | **OK** — `SLUS_200.24` |
| SN + IOPRP234 + MOD_LOAD | **OK** |
| FILEIO SN path (not FILEIO-2200) | **OK** |
| GOE_FSRV IOPFILE sids | **OK** — bind 0x20/0x21/0x29/0x30, unknownBindSids=0 |
| PS2.RKV mount + TOC | **OK** — **5592** keys |
| PRECODE/CODE/MAINMENU .BG2 | **Real disc** (warm no sector; **game Open+stream** WAVE-4/6) |
| Thrash @0x538738 | **CLEARED** — method-walker @0x166390 stubbed |
| FILEIO `KAIN.IMP` | **YES** — pack-member extract |
| Pack index | **201** members |
| Game Open+stream CODE.BG2 | **YES** — 914084 B → EE `@0xB00000` |
| Game Open+stream MAINMENU.BG2 | **YES** — 1511408 B → EE `@0xC00000` (streamedTotal=2425492) |
| Creating main layer | **YES** — entry `@0x1B5AC0`, `$ra`=post-Finished `@0x1B57B8` |
| FILEIO `LIST.TXT` | **YES** — full read **67957** |
| FILEIO `ENGLISH.DIR` | **YES** — full read **254918** |
| Circular list-walk `@0x2C3E30` | **SOFT-STUB** WAVE-6 (jr ra) — unlocks VIF1 Path2 |
| Honest cdvd @100M | **2357** (stream + LIST/ENGLISH; claim1 variant **3736** w/ GAMEKEEPER.ETP) |
| PC @ 100M | **`0x001019B8`** (low-ELF after list-walk leave) |
| px / gifP2 / gifP3 / dmac | **71680 / 111 / 2 / 318** |
| Main menu (`mainmenu-bg2`) | **Not claimed** — Soft-GS still logo-class (prims=1, imgBytes=0) |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF) — 2026-07-31 agent/menu-bo2-w6 claim4

```
PC=0x001019B8  px=71680 prims=1 gifPath1=0 gifPath2=111 gifPath3=2 dmac=318
syscalls=880 cdvdSectors=2357 sifBytes=23472
softgs: imgBytes=0 dispfbPx=0 fragTest=71680
softgs-regs: FRAME_1=0x100000 DISPFB1=0 SCISSOR full XYOFFSET=0x80008000 TEST=0x30002
RealSifRpc: binds=15 calls=78 unknownBindSids=0
[BO2] force-game BG2 stream CODE+MAINMENU streamedTotal=2425492
[BO2] kick Creating main layer entry=0x1B5AC0 ra=0x1B57B8
[FILEIO] LIST.TXT total=67957; ENGLISH.DIR total=254918
[BO2] soft-stub list-walk leaf @ 0x2C3E30
fio2200=False
```

### Wall analysis (wave-6)

1. **Root cause on main tip:** Soft-GS Mul80/AFAIL paints logo-class **px≈71680** (prims=1) early.
   WAVE-3..5 thrash escapes gated at **px&lt;50k** never fired → stuck forever in bit-pack
   `@0x479E30` with **no CODE/MAINMENU stream** (50M baseline: cdvd=648).
2. **WAVE-6 gate:** `IsPreMainmenuSurface` — logo Soft-GS / sparse prims still pre-menu until
   stream+rich Soft-GS. Restores InMap leave, bit-pack leave, usebigfile, force-stream.
3. **usebigfile window:** ~2.5M cycles after force before force-stream plants raw bytes.
4. **Creating `$ra`:** post-Finished continue `@0x1B57B8` (`jal 0x339DC8`) not nop pad `@0x1B5B3C`.
5. **Circular list-walk `@0x2C3E40`:** after ENGLISH, infinite `lw next; bne` heat. Soft-leave
   then permanent **jr ra** plant — Path2 climbs (7→111). Still **no Soft-GS prim growth**.
6. **Soft-GS residual:** gifP2=111 Path2 DIRECT submits; prims=1 / imgBytes=0 / DISPFB1=0 —
   stream + entity parse ≠ mainmenu-bg2 raster. Never invent pixels.
7. **Rejected:** WaitSema always-fabricate; re-kick Creating after ENGLISH (ICON storm);
   abort rem=0xFFFFFFFF as huge-memcpy (broke LIST timing).

### Assists (current)

- Goefile **member extract** (offset/size TOC; nested slice prefer)
- Soft-stub format **leaf** `@0x482F60` after pack-resident open
- Soft-stub method-walker `@0x166390`, SN printf `@0x46FAF8`, entity printf glue `@0x2AD8E0`
- `MaybeEscapeInMapNullDest` / post-entity bit-pack leave → **Starting-code** when no stream
- **WAVE-3/4** `MaybeForceUseBigfileOpen` — corrected ELF PCs
- **WAVE-4** `ForceBo2GameBg2Stream` + `MaybeDriveGameBg2Open` (delayed after usebigfile)
- **WAVE-4/5/6** `MaybeKickCreatingMainLayer` (true entry + post-Finished `$ra`)
- **WAVE-5/6** `MaybeKickPostEnglishMenuDraw` + list-walk soft-stub + PATH3 unmask pulse
- **WAVE-6** `IsPreMainmenuSurface` Soft-GS logo-chrome gate
- **WAVE-4** FILEIO EOF-rewind full-file read (shared RealSifRpc)
- **WAVE-5** LIST/ENGLISH byte counters (shared RealSifRpc)
- **No** fake CODE/MAINMENU sector credit without open; **no** MENU claim

## MENU / #8 residual

**NOT REACHED** (px=71680 logo-class; prims=1 ≪ menu; imgBytes=0). WAVE-6 restored post-ENGLISH
path under main Soft-GS and unlocked Path2 (gifP2=111). Soft-GS still no mainmenu-bg2 surface —
need Path2/Path3 **PRIM/IMAGE** that paints past logo clear (FRAME+DISPFB or real sprite submit).
Issues **#8**, **#17** stay open.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-bo2-w6
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-bo2-w6/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: stream CODE+MAINMENU; Creating @0x1B5AC0; LIST+ENGLISH; gifP2~100+; px logo ~71k; MENU? No
```
