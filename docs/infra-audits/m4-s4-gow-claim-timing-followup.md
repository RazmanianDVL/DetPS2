# M4-S4 GoW claim-tier regression — root cause (timing/sequencing, not a broken mechanism)

**Date:** 2026-08-04
**Mode:** investigation only — **no Core changes, no product default flip, no push.**
**Follow-up to:** `docs/infra-audits/m4-s4-gow-claim-canary.md` §7 item 1 ("TRACE_RPC / TRACE_MIRROR at claim to confirm [S4-MIRROR] write timing vs FreezeCache memcmp").

---

## 1. Answer

**Root cause: a genuine sequencing gap, not a bug in the mirror mechanism itself.**

`PlantIopRpVersion`'s legacy plant path writes the raw EE cell (`0x002C6D30`) **unconditionally**, independent of any RPC/reboot state, at two fixed points: `OnDiscMounted` (mount) and `Step()`'s `c >= 500_000` re-plant (`GodOfWarAssist.cs:531-538`). The new S4 mirror path (`TryMirrorIopRpVersionCells`) only ever writes what `_lastIopRpVersionAscii` currently holds — and that field is **only** populated by a real IOP reboot with a parseable `IOPRP300` arg (S1, `RealSifRpc.OnIopReboot`) or by the assist's own force-tag fallback (`EnsureIopRpGetVersion` → `SetIopRpVersionAscii`, `GodOfWarAssist.cs:485-504`), which itself only fires on a **reboot-generation change** (`GodOfWarAssist.cs:544-573`).

**Confirmed by trace: zero reboot-gen events occur in either arm within the first 5M cycles.** So at the exact cycle (~518,000) where the legacy plant used to unconditionally fill the cell, the mirror-only arm has `_lastIopRpVersionAscii` still empty — `TryMirrorIopRpVersionCells` is a no-op, and the cell stays at the `"...."` placeholder. This produces the **identical** failure signature as the original diagnose-tier "plant fully off, no mirror at all" test (`cdvdSectors` 136→0) — the mirror isn't broken, it simply has nothing to mirror yet at the moment GoW's early boot path needs it.

