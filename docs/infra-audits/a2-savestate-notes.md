# A2 save-state notes: `_latchedAtCycle[16]` serialization

**Scope:** How DetPS2 save/load works today for INTC, what A2 (min IRQ dispatch latency) adds, and how to serialize `Intc._latchedAtCycle` without breaking old save files.  
**Status:** audit / design only — no code changes in this note.  
**Date:** 2026-08-04  
**Files:** `src/DetPS2.Core/Intc.cs`, `SaveState.cs`, `EmotionEngine.cs` (A2 gate), subsystem `WriteState`/`ReadState` peers.

---

## 1. What A2 is

A2 is the **minimum interrupt-dispatch latency** gate in `EmotionEngine.TryDispatchRegisteredIntcHandler`:

- On each genuine **CpuLatched 0→1** edge, `Intc.Raise` / `RearmCpuLatch` stamps  
  `_latchedAtCycle[src] = Intc.CurrentCycleForTrace`.
- Dispatch skips a source while  
  `CurrentCycle() - LatchedAtCycle(src) < Intc.MinDispatchLatencyCycles` (currently **16**).
- Purpose: stop a fresh IRQ from landing on the literal first instruction of an arbitrary callee (live: Blood Omen 2 DMAC src=14 → stack-frame corruption). Sources that have been pending a while already “paid” the latency and are not re-delayed.
- Escape hatch for A/B only: env `DETPS2_DISABLE_A2_IRQ_LATENCY=1`.

Related INTC fields that interact with A2 / sticky STAT (not all saved today):

| Field | Role | Saved today? |
|-------|------|----------------|
| `Stat` | Sticky MMIO STAT | Yes (2× `uint` via SaveState) |
| `Mask` | MMIO MASK | Yes |
| `CpuLatched` | COP0 edge latch | **No** — `RestoreState` forces `0` |
| `_latchedAtCycle[16]` | A2 edge timestamps | **No** |
| `_statHoldUntil[16]` | VBlank write-1-clear hold | **No** |

---

## 2. Current save/load architecture

### 2.1 Top-level format (`SaveState.cs`)

- Magic `0x44505332` (`'DPS2'`).
- `CurrentVersion = 6` (uncompressed). Compressed wrapper ORs `0x80000000` into version and prepends raw payload length + deflate of the full uncompressed blob (which itself starts with magic + version again).
- Load accepts `version ∈ [3, CurrentVersion]` except **v5 is refused** after the v6 Kernel THREADMAN field expansion (stream would desync).

**v5/v6 body order** (`Save` / `LoadV5`):

```
MasterCycles
RDRAM, IOP RAM, SPR
EE.WriteState / Iop.WriteState
SIF (3× uint, load is fire-and-forget)
Pad
Gs / Vu0 / Vu1
Intc: Stat, Mask          ← only two uints; no Intc.WriteState
Dmac / Cdvd / Spu2 / Timers
Kernel.WriteState
Sony? + Sony.WriteState
```

### 2.2 Subsystem pattern (peers)

Most hardware blocks own a pair:

```csharp
public void WriteState(BinaryWriter w) { /* fixed field order */ }
public void ReadState(BinaryReader r)  { /* same order */ }
```

Examples: `Dmac`, `Cdvd`, `Spu2`, `EeTimers`/`TimerChannel`, `Gs`/`GsRegisters`, `EmotionEngine`, `Iop`, `KernelHle`, `SonyKernelHle`.

`SaveState` only **sequences** those calls; layout ownership lives in the subsystem.

### 2.3 Intc is the outlier

`Intc` has **no** `WriteState`/`ReadState`. Call sites:

```csharp
// Save
writer.Write(system.Intc.Stat);
writer.Write(system.Intc.Mask);

// Load (v4 and v5/v6 paths)
system.Intc.RestoreState(stat, mask);
```

`RestoreState` today:

```csharp
Stat = stat;
Mask = mask;
CpuLatched = 0;   // deliberately: avoid IRQ storm on load
// does not touch _latchedAtCycle or _statHoldUntil
```

Also: `Reset()` clears `_statHoldUntil` but **does not** clear `_latchedAtCycle` (stale stamps can survive reset until overwritten by the next edge).

### 2.4 Version-bump precedent: v5 → v6

When Kernel THREADMAN fields were **appended inside** `KernelHle.WriteState`/`ReadState` without a dual-path reader:

