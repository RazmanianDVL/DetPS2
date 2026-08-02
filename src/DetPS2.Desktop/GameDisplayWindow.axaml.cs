using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DetPS2.Core;
using DetPS2.Present;
using System;
using System.IO;

namespace DetPS2.Desktop;

/// <summary>Separate window for the emulated framebuffer (game screen).</summary>
public partial class GameDisplayWindow : Window
{
    private WriteableBitmap? _bitmap;
    private IHostSwapPresenter? _hostPresent;
    /// <summary>Default Software: Avalonia WriteableBitmap is the reliable Soft-GS display path.</summary>
    private string _presentMode = "Software";
    private bool _attachAttempted;
    private int _lastClientW;
    private int _lastClientH;
    /// <summary>True only while host GPU exclusive present is proven (PresentCount advancing).</summary>
    private bool _gpuExclusiveActive;
    /// <summary>Non-zero RGB present pixels from last Avalonia blit (HUD proof of non-black).</summary>
    private int _lastLitPixels;
    private int _presentFrameIndex;
    private int _lastDumpPresentIndex = -999;
    private static readonly bool DumpPresentEnv =
        string.Equals(Environment.GetEnvironmentVariable("DETPS2_DUMP_PRESENT"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Optional vertical flip for Soft-GS → Avalonia. Default from <c>DETPS2_VFLIP</c>;
    /// set via <see cref="SetFlipY"/> (Options / diagnostics when image is inverted).
    /// Soft-GS and Avalonia are both top-left origin — flip only when a title/composite is upside-down.
    /// </summary>
    private bool _flipY = SoftGsAvaloniaBlit.EnvFlipY;

    public event Action? ClosedByUser;
    public event Action<Key, bool>? KeyEvent; // key, isDown

    /// <summary>Active display path name for HUD (Avalonia when exclusive GPU not proven).</summary>
    public string HostPresentName =>
        _gpuExclusiveActive && _hostPresent is { DeviceReady: true } hp
            ? hp.Name
            : (_hostPresent is { DeviceReady: true } ready && ready.Backend != PresentBackend.Software
                ? $"{ready.Name}+Avalonia"
                : "Software(Avalonia)");

    /// <summary>Last Soft-GS present frame lit (non-zero RGB) pixel count.</summary>
    public int LastLitPixels => _lastLitPixels;

    /// <summary>Whether Avalonia blit vertically flips Soft-GS rows.</summary>
    public bool FlipY
    {
        get => _flipY;
        set => _flipY = value;
    }

    public GameDisplayWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            KeyEvent?.Invoke(e.Key, true);
            e.Handled = true;
        };
        KeyUp += (_, e) =>
        {
            KeyEvent?.Invoke(e.Key, false);
            e.Handled = true;
        };
        Opened += (_, _) => TryAttachHostPresent(force: true);
        ClientSizeProperty.Changed.AddClassHandler<GameDisplayWindow>((w, _) => w.OnClientSizeChanged());
        Closing += (_, _) =>
        {
            DisposeHostPresent();
            ClosedByUser?.Invoke();
        };
    }

    /// <summary>
    /// Configure host present backend from Options (Auto / Software / D3D11 / D3D12 / Vulkan / GPU).
    /// Soft-GS remains the pixel source; this only selects the display path.
    /// </summary>
    public void SetPresentMode(string? mode)
    {
        string m = string.IsNullOrWhiteSpace(mode) ? "Software" : mode.Trim();
        if (string.Equals(m, _presentMode, StringComparison.OrdinalIgnoreCase) &&
            (_hostPresent != null || string.Equals(m, "Software", StringComparison.OrdinalIgnoreCase)))
            return;
        _presentMode = m;
        DisposeHostPresent();
        _attachAttempted = false;
        TryAttachHostPresent(force: true);
    }

    /// <summary>Toggle Soft-GS V-flip (for inverted DISPFB / diagnostic with Deception PPM).</summary>
    public void SetFlipY(bool flipY) => _flipY = flipY;

    public void SetTitleInfo(string title) =>
        Title = string.IsNullOrWhiteSpace(title) ? "DetPS2 — Game" : "DetPS2 — " + title;

    public void SetStatus(string text)
    {
        if (StatusLabel != null)
            StatusLabel.Text = text;
    }

    public void SetNoVideoHint(string? detail)
    {
        if (NoVideoOverlay == null) return;
        bool show = !string.IsNullOrEmpty(detail);
        NoVideoOverlay.IsVisible = show;
        if (OverlayDetail != null && detail != null)
            OverlayDetail.Text = detail;
    }

