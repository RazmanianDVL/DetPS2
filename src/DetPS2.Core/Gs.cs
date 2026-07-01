using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer - Phase 2/3
/// Supports multiple primitives + basic texture sampling stub.
/// </summary>
public sealed class Gs
{
    public SystemMemory Memory { get; }

    private const int FB_WIDTH = 640;
    private const int FB_HEIGHT = 448;
    private readonly uint[] _framebuffer = new uint[FB_WIDTH * FB_HEIGHT];

    private uint _currentPrim;
    private uint _currentRgbaq = 0xFFFFFFFF;

    // Simple texture state (stub for Phase 2/3)
    private uint _texBase = 0;
    private int _texWidth = 64;
    private int _texHeight = 64;

    public Gs(SystemMemory memory)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_framebuffer, 0, _framebuffer.Length);
        _currentPrim = 0;
        _currentRgbaq = 0xFFFFFFFF;
    }

    public void ReceiveCommandList(uint address, uint qwc)
    {
        uint primType = _currentPrim & 0x7;

        switch (primType)
        {
            case 1:
                DrawLine(200, 200, 400, 300, _currentRgbaq);
                break;
            case 3:
            case 4:
                DrawTestTriangle();
                break;
            case 5:
                DrawQuad(250, 180, 180, 120, _currentRgbaq);
                break;
            default:
                DrawTestTriangle();
                break;
        }
    }

    public void SetPrim(uint prim)
    {
        _currentPrim = prim;
        Console.WriteLine($"[GS] PRIM = 0x{prim:X}");
    }

    public void SetRGBAQ(uint rgbaq)
    {
        _currentRgbaq = rgbaq;
        Console.WriteLine($"[GS] RGBAQ = 0x{rgbaq:X8}");
    }

    public void DrawVertex(uint xyz)
    {
        // Future: collect vertices for more advanced primitive assembly
    }

    // ==================== Texture Sampling Stub ====================

    public uint SampleTexture(int u, int v)
    {
        // Very simple nearest-neighbor sampling from a fake texture region
        // In a real implementation this would read from GS VRAM / texture memory
        int tu = u % _texWidth;
        int tv = v % _texHeight;

        // Generate a simple checkerboard pattern for demo purposes
        bool checker = ((tu / 8) + (tv / 8)) % 2 == 0;
        return checker ? 0xFFFF00FF : 0xFF00FFFF; // magenta / cyan
    }

    public void SetTexture(uint baseAddr, int width, int height)
    {
        _texBase = baseAddr;
        _texWidth = width;
        _texHeight = height;
        Console.WriteLine($"[GS] Texture set @ 0x{baseAddr:X} ({width}x{height})");
    }

    // ==================== Primitive Drawing ====================

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
            if (x0 >= 0 && x0 < FB_WIDTH && y0 >= 0 && y0 < FB_HEIGHT)
            {
                _framebuffer[y0 * FB_WIDTH + x0] = color;
            }

            if (x0 == x1 && y0 == y1) break;

            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx)  { err += dx; y0 += sy; }
        }

        Console.WriteLine("[GS] Drew line.");
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

        Console.WriteLine("[GS] Drew quad.");
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
                if (PointInTriangle(x, y, v0, v1, v2))
                {
                    _framebuffer[y * FB_WIDTH + x] = v0.c;
                }
            }
        }

        Console.WriteLine("[GS] Drew triangle.");
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
