using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Synthesizer (GS) - Basic command handling.
/// Still early, but starting to understand GS commands.
/// </summary>
public sealed class Gs
{
    public SystemMemory Memory { get; }

    // Very basic internal state
    private uint _frameBufferBase;
    private uint _zBufferBase;
    private uint _texBase;

    public Gs(SystemMemory memory)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public void Reset()
    {
        _frameBufferBase = 0;
        _zBufferBase = 0;
        _texBase = 0;
    }

    /// <summary>
    /// Receives a list of GS commands from the GIF.
    /// This is where we will eventually interpret actual drawing commands.
    /// </summary>
    public void ReceiveCommandList(uint address, uint qwc)
    {
        Console.WriteLine($"[GS] Received command list: {qwc} quadwords");

        // For now, just simulate processing some commands
        // In a real implementation we would loop through the data and execute GS register writes + primitives
        for (uint i = 0; i < Math.Min(qwc, 4); i++)
        {
            uint cmdAddr = address + (i * 16);
            uint reg = Memory?.Read32(cmdAddr) ?? 0;
            uint data = Memory?.Read32(cmdAddr + 4) ?? 0;

            // Very crude register detection (real GS has many registers)
            if (reg == 0x00) // Example: FRAME_1 or similar
                Console.WriteLine($"[GS] Possible FRAME register write: 0x{data:X8}");
            else if (reg == 0x01)
                Console.WriteLine($"[GS] Possible ZBUF write");
        }

        // Temporary: draw something
        DrawTestPattern();
    }

    public void DrawTestPattern()
    {
        Console.WriteLine("[GS] >>> Drawing test pattern to framebuffer (simulated)");
        // Future: Write actual pixel data into a software framebuffer here
    }

    public void Step(ulong cycles) { }
}
