# EE LOADFILE / GetVersion surface (WP-22 prep — Track T7)

**Status:** documented current HLE path + `DETPS2_LITERAL_IRX` EE gate  
**Owned:** `SonyKernelHle.cs` (EE syscalls / SIF DMA / RPC drain), `docs/irx/EE_LOADFILE.md`  
**Related:** `RealSifRpc.HandleLoadFile` (T4), `IopExtendedBiosHost` UDNL/IOPRP (T8), `docs/bios-ports/LOADFILE.md`

---

## What retail EE code actually does

Commercial EE libraries do **not** load IOP modules via a raw EE syscall. They:

1. `sceSifInitRpc` / `sceSifBindRpc` → bind client to IOP service **sid=`0x80000006`** (LOADFILE).  
2. `sceSifCallRpc` with function number:
   - `0` `LF_F_MOD_LOAD`, `1` ELF_LOAD, `4/5` MG_*, `6` MOD_BUF_LOAD, …
   - **`0xFF` `LF_F_GET_VERSION`** — 4-byte reply (classic dword or IOPRP ASCII tag).  
3. Wire path: EE builds a **sifrpc bind/call packet** → **`sceSifSetDma`** (EE→IOP) → IOP SIFCMD/LOADFILE answers → RPC_END / client sema.

Ground truth: ps2sdk `ee/kernel/src/loadfile.c`, BIOS LOADFILE.IRX (`docs/BIOS_DISSECTION.md` §6, `docs/bios-ports/LOADFILE.md`).

---

## DetPS2 EE path today (HLE-first)

```text
EE game
  → syscall SifSetDma (SonyKernelHle 0x77)
       PerformSifSetDma
         Sif.Sif1EeToIop  (bytes land in IOP RAM — correct bus)
         HleSifCmdFromEe
           if RealSifRpc.IsRealRpcPacket → Sif.SubmitRealRpc(gen)
  → DrainRealRpcQueue (same tick for older gen; ambient tick for current gen)
       RealSifRpc.TryHandle
         HandleBind / HandleCall
           sid=0x80000006 → HandleLoadFile  **← HLE answerer, not R3000 LOADFILE.IRX**
```

| Stage | File / API | Live IOP IRX? |
|-------|------------|---------------|
| EE syscall ABI | `SonyKernelHle` `0x77` SifSetDma, `0x7x` Sif*Reg | N/A (EE kernel) |
| Packet queue | `Sif.SubmitRealRpc` / `TryDequeueRealRpc` | No — generation-gated EE-side queue |
| LOADFILE service | `RealSifRpc.HandleLoadFile` | **No** — C# HLE |
| Post-reboot contracts | `OnIopRebootCompleted` → `RealRpc.OnIopReboot(arg)` | Surfaces UDNL arg string only |
| Homebrew buffer load | `BiosHle` `SysSifLoadModuleBuffer` | Direct `IopModules.LoadIrx` (bypasses LOADFILE RPC) |

### GetVersion (`fno=0xFF`) — current policy

| Condition | Reply dword |
|-----------|-------------|
| Default (`PreferIopRpGetVersion=false`) | **`0x00020000`** classic LOADFILE placeholder |
| `PreferIopRpGetVersion=true` **and** non-empty `_lastIopRpVersionAscii` | Packed 4 ASCII chars LE (e.g. `"2430"`, `"2800"`, `"3000"`) |
| Tag source | `SifIopReset` / RESET_CMD arg → `ExtractIopRpVersionAscii` (`IOPRPxxx` / `DNASxxx`) or title assist `SetIopRpVersionAscii` |

**Why PreferIopRp is off by default:** Shaolin Monks spine A/B regressed when GetVersion always returned IOPRP digits (RPC cadence / FILEIO-2200 arming). Title assists set `PreferIopRpGetVersion` where SN/Midway gates need ASCII.

### What is *not* an EE version plant (T7)

| Mechanism | Role |
|-----------|------|
| `RealRpc.OnIopReboot(LastIopRebootArg)` | Hand off **real reset arg** into GetVersion tag store — not a fake EE RAM write |
| GameQuirks IOPRP RAM plants | **Out of T7** (debt / Block G); do not expand here |
| `ForcePlantMidwayPair` (FindAddress CRT0) | Unrelated CRT0 pair fixup — not LOADFILE GetVersion |

There is **no** EE-side `Write32` of `"3000"` / IOPRP digits in `SonyKernelHle` / `KernelHle` / `BiosHle`.

---

## `DETPS2_LITERAL_IRX=0|1` (bisect switch)

Shared helper: `IopExtendedBiosHost.IsLiteralIrxEnabled()`  
(`DETPS2_LITERAL_IRX=1` only; unset or `0` = legacy HLE-first bisect.)

| Flag | EE LOADFILE surface (T7) | Intent |
|------|--------------------------|--------|
| **`0` / unset** | Unchanged: queue → **HLE** `HandleLoadFile`; PreferIopRp only if title/assist set it | Stable smokes + commercial HLE bisect |
| **`1`** | Same HLE answerer **until** live IOP LOADFILE executes (WP-22 full). After `SifIopReset` completes with a parseable IOPRP/DNAS arg, EE **opts in** `PreferIopRpGetVersion` so GetVersion is **UDNL-arg-derived**, not the bare `0x00020000` placeholder that fights real disc version checks. Optional `PreferLiveLoadFileRpc` scaffold for future “skip HLE CALL when IOP owns sid”. | Prefer path that will hit live IOP; no GameQuirk plant |

**Do not break HLE=0 bisect:** smokes and default runs leave the env unset → classic GetVersion `0x00020000` and full HLE drain.

### Target architecture (WP-22 / G2)

```text
EE SifSetDma → IOP RAM packet
  → IOP R3000 executes LOADFILE.IRX sifrpc server
  → GetVersion / MOD_LOAD from **live** stack (modres real)
C# RealSifRpc.HandleLoadFile becomes fallback when LITERAL_IRX=0 or IRX not runnable
```

EE work remaining after prep: when IOP SIFCMD can complete bind/call, set `PreferLiveLoadFileRpc` (or equivalent) so `DrainRealRpcQueue` does not HLE-complete LOADFILE CALL packets that live IOP owns. Depends on T1/T2/T4 (WP-19/20) + T8 UDNL exec.

---

## Smoke touchpoints (must stay green)

- `RealSifRpc_LoadFileModuleElfSetGetSearch` — expects default GetVersion **`0x00020000`**
- `RealSifRpc_LoadFile_SearchStopUnloadContracts`
- `BiosUdnl_IopRpImageApplyAndSecrMgPath` (LOADFILE MG_* via HLE RPC)
- `KernelHle_*` / `SifRpc_ViaHleSyscall` / `BiosHle_SifInitEeSyncContracts`

None of these set `DETPS2_LITERAL_IRX=1`.

---

## Explicit non-goals (this track)

- No GameQuirk version RAM plants.  
- No RealSifRpc FILEIO soft-success expansion.  
- No IOP interpreter / IrxLoader ownership (T1/T2).  
- No forcing PreferIopRp under `LITERAL_IRX=0`.
