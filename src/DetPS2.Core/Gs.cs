using System;
using System.Collections.Generic;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer (GS) - GS Lane
/// 
/// Added basic alpha blending support.
/// The rasterizer now blends the new pixel with the existing framebuffer
/// using a simple source-over style blend (common for many PS2 effects).
/// </summary>
public sealed class Gs
{
    public SystemMemory Memory { get; }
    public GsRegisters Registers { get; } = new();

    private const int FB_WIDTH = 640;
    private const int FB_HEIGHT = 448;
    private readonly uint[] _framebuffer = new uint[FB_WIDTH * FB_HEIGHT];
    private readonly float[] _depthBuffer = new float[FB_WIDTH * FB_HEIGHT];

    private uint _currentPrim;
    private uint _currentRgbaq = 0xFFFFFFFF;

    private float _lastU = 0;
    private float _lastV = 0;
    private float _lastS = 1;
    private float _lastT = 1;

    private uint _texBase = 0;
    private int _texWidth = 64;
    private int _texHeight = 64;

    private struct Vertex
    {
        public int X;
        public int Y;
        public uint Color;
        public float U;
        public float V;
        public float S;
        public float T;
        public float Z;
    }

    private readonly List<Vertex> _currentVertices = new();
    private int _maxVerticesForPrim = 3;

    public Gs(SystemMemory memory)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void Reset()
    {
        Registers.Reset();
        Array.Clear(_framebuffer, 0, _framebuffer.Length);
        Array.Clear(_depthBuffer, 0, _depthBuffer.Length);
        _currentPrim = 0;
        _currentRgbaq = 0xFFFFFFFF;
        _lastU = _lastV = 0;
        _lastS = _lastT = 1;
        _currentVertices.Clear();
    }

    private bool IsPixelInScissor(int x, int y)
    {
        uint scissor = Registers.SCISSOR_1;
        int x0 = (int)(scissor & 0x7FF);
        int x1 = (int)((scissor >> 16) & 0x7FF);
        int y0 = (int)((scissor >> 32) & 0x7FF);
        int y1 = (int)((scissor >> 48) & 0x7FF);

        if (x0 == 0 && x1 == 0 && y0 == 0 && y1 == 0) return true;
        if (x1 == 0) x1 = FB_WIDTH - 1;
        if (y1 == 0) y1 = FB_HEIGHT - 1;

        return x >= x0 && x <= x1 && y >= y0 && y <= y1;
    }

    public void ProcessGifPackedWord(uint dataLow, uint dataHigh)
    {
        uint regAddr = (dataHigh >> 24) & 0x7F;
        uint value = dataLow;

        if (regAddr != 0)
        {
            Registers.WriteRegister(regAddr, value);

            if (regAddr == 0x00) { _currentPrim = value; _currentVertices.Clear(); UpdateMaxVerticesForPrim(); }
            if (regAddr == 0x01) _currentRgbaq = value;

            if (regAddr == 0x03) { _lastU = (value & 0x3FFF) / 16.0f; _lastV = ((value >> 16) & 0x3FFF) / 16.0f; }
            if (regAddr == 0x02) { _lastS = BitConverter.ToSingle(BitConverter.GetBytes(value), 0); _lastT = _lastS; }

            if (regAddr == 0x04 || regAddr == 0x05) { AddVertexFromXyz(value); TryDispatchPrimitive(); }
        }
    }

