# SCPH70008 BIOS dissection (Ghidra + ROMDIR)

**Source image:** `Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin` (4 MiB)  
**Method:** `RomdirExtractor` → extract IRX ELFs → Ghidra 12.1.2 headless (`MIPS:LE:32:default`)  
**Scripts:** `C:\Users\xxraz\ghidra\scripts\BiosModuleDecomp.java`  
**Artifacts:** `tools/bios-extract/*.bin`, `tools/bios-decomp/*_ALL.txt`  
**Do not commit BIOS blobs.** Extracted modules are local diagnostics only.

This document is the **service map** every commercial title shares. Game-specific PCs are not the authority — these modules are.

---

## 1. ROMDIR inventory (101 entries)

Boot-critical IOP modules (with real ELF offsets verified by `7F ELF` search):

| Module | Size | Role (from strings / decomp) |
|--------|------|------------------------------|
| SYSMEM | 4625 | IOP heap; EE RPC **sid=0x80000003** |
| LOADCORE | 9597 | IRX load core |
| EXCEPMAN | 3033 | Exception manager |
| INTRMANP / INTRMANI | 6–7K | Interrupt managers |
| DMACMAN | 14069 | IOP DMAC |
| TIMEMAN* | ~3K | Timers |
| THREADMAN | 36225 | Threads / semas / event flags / pools |
| **VBLANK** | 3465 | IOP vblank callback lists |
| IOMAN | 8041 | Device manager |
| MODLOAD | 9025 | Module loader |
| SIFMAN | 5529 | SIF DMA transport |
| **SIFCMD** | 8753 | SIF command + **RPC** interface |
| SIFINIT | 1041 | SIF init |
| EESYNC | 1177 | EE/IOP sync |
| **LOADFILE** | 10065 | EE module load RPC **sid=0x80000006** |
| CDVDMAN / CDVDFSV | ~33K | CD manager + file service |
| FILEIO | 8437 | IOP file I/O |
| PADMAN / MCMAN / MCSERV | … | Pad / MC |
| KERNEL (EE) | 93736 | EE kernel image |

Full table: `detps2 romdir-list <bios>`.

---

## 2. IOPBTCONF — exact boot order (BIOS text)

Raw at ROMDIR naive offset of `IOPBTCONF` (not ELF-relocated):

```
@800
SYSMEM
LOADCORE
EXCEPMAN
INTRMANP
INTRMANI
SSBUSC
DMACMAN
TIMEMANP
TIMEMANI
SYSCLIB
HEAPLIB
EECONF
THREADMAN
VBLANK
IOMAN
MODLOAD
ROMDRV
STDIO
SIFMAN
IGREETING
SIFCMD
REBOOT
LOADFILE
CDVDMAN
CDVDFSV
SIFINIT
FILEIO
```

`IOPBTCON2` is a shorter alternate path ending at `CDVDMAN` (no SIFCMD/LOADFILE/FILEIO).

**Contract for DetPS2:** `BiosBootHost` must present this stack as already “up” before any commercial ELF runs. Names alone are not enough — **SIFCMD RPC_END replies** and **THREADMAN sema wake** must match the decompiled behavior below.

---

## 3. SIFCMD (Ghidra) — RPC wire truth

Module string: `IOP_SIF_rpc_interface` / `sifcmd`.

### 3.1 Init (`FUN_000006c0`)

Registers IOP command handlers (register-cmd helper `FUN_0000035c`):

| CID | Handler (mod-relative) | Role |
|-----|------------------------|------|
| **0x80000008** | `0x994` | **SIF_CMD_RPC_END** (reply complete) |
| **0x80000009** | `0xC48` | **SIF_CMD_RPC_BIND** |
| **0x8000000A** | `0xE08` | **SIF_CMD_RPC_CALL** |
| **0x8000000C** | `0xA68` | **SIF_CMD_RPC_RDATA** |

Also pokes `FUN_000004e0(0x80000001, …)` (INIT-style).

### 3.2 BIND handler (`FUN_00000c48`) — **must reply with RPC_END**

Pseudocode from decomp:

```
pkt = alloc_reply_slot(pool);
pkt[+0x14] = req[+0x14];
pkt[+0x20] = 0x80000009;          // echo BIND
pkt[+0x1c] = req[+0x1c];          // client / server cookie
server = lookup(req[+0x20] /* sid */);
if (!server) { pkt[+0x24..+0x2c] = 0; }
else {
  pkt[+0x24] = server;
  pkt[+0x28] = server[+8];
  pkt[+0x2c] = server[+0x14];
}
SendCmd(0x80000008, pkt, 0x40, 0, 0, 0);  // **SIF_CMD_RPC_END**
```

`FUN_00000524` / `FUN_000004e0` = SIF command send to EE (DMA + irq).

### 3.3 CALL completion (`FUN_000013a4`)

Runs server callback, then either:

- DMA result + **`SendCmd(0x80000008, …)`** (async), or  
- Builds multi-descriptor DMA including the RPC_END packet.

### 3.4 RPC_END handler on IOP (`FUN_00000994`)

When **EE** sends 0x8000000A-related completion traffic back:

- If cid == CALL: invoke optional server completion callback  
- If cid == BIND: write server fields into client object  
- Free EE packet slot (`FUN_0000092c`: clear bit1 in flags, zero field +0x18)

### 3.5 DetPS2 implication

EE `sceSifBindRpc` / `sceSifCallRpc` **WaitSema** on `SifRpcClientData_t.hdr.sema_id` only after the **EE-side** command handler for **0x80000008** runs.

HLE that only mutates IOP state and `SignalSema` without modeling RPC_END free semantics will desync once the EE library checks packet flags.  
Current `RealSifRpc` mirrors packet_free + SignalSema (see comments citing sifrpc.c). **BIOS confirms RPC_END is the producer** of that free+wake path.

Constants already in `RealSifRpc.cs`:

```csharp
CidRpcEnd  = 0x80000008;
CidRpcBind = 0x80000009;
CidRpcCall = 0x8000000A;
CidRpcRdata = 0x8000000C;
```

---

## 4. THREADMAN (Ghidra) — WaitSema / SignalSema

Module: `Multi_Thread_Manager` / `IOP Realtime Kernel Ver.0.9.1`.

### 4.1 WaitSema (`FUN_00003444`)

- Validates sema id magic `0x7f02` and generation bits  
- If **count (`+0x20`) < 1**:  
  - Mark current thread wait state **4**, wait object type **3** (sema)  
  - Link thread into sema wait queue (`+0x10` waiter count++)  
  - Yield (`FUN_0000046c`)  
- Else: **count--**, return success  

Warning string when poll-wait fails: `WARNING: WaitSema KE_CAN_NOT_WAIT`.

### 4.2 SignalSema (sibling ~`FUN_000033xx`)

- If waiters (`+0x10` ≠ 0): dequeue one, mark ready (state **2**), clear wait, schedule  
- Else if count < max: **count++**  
- Else error  

### 4.3 DetPS2 implication

`KernelState.WaitSemaBlocking` / `SignalSema` must preserve: **count vs waiter queue**, wake **one** waiter, no false success without count or wake.  
Partial GPR restore across threads breaks waiters that hold `v1`/`a0` mid-poll (fixed separately: full GPR save).

---

## 5. VBLANK IOP module (Ghidra)

Module: `Vblank_service` / depends `intrman`, `thbase`, `thevent`.

| Function | Role |
|----------|------|
| `FUN_00000164` | **Register** handler: `(which, priority, callback, arg)` — which 0/1 selects start vs end list |
| `FUN_000002ac` | **Unregister** by callback pointer |
| `FUN_00000374` | **Dispatch start-list**: walk list, call `callback(arg)`; if returns 0, move node to free list |
| `FUN_0000042c` | **Dispatch end-list** (same pattern) |
| `FUN_000004b4` | Enable/clear IOP IRQ bits **1,2** (vblank-related) |
| `FUN_000004fc` | Enable/clear IRQ bits **4,8** |
| `FUN_00000544` | Signal event flag (thevent) for waiters |

**This is IOP vblank**, not EE `INTC` cause 2. EE games that `AddIntcHandler(2, …)` use the **EE KERNEL** path + PCRTC → INTC.  
IOP VBLANK still matters for CDVDFSV / FILEIO / drivers that `WaitEventFlag` on vblank.

