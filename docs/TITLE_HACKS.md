# Title-Specific Hacks

**Policy**: Prefer **global** fixes (EE/GS/VU/CDVD).
Only add a row here when a global fix would break other titles.

**As of the GameQuirks SDK** (see `src/DetPS2.Core/GameQuirks/IGameQuirkModule.cs` and
`docs/DEVELOPER_GUIDE.md` §7): new per-title hacks should be implemented as an
`IGameQuirkModule`, registered by disc serial in `GameQuirkRegistry`, and logged here.
They should NOT be hand-edited into shared core files.

## Real SIF RPC dispatch (2026-08-02) — the thing this whole file exists to make unnecessary

Every PS2 title, however different its own engine, registers its IOP-side services through the
exact same standard BIOS mechanism: `sceSifSetRpcQueue` + `sceSifRegisterRpc`, building a real
linked list of `SifRpcServerData_t` entries in IOP memory that the real SIFCMD dispatcher walks
by `sid`. This was ground-truthed by extracting the real `SIFCMD.IRX` straight from the BIOS ROM
(`romdir-extract`) and decompiling it in Ghidra (project `SIFCMD`):

- `sceSifSetRpcQueue` == `FUN_00001088` — appends a queue to a global chain (module-relative
  `.data` offset `0x2a60` holds the chain head; each queue's `+0x14` is "next queue").
- `sceSifRegisterRpc` == `FUN_00001130` — appends a `SifRpcServerData_t` to a queue's server list
  (`+0x00` sid, `+0x04` func, `+0x08` buff, `+0x38` "next server", queue's `+0x08` is the list
  head). Verified the offset convention is correct: SIFCMD's real live entry point
  (`0x1C1580D0`) minus its real live load base (`0x1C158000`) is exactly `0xD0`, matching the
  Ghidra-analyzed `module_start` function address.

`RealSifRpc.HandleCall` now walks this REAL, live registry first (`TryFindRealRpcServer`) before
falling through to any hardcoded per-sid HLE branch below. If a genuinely loaded, genuinely
running module has actually registered a handler for the sid being called, its real handler runs
on the IOP R3000 core with the real request bytes (`TryDispatchRealRegisteredRpc` — full IOP
context save/restore around the call, since it runs mid-quantum from EE-side call handling), and
its real reply is used — no guessing. Bounds-checked (a matched handler address must land inside
some genuinely loaded module's real image, guarding against a partially-initialized entry while a
module's own `_start` is still mid-registration) and fully backward-compatible: whenever nothing
is really registered yet (module hasn't reached that call, or the service is one of the small set
BIOS-stack modules intentionally never run for real, e.g. LOADFILE/CDVDFSV), it falls straight
through to the existing HLE below, unchanged. Verified safe across the whole roster (all 9 titles
in `user-media.json` boot cleanly, no regressions) and opt-out via `DETPS2_NO_REAL_RPC=1` for
bisection. Trace via `DETPS2_TRACE_REALRPC=1`.

**Current status — not yet firing in practice**: for Whiplash, `IOPFILE.IRX`'s real `_start` now
genuinely executes (see its row below), but its queue-chain head reads `0x00000000` even after
2,000,000 real instructions — meaning its real init hasn't reached `sceSifSetRpcQueue` yet within
that budget, or is blocked on something else earlier in its own init that isn't yet correctly
emulated. That's the next real question: not a per-title hack, but IOP-execution correctness —
what real primitive is `IOPFILE.IRX` waiting on early in its own `_start` that we don't yet
service correctly. Once that's solved (for this or any other title's own IOP driver), this
mechanism activates automatically with zero further per-title work.

**2026-08-03 investigation — Ghidra-verified the C# BIOS conversion against the real BIOS ROM
(`Documents/PCSX2/bios/...SCPH70008.bin`), per direct instruction not to keep guessing at
per-title protocols when the underlying infrastructure itself might be the gap.** Findings:
- **The IOP kernel dispatch is more correct than assumed, not less.** `EXCEPMAN`/`INTRMANP`/
  `INTRMANI` all genuinely complete their real `_start` during the standard boot's IOPBTCONF walk
  (`BiosBootHost.BootIopBtConfLiteral`, which runs unconditionally — no skip-list applies there,
  that only gates *later*, game-requested re-`MOD_LOAD` calls). Verified live: IOP RAM `0x80`
  (the BEV=0 general exception vector) genuinely changes from the emulator's placeholder stub to
  real `SW`-opcode dispatcher instructions after they run — real code is installing a real
  handler chain, exactly as on hardware.
- **Real IOP syscalls do fire** (confirmed via new `[IOP-EXC]` tracing showing the real Sony
  kernel convention: syscall number in `$v0` at trap time, e.g. 1, 8, 0x10, 0x14, 0x20 observed
  live) and mostly return without crashing.
- **Root-caused the actual blocker**: `SDRDRV.IRX`'s real `_start` (the sound hardware driver,
  genuinely running per the 2026-08-02 module-loading fix) eventually calls what is almost
  certainly its own real `SifRpcFunc_t` RPC handler (`FUN_00000410` in the real disc `SDRDRV.IRX`
  — signature and body are an unmistakable `int fno, void *buf` dispatch across ~40 real SPU2
  register operations). Its epilogue is completely ordinary MIPS (`lw ra,0x38(sp); jr ra`) — the
  bug is not in this function. `$ra` reads back as **zero** from the stack at that point, meaning
  something earlier in the real call chain never wrote a real return address there. Confirmed via
  new `[IOP-BADJUMP]` tracing (any `JR`/`JALR` landing under `0x1000`) that the CPU then free-falls
  into the shared, zero-initialized `_start` stack region and infinite-loops on `jr $0` (the raw
  encoding of an all-zero word) for the rest of that module's instruction budget — which is very
  likely why `IOPFILE.IRX` (sharing the same per-module execution budget/scheduling model) never
  gets far enough to reach its own `sceSifSetRpcQueue`/`sceSifRegisterRpc` calls.
