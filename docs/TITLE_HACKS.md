# Title-Specific Hacks

**Policy**: Prefer **global** fixes (EE/GS/VU/CDVD).
Only add a row here when a global fix would break other titles.

**As of the GameQuirks SDK** (see `src/DetPS2.Core/GameQuirks/IGameQuirkModule.cs` and
`docs/DEVELOPER_GUIDE.md` §7): new per-title hacks should be implemented as an
`IGameQuirkModule`, registered by disc serial in `GameQuirkRegistry`, and logged here.
They should NOT be hand-edited into shared core files.

| Title id | Serial | Hack | Reason | Date |
|----------|--------|------|--------|------|
| Mortal Kombat: Shaolin Monks (USA) | `SLUS_210.87` | `MidwayBootAssist` — ADX multi-table scrub, list-walk break, format-stall→main (gifP3 5→12), **post-spine memset break @0x385278**, dense pad. No `*0x75C0D0` plant. | CRI ADX / WAD under HLE; format/list/memset parks | 2026-07-30 |
| Burnout 3: Takedown (USA) | `SLUS_210.50` | `Burnout3Assist` + **`HandleLgDev`**; **lgDeviceInit entry stub @`0x4438E0`** + residual CallRpc complete; IOPRP `"2800"`; flip re-arm | lgDeviceInit thrash → left VBlank; still no game FILEIO | 2026-07-30 |
| Blood Omen 2 (USA) | `SLUS_200.24` | `BloodOmen2SnAssist` — SN stubs, IOPRP `"2340"`, real BG2, **format leaf stub @0x482F60**, cache-flush stub, exception-vector rescue, pad; SHARED pack + CODE/MAINMENU sector credit | KAIN pack + format thrash cleared; px=3 draw stall (#8) | 2026-07-30 |
| God of War (USA) | `SCUS_973.99` | `GodOfWarAssist` — IOPRP `"3000"`, FreezeCache, BST, freelist bump-alloc (RDRAM-clamped arena), **soft-tick wait** `0x17A1D0`/`*0x29C7D4`, free-search plant `*0x29BEB0`, WaitSema a0 clamp, PickSafeResume hard-cap &lt;0x2C0000; dormant-main re-start on WaitSema trampoline; **keep** soft-float `0x2847xx` residual escape (removal regressed dmac) | CDVD 142; RPC 16/443; dmac=463 sif=95k; still px=0 gifP3=0 | 2026-07-30 |

Format: short description + link to issue/commit when available.
