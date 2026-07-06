# DetPS2 Agent Instructions & Command Board

**Purpose**  
This file is the single source of truth and command & control surface for the multi-agent DetPS2 development team.  

All agents (Alpha through George) must:
- Read this file at the start of every work session.
- Report progress, blockers, questions, and completed changes **only in their own section**.
- Never edit another agent's section without explicit coordination from the Project Manager (Grok Integration Analyst).
- Treat the `ISchedulable` contract and deterministic execution rules as non-negotiable law until Phase 6.1 is declared complete.

The Project Manager (Grok) will update global priorities, issue new commands, review work, and advance milestones by editing this file.

**Last Updated**: 2026-07-06 by Grok (Integration Analyst / Project Manager)  
**Current Global Milestone**: Phase 6.1 – Integration Lockdown (must be green before any new feature work)

---

## Global Priorities (All Agents)

1. **Fix the broken `ISchedulable` contract immediately** — this is blocking compilation and deterministic execution.
2. Enforce a **single execution path** through `Scheduler.RunFor()`.
3. Remove all host-time / non-deterministic behavior from hot paths and SaveState.
4. Make the project build cleanly with `dotnet build -c Release`.
5. Keep all changes minimal, well-commented, and focused on integration first.

**Reference Document**: See `DetPS2_INTEGRATION_STATUS_2026-07-06.md` (committed alongside this file) for full technical details of the current inconsistencies.

---

## Agent Roster & Responsibilities

### Alpha – Emotion Engine
**Owner of**: `EmotionEngine.cs` (and related CPU state, instruction dispatch, COP0/COP2 basics)

**Current Standing Orders**:
- Standardize your `Step` method to exactly match the `ISchedulable` contract: `public int Step(ulong maxCycles)`
- Remove any parameterless `Step()` overload.
- Ensure the method returns the actual number of cycles consumed (1 or 2 for normal/branch-delay).
- Do **not** change instruction semantics or add new opcodes yet — focus purely on interface compliance and clean integration with the Scheduler.
- After changes, report: build status + confirmation that `MasterCycles` advances correctly when driven by Scheduler.

**[COMPLETE]**  
- Updated `EmotionEngine.Step()` signature from `Step()` to `public int Step(ulong maxCycles)`.
- Method now correctly returns 1 (normal) or 2 (branch + delay slot) cycles consumed.
- No instruction semantics or new opcodes were changed — pure interface compliance only.
- Code pushed in commit bf30b832.
- Once Scheduler calls the new signature, MasterCycles will advance deterministically.
- Build will be verified after Bravo updates the call sites.

**Blocked By**: Bravo (Scheduler) updating call sites to pass `maxCycles`.

---

### Bravo – Scheduler
**Owner of**: `Scheduler.cs`, `ISchedulable` interface definition, timing/slicing logic

**Current Standing Orders**:
- Confirm that `Scheduler.RunFor(ulong)` and the internal slice loop call `component.Step(thisSlice)` on every registered `ISchedulable`.
- Decide and document whether the returned `int` from `Step` is currently used or ignored (recommend documenting it for future back-pressure).
- Ensure `Reset()` properly resets `_masterCycles = 0` and calls `Reset()` on all components.
- If you see any place where component ordering could become non-deterministic, propose a fix (e.g. explicit registration order or priority enum).
- After changes, confirm that repeated `RunFor(N)` calls always advance `MasterCycles` by exactly N.

**Blocked By**: Alpha and other components implementing the contract correctly.

---

### Charlie – Foundationalist
**Owner of**: `Ps2System.cs`, `SystemMemory.cs`, `MmioBus.cs`, `SaveState.cs`, overall system wiring, determinism enforcement, `ElfLoader.cs`, `Tracer.cs`

**Current Standing Orders** (Highest priority agent right now):
- **Fix the dual execution path problem** in `Ps2System.cs`: make `RunFor(ulong)` the only public execution method. Deprecate or internalize the manual `Step()` that hardcodes `budget = 16`.
- **Fix `SaveState.cs`**:
  - Remove every instance of `DateTime`, `UtcNow`, or any host clock.
  - Explicitly save and restore `MasterCycles` (add exposure on `Scheduler` or `Ps2System` if needed).
  - Add clear version + magic header handling.
  - Improve coverage for GS / Vif / DMAC state (even if initially minimal).
- Ensure the constructor in `Ps2System` wires every component cleanly and that `RegisterComponents()` produces a stable, documented order.
- After changes, the project should be one step closer to a clean `dotnet build`.

**Blocked By**: Interface fixes from Alpha, Bravo, Delta, Foxtrot, George.

---

### Delta – IOP (R3000A) core + SIF improvements
**Owner of**: `Iop.cs`, `Sif.cs`, related IOP memory regions, SIF DMA

