# C1 finding — IOP stack-region overlaps (pre-existing, blocks naive T1)

**Status:** finding, code-verified — blocks T1-as-sketched, needs dual-ACK on fix approach
**Date:** 2026-08-04
**Tip:** `0ec10e7` (M1 CHCR single-round landed, verified)
**Author:** Claude, per [c1-iop-thread-table-pressure-design.md](c1-iop-thread-table-pressure-design.md) TP-Q4 ("implement only after dual-ACK + stack layout audit")

---

## 0. One-line

TP-Q4's required stack-layout audit found **two real, pre-existing physical-address
overlaps** in the *already-shipped* 32-slot table — independent of any slot-count raise.
T1 as literally sketched (bump `MaxIopThreadSlots` 32→64 on the same per-id formula) would
extend this problem, not just risk a new one. Fix the layout first.

---

## 1. Method

Hand arithmetic first (see design doc TP-Q4 intent), then verified against real code paths
via the actual public `Iop` API — `CreateDormantThreadContext` (same call `CreateThread` HLE
uses) and `CreateThreadContext(0, RealRpcDispatchStackTop, ...)` (same call
`TryEnterRealRpcDispatch` uses) — not simulated, the real allocation code. Temp diagnostic
(`Tests/TempStackAudit.cs`, gated on `DETPS2_TEMP_STACK_AUDIT=1`) added, run, and fully
reverted (`git status` clean after).

Sequence: enable multi-thread scaffolding, fill 30 dormant thread contexts (ids 1-30, the
same path real `CreateThread` HLE calls take), then request the RealSifRpc dispatch scratch
context (lands on whatever id is still free — id 31 in this run).

## 2. Result (real, code-computed ranges)

```
IOP_RAM bound = 0x200000
boot(id0)                    [0x1EE000,0x1F0000)
thread(id1)                  [0x1B0000,0x1B2000)   (module-entry arena, ids 1-8)
...
thread(id8)                  [0x1BE000,0x1C0000)
thread(id9)                  [0x1D2000,0x1D4000)   (ThreadStackRegion formula, ids 9-30)
...
thread(id15)                 [0x1DE000,0x1E0000)
thread(id16)                 [0x1E0000,0x1E2000)
...
thread(id23)                 [0x1EE000,0x1F0000)
...
thread(id30)                 [0x1FC000,0x1FE000)
RealSifRpc-scratch(id31)     [0x1DE000,0x1E0000)

=== Overlap check ===
OVERLAP: boot(id0) [0x1EE000,0x1F0000) <-> thread(id23) [0x1EE000,0x1F0000)
OVERLAP: thread(id15) [0x1DE000,0x1E0000) <-> RealSifRpc-scratch(id31) [0x1DE000,0x1E0000)
```

Two identical-range collisions:

1. **Boot thread (id0) vs id23.** `EnsureThreadTable`/`ResetThreadTable` give boot a
   *fixed* stack top of `0x1F0000` (`IopModuleHost.DefaultModuleStack`, when live SP is
   still 0). The id-derived formula (`ThreadStackRegionBase + (id+1)*ThreadStackSlotSize`)
   produces the exact same top for id=23. Any real disc thread that lands on slot 23 shares
   physical stack bytes with the boot thread.

2. **RealSifRpc scratch vs id15.** `RealRpcDispatchStackTop=0x1E0000` is hardcoded and
   `CreateThreadContext` takes an *explicit* stackTop from the caller — it does **not**
   derive the stack range from whatever table id it happens to land on. So this collision is
   with the **physical address range**, not a specific id: whichever real thread lands on
   slot 15 (reachable any time ≥15 threads are concurrently live) shares
   `[0x1DE000,0x1E0000)` with RealSifRpc's dispatch scratch stack, regardless of which slot
   id RealSifRpc itself is assigned.

Both are **pre-existing in the current shipped 32-slot design** — they do not require
raising `MaxIopThreadSlots` to manifest, only enough concurrently-live real threads (≥15 for
#2, ==23 for #1). The IOP is cooperative/single-live-context (only one context's *registers*
are live at a time), but **stack contents are physical bytes in IOP RAM, not part of the
saved context struct** — a parked thread's stack data can be silently clobbered by whichever
other mechanism next writes into the same physical range, then corrupt that thread on resume.

Comment at `Iop.cs:78-81` ("Below THREADMAN entry slots (0x1D0000) and RealSifRpc scratch
(0x1E0000)") is describing intent, not actual layout — the id-derived formula's high end
(ids 9-31) actually runs *through* both of those addresses, not below them.

Additionally confirmed (boundary, not yet an observed overlap): id=31's stack top computes
to exactly `0x200000`, the literal edge of real 2MB IOP RAM — the current 32-slot table
already uses the full available range with zero headroom.

## 3. Why this blocks T1-as-sketched

The design doc's T1 sketch says "re-check bases if N grows" as if the current bases are
already non-overlapping and only need re-checking after growth. They are not — growing N on
the same formula only pushes the collision-reachable id range higher and adds more ids that
alias the fixed 0x1E0000/0x1F0000 addresses at different offsets, without fixing the root
cause. A naive 32→64 raise is not safe to implement on the current formula.

## 4. Proposed fix direction (for dual-ACK, not yet implemented)

Reserve the two fixed-address regions (`RealRpcDispatchStackTop` range and boot's
`0x1F0000` range) *out of* the id-derived allocation space, rather than letting the
per-id formula wander through them. Concretely: shrink `ThreadStackRegionBase`'s usable
span so its last id lands below `0x1DE000`, and carve boot + RealSifRpc scratch into their
own dedicated slots outside the id-derived arithmetic entirely (they already have fixed,
known addresses — they just need to be *excluded*, not coincidentally missed). This also
naturally resolves the id=31-at-boundary issue since the usable span would end earlier.

This is a real (if small) layout redesign, not a mechanical constant bump — proposing it go
through the same dual-ACK step as T1 itself before any Core edit.

## 5. Dual-ACK (new)

| ID | Question | Bias |
|----|----------|------|
| **TP-Q5** | Fix the two pre-existing overlaps (boot/id23, RealSifRpc/id15) before touching `MaxIopThreadSlots` at all? | **Yes** — real corruption risk, independent of T1 |
| **TP-Q6** | Fix direction: carve fixed-address regions out of the id-derived formula (§4) rather than raise slot count first? | **Yes** |
| **TP-Q7** | Once carved, is 64 slots on the shrunk id-derived span still worth pursuing as originally scoped, or does the shrink make the effective capacity gain too small to bother? | Open — depends on §4 arithmetic once carved |

---

## 6. Status

- [x] Hand arithmetic
- [x] Code-verified via real public API (temp diagnostic, fully reverted)
- [x] Two real overlaps confirmed, root cause identified
- [ ] Dual-ACK on fix direction (§5)
- [ ] **No Core** until ACK
