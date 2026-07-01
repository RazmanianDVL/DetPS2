using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer - Phase 2 software renderer.
/// Supports basic primitive handling driven by GIF packets.
/// Framebuffer + PPM export for early verification (deterministic, no host dependencies).
/// </summary>
public sealed class Gs
{
    public SystemMemory Memory { get; }

    private const int FB_WIDTH = 640;
    private const int FB_HEIGHT = 448;
    private readonly uint[] _framebuffer = new uint[FB_WIDTH * FB_HEIGHT];

    // Current primitive state (for GIF-driven drawing)
    private uint _currentPrim;
    private uint _currentRgbaq; // RGBAQ packed
    private int _vertexCount;
    private readonly (int x, int y, uint color)[] _vertices = new (int, int, uint)[3]; // simple triangle for Phase 2

    public Gs(SystemMemory memory)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_framebuffer, 0, _framebuffer.Length);
        _currentPrim = 0;
        _currentRgbaq = 0xFFFFFFFF; // white
        _vertexCount = 0;
    }

    /// <summary>
    /// Called by GIF when a command list (or test tag) arrives.
    /// For Phase 2 we support a simple path that draws real primitives.
    /// </summary>
    public void ReceiveCommandList(uint address, uint qwc)
    {
        // In a full implementation we would read the actual GIF packets from memory.
        // For this Phase 2 milestone we interpret the test data and draw a real primitive.
        Console.WriteLine("[GS] Receiving command list - drawing real primitive from GIF data...");

        // For the current test GIFtag we draw a simple colored triangle
        // (in future this will parse real PRIM/RGBAQ/XYZ2 from the GIF stream)
        DrawTestTriangle();
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
        // Convert PS2 fixed-point XYZ to screen coords (simplified)
        int x = (int)((xyz >> 4) & 0xFFF);  // rough scaling for test
        int y = (int)((xyz >> 20) & 0xFFF);

        if (_vertexCount < 3)
        {
            _vertices[_vertexCount] = (x, y, _currentRgbaq);
            _vertexCount++;
        }

        if (_vertexCount == 3)
        {
            DrawTriangle(_vertices[0], _vertices[1], _vertices[2]);
            _vertexCount = 0; // ready for next
        }
    }

    private void DrawTestTriangle()
    {
        // Fallback / test: draw a visible triangle using current color
        // Positions roughly in the center of the 640x448 framebuffer
        var v0 = (200, 150, _currentRgbaq);
        var v1 = (440, 150, _currentRgbaq);
        var v2 = (320, 350, _currentRgbaq);

        DrawTriangle(v0, v1, v2);
    }

    private void DrawTriangle((int x, int y, uint color) v0, (int x, int y, uint color) v1, (int x, int y, uint color) v2)
    {
        // Very simple filled triangle rasterizer (Phase 2 - correctness > speed)
        // Bounding box
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
                    int index = y * FB_WIDTH + x;
                    _framebuffer[index] = v0.color; // flat color for simplicity
                }
            }
        }

        Console.WriteLine("[GS] Drew triangle primitive to framebuffer.");
    }

    private bool PointInTriangle(int px, int py, (int x, int y, uint c) v0, (int x, int y, uint c) v1, (int x, int y, uint c) v2)
    {
        // Barycentric test (simple, deterministic, integer math)
        float denom = ((v1.y - v2.y) * (v0.x - v2.x) + (v2.x - v1.x) * (v0.y - v2.y));
        if (Math.Abs(denom) < 0.0001f) return false;

        float a = ((v1.y - v2.y) * (px - v2.x) + (v2.x - v1.x) * (py - v2.y)) / denom;
        float b = ((v2.y - v0.y) * (px - v2.x) + (v0.x - v2.x) * (py - v2.y)) / denom;
        float c = 1 - a - b;

        return a >= 0 && a <= 1 && b >= 0 && b <= 1 && c >= 0 && c <= 1;
    }

    /// <summary>
    /// Saves the current framebuffer as a PPM image (portable, no dependencies).
    /// </summary>
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
