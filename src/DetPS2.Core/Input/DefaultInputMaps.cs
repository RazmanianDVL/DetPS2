namespace DetPS2.Core.Input;

/// <summary>
/// Built-in host→PS2 binding tables. Defaults match existing Desktop MapKey and
/// <see cref="HostGamepadService"/> XInput / DS4 / DualSense / GuitarHero maps.
/// </summary>
public static class DefaultInputMaps
{
    /// <summary>
    /// Keyboard: Enter=Start, Shift=Select, WASD/Arrows=D-pad,
    /// Z/K=Cross, X/L=Circle, C/I=Triangle, J=Square, Q=L1, E=R1.
    /// </summary>
    public static InputBindingTable Keyboard()
    {
        var t = new InputBindingTable("Keyboard", ControllerProfile.Standard);
        t.Add(HostSources.KbEnter, PadInput.Button.Start);
        t.Add(HostSources.KbShift, PadInput.Button.Select);
        t.Add(HostSources.KbUp, PadInput.Button.Up);
        t.Add(HostSources.KbDown, PadInput.Button.Down);
        t.Add(HostSources.KbLeft, PadInput.Button.Left);
        t.Add(HostSources.KbRight, PadInput.Button.Right);
        t.Add(HostSources.KbW, PadInput.Button.Up);
        t.Add(HostSources.KbS, PadInput.Button.Down);
        t.Add(HostSources.KbA, PadInput.Button.Left);
        t.Add(HostSources.KbD, PadInput.Button.Right);
        t.Add(HostSources.KbZ, PadInput.Button.Cross);
        t.Add(HostSources.KbK, PadInput.Button.Cross);
        t.Add(HostSources.KbX, PadInput.Button.Circle);
        t.Add(HostSources.KbL, PadInput.Button.Circle);
        t.Add(HostSources.KbC, PadInput.Button.Triangle);
        t.Add(HostSources.KbI, PadInput.Button.Triangle);
        t.Add(HostSources.KbJ, PadInput.Button.Square);
        t.Add(HostSources.KbQ, PadInput.Button.L1);
        t.Add(HostSources.KbE, PadInput.Button.R1);
        return t;
    }

    /// <summary>
    /// XInput / Xbox 360 / One / Series: A Cross, B Circle, X Square, Y Triangle,
    /// LB/RB L1/R1, LT/RT L2/R2, Start/Back, D-pad, sticks, LS/RS → L3/R3.
    /// Matches <c>HostGamepadService.PollXInput</c> historical map.
    /// </summary>
    public static InputBindingTable XInput()
    {
        var t = new InputBindingTable("XInput", ControllerProfile.Standard);
        t.Add(HostSources.XiA, PadInput.Button.Cross);
        t.Add(HostSources.XiB, PadInput.Button.Circle);
        t.Add(HostSources.XiX, PadInput.Button.Square);
        t.Add(HostSources.XiY, PadInput.Button.Triangle);
        t.Add(HostSources.XiLB, PadInput.Button.L1);
        t.Add(HostSources.XiRB, PadInput.Button.R1);
        t.Add(HostSources.XiLT, PadInput.Button.L2);
        t.Add(HostSources.XiRT, PadInput.Button.R2);
        t.Add(HostSources.XiStart, PadInput.Button.Start);
        t.Add(HostSources.XiBack, PadInput.Button.Select);
        t.Add(HostSources.XiDPadUp, PadInput.Button.Up);
        t.Add(HostSources.XiDPadDown, PadInput.Button.Down);
        t.Add(HostSources.XiDPadLeft, PadInput.Button.Left);
        t.Add(HostSources.XiDPadRight, PadInput.Button.Right);
        t.Add(HostSources.XiLS, PadInput.Button.L3);
        t.Add(HostSources.XiRS, PadInput.Button.R3);
        t.Add(HostSources.XiLX, PadAxis.LeftX);
        t.Add(HostSources.XiLY, PadAxis.LeftY);
        t.Add(HostSources.XiRX, PadAxis.RightX);
        t.Add(HostSources.XiRY, PadAxis.RightY);
        return t;
    }