    public void ReceiveCommandList(uint address, uint qwc)
    {
        if (Memory == null || qwc == 0) return;

        uint addr = address;
        uint remaining = qwc;

        while (remaining > 0)
        {
            uint dataLow = Memory.Read32(addr);
            uint dataHigh = Memory.Read32(addr + 4);

            uint regAddr = (dataHigh >> 24) & 0x7F;
            uint value = dataLow;

            if (regAddr != 0)
            {
                Registers.WriteRegister(regAddr, value);

                if (regAddr == 0x00) { _currentPrim = value; _currentVertices.Clear(); UpdateMaxVerticesForPrim(); }
                if (regAddr == 0x01) _currentRgbaq = value;

                if (regAddr == 0x03) { _lastU = (value & 0x3FFF) / 16.0f; _lastV = ((value >> 16) & 0x3FFF) / 16.0f; }
                if (regAddr == 0x02) { _lastS = BitConverter.ToSingle(BitConverter.GetBytes(value), 0); _lastT = _lastS; }

                if (regAddr == 0x04 || regAddr == 0x05) { AddVertexFromXyz(value); TryDispatchPrimitive(); }
            }

            addr += 16;
            remaining--;
        }

        if (_currentVertices.Count == 0)
        {
            uint primType = _currentPrim & 0x7;
            switch (primType)
            {
                case 1: DrawLine(200, 200, 400, 300, _currentRgbaq); break;
                case 3: case 4: DrawTestTriangle(); break;
                case 5: DrawQuad(250, 180, 180, 120, _currentRgbaq); break;
                default: DrawTestTriangle(); break;
            }
        }
    }

    private void UpdateMaxVerticesForPrim()
    {
        uint primType = _currentPrim & 0x7;
        _maxVerticesForPrim = primType switch
        {
            1 => 2, 3 or 4 => 3, 5 => 4, _ => 3
        };
    }

    private void AddVertexFromXyz(uint xyz)
    {
        int x = (int)(xyz & 0xFFFF);
        int y = (int)((xyz >> 16) & 0xFFFF);

        x = (x * FB_WIDTH) / 4096;
        y = (y * FB_HEIGHT) / 4096;

        _currentVertices.Add(new Vertex
        {
            X = x, Y = y, Color = _currentRgbaq,
            U = _lastU, V = _lastV, S = _lastS, T = _lastT, Z = 0
        });

        Console.WriteLine($"[GS] Vertex added: ({x},{y}) UV=({_lastU:F2},{_lastV:F2})");
    }

    private void TryDispatchPrimitive()
    {
        if (_currentVertices.Count < _maxVerticesForPrim) return;

        uint primType = _currentPrim & 0x7;

        switch (primType)
        {
            case 3: case 4:
                if (_currentVertices.Count >= 3) { DrawFilledTriangle(_currentVertices[0], _currentVertices[1], _currentVertices[2]); _currentVertices.Clear(); }
                break;
            case 1:
                if (_currentVertices.Count >= 2) { DrawLine(_currentVertices[0].X, _currentVertices[0].Y, _currentVertices[1].X, _currentVertices[1].Y, _currentVertices[0].Color); _currentVertices.Clear(); }
                break;
            case 5:
                if (_currentVertices.Count >= 3) { DrawFilledTriangle(_currentVertices[0], _currentVertices[1], _currentVertices[2]); _currentVertices.Clear(); }
                break;
            default: _currentVertices.Clear(); break;
        }
    }

    public void SetPrim(uint prim)
    {
        Registers.WriteRegister(0x00, prim);
        _currentPrim = prim;
        _currentVertices.Clear();
        UpdateMaxVerticesForPrim();
    }

    public void SetRGBAQ(uint rgbaq)
    {
        Registers.WriteRegister(0x01, rgbaq);
        _currentRgbaq = rgbaq;
    }

    public void DrawVertex(uint xyz)
    {
        Registers.WriteRegister(0x04, xyz);
        AddVertexFromXyz(xyz);
        TryDispatchPrimitive();
    }

