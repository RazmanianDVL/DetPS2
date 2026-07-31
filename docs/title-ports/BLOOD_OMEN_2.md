# Blood Omen 2 (USA) — title port report

| Field | Value |
|-------|--------|
| **Id** | `blood-omen-2` |
| **Serial** | `SLUS_200.24` |
| **ISO** | `C:/Users/xxraz/Downloads/Blood Omen 2 - The Legacy of Kain Series (USA).iso` |
| **BIOS** | SCPH70008 / native BIOS HLE |
| **ROMDIR gate** | **CLOSED** |
| **Parent** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2` |
| **Branch** | `agent/menu-bo2-w2` @ tip main `3748553` |
| **Date** | 2026-07-31 |
| **Status** | **goefile member extract** (`pack-member open`); KAIN.IMP → PRECODE off=0 size=172028; InMap leave; **no** game CODE/MAINMENU Open; **px=3**; **MENU? No.** |

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
| FILEIO `KAIN.IMP` | **YES** — **member extract** → PRECODE.BG2 off=0x0 size=172028 |
| Pack index | **201** members (nested goefile slices preferred when present) |
| Honest cdvd (no fake CODE/MAINMENU note) | **548** |
| SN Dest-Database storm | **CLEARED** — soft-stub SN printf @0x46FAF8 post pack-open |
| Post-KAIN format thrash @0x4830xx | **CLEARED** — soft-stub format **leaf only** `0x482F60` |
| InMap null-dest park @0x2B9F34 | **CLEARED** — leave → ra `0x2B9E8C` slot `0x5378A8` @~93.6M |
| Game GOE Open CODE/MAINMENU .BG2 | **NO** (host warm only; no FILEIO game Open) |
| PC @ 100M | **`0x004891E8`** (RPC worker / WaitSema fabric residual on tip) |
| cdvdSectors | **548** (honest; Bo2PackResidentOpens=2) |
| px / gifP3 / dmac | **3 / 2 / 380** |
| Main menu (`mainmenu-bg2`) | **Not reached** (px still logo-class) |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF) — 2026-07-31 agent/menu-bo2-w2

```
PC=0x004891E8  px=3 gifPath3=2 dmac=380 sifBytes=39264
syscalls=3752500 cdvdSectors=548
RealSifRpc: binds=15 calls=104 unknownBindSids=0
[BO2] pack index PRECODE members+=4; CODE +=193; MAINMENU +=4 total=201
[BO2] pack-member open key="assets/etypes/kain/kain.imp" parent=PRECODE.BG2 off=0x0 size=172028 n=1..2
[BO2] soft-stub SN printf @ 0x46FAF8
[BO2] soft-stub format leaf @ 0x482F60 (wrapper intact)
[BO2] soft-stub entity printf glue @ 0x2AD8E0/0x2AD910
[BO2] leave InMap helper 0x002B9F5C -> ra=0x002B9E8C slot=0x005378A8 n=1 cyc=93600000
fio2200=False
no game Open of CODE.BG2 / MAINMENU.BG2 (host warm only; no sector credit)
```

### Wall analysis (wave-2)

1. **Member extract:** `TryOpenBo2PackResident` now indexes goefile members with
   `(parent, offset, size)`. Nested `goefile` regions claim tight slices; root-only
   symbols (kain.imp in PRECODE) map to parent off=0 (package *is* the member). Live:
   `pack-member open … off=0x0 size=172028` — same bytes as whole PRECODE, but
   infrastructure serves slices when nested packages exist (CODE has nested goefiles).
2. **No fake sectors:** CODE/MAINMENU host warm remains `countSectors=false`. Honest
   plateau **cdvd=548**.
3. **InMap** still leaves; residual is post-entity / tip WaitSema fabric (`WHIP_SEMA_FIX_V2`
   sema=3 storm @0x488894, PC ends 0x4891E8) — not a CODE Open.
4. **#17/#8:** Still no `"Starting code big file"` / game FILEIO of CODE.BG2 or
   MAINMENU.BG2. Soft-GS logo-class px=3. **No mainmenu-bg2 claim.**

### Assists (current)

- Goefile **member extract** (offset/size TOC; nested slice prefer)
- Soft-stub format **leaf** @`0x482F60` after pack-resident open; wrapper/bridge intact
- Soft-stub method-walker @`0x166390`, SN printf @`0x46FAF8`, entity printf glue @`0x2AD8E0`
- `MaybeEscapeInMapNullDest` — leave a1==0 helper / skip bad jalr
- `MaybeEscapePostEntityBitPack` — soft-leave 0x479E00..0x47A280 after InMap (rate-limited)
- Huge-memcpy abort @`0x4803E0` when remaining count > 64K
- **No** fake CODE/MAINMENU sector credit

## MENU / #8 residual

**NOT REACHED** (px=3 ≪ menu; no claim). Next: PINE ground-truth of post-entity
`"Starting code big file"` / CODE.BG2 Open path (usebigfile StartBigFile), or deeper
entity registration without method-walker full-stub so InMap destinations are real.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-bo2-w2
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-bo2-w2/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: pack-member open kain.imp off=0, format leaf stub, leave InMap, cdvd=548, px=3
```
