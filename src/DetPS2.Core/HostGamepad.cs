using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using DetPS2.Core.Input;

namespace DetPS2.Core;

/// <summary>How the host device is classified for the UI.</summary>
public enum ControllerHardwareKind
{
    Keyboard,
    XInput,
    DualShock4,
    DualSense,
    GuitarHero,   // Riffmaster / GH / RB guitar-class
    GenericHid,
    Unknown
}

/// <summary>Software mapping profile applied after hardware poll.</summary>
public enum ControllerProfile
{
    /// <summary>Standard DualShock-style digital/analog pad.</summary>
    Standard = 0,
    /// <summary>
    /// Guitar Hero layout: frets → face/shoulders, strum → D-pad U/D, whammy → R-stick Y.
    /// Works with Riffmaster / GH guitars that appear as XInput or HID.
    /// </summary>
    GuitarHero = 1
}

public sealed class GamepadDeviceInfo
{
    /// <summary>Stable id: "kb", "xi:0".."xi:3", "hid:VID:PID:n"</summary>
    public string Id { get; init; } = "kb";
    public string Name { get; init; } = "";
    public bool Connected { get; init; }
    public ControllerHardwareKind Kind { get; init; }
    public string Backend { get; init; } = "";
    public int XInputIndex { get; init; } = -1;
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
}

/// <summary>
/// Multi-backend host controllers: XInput (Xbox), HID classify (DS4/DualSense/guitars), keyboard.
/// Profile swap: Standard pad vs Guitar Hero mapping per player.
/// </summary>
public sealed class HostGamepadService
{
    public const int MaxXInput = 4;

    public string Player1DeviceId { get; set; } = "kb";
    public string Player2DeviceId { get; set; } = "kb";
    public ControllerProfile Player1Profile { get; set; } = ControllerProfile.Standard;
    public ControllerProfile Player2Profile { get; set; } = ControllerProfile.Standard;

    /// <summary>
    /// Optional P1 binding table. Null = resolve from <see cref="DefaultInputMaps"/> for device + profile.
    /// </summary>
    public InputBindingTable? Player1Bindings { get; set; }
    /// <summary>Optional P2 binding table. Null = device/profile defaults.</summary>
    public InputBindingTable? Player2Bindings { get; set; }

    // Legacy int API used by older config
    public int Player1Device
    {
        get => ParseXi(Player1DeviceId);
        set => Player1DeviceId = value < 0 ? "kb" : "xi:" + value;
    }
    public int Player2Device
    {
        get => ParseXi(Player2DeviceId);
        set => Player2DeviceId = value < 0 ? "kb" : "xi:" + value;
    }

