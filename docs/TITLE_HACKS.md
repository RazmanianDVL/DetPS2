# Title-Specific Hacks

**Policy**: Prefer **global** fixes (EE/GS/VU/CDVD).
Only add a row here when a global fix would break other titles.

**As of the GameQuirks SDK** (see `src/DetPS2.Core/GameQuirks/IGameQuirkModule.cs` and
`docs/DEVELOPER_GUIDE.md` §7): new per-title hacks should be implemented as an
`IGameQuirkModule`, registered by disc serial in `GameQuirkRegistry`, and logged here.
They should NOT be hand-edited into shared core files.

| Title id | Serial | Hack | Reason | Date |
|----------|--------|------|--------|------|
| Mortal Kombat: Shaolin Monks (USA) | SLUS_210.87 | MidwayBootAssist — Wave-7: WAD body plant + type=2 arena-only + C1C0 force entered=1 + second-chrome Soft-GS PATH3 (FBB0-gated) + sel-idx; SearchFile gate; no type5/sm+0x28. | **mk-mainmenu MENU YES** gifP3=18 px=966k prims=9 | 2026-07-31 |
| Burnout 3: Takedown (USA) | `SLUS_210.50` | `Burnout3Assist` + **`HandleLgDev`**; residual→STG; FRONTEND 4MiB plant; Soft-GS **merge composite** + DA XYZ; GIF `0x2198xx` ring leave; denser pad post-chrome | STG+TXD+FRONTEND cdvd=6584 **px=24407048** dispfbPx=2273160 **logo-frontend MENU YES** (wave-6) | 2026-07-31 |
| Blood Omen 2 (USA) | `SLUS_200.24` | `BloodOmen2SnAssist` — WAVE-7 dual list-stub + ofx title FB; stream CODE/MAINMENU; FILEIO EOF-rewind; **no** fake warm sector credit | **MENU YES** title-surface Soft-GS px=286720 gifP2=106 stream=2.4MB cdvd=6512 | 2026-07-31 |
| God of War (USA) | `SCUS_973.99` | `GodOfWarAssist` — w11: Fedo disasm + `*0x2A3310` stream-ready + name-leading wad ctx + retail 0x27D7C8 restore post-type2; no force-LoadWad PC (data-as-code); Path3MaskedByVif held | cdvd=1202 gifP2=1082 **px=0 FRAME_1=0** shell decode residual | 2026-07-31 |

Format: short description + link to issue/commit when available.
