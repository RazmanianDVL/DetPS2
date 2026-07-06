using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DetPS2.Core;
using System;
using System.IO;

namespace DetPS2.Desktop;

public partial class MainWindow : Window
{
    private Ps2System? _system;
    private WriteableBitmap? _framebufferBitmap;
    private DispatcherTimer? _renderTimer;
    private bool _isRunning;
    private long _lastCycles;
    private DateTime _lastFpsUpdate = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();
        InitializeEmulator();
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

        // Hook: Immediately show a nice colorful scene so the UI never feels empty or stub-like
        _system.Gs.RenderTestScene();
        UpdateFramebuffer();

        UpdateStatus("DetPS2Sharp ready. Use the toolbar to load BIOS/ELF or draw test scenes.");

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16.666) };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_system == null || _framebufferBitmap == null) return;

        if (_isRunning)
        {
            _system.RunFor(1_500_000);
        }

        UpdateFramebuffer();
        UpdateStatusText();
    }

    private unsafe void UpdateFramebuffer()
    {
        if (_system == null || _framebufferBitmap == null) return;

        var framebuffer = _system.Gs.GetFramebuffer();
        int width = _system.Gs.FramebufferWidth;
        int height = _system.Gs.FramebufferHeight;

        using var locked = _framebufferBitmap.Lock();
        uint* dest = (uint*)locked.Address;

        int pixelsToCopy = Math.Min(framebuffer.Length, width * height);
        for (int i = 0; i < pixelsToCopy; i++)
        {
            uint src = framebuffer[i];
            byte r = (byte)((src >> 16) & 0xFF);
            byte g = (byte)((src >> 8) & 0xFF);
            byte b = (byte)(src & 0xFF);

            dest[i] = (uint)((0xFFu << 24) | ((uint)b << 16) | ((uint)g << 8) | r);
        }
    }

    private void UpdateStatusText()
    {
        if (_system == null) return;

        CyclesText.Text = $"Master Cycles: {_system.MasterCycles:N0}";

        var now = DateTime.UtcNow;
        if ((now - _lastFpsUpdate).TotalSeconds >= 1.0)
        {
            long current = (long)_system.MasterCycles;
            long delta = current - _lastCycles;
            double fps = delta / 1_500_000.0;
            FpsText.Text = $"~{fps:F1} updates/sec";
            _lastCycles = current;
            _lastFpsUpdate = now;
        }

        StatusText.Text = _isRunning ? "Running" : "Ready";
    }

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    private async void OnLoadBiosClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;

        var files = await this.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Select PS2 BIOS",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("BIOS") { Patterns = new[] { "*.bin", "*.rom" } } }
        });

        if (files.Count > 0)
        {
            try
            {
                _system.LoadBios(files[0].Path.LocalPath);
                UpdateStatus($"BIOS loaded: {Path.GetFileName(files[0].Path.LocalPath)}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading BIOS: {ex.Message}");
            }
        }
    }

    private async void OnLoadElfClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;

        var files = await this.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Select PS2 ELF Homebrew",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("ELF") { Patterns = new[] { "*.elf", "*.ELF" } } }
        });

        if (files.Count > 0)
        {
            try
            {
                byte[] elfData = await File.ReadAllBytesAsync(files[0].Path.LocalPath);
                ulong entry = ElfLoader.LoadElf(elfData, _system.Memory);
                UpdateStatus($"ELF loaded successfully. Entry point: 0x{entry:X8}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading ELF: {ex.Message}");
            }
        }
    }

    private void OnRunClick(object? sender, RoutedEventArgs e)
    {
        _isRunning = true;
        UpdateStatus("Running...");
    }

    private void OnPauseClick(object? sender, RoutedEventArgs e)
    {
        _isRunning = false;
        UpdateStatus("Paused");
    }

    private void OnStepClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.RunFor(1_000_000);
        UpdateFramebuffer();
        UpdateStatusText();
        UpdateStatus("Stepped 1 million cycles");
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Reset();
        _isRunning = false;
        _system.Gs.RenderTestScene();   // Re-draw nice scene after reset
        UpdateFramebuffer();
        UpdateStatusText();
        UpdateStatus("System reset - test scene restored");
    }

    private void OnTestDrawClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;

        _system.Gs.RenderTestScene();
        UpdateFramebuffer();
        UpdateStatus("Colorful test scene rendered");
    }

    private async void OnSaveStateClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;

        var file = await this.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Save State",
            DefaultExtension = ".dps2",
            FileTypeChoices = new[] { new FilePickerFileType("DetPS2 Save State") { Patterns = new[] { "*.dps2" } } }
        });

        if (file != null)
        {
            try
            {
                byte[] data = _system.SaveState();
                await File.WriteAllBytesAsync(file.Path.LocalPath, data);
                UpdateStatus($"State saved: {Path.GetFileName(file.Path.LocalPath)}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Save failed: {ex.Message}");
            }
        }
    }

    private async void OnLoadStateClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;

        var files = await this.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Load Save State",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("DetPS2 State") { Patterns = new[] { "*.dps2" } } }
        });

        if (files.Count > 0)
        {
            try
            {
                byte[] data = await File.ReadAllBytesAsync(files[0].Path.LocalPath);
                bool success = _system.LoadState(data);
                UpdateFramebuffer();
                UpdateStatus(success ? "State loaded successfully" : "State load failed");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Load failed: {ex.Message}");
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _renderTimer?.Stop();
        base.OnClosed(e);
    }
}