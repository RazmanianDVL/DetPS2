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

    private void Log(string message)
    {
        if (LogTextBox == null) return;
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogTextBox.Text += $"[{timestamp}] {message}" + Environment.NewLine;
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
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
            if (DisplayBorder != null) DisplayBorder.BorderBrush = new SolidColorBrush(Colors.DodgerBlue);
        }
        else e.DragEffects = DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (DisplayBorder != null) DisplayBorder.BorderBrush = new SolidColorBrush(Colors.Gray);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DisplayBorder != null) DisplayBorder.BorderBrush = new SolidColorBrush(Colors.Gray);
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
                Log($"BIOS loaded via drag & drop: {Path.GetFileName(path)}");
            }
            else if (ext == ".elf")
            {
                byte[] elfData = await File.ReadAllBytesAsync(path);
                ulong entry = ElfLoader.LoadElf(elfData, _system.Memory);
                Log($"ELF loaded via drag & drop — Entry: 0x{entry:X8}");
            }
            else
            {
                Log("Unsupported file type");
            }
            UpdateSidebar();
        }
        catch (Exception ex) { Log($"Drop error: {ex.Message}"); }
    }

    private void OnFramebufferWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0)
            _zoomLevel = Math.Min(_zoomLevel + ZoomStep, MaxZoom);
        else
            _zoomLevel = Math.Max(_zoomLevel - ZoomStep, MinZoom);

        UpdateZoomDisplay();
        e.Handled = true;
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_system == null || _framebufferBitmap == null) return;
        if (_isRunning) _system.RunFor(_cyclesPerTick);
        UpdateFramebuffer();
        UpdateStatusText();
        UpdateSidebar();
    }

    private unsafe void UpdateFramebuffer()
    {
        if (_system == null || _framebufferBitmap == null) return;
        var fb = _system.Gs.GetFramebuffer();
        using var locked = _framebufferBitmap.Lock();
        uint* dest = (uint*)locked.Address;
        for (int i = 0; i < fb.Length; i++)
        {
            uint src = fb[i];
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
    }

    private void UpdateSidebar()
    {
        if (_system == null) return;
        SidebarStatus.Text = _isRunning ? "Running" : "Ready";
        SidebarCycles.Text = _system.MasterCycles.ToString("N0");
        SidebarSpeed.Text = _currentSpeedMode;
        SidebarFps.Text = $"~{(_cyclesPerTick / 1_500_000.0):F1}";
    }

    private void UpdateStatus(string message) => StatusText.Text = message;

    private void UpdateZoomDisplay()
    {
        ZoomLevelText.Text = $"{_zoomLevel * 100:F0}%";
        if (FramebufferImage.RenderTransform is ScaleTransform st)
        {
            st.ScaleX = _zoomLevel;
            st.ScaleY = _zoomLevel;
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
        Log($"Zoomed in ({_zoomLevel * 100:F0}%)");
    }

    private void OnZoomOutClick(object? sender, RoutedEventArgs e)
    {
        _zoomLevel = Math.Max(_zoomLevel - ZoomStep, MinZoom);
        UpdateZoomDisplay();
        Log($"Zoomed out ({_zoomLevel * 100:F0}%)");
    }

    private void OnResetZoomClick(object? sender, RoutedEventArgs e)
    {
        _zoomLevel = 1.0;
        UpdateZoomDisplay();
        Log("Zoom reset to 100%");
    }

    private void OnSpeedChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SpeedCombo.SelectedIndex < 0 || _system == null) return;
        switch (SpeedCombo.SelectedIndex)
        {
            case 0: _cyclesPerTick = 300_000; _currentSpeedMode = "Slow"; break;
            case 1: _cyclesPerTick = 1_500_000; _currentSpeedMode = "Normal"; break;
            case 2: _cyclesPerTick = 6_000_000; _currentSpeedMode = "Fast"; break;
            case 3: _cyclesPerTick = 25_000_000; _currentSpeedMode = "Unlimited"; break;
        }
        Log($"Speed changed to {_currentSpeedMode}");
        UpdateStatus($"Speed: {_currentSpeedMode}");
        UpdateSidebar();
    }

    private async void OnLoadBiosClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        var files = await this.StorageProvider.OpenFilePickerAsync(new() { Title = "Select PS2 BIOS", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("BIOS") { Patterns = new[] { "*.bin", "*.rom" } } } });
        if (files.Count > 0)
        {
            try { _system.LoadBios(files[0].Path.LocalPath); Log($"BIOS loaded: {Path.GetFileName(files[0].Path.LocalPath)}"); UpdateSidebar(); } catch (Exception ex) { Log($"Error: {ex.Message}"); }
        }
    }

    private async void OnLoadElfClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        var files = await this.StorageProvider.OpenFilePickerAsync(new() { Title = "Select PS2 ELF", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("ELF") { Patterns = new[] { "*.elf", "*.ELF" } } } });
        if (files.Count > 0)
        {
            try { byte[] data = await File.ReadAllBytesAsync(files[0].Path.LocalPath); ulong entry = ElfLoader.LoadElf(data, _system.Memory); Log($"ELF loaded — Entry: 0x{entry:X8}"); UpdateSidebar(); } catch (Exception ex) { Log($"Error: {ex.Message}"); }
        }
    }

    private void OnRunClick(object? sender, RoutedEventArgs e) { _isRunning = true; Log("Emulation started"); UpdateStatus("Running..."); UpdateSidebar(); }
    private void OnPauseClick(object? sender, RoutedEventArgs e) { _isRunning = false; Log("Emulation paused"); UpdateStatus("Paused"); UpdateSidebar(); }
    private void OnStepClick(object? sender, RoutedEventArgs e) { if (_system == null) return; _system.RunFor(1_000_000); UpdateFramebuffer(); UpdateStatusText(); UpdateSidebar(); Log("Stepped 1M cycles"); }
    private void OnResetClick(object? sender, RoutedEventArgs e) { if (_system == null) return; _system.Reset(); _isRunning = false; _system.Gs.RenderTestScene(); UpdateFramebuffer(); UpdateStatusText(); UpdateSidebar(); Log("System reset"); UpdateStatus("Reset"); }
    private void OnTestDrawClick(object? sender, RoutedEventArgs e) { if (_system == null) return; _system.Gs.RenderTestScene(); UpdateFramebuffer(); UpdateSidebar(); Log("Test scene rendered"); }

    private async void OnSaveStateClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        var file = await this.StorageProvider.SaveFilePickerAsync(new() { Title = "Save State", DefaultExtension = ".dps2", FileTypeChoices = new[] { new FilePickerFileType("DetPS2 Save State") { Patterns = new[] { "*.dps2" } } } });
        if (file != null) { try { byte[] data = _system.SaveState(); await File.WriteAllBytesAsync(file.Path.LocalPath, data); Log("State saved"); } catch { Log("Save error"); } }
    }

    private async void OnLoadStateClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        var files = await this.StorageProvider.OpenFilePickerAsync(new() { Title = "Load State", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("DetPS2 State") { Patterns = new[] { "*.dps2" } } } });
        if (files.Count > 0) { try { byte[] data = await File.ReadAllBytesAsync(files[0].Path.LocalPath); _system.LoadState(data); UpdateFramebuffer(); UpdateSidebar(); Log("State loaded"); } catch { Log("Load error"); } }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new Window { Title = "About DetPS2Sharp", Width = 420, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "DetPS2Sharp", FontSize = 22, FontWeight = FontWeight.Bold });
        stack.Children.Add(new TextBlock { Text = "Deterministic PS2 Emulator in Pure C#" });
        stack.Children.Add(new TextBlock { Text = "Version: July 2026" });
        stack.Children.Add(new TextBlock { Text = "UI fully hooked into core" });
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Thickness(0,12,0,0) };
        ok.Click += (_, __) => about.Close();
        stack.Children.Add(ok);
        about.Content = stack;
        await about.ShowDialog(this);
    }

    private void OnResetFramebufferViewClick(object? sender, RoutedEventArgs e)
    {
        if (_system != null) { _system.Gs.RenderTestScene(); UpdateFramebuffer(); UpdateSidebar(); Log("Framebuffer reset"); }
    }

    protected override void OnClosed(EventArgs e) { _renderTimer?.Stop(); base.OnClosed(e); }
}