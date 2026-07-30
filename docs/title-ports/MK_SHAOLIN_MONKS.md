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

## Result this session (wave-6)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — healthy ADX pump / pad-poll / stream-tick loop held 120–150M; **hard accept-to-submenu not certified** |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **5** (logo spine; Path3 not yet climbing under current HEAD RPC timing) |
| dmac | **7** |
| Frame cb | **`*0x75BDD8=0x43F920` held** + arg `0x5BB860` |
| Group-6 multi | **`*0x75E950=0x43F920` held** cookie `0x5BB860` |
| Pad | Dense inject once multi+frame-cb live; ghost PADMAN; PC stays in pad-poll `0x4275xx` |
| UI strings | **Kombat**, **Start**, **Continue**, **Options**, **Select** in RDRAM |
| Accept | Soft thrash walls escaped; **selection index + second UI chrome not proven** |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; **no `*0x75C0D0` plant** |
| Smokes | **ALL PASSED** |

---

## How far

| Milestone | Status | Evidence |
|-----------|--------|----------|
| Disc boot / ELF | **Yes** | `Booted SLUS_210.87 entry=0x0011C070` |
| CRI ADX / GAMEDATA.WAD | **Yes** | `cdvdSectors=198840` |
| Frame cb re-arm | **Yes** | `*0x75BDD8=0x43F920` held after plant |
| Group-6 multi plant | **Yes** | `*0x75E950=0x43F920` held |
| Healthy post-WAD EE loop | **Yes** | PC oscillates `0x4149xx` ↔ `0x4275xx` ↔ `0x4156E0` ↔ `0x43F9xx`/`0x43FAxx` |
| Open-bus death (pre-wave-6) | **Fixed** | Was `0x00F30Cxx` thrash; now holds pad-poll |
| UI strings | **Partial** | Kombat / Start / Continue / Options / Select |
| Selection index change | **No** | `*0x54E620` is re-entrancy flag (set@`0x42757C`/clear@`0x427588`), not index |
| Second UI chrome (gifP3 lift) | **No** | gifP3 stays 5 |
| Main menu hard accept | **No** | MENU NEAR only |

### pad-inject @ 120M (host-present, wave-6c)

```
  55000000  PRESS START
  60000000  group-6 + frame-cb plant; logo-spine → ADX pump (not main)
  60750000  menu-sel fcb=g6=0x43F920 *54E600 climbing
  75000000  PRESS CROSS  PC in 0x4275xx / 0x43FAxx
  88000000  PRESS DOWN
  98000000  PRESS CROSS
 120000000  final PC=0x4275C0 gifP3=5 dmac=7 cdvd=198840 syscalls~380k px=31.8M
```

### Pre-wave-6 HEAD regression (baseline before fix)

```
  logo-spine kick pad-poll 0x427558 → Midway main
  IOPRP gen=2; title-hash thrash 0x47EBxx / outer list 0x474Cxx
  open-bus death 0x00F30Cxx; gifP3 stuck 8 then dead
```

---

## Fixes this session (wave-6)

1. **Logo-spine kick narrow** — never kick productive pad-poll (`0x4275xx`), frame-cb dispatch (`0x4156xx`), or stream tick (`0x43F9xx`). After bulk WAD resume to **ADX pump** not Midway main (avoids IOPRP + outer-list storms). Skip kick once group-6 multi filled.
2. **Title-hash sticky thrash escape** — band `0x47EAE0..0x47EFC0` re-visits → ADX pump / pad-poll; no gifP3≥12 gate (HEAD residual was stuck at gifP3=8).
3. **Outer list thrash escape** — band `0x474C00..0x474E00` sticky → pump.
4. **Open-bus nop-sled rescue** — past-RDRAM sticky prefers pump/pad-poll over stack-scan garbage (`0x170BFC` loop).
5. **Denser pad when multi+frame-cb live** even if gifP3 still 5–8; expanded menu-sel telemetry (cookie + small-int scan).

Prior spine restores held: ADX self-deadlock scrub, list-walk break, format-stall, VU blit escape, memset break, frame-cb re-arm, group-6 multi, no `*0x75C0D0` plant.

## MENU

**NEAR-MENU (interactive-class EE path, not full accept-to-submenu).**

Evidence for NEAR:
- Stable ADX pump / pad-poll / stream-tick / frame-cb loop through 150M
- `*0x75BDD8` + `*0x75E950` held at stream tick
- Dense pad + ghost DMA; syscalls climb
- Title strings + Continue/Options/Select in RDRAM
- Menu tick `*0x54E600` climbs under pad

Missing for MENU YES / issue #7 close:
- **Selection index** memory not identified (watch proved `*0x54E620` is dispatch re-entrancy, not index)
- **Second UI chrome** — gifP3 stays 5 (no Path3 lift after pad)
- Stream cookie object at `0x5BB860` remains **all-zero** (slot planted; object body never inited by real `FUN_0043ccf8` path)
- Hard accept-to-submenu (selection change + second chrome) unproven

### Residual wall (for orchestrator / PCSX2-PINE)

1. **Stream cookie `0x5BB860` object zero** — plant only the multi-slot/frame-cb pointers; real init that fills the cookie object is skipped. Stream tick `0x43F920` runs and sometimes enters work `0x43FAE8` (gate via `*(FUN_0043CB18()+16) != 1`), but UI state may still need cookie fields. Need PCSX2 dump of `0x5BB860..+0x40` on real menu, or decompile of `FUN_0043ccf8` / cookie ctor.
2. **Selection index location unknown** — scan of `0x54E5E0..+0x80` small ints does not move with DOWN. Need PINE watch on pad-accept store after CROSS on real hardware/PCSX2.
3. **gifP3 stuck at 5** under current HEAD (post-FILEIO/MFL RPC merges) vs historical wiki 12–14. Spine restore via main re-home is now intentionally avoided (causes death path). Shared GS/Path3 HLE or missing logo→menu transition callback may be required.
4. Prefer **shared HLE** for any cookie/object init if the root cause is incomplete resource-manager registration — not more permanent `.text` plants.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/sm-agent
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/sm-agent/DetPS2.Core.dll pad-inject user-media-mk.json --cycles=120000000 --host-present `
  --press=START:55000000:1500000 --press=CROSS:75000000:2000000 `
  --press=DOWN:88000000:800000 --press=CROSS:98000000:2000000
dotnet exec out/sm-agent/DetPS2.Core.dll blocker-trace user-media-mk.json --cycles=120000000 --host-present `
  --find-string=Kombat --find-string=Start --find-string=Continue --find-string=Options
dotnet build Tests/DetPS2.Tests.csproj -c Release -o out/sm-tests
dotnet exec out/sm-tests/DetPS2.Tests.dll
```