    /// <summary>
    /// UI-thread present from EE worker Soft-GS snapshot (never call into a mid-RunFor system).
    /// Always blits Avalonia — never takes GPU-exclusive HWND ownership from a worker snapshot
    /// (exclusive requires proven live PresentCount advance on the UI-thread PresentFrame path).
    /// </summary>
    public void PresentSnapshot(ReadOnlySpan<uint> fb, int w, int h, long pxWritten, ulong cycles, ulong pc,
        int gifP3, long litHint = -1)
    {
        if (w <= 0 || h <= 0 || fb.Length < w * h) return;

        // Snapshot path owns Avalonia only — clear any stale exclusive flag from prior PresentFrame.
        _gpuExclusiveActive = false;

        bool hasPixels = pxWritten > 0 || litHint > 0;
        if (NoVideoOverlay != null)
            NoVideoOverlay.IsVisible = !hasPixels;
        if (NoVideoOverlay is { IsVisible: true } && OverlayDetail != null)
        {
            OverlayDetail.Text =
                $"PC=0x{pc:X8}  cycles={cycles:N0}\n" +
                $"GS px={pxWritten:N0}  lit={(litHint >= 0 ? litHint.ToString("N0") : "?")}  gifP3={gifP3}\n" +
                $"host-present={HostPresentName}  vflip={(_flipY ? "on" : "off")}\n" +
                "EE on background thread — UI stays responsive\n" +
                "Details: %TEMP%\\DetPS2\\session-*.log";
        }

        // Optional host GPU upload for dual-present stats, but Avalonia stays visible (never exclusive).
        TryHostPresentNonExclusive(fb, w, h);

        BlitSoftGsToAvalonia(fb, w, h, computeLit: litHint < 0);
        if (litHint >= 0)
            _lastLitPixels = (int)Math.Min(int.MaxValue, litHint);
        MaybeDumpPresentPpm(fb, w, h);
    }

    public unsafe void PresentFrame(Ps2System system)
    {
        if (system == null) return;

        var gs = system.Gs;
        var fb = gs.GetPresentSpan();
        int w = gs.FramebufferWidth;
        int h = gs.FramebufferHeight;
        if (w <= 0 || h <= 0 || fb.Length < w * h) return;

        // When Soft-GS claims px>0 but the present span is still mostly black and local
        // IMAGE exists, force one DISPFB composite (merge cache may have skipped logos).
        // GetPresentSpan already tried Composite; ForceRefresh zeros DispfbPixelsComposited.
        int sampledLit = CountLitSampled(fb, step: 16);
        int sampleSlots = (fb.Length + 15) / 16;
        bool mostlyBlack = sampledLit * 100 < sampleSlots; // <1% of samples lit
        bool hasLocalImage = gs.ImageBytesWritten > 0;
        if (gs.PixelsWritten > 0 && mostlyBlack && hasLocalImage)
        {
            long forced = gs.ForceRefreshPresentComposite();
            if (forced > 0)
            {
                fb = gs.GetPresentSpan();
                if (fb.Length < w * h) return;
                sampledLit = CountLitSampled(fb, step: 16);
            }
        }

        // Soft-GS counters are truth: any pixels written means video exists (may be mostly
        // black early). Never show "No video" when PixelsWritten > 0 / host overlay is on.
        bool hasPixels = system.Gs.PixelsWritten > 0 || system.Gs.HostOverlayActive;
        if (NoVideoOverlay != null)
            NoVideoOverlay.IsVisible = !hasPixels;
        if (NoVideoOverlay is { IsVisible: true } && OverlayDetail != null)
        {
            OverlayDetail.Text =
                $"PC=0x{system.EE.PC:X8}  cycles={system.MasterCycles:N0}\n" +
                $"GS px={system.Gs.PixelsWritten:N0}  lit={_lastLitPixels:N0}  gifP3={system.Gif.Path3Transfers}  " +
                $"overlay={(system.Gs.HostOverlayActive ? "on" : "off")}\n" +
                $"host-present={HostPresentName}  vflip={(_flipY ? "on" : "off")}\n" +
                $"boot-assist: {system.MidwayAssist.Status}  " +
                $"fmv={system.MidwayAssist.LogoFrame}/{system.MidwayAssist.LogoFramesTotal}\n" +
                "Details: %TEMP%\\DetPS2\\session-*.log";
        }

        TryAttachHostPresent(force: false);

        // Soft-GS → Avalonia WriteableBitmap is the reliable display path.
        // Host GPU Present is optional: only take exclusive HWND ownership when Present
        // actually advances (DeviceReady alone is not enough — silent no-ops / wrong
        // surface / SoftwareSwapPresenter staging used to hide Avalonia and show black).
        // NEVER hide Avalonia unless GPU exclusive is proven this frame.
        bool gpuExclusiveOk = false;
        if (_hostPresent is { DeviceReady: true } hp &&
            hp.Backend != PresentBackend.Software)
        {
            try
            {
                ulong before = hp.Stats.PresentCount;
                string errBefore = hp.Stats.LastError ?? "";
                hp.Present(fb, w, h);
                bool advanced = hp.Stats.PresentCount > before;
                bool newErr = !string.IsNullOrEmpty(hp.Stats.LastError) &&
                              !string.Equals(hp.Stats.LastError, errBefore, StringComparison.Ordinal);
                // Exclusive GPU only when Soft-GS has pixels and present advanced without error.
                gpuExclusiveOk = hasPixels && advanced && !newErr && hp.DeviceReady;
            }
            catch
            {
                gpuExclusiveOk = false;
            }
        }

        _gpuExclusiveActive = gpuExclusiveOk;
        if (gpuExclusiveOk)
        {
            // Still count lit for HUD / dump even when GPU owns the HWND.
            _lastLitPixels = CountLitFull(fb);
            MaybeDumpPresentPpm(fb, w, h);
            if (GameImage != null)
                GameImage.IsVisible = false;
            return;
        }

        // Avalonia blit: always visible when GPU exclusive is not proven.
        BlitSoftGsToAvalonia(fb, w, h);
        MaybeDumpPresentPpm(fb, w, h);
    }

