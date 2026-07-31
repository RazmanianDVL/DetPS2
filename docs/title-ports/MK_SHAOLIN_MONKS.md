# Mortal Kombat: Shaolin Monks (USA) — commercial port progress

| Field | Value |
|-------|--------|
| Title | Mortal Kombat - Shaolin Monks (USA) |
| Serial | `SLUS_210.87` |
| Media id | `mk-shaolin-monks` |
| ISO | `C:/Users/xxraz/Downloads/MortalKombatShaolinMonks(USA).iso` |
| BIOS | `C:/Users/xxraz/Documents/PCSX2/bios/Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin` |
| Config | `user-media-mk.json` |
| Worktree | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2-sm-w4` |
| Agent date | 2026-07-31 (wave-4) |
| ROMDIR gate | **CLOSED** |
| IRX tip | `6deaa0e` always-on; 27/27 IOPBTCONF |
| Wave-14 tip | SifRpc MSFLAG plant removed; WAD + gifP3=11 restored |

---


## Result this session (wave-4 / sm+0x28 jalr poison + arena)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR?** — Soft-GS gifP3=**11** px=**573440** PC FAE8 body; **not MENU YES** |
| Root cause of force TIMEOUT | **Fixed** — wave-3 planted `sm+0x28/+0x2C=0x100000` as "capacity"; those fields are **allocator fn ptrs** (`FUN_0043BE08`/`43BE98` `jalr`). Live AdEL @0x1FFFFFFF → TIMEOUT |
| Arena + EE bump stub | **Landed** — stub @`0x01FE0100`, ctx @`0x01FE0200`, arena `0x00C00000`..+8MiB; desc buf 6MiB |
| Force 43B670 | **Completes cleanly** (~300k cyc → trampoline); finds free slot `0x55E25C`; type-1 object path entered |
| 43AB88 object | **Still fails** (v0=0) — residual inside type-1 construction after `43A4F8` / `0x56xxxx` tables |
| C1C0 / 26FBF0 | **Still never binds** (no successful slot object) |
| Synthetic type5 | **Not used** |
| Selection index | Still unproven |

### Soft-GS scoreboard (wave-4)

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **Claim tip (pre-w4)** | varies | **573440** | 2 | **11** | ~142 | NEAR plateau |
| **Wave-3 force** | thrash / TIMEOUT | — | — | 11 then death | — | jalr poison |
| **Wave-4 100M pad** | **`0x43FBE8`** FAE8 | **573440** | 2 | **11** | 30 | force returns; slot0 empty; spine held |
| **Wave-4 scoreboard-metrics** | `0x43FBE8` | **573440** | 2 | **11** | 30 | cdvd=201914 exit=false |

### Change class

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - Remove sm+0x28/+0x2C `0x100000` capacity plant (jalr poison)
  - Install EE bump-alloc stub + stream-manager allocator pointers
  - Prefill descriptor +0x18/+0x1C (6MiB) so `43BA48`→`43BDD0` used instead of `43BE08`
  - Seed Midway heap table for natural `20F058`; force always uses `43B670`
  - Early ESCAPE resume when force PC leaves bands (250k) + scrub bogus `*0x678458<0x100000`
- **SHARED**: none
- **Tool**: `tools/_patch_sm_w4.py`

### Residual wall (wave-4)

1. **`FUN_0043AB88` returns null** on type-1 object body after arena alloc succeeds (pcbreak: slot `0x55E25C`, buffers live, path reaches `0x43AEB0` then fails deeper in `0x56xxxx` table work). Need PCSX2+PINE live object / table dump **or** deeper decompile of post-`43A4F8` type-1 path.
2. **C1C0 never runs** until slot+0x3C object is non-null.
3. **Selection index** still unproven under D-pad.
4. **Do not** re-plant sm+0x28 as raw size; **do not** re-enable type5 slot plants.

### Play! / PINE

- Play! GameConfig.xml: no SLUS_210.87 entry
- PINE: **N this wave** (pcbreak + disasm of 43BE08/43AB88/43A410 sufficient for jalr poison + object residual)

---

## Result prior session (wave-3 / reconstruct load request)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR? / GS?** — not claimed. Host tip currently Exit@~22M (baseline A/B same without w3 patch) |
| Reconstruct 26F918 args | **Landed** — a1/a2 are **display dims** (512x384), not name ptrs; path via FUN_00211148 name table 0x4D3A10[id]+1 into handle+0xEC; t0=heap id from *0x584918 |
| Heap wall | Midway heap table 0x65E998 **empty** under HLE → full 26F918 hits FUN_0020F058 AdEL. Wave-3 prefers **FUN_0043B670** with prepared descriptor + sm+0x28/+0x2C capacity |
| Trampoline timeout | 2M-cycle abandon if force path never returns (prevents AdEL hang) |
| Synthetic type5 | **Not used** |
| PresentEeSifHandshake in StartLoadedModule | **Not re-added** |
| Selection index | Still unproven |

### Soft-GS scoreboard

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **Wave-2 (healthy boot)** | FAE8 bands | 573k–860k | 2–3 | **8–9** | 12–16 | force 26F918 a1=a2=0 → *0x678458=0 |
| **Wave-3 mid-session (shared tree)** | hung 0x20243C after AdEL | 860160 | 3 | **11** | 17 | reconstructed dims/path; 26F918 AdEL @0x552023 (dead heap) |
| **Wave-3 tip A/B (isolated)** | 0x486C10 | 286720 | 1 | **5** | 7 | Exit@22M — **baseline without patch same** |

### Change class

- **TITLE_LOCAL** MidwayBootAssist.cs: reconstruct 32EA08/26FD80 load-request; IsResourceHeapLive → force 43B670 when heaps dead; descriptor builder; path strcpy handle+0xEC; sm+0x28/+0x2C capacity; 2M trampoline timeout.
- **SHARED**: none. No PresentEeSifHandshake in StartLoadedModule. No type5 plants.
- **Tool**: 	ools/_patch_sm_w3.py (idempotent).

### Residual wall

1. Midway heaps never init under HLE → cannot run full 26F918 I/O path; 43B670 alone may still fail 43AB88 object create without real resource body.
2. Host tip Exit@22M (vector thrash) blocks late-force verification this session — not attributed to w3 (baseline match).
3. Selection index + interactive MAINMENU still unproven.
4. Next: PCSX2+PINE live *0x678458 / heap table after real menu, or root-cause heap init path under IRX.

### Play! / PINE

- Play! GameConfig.xml: no SLUS_210.87 entry
- PINE: **N this wave** (ELF XREF + disasm of 26F918/26FD80/32EA08/211148/43B9F8 sufficient for arg reconstruction)

---

## Result prior session (wave-2 / resource bind path)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR?** — not claimed. Soft-GS gifP3 plateau 8-9; slot0 still empty |
| Real resource bind path | **Landed** TITLE_LOCAL force-call `FUN_0026F918` then `FUN_0026FBF0` (BFC0→C1C0 chain) |
| Kick outcome | Force fires when gifP3>=8 @70M+; `*0x678458` stays 0 after kick (43B670 fails without real resource name args) |
| Synthetic type5 | **Not used** (wave-12 reject held) |
| PresentEeSifHandshake in StartLoadedModule | **Not re-added** (MSFLAG once at Sif.Reset) |
| Selection index | Still unproven under D-pad |

### Soft-GS scoreboard

| | PC | px | prims | gifP3 | dmac | notes |
|--|-----|-----|-------|-------|------|-------|
| **Before (tip 3748553 / wave-14)** | `0x43FAB4` | 573440 | 2 | **11** | 88 | FAE8 NEAR?; empty slots |
| **After (agent/menu-sm-w2)** | FAE8 / lock bands | 573440–860160 | 2–3 | **8–9** | 12–16 | force kick returns empty handle; no MENU claim |

### Change class

- **TITLE_LOCAL** `MidwayBootAssist.cs`: `MaybeForceResourceSlotBind` / `MaybeResumeAfterForcedResourceBind` — real EE force-call of `FUN_0026F918` (slot alloc via `FUN_0043B670` into stream pool `0x55E25C`) then `FUN_0026FBF0` (sole BFC0→C1C0 caller). Trampoline `0x01FE0030`. Prep handle at `0x678458` mirrors `FUN_0026FD80` zero+flags only.
- **SHARED**: none. Sif MSFLAG remains once at `Sif.Reset` only.
- **Rejected again**: synthetic type5 slot plants; force `26FD80` poll loop; PresentEeSifHandshake in StartLoadedModule.

### Residual wall

1. `FUN_0026F918` returns without `*0x678458=slot` when a1/a2 resource name args are zero — need real load request from `FUN_0032EA08` args or PCSX2+PINE live handle dump.
2. gifP3 8–9 on this wave (below wave-14 11 plateau) — second chrome still blocked.
3. Selection index unproven.

### Play! / PINE

- Play! `GameConfig.xml`: no SLUS_210.87 entry
- PINE: **N this wave** (ELF XREF of sole bind chain sufficient to land force path; kick fail is missing resource IDs, not unknown XREF)

---

## Result prior session (wave-14 / WAD restore + spine)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR?** @100M — gifP3=11 dmac=88 Soft-GS; selection + second chrome still unproven |
| Root cause of tip cdvd=1 | **Fixed** — `SifRpc.StartLoadedModule` PresentEeSifHandshake before every SIFMAN/SIFCMD/SIFINIT `_start` (c423c4f) collapsed GAMEDATA.WAD |
| LooksLikeAsciiWord EPC | **Fixed** — aligned code PCs (e.g. `0x414A30`) matched ≥3 printable; removed from data-as-code test |
| Logo spine main kick | **Added** — one Midway main re-entry when gifP3≤5 after cdvd≥180k (mirrors historical AdEL→main) |
| diagnose 20M | PC=`0x47FCF4` px=286720 prims=1 gifP3=5 dmac=7 cdvd=**198840** binds=16/252 |
| 100M host-present | PC=`0x43FAB4` px=573440 gifP3=**11** dmac=**88** cdvd=201400 (FAE8 stream body) |

### Change class

- **SHARED** `SifRpc.cs`: remove PresentEeSifHandshake from StartLoadedModule (keep `Sif.PresentSifInit` cold path)
- **TITLE_LOCAL** `MidwayBootAssist.cs`: data-EPC unaligned/past-RDRAM only; post-WAD main spine kick; delay group-6 plant until gifP3≥8
- **Not done:** synthetic stream slots, C1C0 force, FFmpeg

### Residual wall

1. gifP3 plateau **11** — empty stream slots / C1C0 never binds
2. Selection index still unproven
3. Soft-GS prims low — not full interactive MAINMENU claim

---

## Result prior session (wave-13 / IRX Soft-GS GS?)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR?** @100M only — diagnose 20M stays **GS?** (expected pre-spine) |
| Root cause of 286k px | **Documented** — one Soft-GS clear; not interactive MAINMENU; historical 11.7M px was host FMV inflation (retired) |
| AdEL “GAMEDATA” wild jump | **Fixed** — data-as-code AdEL re-home (EE + Midway vector escape) |
| gifP3 @100M | **11** (plateau; second chrome still blocked) |
| dmac @100M | **88** (was 16 pre-fix) |
| Stream slots / C1C0 | **Still empty / never binds** (wave-12 residual) |
| diagnose 20M | PC=`0x47FCF4` px=286720 prims=1 gifP3=5 dmac=7 cdvd=198840 binds=16 calls=252 |
| 100M host-present | PC=`0x43FAB4` px=573440 prims=2 gifP3=11 dmac=88 cdvd=201400 binds=23 calls=276 |

### Play! / PINE

- Play! `GameConfig.xml`: **no SLUS_210.87 entry** (generic IOP HLE)
- PINE: **N** for this wave (pcbreak AdEL + disasm sufficient)

### Change class

- **SHARED** `EmotionEngine.cs`: unaligned fetch → if PC looks like data-as-code (ASCII / past RDRAM), recover like open-bus (not every AdEL)
- **TITLE_LOCAL** `MidwayBootAssist.cs`: exception-vector escape when EPC is data-as-code
- **SHARED** `Program.cs`: `scoreboard-metrics` includes `prims`
- **Not done:** FFmpeg, slot plants, RealSifRpc thrash, force-call 26FBF0/C1C0

### Why 286720 px is not MAINMENU

1. `640×448 = 286720` — single Soft-GS framebuffer clear (`prims=1`).
2. diagnose 20M is **before** logo-spine restore (~58M) and stream plants (~60M).
3. Host logo Blit no longer counts as Soft-GS `px` (correctness).
4. Pad OPEN works (ghost after IOPRP); wall is **empty stream slots**, not pad.
5. Full writeup: [`out/traces/MK_IRX_GS_ROOT_CAUSE_20260731.md`](../../out/traces/MK_IRX_GS_ROOT_CAUSE_20260731.md)

### Residual wall (wave-13)

1. **gifP3 plateau 11** — FAE8 live; slot0 empty → no second-chrome Path3.
2. **C1C0 / 26FBF0 never runs** — force resource status does not bind objects.
3. **Soft-GS prims=2** @100M — almost no UI raster until objects bind.
4. Next: PCSX2+PINE live stream slots **or** correct path to `FUN_0026FBF0` with real manager (no type5 plants).

## Result prior session (wave-12)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — no claim; selection index + second chrome still unproven |
| Stream slot objects | **Blocked** — synthetic slot0 plant **REJECTED** (EE exception death) |
| C1C0 bind path | **Never runs** under HLE (pcbreak 0 hits @100M) |
| gifP3 | **11** (plateau held; no second-chrome lift) |
| dmac | **16** @100M |
| Selection index | Still unproven — multi busy `0x54E608+i*4` is re-entrancy only |
| diagnose 20M | PC=`0x47FCF0` px=11.7M gifP3=5 dmac=7 cdvd=198840 (baseline hold) |
| claim-class 100M | PC=`0x43FB40` px=32.1M gifP3=11 dmac=16 syscalls~914k cdvd=198840 |

### Play! / PINE

- Play! `GameConfig.xml`: **no SLUS_210.87 entry** (generic IOP HLE)
- Play! TITLE: generic only
- Play! PAD: `Iop_PadMan.cpp` — already SHARED
- PINE: **N** (disasm + pcbreak sufficient for reject)

### Change class

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - Wider `sel-idx-delta` bases (`0x54E608` multi busy, `0x75E7A0`, 0..16 cap)
  - Documented **REJECTED** `MaybePlantStreamSlotObjects` (type5 stub + D6F8[0])
  - **Not called** from Step — residual only as comment + empty stub for next agent
- **SHARED**: none

### Stream slot diagnosis (wave-12 evidence)

| Fact | Evidence |
|------|----------|
| Sole C1C0 caller | `jal 0x43C1C0` only at `0x43C0D4` inside `FUN_0043BFC0` |
| Sole BFC0 caller | `0x26FC34` inside `FUN_0026FBF0` (resource post-load bind) |
| `FUN_0026FBF0` / `0x26FD80` / C1C0 | **0 pcbreak hits** through 100M pad-inject |
| FAE8 still walks | Live PC samples `0x43FB40` / `0x43FBB0` / `0x44D744` (empty FBB0 + D6F8) |
| Slot plant type5 | Plant @74.1M → PC `0x8000018x` by 80M; final `0xAB47` — **regresses** healthy FAE8 |
| D770 object contract | `442A0`: +0x48≠0 valid; `D7C8`: type must be **1..4** to continue (type5 early-out still unsafe in practice with partial slot) |

**Do not** re-enable synthetic slot plants without PCSX2 live object dump or a working `26FBF0` path.

### Residual wall (wave-12)

1. **gifP3 plateau 11** — FAE8 live; empty slots / empty D6F8 → no second-chrome Path3.
2. **Selection index unknown** — D-pad only toggles multi re-entrancy + CAS cells.
3. **FUN_0043C1C0 never binds** — resource post-load (`26FBF0`) never reached; force status at `0x678458+0x48` unblocks wait but does not run bind.
4. Next: PCSX2+PINE dump of live menu stream slots **or** force/reach `FUN_0026FBF0` with a real manager pointer at `*0x678458`.

## Result prior session (wave-11)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — stream-manager ready planted; **selection index + gifP3≥12 still unproven** |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **11** (plateau; no second-chrome lift) |
| dmac | **16** @100M |
| Frame cb | **`*0x75BDD8=0x43F920` held** + arg `0x5BB860` |
| Group-6 multi | **`*0x75E950=0x43F920` held**; escapes **`0x427678`** (a0=6) |
| Stream cookie | **`*0x5BB860=1` planted** |
| Stream manager | **`*0x55E228` (base+0x38) ready=1** soft-plant (CD58 defaults); slot0 still empty |
| Stream work gate | **`*0x55E1EC=1` held**; skip `*0x55E200=0`; CAS re-arm held |
| Pad | Dense START/CROSS/DOWN/UP; ghost PADMAN; Play! TITLE/PAD consulted |
| Final PC | **`0x43FB40`** (FUN_0043FAE8) @100M |
| Accept | Stream leaf live; **selection index + second UI chrome not proven** |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; **no `*0x75C0D0` plant** |
| diagnose 20M | PC=`0x47FCF0` px=11.7M gifP3=5 dmac=7 cdvd=198840 (baseline hold) |

### Play! / PINE

- Play! `GameConfig.xml`: **no SLUS_210.87 entry** (generic IOP HLE)
- Play! PAD: `Iop_PadMan.cpp` (0x80000100) — already ported SHARED
- Play! TITLE: generic only — no title patches for SLUS_210.87
- PINE: **N** (full disasm of CD58 / FAE8 / FBB0 / C1C0 / D770 / parent `0x43CC40`)

### Change class

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - `MaybeInitStreamManager` — soft-plant FUN_0043CD58 defaults (`*base+0x38=1` ready, float config, clear lock/CAS garbage)
  - **Rejected**: force-call CD58 via trampoline (regressed gifP3 11→5 — resume raced other Step assists)
  - Wider menu-sel: `smReady`, slot `obj/+0x3C`, `wk/+0x60`, D6F8 table, `sel-idx-delta` under D-pad
- **SHARED**: none

### Stream manager diagnosis (disasm)

| Addr | Role |
|------|------|
| `FUN_0043CB18` | returns base `0x55E1F0` |
| `FUN_0043CD58` | memset manager 0x15CC; defaults; ready `*base+0x38=1` — sole caller `0x43CC40` never reached under HLE |
| `FUN_0043FAE8` | gate `*0x55E1EC==1` + CAS `*base+0x58`; walk 8 slots at `base+0x6C` stride `0x2AC` → `FBB0` |
| `FUN_0043FBB0` | requires `*slot==1` and `*slot+0x60!=1`; work uses `*slot+0x3C` object → `0x44D770` |
| `FUN_0043C1C0` | binds slot flag + object from resource descriptors (never runs — no objects) |
| D6F8 table | `0x55FA0C` ×8 — empty at 100M |

**Do not** plant `*slot=1` with null object (FBB0→D770 null path).

### Selection index (wave-11)

Dense D-pad `sel-idx-delta` logs: only binary toggles `*54E5E0/*54E610/*54E618/*54E620` 0↔1 and CAS `*55E248`. **No stable 0..N cell** that tracks Up/Down.

### pad-inject @ 100M (host-present, wave-11 soft-plant)

```
  58200000  logo-spine kick → ADX pump gifP3=5
  60000000  group-6 + frame-cb + cookie=1 + CD58 defaults ready=1
  73200000  re-arm stream CAS; gifP3 climbs 6→8→11
  75000000+ CROSS/DOWN/UP; FAE8 body; slot0 remains 0
 100000000  final PC=0x43FB40 gifP3=11 dmac=16 px=32112640 syscalls~914k cdvd=198840
```

### Residual wall (wave-11)

1. **gifP3 plateau 11** — FAE8 live; **slot0 empty** / no D6F8 objects → no second-chrome Path3.
2. **Selection index still unknown** — only busy/re-entrancy flags under D-pad.
3. **FUN_0043C1C0** resource→slot bind never runs; need PCSX2+PINE live menu objects or resource-path fix.
4. Force-call CD58 unsafe without isolated Step return after force.

## Result prior session (wave-10)

| Goal | Status |
|------|--------|
| **MAIN MENU** | **NEAR** — group-6 entry (`a0=6`) + natural multi countdown; **selection index + gifP3≥12 still unproven** |
| WAD stream | **Yes** — `cdvd=198840` |
| gifP3 | **11** (plateau; no second-chrome lift) |
| dmac | **16** @100M (stream body live; 120M CAS path still needed for dmac climb) |
| Frame cb | **`*0x75BDD8=0x43F920` held** + arg `0x5BB860` |
| Group-6 multi | **`*0x75E950=0x43F920` held**; escapes land **`0x427678`** (a0=6) not bare `0x427518` |
| Stream cookie | **`*0x5BB860=1` planted**; slot0 `*0x55E25C` still zero |
| Stream work gate | **`*0x55E1EC=1` held**; skip `*0x55E200=0`; CAS re-arm held |
| Pad | Dense START/CROSS/DOWN/UP; ghost PADMAN; Play! PAD consulted (generic) |
| Final PC | **`0x43FB40`** (FUN_0043FAE8 work loop) sustained 87–100M — not worker thrash |
| Accept | Multi + stream leaf live under pad; **selection index + second UI chrome not proven** |
| Constraints | `DETPS2_SEMA_STALL_YIELD` OFF; **no `*0x75C0D0` plant** |
| diagnose 20M | PC=`0x47FCF0` px=11.7M gifP3=5 dmac=7 cdvd=198840 (baseline hold) |
| verify 50M | PC=`0x41D608` px=28.9M gifP3=5 dmac=7 cdvd=198840 exitReq=False |

### Play! / PINE (wave-10)

- Play! `GameConfig.xml`: **no SLUS_210.87 entry** (generic IOP HLE)
- Play! PAD: `Iop_PadMan.cpp` (0x80000100) — ghost DMA + ForceRefreshPad already ported SHARED
- Play! TITLE/SIF: generic modules only
- PINE: **N** (disasm of multi `0x427518` / group-6 entry `0x427678` / FAE8 sufficient)

### Change class (wave-10)

- **TITLE_LOCAL** `MidwayBootAssist.cs`:
  - `PickMenuDispatchResume` / `ApplyMenuDispatchResume` — prefer **`0x427678`** (sets a0=6) over bare multi; only heal *dead* $ra (never retarget worker→pump — that caused `PC=0x8000018C`)
  - `MaybeBreakMenuCallbackCountdown` — **no sticky break on natural s2=0..5** (only absurd s2≥64 / extreme sticky)
  - Post-spine + lock-wrapper escapes use menu-dispatch helper; ADX lock→menu kick after spine
  - Wider menu-sel telemetry (stream slot0 + pad DMA + extra BSS bands)
- **SHARED**: none this wave

### pad-inject @ 100M (host-present, wave-10)

```
  58200000  logo-spine kick → ADX pump gifP3=5
  60000000  group-6 + frame-cb + cookie=1 + stream gateEc=1
  73200000  re-arm stream CAS; gifP3 climbs 6→8→11
  75000000  CROSS; gifP3=11; memset + VU escape
  77000000  post-spine 0x47FEA8 → 0x427678 (group-6 a0=6)
  82000000  DOWN @ multi loop 0x427570
  84800000  CROSS → stream slot 0x43FBB0
  87000000+ sustained FAE8 body 0x43FB40 / 0x43FC14 / frame-cb 0x4156E4
 100000000  final PC=0x43FB40 gifP3=11 dmac=16 syscalls~914k cdvd=198840
```

### Residual wall (wave-10)

1. **gifP3 plateau 11** — stream FAE8 loop live but Path3 not 12–14; stream work **slot0 empty** (`*0x55E25C=0`) so FBB0 has nothing to draw.
2. **Selection index location still unknown** — D-pad moves multi busy flags `*54E610/*54E618` only; no stable 0..N cell under pad.
3. **Hard accept-to-submenu unproven** — no new UI string set after CROSS.
4. Next: PCSX2+PINE dump of live menu object / stream-manager slots under real pad; or force `FUN_0043CD58` stream-manager init if registration path never ran.

## Result prior session (wave-9)

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

### Play! / PINE (wave-9)

- Play! `GameConfig.xml`: **no SLUS_210.87 entry** (generic IOP HLE)
- Play! PAD: `Iop_PadMan.cpp` (0x80000100) — ghost DMA + ForceRefreshPad already ported SHARED
- PINE: **N** (not used this wave; disasm of FAE8/F920 sufficient for CAS wall)

### Change class (wave-9)

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
