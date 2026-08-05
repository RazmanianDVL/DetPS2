# GameQuirks / MidwayBootAssist — infrastructure debt audit

**Date**: 2026-08-04  
**Scope**: `src/DetPS2.Core/GameQuirks/*.cs` + `src/DetPS2.Core/MidwayBootAssist.cs`  
**Mode**: read-only classification. No code deleted or “cleaned up” here.  
**Goal**: eliminate the *need* for these assists via shared EE / IOP / SIF / DMA / UDNL / thread infra — not delete the safety nets until that infra exists.

## How to read this

| Tag | Meaning |
|-----|---------|
| **INFRA** | Papers over a real emulator gap (timing, IOP reboot/UDNL, SIF/RPC, DMA IRQ, EE threads/semas, FILEIO path, incomplete HLE). Shared core should absorb this. |
| **SECONDARY** | Escapes thrash that only exists because an earlier INFRA gap left heaps/lists/SP/`$ra` wrong. May shrink once the root INFRA lands; not itself “performance.” |
| **PRESENT** | Soft-GS Host→Local / DISPFB composite residual so present is non-black. Graphics/pipeline honesty gap, not pure FPS. |
| **INTERACTIVE** | Dense pad inject / ForceRefreshPad for INTERACTIVE residual claims. Test-campaign assist; not a cycle-budget optimization. |
| **PERF** | Pure performance (soft-float host IEEE, cycle-budget shortcuts with no correctness claim). Rare in this tree. |
| **SDK** | Registry / interface only — no runtime debt. |

**Honesty rule**: almost nothing in this folder is “just for speed.” Most rows are boot/correctness debt that titles hit under HLE + incomplete real IOP. Prefer global fixes per `docs/TITLE_HACKS.md` and `IGameQuirkModule` policy.

---

## One-line roll-up (per file)

