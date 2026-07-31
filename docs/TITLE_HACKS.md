# Title-Specific Hacks

**Policy**: Prefer **global** fixes (EE/GS/VU/CDVD).
Only add a row here when a global fix would break other titles.

**As of the GameQuirks SDK** (see `src/DetPS2.Core/GameQuirks/IGameQuirkModule.cs` and
`docs/DEVELOPER_GUIDE.md` §7): new per-title hacks should be implemented as an
`IGameQuirkModule`, registered by disc serial in `GameQuirkRegistry`, and logged here.
They should NOT be hand-edited into shared core files.

| Title id | Serial | Hack | Reason | Date |
|----------|--------|------|--------|------|
| Mortal Kombat: Shaolin Monks (USA) | SLUS_210.87 | MidwayBootAssist — ADX scrub, format-stall→main, stream CD58 soft-plant, dense pad. Wave-3: reconstruct 26F918 load-request (dims/path/heap); prefer 43B670 when heaps dead; trampoline timeout. No type5. No PresentEeSifHandshake in StartLoadedModule. | Heaps empty under HLE; Exit@22M host tip variance; slot0 empty; MENU NEAR? | 2026-07-31 |
| Burnout 3: Takedown (USA) | `SLUS_210.50` | `Burnout3Assist` + **`HandleLgDev`**; residual→STG; post-TXD PATH3 M3P unmask; FRONTEND.TXD host plant; flip bypass delay; Soft-GS **Mul80**; Dmac END ADDR=0 **DA high-TADR only** (wave-4) | STG+Global+FRONTEND cdvd=6584 **px=3091** stable (logo-frontend Soft-GS; not MENU) | 2026-07-31 |
| Blood Omen 2 (USA) | `SLUS_200.24` | `BloodOmen2SnAssist` — SN stubs, IOPRP `"2340"`, real BG2, **goefile member extract**, WAVE-3 **force-game CODE/MAINMENU Open** (`Bo2GameBg2Opens`); **no** fake warm sector credit | pack-member KAIN OK; gameOpens=2 cdvd=1733; px=3; mainmenu-bg2 Soft-GS not claimed | 2026-07-31 |
| God of War (USA) | `SCUS_973.99` | `GodOfWarAssist` — EE IOPRP `"3000"` plant, FreezeCache, BST, freelist bump-alloc, soft-tick/free-search, WaitSema clamp, PickSafeResume &lt;0x2C0000; **post-empty-reboot** `SetIopRpVersionAscii("3000")`+UDNL IOPRP300 handoff; flag-spin hard-return; **no** early GetVersion force / no invented main Entry | CDVD 142 IRX-only; RPC 16/443; dmac=463; still px=0 FILEIO=0 | 2026-07-30 |

Format: short description + link to issue/commit when available.
