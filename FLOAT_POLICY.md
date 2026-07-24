# DetPS2 Deterministic Float Policy (Phase 10)

## Rules

1. **Master timing is integer-only** (`ulong` master cycles). Never use `DateTime`, `Stopwatch`, or `Environment.TickCount` in the emulation hot path or save states.

2. **binary32 only in VU/GS math** after each logical op, results pass through `DeterministicFloat.Canonicalize` where used.

3. **No FMA** in core. Multiply and add are separate so hosts with FMA hardware match those without.

4. Prefer **`MathF`** over `Math` for single-precision.

5. **NaN**: canonicalize to quiet NaN bit pattern `0x7FC00000` (preserve sign when present).

6. **Denormals**: optional flush-to-zero via `DeterministicFloat.FlushDenormals` (default **false**).

7. **SIMD**: not used in core float paths in Phase 10; if added later, must match scalar bit results or be disabled in deterministic mode.

## API

```csharp
DeterministicFloat.Add / Sub / Mul / Div / Sqrt / Madd / Min / Max
DeterministicFloat.ToBits / FromBits / Canonicalize
```

## Verification

Golden framebuffer hashes (`RegressionFixtures.HashFramebuffer`) must remain stable across optimisations that claim to be non-semantic.
