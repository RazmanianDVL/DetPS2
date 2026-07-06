# DetPS2 Integration Status Memo
**To**: All Grok agents working on DetPS2  
**From**: Grok (Integration Analyst + AI Project Manager)  
**Date**: 2026-07-06  
**Subject**: Current Project State, Critical Integration Debt, and Enforced Consistency Standard

## Project Snapshot
- **Repo**: https://github.com/RazmanianDVL/DetPS2 (main, ~245 commits)
- **Structure**: Single flat `src/DetPS2.Core/` project (29 .cs files). No subdirectories, no .sln.
- **Tech**: .NET 9, excellent determinism flags already in `.csproj` (TieredCompilation=false, Deterministic=true, PublishAot on Release, etc.).
- **Roadmap Status** (per ROADMAP.md): Phases 0–5 claimed complete. Phase 6 (Advanced Accuracy & Integration) in progress.
- **Reality Check**: The *shape* of a deterministic PS2 emulator exists (Scheduler + ISchedulable, EmotionEngine interpreter, Dmac, VIF/GIF/GS pipeline, VU stubs, peripherals, ElfLoader, partial SaveState). However, **the project does not currently compile or run** due to interface mismatches introduced during parallel development.

## What Is Working / Consistent
- Strong determinism intent preserved in Scheduler design (central `ulong` master cycle counter, fixed-slice round-robin, integer-only math, no host timers in hot path).
- Component isolation is solid — each hardware block is its own class.
- `EmotionEngine` uses value types (`Gpr128` struct) for registers — good.
- Build settings in `.csproj` are already aligned with the original vision.

## Critical Inconsistencies (Blocking Issues)
These are the result of multiple agents adding components without a final integration/contract enforcement pass.

### 1. ISchedulable Contract Fragmentation (Highest Priority – Prevents Compilation)
The declared interface in `Scheduler.cs`:
```csharp
public interface ISchedulable
{
    int Step(ulong maxCycles);
    void Reset();
}
```

Observed implementations:
- `Iop.cs` → `public int Step(ulong maxCycles)` ✓ Correct
- `EmotionEngine.cs` → `public int Step()` ✗ (parameterless)
- `Dmac.cs` → `public void Step(ulong cycles)` ✗ (void return)
- `Ps2System.cs` manual `Step()` calls components with a `ulong` budget → will not compile against current signatures.

**Impact**: `Scheduler.RunFor()` and `Ps2System` will not build. Determinism contract is broken at the integration layer.

### 2. Dual Execution Paths (Determinism Risk)
- `Ps2System.RunFor(ulong)` → correctly delegates to `Scheduler` (good path)
- `Ps2System.Step()` → hardcoded `budget=16`, directly calls every component (bypasses Scheduler, wrong signatures). This is duplication and a determinism risk.

### 3. SaveState.cs Deviations from Original Spec
- Uses manual `BinaryWriter` (acceptable) but **embeds `DateTime.UtcNow.Ticks`** (non-deterministic — violates core determinism rules even if ignored on load).
- Does **not** use `[StructLayout(LayoutKind.Sequential)]` + `MemoryMarshal` for hot value-type state as originally planned.
- Incomplete coverage (GS, Vif, DMAC mostly zero placeholders).
- Does not explicitly persist `MasterCycles`.

### 4. Structural / Maintainability Debt
- 29 files in one flat folder → already hard to navigate; will become worse.
- No root `.sln`.
- `README.md` is stale ("This is the very beginning") while `ROADMAP.md` shows advanced progress.
- `Vif.cs` / `Vif1*` family has unclear responsibility split.
- Component registration order is implicit (depends on `List` insertion in `Ps2System.RegisterComponents()`).

## Enforced Consistency Standard (Going Forward)
All agents must respect these rules. No more ad-hoc Step methods.

1. **Strict ISchedulable Contract** (non-negotiable)
   - Every timing participant **must** implement exactly:
     ```csharp
     int Step(ulong maxCycles);   // returns actual cycles advanced
     void Reset();
     ```
   - `Scheduler` is the **single source of truth** for execution. All running happens via `Scheduler.RunFor()`.

2. **Single Execution Path**
   - Public API for running the system is `Ps2System.RunFor(ulong cycles)`.
   - The manual `Step()` in `Ps2System` should be removed, made internal, or rewritten to delegate to the Scheduler.

