using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

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

    public bool PollInto(PadInput pad, string deviceId, ControllerProfile profile)
    {
        if (string.IsNullOrEmpty(deviceId) || deviceId == "kb") return false;
        if (!OperatingSystem.IsWindows()) return false;

        uint buttons = 0;
        byte lx = 0x80, ly = 0x80, rx = 0x80, ry = 0x80;
        bool ok = false;

        if (deviceId.StartsWith("xi:", StringComparison.OrdinalIgnoreCase))
        {
            int idx = ParseXi(deviceId);
            ok = PollXInput(idx, out buttons, out lx, out ly, out rx, out ry);
        }
        else if (deviceId.StartsWith("hid:", StringComparison.OrdinalIgnoreCase))
        {
            ok = PollHidCached(deviceId, out buttons, out lx, out ly, out rx, out ry);
        }

        if (!ok) return false;

        if (profile == ControllerProfile.GuitarHero)
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

    private bool PollXInput(int index, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry)
    {
        buttons = 0; lx = ly = rx = ry = 0x80;
        if (index < 0) return false;
        var st = new XINPUT_STATE();
        if (XInputGetState(index, ref st) != 0) return false;

        ushort b = st.Gamepad.wButtons;
        if ((b & 0x1000) != 0) buttons |= (uint)PadInput.Button.Cross;      // A
        if ((b & 0x2000) != 0) buttons |= (uint)PadInput.Button.Circle;     // B
        if ((b & 0x4000) != 0) buttons |= (uint)PadInput.Button.Square;     // X
        if ((b & 0x8000) != 0) buttons |= (uint)PadInput.Button.Triangle;   // Y
        if ((b & 0x0100) != 0) buttons |= (uint)PadInput.Button.L1;
        if ((b & 0x0200) != 0) buttons |= (uint)PadInput.Button.R1;
        if ((b & 0x0001) != 0) buttons |= (uint)PadInput.Button.Up;
        if ((b & 0x0002) != 0) buttons |= (uint)PadInput.Button.Down;
        if ((b & 0x0004) != 0) buttons |= (uint)PadInput.Button.Left;
        if ((b & 0x0008) != 0) buttons |= (uint)PadInput.Button.Right;
        if ((b & 0x0010) != 0) buttons |= (uint)PadInput.Button.Start;
        if ((b & 0x0020) != 0) buttons |= (uint)PadInput.Button.Select;
        if ((b & 0x0040) != 0) buttons |= (uint)PadInput.Button.L3;
        if ((b & 0x0080) != 0) buttons |= (uint)PadInput.Button.R3;
        if (st.Gamepad.bLeftTrigger > 30) buttons |= (uint)PadInput.Button.L2;
        if (st.Gamepad.bRightTrigger > 30) buttons |= (uint)PadInput.Button.R2;

        lx = AxisToByte(st.Gamepad.sThumbLX);
        ly = AxisToByte((short)-st.Gamepad.sThumbLY);
        rx = AxisToByte(st.Gamepad.sThumbRX);
        ry = AxisToByte((short)-st.Gamepad.sThumbRY);
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

    private bool PollHidCached(string id, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry)
    {
        buttons = 0; lx = ly = rx = ry = 0x80;
        if (!_hidHandles.TryGetValue(id, out nint h) || h == 0 || h == new nint(-1))
            return false;

        // Non-blocking peek: use HidD_GetInputReport if possible; else skip (XInput path preferred when available)
        byte[] buf = new byte[64];
        // Many DS4/DualSense devices need exclusive access; if read fails, return false
        uint read = 0;
        // Try synchronous short read via ReadFile with 0 timeout is hard; use HidD_GetInputReport
        if (!HidD_GetInputReport(h, buf, (uint)buf.Length))
        {
            // Fallback: if we have last report, use it
            if (_hidLastReport.TryGetValue(id, out var last))
                buf = last;
            else
                return false;
        }
        else
        {
            _hidLastReport[id] = (byte[])buf.Clone();
        }

        _hidKind.TryGetValue(id, out var kind);
        if (kind == ControllerHardwareKind.DualShock4)
            return ParseDs4(buf, out buttons, out lx, out ly, out rx, out ry);
        if (kind == ControllerHardwareKind.DualSense)
            return ParseDualSense(buf, out buttons, out lx, out ly, out rx, out ry);
        // Generic / guitar HID: map first 16 button bits + two axes if present
        return ParseGenericHid(buf, out buttons, out lx, out ly, out rx, out ry);
    }

    private static bool ParseDs4(byte[] r, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry)
    {
        buttons = 0; lx = ly = rx = ry = 0x80;
        // USB report often starts at 0; BT has offset. Detect by size/report id
        int o = r[0] == 0x11 ? 2 : 0; // BT vs USB rough
        if (r.Length < o + 10) return false;
        lx = r[o + 1];
        ly = r[o + 2];
        rx = r[o + 3];
        ry = r[o + 4];
        byte b1 = r[o + 5];
        byte b2 = r[o + 6];
        byte b3 = r[o + 7];
        // D-pad in low nibble of b1
        int dpad = b1 & 0x0F;
        if (dpad is 0 or 1 or 7) buttons |= (uint)PadInput.Button.Up;
        if (dpad is 2 or 3 or 1) buttons |= (uint)PadInput.Button.Right;
        if (dpad is 4 or 3 or 5) buttons |= (uint)PadInput.Button.Down;
        if (dpad is 6 or 5 or 7) buttons |= (uint)PadInput.Button.Left;
        if ((b1 & 0x10) != 0) buttons |= (uint)PadInput.Button.Square;
        if ((b1 & 0x20) != 0) buttons |= (uint)PadInput.Button.Cross;
        if ((b1 & 0x40) != 0) buttons |= (uint)PadInput.Button.Circle;
        if ((b1 & 0x80) != 0) buttons |= (uint)PadInput.Button.Triangle;
        if ((b2 & 0x01) != 0) buttons |= (uint)PadInput.Button.L1;
        if ((b2 & 0x02) != 0) buttons |= (uint)PadInput.Button.R1;
        if ((b2 & 0x04) != 0) buttons |= (uint)PadInput.Button.L2;
        if ((b2 & 0x08) != 0) buttons |= (uint)PadInput.Button.R2;
        if ((b2 & 0x10) != 0) buttons |= (uint)PadInput.Button.Select;
        if ((b2 & 0x20) != 0) buttons |= (uint)PadInput.Button.Start;
        if ((b2 & 0x40) != 0) buttons |= (uint)PadInput.Button.L3;
        if ((b2 & 0x80) != 0) buttons |= (uint)PadInput.Button.R3;
        _ = b3;
        return true;
    }

    private static bool ParseDualSense(byte[] r, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry)
    {
        // Similar layout to DS4 for first buttons on USB report id 0x01
        buttons = 0; lx = ly = rx = ry = 0x80;
        int o = r[0] == 0x01 ? 1 : 0;
        if (r.Length < o + 10) return ParseDs4(r, out buttons, out lx, out ly, out rx, out ry);
        lx = r[o + 0];
        ly = r[o + 1];
        rx = r[o + 2];
        ry = r[o + 3];
        byte b1 = r[o + 7];
        byte b2 = r[o + 8];
        int dpad = b1 & 0x0F;
        if (dpad is 0 or 1 or 7) buttons |= (uint)PadInput.Button.Up;
        if (dpad is 2 or 3 or 1) buttons |= (uint)PadInput.Button.Right;
        if (dpad is 4 or 3 or 5) buttons |= (uint)PadInput.Button.Down;
        if (dpad is 6 or 5 or 7) buttons |= (uint)PadInput.Button.Left;
        if ((b1 & 0x10) != 0) buttons |= (uint)PadInput.Button.Square;
        if ((b1 & 0x20) != 0) buttons |= (uint)PadInput.Button.Cross;
        if ((b1 & 0x40) != 0) buttons |= (uint)PadInput.Button.Circle;
        if ((b1 & 0x80) != 0) buttons |= (uint)PadInput.Button.Triangle;
        if ((b2 & 0x01) != 0) buttons |= (uint)PadInput.Button.L1;
        if ((b2 & 0x02) != 0) buttons |= (uint)PadInput.Button.R1;
        if ((b2 & 0x04) != 0) buttons |= (uint)PadInput.Button.L2;
        if ((b2 & 0x08) != 0) buttons |= (uint)PadInput.Button.R2;
        if ((b2 & 0x10) != 0) buttons |= (uint)PadInput.Button.Select;
        if ((b2 & 0x20) != 0) buttons |= (uint)PadInput.Button.Start;
        if ((b2 & 0x40) != 0) buttons |= (uint)PadInput.Button.L3;
        if ((b2 & 0x80) != 0) buttons |= (uint)PadInput.Button.R3;
        return true;
    }

    private static bool ParseGenericHid(byte[] r, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry)
    {
        buttons = 0; lx = ly = rx = ry = 0x80;
        if (r.Length < 4) return false;
        // Very rough: axes then buttons
        if (r.Length > 2) { lx = r[0]; ly = r[1]; }
        if (r.Length > 4) { rx = r[2]; ry = r[3]; }
        int btnOff = r.Length > 6 ? 4 : 2;
        uint bits = r[btnOff];
        if (r.Length > btnOff + 1) bits |= (uint)r[btnOff + 1] << 8;
        // Map first 16 bits onto digital face roughly
        if ((bits & 1) != 0) buttons |= (uint)PadInput.Button.Cross;
        if ((bits & 2) != 0) buttons |= (uint)PadInput.Button.Circle;
        if ((bits & 4) != 0) buttons |= (uint)PadInput.Button.Square;
        if ((bits & 8) != 0) buttons |= (uint)PadInput.Button.Triangle;
        if ((bits & 16) != 0) buttons |= (uint)PadInput.Button.L1;
        if ((bits & 32) != 0) buttons |= (uint)PadInput.Button.R1;
        if ((bits & 64) != 0) buttons |= (uint)PadInput.Button.Select;
        if ((bits & 128) != 0) buttons |= (uint)PadInput.Button.Start;
        if ((bits & 256) != 0) buttons |= (uint)PadInput.Button.Up;
        if ((bits & 512) != 0) buttons |= (uint)PadInput.Button.Down;
        if ((bits & 1024) != 0) buttons |= (uint)PadInput.Button.Left;
        if ((bits & 2048) != 0) buttons |= (uint)PadInput.Button.Right;
        return bits != 0 || lx != 0x80 || ly != 0x80;
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
