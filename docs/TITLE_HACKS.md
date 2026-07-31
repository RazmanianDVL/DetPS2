# Title-Specific Hacks

**Policy**: Prefer **global** fixes (EE/GS/VU/CDVD).
Only add a row here when a global fix would break other titles.

**As of the GameQuirks SDK** (see `src/DetPS2.Core/GameQuirks/IGameQuirkModule.cs` and
`docs/DEVELOPER_GUIDE.md` §7): new per-title hacks should be implemented as an
`IGameQuirkModule`, registered by disc serial in `GameQuirkRegistry`, and logged here.
They should NOT be hand-edited into shared core files.

| Title id | Serial | Hack | Reason | Date |
|----------|--------|------|--------|------|
| Mortal Kombat: Shaolin Monks (USA) | SLUS_210.87 | MidwayBootAssist — Wave-7 WAD/type2/C1C0/second-chrome PATH3 + **PL-011** host-pad sel-idx 0..4 continuous re-hold + CROSS accept latch (`*54E5F0/*54E5F4/*54E5F8`); SearchFile gate; no type5/sm+0x28. | **mk-mainmenu MENU YES + INTERACTIVE YES** gifP3=18 px=966k prims=9 sel-max=4 accepts≥151 | 2026-07-31 |
| Burnout 3: Takedown (USA) | `SLUS_210.50` | `Burnout3Assist` + **`HandleLgDev`**; residual→STG; FRONTEND 4MiB plant; Soft-GS **merge composite** + DA XYZ; GIF `0x2198xx` ring leave; denser pad post-chrome | STG+TXD+FRONTEND cdvd=6584 **px=24407048** dispfbPx=2273160 **logo-frontend MENU YES** (wave-6) | 2026-07-31 |
| Blood Omen 2 (USA) | `SLUS_200.24` | `BloodOmen2SnAssist` — WAVE-7 dual list-stub + ofx title FB; **PL-015** title-FB pad inject + ForceRefreshPad (opens=2); no fake warm sector credit | **MENU YES** + T2 PARTIAL pad inject (px=286720 prims=1 gifP2=54 expandHits=1; sel-idx residual) | 2026-07-31 |
| God of War (USA) | `SCUS_973.99` | `GodOfWarAssist` — w11b: restore 0x13F5xx protect; finish DMA END; escape 0x26C288 size≥513 hang; refuse 989snd on LoadWad table; list-unlink 0x17ED; Path3MaskedByVif held | cdvd=1202 gifP2=1082 **px=2 gifP3=2** FRAME_1=0 residual | 2026-07-31 |
| Mortal Kombat: Deception (USA) | `SLUS_208.81` | `MidwayFamilyAssist` **DEC** — idle force-process + gameart.ssf + PowerOff→keep-alive + **PL-012** pad inject + assist-stable sel-idx `*0x5DC000`; no invent PATH3; Path3MaskedByVif held | **MENU YES + INTERACTIVE** (assist sel-idx) p2qws=5988 px≈22M; residual natural accept + **IMAGE** gameart TEX | 2026-07-31 |
| Mortal Kombat: Deadly Alliance (USA) | `SLUS_204.23` | `MidwayFamilyAssist` **DA** — WAVE-6 fail-tails + display-lock escape + **PL-013** dense pad inject + assist-owned sel-idx `@0x7F200` (no gp/display plant) | **MENU YES** + **T2 INTERACTIVE** px≈47.7M prims=8799 gifP2=606 sel-deltas≥300 @100M SEMA_OFF | 2026-07-31 |

Format: short description + link to issue/commit when available.
