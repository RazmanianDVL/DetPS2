using System;
using System.Globalization;

namespace DetPS2.Core.Input;

/// <summary>Kind of emulated DualShock target a host control maps onto.</summary>
public enum PadTargetKind
{
    Button = 0,
    Axis = 1,
}

/// <summary>Analog stick axes on the emulated DualShock (0x80 center).</summary>
public enum PadAxis
{
    LeftX = 0,
    LeftY = 1,
    RightX = 2,
    RightY = 3,
}

/// <summary>
/// Canonical host source id strings used by bindings and PAD-2 capture UI.
/// Keyboard: <c>kb:Enter</c>, XInput/Xbox: <c>xi:A</c>, DualShock layout: <c>ds:Cross</c>,
/// generic HID bit: <c>hid:0</c>..
/// </summary>
public static class HostSources
{
    // --- Keyboard (names match Desktop MapKey / InputMapper) ---
    public const string KbEnter = "kb:Enter";
    public const string KbShift = "kb:Shift";
    public const string KbUp = "kb:Up";
    public const string KbDown = "kb:Down";
    public const string KbLeft = "kb:Left";
    public const string KbRight = "kb:Right";
    public const string KbW = "kb:W";
    public const string KbA = "kb:A";
    public const string KbS = "kb:S";
    public const string KbD = "kb:D";
    public const string KbZ = "kb:Z";
    public const string KbX = "kb:X";
    public const string KbC = "kb:C";
    public const string KbJ = "kb:J";
    public const string KbK = "kb:K";
    public const string KbL = "kb:L";
    public const string KbI = "kb:I";
    public const string KbQ = "kb:Q";
    public const string KbE = "kb:E";

    // --- XInput / Xbox 360 / One / Series physical ---
    public const string XiA = "xi:A";
    public const string XiB = "xi:B";
    public const string XiX = "xi:X";
    public const string XiY = "xi:Y";
    public const string XiLB = "xi:LB";
    public const string XiRB = "xi:RB";
    public const string XiLT = "xi:LT";
    public const string XiRT = "xi:RT";
    public const string XiStart = "xi:Start";
    public const string XiBack = "xi:Back";
    public const string XiDPadUp = "xi:DPadUp";
    public const string XiDPadDown = "xi:DPadDown";
    public const string XiDPadLeft = "xi:DPadLeft";
    public const string XiDPadRight = "xi:DPadRight";
    public const string XiLS = "xi:LS";
    public const string XiRS = "xi:RS";
    public const string XiLX = "xi:LX";
    public const string XiLY = "xi:LY";
    public const string XiRX = "xi:RX";
    public const string XiRY = "xi:RY";

    // --- DualShock 4 / DualSense physical (Sony layout = PS2 names) ---
    public const string DsCross = "ds:Cross";
    public const string DsCircle = "ds:Circle";
    public const string DsSquare = "ds:Square";
    public const string DsTriangle = "ds:Triangle";
    public const string DsL1 = "ds:L1";
    public const string DsR1 = "ds:R1";
    public const string DsL2 = "ds:L2";
    public const string DsR2 = "ds:R2";
    public const string DsStart = "ds:Start";
    public const string DsSelect = "ds:Select";
    public const string DsUp = "ds:Up";
    public const string DsDown = "ds:Down";
    public const string DsLeft = "ds:Left";
    public const string DsRight = "ds:Right";
    public const string DsL3 = "ds:L3";
    public const string DsR3 = "ds:R3";
    public const string DsLX = "ds:LX";
    public const string DsLY = "ds:LY";
    public const string DsRX = "ds:RX";
    public const string DsRY = "ds:RY";

    public static string Keyboard(string keyName) => "kb:" + keyName;
    public static string XInput(string name) => "xi:" + name;
    public static string DualShock(string name) => "ds:" + name;
    public static string HidBit(int index) => "hid:" + index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One host-source → emulated DualShock target mapping.
/// JSON-friendly via <see cref="SourceId"/> + <see cref="TargetName"/>.
/// </summary>
public sealed class InputBinding
{
    /// <summary>Host source id, e.g. <c>kb:Z</c>, <c>xi:A</c>, <c>ds:Cross</c>.</summary>
    public string SourceId { get; set; } = "";

    public PadTargetKind TargetKind { get; set; } = PadTargetKind.Button;

    /// <summary>When <see cref="TargetKind"/> is Button.</summary>
    public PadInput.Button TargetButton { get; set; } = PadInput.Button.None;

