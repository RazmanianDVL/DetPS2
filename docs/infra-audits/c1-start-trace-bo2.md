# C1 module `_start` TRACE — Blood Omen 2 (name the registerRpc barrier)

**Date:** 2026-08-04  
**Tip:** `f2207fb`+  
**Budget:** 50M blocker-trace  
**Media:** `user-media-bloodomen2.json`  
**Env:** `DETPS2_IOP_THREADS=1` `DETPS2_IOP_REAL_RPC=1` `DETPS2_TRACE_BTCONF_STEP=1` `DETPS2_TRACE_LITERAL_IRX=1` `DETPS2_TRACE_REALRPC=1`  
**Mode:** measurement only — **existing TRACE flags only, no Core edits**  
**Prior:** `c1-registerrpc-trace-bo2.md` (empty `firstQueue`)

---

## 0. One-line barrier

**Disc modules that should grow the live RPC table never finish `_start` under budget:**  
`IOPFILE` and `SDRDRV` both **`hit budget 100000` with `ret=False`**.  
IOPRP re-start path **SKIPs HLE-owned** modules including **SIFCMD** (no second real `_start` of the RPC host). Live queue stays empty — consistent with empty `firstQueue` TRACE.

---

## 1. Command

```powershell
$env:DETPS2_IOP_THREADS="1"; $env:DETPS2_IOP_REAL_RPC="1"
$env:DETPS2_TRACE_BTCONF_STEP="1"; $env:DETPS2_TRACE_LITERAL_IRX="1"
$env:DETPS2_TRACE_REALRPC="1"
dotnet exec out/scoreboard-build/DetPS2.Core.dll blocker-trace user-media-bloodomen2.json `
  --cycles=50000000 --host-present 2> out/canaries/c1-start-trace-bo2/err.txt
```

Wall ~7.3 s, EXIT=0. Artifacts: `out/canaries/c1-start-trace-bo2/`

---

## 2. IOPBTCONF walk (R3000 `_start` outcomes)

| Module | Outcome (TRACE) |
|--------|-----------------|
| SYSMEM | returned sentinel (635 insn) |
| **LOADCORE** | **resident spin** after **50000** insn @ `pc=0x000145D4` |
| EXCEPMAN…HEAPLIB | mostly returned sentinel |
| **EECONF** | **hit budget 50000** |
| THREADMAN | returned (15235) |
| IOMAN…ROMDRV…STDIO…SIFMAN | returned |
| IGREETING | returned |
| **SIFCMD** | **boot quanta resident (IRQ wait)** after **50000** @ `pc=0x0005CEB8` |
| REBOOT…LOADFILE…CDVD* | returned |
| **SIFINIT** | **boot quanta resident (IRQ wait)** after **50000** @ same `pc=0x0005CEB8` |
| **FILEIO** | returned (27089) |

Boot summary: `r3000exec=27` `r3000insns=327790` `fail=0`.

**Note:** SIFCMD “resident IRQ wait” is a known boot quanta pattern — not by itself proof registerRpc failed (FILEIO still returned). Live queue emptiness still tracks **later disc IRX** and/or incomplete SIFCMD worker setup under single-budget start.

---

## 3. IOPRP re-start (after `cdrom0:\IOPRP234.IMG`)

All extractable IOPRP modules that match HLE-owned names are **SKIP**:

```text
[BIOS] IOPRP StartLoadedModule SKIP hle-owned name=SIFCMD
… FILEIO, LOADFILE, THREADMAN, IOMAN, MODLOAD, CDVD*, …
[BIOS] IOPRP StartLoadedIopRpModules started=0 skipHle=15/15 r3000insns=0
```

**Implication:** reboot-gen IOPRP path does **not** re-run real SIFCMD `_start` (HLE ownership wins). Live `sceSifRegisterRpc` growth cannot come from this re-start path for HLE-owned servers.

---

## 4. LOADFILE / disc modules (the smoking gun)

```text
[LOADFILE] StartLoadedModule SKIP hle-owned name=MCMAN / MCSERV / PADMAN
[LOADFILE] StartLoadedModule name=IOPFILE id=100 ok=True insns=100000 modres=0 (v0=0 ret=False)
  msg=hit budget 100000 after 100000 insn
[LOADFILE] StartLoadedModule name=SDRDRV id=99 ok=True insns=100000 modres=0 (v0=0 ret=False)
  msg=hit budget 100000 after 100000 insn
```

| Module | Budget | Returned? | Likely role |
|--------|-------:|-----------|-------------|
| **IOPFILE** | 100000 | **No** | Parent design’s example: disc IRX that **spawns workers / yields** before registerRpc |
| **SDRDRV** | 100000 | **No** | Sound driver; long `_start` / wait |
| MCMAN/PADMAN | — | SKIP HLE | HLE-owned |

Parent design (`IOP_MULTITHREAD_AND_REAL_RPC.md`): *“IOPFILE.IRX spawn worker threads and yield inside `_start`… larger budgets do not fix without multi-context.”*  
This TRACE **names the barrier on BO2:** **IOPFILE (and SDRDRV) exhaust the 100k `_start` budget without return** under `IOP_THREADS=1` — scaffolding is on, but **cooperative yield + continue does not complete registration within the arming call**.

---

## 5. Synthesis with empty firstQueue

| Layer | Finding |
|-------|---------|
| CALL time | `firstQueue=0` always (`c1-registerrpc-trace-bo2.md`) |
| Boot SIFCMD | Started once; may park in IRQ wait quanta |
| IOPRP re-start | SIFCMD HLE-skip — no second real register pass |
| Disc IRX | **IOPFILE/SDRDRV hit budget, ret=False** — never finish `_start` |

**Primary next implement seat (design-first dual-ACK):** make `StartLoadedModule` / literal start path **survive yield** for IOPFILE-class modules under `IOP_THREADS` so registerRpc can run *after* worker create — not just raise 100k budget (already known insufficient).

---

## 6. Non-claims

- Does not claim IOPFILE is the only commercial title’s barrier.  
- Does not implement multi-start residual drain.  
- Does not change HLE-owned skip policy.

---

```text
C1 _start TRACE BO2 @50M
  IOPFILE + SDRDRV: hit budget 100k ret=False
  IOPRP: 15/15 HLE-skip including SIFCMD
  barrier named: disc IRX _start never completes (yield/worker class)
  No Core. Next: dual-ACK implement for yield-surviving start
```
