using System;

namespace DetPS2.Present;

/// <summary>
/// CPU host presenter: copies Soft-GS FB into a snapshot buffer (same role as Core
/// <c>SoftwareFramePresenter</c>). Always <see cref="DeviceReady"/>; HWND attach is
/// optional bookkeeping only (no native swap chain).
/// Soft-GS remains truth — this only stages pixels for UI / tests.
/// </summary>
public sealed class SoftwareSwapPresenter : IHostSwapPresenter
{
    private nint _hwnd;
    private readonly object _lock = new();

    public PresentBackend Backend => PresentBackend.Software;
    public string Name => "Software";
    public bool DeviceReady => true;
    public bool WindowAttached => _hwnd != 0;
    public UpscaleMode UpscaleMode { get; set; } = UpscaleMode.None;
    public PresentStats Stats { get; } = new() { BackendName = "Software", DeviceReady = true };

    /// <summary>Last CPU snapshot (0xAARRGGBB). Null until first present.</summary>
    public uint[]? LastFrame { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    public bool AttachWindow(nint hwnd, int width, int height)
    {
        if (hwnd == 0 || width <= 0 || height <= 0)
        {
            Stats.LastError = "Software: invalid hwnd/size";
            return false;
        }

        lock (_lock)
        {
            _hwnd = hwnd;
            Stats.OutputWidth = width;
            Stats.OutputHeight = height;
            Stats.DeviceReady = true;
            Stats.LastError = "";
            return true;
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        lock (_lock)
        {
            Stats.OutputWidth = width;
            Stats.OutputHeight = height;
        }
    }

    public void Present(ReadOnlySpan<uint> framebuffer, int width, int height)
    {
        if (width <= 0 || height <= 0 || framebuffer.Length < width * height)
            return;

        lock (_lock)
        {
            Width = width;
            Height = height;
            int n = width * height;
            if (LastFrame == null || LastFrame.Length != n)
                LastFrame = new uint[n];
            framebuffer.Slice(0, n).CopyTo(LastFrame);

            Stats.SourceWidth = width;
            Stats.SourceHeight = height;
            Stats.BytesUploaded += (ulong)n * 4;
            Stats.UploadCount++;
            Stats.PresentCount++;
            Stats.DeviceReady = true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            LastFrame = null;
            Width = Height = 0;
            Stats.ClearCounters();
            Stats.DeviceReady = true;
            Stats.LastError = "";
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            LastFrame = null;
            _hwnd = 0;
        }
        GC.SuppressFinalize(this);
    }
}
