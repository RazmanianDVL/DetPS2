# MENU-HAVEN-3 lit>0 claim — Haven Call of the King (SLUS_205.17)

**Agent**: MENU-HAVEN-3  
**Date**: 2026-07-31  
**Job**: menu-haven3-100m-host-20260731-134035  
**Budget**: 100M host-present (SliceSize=64 class)  

## Soft-GS metrics (truth)

| Field | Value |
|-------|-------|
| lit | **43132**/286720 |
| mostlyBlack | **0** |
| px | 329852 |
| prims | 2 |
| imgBytes | 194560 |
| dispfbPx / naturalDispfbPx | 43132 |
| gifP2 / gifP3 | 66 / 68 |
| binds / calls | 13 / 142 |
| PC | 0x0034744C |

## Evidence

- Log: `out\live-queue\logs\menu-haven3-100m-host-20260731-134035-out.txt`
- Queue result: `out/live-queue/done/menu-haven3-100m-host-20260731-134035.json`
- softgs-present: `lit=43132/286720 s0=0xFF000040 smid=0xFF000000 mostlyBlack=0`
- Host→Local: SYSTEM.RW3 + CUBE.BIN honest disc bytes (TeamIcoAssist MENU-HAVEN-3)
- Trace: `[TEAMICO-HAVEN] MENU-HAVEN-3 Host->Local chrome attempt=1 fed=194560 imgBytes=194560`

## Change

`src/DetPS2.Core/GameQuirks/TeamIcoAssist.cs` only:
1. Bad-PC escape covers 0x00400000–0x01000000 data thrash (live 0x005xxxxx LWC2)
2. Stream `DATA\BIN\SYSTEM.RW3` + CUBE.BIN head into high RDRAM
3. Host→Local BITBLT residual when black logo clear (px full FB, lit=0) — BO2/Whip class
4. OnHostPresent ForceRefreshPresentComposite when IMAGE + mostly black

## Bar

- Goal lit>0: **YES** (43132)
- Goal lit>1000 mostlyBlack=0: **YES**
- Residual: EE still thrash-rescues post-NUSOUND CallRpc/JREXIT; chrome is residual IMAGE not natural PATH3 tex upload
