using System;

namespace DetPS2.Core;

/// <summary>
/// GS Pipeline orchestrator (Phase 7).
/// GIF paths → GS → PCRTC present.
/// </summary>
public sealed class GsPipeline
{
    private readonly Gs _gs;
    private readonly Gif _gif;
    private readonly Pcrtc _pcrtc;

    public Gs Gs => _gs;
    public Gif Gif => _gif;
    public Pcrtc Pcrtc => _pcrtc;

    public long FramesPresented { get; private set; }

    public GsPipeline(Gs gs, Gif gif, Pcrtc pcrtc)
    {
        _gs = gs ?? throw new ArgumentNullException(nameof(gs));
        _gif = gif ?? throw new ArgumentNullException(nameof(gif));
        _pcrtc = pcrtc ?? throw new ArgumentNullException(nameof(pcrtc));
    }

    public void Reset() => FramesPresented = 0;

    public void ProcessPath3(uint address, uint qwc) => _gif.ReceivePath3Data(address, qwc);
    public void ProcessPath2(uint address, uint qwc) => _gif.ReceivePath2Data(address, qwc);
    public void ProcessPath1(uint address, uint qwc) => _gif.ReceivePath1Data(address, qwc);

    /// <summary>Screen-space triangle via GS (no GIF).</summary>
    public void DrawImmediateTriangle(int x0, int y0, int x1, int y1, int x2, int y2, uint color)
    {
        _gs.WriteGsRegister(0x00, 0x03);
        _gs.DrawScreenTriangle(x0, y0, x1, y1, x2, y2, color);
    }

    public void Present(string? ppmPath = null)
    {
        if (ppmPath != null)
            _pcrtc.Present(ppmPath);
        else
            _pcrtc.PresentFrame();
        FramesPresented++;
    }
}
