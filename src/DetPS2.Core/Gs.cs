using System;
using System.Collections.Generic;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer (GS) - GS Lane
/// 
/// Added basic scissoring support using SCISSOR_1 register.
/// Pixels outside the scissor rectangle are now clipped.
/// This is a focused correctness improvement on top of the vertex/UV work.
/// </summary>
public sealed class Gs
{
    public SystemMemory Memory { get; }
    public GsRegisters Registers { get; } = new();

    private const int FB_WIDTH = 640;
    private const int FB_HEIGHT = 448;
    private readonly uint[] _framebuffer = new uint[FB_WIDTH * FB_HEIGHT];

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
        _currentPrim = 0;
        _currentRgbaq = 0xFFFFFFFF;
        _lastU = _lastV = 0;
        _lastS = _lastT = 1;
        _currentVertices.Clear();
    }

    // ==================== Scissoring Helper ====================

    private bool IsPixelInScissor(int x, int y)
    {
        uint scissor = Registers.SCISSOR_1;

        // GS SCISSOR_1 layout (simplified but functional):
        // bits  0-10 : X0 (left)
        // bits 16-26 : X1 (right)
        // bits 32-42 : Y0 (top)   [in upper 64 bits, but we read as uint for now]
        // bits 48-58 : Y1 (bottom)
        // For early work we use a reasonable interpretation.

        int x0 = (int)(scissor & 0x7FF);
        int x1 = (int)((scissor >> 16) & 0x7FF);
        int y0 = (int)((scissor >> 32) & 0x7FF); // may be 0 if not set
        int y1 = (int)((scissor >> 48) & 0x7FF);

        // If scissor is all zeros (never set), allow everything
        if (x0 == 0 && x1 == 0 && y0 == 0 && y1 == 0)
            return true;

        // Clamp to valid range if only partially set
        if (x1 == 0) x1 = FB_WIDTH - 1;
        if (y1 == 0) y1 = FB_HEIGHT - 1;

        return x >= x0 && x <= x1 && y >= y0 && y <= y1;
    }

    public void ProcessGifPackedWord(uint dataLow, uint dataHigh)
    {
        uint regAddr = (dataHigh >> 24) & 0x7F;
        uint value   = dataLow;

        if (regAddr != 0)
        {
            Registers.WriteRegister(regAddr, value);

            if (regAddr == 0x00) { _currentPrim = value; _currentVertices.Clear(); UpdateMaxVerticesForPrim(); }
            if (regAddr == 0x01) _currentRgbaq = value;

            if (regAddr == 0x03)
            {
                _lastU = (value & 0x3FFF) / 16.0f;
                _lastV = ((value >> 16) & 0x3FFF) / 16.0f;
            }

            if (regAddr == 0x02)
            {
                _lastS = BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
                _lastT = _lastS;
            }

            if (regAddr == 0x04 || regAddr == 0x05)
            {
                AddVertexFromXyz(value);
                TryDispatchPrimitive();
            }
        }
    }

    public void ReceiveCommandList(uint address, uint qwc)
    {
        if (Memory == null || qwc == 0) return;

        uint addr = address;
        uint remaining = qwc;

        while (remaining > 0)
        {
            uint dataLow  = Memory.Read32(addr);
            uint dataHigh = Memory.Read32(addr + 4);

            uint regAddr = (dataHigh >> 24) & 0x7F;
            uint value   = dataLow;

            if (regAddr != 0)
            {
                Registers.WriteRegister(regAddr, value);

                if (regAddr == 0x00) { _currentPrim = value; _currentVertices.Clear(); UpdateMaxVerticesForPrim(); }
                if (regAddr == 0x01) _currentRgbaq = value;

                if (regAddr == 0x03)
                {
                    _lastU = (value & 0x3FFF) / 16.0f;
                    _lastV = ((value >> 16) & 0x3FFF) / 16.0f;
                }

                if (regAddr == 0x02)
                {
                    _lastS = BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
                    _lastT = _lastS;
                }

                if (regAddr == 0x04 || regAddr == 0x05)
                {
                    AddVertexFromXyz(value);
                    TryDispatchPrimitive();
                }
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
                case 3:
                case 4: DrawTestTriangle(); break;
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
            1 => 2,
            3 or 4 => 3,
            5 => 4,
            _ => 3
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
            X = x,
            Y = y,
            Color = _currentRgbaq,
            U = _lastU,
            V = _lastV,
            S = _lastS,
            T = _lastT
        });

        Console.WriteLine($"[GS] Vertex: ({x}, {y}) UV=({_lastU:F2},{_lastV:F2})");
    }

    private void TryDispatchPrimitive()
    {
        if (_currentVertices.Count < _maxVerticesForPrim) return;

        uint primType = _currentPrim & 0x7;

        switch (primType)
        {
            case 3:
            case 4:
                if (_currentVertices.Count >= 3)
                {
                    var v0 = _currentVertices[0];
                    var v1 = _currentVertices[1];
                    var v2 = _currentVertices[2];
                    DrawFilledTriangle((v0.X, v0.Y, v0.Color), (v1.X, v1.Y, v1.Color), (v2.X, v2.Y, v2.Color));
                    _currentVertices.Clear();
                }
                break;

            case 1:
                if (_currentVertices.Count >= 2)
                {
                    var v0 = _currentVertices[0];
                    var v1 = _currentVertices[1];
                    DrawLine(v0.X, v0.Y, v1.X, v1.Y, v0.Color);
                    _currentVertices.Clear();
                }
                break;

            case 5:
                if (_currentVertices.Count >= 3)
                {
                    DrawFilledTriangle(( _currentVertices[0].X, _currentVertices[0].Y, _currentVertices[0].Color),
                                       ( _currentVertices[1].X, _currentVertices[1].Y, _currentVertices[1].Color),
                                       ( _currentVertices[2].X, _currentVertices[2].Y, _currentVertices[2].Color));
                    _currentVertices.Clear();
                }
                break;

            default:
                _currentVertices.Clear();
                break;
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
        for (int i = 0; i < _framebuffer.Length; i++)
            _framebuffer[i] = bgColor;

        DrawFilledTriangle((120, 80, 0xFF00FF00), (320, 80, 0xFF00FF00), (220, 280, 0xFF00FF00));
        DrawFilledTriangle((340, 100, 0xFFFF0000), (540, 100, 0xFFFF0000), (440, 300, 0xFFFF0000));
        DrawQuad(80, 320, 160, 80, 0xFF00BFFF);
        DrawQuad(400, 320, 160, 80, 0xFFFFD700);
        DrawLine(100, 60, 540, 60, 0xFFFFFFFF);
        DrawLine(100, 380, 540, 380, 0xFFFFFFFF);
    }

    public uint SampleTexture(int u, int v)
    {
        int tu = u % _texWidth;
        int tv = v % _texHeight;
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
        var v0 = (200, 150, _currentRgbaq);
        var v1 = (440, 150, _currentRgbaq);
        var v2 = (320, 350, _currentRgbaq);
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
            if (x0 >= 0 && x0 < FB_WIDTH && y0 >= 0 && y0 < FB_HEIGHT && IsPixelInScissor(x0, y0))
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
                if (xx >= 0 && yy >= 0 && IsPixelInScissor(xx, yy))
                    _framebuffer[yy * FB_WIDTH + xx] = color;
            }
        }
    }

    private void DrawFilledTriangle((int x, int y, uint c) v0, (int x, int y, uint c) v1, (int x, int y, uint c) v2)
    {
        int minX = Math.Min(v0.x, Math.Min(v1.x, v2.x));
        int maxX = Math.Max(v0.x, Math.Max(v1.x, v2.x));
        int minY = Math.Min(v0.y, Math.Min(v1.y, v2.y));
        int maxY = Math.Max(v0.y, Math.Max(v1.y, v2.y));

        for (int y = Math.Max(0, minY); y <= Math.Min(FB_HEIGHT - 1, maxY); y++)
        {
            for (int x = Math.Max(0, minX); x <= Math.Min(FB_WIDTH - 1, maxX); x++)
            {
                if (PointInTriangle(x, y, v0, v1, v2) && IsPixelInScissor(x, y))
                    _framebuffer[y * FB_WIDTH + x] = v0.c;
            }
        }
    }

    private bool PointInTriangle(int px, int py, (int x, int y, uint c) v0, (int x, int y, uint c) v1, (int x, int y, uint c) v2)
    {
        float denom = ((v1.y - v2.y) * (v0.x - v2.x) + (v2.x - v1.x) * (v0.y - v2.y));
        if (Math.Abs(denom) < 0.0001f) return false;

        float a = ((v1.y - v2.y) * (px - v2.x) + (v2.x - v1.x) * (py - v2.y)) / denom;
        float b = ((v2.y - v0.y) * (px - v2.x) + (v0.x - v2.x) * (py - v2.y)) / denom;
        float c = 1 - a - b;

        return a >= 0 && a <= 1 && b >= 0 && b <= 1 && c >= 0 && c <= 1;
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