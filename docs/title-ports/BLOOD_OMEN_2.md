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
| **Status** | RKV 5592; real MAINMENU.BG2; cache-flush leaf stub; exception-vector rescue; **px=3**; menu not reached |

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF | **OK** — `SLUS_200.24` |
| SN + IOPRP234 + MOD_LOAD | **OK** |
| FILEIO + ENGLISH.DIR | **OK** |
| GOE_FSRV IOPFILE sids | **OK** — unknownBindSids=0 |
| PS2.RKV mount + TOC | **OK** — **5592** keys |
| PRECODE/CODE/MAINMENU .BG2 | **Real disc** |
| PC @ 100M | **`0x004520AC`** (post exception-vector rescue) |
| cdvdSectors | **1649** |
| px / gifP3 | **3 / 2** |
| Main menu | **Not reached** |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF)

```
PC=0x004520AC  px=3 gifPath3=2 dmac=11 sifBytes=17241
syscalls=730 cdvdSectors=1649
RealSifRpc: binds=15 calls=61 unknownServiceCalls=1 unknownBindSids=0
```

### Assists

- Title SN stubs + IOPRP `"2340"` + path-combine identity  
- **Removed false menu-draw target `0x479E04`** (disasm: mid bit-pack `ori v0,v0,0xFFFF` — not UI)  
- **Cache-flush skip**: `0x48A9xx` loop with `t2≈0x692289` → snap `0x48A974`  
- Bad-PC rescue for data/`0x538xxx` / NOP sleds only (never WaitSema body)  
- Dense START/CROSS pad after cdvd≥100  
- Leave WaitSema @ `0x488898` alone (PulseWaiters only; no blind yank)

## MENU

**NOT REACHED.** Real MAINMENU.BG2 + cdvd 1649; cache-flush leaf stubbed; exception vector rescued to `0x4520AC`; **px stuck at 3**.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/menu4build
# do NOT set DETPS2_SEMA_STALL_YIELD
dotnet exec out/menu4build/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: cdvdSectors>=1643, skip cache-flush log, PC not solely 0x479E04
```