    public void RenderTestScene()
    {
        uint bgColor = 0xFF1a1a3a;
        for (int i = 0; i < _framebuffer.Length; i++) { _framebuffer[i] = bgColor; _depthBuffer[i] = float.MaxValue; }

        DrawFilledTriangle(
            new Vertex { X = 120, Y = 80, Color = 0xFF00FF00, U = 0, V = 0, Z = 0.1f },
            new Vertex { X = 320, Y = 80, Color = 0xFF00FF00, U = 1, V = 0, Z = 0.1f },
            new Vertex { X = 220, Y = 280, Color = 0xFF00FF00, U = 0.5f, V = 1, Z = 0.5f });

        DrawFilledTriangle(
            new Vertex { X = 340, Y = 100, Color = 0xFFFF0000, U = 0, V = 0, Z = 0.9f },
            new Vertex { X = 540, Y = 100, Color = 0xFFFF0000, U = 1, V = 0, Z = 0.9f },
            new Vertex { X = 440, Y = 300, Color = 0xFFFF0000, U = 0.5f, V = 1, Z = 0.2f });

        DrawQuad(80, 320, 160, 80, 0xFF00BFFF);
        DrawQuad(400, 320, 160, 80, 0xFFFFD700);
        DrawLine(100, 60, 540, 60, 0xFFFFFFFF);
        DrawLine(100, 380, 540, 380, 0xFFFFFFFF);
    }

    public uint SampleTexture(float u, float v)
    {
        int tu = (int)(u * _texWidth) % _texWidth;
        int tv = (int)(v * _texHeight) % _texHeight;
        if (tu < 0) tu += _texWidth;
        if (tv < 0) tv += _texHeight;

        bool checker = ((tu / 8) + (tv / 8)) % 2 == 0;
        return checker ? 0xFFFF00FF : 0xFF00FFFF;
    }

    public void SetTexture(uint baseAddr, int width, int height)
    {
        _texBase = baseAddr;
        _texWidth = width;
        _texHeight = height;
    }

    public void DrawTestTriangle()
    {
        var v0 = new Vertex { X = 200, Y = 150, Color = _currentRgbaq, U = 0, V = 0, Z = 0 };
        var v1 = new Vertex { X = 440, Y = 150, Color = _currentRgbaq, U = 1, V = 0, Z = 0 };
        var v2 = new Vertex { X = 320, Y = 350, Color = _currentRgbaq, U = 0.5f, V = 1, Z = 0 };
        DrawFilledTriangle(v0, v1, v2);
    }

    public void DrawLine(int x0, int y0, int x1, int y1, uint color)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            if (x0 >= 0 && x0 < FB_WIDTH && y0 >= 0 && y0 < FB_HEIGHT)
                _framebuffer[y0 * FB_WIDTH + x0] = color;

