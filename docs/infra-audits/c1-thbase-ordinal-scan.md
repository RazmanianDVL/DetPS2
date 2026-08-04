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

## 4. CreateThread arg layout (disassembly at export #4)

```text
CreateThread @ 0x1C010C5C (loadBase+0xC5C):
  addiu sp,sp,-40
  addu  s1, a0, zero          ; s1 = thread param block*
  jal   ...
  lw    v0, 0(s1)             ; +0x00 attr (mask check)
  lw    v0, 16(s1)            ; +0x10 initPriority (sltiu vs 126)
  lw    v0, 8(s1)             ; +0x08 entry (must be 4-byte aligned)
```

**v1 trampoline contract (sufficient for READY peers):**

| Arg | Meaning |
|-----|---------|
| `$a0` | Pointer to thread param block in IOP RAM |
| `*(a0+0x08)` | **entry PC** (function) |
| `*(a0+0x10)` | initPriority (validate only; ignore for v1 RR) |
| stack | Prefer DetPS2 unique stack arena (C1.2); optional later: honor `*(a0+0x0C)` stack if present |

**StartThread (`$a0` = thread id):** mark that tid READY; return 0 on success.

## 5. Non-claims

- Full attr/option/gp fidelity not required for v1 READY-peer surface.  
- Does not implement trampoline this seat (ordinals + args now enough to implement next).

---

```text
thbase ordinal scan THREADMAN.irx
  CreateThread=4 StartThread=6 DeleteThread=5
  SleepThread=24 WakeupThread=25 confirmed vs THREADMAN.md
  ready for flag-gated trampoline implement
```
