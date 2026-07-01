using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer with software framebuffer and image export.
/// </summary>
public sealed class Gs
{
    public SystemMemory Memory { get; }

    private const int FB_WIDTH = 640;
    private const int FB_HEIGHT = 448;
    private readonly uint[] _framebuffer = new uint[FB_WIDTH * FB_HEIGHT];

    public Gs(SystemMemory memory)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public void Reset()
    {
        Array.Clear(_framebuffer, 0, _framebuffer.Length);
    }

    public void ReceiveCommandList(uint address, uint qwc)
    {
        DrawTestPattern();
    }

    public void DrawTestPattern()
    {
        Console.WriteLine("[GS] Drawing improved test pattern...");

        // Draw a nice gradient background
        for (int y = 0; y < FB_HEIGHT; y++)
        {
            for (int x = 0; x < FB_WIDTH; x++)
            {
                int index = y * FB_WIDTH + x;

                byte r = (byte)(x * 255 / FB_WIDTH);
                byte g = (byte)(y * 255 / FB_HEIGHT);
                byte b = (byte)(((x + y) / 2) * 255 / ((FB_WIDTH + FB_HEIGHT) / 2));

                _framebuffer[index] = (uint)(0xFF000000 | (r << 16) | (g << 8) | b);
            }
        }

        // Draw a bright rectangle in the middle
        for (int y = 150; y < 300; y++)
        {
            for (int x = 200; x < 440; x++)
            {
                int index = y * FB_WIDTH + x;
                _framebuffer[index] = 0xFFFF00FF; // Magenta
            }
        }

        Console.WriteLine("[GS] Test pattern complete. Framebuffer ready.");
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
