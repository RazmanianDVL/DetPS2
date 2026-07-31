# DetPS2Sharp — Deterministic PS2 Emulator in Pure C#

**Goal**: A clean-slate PlayStation 2 emulator written entirely in modern C# (.NET 9), with **determinism** and **correctness over “working”** as core design principles.

**Correctness doctrine**: we do **not** take host shortcuts that fake console behavior (e.g. FFmpeg boot logos, synthetic UI paint, invented I/O). Soft-GS truth and honest residuals beat a flashy wrong screen. See **[docs/CORRECTNESS.md](docs/CORRECTNESS.md)**.

## Status — **v0.1.0 Foundation** (July 2026)

**Versioning policy**: the version number tracks real, user-visible commercial playability —
**not** internal engineering completeness. **`1.0.0` is reserved for ≥10% of
[docs/TARGET_CATALOG.md](docs/TARGET_CATALOG.md)'s titles fully playable start-to-finish with no
errors.** As of `v0.1.0`, **zero** commercial titles have even reached a main menu — an earlier
version scheme reached "v3.1.0" purely from finishing internal/synthetic engineering phases, which
was a misleading way to represent that, and has been retired (see `src/DetPS2.Core/VersionInfo.cs`).

**What is done**: a deterministic pure-C# PS2 engineering foundation — EE/IOP interpreters + real
ALU JIT, software GS, kernel HLE, save states, netplay/rollback infrastructure, and CLI/Desktop
tooling — all verified only against synthetic fixtures and homebrew, not real commercial play.
**What is not done**: any commercial title reaching its main menu, let alone being playable.
**Authoritative list**: **[COMPLETENESS.md](COMPLETENESS.md)**.

**Active work (pivot 2026-07-30)**: the emulator **fully uses real BIOS/disc IRX on the IOP** (not
an optional “mode”) — same model as other PS2 emulators. C# owns EE/Soft-GS/devices; IOP modules
**load and execute**. Phase plan: **[docs/IRX_EXECUTION_PHASE_PLAN.md](docs/IRX_EXECUTION_PHASE_PLAN.md)**.
Epic: GitHub **#12**. Emergency HLE bisect only: `DETPS2_FORCE_HLE_IOP=1`.

```bash
dotnet run --project src/DetPS2.Core -c Release -- dump-spine
dotnet run --project src/DetPS2.Core -c Release -- play-path
dotnet run --project src/DetPS2.Core -c Release -- majority-catalog
dotnet run --project src/DetPS2.Core -c Release -- netplay-cert
dotnet run --project src/DetPS2.Core -c Release -- commercial-checklist
dotnet run --project Tests -c Release
pwsh ./publish.ps1
```

Copy `user-media.example.json` → `user-media.json` (gitignored) for dump paths.

| Doc | Purpose |
|-----|---------|
| [docs/DEVELOPER_GUIDE.md](docs/DEVELOPER_GUIDE.md) | **Start here to contribute** — full subsystem map, HLE layering, interrupt system, GameQuirks SDK, and the dated log of ongoing commercial bring-up work |
| [COMPLETENESS.md](COMPLETENESS.md) | **Honest done vs open** |
| [docs/TARGET_CATALOG.md](docs/TARGET_CATALOG.md) | Title list for majority math |
| [RELEASE_NOTES.md](RELEASE_NOTES.md) | v0.1.0 release notes |
| [ROADMAP.md](ROADMAP.md) | Full phase-by-phase history (0–56) |
| [NEXT_PLAN.md](NEXT_PLAN.md) | Pointer to current focus |
| [ARCHITECTURE.md](ARCHITECTURE.md) / [ARCHITECTURE_FREEZE.md](ARCHITECTURE_FREEZE.md) | Contracts |
| [COMPATIBILITY.md](COMPATIBILITY.md) | What runs |
| [FLOAT_POLICY.md](FLOAT_POLICY.md) | Deterministic float rules |
| [PERF_NOTES.md](PERF_NOTES.md) | Timing notes |
| [CONTRIBUTING.md](CONTRIBUTING.md) | How to contribute |
| [docs/CORRECTNESS.md](docs/CORRECTNESS.md) | **Correct over working** — no host-cheat presentation |

| Layer | State |
|-------|--------|
| Emotion Engine | Interpreter + **real ALU JIT** (S1 synthetic met; see COMPLETENESS) |
| IOP | Expanded R3000A + SIF/CDVD + RPC module stubs |
| Scheduler | Fixed-slice, work-cost, event-queue |
| GS / GIF | Software renderer Path1/2/3 + PSMCT16 (Det truth) |
| VU0/1 + VIF | Micro, MSCAL, V4_32; Vif1 façade → `Vif` |
| Kernel HLE | Threads, semas, event flags, WaitVblank, FIO via RPC |
| Save states | v4 Deflate + delta snapshots |
| Input | Digital + analog pad; **INPR record/replay**; SIO2 |
| Audio | SPU2 mix + ring; Desktop **WinMM** on Windows (or meter) |
| Present | Software + GPU staging + SoftwareUpscale + **AcceleratedParallel** (no native Vulkan yet) |
| Netplay | Lockstep TCP/UDP + rollback sim (synthetic soak; commercial open) |
| Desktop | Avalonia: load, debug, tapes, present mode, netplay menus |

## Building & Running

```bash
dotnet build DetPS2.slnx -c Release
dotnet run --project Tests -c Release
dotnet run --project src/DetPS2.Core -c Release
pwsh ./launch.ps1
# or: dotnet run --project src/DetPS2.Desktop -c Release
```

**Play with your BIOS + ISOs**: see **[PLAY.md](PLAY.md)**.

### Desktop

- **Media Folder** — pick ISO library once (saved under `%LocalAppData%\DetPS2\config.json`)  
- **Set BIOS** — remember path; **Boot** / double-click list item  
- **F5/F6/F9** run / pause / reset · **F2** boot selected · **F4** rescan  
- **File → Load ISO / ELF / BIOS** · drag-drop also works  
- **View → Present** modes · **Netplay** (experimental)  

### Determinism contracts

1. `int Step(ulong maxCycles); void Reset();` on all timed components  
2. Public run API: `Ps2System.RunFor` only  
3. No host time in core/save paths  
4. Input replay → identical `MasterCycles` + FB hash  
5. Present/GPU path never replaces software GS for hashes when `DeterminismMode` is on  

## Legal

You must provide your own legal BIOS dump and game images. This project never includes copyrighted material.
