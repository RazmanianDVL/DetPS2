# C1 — `thbase` ordinal ground-truth (THREADMAN.IRX export scan)

**Date:** 2026-08-04  
**Tip:** `bd3bab1`  
**Source:** `tools/bios-extract/THREADMAN.irx` via `detps2 load-irx … --scan-exports`  
**Purpose:** unblock CreateThread intercept implement — **no Core this seat**

---

## 1. Command

```powershell
dotnet exec out/scoreboard-build/DetPS2.Core.dll load-irx tools/bios-extract/THREADMAN.irx --scan-exports
```

Image: name=`Multi_Thread_Manager` loadBase=`0x1C010000` size=`0x6C94` entry=`0x1C010010`

---

## 2. `lib=thbase v1.1` — 42 exports (addresses relative to module image base)

Cross-check vs `docs/bios-ports/THREADMAN.md` FUN offsets (decomp imageBase=0):

| Ordinal | Export addr | FUN_ match (docs) | Name (contract) |
|--------:|------------:|-------------------|-----------------|
| 4 | `0x10C5C` | (CreateThread band) | **CreateThread** (standard thbase #4) |
| 5 | `0x10E10` | | **DeleteThread** |
| 6 | `0x11028` | | **StartThread** |
| 7 | `0x11118` | | ExitThread |
| 8 | `0x1123C` | | ExitDeleteThread |
| 9 | `0x112D0` | | TerminateThread |
| … | … | | … |
| **24** | **`0x1200C`** | **`FUN_0000200c` SleepThread** | **SleepThread** ✓ confirmed |
| **25** | **`0x120E4`** | **`FUN_000020e4` WakeupThread** | **WakeupThread** ✓ confirmed |
| 27 | `0x122DC` | CancelWakeup | CancelWakeupThread |
| 30–33 | … | ReferThreadStatus band | Refer* |

**SleepThread/WakeupThread ordinals 24/25 match decomp offsets exactly** → the export table is the standard Sony `thbase` layout. Therefore:

| API | **Ordinal** |
|-----|------------:|
| **CreateThread** | **4** |
| **DeleteThread** | **5** |
| **StartThread** | **6** |

(Also matches long-standing ps2sdk `thbase` export ordering.)

---

## 3. Implement notes (next Core seat)

When `DETPS2_IOP_CREATE_THREAD=1` **and** kill unset, after `LinkImports` (or as a post-pass):

1. Scan importer modules for `lib=thbase` import stubs with ordinals **4** and **6**.  
2. Patch stub `J` targets to DetPS2 HLE traps (not real THREADMAN.IRX).  
3. Trap handlers:  
   - **CreateThread** → `Iop.CreateThreadContext(entry, stackTop)` with status **DORMANT** (or READY if API returns started — ground-truth args next)  
   - **StartThread** → mark tid **READY**  
4. `DETPS2_IOP_THREADS=1` alone: **no patch** (real THREADMAN continues).

Artifacts: `out/canaries/c1-thbase-ordinal-scan/out.txt`

---

## 4. Non-claims

- Argument layouts (ee_thread / stack size in `$a0`) still need a short decompile pass before implement.  
- Does not implement trampoline this seat.

---

```text
thbase ordinal scan THREADMAN.irx
  CreateThread=4 StartThread=6 DeleteThread=5
  SleepThread=24 WakeupThread=25 confirmed vs THREADMAN.md
  ready for flag-gated trampoline implement
```
