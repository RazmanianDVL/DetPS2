# RealSifRpc dual path — live `sceSifRegisterRpc` table vs HLE

**Status:** design sketch (infra audit) — no code changes in this note  
**Owned code:** `src/DetPS2.Core/RealSifRpc.cs` (primary), queue drain in `SonyKernelHle.DrainRealRpcQueue`, transport in `Sif.cs`  
**Related:** `docs/irx/SIF_BRIDGE.md`, `docs/irx/HLE_TO_IRX_MATRIX.md`, `docs/TITLE_HACKS.md` (Real SIF RPC dispatch), `docs/IRX_EXECUTION_PHASE_PLAN.md` WP-49  
**Date:** 2026-08-04

---

## 1. Goal

Let a **live** IOP `sceSifRegisterRpc` server table take **precedence** over C# HLE in `RealSifRpc` whenever a genuinely registered handler exists for a SID — without breaking current commercial titles that still depend on HLE.

Constraints:

1. Default product path must keep today’s roster green (HLE answers when live registration is missing or incomplete).
2. Dual path must be **feature-flagged** for bisect (opt-out of live dispatch; later opt-in for fail-fast).
3. Transport (BIND / CALL / RDATA / RPC_END) stays in `RealSifRpc` until WP-20 SIFCMD IRX owns the wire; this note is about **service body** precedence only.

---

## 2. Current call graph (as of 2026-08-02/03 code)

### 2.1 Packet entry

```text
EE SifSetDma (syscall 0x77)
  → SonyKernelHle.PerformSifSetDma
  → Sif.Sif1EeToIop + Sif.SubmitRealRpc(pkt, generation)
  → (later tick) SonyKernelHle.DrainRealRpcQueue
  → RealSifRpc.TryHandle(pkt)
       cid 0x80000009 → HandleBind
       cid 0x8000000A → HandleCall
       cid 0x8000000C → HandleRdata
```

`TryHandle` only recognizes BIND/CALL/RDATA; other SIFCMD cids fall through to system-cid / heuristic handling outside this class.

### 2.2 BIND (`HandleBind`)

| Step | Behavior |
|------|----------|
| Read `cdPtr` (+28), `sid` (+32) | `SifRpcBindPkt_t` |
| `cdPtr == 0` | Drop; no RPC_END (no client to wake) |
| Else | Assign IOP scratch `argBuf` + `ctrlBuf`; map `_cdToSid` / `_cdToArgBuf` |
| Client plant | `cd+20=buf`, `cd+24=cbuf`, `cd+36=server(sid)` |
| Complete | `CompleteRpcEnd` → clear packet, `SignalSema(cd->sema_id)` if ≥0 |

**Unknown SID policy on BIND (today):**

- BIND **always completes successfully** for any non-null `cdPtr` (including proprietary title SIDs).
- SIDs not in the allow-list increment `UnknownBindSids` and append to `_unknownSidsSeen` (telemetry only).
- Allow-list includes BIOS SIDs (FILEIO/LOADFILE/SYSMEM/CDVD/PAD/MC), known middleware (SNDF/SFSV/CRI/SDRDRV/989/MSL/MWFILE/GTFS/IOPFILE/…), and soft-HLE SIDs (PL2303, AAAIOP).
- **There is no live-registry check on BIND.** HLE invents opaque handles so EE `sceSifBindRpc` always unblocks, even if the real IRX has not yet called `sceSifRegisterRpc`.

This is intentional: retail EE clients spin on bind semaphores; denying bind when the IOP table is empty would freeze titles while `_start` is still incomplete (current common case — see §5).

### 2.3 CALL (`HandleCall`) — dual path already half-present

Order today:

