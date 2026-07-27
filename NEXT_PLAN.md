# DetPS2 Next Plan

**Created**: 2026-07-22
**Updated**: 2026-07-27

**Status**: **v0.1.0 Foundation** — engineering Phases 0–56 done on synthetic/homebrew fixtures only
(previously mislabeled product version "v3.1.0 Completeness" — see `src/DetPS2.Core/VersionInfo.cs`
for the corrected versioning policy). **Zero commercial titles reach a main menu yet.** Full
phase-by-phase engineering history lives in [ROADMAP.md](ROADMAP.md); done-vs-open status lives in
[COMPLETENESS.md](COMPLETENESS.md) — both are the authoritative references, not this file.

**Current focus (not phase-numbered)**: real commercial bring-up on user-supplied dumps, using
Mortal Kombat: Shaolin Monks (`SLUS_210.87`) as the representative case study — the hypothesis
being that general emulation/HLE fixes found via one commercial title's boot path have broad value
across the library (borne out repeatedly so far: every bug found this way has been a general bug,
not a title-specific one). Dated, detailed investigation notes for this work live in
[docs/DEVELOPER_GUIDE.md](docs/DEVELOPER_GUIDE.md), not here — that file is the log; this file is
just a pointer. Current blockers and priority order are tracked in
[GitHub Issues](https://github.com/RazmanianDVL/DetPS2/issues).

Also added recently: a virtual HDD (APA + PFS, real on-disk format — see
`docs/DEVELOPER_GUIDE.md` §9) and a `pad-inject` CLI tool for scripted controller-input testing
against a running boot.

---

## Post–v1.0 ideas (not blocking)

1. OS audio device on `RingBufferAudioSink`
2. Real Vulkan/OpenGL upload behind `GpuFramePresenter`
3. IRX ELF loader + more kernel HLE
4. Full likely-branch nullify
5. Expand compatibility matrix with user-run homebrew notes
6. Wire the virtual HDD (APA/PFS) to game-facing I/O (SIF RPC service, IOP device HLE) — currently
   foundation-only, unit-tested in isolation
