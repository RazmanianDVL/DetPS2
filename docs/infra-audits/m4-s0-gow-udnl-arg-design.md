# M4-S0-GOW design — GoW real UDNL/reboot-arg fidelity (investigate-first)

**Status:** design only (ready for dual ACK) — **no Core implement this turn**
**Date:** 2026-08-04
**Mode:** infra-only. **No GodOfWarAssist behavior changes. No Gif.cs. No push.**
**Tracks:** M4 UDNL/GetVersion unification **S0** (`docs/UDNL_GETVERSION_UNIFICATION.md` §3.1: "Path combine / UsingCD / short-name so `LastIopRebootArg` is retail-shaped without title rewrite"), specifically the row naming "GoW empty-arg force-UDNL" as an S0 retirement target.
**Follow-up to:** `docs/infra-audits/m4-s4-gow-claim-timing-followup.md` — S4's EE-mirror can't help GoW because `_lastIopRpVersionAscii` is never populated early (no real reboot-gen event with a parseable arg happens in the window the legacy plant occupies); the mirror mechanism is correct, the gap is upstream.

---

## 0. One-line summary

I read the *generic* RESET_CMD arg-parsing path (`SonyKernelHle.cs:1717-1743`) and confirmed it is **not** the bug — it faithfully reads whatever bytes the game's own EE code wrote into the real `SifCmdResetData_t` packet buffer (`argLen` + `arg[80]` straight from EE RDRAM, no DetPS2-side fabrication or truncation). That means GoW's empty/late arg is either (a) a fact about GoW's real retail boot sequence not matching the "early UDNL reboot with IOPRP tag" pattern most other titles follow, or (b) GoW's own EE code fails to *construct* a correct arg string before calling the reset syscall, the same general shape as two **already-diagnosed, already-fixed** bugs for other titles this session — but a **different specific mechanism** in each case:

| Title | Confirmed root cause (already fixed) | Mechanism class |
|---|---|---|
| BO2 (`BloodOmen2SnAssist.cs:187-217`) | A real EE library "short-name path-combine" helper (`0x2DB138`) truncates an otherwise-correctly-built path buffer down to `"c"` under HLE; the caller's own buffer is fine going in. Fix: patch that ONE helper function to `jr ra; nop` (identity) so the caller's buffer survives. | Real EE subroutine misbehaves under DetPS2's environment (likely depends on some real IOP-side callback DetPS2 doesn't service) |
| Whiplash (`WhiplashAssist.cs`, header comment + `UsingCD` patches ~line 84-98, 291-300) | Retail ELF carries a dual SN ProView devkit/retail path; EE code branches on a `UsingCD` detection byte that defaults to the **host-devkit** branch (`host0:~/bin/IOPRP255.IMG`) under DetPS2 instead of the real-disc branch (`cdrom0:...`), because DetPS2 doesn't naturally make that detection resolve the retail way. Fix: force the detection byte + rewrite `host0:` → `cdrom0:` if it still appears post-reboot. | EE-side devkit/retail branch selection resolves wrong, not a buffer-corruption bug |
| **GoW** | **Not yet identified.** Comment at `GodOfWarAssist.cs:540` ("Empty SifIopReset (live tip @~61M arg=\"\")") records this as an already-observed live fact from earlier ground-truthing, but no root mechanism is documented in this codebase yet. | **Unknown — this design's open question #1** |

