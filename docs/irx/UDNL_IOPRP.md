# UDNL / disc IOPRP — ApplyIopRpImage (Track T8, WP-25 prep)

**Owned:** `src/DetPS2.Core/IopExtendedBiosHost.cs`  
**Related:** `docs/bios-ports/UDNL.md`, epic #12, `docs/IRX_EXECUTION_PHASE_PLAN.md` WP-25  
**Date:** 2026-07-30

## Real hardware contract

1. EE `SifIopReset("rom0:UDNL cdrom0:\\…\\IOPRPxxx.IMG;1")` (or `DNAS*.IMG`).
2. UDNL opens the image, finds **IOPBTCONF** (or ROMDIR module list), **loads and starts** each listed IRX in order.
3. LOADFILE clients then `LF_F_GET_VERSION` and strcmp the 4-byte reply against IOPRP digits (`"2340"`, `"3000"`, …).

DetPS2 must eventually match step 2 with **LoadIrx + IOP R3000 `_start`**, not name-table presence alone.

---

## `ApplyIopRpImage` — what it does today

Entry points:

| API | Caller | Host counters |
|-----|--------|---------------|
| `ApplyIopRpImage(sys, image, sourceName)` | UDNL handoff, smokes | updates `IopRpImagesApplied`, `LastIopRp*` |
| `ApplyIopRpImageBytes(modules, mem, image, …)` | LOADFILE `MOD_LOAD` of `IOPRP*.IMG` / `DNAS*.IMG` | local only (`elfsLoaded` out) |

Core path (`ApplyIopRpImageCore`):

1. `TryParseIopRpContainer` — ROMDIR (`RESET` @ 0 or BIOS-style scan).
2. `ExtractIopBtConfNamesFromImage` — IOPBTCONF/IOPBTCON2 text; skip `@` directives.
3. Else: all non-meta ROMDIR names.
4. For each name: **`RegisterModule(key, systemResident: true)`** (always).
5. Optionally: extract plain ELF (`0x7F ELF`) → **`IopModuleHost.LoadIrx`** → record `IopRpLoadedEntry`.

### Critical historical behavior: name-only commercial handoff

Before WP-25 prep, **UDNL commercial handoff always skipped LoadIrx**:

```csharp
// ApplyUdnlHandoff — legacy (LITERAL_IRX off)
_iopRpNameOnlyApply = true;
try { ApplyIopRpImage(sys, image, src); }
finally { _iopRpNameOnlyApply = false; }
```

And in core:

```csharp
// Name-only → RegisterModule only; no LoadIrx, no Entry/LoadBase image upgrade
bool loadElfs = !_iopRpNameOnlyApply && !IsIopRpNameOnlyForced();
// foreach name: RegisterModule; if (!loadElfs) continue; else LoadIrx(...)
```

So retail disc images often produced **name-table registrations only** — modules looked “loaded” to EE probes / LOADFILE search, but:

- No IRX bytes in IOP RAM for those names (unless already HLE-stubbed).
- No `LoadedIrx.HasImage` / real `Entry` from the disc container.
- No path to IOP R3000 `_start` (WP-08+ exec).

Direct smoke callers (`ApplyIopRpImage` without the handoff flag) and LOADFILE `ApplyIopRpImageBytes` already LoadIrx’d extractable ELFs.

---

## Load policy after WP-25 prep

| Condition | Behavior |
|-----------|----------|
| `DETPS2_IOPRP_NAME_ONLY=1` | **Always name-only** (emergency bisect; overrides everything). |
| `DETPS2_LITERAL_IRX=1` | **LoadIrx** extractable ELFs for **all** apply paths, including commercial UDNL handoff. Records `LastIopRpLoadedEntries`. |
| Neither (default / `LITERAL_IRX=0`) | Direct `ApplyIopRpImage` / `ApplyIopRpImageBytes`: LoadIrx. UDNL handoff: **name-only** (legacy HLE-first). |
| `DETPS2_UDNL_SKIP_IMAGE=1` | Skip image apply entirely (diagnostic); still registers soft name list. |

Helpers: `IopExtendedBiosHost.IsLiteralIrxEnabled()`, `IsIopRpNameOnlyForced()`.

### Entry recording for execution

On each successful `LoadIrx`, host records:

```text
IopRpLoadedEntry { Name, Entry, LoadBase, Size, ModuleId }
```

Exposed as `LastIopRpLoadedEntries` / count via `LastIopRpElfsLoaded`.

Additionally, under `DETPS2_LITERAL_IRX=1`, `IopModuleHost.LoadIrx` sets **pending literal entry**
(`HasPendingLiteralEntry` / last module id+phys PC) so T0/Ps2System can arm `Iop.PC`
(`TryArmPendingLiteralEntry`) or smokes can call `StartLoadedModule` (Block B).

`DETPS2_TRACE_BIOS=1` logs each ELF: name, id, entry, base, size.

---

## Call graph

```text
SifIopReset("rom0:UDNL …IOPRP….IMG")
  → BiosBootHost.ApplyPostIopRebootContracts
    → IopExtendedBiosHost.ApplyUdnlHandoff
      → TryResolveUdnlImageBytes (ISO/FILEIO)
      → ApplyIopRpImage
         → ApplyIopRpImageCore
            → RegisterModule (every IOPBTCONF name)
            → [if loadElfs] ExtractEntryElf + LoadIrx + LastIopRpLoadedEntries

LOADFILE LF_F_MOD_LOAD of IOPRP*.IMG / DNAS*.IMG
  → ApplyIopRpImageBytes (same core; respects NAME_ONLY + LITERAL_IRX)
```

---

## Smokes

| Test | Expectation |
|------|-------------|
| `BiosUdnl_IopRpImageApplyAndSecrMgPath` | Synthetic image: ≥2 ELFs via direct `ApplyIopRpImage`; disc UDNL bumps `IopRpImagesApplied`; SECR/MG paths. **Must keep passing.** |
| Future (WP-25 exit) | Under `DETPS2_LITERAL_IRX=1`, commercial handoff of synthetic/disc image: `LastIopRpElfsLoaded ≥ 1` and `LastIopRpLoadedEntries[i].Entry != 0`; later log IOP insn in module text. |

No new smoke required for this prep commit if existing BiosUdnl_* stay green. Recommend orchestrator add a LITERAL_IRX=1 assert on handoff when Block B exec is ready.

---

## Residuals (not this prep)

1. **IOP R3000 `_start`** of disc IRX — LoadIrx + entry recorded; actual PC run is T1/T2 (WP-08+).
2. **MagicGate** encrypted modules — honest SECR fail; no secrets.
3. **Full multi-image merge** with rom0 BIOS overlay as real UDNL.
4. **Title EE RAM version plants** — GameQuirks; generic path is GetVersion ASCII from handoff.

## Gate note (WP-25)

Exit test: *“UDNL applies disc IOPRP image with LoadIrx+exec (not name-only register)”* — this prep wires **LoadIrx + entry record under `DETPS2_LITERAL_IRX=1`**. Full **exec** waits on IOP step quanta (WP-08/11).
