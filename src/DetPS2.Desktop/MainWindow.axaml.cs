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

        // Create WriteableBitmap matching GS framebuffer size (640x448)
        _framebufferBitmap = new WriteableBitmap(
            new PixelSize(640, 448),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        FramebufferImage.Source = _framebufferBitmap;

        UpdateStatus("Emulator initialized. Load BIOS or click Test Draw.");

        // 60 FPS render loop
        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16.666)
        };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_system == null || _framebufferBitmap == null) return;

        if (_isRunning)
        {
            // Run a chunk of cycles per frame (~1.5M cycles @ ~90M EE cycles/sec target later)
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

        // Convert 0x00RRGGBB (from Gs) to BGRA8888
        for (int i = 0; i < framebuffer.Length; i++)
        {
            uint src = framebuffer[i];
            byte r = (byte)((src >> 16) & 0xFF);
            byte g = (byte)((src >> 8) & 0xFF);
            byte b = (byte)(src & 0xFF);

            // BGRA order for Avalonia WriteableBitmap
            dest[i] = (uint)((0xFF << 24) | (b << 16) | (g << 8) | r);
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
            double fps = delta / 1_500_000.0; // rough estimate based on cycles per tick
            FpsText.Text = $"~{fps:F1} updates/sec";
            _lastCycles = current;
            _lastFpsUpdate = now;
        }

        StatusText.Text = _isRunning ? "Running" : "Paused";
    }

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    // ==================== Button Handlers ====================

    private async void OnLoadBiosClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new()
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
                UpdateStatus($"Failed to load BIOS: {ex.Message}");
            }
        }
    }

    private async void OnLoadElfClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new()
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
                UpdateStatus($"ELF loaded. Entry: 0x{entry:X8} (EE PC not yet wired)");
                // TODO: Wire to EmotionEngine.PC once EE interpreter is expanded
            }
            catch (Exception ex)
            {
                UpdateStatus($"Failed to load ELF: {ex.Message}");
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
        UpdateStatus("Stepped 1M cycles");
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Reset();
        _isRunning = false;
        UpdateFramebuffer();
        UpdateStatusText();
        UpdateStatus("System reset");
    }

    private void OnTestDrawClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;

        // Trigger test drawing through GIF/GS path
        _system.Gif.ReceivePath3Data(0, 1); // This will parse and call into GS
        UpdateFramebuffer();
        UpdateStatus("Test scene rendered via GIF -> GS");
    }

    private async void OnSaveStateClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new()
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

        var files = await StorageProvider.OpenFilePickerAsync(new()
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