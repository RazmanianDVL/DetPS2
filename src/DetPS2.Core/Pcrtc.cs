using System;

namespace DetPS2.Core;

/// <summary>
/// PCRTC (CRT Controller) - Minimal implementation for Phase 2.
/// Handles final display output. Currently saves framebuffer as PPM.
/// In later phases this can drive a real window.
/// </summary>
public sealed class Pcrtc
{
    private readonly Gs _gs;

    public Pcrtc(Gs gs)
    {
        _gs = gs ?? throw new ArgumentNullException(nameof(gs));
    }

    public void Reset() { }

    /// <summary>
    /// Present the current framebuffer (Phase 2: save as PPM).
    /// </summary>
    public void Present(string filename = "detps2_frame.ppm")
    {
        _gs.SaveFramebufferAsPPM(filename);
        Console.WriteLine($"[PCRTC] Frame presented to {filename}");
    }

    public void Step(ulong cycles) { }
}