---

## 6. LOADFILE (Ghidra)

- Registers RPC **sid=0x80000006** (`FUN_000018c8(..., 0x80000006, 0x4c4, …)`)  
- Strings: `loadmodule:`, `loadelf:`, `Load File service.(99/11/05)`  
- Handlers return `{ result, modres }` style buffers (matches existing `HandleLoadFile`)
- Full real implementation is ~30 functions (`tools/bios-decomp/LOADFILE_ALL.txt`) including real
  ELF/IRX loading (`FUN_00000a48`/`FUN_00000cf4`/`FUN_000010dc`); DetPS2's `HandleLoadFile` +
  `IrxLoader.cs` achieve the same functional result (real relocation, real module registration)
  through an independently-verified path rather than a literal transliteration of this module —
  not yet reconciled line-for-line, lower priority since the existing path already works.

---

## 6.1 CDVDFSV — real SCMD/NCMD command semantics (2026-07-29 follow-up)

Ghidra-decompiled in full (`tools/bios-decomp/CDVDFSV_ALL.txt`, 2876 lines). Two real command
dispatchers, registered exactly where expected:

| sid | Dispatcher | Real cases found |
|-----|-----------|-------------------|
| `0x80000593` (SCMD) | `FUN_000041b8` | 25 (`0x1`-`0x19`) |
| `0x80000595` (NCMD) | `FUN_00003f3c` | 14 (`0x1`-`0xe`) |
| `0x80000592` | raw addr `0x204` | not yet decompiled as its own dispatcher |
| `0x80000597` | raw addr `0x2f0` | not yet decompiled |
| `0x8000059a` | `FUN_000032d8` | not yet decompiled |

Every real reply is **result word first, payload starting at word[1]** — confirmed directly from
the decompile (e.g. `*param_3 = uVar1; param_3[1] = *param_1;` for WRITE RTC). The pre-existing
`RealSifRpc.cs` handling wrote payload bytes starting at word[0] with no result word at all for
several commands, and had case 7 mislabeled `ScmdApplySCmd` — the real case 7 is WRITE_ILinkID
(confirmed via its real debug string `"WRITE ILinkID call"`). NCMD read (`fno` 1/2/3) real handlers
return the accumulated **byte count actually transferred**, not a boolean — DetPS2 previously
returned `1`/`0` regardless of sector count.

Ported into a new `HandleCdScmd` (`RealSifRpc.cs`) covering all 25 real SCMD cases with the correct
word-count/ordering shape; hardware DetPS2 doesn't model for real (mechacon RTC/NVM/iLink ID/console
ID) gets structurally-correct synthetic values rather than fabricated real console secrets. 2 new
smoke tests. Verified: full smoke suite green, 9-title cross-check byte-identical (these specific
commands aren't yet on Shaolin Monks' critical path within the tested cycle window, but are now
correct for whichever title/path does exercise them — general fix, not Shaolin-specific).

**Not yet decompiled**: the other two SCMD-family services (`0x80000592`/`0x80000597`/`0x8000059a`)
and their real dispatchers.

---

## 6.2 IOMAN — real file-descriptor table (2026-07-29)

Ghidra-decompiled in full (`tools/bios-decomp/IOMAN_ALL.txt`, 39 real functions). Confirmed: real
`sceOpen`/`sceClose`/`sceRead`/`sceWrite`/`sceLseek`/`sceIoctl`/`sceRemove`/`sceMkdir`/`sceRmdir`/
`sceDopen`/`sceDclose`/`sceDread`/`sceGetstat`/`sceChstat`/`sceFormat` all dispatch through one
16-slot file-descriptor table (`FUN_00000b98` allocates, `FUN_00000c3c` validates — bound confirmed
independently from both sides) shared between file and directory opens (`sceDopen` calls the exact
same allocator as `sceOpen`), returning real errno `-24` (EMFILE, the module's own debug string is
literally "out of file descriptors") on exhaustion. DetPS2's own fd allocator had no such bound.
Ported into `IopModuleHost.FileOpen`/`DirOpen` (`SifRpc.cs`) — real 16-slot shared-pool bound, real
exhaustion errno, verified with a new smoke test and zero cross-title regression.

