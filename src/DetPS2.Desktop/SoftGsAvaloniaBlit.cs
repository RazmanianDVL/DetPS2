using System;

namespace DetPS2.Desktop;

/// <summary>
/// Soft-GS → Avalonia BGRA packing (pure, unit-testable; no Avalonia types).
///
/// Soft-GS framebuffer layout (authoritative for Desktop present):
/// <list type="bullet">
/// <item><description>Each pixel is a little-endian <c>uint</c> packed as <c>0xAARRGGBB</c>.</description></item>
/// <item><description>Bit lanes: [7:0]=B, [15:8]=G, [23:16]=R, [31:24]=A.</description></item>
/// <item><description>In memory (LE byte order): <c>B, G, R, A</c> — identical to DXGI
/// <c>B8G8R8A8_UNorm</c> and Avalonia <c>PixelFormat.Bgra8888</c>.</description></item>
/// <item><description>Origin is top-left, row-major, width×height words (no padding in Soft-GS span).</description></item>
/// </list>
/// Host present must force A=0xFF so opaque Avalonia bitmaps never treat RGB as transparent black.
/// Optional V-flip addresses inverted CRT/DISPFB composites (env <c>DETPS2_VFLIP=1</c>).
/// </summary>
public static class SoftGsAvaloniaBlit
{
    /// <summary>True when <c>DETPS2_VFLIP=1</c> (or <c>true</c>/<c>yes</c>).</summary>
    public static bool EnvFlipY => ParseEnvFlag("DETPS2_VFLIP");

    /// <summary>
    /// Pack Soft-GS <c>0xAARRGGBB</c> into a tightly packed BGRA8888 destination (one uint per pixel).
    /// Returns lit (non-zero RGB) pixel count after forcing opaque alpha.
    /// </summary>
    public static int PackToBgra(
        ReadOnlySpan<uint> softGs,
        int w,
        int h,
        Span<uint> dstBgra,
        bool flipY = false)
    {
        if (w <= 0 || h <= 0) return 0;
        int n = w * h;
        if (softGs.Length < n || dstBgra.Length < n) return 0;

        int lit = 0;
        for (int y = 0; y < h; y++)
        {
            int srcY = flipY ? (h - 1 - y) : y;
            int srcRow = srcY * w;
            int dstRow = y * w;
            for (int x = 0; x < w; x++)
            {
                // Soft-GS 0xAARRGGBB LE == Avalonia Bgra8888 memory — no channel swizzle.
                uint p = softGs[srcRow + x] | 0xFF000000u;
                dstBgra[dstRow + x] = p;
                if ((p & 0x00FFFFFFu) != 0)
                    lit++;
            }
        }
        return lit;
    }

    /// <summary>
    /// Pack into a strided destination (Avalonia WriteableBitmap RowBytes may exceed w*4).
    /// <paramref name="dst"/> is a byte pointer base; <paramref name="rowBytes"/> is stride in bytes.
    /// </summary>
    public static unsafe int PackToBgraStrided(
        ReadOnlySpan<uint> softGs,
        int w,
        int h,
        byte* dst,
        int rowBytes,
        bool flipY = false)
    {
        if (w <= 0 || h <= 0 || dst == null || rowBytes < w * 4) return 0;
        int n = w * h;
        if (softGs.Length < n) return 0;

        int lit = 0;
        fixed (uint* srcPtr = softGs)
        {
            for (int y = 0; y < h; y++)
            {
                int srcY = flipY ? (h - 1 - y) : y;
                uint* srcRow = srcPtr + (long)srcY * w;
                uint* dstRow = (uint*)(dst + (long)y * rowBytes);
                for (int x = 0; x < w; x++)
                {
                    uint p = srcRow[x] | 0xFF000000u;
                    dstRow[x] = p;
                    if ((p & 0x00FFFFFFu) != 0)
                        lit++;
                }
            }
        }
        return lit;
    }

    /// <summary>
    /// Unit-level contract check: packing, opaque alpha, optional V-flip with a synthetic FB
    /// that mimics a Deception-class top/bottom chrome strip (cyan 0xFF00C5FF).
    /// Returns null on success, or an error string.
    /// </summary>
    public static string? SelfTest()
    {
        const int w = 8, h = 4;
        var src = new uint[w * h];
        // Top row: cyan (Soft-GS 0xAARRGGBB → R=0,G=197,B=255) — matches live-present-deception.ppm
        // Bottom row: magenta (R=255,G=0,B=255)
        uint cyan = 0xFF00C5FFu;    // A=FF R=00 G=C5 B=FF
        uint magenta = 0xFFFF00FFu; // A=FF R=FF G=00 B=FF
        for (int x = 0; x < w; x++)
        {
            src[x] = cyan;                 // y=0
            src[(h - 1) * w + x] = magenta; // y=h-1
        }
        // Mid rows black with A=0 to prove opaque force
        src[w] = 0x00000000u;

        var dst = new uint[w * h];
        int lit = PackToBgra(src, w, h, dst, flipY: false);
        if ((dst[0] & 0xFF000000u) != 0xFF000000u)
            return "opaque A not forced on lit pixel";
        if (dst[0] != cyan)
            return $"BGRA pack mismatch: got 0x{dst[0]:X8} expected cyan 0x{cyan:X8}";
        if (dst[w] != 0xFF000000u)
            return $"black A not forced: 0x{dst[w]:X8}";
        if (dst[(h - 1) * w] != magenta)
            return "bottom row not magenta without flip";
        // LE memory of 0xAARRGGBB is B,G,R,A
        byte[] bytes = BitConverter.GetBytes(dst[0]);
        if (!BitConverter.IsLittleEndian)
            return "self-test requires LE host";
        if (bytes[0] != 0xFF || bytes[1] != 0xC5 || bytes[2] != 0x00 || bytes[3] != 0xFF)
            return $"LE BGRA bytes wrong: {bytes[0]:X2},{bytes[1]:X2},{bytes[2]:X2},{bytes[3]:X2}";

        var flipped = new uint[w * h];
        PackToBgra(src, w, h, flipped, flipY: true);
        if (flipped[0] != magenta)
            return $"V-flip top should be magenta, got 0x{flipped[0]:X8}";
        if (flipped[(h - 1) * w] != cyan)
            return $"V-flip bottom should be cyan, got 0x{flipped[(h - 1) * w]:X8}";

        if (lit < w) // top cyan row at minimum
            return $"lit count too low: {lit}";

        return null;
    }

    private static bool ParseEnvFlag(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v)) return false;
        v = v.Trim();
        return v == "1"
               || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase)
               || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase);
    }
}
