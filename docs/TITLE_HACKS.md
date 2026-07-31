# Title-Specific Hacks

**Policy**: Prefer **global** fixes (EE/GS/VU/CDVD).
Only add a row here when a global fix would break other titles.

**As of the GameQuirks SDK** (see `src/DetPS2.Core/GameQuirks/IGameQuirkModule.cs` and
`docs/DEVELOPER_GUIDE.md` §7): new per-title hacks should be implemented as an
`IGameQuirkModule`, registered by disc serial in `GameQuirkRegistry`, and logged here.
They should NOT be hand-edited into shared core files.

| Title id | Serial | Hack | Reason | Date |
|----------|--------|------|--------|------|
| Mortal Kombat: Shaolin Monks (USA) | SLUS_210.87 | MidwayBootAssist — ADX scrub, stream CD58, dense pad. Wave-5: SearchFile copy-back gate (SHARED RealSifRpc — tip gifP3 5→11); slot object complete after 43AB88 null; force 26FBF0; no type5. | slot0+obj live NEAR gifP3=11; 26FBF0 ESCAPE residual; sel-idx/second chrome open | 2026-07-31 |
| Burnout 3: Takedown (USA) | `SLUS_210.50` | `Burnout3Assist` + **`HandleLgDev`**; residual→STG; FRONTEND plant; Soft-GS **merge composite** (IMAGE over sparse AFAIL); denser pad post-chrome | STG+TXD+FRONTEND cdvd=6584 **px=25594** dispfbPx=24336 logo-frontend Soft-GS YES (wave-5) | 2026-07-31 |
| Blood Omen 2 (USA) | `SLUS_200.24` | `BloodOmen2SnAssist` — SN stubs, IOPRP `"2340"`, real BG2, goefile member extract, WAVE-4 Open+stream CODE/MAINMENU, WAVE-5 **low-ELF thrash fix** + Creating entry `@0x1B5AC0` + LIST/ENGLISH counters + post-ENGLISH Soft-GS residual; FILEIO EOF-rewind; **no** fake warm sector credit | stream 2.4MB; LIST+ENGLISH; PC post-ENGLISH list-walk; cdvd=2135; px=3; mainmenu Soft-GS not claimed | 2026-07-31 |
| God of War (USA) | `SCUS_973.99` | `GodOfWarAssist` — EE IOPRP `"3000"` plant, FreezeCache, BST, freelist bump-alloc, soft-tick/free-search, WaitSema clamp, PickSafeResume &lt;0x2C0000; **post-empty-reboot** `SetIopRpVersionAscii("3000")`+UDNL IOPRP300 handoff; flag-spin hard-return; **no** early GetVersion force / no invented main Entry | CDVD 142 IRX-only; RPC 16/443; dmac=463; still px=0 FILEIO=0 | 2026-07-30 |

Format: short description + link to issue/commit when available.