**These are three different bugs in the same problem family, not one generic bug with three symptoms.** The header comment at `WhiplashAssist.cs:22` groups all three ("same UDNL version-handoff class as BO2/B3/GoW") at the *symptom* level (arg doesn't reach the syscall boundary in retail-shaped form), but the *fix* for each has turned out to be title-specific at the mechanism level so far — BO2's fix (patch one library routine) would do nothing for Whiplash's problem (a branch-selection byte), and neither would help GoW unless GoW happens to hit the exact same mechanism as one of them, which is **not yet confirmed**.

---

## 1. What I confirmed vs did not confirm

### 1.1 Confirmed (this seat, code read only)

- `SonyKernelHle.cs`'s `case 0x80000003` (RESET_CMD handler, lines 1717-1743) reads `argLen`/`mode`/`arg[80]` directly from the EE-supplied packet at `eePacket + 0x10/0x14/0x18`, byte-for-byte, with no DetPS2-side truncation, fabrication, or title-specific branching. **This generic path is not the bug** — whatever string GoW's own EE code puts in that buffer is what DetPS2 sees.
- `Sif.MarkIopRebootPending` (`Sif.cs:324-338`) stores that arg verbatim (capped at `IopRebootArgMax`, same cap applied uniformly to all titles) and `TryCompletePendingIopReboot` (`Sif.cs:345-354`) increments `IopRebootGeneration` on the *next* SMFLAG poll — also generic, also not GoW-specific.
- The M4-S4 timing follow-up's trace evidence: **zero** `OnIopReboot`/reboot-gen events occur for GoW in the first 5,000,000 cycles of either canary arm. This is consistent with (not proof of) the existing code comment's claim that GoW's real reboot happens much later (~61M) and arrives with an empty arg at that point.

### 1.2 NOT confirmed (needs a dedicated investigation seat, not resolved by this design doc)

- **Whether GoW's real EE code ever writes a parseable `IOPRP300`-bearing string into the RESET_CMD arg buffer at all**, or whether GoW's retail boot sequence simply doesn't route its UDNL/IOPRP handoff through this specific syscall pattern the way BO2/Whiplash do (e.g., a different, GoW-specific boot-loader stage baked into its own ELF that never calls a generic `SifIopReset`-with-UDNL-arg pattern).
- **If GoW's code does attempt to build a real arg, where in that construction it fails** — same class as BO2 (a shared library routine truncating a correct buffer) is a live hypothesis given the "same UDNL version-handoff class" comment, but I have not read GoW's disassembly at its actual RESET_CMD call site to confirm this, and I do not have Ghidra/live-trace tooling access in this seat to do so. This needs the same kind of ground-truthing that originally diagnosed BO2's and Whiplash's mechanisms (referenced throughout this session's history but not something a docs-only design pass can redo from grep alone).
- **Whether the same class of fix (patch a shared misbehaving library routine, à la BO2) would generalize to GoW**, if GoW turns out to call the identical or a related routine at a different address (SDK library code is often shared/statically-linked per-title at different link addresses, so "same routine, different VA" is plausible but unverified).

---

## 2. Why I'm not proposing a mechanism yet

Per this session's own established discipline (A0 before M7's Slice 2/3; my own M7-c fork's finding that two of four candidate Gif.cs fixes were already-landed, making a guessed fix pointless; M4-g's own pre-check-before-scoping precedent): **proposing a specific GoW arg-fix mechanism right now would be guessing.** I have three real, different reference mechanisms (BO2's helper-patch, Whiplash's branch-detection-plus-rewrite, and "unknown" for GoW) and zero direct evidence for which — if any — applies to GoW. Writing a "Core mechanism" section here would either (a) blindly copy BO2's fix and probably do nothing (different title, likely different code entirely), or (b) invent a GoW-specific patch without knowing what's actually broken, which risks becoming exactly the kind of untested per-title tinkering the mission wants avoided.

**Recommended shape: an investigation seat first**, not a Core PR.

---

## 3. Proposed next seat (investigation, not implementation)

### 3.1 Intent

Determine, with real evidence (trace + ideally live disassembly at the actual RESET_CMD call site in GoW's retail ELF, matching how BO2's and Whiplash's mechanisms were originally ground-truthed), which of these GoW actually is:

```text
Does GoW's EE code ever construct a non-empty RESET_CMD arg string in its own
buffer before the syscall (verify via memory-watch / EE PC trace around the
~61M live reboot event), even if it doesn't reach DetPS2 correctly?
        │
        ▼ no — buffer is genuinely empty going in
    Bucket "GoW's retail UDNL handoff doesn't work this way" — the S0 fix
    (if any) is NOT an arg-repair patch; may need a different discovery
    mechanism entirely (e.g. reading the version tag from wherever GoW's
    boot loader really gets it). Likely means GoW's plant/mirror problem
    has no S0-class fix and stays a documented residual.
        │
        ▼ yes — buffer has a real string, but it's wrong/truncated/empty
        │        by the time DetPS2's RESET_CMD handler reads it
    Trace exactly where between "EE writes the buffer" and "EE issues the
    syscall" the content is lost — same class of question BO2's
    PatchCdromPathCombine and Whiplash's UsingCD detection already answered
    for their titles.
        │
        ├─ same shared-library-routine-truncates-buffer shape as BO2 →
        │  check if it's literally the same routine (shared PS2SDK/ProDG
        │  code, different link address) → BO2's fix *might* generalize
        │  to "patch this class of routine wherever it's found", which
        │  would be a genuine multi-title infra win, not a GoW-only patch
        │
        └─ different mechanism entirely → GoW needs its own diagnosis,
           documented honestly as title-specific if no shared class fits
```

### 3.2 What this investigation seat should NOT do

- Should not patch `GodOfWarAssist.cs` speculatively "to see if it helps" — that's exactly the guess-and-check pattern this doc is arguing against.
- Should not reuse BO2's `PatchCdromPathCombine` address/patch verbatim on GoW without confirming GoW's code actually has the same routine at some address — patching a *different* function at a *guessed* address risks real corruption.
- Should not touch `Gif.cs`, `RealSifRpc.cs`, or `IopRpEeVersionMirror.cs`.

### 3.3 Env / trace flags (investigation only, no product behavior)

| Env | Role |
|---|---|
| `DETPS2_TRACE_REBOOT=1` | Already exists (`Sif.cs:334-337`) — shows `arglen`/`mode`/`arg` at the moment RESET_CMD is marked pending. Use this to confirm the ~61M event and its exact arg content live, not just trust the old code comment. |
| `DETPS2_TRACE_BIOS=1` | Already exists — GoW assist's own trace lines around the reboot-gen branch (`GodOfWarAssist.cs:559-570`). |
| Memory watch (`--watch`/`--watch-after`, hardened this session per earlier `SystemMemory.WatchAfterCycle` work) | If GoW's EE code does build a buffer that gets lost before the syscall, a targeted watch on the likely reboot-arg buffer address (GoW's own SifCmdResetData_t staging area — address unknown, needs discovery, not assumed) could show the same "write then get overwritten/truncated" pattern BO2's investigation originally found. |

---

## 4. Flag-gated, kill-switch, default-safe strategy

**Not applicable to this seat** — there is no Core mechanism proposed here to gate. Any future fix that comes out of the investigation must follow the same pattern every other landing this session used: default OFF / opt-in, explicit kill-switch, diagnose-tier proof-of-concept **and claim-tier (100M) byte-identical/non-worse validation before any product-default change** — restating the same M4-S4-MIRROR Q9 bar explicitly so it isn't forgotten when this investigation produces a real candidate fix.

---

## 5. Validation plan (for the investigation seat itself, not a future fix)

| Check | Expect |
|---|---|
| `DETPS2_TRACE_REBOOT=1` run to ~65M (past the documented ~61M event) | Confirms or updates the "empty arg @~61M" claim with fresh evidence — the code comment is from earlier ground-truthing and should be re-verified against the current build, not assumed still accurate |
| If a non-empty-but-wrong arg is found | Compare byte-for-byte against BO2's and Whiplash's already-documented failure shapes (truncated-to-single-char vs host0-prefixed) to see if GoW matches either known pattern |
| If genuinely empty at the syscall boundary | That's evidence for "GoW's retail code doesn't build this argument at all" — a different, larger finding than a repairable arg-corruption bug |

No product scoreboard validation needed for this seat — it produces a diagnosis, not a code change.

---

## 6. Non-goals

| Non-goal | Why |
|---|---|
| Any `GodOfWarAssist.cs` behavior change | This is diagnosis-only; a fix (if one exists) is a separate future seat with its own dual-ACK |
| Assuming GoW's bug is the same as BO2's or Whiplash's without verification | Three different confirmed mechanisms exist in this exact problem family already — assuming without checking risks a wasted patch |
| Reusing BO2's `PatchCdromPathCombine` on GoW without confirming the shared routine exists in GoW's binary | Different title, different link addresses; blind reuse could write to the wrong function |
| Declaring GoW's plant permanently un-retirable | Premature — we don't know yet whether this is fixable; only that it isn't fixable via the S4 mirror alone |
| Gif.cs / Slice 2 work | Different milestone, different orchestrator's active claim right now |

---

## 7. Open questions for dual-ACK

| ID | Question | Options | Design bias |
|----|----------|---------|-------------|
| **Q1** | Should the next seat be a live disassembly/Ghidra investigation of GoW's actual RESET_CMD call site (matching how BO2/Whiplash were originally ground-truthed), or is a fresh `DETPS2_TRACE_REBOOT`/memory-watch run at ~61M sufficient to classify the bug first, with Ghidra only if trace evidence is inconclusive? | (a) trace-first, escalate to disassembly only if inconclusive (b) go straight to disassembly | **(a)** — cheaper, and the trace flags already exist; matches this session's "cheapest evidence first" pattern (e.g. A0 inventory reused existing docs before running new canaries) |
| **Q2** | If GoW's real code turns out to never write a UDNL/IOPRP arg at all (bucket 1 in §3.1's tree), should GoW's plant simply stay a permanent, documented residual (accept it, move on), or is it worth a *separate* investigation into whether GoW's real version tag is discoverable some other way (e.g. read directly from the mounted disc's IOPRP filename during LoadModule, independent of the RESET_CMD arg path)? | (a) accept as documented residual (b) open a further "alternate tag discovery" investigation | **(a) as the default outcome**, with **(b)** only if whoever runs the investigation seat finds a concrete, non-speculative alternate signal while looking — don't manufacture a fourth investigation seat preemptively |
| **Q3** | Who runs this investigation (trace-first) seat — same orchestrator who wrote this design (continuity with the S4 timing chain), or whichever of us is free first, matching this session's established first-lock-wins convention? | dedicated / first-free | **first-free** — no strong reason for continuity to override normal picks, this is a bounded, well-specified investigation either of us can execute from this doc |
| **Q4** | Should the "same shared library routine as BO2, different address" hypothesis be checked FIRST (cheap: does GoW's binary contain the same or similar-looking short-name-combine routine signature, even before confirming it's actually called on GoW's reboot path), or only after confirming GoW's arg is non-empty-but-wrong via trace? | (a) check trace first, only look for the shared routine if trace shows non-empty-but-wrong (b) check for the routine's presence in the binary regardless, in parallel | **(a)** — no point hunting for a routine that patches a buffer if the buffer was never written to begin with; sequence the cheap trace check first |

---

## 8. Definition of done (this investigation seat, not a fix)

- [ ] Dual ACK on Q1-Q4 (or recorded deferrals).
- [ ] `DETPS2_TRACE_REBOOT=1` (+ memory watch if needed) run past GoW's documented ~61M reboot event, fresh evidence captured (not just the old code comment trusted as-is).
- [ ] GoW's bug classified into one of: (a) never writes an arg at all, (b) writes a real arg that's lost/truncated in a BO2-like way, (c) writes a real arg that's lost/truncated in a Whiplash-like way, (d) something genuinely new.
- [ ] If (b)/(c)/(d): a **future, separate** design doc proposes the actual fix mechanism, following the same dual-ACK process this doc and M4-S4-MIRROR/M7-c used.
- [ ] If (a): GoW's plant is documented as a confirmed, evidence-backed permanent residual (not a "we didn't get to it" gap) in `docs/UDNL_GETVERSION_UNIFICATION.md`'s per-title retirement table.
- [ ] **This design seat:** document only — no Core implement, no GodOfWarAssist edit, unless ACK marks something trivial (unlikely given the scope here).

---

## 9. References (absolute paths)

| Artifact | Path |
|---|---|
| M4-S4 timing follow-up (what led here) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m4-s4-gow-claim-timing-followup.md` |
| M4-S4-MIRROR design (Q4 hedge, Q9 claim-tier bar) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m4-s4-ee-mirror-design.md` |
| UDNL/GetVersion unification (S0 stage definition) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\UDNL_GETVERSION_UNIFICATION.md` |
| GoW assist (force-tag/force-UDNL/mirror call sites) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\GodOfWarAssist.cs` (lines 353-434, 481-573) |
| BO2 assist (confirmed path-combine fix, reference mechanism) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\BloodOmen2SnAssist.cs` (lines 185-217) |
| Whiplash assist (confirmed UsingCD fix, reference mechanism) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\GameQuirks\WhiplashAssist.cs` |
| Generic RESET_CMD handler (confirmed not the bug) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\SonyKernelHle.cs` (lines 1717-1743) |
| Generic reboot-arg storage (confirmed not the bug) | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\src\DetPS2.Core\Sif.cs` (lines 303-354) |
| M7-c (structural precedent: "read code first, two of four guesses were already wrong, investigate before fixing") | `C:\Users\user\.grok\worktrees\windows-detps2\detps2\docs\infra-audits\m7c-path23-image-delivery-design.md` |

---

*Design/investigation-scoping only. No Core code changes in this note. GoW's empty-arg mechanism is confirmed NOT to be a generic DetPS2 packet-parsing bug (that path is faithful and shared across all titles) — it is either a fact about GoW's real boot sequence or a title-specific EE-code construction bug in the same problem family as BO2's and Whiplash's already-fixed (but mechanically different) S0 issues. Proposing a fix before knowing which would be guessing.*
