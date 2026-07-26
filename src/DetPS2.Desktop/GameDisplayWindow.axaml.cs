using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DetPS2.Core;
using System;

namespace DetPS2.Desktop;

/// <summary>Separate window for the emulated framebuffer (game screen).</summary>
public partial class GameDisplayWindow : Window
{
    private WriteableBitmap? _bitmap;
    public event Action? ClosedByUser;
    public event Action<Key, bool>? KeyEvent; // key, isDown

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
        Closing += (_, _) => ClosedByUser?.Invoke();
    }

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

    public unsafe void PresentFrame(Ps2System system)
    {
        if (system == null || GameImage == null) return;

        // FMV pacing is owned by MainWindow (OnHostPresent once per UI tick).
        // This method only blits GetPresentSpan → WriteableBitmap.

        var fb = system.Gs.GetPresentSpan();
        int w = system.Gs.FramebufferWidth;
        int h = system.Gs.FramebufferHeight;
        if (w <= 0 || h <= 0 || fb.Length < w * h) return;

        bool hasPixels = system.Gs.PixelsWritten > 0 || system.Gs.HostOverlayActive;
        bool mostlyBlack = !system.Gs.HostOverlayActive && IsMostlyBlack(fb, w * h);
        if (NoVideoOverlay != null)
            NoVideoOverlay.IsVisible = !hasPixels || (mostlyBlack && system.MasterCycles < 30_000_000
                && !system.MidwayAssist.LogoActive && system.MidwayAssist.LogoFramesTotal == 0);
        if (NoVideoOverlay is { IsVisible: true } && OverlayDetail != null)
        {
            OverlayDetail.Text =
                $"PC=0x{system.EE.PC:X8}  cycles={system.MasterCycles:N0}\n" +
                $"GS px={system.Gs.PixelsWritten:N0}  gifP3={system.Gif.Path3Transfers}  " +
                $"overlay={(system.Gs.HostOverlayActive ? "on" : "off")}\n" +
                $"boot-assist: {system.MidwayAssist.Status}  " +
                $"fmv={system.MidwayAssist.LogoFrame}/{system.MidwayAssist.LogoFramesTotal}\n" +
                "Details: %TEMP%\\DetPS2\\session-*.log";
        }

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
            // Avalonia Bgra8888 LE = B,G,R,A bytes → uint 0xAARRGGBB (same as our GS pack).
            // RowBytes stride required or the image shears.
            int rowBytes = locked.RowBytes;
            byte* basePtr = (byte*)locked.Address;
            for (int y = 0; y < h; y++)
            {
                uint* destRow = (uint*)(basePtr + y * rowBytes);
                int srcRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    uint src = fb[srcRow + x];
                    byte r = (byte)((src >> 16) & 0xFF);
                    byte g = (byte)((src >> 8) & 0xFF);
                    byte b = (byte)(src & 0xFF);
                    destRow[x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
                }
            }
        }

        // Re-bind Source every frame so Avalonia composition cannot stick on the first upload.
        // (Some hosts cache Image content if Source reference never changes.)
        if (!ReferenceEquals(GameImage.Source, _bitmap) || newBitmap)
            GameImage.Source = _bitmap;
        else
        {
            GameImage.Source = null;
            GameImage.Source = _bitmap;
        }
        GameImage.InvalidateVisual();
        InvalidateVisual();
    }

    private static bool IsMostlyBlack(ReadOnlySpan<uint> fb, int n)
    {
        int lit = 0;
        int step = Math.Max(1, n / 2000);
        int samples = 0;
        for (int i = 0; i < n; i += step)
        {
            uint p = fb[i];
            int r = (int)((p >> 16) & 0xFF);
            int g = (int)((p >> 8) & 0xFF);
            int b = (int)(p & 0xFF);
            if (r > 18 || g > 18 || b > 18) lit++;
            samples++;
        }
        return lit < Math.Max(2, samples / 40);
    }
}
