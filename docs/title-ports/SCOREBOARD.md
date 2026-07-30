# Commercial title scoreboard — main menu campaign

**Date**: 2026-07-30  
**Build**: `out/menu6build` Release  
**Constraints**: `DETPS2_SEMA_STALL_YIELD` **OFF** · PollSema-id · no global DMAC force-finish · no `*0x75C0D0` plant  
**Smokes**: **ALL PASSED** (`out/menu6tests`)

| Title | Serial | Menu? | PC (final) | px | gifP3 | dmac | cdvd | RPC binds/calls | Key unlock this session |
|-------|--------|-------|------------|----|-------|------|------|-----------------|-------------------------|
| **MK Shaolin Monks** | SLUS_210.87 | **NEAR** | `0x4275C0` (pad) | 32.4M | **12** | **17** | 198840 | 23 / ~195 | Dense D-pad+CROSS; **"Kombat"**+**"Start"**; PC `0x4148EC`↔`0x4275xx` |
| **Burnout 3** | SLUS_210.50 | **No** | `0x122A20` | 0 | **380** | **482** | 425 | 13 / **555** | Flip-wait bypass; GTFS SIDs; FILEIO fno=23 soft; gifP3 35→**380**; still IRX-only cdvd |
| **Blood Omen 2** | SLUS_200.24 | **No** | `0x2CD7E0` | 3 | 2 | 8 | 1649 | 14 / ~64 | WaitSema→`0x46FB88` v0 match; post-match PATH3; MAINMENU.BG2 real; px stuck 3 |
| **God of War** | SCUS_973.99 | **No** | residual | 0 | 0 | ~33 | 142 | 10 / ~59 | cache-wb + freelist stubs; stream-ready assist; still px=0 |

## Menu evidence bar

| Signal | MK | B3 | BO2 | GoW |
|--------|----|----|-----|-----|
| Stable non-assert PC | Yes (pad band) | Partial (`0x122A20`) | Yes (`0x2CD7E0` / `0x46FCxx`) | Residual thrash |
| GS UI (px / gifP3 growth) | **gifP3 12** | **gifP3 380** (logo/flip) | px=3 | none |
| Pad response | **Yes** (START/CROSS/DOWN→PC band) | armed | armed | armed |
| UI string in RDRAM | **"Kombat"** + **"Start"** | — | MAINMENU.BG2 path | — |
| Disc assets flowing | WAD 198k | IRX 425 | BG2 1649 | 142 |

## Verdict

- **MK**: **NEAR-MENU / interactive-class** — gifP3=12 / dmac=17 under dense pad-inject (START/CROSS/DOWN); CROSS/DOWN moves PC `0x4148EC`↔`0x4275xx`; **"Kombat"** @ `0x57FA64/B8` and many **"Start"** strings in RDRAM. Full accept-to-submenu / second UI chrome still soft → **MENU = NEAR (not full YES)**. Wave-8: stream cookie plant + sticky lock/VU escapes; syscalls~4.3M @150M; gifP3 still 11; selection index unproven.
- **B3**: Permanent flip-wait bypass + GTFS RPC (`0x00475453` / `0x00150276`) + FILEIO fno=23 soft-success + table-walk stub → **gifP3=380 / dmac=482**, binds unknown=0, calls=555. Still **cdvd=425** IRX-only (no game FILEIO open of Criterion assets). Exception residual after flip leave when peers mis-started (reverted to main-only).
- **BO2**: Real MAINMENU.BG2; WaitSema complete to caller `0x46FB88` (v0 match); post-match PATH3 arm at `0x46FC74` then kick to `0x2CD7E0`. Still **px=3**. Draw path after match body not producing UI frames.
- **GoW**: Freelist/list escapes + permanent cache-wb/freelist stubs + stream-ready assist; still **px=0** / list/KSEG residual; RPC calls low vs historical 386 peak.

## MENU yes/no (campaign bar)

| Title | MENU? |
|-------|-------|
| MK Shaolin Monks | **NEAR** (interactive-class; not full accept) |
| Burnout 3 | **No** |
| Blood Omen 2 | **No** |
| God of War | **No** |

> **No title has proven true interactive main-menu YES this session.**

## Next priority

1. B3 — first game-data FILEIO/NCMD after flip leave (`cdvd≫425`); stop fno=23 thrash with real XFILEIO semantics; GTFS stage open  
2. BO2 — MAINMENU draw (`px≫3`) from post-match `0x46FCxx` / `0x2CD7E0` without exception  
3. GoW — first GS from world lists (`px>0`); break `0x2A0xxx` / freelist re-entry without EXL  
4. MK — accept-to-submenu (selection index change + second UI string / state)

## Unlocks landed this session (code)

| Area | Change |
|------|--------|
| `RealSifRpc` | GTFS SIDs `0x00475453` / `0x00150276` / fourCC; FILEIO fno 17–64 soft-success |
| `Burnout3Assist` | Permanent `j 0x1F2520` flip-wait bypass; proactive table-walk stub; bad-PC rescue; denser menu kick |
| `BloodOmen2SnAssist` | WaitSema always → `0x46FB88` v0 match; nop jal after thrash; post-match PATH3; safer resume list |
| `GodOfWarAssist` | Permanent cache-wb + freelist entry stubs; KSEG/0x21FF thrash escape; stream-ready |
| `MidwayBootAssist` | Denser gifP3≥12 pad (D-pad + CROSS accept cadence) |

## Reproduce all four

```powershell
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/menu6build
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/menu6build/DetPS2.Core.dll blocker-trace burnout-only.json --cycles=100000000 --host-present
dotnet exec out/menu6build/DetPS2.Core.dll blocker-trace user-media-mk.json --cycles=100000000 --host-present --find-string=Kombat --find-string=Start
dotnet exec out/menu6build/DetPS2.Core.dll pad-inject user-media-mk.json --cycles=120000000 --host-present `
  --press=START:55000000:1500000 --press=CROSS:75000000:2000000 --press=DOWN:88000000:800000 --press=CROSS:98000000:2000000
dotnet exec out/menu6build/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json --cycles=100000000 --host-present
dotnet exec out/menu6build/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present
dotnet build Tests/DetPS2.Tests.csproj -c Release -o out/menu6tests
out/menu6tests/DetPS2.Tests.exe
```