    /// <summary>
    /// Soft-GS → Avalonia <see cref="PixelFormat.Bgra8888"/> blit.
    /// <para>
    /// Soft-GS pack is <c>0xAARRGGBB</c> (LE bytes B,G,R,A) — same memory layout as Avalonia
    /// Bgra8888 / DXGI B8G8R8A8. Force A=0xFF so Opaque bitmaps never treat RGB as transparent.
    /// Optional <see cref="_flipY"/> when the composite is inverted (see Deception PPM diagnostics).
    /// </para>
    /// </summary>
    private unsafe void BlitSoftGsToAvalonia(ReadOnlySpan<uint> fb, int w, int h, bool computeLit = true)
    {
        if (GameImage == null) return;
        // Always show Avalonia surface unless PresentFrame proved GPU exclusive this frame.
        GameImage.IsVisible = true;

        bool newBitmap = _bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h;
        if (newBitmap)
        {
            _bitmap = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
        }

        using (var locked = _bitmap!.Lock())
        {
            int rowBytes = locked.RowBytes;
            byte* dstBase = (byte*)locked.Address;
            // Caller (PresentSnapshot) may already know the lit count from the worker-thread
            // hint — skip the redundant full-framebuffer scan when it does (the result was
            // being computed here then immediately discarded in favor of the hint).
            int lit = SoftGsAvaloniaBlit.PackToBgraStrided(
                fb, w, h, dstBase, rowBytes, flipY: _flipY, computeLit: computeLit);
            if (computeLit)
                _lastLitPixels = lit;
        }

        // Rebind Source every frame so Avalonia always refreshes after memcpy-only updates
        // (InvalidateVisual alone has been insufficient on some Win32 present paths).
        GameImage.Source = null;
        GameImage.Source = _bitmap;
        GameImage.InvalidateVisual();
        // (Removed: whole-window InvalidateVisual() — redundant with the image-level
        // invalidate just above; invalidating the entire Window forced an extra
        // layout/render pass every frame at 60Hz for no additional visual effect.)
    }

    /// <summary>
    /// Feed host GPU presenter without hiding Avalonia (snapshot / dual-present path).
    /// Failures are ignored — Avalonia remains the visible Soft-GS path.
    /// </summary>
    private void TryHostPresentNonExclusive(ReadOnlySpan<uint> fb, int w, int h)
    {
        if (_hostPresent is not { DeviceReady: true } hp) return;
        if (hp.Backend == PresentBackend.Software) return;
        try { hp.Present(fb, w, h); }
        catch { /* Avalonia is truth for this path */ }
    }

