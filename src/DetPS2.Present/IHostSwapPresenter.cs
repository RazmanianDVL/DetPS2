using System;

namespace DetPS2.Present;

/// <summary>
/// Host swap-chain presenter: displays Soft-GS framebuffer to a native window.
/// Soft-GS remains the determinism source of truth; presenters only display.
/// </summary>
public interface IHostSwapPresenter : IDisposable
{
    /// <summary>Backend kind that was selected / implemented.</summary>
    PresentBackend Backend { get; }

    /// <summary>Human-readable backend name (e.g. "D3D11", "Software").</summary>
    string Name { get; }

    /// <summary>
    /// True when the native device and (if applicable) swap chain are ready.
    /// When false, <see cref="Present"/> is a no-op and must not throw.
    /// </summary>
    bool DeviceReady { get; }

    /// <summary>True after a successful <see cref="AttachWindow"/>.</summary>
    bool WindowAttached { get; }

    /// <summary>Optional display upscale mode (host-side only; never affects Det hash).</summary>
    UpscaleMode UpscaleMode { get; set; }

    /// <summary>Cumulative present / upload stats.</summary>
    PresentStats Stats { get; }

    /// <summary>
    /// Attach (or re-attach) to a native HWND and create the swap chain / output surface.
    /// Returns false if the backend cannot attach (no throw).
    /// </summary>
    /// <param name="hwnd">Win32 HWND (nint). Zero is invalid.</param>
    /// <param name="width">Swap-chain / output width in pixels.</param>
    /// <param name="height">Swap-chain / output height in pixels.</param>
    bool AttachWindow(nint hwnd, int width, int height);

    /// <summary>Resize the output surface. No-op if not attached / not ready.</summary>
    void Resize(int width, int height);

    /// <summary>
    /// Upload Soft-GS packed pixels (<c>0xAARRGGBB</c> / BGRA LE) and present one frame.
    /// Must not throw when <see cref="DeviceReady"/> is false.
    /// </summary>
    void Present(ReadOnlySpan<uint> framebuffer, int width, int height);

    /// <summary>Clear staging state and counters; keep device if still valid.</summary>
    void Reset();
}

/// <summary>Host present backend selection.</summary>
public enum PresentBackend
{
    /// <summary>Try D3D12 → D3D11 → Vulkan → OpenGL → Software (first with DeviceReady).</summary>
    Auto = 0,

    /// <summary>CPU snapshot / blit path (always ready).</summary>
    Software = 1,

    /// <summary>Native Vulkan via Silk.NET (<see cref="VulkanSwapPresenter"/>).</summary>
    Vulkan = 2,

    /// <summary>Direct3D 11 swap chain + staging upload.</summary>
    D3D11 = 3,

    /// <summary>Direct3D 12 swap chain + upload-heap path (<see cref="D3D12SwapPresenter"/>).</summary>
    D3D12 = 4,

    /// <summary>OpenGL via WGL (Windows) (<see cref="OpenGLSwapPresenter"/>).</summary>
    OpenGL = 5,
}

/// <summary>Host-side upscale when output size ≠ Soft-GS FB size.</summary>
public enum UpscaleMode
{
    /// <summary>No upscale; copy/crop to output as-is.</summary>
    None = 0,

    /// <summary>Nearest-neighbor scale to output.</summary>
    Nearest = 1,

    /// <summary>Bilinear scale to output (CPU path; GPU later).</summary>
    Bilinear = 2,
}

/// <summary>Mutable present / upload counters for HUD and tests.</summary>
public sealed class PresentStats
{
    public ulong PresentCount { get; set; }
    public ulong UploadCount { get; set; }
    public ulong BytesUploaded { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public int OutputWidth { get; set; }
    public int OutputHeight { get; set; }
    public bool DeviceReady { get; set; }
    public string BackendName { get; set; } = "";
    public string LastError { get; set; } = "";

    public void ClearCounters()
    {
        PresentCount = 0;
        UploadCount = 0;
        BytesUploaded = 0;
        SourceWidth = SourceHeight = 0;
        // Keep OutputWidth/Height and DeviceReady/BackendName.
    }

    public PresentStats Clone() => new()
    {
        PresentCount = PresentCount,
        UploadCount = UploadCount,
        BytesUploaded = BytesUploaded,
        SourceWidth = SourceWidth,
        SourceHeight = SourceHeight,
        OutputWidth = OutputWidth,
        OutputHeight = OutputHeight,
        DeviceReady = DeviceReady,
        BackendName = BackendName,
        LastError = LastError,
    };
}
