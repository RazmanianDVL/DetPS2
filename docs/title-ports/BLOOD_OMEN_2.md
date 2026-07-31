# Blood Omen 2 (USA) — title port report

| Field | Value |
|-------|--------|
| **Id** | `blood-omen-2` |
| **Serial** | `SLUS_200.24` |
| **ISO** | `C:/Users/xxraz/Downloads/Blood Omen 2 - The Legacy of Kain Series (USA).iso` |
| **BIOS** | SCPH70008 / native BIOS HLE |
| **ROMDIR gate** | **CLOSED** |
| **Parent** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2` |
| **Branch** | `agent/menu-bo2` @ `cc821b9` |
| **Date** | 2026-07-31 |
| **Status** | **FILEIO KAIN.IMP pack-resolved** (`Bo2PackResidentOpens`); format leaf soft-stubbed; **InMap 0x2B9F34 wall cleared**; post-entity heat @`0x479E30`/`0x47A0xx`; **px=3**; **#17** no game CODE/MAINMENU Open; **#8** draw stall. **MENU? No.** |

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF | **OK** — `SLUS_200.24` |
| SN + IOPRP234 + MOD_LOAD | **OK** |
| FILEIO SN path (not FILEIO-2200) | **OK** |
| GOE_FSRV IOPFILE sids | **OK** — bind 0x20/0x21/0x29/0x30, unknownBindSids=0 |
| PS2.RKV mount + TOC | **OK** — **5592** keys |
| PRECODE/CODE/MAINMENU .BG2 | **Real disc** (host warm, **no** sector credit) |
| Thrash @0x538738 | **CLEARED** — method-walker @0x166390 stubbed |
| FILEIO `KAIN.IMP` | **YES** — pack-resident open → PRECODE.BG2 (size=172028, full read @0xA242A0) |
| Pack index | **201** paths |
| Honest cdvd (no fake CODE/MAINMENU note) | **548** |
| SN Dest-Database storm | **CLEARED** — soft-stub SN printf @0x46FAF8 post pack-open |
| Post-KAIN format thrash @0x4830xx | **CLEARED** — soft-stub format **leaf only** `0x482F60` (wrapper/bridge intact) |
| InMap null-dest park @0x2B9F34 | **CLEARED** — `MaybeEscapeInMapNullDest` leave → ra `0x2B9E28` slot `0x5378A8` |
| Game GOE Open CODE/MAINMENU .BG2 | **NO** (host warm only; no FILEIO game Open; no CODE.BG2 in RDRAM) |
| PC @ 100M | **`0x00441FBC`** (post-InMap; heat in `0x479E30` bit-pack + `0x47A0xx`) |
| cdvdSectors | **548** (honest; Bo2PackResidentOpens=2) |
| px / gifP3 / dmac | **3 / 2 / 185** |
| Main menu (`mainmenu-bg2`) | **Not reached** (px still logo-class) |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF) — 2026-07-31 agent/menu-bo2

```
PC=0x00441FBC  px=3 gifPath3=2 dmac=185 sifBytes=39264
syscalls=1070 cdvdSectors=548
RealSifRpc: binds=15 calls=104 unknownBindSids=0
[BO2] pack-resident open key="assets/etypes/kain/kain.imp" parent=PRECODE.BG2 n=1..2
[BO2] soft-stub SN printf @ 0x46FAF8
[BO2] soft-stub format leaf @ 0x482F60 (wrapper intact)
[BO2] soft-stub entity printf glue @ 0x2AD8E0/0x2AD910 (format intact)
[BO2] leave InMap helper 0x002B9F34 -> ra=0x002B9E28 slot=0x005378A8
fio2200=False
find-string CODE.BG2 / MAINMENU: no match in RDRAM (no game Open path plant)
```

### Wall analysis (2026-07-31)

1. **Honesty:** Fake CODE+MAINMENU sector credit removed on main (`c423c4f`). Gating uses
   `Bo2PackResidentOpens` (not inflated cdvd). Honest plateaus at **cdvd=548**.
2. **KAIN.IMP pack-resident** — YES via FILEIO; full read 172028 → `0xA242A0` (PRECODE goefile).
3. **Format thrash** — leaf-only stub after pack open. Permanent wrapper/bridge plants rejected:
   they broke `0x485318` ("Bad Destination for InMap %s") and rescue-looped mid `0x2B9F34`.
4. **InMap wall cleared** — a1==0 path + bad vtable jalr; soft-leave helper with default slot.
   Live: leave @ ~64.6M → brief data thrash → PC lands **`0x441FBC`**.
5. **Post-InMap residual** — PcProfiler heat at `0x479E30` (bit-pack) and `0x47A0xx` (near
   historical Manager State diagnose `0x47A23C`). Still **no** `"Starting code big file"` /
   CODE.BG2 FILEIO Open. Method-walker stub may leave entity vtables incomplete → null InMap
   destinations; structural fix remains goefile member extract for `.IMP`.
6. **px≈3 / menu (#8):** Soft-GS logo-class. **No mainmenu-bg2 claim.**

### Assists (current)

- Soft-stub format **leaf** @`0x482F60` after pack-resident open; wrapper/bridge intact
- Soft-stub method-walker @`0x166390`, SN printf @`0x46FAF8`, entity printf glue @`0x2AD8E0`
- `MaybeEscapeInMapNullDest` — leave a1==0 helper / skip bad jalr
- Huge-memcpy abort @`0x4803E0` when remaining count > 64K
- Cold-resume rejects mid format / InMap helper frames
- **No** fake CODE/MAINMENU sector credit

## MENU / #8 residual

**NOT REACHED** (px=3 ≪ menu; no claim). Next: real goefile member extract for `.IMP` (not
whole-PRECODE serve), or PINE ground-truth of post-entity `"Starting code big file"` /
CODE.BG2 Open (Whiplash-transferable GOE #17).

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-bo2
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-bo2/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: KAIN PACK n=2, format leaf stub, leave InMap, PC near 0x441Fxx, cdvd=548, px=3
```
