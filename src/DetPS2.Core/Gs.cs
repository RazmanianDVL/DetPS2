using System;
using System.Collections.Generic;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer — Phase 7 software renderer.
/// GIF → registers → primitive assembly → rasterize → framebuffer.
/// Deterministic; integer-friendly depth (24-bit fixed as float for convenience).
/// </summary>
public sealed class Gs : ISchedulable
{
    public const int FB_WIDTH = 640;
    public const int FB_HEIGHT = 448;

    public SystemMemory Memory { get; }
    public GsRegisters Registers { get; } = new();

    private readonly uint[] _framebuffer = new uint[FB_WIDTH * FB_HEIGHT];
    private readonly float[] _depthBuffer = new float[FB_WIDTH * FB_HEIGHT];

    // Local GS memory for BITBLT / IMAGE (1MB word-addressable subset)
    private readonly byte[] _localMem = new byte[4 * 1024 * 1024];

    /// <summary>
    /// Host-side present overlay (FMV HLE / boot assist). When set, <see cref="GetPresentSpan"/>
    /// returns this instead of the software raster FB so game black-clears do not hide the overlay.
    /// Deterministic: same overlay bytes → same on-screen pixels. Not a game-specific port.
    /// </summary>
    private uint[]? _hostOverlay;
    private bool _hostOverlayActive;

    private uint _currentPrim;
    private uint _currentRgbaq = 0xFFFFFFFF;
    private float _lastU, _lastV, _lastS = 1f, _lastT = 1f, _lastQ = 1f;
    private float _lastFog;
    private int _texWidth = 64, _texHeight = 64;
    private uint _texBase;
    private bool _useProceduralTexture = true;
    private uint _clutBase;
    private readonly uint[] _clut = new uint[256]; // PSMCT32 palette
    private bool _hasClut;
    public long FragmentsRejectedAlpha { get; private set; }
    public long TexFlushCount { get; private set; }
    /// <summary>Phase 42: nearest (false) or bilinear (true) when sampling non-procedural textures.</summary>
    public bool BilinearFilter { get; set; }
    public long BilinearSamples { get; private set; }

    private readonly List<Vertex> _verts = new(16);
    private int _stripCount;

    // Stats (tests)
    public long PrimitivesDrawn { get; private set; }
    public long PixelsWritten { get; private set; }
    public long FragmentsTested { get; private set; }
    public long FragmentsRejectedDepth { get; private set; }

    public struct Vertex
    {
        public int X, Y;
        public float Z;
        public uint Color;
        public float U, V, S, T, Q;
        public float Fog;
    }

    public Gs(SystemMemory memory)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void Reset()
    {
        Registers.Reset();
        Array.Clear(_framebuffer);
        Array.Clear(_depthBuffer);
        Array.Clear(_localMem);
        _hostOverlay = null;
        _hostOverlayActive = false;
        _currentPrim = 0;
        _currentRgbaq = 0xFFFFFFFF;
        _lastU = _lastV = 0;
        _lastS = _lastT = _lastQ = 1f;
        _lastFog = 0;
        _texWidth = 64;
        _texHeight = 64;
        _texBase = 0;
        _useProceduralTexture = true;
        _verts.Clear();
        _stripCount = 0;
        PrimitivesDrawn = PixelsWritten = FragmentsTested = FragmentsRejectedDepth = 0;
        FragmentsRejectedAlpha = TexFlushCount = 0;
        BilinearFilter = false;
        BilinearSamples = 0;
        _clutBase = 0;
        _hasClut = false;
        Array.Clear(_clut);
        // Default depth far
        for (int i = 0; i < _depthBuffer.Length; i++)
            _depthBuffer[i] = float.MaxValue;
    }

    /// <summary>TEXFLUSH — invalidate texture cache (stat only; soft GS samples local mem live).</summary>
    public void TexFlush() => TexFlushCount++;

    // ===================== GIF / register writes =====================

    public void WriteGsRegister(uint reg, ulong value)
    {
        reg &= 0x7F;
        Registers.WriteRegister64(reg, value);
        OnRegisterWrite(reg, value);
    }

    public void ProcessGifPackedWord(uint dataLow, uint dataHigh)
    {
        // PACKED A+D: low 64 = data, high byte of high dword often reg in bits 0-7 of upper...
        // Standard: QW = [data64][reg8 in bits 0-7 of second half low?]. We use prior convention:
        // dataLow = low 32 of data, dataHigh high 32 of data, reg in bits 24-30 of dataHigh when only 32-bit data used.
        // Full 64-bit data form:
        ulong data = dataLow | ((ulong)(dataHigh & 0x00FFFFFF) << 32);
        uint reg = (dataHigh >> 24) & 0x7F;
        // Also accept pure reg in low byte of high when format uses AD
        if (reg == 0 && (dataHigh & 0x7F) != 0 && (dataHigh >> 8) == 0)
            reg = dataHigh & 0x7F;

        if (reg == 0 && dataLow == 0 && dataHigh == 0) return;

        // Prefer AD style: second QW half's low 8 bits = reg when using high as reg-only
        // Keep legacy: reg from bits 24-30 of high when writing 32-bit values
        uint regAddr = (dataHigh >> 24) & 0x7F;
        if (regAddr == 0)
            regAddr = dataHigh & 0x7F;

        WriteGsRegister(regAddr, dataLow | ((ulong)(dataHigh & 0xFFFFFF) << 32));
    }

    /// <summary>Packed register write with explicit reg and 64-bit data.</summary>
    public void WritePacked(uint reg, ulong data) => WriteGsRegister(reg, data);

    private void OnRegisterWrite(uint reg, ulong value)
    {
        switch (reg)
        {
            case 0x00: // PRIM
                _currentPrim = (uint)value;
                _verts.Clear();
                _stripCount = 0;
                break;
            case 0x01: // RGBAQ
                _currentRgbaq = (uint)value;
                // Q in upper 32 bits as float
                if ((value >> 32) != 0)
                    _lastQ = BitConverter.Int32BitsToSingle((int)(value >> 32));
                break;
            case 0x02: // ST
                _lastS = BitConverter.Int32BitsToSingle((int)(value & 0xFFFFFFFF));
                _lastT = BitConverter.Int32BitsToSingle((int)(value >> 32));
                break;
            case 0x03: // UV (12.4 fixed)
                _lastU = ((value & 0x3FFF)) / 16.0f;
                _lastV = (((value >> 16) & 0x3FFF)) / 16.0f;
                break;
            case 0x0A: // FOG
                _lastFog = ((value >> 56) & 0xFF) / 255.0f;
                break;
            case 0x06: // TEX0_1
                ApplyTex0(value);
                break;
            case 0x04: // XYZ2 — kick draw
                AddVertexFromXyz(value, kick: true);
                break;
            case 0x05: // XYZ3 — no kick (strip build)
                AddVertexFromXyz(value, kick: false);
                break;
        }
    }