- `CurrentVersion` became **6**.
- Loading a **v5** blob with the new `Kernel.ReadState` would mis-consume the stream (extra fields expected after each thread/sema).
- Policy: **refuse v5** (`version >= 5 && version < 6 → false`), keep v3/v4 on the old partial path, treat v6 as the only full-system path.

That is the “hard break” style. Soft appends are also possible **when the call site can branch on file version** before reading a subsystem’s tail.

---

## 3. Why A2 needs save awareness

After a clean `RestoreState`:

1. `CpuLatched = 0` → A2 is **inert** until the next hardware `Raise` / `RearmCpuLatch`.
2. That next edge stamps `_latchedAtCycle[src] = CurrentCycleForTrace` (which should track restored `MasterCycles` via the usual tick path).
3. So for **current** restore semantics (latch cleared), forgetting `_latchedAtCycle` is mostly harmless: zeros or stale values only matter once a bit is latched, and the next edge overwrites the stamp.

Serialization becomes **necessary for fidelity** if/when load restores **non-zero `CpuLatched`** (true mid-IRQ resume), or when `_statHoldUntil` is restored so VBlank hold windows survive load. Without matching timestamps:

| Restored state | `_latchedAtCycle` missing/zero | Effect |
|----------------|--------------------------------|--------|
| `CpuLatched` bit set, `MasterCycles ≥ 16` | `cycle - 0 ≥ 16` | A2 **inactive** (immediate dispatch) — “already paid” default |
| `CpuLatched` bit set, `MasterCycles < 16` | `cycle - 0 < 16` | A2 **holds** up to 16 cycles — rare early-boot edge |
| `CpuLatched` bit set, real stamp was `MasterCycles - 3` | lost → zero | May **skip** the remaining 13-cycle hold → slightly earlier dispatch than live |

**Safe default for old saves:** leave `_latchedAtCycle` all **zeros** (and keep clearing `CpuLatched` unless intentionally expanding restore). That matches “long-pending / already paid” for any post-boot MasterCycles and matches today’s storm-avoidance policy.

---

## 4. Recommended serialization plan (do not break old saves)

### 4.1 Prefer soft version bump → **v7**, not a hard refuse of v6

Unlike Kernel v5→v6, the Intc blob is a **small, isolated slice** in the middle of `LoadV5`. Branching on `version` at that slice preserves v6 (and keeps v3/v4 as today).

**Do not** unconditionally lengthen the Intc read inside a shared path that still claims to load v6 files: that desyncs `Dmac.ReadState` and everything after it.

### 4.2 Proposed layout

**v6 and earlier (unchanged):**

```
uint Stat
uint Mask
→ RestoreState(stat, mask)   // CpuLatched=0; arrays default/zero
```

**v7+ Intc blob (owned by `Intc.WriteState` / `ReadState`):**

```
uint Stat
uint Mask
uint CpuLatched              // optional but recommended if A2 is restored for real
ulong[16] latchedAtCycle     // always 16 entries; InterruptSource uses 0..14
// optional second array if VBlank hold must survive load:
// ulong[16] statHoldUntil
```

Wire order in `SaveState.Save` / `LoadV5` (rename or parameterize as `LoadV5Plus`):

```text
… Vu1 …
if (version >= 7)
    system.Intc.ReadState(reader);     // full blob
else {
    uint stat = reader.ReadUInt32();
    uint mask = reader.ReadUInt32();
    system.Intc.RestoreState(stat, mask);
    // Ensure A2 arrays are zero: Array.Clear(_latchedAtCycle) inside RestoreState/Reset
}
… Dmac …
```

Writer always emits **v7** layout when `CurrentVersion = 7`.

### 4.3 API shape (mirror peers)

```csharp
// Intc.cs — conceptual
public void WriteState(BinaryWriter w)
{
    w.Write(Stat);
    w.Write(Mask);
    w.Write(CpuLatched);
    for (int i = 0; i < 16; i++) w.Write(_latchedAtCycle[i]);
    // optional: for (int i = 0; i < 16; i++) w.Write(_statHoldUntil[i]);
}

public void ReadState(BinaryReader r)
{
    Stat = r.ReadUInt32();
    Mask = r.ReadUInt32();
    CpuLatched = r.ReadUInt32();
    for (int i = 0; i < 16; i++) _latchedAtCycle[i] = r.ReadUInt64();
    // optional hold array…
    _onChanged?.Invoke();
}

// RestoreState: keep as the v≤6 path; also clear A2 arrays explicitly
public void RestoreState(uint stat, uint mask)
{
    Stat = stat;
    Mask = mask;
    CpuLatched = 0;
    Array.Clear(_latchedAtCycle);
    Array.Clear(_statHoldUntil);   // consistent with Reset intent
    _onChanged?.Invoke();
}
```

