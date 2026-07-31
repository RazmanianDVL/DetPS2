# Module runtime — LoadIrx, registry, literal start (WP-04 / WP-07 / WP-08)

**Track:** T2 (IRX loader / module runtime)  
**Owned code:** `IrxLoader.cs`, `IopModuleHost` / `LoadedIrx` in `SifRpc.cs`  
**Related:** `docs/IRX_EXECUTION_PHASE_PLAN.md`, `docs/bios-ports/MODLOAD.md`

---

## How `LoadIrx` works today

```text
EE / LOADFILE / BiosBootHost
        │
        ▼
IopModuleHost.LoadIrx(elf, mem, name?)
        │
        ├─1─ IrxLoader.Load(elf, mem, nextIopBase)
        │      • ELF32 LE; section path (real IRX REL) or PT_LOAD-only (fixtures)
        │      • Place SHF_ALLOC / PT_LOAD into IOP RAM (EE map 0x1Cxxxxxx)
        │      • Apply R_MIPS_26 / HI16 / LO16 when present
        │      • Parse .iopmod → name, gp, version
        │      • Return Entry, Gp, LoadBase, Size, ModuleName
        │
        ├─2─ Register / upgrade LoadedIrx in module table
        │      • Entry, Gp, LoadBase, Size, Name, HasImage=true, State=Loaded
        │
        ├─3─ LOADCORE link
        │      • ScanExports over [LoadBase, LoadBase+Size)
        │      • LinkImports against ExportRegistry (prior modules)
        │
        ├─4─ HLE StartModule(id)  →  State=Started, LastModRes=MODULE_RESIDENT_END
        │      (registry / LOADFILE “module is up” probes — no R3000)
        │
        └─5─ If DETPS2_LITERAL_IRX=1: record pending literal entry
               (id, IOP-phys PC, gp) for TryArmPendingLiteralEntry
```

### Addresses

| Field | Storage | IOP core use |
|-------|---------|----------------|
| `LoadedIrx.Entry` / `LoadBase` | EE-mapped `0x1Cxxxxxx` | Convert with `IopModuleHost.ToIopPhys` before `Iop.PC` |
| `Iop.PC` / `SystemMemory.IopRead32` | IOP physical `0x00000000–0x001FFFFF` | Same chip as EE `0x1Cxxxxxx` window |

Feeding EE-mapped addresses into `Iop.PC` fetches **zeros** (wrong bus numbering). Always convert.

### Historic gap (the reason this doc exists)

Until WP-07/08, **nothing ever started the IOP PC at module entry**.  
`LoadIrx` planted real bytes + linked imports, then `StartModule` only flipped a C# enum.  
`Iop.Step` still ran (scheduler-registered), but from BIOS reset PC `0xBFC00000` / idle — **never** from a loaded IRX `_start`.

That is the root of “HLE plant waves”: services were re-implemented in C# because the real IRX never ran.

---

## Runnable module context (WP-07)

After Load+Link, each image-backed entry records:

| Field | Meaning |
|-------|---------|
| `Name` | Normalized module name (uppercase, device/version stripped) |
| `Entry` | EE-mapped entry VA |
| `Gp` | From `.iopmod` (0 for PT_LOAD fixtures) |
| `LoadBase` | EE-mapped text/base |
| `Size` | Loaded extent (scan window floor 0x1000) |
| `HasImage` | Real bytes in IOP RAM |
| `EntryExecuted` | Set true after a successful `StartLoadedModule` with insn &gt; 0 |
| `LastEntryInstructions` | Insn count of last literal start |

### APIs