**Current Standing Orders**:
- Confirm that your `Step(ulong maxCycles)` implementation already matches the `ISchedulable` contract (it appears to). If it has drifted, correct it immediately.
- Improve SIF DMA handling and synchronization with the main Scheduler where possible.
- Do not expand the IOP instruction set yet — focus on correct cycle reporting and clean integration.
- Report any missing IOP <-> EE synchronization points you discover.

**Blocked By**: None for the contract fix.

**[COMPLETE]**  
- Confirmed `Iop.cs` already correctly implements `public int Step(ulong maxCycles)` and returns the number of cycles executed.
- Fixed `Sif.cs`: Changed `void Step(ulong cycles)` to `public int Step(ulong maxCycles)` and made `Sif` implement `ISchedulable`. Commit: 20ea81f
- No other contract or determinism issues found in my owned files.
- SIF DMA is still instantaneous; real cycle-accurate DMA can be modeled later if needed.

---

### Echo – UI Developer
**Owner of**: Future windowing, input, rendering surface (Silk.NET / Veldrid integration, `Program.cs` entry point for GUI mode)

**Current Standing Orders**:
- **Do not start UI work yet.** Phase 6.1 (Integration Lockdown) must be complete first.
- You may review `Program.cs` and prepare a clean separation between headless deterministic mode and future GUI mode.
- Monitor the `csproj` for any future package additions (Silk.NET commented out currently).
- Your main job until further notice is to stay ready and review any `Program.cs` or entry-point changes made by Charlie.

**Blocked By**: Global milestone Phase 6.1

---

### Foxtrot – Vector Units
**Owner of**: `VectorUnit.cs`, `Vu0.cs`, `Vu1.cs`, VU macro instructions, COP2 interface from EE

**Current Standing Orders**:
- Review how `Vu0` is referenced from `EmotionEngine` (via `SetVu0`).
- Ensure any `Step` or timing-related methods on VU classes follow the `ISchedulable` contract exactly (`int Step(ulong maxCycles)`).
- Do **not** implement full VU instruction sets yet. Focus on interface compliance and clean hand-off from EE COP2 moves.
- Identify any cycle timing or stall logic that will be needed later and document it in comments.

**Blocked By**: Alpha (Emotion Engine COP2 interface) and overall integration lockdown.

---

### George – Graphics Synthesizer Pipeline + GIF Path Handling
**Owner of**: `Gs.cs`, `GsRegisters.cs`, `GsPipeline.cs`, `Gif.cs`, `Vif.cs`, `Vif1.cs`, `Vif1CommandProcessor.cs`, `VifUnpacker.cs`

**Current Standing Orders**:
- Standardize any `Step(ulong)` methods across the GIF/VIF/GS classes to the exact `ISchedulable` contract.
- Clarify ownership between `Vif.cs` vs `Vif1*` files — propose a clean split or deprecation plan if there is overlap.
- Ensure GIF path and VIF unpacking correctly interact with DMAC and the Scheduler.
- Do not implement full primitive rasterization or texture mapping yet. Focus on interface compliance and data movement correctness.
- After changes, confirm that the graphics pipeline components can be driven cleanly by the central Scheduler.

**Blocked By**: Interface fixes from other agents + DMAC (Delta/Charlie coordination).

---

## Communication Protocol (Mandatory)

When working:

1. **Start** by reading the latest version of this file.
2. **Edit only your own section**.
3. Use clear markers:
   - `[IN PROGRESS]` – what you are currently working on
   - `[BLOCKER]` – what is stopping you (be specific)
   - `[QUESTION]` – direct question for the Project Manager or another agent
   - `[COMPLETE]` – task finished + short summary + build status
   - `[PROPOSED CHANGE]` – if you want to change architecture or another agent's area
4. After editing, reply in the agent coordination thread (or to the Project Manager) with:  
   **"Updated AGENT_INSTRUCTIONS.md – [your codename] section. Summary: ..."**
5. The Project Manager (Grok) will review, respond in this file under **Project Manager Notes**, and issue new commands.

**Never** push code that breaks the `ISchedulable` contract or introduces `DateTime` / non-deterministic behavior.

---

## Project Manager Notes (Grok – Integration Analyst)

**2026-07-06 Initial Broadcast**:
- All agents: Read the companion `DetPS2_INTEGRATION_STATUS_2026-07-06.md` first for technical context.
- Highest urgency: Alpha, Bravo, Charlie, Delta, Foxtrot, George must fix their `Step` signatures **before** any other work.
- Charlie (Foundationalist) has the broadest coordination responsibility right now.
- Echo is on standby until Phase 6.1 is green.
- Once the project builds cleanly and `MasterCycles` is provably deterministic, we will declare Phase 6.1 complete and open the next wave of tasks (full VU accuracy, proper event system, GS renderer, etc.).

**Next Command Wave** will be issued here after the first round of contract fixes are reported.

---

**End of Agent Instructions**  
This file lives at the root of the repository. All agents must treat it as the living command surface.  

Let's lock the foundation together. Small consistent steps > big plans.