```text
1. Resolve sid/argBuf from _cdToSid/_cdToArgBuf (from prior BIND)
2. LIVE PATH (if enabled):
     DETPS2_NO_REAL_RPC != "1"
     && TryFindRealRpcServer(sid) → (func, buff)
     && TryDispatchRealRegisteredRpc(...) → reply in recvBuf
   → CompleteRpcEnd(isCall: true); return
3. HLE PATH — long special-case ladder (CRI ADX, LOADFILE, CD SCMD/NCMD,
   SearchFile, DiskReady, FILEIO, SYSMEM, 989, IOPFILE, DBCMAN siblings,
   LGDEV, GTFS, MWFILE, MSL, PL2303, AAAIOP, …)
4. Dispatch(sid, fno, …) switch — remaining SIDs + default
5. Write single-word result (or multi-word plants); CompleteRpcEnd
```

Live path details (`TryFindRealRpcServer` / `TryDispatchRealRegisteredRpc`):

| Item | Ground truth |
|------|----------------|
| Registry source | Real SIFCMD.IRX global queue chain at module-relative `.data+0x2a60` |
| Queue struct | `+0x08` server list head, `+0x14` next queue |
| Server struct | `+0x00` sid, `+0x04` func, `+0x08` buff, `+0x38` next server |
| Safety | `func` must land inside **some** loaded IRX image (partial-init guard) |
| Invoke | Save IOP GPR/PC; call `handler(fno, buf, size)` with scratch SP; restore context |
| Reply | `v0` = reply pointer; copy up to `recvSize` into EE `recvBuf` |
| Failure | return false → fall through to HLE (no partial CompleteRpcEnd) |

### 2.4 `Dispatch` (HLE fallback core)

`Dispatch` is the catch-all after dedicated `HandleCall` branches. It:

- Switches known SIDs to `HandlePad` / `HandleMcServ` / soft-success middleware / etc.
- **Default:** `UnknownServiceCalls++`, return **0** (IOP “OK” convention preferred over historical “return 1”, because 989snd treats 1 as fail).

So unknown **calls** after a successful bind are soft-success 0, not fail-closed — except where a specialized handler already wrote a multi-word reply.

### 2.5 Unbound CALL (`sid == 0`)

If CALL arrives with a `cdPtr` never seen in BIND, `sid` is 0:

- Live registry lookup is **skipped** (`sid != 0` guard).
- Falls into HLE ladder / `Dispatch` default → soft 0 + `UnknownServiceCalls` if no branch matches.

Title assists sometimes call `HostCompleteBind` / soft-bind helpers to repair missing bind maps without re-running EE bind transport.

---

## 3. Proposed dual-path policy (precedence)

### 3.1 Precedence rule (target)

| Condition | CALL body | BIND |
|-----------|-----------|------|
| Live server registered for SID **and** live dispatch succeeds | **Live IOP handler only** — skip HLE | Still HLE-complete (plant handles + RPC_END) unless live BIND is wired (future) |
| Live server registered, live dispatch **fails** (timeout / no return / null reply) | Fall back to **HLE** (default product) **or** fail-fast (flag) | n/a |
| No live server | **HLE only** (today’s ladder + `Dispatch`) | Always HLE-complete |
| `DETPS2_NO_REAL_RPC=1` | **HLE only** (current emergency opt-out) | unchanged |

**Key product rule:** live table **takes precedence** over HLE when both could answer; HLE is fallback when the table is empty or the real handler cannot complete within budget. That preserves titles that never reach `sceSifRegisterRpc` while allowing disc IRX that *do* register to own their protocol without per-title C#.

### 3.2 Why BIND stays HLE-first (for now)

Real BIND is SIFCMD’s `FUN_00000c48`: look up SID in the same server list; if missing, real hardware leaves the client waiting (or retries). DetPS2 **cannot** mirror “bind fails until registered” until:

1. Module `_start` reliably reaches `sceSifSetRpcQueue` + `sceSifRegisterRpc` (open gap — cooperative IOP threads / early waits; see `docs/TITLE_HACKS.md`).
2. Or we synthesize registration earlier (undesirable — lies about IRX ownership).

Until then:

- BIND always HLE-succeeds (current).
- CALL prefers live body when the table eventually fills.
- Optional later: under a **strict** flag, BIND waits / fails if SID not registered (only for IRX purity bisect).

### 3.3 HLE “owned SIDs” vs live-owned SIDs

From `HLE_TO_IRX_MATRIX.md` / WP-49:

| Class | Examples | Dual-path note |
|-------|----------|----------------|
| **BIOS stack HLE-intentional** | LOADFILE, CDVDFSV SCMD/NCMD, FILEIO, SYSMEM (until live server proven) | Live path may already skip them if SIFCMD table empty; if a real IRX *does* register the same SID, **live wins** only when flag policy allows (see denylist below) |
| **Title middleware HLE** | IOPFILE, GTFS, MWFILE, CRI ADX | Live preferred the moment disc IRX registers; HLE remains soft-success floor |
| **Telemetry unknown** | any SID not in allow-list | BIND still ok; CALL soft 0; under LITERAL_IRX fail-fast later (WP-49) |

**BIOS denylist (recommended):** even if a half-initialized CDVDFSV/LOADFILE entry appears in the chain, default product should **not** prefer a flaky live handler over ground-truthed HLE until WP-22 / WP-30 exit tests pass. Implementation sketch:

```text
bool preferLive = TryFindRealRpcServer(...)
  && !IsHleStickySid(sid)   // LOADFILE, CD_*, FILEIO, SYSMEM, PAD? policy-driven
  && LiveRpcEnabled();
```

Alternatively: sticky HLE only when live dispatch returns false; if live returns true, always accept (current code — no denylist). Sticky denylist is safer for commercial menus.

---

## 4. Feature flags

### 4.1 Existing

| Flag | Effect today |
|------|----------------|
| `DETPS2_NO_REAL_RPC=1` | Disables `TryFindRealRpcServer` / live CALL path; pure HLE |
| `DETPS2_TRACE_REALRPC=1` | Logs SIFCMD chain walk |
| `DETPS2_TRACE_RPC=1` | Logs HandleBind/HandleCall / REAL-RPC invoke |
| `DETPS2_FORCE_HLE_IOP=1` / legacy `DETPS2_LITERAL_IRX=0` | Emergency: skip literal IRX arming; HLE floor (does not by itself disable live RPC walk if SIFCMD image exists) |
| `SonyKernelHle.PreferLiveLoadFileRpc` + LITERAL_IRX | Scaffold: **skip** entire `TryHandle` for dequeued packets (starves unless IOP answers) — orthogonal, coarser than dual-path |

### 4.2 Proposed (sketch only — not implemented here)

| Flag / property | Default | Purpose |
|-----------------|---------|---------|
| `DETPS2_NO_REAL_RPC=1` | off | Keep as hard off for live CALL |
| `DETPS2_PREFER_LIVE_RPC=1` | **off or on?** — recommend **on** matching current code once stable; document as product-on | Explicit “live table precedence” |
| `DETPS2_LIVE_RPC_NO_HLE_FALLBACK=1` | **off** | If live entry found but dispatch fails → hard fail (bisect), not HLE |
| `DETPS2_LITERAL_IRX` / `IsLiteralIrxEnabled` | product on unless FORCE_HLE | When on + WP-49: unknown SID or HLE hit for IRX-owned SID → throw |
| `DETPS2_HLE_STICKY_SIDS=loadfile,cdvd,fileio,sysmem,pad` | optional denylist | Never prefer live for listed classes until arming green |
| `DETPS2_STRICT_BIND=1` | **off** | BIND fails / delays if SID not in live table (IRX purity only) |

**Title safety defaults:**

- Production / `user-media.json` fleet: live precedence **on**, HLE fallback **on**, strict bind **off**, fail-fast **off**.
- IRX purity bisect: `LITERAL_IRX` + `LIVE_RPC_NO_HLE_FALLBACK` + later WP-49 throw on HLE hit for owned SIDs.
- Emergency: `DETPS2_NO_REAL_RPC=1` restores pure HLE CALL (current proven path for soft-success titles).

