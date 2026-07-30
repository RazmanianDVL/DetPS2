# Mortal Kombat: Shaolin Monks (USA) — commercial port progress

| Field | Value |
|-------|--------|
| Title | Mortal Kombat - Shaolin Monks (USA) |
| Serial | `SLUS_210.87` |
| Media id | `mk-shaolin-monks` |
| ISO | `C:/Users/xxraz/Downloads/MortalKombatShaolinMonks(USA).iso` |
| BIOS | `C:/Users/xxraz/Documents/PCSX2/bios/Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin` |
| Config | `user-media-mk.json` |
| Worktree | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2` |
| Agent date | 2026-07-30 |
| ROMDIR gate | **CLOSED** |

---

## Result this session (wave-9)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — stream CAS re-arm + post-spine worker escape; **selection index + gifP3≥12 still unproven** |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **11** (plateau; CAS re-arm did **not** lift Path3 to 12–14) |
| dmac | **730** @120M (was **16** wave-8 — stream body re-enters) |
| Frame cb | **`*0x75BDD8=0x43F920` held** + arg `0x5BB860` |
| Group-6 multi | **`*0x75E950=0x43F920` held** cookie `0x5BB860` |
| Stream cookie | **`*0x5BB860=1` planted** (live may become `0x5BB8`) |
| Stream work gate | **`*0x55E1EC=1` held**; skip `*0x55E200=0` held |
| Stream CAS | **`*0x55E248` re-armed 0** (was stuck 1 after first FAE8 pass) |
| Pad | Dense START/CROSS/DOWN/UP; ghost PADMAN; Play! PAD consulted (generic) |
| Final PC | **`0x43FB60`** (stream work body) — was `0x480Axx` worker thrash |
| Accept | Stream leaf live; **selection index + second UI chrome not proven** |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; **no `*0x75C0D0` plant** |
| Smokes | **ALL PASSED** |
| diagnose 20M | PC=`0x47FCF0` px=11.7M gifP3=5 dmac=7 cdvd=198840 binds=16 calls=241 (baseline hold) |

### Play! / PINE

- Play! `GameConfig.xml`: **no SLUS_210.87 entry** (generic IOP HLE)
- Play! PAD: `Iop_PadMan.cpp` (0x80000100) — ghost DMA + ForceRefreshPad already ported SHARED
- PINE: **N** (not used this wave; disasm of FAE8/F920 sufficient for CAS wall)

### Change class

- **TITLE** `MidwayBootAssist.cs`: `MaybeRearmStreamCas`, `MaybeEscapePostSpineWorkerThrash`, skip-flag hold, prefer group-6/stream over ADX for post-spine lock escape
- **SHARED**: none this wave

### pad-inject @ 120M (host-present, wave-9)

```
  58200000  logo-spine kick → ADX pump gifP3=5
  60000000  group-6 + frame-cb + cookie=1 + stream gateEc=1 skip200=0
  73200000  re-arm stream CAS *0x55E248=0 (was 1); gifP3 climbs 6→8→11
  75000000  CROSS; gifP3=11; memset + VU pastEp escape
  77000000  post-spine worker thrash 0x47FEA8 → pump/group-6
  85550000  menu-sel tick *54E600 climbs; cas248 oscillates 0/1 under re-arm
 120000000  final PC=0x43FB60 gifP3=11 dmac=730 syscalls~1.08M cdvd=198840
