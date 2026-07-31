# LIBSD (Sound Device Library)

Agent: AGENT-CS (Phases 6+7)  
Date: 2026-07-30  
Authority: ps2sdk `iop/sound/libsd` `exports.tab` + `libsd-common.h` / `libsd.h`; SCPH70008 ROMDIR; Ghidra module string `libsd`  
Surface: `src/DetPS2.Core/IopLibSdHost.cs` + Spu2 host hooks; installed from `IopExtendedBiosHost.Install`

## 1. Role

Retail IOP audio stacks (SDRDRV, MSL.IRX, game sound IRX) import the **`libsd`** export table and call `sceSdInit` / `sceSdSetParam` / `sceSdSetSwitch(KON|KOFF)` before any voice playback. Without a registered table, LOADCORE LinkImports patches every ordinal to unresolved `jr ra` and init returns garbage.

DetPS2 HLE provides:

1. Export table **libsd 1.4** (28 ordinals, ps2sdk `DECLARE_EXPORT_TABLE`).
2. Functional host API for init, params, switches (key-on/off), addresses, master volumes, voice transfer, note↔pitch.
3. Key-on path into `Spu2` (ADPCM/tone voices). **Not** a full dual-core mixer or effect DSP.

## 2. Export ordinals (ps2sdk exports.tab)

| Ord | Name | HLE |
|-----|------|-----|
| 0 | `_start` | jr ra stub |
| 1 | `_retonly` | jr ra stub |
| 2 | `sceSdQuit` | host `SdQuit` |
| 3 | `_retonly` | jr ra stub |
| 4 | `sceSdInit` | host `SdInit` → 0 |
| 5 | `sceSdSetParam` | host → Spu2 voice params |
| 6 | `sceSdGetParam` | host |
| 7 | `sceSdSetSwitch` | KON/KOFF → Spu2 KeyOn/Off |
| 8 | `sceSdGetSwitch` | host shadow |
| 9 | `sceSdSetAddr` | SSA → Spu2 SSA |
| 10 | `sceSdGetAddr` | host |
| 11–12 | CoreAttr | host shadow |
| 13–14 | Note2Pitch / Pitch2Note | equal-tempered approx |
| 15–16 | ProcBatch* | stub success |
| 17–20 | Voice/Block Trans + status | IOP↔SPU RAM HLE |
| 21–22 | callbacks | jr ra / null |
| 23–25 | effect attr / clear WA | soft reverb flag on Spu2 |
| 26–27 | intr handlers | jr ra / null |

Version **1.5** imports (StopTrans / CleanEffect / EffectMode, ordinals 30–33) are not in the 1.4 export table; callers that import 1.5 still resolve 0–27.

## 3. Entry encoding (`libsd-common.h`)

```text
SD_VOICE(core, v)     = core | (v << 1)
entry                 = (paramKind << 8) | SD_VOICE(...)
SD_SWITCH_KON         = 0x15 << 8
SD_SWITCH_KOFF        = 0x16 << 8
SD_VADDR_SSA          = 0x20 << 8
```

Helpers on host: `MakeVoiceEntry` / `MakeSwitchEntry` / `MakeAddrEntry`.

## 4. Spu2 model note

Hardware SPU2 is **2 cores × 24 voices**. DetPS2 `Spu2` models **24 voices**. Core1 voice indices alias into 0..23 for HLE. Full dual-core mix + effect DSP is intentional residual (does not block LIBSD gate **OK (core)**).

## 5. Smokes

- `LibSd_InitSetParamKeyOnContracts` — SdInit, SetParam pitch/vol, SetAddr SSA, SetSwitch KON → voice playing.
- `BiosExtendedRomdir_SecrClearSpuLibSdUdnl` — libsd export table present after commercial IOP start.
- Existing Spu2 mix smokes unchanged.

## 6. Residuals (non-blocking)

- Full dual-core 48-voice mix and true effect work-area DSP.
- Cycle-accurate transfer IRQs / DMA busy windows (HLE completes VoiceTrans immediately).
- ProcBatch opcode interpreter.
- R3000 execution of retail LIBSD.IRX bodies (export plant is jr ra; host C# is the functional path).
- Midway MSL-specific assists (**out of scope** — GameQuirks forbidden in this campaign).

## 7. Gate

| Field | Value |
|-------|--------|
| ROMDIR row | LIBSD |
| Tag | **OK** (core) — mixer residual documented above |
| Port | this file + `IopLibSdHost.cs` |
