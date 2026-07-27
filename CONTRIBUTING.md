# Contributing to DetPS2

**New here?** Read [`docs/DEVELOPER_GUIDE.md`](docs/DEVELOPER_GUIDE.md) first — a full map of
every subsystem, the HLE/interrupt architecture, and (§7) the `GameQuirks` SDK for contributing
per-title fixes without touching shared core files. This document is the process/rules layer on
top of that.

## Principles

1. **Determinism first** — no host clocks in core or save paths (`FLOAT_POLICY.md`).
2. **Single run API** — `Ps2System.RunFor` / `Scheduler.RunFor` only.
3. **`ISchedulable` contract** — `int Step(ulong maxCycles); void Reset();`
4. **Legal** — never commit BIOS, ISOs, or game dumps.
5. **Tests** — every phase feature needs a smoke test in `Tests/SmokeTests.cs`.

## Workflow

```bash
dotnet build DetPS2.slnx -c Release
dotnet run --project Tests -c Release
dotnet run --project src/DetPS2.Core -c Release
dotnet run --project src/DetPS2.Desktop -c Release
```

## Project layout

| Path | Role |
|------|------|
| `src/DetPS2.Core` | Emulator core (pure C#, determinism) |
| `src/DetPS2.Core/GameQuirks` | Per-title HLE fix modules (`IGameQuirkModule`) — see `docs/DEVELOPER_GUIDE.md` §7 |
| `src/DetPS2.Desktop` | Avalonia UI / debugger surface |
| `Tests` | Smoke / regression suite |
| `docs/DEVELOPER_GUIDE.md` | Full architecture map + how to integrate (start here) |
| `docs/TITLE_HACKS.md` | Log of per-title workarounds and why a general fix wasn't possible |
| `ROADMAP.md` | Full phase history (0–56) |
| `ARCHITECTURE.md` | Contracts and registration order |
| `COMPATIBILITY.md` | Title / path tracker |

## Architecture freeze (Phase 11)

The following are **stable contracts** — change only with a migration note:

- `ISchedulable` / `Scheduler.RunFor` semantics (exact MasterCycles budget)
- Save state magic `DPS2` + versioned payload (v4 compressed envelope)
- Input tape magic `INPR` v1
- HLE syscall numbers in `BiosHle` (document renumbers)
- Golden FB hash tests for `RenderTestScene` after optimisations

## Debugger / tracer

- Breakpoints: `system.Debugger.AddBreakpoint(addr)`; EE halts in `Step` when enabled
- Tracer: `system.Tracer.Enable()`; format `C={cycle} PC=... OP=...`; diff via `Tracer.Diff` (see `docs/TRACE_DIFF.md`)

## Netplay / replay

- Record: `InputRecording.StartRecording()` while running
- Replay: serialize tape, `StartPlayback`, same `RunFor` schedule → identical cycles + FB hash
- `NetplaySession` lockstep quanta for future LAN binding

## Pull requests

- Keep diffs focused; log dated findings in `docs/DEVELOPER_GUIDE.md` when investigating a bug or bring-up blocker
- Do not add P/Invoke to the core hot path without an issue discussion
