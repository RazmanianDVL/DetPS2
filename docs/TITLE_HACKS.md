# Title-Specific Hacks

**Policy**: Prefer **global** fixes (EE/GS/VU/CDVD).
Only add a row here when a global fix would break other titles.

**As of the GameQuirks SDK** (see `src/DetPS2.Core/GameQuirks/IGameQuirkModule.cs` and
`docs/DEVELOPER_GUIDE.md` §7): new per-title hacks should be implemented as an
`IGameQuirkModule`, registered by disc serial in `GameQuirkRegistry`, and logged here.
They should NOT be hand-edited into shared core files.

| Title id | Serial | Hack | Reason | Date |
|----------|--------|------|--------|------|
| Mortal Kombat: Shaolin Monks (USA) | `SLUS_210.87` | `MidwayBootAssist` — ADX multi-table scrub, list-walk break, format-stall→main (gifP3 5→12), **post-spine memset break @0x385278**, stream CD58 defaults soft-plant, dense pad. No `*0x75C0D0` plant. No CD58 force-call (gifP3 regress). **No synthetic stream slot plant** (type5 stub → EE exception @80M). | CRI ADX / WAD under HLE; empty stream slots; wave-2 force `26F918`→`26FBF0` kick *0x678458=0 | 2026-07-31 |
| Burnout 3: Takedown (USA) | `SLUS_210.50` | `Burnout3Assist` + **`HandleLgDev`**; residual→STG; post-TXD PATH3 M3P unmask; FRONTEND.TXD host plant; flip bypass delay | STG+Global+FRONTEND plant (cdvd~6584) gifP3~430; **px=0** presentation | 2026-07-30 |
| Blood Omen 2 (USA) | `SLUS_200.24` | `BloodOmen2SnAssist` — SN stubs, IOPRP `"2340"`, real BG2, **goefile member extract**, WAVE-4 **Open+stream** CODE/MAINMENU + Creating main layer (ELF PC fix); FILEIO EOF-rewind; **no** fake warm sector credit | stream 2.4MB; LIST+ENGLISH full read; cdvd=2135; px=3; mainmenu-bg2 Soft-GS not claimed | 2026-07-31 |
| God of War (USA) | `SCUS_973.99` | `GodOfWarAssist` — EE IOPRP `"3000"` plant, FreezeCache, BST, freelist bump-alloc, soft-tick/free-search, WaitSema clamp, PickSafeResume &lt;0x2C0000; **post-empty-reboot** `SetIopRpVersionAscii("3000")`+UDNL IOPRP300 handoff; flag-spin hard-return; **no** early GetVersion force / no invented main Entry | CDVD 142 IRX-only; RPC 16/443; dmac=463; still px=0 FILEIO=0 | 2026-07-30 |

Format: short description + link to issue/commit when available.
