# DetPS2Sharp — Deterministic PS2 Emulator in Pure C#

**Goal**: A clean-slate PlayStation 2 emulator written entirely in modern C# (.NET 9), with **determinism as a core design principle**.

## Status — **v3.1.0 Completeness** (July 2026)

**What is done**: deterministic pure-C# PS2 foundation through Phase **56** — synthetic gates green, dump spine ready, play-path/majority/netplay cert tooling, real ALU JIT (S1), accelerated present.  
**What is not done**: commercial majority on **your** games, native Vulkan, full MPEG IPU.  
**Authoritative list**: **[COMPLETENESS.md](COMPLETENESS.md)**.

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
| [docs/DEVELOPER_GUIDE.md](docs/DEVELOPER_GUIDE.md) | **Start here to contribute** — full subsystem map, HLE layering, interrupt system, GameQuirks SDK |
| [COMPLETENESS.md](COMPLETENESS.md) | **Honest done vs open** |
| [COMMERCIAL_PLAN.md](COMMERCIAL_PLAN.md) | Phases 40–49 (synthetic campaign) |
| [PARITY_PLAN.md](PARITY_PLAN.md) | v2.0 parity plan (Phases 21–39) |
| [docs/TARGET_CATALOG.md](docs/TARGET_CATALOG.md) | Title list for majority math |
| [RELEASE_NOTES.md](RELEASE_NOTES.md) | v3.0 release notes |
| [BUILD_PLAN.md](BUILD_PLAN.md) | Phases 0–11 product arc |
| [ROADMAP.md](ROADMAP.md) | Phase status summary |
| [NEXT_PLAN.md](NEXT_PLAN.md) | Pointer + phase status |
| [ARCHITECTURE.md](ARCHITECTURE.md) / [ARCHITECTURE_FREEZE.md](ARCHITECTURE_FREEZE.md) | Contracts |
| [COMPATIBILITY.md](COMPATIBILITY.md) | What runs |
| [FLOAT_POLICY.md](FLOAT_POLICY.md) | Deterministic float rules |
| [PERF_NOTES.md](PERF_NOTES.md) | Timing notes |
| [CONTRIBUTING.md](CONTRIBUTING.md) | How to contribute |

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