**Not yet ported**: the real device-path parser (`FUN_00000d28` — colon-delimited device name with
optional trailing-digit unit-number extraction, e.g. `mc0:` → device `mc` unit `0`) and the real
`AddDrv`/`DelDrv` device-registry (`FUN_00000e8c`/`FUN_00000f44`). DetPS2 currently only special-cases
`cdrom0:`/`cdrom:` path prefixes rather than a general device registry, because it doesn't yet have
multiple distinct real backing-store implementations (host0:/mc0:/pfs0: all funnel through the same
code paths today) for a general dispatcher to route between — porting the full registry doesn't have
a behavioral payoff until that changes, so it's deliberately deferred rather than half-done.

## 6.3 SIFMAN — ground-truthed, not a literal-port candidate (2026-07-29)

Ghidra-decompiled in full (`tools/bios-decomp/SIFMAN_ALL.txt`, 27 real functions). Confirmed this
module's real job is **direct physical DMAC/SBUS register programming** — every function pokes real
IOP hardware register addresses (`0xBF8010xx`/`0xBF8015xx`/`0xBD0000xx`, the real SIF0/SIF1/SIF2 DMA
channel + SBUS control registers), not a software-level transport abstraction. DetPS2's existing
`Sif.cs` already implements the *functional result* SIFMAN exists to provide (reliable EE↔IOP byte
transport) via its own working, extensively-verified mechanism — a literal port of SIFMAN's raw
register pokes would require first building real IOP-side DMAC hardware register emulation from
scratch (mirroring what `Dmac.cs` already does for the EE side), a much bigger prerequisite with no
clear payoff over the working abstraction that already exists. Documented as ground-truthed and
deliberately not ported, rather than silently skipped.

## 6.4 THREADMAN — real scope larger than currently ported (2026-07-29)

