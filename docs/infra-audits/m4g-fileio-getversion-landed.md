# M4-g — FILEIO GetVersion tag-if-applied **LANDED** (status)

**Date:** 2026-08-04  
**Status:** **already in Core** — do not re-implement  
**Design:** `m4g-fileio-getversion-tag-if-applied.md`  
**Code:** `RealSifRpc.cs` `case FioGetVersion` (~1932–1940)

---

## Policy (live)

```csharp
// M4-g packing: tag-if-applied (mirror LOADFILE), not PreferIopRp-gated.
int fioGv = !GetVersionClassicOverride && !string.IsNullOrEmpty(_lastIopRpVersionAscii)
    ? PackAsciiVersion(_lastIopRpVersionAscii)
    : unchecked((int)0x00020000);
```

| Axis | Gate |
|------|------|
| **Reply dword** | tag-if-applied (M4-g) — Prefer **not** consulted |
| **FILEIO-2200 arm** | still PreferIopRp + iopVer≥3000 + dual EE ptrs (unchanged) |
| **PreferSnFileIo** | disarms 2200 |

Matches LOADFILE M4-b packing rule. Comment block in Core cites design + M4-f Whip dual-suppress evidence.

---

## Residual (not M4-g)

- GoW plant (M4/M8) — separate  
- Prefer soft-off fleet — M8 audit  
- Claim-tier Whip residual cadence if still questioned  

```text
M4-g LANDED in Core
  FILEIO GetVersion packing = tag-if-applied
  no further Core for this ticket
```
