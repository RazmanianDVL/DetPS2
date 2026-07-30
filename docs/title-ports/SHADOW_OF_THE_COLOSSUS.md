# Shadow of the Colossus (USA) — commercial title port

| Field | Value |
|-------|--------|
| **Title** | Shadow of the Colossus (USA) |
| **user-media id** | `shadow-of-the-colossus` |
| **Serial / BOOT2** | `SCUS_974.72` |
| **Media config** | `user-media-sotc.json` |
| **Build** | `out/sotc-w2` Release |
| **ROMDIR gate** | **CLOSED** (post-INTC tip `699397e`) |
| **Status** | Past FILEIO thrash; **STARTUP.XFF** + **KERNEL.XFF** open/read; KERNEL entry thrash; still `px=0` |
| **Last updated** | 2026-07-30 |

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc boot + ELF | **Yes** |
| IOPRP300 GetVersion (`PreferIopRpGetVersion`) | **Yes** — TeamIcoAssist policy-only |
| INTC poll / MOD_LOAD IRX chain | **Yes** — tip `699397e` SHARED + TeamIco |
| FILEIO-2200 Init (fno=255 resultPtrs) | **Yes** — SHARED |
| FILEIO Getstat protocol (no thrash) | **Yes** — 2200 packet + reply; truncated probes ENOENT |
| **STARTUP.XFF open+read** | **Yes** (26 B header, disc-true) |
| **KERNEL.XFF open+read** | **Yes** (415908 B → cdvd≈450) |
| sid `0x80000220` (PL2303 bind) | **Known soft-HLE** — `unknownBindSids=0` |
| GS / px | **No** (`px=0`) |
| Main menu | **No** |

### Evidence @ 100M (host-present, SEMA_STALL_YIELD OFF)

```
PC=0x001B30F8  px=0 gifPath3=1 dmac=1 sifBytes=10095 syscalls=5129184
cdvdSectors=450
RealSifRpc: binds=15 calls=30 unknownServiceCalls=0 unknownBindSids=0
threads: 6 (KERNEL loaded; entry thrash / UnknownSpecial)
```

**FILEIO sequence (trace):**
1. Init/GetVersion → resultPtr0/1 armed, IOPRP ASCII `"3000"`
2. getstat `cdrom0:\STARTUP.*` probe → ENOENT (expected short/truncated name)
3. open+read `cdrom0:\STARTUP.XFF;1` → fd=0 size=26
4. getstat `cdrom0:\KERNEL.X` → ENOENT (truncated; real HW same)
5. open+read `cdrom0:\KERNEL.XFF;1` → 415908 bytes @ EE `0x001AA7C0`
6. write overlay / second open; residual KERNEL PC thrash

### SHARED vs LOCAL

| Change | Class |
|--------|--------|
| FILEIO-2200 command layouts (Play! `CFileIoHandler2200`) | **SHARED** `RealSifRpc` |
| Delayed READ reply (one VBlank; Play SotC comment) | **SHARED** `BiosHle.OnVblank` |
| PL2303 sid `0x80000220` soft bind/call | **SHARED** |
| TeamIcoAssist `PreferIopRpGetVersion` only | **LOCAL policy** — no memory plants |
| INTC poll base repair / ISR GPR protect | **SHARED** (tip `699397e`) |

**Play! consulted:** `Source/iop/Iop_FileIoHandler2200.cpp` / `.h` — GETSTAT/OPEN/READ/WRITE COMMAND layouts, Init resultPtrs, delayed READ reply for SotC, SIFCMD `0x80000011` collapsed to `ISignalSema` + reply buffer fill.

### Constraints

- `DETPS2_SEMA_STALL_YIELD` **OFF**
- TeamIcoAssist policy-only (no `*addr` plants)
- Prefer SHARED FILEIO/bind HLE

## MENU REACHED?

**No.**

> Unstuck past INTC + MOD_LOAD; **FILEIO-2200 SHARED** opens **STARTUP.XFF** and loads **KERNEL.XFF** (cdvd 0→41→**450**). Residual: KERNEL code band thrash (`UnknownSpecial` / heavy `0x42`/`0x2F` syscalls), **px=0**.

### Next

1. Correct KERNEL entry / avoid executing ASCII data as code (`UnknownSpecial` at `0x001E6xxx`).  
2. First GS from post-KERNEL path (`px>0` / gifP3 growth).  
3. MANAGER.XFF / GAMECORE.XFF open chain after stable KERNEL.

## Reproduce

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/sotc-w2
$env:DETPS2_TRACE_BIOS='1'
$env:DETPS2_TRACE_RPC='1'
dotnet exec out/sotc-w2/DetPS2.Core.dll blocker-trace user-media-sotc.json --cycles=100000000 --host-present --find-string=XFF --find-string=MANAGER --find-string=GAMECORE
```