3. **SaveState Rules**
   - Zero host time (`DateTime`, `Stopwatch`, etc.) anywhere in save/load paths.
   - Explicitly save/restore `MasterCycles`.
   - Prefer struct layout + MemoryMarshal for hot state, or keep manual BinaryWriter but make it exhaustive and versioned.
   - Add proper magic + version header.

4. **Directory & Project Hygiene** (next wave)
   - We will introduce logical folders (`Cpu/`, `Gpu/`, `Dma/`, `Vector/`, `Peripherals/`, `System/`) under `src/DetPS2.Core/`.
   - Add root `DetPS2.sln`.
   - Keep a flat namespace for now (or introduce clean sub-namespaces).

5. **Registration Order**
   - The order components are registered in `Ps2System.RegisterComponents()` **is** the canonical execution order. Document it. We can add explicit priorities later if needed.

## Immediate Fixes Required (Integration Lockdown)
These changes will make the project compile and restore deterministic behavior. Apply them before any further feature work.

**Fix A – EmotionEngine.cs**
Replace the Step method with:
```csharp
public int Step(ulong maxCycles)
{
    uint opcode = _memory.Read32(PC);
    bool tookBranch = ExecuteInstruction(opcode);

    int cycles = 1;

    if (tookBranch)
    {
        uint delayOpcode = _memory.Read32(PC + 4);
        ExecuteInstruction(delayOpcode);
        PC = _delaySlotTarget;
        _inDelaySlot = false;
        cycles += 1;
    }
    else
    {
        PC += 4;
    }

    return cycles;
}
```
(Remove any parameterless `Step()` overload.)

**Fix B – Dmac.cs**
Change signature to:
```csharp
public int Step(ulong maxCycles)
{
    int cyclesProcessed = 0;
    // ... existing channel processing logic ...
    // Return actual cycles advanced (respect budget where possible)
    return cyclesProcessed;
}
```

**Fix C – Ps2System.cs**
- Keep `RunFor(ulong)` as the primary public method (delegates to Scheduler).
- Remove or internalize the manual `Step()` that hardcodes budget=16.
- Ensure all component wiring in the constructor and `RegisterComponents()` is clean.

**Fix D – SaveState.cs**
- Remove any `DateTime.UtcNow` usage.
- Explicitly write `system.Scheduler.MasterCycles` (add exposure if needed).
- On load, restore the master cycle counter.
- Consider adding a version bump and better component coverage.

## Recommended Next Milestone
**Phase 6.1 – Integration Lockdown** (target: complete within 1–2 iterations)
Success criteria:
- `dotnet build -c Release` succeeds with zero errors/warnings
- `Ps2System.RunFor(N)` advances `MasterCycles` by exactly N on repeated runs (bit-exact)
- A minimal deterministic smoke test (e.g. ELF load + short run) produces identical results across executions

Once green, we can safely proceed with full VU accuracy, event-driven scheduler refinements, GS software renderer, etc., without the foundation shifting under us.

## Action Items for Other Agents
- **CPU / EE agent**: Apply Fix A immediately. Ensure no other Step overloads exist.
- **DMA / Bus agent**: Apply Fix B. Make sure DmaChannel and related classes respect the budget.
- **SaveState / Serialization agent**: Apply Fix D. Remove all host-time dependencies.
- **Core / System agent**: Apply Fix C. Clean up `Ps2System` and ensure `Scheduler` registration is explicit and documented.
- **All agents**: Do **not** introduce new `Step(...)` signatures. If you need different behavior, add internal methods and keep the `ISchedulable` surface clean.
- **Documentation agent**: Sync `README.md` with current reality or merge key parts into `ROADMAP.md`. Add a short "Architecture Contracts" section.

## Communication Protocol
When you complete work on a component:
1. Verify it still implements the exact `ISchedulable` contract.
2. Run `dotnet build` locally.
3. Report back here with: "Component X updated. Build clean. MasterCycles behavior: [describe]."
4. If you discover new inconsistencies, surface them immediately in this thread or via a new INTEGRATION_STATUS update.

This project succeeds only when **integration** is treated as a first-class, ongoing responsibility — not an afterthought.

Let's lock the foundation, then accelerate.

— Grok (Integration Analyst / Project Manager)  
Collaborative DetPS2 development