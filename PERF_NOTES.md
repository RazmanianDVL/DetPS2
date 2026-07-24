# DetPS2 Performance Notes (Phases 51–52)

Measured on host via Stopwatch **outside** the core. Core remains free of host timers.

## Gates

| ID | Target | Status |
|----|--------|--------|
| S1 | EE JIT ≥ 10× interp on synthetic self-loop | **Met** (Phase 51). Closed-form / specialized pure-ALU self-loop (`beq r0,r0` + `addiu`). Smoke: `Perf_S1_Documented`, `Perf_EeJitBenchmark`. |
| S2 | P2 titles ≥ 100% full speed | **Open** — needs user dumps |
| S3 | Snapshot load ≤ 2 ms | **Partial** — FastDelta often sub-ms; full state host-dependent |
| S4 | Frame budget 16.6/33.3 ms | Host `FrameLimiter` only |

## EE JIT (Phase 51)

- Pure-I/R blocks decoded to `EeJit.Decoded`
- Trailing `BEQ`/`BNE` with nop delay → specialized loop runner
- Always-taken `beq r0,r0` + single `addiu rt,rt,imm` → **closed-form** multiply-add (Det-identical Lo)
- `HasRealAluEmit == true`
- Parity: `EeJit_RealAlu_ParityLoop`, `EeJit_ParityWithInterp`

## Present (Phase 52)

| Mode | What it is |
|------|------------|
| Software | CPU FB snapshot |
| GPU staging | Copy to texture buffer (CPU) |
| SoftwareUpscale | Single-thread bilinear upscale |
| **AcceleratedParallel** | Multi-core bilinear upscale (Det hash still software GS) |
| Native Vulkan | **Not wired** (`VulkanDeviceReady=false`) |

## How to record

```bash
dotnet run --project Tests -c Release
# note Perf_EeJitBenchmark speedup= and Perf_S1_Documented s1=
```
