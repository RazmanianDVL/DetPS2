# C1 audit — why `firstQueue` stays 0 after full C1 stack (registerRpc barrier)

**Status:** measurement / synthesis — **docs only, no Core**  
**Date:** 2026-08-04  
**Tip:** `27af7d7` (yield-start + CreateThread storm-fixed + WaitSema phase-2)  
**Seat:** dual-idle split A (Grok); Claude on C (IOPFILE 100k-reach)  
**Evidence:** `c1-registerrpc-trace-bo2.md`, `c1-start-trace-bo2.md`, Claude BO2 canaries seq0133/0138/0142  

---

## 0. One-line answer

**Live `sceSifSetRpcQueue` chain never gets a head on BO2:** every CALL-time probe still sees `firstQueue=0x00000000`. C1 yield surface (CreateThread + WaitSema + residual start) is **wired and safe** but **does not fire on the modules that would register**, so the live registry cannot grow. HLE dual-path remains product RPC.

---

## 1. What “firstQueue” means (code)

`RealSifRpc` live path (DBG under `DETPS2_TRACE_REALRPC`):

| Field | Source |
|-------|--------|
| `sifcmdLoadBase` | Loaded **SIFCMD** IRX image base (non-zero on BO2) |
| `chainHead` | `sifcmd.LoadBase + SifCmdQueueChainOffset` (static offset into SIFCMD data) |
| **`firstQueue`** | `IopRead32(chainHead)` phys-masked — **head of live RPC queue list** |

Filled only when real IOP code runs **`sceSifSetRpcQueue`** (SIFCMD export) and later **`sceSifRegisterRpc`** links servers. Prefer-live before HLE only when this list is non-empty (`LiveRpcHits`).

**Observed (BO2, repeatedly):** `sifcmdLoadBase` / `chainHead` **non-zero**, `firstQueue` **always 0**, `liveRpcHits=0`, `binds`/`calls` still HLE-served.

---

## 2. Post-C1 stack status (not stale)

| Mechanism | Flag (default off) | BO2 canary (Claude) |
|-----------|--------------------|---------------------|
| Multi-thread table | `DETPS2_IOP_THREADS` | on in canaries |
| Residual start | `DETPS2_IOP_YIELD_START` | on |
| CreateThread HLE | `DETPS2_IOP_CREATE_THREAD` | **39 legit** creates after storm fix (was 9.5M) |
| Wait/Signal/Sleep HLE | `DETPS2_IOP_WAIT_YIELD` | **0 trap firings**; LINKIMPORTS shows IOPFILE 3 + SDRDRV 1 patches |
| Live registry | — | **`firstQueue=0` unchanged** |

**Interpretation:** mechanisms are not no-ops (CreateThread fires; WaitYield stubs patched). They are **downstream of the registerRpc call sites** that BO2 never reaches inside measured `_start` budgets.

---

## 3. Barrier classes (ordered)

### B1 — Disc IRX `_start` never completes (primary, named earlier)

From `c1-start-trace-bo2.md`:

| Module | Outcome |
|--------|---------|
| **IOPFILE** | `hit budget 100000` `ret=False` |
| **SDRDRV** | same |
| MCMAN/PADMAN/… | **SKIP hle-owned** (no real `_start`) |

IOPFILE is the parent-design example of “spawn workers + yield then register.” Incomplete `_start` ⇒ **no SetRpcQueue/RegisterRpc from that module**.

Claude WaitSema canary: WaitYield **never entered** despite patches → **no WaitSema/Sleep call site hit within budget** (busy path or earlier hang; Claude seat C will name PC pattern).

### B2 — SIFCMD / IOPRP HLE ownership skips re-start

`IopExtendedBiosHost.HleOwnedIopRpSkipStart` includes **SIFCMD**, FILEIO, LOADFILE, THREADMAN, …

After `cdrom0:\IOPRP*.IMG`:

```text
IOPRP StartLoadedModule SKIP hle-owned name=SIFCMD
… started=0 skipHle=15/15 r3000insns=0
```

