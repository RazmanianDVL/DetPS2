using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer (GS) with a simple software framebuffer.
/// This is the beginning of actual pixel output.
/// </summary>
public sealed class Gs
{
    public SystemMemory Memory { get; }

    // Simple software framebuffer (ARGB8888)
    // Using a 1D array for simplicity. Resolution is arbitrary for now.
    private const int FB_WIDTH = 640;
    private const int FB_HEIGHT = 448;
    private readonly uint[] _framebuffer = new uint[FB_WIDTH * FB_HEIGHT];

    private uint _frameBufferBase;
    private uint _zBufferBase;

    public Gs(SystemMemory memory)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public void Reset()
    {
        Array.Clear(_framebuffer, 0, _framebuffer.Length);
        _frameBufferBase = 0;
        _zBufferBase = 0;
    }

    public void ReceiveCommandList(uint address, uint qwc)
    {
        Console.WriteLine($"[GS] Processing {qwc} quadwords of GS commands");

        // For early testing, just draw something whenever we receive data
        DrawTestPattern();
    }

    /// <summary>
    /// Draws a simple test pattern directly into the software framebuffer.
    /// This is the first step toward real rendering.
    /// </summary>
    public void DrawTestPattern()
    {
        Console.WriteLine("[GS] Drawing test pattern into software framebuffer...");

        // Draw a simple gradient / colored rectangle
        for (int y = 100; y < 200; y++)
        {
            for (int x = 100; x < 400; x++)
            {
                int index = y * FB_WIDTH + x;

                // Simple color: Red-ish gradient
                byte r = (byte)((x - 100) * 255 / 300);
                byte g = 0;
                byte b = (byte)((y - 100) * 255 / 100);
                byte a = 255;

                _framebuffer[index] = (uint)((a << 24) | (r << 16) | (g << 8) | b);
            }
        }

        Console.WriteLine("[GS] Test pattern drawn. Framebuffer now contains pixels.");
    }

    /// <summary>
    /// Returns a copy of the current framebuffer (for later display).
    /// </summary>
    public uint[] GetFramebuffer()
    {
        return (uint[])_framebuffer.Clone();
    }

    public int FramebufferWidth => FB_WIDTH;
    public int FramebufferHeight => FB_HEIGHT;

    public void Step(ulong cycles) { }
}
