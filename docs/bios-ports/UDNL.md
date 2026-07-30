# UDNL port — IOPRP/DNAS image apply (Phase 3)

**Agent:** AGENT-U (BIOS-only)  
**Date:** 2026-07-30  
**Surface:** `IopExtendedBiosHost` + `BiosBootHost.ApplyPostIopRebootContracts`

## Authority

| Source | Status |
|--------|--------|
| `tools/bios-extract/UDNL.irx` | Present (local extract; do not commit BIOS blobs) |
| Ghidra `UDNL_ALL.txt` | Strings: `IOPBTCONF`, `file '%s' can't open`, `panic ! '%s' not found` |
| Retail IOPRP*.IMG | ROMDIR-in-IMG (`RESET\0` @ offset 0); verified on `ioprp234.img` |
| `RomdirExtractor` | Shared 16-byte entry parse (same as BIOS ROMDIR) |
| ps2tek / Woon Yung UDNL notes | Reboot arg `rom0:UDNL <image>` |

## Real contracts

1. EE issues `SifIopReset("rom0:UDNL cdrom0:\\…\\IOPRPxxx.IMG;1")` (or DNAS*.IMG).
2. UDNL opens the image, finds **IOPBTCONF** (image or merged ROM set), loads listed IRX in order.
3. LOADFILE clients then `LF_F_GET_VERSION` and strcmp the 4-byte reply against IOPRP digits (`"2340"`, `"3000"`, …).

## DetPS2 HLE

| Step | Implementation |
|------|----------------|
| Version tag | `RealSifRpc.ExtractIopRpVersionAscii` / `OnIopReboot` → GetVersion when `PreferIopRpGetVersion` |
| Handoff entry | `BiosBootHost.ApplyPostIopRebootContracts` → `IopExtendedBiosHost.ApplyUdnlHandoff` |
| Image path | `ExtractUdnlImagePath` from reboot arg |
| Bytes | `IopModuleHost.ReadDiscFileBytes` (ISO/FILEIO) |
| Container parse | `TryParseIopRpContainer` — ROMDIR table (`RESET` @ 0 or BIOS-style scan) |
| IOPBTCONF | `ExtractIopBtConfNamesFromImage` — skip `@` directives |
| Register | Every IOPBTCONF name (or all non-meta ROMDIR names if no conf) |
| LoadIrx | When entry extracts as plain ELF (`0x7F ELF`) |
| Fallback | No image bytes → soft-register `UdnlImageModuleNames` + version token |
| CLEARSPU | Re-run after apply |

### Synthetic image builder

`IopExtendedBiosHost.BuildSyntheticIopRpImage(btconf, elfModules)` builds a minimal ROMDIR-in-IMG for smokes (RESET/ROMDIR/EXTINFO/IOPBTCONF + optional IRX ELFs).

## LOADFILE interaction

- `LF_F_MOD_LOAD` of `IOPRP*.IMG` / `DNAS*.IMG` calls `ApplyIopRpImageBytes` (same parser).
- MG_* paths do **not** decrypt MagicGate; see `LOADFILE.md` + SECRMAN section in `ROMDIR_FULL_AUDIT.md`.

## Smokes

| Test | Covers |
|------|--------|
| `BiosExtendedRomdir_SecrClearSpuLibSdUdnl` | Version handoff + module names + SECRMAN export |
| `BiosUdnl_IopRpImageApplyAndSecrMgPath` | Synthetic image parse, IOPBTCONF, LoadIrx, disc UDNL apply, SECR plain/encrypted, MG_MOD_LOAD |

## Residuals (intentional)

1. **MagicGate crypto** — SECRMAN refuses non-ELF with clear errno; no console secrets.
2. **R3000 `_start` of loaded image IRX** — LoadIrx plants bytes + export scan; no IOP CPU exec.
3. **Full multi-image merge** with rom0 BIOS modules as real UDNL does — HLE applies the disc image when present; BIOS contracts already installed by `StartCommercialIop`.
4. **Title EE RAM version plants** — GameQuirks only; generic path is LOADFILE GetVersion ASCII.

## Gate

| Module | Tag | Notes |
|--------|-----|-------|
| UDNL | **OK** | Image apply + version + smokes; residual = MG crypto / R3000 exec only |
| SECRMAN | **PARTIAL** | Plain passthrough OK; encrypted honest fail; no mechacon crypto |
