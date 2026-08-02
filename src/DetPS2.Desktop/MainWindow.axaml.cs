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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DetPS2.Desktop;

public partial class MainWindow : Window, IOptionsHost
{
    private Ps2System? _system;
    private readonly EmulationWorker _emuWorker = new();
    private ulong _lastWorkerPolicyMin;
    private ulong _lastWorkerPolicyMax;
    private bool _workerPolicySeeded;
    private DispatcherTimer? _renderTimer;
    private bool _isRunning;
    // Default Fast: commercial Soft-GS first paint sooner; Normal (6M) is the calm play default.
    // Frame limit defaults OFF during bring-up; WaitFrame (if ON) runs on EmulationWorker only —
    // never on the Avalonia UI thread. Boot race temporarily Unlimited until lit>100 / 200M.
    private ulong _cyclesPerTick = 25_000_000;
    private string _currentSpeedMode = "Fast";
    private string _presentModeLabel = "Software";
    private long _lastLoggedAudioSamples;
    private long _detailLogCounter;
    private long _sidebarTickCounter;
    // Throughput meter (wall cycles/sec) — logged every ~1s while running after boot.
    private readonly Stopwatch _throughputSw = new();
    private ulong _throughputLastCycles;
    private long _throughputLastMs;
    private double _lastCyclesPerSec;
    private bool _loggedFirstSoftGsPx;
    // Boot race (auto-run only): Unlimited + skip frame wait until lit>100 OR cycles>200M.
    // Temporary only — never mutates persisted Options (frameLimit / speed / PresentMode).
    private bool _bootUncappedUntilSoftGs;
    private string _preBootSpeedMode = "Fast";
    private ulong _preBootCyclesPerTick = 25_000_000;
    // When PresentMode is Auto: force Avalonia Software present for first 30s wall so Soft-GS is visible.
    private bool _bootForceSoftwarePresent;
    private readonly Stopwatch _bootPresentSw = new();
    private const int BootSoftwarePresentMs = 30_000;
    private const int BootLitPixelsDone = 100;
    private const ulong BootCyclesCap = 200_000_000UL;
    private const ulong BootUnlimitedQuantum = 50_000_000UL;
    // Last speed policy pushed to EmulationWorker (avoid stomping adaptive quantum every UI tick).
    private string _lastWorkerSpeedKey = "";
    // Latest worker lit (from snapshot present) — boot-race end without racing mid-RunFor GS.
    private long _lastWorkerLit;

    private readonly RingBufferAudioSink _audioSink = new();
    private bool _recordingTape;
    private NetplaySession? _netplay;
    private INetplayTransport? _netplayTransport;
    private ProductionRollbackPeer? _rollbackPeer;
    private readonly NetGraph _netGraph = new();
    private readonly DesyncDumpWriter _desyncDump = new();
    private readonly FrameLimiter _frameLimit = new() { Enabled = false, TargetFps = 60 };
    private readonly RunAhead _runAhead = new();
    private IHostAudioDevice? _hostAudio;
    private EmulatorConfig _config = new();
    private string _lastBootMessage = "—";
    private readonly HostGamepadService _gamepads = new();
    private readonly SessionLog _sessionLog = new();
    private GameDisplayWindow? _gameWindow;
    private string? _currentGameTitle;
    private OptionsWindow? _optionsWindow;

    private string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DetPS2", "config.json");

    // --- IOptionsHost (OptionsWindow shell; UI-2 / PAD pages under Options/) ---
    EmulatorConfig IOptionsHost.Config => _config;
    FrameLimiter IOptionsHost.FrameLimit => _frameLimit;
    HostGamepadService IOptionsHost.Gamepads => _gamepads;
    string IOptionsHost.CurrentSpeedMode
    {
        get => _currentSpeedMode;
        set => _currentSpeedMode = value;
    }
    ulong IOptionsHost.CyclesPerTick
    {
        get => _cyclesPerTick;
        set => _cyclesPerTick = value;
    }
    string IOptionsHost.SessionLogPath => _sessionLog.LogPath ?? "";
    string IOptionsHost.SessionLogDir => _sessionLog.TempDir;
    string IOptionsHost.PresentModeLabel
    {
        get => _presentModeLabel;
        set => _presentModeLabel = value;
    }

