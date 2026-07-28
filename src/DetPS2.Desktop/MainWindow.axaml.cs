using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DetPS2.Core;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DetPS2.Desktop;

public partial class MainWindow : Window
{
    private Ps2System? _system;
    private DispatcherTimer? _renderTimer;
    private bool _isRunning;
    private ulong _cyclesPerTick = 1_500_000;
    private string _currentSpeedMode = "Normal";
    private string _presentModeLabel = "Software";
    private long _lastLoggedAudioSamples;
    private long _detailLogCounter;

    private readonly RingBufferAudioSink _audioSink = new();
    private bool _recordingTape;
    private NetplaySession? _netplay;
    private INetplayTransport? _netplayTransport;
    private ProductionRollbackPeer? _rollbackPeer;
    private readonly NetGraph _netGraph = new();
    private readonly DesyncDumpWriter _desyncDump = new();
    private readonly FrameLimiter _frameLimit = new() { Enabled = true, TargetFps = 60 };
    private readonly RunAhead _runAhead = new();
    private IHostAudioDevice? _hostAudio;
    private EmulatorConfig _config = new();
    private string _lastBootMessage = "—";
    private readonly HostGamepadService _gamepads = new();
    private readonly SessionLog _sessionLog = new();
    private GameDisplayWindow? _gameWindow;
    private string? _currentGameTitle;

