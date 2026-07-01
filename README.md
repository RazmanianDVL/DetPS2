# DetPS2Sharp — Deterministic PS2 Emulator in Pure C#

**Goal**: A clean-slate, from-the-ground-up PlayStation 2 emulator written entirely in modern C# (.NET 8/9+), with **determinism as a core design principle** rather than an afterthought.

## Why C#?
Existing PS2 emulators (PCSX2, DobieStation, etc.) are written in C++. While extremely fast, C++ brings:
- Undefined behavior (strict aliasing, signed overflow, uninitialized memory, etc.)
- Compiler/optimization differences across platforms and versions
- Pointer aliasing and memory model subtleties that break reproducibility

C# (especially with modern .NET, `Span<T>`, `ref struct`, `Vector128<T>`, and NativeAOT) gives us:
- Much stronger guarantees against many classes of UB
- Easier, safer, more readable code for complex state machines
- Excellent debuggability and tooling (reflection, source generators, great IDE support)
- Still very high performance when written carefully (many hot paths can approach C++ speeds)

We accept that raw peak performance may be a bit lower than a heavily optimized C++ emulator, but we gain **developer velocity**, **correctness**, and **true determinism** — which is the entire point of this project.

## Determinism Strategy (C# Specific)
- **Master cycle counter** (`ulong`) drives everything. No host timers or `DateTime` in the hot path.
- All state is value types (`struct`) where possible. No hidden allocations or object identity issues in the emulation loop.
- Integer-only arithmetic for timing and most calculations. Avoid `float`/`double` in core state.
- Explicit, predictable memory layout. Use `[StructLayout(LayoutKind.Sequential)]` + `MemoryMarshal` for save states.
- Input events are timestamped to exact master cycles.
- No non-deterministic collections in hot paths (no `Dictionary` iteration order reliance; use arrays or `SortedDictionary` when ordering matters).
- NativeAOT + Tiered Compilation disabled for release "deterministic mode" builds.
- Optional full execution tracer that can be diffed between runs.
- Save states are byte-exact and portable.

## Tech Stack
- **.NET 9** (or .NET 8) — latest LTS or current
- **NativeAOT** for release builds (near-native perf, smaller binaries, more predictable behavior)
- `System.Runtime.Intrinsics` (`Vector128<T>`, `Vector64<T>`) for VU/GS hot paths
- `Silk.NET` or `Veldrid` (later) for cross-platform windowing + input + rendering
- Pure C# — minimal or no P/Invoke in the core emulation loop

## Current Status
This is the very beginning. We are following a DobieStation-inspired incremental approach but in clean C#:

1. Memory map + basic RDRAM
2. Emotion Engine (R5900) interpreter skeleton
3. Minimal HLE for syscalls to run simple homebrew
4. DMAC + GIF
5. Software GS renderer (pixels on screen)
6. ... then VUs, IOP, full timing, determinism polish, etc.

## Building & Running
```bash
cd src/DetPS2.Core
dotnet build -c Release
dotnet run -c Release
```

For maximum determinism in the future:
```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishAot=true -p:TieredCompilation=false
```

## Contributing / "We"
This is a collaborative project between you (DeAndre) and me (Grok). I'll generate clean, well-commented code, explain every architectural decision, and we iterate. Small, working milestones > big plans.

Let's make the most deterministic and debuggable PS2 emulator that has ever existed.

---

**Legal note**: You must provide your own legal BIOS dump and game images. This project will never include copyrighted material.
