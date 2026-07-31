using System;
using System.IO;

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

    /// <summary>GX-040: privileged DISPFB/DISPLAY/PMODE circuit snapshot.</summary>
    public GsDisplayCircuitInfo GetDisplayCircuitInfo() => _gs.GetDisplayCircuitInfo();

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
        // GX-041: ensure DISPFB→FB (natural or residual FRAME) before PCRTC present/dump.
        _gs.CompositeDispfbToFramebuffer();
        if (ppmPath != null)
            _pcrtc.Present(ppmPath);
        else
            _pcrtc.PresentFrame();
        FramesPresented++;
    }

    /// <summary>GX-041 present helper: composite then optional Soft-GS PPM when px&gt;0.</summary>
    public long PresentDispfbCircuit(string? ppmPath = null)
    {
        long written = _gs.CompositeDispfbToFramebuffer();
        if (ppmPath != null)
            DumpSoftGsIfDrawn(ppmPath);
        else
            _pcrtc.PresentFrame();
        FramesPresented++;
        return written;
    }

    /// <summary>
    /// GX-002: dump Soft-GS framebuffer as PPM when any pixels have been written.
    /// Returns true if a file was written; false if px==0 (no dump / black-only skip).
    /// Host present is never the truth source — this always uses software GS.
    /// </summary>
    public bool DumpSoftGsIfDrawn(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (_gs.PixelsWritten <= 0) return false;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _gs.SaveFramebufferAsPPM(path);
        return true;
    }

    /// <summary>Always dump Soft-GS PPM (even if black) — diagnostics only.</summary>
    public void DumpSoftGs(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _gs.SaveFramebufferAsPPM(path);
    }
}