    /// <summary>When <see cref="TargetKind"/> is Axis.</summary>
    public PadAxis TargetAxis { get; set; } = PadAxis.LeftX;

    /// <summary>Invert stick axis (or treat axis source as active when below −threshold).</summary>
    public bool Invert { get; set; }

    /// <summary>
    /// When source is an analog axis and target is a button: press when
    /// |axis| ≥ threshold (sticks −1..1) or axis ≥ threshold (triggers 0..1).
    /// 0 = use default (0.5 for triggers, 0.55 for sticks).
    /// </summary>
    public float AxisToButtonThreshold { get; set; }

    public InputBinding() { }

    public InputBinding(string sourceId, PadInput.Button button)
    {
        SourceId = sourceId;
        TargetKind = PadTargetKind.Button;
        TargetButton = button;
    }

    public InputBinding(string sourceId, PadAxis axis, bool invert = false)
    {
        SourceId = sourceId;
        TargetKind = PadTargetKind.Axis;
        TargetAxis = axis;
        Invert = invert;
    }

    /// <summary>Serializable target token for config/UI: button name or axis name.</summary>
    public string TargetName
    {
        get => TargetKind == PadTargetKind.Axis
            ? AxisToName(TargetAxis)
            : ButtonToName(TargetButton);
        set => SetTargetFromName(value);
    }

    public void SetTargetFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TargetKind = PadTargetKind.Button;
            TargetButton = PadInput.Button.None;
            return;
        }

        name = name.Trim();
        if (name.StartsWith("Axis.", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(5);
        if (TryParseAxis(name, out var axis))
        {
            TargetKind = PadTargetKind.Axis;
            TargetAxis = axis;
            return;
        }

        TargetKind = PadTargetKind.Button;
        TargetButton = ParseButton(name);
    }

    public static string ButtonToName(PadInput.Button b) => b switch
    {
        PadInput.Button.Select => "Select",
        PadInput.Button.L3 => "L3",
        PadInput.Button.R3 => "R3",
        PadInput.Button.Start => "Start",
        PadInput.Button.Up => "Up",
        PadInput.Button.Right => "Right",
        PadInput.Button.Down => "Down",
        PadInput.Button.Left => "Left",
        PadInput.Button.L2 => "L2",
        PadInput.Button.R2 => "R2",
        PadInput.Button.L1 => "L1",
        PadInput.Button.R1 => "R1",
        PadInput.Button.Triangle => "Triangle",
        PadInput.Button.Circle => "Circle",
        PadInput.Button.Cross => "Cross",
        PadInput.Button.Square => "Square",
        _ => "None"
    };

    public static PadInput.Button ParseButton(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return PadInput.Button.None;
        // Prefer full DualShock names in config. Lone A/B/X/Y follow Xbox face → PS2 defaults.
        return name.Trim().ToUpperInvariant() switch
        {
            "SELECT" or "BACK" or "SHARE" => PadInput.Button.Select,
            "L3" or "LS" => PadInput.Button.L3,
            "R3" or "RS" => PadInput.Button.R3,
            "START" or "OPTIONS" => PadInput.Button.Start,
            "UP" or "DPADUP" => PadInput.Button.Up,
            "RIGHT" or "DPADRIGHT" => PadInput.Button.Right,
            "DOWN" or "DPADDOWN" => PadInput.Button.Down,
            "LEFT" or "DPADLEFT" => PadInput.Button.Left,
            "L2" or "LT" => PadInput.Button.L2,
            "R2" or "RT" => PadInput.Button.R2,
            "L1" or "LB" => PadInput.Button.L1,
            "R1" or "RB" => PadInput.Button.R1,
            "TRIANGLE" => PadInput.Button.Triangle,
            "CIRCLE" => PadInput.Button.Circle,
            "CROSS" => PadInput.Button.Cross,
            "SQUARE" => PadInput.Button.Square,
            "A" or "XBOXA" => PadInput.Button.Cross,
            "B" or "XBOXB" => PadInput.Button.Circle,
            "X" or "XBOXX" => PadInput.Button.Square,
            "Y" or "XBOXY" => PadInput.Button.Triangle,
            _ => PadInput.Button.None
        };
    }

    public static string AxisToName(PadAxis a) => a switch
    {
        PadAxis.LeftX => "LeftX",
        PadAxis.LeftY => "LeftY",
        PadAxis.RightX => "RightX",
        PadAxis.RightY => "RightY",
        _ => "LeftX"
    };

    public static bool TryParseAxis(string? name, out PadAxis axis)
    {
        axis = PadAxis.LeftX;
        if (string.IsNullOrWhiteSpace(name)) return false;
        switch (name.Trim().ToUpperInvariant())
        {
            case "LEFTX":
            case "LX":
            case "LEFTSTICKX":
                axis = PadAxis.LeftX; return true;
            case "LEFTY":
            case "LY":
            case "LEFTSTICKY":
                axis = PadAxis.LeftY; return true;
            case "RIGHTX":
            case "RX":
            case "RIGHTSTICKX":
                axis = PadAxis.RightX; return true;
            case "RIGHTY":
            case "RY":
            case "RIGHTSTICKY":
                axis = PadAxis.RightY; return true;
            default:
                return false;
        }
    }

    /// <summary>All digital DualShock buttons PAD-2 can bind to (excludes None).</summary>
    public static PadInput.Button[] AllTargetButtons { get; } =
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

    public static PadAxis[] AllTargetAxes { get; } =
    {
        PadAxis.LeftX, PadAxis.LeftY, PadAxis.RightX, PadAxis.RightY
    };

    public InputBinding Clone() => new()
    {
        SourceId = SourceId,
        TargetKind = TargetKind,
        TargetButton = TargetButton,
        TargetAxis = TargetAxis,
        Invert = Invert,
        AxisToButtonThreshold = AxisToButtonThreshold
    };
}

/// <summary>
/// JSON DTO for <see cref="EmulatorConfig"/> player binding lists.
/// Old configs without these properties deserialize as null (defaults apply).
/// </summary>
public sealed class InputBindingEntry
{
    /// <summary>Host source id (<c>kb:Z</c>, <c>xi:A</c>, …).</summary>
    public string Source { get; set; } = "";

