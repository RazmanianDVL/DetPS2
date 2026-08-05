# SCPH70008 BIOS dissection (Ghidra + ROMDIR)

**Source image:** `Sony PlayStation 2 BIOS (E)(v2.0)(2004-06-14)[SCPH70008].bin` (4 MiB)  
**Method:** `RomdirExtractor` → extract IRX ELFs → Ghidra 12.1.2 headless (`MIPS:LE:32:default`)  
**Scripts:** `C:\Users\user\ghidra\scripts\BiosModuleDecomp.java`  
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

## 6.5 LOADCORE — real cross-module import/export linking, fully ported (2026-07-29)

**This closes out IRX Phase 2 for real** (the earlier session-45 task tracker entry marked it
"completed" via a scope reduction — extract+relocate and let the real LOADCORE module link itself —
before literal LOADCORE execution was judged out of scope; this section supersedes that with an
actual, verified port of the real algorithm instead).

Ghidra-decompiled LOADCORE.IRX ("Module Manager") in full. No debug strings to lean on this time —
identified the real relocation processor (`FUN_0000165c`) by its case-4 logic (`(*puVar7 & 0x3ffffff)*4
+ base) >> 2` — an exact byte-for-byte match for this project's own already-verified R_MIPS_26
handling from IRX Phase 1 — which is itself a strong independent confirmation that Phase 1's
relocation work was correct. Its case '\x02' (`*puVar7 = *puVar7 + iVar11`, a plain full-address add)
identified a relocation type (R_MIPS_32) this project's loader had *not* previously handled — added.

Found the real cross-module linker by searching for `0x41E00000`, the exact import-stub-table magic
already known from ground-truthing real disc IRX files earlier this session
(`MKSM_IOP_RPC_PROTOCOLS.md`'s "unlinked stubs are `jr ra` + `addiu zero,zero,ORDINAL`" note).
`FUN_00001064` is the real resolver. Exact algorithm, transliterated directly:

- **Export table** (a module registers one per library it provides — e.g. THREADMAN registers
  separate tables for `thbase`/`thevent`/`thsemap`/`thmsgbx`/`thfpool`/`thvpool`/`thrdman`):
  `+0x00` magic `0x41C00000`, `+0x04` next (runtime-only), `+0x08` version (u16, high byte = major),
  `+0x0C` name (8 bytes), `+0x14` NUL-terminated array of real function pointers.
- **Import stub** (one unresolved call site): 2-word pair, `[0]` placeholder, `[1]`
  `addiu zero,zero,ORDINAL` (opcode 9 marks it unresolved; the ordinal indexes the target
  library's export array).
- **Resolution**: for each stub whose word[1] is still an ADDIU, if the ordinal is in range, patch
  word[0] to a real `J exports[ordinal]` instruction; otherwise patch to `jr ra` (safe no-op for
  an export the library doesn't actually provide at this version).

**Ported into `IrxLoader.cs`** (`ScanExports`/`LinkImports`) and wired into `IopModuleHost.LoadIrx`
so every module load automatically registers its exports and resolves its own imports against
everything loaded so far — real boot order matters exactly as it does on real hardware.

**Verified against real extracted BIOS modules first** (`load-irx --scan-exports`), not just
synthetic data: SYSMEM → `sysmem` v1.1 (16 funcs), LOADCORE → `loadcore` v1.1 (25 funcs), INTRMANP/I →
`intrman` v1.2 (32 funcs), VBLANK → `vblank` v1.1 (10 funcs), IOMAN → `ioman` v1.2 (25 funcs), MODLOAD
→ `modload` v1.1 (16 funcs), SIFMAN → `sifman` v1.1 (36 funcs), SIFCMD → `sifcmd` v1.1 (32 funcs), and
**THREADMAN → all 7 real libraries at once** (`thbase`/`thevent`/`thsemap`/`thmsgbx`/`thfpool`/
`thvpool`/`thrdman`) — every name and function-pointer address is real, correctly-relocated data read
straight out of the real BIOS.

**Found and fixed a second, independent real bug along the way**: THREADMAN's real loaded size
(0x6C94, confirmed live) is nearly 2x the fixed 0x4000 (16KB) per-module spacing
`IopModuleHost.LoadIrx` used for its next-module allocation address — any module loaded right after
a module bigger than 16KB would have silently overwritten its tail (which is exactly where
THREADMAN's own export tables live). Added a real `Size` field to `IrxLoader.LoadResult` (the
highest section end, not a guess) and made the next-module base advance past the real size,
16KB-aligned, instead of a fixed stride.

New synthetic smoke test (`IrxLoader_LinkImports_PatchesRealStubFormat`) builds the real table
formats directly in memory and verifies both the in-range J-instruction patch and the out-of-range
jr-ra fallback, including reconstructing the real MIPS J-type target (top 4 bits from the executing
PC) to confirm it lands exactly on the intended function. Full smoke suite green; 9-title
cross-check byte-identical to baseline.

## 6.6 EXCEPMAN — real exception-handler registry (2026-07-29)

Ghidra-decompiled in full (`tools/bios-decomp/EXCEPMAN_ALL.txt`, 14 real functions). Real
per-exception-code (16 codes, `0x0`-`0xf`), priority-ordered handler chain — `RegisterExceptionHandler`/
`RegisterPriorityExceptionHandler`/`ReleaseExceptionHandler`, real result codes read directly off the
decompile (`-50` invalid excCode, `-51` not found). Confirmed architecturally distinct from INTRMAN:
EXCEPMAN handles synchronous CPU exceptions (syscall, address error, TLB miss), INTRMAN handles
asynchronous hardware interrupts.

Ported into new `IopExcepManHost.cs`, wired into `Ps2System`. Bookkeeping only — like the LOADCORE
linking work, nothing in this project executes real R3000A BIOS code yet, so nothing currently
dispatches through the chain this builds, but a module's real registration call now succeeds/fails/
orders exactly as it would on real hardware.

**SIFINIT and EESYNC checked and found not worth a dedicated port**: both are tiny (3 and 5 real
functions respectively) thin bootstrap wrappers around external SIFMAN/EE-sync calls with no
meaningful internal state or logic of their own.

## 6.7 MCSERV — real function-number range was completely wrong (2026-07-29)

**A real, high-value bug, not just missing coverage.** Ghidra-decompiled MCSERV.IRX in full and found
its real RPC dispatcher (`FUN_0000655c`, found via the module's version string
`"PsIImcserv 1.30"`'s containing region) switches on **`0x70`-`0x80`** (17 real cases) — completely
different from the `0x00`-`0x14` range `HandleMcServ` had assumed with no real-source citation. This
means **every real MCSERV call from any title was previously falling through to the generic
"unmapped → return 0" default**, regardless of what the game actually asked for — the exact same class
of mistake as CDVDFSV's mislabeled case 7 (§6.1), just undiscovered until this pass because nothing
had gone back to check `HandleMcServ`'s assumptions against a real decompile before.

Confirmed two cases with high structural confidence — `0x73` (`FUN_000003e4`, a buffer/size read loop
over a low-level read primitive) and `0x74` (`FUN_00000624`, the same loop shape over a paired
low-level write primitive) — and remapped them to this file's existing read/write logic (real
save-data load/save is the highest-value MCSERV path). The other 15 real case numbers are confirmed
present in the real dispatcher but not individually mapped to specific semantics without further
decompile work (open/close/seek/flush/mkdir/format/delete/getdir/geticon and others); they fall
through to the service's existing 0-for-unmapped convention rather than guessing wrong-but-plausible
per-case behavior for each.

New smoke test confirms both that real `fno=0x74` now actually transfers data and that the old,
wrongly-assumed `fno=0x06` (never a real MCSERV case number) correctly does *not* match. Full smoke
suite green; 9-title cross-check byte-identical (no tracked title exercises memory card I/O within
the tested cycle window yet, but this is now correct for whichever title/path does).

**Not yet done**: individually verify the remaining 15 real MCSERV case numbers (see §6.8 for why
MCMAN itself is not a near-term port target for these).

---

## 6.8 MCMAN — real card-format backend, scoped out for now (2026-07-29)

Extracted and fully Ghidra-decompiled (`tools/bios-decomp/MCMAN_ALL.txt`, 151 functions, no dump-cap
truncation). Confirmed this is genuine low-level PS1/PS2 dual-format memory-card filesystem code, not
a thin RPC shell: per-port state structs on a `0x180`-byte stride, `0x2000`-byte sector I/O
(`FUN_00002e10` reads a real sector count via `0x2000 / <bytes-per-page>` and loops a real low-level
sector-read primitive `FUN_000059a0`), a versioned superblock write (literal string `"1.1.0.0"`), and
a large body of what is very likely ECC/wear-leveling/directory-table logic given the function count
and size distribution (several 1-2KB functions consistent with FAT-style directory/cluster
management).

**Scoped out, not abandoned.** This is a legitimate, self-contained subsystem (real card image
persistence) but porting it wholesale is disproportionate to any currently known blocking symptom —
`HandleMcServ`'s existing zero-fill-read / echo-size-write stub already returns plausible success
values, which is sufficient for any boot path that only checks "did the call succeed," and no
tracked title in the 9-title cross-check exercises deep card I/O within the tested cycle window. A
real port here is a dedicated future feature (actual save-data persistence to a host file), not a
playability blocker for the currently tracked titles — revisit if a specific title's stall is proven
to be inside real MCMAN logic, not just the RPC dispatch layer already fixed in §6.7.

XMCMAN/XMCSERV (extended/multi-card variants, larger: 80KB/5.8KB) were found in the ROMDIR listing
but not extracted — same reasoning applies, lower priority still since XMC is PS2-specific expansion
hardware most titles don't touch.

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
| **LOADCORE cross-module linking** | `IrxLoader.ScanExports`/`LinkImports`, wired into `IopModuleHost.LoadIrx` | **Full real port** (§6.5), 2026-07-29 — real export-table/import-stub format, real J-instruction stub patching, verified against real extracted BIOS modules (SYSMEM/THREADMAN/SIFMAN/SIFCMD/etc. all produce correct real library names+function pointers). Also fixed a real module-spacing overlap bug found along the way. |
| EXCEPMAN | `IopExcepManHost` | Real per-excCode priority-ordered handler registry (§6.6), 2026-07-29 — bookkeeping only, inert until real IOP execution. |
| PADMAN RPC | `RealSifRpc.HandlePad` | Already real (confirmed 2026-07-29 against a fresh decompile) — the prior session had already ground-truthed all 15 real `0x800001xx` cases; no gap found. |
| **MCSERV RPC** | `RealSifRpc.HandleMcServ` | **Real fno range was completely wrong** (§6.7), 2026-07-29 — every real call previously missed every case (assumed `0x00-0x14`, real is `0x70-0x80`). Fixed the two highest-value cases (real read/write); 15 remaining real case numbers confirmed but not individually mapped yet. |
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
