# Title-Specific Hacks

**Policy**: Prefer **global** fixes (EE/GS/VU/CDVD).
Only add a row here when a global fix would break other titles.

**As of the GameQuirks SDK** (see `src/DetPS2.Core/GameQuirks/IGameQuirkModule.cs` and
`docs/DEVELOPER_GUIDE.md` §7): new per-title hacks should be implemented as an
`IGameQuirkModule`, registered by disc serial in `GameQuirkRegistry`, and logged here.
They should NOT be hand-edited into shared core files.

| Title id | Serial | Hack | Reason | Date |
|----------|--------|------|--------|------|
| Mortal Kombat: Shaolin Monks (USA) | `SLUS_210.87` | `MidwayBootAssist.cs` (968 lines) — **migrated to `IGameQuirkModule`, correctly serial-gated as of 2026-07-25** (see DEVELOPER_GUIDE.md §7.3): FMV logo playback from cached decoded frames, forced jump into CRT0's real `main()` when fast-boot never reaches it, forced re-entry into the SIF-init routine, synthetic SIF worklist/ring planting, and several PC-range-gated "unstick" patches that force a wait loop's expected return value when our IOP-side HLE can't complete the real handshake. | Real IOP module execution (SIF RPC handshakes driven by proprietary/undocumented middleware — SNDF_Driver, CRI ADX, SDRDRV) and full BIOS execution are both out of scope for now; these hacks let the disc boot and render in the meantime. | 2026-07 |

Format: short description + link to issue/commit when available.
