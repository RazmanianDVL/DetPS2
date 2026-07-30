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
| **Status** | **FILEIO KAIN.IMP pack-resolved**; format leaf **soft-stubbed** (`0x482F60`); CODE+MAINMENU sector credit; **px=3**; game GOE Open / MAINMENU draw residual |

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

### Wall analysis

1. **KAIN.IMP pack-resident** — YES (PRECODE goefile bytes as factory stream).
2. **SN Manager State Dest-Database storm** — soft-stub SN printf after real asset I/O (cdvd≥500).
3. **Post-KAIN format thrash** — disasm: leaf `0x482F60` (frame 720) contains printf-style loop with `s2=0x25` ('%') + `jal 0x486EC0`. Soft-stub leaf (not `0x482E30` flags==0x0A path; not cold `0x48A980`).
4. **Game GOE Open residual (#17):** host warm + sector-credit note only. No game IOPFILE Open of CODE/MAINMENU into EE stream after bind `0x29`.
5. **px≈3 / menu (#8):** dmac 177→326 but Soft-GS still logo-class. **#8 stays open.**

### Play! GameConfig (exception handler)

Play! patches `0x00463018`/`0x0046301C` (jr ra; li v0,1) — "Nullify custom exception handler."
DetPS2 already patches SN TEQ gadget @`0x00463008` for scan success without nullifying
`SetVCommonHandler`. **Not applying GameConfig** this wave — not proven structural for MAINMENU;
prefer real handler + exception-vector rescue until version-matched need is shown.

### Assists (this wave)

- Soft-stub format leaf @`0x482F60` after cdvd≥500 (clears '%' thrash without frame corruption)
- SHARED pack open: force CODE+MAINMENU sector credit once after first pack-resident open
- Cold-resume rejects mid-format body / bad stack targets (no epilogue-with-sp=0 AV)

## MENU / #8 residual

**NOT REACHED.** Format thrash cleared and cdvd/dmac advanced, but game still does not Open/stream
MAINMENU.BG2 into the EE factory for UI draw (`px ≫ 3`). Next: real GOE Open / StartBigFile
for CODE+MAINMENU after entity path; then Soft-GS growth.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-bo2
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-bo2/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: KAIN.IMP PACK, soft-stub SN, unwind goefile, PC near 0x48A980, cdvd>=500, fio2200=False
```
