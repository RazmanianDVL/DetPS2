using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DetPS2.Core;
using DetPS2.Core.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DetPS2.Desktop.Options;

/// <summary>
/// Options → Controllers page: device pick (P1/P2), Standard / GuitarHero profiles,
/// and per-<see cref="PadInput.Button"/> host remaps with capture dialog.
///
/// <para><b>Hosting from UI-1 <c>OptionsWindow</c></b></para>
/// <list type="number">
/// <item>Category list already has <c>Tag="Controllers"</c>.</item>
/// <item><c>ShowCategory("Controllers")</c> sets
/// <c>ContentHost.Content = new OptionsControllersPage(host.Config, host.Gamepads)</c>.</item>
/// <item>On leave Controllers / window Close: call <see cref="ApplyToConfig"/> then
/// <see cref="IOptionsHost.PersistConfig"/>.</item>
/// <item>Standalone (no shell): <see cref="ShowAsDialogAsync"/>.</item>
/// <item>Keyboard remaps: <see cref="ButtonBindings"/> (pad name → host code) or
/// <see cref="ApplyToInputMapper"/>. Device poll remains MainWindow / PAD-1.</item>
/// <item>UI-local pad-name→host-code dictionary (<see cref="ExportBindings"/> /
/// <see cref="ImportBindings"/>) is converted to a real core
/// <see cref="InputBindingTable"/> in <see cref="BuildBindingTableFromUi"/> and persisted
/// via <see cref="EmulatorConfig.SetPlayer1Bindings"/> / <c>SetPlayer2Bindings</c>.</item>
/// </list>
/// </summary>
public partial class OptionsControllersPage : UserControl
{
    private readonly HostGamepadService _gamepads;
    private EmulatorConfig _config;
    private readonly ObservableCollection<BindingRow> _rows = new();

    /// <summary>
    /// Pad button name → host binding code (e.g. "Cross" → "Z", or "Pad:A").
    /// Local until InputBindingTable is available in core.
    /// </summary>
    private readonly Dictionary<string, string> _bindings =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after Apply writes device/profile into the bound config.</summary>
    public event EventHandler? Applied;

    public OptionsControllersPage()
        : this(new EmulatorConfig(), null)
    {
    }

    public OptionsControllersPage(EmulatorConfig config, HostGamepadService? gamepads = null)
    {
        InitializeComponent();
        _config = config ?? new EmulatorConfig();
        _gamepads = gamepads ?? new HostGamepadService();

        FillProfiles(Player1ProfileBox);
        FillProfiles(Player2ProfileBox);
        ImportBindings(PadButtonCatalog.DefaultPadToHost());
        RebuildBindingRows();
        LoadFromConfig(_config);
        RefreshDevices();
    }

    /// <summary>Current pad→host map (copy).</summary>
    public IReadOnlyDictionary<string, string> ButtonBindings =>
        new Dictionary<string, string>(_bindings, StringComparer.OrdinalIgnoreCase);

    /// <summary>Config instance this page last applied to / loaded from.</summary>
    public EmulatorConfig Config => _config;

    public void LoadFromConfig(EmulatorConfig config)
    {
        _config = config ?? new EmulatorConfig();
        _config.MigrateGamepadIds();
        SelectDevice(Player1DeviceBox, _config.Player1DeviceId);
        SelectDevice(Player2DeviceBox, _config.Player2DeviceId);
        SelectProfile(Player1ProfileBox, _config.Player1Profile);
        SelectProfile(Player2ProfileBox, _config.Player2Profile);
        SetStatus($"Loaded P1={_config.Player1DeviceId} / P2={_config.Player2DeviceId}");
    }