    private static int CountLitSampled(ReadOnlySpan<uint> fb, int step)
    {
        if (step < 1) step = 1;
        int lit = 0;
        for (int i = 0; i < fb.Length; i += step)
        {
            if ((fb[i] & 0x00FFFFFFu) != 0)
                lit++;
        }
        return lit;
    }

    private static int CountLitFull(ReadOnlySpan<uint> fb)
    {
        int lit = 0;
        for (int i = 0; i < fb.Length; i++)
        {
            if ((fb[i] & 0x00FFFFFFu) != 0)
                lit++;
        }
        return lit;
    }

    /// <summary>
    /// When DETPS2_DUMP_PRESENT=1, overwrite %TEMP%\DetPS2\last-present.ppm (~2 Hz).
    /// PPM is always Soft-GS orientation (no V-flip) so dumps compare to CLI Soft-GS truth.
    /// </summary>
    private void MaybeDumpPresentPpm(ReadOnlySpan<uint> fb, int w, int h)
    {
        if (!DumpPresentEnv) return;
        _presentFrameIndex++;
        // Throttle: dump about every 30 presents so play stays interactive.
        if (_presentFrameIndex - _lastDumpPresentIndex < 30) return;
        _lastDumpPresentIndex = _presentFrameIndex;
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "DetPS2");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "last-present.ppm");
            // Binary P6 is far smaller/faster than ASCII P3 for 640×448.
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var bw = new BinaryWriter(fs);
            var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
            bw.Write(header);
            for (int i = 0; i < w * h && i < fb.Length; i++)
            {
                uint p = fb[i];
                // Soft-GS 0xAARRGGBB → PPM R,G,B
                bw.Write((byte)((p >> 16) & 0xFF)); // R
                bw.Write((byte)((p >> 8) & 0xFF));  // G
                bw.Write((byte)(p & 0xFF));         // B
            }
        }
        catch
        {
            // Diagnostic only — never fail present.
        }
    }

    private void OnClientSizeChanged()
    {
        int w = Math.Max(1, (int)ClientSize.Width);
        int h = Math.Max(1, (int)Math.Max(1, ClientSize.Height - 28)); // status bar ~28
        if (w == _lastClientW && h == _lastClientH) return;
        _lastClientW = w;
        _lastClientH = h;
        try { _hostPresent?.Resize(w, h); }
        catch { /* ignore */ }
    }

    private void TryAttachHostPresent(bool force)
    {
        if (!force && _attachAttempted) return;
        if (_hostPresent is { DeviceReady: true }) return;

        // Software mode: skip native GPU (Avalonia path only).
        if (string.Equals(_presentMode, "Software", StringComparison.OrdinalIgnoreCase))
        {
            DisposeHostPresent();
            _attachAttempted = true;
            return;
        }

        nint hwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (hwnd == 0) return;

        int w = Math.Max(1, (int)ClientSize.Width);
        int h = Math.Max(1, (int)Math.Max(1, ClientSize.Height - 28));
        _lastClientW = w;
        _lastClientH = h;
        _attachAttempted = true;

        PresentBackend backend = ParseBackend(_presentMode);
        try
        {
            DisposeHostPresent();
            _hostPresent = PresentBackendFactory.CreateAndAttach(hwnd, w, h, backend);
        }
        catch (Exception ex)
        {
            DisposeHostPresent();
            // Keep Avalonia path; surface error in status when possible.
            if (StatusLabel != null)
                StatusLabel.Text = "Host present failed: " + ex.Message;
        }
    }

    private static PresentBackend ParseBackend(string mode)
    {
        if (string.Equals(mode, "D3D11", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "GPU", StringComparison.OrdinalIgnoreCase))
            return PresentBackend.D3D11;
        if (string.Equals(mode, "D3D12", StringComparison.OrdinalIgnoreCase))
            return PresentBackend.D3D12;
        if (string.Equals(mode, "Vulkan", StringComparison.OrdinalIgnoreCase))
            return PresentBackend.Vulkan;
        if (string.Equals(mode, "OpenGL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "GL", StringComparison.OrdinalIgnoreCase))
            return PresentBackend.OpenGL;
        if (string.Equals(mode, "Software", StringComparison.OrdinalIgnoreCase))
            return PresentBackend.Software;
        return PresentBackend.Auto;
    }

    private void DisposeHostPresent()
    {
        try { _hostPresent?.Dispose(); }
        catch { /* ignore */ }
        _hostPresent = null;
        _gpuExclusiveActive = false;
    }
}
