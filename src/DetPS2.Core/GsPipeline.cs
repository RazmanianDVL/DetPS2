using System;

namespace DetPS2.Core;

/// <summary>
/// GS Pipeline - Foundational structure for the Graphics Synthesizer rendering pipeline.
/// 
/// This class provides the high-level architecture for how graphics data flows:
/// GIF → GS Registers → Primitive Assembly → Rasterization → Framebuffer
/// 
/// Current state: Foundation only.
/// Heavy implementation (real primitive assembly, rasterization logic, texture sampling, etc.)
/// should be built on top of this structure.
/// </summary>
public sealed class GsPipeline
{
    private readonly Gs _gs;
    private readonly GsRegisters _registers;

    public GsPipeline(Gs gs, GsRegisters registers)
    {
        _gs = gs ?? throw new ArgumentNullException(nameof(gs));
        _registers = registers ?? throw new ArgumentNullException(nameof(registers));
    }

    public void Reset() { }

    /// <summary>
    /// Main entry point when GIF sends a command list to the GS.
    /// This is where register writes and primitive data would be processed.
    /// </summary>
    public void ProcessCommandList(uint address, uint qwc)
    {
        // Foundation hook - actual processing logic goes here in future implementations
        _gs.ReceiveCommandList(address, qwc);
    }

    /// <summary>
    /// Called when a new primitive type is set.
    /// </summary>
    public void OnPrimChanged(uint prim)
    {
        // Foundation for tracking current primitive state
    }

    public void Step(ulong cycles) { }
}