### 4.4 Version table after change

| File version | Load path | Intc fields | A2 arrays |
|--------------|-----------|-------------|-----------|
| 3 | `LoadV3OrV4` | none | N/A |
| 4 | `LoadV3OrV4` | Stat+Mask via `RestoreState` | default zeros |
| 5 | **refused** (Kernel) | — | — |
| 6 | full body, Intc 2×uint | Stat+Mask, CpuLatched forced 0 | **default zeros** |
| 7+ | full body, `Intc.ReadState` | Stat+Mask+CpuLatched+16×ulong (+ optional hold) | **restored** |

New builds: save as v7 only; load v6 with defaults (no refuse). Old builds cannot load v7 (existing `version > CurrentVersion` guard) — acceptable and consistent with every prior bump.

### 4.5 Anti-patterns (do not do)

1. **Append 16 ulongs without bumping version** and always reading them → v6 files desync at DMAC.  
2. **Bump version and change `Kernel`-style ReadState without dual path while still advertising “v6 loads”** → silent corruption or refuse-everything.  
3. **Save `_latchedAtCycle` but leave `CpuLatched` forced to 0 and never document it** → wasted bytes; fine if temporary, but prefer restoring both or neither.  
4. **Rely on “end of stream” probing** — Intc is mid-file; length-based optional tails are the wrong tool here. Use the global version word.

---

## 5. Semantics checklist for implementers

1. **Array length is 16**, not 15: field is `ulong[16]`; `InterruptSource` max is `DmaController = 14`. Always write/read **16** for fixed layout.  
2. **Stamp source of truth** is CpuLatched’s 0→1 edge (not Stat’s edge) — do not invent timestamps from `Stat` alone on load.  
3. **Zero default** after old load: A2 inactive for any source until a new edge; matches “already paid” for large MasterCycles.  
4. **Clear on `Reset` and `RestoreState`**: fix the current gap so stale `_latchedAtCycle` cannot survive reset.  
5. **`CurrentCycleForTrace`**: must stay coherent with restored `MasterCycles` after load (already required for hold windows and A2).  
6. **Tests** (when implementing):  
   - Round-trip: set non-zero `_latchedAtCycle` / `CpuLatched` via Raise, save, load, compare stamps.  
   - Cross-version: save as v7, hand-edit or fixture a v6 blob (Stat/Mask only), confirm load succeeds and A2 arrays are zero.  
   - Existing smokes: `SaveState_MasterCyclesRoundTrip`, `SaveState_FullSubsystemRoundTrip`, `SaveState_CompressesEmptyRam`, `Intc_VBlankStartStickyForPollers`.  
7. **Optional `_statHoldUntil`**: same version gate; default zeros means hold expired → software clear works immediately after old load (acceptable; sticky poll window only matters live mid-VBlank).

---

## 6. Minimal change set (when code lands)

| File | Change |
|------|--------|
| `SaveState.cs` | `CurrentVersion = 7`; doc comment for v7; pass version into load body; Intc branch |
| `Intc.cs` | `WriteState`/`ReadState`; clear arrays in `Reset`/`RestoreState` |
| `Tests/SmokeTests.cs` | A2/Intc round-trip + optional v6-compat fixture |
| This doc | mark implemented when done |

No need to touch Dmac/Cdvd/Kernel field orders if Intc’s expanded tail is gated solely by the top-level version.

---

## 7. Summary recommendation

| Question | Answer |
|----------|--------|
| Must `_latchedAtCycle` be saved for correct A2 after load? | **Yes for full fidelity** once `CpuLatched` can be non-zero post-load; **optional under current RestoreState** (latch cleared). |
| How to avoid breaking old saves? | **Bump to v7**; keep reading **only Stat+Mask** for `version < 7`; **zero** A2 arrays on that path. |
| Refuse v6 like v5? | **No** — soft branch at Intc slice is enough. |
| Default for missing data? | **All-zero** `_latchedAtCycle` (and keep `CpuLatched = 0` on the legacy path). |

This matches the project’s subsystem `WriteState`/`ReadState` ownership model and the safer of the two versioning styles used in-tree (gated append vs hard refuse).
