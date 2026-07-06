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

        UpdateStatus("DetPS2Sharp ready — Sidebar active");

        // Initialize sidebar
        UpdateSidebar();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16.666) };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
    }

    private void SetupDragDrop()
    {
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter, RoutingStrategies.Bubble);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Bubble);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble);

        if (DisplayBorder != null)
        {
            DisplayBorder.AddHandler(DragDrop.DragEnterEvent, OnDragEnter, RoutingStrategies.Bubble);
            DisplayBorder.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Bubble);
            DisplayBorder.AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
            if (DisplayBorder != null)
                DisplayBorder.BorderBrush = new SolidColorBrush(Colors.DodgerBlue);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (DisplayBorder != null)
            DisplayBorder.BorderBrush = new SolidColorBrush(Colors.Gray);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DisplayBorder != null)
            DisplayBorder.BorderBrush = new SolidColorBrush(Colors.Gray);

        if (_system == null) return;

        var files = e.Data.GetFiles()?.ToArray();
        if (files == null || files.Length == 0) return;

        var file = files[0];
        string path = file.Path.LocalPath;
        string ext = Path.GetExtension(path).ToLowerInvariant();

        try
        {
            if (ext == ".bin" || ext == ".rom")
            {
                _system.LoadBios(path);
                UpdateStatus($"BIOS loaded via drag & drop: {Path.GetFileName(path)}");
                UpdateSidebar();
            }
            else if (ext == ".elf")
            {
                byte[] elfData = await File.ReadAllBytesAsync(path);
                ulong entry = ElfLoader.LoadElf(elfData, _system.Memory);
                UpdateStatus($"ELF loaded via drag & drop — Entry: 0x{entry:X8}");
                UpdateSidebar();
            }
            else
            {
                UpdateStatus("Unsupported file type (.bin, .rom, or .elf only)");
            }
        }
        catch (Exception ex)
        {
            UpdateStatus($"Drop failed: {ex.Message}");
        }
    }

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
        FpsText.Text = $"~{(_cyclesPerTick / 1_500_000.0):F1} updates/sec";
    }

    private void UpdateSidebar()
    {
        if (_system == null) return;

        SidebarStatus.Text = _isRunning ? "Running" : "Ready";
        SidebarCycles.Text = _system.MasterCycles.ToString("N0");
        SidebarSpeed.Text = _currentSpeedMode;
        SidebarFps.Text = $"~{(_cyclesPerTick / 1_500_000.0):F1}";
    }

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    private void OnSpeedChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SpeedCombo.SelectedIndex < 0 || _system == null) return;

        switch (SpeedCombo.SelectedIndex)
        {
            case 0:
                _cyclesPerTick = 300_000;
                _currentSpeedMode = "Slow";
                UpdateStatus("Speed: Slow");
                break;
            case 1:
                _cyclesPerTick = 1_500_000;
                _currentSpeedMode = "Normal";
                UpdateStatus("Speed: Normal");
                break;
            case 2:
                _cyclesPerTick = 6_000_000;
                _currentSpeedMode = "Fast";
                UpdateStatus("Speed: Fast");
                break;
            case 3:
                _cyclesPerTick = 25_000_000;
                _currentSpeedMode = "Unlimited";
                UpdateStatus("Speed: Unlimited");
                break;
        }

        UpdateSidebar();
    }

    // ==================== All other handlers (Load, Run, etc.) remain unchanged ====================

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
                UpdateSidebar();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error: {ex.Message}");
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
                UpdateStatus($"ELF loaded successfully — Entry: 0x{entry:X8}");
                UpdateSidebar();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error: {ex.Message}");
            }
        }
    }

    private void OnRunClick(object? sender, RoutedEventArgs e)
    {
        _isRunning = true;
        UpdateStatus("Running...");
        UpdateSidebar();
    }

    private void OnPauseClick(object? sender, RoutedEventArgs e)
    {
        _isRunning = false;
        UpdateStatus("Paused");
        UpdateSidebar();
    }

    private void OnStepClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.RunFor(1_000_000);
        UpdateFramebuffer();
        UpdateStatusText();
        UpdateSidebar();
        UpdateStatus("Stepped 1 million cycles");
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Reset();
        _isRunning = false;
        _system.Gs.RenderTestScene();
        UpdateFramebuffer();
        UpdateStatusText();
        UpdateSidebar();
        UpdateStatus("System reset — test scene restored");
    }

    private void OnTestDrawClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Gs.RenderTestScene();
        UpdateFramebuffer();
        UpdateSidebar();
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
                UpdateSidebar();
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
                UpdateSidebar();
                UpdateStatus(success ? "State loaded successfully" : "State load failed");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Load failed: {ex.Message}");
            }
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new Window
        {
            Title = "About DetPS2Sharp",
            Width = 420,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "DetPS2Sharp", FontSize = 22, FontWeight = Avalonia.Media.FontWeight.Bold });
        stack.Children.Add(new TextBlock { Text = "Deterministic PlayStation 2 Emulator in Pure C#" });
        stack.Children.Add(new TextBlock { Text = "Version: Early Development (July 2026)" });
        stack.Children.Add(new TextBlock { Text = "Architecture: .NET 9 + Avalonia + NativeAOT ready" });
        stack.Children.Add(new TextBlock { Text = "" });
        stack.Children.Add(new TextBlock { Text = "A clean-slate, determinism-first PS2 emulator." });
        stack.Children.Add(new TextBlock { Text = "Built collaboratively — UI layer fully hooked into core." });

        var okButton = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        okButton.Click += (_, __) => about.Close();
        stack.Children.Add(okButton);

        about.Content = stack;
        await about.ShowDialog(this);
    }

    private void OnResetFramebufferViewClick(object? sender, RoutedEventArgs e)
    {
        if (_system != null)
        {
            _system.Gs.RenderTestScene();
            UpdateFramebuffer();
            UpdateSidebar();
            UpdateStatus("Framebuffer view reset to test scene");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _renderTimer?.Stop();
        base.OnClosed(e);
    }
}