**Implication:** reboot-gen path will **not** re-run real SIFCMD `_start` to rebuild the queue. Live register growth for BIOS-class servers must come from the **first** real `_start` (boot quanta) or non-HLE-owned disc IRX — not IOPRP re-start.

Boot TRACE: SIFCMD often **“boot quanta resident (IRQ wait)”** after 50k — may never finish queue init under single-budget start either (secondary; needs separate TRACE if B1 fixed).

### B3 — Prefer-live without table is a no-op (not a bug)

`DETPS2_IOP_REAL_RPC=1` + empty `firstQueue` correctly falls back to HLE. Flag-on without registry growth is **expected**, not a wiring regression.

### B4 — Ruled out / mitigated

| Hypothesis | Status |
|------------|--------|
| CREATE_THREAD retry storm starving progress | **Fixed** (`ce3d306`); 39 creates stable |
| WaitSema HLE not linked | **Ruled out** (LINKIMPORTS overrides present) |
| `sifcmdLoadBase` missing | **Ruled out** (DBG non-zero) |

---

## 4. Who should fill firstQueue? (product model)

| Server class | Expected real path | DetPS2 today |
|--------------|--------------------|--------------|
| FILEIO / LOADFILE / PADMAN / … | HLE-owned **or** real IRX `_start` + register | HLE product path; real start often **SKIP** or incomplete |
| Disc IRX (IOPFILE, game sound, …) | Real `_start` → CreateThread/Wait → **RegisterRpc** | `_start` budget fail; C1 surface ready but unreached |
| SIFCMD queue host | Real SIFCMD `_start` SetRpcQueue | Image present; queue head empty; IOPRP re-start skipped |

North star: **IRX is product**; empty firstQueue means live path never armed.

---

## 5. Recommended next seats (dual-ACK before Core)

| ID | Seat | Type | Why |
|----|------|------|-----|
| **FQ-1** | Claude **C** outcome: IOPFILE PC/insn class at 100k | measure | Explains WaitSema=0 and whether more budget alone helps |
| **FQ-2** | Synthetic IRX smoke: CreateThread+Wait/Signal+`RegisterRpc` plant → EE CALL `LiveRpcHits≥1` | smoke | **Exit criterion** for C1 registry growth independent of BO2 |
| **FQ-3** | Optional: one non-BO2 title TRACE with disc IRX that registers early | measure | Cross-title proof |
| **FQ-4** | Design: HLE-owned skip policy vs “literal SIFCMD queue init once” | design | Only if B2 is the residual after B1 |
| **FQ-5** | **Not** raise 100k budget as sole fix | — | Already known insufficient without yield completion |

**Bias:** **FQ-2** after Claude C (or in parallel if C is pure TRACE docs) — proves the C1 chain end-to-end without depending on BO2 IOPFILE reach.

---

## 6. Dual-ACK questions

| ID | Question | Bias |
|----|----------|------|
| **FQ-Q1** | Accept B1+B2 as the firstQueue barrier class (not missing prefer-live flag)? | **Yes** |
| **FQ-Q2** | Next implement: synthetic RegisterRpc smoke (FQ-2) before more BO2 Core? | **Yes** |
| **FQ-Q3** | Touch HLE-owned IOPRP skip list this milestone? | **No** until FQ-1/2 |
| **FQ-Q4** | GameQuirks fake registerRpc? | **Banned** |

---

## 7. Definition of done (this seat)

- [x] firstQueue semantics tied to RealSifRpc DBG + SetRpcQueue  
- [x] Post-C1 canary evidence folded in (39 creates, 0 WaitYield, firstQueue=0)  
- [x] Barrier classes B1–B4 + next seats FQ-1..5  
- [ ] Dual-ACK FQ-Q1..Q4  
- [ ] **No Core** this seat  

---

```text
firstQueue=0 after full C1 stack
  SIFCMD image present; queue head never linked
  IOPFILE/SDRDRV _start incomplete; WaitSema surface unreached
  IOPRP HLE-skip blocks SIFCMD re-register
  next: synthetic RegisterRpc smoke + Claude C reach TRACE
```
