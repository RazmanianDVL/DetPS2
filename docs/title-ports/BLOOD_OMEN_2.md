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
| **Status** | **FILEIO `KAIN.IMP` pack-resolved** (PRECODE goefile); RKV 5592; GOE 0x29; **px=3**; game GOE Open .BG2 residual |

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF | **OK** — `SLUS_200.24` |
| SN + IOPRP234 + MOD_LOAD | **OK** |
| FILEIO SN path (not FILEIO-2200) | **OK** — SotC 2200 mis-arm fixed |
| GOE_FSRV IOPFILE sids | **OK** — bind 0x20/0x21/0x29/0x30, unknownBindSids=0 |
| PS2.RKV mount + TOC | **OK** — **5592** keys |
| PRECODE/CODE/MAINMENU .BG2 | **Real disc** (host warm, no sector credit) |
| Thrash @0x538738 | **CLEARED** — method-walker @0x166390 stubbed |
| FILEIO `KAIN.IMP` | **YES** — pack-resident open → PRECODE.BG2 goefile (result≥0, size=172028, full read) |
| Pack index | **201** paths from PRECODE/CODE/MAINMENU goefile string tables |
| Game GOE Open .BG2 | **Not yet** (only warm probe + FILEIO pack substitute) |
| PC @ 100M | **`0x00488898`** (WaitSema; post-KAIN read) |
| cdvdSectors | **548** (was 380 pre-pack; +~168 from KAIN/PRECODE credit) |
| px / gifP3 | **3 / 2** |
| Main menu | **Not reached** |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF) — 2026-07-30 #17 pack

```
PC=0x00488898  px=3 gifPath3=2 dmac=326 sifBytes=471840
syscalls=7075 cdvdSectors=548
RealSifRpc: binds=15 calls=855 unknownBindSids=0
[BO2] pack index PRECODE paths+=4; CODE paths+=193; MAINMENU paths+=4 total=201
[FILEIO] open PACK …\KAIN\KAIN.IMP;1 fd=2 size=172028
[FILEIO] open path=…\KAIN.IMP;1 mode=0x9A result=2 size=172028
[FILEIO] read fd=2 buf=0x00A242A0 size=172028 result=172028
thrash rescues=0  fio2200=False
```

### Wall analysis (GitHub #17)

1. **SotC FILEIO-2200 mis-arm (SHARED):** fixed earlier (SN wrappers / IOPRP≥3000 Init only).
2. **Thrash @0x538738:** method-walker soft-stub after RKV token — cleared.
3. **KAIN.IMP ENOENT (pack-resident):** ISO has only `ASSETS/ETYPES/KAIN/KAIN~1.REA` + `LIST.TXT`.
   Entity paths are baked into Crystal Dynamics **goefile** bigfiles (`PRECODE.BG2` /
   `CODE.BG2` / level `.BG2`). Shared HLE: index path strings in those packs; on FILEIO/GOE
   open miss, serve parent goefile bytes as `bo2pack:` memory image. Live: `KAIN.IMP` →
   PRECODE (172028 B) full EE read @ `0xA242A0`.
4. **Game GOE Open residual:** still only host warm of PRECODE/CODE/MAINMENU (no sector
   credit). No game `IOPFILE` Open op for `.BG2` after 0x29 bind — StartBigFile / usebigfile
   stream path incomplete. **#17 stays open.**
5. **px≈3 / menu (#8):** draw path after entity load still stalled. **#8 stays open.**

### Assists

- Title SN stubs + IOPRP `"2340"` + path-combine identity  
- Soft-stub method-walker @`0x166390` after GOE/RKV (cdvd≥350)  
- Cache-flush leaf stub @`0x48A8D0`  
- Dense START/CROSS pad after cdvd≥100  
- Leave WaitSema @ `0x488898` alone (CompleteRpcEnd owns RPC)  
- **SHARED** goefile pack-resident open (FILEIO + GOE) — not title thrash

## MENU / #8 residual

**NOT REACHED.** KAIN.IMP pack open + full PRECODE payload read clears the ENOENT wall, but
game GOE Open of CODE/MAINMENU `.BG2` and draw path (`px ≫ 3`) remain. Next: real GOE Open
stream for code/menu bigfiles (not host warm); then UI/px growth.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-bo2
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-bo2/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: KAIN.IMP PACK open size=172028 full read, thrash rescues=0, cdvdSectors≥500, fio2200=False
```
