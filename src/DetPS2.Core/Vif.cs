using System;

namespace DetPS2.Core;

/// <summary>
/// Vector Interface (VIF) - Minimal Phase 2 implementation focused on PATH3.
/// VIF1 can feed the GIF directly (PATH3).
/// This is a stub that recognizes PATH3 transfers.
/// </summary>
public sealed class Vif
{
    private readonly Gs _gs;
    private readonly Gif _gif;

    public Vif(Gs gs, Gif gif)
    {
        _gs = gs ?? throw new ArgumentNullException(nameof(gs));
        _gif = gif ?? throw new ArgumentNullException(nameof(gif));
    }

    public void Reset() { }

    /// <summary>
    /// Handle a VIF1 PATH3 transfer (common way games send data to GIF).
    /// For Phase 2 we simply forward to GIF.
    /// </summary>
    public void ProcessPath3(uint address, uint qwc)
    {
        Console.WriteLine($"[VIF] PATH3 transfer received ({qwc} quadwords)");
        _gif.ReceivePath3Data(address, qwc);
    }

    public void Step(ulong cycles) { }
}
