using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer (GS) - Minimal software renderer skeleton.
/// This is the beginning of getting pixels on screen.
/// </summary>
public sealed class Gs
{
    public SystemMemory Memory { get; }

    public Gs(SystemMemory memory)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public void Reset() { }

    /// <summary>
    /// Called by GIF when it has data to send to the GS.
    /// For now this is just a stub.
    /// </summary>
    public void ReceiveGifData(uint address, uint qwc)
    {
        Console.WriteLine($"[GS] Received {qwc} quadwords from GIF at 0x{address:X8}");

        // TODO: Parse actual GS commands and draw pixels
        // For now we just pretend we drew something
        Console.WriteLine("[GS] (Stub) Drawing to framebuffer...");
    }

    public void Step(ulong cycles) { }

    /// <summary>
    /// Very crude way to "draw" something for early testing.
    /// We can improve this later with actual primitive rasterization.
    /// </summary>
    public void DrawTestPattern()
    {
        Console.WriteLine("[GS] Drawing test pattern (colored rectangle simulation)");
        // In a real implementation we would write to GS VRAM / framebuffer here
    }
}
