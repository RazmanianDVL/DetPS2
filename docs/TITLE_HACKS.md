# Title-Specific Hacks

**Policy**: Prefer **global** fixes (EE/GS/VU/CDVD).
Only add a row here when a global fix would break other titles.

**As of the GameQuirks SDK** (see `src/DetPS2.Core/GameQuirks/IGameQuirkModule.cs` and
`docs/DEVELOPER_GUIDE.md` §7): new per-title hacks should be implemented as an
`IGameQuirkModule`, registered by disc serial in `GameQuirkRegistry`, and logged here.
They should NOT be hand-edited into shared core files.

| Title id | Serial | Hack | Reason | Date |
|----------|--------|------|--------|------|
| Mortal Kombat: Shaolin Monks (USA) | SLUS_210.87 | MidwayBootAssist — ADX scrub, stream CD58 soft-plant, dense pad. Wave-4: fix sm+0x28/+0x2C jalr poison (not capacity); EE bump stub + 6MiB desc arena; force 43B670 completes; 43AB88 type-1 still null. No type5. | 43AB88 object null → C1C0 never binds; gifP3=11 NEAR?; sel-idx unproven | 2026-07-31 |
| Burnout 3: Takedown (USA) | `SLUS_210.50` | `Burnout3Assist` + **`HandleLgDev`**; residual→STG; FRONTEND plant; Soft-GS **merge composite** (IMAGE over sparse AFAIL); denser pad post-chrome | STG+TXD+FRONTEND cdvd=6584 **px=25594** dispfbPx=24336 logo-frontend Soft-GS YES (wave-5) | 2026-07-31 |
| Blood Omen 2 (USA) | `SLUS_200.24` | `BloodOmen2SnAssist` — SN stubs, IOPRP `"2340"`, real BG2, **goefile member extract**, WAVE-3 **force-game CODE/MAINMENU Open** (`Bo2GameBg2Opens`); **no** fake warm sector credit | pack-member KAIN OK; gameOpens=2 cdvd=1733; px=3; mainmenu-bg2 Soft-GS not claimed | 2026-07-31 |
| God of War (USA) | `SCUS_973.99` | `GodOfWarAssist` — EE IOPRP `"3000"` plant, FreezeCache, BST, freelist bump-alloc, soft-tick/free-search, WaitSema clamp, PickSafeResume &lt;0x2C0000; **post-empty-reboot** `SetIopRpVersionAscii("3000")`+UDNL IOPRP300 handoff; flag-spin hard-return; **no** early GetVersion force / no invented main Entry | CDVD 142 IRX-only; RPC 16/443; dmac=463; still px=0 FILEIO=0 | 2026-07-30 |

Format: short description + link to issue/commit when available.