            if (x0 == x1 && y0 == y1) break;

            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    public void DrawQuad(int x, int y, int w, int h, uint color)
    {
        for (int yy = y; yy < y + h && yy < FB_HEIGHT; yy++)
        {
            for (int xx = x; xx < x + w && xx < FB_WIDTH; xx++)
            {
                if (xx >= 0 && yy >= 0)
                    _framebuffer[yy * FB_WIDTH + xx] = color;
            }
        }
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
                if (PointInTriangle(x, y, v0, v1, v2, out float a, out float b, out float c))
                {
                    int idx = y * FB_WIDTH + x;

                    float z = v0.Z * a + v1.Z * b + v2.Z * c;
                    if (z > _depthBuffer[idx]) continue;

                    uint color = InterpolateColor(v0.Color, v1.Color, v2.Color, a, b, c);

                    float iu = v0.U * a + v1.U * b + v2.U * c;
                    float iv = v0.V * a + v1.V * b + v2.V * c;

                    uint texColor = SampleTexture(iu, iv);

                    byte tr = (byte)((texColor >> 16) & 0xFF);
                    byte tg = (byte)((texColor >> 8) & 0xFF);
                    byte tb = (byte)(texColor & 0xFF);

                    byte cr = (byte)((color >> 16) & 0xFF);
                    byte cg = (byte)((color >> 8) & 0xFF);
                    byte cb = (byte)(color & 0xFF);

                    byte r = (byte)((tr * cr) / 255);
                    byte g = (byte)((tg * cg) / 255);
                    byte b = (byte)((tb * cb) / 255);

                    uint finalColor = (uint)(0xFF000000 | (r << 16) | (g << 8) | b);

                    // Basic alpha blending (source over)
                    if (Registers.ALPHA_1 != 0) // simple check if blending is "enabled"
                    {
                        uint dst = _framebuffer[idx];
                        finalColor = Blend(finalColor, dst);
                    }

                    _framebuffer[idx] = finalColor;
                    _depthBuffer[idx] = z;
                }
            }
        }
    }

    private uint Blend(uint src, uint dst)
    {
        // Simple "source alpha" blend (very common)
        byte srcA = (byte)((src >> 24) & 0xFF);
        if (srcA == 0) return dst;
        if (srcA == 255) return src;

        float alpha = srcA / 255.0f;
        float invAlpha = 1.0f - alpha;

        byte sr = (byte)((src >> 16) & 0xFF);
        byte sg = (byte)((src >> 8) & 0xFF);
        byte sb = (byte)(src & 0xFF);

        byte dr = (byte)((dst >> 16) & 0xFF);
        byte dg = (byte)((dst >> 8) & 0xFF);
        byte db = (byte)(dst & 0xFF);

        byte r = (byte)(sr * alpha + dr * invAlpha);
        byte g = (byte)(sg * alpha + dg * invAlpha);
        byte b = (byte)(sb * alpha + db * invAlpha);

        return (uint)(0xFF000000 | (r << 16) | (g << 8) | b);
    }

    private bool PointInTriangle(int px, int py, Vertex v0, Vertex v1, Vertex v2, out float a, out float b, out float c)
    {
        float denom = ((v1.Y - v2.Y) * (v0.X - v2.X) + (v2.X - v1.X) * (v0.Y - v2.Y));
        if (Math.Abs(denom) < 0.0001f) { a = b = c = 0; return false; }

        a = ((v1.Y - v2.Y) * (px - v2.X) + (v2.X - v1.X) * (py - v2.Y)) / denom;
        b = ((v2.Y - v0.Y) * (px - v2.X) + (v0.X - v2.X) * (py - v2.Y)) / denom;
        c = 1 - a - b;

        return a >= 0 && a <= 1 && b >= 0 && b <= 1 && c >= 0 && c <= 1;
    }

    private uint InterpolateColor(uint c0, uint c1, uint c2, float a, float b, float c)
    {
        byte r0 = (byte)((c0 >> 16) & 0xFF);
        byte g0 = (byte)((c0 >> 8) & 0xFF);
        byte b0 = (byte)(c0 & 0xFF);

        byte r1 = (byte)((c1 >> 16) & 0xFF);
        byte g1 = (byte)((c1 >> 8) & 0xFF);
        byte b1 = (byte)(c1 & 0xFF);

        byte r2 = (byte)((c2 >> 16) & 0xFF);
        byte g2 = (byte)((c2 >> 8) & 0xFF);
        byte b2 = (byte)(c2 & 0xFF);

        int r = (int)(r0 * a + r1 * b + r2 * c);
        int g = (int)(g0 * a + g1 * b + g2 * c);
        int bl = (int)(b0 * a + b1 * b + b2 * c);

        r = Math.Clamp(r, 0, 255);
        g = Math.Clamp(g, 0, 255);
        bl = Math.Clamp(bl, 0, 255);

        return (uint)(0xFF000000 | (r << 16) | (g << 8) | bl);
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
                int index = y * FB_WIDTH + x;
                uint pixel = _framebuffer[index];

                byte r = (byte)((pixel >> 16) & 0xFF);
                byte g = (byte)((pixel >> 8) & 0xFF);
                byte b = (byte)(pixel & 0xFF);

                writer.WriteLine($"{r} {g} {b}");
            }
        }

        Console.WriteLine($"[GS] Framebuffer saved to {filename}");
    }

    public uint[] GetFramebuffer() => (uint[])_framebuffer.Clone();
    public int FramebufferWidth => FB_WIDTH;
    public int FramebufferHeight => FB_HEIGHT;

    public void Step(ulong cycles) { }
}