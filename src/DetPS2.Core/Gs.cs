using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer (GS) - GS Lane active (Phase 7 foundation)
/// 
/// Milestone: GsRegisters integration + basic real packet processing
/// 
/// We now have:
/// - Explicit, save-state friendly register file
/// - ReceiveCommandList that walks GIF data and applies registers
/// - Legacy drawing path preserved so we still get visible output
/// 
/// Next in lane: Proper primitive assembly from real vertex data + GIFtag handling improvements
/// </summary>
public sealed class Gs
{
    public SystemMemory Memory { get; }
    public GsRegisters Registers { get; } = new();

    private const int FB_WIDTH = 640;
    private const int FB_HEIGHT = 448;
    private readonly uint[] _framebuffer = new uint[FB_WIDTH * FB_HEIGHT];

    // Temporary legacy fields during transition to full register-driven pipeline
    private uint _currentPrim;
    private uint _currentRgbaq = 0xFFFFFFFF;

    // Texture stub (will be replaced by proper state in Registers + sampler)
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
        Registers.Reset();
        Array.Clear(_framebuffer, 0, _framebuffer.Length);
        _currentPrim = 0;
        _currentRgbaq = 0xFFFFFFFF;
    }

    /// <summary>
    /// Main entry point from GIF.
    /// Walks the buffer at [address, address + qwc*16] and applies A+D style register writes.
    /// </summary>
    public void ReceiveCommandList(uint address, uint qwc)
    {
        if (Memory == null || qwc == 0) return;

        uint addr = address;
        uint remaining = qwc;

        while (remaining > 0)
        {
            // GIF data is 128-bit words. We read two 32-bit words per step.
            uint dataLow  = Memory.Read32(addr);
            uint dataHigh = Memory.Read32(addr + 4);

            // Crude but functional A+D extraction for early work.
            // Real format: lower 64 bits = data, upper contains address + control bits.
            uint regAddr = (dataHigh >> 24) & 0x7F;   // common location for register address in many packets
            uint value   = dataLow;

            if (regAddr != 0)
            {
                Registers.WriteRegister(regAddr, value);

                // Keep legacy path alive during transition
                if (regAddr == 0x00) _currentPrim = value;
                if (regAddr == 0x01) _currentRgbaq = value;
            }

            addr += 16;
            remaining--;
        }

        // Temporary visual output so we can see progress immediately.
        // This will be replaced by real DrawPrimitive calls once we have vertex assembly.
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

    public void SetPrim(uint prim)
    {
        Registers.WriteRegister(0x00, prim);
        _currentPrim = prim;
        Console.WriteLine($"[GS] PRIM = 0x{prim:X}");
    }

    public void SetRGBAQ(uint rgbaq)
    {
        Registers.WriteRegister(0x01, rgbaq);
        _currentRgbaq = rgbaq;
        Console.WriteLine($"[GS] RGBAQ = 0x{rgbaq:X8}");
    }

    public void DrawVertex(uint xyz)
    {
        Registers.WriteRegister(0x04, xyz);
        // TODO (next in lane): collect into vertex buffer for real primitive assembly
    }

    // ==================== Texture (stub) ====================

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

    // ==================== Software Drawing Primitives ====================

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
                _framebuffer[y0 * FB_WIDTH + x0] = color;

            if (x0 == x1 && y0 == y1) break;

            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
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
                    _framebuffer[y * FB_WIDTH + x] = v0.c;
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