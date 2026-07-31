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
| Blood Omen 2 (USA) | `SLUS_200.24` | `BloodOmen2SnAssist` — SN/IOPRP/goefile; WAVE-4 stream CODE/MAINMENU; WAVE-5 low-ELF thrash + Creating `@0x1B5AC0` + LIST/ENGLISH; WAVE-6 **`IsPreMainmenuSurface`** (logo Soft-GS ~71k must not gate thrash), usebigfile window, post-Finished `$ra`, circular list-walk soft-stub `@0x2C3E30`; FILEIO EOF-rewind; **no** fake warm sector credit | stream 2.4MB; LIST+ENGLISH; gifP2=111; px=71680 logo; mainmenu Soft-GS not claimed | 2026-07-31 |
| God of War (USA) | `SCUS_973.99` | `GodOfWarAssist` — EE IOPRP `"3000"` plant, FreezeCache, BST, freelist bump-alloc, soft-tick/free-search, WaitSema clamp, PickSafeResume &lt;0x2C0000; **post-empty-reboot** `SetIopRpVersionAscii("3000")`+UDNL IOPRP300 handoff; flag-spin hard-return; **no** early GetVersion force / no invented main Entry | CDVD 142 IRX-only; RPC 16/443; dmac=463; still px=0 FILEIO=0 | 2026-07-30 |

Format: short description + link to issue/commit when available.