```

### Residual wall (wave-9)

1. **gifP3 plateau 11** — stream FAE8 re-entry lifts **dmac** (16→730) not Path3; second chrome needs UI/PATH3 path, not only stream DMA.
2. **Selection index location still unknown** — D-pad moves `*54E610/*54E618` flags only; wider BSS scan not yet a stable 0..N cell.
3. **Hard accept-to-submenu unproven** — no new UI string set after CROSS.
4. Prefer PCSX2+PINE dump of menu object / selection cell under real pad if next wave stalls.

## Result prior session (wave-8)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — cookie init + sticky thrash escapes; **selection index + second chrome unproven** |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **11** (no second-chrome lift to 12–14) |
| dmac | **16** |
| Frame cb | **`*0x75BDD8=0x43F920` held** + arg `0x5BB860` |
| Group-6 multi | **`*0x75E950=0x43F920` held** cookie `0x5BB860` |
| Stream cookie | **`*0x5BB860=1` planted** (later live word0 may become `0x5BB8`) |
| Stream work gate | **`*0x55E1EC=1` held** |
| Pad | Dense START/CROSS/DOWN/UP; sticky lock→pump; syscalls **~4.34M** @150M |
| UI strings | **Kombat**, **Start** in RDRAM |
| Accept | Soft thrash walls escaped; **selection index + second UI chrome not proven** |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; **no `*0x75C0D0` plant** |
| Smokes | **ALL PASSED** |

### pad-inject @ 150M (host-present, wave-8)

```
  60000000  group-6 + frame-cb + cookie=1 + stream gate
  75000000  CROSS; gifP3 5→11; VU pastEp escape → ADX pump
  76850000  lock hot break
  89150000+ stickyBand lock thrash → 0x4147F8 (syscalls climb hard)
 150000000  final PC~0x480A88 gifP3=11 dmac=16 syscalls~4.34M cdvd=198840
```

### Residual wall (wave-8)

1. **gifP3 plateau 11** — second chrome / historical 12–14 YES band not reached.
2. **Selection index location still unknown** — D-pad does not move a stable 0..N cell under pad telemetry (`*54E610/*54E618` flags move only).
3. **Hard accept-to-submenu unproven** — no new UI string set after CROSS.
4. Late PC often in commercial-worker / lock / pad bands rather than a clear menu accept leaf.

## Result prior session (wave-7)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — G0 Exit/no-WAD regression **fixed**; stream work gate **open**; hard accept still unproven |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **11** (spine restored; was 5 under pre-RR G0 regression / wave-6 no-main-rehome) |
| dmac | **16** |
| Frame cb | **`*0x75BDD8=0x43F920` held** + arg `0x5BB860` |
| Group-6 multi | **`*0x75E950=0x43F920` held** cookie `0x5BB860` |
| Stream work gate | **`*0x55E1EC=1` held** (was wrong plant at `0x55E1E8` only) |
| Pad | Dense inject; ghost PADMAN; PC in pad-poll / stream work / lock wrappers |
| UI strings | **Kombat**, **Start** in RDRAM |
| Accept | Soft thrash walls escaped; stream work body **runs**; **selection index + second UI chrome not proven** |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; **no `*0x75C0D0` plant** |
| Smokes | **ALL PASSED** |

---

## How far

| Milestone | Status | Evidence |
|-----------|--------|----------|
| Disc boot / ELF | **Yes** | `Booted SLUS_210.87 entry=0x0011C070` |
| CRI ADX / GAMEDATA.WAD | **Yes** | `cdvdSectors=198840` |
| G0 THREADMAN RR for SM | **Yes** | `PreferRoundRobinSched=true` on disc mount — priority band caused Exit@12.4M |
| Frame cb re-arm | **Yes** | `*0x75BDD8=0x43F920` held after plant |
| Group-6 multi plant | **Yes** | `*0x75E950=0x43F920` held |
| Stream work gate | **Yes** | `*0x55E1EC=1` — `FUN_0043FAE8` no longer early-outs |
| Healthy post-WAD EE loop | **Yes** | PC in `0x414xxx` / `0x4275xx` / `0x43FBxx` / `0x43FDxx` / `0x44D7xx` |
| UI strings | **Partial** | Kombat / Start (Continue/Options not always found at 120M) |
| Selection index change | **No** | `*0x54E620` re-entrancy only; small-int scan in `0x54E5E0..` does not track D-pad |
| Second UI chrome (gifP3 lift) | **Partial** | gifP3 **5→11** + dmac **7→16**; still short of historical 12–14 YES band |
| Main menu hard accept | **No** | MENU NEAR only |

### pad-inject @ 120M (host-present, wave-7)

```
  18000000  ADX gate binds=22 calls=224
  55000000  resource gate cdvd=198840
  58200000  logo-spine kick → ADX pump
  60000000  group-6 + frame-cb + stream gateEc=1
  75000000  PRESS CROSS; gifP3 climbs 5→11; VU blit escape
  88000000  PRESS DOWN
  98000000  PRESS CROSS
 120000000  final PC=0x38568C gifP3=11 dmac=16 cdvd=198840 syscalls~1.11M px=32.1M
```

### G0 regression fixed (wave-7)

**Symptom after G0 BIOS merge:** EE `Exit(0)` @ ~12.4M with corrupt `$ra`, `cdvdSectors=1`, syscalls frozen ~1729.

**Root cause (A/B):** `KernelState.FindNextRunnable` priority band + `MaybePreempt` reordered Midway ADX pump vs main. Pre-G0 circular RR restored WAD.

**SHARED fix:**
- `KernelState.PreferRoundRobinSched` — SM sets true on disc mount (`MidwayBootAssist.OnDiscMounted`)
- Priority scheduling remains default for THREADMAN smokes (`KernelHle_ThreadmanPriorityAndDelay` still passes)
- UDNL commercial handoff: IOPRP image apply is **name-only** (no bulk LoadIrx of retail IRX)

**TITLE_LOCAL fix:**
- Plant/hold `*0x55E1EC=1` (stream work gate for `FUN_0043FAE8`) — prior plant was wrong offset `0x55E1E8` only

---

## Fixes this session (wave-7)

1. **SHARED `KernelHle.PreferRoundRobinSched`** — SM opts into circular RR; priority band remains default for G0 smokes
2. **SHARED UDNL name-only IOPRP** — commercial handoff registers module names without LoadIrx upgrade of HLE services
3. **TITLE_LOCAL stream work gate** — `MaybeHoldStreamWorkGate` + resource-gate plant of `*0x55E1EC=1`
4. **Alarm fire soft** — callback invoke opt-in via `DETPS2_ALARM_FIRE=1` (API still arms/releases)
5. **ChangeThreadPriority** — no forced SwitchToNext unless `DETPS2_PRIO_YIELD=1`

Prior spine restores held: ADX self-deadlock scrub, list-walk break, format-stall, VU blit escape, memset break, frame-cb re-arm, group-6 multi, logo-spine narrow, title-hash escape, no `*0x75C0D0` plant.

## MENU

**NEAR-MENU (interactive-class EE path, not full accept-to-submenu).**

Evidence for NEAR:
- Stable post-WAD EE loop through 120M after G0 RR fix
- `*0x75BDD8` + `*0x75E950` held at stream tick
- `*0x55E1EC=1` — stream work body entered (`0x43FB30`, `0x43FDB0`, `0x44D744`)
- Dense pad + ghost DMA; syscalls climb
- Title strings Kombat/Start in RDRAM
- Menu tick `*0x54E600` climbs under pad

Missing for MENU YES / issue #7 close:
- **Selection index** memory not identified
- **Selection index** still unproven under pad
- Stream cookie object at `0x5BB860` remains **all-zero**
- gifP3 plateau **11** (needs 12–14+ / second chrome certification)
- Late PC parks in VU blit band `0x38568C` after spine (guard fires; still sticky)
- Hard accept-to-submenu unproven

### Residual wall

1. **Stream cookie `0x5BB860` object zero** — slot planted; object body never inited by real `FUN_0043ccf8` path. Need PCSX2 dump or cookie ctor decompile.
2. **Selection index location unknown** — D-pad does not move small ints in `0x54E5E0..+0x80`.
3. **gifP3 plateau 11** — spine restored but not full historical 12–14 YES band; VU band sticky after.
4. Prefer **shared HLE** for cookie/object init if root cause is incomplete resource-manager registration.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/game-sm
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/game-sm/DetPS2.Core.dll pad-inject user-media-mk.json --cycles=120000000 --host-present `
  --press=START:55000000:1500000 --press=CROSS:75000000:2000000 `
  --press=DOWN:88000000:800000 --press=CROSS:98000000:2000000
dotnet build Tests/DetPS2.Tests.csproj -c Release -o out/game-sm-tests
dotnet exec out/game-sm-tests/DetPS2.Tests.dll
```
