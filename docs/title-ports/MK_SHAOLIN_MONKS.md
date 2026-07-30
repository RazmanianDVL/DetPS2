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

## Result this session

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — gifP3 **12**, dmac **17**; D-pad+CROSS pad; PC oscillates `0x4148xx`/`0x4275xx`; **"Kombat"**+**"Start"** strings; accept soft |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **12** (logo spine restored from 5) |
| ADX multi-table self-deadlock | **FIXED** — do not plant `0x414568` into `0x75E7A0` |
| Post-list format stall `0x47670C` | **Escaped** → Midway main |
| Post-spine memset `0x385278` | **Broken** when `a2-a1` absurd |
| Pad inject START/CROSS | Dense after 60M + CLI `pad-inject` |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; PollSema-id; **no `*0x75C0D0` plant** |
| Smokes | **ALL PASSED** |

---

## How far

| Milestone | Status | Evidence |
|-----------|--------|----------|
| Disc boot / ELF | **Yes** | `Booted SLUS_210.87 entry=0x0011C070` |
| CRI ADX / GAMEDATA.WAD | **Yes** | `cdvdSectors=198840` |
| Historical gifP3=11 spine | **Yes / better** | gifP3=**12** dmac=**17** binds=**23** |
| Pad START lifts GS | **Yes** | 55M START → 67M gifP3=12 dmac=17 |
| Pad CROSS moves PC | **Yes** | 95M CROSS → `0x4148EC` / final `0x4275C0` |
| UI string | **Partial** | `"Kombat"` @ `0x57FA64`, `0x57FAB8`; many `"Start"` hits |
| Main menu (interactive UI) | **Near** | Stable spine + pad trajectory + title/start strings; accept-to-submenu soft |

### pad-inject @ 120M (host-present)

```
  55000000  PRESS START  gifP3=5
  62000000  gifP3=7 dmac=12
  67000000  gifP3=12 dmac=17  PC=0x385504  ← spine + memset band
  65000000  break menu memset remain=huge → 0x385294
  75000000  PRESS CROSS  PC=0x3854FC..0x3855EC
  87000000  PC=0x47FD84
  95000000  PRESS CROSS  PC=0x4148EC  (ADX)  syscalls climbing
 120000000  final PC=0x4275C0 gifP3=12 dmac=17 cdvd=198840 px=32.4M
```

---

## Fixes this session

1. **Dense menu pad** (`MaybeInjectMenuPad`) — faster cadence once gifP3≥11; CROSS-heavy in menu PC bands; wake SleepThread peers.
2. **Break post-spine memset** (`MaybeBreakMenuMemset`) — `0x385278` clear loop with inverted/huge `a2` never exits; force epilogue.
3. Prior spine restores held: ADX self-deadlock scrub, list-walk break, format-stall → Midway main, no `*0x75C0D0` plant.

## MENU

**NEAR-MENU (interactive-class, not full accept-to-submenu).**  
Evidence: gifP3=12 / dmac=17 / WAD 198k / START restores spine / CROSS changes PC into ADX-title / **"Kombat"** in RDRAM. Missing: stable menu-only selection chrome + accept-to-submenu proof.

## Commands

```powershell
dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o out/menu4build
# do NOT set DETPS2_SEMA_STALL_YIELD
$env:DETPS2_TRACE_BIOS='1'
dotnet exec out/menu4build/DetPS2.Core.dll blocker-trace user-media-mk.json --cycles=100000000 --host-present --find-string=Kombat
dotnet exec out/menu4build/DetPS2.Core.dll pad-inject user-media-mk.json --cycles=120000000 --host-present `
  --press=START:55000000:1200000 --press=CROSS:75000000:1500000 --press=CROSS:95000000:1500000
```