    /// <summary>Alias for Xbox 360 / One / Series (same as XInput defaults).</summary>
    public static InputBindingTable Xbox() => WithName(XInput(), "Xbox");

    /// <summary>
    /// DualShock 4: identity map (physical Sony layout → PS2 bits).
    /// </summary>
    public static InputBindingTable DualShock4()
    {
        var t = DualShockLayout("DualShock4");
        return t;
    }

    /// <summary>
    /// DualSense: same identity layout as DS4 for digital + sticks.
    /// </summary>
    public static InputBindingTable DualSense()
    {
        var t = DualShockLayout("DualSense");
        return t;
    }

    private static InputBindingTable DualShockLayout(string name)
    {
        var t = new InputBindingTable(name, ControllerProfile.Standard);
        t.Add(HostSources.DsCross, PadInput.Button.Cross);
        t.Add(HostSources.DsCircle, PadInput.Button.Circle);
        t.Add(HostSources.DsSquare, PadInput.Button.Square);
        t.Add(HostSources.DsTriangle, PadInput.Button.Triangle);
        t.Add(HostSources.DsL1, PadInput.Button.L1);
        t.Add(HostSources.DsR1, PadInput.Button.R1);
        t.Add(HostSources.DsL2, PadInput.Button.L2);
        t.Add(HostSources.DsR2, PadInput.Button.R2);
        t.Add(HostSources.DsStart, PadInput.Button.Start);
        t.Add(HostSources.DsSelect, PadInput.Button.Select);
        t.Add(HostSources.DsUp, PadInput.Button.Up);
        t.Add(HostSources.DsDown, PadInput.Button.Down);
        t.Add(HostSources.DsLeft, PadInput.Button.Left);
        t.Add(HostSources.DsRight, PadInput.Button.Right);
        t.Add(HostSources.DsL3, PadInput.Button.L3);
        t.Add(HostSources.DsR3, PadInput.Button.R3);
        t.Add(HostSources.DsLX, PadAxis.LeftX);
        t.Add(HostSources.DsLY, PadAxis.LeftY);
        t.Add(HostSources.DsRX, PadAxis.RightX);
        t.Add(HostSources.DsRY, PadAxis.RightY);
        return t;
    }

    /// <summary>
    /// Guitar Hero / PDP Riffmaster profile (XInput-mode frets).
    /// Green=A→R2, Red=B→Circle, Yellow=Y→Triangle, Blue=X→Cross, Orange=LB/RB→Square,
    /// Strum D-pad U/D, Start/Select, whammy on right stick Y.
    /// Matches <see cref="HostGamepadService.ApplyGuitarHeroProfile"/> output when fed standard XInput map.
    /// </summary>
    public static InputBindingTable GuitarHero()
    {
        var t = new InputBindingTable("GuitarHero", ControllerProfile.GuitarHero);
        // Frets (Xbox GH / Riffmaster in XInput mode)
        t.Add(HostSources.XiA, PadInput.Button.R2);       // green
        t.Add(HostSources.XiB, PadInput.Button.Circle);   // red
        t.Add(HostSources.XiY, PadInput.Button.Triangle); // yellow
        t.Add(HostSources.XiX, PadInput.Button.Cross);    // blue
        t.Add(HostSources.XiLB, PadInput.Button.Square);  // orange
        t.Add(HostSources.XiRB, PadInput.Button.Square);  // orange alt
        t.Add(HostSources.XiLT, PadInput.Button.L2);      // tilt / extra
        t.Add(HostSources.XiDPadUp, PadInput.Button.Up);
        t.Add(HostSources.XiDPadDown, PadInput.Button.Down);
        t.Add(HostSources.XiDPadLeft, PadInput.Button.Left);
        t.Add(HostSources.XiDPadRight, PadInput.Button.Right);
        t.Add(HostSources.XiStart, PadInput.Button.Start);
        t.Add(HostSources.XiBack, PadInput.Button.Select);
        // Whammy on right stick Y; center X left stick unused for frets
        t.Add(HostSources.XiRY, PadAxis.RightY);
        t.Add(HostSources.XiRX, PadAxis.RightX);
        // Also accept left-stick Y as alternate strum (axis→button)
        t.Add(new InputBinding(HostSources.XiLY, PadInput.Button.Up)
        {
            Invert = true,
            AxisToButtonThreshold = 0.5f
        });
        t.Add(new InputBinding(HostSources.XiLY, PadInput.Button.Down)
        {
            AxisToButtonThreshold = 0.5f
        });
        return t;
    }