    void IOptionsHost.PersistConfig() => PersistConfig();
    void IOptionsHost.Log(string message) => Log(message);
    void IOptionsHost.OnLibraryPathsChanged()
    {
        RefreshLibraryList();
        UpdateLibraryStatusTexts();
    }
    void IOptionsHost.LoadBiosPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            _system?.LoadBios(path);
        _config.BiosPath = path ?? "";
    }
    Task<string?> IOptionsHost.PromptLibraryPathAsync(string initial) => PromptLibraryPathAsync(initial);
    void IOptionsHost.ApplyFolderScan(string path) => ApplyFolderScan(path);
    void IOptionsHost.OpenLogFolder() => OnOpenLogFolderClick(null, new RoutedEventArgs());
    void IOptionsHost.ApplyFrameLimitFromConfig()
    {
        // Always mirror the user's permanent Options choice into _frameLimit.
        // During boot race ShouldSkipFrameWait() still bypasses WaitFrame — do not clear Enabled
        // (that would permanently wipe DefaultFrameLimit on the next PersistConfig).
        _frameLimit.Enabled = _config.DefaultFrameLimit;
        _frameLimit.TargetFps = _config.DefaultTargetFps > 0 ? _config.DefaultTargetFps : 60;
        SyncWorkerFramePacing();
        if (_bootUncappedUntilSoftGs && _frameLimit.Enabled)
            Log("Frame limit kept ON in Options — skipped for boot race only (lit>100 or 200M cycles)");
    }
    void IOptionsHost.ApplyPresentModeFromConfig()
    {
        string mode = string.IsNullOrWhiteSpace(_config.PresentMode) ? "Software" : _config.PresentMode.Trim();
        // Legacy alias
        if (string.Equals(mode, "GPU", StringComparison.OrdinalIgnoreCase))
            mode = "D3D11";
        _config.PresentMode = mode;
        _presentModeLabel = mode;
        // Core PresentPipeline stays Software for Det hash; host GPU is GameDisplayWindow only.
        _system?.Present.UseSoftware();
        // Boot race with Auto: keep Avalonia Soft-GS path for first 30s (GPU exclusive later).
        string hostMode = EffectiveHostPresentMode();
        _gameWindow?.SetPresentMode(hostMode);
        _gamepads.ApplyConfigBindings(_config);
        string note = _bootForceSoftwarePresent && IsAutoPresentMode(mode)
            ? " (boot: Software for Soft-GS, Auto resumes after 30s)"
            : "";
        Log($"Host renderer: {mode}{note}" +
            (_gameWindow != null ? $" (game window → {_gameWindow.HostPresentName})" : " (applies when game window opens)"));
    }

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
                _isRunning = false;
                try
                {
                    _emuWorker.Detach();
                    _emuWorker.Dispose();
                }
                catch { /* ignore shutdown races */ }
                CloseGameWindow();
                try { _optionsWindow?.Close(); } catch { /* ignore */ }
                _sessionLog.Dispose();
                _hostAudio?.Dispose();
            };
            LoadConfigAndLibrary();
            Log($"{VersionInfo.Banner}");
            Log($"Session log: {_sessionLog.LogPath}");
            Log("Double-click a title to boot — gameplay opens in a separate window.");
            Log("Library path and other settings: Options → General…");
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
            _gamepads.ApplyConfigBindings(_config);
            RefreshLibraryList();
            UpdateLibraryStatusTexts();
            EnqueueLibraryMetadataRefresh();

            if (_config.HasGamesFolder)
                Log($"Library path: {_config.GamesFolder} ({_config.Games.Count} items)");
            else
                Log("No library path yet — Options → General → Set library path…");

            // Desktop always uses built-in native HLE — no BIOS dump required.
            try
            {
                _system?.LoadBiosNative();
                Log("Native BIOS HLE ready (no BIOS dump required)");
            }
            catch (Exception ex)
            {
                Log($"Native BIOS HLE init warning: {ex.Message}");
            }

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
            // Permanent Options only. Boot race never clears FrameLimit.Enabled, so saving
            // Enabled here keeps the user's Graphics checkbox (temporary WaitFrame skip only).
            _config.DefaultFrameLimit = _frameLimit.Enabled;
            _config.DefaultTargetFps = _frameLimit.TargetFps;
            // Prefer config.PresentMode (set by Options → Graphics); never write temporary
            // boot Software force back into config when user chose Auto.
            if (string.IsNullOrWhiteSpace(_config.PresentMode))
                _config.PresentMode = string.IsNullOrWhiteSpace(_presentModeLabel) ? "Software" : _presentModeLabel;
            else
                _presentModeLabel = _config.PresentMode;
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
        var items = _config.Games.Select(g => new LibraryItemVm(g)).ToList();
        LibraryList.ItemsSource = null;
        LibraryList.ItemsSource = items;
        if (EmptyLibraryHint != null)
            EmptyLibraryHint.IsVisible = items.Count == 0;
        if (!string.IsNullOrEmpty(_config.LastGameId))
        {
            var match = items.FirstOrDefault(v =>
                string.Equals(v.Game.GameId, _config.LastGameId, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                LibraryList.SelectedItem = match;
        }
    }

    /// <summary>Resolve serials + optional box art without blocking the UI thread.</summary>
    private void EnqueueLibraryMetadataRefresh()
    {
        if (_config.Games.Count == 0) return;
        var snapshot = _config.Games.ToList();
        var cfg = _config;
        _ = Task.Run(async () =>
        {
            try
            {
                using var meta = new DetPS2.Core.Metadata.LibraryMetadataService(cfg);
                foreach (var g in snapshot)
                {
                    try { await meta.EnsureSerialAndEnqueueAsync(g.Path, g).ConfigureAwait(false); }
                    catch { /* per-title identify failures are non-fatal */ }
                }
                // Allow a short window for in-flight scrapes to write cache.
                await Task.Delay(800).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        PersistConfig();
                        RefreshLibraryList();
                    }
                    catch { /* ignore */ }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    Log("Metadata refresh: " + ex.Message));
            }
        });
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

    private bool MapKey(Key key, out PadInput.Button button)
    {
        // Prefer saved keyboard bindings (PAD-1 table) when present.
        string code = Options.BindingCaptureDialog.KeyToCode(key);
        if (!string.IsNullOrEmpty(code))
        {
            var table = _config.GetPlayer1BindingTable(ControllerHardwareKind.Keyboard);
            if (table.TryMapKey(code, out button) && button != PadInput.Button.None)
                return true;
        }

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
        // Main window no longer hosts a LOG panel; session file is the live sink.
        _sessionLog.Write(message);
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
                // BIOS dumps are ignored — Desktop uses built-in native HLE only.
                Log("BIOS dump ignored (native HLE is always used). Drop an .iso / .bin disc or .elf instead.");
            }
            else if (ext == ".bin")
            {
                // Large .bin ≈ disc image; small files are treated as non-disc (not BIOS).
                var info = new FileInfo(path);
                if (info.Length > 2_000_000)
                    await BootMediaPathAsync(path, autoRun: _config.AutoRunAfterBoot);
                else
                    Log("Small .bin ignored (not treated as BIOS). Drop a disc image (.iso / large .bin) or .elf.");
            }
            else if (ext is ".elf" or ".iso")
            {
                await BootMediaPathAsync(path, autoRun: _config.AutoRunAfterBoot);
            }
            else
            {
                Log("Unsupported file type (use .iso, .bin disc image, or .elf)");
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
            // CRITICAL: EE never runs on the UI thread for solo play (Windows "Not Responding").
            // Worker: RunFor + ActiveQuirk.OnHostPresent + PresentFrame + Soft-GS snapshot.
            // UI: PresentSnapshot / TryGetPresent only (see PresentToGameWindow).
            bool wantRun = _isRunning && !_system.Debugger.Halted;
            bool netplayActive = wantRun
                && _netplay != null && _netplay.Running
                && _netplayTransport != null && _netplayTransport.IsConnected;

            // Quantum policy ~0.5–4M (adaptive inside worker). Do NOT assign Quantum every
            // tick — that stomps AdaptQuantum. Seed only when speed/boot bounds change.
            ApplyWorkerQuantumPolicy(seed: false);
            // Frame limit WaitFrame on worker only (never UI).
            SyncWorkerFramePacing();

            if (netplayActive)
            {
                // Netplay lockstep stays on UI (deterministic). Worker must stay off.
                _emuWorker.IsRunning = false;
                if (!_throughputSw.IsRunning)
                    BeginThroughputWindow();
                try
                {
                    _netplay!.AdvanceNetworked(_system, _system.Pad.Buttons, recvTimeoutMs: 1);
                    _system.PresentFrame();
                }
                catch (Exception ex)
                {
                    Log($"Netplay frame error: {ex.Message}");
                    CrashLog.Write("netplay frame", ex, _system);
                    StopNetplayInternal();
                }
                MaybeNoteSoftGsAndEndBootBoost();
                MaybeEndBootSoftwareForce();
                MaybeLogThroughput();
            }
            else
            {
                _emuWorker.IsRunning = wantRun;
                if (wantRun)
                {
                    if (!_throughputSw.IsRunning)
                        BeginThroughputWindow();
                    MaybeNoteSoftGsAndEndBootBoost();
                    MaybeEndBootSoftwareForce();
                    MaybeLogThroughput();
                }
            }

            if (_system.Debugger.Halted)
            {
                _isRunning = false;
                _emuWorker.IsRunning = false;
                UpdateStatus("Breakpoint");
            }
            PollGamepads();
            DrainAudioMeter();
            if (_gameWindow != null)
            {
                PresentToGameWindow();
                _detailLogCounter++;
                if (_detailLogCounter % 120 == 0 && _isRunning)
                {
                    // Status fields may tear vs mid-slice EE; logging only.
                    Log($"… worker={_emuWorker.Status} q={_emuWorker.Quantum:N0} " +
                        $"presentHz={_emuWorker.PresentHz:F0} PC=0x{_system.EE.PC:X8} c={_system.MasterCycles:N0} " +
                        $"px={_system.Gs.PixelsWritten:N0} lit={_gameWindow.LastLitPixels:N0} " +
                        $"throughput={FormatThroughput(_lastCyclesPerSec)}");
                }
            }
            UpdateStatusText();
            _sidebarTickCounter++;
            if (_system.Debugger.Halted || (_sidebarTickCounter % 15) == 0)
                UpdateSidebar();
        }
        catch (Exception ex)
        {
            CrashLog.Write("render tick", ex, _system);
            _sessionLog.WriteException("render tick", ex);
            Log($"Emulation error: {ex.Message}");
            _isRunning = false;
            _emuWorker.IsRunning = false;
        }
    }

    /// <summary>True when host WaitFrame must not block (Unlimited, or temporary boot Soft-GS race).</summary>
    private bool ShouldSkipFrameWait()
    {
        if (_bootUncappedUntilSoftGs) return true;
        return string.Equals(_currentSpeedMode, "Unlimited", StringComparison.OrdinalIgnoreCase);
    }

    private ulong EffectiveCyclesPerTick()
    {
        // Boot race: hard Unlimited quantum so Soft-GS chrome / Deception MidwayFamilyAssist get CPU.
        // Note: UI never RunFor this amount — worker policy is capped to MaxQuantum (4M).
        if (_bootUncappedUntilSoftGs)
            return BootUnlimitedQuantum;
        return _cyclesPerTick;
    }

    /// <summary>
    /// Map speed mode / boot race into worker quantum bounds for ~30–60 present/s.
    /// Playable band defaults to EmulationWorker.Min/MaxQuantum (0.5–4M). Slow is lower;
    /// boot/Unlimited still cap at MaxQuantum so UI presents stay fluid (not 50M freezes).
    /// When <paramref name="seed"/> is false, skip work unless speed/boot key changed.
    /// </summary>
    private void ApplyWorkerQuantumPolicy(bool seed)
    {
        string mode = _bootUncappedUntilSoftGs
            ? "BootRace"
            : (string.IsNullOrWhiteSpace(_currentSpeedMode) ? "Fast" : _currentSpeedMode);

        ulong min, max, seedQ;
        if (string.Equals(mode, "Slow", StringComparison.OrdinalIgnoreCase))
        {
            min = 100_000UL;
            max = 500_000UL;
            seedQ = 300_000UL;
        }
        else if (string.Equals(mode, "Normal", StringComparison.OrdinalIgnoreCase))
        {
            min = EmulationWorker.MinQuantum;
            max = 3_000_000UL;
            seedQ = 1_500_000UL;
        }
        else
        {
            // Fast / Unlimited / BootRace: playable 0.5–4M/slice (more slices, not larger freezes).
            min = EmulationWorker.MinQuantum;
            max = EmulationWorker.MaxQuantum;
            seedQ = 2_000_000UL;
        }

        // Key includes speed + boot-race so we re-seed when those change.
        string key = mode + "|" + _cyclesPerTick + "|" + min + "|" + max;
        bool keyChanged = !string.Equals(key, _lastWorkerSpeedKey, StringComparison.Ordinal);
        bool boundsChanged = min != _lastWorkerPolicyMin || max != _lastWorkerPolicyMax;
        bool doSeed = seed || !_workerPolicySeeded || keyChanged;

        if (!doSeed && !boundsChanged)
            return;

        _emuWorker.SetQuantumPolicy(min, max, seedQuantum: doSeed ? seedQ : null);
        _lastWorkerPolicyMin = min;
        _lastWorkerPolicyMax = max;
        _lastWorkerSpeedKey = key;
        _workerPolicySeeded = true;
    }

    /// <summary>
    /// Host frame pacing on the EE worker only (never Avalonia UI).
    /// Boot race / Unlimited skip WaitFrame even if Options has frame limit ON.
    /// </summary>
    private void SyncWorkerFramePacing()
    {
        bool pace = _frameLimit.Enabled && !ShouldSkipFrameWait();
        _emuWorker.SetFramePacing(_frameLimit, enabled: pace);
    }

    /// <summary>Alias used by boot/run paths that previously called SyncWorkerQuantumPolicy.</summary>
    private void SyncWorkerQuantumPolicy() => ApplyWorkerQuantumPolicy(seed: true);

    /// <summary>Host present string for the game window (may temporarily force Software during Auto boot).</summary>
    private string EffectiveHostPresentMode()
    {
        if (_bootForceSoftwarePresent)
            return "Software";
        return string.IsNullOrWhiteSpace(_presentModeLabel) ? "Software" : _presentModeLabel;
    }

    private static bool IsAutoPresentMode(string? mode) =>
        string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase);

    private void BeginThroughputWindow()
    {
        _throughputSw.Restart();
        _throughputLastMs = 0;
        _throughputLastCycles = _system?.MasterCycles ?? 0;
        _lastCyclesPerSec = 0;
    }

    private void MaybeLogThroughput()
    {
        if (_system == null || !_throughputSw.IsRunning) return;
        long ms = _throughputSw.ElapsedMilliseconds;
        long dt = ms - _throughputLastMs;
        // After boot: every 1s — cyc, px, lit, Mcyc/s (task metrics for Soft-GS bring-up).
        if (dt < 1000) return;

        ulong nowC = _system.MasterCycles;
        ulong dCyc = nowC - _throughputLastCycles;
        double sec = dt / 1000.0;
        _lastCyclesPerSec = sec > 0 ? dCyc / sec : 0;
        _throughputLastMs = ms;
        _throughputLastCycles = nowC;

        int lit = _gameWindow?.LastLitPixels ?? 0;
        if (lit <= 0 && _lastWorkerLit > 0)
            lit = (int)Math.Min(int.MaxValue, _lastWorkerLit);
        Log($"BootMetrics: cyc={nowC:N0}  px={_system.Gs.PixelsWritten:N0}  lit={lit:N0}  " +
            $"{FormatThroughput(_lastCyclesPerSec)}  " +
            $"speed={_currentSpeedMode}  frameLimit={(_frameLimit.Enabled && !ShouldSkipFrameWait() ? "on" : "off")}  " +
            $"workerQ={_emuWorker.Quantum:N0} presentHz={_emuWorker.PresentHz:F0}" +
            (_bootUncappedUntilSoftGs ? "  [boot-race]" : ""));
    }

    private static string FormatThroughput(double cyclesPerSec)
    {
        if (cyclesPerSec <= 0) return "— Mcyc/s";
        if (cyclesPerSec >= 1_000_000)
            return $"{cyclesPerSec / 1_000_000.0:F1} Mcyc/s";
        if (cyclesPerSec >= 1_000)
            return $"{cyclesPerSec / 1_000.0:F0} kcyc/s";
        return $"{cyclesPerSec:F0} cyc/s";
    }

    private void MaybeNoteSoftGsAndEndBootBoost()
    {
        if (_system == null) return;
        long px = _system.Gs.PixelsWritten;
        // Prefer worker snapshot lit (STAB-1) — UI PresentFrame used to race mid-RunFor.
        long litLong = _lastWorkerLit;
        if (litLong <= 0)
            litLong = _emuWorker.LastLit;
        if (litLong <= 0)
            litLong = _gameWindow?.LastLitPixels ?? 0;
        int lit = (int)Math.Min(int.MaxValue, litLong);
        ulong cycles = _system.MasterCycles;

        if (px > 0 && !_loggedFirstSoftGsPx)
        {
            _loggedFirstSoftGsPx = true;
            Log($"Soft-GS first pixels: px={px:N0} lit={lit:N0} at c={cycles:N0} " +
                $"(throughput={FormatThroughput(_lastCyclesPerSec)})");
        }

        if (!_bootUncappedUntilSoftGs) return;

        // End temporary Unlimited / frame-wait skip when Soft-GS chrome is clearly on screen
        // (lit>100) OR after a hard cycle budget (200M) so Unlimited cannot run forever.
        bool litDone = lit > BootLitPixelsDone;
        bool cyclesDone = cycles > BootCyclesCap;
        if (!litDone && !cyclesDone) return;

        EndBootRace(litDone, lit, cycles);
    }

    /// <summary>
    /// Leave temporary Unlimited; restore Normal/Fast (never leave Unlimited as permanent default).
    /// Optional frame limit (60 fps) resumes on the worker thread without freezing UI.
    /// </summary>
    private void EndBootRace(bool litDone, int lit, ulong cycles)
    {
        _bootUncappedUntilSoftGs = false;

        // Restore pre-boot speed only if still on temporary Unlimited we armed.
        // If the user changed speed via Options mid-race, keep their choice.
        bool stillTempUnlimited =
            string.Equals(_currentSpeedMode, "Unlimited", StringComparison.OrdinalIgnoreCase) &&
            _cyclesPerTick >= BootUnlimitedQuantum;
        if (stillTempUnlimited)
        {
            // Prefer Fast/Normal — never persist Unlimited as the post-paint default.
            string restore = string.IsNullOrWhiteSpace(_preBootSpeedMode) ? "Fast" : _preBootSpeedMode;
            if (string.Equals(restore, "Unlimited", StringComparison.OrdinalIgnoreCase))
                restore = "Fast";
            _currentSpeedMode = restore;
            if (_preBootCyclesPerTick > 0 && _preBootCyclesPerTick < BootUnlimitedQuantum)
                _cyclesPerTick = _preBootCyclesPerTick;
            else
                _cyclesPerTick = string.Equals(restore, "Normal", StringComparison.OrdinalIgnoreCase)
                    ? 6_000_000UL
                    : 25_000_000UL; // Fast default
        }

        // Re-seed worker quantum for restored Normal/Fast (adaptive takes over after).
        _lastWorkerSpeedKey = "";
        ApplyWorkerQuantumPolicy(seed: true);

        // Frame limit optional 60fps after lit>100: if Options has it ON, reset phase so we
        // do not sleep for a huge backlog, then pace on the EE worker (UI stays free).
        if (_frameLimit.Enabled)
        {
            _frameLimit.TargetFps = _frameLimit.TargetFps > 0 ? _frameLimit.TargetFps : 60;
            _frameLimit.Reset();
        }
        SyncWorkerFramePacing();

        string reason = litDone
            ? $"lit={lit:N0}>{BootLitPixelsDone}"
            : $"cycles={cycles:N0}>{BootCyclesCap:N0}";
        Log($"Boot race ended ({reason}) — speed={_currentSpeedMode} " +
            $"workerQ={_emuWorker.Quantum:N0} (bounds {_emuWorker.QuantumMin:N0}..{_emuWorker.QuantumMax:N0})  " +
            $"frameLimit={(_frameLimit.Enabled ? $"on {_frameLimit.TargetFps}fps (worker)" : "off")} " +
            "(permanent Options unchanged)");
    }

    private void MaybeEndBootSoftwareForce()
    {
        if (!_bootForceSoftwarePresent) return;
        if (_bootPresentSw.IsRunning && _bootPresentSw.ElapsedMilliseconds < BootSoftwarePresentMs)
            return;

        _bootForceSoftwarePresent = false;
        _bootPresentSw.Reset();
        string mode = EffectiveHostPresentMode();
        _gameWindow?.SetPresentMode(mode);
        Log($"Boot Soft-GS Software window ended (30s) — host present → {mode}");
    }

    private void ArmBootUncappedForCommercial()
    {
        // Snapshot permanent speed so we can restore after lit>100 / 200M (never persist Unlimited).
        // If user was already Unlimited, fall back to Fast for post-paint restore.
        _preBootSpeedMode = _currentSpeedMode;
        if (string.Equals(_preBootSpeedMode, "Unlimited", StringComparison.OrdinalIgnoreCase))
            _preBootSpeedMode = "Fast";
        _preBootCyclesPerTick = _cyclesPerTick;
        if (_preBootCyclesPerTick == 0 || _preBootCyclesPerTick >= BootUnlimitedQuantum)
            _preBootCyclesPerTick = 25_000_000; // Fast

        _bootUncappedUntilSoftGs = true;
        _loggedFirstSoftGsPx = false;
        _lastWorkerLit = 0;
        // Temporary Unlimited — CPU hard until Soft-GS chrome or cycle cap.
        _cyclesPerTick = BootUnlimitedQuantum;
        _currentSpeedMode = "Unlimited";
        BeginThroughputWindow();

        _lastWorkerSpeedKey = "";
        ApplyWorkerQuantumPolicy(seed: true);
        SyncWorkerFramePacing(); // boot race → pacing off even if Options has frame limit ON

        // PresentMode Auto: force Avalonia Software so Soft-GS is visible ASAP; GPU exclusive after 30s.
        string cfgPresent = string.IsNullOrWhiteSpace(_config.PresentMode) ? _presentModeLabel : _config.PresentMode;
        if (IsAutoPresentMode(cfgPresent))
        {
            _bootForceSoftwarePresent = true;
            _bootPresentSw.Restart();
            _gameWindow?.SetPresentMode("Software");
        }
        else
        {
            _bootForceSoftwarePresent = false;
            _bootPresentSw.Reset();
        }

        Log($"Boot race: speed=Unlimited (temp) workerQ={_emuWorker.Quantum:N0} " +
            $"frameLimit={(_frameLimit.Enabled ? "on in Options (skipped until lit>100 or 200M)" : "off")} " +
            $"present={cfgPresent}" +
            (_bootForceSoftwarePresent ? "→Software(30s Soft-GS)" : "") +
            " — restores " + _preBootSpeedMode + " after first paint; Options not rewritten");
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
                _emuWorker.IsRunning = false;
                _gameWindow = null;
                UpdateSidebar();
            };
            _gameWindow.Show(this);
            Log("Opened game display window");
            _sessionLog.Write("GameDisplayWindow shown");
        }
        _gameWindow.SetPresentMode(EffectiveHostPresentMode());
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

    /// <summary>
    /// Prefer <see cref="EmulationWorker.TryGetPresent"/> → <see cref="GameDisplayWindow.PresentSnapshot"/>
    /// for solo play (never sample live Soft-GS mid-RunFor). Live
    /// <see cref="GameDisplayWindow.PresentFrame"/> only when the worker is idle
    /// (pause / step / test scene / netplay lockstep).
    /// </summary>
    private void PresentToGameWindow()
    {
        if (_system == null) return;
        if (_gameWindow == null) return;
        try
        {
            bool netplayActive = _netplay != null && _netplay.Running
                && _netplayTransport != null && _netplayTransport.IsConnected;
            bool workerDriving = _emuWorker.IsRunning && _emuWorker.IsWorkerAlive && !netplayActive;

            // Prefer snapshot path first — UI must not touch live GS while EE runs on worker.
            bool usedSnapshot = false;
            ulong statusPc = 0;
            ulong statusCycles = 0;
            long statusPx = 0;
            long statusLit = _lastWorkerLit;
            if (_emuWorker.TryGetPresent(out var pixels, out int w, out int h,
                    out long px, out long lit, out ulong cycles, out ulong pc, out int gifP3))
            {
                _lastWorkerLit = lit;
                statusPc = pc;
                statusCycles = cycles;
                statusPx = px;
                statusLit = lit;
                if (workerDriving)
                {
                    _gameWindow.PresentSnapshot(pixels.Span, w, h, px, cycles, pc, gifP3, litHint: lit);
                    usedSnapshot = true;
                }
            }

            if (!usedSnapshot && !workerDriving)
            {
                // Worker idle: safe to sample live Soft-GS (step, pause, test scene, netplay).
                _gameWindow.PresentFrame(_system);
                _lastWorkerLit = _gameWindow.LastLitPixels;
                statusPc = _system.EE.PC;
                statusCycles = _system.MasterCycles;
                statusPx = _system.Gs.PixelsWritten;
                statusLit = _gameWindow.LastLitPixels;
            }
            else if (!usedSnapshot && workerDriving)
            {
                // Solo + worker active but no snap yet — skip live present (would race mid-RunFor).
                statusPc = _system.EE.PC;
                statusCycles = _system.MasterCycles;
                statusPx = _system.Gs.PixelsWritten;
                statusLit = _lastWorkerLit;
            }

            // Status chrome: update ~4×/s (not every UI tick) to cut string/UI churn.
            // Prefer snapshot metrics when available so we do not chase mid-RunFor GS.
            if ((_detailLogCounter % 15) == 0)
            {
                _gameWindow.SetStatus(
                    $"PC=0x{statusPc:X8}  c={statusCycles:N0}  " +
                    $"{FormatThroughput(_lastCyclesPerSec)}  px={statusPx:N0}  " +
                    $"lit={statusLit:N0}  q={_emuWorker.Quantum:N0}  " +
                    $"Hz={_emuWorker.PresentHz:F0}  present={_gameWindow.HostPresentName}  " +
                    $"ee={_emuWorker.Status}");
            }
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
        _emuWorker.PauseAndWait();
        ClearBootRaceState(restoreSpeed: true);
        _throughputSw.Reset();
        CloseGameWindow();
        Log("Emulation stopped; game window closed");
        _sessionLog.WriteSystemSnapshot(_system, "stop");
        UpdateStatus("Stopped");
        UpdateSidebar();
    }

    /// <summary>Clear temporary boot-race flags without writing Options/config.</summary>
    private void ClearBootRaceState(bool restoreSpeed)
    {
        if (restoreSpeed && _bootUncappedUntilSoftGs)
        {
            string restore = string.IsNullOrWhiteSpace(_preBootSpeedMode) ? "Fast" : _preBootSpeedMode;
            if (string.Equals(restore, "Unlimited", StringComparison.OrdinalIgnoreCase))
                restore = "Fast";
            _currentSpeedMode = restore;
            _cyclesPerTick = _preBootCyclesPerTick > 0 && _preBootCyclesPerTick < BootUnlimitedQuantum
                ? _preBootCyclesPerTick
                : (string.Equals(restore, "Normal", StringComparison.OrdinalIgnoreCase) ? 6_000_000UL : 25_000_000UL);
        }
        _bootUncappedUntilSoftGs = false;
        _bootForceSoftwarePresent = false;
        _bootPresentSw.Reset();
        _loggedFirstSoftGsPx = false;
        _lastWorkerSpeedKey = "";
        ApplyWorkerQuantumPolicy(seed: true);
        SyncWorkerFramePacing();
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

    /// <summary>
    /// Pad poll stays on the UI thread (keyboard + HostGamepad). Race note: the EE worker
    /// may read Pad.Buttons mid-slice while UI writes Press/Release — at most a 1-frame tear
    /// of button bits. Acceptable for solo play; netplay freezes worker so pad is UI-owned.
    /// Do not lock Pad from both sides without measuring: pad reads are hot in SIO2.
    /// </summary>
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
        // Permanent Options → runtime mirrors. Boot race only overrides WaitFrame / quantum /
        // Auto→Software host present; never clear DefaultFrameLimit or rewrite speed into config.
        _frameLimit.Enabled = _config.DefaultFrameLimit;
        _frameLimit.TargetFps = _config.DefaultTargetFps > 0 ? _config.DefaultTargetFps : 60;
        // Default Software: Avalonia Soft-GS blit is reliable; GPU is optional via Options.
        _presentModeLabel = string.IsNullOrWhiteSpace(_config.PresentMode) ? "Software" : _config.PresentMode;
        _runAhead.Frames = Math.Clamp(_config.RunAheadFrames, 0, 4);
        if (_system != null)
        {
            _system.UseJit = _config.EnableJit;
            _system.EeJit.Enabled = _config.EnableJit;
            _system.Present.UseSoftware();
        }
        _gamepads.ApplyConfigBindings(_config);
        // EffectiveHostPresentMode keeps Soft-GS Avalonia during Auto boot force window.
        _gameWindow?.SetPresentMode(EffectiveHostPresentMode());
        SyncWorkerFramePacing();
        ApplyWorkerQuantumPolicy(seed: false);
    }

    /// <summary>
    /// Drain core-produced samples (no host clock drives the core).
    /// Full OS audio device playback can plug into the same ring later.
    /// </summary>
    private void DrainAudioMeter()
    {
        // Host device pump (Phase 43) — skip OS pump when host audio disabled
        if (!_config.EnableHostAudio)
        {
            // Still drain the ring so it does not back up
            _audioSink.Drain(stackalloc short[4096]);
            return;
        }
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
        // Cycles/FPS chrome removed from main — status line is updated via UpdateStatus/UpdateSidebar.
    }

    private void UpdateSidebar()
    {
        // Status sidebar removed from main; thin status line only on notable state.
        if (_system == null) return;
        if (_system.Debugger.Halted)
            UpdateStatus("Breakpoint");
        else if (_isRunning && StatusText != null &&
                 (StatusText.Text == null || !StatusText.Text.StartsWith("Running", StringComparison.Ordinal)))
            UpdateStatus($"Running — {_lastBootMessage}");
    }

    private void UpdateLibraryStatusTexts()
    {
        // Library path labels live under Options → General now.
    }

    private void UpdateStatus(string message)
    {
        if (StatusText != null)
            StatusText.Text = message;
    }

    private void OpenOptions(string category)
    {
        try
        {
            if (_optionsWindow != null)
            {
                _optionsWindow.SelectCategory(category);
                _optionsWindow.Activate();
                return;
            }

            _optionsWindow = new OptionsWindow(this, category);
            _optionsWindow.Closed += (_, __) =>
            {
                // Apply virtual HDD / JIT / run-ahead when options closes
                ApplyVirtualHddFromConfig();
                ApplyConfigToUi();
                ApplyHostAudioFromConfig();
                _optionsWindow = null;
                RefreshLibraryList();
            };
            _optionsWindow.Show(this);
        }
        catch (Exception ex)
        {
            CrashLog.Write("open options", ex);
            Log("Could not open Options: " + ex.Message);
        }
    }

    private void ApplyVirtualHddFromConfig()
    {
        if (_system == null) return;
        if (_config.EnableVirtualHdd && !string.IsNullOrEmpty(_config.VirtualHddPath))
        {
            bool ok = _system.TryEnableVirtualHdd(_config.VirtualHddPath, _config.VirtualHddSizeMb * 1024L * 1024L);
            Log(ok
                ? $"Virtual HDD enabled: {_config.VirtualHddPath}"
                : "Virtual HDD failed to open/create — check the path");
        }
        else
        {
            _system.DisableVirtualHdd();
        }
    }

    /// <summary>Open/close host audio device from <see cref="EmulatorConfig.EnableHostAudio"/>.</summary>
    private void ApplyHostAudioFromConfig()
    {
        try
        {
            if (_config.EnableHostAudio)
            {
                if (_hostAudio == null || !_hostAudio.IsOpen)
                {
                    _hostAudio ??= HostAudioFactory.CreateDefault();
                    _hostAudio.Open(48000);
                }
            }
            else if (_hostAudio != null && _hostAudio.IsOpen)
            {
                _hostAudio.Close();
            }
        }
        catch (Exception ex)
        {
            Log("Host audio apply: " + ex.Message);
        }
    }

    private void OnOptionsGeneralClick(object? sender, RoutedEventArgs e) => OpenOptions("General");
    private void OnOptionsGraphicsClick(object? sender, RoutedEventArgs e) => OpenOptions("Graphics");
    private void OnOptionsControllersClick(object? sender, RoutedEventArgs e) => OpenOptions("Controllers");
    private void OnOptionsEmulationClick(object? sender, RoutedEventArgs e) => OpenOptions("Emulation");
    private void OnOptionsAudioClick(object? sender, RoutedEventArgs e) => OpenOptions("Audio");
    private void OnOptionsMetadataClick(object? sender, RoutedEventArgs e) => OpenOptions("Metadata");
    private void OnOptionsAdvancedClick(object? sender, RoutedEventArgs e) => OpenOptions("Advanced");

    /// <summary>Legacy entry point — opens Options at General (migrated from Settings dialog).</summary>
    private void OnSettingsClick(object? sender, RoutedEventArgs e) => OpenOptions("General");

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
        string? path = GetSelectedGamePath();
        if (path != null)
            _ = BootMediaPathAsync(path, autoRun: _config.AutoRunAfterBoot);
        else
            Log("Select a game in the media library first");
    }

    private void OnLibraryDoubleTapped(object? sender, TappedEventArgs e)
    {
        string? path = GetSelectedGamePath();
        if (path != null)
            _ = BootMediaPathAsync(path, autoRun: _config.AutoRunAfterBoot);
    }

    private string? GetSelectedGamePath() =>
        LibraryList?.SelectedItem switch
        {
            LibraryItemVm vm => vm.Game.Path,
            GameSettings g => g.Path,
            _ => null
        };

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
            // Cold-start each boot (CLI blocker-trace does the same). Reusing a dirty
            // Ps2System left MasterCycles/GS/DMA from the previous title and made second
            // boots in one Desktop session show px=0 forever (session-20260731-095158).
            // Detach waits for in-flight worker RunFor so the old system is idle.
            _isRunning = false;
            _emuWorker.Detach();
            _system = new Ps2System();
            _system.SetAudioSink(_audioSink);
            _emuWorker.Attach(_system);

            // Always bring up native commercial HLE — no BIOS dump required on Desktop.
            _system.LoadBiosNative();

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
            Log("Note: DetPS2 fast-boots the disc ELF with native HLE (no BIOS dump).");
            string quirkName = _system.ActiveQuirk?.DisplayName
                ?? _system.ActiveQuirk?.Serial
                ?? "(none)";
            Log($"Active quirk: {quirkName}  MidwayAssist={_system.MidwayAssist.Status}");
            // Deception / DA / Arm: MidwayFamilyAssist.Step + OnHostPresent run every UI tick (mkFamHot slices).
            if (_system.ActiveQuirk is MidwayFamilyAssist)
                Log("Deception-friendly path: MidwayFamilyAssist ActiveQuirk Step/OnHostPresent armed (SN/PAD/menu gates)");
            Log("Audio: host output is live when SPU2 voices produce samples (no test tone).");
            // Boot logos / Sofdec must come from Soft-GS (IPU/CRI). Host FFmpeg decode was removed.
            if (!_system.MidwayAssist.FramesReady)
                Log("No host FMV overlay — logos only if Soft-GS renders them (IPU/CRI).");
            Log("Tip: auto-run boot race = Unlimited 50M/tick, frame wait skipped until lit>100 or 200M cycles (Options not rewritten).");
            if (autoRun)
            {
                ArmBootUncappedForCommercial();
                EnsureGameWindow(Path.GetFileNameWithoutExtension(path));
                // Re-apply effective present after window exists (Auto→Software force).
                _gameWindow?.SetPresentMode(EffectiveHostPresentMode());
                _workerPolicySeeded = false; // re-seed after boot race arm
                SyncWorkerQuantumPolicy();
                _isRunning = true;
                _emuWorker.IsRunning = true;
                UpdateStatus("Running...");
                Log("Game window open — EE on background thread (UI responsive). Pause F6 / Stop F9");
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
        if (_system != null)
            _emuWorker.Attach(_system);
        SyncWorkerQuantumPolicy();
        _isRunning = true;
        _emuWorker.IsRunning = true;
        BeginThroughputWindow();
        Log("Emulation started / resumed (background EE worker; UI present via snapshot)");
        _sessionLog.WriteSystemSnapshot(_system, "run");
        UpdateStatus("Running...");
        UpdateSidebar();
    }

    private void OnPauseClick(object? sender, RoutedEventArgs e)
    {
        _isRunning = false;
        _emuWorker.PauseAndWait();
        _throughputSw.Reset();
        // One live present so pause frame is current Soft-GS.
        PresentToGameWindow();
        Log("Emulation paused");
        _sessionLog.WriteSystemSnapshot(_system, "pause");
        UpdateStatus("Paused");
        UpdateSidebar();
    }

    private void OnStepClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        // Step is debug-only on UI: worker must be idle so RunFor is exclusive.
        _isRunning = false;
        _emuWorker.PauseAndWait();
        _system.RunFor(1_000_000);
        EnsureGameWindow(_currentGameTitle);
        PresentToGameWindow();
        UpdateStatusText();
        UpdateSidebar();
        Log("Stepped 1M cycles (UI thread; worker paused)");
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _isRunning = false;
        _emuWorker.PauseAndWait();
        _system.Reset();
        _audioSink.Reset();
        _lastLoggedAudioSamples = 0;
        _system.SetAudioSink(_audioSink);
        ClearBootRaceState(restoreSpeed: true);
        _throughputSw.Reset();
        CloseGameWindow();
        UpdateStatusText();
        UpdateSidebar();
        Log("System reset");
        UpdateStatus("Reset");
    }

    private void OnTestDrawClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        _emuWorker.PauseAndWait();
        _system.Gs.RenderTestScene();
        EnsureGameWindow("Test scene");
        PresentToGameWindow();
        Log("Test scene shown in game window");
    }

    private async void OnSaveStateClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        bool wasRunning = _isRunning;
        _isRunning = false;
        _emuWorker.PauseAndWait();
        var file = await this.StorageProvider.SaveFilePickerAsync(new() { Title = "Save State", DefaultExtension = ".dps2", FileTypeChoices = new[] { new FilePickerFileType("DetPS2 Save State") { Patterns = new[] { "*.dps2" } } } });
        if (file != null)
        {
            try
            {
                byte[] data = _system.SaveState();
                await File.WriteAllBytesAsync(file.Path.LocalPath, data);
                Log("State saved");
            }
            catch { Log("Save error"); }
        }
        if (wasRunning)
        {
            _isRunning = true;
            _emuWorker.IsRunning = true;
        }
    }

    private async void OnLoadStateClick(object? sender, RoutedEventArgs e)
    {
        if (_system == null) return;
        bool wasRunning = _isRunning;
        _isRunning = false;
        _emuWorker.PauseAndWait();
        var files = await this.StorageProvider.OpenFilePickerAsync(new() { Title = "Load State", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("DetPS2 State") { Patterns = new[] { "*.dps2" } } } });
        if (files.Count > 0)
        {
            try
            {
                byte[] data = await File.ReadAllBytesAsync(files[0].Path.LocalPath);
                _system.LoadState(data);
                UpdateFramebuffer();
                UpdateSidebar();
                Log("State loaded");
            }
            catch { Log("Load error"); }
        }
        if (wasRunning)
        {
            _isRunning = true;
            _emuWorker.IsRunning = true;
        }
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
        SyncWorkerFramePacing();
        Log(_frameLimit.Enabled
            ? $"Frame limit ON ({_frameLimit.TargetFps} FPS, EE worker — UI free)"
            : "Frame limit OFF");
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
        EnqueueLibraryMetadataRefresh();
        Log($"Library path set: {path}");
        Log($"Found {games.Count} media file(s) (.iso / .elf / .bin / .cso)");
        Log($"Saved to {ConfigPath}");
        UpdateStatus($"Library: {games.Count} titles");
    }

    private async void OnControllersClick(object? sender, RoutedEventArgs e)
    {
        bool applied = await Options.OptionsControllersPage.ShowAsDialogAsync(this, _config, _gamepads);
        if (!applied) return;
        _gamepads.ApplyConfigBindings(_config);
        PersistConfig();
        Log($"P1: {_config.Player1DeviceId} profile={_config.Player1Profile}");
        Log($"P2: {_config.Player2DeviceId} profile={_config.Player2Profile}");
        if (_config.Player1Profile == "GuitarHero" || _config.Player2Profile == "GuitarHero")
            Log("Guitar Hero map: frets→R2/○/△/✕/□, strum→D-pad U/D, whammy→R-stick Y");
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
        stack.Children.Add(new TextBlock { Text = "Media library · native HLE · deterministic core", TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock
        {
            Text = "Choose a media folder (saved in AppData). Select an ISO and Boot.\nCommercial services use built-in native HLE — no BIOS dump required.",
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