| API | Role |
|-----|------|
| `GetModuleTable()` / `GetModuleIdList` | Full MODLOAD-style table |
| **`GetRunnableModules()`** | `HasImage && Entry != 0` — candidates for R3000 start |
| **`StartModule(id)`** | HLE only: mark Started / return `modres` (no R3000) |
| **`PrepareModuleEntry(iop, id)`** | Set PC/GP/RA/SP/a0/a1 for entry; no step |
| **`StartLoadedModule(system, id, maxInsns?)`** | Prepare + step until return sentinel or budget |
| **`TryArmPendingLiteralEntry(iop)`** | Apply last LoadIrx pending entry (LITERAL_IRX=1) |
| `ToIopPhys(addr)` | EE-map → IOP physical |

### `StartLoadedModule` contract

1. `PrepareModuleEntry`:  
   - `PC = ToIopPhys(Entry)`  
   - `$gp` = `Gp` if non-zero  
   - `$ra` = `ModuleReturnSentinel` (`0xBEE0`)  
   - `$sp` = `DefaultModuleStack` (`0x001FF000`)  
   - `a0/a1` = 0 (argc/argv)  
2. `Iop.Step` in chunks until:
   - `PC == ModuleReturnSentinel` (module did `jr ra`), or  
   - instruction budget exhausted, or  
   - `Iop.Running == false`  
3. `LastModRes` ← `v0` (`$2`)  
4. Metrics: `ModuleEntryInstructions`, `ModuleEntryRuns`, per-module `LastEntryInstructions`

Synthetic fixture `IrxLoader.BuildMinimalIrx` is `jr ra; nop` — returns in a few instructions (smoke **`Irx_ExecutesMinimal`**).

---

## `DETPS2_LITERAL_IRX` path

| Value | Behavior |
|-------|----------|
| unset / not `1` | LoadIrx = load + link + HLE Start only (legacy bisect-friendly) |
| **`1`** | Same, **plus** pending literal entry recorded for arming |

`StartLoadedModule` is **always** available (not env-gated) — preferred for tests and explicit starts.

### T0 handoff (Ps2System.RunFor — not owned by T2)

T2 does **not** edit `Ps2System.cs`. When LITERAL_IRX scheduling is wired (WP-11):

```csharp
// Suggested in Ps2System.RunFor / IOP quantum (T0):
if (IopModuleHost.IsLiteralIrxEnabled && IopModules.HasPendingLiteralEntry)
    IopModules.TryArmPendingLiteralEntry(Iop);
// existing Scheduler already Steps Iop with EE
```

Until T0 wires this, smokes call `StartLoadedModule` directly.

---

## What is still HLE

- Name-only `RegisterModule` (InitDefaults FILEIO/PADMAN/…) — no image  
- `StartModule` without `StartLoadedModule` — soft resident  
- FILEIO/PADMAN RPC bodies in `RealSifRpc` until disc/BIOS IRX entries actually own those SIDs (later WPs)  
- Import stubs for missing libraries → `jr ra` (LOADCORE-compatible safe stub)

---

## Exit tests

| WP | Smoke / check |
|----|----------------|
| WP-04 | This design note |
| WP-07 | `GetRunnableModules` non-empty after LoadIrx; Entry/Gp/LoadBase/Size/Name set |
| WP-08 | **`Irx_ExecutesMinimal`**: Load `BuildMinimalIrx` → `StartLoadedModule` → `InstructionsExecuted > 0`, prefer `ReturnedToSentinel` |

Metrics to report: `Iop.InstructionsExecuted` (delta) and/or `ModuleRunResult.InstructionsExecuted` / `IopModuleHost.ModuleEntryInstructions`.

---

## Files

| Path | Change |
|------|--------|
| `src/DetPS2.Core/IrxLoader.cs` | Load / relocate / export-import (unchanged contract; Entry/Gp/Size already returned) |
| `src/DetPS2.Core/SifRpc.cs` | `LoadedIrx` run context, `GetRunnableModules`, `StartLoadedModule`, pending literal arm |
| `docs/irx/MODULE_RUNTIME.md` | This document |
| `Tests/SmokeTests.cs` | `Irx_ExecutesMinimal` |