| File | Absolute path | Classification (one line) |
|------|---------------|---------------------------|
| `IGameQuirkModule.cs` | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\IGameQuirkModule.cs` | **SDK** — extension contract; documents that quirks exist because HLE/IOP is incomplete, not performance. |
| `GameQuirkRegistry.cs` | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\GameQuirkRegistry.cs` | **SDK** — serial→factory table only; no timing/SIF/DMA behavior. |
| `MidwayBootAssist.cs` | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\MidwayBootAssist.cs` | **INFRA-heavy (SM)** — SIF force-init, starved WaitSema/Sleep, real SIF CD synthesis, CRI/ADXF plants, resource-stream force, PATH3/logo residual; not pure perf. |
| `MidwayFamilyAssist.cs` | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\MidwayFamilyAssist.cs` | **INFRA** — IOPRP/PADMAN version policy + SN FILEIO + MFL/MSL path/ring bridges + display-queue/lock escapes; Host→Local gameart is **PRESENT**. |
| `BloodOmen2SnAssist.cs` | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\BloodOmen2SnAssist.cs` | **INFRA** — SN ProDG scan stubs + cdrom short-name + IOPRP `"2340"`/UDNL arg (UDNL/path HLE); chrome/pad are **PRESENT**/**INTERACTIVE**. |
| `Burnout3Assist.cs` | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\Burnout3Assist.cs` | **INFRA** — IOPRP `"2800"` plant + GS flip pending via **CreditOwedHandlerCall** (DMA IRQ timing) + LGDEV/SIF thrash stubs; stage plant/pad **SECONDARY**/**INTERACTIVE**. |
| `GodOfWarAssist.cs` | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\GodOfWarAssist.cs` | **INFRA** — IOPRP `"3000"`/FreezeCache + empty-SIF poll + worker SwitchTo/SignalSema + sticky GIF DMA tags; heap/BST escapes **SECONDARY**; R_SHELL feed **PRESENT**. |
| `TeamIcoAssist.cs` | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\TeamIcoAssist.cs` | **INFRA** (good pattern) — PreferIopRpGetVersion only for SotC/Ico; Haven adds VIF busy/IRQ, JREXIT/`$ra` rescue, WaitSema pulse; soft-float register is **PERF**; Host→Local **PRESENT**. |
| `VexxAssist.cs` | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\VexxAssist.cs` | **INFRA** — IOPRP + host-served CD I/O (FILEIO/SIF never binds) + STREE CRC stream + AAAIOP sid HLE + freelist/list escapes; pad **INTERACTIVE**. |
| `WhiplashAssist.cs` | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\WhiplashAssist.cs` | **INFRA** — UsingCD force + IOPRP `"2550"` + FlushCache JREXIT/WaitSema rescue + GOE/RKV warm; Host→Local firstscreen **PRESENT**; pad **INTERACTIVE**. |

**None of the runtime modules are “pure performance only.”** Closest to PERF: Haven soft-float host registration (cycle cost of interpreter soft-double), which still unblocks FILEIO/DLL.DAT timing rather than shaving FPS for its own sake.

---

## Cross-cutting infra themes (shared gaps these paper over)

### 1. UDNL / IOPRP GetVersion handoff — **INFRA**

Almost every commercial assist plants ASCII IOPRP tags (`"2340"`, `"2550"`, `"2800"`, `"3000"`, `"2500"` via PreferIopRp) because HLE does not apply disc IOPRP images the way real `UDNL` does. LOADFILE GetVersion stays classic `0x00020000` or `"...."` → `0xFFFEFFFC` / Exit / FreezeCache.

**Eliminate via**: real or shared UDNL image apply + LOADFILE GetVersion filled from the applied image (see `docs/bios-ports/`, `docs/irx/UDNL_IOPRP.md`). Once universal, delete version plants across BO2/B3/GoW/Vexx/Whip/TeamIco PreferIopRp flags.

### 2. SIF / RPC path completeness — **INFRA**

Midway SM: force `sceSifInitRpc`, worklist plant, synthesized real SIF CD read, SIF poll success after logo.  
DA: MFL CallRpc send-buffer path empty under HLE (`_cdToArgBuf` vs EE send).  
GoW: empty SIF soft-return / poll-loop escapes.  
Vexx: host CD I/O because FILEIO bind never appears after SearchFile.  
RealSifRpc live registry still often empty until IOP modules finish `_start` (documented in `docs/TITLE_HACKS.md`).

**Eliminate via**: complete SIFCMD dispatch + IOP module run to `sceSifRegisterRpc`, correct CallRpc arg/reply mapping, DMA completion that unblocks SifSetDma waiters.

### 3. EE thread / WaitSema / SleepThread scheduling — **INFRA**

SM: `MaybeUnblockStarvedSema` / `MaybeUnblockStarvedSleep` (later partially generalized in `KernelHle`).  
GoW: SwitchTo worker after SignalSema; main SP poison repairs.  
Whip/Haven: JREXIT when `$ra=0` after CD_NCMD / CallRpc; WaitSema pulse on worker.  
IOP-side THREADMAN multi-thread GPR save is a deeper gap (TITLE_HACKS 2026-08-03).

**Eliminate via**: correct SignalSema/WakeupThread semantics, worker vs main scheduling, stack/`$ra` integrity across HLE traps; real IOP THREADMAN context switch for genuine IRX.

### 4. DMA / VIF / GIF IRQ completion — **INFRA**

B3: flip-queue pending only drains on VIF1/GIF DMAC handler IRQs → `CreditOwedHandlerCall` re-arm.  
GoW: sticky GIF DMA tag builders force END/QWC.  
Haven: VIF1 software-busy flag stuck while CHCR.STR clear.

**Eliminate via**: full DMAC completion → handler callbacks → VIF/GIF status bits matching hardware so games never see permanent pending/busy.

### 5. Path / media / proprietary FS middleware — **INFRA**

BO2: cdrom short-name rewrite collapses paths under HLE.  
Whip: UsingCD unset → host0 IOPRP path → Exit.  
SM: CRI cvFs / ADXF never registered on fast-boot spine → WAD open fails.  
Vexx: STREE0 virtual paths + host-serve when FILEIO fails.  
DA: MKDA.PAK / gameart.ssf stream readiness.

**Eliminate via**: correct ISO path combine, config/UsingCD defaults, and either real middleware IRX or shared HLE for CRI/GOE/STREE-class archives — not per-title PC plants.

### 6. Soft-GS present residual — **PRESENT** (not FPS)

Host→Local BITBLT of honest disc bytes (BO2 MAINMENU, Dec/DA gameart, GoW R_SHELL/TIT1, Whip firstscreen, Haven/SotC chrome) when PATH2/3 IMAGE never lands. Policy across files: **no invent PATH3 / no synthetic color** — residual only.

**Eliminate via**: EE→GIF PATH2/3 IMAGE + DISPFB composite working end-to-end so assist chrome is unnecessary.

### 7. Interactive pad residual — **INTERACTIVE**

Dense START/CROSS/D-pad + `ForceRefreshPad` after Soft-GS chrome (SM, DA/Dec, B3, GoW, BO2, Vexx, Whip). Keeps dual-buffer pad DMA STABLE between presents.

**Eliminate via**: reliable pad DMA dual-buffer + VBlank cadence without host-present side inject for correctness (host inject for *play* remains fine).

---

## Per-module detail (honest debt map)

### `IGameQuirkModule.cs` / `GameQuirkRegistry.cs` — SDK

- Interface + serial registry.  
- Policy text already states quirks are for HLE/IOP gaps, and global fixes win.  
- **No elimination target** except shrinking the set of registered factories as infra lands.

### `MidwayBootAssist.cs` (SLUS_210.87 Shaolin Monks) — largest debt surface

| Mechanism | Tag | Root gap to kill the quirk |
|-----------|-----|----------------------------|
| `MaybeForceSifInit` + trampoline | INFRA | CRT0 / SIF init on commercial fast-boot so `sceSifInitRpc` runs organically |
| `PlantSifWorklist` / Unstick-class SIF waits | INFRA | SIFCMD/RPC completion + waiters |
| `MaybeForceManagerInit` / `MaybeForceInitLocks` | INFRA | Real main/CRT0 path reaches manager + CreateSema without deadlock |
| `MaybeUnblockStarvedSema` / `MaybeUnblockStarvedSleep` | INFRA | Kernel thread wake / who signals these semas |
| `MaybeCompleteRealSifCdRead` | INFRA | Real FILEIO/CD RPC + Sif.Step cadence |
| CRI cvFs / ADXF plant + pump | INFRA | Middleware init + ISO-backed FS without synthetic ops table |
| Resource force BFC0/C1C0/D770/stream tick | INFRA | WAD load/bind after ADX when stream manager state is real |
| List/memset/hash thrash escapes | SECONDARY | Corrupt structures from incomplete resource path |
| Logo spine main kick / PATH3 gap-fill | PRESENT / SECONDARY | Natural gifP3 + Soft-GS NaturalDispfb without forced spine |
| Menu selection plant / pad inject | INTERACTIVE | Menu state reads pad organically |
| Host present FMV pacing hooks | PRESENT | Soft-GS owns logo; no host FFmpeg (policy already correct) |

**Not pure performance.** Fast-boot skipping CRT0 creates structural debt; “cycle budget” comments are about *diagnose windows* (50M vs 100M Soft-GS claim), not FPS hacks.

### `MidwayFamilyAssist.cs` (DA / Deception / Armageddon)

| Mechanism | Tag | Root gap |
|-----------|-----|----------|
| `PadModVerMajor4` + PreferIopRp + PreferSnFileIo | INFRA | PADMAN/IOPRP/FILEIO version + SN reply layout |
| Heap tree cycle break | SECONDARY | Incomplete OVL free after stub load |
| DA wait-ready / MSL ring seed / MFL path bridge | INFRA | Async MSL/MFL open path + archive stream |
| Soft-success fail-tails / post-logo gates | SECONDARY | Logo/display consumers fail under incomplete stream |
| Display lock / process force | INFRA | VIF1 display queue + IRQ clear of lock |
| Host→Local gameart.ssf | PRESENT | EE texture upload path |
| Menu pad / sel-index | INTERACTIVE | — |

Intentionally **does not** run SM CRI/logo plants — narrower than MidwayBootAssist.

### `BloodOmen2SnAssist.cs` (SLUS_200.24)

| Mechanism | Tag | Root gap |
|-----------|-----|----------|
| SN debugger extension stubs + scan success | INFRA / title middleware | SN ProDG load path under HLE |
| Patch cdrom short-name rewrite | INFRA | Path combine correctness |
| IOPRP `"2340"` + UDNL arg rewrite | INFRA | UDNL handoff |
| PreferIopRpGetVersion | INFRA | same |
| UseBigfile / GOE / freelist / list thrash | SECONDARY | Asset open after SN/IOP path |
| Host→Local MAINMENU + present refresh | PRESENT | IMAGE→DISPFB |
| ForceRefreshPad / interactive pad | INTERACTIVE | Pad DMA |

### `Burnout3Assist.cs` (SLUS_210.50)

| Mechanism | Tag | Root gap |
|-----------|-----|----------|
| IOPRP `"2800"` plant | INFRA | UDNL / DNAS280 |
| Flip pending + `CreditOwedHandlerCall` | INFRA | **DMA IRQ / AddDmacHandler timing** |
| Flip-wait stub / leave park | SECONDARY | Same IRQ gap + cadence |
| LGDEV entry/CallRpc stubs | INFRA | Proprietary LG device RPC (or skip path correctness) |
| Boot wait flag / stage/frontend plant | SECONDARY | Post-IRX asset bind |
| Logo pad / presentation leave | INTERACTIVE / SECONDARY | — |

### `GodOfWarAssist.cs` (SCUS_973.99)

| Mechanism | Tag | Root gap |
|-----------|-----|----------|
| IOPRP `"3000"` + FreezeCache clear + UDNL arg | INFRA | UDNL |
| Heap defaults / freelist / BST walk guards | SECONDARY | Config dict never filled under HLE path |
| Empty SIF poll / caller escapes | INFRA | SIFCMD traffic |
| Worker yield SwitchTo + SignalSema | INFRA | EE multi-thread + cmd queue |
| Sticky GIF DMA tag finish | INFRA | GIF/DMAC tag builder completion |
| Table-index / byte-sum / list escapes | SECONDARY | Corrupt pool / huge length |
| Host→Local R_SHELL/TIT1 | PRESENT | Stream→IMAGE |
| Pad after Soft-GS | INTERACTIVE | — |

### `TeamIcoAssist.cs` (SotC / Ico / Haven)

| Mechanism | Tag | Root gap |
|-----------|-----|----------|
| PreferIopRpGetVersion only (SotC/Ico/Haven) | INFRA | **Best-practice** version policy — no memory plant |
| Haven SoftFloatBridge register | PERF (unblock) | Interpreter soft-double cost in LUT fill |
| Haven VIF busy clear + VIF1 IRQ credit | INFRA | DMA complete / software busy flag |
| Haven JREXIT / poison `$ra` / bad-PC / WaitSema pulse | INFRA / SECONDARY | Stack/`$ra` after open-bus thrash |
| Host→Local SYSTEM.RW3 / CUBE / MANAGER / NICO | PRESENT | Soft-GS IMAGE residual |
| Haven VBlank poll sticky | INFRA | INTC VBlank timing |

### `VexxAssist.cs` (SLUS_203.83)

| Mechanism | Tag | Root gap |
|-----------|-----|----------|
| IOPRP version plants | INFRA | UDNL |
| Host CD stubs + STREE CRC index + package recover | INFRA | FILEIO bind + TRE virtual FS |
| AAAIOP sid soft HLE | INFRA | Real IRX RPC or shared audio-driver HLE |
| Freelist / list / ctor / name-search escapes | SECONDARY | Incomplete open/read dest + object ctor |
| CRT/string/malloc plants | INFRA / SECONDARY | Heap hook never installed |
| Pad inject | INTERACTIVE | — |

### `WhiplashAssist.cs` (SLUS_206.84)

| Mechanism | Tag | Root gap |
|-----------|-----|----------|
| UsingCD force branches | INFRA | Config default / media detect |
| IOPRP `"2550"` + PreferIopRp + PreferSnFileIo | INFRA | UDNL + SN FILEIO layout |
| FlushCache JREXIT rescue + WaitSema pulse | INFRA | `$ra`/thread after CD_NCMD |
| PS2.RKV / GOE warm + ring fill | INFRA / SECONDARY | Stream-table → GOE Open/Start cadence |
| Host→Local firstscreen/frontend | PRESENT | IMAGE residual |
| Pad inject | INTERACTIVE | Title-local WaitSema only (good constraint) |

---

## Priority order to *eliminate need* (not delete quirks yet)

1. **UDNL + LOADFILE GetVersion** — single fix retires the largest shared plant class (BO2/B3/GoW/Vexx/Whip/TeamIco/Midway family).  
2. **SIFCMD real registration + CallRpc arg/reply fidelity** — retires MFL bridges, empty-SIF polls, host-CD substitutes where FILEIO should work.  
3. **DMAC → VIF/GIF handler IRQs** — retires B3 flip re-arm, GoW DMA tag finish, Haven VIF busy.  
4. **EE WaitSema / multi-thread SwitchTo correctness** — retires SM starved-sema, GoW worker force, Whip/Haven JREXIT rescues (overlap with KernelHle generalizations already started).  
5. **IOP THREADMAN real context switch** — needed for genuine IRX `_start` to reach `sceSifRegisterRpc` (TITLE_HACKS root cause).  
6. **PATH2/3 IMAGE + DISPFB** — retires Host→Local PRESENT residuals without inventing pixels.  
7. **Only then** strip per-title SECONDARY thrash escapes and INTERACTIVE densify if natural pad/menu works.

---

## Explicit non-goals of this audit

- Do **not** mass-delete GameQuirks “because debt.” Menus/campaigns still depend on them.  
- Do **not** treat Host→Local honest disc bytes as “cheating performance” — they are residual present until GIF IMAGE works.  
- Do **not** merge SM plants into DA/Dec/Arm (MidwayFamilyAssist exists specifically to avoid that).  
- Prefer **infra PRs that make a quirk go quiet under env-off flags** over silent removal.

---

## Source map

| Artifact | Path |
|----------|------|
| Quirks dir | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\` |
| SM assist (lives outside folder, registered as module) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\MidwayBootAssist.cs` |
| Policy | `docs/TITLE_HACKS.md`, `docs/DEVELOPER_GUIDE.md` §7 |
| Related IRX/SIF notes | `docs/irx/SIF_BRIDGE.md`, `docs/irx/UDNL_IOPRP.md`, `docs/bios-ports/SIFINIT_EESYNC.md` |

---

*Audit only. Keep assists until the corresponding INFRA row has a shared fix and title fleet confirms silence.*