**This is not a simple fix** (not "also register the force-tag call site" — that call site is itself gated on the same reboot-gen event that hasn't happened yet). It confirms M4-S4-MIRROR's own **Q4** hedge was correct: GoW's **S0 (arg-fidelity)** gap — why the real UDNL/reboot arg is empty/late for this title — needs to be closed (or some other genuinely-early, non-invented tag source found) before the mirror mechanism can actually replace the legacy plant for GoW.

---

## 2. Evidence

### 2.1 Setup

Reused the existing Release build at `out/scoreboard-build/DetPS2.Core.dll` (built 12:44 CDT, newer than `GodOfWarAssist.cs`'s last commit `b23dd45` @ 12:38:43) — **did not rebuild**, to avoid picking up Grok's concurrent uncommitted `Gif.cs`/`Program.cs` Slice 2a work.

Two short (5,000,000-cycle) runs with `DETPS2_TRACE_BIOS=1 DETPS2_TRACE_RPC=1 DETPS2_TRACE_REBOOT=1`:

| Arm | Env |
|---|---|
| Baseline | none (product default: plant ON, mirror off) |
| Plant-off + mirror | `DETPS2_M8A_GOW_NO_VERSION_PLANT=1` + `DETPS2_MIRROR_IOPRP_CELLS=1` |

### 2.2 Result at 5M cycles (first clear divergence point)

| Field | Baseline | Plant-off + mirror | Note |
|---|---|---|---|
| `cdvdSectors` | **136** | **0** | Identical collapse to the *original* diagnose-tier plant-off-alone result (`m8a-gow-dual-suppress-results.md`) — mirror provided **zero** benefit at this point |
| `calls` | 21 | 12 | Matches the same diagnose-tier delta shape |
| `sifBytes` | 8116 | 2932 | |
| `syscalls` | 2284 | 2249 | Close — the RPC/thread-setup traffic itself is similar; the divergence is specifically boot-progress-linked |
| `pc` | `0x002845A4` | `0x00284AA8` | Close together at this point (~1.3KB apart) — diverges much further by 100M (`0x0017A0DC` vs `0x0026C4B4` per the parent canary), consistent with an early fork that compounds over time rather than a late, sudden failure |
| `gifP2`/`gifP3`/`imgBytes`/`gifCompleted`/`px`/`prims` | identical both arms | identical both arms | Confirms (again) the GS/GIF pipeline is completely unaffected — this is purely a boot/RPC-sequencing issue |

### 2.3 Trace confirms the mechanism, not a guess

```text
[GOW] planted IOPRP version "3000" @ 0x002C6D30 cyc=518000
```

This exact line appears **identically in both arms** at the same cycle. In the baseline it means the raw write happened. In the plant-off+mirror arm, code review confirms this trace line is emitted unconditionally by the `Step()` call site (`GodOfWarAssist.cs:536-537`) regardless of which branch `PlantIopRpVersion` internally took — so the log message is **misleading** (says "planted" even when the function took the early-return mirror-only path and wrote nothing, because `_lastIopRpVersionAscii` was empty). Flagging this as a minor trace-accuracy issue worth fixing separately (low priority, cosmetic).

**Zero reboot-gen / `OnIopReboot` / `SetIopRpVersionAscii` trace lines appear in either arm within the 5M-cycle window** (`grep -c` returned 0 for both). This directly confirms `_lastIopRpVersionAscii` has never been populated by cycle 518,000 in either arm — the legacy plant doesn't care (writes unconditionally); the mirror does care and has nothing to write.

---

## 3. Why this isn't a quick patch

Three ways to "fix" this were considered and rejected as violating the M4-S4 design's own non-goals:

| Option | Why rejected |
|---|---|
| Make the mirror fire unconditionally with a fallback hardcoded tag at the same `c>=500_000` mark | Reintroduces exactly the per-title invented-digit-in-Core pattern S4 was designed to eliminate (`m4-s4-ee-mirror-design.md` §3.4: "Never invent from serial... inside Core") |
| Lower the reboot-gen gate so force-tag fires earlier / unconditionally | Would resurrect the *other*, already-documented regression: the code comment at `GodOfWarAssist.cs:429-431` explicitly warns that setting `GetVersion="3000"` from cycle 0 previously regressed `binds 16→10 / dmac 463→321` (FILEIO-2200 arming skew) — this isn't a new problem, it's a known landmine the legacy plant's specific timing (mount + 500k, not cycle 0) was tuned to avoid |
| Just accept mirror-only and move on | Fails M4-S4-MIRROR's own Q9 claim-tier bar (already correctly enforced — plant stays ON) |

**The real fix is upstream: GoW's S0 (arg fidelity).** If the actual `SifIopReset` reboot arg reaching GoW's assist genuinely contained a parseable `IOPRP300` string **early** (near cycle 0-500k, matching the timing window the legacy plant already occupies), `_lastIopRpVersionAscii` would populate naturally via the real S1 path (`RealSifRpc.OnIopReboot`) at the same time, and the mirror would have real data to write at exactly the moment it's needed — no invented tags, no timing hacks. This is out of scope for this investigation (S0 is a separate, not-yet-scoped track per the M4 design doc's own stage list) but is now the concretely evidenced next step for anyone who wants to actually retire GoW's plant.

---

## 4. Recommendation

- **Do not attempt a quick S4-side fix.** The mirror mechanism is working exactly as designed; the gap is that GoW doesn't have a real, early tag-store event to mirror from.
- **File as a design-first dependency**, not a bug in `IopRpEeVersionMirror` or `GodOfWarAssist`'s mirror wiring.
- Next real step (separate, future seat, needs its own dual-ACK): investigate **why** GoW's real UDNL reboot arg is empty/late in the first place (S0 track) — only then does GoW's plant become a legitimate quiet-retire candidate under the mirror mechanism.
- Minor, low-priority cosmetic fix noted in passing: the `[GOW] planted IOPRP version...` trace line at `GodOfWarAssist.cs:536-537` should distinguish "wrote raw cell" from "attempted mirror-only, no-op" so future traces aren't misleading — not blocking, not done here.

---

## 5. References (absolute paths)

| Artifact | Path |
|---|---|
| Parent claim canary | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m4-s4-gow-claim-canary.md` |
| M4-S4 design (Q4 hedge) | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m4-s4-ee-mirror-design.md` |
| Original diagnose dual-suppress | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m8a-gow-dual-suppress-results.md` |
| GoW assist (plant/mirror/force-tag call sites) | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\GodOfWarAssist.cs` (see lines 353-434, 481-573) |
| Raw trace artifacts (this investigation) | `C:\Users\xxraz\.grok\worktrees\windows-detps2\detps2\out\canaries\m4-s4-timing-followup\` |

---

*Investigation only. No Core code changes. No product default change. No push.*