---

## 5. Why live path rarely fires today (non-blocking for dual-path design)

Documented in `TITLE_HACKS.md` / DEVELOPER_GUIDE §5.3:

- Disc IRX `_start` often does not reach `sceSifSetRpcQueue` / `sceSifRegisterRpc` within single-context budgets (cooperative threads, early waits).
- SIFCMD chain head may stay 0 after millions of instructions.
- Therefore **HLE still services nearly all CALLs** on the commercial roster.

Dual-path design does **not** require fixing that first: when registration starts succeeding, precedence activates automatically with zero per-title work. Until then, flags and HLE ladder keep titles bootable.

---

## 6. Implementation sketch (future code — not this doc)

No code in this PR. Suggested minimal surgery when implementing:

1. **Extract** `bool TryHandleCallLive(...)` from the top of `HandleCall` (already contiguous).
2. **Gate** with a single helper:

   ```csharp
   static bool LiveRpcDispatchEnabled() =>
       Environment.GetEnvironmentVariable("DETPS2_NO_REAL_RPC") != "1"
       // && optional PREFER_LIVE_RPC != "0"
       ;
   ```

3. **On live failure after hit:** if `DETPS2_LIVE_RPC_NO_HLE_FALLBACK=1` log + leave packet incomplete or complete with error; else fall through HLE.
4. **Counters:** add `LiveRpcHits` / `LiveRpcFallbacks` next to `UnknownServiceCalls` for scoreboard.
5. **WP-49:** under `IsLiteralIrxEnabled`, if SID is marked IRX-owned in matrix and HLE path runs → throw/assert (opt-in first via env).
6. **Do not** change BIND success semantics without a separate `STRICT_BIND` flag.
7. **Do not** delete HLE handlers until matrix status is DEVICE/live green for that SID.

### 6.1 Interaction with `PreferLiveLoadFileRpc`

That flag skips **all** HLE for dequeued packets (including BIND completion). Dual-path is finer-grained: always run `TryHandle` for transport, prefer live only for CALL bodies. Prefer keeping LOADFILE on sticky HLE until WP-22 exit; do not enable `PreferLiveLoadFileRpc` by default.

---

## 7. Regression / exit checks

| Check | Expectation |
|-------|-------------|
| Smokes `RealSifRpc_*` | Unchanged with defaults (HLE) |
| Fleet titles (`user-media.json`) | `DETPS2_NO_REAL_RPC=1` A/B == default when live table empty |
| When a test IRX registers a dummy SID | Live CALL path logs `[REAL-RPC]`; HLE `UnknownServiceCalls` does not increment for that call |
| `DETPS2_NO_REAL_RPC=1` | Live path never runs; soft-success HLE unchanged |
| Future `LIVE_RPC_NO_HLE_FALLBACK` + broken live handler | Visible fail, not silent soft-0 |

Suggested smoke (later): load minimal IRX that `sceSifRegisterRpc(sid=test)`, BIND+CALL from EE fixture, assert live hit counter and reply bytes without HLE branch.

---

## 8. Summary

| Path | BIND | CALL body |
|------|------|-----------|
| **Live table hit** | (still HLE plant) | **Prefer live IOP `SifRpcFunc_t`** |
| **Live miss / disabled** | HLE plant | HLE ladder + `Dispatch` |
| **Unknown SID** | Complete + `UnknownBindSids++` | Soft 0 + `UnknownServiceCalls++` (unless WP-49 fail-fast) |

**Precedence:** live `sceSifRegisterRpc` table over HLE when dispatch succeeds.  
**Safety:** feature flags (`DETPS2_NO_REAL_RPC`, proposed no-fallback / sticky-HLE / strict-bind) keep current titles on HLE until IRX registration and device bridges are ready.  
**No code changes** in this audit — sketch only.