    /// <summary>PDP Riffmaster uses the GuitarHero profile table.</summary>
    public static InputBindingTable Riffmaster() => WithName(GuitarHero(), "Riffmaster");

    /// <summary>
    /// Generic HID bit layout matching <c>ParseGenericHid</c> historical mapping.
    /// </summary>
    public static InputBindingTable GenericHid()
    {
        var t = new InputBindingTable("GenericHid", ControllerProfile.Standard);
        t.Add(HostSources.HidBit(0), PadInput.Button.Cross);
        t.Add(HostSources.HidBit(1), PadInput.Button.Circle);
        t.Add(HostSources.HidBit(2), PadInput.Button.Square);
        t.Add(HostSources.HidBit(3), PadInput.Button.Triangle);
        t.Add(HostSources.HidBit(4), PadInput.Button.L1);
        t.Add(HostSources.HidBit(5), PadInput.Button.R1);
        t.Add(HostSources.HidBit(6), PadInput.Button.Select);
        t.Add(HostSources.HidBit(7), PadInput.Button.Start);
        t.Add(HostSources.HidBit(8), PadInput.Button.Up);
        t.Add(HostSources.HidBit(9), PadInput.Button.Down);
        t.Add(HostSources.HidBit(10), PadInput.Button.Left);
        t.Add(HostSources.HidBit(11), PadInput.Button.Right);
        t.Add("hid:LX", PadAxis.LeftX);
        t.Add("hid:LY", PadAxis.LeftY);
        t.Add("hid:RX", PadAxis.RightX);
        t.Add("hid:RY", PadAxis.RightY);
        return t;
    }

    /// <summary>
    /// Resolve the default table for a hardware kind + software profile.
    /// GuitarHero software profile on XInput uses the fret table; other backends keep their
    /// standard map and <see cref="HostGamepadService"/> still runs <c>ApplyGuitarHeroProfile</c>.
    /// </summary>
    public static InputBindingTable Resolve(ControllerHardwareKind kind, ControllerProfile profile)
    {
        if (profile == ControllerProfile.GuitarHero &&
            kind is ControllerHardwareKind.XInput or ControllerHardwareKind.Unknown)
            return GuitarHero();

        return kind switch
        {
            ControllerHardwareKind.Keyboard => Keyboard(),
            ControllerHardwareKind.DualShock4 => DualShock4(),
            ControllerHardwareKind.DualSense => DualSense(),
            // Guitar hardware on XInput slots is still Kind=XInput; HID guitars use generic bits.
            ControllerHardwareKind.GuitarHero => GenericHid(),
            ControllerHardwareKind.XInput => XInput(),
            ControllerHardwareKind.GenericHid => GenericHid(),
            _ => XInput()
        };
    }

    /// <summary>Resolve from device id prefix when kind is unknown.</summary>
    public static InputBindingTable Resolve(string? deviceId, ControllerProfile profile)
    {
        if (string.IsNullOrEmpty(deviceId) || deviceId == "kb")
            return profile == ControllerProfile.GuitarHero ? GuitarHero() : Keyboard();
        if (deviceId.StartsWith("xi:", System.StringComparison.OrdinalIgnoreCase))
            return profile == ControllerProfile.GuitarHero ? GuitarHero() : XInput();
        if (deviceId.StartsWith("hid:", System.StringComparison.OrdinalIgnoreCase))
            return DualShock4(); // HID path classifies; prefer Resolve(kind, profile)
        return XInput();
    }

    private static InputBindingTable WithName(InputBindingTable t, string name)
    {
        t.Name = name;
        return t;
    }
}
