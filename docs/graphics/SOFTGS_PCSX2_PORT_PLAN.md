# Soft-GS ← PCSX2 GSdx software renderer port plan

**Status:** DRAFT (second pass) — dual-ACK on D1-D10, D12; D11 (savestate/rollback contract, §5.1) awaiting Grok's refinement before SG-1 scaffold starts  
**Date:** 2026-08-06  
**Owners:** Grok (draft) · Claude (review)  
**Reference tree (local):** `C:\Users\xxraz\Documents\c++-projects\Pulls\pcsx2-online\plugins\GSdx\Renderers\SW\`  
**Upstream:** [pcsx2-online](https://github.com/nipkownix/pcsx2-online) (GPLv2/v3 + LGPL components)  
**Parent GFX plan:** [GRAPHICS_PIPELINE_PHASE_PLAN.md](../GRAPHICS_PIPELINE_PHASE_PLAN.md)  
**Doctrine parents (non-negotiable):** [CORRECTNESS.md](../CORRECTNESS.md) · [FLOAT_POLICY.md](../../FLOAT_POLICY.md) · host-stack Soft-GS truth

---

## 0. Why this document exists

User course correction (2026-08-06):

1. **Stop defaulting to per-title Assist hacks.** Real infra bugs were fixed (depth ZTST direction, syscall-return threading, compositor multi-mark), but per-title plants (e.g. FRONTEND.TXD host plant) actively corrupted guest memory and burned multi-hour chases (S376–S379).
2. **GPL / PCSX2 port is authorized** for preservation and online play — not a commercial-closed product bar.
3. **Mission bar is multi-client sync**, not “looks right on one machine.” Netplay-capable correctness is the product.

Tonight’s general Soft-GS/HLE fixes **stay**. B3-scoped residual micro-investigation is **superseded** by this plan as primary focus. This doc lives under `docs/graphics/` as **general infra**, not under a B3-titled audit.

---

## 1. Mission alignment (same doctrine, not a parallel standard)

### 1.1 Soft-GS remains truth

From `docs/CORRECTNESS.md` and the host-stack plan:

| Rule | Implication for this port |
|------|---------------------------|
| Soft-GS metrics / FB are ground truth | Native raster fills the same Soft-GS FB / local-mem model claims already use |
| Host GPU is display only | Vulkan/D3D only upload Soft-GS BGRA; never invent pixels |
| Determinism on the core path | DetMode Soft-GS must hash-identically across clients |
| No planted logos / synthetic branded UI | Port improves **honest** raster; does not restore Assist plants |

### 1.2 FLOAT_POLICY.md applies to the GS slice

DetMode Soft-GS **must follow the same rules already governing EE/VU float work**, not invent a separate “GS-only” standard:

| FLOAT_POLICY rule | DetMode Soft-GS requirement |
|-------------------|----------------------------|
| Master timing is **integer-only** (`ulong` master cycles) | GS never advances wall-clock; only consumes guest commands already scheduled by Core |
| **binary32** after logical ops; canonicalize where used | Vertex/edge/interpolate paths use binary32 with the same canonicalize / NaN policy as `DeterministicFloat` (or bit-identical native equivalent documented in §5) |
| **No FMA** in core | Disable FMA / `/fp:contract` / fused mul-add in DetMode native builds |
| Prefer single-precision | No double-wide intermediate for det path |
| NaN → quiet NaN `0x7FC00000` (sign policy as existing) | Match Core’s canonicalize |
| Denormals: optional FTZ (default **false** in policy) | Lock one choice for DetMode (document: match Core default **false** unless dual-ACK to FTZ both) |
| **SIMD not used** unless bit-identical to scalar | DetMode v1: **scalar or single fixed SSE2 path with golden proof vs scalar**; no runtime AVX dispatch |

**Non-goal:** a GS library that is “internally consistent” but drifts from EE/VU float doctrine.

### 1.3 Netplay bar

Same input tape + same DetMode build ⇒ same Soft-GS framebuffer hash and same local-mem page hashes used by rollback / netplay certification (`docs/NETPLAY_CERTIFIED.md`, `docs/ROLLBACK.md`). Visual “looks ok” on one host is **not** a ship gate.

---

## 2. Reference architecture (GSdx SW)

### 2.1 Modules to study / adapt (in dependency order)

| GSdx SW / GSdx piece | Role | Soft-GS analogue today |
|----------------------|------|------------------------|
| `GSLocalMemory` (+ tables) | PS2 local VRAM layout, PSM swizzle, page/block | `_localMem` + partial PSM paths in `Gs.cs` |
| `GSClut` | CLUT load / expand | Partial CLUT in Soft-GS |
| `GSTextureCacheSW` / `GSTextureSW` | SW texture source from local mem | `SampleTexture` + upload helpers |
| `GSVertexSW` | Edge/vertex attributes (`GSVector4` p/t/c) | Host float verts + XYZ2 path |
| `GSRasterizer` / `GSRasterizerList` | Prim setup, scanline queue, optional workers | C# triangle/line/sprite raster |
| `GSDrawScanline` (+ codegen variants) | Per-pixel TEX/Z/A/blend | `DepthPass` / `AlphaTestPass` / `Modulate` / `Blend` |
| `GSRendererSW` | Draw orchestration, env, nativeres | `Gs` register + GIF delivery façade |
| `GSTables` | ZTST / ATST / blend tables (already cited for ZTST) | Hard-coded switches in Soft-GS |

**In:** the software renderer + local memory + CLUT + SW tex cache.  
**Out of v1:** GSdx **HW** backends (DX11/12/OpenGL/Vulkan), upscale, CRC hacks, MTGS EE coupling, capture, OSD, ini-driven “fixes.”

### 2.2 Why not “link PCSX2/GSdx wholesale”

- Pulls MTGS, plugin ABI, ini, upscale, HW hacks, non-det defaults.
- DetPS2 owns EE / IOP / GIF / VIF / DMAC / scheduler / netplay — GS must be a **narrow sink**.
- Mission needs **DetMode**, not GSdx default multi-thread multi-ISA speed path.

### 2.3 Why not full C# transliteration of GSdx SW

- Tens of kLOC plus SSE/AVX/AVX2 codegen variants.
- Translation bugs re-create the live-trace one-bug-at-a-time loop this plan exits.
- Language policy (2026-08-05) already allows justified C++ when dual-ACK’d.

---

## 3. Determinism findings (gate B — source-backed)

GSdx SW is a **correctness oracle**, not a drop-in netplay rasterizer.

| Risk | Evidence in tree | DetMode mitigation |
|------|------------------|--------------------|
| **Runtime ISA dispatch** | `GSDrawScanlineCodeGenerator*.cpp` SSE / AVX / AVX2; runtime `m_cpu.has(...)` | Force **one** implementation for DetMode (prefer non-JIT scalar first; optional fixed SSE2 only after golden ≡ scalar) |
| **Multi-thread workers** | `GSRasterizerList` + `GSRendererSW(int threads)` scanline striping | DetMode: **1 worker** (v1). Later: fixed N + fixed strip map only if hash-proven |
| **Float vertex math** | `GSVertexSW` = float `GSVector4`; `ceil`, divides, crosses | Follow FLOAT_POLICY (§1.2); lock FTZ/DAZ; no FMA |
| **Design intent** | Single-player visual speed | DetMode is a first-class build/runtime profile, not an afterthought |

**Rejected assumption:** “Port GSdx SW as-is ⇒ free cross-client sync.”  
Optional live SSE-vs-AVX spike is **skipped** (Claude dual-ACK): the codegen file layout is already concrete evidence.

---

## 4. Target architecture (hybrid)

```text
  EE / VIF / GIF / DMAC (C# DetPS2.Core)
            │ guest GS commands / IMAGE / PATH data
            ▼
  Gs façade (C#)  ── claim metrics, present span, savestate glue, netplay hash hooks
            │ P/Invoke or C++/CLI thin boundary (TBD in implementation PR)
            ▼
  DetPS2.SoftGsNative (C++)   DetMode ON for netplay / claims
            │ port/adapt GSdx SW slice
            ▼
  local mem · CLUT · TEX cache SW · raster/scanline · host FB (BGRA)
            │
            ▼
  Host present (display only) · blocker-trace / scoreboard Soft-GS truth
```

### 4.1 Component responsibilities

| Component | Owns | Does not own |
|-----------|------|--------------|
| **C# `Gs` / GIF path** | Register write API surface, GIF packed/reglist/image delivery into native, claim strings, savestate orchestration, Soft-GS hash export | Pixel math once native DetMode is on |
| **`DetPS2.SoftGsNative`** | Local mem, CLUT, texture sample, prim raster, Z/A/blend, page marks for composite | EE timing, IOP, pad, netplay transport |
| **Host present** | Upload Soft-GS FB | Raster, upscale-as-truth |

### 4.2 Integration surface (current Soft-GS hooks to preserve)

Minimum façade the native lib must satisfy (names illustrative):

- **Rollback snapshot** — see §5.1 (not a slow generic serialize path)
- Write GS register / GIF path delivery (packed, reglist, image)
- Present: BGRA framebuffer span (or copy-out) for Soft-GS truth
- Counters: prims, px, reject bins (depth/alpha/scissor), imgBytes — enough for existing claim lines
- Hash: stable FNV/xxHash of FB + optional local-mem pages for netplay

### 4.3 Migration strategy

1. **Shadow mode (measure):** native draws in parallel; C# Soft-GS still authoritative until dual-ACK cutover.
2. **DetMode native authoritative** for claim / netplay when golden matches agreement criteria.
3. **Legacy C# Soft-GS** retained behind env for bisect until fleet soak green, then deprecated (not deleted until dual-ACK).

---

## 5. DetMode contract (normative)

DetMode is **on** for netplay, claim budgets, and CI goldens. Performance profiles may relax only when **not** hashing.

| Knob | DetMode value |
|------|----------------|
| Worker threads | **1** |
| Scanline impl | Scalar (v1) or single fixed SSE2 with proof ≡ scalar |
| Runtime CPU dispatch | **Off** |
| FMA / fp contract | **Off** |
| Float width | binary32 + FLOAT_POLICY canonicalize |
| FTZ/DAZ | Match Core FLOAT_POLICY default (document explicitly in impl PR) |
| Upscale / HW path | **N/A** (not linked) |
| Output | Native res only (`m_nativeres`-class) |

Any change to DetMode knobs requires dual-ACK + golden update.

---

## 5.1 Savestate / rollback-snapshot contract (Claude addition, needs Grok refinement)

**Why this needs its own section, not just open question #2**: the project's actual end goal is *rollback* netplay, not just lockstep sync — which means GS state must be cheaply and *frequently* snapshotted and restored (potentially every frame or every few frames during a rollback resimulation), not occasionally serialized to a save file. A generic "opaque blob, serialize on demand" framing is fine for savestates-to-disk but risks quietly becoming the actual performance bottleneck for rollback viability even with a fully correct, deterministic DetMode renderer underneath it. This needs a real, load-bearing design decision now, not a casual resolution.

**Proposed contract:**

| Requirement | Rationale |
|---|---|
| Native owns a tightly-packed, contiguous memory layout for local mem + GS register file (not scattered heap objects) | Enables cheap `memcpy`-style snapshot/restore, not per-field serialization |
| Snapshot/restore is a raw copy, not a generic serializer, on the hot rollback path | Serialization overhead (reflection, per-field walks) is not acceptable at per-frame rollback cadence |
| Native exposes a stable, versioned binary layout (explicit format version tag in the blob header) | A savestate produced by one native-lib build must have a documented compatibility/migration story against a newer build, not silently corrupt |
| C# owns *when* to snapshot/restore (rollback scheduling, ring buffer of recent states); native owns *what* the state actually is | Keeps rollback scheduling logic testable/inspectable in C#, keeps the native surface narrow (matches §4.1's responsibility split) |
| Snapshot/restore cost is measured and tracked as a real metric from SG-2 onward (not deferred to "later, once it's slow") | Rollback feasibility depends on this number; catching a bad design early is far cheaper than after SG-7 |

**SG-8 note:** fixed-N deterministic workers (currently optional/deferred) should be scheduled as a real planned phase once correctness lands, not indefinitely optional — rollback resimulation needs headroom to replay multiple frames quickly, so raw single-thread scalar performance is a real, load-bearing constraint for the mission, not just a nice-to-have speedup.

---

## 6. Module map — GSdx → Soft-GS work packages

| WP | Deliverable | Exit |
|----|-------------|------|
| **SG-0** | This design dual-ACK’d; license/attribution note | No import yet |
| **SG-1** | Scaffold `DetPS2.SoftGsNative` project + C# P/Invoke stub + empty DetMode flags | Builds in CI |
| **SG-2** | Local mem + PSM tables (from GSdx) + IMAGE write path parity tests | Round-trip IMAGE vs known vectors |
| **SG-3** | CLUT + TEX0 sample path (SW cache) | Textured quad vectors |
| **SG-4** | Register env (TEST/FRAME/ZBUF/ALPHA/…) vs GSdx tables | ZTST/ATST vectors incl. hardware direction |
| **SG-5** | Raster/scanline DetMode scalar | Prim/Z/A/blend goldens |
| **SG-6** | GIF delivery bridge from C# (no EE change) | blocker-trace claim path uses native in shadow |
| **SG-7** | Cutover DetMode native for claims + multi-machine golden | §7 green |
| **SG-8** | Optional fixed-N det workers (only if hash-proven) | Optional |

Title-specific Assist work is **out of scope** unless a gap is proven general and dual-ACK’d as infra.

---

## 7. Validation (golden hashes)

### 7.1 Required

| Test | Purpose |
|------|---------|
| Synthetic Soft-GS vectors (prim/Z/A/TEX) | Unit correctness vs fixed expected FB hashes |
| Release vs Debug on **one** machine | Compiler/config drift |
| Existing DetPS2 smokes / homebrew GS fixtures | Regress EE integration |
| Claim budget on a **small multi-title set** (no new Assist) | Fleet non-black / non-crash smoke |

### 7.2 Mission-critical (Claude refinement — required before “netplay ready”)

| Test | Purpose |
|------|---------|
| **Cross-machine / cross-CPU-vendor** golden of the same DetMode build + same input tape | Real multi-client scenario; Release/Debug alone is **not** sufficient |
| Same binary on two physical hosts when available | Catches ISA/OS float edge cases DetMode claims to freeze |

If only one physical host is available, document the gap honestly and do not claim netplay Soft-GS certification.

### 7.3 Explicit non-validation

- “Looks better in a window” without hash.
- HW GSdx screenshots as Soft-GS truth.
- Single-title Assist-enabled runs as proof of general raster.

---

## 8. Licensing & tree hygiene

- User authorized GPL use for preservation / online play.
- Imported GSdx-derived sources keep **upstream license headers** + SPDX; project README/NOTICE lists PCSX2/GSdx provenance.
- Prefer **vendored adapted sources** under `native/softgs/` (name TBD) over submodule-of-full-pcsx2.
- Do **not** import HW renderers, 3rdparty GPU stacks, or PCSX2 EE.

---

## 9. Non-goals (v1)

- Per-title Assist plants / FORCE_DISP expansion as default progress tool.
- Linking full PCSX2 or GSdx plugin host.
- GSdx HW path as Soft-GS truth.
- Runtime AVX2 “fast” path in DetMode.
- Replacing EE/VU determinism policy with a GS-only policy.
- B3 residual lit% micro as primary seat while this plan is active.

---

## 10. Relationship to landed general fixes (keep)

These remain product defaults independent of the port:

| Fix | Why it stays |
|-----|----------------|
| Soft-GS HW ZTST default ON (`SoftGsHwZtst`) | Matches hardware direction; fleet-soaked |
| Compositor multi-mark pages | General Soft-GS composite correctness |
| P4 syscall `$v0` to yielder | General HLE/threading ABI |

**Frozen without new dual-ACK + general-infra justification:** expanding B3-scoped FORCE_DISP-class assists; reintroducing host plants.

---

## 11. Decision log

| ID | Decision | Status |
|----|----------|--------|
| D1 | Primary focus = GSdx SW study + DetMode Soft-GS port | Dual-ACK S398/S899 |
| D2 | Hybrid C++ SoftGsNative + C# façade | Dual-ACK |
| D3 | Not wholesale PCSX2 link; not full C# translit | Dual-ACK |
| D4 | DetMode: 1 worker; no runtime ISA dispatch; FLOAT_POLICY shared | Dual-ACK (this doc) |
| D5 | Skip optional SSE-vs-AVX live spike | Claude S898 |
| D6 | Design note path: `docs/graphics/SOFTGS_PCSX2_PORT_PLAN.md` | Claude S898 |
| D7 | Cross-machine golden required for netplay Soft-GS claim | Claude S898 |
| D8 | No GSdx import until design dual-ACK | Superseded by D9-D12 |
| D9 | `LibraryImport` source-gen P/Invoke over C++/CLI (portability — C++/CLI is Windows/.NET-only; multi-OS netplay clients rule it out) | Claude S900 |
| D10 | FTZ: match Core FLOAT_POLICY default (`false`), no separate GS-only denormal regime | Claude S900 |
| D11 | Savestate/rollback-snapshot contract (§5.1): native-owned packed/versioned layout, raw-copy hot path, C# owns scheduling | Claude S900 — **needs Grok refinement, not yet final** |
| D12 | SG-8 (fixed-N det workers) elevated from purely optional to a real planned phase post-correctness, given rollback resimulation performance needs | Claude S900 |

---

## 12. Immediate next steps (after Claude dual-ACK of this draft)

1. Claude reviews this doc → dual-ACK or amendment.
2. Only then: SG-1 scaffold (empty native lib + build wiring) — still **no** bulk GSdx file drop until SG-1 review.
3. SG-2+ incremental import with tests per WP.

---

## 13. Open questions — resolved in review (Claude, S900)

1. ~~P/Invoke vs C++/CLI vs `LibraryImport`~~ → **`LibraryImport` source-gen** (D9).
2. ~~Savestate: opaque blob vs C#-owned copy~~ → **§5.1 contract** (D11) — needs Grok pass before this is truly closed.
3. ~~FTZ~~ → **match Core FLOAT_POLICY default `false`** (D10).
4. Multi-title smoke set for SG-7 → **agreed as proposed**: Vexx, GoW, Deception, B3 nopad claim, no new Assist.

**Status: dual-ACK on D1-D10, D12. D11 (savestate/rollback contract) is a first-pass proposal from Claude, not yet dual-ACK'd — needs Grok's read given closer familiarity with Core's existing savestate/rollback scaffolding before SG-1 locks the ABI shape.** Once D11 is settled, this doc is fully dual-ACK'd and SG-1 scaffold can start.

---

*End of draft. No GSdx sources are imported by this document alone.*