    private string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DetPS2", "config.json");

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            InitializeEmulator();
            SetupDragDrop();
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            Closed += (_, __) =>
            {
                CloseGameWindow();
                _sessionLog.Dispose();
                _hostAudio?.Dispose();
            };
            LoadConfigAndLibrary();
            if (SidebarLogPath != null)
                SidebarLogPath.Text = _sessionLog.LogPath ?? "—";
            Log($"{VersionInfo.Banner}");
            Log($"Session log: {_sessionLog.LogPath}");
            Log("Choose a media folder, then Boot a title — gameplay opens in a separate window.");
            Log("BIOS, controllers, and advanced options: File → Settings…");
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainWindow ctor", ex);
            _sessionLog.WriteException("ctor", ex);
            throw new InvalidOperationException(
                "Desktop failed to start: " + ex.Message + " (see %TEMP%\\DetPS2 logs)", ex);
        }
    }

    private void LoadConfigAndLibrary()
    {
        try
        {
            if (File.Exists(ConfigPath))
                _config = EmulatorConfig.Load(ConfigPath);
            else
                _config = new EmulatorConfig();

            _config.EnsureMemCardPathDefault();
            ApplyConfigToUi();
            RefreshLibraryList();
            UpdateLibraryStatusTexts();

            if (_config.HasGamesFolder)
                Log($"Library path: {_config.GamesFolder} ({_config.Games.Count} items)");
            else
                Log("No library path yet — click Set library path… (local folder or \\\\server\\share)");

            if (_config.HasBiosFile)
            {
                try
                {
                    _system?.LoadBios(_config.BiosPath);
                    Log($"BIOS restored: {Path.GetFileName(_config.BiosPath)}");
                }
                catch (Exception ex)
                {
                    Log($"BIOS path saved but failed to load: {ex.Message}");
                }
            }
            else
                Log("BIOS not set — File → Load BIOS (required for many retail discs)");

            if (_config.EnableVirtualHdd && !string.IsNullOrEmpty(_config.VirtualHddPath) && _system != null)
            {
                bool ok = _system.TryEnableVirtualHdd(_config.VirtualHddPath, _config.VirtualHddSizeMb * 1024L * 1024L);
                Log(ok ? $"Virtual HDD restored: {_config.VirtualHddPath}" : "Virtual HDD path saved but failed to open/create");
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("config load failed", ex);
            Log($"Config load warning: {ex.Message}");
        }
    }

    private void PersistConfig()
    {
        try
        {
            _config.DefaultFrameLimit = _frameLimit.Enabled;
            _config.DefaultTargetFps = _frameLimit.TargetFps;
            _config.PresentMode = _presentModeLabel;
            _config.EnableJit = _system?.UseJit ?? false;
            _config.Save(ConfigPath);
        }
        catch (Exception ex)
        {
            CrashLog.Write("persist config", ex, _system);
            Log($"Could not save settings: {ex.Message}");
        }
    }

    private void RefreshLibraryList()
    {
        if (LibraryList == null) return;
        LibraryList.ItemsSource = null;
        LibraryList.ItemsSource = _config.Games.ToList();
        if (!string.IsNullOrEmpty(_config.LastGameId))
        {
            var match = _config.Games.FirstOrDefault(g =>
                string.Equals(g.GameId, _config.LastGameId, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                LibraryList.SelectedItem = match;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_system == null) return;
        if (MapKey(e.Key, out var btn))
        {
            _system.Pad.Press(btn);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_system == null) return;
        if (MapKey(e.Key, out var btn))
        {
            _system.Pad.Release(btn);
            e.Handled = true;
        }
    }

    private static bool MapKey(Key key, out PadInput.Button button)
    {
        // WASD + arrows = D-pad; ZXCI / KL = face; QE = shoulders; Enter/Shift = Start/Select; J = Square
        button = key switch
        {
            Key.Enter => PadInput.Button.Start,
            Key.RightShift or Key.LeftShift => PadInput.Button.Select,
            Key.Up or Key.W => PadInput.Button.Up,
            Key.Down or Key.S => PadInput.Button.Down,
            Key.Left or Key.A => PadInput.Button.Left,
            Key.Right or Key.D => PadInput.Button.Right,
            Key.Z or Key.K => PadInput.Button.Cross,
            Key.X or Key.L => PadInput.Button.Circle,
            Key.J => PadInput.Button.Square,
            Key.C or Key.I => PadInput.Button.Triangle,
            Key.Q => PadInput.Button.L1,
            Key.E => PadInput.Button.R1,
            _ => PadInput.Button.None
        };
        return button != PadInput.Button.None;
    }

    private void InitializeEmulator()
    {
        _system = new Ps2System();
        _system.SetAudioSink(_audioSink);
        _hostAudio = HostAudioFactory.CreateDefault();
        _hostAudio.Open(48000);
        // No test-scene blotch on main window — game screen is a separate window
        UpdateStatus("Ready");
        UpdateSidebar();
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16.666) };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
        _sessionLog.Write("Emulator core initialized");
    }

    private void Log(string message)
    {
        _sessionLog.Write(message);
        if (LogTextBox == null) return;
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogTextBox.Text += $"[{timestamp}] {message}" + Environment.NewLine;
        // Cap UI log size
        if (LogTextBox.Text.Length > 80_000)
            LogTextBox.Text = LogTextBox.Text[^60_000..];
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
    }

    private void SetupDragDrop()
    {
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter, RoutingStrategies.Bubble);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Bubble);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble);
        if (LibraryDropZone != null)
        {
            LibraryDropZone.AddHandler(DragDrop.DragEnterEvent, OnDragEnter, RoutingStrategies.Bubble);
            LibraryDropZone.AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) { }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_system == null) return;

        var files = e.Data.GetFiles()?.ToArray();
        if (files == null || files.Length == 0) return;

        var file = files[0];
        string path = file.Path.LocalPath;
        string ext = Path.GetExtension(path).ToLowerInvariant();

        try
        {
            if (ext == ".rom")
            {
                _system.LoadBios(path);
                _config.BiosPath = path;
                PersistConfig();
                UpdateLibraryStatusTexts();
                Log($"BIOS loaded via drag & drop: {Path.GetFileName(path)}");
            }
            else if (ext == ".bin")
            {
                // Large .bin ≈ disc image; small ≈ BIOS
                var info = new FileInfo(path);
                if (info.Length > 2_000_000)
                    await BootMediaPathAsync(path, autoRun: _config.AutoRunAfterBoot);
                else
                {
                    _system.LoadBios(path);
                    _config.BiosPath = path;
                    PersistConfig();
                    UpdateLibraryStatusTexts();
                    Log($"BIOS loaded via drag & drop: {Path.GetFileName(path)}");
                }
            }
            else if (ext is ".elf" or ".iso")
            {
                await BootMediaPathAsync(path, autoRun: _config.AutoRunAfterBoot);
            }
            else
            {
                Log("Unsupported file type (use BIOS .bin/.rom, .iso, .elf)");
            }
            UpdateSidebar();
        }
        catch (Exception ex) { Log($"Drop error: {ex.Message}"); }
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_system == null) return;
        try
        {
            if (_isRunning && !_system.Debugger.Halted)
            {
                bool netplayActive = _netplay != null && _netplay.Running
                    && _netplayTransport != null && _netplayTransport.IsConnected;
                if (netplayActive)
                {
                    try
                    {
                        _netplay!.AdvanceNetworked(_system, _system.Pad.Buttons, recvTimeoutMs: 1);
                    }
                    catch (Exception ex)
                    {
                        Log($"Netplay frame error: {ex.Message}");
                        CrashLog.Write("netplay frame", ex, _system);
                        StopNetplayInternal();
                    }
                    _system.PresentFrame();
                }
                else if (_runAhead.Enabled && _presentModeLabel != "Netplay")
                {
                    // Solo run-ahead only (never with netplay)
                    _runAhead.Apply(_system, _cyclesPerTick, () =>
                    {
                        _system.PresentFrame();
                        UpdateFramebuffer();
                    });
                }
                else
                {
                    _system.RunFor(_cyclesPerTick);
                    // Phase 1: FMV advances on host present only (once per UI tick).
                    // Must not live inside RunFor or the logo burns in one EE slice.
                    // ActiveQuirk aliases MidwayAssist when the mounted disc is SLUS_210.87
                    // (see Ps2System.MidwayAssist) — call once via ActiveQuirk so this is
                    // correctly serial-gated instead of firing for every commercial title.
                    _system.ActiveQuirk?.OnHostPresent(_system);
                    _system.PresentFrame();
                }

                if (_frameLimit.Enabled && !netplayActive)
                    _frameLimit.WaitFrame();
            }
            else
            {
                _system.PresentFrame();
            }

            if (_system.Debugger.Halted)
            {
                _isRunning = false;
                UpdateStatus("Breakpoint");
            }
            PollGamepads();
            DrainAudioMeter();
            // Always push pixels to the game window while a system exists (paused still shows last frame)
            if (_gameWindow != null)
            {
                PresentToGameWindow();
                _detailLogCounter++;
                // Phase 1 diagnostics: every ~1s at 60fps so FMV freezes are obvious in the log
                if (_detailLogCounter % 60 == 0 && _system != null && _isRunning)
                {
                    _sessionLog.WriteSystemSnapshot(_system, "tick");
                    _sessionLog.Write(
                        $"fmv={_system.MidwayAssist.LogoFrame}/{_system.MidwayAssist.LogoFramesTotal} " +
                        $"assist={_system.MidwayAssist.Status} overlay={_system.Gs.HostOverlayActive} " +
                        $"presented={_system.MidwayAssist.FramesPresented}");
                    Log($"… running PC=0x{_system.EE.PC:X8} c={_system.MasterCycles:N0} " +
                        $"overlay={(_system.Gs.HostOverlayActive ? "on" : "off")} " +
                        $"assist={_system.MidwayAssist.Status} " +
                        $"fmv={_system.MidwayAssist.LogoFrame}/{_system.MidwayAssist.LogoFramesTotal}");
                }
            }
            UpdateStatusText();
            UpdateSidebar();
        }
        catch (Exception ex)
        {
            CrashLog.Write("render tick", ex, _system);
            _sessionLog.WriteException("render tick", ex);
            _sessionLog.WriteSystemSnapshot(_system, "tick-error");
            Log($"Emulation error: {ex.Message}");
            _isRunning = false;
        }
    }

    private void EnsureGameWindow(string? title = null)
    {
        if (!string.IsNullOrEmpty(title))
            _currentGameTitle = title;
        if (_gameWindow == null)
        {
            _gameWindow = new GameDisplayWindow();
            _gameWindow.KeyEvent += OnGameWindowKey;
            _gameWindow.ClosedByUser += () =>
            {
                Log("Game window closed by user — emulation paused");
                _isRunning = false;
                _gameWindow = null;
                UpdateSidebar();
            };
            _gameWindow.Show(this);
            Log("Opened game display window");
            _sessionLog.Write("GameDisplayWindow shown");
        }
        _gameWindow.SetTitleInfo(_currentGameTitle ?? "Game");
        _gameWindow.Activate();
    }

    private void CloseGameWindow()
    {
        if (_gameWindow == null) return;
        try
        {
            _gameWindow.KeyEvent -= OnGameWindowKey;
            _gameWindow.Close();
        }
        catch { /* ignore */ }
        _gameWindow = null;
    }

    private void PresentToGameWindow()
    {
        if (_system == null) return;
        if (_gameWindow == null) return;
        try
        {
            _gameWindow.PresentFrame(_system);
            _gameWindow.SetStatus($"PC=0x{_system.EE.PC:X8}  cycles={_system.MasterCycles:N0}  px={_system.Gs.PixelsWritten:N0}");
        }
        catch (Exception ex)
        {
            _sessionLog.WriteException("present", ex);
        }
    }

    private void OnGameWindowKey(Key key, bool isDown)
    {
        if (_system == null) return;
        if (!MapKey(key, out var btn)) return;
        if (isDown) _system.Pad.Press(btn);
        else _system.Pad.Release(btn);
    }

    private void OnShowGameWindowClick(object? sender, RoutedEventArgs e) => EnsureGameWindow(_currentGameTitle);

    private void OnStopEmulationClick(object? sender, RoutedEventArgs e)
    {
        _isRunning = false;
        CloseGameWindow();
        Log("Emulation stopped; game window closed");
        _sessionLog.WriteSystemSnapshot(_system, "stop");
        UpdateStatus("Stopped");
        UpdateSidebar();
    }

    private void OnOpenLogFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            string dir = _sessionLog.TempDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
            Log($"Opened log folder: {dir}");
        }
        catch (Exception ex) { Log("Could not open log folder: " + ex.Message); }
    }

    private void PollGamepads()
    {
        if (_system == null) return;
        _config.MigrateGamepadIds();
        var p1Profile = EmulatorConfig.ParseProfile(_config.Player1Profile);
        var p2Profile = EmulatorConfig.ParseProfile(_config.Player2Profile);

        // P1: hardware overrides keyboard when not "kb"
        if (!string.Equals(_config.Player1DeviceId, "kb", StringComparison.OrdinalIgnoreCase))
            _gamepads.PollInto(_system.Pad, _config.Player1DeviceId, p1Profile);

        // P2: multitap port 1
        if (!string.Equals(_config.Player2DeviceId, "kb", StringComparison.OrdinalIgnoreCase)
            && _system.Multitap.Ports.Length > 1
            && _system.Multitap.Ports[1] != null)
        {
            _gamepads.PollInto(_system.Multitap.Ports[1], _config.Player2DeviceId, p2Profile);
        }
    }

    private void ApplyConfigToUi()
    {
        _frameLimit.Enabled = _config.DefaultFrameLimit;
        _frameLimit.TargetFps = _config.DefaultTargetFps;
        if (_system != null)
        {
            _system.UseJit = _config.EnableJit;
            _system.EeJit.Enabled = _config.EnableJit;
            if (string.Equals(_config.PresentMode, "GPU", StringComparison.OrdinalIgnoreCase))
            {
                _system.Present.UseGpu();
                _presentModeLabel = "GPU";
            }
        }
    }

    /// <summary>
    /// Drain core-produced samples (no host clock drives the core).
    /// Full OS audio device playback can plug into the same ring later.
    /// </summary>
    private void DrainAudioMeter()
    {
        // Host device pump (Phase 43)
        _hostAudio?.Pump(_audioSink, 2048);
        long recv = _audioSink.SamplesReceived;
        if (recv > 0 && recv - _lastLoggedAudioSamples >= 48000)
        {
            _lastLoggedAudioSamples = recv;
            Log($"Audio: {_audioSink.SamplesReceived:N0} samples (peak={_hostAudio?.LastPeak ?? 0})");
        }
    }

    private void UpdateFramebuffer() => PresentToGameWindow();

    private void UpdateStatusText()
    {
        if (_system == null) return;
        CyclesText.Text = $"Master Cycles: {_system.MasterCycles:N0}";
    }

    private void UpdateSidebar()
    {
        if (_system == null) return;
        if (SidebarStatus != null)
        {
            if (_system.Debugger.Halted)
                SidebarStatus.Text = "Breakpoint";
            else if (_isRunning)
                SidebarStatus.Text = "Running (game window)";
            else
                SidebarStatus.Text = "Ready";
        }
        if (SidebarCycles != null)
            SidebarCycles.Text = _system.MasterCycles.ToString("N0");
        if (SidebarPc != null)
            SidebarPc.Text = $"0x{_system.EE.PC:X8}";
        if (SidebarBoot != null)
            SidebarBoot.Text = _lastBootMessage;
        if (SidebarLogPath != null)
            SidebarLogPath.Text = _sessionLog.LogPath ?? "—";
    }

    private void UpdateLibraryStatusTexts()
    {
        if (LibraryFolderText != null)
            LibraryFolderText.Text = _config.HasGamesFolder
                ? _config.GamesFolder
                : "No library path set — click Set library path… (local or \\\\server\\share)";
        if (LibraryBiosText != null)
            LibraryBiosText.Text = _config.HasBiosFile
                ? Path.GetFileName(_config.BiosPath)
                : "not set (File → Settings…)";
    }

    private void UpdateStatus(string message)
    {
        if (StatusText != null)
            StatusText.Text = message;
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var biosLabel = new TextBlock
        {
            Text = _config.HasBiosFile ? _config.BiosPath : "No BIOS selected",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11
        };
        var speed = new ComboBox { Width = 280 };
        speed.Items.Add(new ComboBoxItem { Content = "Slow", Tag = 0 });
        speed.Items.Add(new ComboBoxItem { Content = "Normal", Tag = 1 });
        speed.Items.Add(new ComboBoxItem { Content = "Fast", Tag = 2 });
        speed.Items.Add(new ComboBoxItem { Content = "Unlimited", Tag = 3 });
        speed.SelectedIndex = _currentSpeedMode switch
        {
            "Slow" => 0,
            "Fast" => 2,
            "Unlimited" => 3,
            _ => 1
        };
        var autoRun = new CheckBox { Content = "Auto-run after boot", IsChecked = _config.AutoRunAfterBoot };
        var verify = new CheckBox { Content = "Verify media on boot (serial/hash)", IsChecked = _config.VerifyMediaOnBoot };
        var frameLimit = new CheckBox { Content = "Frame limit ~60 FPS", IsChecked = _frameLimit.Enabled };

        // Memory cards are always on and are the primary save path — nothing to configure here.
        // The virtual HDD is optional, larger-capacity storage a title's own save code would
        // need to explicitly use; off by default until enabled here.
        var hddLabel = new TextBlock
        {
            Text = string.IsNullOrEmpty(_config.VirtualHddPath) ? "No virtual HDD file selected" : _config.VirtualHddPath,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11
        };
        var enableHdd = new CheckBox { Content = "Enable virtual HDD (optional — memory cards remain the primary save)", IsChecked = _config.EnableVirtualHdd };

        var win = new Window
        {
            Title = "Settings",
            Width = 480,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var pickBiosBtn = new Button { Content = "Choose BIOS file…", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Padding = new Thickness(10, 8) };
        pickBiosBtn.Click += async (_, __) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select PS2 BIOS",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("BIOS") { Patterns = new[] { "*.bin", "*.rom", "*.BIN", "*.ROM" } },
                    new FilePickerFileType("All") { Patterns = new[] { "*.*" } }
                }
            });
            if (files == null || files.Count == 0) return;
            string path = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
            try
            {
                _system?.LoadBios(path);
                _config.BiosPath = path;
                PersistConfig();
                biosLabel.Text = path;
                Log($"BIOS set: {path}");
                UpdateLibraryStatusTexts();
            }
            catch (Exception ex) { Log("BIOS error: " + ex.Message); }
        };

        var ctrlBtn = new Button { Content = "Controllers (P1/P2)…", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Padding = new Thickness(10, 8) };
        ctrlBtn.Click += (_, __) => OnControllersClick(sender, e);

        var pickHddBtn = new Button { Content = "Choose virtual HDD file…", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Padding = new Thickness(10, 8) };
        pickHddBtn.Click += async (_, __) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Virtual HDD file (created if it doesn't exist yet)",
                SuggestedFileName = "detps2_hdd.img",
                FileTypeChoices = new[] { new FilePickerFileType("HDD image") { Patterns = new[] { "*.img", "*.bin" } } }
            });
            if (file == null) return;
            string path = file.TryGetLocalPath() ?? file.Path.LocalPath;
            hddLabel.Text = path;
            _config.VirtualHddPath = path;
        };

        var save = new Button { Content = "Save & close", Width = 120, IsDefault = true };
        save.Click += (_, __) =>
        {
            _config.AutoRunAfterBoot = autoRun.IsChecked == true;
            _config.VerifyMediaOnBoot = verify.IsChecked == true;
            _frameLimit.Enabled = frameLimit.IsChecked == true;

            _config.EnableVirtualHdd = enableHdd.IsChecked == true;
            if (_config.EnableVirtualHdd && !string.IsNullOrEmpty(_config.VirtualHddPath) && _system != null)
            {
                bool ok = _system.TryEnableVirtualHdd(_config.VirtualHddPath, _config.VirtualHddSizeMb * 1024L * 1024L);
                Log(ok ? $"Virtual HDD enabled: {_config.VirtualHddPath}" : "Virtual HDD failed to open/create — check the path");
            }
            else
            {
                _system?.DisableVirtualHdd();
            }

            int si = speed.SelectedIndex;
            switch (si)
            {
                case 0: _cyclesPerTick = 300_000; _currentSpeedMode = "Slow"; break;
                case 2: _cyclesPerTick = 6_000_000; _currentSpeedMode = "Fast"; break;
                case 3: _cyclesPerTick = 25_000_000; _currentSpeedMode = "Unlimited"; break;
                default: _cyclesPerTick = 1_500_000; _currentSpeedMode = "Normal"; break;
            }
            PersistConfig();
            Log($"Settings saved (speed={_currentSpeedMode}, BIOS={(_config.HasBiosFile ? "set" : "none")})");
            win.Close();
        };

        win.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "BIOS", FontWeight = FontWeight.Bold },
                biosLabel,
                pickBiosBtn,
                new Separator(),
                new TextBlock { Text = "Controllers", FontWeight = FontWeight.Bold },
                ctrlBtn,
                new Separator(),
                new TextBlock { Text = "Storage", FontWeight = FontWeight.Bold },
                new TextBlock { Text = "Memory cards are always on and are the primary save.", FontSize = 11, Foreground = Brushes.Gray },
                enableHdd,
                hddLabel,
                pickHddBtn,
                new Separator(),
                new TextBlock { Text = "Emulation", FontWeight = FontWeight.Bold },
                new TextBlock { Text = "Speed" },
                speed,
                autoRun,
                verify,
                frameLimit,
                new Separator(),
                new TextBlock
                {
                    Text = $"Session log:\n{_sessionLog.LogPath}",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray
                },
                save
            }
        };
        await win.ShowDialog(this);
    }

    private async void OnLoadBiosClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        try
        {
            var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select PS2 BIOS file",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PS2 BIOS") { Patterns = new[] { "*.bin", "*.rom", "*.BIN", "*.ROM" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                }
            });
            if (files == null || files.Count == 0)
            {
                Log("BIOS picker cancelled");
                return;
            }
            string path = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Log($"BIOS path invalid: {path}");
                return;
            }
            _system.LoadBios(path);
            _config.BiosPath = path;
            PersistConfig();
            UpdateLibraryStatusTexts();
            _lastBootMessage = $"BIOS {Path.GetFileName(path)}";
            Log($"BIOS loaded & saved: {path}");
            UpdateStatus($"BIOS: {Path.GetFileName(path)}");
            UpdateSidebar();
        }
        catch (Exception ex)
        {
            Log($"BIOS error: {ex.Message}");
            CrashLog.Write("bios picker", ex, _system);
        }
    }

    private async void OnLoadElfClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        var files = await this.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Select PS2 ELF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("ELF") { Patterns = new[] { "*.elf", "*.ELF" } } }
        });
        if (files.Count == 0) return;
        await BootMediaPathAsync(files[0].Path.LocalPath, autoRun: _config.AutoRunAfterBoot);
    }

    private async void OnLoadIsoClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        var files = await this.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Select PS2 ISO",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Disc images") { Patterns = new[] { "*.iso", "*.ISO", "*.bin", "*.BIN" } }
            }
        });
        if (files.Count == 0) return;
        await BootMediaPathAsync(files[0].Path.LocalPath, autoRun: _config.AutoRunAfterBoot);
    }

    private void OnBootSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (LibraryList?.SelectedItem is GameSettings g)
            _ = BootMediaPathAsync(g.Path, autoRun: _config.AutoRunAfterBoot);
        else
            Log("Select a game in the media library first");
    }

    private void OnLibraryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (LibraryList?.SelectedItem is GameSettings g)
            _ = BootMediaPathAsync(g.Path, autoRun: _config.AutoRunAfterBoot);
    }

    private void OnRescanLibraryClick(object? sender, RoutedEventArgs e)
    {
        if (!_config.HasGamesFolder)
        {
            Log("No media folder set — choose one first");
            return;
        }
        ApplyFolderScan(_config.GamesFolder);
    }

    private async Task BootMediaPathAsync(string path, bool autoRun)
    {
        if (_system == null) return;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Log($"File not found: {path}");
            return;
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".cso")
        {
            _lastBootMessage = "CSO not supported yet";
            Log("CSO compressed discs are not supported — use ISO");
            UpdateSidebar();
            return;
        }

        try
        {
            if (_config.HasBiosFile)
            {
                try { _system.LoadBios(_config.BiosPath); }
                catch (Exception ex) { Log($"BIOS reload warning: {ex.Message}"); }
            }
            else
                Log("Warning: no BIOS set — direct ELF/ISO boot may fail on retail discs");

            if (ext is ".elf")
            {
                byte[] data = await File.ReadAllBytesAsync(path);
                var load = _system.LoadElf(data);
                _lastBootMessage = $"ELF entry=0x{load.Entry:X8}";
                Log($"ELF loaded — Entry: 0x{load.Entry:X8} GP=0x{load.Gp:X8}");
            }
            else if (ext is ".iso" or ".bin")
            {
                // Stream multi-GB / UNC paths — do not ReadAllBytes (2GB array limit)
                if (_config.VerifyMediaOnBoot)
                {
                    try
                    {
                        var id = await MediaVerify.IdentifyWithOnlineAsync(path, allowNetwork: true);
                        Log($"Media check: {id.Message}");
                        if (!string.IsNullOrEmpty(id.QuickSha256))
                            Log($"  quick-sha256={id.QuickSha256[..Math.Min(16, id.QuickSha256.Length)]}… size={id.SizeBytes / (1024 * 1024)}MB");
                    }
                    catch (Exception vex) { Log($"Media check skipped: {vex.Message}"); }
                }

                var boot = await Task.Run(() => _system.BootDiscFile(path));
                _lastBootMessage = boot.Success ? boot.Message : $"FAIL: {boot.Message}";
                Log(boot.Success ? $"Disc boot: {boot.Message}" : $"Disc boot failed: {boot.Message}");
                if (!boot.Success)
                {
                    UpdateSidebar();
                    return;
                }
            }
            else
            {
                Log($"Unsupported type: {ext}");
                return;
            }

            var gs = _config.GetOrAddGame(path);
            _config.LastGameId = gs.GameId;
            PersistConfig();
            RefreshLibraryList();

            _sessionLog.WriteSystemSnapshot(_system, "post-boot");
            Log("Note: DetPS2 fast-boots the disc ELF (BIOS logo sequence is not fully LLE).");
            Log($"Boot assist: {_system.MidwayAssist.Status} (FMV frames ready={_system.MidwayAssist.FramesReady}).");
            Log("Audio: host output is live when SPU2 voices produce samples (no test tone).");
            // Give async FMV preload a moment if cache was cold
            if (!_system.MidwayAssist.FramesReady)
            {
                Log("Warming boot-movie cache (one-time ffmpeg decode)…");
                await Task.Run(() =>
                {
                    for (int i = 0; i < 100 && !_system.MidwayAssist.FramesReady; i++)
                        System.Threading.Thread.Sleep(100);
                });
                Log($"Boot-movie cache: {_system.MidwayAssist.Status} frames={_system.MidwayAssist.LogoFramesTotal}");
            }
            if (autoRun)
            {
                EnsureGameWindow(Path.GetFileNameWithoutExtension(path));
                _isRunning = true;
                UpdateStatus("Running...");
                Log("Game window open — emulation running (Pause F6 / Stop F9)");
                PresentToGameWindow();
            }
            UpdateSidebar();
        }
        catch (Exception ex)
        {
            _lastBootMessage = ex.Message;
            Log($"Boot error: {ex.Message}");
            _sessionLog.WriteException("boot", ex);
            CrashLog.Write("boot media", ex, _system);
            UpdateSidebar();
        }
    }

    private void OnRunClick(object? sender, RoutedEventArgs e)
    {
        EnsureGameWindow(_currentGameTitle);
        _isRunning = true;
        Log("Emulation started / resumed");
        _sessionLog.WriteSystemSnapshot(_system, "run");
        UpdateStatus("Running...");
        UpdateSidebar();
    }

    private void OnPauseClick(object? sender, RoutedEventArgs e)
    {
        _isRunning = false;
        Log("Emulation paused");
        _sessionLog.WriteSystemSnapshot(_system, "pause");
        UpdateStatus("Paused");
        UpdateSidebar();
    }

    private void OnStepClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.RunFor(1_000_000);
        EnsureGameWindow(_currentGameTitle);
        PresentToGameWindow();
        UpdateStatusText();
        UpdateSidebar();
        Log("Stepped 1M cycles");
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Reset();
        _audioSink.Reset();
        _lastLoggedAudioSamples = 0;
        _system.SetAudioSink(_audioSink);
        _isRunning = false;
        CloseGameWindow();
        UpdateStatusText();
        UpdateSidebar();
        Log("System reset");
        UpdateStatus("Reset");
    }

    private void OnTestDrawClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Gs.RenderTestScene();
        EnsureGameWindow("Test scene");
        PresentToGameWindow();
        Log("Test scene shown in game window");
    }

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

    private void OnDebugStepClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _isRunning = false;
        _system.DebugStepInstruction();
        UpdateFramebuffer();
        RefreshDebugPanel();
        Log(_system.Debugger.Halted
            ? $"Halted at PC=0x{_system.Debugger.HaltPc:X8}"
            : "Stepped");
        UpdateSidebar();
    }

    private void OnDebugContinueClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Debugger.Continue();
        _isRunning = true;
        Log("Debug continue");
        UpdateStatus("Running...");
        UpdateSidebar();
    }

    private void OnBreakpointAtPcClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Debugger.Enabled = true;
        _system.Debugger.AddBreakpoint(_system.EE.PC);
        Log($"Breakpoint @ 0x{_system.EE.PC:X8} (count={_system.Debugger.BreakpointCount})");
    }

    private void OnClearBreakpointsClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Debugger.ClearBreakpoints();
        Log("Breakpoints cleared");
    }

    private void OnToggleTracerClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        if (_system.Tracer.Enabled)
        {
            _system.Tracer.Disable();
            Log("Tracer off");
        }
        else
        {
            _system.Tracer.Enable();
            Log("Tracer on (in-memory)");
        }
    }

    private void OnRefreshRegsClick(object? sender, RoutedEventArgs e) => RefreshDebugPanel();

    private void RefreshDebugPanel()
    {
        if (_system == null) return;
        string dump = _system.Debugger.FormatRegisters(_system.EE)
            + "\n" + _system.Debugger.FormatMemory(_system.Memory, _system.EE.PC, 4);
        _sessionLog.WriteDetail("regs", dump.Replace("\r\n", " | ").Replace("\n", " | "));
        Log("Registers written to session log");
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnRecordTapeClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.InputRecording.StartRecording();
        _recordingTape = true;
        Log("Input tape recording started (INPR)");
        UpdateStatus("Recording tape...");
    }

    private async void OnStopRecordTapeClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null || !_recordingTape) { Log("Not recording"); return; }
        _system.InputRecording.StopRecording();
        _recordingTape = false;
        var file = await this.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Save Input Tape",
            DefaultExtension = ".inpr",
            FileTypeChoices = new[] { new FilePickerFileType("DetPS2 Input Tape") { Patterns = new[] { "*.inpr" } } }
        });
        if (file != null)
        {
            try
            {
                await File.WriteAllBytesAsync(file.Path.LocalPath, _system.InputRecording.Serialize());
                Log($"Tape saved ({_system.InputRecording.FrameCount} frames): {Path.GetFileName(file.Path.LocalPath)}");
            }
            catch (Exception ex) { Log($"Tape save error: {ex.Message}"); }
        }
        UpdateStatus("Ready");
    }

    private async void OnPlayTapeClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        var files = await this.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Play Input Tape",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("DetPS2 Input Tape") { Patterns = new[] { "*.inpr" } } }
        });
        if (files.Count == 0) return;
        try
        {
            byte[] data = await File.ReadAllBytesAsync(files[0].Path.LocalPath);
            if (!_system.InputRecording.Deserialize(data))
            {
                Log("Invalid INPR tape");
                return;
            }
            _system.InputRecording.StartPlayback();
            _isRunning = true;
            Log($"Playing tape ({_system.InputRecording.FrameCount} frames)");
            UpdateStatus("Playing tape...");
        }
        catch (Exception ex) { Log($"Tape load error: {ex.Message}"); }
    }

    private void OnPresentSoftwareClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Present.DeterminismMode = true;
        _system.Present.UseSoftware();
        _presentModeLabel = "Software";
        Log("Present mode: Software (determinism)");
        UpdateSidebar();
    }

    private void OnPresentGpuClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        // DeterminismMode stays true: software snapshot still filled for hashes
        _system.Present.DeterminismMode = true;
        _system.Present.UseGpu();
        _presentModeLabel = "GPU";
        Log("Present mode: GPU staging (software GS remains truth)");
        UpdateSidebar();
    }

    private void OnPresentVulkanClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Present.DeterminismMode = true;
        _system.Present.UseVulkan();
        _system.Present.Vulkan.Scale = 1.5f;
        _system.Present.Vulkan.BilinearUpscale = true;
        _system.Present.UseCommandBuffer = true;
        _presentModeLabel = "SoftwareUpscale";
        Log($"Present: {_system.Present.Vulkan.Name} (nativeVulkan={_system.Present.Vulkan.VulkanDeviceReady}; CPU upscale only)");
        UpdateSidebar();
    }

    private void OnPresentAcceleratedClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.Present.DeterminismMode = true;
        _system.Present.UseAccelerated();
        _system.Present.Accelerated.Scale = 2f;
        _system.Present.Accelerated.Parallel = true;
        _system.Present.UseCommandBuffer = true;
        _presentModeLabel = "Accelerated";
        Log($"Present: {_system.Present.Accelerated.Name} (parallel CPU upscale; Det=software GS)");
        UpdateSidebar();
    }

    private void OnNetplayHostClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        StopNetplayInternal();
        try
        {
            Log("Netplay host listening on TCP :29700 (60s)...");
            // Accept on background so UI doesn't freeze forever without notice
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var transport = TcpNetplayTransport.Host(29700, acceptTimeoutMs: 60_000);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _netplayTransport = transport;
                        _netplay = new NetplaySession(NetplaySession.Role.Host);
                        _netplay.AttachTransport(transport);
                        _netplay.Start();
                        Log("Netplay host: client connected");
                        UpdateStatus("Netplay Host");
                    });
                }
                catch (Exception ex)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Log($"Netplay host failed: {ex.Message}"));
                }
            });
        }
        catch (Exception ex) { Log($"Netplay host error: {ex.Message}"); }
    }

    private async void OnNetplayClientClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        StopNetplayInternal();
        // Simple default: localhost
        string host = "127.0.0.1";
        try
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                var transport = TcpNetplayTransport.Connect(host, 29700, timeoutMs: 10_000);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _netplayTransport = transport;
                    _netplay = new NetplaySession(NetplaySession.Role.Client);
                    _netplay.AttachTransport(transport);
                    _netplay.Start();
                    Log($"Netplay client connected to {host}:29700");
                    UpdateStatus("Netplay Client");
                });
            });
        }
        catch (Exception ex) { Log($"Netplay client error: {ex.Message}"); }
    }

    private void OnNetplayStopClick(object? sender, RoutedEventArgs e)
    {
        StopNetplayInternal();
        Log("Netplay stopped");
        UpdateStatus("Ready");
    }

    private void OnNetplayUdpHostClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        StopNetplayInternal();
        Log("UDP netplay host on :29701 (60s wait for peer)...");
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var transport = UdpNetplayTransport.Host(29701, waitPeerMs: 60_000);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _netplayTransport = transport;
                    _netplay = new NetplaySession(NetplaySession.Role.Host);
                    _netplay.AttachTransport(transport);
                    _netplay.Start();
                    _rollbackPeer = new ProductionRollbackPeer { FrameAdvantage = 1, InputDelay = 2 };
                    _rollbackPeer.Attach(transport);
                    _rollbackPeer.Start(_system!);
                    Log("UDP host: peer connected (N4 prototype)");
                    UpdateStatus("UDP Host");
                });
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Log($"UDP host failed: {ex.Message}"));
            }
        });
    }

    private async void OnNetplayUdpClientClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        StopNetplayInternal();
        string host = "127.0.0.1";
        try
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                var transport = UdpNetplayTransport.Connect(host, 29701);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _netplayTransport = transport;
                    _netplay = new NetplaySession(NetplaySession.Role.Client);
                    _netplay.AttachTransport(transport);
                    _netplay.Start();
                    _rollbackPeer = new ProductionRollbackPeer { FrameAdvantage = 1, InputDelay = 2 };
                    _rollbackPeer.Attach(transport);
                    _rollbackPeer.Start(_system!);
                    Log($"UDP client connected to {host}:29701");
                    UpdateStatus("UDP Client");
                });
            });
        }
        catch (Exception ex) { Log($"UDP client error: {ex.Message}"); }
    }

    private void OnNetGraphClick(object? sender, RoutedEventArgs e)
    {
        string g = _rollbackPeer != null
            ? _rollbackPeer.Graph.Format()
            : _netplay != null
                ? $"lockstep f={_netplay.FrameIndex} advances={_netplay.LocalAdvances} desync={_netplay.Desync.DesyncCount}"
                : _netGraph.Format();
        Log($"NetGraph: {g}");
        UpdateStatus(g.Length > 48 ? g[..48] + "…" : g);
    }

    private void OnDesyncDumpClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        var dump = _rollbackPeer?.DesyncDump ?? _desyncDump;
        if (dump.Count == 0)
        {
            // Force a diagnostic dump of current state
            dump.Record(_system, 0, DesyncDetector.HashState(_system), 0xDEADBEEF, "manual");
        }
        Log(dump.LastSummary ?? "no desync");
        if (!string.IsNullOrEmpty(dump.LastPath))
            Log($"Desync dump: {dump.LastPath}");
    }

    private void StopNetplayInternal()
    {
        _netplay?.Stop();
        _netplay = null;
        _rollbackPeer?.Stop();
        _rollbackPeer = null;
        try { _netplayTransport?.Dispose(); } catch { /* ignore */ }
        _netplayTransport = null;
    }

    private void OnToggleFrameLimitClick(object? sender, RoutedEventArgs e)
    {
        _frameLimit.Enabled = !_frameLimit.Enabled;
        _frameLimit.Reset();
        Log(_frameLimit.Enabled ? $"Frame limit ON ({_frameLimit.TargetFps} FPS)" : "Frame limit OFF");
    }

    private void OnRunAhead0Click(object? sender, RoutedEventArgs e) { _runAhead.Frames = 0; Log("Run-ahead off"); }
    private void OnRunAhead1Click(object? sender, RoutedEventArgs e) { _runAhead.Frames = 1; Log("Run-ahead +1 (solo only)"); }
    private void OnRunAhead2Click(object? sender, RoutedEventArgs e) { _runAhead.Frames = 2; Log("Run-ahead +2 (solo only)"); }

    private void OnToggleJitClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.UseJit = !_system.UseJit;
        _system.EeJit.Enabled = _system.UseJit;
        _config.EnableJit = _system.UseJit;
        Log(_system.UseJit ? "JIT enabled (Det parity block cache)" : "JIT disabled");
    }

    /// <summary>
    /// Single library path entry: type a local path or UNC, or Browse for a folder.
    /// </summary>
    private async void OnSetLibraryPathClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PromptLibraryPathAsync(_config.GamesFolder);
        if (string.IsNullOrWhiteSpace(path)) return;
        ApplyFolderScan(path);
    }

    private async Task<string?> PromptLibraryPathAsync(string initial)
    {
        var win = new Window
        {
            Title = "Library path",
            Width = 560,
            Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var box = new TextBox
        {
            Text = initial ?? "",
            Watermark = @"C:\PS2\ISOs  or  \\server\share\ISOs"
        };
        var browse = new Button { Content = "Browse…", Width = 100, Padding = new Thickness(8, 4) };
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        string? result = null;

        browse.Click += async (_, __) =>
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select game library folder",
                    AllowMultiple = false
                });
                if (folders != null && folders.Count > 0)
                {
                    string p = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
                    if (!string.IsNullOrWhiteSpace(p))
                        box.Text = p;
                }
            }
            catch (Exception ex)
            {
                Log("Folder browser failed (you can still paste a UNC path): " + ex.Message);
            }
        };
        ok.Click += (_, __) => { result = box.Text?.Trim(); win.Close(); };
        cancel.Click += (_, __) => { result = null; win.Close(); };

        win.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Path to your game library (local folder or network UNC). " +
                           "ISOs and ELFs in that folder are listed for boot.",
                    TextWrapping = TextWrapping.Wrap
                },
                new DockPanel
                {
                    Children =
                    {
                        browse,
                        box
                    }
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { ok, cancel }
                }
            }
        };
        // Dock browse to the right of the path box
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(8, 0, 0, 0);

        await win.ShowDialog(this);
        return result;
    }

    private void ApplyFolderScan(string path)
    {
        path = GameLibrary.NormalizeLibraryPath(path);
        bool exists;
        try { exists = Directory.Exists(path); }
        catch (Exception ex)
        {
            Log($"Cannot access path: {path} ({ex.Message})");
            return;
        }
        if (!exists)
        {
            Log($"Folder does not exist or is not reachable: {path}");
            Log("For network shares, map the drive or ensure \\\\server\\share is reachable in Explorer first.");
            return;
        }

        var games = GameLibrary.ScanFolder(path);
        _config.ApplyScan(path, games);
        PersistConfig();
        RefreshLibraryList();
        UpdateLibraryStatusTexts();
        Log($"Library path set: {path}");
        Log($"Found {games.Count} media file(s) (.iso / .elf / .bin / .cso)");
        Log($"Saved to {ConfigPath}");
        UpdateStatus($"Library: {games.Count} titles");
    }

    private async void OnControllersClick(object? sender, RoutedEventArgs e)
    {
        _config.MigrateGamepadIds();
        var devices = _gamepads.Enumerate();
        var p1 = new ComboBox { Width = 360, MinHeight = 32 };
        var p2 = new ComboBox { Width = 360, MinHeight = 32 };
        var prof1 = new ComboBox { Width = 360 };
        var prof2 = new ComboBox { Width = 360 };
        prof1.Items.Add(new ComboBoxItem { Content = "Standard DualShock-style pad", Tag = "Standard" });
        prof1.Items.Add(new ComboBoxItem { Content = "Guitar Hero / Riffmaster mapping", Tag = "GuitarHero" });
        prof2.Items.Add(new ComboBoxItem { Content = "Standard DualShock-style pad", Tag = "Standard" });
        prof2.Items.Add(new ComboBoxItem { Content = "Guitar Hero / Riffmaster mapping", Tag = "GuitarHero" });

        foreach (var d in devices)
        {
            string label = d.Connected
                ? $"[{d.Kind}] {d.Name}"
                : $"[{d.Kind}] {d.Name}";
            p1.Items.Add(new ComboBoxItem { Content = label, Tag = d.Id });
            p2.Items.Add(new ComboBoxItem { Content = label, Tag = d.Id });
        }
        SelectByStringTag(p1, _config.Player1DeviceId);
        SelectByStringTag(p2, _config.Player2DeviceId);
        SelectByStringTag(prof1, _config.Player1Profile);
        SelectByStringTag(prof2, _config.Player2Profile);

        var win = new Window
        {
            Title = "Controllers — devices & type (P1 / P2)",
            Width = 480,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var refresh = new Button { Content = "Refresh devices", Width = 130 };
        refresh.Click += (_, __) =>
        {
            // Re-open dialog with fresh enum
            win.Close();
            OnControllersClick(sender, e);
        };
        var save = new Button { Content = "Save", Width = 100, IsDefault = true };
        save.Click += (_, __) =>
        {
            _config.Player1DeviceId = GetStringTag(p1) ?? "kb";
            _config.Player2DeviceId = GetStringTag(p2) ?? "kb";
            _config.Player1Profile = GetStringTag(prof1) ?? "Standard";
            _config.Player2Profile = GetStringTag(prof2) ?? "Standard";
            // Keep legacy ints in sync for older code paths
            _config.Player1Gamepad = _config.Player1DeviceId.StartsWith("xi:") &&
                int.TryParse(_config.Player1DeviceId.AsSpan(3), out int a) ? a : -1;
            _config.Player2Gamepad = _config.Player2DeviceId.StartsWith("xi:") &&
                int.TryParse(_config.Player2DeviceId.AsSpan(3), out int b) ? b : -1;
            PersistConfig();
            Log($"P1: {_config.Player1DeviceId} profile={_config.Player1Profile}");
            Log($"P2: {_config.Player2DeviceId} profile={_config.Player2Profile}");
            if (_config.Player1Profile == "GuitarHero" || _config.Player2Profile == "GuitarHero")
                Log("Guitar Hero map: frets→R2/○/△/✕/□, strum→D-pad U/D, whammy→R-stick Y");
            win.Close();
        };
        win.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Detects XInput (Xbox), HID DualShock 4 / DualSense, and guitar-class devices (Riffmaster, GH/RB). " +
                          "Use Controller type to switch a player to Guitar Hero mapping without changing the physical device.",
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock { Text = "Player 1 — device", FontWeight = FontWeight.Bold },
                p1,
                new TextBlock { Text = "Player 1 — controller type", FontWeight = FontWeight.Bold },
                prof1,
                new TextBlock { Text = "Player 2 — device", FontWeight = FontWeight.Bold },
                p2,
                new TextBlock { Text = "Player 2 — controller type", FontWeight = FontWeight.Bold },
                prof2,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    Children = { save, refresh }
                }
            }
        };
        await win.ShowDialog(this);
    }

    private static void SelectByStringTag(ComboBox box, string? tag)
    {
        tag ??= "kb";
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem cbi && cbi.Tag is string t &&
                string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string? GetStringTag(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem cbi && cbi.Tag is string t) return t;
        return null;
    }

    private void OnSaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        PersistConfig();
        Log($"Settings saved: {ConfigPath}");
    }

    private void OnLoadSettingsClick(object? sender, RoutedEventArgs e)
    {
        LoadConfigAndLibrary();
        Log($"Settings reloaded ({_config.Games.Count} games)");
    }

    private async void OnExportMemCardClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        var file = await this.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Export Memory Card",
            DefaultExtension = ".ps2",
            FileTypeChoices = new[] { new FilePickerFileType("PS2 MemCard") { Patterns = new[] { "*.ps2", "*.mcd" } } }
        });
        if (file == null) return;
        try
        {
            MemCardManager.SaveToFile(_system.MemCard, file.Path.LocalPath);
            Log($"Memory card exported ({_system.MemCard.FileCount} files)");
        }
        catch (Exception ex) { Log($"Export failed: {ex.Message}"); }
    }

    private async void OnImportMemCardClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        var files = await this.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Import Memory Card",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PS2 MemCard") { Patterns = new[] { "*.ps2", "*.mcd", "*.*" } } }
        });
        if (files.Count == 0) return;
        try
        {
            var card = MemCardManager.LoadFromFile(files[0].Path.LocalPath);
            // Copy files into system card
            _system.MemCard.Format();
            if (card.HasFile("__RAW__"))
            {
                var raw = card.ReadFile("__RAW__");
                if (raw != null) _system.MemCard.WriteFile("__RAW__", raw);
            }
            Log("Memory card imported");
        }
        catch (Exception ex) { Log($"Import failed: {ex.Message}"); }
    }

    private void OnFormatMemCardClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _system.MemCard.Format();
        Log("Memory card formatted");
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new Window { Title = "About DetPS2Sharp", Width = 460, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "DetPS2Sharp", FontSize = 22, FontWeight = FontWeight.Bold });
        stack.Children.Add(new TextBlock { Text = "Deterministic PS2 Emulator in Pure C#" });
        stack.Children.Add(new TextBlock { Text = VersionInfo.Banner, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = "Media library · BIOS+ISO boot · deterministic core", TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock
        {
            Text = "Choose a media folder (saved in AppData). Set BIOS once. Select an ISO and Boot.\nProvide your own legal BIOS/ISOs.",
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock { Text = $"Config: {ConfigPath}", FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Thickness(0,12,0,0) };
        ok.Click += (_, __) => about.Close();
        stack.Children.Add(ok);
        about.Content = stack;
        await about.ShowDialog(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _renderTimer?.Stop();
        StopNetplayInternal();
        CloseGameWindow();
        _sessionLog.Dispose();
        base.OnClosed(e);
    }
}