    private void ApplyTex0(ulong tex0)
    {
        _texWidth = Registers.TexWidth;
        _texHeight = Registers.TexHeight;
        _texBase = Registers.TexBaseWords * 64;
        // If TEX0 was explicitly set with non-zero TBP0, sample local mem
        if ((tex0 & 0x3FFF) != 0 || ((tex0 >> 20) & 0x3F) != 0)
            _useProceduralTexture = false;
    }

    public void SetPrim(uint prim) => WriteGsRegister(0x00, prim);
    public void SetRGBAQ(uint rgbaq) => WriteGsRegister(0x01, rgbaq);

    public void DrawVertex(uint xyz) => WriteGsRegister(0x04, xyz);

    public void DrawVertex64(ulong xyz2) => WriteGsRegister(0x04, xyz2);

    // ===================== Vertex / primitives =====================

    private void AddVertexFromXyz(ulong xyz, bool kick)
    {
        // XYZ: X 16-bit 12.4, Y 16-bit 12.4, Z 24-bit in upper
        int xRaw = (int)(xyz & 0xFFFF);
        int yRaw = (int)((xyz >> 16) & 0xFFFF);
        uint zRaw = (uint)((xyz >> 32) & 0xFFFFFF);

        Registers.GetXyOffset(out int ofx, out int ofy);
        // Convert 12.4 to pixel: (raw - offset) / 16
        int x = (xRaw - ofx) >> 4;
        int y = (yRaw - ofy) >> 4;

        // If offset is 0 and values look like already-screen or scaled 0..4096 homebrew style
        if (ofx == 0 && ofy == 0 && xRaw > FB_WIDTH * 16)
        {
            x = (xRaw * FB_WIDTH) / 4096;
            y = (yRaw * FB_HEIGHT) / 4096;
        }

        float z = zRaw / (float)0xFFFFFF;

        var v = new Vertex
        {
            X = x,
            Y = y,
            Z = z,
            Color = _currentRgbaq,
            U = _lastU,
            V = _lastV,
            S = _lastS,
            T = _lastT,
            Q = _lastQ,
            Fog = _lastFog
        };
        _verts.Add(v);

        if (kick)
            TryAssemble();
    }

    /// <summary>Add a screen-space vertex directly (tests / HLE). Kicks assembly when enough verts exist.</summary>
    public void AddScreenVertex(int x, int y, float z, uint color, float u = 0, float v = 0)
    {
        _currentRgbaq = color;
        _verts.Add(new Vertex { X = x, Y = y, Z = z, Color = color, U = u, V = v, S = u, T = v, Q = 1, Fog = 0 });
        TryAssemble();
    }

    /// <summary>Force-draw a triangle in screen pixels (bypasses GIF).</summary>
    public void DrawScreenTriangle(int x0, int y0, int x1, int y1, int x2, int y2, uint color, float z0 = 0.1f, float z1 = 0.1f, float z2 = 0.1f, float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 0, float u2 = 0.5f, float v2 = 1)
    {
        DrawFilledTriangle(
            new Vertex { X = x0, Y = y0, Z = z0, Color = color, U = u0, V = v0, S = u0, T = v0, Q = 1 },
            new Vertex { X = x1, Y = y1, Z = z1, Color = color, U = u1, V = v1, S = u1, T = v1, Q = 1 },
            new Vertex { X = x2, Y = y2, Z = z2, Color = color, U = u2, V = v2, S = u2, T = v2, Q = 1 });
        PrimitivesDrawn++;
    }

    private int VertsNeeded(int primType) => primType switch
    {
        0 => 1, // point
        1 => 2, // line
        2 => 2, // line strip (first needs 2, then 1)
        3 => 3, // triangle
        4 => 3, // triangle strip
        5 => 3, // triangle fan
        6 => 2, // sprite
        _ => 3
    };

    private void TryAssemble()
    {
        int pt = (int)(_currentPrim & 0x7);
        int need = VertsNeeded(pt);

        switch (pt)
        {
            case 0: // Point
                if (_verts.Count >= 1)
                {
                    DrawPoint(_verts[0]);
                    _verts.Clear();
                    PrimitivesDrawn++;
                }
                break;
            case 1: // Line
                if (_verts.Count >= 2)
                {
                    DrawLine(_verts[0], _verts[1]);
                    _verts.Clear();
                    PrimitivesDrawn++;
                }
                break;
            case 2: // Line strip
                while (_verts.Count >= 2)
                {
                    DrawLine(_verts[0], _verts[1]);
                    _verts.RemoveAt(0);
                    PrimitivesDrawn++;
                }
                break;
            case 3: // Triangle
                if (_verts.Count >= 3)
                {
                    DrawFilledTriangle(_verts[0], _verts[1], _verts[2]);
                    _verts.Clear();
                    PrimitivesDrawn++;
                }
                break;
            case 4: // Triangle strip
                while (_verts.Count >= 3)
                {
                    if ((_stripCount & 1) == 0)
                        DrawFilledTriangle(_verts[0], _verts[1], _verts[2]);
                    else
                        DrawFilledTriangle(_verts[1], _verts[0], _verts[2]);
                    _verts.RemoveAt(0);
                    _stripCount++;
                    PrimitivesDrawn++;
                }
                break;
            case 5: // Triangle fan
                while (_verts.Count >= 3)
                {
                    DrawFilledTriangle(_verts[0], _verts[1], _verts[2]);
                    // keep v0, replace v1 with old v2 by removing index 1
                    _verts.RemoveAt(1);
                    PrimitivesDrawn++;
                }
                break;
            case 6: // Sprite: two corners
                if (_verts.Count >= 2)
                {
                    DrawSprite(_verts[0], _verts[1]);
                    _verts.Clear();
                    PrimitivesDrawn++;
                }
                break;
            default:
                if (_verts.Count >= need) _verts.Clear();
                break;
        }
    }

    // ===================== Raster =====================

    private bool InScissor(int x, int y)
    {
        Registers.GetScissor(out int x0, out int x1, out int y0, out int y1);
        return x >= x0 && x <= x1 && y >= y0 && y <= y1;
    }

