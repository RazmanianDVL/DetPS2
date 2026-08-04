# M4-S0-GOW-TRACE — GoW reboot-arg classification

**Status:** classify complete (read-only + docs) — **no Core / GameQuirks product changes**
**Date:** 2026-08-04
**Tip:** `acf0dee` (docs M4-S0 design already landed)
**Build:** `out/scoreboard-build` Release (`DetPS2.Core.dll` @ 2026-08-04 12:44 CDT)
**Media:** `user-media-god-of-war.json` → `GodofWar(USA).iso` (ISO contains `IOPRP300` string)
**Parent design:** `docs/infra-audits/m4-s0-gow-udnl-arg-design.md` §3.1 / §8 DoD
**Artifacts:** `out/canaries/m4-s0-gow-trace/20260804-131359/`

---

## 1. Classification (verdict)

| Field | Value |
|-------|--------|
| **Letter** | **A** |
| **Bucket** | **never writes arg** (stronger: **never issues `RESET_CMD` / `SifIopReset` at all within claim 100M**) |
| **Not B** | No BO2-shaped truncation (`"c"` / short-name path-combine residue) — no arg buffer reaches the handler |
| **Not C** | No Whiplash-shaped `host0:` / devkit branch string |
| **Not D** | Fits design-tree bucket (a): retail UDNL handoff does not arrive via this syscall path in the claim window; not a new truncation/branch mechanism |

**One-line evidence:** claim 100M with `DETPS2_TRACE_REBOOT=1` → **0** `[REBOOT] pending/complete/handoff` lines, **0** SIFCMD `cid=0x80000003`, **0** `[GOW] post-reboot UDNL` / `OnIopReboot`; only system SIFCMD is `0x80000000`; `GET_VERSION` stays `ioprp=""`.

---

## 2. Setup

| Item | Value |
|------|--------|
| Command | `dotnet exec out/scoreboard-build/DetPS2.Core.dll blocker-trace user-media-god-of-war.json --cycles=100000000 --host-present` |
| Env (both arms) | `DETPS2_TRACE_REBOOT=1` `DETPS2_TRACE_BIOS=1` `DETPS2_TRACE_RPC=1` |
| Arm 1 | product defaults (plant ON) |
| Arm 2 (diagnostic) | `DETPS2_M8A_GOW_NO_VERSION_PLANT=1` — does plant-off surface a late retail reboot? |
| Wall | ~29 s (baseline), ~26 s (plant-off) |

No Core rebuild required beyond existing scoreboard-build; no assist/env product flips for the primary arm.

---

## 3. Evidence — reboot / UDNL / arg surface

### 3.1 Baseline claim 100M (product plant ON)

**Grep counts on `claim-100m-err.txt`:**

| Pattern | Count |
|---------|------:|
| `[REBOOT]` (pending / complete / handoff) | **0** |
| SIFCMD `cid=0x80000003` (RESET_CMD) | **0** |
| `[GOW] post-reboot UDNL` | **0** |
| `OnIopReboot` | **0** |
| `reboot-gen=` | **0** |

**All system-class SIFCMD + plant + GetVersion lines that did fire:**

```text
[GOW] planted IOPRP version "3000" @ 0x002C6D30 cyc=518000
[SIFCMD] cid=0x80000000 dest=0x00000000 opt=0x00000000 psize=20 dsize=0 eePacket=0x00327880
[LOADFILE] GET_VERSION result=0x00020000 ioprp="" preferIopRp=False
```

Interpretation:

- EE sends **INIT-class** SIFCMD `0x80000000` only among the Sony system CIDs; **never** `0x80000003` RESET_CMD.
- `MarkIopRebootPending` therefore never runs → `LastIopRebootArg` stays the default empty string for the whole claim window.
- Assist force-UDNL / force-tag path is gated on `IopRebootGeneration` change (`GodOfWarAssist.cs` reboot-gen block) — **never entered**.
- Early plant at `cyc=518000` still runs (product residual); GetVersion returns classic `0x00020000` with **empty** IOPRP ASCII tag store.

**Around the historically claimed ~61M empty reboot:** only pad-after-px noise, no reboot:

```text
[GOW] PL-016 pad-after-px n=896 btn=0x4008 open=0 ghost=0 px=1646610 prims=6 gifP2=17 softGs?=1 state?=0 cyc=61000000
```

**Claim metrics (stdout):**

```text
after 100000000 cyc: PC=0x0017A0DC
px=1646610 … cdvdSectors=646 … RealSifRpc: binds=10 calls=42 …
```

### 3.2 Plant-off claim 100M (diagnostic)

Question: does suppressing the early EE-cell plant unmask a retail `SifIopReset` with an arg we could classify as B/C?

**Answer: no.** Still zero reboot surface:

```text
[GOW] planted IOPRP version "3000" @ 0x002C6D30 cyc=518000   # log line only; gate skips write
[SIFCMD] cid=0x80000000 …
[LOADFILE] GET_VERSION result=0x00020000 ioprp="" preferIopRp=False
```

| Pattern | Count |
|---------|------:|
| `[REBOOT]` | **0** |
| SIFCMD `cid=0x80000003` | **0** |

```text
after 100000000 cyc: PC=0x0026C4B4
… cdvdSectors=510 … RealSifRpc: binds=10 calls=19 …
```

Plant-off still diverges progress (matches prior M4-S4 claim canary shape: baseline PC `0x0017A0DC` vs plant-off `0x0026C4B4`) but **does not produce a RESET_CMD** to inspect.

### 3.3 Contrast — titles that *do* write retail UDNL args (same TRACE_REBOOT)

BO2 claim validation (`out/canaries/bo2-claim-validation/softoff-err.txt`) shows the healthy S0 shape:

