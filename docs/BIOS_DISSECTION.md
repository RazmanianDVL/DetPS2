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
| INTRMAN/TIMEMAN/IOMAN | `IopSystemHost` | Register IRQ / time / devices |
| LOADFILE / SYSMEM / FILEIO / CDVD | `RealSifRpc` + disc IRX load / getstat/dir | RPC sid handlers |
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