Ghidra-decompiled in full (`tools/bios-decomp/THREADMAN_ALL.txt`, 80 real functions — the largest
BIOS module). Confirmed this is the complete real-time kernel: priority-based ready queues (not just
sema counting), message boxes (Mbx), variable/fixed memory pools (Vpl/Fpl), and — notably — **real
per-thread stack-overflow detection** (`FUN_00001cfc`/"CheckThreadStack()": compares current SP
against `thread+0x3c`'s stack-limit field with a 168-byte margin, panics if exceeded). DetPS2's
existing `KernelState` (round-robin + real sema count/waiter-queue semantics, already verified
working extensively this session) is a different, working implementation of the same contract, not a
literal port of this module — replacing it wholesale would be a large architectural risk for
uncertain payoff. The stack-overflow check specifically is a good candidate for a future, carefully
isolated addition (diagnostic-only, shouldn't affect normal execution) but needs real verification
against actual game stack-usage patterns before landing, not a rushed port.

---

## 7. What DetPS2 must implement (BIOS order, not game PCs)

1. **Present IOPBTCONF stack** as registered (names + RPC sids) before ELF entry — `BiosBootHost`.  
2. **SIFCMD:** EE→IOP BIND/CALL/**RDATA**; IOP→EE **RPC_END 0x80000008** free+wake semantics.  
3. **THREADMAN:** real sema count / single-waiter wake; PollSema; DeleteSema wakes waiters; iSignalSema.  
4. **LOADFILE 0x80000006** / **SYSMEM 0x80000003** / **FILEIO 0x80000001** / **CD 0x80000592–595**.  
5. **IOP VBLANK.IRX** callback lists + event-flag pulse on PCRTC edge (`IopVblankHost`).  
6. **EE INTC VBlankStart:** sticky STAT for software poll; COP0 edge latch so bare-`eret` does not storm (hardware, not Midway).  
7. **Full GPR context** on every thread switch (BIOS threadman switch_context saves full state).  

Optional later: execute relocated BIOS IRX on IOP R3000 (large). Until then, HLE must match **this** decompiled ABI.

### Implemented C# surface (this tree)

| Module | Class / entry | Status |
|--------|---------------|--------|
| IOPBTCONF + ROMDIR | `BiosBootHost.StartCommercialIop` | HLE destinations registered |
| SIFCMD BIND/CALL/RDATA/RPC_END | `RealSifRpc` | Full transport HLE |
| SIFCMD INIT + EE ready slots | `SonyKernelHle.AcknowledgeEeSifCmdReady` + boot plant | `0x778800` queue-ready |
| THREADMAN semas | `KernelState` + `SonyKernelHle` 0x40–0x48 | Count/wake/Poll/Delete/i* |
| VBLANK IOP | `IopVblankHost` via `BiosHle.OnVblank` | Lists + event flag |
| INTRMAN/TIMEMAN | `IopSystemHost` | Register IRQ / time — **contract only, not decompiled yet** |
| IOMAN fd table | `IopModuleHost.FileOpen`/`DirOpen` | Real 16-slot shared pool + real EMFILE errno ported (§6.2), 2026-07-29. Device registry/path-parser (`FUN_00000d28`/AddDrv/DelDrv) deliberately deferred, no payoff yet. |
| CDVDFSV SCMD/NCMD | `RealSifRpc.HandleCdScmd` + NCMD read/GetToc | Full real command-set port (§6.1), 2026-07-29 |
| SIFMAN | *(not a port target)* | Ground-truthed (§6.3) — real job is IOP DMAC/SBUS register programming; DetPS2's `Sif.cs` already implements the functional result via a different, working mechanism. |
| THREADMAN (full scheduler) | `KernelState` (different implementation) | Ground-truthed real scope (§6.4) — 80 real functions incl. priority ready-queues, Mbx/Vpl/Fpl, real stack-overflow detection; DetPS2's existing sema-count/waiter-queue implementation is a different, working contract, not a literal port. |
| LOADFILE / SYSMEM / FILEIO | `RealSifRpc` + disc IRX load / getstat/dir | RPC sid handlers (functionally equivalent, not literal transliteration — see §6) |
| PADMAN / MCSERV | `HandlePad` / `HandleMcServ` | Open/read/getInfo |
| EE INTC + full GPR | `Intc` / `KernelState.SaveFullContext` | Prior session |

### Live verify (MK Shaolin Monks SLUS_210.87)

| Config | Cycles | binds/calls | cdvd sectors | px | Notes |
|--------|--------|-------------|--------------|-----|--------|
| Pure BIOS (`--no-assist`) | 100M | 11 / 651 | 29 | 286K | SIF ready slots unblocked `sceSifInitRpc` |
| + CRI structural HLE always on | 50M | 12 / 594 | 30 | **29M** | `GAMEDATA.WAD` open, ADX gate |
| + ADXF bulk pump + host-present | 200M | 13 / 990 | **198840** (full WAD) | **77M** | PC into game `0x6B0DF0` |

Structural CRI (cvFs + ADX gate + ADXF pump) runs even with `--no-assist`; only PC-force Midway assists are suppressed.

---

## 8. Reproduce

```powershell
$bios = "...\SCPH70008.bin"
$dll  = "src\DetPS2.Core\bin\Release\net9.0\DetPS2.Core.dll"
$gh   = "$env:USERPROFILE\ghidra\ghidra_12.1.2_PUBLIC\support\analyzeHeadless.bat"

dotnet $dll romdir-list $bios
dotnet $dll romdir-extract $bios SIFCMD tools/bios-extract/SIFCMD.bin

& $gh $env:USERPROFILE\ghidra\projects BiosSif `
  -import tools/bios-extract/SIFCMD.bin `
  -processor "MIPS:LE:32:default" `
  -scriptPath $env:USERPROFILE\ghidra\scripts `
  -postScript BiosModuleDecomp.java tools/bios-decomp/SIFCMD_ALL.txt `
  -deleteProject
```

---

## 9. Bottom line

The BIOS does **not** leave “mystery destinations.”  

- **IOPBTCONF** names the modules and order.  
- **SIFCMD** defines BIND/CALL/RDATA and **replies with 0x80000008**.  
- **THREADMAN** defines WaitSema block + SignalSema wake.  
- **VBLANK** defines IOP callback lists (separate from EE INTC).  

DetPS2’s job is to re-host those contracts in C#. Further progress is “match the next BIOS function we still stub,” not “patch the next game PC.”
