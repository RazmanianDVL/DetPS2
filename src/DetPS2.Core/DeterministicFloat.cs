using System;
using System.Runtime.CompilerServices;

namespace DetPS2.Core;

/// <summary>
/// Deterministic floating-point policy (Phase 10).
///
/// Policy (DetPS2 core):
/// 1. Hot-path timing uses integer master cycles only — never DateTime/Stopwatch.
/// 2. VU/GS float ops use IEEE-754 binary32 via BitConverter bit patterns;
///    no platform-specific extended precision (we never store intermediates as double
///    except where MathF is used, and results are immediately cast back to float).
/// 3. Prefer <see cref="MathF"/> over <see cref="Math"/> for single-precision.
/// 4. NaN / Inf: quiet-NaN canonicalized where we produce them; comparisons use
///    explicit bit checks when determinism of unordered compares matters.
/// 5. Host SIMD may differ for fused ops — DetPS2 does not use FMA in core;
///    all mul/add are separate float ops for cross-platform bit stability.
///
/// See FLOAT_POLICY.md for contributor-facing notes.
/// </summary>
public static class DeterministicFloat
{
    public const uint CanonicalQNaNBits = 0x7FC00000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FromBits(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ToBits(float f) => BitConverter.SingleToUInt32Bits(f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Canonicalize(float f)
    {
        uint b = ToBits(f);
        // Canonicalize signaling/quiet NaN to a single QNaN bit pattern
        if ((b & 0x7F800000) == 0x7F800000 && (b & 0x007FFFFF) != 0)
            return FromBits(CanonicalQNaNBits | (b & 0x80000000));
        // Flush denormals to signed zero for stricter determinism (optional policy flag)
        if (FlushDenormals && (b & 0x7F800000) == 0 && (b & 0x007FFFFF) != 0)
            return FromBits(b & 0x80000000);
        return f;
    }

    /// <summary>When true, denormals become signed zero after ops that call Canonicalize.</summary>
    public static bool FlushDenormals { get; set; } = false;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Add(float a, float b) => Canonicalize(a + b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sub(float a, float b) => Canonicalize(a - b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Mul(float a, float b) => Canonicalize(a * b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Div(float a, float b)
    {
        if (b == 0f)
            return Canonicalize(a > 0 ? float.PositiveInfinity : a < 0 ? float.NegativeInfinity : FromBits(CanonicalQNaNBits));
        return Canonicalize(a / b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sqrt(float a) => Canonicalize(MathF.Sqrt(MathF.Abs(a)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Madd(float a, float b, float c) =>
        // Non-FMA: mul then add (deterministic across hosts)
        Add(Mul(a, b), c);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Min(float a, float b) => a < b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Max(float a, float b) => a > b ? a : b;

    /// <summary>FNV-1a hash of float bits — for golden regression fixtures.</summary>
    public static ulong HashBits(float f, ulong seed = 2166136261UL)
    {
        uint b = ToBits(f);
        seed ^= b & 0xFF; seed *= 16777619;
        seed ^= (b >> 8) & 0xFF; seed *= 16777619;
        seed ^= (b >> 16) & 0xFF; seed *= 16777619;
        seed ^= (b >> 24) & 0xFF; seed *= 16777619;
        return seed;
    }
}
