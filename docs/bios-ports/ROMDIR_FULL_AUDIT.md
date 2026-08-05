# SCPH70008 full ROMDIR functional audit

| Field | Value |
|-------|--------|
| **BIOS image** | `Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin` (4 MiB) |
| **Path (operator-local)** | PCSX2 bios dir (see `user-media*.json` `biosPath`) |
| **Method** | `RomdirExtractor` + Ghidra 12.1.2 headless (`BiosModuleDecomp.java`) on extracted IRX |
| **Date** | 2026-07-30 |
| **Verdict** | **Gate complete** for commercial IOPBTCONF set; **extended ROMDIR HLE landed** for SECRMAN/CLEARSPU/LIBSD/UDNL/X*/THREADMAN pool exports; **literal full BIOS IRX execution still not claimed** |

---

## 1. What “entire BIOS converted to C#” means here

DetPS2 does **not** execute the retail BIOS R3000/EE kernel instruction stream as a full guest. It:

1. Loads the BIOS ROM as **data** (ROMDIR / rom0 content / optional IRX ELF parse).
2. Reimplements the **service contracts** commercial titles expect (HLE).

| Claim | Status |
|-------|--------|
| All **IOPBTCONF @800 required** modules have HLE destinations | **YES** — gate **CLOSED** (`ROMDIR_GATE.md`) |
| All **101 ROMDIR names** register when BIOS is bound | **YES** — `StartCommercialIop` walks full ROMDIR |
| Extended services (SECRMAN, CLEARSPU, LIBSD, UDNL, X*) have functional HLE | **YES** — `IopExtendedBiosHost` (this pass) |
| MagicGate crypto / mechacon auth bit-accurate | **NO** — SECRMAN passthrough for plain loads |
| Literal R3000 execution of every BIOS IRX | **NO** — intentional architecture residual (#12) |
| EE KERNEL (93 KiB) as full guest OS | **NO** — EE syscalls via `SonyKernelHle` |
| OSD / PS1DRV / FONT assets as interactive firmware | **NO** — out of commercial game path |

---

## 2. ROMDIR inventory (101 entries)

Parsed from live SCPH70008 image (`RESET` @ file offset 10048).

### Boot / config (not IRX services)

| Name | Size | DetPS2 role |
|------|------|-------------|
| RESET, ROMDIR, EXTINFO, ROMVER, SBIN, LOGO | … | Data / branding |
| IOPBTCONF / IOPBTCON2 | 234 / 195 | Boot order text → `BiosBootHost.ExtractIopBtConfNames` |
| EENULL, EELOADCNF, LIBFI, PS1IDx, TZLIST, OSD*, TBIN, KROM*, VERSTR, ROMGSCRT | … | Optional / PS1 / OSD — name registration only when present |
| FONTM / FNTIMAGE / SNDIMAGE / TEXIMAGE / ICOIMAGE / PS2LOGO / OSDSYS / OSDSND / KERNEL / PS1DRV / EELOAD / TEST* | large | Assets or EE kernel image — **not** reimplemented as full guest; EE syscalls HLE'd |

### IOPBTCONF @800 (required HLE — gate CLOSED)

See `ROMDIR_GATE.md` rows 1–26 + PADMAN/SIO2MAN/EESYNC.

### Extended service modules (this audit)

| Module | Ghidra notes | C# HLE |
|--------|--------------|--------|
| **ADDDRV** | tiny; string `romdrv` | Name + IOMAN AddDrv path |
| **SECRMAN** | SecrAuthCard / SecrCardBootFile / SecrDiskBootFile / mechacon auth strings | Export table; plain ELF `SecrDiskBootFile`/`SecrCardBootFile` → 0; non-ELF → `SecrErrCannotDecrypt`; LOADFILE `MG_*` shares plain path + classify |
| **CLEARSPU** | `clearspu: completed`, SPU T/O waits, `bf90xxxx` SPU regs | `Spu2.Reset()` on install + UDNL handoff |
| **UDNL** | `IOPBTCONF`, open/panic strings; loads IOPRP image | Version ASCII + **ROMDIR-in-IMG parse** + IOPBTCONF register + LoadIrx when ELF; disc path resolve; CLEARSPU; see `UDNL.md` |
| **LIBSD** | `Sound Device Library` | Export table stubs for LinkImports |
| **X\*** / **T\*** / **NCDVDMAN** | retail X-path twins | Aliases to primary HLE modules |
| **XMTAPMAN** | `mtapman` | Name registration; multitap HW via `Sio2`/`Multitap` |
| **THREADMAN** thmsgbx/vpl/fpl | WARNING strings for ReceiveMbx / AllocateVpl/Fpl | Export libs `thmsgbx`/`thvpool`/`thfpool` planted (jr ra stubs); full object model still PARTIAL vs EE (IOP-side pools) |

---

## 3. Ghidra artifacts

| Artifact | Location |
|----------|----------|
| Extracted IRX | `tools/bios-extract/*.irx` (**local; do not commit BIOS blobs**) |
| Prior full decomp | `tools/bios-decomp/*_ALL.txt` |
| This pass | `C:\Users\user\ghidra\bios_module_audit\{SECRMAN,UDNL,CLEARSPU,LIBSD}_ALL.txt` |
| Ghidra projects | `C:\Users\user\ghidra\projects\PS2Bios.gpr`, `BiosAudit_*` |

---

## 4. Code anchors

| Piece | Path |
|-------|------|
| Contract table | `BiosBootHost.BootCriticalContracts` |
| Extended HLE host | `IopExtendedBiosHost.cs` |
| ROMDIR parse | `RomdirExtractor.cs` |
| UDNL / reboot | `BiosBootHost.ApplyPostIopRebootContracts` → `ApplyUdnlHandoff` → optional `ApplyIopRpImage` |
| IOPRP parse | `IopExtendedBiosHost.TryParseIopRpContainer` / `BuildSyntheticIopRpImage` |
| Port doc | `docs/bios-ports/UDNL.md` |
| Smokes | `BiosBootHost_IopBtConfContracts`, `BiosRomdirGate_PortDocsForRequiredModules`, `BiosExtendedRomdir_SecrClearSpuLibSdUdnl`, `BiosUdnl_IopRpImageApplyAndSecrMgPath` |

---

## 5. Remaining honest gaps (not “unimplemented by accident”)

1. **MagicGate** decrypt (SECRMAN real crypto) — requires console secrets; intentionally not faked (encrypted → clear fail).
2. **IOPRP.img unpack residual** — ROMDIR + IOPBTCONF + LoadIrx when ELF extractable is implemented; multi-image merge with full rom0 overlay and R3000 `_start` still residual.
3. **THREADMAN Mbx/Vpl/Fpl object model** on EE — EE has no CreateMbx syscalls; IOP export stubs link only. Deeper object HLE still open (#14).
4. **Literal IRX execution** (#12).
5. **OSD / browser / PS1 classic** firmware paths.
6. **INTRMAN / DMACMAN / SSBUSC** still PARTIAL (usable; not full Ghidra parity).

---

*Audit orchestrator pass 2026-07-30 — implement extended hosts + smokes; commercial gate remains CLOSED.*
