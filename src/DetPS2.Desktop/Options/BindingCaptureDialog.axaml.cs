using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DetPS2.Core;
using System;
using System.Collections.Generic;

namespace DetPS2.Desktop.Options;

/// <summary>
/// Modal capture of a host key or gamepad button for pad remapping.
/// Listens for <see cref="KeyDown"/> and polls <see cref="HostGamepadService"/>;
/// times out after <see cref="TimeoutSeconds"/> (default 5).
/// </summary>
public partial class BindingCaptureDialog : Window
{
    public const double TimeoutSeconds = 5.0;

    private readonly HostGamepadService _gamepads;
    private readonly string? _deviceId;
    private readonly PadInput _scratch = new();
    private readonly DispatcherTimer _timer;
    private readonly DateTime _deadline;
    private uint _baselineButtons;
    private bool _finished;

    /// <summary>Non-null when the user pressed something before timeout/cancel.</summary>
    public BindingCaptureResult? Result { get; private set; }

    /// <summary>XAML / designer ctor — prefer the overload with pad button name.</summary>
    public BindingCaptureDialog()
        : this("Button", null, null)
    {
    }

    public BindingCaptureDialog(
        string padButtonName,
        HostGamepadService? gamepads = null,
        string? deviceId = null)
    {
        InitializeComponent();
        _gamepads = gamepads ?? new HostGamepadService();
        _deviceId = string.IsNullOrWhiteSpace(deviceId) || deviceId == "kb" ? null : deviceId;
        _deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);

        if (TargetText != null)
            TargetText.Text = $"Rebind PS2 button: {padButtonName}";
        if (PromptText != null)
            PromptText.Text = "Press a key or gamepad button…";

        KeyDown += OnKeyDownCapture;
        Opened += OnOpened;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // Snapshot current gamepad mask so held buttons are ignored until release + press.
        _baselineButtons = ReadPadButtons();
        Focus();
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_finished) return;

        double remaining = (_deadline - DateTime.UtcNow).TotalSeconds;
        if (CountdownText != null)
            CountdownText.Text = remaining > 0 ? $"{remaining:0.0}s" : "0.0s";

        if (remaining <= 0)
        {
            Finish(null);
            return;
        }

        uint now = ReadPadButtons();
        // Edge: bits that are down now but were not in the baseline.
        uint pressed = now & ~_baselineButtons;
        if (pressed == 0)
        {
            // Allow baseline to drop as user releases held buttons.
            _baselineButtons &= now;
            return;
        }

        // Prefer lowest set bit as the captured digital face/d-pad control.
        foreach (var btn in PadButtonCatalog.All)
        {
            uint bit = (uint)btn;
            if ((pressed & bit) == 0) continue;
            string name = btn.ToString();
            Finish(new BindingCaptureResult(
                HostCode: "Pad:" + name,
                Display: "Gamepad " + name,
                Source: BindingCaptureSource.Gamepad));
            return;
        }
    }

    private uint ReadPadButtons()
    {
        if (_deviceId == null) return 0;
        _scratch.Reset();
        if (!_gamepads.PollInto(_scratch, _deviceId, ControllerProfile.Standard))
            return 0;
        return _scratch.Buttons;
    }

    private void OnKeyDownCapture(object? sender, KeyEventArgs e)
    {
        if (_finished) return;
        if (e.Key is Key.Escape)
        {
            Finish(null);
            e.Handled = true;
            return;
        }

        // Ignore pure modifiers as bindings (user can still bind Shift via Left/Right Shift names).
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
        {
            return;
        }

        string code = KeyToCode(e.Key);
        if (string.IsNullOrEmpty(code)) return;

        Finish(new BindingCaptureResult(
            HostCode: code,
            Display: code,
            Source: BindingCaptureSource.Keyboard));
        e.Handled = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Finish(null);

    private void Finish(BindingCaptureResult? result)
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        Result = result;
        Close();
    }

    /// <summary>Stable host key name used as a binding string (matches InputMapper / MainWindow style).</summary>
    public static string KeyToCode(Key key) => key switch
    {
        Key.Enter or Key.Return => "Enter",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.Space => "Space",
        Key.Escape => "Escape",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.D0 => "0",
        Key.D1 => "1",
        Key.D2 => "2",
        Key.D3 => "3",
        Key.D4 => "4",
        Key.D5 => "5",
        Key.D6 => "6",
        Key.D7 => "7",
        Key.D8 => "8",
        Key.D9 => "9",
        Key.NumPad0 => "Num0",
        Key.NumPad1 => "Num1",
        Key.NumPad2 => "Num2",
        Key.NumPad3 => "Num3",
        Key.NumPad4 => "Num4",
        Key.NumPad5 => "Num5",
        Key.NumPad6 => "Num6",
        Key.NumPad7 => "Num7",
        Key.NumPad8 => "Num8",
        Key.NumPad9 => "Num9",
        Key.Add => "NumAdd",
        Key.Subtract => "NumSub",
        Key.Multiply => "NumMul",
        Key.Divide => "NumDiv",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        _ when key >= Key.A && key <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        _ when key >= Key.F1 && key <= Key.F12 => "F" + (1 + (key - Key.F1)),
        _ => key.ToString()
    };
}

public enum BindingCaptureSource
{
    Keyboard,
    Gamepad
}

/// <summary>Result of a successful capture (null Result on dialog = cancel/timeout).</summary>
public sealed record BindingCaptureResult(string HostCode, string Display, BindingCaptureSource Source);

/// <summary>All discrete <see cref="PadInput.Button"/> values (excludes <see cref="PadInput.Button.None"/>).</summary>
public static class PadButtonCatalog
{
    public static readonly PadInput.Button[] All =
    {
        PadInput.Button.Select,
        PadInput.Button.L3,
        PadInput.Button.R3,
        PadInput.Button.Start,
        PadInput.Button.Up,
        PadInput.Button.Right,
        PadInput.Button.Down,
        PadInput.Button.Left,
        PadInput.Button.L2,
        PadInput.Button.R2,
        PadInput.Button.L1,
        PadInput.Button.R1,
        PadInput.Button.Triangle,
        PadInput.Button.Circle,
        PadInput.Button.Cross,
        PadInput.Button.Square,
    };

    /// <summary>Default host key → pad button (same defaults as <see cref="InputMapper"/> / MainWindow).</summary>
    public static Dictionary<string, string> DefaultPadToHost()
    {
        // Pad button name → host key code
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Start"] = "Enter",
            ["Select"] = "Shift",
            ["Up"] = "Up",
            ["Down"] = "Down",
            ["Left"] = "Left",
            ["Right"] = "Right",
            ["Cross"] = "Z",
            ["Circle"] = "X",
            ["Triangle"] = "C",
            ["Square"] = "J",
            ["L1"] = "Q",
            ["R1"] = "E",
            ["L2"] = "1",
            ["R2"] = "3",
            ["L3"] = "F",
            ["R3"] = "G",
        };
    }
}
