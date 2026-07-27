# DetPS2 Next Plan

**Created**: 2026-07-22
**Updated**: 2026-07-27

**Status**: **v3.1.0 Completeness** shipped — Phases 0–56 done (synthetic gates). Full phase-by-phase
history lives in [ROADMAP.md](ROADMAP.md); done-vs-open status lives in
[COMPLETENESS.md](COMPLETENESS.md) — both are the authoritative references, not this file.

**Current focus (post-v3.1.0, not phase-numbered)**: real commercial bring-up on user-supplied
dumps, using Mortal Kombat: Shaolin Monks (`SLUS_210.87`) as the representative case study — the
hypothesis being that general emulation/HLE fixes found via one commercial title's boot path have
broad value across the library (borne out repeatedly so far: every bug found this way has been a
general bug, not a title-specific one). Dated, detailed investigation notes for this work live in
[docs/DEVELOPER_GUIDE.md](docs/DEVELOPER_GUIDE.md), not here — that file is the log; this file is
just a pointer.

Also added since v3.1.0: a virtual HDD (APA + PFS, real on-disk format — see
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
