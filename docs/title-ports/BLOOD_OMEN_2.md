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
| **Status** | **FILEIO KAIN.IMP pack-resolved**; SN Dest-Database storm **cleared**; post-KAIN goefile thrash **unwound**; **px=3**; game GOE Open CODE/MAINMENU residual |

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
| Post-KAIN goefile token thrash @0x4830xx | **UNWOUND** — soft-stub 0x482E30 + frame unwind → PC=0x48A980 |
| Game GOE Open CODE/MAINMENU .BG2 | **Not yet** (host warm only; MAINMENU string absent from RDRAM) |
| PC @ 100M | **`0x0048A980`** (post-flush init; was WaitSema 0x488898) |
| cdvdSectors | **548** |
| px / gifP3 | **3 / 2** |
| Main menu | **Not reached** |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF) — 2026-07-30 #17/#8 wave

```
PC=0x0048A980  px=3 gifPath3=2 dmac=177 sifBytes=39264
syscalls=1171 cdvdSectors=548
RealSifRpc: binds=15 calls=104 unknownBindSids=0
[BO2] pack index … total=201
[FILEIO] open PACK …KAIN.IMP size=172028 full read
[BO2] soft-stub SN printf @ 0x46FAF8
[BO2] soft-stub goefile process @ 0x482E30
[BO2] unwind goefile frame 0x483074 -> … -> 0x48A980
find-string MAINMENU: no match
fio2200=False
```

### Wall analysis

1. **KAIN.IMP pack-resident** — YES (PRECODE goefile bytes).
2. **SN Manager State Dest-Database storm** — soft-stub SN printf after real asset I/O (cdvd>=500) so WaitSema@0x488894 no longer burns 100M.
3. **Post-KAIN goefile token scan @0x4830xx** — PRECODE parse sticks looking for token 0x25; soft-stub process leaf + frame unwind to 0x48A980.
4. **Game GOE Open residual (#17):** still only host warm of PRECODE/CODE/MAINMENU. No game IOPFILE Open op for `.BG2` after 0x29. StartBigFile path incomplete after entity soft-fail.
5. **px≈3 / menu (#8):** draw path still stalled. **#8 stays open.**

### Assists (this wave)

- Soft-stub SN printf @`0x46FAF8` after cdvd>=500 (proactive, not only thrash-rescue)
- Soft-stub goefile process @`0x482E30` + unwind 0x2D0 frame from token thrash @0x4830xx
- SHARED goefile pack-resident open (FILEIO + GOE) + bare CODE/PRECODE/MAINMENU path normalize

## MENU / #8 residual

**NOT REACHED.** KAIN pack + SN unstick + goefile thrash unwind clear the post-Manager-State park, but game GOE Open of CODE/MAINMENU `.BG2` and draw path (`px ≫ 3`) remain. Next: real GOE Open stream for code/menu bigfiles after entity load; then UI/px growth.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-bo2
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-bo2/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: KAIN.IMP PACK, soft-stub SN, unwind goefile, PC near 0x48A980, cdvd>=500, fio2200=False
```
