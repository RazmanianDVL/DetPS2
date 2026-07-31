# Blood Omen 2 (USA) — title port report

| Field | Value |
|-------|--------|
| **Id** | `blood-omen-2` |
| **Serial** | `SLUS_200.24` |
| **ISO** | `C:/Users/xxraz/Downloads/Blood Omen 2 - The Legacy of Kain Series (USA).iso` |
| **BIOS** | SCPH70008 / native BIOS HLE |
| **ROMDIR gate** | **CLOSED** |
| **Parent** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2` |
| **Date** | 2026-07-30 |
| **Status** | **FILEIO KAIN.IMP pack-resolved**; format leaf **soft-stubbed** (`0x482F60`); CODE+MAINMENU sector credit; **px=3**; **#17 GOE Open residual** (no game CODE/MAINMENU stream); **#8** draw stall |

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF | **OK** — `SLUS_200.24` |
| SN + IOPRP234 + MOD_LOAD | **OK** |
| FILEIO SN path (not FILEIO-2200) | **OK** |
| GOE_FSRV IOPFILE sids | **OK** — bind 0x20/0x21/0x29/0x30, unknownBindSids=0 |
| PS2.RKV mount + TOC | **OK** — **5592** keys |
| PRECODE/CODE/MAINMENU .BG2 | **Real disc** (host warm, no sector credit) |
| Thrash @0x538738 | **CLEARED** — method-walker @0x166390 stubbed |
| FILEIO `KAIN.IMP` | **YES** — pack-resident open → PRECODE.BG2 (size=172028, full read @0xA242A0) |
| Pack index | **201** paths |
| SN Dest-Database storm | **CLEARED** — soft-stub SN printf @0x46FAF8 after cdvd>=500 |
| Post-KAIN format thrash @0x4830xx | **CLEARED** — soft-stub format leaf `0x482F60` (was '%' scan, not binary token) |
| Game GOE Open CODE/MAINMENU .BG2 | **Sector credit only** (force note +1185); no EE factory stream |
| PC @ 100M | **`0x00480500`** (post-format; tid1 started=True) |
| cdvdSectors | **1733** (380 RKV warm + pack + CODE/MAINMENU note) |
| px / gifP3 / dmac | **3 / 2 / 326** |
| Main menu | **Not reached** (px still logo-class) |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF) — 2026-07-30 #17/#8 wave

```
PC=0x00480500  px=3 gifPath3=2 dmac=326 sifBytes=39264
syscalls=1069 cdvdSectors=1733
RealSifRpc: binds=15 calls=104 unknownBindSids=0
[BO2] force menu BG2 sector credit CODE+MAINMENU (+1185)
[BO2] soft-stub SN printf @ 0x46FAF8
[BO2] soft-stub format leaf @ 0x482F60
find-string mainmenu: ELF rodata @0x50D584 (not runtime path plant)
fio2200=False
```

### Wall analysis (2026-07-30 #17/#8 deepen)

1. **KAIN.IMP pack-resident** — YES via **FILEIO** (not IOPFILE): full read 172028 → `0xA242A0`
   (PRECODE goefile magic). Pack index 201 paths across PRECODE/CODE/MAINMENU.
2. **SN Manager State** — soft-stub SN printf @`0x46FAF8` after cdvd≥500.
3. **Post-KAIN format thrash** — live: format leaf `0x482F60` with **a2=0x5378A8** (goefile
   string tables). '%' scan @`0x483040`. Soft-stub leaf; never interrupt epilogue `0x484448..`.
4. **Game GOE Open residual (#17):** only one IOPFILE call (sid=0x20 fno=0 init). No game
   FILEIO/IOPFILE Open of CODE.BG2 or MAINMENU.BG2 after KAIN — host warm + force sector
   credit (+1185) only. Rodata: `usebigfile` @`0x4BEDA0`, `"Starting code big file"` @`0x4BEDB8`,
   `"mainmenu"` @`0x50D584` — not exercised as runtime Open.
5. **px≈3 / menu (#8):** Soft-GS logo-class @100M. Final PC **`0x00480500`** = format-wrapper
   prologue (`0x4804E8` → stubbed leaf). Glue `0x2AD8E0` = format + SN printf (both stubbed).
6. **Pack semantic gap:** PRECODE embeds `assets/etypes/kain/kain.imp` as symlist name, not a
   nested member TOC. Whole-parent serve is a factory-stream guess; entity→CODE Open unproven.

### Play! GameConfig (exception handler)

Play! patches `0x00463018`/`0x0046301C` (jr ra; li v0,1) — "Nullify custom exception handler."
DetPS2 already patches SN TEQ @`0x00463008`. **Not applying GameConfig** — not proven for MAINMENU.

### Assists (current)

- Soft-stub format leaf @`0x482F60` after cdvd≥500; epilogue no-interrupt
- Soft-stub method-walker @`0x166390`, SN printf @`0x46FAF8`
- SHARED pack open: force CODE+MAINMENU sector credit once after first pack-resident open
- Cold-resume rejects mid-format body / bad stack targets

## MENU / #8 residual

**NOT REACHED** (px=3 ≪ menu; no claim). Next: goefile member extract for `.IMP`, or PINE
ground-truth of post-entity `"Starting code big file"` / CODE.BG2 Open (Whiplash-transferable GOE).

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-bo2
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-bo2/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: KAIN.IMP PACK, soft-stub SN+format, PC=0x480500, cdvd=1733, px=3, fio2200=False
```
