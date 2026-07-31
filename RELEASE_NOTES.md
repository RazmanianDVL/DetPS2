# DetPS2Sharp — Release Notes

---

## Unreleased (main tip, 2026-07-31) — commercial Soft-GS menus

**Not a version bump yet** (still **v0.1.0** until formal tag). Milestone for operators:

| Milestone | Status |
|-----------|--------|
| Soft-GS **MENU YES** on 9 commercial titles | **Done** (SEMA_OFF, Soft-GS truth) |
| Pad **INTERACTIVE** fleet-wide (P1) | **In progress** (S1 wave) |
| Soft-GS graphics pipeline past expand/PATH3 crutches | **In progress** (G0 metrics landed; G1+) |
| First gameplay rooms (P4+) | Planned |
| IRX-pure FILEIO/PAD | Planned (S7 couple) |

**Scoreboard:** [docs/title-ports/SCOREBOARD.md](docs/title-ports/SCOREBOARD.md)  
**Plans:** [NEXT_PLAN.md](NEXT_PLAN.md) · [POST_MENU_PHASE_PLAN](docs/POST_MENU_PHASE_PLAN.md) · [GRAPHICS_PIPELINE](docs/GRAPHICS_PIPELINE_PHASE_PLAN.md)  
**Epic:** [#12](https://github.com/RazmanianDVL/DetPS2/issues/12)

Notable Soft-GS infrastructure: Path2 sticky GIF, XYZ2/XYZ3 kick map, merge composite, ofx title-strip expand + **expandHits** telemetry, claim/scoreboard tier hooks.

---

# DetPS2Sharp v0.1.0 — Release Notes

**Date**: 2026-07-27
**Codename**: Foundation

---

## Versioning correction

Earlier releases reached version numbers up to **v3.1.0 ("Completeness")** purely from finishing
internal, synthetic engineering phases — while **zero commercial titles could be played at all**,
not even to a main menu. That was a misleading way to represent project status: a "v3" number
reads as mature/shippable, and this project was not.

**New policy** (see `src/DetPS2.Core/VersionInfo.cs`): the version number now tracks only real,
user-visible commercial playability. Pre-1.0 versions bump on real playability milestones (first
title reaches a main menu, first title fully playable, etc.), never on engineering-phase
completion. **`v1.0.0` is reserved for at least 10% of `docs/TARGET_CATALOG.md`'s titles fully
playable start-to-finish with no errors.** As of this release, that count is **0**.

## What v0.1.0 is

The engineering foundation everything else builds on — EE/IOP interpreters and a real ALU JIT,
software GS, kernel HLE, save states, netplay/rollback infrastructure, and CLI/Desktop tooling.
All of it is verified against synthetic fixtures and homebrew only; **none of it has been shown to
make a real commercial game playable yet**. Internally this corresponds to what was previously
called "Phases 0–56" — see [ROADMAP.md](ROADMAP.md) for that full history.

## Honest limits

| Claim | Status |
|-------|--------|
| Any commercial title reaches a main menu | **No** — actively being worked on, see below |
| Commercial games majority | **No** — needs a title to be playable first |
| Native Vulkan | **Not wired** — use AcceleratedParallel |
| Full MPEG IPU | Stub + SkipFMV |
| Commercial netplay | Synthetic cert only |

See [COMPLETENESS.md](COMPLETENESS.md).

## Active work

Real commercial bring-up against a user-supplied dump: Mortal Kombat: Shaolin Monks (`SLUS_210.87`)
boots past its logo into real gameplay/menu-adjacent code, currently blocked on a traced
runtime-library registry-lookup bug. See `docs/DEVELOPER_GUIDE.md` for the dated investigation log
and [GitHub Issues](https://github.com/RazmanianDVL/DetPS2/issues) for current blockers and
priority order.

---

## CLI

```bash
dotnet run --project src/DetPS2.Core -c Release -- dump-spine
dotnet run --project src/DetPS2.Core -c Release -- play-path
dotnet run --project src/DetPS2.Core -c Release -- majority-catalog
dotnet run --project src/DetPS2.Core -c Release -- netplay-cert 600
dotnet run --project src/DetPS2.Core -c Release -- commercial-checklist
dotnet run --project Tests -c Release
pwsh ./publish.ps1
```

---

## Legal

Provide your own BIOS and game ISOs. DetPS2 does not ship copyrighted dumps.