    /// <summary>
    /// Writes P1/P2 device ids + profiles into <paramref name="config"/> (or the bound config)
    /// and keeps legacy <see cref="EmulatorConfig.Player1Gamepad"/> ints in sync.
    /// Does not call Save — host owns persistence path.
    /// </summary>
    public void ApplyToConfig(EmulatorConfig? config = null)
    {
        var cfg = config ?? _config;
        cfg.MigrateGamepadIds();
        cfg.Player1DeviceId = GetSelectedDeviceId(Player1DeviceBox) ?? "kb";
        cfg.Player2DeviceId = GetSelectedDeviceId(Player2DeviceBox) ?? "kb";
        cfg.Player1Profile = GetSelectedProfile(Player1ProfileBox) ?? "Standard";
        cfg.Player2Profile = GetSelectedProfile(Player2ProfileBox) ?? "Standard";
        cfg.Player1Gamepad = ParseXiIndex(cfg.Player1DeviceId);
        cfg.Player2Gamepad = ParseXiIndex(cfg.Player2DeviceId);

        // Persist remaps into PAD-1 InputBindingTable entries (host Source → pad Target).
        cfg.SetPlayer1Bindings(BuildBindingTableFromUi(cfg.Player1DeviceId, cfg.Player1Profile));

        _config = cfg;
        SetStatus($"Applied P1={cfg.Player1DeviceId} ({cfg.Player1Profile}), P2={cfg.Player2DeviceId} ({cfg.Player2Profile})");
        Applied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Convert pad-name → host-code dictionary into an <see cref="InputBindingTable"/>
    /// overlay (keyboard <c>kb:Z</c>, XInput raw via capture, etc.).
    /// </summary>
    private InputBindingTable BuildBindingTableFromUi(string deviceId, string profileName)
    {
        var kind = deviceId.StartsWith("xi:", StringComparison.OrdinalIgnoreCase)
            ? ControllerHardwareKind.XInput
            : deviceId.StartsWith("hid:", StringComparison.OrdinalIgnoreCase)
                ? ControllerHardwareKind.DualShock4
                : ControllerHardwareKind.Keyboard;
        var profile = EmulatorConfig.ParseProfile(profileName);
        var table = DefaultInputMaps.Resolve(kind, profile).Clone();

        foreach (var (padName, host) in _bindings)
        {
            if (string.IsNullOrWhiteSpace(host)) continue;
            if (!Enum.TryParse(padName, ignoreCase: true, out PadInput.Button btn) || btn == PadInput.Button.None)
                continue;

            string source = NormalizeHostSource(host, kind);
            if (string.IsNullOrEmpty(source)) continue;

            // Capture gave us the host control; bind it to this pad face.
            table.UnbindTarget(btn);
            // Also drop this source's previous target so one key isn't dual-bound.
            table.RemoveBySource(source);
            table.Bind(source, btn);
        }

        table.Name = "Custom";
        table.Profile = profile;
        return table;
    }

    private static string NormalizeHostSource(string host, ControllerHardwareKind kind)
    {
        host = host.Trim();
        if (host.StartsWith("kb:", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("xi:", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("ds:", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("hid:", StringComparison.OrdinalIgnoreCase))
            return host;

        // Keyboard capture returns bare "Z" / "Enter"
        if (host.StartsWith("Pad:", StringComparison.OrdinalIgnoreCase))
        {
            // Gamepad capture currently reports emulated pad face; map common faces to XInput.
            string face = host.Substring(4);
            return face.ToLowerInvariant() switch
            {
                "cross" => HostSources.XiA,
                "circle" => HostSources.XiB,
                "square" => HostSources.XiX,
                "triangle" => HostSources.XiY,
                "l1" => HostSources.XiLB,
                "r1" => HostSources.XiRB,
                "l2" => HostSources.XiLT,
                "r2" => HostSources.XiRT,
                "start" => HostSources.XiStart,
                "select" => HostSources.XiBack,
                "up" => HostSources.XiDPadUp,
                "down" => HostSources.XiDPadDown,
                "left" => HostSources.XiDPadLeft,
                "right" => HostSources.XiDPadRight,
                "l3" => HostSources.XiLS,
                "r3" => HostSources.XiRS,
                _ => kind == ControllerHardwareKind.Keyboard ? HostSources.Keyboard(face) : "xi:" + face
            };
        }

        return HostSources.Keyboard(host);
    }

    /// <summary>
    /// Push keyboard remaps into an <see cref="InputMapper"/> (host key → pad button).
    /// Clears defaults first so unbound pad faces stay unbound.
    /// </summary>
    public void ApplyToInputMapper(InputMapper mapper)
    {
        if (mapper == null) return;
        mapper.ResetDefaults();
        // Invert: our map is pad→host; InputMapper is host→pad.
        // Re-bind every host code we know about.
        foreach (var (padName, host) in _bindings)
        {
            if (string.IsNullOrWhiteSpace(host) || host.StartsWith("Pad:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Enum.TryParse(padName, ignoreCase: true, out PadInput.Button btn) || btn == PadInput.Button.None)
                continue;
            mapper.Bind(host, btn);
        }
    }

    public Dictionary<string, string> ExportBindings() =>
        new(_bindings, StringComparer.OrdinalIgnoreCase);

    public void ImportBindings(IDictionary<string, string>? map)
    {
        _bindings.Clear();
        if (map == null)
        {
            foreach (var kv in PadButtonCatalog.DefaultPadToHost())
                _bindings[kv.Key] = kv.Value;
        }
        else
        {
            foreach (var kv in map)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                    _bindings[kv.Key] = kv.Value;
            }
            // Ensure every pad button has a row entry
            foreach (var btn in PadButtonCatalog.All)
            {
                string name = btn.ToString();
                if (!_bindings.ContainsKey(name))
                    _bindings[name] = "";
            }
        }
        RebuildBindingRows();
    }

    public void RefreshDevices()
    {
        string? keep1 = GetSelectedDeviceId(Player1DeviceBox) ?? _config.Player1DeviceId;
        string? keep2 = GetSelectedDeviceId(Player2DeviceBox) ?? _config.Player2DeviceId;

        FillDevices(Player1DeviceBox);
        FillDevices(Player2DeviceBox);
        SelectDevice(Player1DeviceBox, keep1);
        SelectDevice(Player2DeviceBox, keep2);
        SetStatus($"Devices refreshed ({Player1DeviceBox?.Items.Count ?? 0})");
    }

    /// <summary>
    /// Show this page in a modal window. Returns true if the user clicked Apply.
    /// </summary>
    public static async Task<bool> ShowAsDialogAsync(
        Window owner,
        EmulatorConfig config,
        HostGamepadService? gamepads = null,
        string title = "Controllers")
    {
        bool applied = false;
        var page = new OptionsControllersPage(config, gamepads);
        var win = new Window
        {
            Title = title,
            Width = 640,
            Height = 620,
            MinWidth = 480,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = page
        };

        page.Applied += (_, __) =>
        {
            applied = true;
            win.Close();
        };

        // Also close on Apply button: Applied already fires; keep Cancel via window chrome.
        await win.ShowDialog(owner);
        return applied;
    }

    private void OnRefreshDevicesClick(object? sender, RoutedEventArgs e) => RefreshDevices();

    private void OnResetBindingsClick(object? sender, RoutedEventArgs e)
    {
        ImportBindings(PadButtonCatalog.DefaultPadToHost());
        SetStatus("Key bindings reset to defaults");
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e) => ApplyToConfig();

    private async void OnRebindRow(BindingRow row)
    {
        var owner = this.GetVisualRoot() as Window;
        string deviceId = GetSelectedDeviceId(Player1DeviceBox) ?? "kb";
        var dlg = new BindingCaptureDialog(row.PadButtonName, _gamepads, deviceId);
        if (owner != null)
            await dlg.ShowDialog(owner);
        else
        {
            var tcs = new TaskCompletionSource();
            dlg.Closed += (_, __) => tcs.TrySetResult();
            dlg.Show();
            await tcs.Task;
        }

        if (dlg.Result == null)
        {
            SetStatus("Capture cancelled or timed out");
            return;
        }

        _bindings[row.PadButtonName] = dlg.Result.HostCode;
        row.HostCode = dlg.Result.HostCode;
        row.HostDisplay = dlg.Result.Display;
        SetStatus($"Bound {row.PadButtonName} ← {dlg.Result.Display}");
    }

    private void RebuildBindingRows()
    {
        _rows.Clear();
        foreach (var btn in PadButtonCatalog.All)
        {
            string name = btn.ToString();
            _bindings.TryGetValue(name, out string? host);
            host ??= "";
            var row = new BindingRow(name, host, OnRebindRow);
            _rows.Add(row);
        }
        if (BindingList != null)
            BindingList.ItemsSource = _rows;
    }

    private static void FillProfiles(ComboBox? box)
    {
        if (box == null) return;
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = "Standard (DualShock-style)", Tag = "Standard" });
        box.Items.Add(new ComboBoxItem { Content = "Guitar Hero / Riffmaster", Tag = "GuitarHero" });
        box.SelectedIndex = 0;
    }

    private void FillDevices(ComboBox? box)
    {
        if (box == null) return;
        box.Items.Clear();
        IReadOnlyList<GamepadDeviceInfo> devices;
        try { devices = _gamepads.Enumerate(); }
        catch { devices = Array.Empty<GamepadDeviceInfo>(); }

        if (devices.Count == 0)
        {
            box.Items.Add(new ComboBoxItem
            {
                Content = "Keyboard only",
                Tag = "kb"
            });
        }
        else
        {
            foreach (var d in devices)
            {
                string conn = d.Connected ? "" : " (empty)";
                string label = $"[{d.Kind}] {d.Name}{conn}";
                box.Items.Add(new ComboBoxItem { Content = label, Tag = d.Id });
            }
        }
        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }

    private static void SelectDevice(ComboBox? box, string? id)
    {
        if (box == null) return;
        id ??= "kb";
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem cbi && cbi.Tag is string t &&
                string.Equals(t, id, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static void SelectProfile(ComboBox? box, string? profile)
    {
        if (box == null) return;
        profile = string.IsNullOrWhiteSpace(profile) ? "Standard" : profile;
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem cbi && cbi.Tag is string t &&
                string.Equals(t, profile, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string? GetSelectedDeviceId(ComboBox? box)
    {
        if (box?.SelectedItem is ComboBoxItem cbi && cbi.Tag is string t) return t;
        return null;
    }

    private static string? GetSelectedProfile(ComboBox? box)
    {
        if (box?.SelectedItem is ComboBoxItem cbi && cbi.Tag is string t) return t;
        return null;
    }

    private static int ParseXiIndex(string deviceId)
    {
        if (deviceId != null && deviceId.StartsWith("xi:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(deviceId.AsSpan(3), out int i))
            return i;
        return -1;
    }

    private void SetStatus(string text)
    {
        if (StatusText != null)
            StatusText.Text = text;
    }

    private sealed class BindingRow : INotifyPropertyChanged
    {
        private string _hostCode;
        private string _hostDisplay;

        public string PadButtonName { get; }
        public ICommand RebindCommand { get; }

        public string HostCode
        {
            get => _hostCode;
            set { if (_hostCode != value) { _hostCode = value; OnPropertyChanged(); } }
        }

        public string HostDisplay
        {
            get => _hostDisplay;
            set { if (_hostDisplay != value) { _hostDisplay = value; OnPropertyChanged(); } }
        }

        public BindingRow(string padButtonName, string hostCode, Action<BindingRow> onRebind)
        {
            PadButtonName = padButtonName;
            _hostCode = hostCode ?? "";
            _hostDisplay = string.IsNullOrEmpty(hostCode) ? "(unbound)" : hostCode;
            RebindCommand = new RelayCommand(() => onRebind(this));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action _exec;
        public RelayCommand(Action exec) => _exec = exec;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _exec();
#pragma warning disable CS0067
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    }
}