- **Working theory, not yet confirmed**: real IOP module `_start` routines commonly spawn a
  worker thread and return control cooperatively rather than running everything inline on the
  entry stack — our current "run `_start` in isolation on one fixed stack until it returns or
  hits an instruction budget" model doesn't model that, so a module whose real init legitimately
  depends on a second thread getting scheduled may never see it happen, and calling into its own
  registered handler from the wrong stack/thread context (as `SDRDRV.IRX` appears to) would
  produce exactly this symptom. Next step: trace `SDRDRV.IRX`'s real `_start` call chain (Ghidra)
  from entry down to the `FUN_00000410` call site to confirm.

New diagnostics added and kept (zero cost when unset, same convention as existing `DETPS2_TRACE_*`
flags): `DETPS2_TRACE_BTCONF_STEP=1` (per-module IOPBTCONF boot step + IOP RAM `0x80`-`0x8F`
dump), `[IOP-EXC]` now includes `v0`/`v1`/`a0-a3`/`ra`, `[IOP-BADJUMP]` (any indirect jump to an
address `< 0x1000`).

| Title id | Serial | Hack | Reason | Date |
|----------|--------|------|--------|------|
| Mortal Kombat: Shaolin Monks (USA) | SLUS_210.87 | MidwayBootAssist — Wave-7 WAD/type2/C1C0/second-chrome PATH3 + **PL-011** host-pad sel-idx 0..4 continuous re-hold + CROSS accept latch (`*54E5F0/*54E5F4/*54E5F8`); SearchFile gate; no type5/sm+0x28. | **mk-mainmenu MENU YES + INTERACTIVE YES** gifP3=18 px=966k prims=9 sel-max=4 accepts≥151 | 2026-07-31 |
| Burnout 3: Takedown (USA) | `SLUS_210.50` | `Burnout3Assist` + DBC paint (`work=0x0067CCC0`); residual→STG; FRONTEND plant; Soft-GS; **MENU-B3-2** presentation leave dead-ra→`0x223228` + pad-script | STG+TXD+FRONTEND cdvd=6584 **px≈23.6M lit≈100k** **logo-frontend MENU YES**; **INTERACTIVE PARTIAL** PC left park `0x12DF84` | 2026-07-31 |
| Blood Omen 2 (USA) | `SLUS_200.24` | `BloodOmen2SnAssist` — WAVE-7 dual list-stub + ofx title FB; **PL-015** title-FB pad inject + ForceRefreshPad (opens=2); no fake warm sector credit. **PL-027/G-GFX-3 Host→Local MAINMENU/MAINSKY DISABLED 2026-08-02**: real disc bytes at `RESOUR~1/LEVELS/UI/MAINMENU.BG2` are a Crystal Dynamics "goefile" container (magic `goefile`/`symlist`) whose early bytes are ASCII scripting symbol names (`getstate`, `position`, `rotation`, `color`, …; entropy ~4.2 bits/byte), not pixels — painting them as raw PSMCT32 fabricated garbage, not real menu art. Real texture bytes (if present in BG2 raw form at all) are further in, behind an undecoded sub-section. | Host→Local residual now suppressed — honest framebuffer instead of fabricated garbage; T2 PARTIAL pad inject unaffected | 2026-08-02 |
| God of War (USA) | `SCUS_973.99` | `GodOfWarAssist` — Path2 expand + **Host→Local residual** R_SHELL/TIT1 (lit residual); PL-023 DMA END; pad-after-px; Path3MaskedByVif held | **Host→Local residual** lit=60866 residualDispfb=60866 natural=0 expandHits=2 — **not natural MENU YES** | 2026-07-31 |
| Haven: Call of the King (USA) | `SLUS_205.17` | `TeamIcoAssist` — PreferIopRp + SoftFloatBridge + VIF/JREXIT + MENU-HAVEN-3 Host→Local SYSTEM.RW3/CUBE + **MENU-HAVEN-4 poison-`$ra`** | Soft-GS lit=43132 @100M Host→Local residual; **fleet 50M CRT0 px=0 expected** | 2026-07-31 |
| Shadow of the Colossus (USA) | `SCUS_974.72` | `TeamIcoAssist` — PreferIopRp + MENU-SOTC-2 Host→Local MANAGER/NICO/KERNEL | Soft-GS lit=120153 Host→Local residual — **not natural MENU YES** | 2026-07-31 |
| Whiplash (USA) | `SLUS_206.84` | `WhiplashAssist` — MENU-WHIP-2 Host→Local GOE firstscreen + ofx expand **DISABLED 2026-08-02** (fabricated non-image RKV script/param bytes as PSMCT32 pixels; RKV confirmed genuinely audio-only via full TOC dump, 356+ `vo/*`/`streams/wav/*` entries dominate the 1.29 GiB). Real per-level graphics geometry lives in `WHIPLASH/MAP/*.MP2` (`goefile`→`MAP0`→`MPGM` chunks, VU1-microcode-packed vertex blobs — not yet decoded) and materials reference textures **by name** (`MPIM` chunks), not embedded pixels; the shared texture resource pool itself is still unlocated. **RealSifRpc GOE stream-table relay rewritten 2026-08-02**: the old bridge bulk-preloaded a fixed Code/firstscreen/frontend order into an invented `0x01C00000` scratch address after an arbitrary poll-count wait; traced the real request packet instead — its `w2` field is a client poll cursor, not a stream selector (counts 0x2..0xFF as one run), and it carries the EE's own real ring-buffer pointer at `+0x1C`. Streams now open lazily by real TOC name the instant the game asks, and bytes deliver only into the real per-request pointer, rotating fair-share (no fixed order, no guessed address). `MaybeFillTitleRing`'s address-guessing scanner removed as redundant. | Verified live: `Code` (574,216B), `firstscreen` (184,708B, **100% delivered**), `frontend` (1,240,220B) stream real bytes progressively; EE PC visibly advances into new code once firstscreen completes (was static before). Still no natural visible render by 400M cycles — game stops issuing stream-table polls after ~1 MB total (Code 75%, frontend 35%) while thread 2 (an unrelated SN-runtime scheduler-helper) spins forever on a fabricated `WaitSema(3)` signal; likely starves the real producer. Next: investigate the sema=3 fabricate loop (`WHIP_SEMA_FIX_V3` in `SonyKernelHle.cs`). **Separate, general infra fix landed the same day** (`RealSifRpc.LoadModuleByPath`, applies to *every* title, not just Whiplash): the real disc `IOPFILE.IRX` — Crystal Dynamics' actual compiled GOE_FSRV driver — was silently never loading. An earlier probe (e.g. an empty-path MOD_LOAD) pre-registered the module *name* with no image, and the real, later MOD_LOAD request with the true disc path was short-circuited by an "already registered" fast path that never checked whether the existing registration actually had a loaded image, so the real bytes were never read and `_start` never ran. Fixed to only take that fast path when the existing registration already has a real image or is deliberately HLE-owned (`PADMAN`/`MCMAN`/etc.); otherwise it falls through to the real disc load. Verified live: `IOPFILE.IRX`'s real `_start` now genuinely executes on the IOP R3000 interpreter (100,000+ real instructions, previously zero) for both Whiplash and Blood Omen 2, and the same fix also unblocked `SDRDRV`/`IOPSND`/`IOPMEM`/`IOPSNDS`. Its `_start` doesn't return within a 2M-instruction budget (likely spawns a worker thread and cooperatively yields rather than returning) — real cross-scheduler interleaving of a mid-`_start` IOP module is the next open question, not yet solved. | 2026-08-02 |
| Mortal Kombat: Deception (USA) | `SLUS_208.81` | `MidwayFamilyAssist` **DEC** — PL-012 INTERACTIVE + **PL-029** gameart Host->Local Soft-GS (imgBytes art-scale); Path3MaskedByVif held | **MENU+INTERACTIVE** + imgBytes=557056 @100M; residual EE BITBLT natural | 2026-07-31 |
| Mortal Kombat: Deadly Alliance (USA) | `SLUS_204.23` | `MidwayFamilyAssist` **DA** — WAVE-6 fail-tails + **PL-013** pad sel-idx + **PL-030** menu-band display drain + belt fail-tail demote (core 6) | **MENU YES** + **T2 YES** + FRONTEND PARTIAL gifCompleted=2980 px≈47.7M imgBytes=98304 @100M SEMA_OFF | 2026-07-31 |

Format: short description + link to issue/commit when available.
