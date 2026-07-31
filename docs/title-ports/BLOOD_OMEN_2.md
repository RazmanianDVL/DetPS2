# Blood Omen 2 (USA) — title port report

| Field | Value |
|-------|--------|
| **Id** | `blood-omen-2` |
| **Serial** | `SLUS_200.24` |
| **ISO** | `C:/Users/xxraz/Downloads/Blood Omen 2 - The Legacy of Kain Series (USA).iso` |
| **BIOS** | SCPH70008 / native BIOS HLE |
| **ROMDIR gate** | **CLOSED** |
| **Parent** | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-bo2-w3` |
| **Branch** | `agent/menu-bo2-w3` @ tip main `8da8267` |
| **Date** | 2026-07-31 |
| **Status** | **WAVE-3:** goefile member extract + **game CODE/MAINMENU Open** (`Bo2GameBg2Opens=2`, countSectors=true); cdvd=**1733** honest; **px=3**; **MENU? No.** |

---

## How far

| Milestone | Result |
|-----------|--------|
| Disc / ELF | **OK** — `SLUS_200.24` |
| SN + IOPRP234 + MOD_LOAD | **OK** |
| FILEIO SN path (not FILEIO-2200) | **OK** |
| GOE_FSRV IOPFILE sids | **OK** — bind 0x20/0x21/0x29/0x30, unknownBindSids=0 |
| PS2.RKV mount + TOC | **OK** — **5592** keys |
| PRECODE/CODE/MAINMENU .BG2 | **Real disc** (warm no sector; **game Open** WAVE-3) |
| Thrash @0x538738 | **CLEARED** — method-walker @0x166390 stubbed |
| FILEIO `KAIN.IMP` | **YES** — **member extract** → PRECODE.BG2 off=0x0 size=172028 |
| Pack index | **201** members (nested goefile slices preferred when present) |
| Game GOE/FILEIO Open CODE.BG2 | **YES** — force-game path countSectors=true → gameOpens=1 |
| Game Open MAINMENU.BG2 | **YES** — force-game path → gameOpens=2 |
| Honest cdvd | **1733** (=548 base + CODE≈447 + MAINMENU≈738) |
| SN Dest-Database storm | **CLEARED** — soft-stub SN printf @0x46FAF8 post pack-open |
| Post-KAIN format thrash | **CLEARED** — soft-stub format leaf `0x482F60` |
| InMap null-dest park | **CLEARED** — leave helper @~65.3M |
| PC @ 100M | **`0x00441FBC`** (post-drive residual thrash / data) |
| cdvdSectors | **1733** (honest game BG2 opens) |
| px / gifP3 / dmac | **3 / 2 / 195** |
| Main menu (`mainmenu-bg2`) | **Not reached** (px still logo-class; open≠draw) |

### blocker-trace @ 100M (host-present, SEMA_STALL_YIELD OFF) — 2026-07-31 agent/menu-bo2-w3

```
PC=0x00441FBC  px=3 gifPath3=2 dmac=195 sifBytes=39264
syscalls=1070 cdvdSectors=1733
RealSifRpc: binds=15 calls=104 unknownBindSids=0
[BO2] pack index PRECODE members+=4; CODE +=193; MAINMENU +=4 total=201
[BO2] pack-member open key="assets/etypes/kain/kain.imp" parent=PRECODE.BG2 off=0x0 size=172028 n=1..2
[BO2] leave InMap helper 0x002B9F20 -> ra=0x002B9E28 n=1 cyc=65300000
[BO2] real BG2 open path="…\CODE.BG2" countSectors=True gameOpens=1
[BO2] force-game BG2 open token="CODE" fd=2 gameOpens=1
[BO2] drive-game BG2 token=CODE cdvd=995 code=True menu=False cyc=65500000
[BO2] real BG2 open path="…\MAINMENU.BG2" countSectors=True gameOpens=2
[BO2] force-game BG2 open token="MAINMENU" fd=2 gameOpens=2
[BO2] drive-game BG2 token=MAINMENU cdvd=1733 code=True menu=True cyc=65550000
fio2200=False
```

### Wall analysis (wave-3)

1. **Member extract (wave-2):** intact — `pack-member open kain.imp off=0 size=172028`.
2. **usebigfile Open path (wave-3):** EE force into big-boot `0x1B5DD0` stalls mid-path
   (`0x1B5F18`) without FILEIO; residual is WaitSema fabric / InMap leave, never natural
   `"Starting code big file"`. **Force-game open** via `RealSifRpc.ForceBo2GameBg2Open`
   uses the same `TryOpenBo2RealBg2(countSectors:true)` path as game IOPFILE/FILEIO —
   real disc bytes + honest sector credit for CODE then MAINMENU. **Not** host-warm
   (warm remains `countSectors=false`).
3. **Soft-GS:** px=3 still logo-class. Opening packs on the host path does not stream
   goefile payloads into EE draw structures; mainmenu-bg2 Soft-GS not claimed.
4. **Thread residual:** tid=1 `started=False` after drive; syscalls plateau ~1070 (no
   WaitSema fabric storm). Next: EE stream/read of opened CODE/MAINMENU into GS path.

### Assists (current)

- Goefile **member extract** (offset/size TOC; nested slice prefer)
- Soft-stub format **leaf** @`0x482F60` after pack-resident open; wrapper/bridge intact
- Soft-stub method-walker @`0x166390`, SN printf @`0x46FAF8`, entity printf glue @`0x2AD8E0`
- `MaybeEscapeInMapNullDest` — leave a1==0 helper / skip bad jalr
- `MaybeEscapePostEntityBitPack` — soft-leave 0x479E00..0x47A280 after InMap
- **WAVE-3** `MaybeForceUseBigfileOpen` — Midway-style force into big-boot / Starting-code
- **WAVE-3** `MaybeDriveGameBg2Open` — `ForceBo2GameBg2Open("CODE"|"MAINMENU")` with sector credit
- `Bo2GameBg2Opens` counter (game opens only; warm excluded)
- Huge-memcpy abort @`0x4803E0` when remaining count > 64K
- **No** fake CODE/MAINMENU sector credit without open

## MENU / #8 residual

**NOT REACHED** (px=3 ≪ menu; no claim). Game CODE/MAINMENU Open path is now forced with
honest cdvd=1733. Next: EE-side StartBigFile/stream so MAINMENU.BG2 bytes reach Soft-GS
(prims/px), or PINE ground-truth of post-open draw path.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-bo2-w3
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'; $env:DETPS2_TRACE_RPC='1'
dotnet exec out/game-bo2-w3/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
# expect: pack-member kain.imp; leave InMap; force-game CODE+MAINMENU; cdvd=1733; gameOpens=2; px=3
```
