using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DetPS2.Core;
using System;
using System.IO;
using System.Linq;

namespace DetPS2.Desktop;

public partial class MainWindow : Window
{
    private Ps2System? _system;
    private WriteableBitmap? _framebufferBitmap;
    private DispatcherTimer? _renderTimer;
    private bool _isRunning;
    private long _lastCycles;
    private DateTime _lastFpsUpdate = DateTime.UtcNow;

    private ulong _cyclesPerTick = 1_500_000;
    private string _currentSpeedMode = "Normal";

    // Zoom state
    private double _zoomLevel = 1.0;
    private const double ZoomStep = 0.25;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 6.0;

    public MainWindow()
    {
        InitializeComponent();
        InitializeEmulator();
        SetupDragDrop();
    }

    private void InitializeEmulator()
    {
        _system = new Ps2System();

        _framebufferBitmap = new WriteableBitmap(
            new PixelSize(640, 448),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        FramebufferImage.Source = _framebufferBitmap;

        _system.Gs.RenderTestScene();
        UpdateFramebuffer();
        UpdateZoomDisplay();

        Log("DetPS2Sharp initialized — UI fully hooked");
        UpdateStatus("Ready");
        UpdateSidebar();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16.666) };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
    }

    private void Log(string message) { /* ... existing log implementation ... */ }

    private void SetupDragDrop() { /* ... */ }
    private void OnDragEnter(object? sender, DragEventArgs e) { /* ... */ }
    private void OnDragLeave(object? sender, DragEventArgs e) { /* ... */ }
    private async void OnDrop(object? sender, DragEventArgs e) { /* ... existing with Log calls ... */ }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_system == null || _framebufferBitmap == null) return;

        if (_isRunning)
        {
            _system.RunFor(_cyclesPerTick);
        }

        UpdateFramebuffer();
        UpdateStatusText();
        UpdateSidebar();
    }

    private unsafe void UpdateFramebuffer() { /* ... existing ... */ }

    private void UpdateStatusText() { /* ... existing ... */ }

    private void UpdateSidebar() { /* ... existing ... */ }

    private void UpdateStatus(string message) { StatusText.Text = message; }

    private void UpdateZoomDisplay()
    {
        ZoomLevelText.Text = $"{_zoomLevel * 100:F0}%";

        if (FramebufferImage.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = _zoomLevel;
            scale.ScaleY = _zoomLevel;
        }
        else
        {
            FramebufferImage.RenderTransform = new ScaleTransform(_zoomLevel, _zoomLevel);
        }
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e)
    {
        _zoomLevel = Math.Min(_zoomLevel + ZoomStep, MaxZoom);
        UpdateZoomDisplay();
        Log($"Zoomed in to {_zoomLevel * 100:F0}%");
    }

    private void OnZoomOutClick(object? sender, RoutedEventArgs e)
    {
        _zoomLevel = Math.Max(_zoomLevel - ZoomStep, MinZoom);
        UpdateZoomDisplay();
        Log($"Zoomed out to {_zoomLevel * 100:F0}%");
    }

    private void OnResetZoomClick(object? sender, RoutedEventArgs e)
    {
        _zoomLevel = 1.0;
        UpdateZoomDisplay();
        Log("Zoom reset to 100%");
    }

    private void OnSpeedChanged(object? sender, SelectionChangedEventArgs e) { /* ... existing with Log ... */ }

    // All other handlers (LoadBios, LoadElf, Run, Pause, Reset, etc.) remain the same as previous version
    private async void OnLoadBiosClick(object? sender, RoutedEventArgs e) { /* ... */ }
    private async void OnLoadElfClick(object? sender, RoutedEventArgs e) { /* ... */ }
    private void OnRunClick(object? sender, RoutedEventArgs e) { /* ... */ }
    private void OnPauseClick(object? sender, RoutedEventArgs e) { /* ... */ }
    private void OnStepClick(object? sender, RoutedEventArgs e) { /* ... */ }
    private void OnResetClick(object? sender, RoutedEventArgs e) { /* ... */ }
    private void OnTestDrawClick(object? sender, RoutedEventArgs e) { /* ... */ }
    private async void OnSaveStateClick(object? sender, RoutedEventArgs e) { /* ... */ }
    private async void OnLoadStateClick(object? sender, RoutedEventArgs e) { /* ... */ }
    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
    private async void OnAboutClick(object? sender, RoutedEventArgs e) { /* ... existing About dialog ... */ }
    private void OnResetFramebufferViewClick(object? sender, RoutedEventArgs e) { /* ... */ }

    protected override void OnClosed(EventArgs e)
    {
        _renderTimer?.Stop();
        base.OnClosed(e);
    }
}