    private static int ParseXi(string id)
    {
        if (id != null && id.StartsWith("xi:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(id.AsSpan(3), out int i)) return i;
        return -1;
    }

    /// <summary>Apply config binding lists onto this service (null lists keep defaults).</summary>
    public void ApplyConfigBindings(EmulatorConfig cfg)
    {
        if (cfg == null) return;
        Player1Profile = EmulatorConfig.ParseProfile(cfg.Player1Profile);
        Player2Profile = EmulatorConfig.ParseProfile(cfg.Player2Profile);
        Player1DeviceId = cfg.Player1DeviceId ?? "kb";
        Player2DeviceId = cfg.Player2DeviceId ?? "kb";
        // Full effective tables (defaults + overlays) so PollInto uses the same map as config helpers.
        Player1Bindings = cfg.Player1Bindings is { Count: > 0 }
            ? cfg.GetPlayer1BindingTable(GuessKind(cfg.Player1DeviceId))
            : null;
        Player2Bindings = cfg.Player2Bindings is { Count: > 0 }
            ? cfg.GetPlayer2BindingTable(GuessKind(cfg.Player2DeviceId))
            : null;
    }

    private static ControllerHardwareKind GuessKind(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || deviceId == "kb") return ControllerHardwareKind.Keyboard;
        if (deviceId.StartsWith("xi:", StringComparison.OrdinalIgnoreCase)) return ControllerHardwareKind.XInput;
        return ControllerHardwareKind.DualShock4;
    }

    public IReadOnlyList<GamepadDeviceInfo> Enumerate()
    {
        var list = new List<GamepadDeviceInfo>
        {
            new()
            {
                Id = "kb",
                Name = "Keyboard only",
                Connected = true,
                Kind = ControllerHardwareKind.Keyboard,
                Backend = "Keyboard"
            }
        };

        if (!OperatingSystem.IsWindows())
            return list;

        // XInput slots (Xbox, many guitars in XInput mode, Steam Input, etc.)
        for (int i = 0; i < MaxXInput; i++)
        {
            var st = new XINPUT_STATE();
            bool ok = XInputGetState(i, ref st) == 0;
            string name = ok ? $"XInput Controller {i + 1}" : $"XInput slot {i + 1} (empty)";
            // Heuristic: if connected, try to label from caps name isn't available; keep generic
            list.Add(new GamepadDeviceInfo
            {
                Id = "xi:" + i,
                Name = name,
                Connected = ok,
                Kind = ControllerHardwareKind.XInput,
                Backend = "XInput",
                XInputIndex = i
            });
        }

        // Raw Input HID — DS4, DualSense, guitars, other pads
        try { list.AddRange(EnumerateRawHid()); }
        catch { /* ignore */ }

        return list;
    }

    public bool PollInto(PadInput pad, string deviceId, ControllerProfile profile) =>
        PollInto(pad, deviceId, profile, bindings: null);

    /// <summary>
    /// Poll host device and apply an <see cref="InputBindingTable"/>.
    /// When <paramref name="bindings"/> is null, uses player table (if set) or
    /// <see cref="DefaultInputMaps"/> for the device family + profile.
    /// Default XInput→PS2 map is identical to the historical hardcoded map.
    /// </summary>
    public bool PollInto(PadInput pad, string deviceId, ControllerProfile profile, InputBindingTable? bindings)
    {
        if (string.IsNullOrEmpty(deviceId) || deviceId == "kb") return false;
        if (!OperatingSystem.IsWindows()) return false;

        var host = new HostInputState();
        ControllerHardwareKind kind = ControllerHardwareKind.XInput;
        bool ok = false;

        if (deviceId.StartsWith("xi:", StringComparison.OrdinalIgnoreCase))
        {
            int idx = ParseXi(deviceId);
            ok = PollXInputRaw(idx, host);
            kind = ControllerHardwareKind.XInput;
        }
        else if (deviceId.StartsWith("hid:", StringComparison.OrdinalIgnoreCase))
        {
            ok = PollHidRaw(deviceId, host, out kind);
        }

        if (!ok) return false;

        var table = bindings
            ?? (string.Equals(deviceId, Player1DeviceId, StringComparison.OrdinalIgnoreCase) ? Player1Bindings : null)
            ?? (string.Equals(deviceId, Player2DeviceId, StringComparison.OrdinalIgnoreCase) ? Player2Bindings : null)
            ?? DefaultInputMaps.Resolve(kind, profile);

        table.Apply(host, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry);

        // If software profile is GH but the table is still Standard (partial custom / old path),
        // keep the existing frets remap. GH default tables already encode frets → skip.
        if (profile == ControllerProfile.GuitarHero && table.Profile != ControllerProfile.GuitarHero)
            buttons = ApplyGuitarHeroProfile(buttons, ref lx, ref ly, ref rx, ref ry);

        pad.SetButtons(buttons);
        pad.SetLeftStick(lx, ly);
        pad.SetRightStick(rx, ry);
        return true;
    }

    /// <summary>Legacy: poll by XInput index only, standard profile.</summary>
    public bool PollInto(PadInput pad, int deviceIndex) =>
        PollInto(pad, deviceIndex < 0 ? "kb" : "xi:" + deviceIndex, ControllerProfile.Standard);

    /// <summary>
    /// Pure helper for PAD-2 / tests: map a filled <see cref="HostInputState"/> through a table.
    /// </summary>
    public static void ApplyBindings(PadInput pad, HostInputState host, InputBindingTable table) =>
        table.ApplyTo(pad, host);

    /// <summary>
    /// Guitar Hero → DualShock bits used by PS2 GH titles / common emulator maps:
    /// Green=R2, Red=Circle, Yellow=Triangle, Blue=Cross, Orange=Square,
    /// Strum Up/Down = D-pad U/D, Start/Select unchanged, Whammy on R-stick Y.
    /// Input assumed Xbox-style frets on A/B/Y/X/LB before remap when device is already guitar-shaped;
    /// if Standard XInput was read first, frets often sit on A/B/Y/X/LB already.
    /// </summary>
    public static uint ApplyGuitarHeroProfile(uint src, ref byte lx, ref byte ly, ref byte rx, ref byte ry)
    {
        // Treat source as Xbox GH layout: A=green, B=red, Y=yellow, X=blue, LB=orange,
        // DPAD = strum, Start/Back = start/select, right stick Y = whammy
        bool green = (src & (uint)PadInput.Button.Cross) != 0;   // A mapped to Cross in our XInput map
        bool red = (src & (uint)PadInput.Button.Circle) != 0;    // B
        bool blue = (src & (uint)PadInput.Button.Square) != 0;   // X
        bool yellow = (src & (uint)PadInput.Button.Triangle) != 0; // Y
        bool orange = (src & (uint)PadInput.Button.L1) != 0;     // LB
        // Some guitars use shoulders differently — also accept R1 as orange
        if ((src & (uint)PadInput.Button.R1) != 0) orange = true;

        bool strumUp = (src & (uint)PadInput.Button.Up) != 0;
        bool strumDown = (src & (uint)PadInput.Button.Down) != 0;
        // Left stick Y as alternate strum (some adapters)
        if (ly < 0x40) strumUp = true;
        if (ly > 0xC0) strumDown = true;

        uint dst = 0;
        // Classic PCSX2-ish GH → DS mapping
        if (green) dst |= (uint)PadInput.Button.R2;
        if (red) dst |= (uint)PadInput.Button.Circle;
        if (yellow) dst |= (uint)PadInput.Button.Triangle;
        if (blue) dst |= (uint)PadInput.Button.Cross;
        if (orange) dst |= (uint)PadInput.Button.Square;
        if (strumUp) dst |= (uint)PadInput.Button.Up;
        if (strumDown) dst |= (uint)PadInput.Button.Down;
        if ((src & (uint)PadInput.Button.Start) != 0) dst |= (uint)PadInput.Button.Start;
        if ((src & (uint)PadInput.Button.Select) != 0) dst |= (uint)PadInput.Button.Select;
        if ((src & (uint)PadInput.Button.L2) != 0) dst |= (uint)PadInput.Button.L2; // tilt / extra

        // Whammy: keep right stick Y (already polled); center X
        rx = 0x80;
        // ly unused for frets after remap
        lx = 0x80;
        ly = 0x80;
        return dst;
    }

    /// <summary>Fill host state with raw XInput physical controls (xi:* sources).</summary>
    private bool PollXInputRaw(int index, HostInputState host)
    {
        if (index < 0) return false;
        var st = new XINPUT_STATE();
        if (XInputGetState(index, ref st) != 0) return false;

        ushort b = st.Gamepad.wButtons;
        host.SetButton(HostSources.XiA, (b & 0x1000) != 0);
        host.SetButton(HostSources.XiB, (b & 0x2000) != 0);
        host.SetButton(HostSources.XiX, (b & 0x4000) != 0);
        host.SetButton(HostSources.XiY, (b & 0x8000) != 0);
        host.SetButton(HostSources.XiLB, (b & 0x0100) != 0);
        host.SetButton(HostSources.XiRB, (b & 0x0200) != 0);
        host.SetButton(HostSources.XiDPadUp, (b & 0x0001) != 0);
        host.SetButton(HostSources.XiDPadDown, (b & 0x0002) != 0);
        host.SetButton(HostSources.XiDPadLeft, (b & 0x0004) != 0);
        host.SetButton(HostSources.XiDPadRight, (b & 0x0008) != 0);
        host.SetButton(HostSources.XiStart, (b & 0x0010) != 0);
        host.SetButton(HostSources.XiBack, (b & 0x0020) != 0);
        host.SetButton(HostSources.XiLS, (b & 0x0040) != 0);
        host.SetButton(HostSources.XiRS, (b & 0x0080) != 0);

        // Triggers: digital bit for button maps + continuous 0..1 for axis-threshold bindings
        float lt = st.Gamepad.bLeftTrigger / 255f;
        float rt = st.Gamepad.bRightTrigger / 255f;
        host.SetAxis(HostSources.XiLT, lt);
        host.SetAxis(HostSources.XiRT, rt);
        if (st.Gamepad.bLeftTrigger > 30) host.Press(HostSources.XiLT);
        if (st.Gamepad.bRightTrigger > 30) host.Press(HostSources.XiRT);

        // Match historical Y flip: host up = negative Y short → positive ly after -sThumbLY
        host.SetAxis(HostSources.XiLX, InputBindingTable.ShortToStickFloat(st.Gamepad.sThumbLX));
        host.SetAxis(HostSources.XiLY, InputBindingTable.ShortToStickFloat((short)-st.Gamepad.sThumbLY));
        host.SetAxis(HostSources.XiRX, InputBindingTable.ShortToStickFloat(st.Gamepad.sThumbRX));
        host.SetAxis(HostSources.XiRY, InputBindingTable.ShortToStickFloat((short)-st.Gamepad.sThumbRY));
        return true;
    }

    /// <summary>Legacy direct map (used only if something still needs raw Pad bits without a table).</summary>
    private bool PollXInput(int index, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry)
    {
        buttons = 0; lx = ly = rx = ry = 0x80;
        var host = new HostInputState();
        if (!PollXInputRaw(index, host)) return false;
        DefaultInputMaps.XInput().Apply(host, out buttons, out lx, out ly, out rx, out ry);
        return true;
    }

    private static byte AxisToByte(short v)
    {
        if (v > -4000 && v < 4000) return 0x80;
        int n = (v + 32768) * 255 / 65535;
        return (byte)Math.Clamp(n, 0, 255);
    }

    #region HID / Raw Input enumeration

    private readonly Dictionary<string, nint> _hidHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _hidLastReport = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ControllerHardwareKind> _hidKind = new(StringComparer.OrdinalIgnoreCase);

    private List<GamepadDeviceInfo> EnumerateRawHid()
    {
        var list = new List<GamepadDeviceInfo>();
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref count, size);
        if (count == 0) return list;

        var devices = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(devices, ref count, size) == unchecked((uint)-1))
            return list;