```text
[REBOOT] pending arglen=32 mode=0 arg="rom0:UDNL cdrom0:\IOPRP234.IMG;1"
[REBOOT] complete gen=1 smflag=0x70000 arg="rom0:UDNL cdrom0:\IOPRP234.IMG;1"
[REBOOT] handoff gen=1 arg="rom0:UDNL cdrom0:\IOPRP234.IMG;1" … udnlVer="2340"
[RPC] OnIopReboot: … ioprpVer="2340" arg="rom0:UDNL cdrom0:\IOPRP234.IMG;1"
```

GoW claim has **none** of these lines.

### 3.4 Disc content (not missing media)

ISO string scan finds `IOPRP300` near the start of `GodofWar(USA).iso`. Failure mode is **not** "disc lacks IOPRP300"; it is that DetPS2 never observes GoW's EE issuing `SifIopReset("rom0:UDNL …IOPRP300…")` inside 100M cycles under current HLE + assist stack.

---

## 4. Ruling against B / C / D

| Class | Shape to match | GoW tip claim? |
|-------|----------------|----------------|
| **A never writes arg** | No non-empty (or no) RESET arg at syscall boundary | **YES** — zero RESET_CMD; arg never presented |
| **B BO2 truncation** | Buffer built then collapsed (e.g. `"c"` / path-combine helper) | **NO** — no pending arg of any length |
| **C Whiplash host/devkit** | `host0:…IOPRP…` vs `cdrom0:` branch | **NO** — no host0/cdrom0 reboot string |
| **D something new** | Non-empty wrong arg of a third shape | **NO** — absence of the call is still bucket A in the design tree |

### Historical comment vs live tip

`GodOfWarAssist.cs` comment (blame `5669857e`, 2026-07-30):

```text
// Empty SifIopReset (live tip @~61M arg="") leaves UDNL ver="" / GetVersion empty.
```

**Not reproduced at tip `acf0dee` claim 100M.** Live evidence is *stronger* than "empty arg @~61M": there is **no** `SifIopReset` event at all through 100M (baseline and plant-off). Possible explanations (out of scope to fully resolve here):

1. Prior ground-truth was on a different tip / env / progress path that actually reached RESET_CMD.
2. Current assist stack (early plant + other rescues) steers boot so the retail reboot call site is never hit inside the budget.
3. GoW's retail path never used this syscall pattern for IOPRP300 under DetPS2 HLE at all; the old note over-fit a transient observation.

For S0 retirement planning, (1)–(3) all land the same operational classification: **A — no retail-shaped arg reaches `LastIopRebootArg` in the claim window**, so S1/`OnIopReboot` tag extract and S4 mirror have nothing real to publish early.

---

## 5. Timing summary (first real UDNL)

| Event | Baseline claim 100M | Plant-off claim 100M |
|-------|---------------------|----------------------|
| First `[REBOOT] pending` | **never** | **never** |
| First `IopRebootGeneration` bump | **never** (gen stays 0) | **never** |
| First real UDNL handoff from game arg | **never** | **never** |
| Forced UDNL (`ApplyUdnlHandoff(IOPRP300)`) | **never** (reboot-gen gate) | **never** |
| Early plant log | `cyc=518000` | log only (write skipped) |
| GetVersion IOPRP tag | `ioprp=""` | `ioprp=""` |

There is **no** "first real UDNL" event to timestamp under either arm.

---

## 6. Implications (no fix this seat)

1. **S0 arg-repair patches (BO2 path-combine / Whiplash UsingCD) do not apply** until/unless a future seat finds a real RESET_CMD call site with a non-empty wrong buffer — current claim evidence says that site is not exercised.
2. **S4 EE-mirror cannot replace GoW plant** on claim timing: `_lastIopRpVersionAscii` never populates from a real reboot (confirms `m4-s4-gow-claim-timing-followup.md`).
3. Per design Q2 bias: treat plant as an **evidence-backed residual** for the RESET_CMD/UDNL-arg path inside 100M; optional later work is alternate tag discovery (disc IOPRP filename at mount / LoadModule), not blind BO2/Whip patch reuse.
4. If a future seat needs to re-open B/C, first reproduce a live `[REBOOT] pending` line (extend budget, strip more assists, or EE PC-trace the retail reset helper) — classify only after a non-empty-or-empty arg is actually observed at the syscall.

---

## 7. Definition of done checklist

| Item | Status |
|------|--------|
| `DETPS2_TRACE_REBOOT=1` past ~61M (claim 100M) | **Done** |
| Capture reboot-gen / LastIopRebootArg / IOPRP apply / empty vs non-empty / first UDNL timing | **Done** (all empty / never) |
| Classify A/B/C/D | **A** |
| Evidence quotes in this doc | **Done** |
| No Core / GameQuirks product changes | **Done** |

---

## 8. Artifacts (absolute paths)

| File | Path |
|------|------|
| Baseline err | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\out\canaries\m4-s0-gow-trace\20260804-131359\claim-100m-err.txt` |
| Baseline out | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\out\canaries\m4-s0-gow-trace\20260804-131359\claim-100m-out.txt` |
| Plant-off err | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\out\canaries\m4-s0-gow-trace\20260804-131359\claim-100m-plantoff-err.txt` |
| Plant-off out | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\out\canaries\m4-s0-gow-trace\20260804-131359\claim-100m-plantoff-out.txt` |
| Filtered extract | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\out\canaries\m4-s0-gow-trace\20260804-131359\reboot-relevant-extract.txt` |
| This note | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m4-s0-gow-reboot-arg-classify.md` |
| Design parent | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m4-s0-gow-udnl-arg-design.md` |

---

*Classify-only seat. No product code changes. Classification **A** — GoW never presents a RESET_CMD / UDNL reboot arg within claim 100M under tip + TRACE_REBOOT.*
