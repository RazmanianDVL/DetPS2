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
| **Status** | **Past thrash @0x538738** → FILEIO `KAIN.IMP` open; RKV 5592; GOE 0x29; warm .BG2; **px=3**; game .BG2 Open + menu residual |

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
| FILEIO `KAIN.IMP` | **YES** — open request lands (`result=-2` ENOENT; not on ISO root) |
| Game GOE Open .BG2 | **Not yet** (only warm probe) |
| PC @ 100M | **`0x0036754C`** (real .text, post-KAIN) |
| cdvdSectors | **380** (RKV token only; no warm inflation) |
| px / gifP3 | **3 / 2** |
| Main menu | **Not reached** |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF) — 2026-07-30 #17

```
PC=0x0036754C  px=3 gifPath3=2 dmac=330 sifBytes=37520
syscalls=983 cdvdSectors=380
RealSifRpc: binds≈17 calls≈… unknownBindSids=0
[FILEIO] open …\KAIN\KAIN.IMP;1 mode=0x9A result=-2  ← wall cleared
thrash rescues=0
```

### Wall analysis (GitHub #17)

1. **SotC FILEIO-2200 mis-arm (SHARED):** `TryDecodeFio2200Open` matched SN residual path@+20;
   CallRpc returned 1 (Play GENERICREPLY) instead of host fd; IOPRP `"2340" ≥ 2200` also
   falsely armed. Fixed: reject SN wrappers (`w2==4` / `LooksLikeSnFioWrapper`); arm Init only
   for dual EE result pointers + IOPRP ≥ **3000**.
2. **Thrash @0x538738:** post-GOE method-table walker @`0x166390` does
   `lw v0,100(vtable); jalr v0` — live slots held goefile string pointers → execute data.
   Soft-stub leaf `jr ra; v0=0` after RKV token (cdvd≥350). **Not** cold-enter 0x48BCD0.
3. **Play! consulted:** `GameConfig.xml` SLUS_200.24 exception-handler nullify only;
   FILEIO-2200 from `Iop_FileIoHandler2200.h` OPENCOMMAND layout (resultSize ≠ 4).

### Assists

- Title SN stubs + IOPRP `"2340"` + path-combine identity  
- Soft-stub method-walker @`0x166390` after GOE/RKV (cdvd≥350)  
- Cache-flush leaf stub @`0x48A8D0`  
- Dense START/CROSS pad after cdvd≥100  
- Leave WaitSema @ `0x488898` alone (CompleteRpcEnd owns RPC)

## MENU / #8 residual

**NOT REACHED.** `KAIN.IMP` open is ENOENT (asset lives in CODE.BG2 / entity pack, not ISO
8.3). Need real entity payload + game GOE Open of PRECODE/CODE/MAINMENU.BG2 (not host warm)
before draw path / px growth.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/bo2-agent3
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/bo2-agent3/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: KAIN.IMP FILEIO open, thrash rescues=0, fio2200=False, cdvdSectors≈380
```