    private void DrawPoint(Vertex v)
    {
        WriteFragment(v.X, v.Y, v.Z, v.Color, v.U, v.V, v.Fog);
    }

    private void DrawLine(Vertex a, Vertex b)
    {
        int x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y;
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int steps = Math.Max(dx, dy);
        int step = 0;

        while (true)
        {
            float t = steps == 0 ? 0 : step / (float)steps;
            float z = a.Z + (b.Z - a.Z) * t;
            uint col = LerpColor(a.Color, b.Color, t);
            float u = a.U + (b.U - a.U) * t;
            float v = a.V + (b.V - a.V) * t;
            float fog = a.Fog + (b.Fog - a.Fog) * t;
            WriteFragment(x0, y0, z, col, u, v, fog);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
            step++;
        }
    }

    private void DrawSprite(Vertex a, Vertex b)
    {
        int minX = Math.Min(a.X, b.X);
        int maxX = Math.Max(a.X, b.X);
        int minY = Math.Min(a.Y, b.Y);
        int maxY = Math.Max(a.Y, b.Y);
        float z = Math.Min(a.Z, b.Z);
        uint col = a.Color;
        float u0 = a.U, v0 = a.V, u1 = b.U, v1 = b.V;
        int w = Math.Max(1, maxX - minX);
        int h = Math.Max(1, maxY - minY);

        for (int y = minY; y <= maxY; y++)
        {
            float tv = v0 + (v1 - v0) * ((y - minY) / (float)h);
            for (int x = minX; x <= maxX; x++)
            {
                float tu = u0 + (u1 - u0) * ((x - minX) / (float)w);
                WriteFragment(x, y, z, col, tu, tv, a.Fog);
            }
        }
    }

    public void DrawQuad(int x, int y, int w, int h, uint color)
    {
        SetPrim(6); // sprite
        _currentRgbaq = color;
        _verts.Clear();
        AddScreenVertex(x, y, 0, color, 0, 0);
        // second corner — AddScreenVertex kicks; need both without clearing
        // Use raw assembly:
        _verts.Clear();
        _currentPrim = 6;
        _verts.Add(new Vertex { X = x, Y = y, Z = 0, Color = color, U = 0, V = 0 });
        _verts.Add(new Vertex { X = x + w, Y = y + h, Z = 0, Color = color, U = 1, V = 1 });
        DrawSprite(_verts[0], _verts[1]);
        _verts.Clear();
        PrimitivesDrawn++;
    }

    public void DrawLine(int x0, int y0, int x1, int y1, uint color)
    {
        DrawLine(
            new Vertex { X = x0, Y = y0, Z = 0, Color = color },
            new Vertex { X = x1, Y = y1, Z = 0, Color = color });
        PrimitivesDrawn++;
    }

    private void DrawFilledTriangle(Vertex v0, Vertex v1, Vertex v2)
    {
        int minX = Math.Max(0, Math.Min(v0.X, Math.Min(v1.X, v2.X)));
        int maxX = Math.Min(FB_WIDTH - 1, Math.Max(v0.X, Math.Max(v1.X, v2.X)));
        int minY = Math.Max(0, Math.Min(v0.Y, Math.Min(v1.Y, v2.Y)));
        int maxY = Math.Min(FB_HEIGHT - 1, Math.Max(v0.Y, Math.Max(v1.Y, v2.Y)));

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!PointInTriangle(x, y, v0, v1, v2, out float wa, out float wb, out float wc))
                    continue;