    /// <summary>Target: button name (<c>Cross</c>) or axis (<c>LeftX</c> / <c>Axis.LeftX</c>).</summary>
    public string Target { get; set; } = "";

    public bool Invert { get; set; }

    public float AxisToButtonThreshold { get; set; }

    public InputBinding ToBinding()
    {
        var b = new InputBinding { SourceId = Source ?? "", Invert = Invert, AxisToButtonThreshold = AxisToButtonThreshold };
        b.SetTargetFromName(Target);
        return b;
    }

    public static InputBindingEntry FromBinding(InputBinding b) => new()
    {
        Source = b.SourceId,
        Target = b.TargetName,
        Invert = b.Invert,
        AxisToButtonThreshold = b.AxisToButtonThreshold
    };
}

/// <summary>
/// Snapshot of host controls for one poll frame (buttons as source ids, axes as floats).
/// Stick axes: −1..1 (0 center). Triggers: 0..1.
/// </summary>
public sealed class HostInputState
{
    private readonly HashSet<string> _buttons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _axes = new(StringComparer.OrdinalIgnoreCase);

    public void Clear()
    {
        _buttons.Clear();
        _axes.Clear();
    }

    public void SetButton(string sourceId, bool down)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        if (down) _buttons.Add(sourceId);
        else _buttons.Remove(sourceId);
    }

    public void Press(string sourceId) => SetButton(sourceId, true);

    public bool IsDown(string sourceId) =>
        !string.IsNullOrEmpty(sourceId) && _buttons.Contains(sourceId);

    public void SetAxis(string sourceId, float value)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        _axes[sourceId] = value;
    }

    public float GetAxis(string sourceId, float defaultValue = 0f) =>
        _axes.TryGetValue(sourceId, out float v) ? v : defaultValue;

    public bool TryGetAxis(string sourceId, out float value) =>
        _axes.TryGetValue(sourceId, out value);

    public IReadOnlyCollection<string> ActiveButtons => _buttons;

    public IReadOnlyDictionary<string, float> Axes => _axes;

    /// <summary>
    /// True if this source is active as a digital button or past axis threshold.
    /// After optional invert, only the positive side of the axis counts (for strum half-axes).
    /// </summary>
    public bool IsSourceActive(string sourceId, float threshold, bool invert)
    {
        if (IsDown(sourceId)) return true;
        if (!TryGetAxis(sourceId, out float v)) return false;
        if (invert) v = -v;
        return v >= threshold;
    }
}
