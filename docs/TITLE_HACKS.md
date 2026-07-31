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
| Blood Omen 2 (USA) | `SLUS_200.24` | `BloodOmen2SnAssist` — WAVE-7 dual list-stub + ofx title FB; stream CODE/MAINMENU; FILEIO EOF-rewind; **no** fake warm sector credit | **MENU YES** title-surface Soft-GS px=286720 prims=1 imgBytes=0 ofx expand (S0 PL-005 remeasure gifP2≈54 cdvd≈6112; residual multi-prim IMAGE) | 2026-07-31 |
| God of War (USA) | `SCUS_973.99` | `GodOfWarAssist` — w11b: restore 0x13F5xx protect; finish DMA END; escape 0x26C288 size≥513 hang; refuse 989snd on LoadWad table; list-unlink 0x17ED; Path3MaskedByVif held | cdvd=1202 gifP2=1082 **px=2 gifP3=2** FRAME_1=0 residual | 2026-07-31 |
| Mortal Kombat: Deception (USA) | `SLUS_208.81` | `MidwayFamilyAssist` **DEC** — idle force-process + gameart.ssf + PowerOff→keep-alive + **PL-012** pad inject + assist-stable sel-idx `*0x5DC000`; no invent PATH3; Path3MaskedByVif held | **MENU YES + INTERACTIVE** (assist sel-idx) p2qws=5988 px≈22M; residual natural accept + **IMAGE** gameart TEX | 2026-07-31 |

Format: short description + link to issue/commit when available.