        int hidN = 0;
        for (int i = 0; i < count; i++)
        {
            if (devices[i].dwType != RIM_TYPEHID) continue;
            string path = GetDeviceName(devices[i].hDevice);
            if (string.IsNullOrEmpty(path)) continue;
            // Skip keyboards/mice paths
            if (path.Contains("kbd", StringComparison.OrdinalIgnoreCase)) continue;

            ushort vid = 0, pid = 0;
            ParseVidPid(path, out vid, out pid);
            var kind = ClassifyHardware(vid, pid, path);
            // Only surface interesting game devices (not every HID)
            if (kind is ControllerHardwareKind.Unknown)
            {
                // still include if name looks like a game controller
                if (!path.Contains("IG_", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains("COL0", StringComparison.OrdinalIgnoreCase))
                    continue;
                kind = ControllerHardwareKind.GenericHid;
            }

            string id = $"hid:{vid:X4}:{pid:X4}:{hidN++}";
            string friendly = kind switch
            {
                ControllerHardwareKind.DualShock4 => $"DualShock 4 ({vid:X4}:{pid:X4})",
                ControllerHardwareKind.DualSense => $"DualSense ({vid:X4}:{pid:X4})",
                ControllerHardwareKind.GuitarHero => $"Guitar / Riffmaster ({vid:X4}:{pid:X4})",
                _ => $"HID Controller ({vid:X4}:{pid:X4})"
            };

            _hidKind[id] = kind;
            // Open handle for later polls
            try
            {
                nint h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    0, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, 0);
                if (h != 0 && h != new nint(-1))
                {
                    if (_hidHandles.TryGetValue(id, out var old) && old != 0 && old != new nint(-1))
                        CloseHandle(old);
                    _hidHandles[id] = h;
                }
            }
            catch { /* ignore open failures */ }

            list.Add(new GamepadDeviceInfo
            {
                Id = id,
                Name = friendly,
                Connected = true,
                Kind = kind,
                Backend = "HID",
                VendorId = vid,
                ProductId = pid
            });
        }
        return list;
    }

    public static ControllerHardwareKind ClassifyHardware(ushort vid, ushort pid, string? name = null)
    {
        name ??= "";
        // Sony
        if (vid == 0x054C)
        {
            if (pid is 0x05C4 or 0x09CC or 0x0BA0) return ControllerHardwareKind.DualShock4;
            if (pid is 0x0CE6 or 0x0DF2) return ControllerHardwareKind.DualSense;
        }
        // Microsoft Xbox (also via XInput usually)
        if (vid == 0x045E) return ControllerHardwareKind.XInput;

        // Guitar Hero / Rock Band / Riffmaster-class vendors
        // RedOctane, MadCatz, Harmonix, PDP, Guitar Hero licensed
        if (vid is 0x1430 or 0x1BAD or 0x12BA or 0x0E6F or 0x1B73 or 0x0738 or 0x0F0D)
        {
            // PDP Riffmaster and GH guitars
            return ControllerHardwareKind.GuitarHero;
        }

        string n = name.ToUpperInvariant();
        if (n.Contains("GUITAR") || n.Contains("RIFF") || n.Contains("HERO") || n.Contains("JOYCON") == false && n.Contains("STRUM"))
            return ControllerHardwareKind.GuitarHero;
        if (n.Contains("DUALSENSE") || n.Contains("WIRELESS CONTROLLER") && n.Contains("054C"))
            return ControllerHardwareKind.DualSense;
        if (n.Contains("DUALSHOCK") || n.Contains("DS4"))
            return ControllerHardwareKind.DualShock4;

        return ControllerHardwareKind.Unknown;
    }

    private bool PollHidRaw(string id, HostInputState host, out ControllerHardwareKind kind)
    {
        kind = ControllerHardwareKind.GenericHid;
        if (!_hidHandles.TryGetValue(id, out nint h) || h == 0 || h == new nint(-1))
            return false;

        byte[] buf = new byte[64];
        if (!HidD_GetInputReport(h, buf, (uint)buf.Length))
        {
            if (_hidLastReport.TryGetValue(id, out var last))
                buf = last;
            else
                return false;
        }
        else
        {
            _hidLastReport[id] = (byte[])buf.Clone();
        }

        _hidKind.TryGetValue(id, out kind);
        if (kind == ControllerHardwareKind.DualShock4)
            return ParseDs4Raw(buf, host);
        if (kind == ControllerHardwareKind.DualSense)
            return ParseDualSenseRaw(buf, host);
        if (kind == ControllerHardwareKind.GuitarHero)
        {
            // Guitar HID often XInput-shaped when also exposed as xi:; generic bit parse as fallback.
            return ParseGenericHidRaw(buf, host);
        }
        return ParseGenericHidRaw(buf, host);
    }

    private bool PollHidCached(string id, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry)
    {
        buttons = 0; lx = ly = rx = ry = 0x80;
        var host = new HostInputState();
        if (!PollHidRaw(id, host, out var kind)) return false;
        DefaultInputMaps.Resolve(kind, ControllerProfile.Standard)
            .Apply(host, out buttons, out lx, out ly, out rx, out ry);
        return true;
    }

    private static bool ParseDs4Raw(byte[] r, HostInputState host)
    {
        // USB report often starts at 0; BT has offset. Detect by size/report id
        int o = r[0] == 0x11 ? 2 : 0; // BT vs USB rough
        if (r.Length < o + 10) return false;
        host.SetAxis(HostSources.DsLX, InputBindingTable.ByteAxisToFloat(r[o + 1]));
        host.SetAxis(HostSources.DsLY, InputBindingTable.ByteAxisToFloat(r[o + 2]));
        host.SetAxis(HostSources.DsRX, InputBindingTable.ByteAxisToFloat(r[o + 3]));
        host.SetAxis(HostSources.DsRY, InputBindingTable.ByteAxisToFloat(r[o + 4]));
        byte b1 = r[o + 5];
        byte b2 = r[o + 6];
        int dpad = b1 & 0x0F;
        host.SetButton(HostSources.DsUp, dpad is 0 or 1 or 7);
        host.SetButton(HostSources.DsRight, dpad is 2 or 3 or 1);
        host.SetButton(HostSources.DsDown, dpad is 4 or 3 or 5);
        host.SetButton(HostSources.DsLeft, dpad is 6 or 5 or 7);
        host.SetButton(HostSources.DsSquare, (b1 & 0x10) != 0);
        host.SetButton(HostSources.DsCross, (b1 & 0x20) != 0);
        host.SetButton(HostSources.DsCircle, (b1 & 0x40) != 0);
        host.SetButton(HostSources.DsTriangle, (b1 & 0x80) != 0);
        host.SetButton(HostSources.DsL1, (b2 & 0x01) != 0);
        host.SetButton(HostSources.DsR1, (b2 & 0x02) != 0);
        host.SetButton(HostSources.DsL2, (b2 & 0x04) != 0);
        host.SetButton(HostSources.DsR2, (b2 & 0x08) != 0);
        host.SetButton(HostSources.DsSelect, (b2 & 0x10) != 0);
        host.SetButton(HostSources.DsStart, (b2 & 0x20) != 0);
        host.SetButton(HostSources.DsL3, (b2 & 0x40) != 0);
        host.SetButton(HostSources.DsR3, (b2 & 0x80) != 0);
        return true;
    }

    private static bool ParseDs4(byte[] r, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry)
    {
        var host = new HostInputState();
        if (!ParseDs4Raw(r, host))
        {
            buttons = 0; lx = ly = rx = ry = 0x80;
            return false;
        }
        DefaultInputMaps.DualShock4().Apply(host, out buttons, out lx, out ly, out rx, out ry);
        return true;
    }

    private static bool ParseDualSenseRaw(byte[] r, HostInputState host)
    {
        int o = r[0] == 0x01 ? 1 : 0;
        if (r.Length < o + 10) return ParseDs4Raw(r, host);
        host.SetAxis(HostSources.DsLX, InputBindingTable.ByteAxisToFloat(r[o + 0]));
        host.SetAxis(HostSources.DsLY, InputBindingTable.ByteAxisToFloat(r[o + 1]));
        host.SetAxis(HostSources.DsRX, InputBindingTable.ByteAxisToFloat(r[o + 2]));
        host.SetAxis(HostSources.DsRY, InputBindingTable.ByteAxisToFloat(r[o + 3]));
        byte b1 = r[o + 7];
        byte b2 = r[o + 8];
        int dpad = b1 & 0x0F;
        host.SetButton(HostSources.DsUp, dpad is 0 or 1 or 7);
        host.SetButton(HostSources.DsRight, dpad is 2 or 3 or 1);
        host.SetButton(HostSources.DsDown, dpad is 4 or 3 or 5);
        host.SetButton(HostSources.DsLeft, dpad is 6 or 5 or 7);
        host.SetButton(HostSources.DsSquare, (b1 & 0x10) != 0);
        host.SetButton(HostSources.DsCross, (b1 & 0x20) != 0);
        host.SetButton(HostSources.DsCircle, (b1 & 0x40) != 0);
        host.SetButton(HostSources.DsTriangle, (b1 & 0x80) != 0);
        host.SetButton(HostSources.DsL1, (b2 & 0x01) != 0);
        host.SetButton(HostSources.DsR1, (b2 & 0x02) != 0);
        host.SetButton(HostSources.DsL2, (b2 & 0x04) != 0);
        host.SetButton(HostSources.DsR2, (b2 & 0x08) != 0);
        host.SetButton(HostSources.DsSelect, (b2 & 0x10) != 0);
        host.SetButton(HostSources.DsStart, (b2 & 0x20) != 0);
        host.SetButton(HostSources.DsL3, (b2 & 0x40) != 0);
        host.SetButton(HostSources.DsR3, (b2 & 0x80) != 0);
        return true;
    }

    private static bool ParseDualSense(byte[] r, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry)
    {
        var host = new HostInputState();
        if (!ParseDualSenseRaw(r, host))
        {
            buttons = 0; lx = ly = rx = ry = 0x80;
            return false;
        }
        DefaultInputMaps.DualSense().Apply(host, out buttons, out lx, out ly, out rx, out ry);
        return true;
    }

    private static bool ParseGenericHidRaw(byte[] r, HostInputState host)
    {
        if (r.Length < 4) return false;
        if (r.Length > 2)
        {
            host.SetAxis("hid:LX", InputBindingTable.ByteAxisToFloat(r[0]));
            host.SetAxis("hid:LY", InputBindingTable.ByteAxisToFloat(r[1]));
        }
        if (r.Length > 4)
        {
            host.SetAxis("hid:RX", InputBindingTable.ByteAxisToFloat(r[2]));
            host.SetAxis("hid:RY", InputBindingTable.ByteAxisToFloat(r[3]));
        }
        int btnOff = r.Length > 6 ? 4 : 2;
        uint bits = r[btnOff];
        if (r.Length > btnOff + 1) bits |= (uint)r[btnOff + 1] << 8;
        for (int i = 0; i < 16; i++)
        {
            if ((bits & (1u << i)) != 0)
                host.Press(HostSources.HidBit(i));
        }
        return bits != 0 || host.TryGetAxis("hid:LX", out _) || host.TryGetAxis("hid:LY", out _);
    }

    private static bool ParseGenericHid(byte[] r, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry)
    {
        var host = new HostInputState();
        if (!ParseGenericHidRaw(r, host))
        {
            buttons = 0; lx = ly = rx = ry = 0x80;
            return false;
        }
        DefaultInputMaps.GenericHid().Apply(host, out buttons, out lx, out ly, out rx, out ry);
        return true;
    }

    private static void ParseVidPid(string path, out ushort vid, out ushort pid)
    {
        vid = 0; pid = 0;
        // ...VID_054C&PID_0CE6...
        int vi = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        int pi = path.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vi >= 0 && vi + 8 <= path.Length)
            ushort.TryParse(path.AsSpan(vi + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out vid);
        if (pi >= 0 && pi + 8 <= path.Length)
            ushort.TryParse(path.AsSpan(pi + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    private static string GetDeviceName(nint hDevice)
    {
        uint pcbSize = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, nint.Zero, ref pcbSize);
        if (pcbSize == 0) return "";
        nint buf = Marshal.AllocHGlobal((int)pcbSize * 2);
        try
        {
            if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buf, ref pcbSize) == unchecked((uint)-1))
                return "";
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    #endregion

    #region P/Invoke

    private const uint RIM_TYPEHID = 2;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public nint hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(nint hDevice, uint uiCommand, nint pData, ref uint pcbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurity, uint dwCreation, uint dwFlags, nint hTemplate);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(nint hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState_1_4(int dwUserIndex, ref XINPUT_STATE pState);
    [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState_1_3(int dwUserIndex, ref XINPUT_STATE pState);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState_9_1_0(int dwUserIndex, ref XINPUT_STATE pState);

    private static int XInputGetState(int index, ref XINPUT_STATE state)
    {
        try { return XInputGetState_1_4(index, ref state); }
        catch (DllNotFoundException)
        {
            try { return XInputGetState_1_3(index, ref state); }
            catch (DllNotFoundException)
            {
                try { return XInputGetState_9_1_0(index, ref state); }
                catch (DllNotFoundException) { return -1; }
            }
        }
    }

    #endregion
}