                float z = v0.Z * wa + v1.Z * wb + v2.Z * wc;
                uint color = Registers.PrimIip || true
                    ? InterpolateColor(v0.Color, v1.Color, v2.Color, wa, wb, wc)
                    : v0.Color;
                float u = v0.U * wa + v1.U * wb + v2.U * wc;
                float v = v0.V * wa + v1.V * wb + v2.V * wc;
                // ST path
                if (!Registers.PrimFst)
                {
                    float s = v0.S * wa + v1.S * wb + v2.S * wc;
                    float t = v0.T * wa + v1.T * wb + v2.T * wc;
                    float q = v0.Q * wa + v1.Q * wb + v2.Q * wc;
                    if (Math.Abs(q) > 1e-6f) { s /= q; t /= q; }
                    u = s; v = t;
                }
                float fog = v0.Fog * wa + v1.Fog * wb + v2.Fog * wc;
                WriteFragment(x, y, z, color, u, v, fog);
            }
        }
    }

    private void WriteFragment(int x, int y, float z, uint color, float u, float v, float fog)
    {
        if (x < 0 || y < 0 || x >= FB_WIDTH || y >= FB_HEIGHT) return;
        if (!InScissor(x, y)) return;

        int idx = y * FB_WIDTH + x;
        FragmentsTested++;

        if (Registers.DepthTestEnabled)
        {
            if (!DepthPass(z, _depthBuffer[idx], Registers.DepthTestMode))
            {
                FragmentsRejectedDepth++;
                return;
            }
        }
        else
        {
            // Default: closer (smaller z) wins like OpenGL less
            if (z > _depthBuffer[idx])
            {
                FragmentsRejectedDepth++;
                return;
            }
        }

        uint final = color;
        if (Registers.PrimTme)
        {
            uint tex = SampleTexture(u, v);
            final = Modulate(color, tex);
        }

        // Alpha test (Phase 28) — ATE bit in TEST
        if (!AlphaTestPass(final))
        {
            FragmentsRejectedAlpha++;
            return;
        }

        if (Registers.PrimFge)
            final = ApplyFog(final, fog);

        if (Registers.PrimAbe || Registers.ALPHA_1 != 0)
            final = Blend(final, _framebuffer[idx]);

        _framebuffer[idx] = final | 0xFF000000;
        if (!Registers.DepthTestEnabled || Registers.DepthWriteEnabled)
            _depthBuffer[idx] = z;
        PixelsWritten++;
    }

    private static bool DepthPass(float z, float buf, int mode) => mode switch
    {
        0 => false,                         // NEVER
        1 => true,                          // ALWAYS
        2 => z <= buf,                      // GEQUAL (treat as closer-or-equal with smaller z)
        3 => z < buf,                       // GREATER → closer
        _ => z <= buf
    };

    private static uint Modulate(uint color, uint tex)
    {
        byte cr = (byte)((color >> 16) & 0xFF);
        byte cg = (byte)((color >> 8) & 0xFF);
        byte cb = (byte)(color & 0xFF);
        byte ca = (byte)((color >> 24) & 0xFF);
        byte tr = (byte)((tex >> 16) & 0xFF);
        byte tg = (byte)((tex >> 8) & 0xFF);
        byte tb = (byte)(tex & 0xFF);
        byte ta = (byte)((tex >> 24) & 0xFF);
        byte r = (byte)((cr * tr) / 255);
        byte g = (byte)((cg * tg) / 255);
        byte b = (byte)((cb * tb) / 255);
        byte a = (byte)((ca * ta) / 255);
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    private uint ApplyFog(uint color, float fog)
    {
        // fog 0 = full fog color, 1 = full source
        uint fc = (uint)Registers.FOGCOL;
        if (fc == 0) fc = 0x00808080;
        float f = Math.Clamp(fog, 0f, 1f);
        byte sr = (byte)((color >> 16) & 0xFF);
        byte sg = (byte)((color >> 8) & 0xFF);
        byte sb = (byte)(color & 0xFF);
        byte fr = (byte)((fc >> 16) & 0xFF);
        byte fg = (byte)((fc >> 8) & 0xFF);
        byte fb = (byte)(fc & 0xFF);
        byte r = (byte)(sr * f + fr * (1 - f));
        byte g = (byte)(sg * f + fg * (1 - f));
        byte b = (byte)(sb * f + fb * (1 - f));
        return (color & 0xFF000000) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    // ===================== GS local memory swizzle addressing =====================
    // Real GS VRAM is not laid out row-major; it's tiled into 8KB "pages" made of 32
    // fixed-layout blocks, each block internally Z-order (Morton) swizzled. Real games'
    // texture/framebuffer data is written and expected to be read back in this layout —
    // naive row-major addressing (what this file used before) only round-trips data this
    // engine wrote itself, and would render real GS-hardware-authored texture data as
    // scrambled noise. blockTable32/columnTable32 below are transcribed VERBATIM from
    // PCSX2's GSTables.cpp (github.com/PCSX2/pcsx2) — a real, working GS implementation —
    // rather than derived from a general description, given how easy this is to get
    // subtly wrong. PSMT8's block table is confirmed identical to PSMT32's; its column
    // (within-block) table is NOT independently confirmed — generated via the same
    // Morton bit-interleave pattern columnTable32 was confirmed to follow, extended to
    // PSMT8's 16x16 block (vs. PSMCT32's 8x8), which is a reasoned extension, not a
    // verbatim-sourced table. PSMCT16/PSMT4 are NOT swizzled here — deliberately left on
    // the existing row-major path pending further verification (their block dimensions
    // are non-square, arithmetically derived rather than confirmed, per research notes).
    private static readonly int[,] BlockTable32 =
    {
        { 0, 1, 4, 5,16,17,20,21},
        { 2, 3, 6, 7,18,19,22,23},
        { 8, 9,12,13,24,25,28,29},
        {10,11,14,15,26,27,30,31}
    };
    private static readonly int[,] ColumnTable32 =
    {
        { 0, 1, 4, 5, 8, 9,12,13},
        { 2, 3, 6, 7,10,11,14,15},
        {16,17,20,21,24,25,28,29},
        {18,19,22,23,26,27,30,31},
        {32,33,36,37,40,41,44,45},
        {34,35,38,39,42,43,46,47},
        {48,49,52,53,56,57,60,61},
        {50,51,54,55,58,59,62,63}
    };

    /// <summary>Bit-interleave (Morton/Z-order) x and y into a single index — the pattern
    /// ColumnTable32 was confirmed to follow (x supplies even bits, y supplies odd bits).</summary>
    private static int MortonInterleave(int x, int y, int bits)
    {
        int r = 0;
        for (int i = 0; i < bits; i++)
        {
            r |= ((x >> i) & 1) << (2 * i);
            r |= ((y >> i) & 1) << (2 * i + 1);
        }
        return r;
    }

    /// <summary>PSMCT32 swizzled byte offset for pixel (x,y). Page 64x32px, block 8x8px,
    /// 4 bytes/pixel, 256 bytes/block (8192/32), 8192 bytes/page.</summary>
    private static uint SwizzleOffset32(uint texBaseBytes, int x, int y, int bufferWidthPx)
    {
        const int pageW = 64, pageH = 32, blockW = 8, blockH = 8;
        int pagesPerRow = Math.Max(1, (bufferWidthPx + pageW - 1) / pageW);
        int pageX = x / pageW, pageY = y / pageH;
        int pageIdx = pageY * pagesPerRow + pageX;
        int ix = x % pageW, iy = y % pageH;
        int blockIdx = BlockTable32[(iy / blockH) % 4, (ix / blockW) % 8];
        int pixelIdx = ColumnTable32[iy % blockH, ix % blockW];
        return texBaseBytes + (uint)(pageIdx * 8192 + blockIdx * 256 + pixelIdx * 4);
    }

    /// <summary>PSMT8 swizzled byte offset for pixel (x,y). Page 128x64px, block 16x16px
    /// (256 bytes/block, matching PSMT32's block-table shape), 1 byte/pixel.</summary>
    private static uint SwizzleOffset8(uint texBaseBytes, int x, int y, int bufferWidthPx)
    {
        const int pageW = 128, pageH = 64, blockW = 16, blockH = 16;
        int pagesPerRow = Math.Max(1, (bufferWidthPx + pageW - 1) / pageW);
        int pageX = x / pageW, pageY = y / pageH;
        int pageIdx = pageY * pagesPerRow + pageX;
        int ix = x % pageW, iy = y % pageH;
        int blockIdx = BlockTable32[(iy / blockH) % 4, (ix / blockW) % 8];
        int pixelIdx = MortonInterleave(ix % blockW, iy % blockH, 4);
        return texBaseBytes + (uint)(pageIdx * 8192 + blockIdx * 256 + pixelIdx);
    }

    public uint SampleTexture(float u, float v)
    {
        int tw = _texWidth;
        int th = _texHeight;
        float fu = u, fv = v;

        int wms = Registers.ClampWms;
        int wmt = Registers.ClampWmt;
        if (wms == 1) fu = Math.Clamp(fu, 0f, 1f);
        else fu = fu - MathF.Floor(fu);
        if (wmt == 1) fv = Math.Clamp(fv, 0f, 1f);
        else fv = fv - MathF.Floor(fv);

        if (BilinearFilter && !_useProceduralTexture && tw > 1 && th > 1)
        {
            BilinearSamples++;
            float x = fu * (tw - 1);
            float y = fv * (th - 1);
            int x0 = Math.Clamp((int)MathF.Floor(x), 0, tw - 1);
            int y0 = Math.Clamp((int)MathF.Floor(y), 0, th - 1);
            int x1 = Math.Min(x0 + 1, tw - 1);
            int y1 = Math.Min(y0 + 1, th - 1);
            float fx = x - x0, fy = y - y0;
            uint c00 = SampleTexel(x0, y0);
            uint c10 = SampleTexel(x1, y0);
            uint c01 = SampleTexel(x0, y1);
            uint c11 = SampleTexel(x1, y1);
            return Bilerp(c00, c10, c01, c11, fx, fy);
        }

        int tu = Math.Clamp((int)(fu * tw), 0, tw - 1);
        int tv = Math.Clamp((int)(fv * th), 0, th - 1);

        if (_useProceduralTexture)
        {
            bool checker = ((tu / 8) + (tv / 8)) % 2 == 0;
            return checker ? 0xFFFF00FF : 0xFF00FFFF;
        }

        return SampleTexel(tu, tv);
    }

    private uint SampleTexel(int tu, int tv)
    {
        int tw = _texWidth;
        int th = _texHeight;
        tu = Math.Clamp(tu, 0, tw - 1);
        tv = Math.Clamp(tv, 0, th - 1);
        if (_useProceduralTexture)
        {
            bool checker = ((tu / 8) + (tv / 8)) % 2 == 0;
            return checker ? 0xFFFF00FF : 0xFF00FFFF;
        }

        int psm = Registers.TexPsm;
        // PSMT8 (0x13): 8-bit index → CLUT. Real block-swizzled addressing (see
        // SwizzleOffset8) — buffer width defaults to the texture's own width, which is
        // correct for the common single-texture case; real TBW isn't modeled separately.
        if (psm == 0x13)
        {
            int bi = (int)SwizzleOffset8(_texBase, tu, tv, tw);
            if (bi < 0 || bi >= _localMem.Length) return 0xFFFFFFFF;
            byte idx8 = _localMem[bi];
            return _hasClut ? _clut[idx8] : 0xFF000000u | ((uint)idx8 << 16) | ((uint)idx8 << 8) | idx8;
        }
        // PSMT4 (0x14): 4-bit index
        if (psm == 0x14)
        {
            int bi = (int)(_texBase + (tv * tw + tu) / 2);
            if (bi < 0 || bi >= _localMem.Length) return 0xFFFFFFFF;
            byte packed = _localMem[bi];
            int nibble = ((tu + tv * tw) & 1) == 0 ? (packed & 0xF) : (packed >> 4);
            return _hasClut ? _clut[nibble & 0xF] : 0xFF000000u | (uint)(nibble * 17) * 0x010101u;
        }
        // PSMCT16 (0x02): 16-bit RGB555 expand
        if (psm == 0x02)
        {
            int bi = (int)(_texBase + (tv * tw + tu) * 2);
            if (bi < 0 || bi + 1 >= _localMem.Length) return 0xFFFFFFFF;
            ushort p = (ushort)(_localMem[bi] | (_localMem[bi + 1] << 8));
            int r = ((p >> 10) & 0x1F) * 255 / 31;
            int g = ((p >> 5) & 0x1F) * 255 / 31;
            int b = (p & 0x1F) * 255 / 31;
            return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }

        // PSMCT32 / default — real block-swizzled addressing (see SwizzleOffset32).
        int byteIndex = (int)SwizzleOffset32(_texBase, tu, tv, tw);
        if (byteIndex < 0 || byteIndex + 3 >= _localMem.Length)
            return 0xFFFFFFFF;
        return (uint)(_localMem[byteIndex]
            | (_localMem[byteIndex + 1] << 8)
            | (_localMem[byteIndex + 2] << 16)
            | (_localMem[byteIndex + 3] << 24));
    }

    private static uint Bilerp(uint c00, uint c10, uint c01, uint c11, float fx, float fy)
    {
        static byte Lerp(byte a, byte b, float t) => (byte)(a + (b - a) * t);
        static uint Chan(uint c, int shift) => (c >> shift) & 0xFF;
        byte r = Lerp(Lerp((byte)Chan(c00, 16), (byte)Chan(c10, 16), fx),
                      Lerp((byte)Chan(c01, 16), (byte)Chan(c11, 16), fx), fy);
        byte g = Lerp(Lerp((byte)Chan(c00, 8), (byte)Chan(c10, 8), fx),
                      Lerp((byte)Chan(c01, 8), (byte)Chan(c11, 8), fx), fy);
        byte b = Lerp(Lerp((byte)Chan(c00, 0), (byte)Chan(c10, 0), fx),
                      Lerp((byte)Chan(c01, 0), (byte)Chan(c11, 0), fx), fy);
        byte a = Lerp(Lerp((byte)Chan(c00, 24), (byte)Chan(c10, 24), fx),
                      Lerp((byte)Chan(c01, 24), (byte)Chan(c11, 24), fx), fy);
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    /// <summary>Upload 8-bit indexed texture + optional 256-entry CLUT (PSMT8).</summary>
    public void UploadTexture8(int destWordAddr, int width, int height, ReadOnlySpan<byte> indices, ReadOnlySpan<uint> clutRgba)
    {
        _texBase = (uint)(destWordAddr * 64);
        _texWidth = width;
        _texHeight = height;
        _useProceduralTexture = false;
        Registers.WriteRegister64(0x06,
            (ulong)(destWordAddr & 0x3FFF)
            | ((ulong)0x13 << 20) // PSMT8
            | ((ulong)Log2(width) << 26)
            | ((ulong)Log2(height) << 30));
        int n = Math.Min(indices.Length, width * height);
        for (int i = 0; i < n; i++)
        {
            int bi = (int)SwizzleOffset8(_texBase, i % width, i / width, width);
            if (bi >= _localMem.Length) break;
            _localMem[bi] = indices[i];
        }
        if (clutRgba.Length > 0)
        {
            int c = Math.Min(256, clutRgba.Length);
            for (int i = 0; i < c; i++)
                _clut[i] = clutRgba[i] | 0xFF000000u;
            _hasClut = true;
            _clutBase = _texBase;
        }
    }

    private bool AlphaTestPass(uint color)
    {
        // TEST_1: ATE bit 0 of low; ATST bits 1-3; AREF bits 4-11 (simplified via Registers if present)
        // If no alpha test configured, pass
        ulong test = Registers.TEST_1;
        bool ate = (test & 1) != 0;
        if (!ate) return true;
        int atst = (int)((test >> 1) & 7);
        int aref = (int)((test >> 4) & 0xFF);
        int a = (int)((color >> 24) & 0xFF);
        return atst switch
        {
            0 => false,           // NEVER
            1 => true,            // ALWAYS
            2 => a < aref,        // LESS
            3 => a <= aref,       // LEQUAL
            4 => a == aref,       // EQUAL
            5 => a >= aref,       // GEQUAL
            6 => a > aref,        // GREATER
            7 => a != aref,       // NOTEQUAL
            _ => true
        };
    }

    /// <summary>Upload texture pixels into local GS memory (PSMCT32).</summary>
    public void UploadTexture(int destWordAddr, int width, int height, ReadOnlySpan<uint> pixels)
    {
        _texBase = (uint)(destWordAddr * 64);
        _texWidth = width;
        _texHeight = height;
        _useProceduralTexture = false;
        Registers.WriteRegister64(0x06,
            (ulong)(destWordAddr & 0x3FFF)
            | ((ulong)0 << 20) // PSMCT32
            | ((ulong)Log2(width) << 26)
            | ((ulong)Log2(height) << 30));

        int n = Math.Min(pixels.Length, width * height);
        for (int i = 0; i < n; i++)
        {
            int bi = (int)SwizzleOffset32(_texBase, i % width, i / width, width);
            if (bi + 3 >= _localMem.Length) break;
            uint p = pixels[i];
            _localMem[bi] = (byte)p;
            _localMem[bi + 1] = (byte)(p >> 8);
            _localMem[bi + 2] = (byte)(p >> 16);
            _localMem[bi + 3] = (byte)(p >> 24);
        }
    }

    /// <summary>Upload 16-bit RGB555 texture (PSMCT16) into local GS memory.</summary>
    public void UploadTexture16(int destWordAddr, int width, int height, ReadOnlySpan<ushort> pixels)
    {
        _texBase = (uint)(destWordAddr * 64);
        _texWidth = width;
        _texHeight = height;
        _useProceduralTexture = false;
        Registers.WriteRegister64(0x06,
            (ulong)(destWordAddr & 0x3FFF)
            | ((ulong)0x02 << 20) // PSMCT16
            | ((ulong)Log2(width) << 26)
            | ((ulong)Log2(height) << 30));

        int n = Math.Min(pixels.Length, width * height);
        for (int i = 0; i < n; i++)
        {
            int bi = (int)(_texBase + i * 2);
            if (bi + 1 >= _localMem.Length) break;
            ushort p = pixels[i];
            _localMem[bi] = (byte)p;
            _localMem[bi + 1] = (byte)(p >> 8);
        }
    }

    /// <summary>IMAGE path: write raw QW data into local mem at current BITBLT position
    /// (simplified linear — NOT yet swizzle-aware). This is the real path commercial games
    /// use for every texture/framebuffer upload (BITBLTBUF/TRXPOS/TRXREG/TRXDIR-driven
    /// transfer, now correctly stored at their real register addresses — see
    /// GsRegisters.WriteRegister64 — but this method still writes a contiguous linear byte
    /// run rather than tracking a real per-pixel (x,y) cursor through SwizzleOffset32/8, so
    /// data arriving through here doesn't land where SampleTexel now expects it. Only the
    /// synthetic UploadTexture/UploadTexture8 test helpers are swizzle-consistent today.
    /// Making this path correct needs real TRXPOS/TRXREG-driven cursor tracking, not just
    /// a formula change — left as flagged, scoped-out future work.</summary>
    public void WriteImageData(ReadOnlySpan<byte> data, int destByteOffset)
    {
        int n = Math.Min(data.Length, _localMem.Length - destByteOffset);
        if (n <= 0) return;
        data.Slice(0, n).CopyTo(_localMem.AsSpan(destByteOffset));
        _useProceduralTexture = false;
    }

    private static int Log2(int v)
    {
        int l = 0;
        while ((1 << l) < v && l < 10) l++;
        return l;
    }

    public void SetTexture(uint baseAddr, int width, int height)
    {
        _texBase = baseAddr;
        _texWidth = width;
        _texHeight = height;
        _useProceduralTexture = baseAddr == 0;
    }

    private uint Blend(uint src, uint dst)
    {
        // Formula: (A - B) * C >> 7 + D  with A/B/D in {Cs, Cd, 0}, C in {As, Ad, FIX}
        int aSel = Registers.AlphaA;
        int bSel = Registers.AlphaB;
        int cSel = Registers.AlphaC;
        int dSel = Registers.AlphaD;
        int fix = Registers.AlphaFix;

        // Default to standard SRC_OVER if ALPHA not programmed
        if (Registers.ALPHA_1 == 0)
        {
            byte srcA = (byte)((src >> 24) & 0xFF);
            if (srcA == 0) return dst;
            if (srcA == 255) return src;
            return LerpColor(dst, src, srcA / 255f) | 0xFF000000;
        }

        Span<int> cs = stackalloc int[3];
        Span<int> cd = stackalloc int[3];
        cs[0] = (int)((src >> 16) & 0xFF);
        cs[1] = (int)((src >> 8) & 0xFF);
        cs[2] = (int)(src & 0xFF);
        cd[0] = (int)((dst >> 16) & 0xFF);
        cd[1] = (int)((dst >> 8) & 0xFF);
        cd[2] = (int)(dst & 0xFF);
        int as_ = (int)((src >> 24) & 0xFF);
        int ad = (int)((dst >> 24) & 0xFF);

        int Cv = cSel switch { 0 => as_, 1 => ad, 2 => fix, _ => 128 };

        int r = ChannelBlend(cs[0], cd[0], aSel, bSel, dSel, Cv);
        int g = ChannelBlend(cs[1], cd[1], aSel, bSel, dSel, Cv);
        int b = ChannelBlend(cs[2], cd[2], aSel, bSel, dSel, Cv);
        r = Math.Clamp(r, 0, 255);
        g = Math.Clamp(g, 0, 255);
        b = Math.Clamp(b, 0, 255);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int ChannelBlend(int cs, int cd, int aSel, int bSel, int dSel, int c)
    {
        int A = aSel switch { 0 => cs, 1 => cd, _ => 0 };
        int B = bSel switch { 0 => cs, 1 => cd, _ => 0 };
        int D = dSel switch { 0 => cs, 1 => cd, _ => 0 };
        return ((A - B) * c) / 128 + D;
    }

    private static uint LerpColor(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        byte ar = (byte)((a >> 16) & 0xFF), ag = (byte)((a >> 8) & 0xFF), ab = (byte)(a & 0xFF), aa = (byte)((a >> 24) & 0xFF);
        byte br = (byte)((b >> 16) & 0xFF), bg = (byte)((b >> 8) & 0xFF), bb = (byte)(b & 0xFF), ba = (byte)((b >> 24) & 0xFF);
        byte r = (byte)(ar + (br - ar) * t);
        byte g = (byte)(ag + (bg - ag) * t);
        byte bl = (byte)(ab + (bb - ab) * t);
        byte al = (byte)(aa + (ba - aa) * t);
        return (uint)((al << 24) | (r << 16) | (g << 8) | bl);
    }

    private static bool PointInTriangle(int px, int py, Vertex v0, Vertex v1, Vertex v2, out float a, out float b, out float c)
    {
        float denom = (v1.Y - v2.Y) * (v0.X - v2.X) + (v2.X - v1.X) * (v0.Y - v2.Y);
        if (Math.Abs(denom) < 0.0001f) { a = b = c = 0; return false; }
        a = ((v1.Y - v2.Y) * (px - v2.X) + (v2.X - v1.X) * (py - v2.Y)) / denom;
        b = ((v2.Y - v0.Y) * (px - v2.X) + (v0.X - v2.X) * (py - v2.Y)) / denom;
        c = 1 - a - b;
        return a >= -0.001f && b >= -0.001f && c >= -0.001f;
    }

    private static uint InterpolateColor(uint c0, uint c1, uint c2, float a, float b, float c)
    {
        int r = (int)(((c0 >> 16) & 0xFF) * a + ((c1 >> 16) & 0xFF) * b + ((c2 >> 16) & 0xFF) * c);
        int g = (int)(((c0 >> 8) & 0xFF) * a + ((c1 >> 8) & 0xFF) * b + ((c2 >> 8) & 0xFF) * c);
        int bl = (int)((c0 & 0xFF) * a + (c1 & 0xFF) * b + (c2 & 0xFF) * c);
        int al = (int)(((c0 >> 24) & 0xFF) * a + ((c1 >> 24) & 0xFF) * b + ((c2 >> 24) & 0xFF) * c);
        r = Math.Clamp(r, 0, 255); g = Math.Clamp(g, 0, 255); bl = Math.Clamp(bl, 0, 255); al = Math.Clamp(al, 0, 255);
        return (uint)((al << 24) | (r << 16) | (g << 8) | bl);
    }

    // ===================== Legacy / test scene =====================

    public void ReceiveCommandList(uint address, uint qwc)
    {
        // Fallback A+D stream without GIF tags (legacy)
        if (qwc == 0) return;
        uint addr = address;
        for (uint i = 0; i < qwc; i++)
        {
            uint lo = Memory.Read32(addr);
            uint hi = Memory.Read32(addr + 4);
            ProcessGifPackedWord(lo, hi);
            addr += 16;
        }
    }

    public void RenderTestScene()
    {
        uint bg = 0xFF1A1A3A;
        for (int i = 0; i < _framebuffer.Length; i++)
        {
            _framebuffer[i] = bg;
            _depthBuffer[i] = float.MaxValue;
        }

        _useProceduralTexture = true;
        _currentPrim = 0x13; // tri + IIP + TME bits optional
        Registers.WriteRegister64(0x00, 0x13);
        DrawFilledTriangle(
            new Vertex { X = 120, Y = 80, Color = 0xFF00FF00, U = 0, V = 0, Z = 0.1f },
            new Vertex { X = 320, Y = 80, Color = 0xFF00FF00, U = 1, V = 0, Z = 0.1f },
            new Vertex { X = 220, Y = 280, Color = 0xFF00FF00, U = 0.5f, V = 1, Z = 0.5f });
        PrimitivesDrawn++;

        DrawFilledTriangle(
            new Vertex { X = 340, Y = 100, Color = 0xFFFF0000, U = 0, V = 0, Z = 0.9f },
            new Vertex { X = 540, Y = 100, Color = 0xFFFF0000, U = 1, V = 0, Z = 0.9f },
            new Vertex { X = 440, Y = 300, Color = 0xFFFF0000, U = 0.5f, V = 1, Z = 0.2f });
        PrimitivesDrawn++;

        DrawQuad(80, 320, 160, 80, 0xFF00BFFF);
        DrawQuad(400, 320, 160, 80, 0xFFFFD700);
        DrawLine(100, 60, 540, 60, 0xFFFFFFFF);
        DrawLine(100, 380, 540, 380, 0xFFFFFFFF);
    }

    public void DrawTestTriangle()
    {
        DrawFilledTriangle(
            new Vertex { X = 200, Y = 150, Color = _currentRgbaq, U = 0, V = 0, Z = 0 },
            new Vertex { X = 440, Y = 150, Color = _currentRgbaq, U = 1, V = 0, Z = 0 },
            new Vertex { X = 320, Y = 350, Color = _currentRgbaq, U = 0.5f, V = 1, Z = 0 });
        PrimitivesDrawn++;
    }

    public void SaveFramebufferAsPPM(string filename)
    {
        using var writer = new StreamWriter(filename);
        writer.WriteLine("P3");
        writer.WriteLine($"{FB_WIDTH} {FB_HEIGHT}");
        writer.WriteLine("255");
        for (int y = 0; y < FB_HEIGHT; y++)
        {
            for (int x = 0; x < FB_WIDTH; x++)
            {
                uint p = _framebuffer[y * FB_WIDTH + x];
                writer.WriteLine($"{(p >> 16) & 0xFF} {(p >> 8) & 0xFF} {p & 0xFF}");
            }
        }
    }

    public uint[] GetFramebuffer() => (uint[])_framebuffer.Clone();

    /// <summary>Zero-copy view of the software raster buffer (game draws).</summary>
    public ReadOnlySpan<uint> GetFramebufferSpan() => _framebuffer;

    /// <summary>
    /// What the host should show: host FMV/boot overlay if active, else software FB.
    /// Desktop and PresentPipeline should use this for display.
    /// </summary>
    public ReadOnlySpan<uint> GetPresentSpan()
    {
        if (_hostOverlayActive && _hostOverlay != null && _hostOverlay.Length >= FB_WIDTH * FB_HEIGHT)
            return _hostOverlay;
        return _framebuffer;
    }

    public bool HostOverlayActive => _hostOverlayActive;

    /// <summary>Install or clear a full-frame ARGB8888 host overlay (640×448).</summary>
    public void SetHostOverlay(ReadOnlySpan<uint> argb, bool active = true)
    {
        if (!active || argb.Length < FB_WIDTH * FB_HEIGHT)
        {
            _hostOverlayActive = false;
            return;
        }
        if (_hostOverlay == null || _hostOverlay.Length != FB_WIDTH * FB_HEIGHT)
            _hostOverlay = new uint[FB_WIDTH * FB_HEIGHT];
        argb.Slice(0, FB_WIDTH * FB_HEIGHT).CopyTo(_hostOverlay);
        _hostOverlayActive = true;
        // Count as display activity so UI hides "no video" overlay
        PixelsWritten += FB_WIDTH * FB_HEIGHT;
    }

    public void ClearHostOverlay()
    {
        _hostOverlayActive = false;
    }

    public int FramebufferWidth => FB_WIDTH;
    public int FramebufferHeight => FB_HEIGHT;
    public uint GetPixel(int x, int y) =>
        (x < 0 || y < 0 || x >= FB_WIDTH || y >= FB_HEIGHT) ? 0 : _framebuffer[y * FB_WIDTH + x];

    /// <summary>Bulk clear using Span fill (hot path).</summary>
    public void ClearFast(uint color)
    {
        _framebuffer.AsSpan().Fill(color);
        _depthBuffer.AsSpan().Fill(float.MaxValue);
    }

    /// <summary>
    /// Host-side blit of ARGB8888 pixels into the software framebuffer (boot logo HLE).
    /// Counts as real pixels written so present/desktop paths pick the frame up.
    /// </summary>
    public void BlitArgb8888(ReadOnlySpan<uint> argb, int width, int height)
    {
        int w = Math.Min(width, FB_WIDTH);
        int h = Math.Min(height, FB_HEIGHT);
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * width;
            int dstRow = y * FB_WIDTH;
            for (int x = 0; x < w; x++)
            {
                if (srcRow + x >= argb.Length) break;
                _framebuffer[dstRow + x] = argb[srcRow + x];
                PixelsWritten++;
            }
        }
        PrimitivesDrawn++;
        // Also drive host present overlay so subsequent game black-clears cannot hide FMV HLE
        if (width >= FB_WIDTH && height >= FB_HEIGHT && argb.Length >= FB_WIDTH * FB_HEIGHT)
            SetHostOverlay(argb.Slice(0, FB_WIDTH * FB_HEIGHT), active: true);
    }

    public void Clear(uint color, float depth = float.MaxValue)
    {
        for (int i = 0; i < _framebuffer.Length; i++)
        {
            _framebuffer[i] = color;
            _depthBuffer[i] = depth;
        }
    }

    public int Step(ulong maxCycles)
    {
        int cost = CalculateWorkCost(1, 1);
        return Math.Min(cost, (int)Math.Max(1, (long)maxCycles));
    }

    public int CalculateWorkCost(uint qwc, uint nreg = 1)
    {
        const int BaseGsOverhead = 2;
        const int CostPerQwc = 5;
        const int CostPerRegister = 3;
        return Math.Max(1, BaseGsOverhead + (int)qwc * CostPerQwc + (int)nreg * CostPerRegister);
    }

    // -------- Privileged GS path (0x1200_0000) — CSR/IMR/PMODE/DISPFB --------
    private ulong _gsCsr = 0x55; // idle + revision-ish
    private ulong _gsImr = 0xFF00;
    private ulong _busdir;

    /// <summary>Privileged 32-bit view (EE often hits low half of 64-bit regs).</summary>
    public uint ReadPrivileged32(uint address)
    {
        ulong v = ReadPrivileged64(address & ~7u);
        return ((address & 4) != 0) ? (uint)(v >> 32) : (uint)v;
    }

    public void WritePrivileged32(uint address, uint value)
    {
        uint baseAddr = address & ~7u;
        ulong cur = ReadPrivileged64(baseAddr);
        if ((address & 4) != 0)
            cur = (cur & 0xFFFFFFFFUL) | ((ulong)value << 32);
        else
            cur = (cur & 0xFFFFFFFF00000000UL) | value;
        WritePrivileged64(baseAddr, cur);
    }

    public ulong ReadPrivileged64(uint address)
    {
        return (address & 0xFFFF) switch
        {
            0x0000 => Registers.PMODE,
            0x0020 => Registers.SMODE2,
            0x0070 => Registers.DISPFB1,
            0x0080 => Registers.DISPLAY1,
            0x0090 => Registers.DISPFB2,
            0x00A0 => Registers.DISPLAY2,
            0x1000 => _gsCsr | (1UL << 13), // FIFO empty-ish / idle
            0x1010 => _gsImr,
            0x1040 => _busdir,
            _ => 0
        };
    }

    public void WritePrivileged64(uint address, ulong value)
    {
        switch (address & 0xFFFF)
        {
            case 0x0000: SetPrivilegedDisplay(0x0000, value); break;
            case 0x0020: SetPrivilegedDisplay(0x0020, value); break;
            case 0x0070: SetPrivilegedDisplay(0x0070, value); break;
            case 0x0080: SetPrivilegedDisplay(0x0080, value); break;
            case 0x0090: SetPrivilegedDisplay(0x0090, value); break;
            case 0x00A0: SetPrivilegedDisplay(0x00A0, value); break;
            case 0x1000: // GS_CSR — w1c on some bits; RESET when bit 9?
                if ((value & (1UL << 9)) != 0)
                {
                    // soft reset drawing state but keep FB
                    _verts.Clear();
                }
                // FINISH / SIGNAL / VSINT clear when written 1
                _gsCsr &= ~(value & 0xF);
                _gsCsr |= 0x8; // idle
                break;
            case 0x1010: _gsImr = value; break;
            case 0x1040: _busdir = value; break;
        }
    }

    private void SetPrivilegedDisplay(uint which, ulong value)
    {
        // Write through to GsRegisters display fields used by present helpers
        switch (which)
        {
            case 0x0000: Registers.SetPmode(value); break;
            case 0x0020: Registers.SetSmode2(value); break;
            case 0x0070: Registers.SetDispfb1(value); break;
            case 0x0080: Registers.SetDisplay1(value); break;
            case 0x0090: Registers.SetDispfb2(value); break;
            case 0x00A0: Registers.SetDisplay2(value); break;
        }
    }
}
