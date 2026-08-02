using System;
using System.Collections.Generic;
using System.Linq;

namespace DetPS2.Core.Input;

/// <summary>
/// Full remappable host→DualShock binding table.
/// Pure apply path is unit-testable without hardware.
/// </summary>
public sealed class InputBindingTable
{
    private readonly List<InputBinding> _bindings = new();
    private readonly Dictionary<string, List<InputBinding>> _bySource =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Display / profile name (e.g. "XInput", "Keyboard", "GuitarHero").</summary>
    public string Name { get; set; } = "Custom";

    /// <summary>
    /// Software profile this table represents. When <see cref="ControllerProfile.GuitarHero"/>,
    /// <see cref="HostGamepadService"/> skips the post-process GH frets remap (already in table).
    /// </summary>
    public ControllerProfile Profile { get; set; } = ControllerProfile.Standard;

    public int Count => _bindings.Count;

    public IReadOnlyList<InputBinding> Bindings => _bindings;

    public InputBindingTable() { }

    public InputBindingTable(string name, ControllerProfile profile = ControllerProfile.Standard)
    {
        Name = name;
        Profile = profile;
    }

    public void Clear()
    {
        _bindings.Clear();
        _bySource.Clear();
    }

    public void Add(InputBinding binding)
    {
        if (binding == null || string.IsNullOrWhiteSpace(binding.SourceId)) return;
        _bindings.Add(binding);
        Index(binding);
    }

    public void Add(string sourceId, PadInput.Button button) =>
        Add(new InputBinding(sourceId, button));

    public void Add(string sourceId, PadAxis axis, bool invert = false) =>
        Add(new InputBinding(sourceId, axis, invert));

    public void AddRange(IEnumerable<InputBinding> bindings)
    {
        foreach (var b in bindings)
            Add(b);
    }

    /// <summary>Replace all sources that target the same button/axis, or append.</summary>
    public void Bind(string sourceId, PadInput.Button button)
    {
        RemoveBySource(sourceId);
        Add(sourceId, button);
    }

    public void Bind(string sourceId, PadAxis axis, bool invert = false)
    {
        RemoveBySource(sourceId);
        Add(sourceId, axis, invert);
    }

    /// <summary>Bind host source to a target name (<c>Cross</c>, <c>LeftX</c>).</summary>
    public void Bind(string sourceId, string targetName, bool invert = false)
    {
        RemoveBySource(sourceId);
        var b = new InputBinding { SourceId = sourceId, Invert = invert };
        b.SetTargetFromName(targetName);
        Add(b);
    }

    public void RemoveBySource(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        _bindings.RemoveAll(b => string.Equals(b.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        _bySource.Remove(sourceId);
    }

    /// <summary>Remove bindings that target a given button.</summary>
    public void UnbindTarget(PadInput.Button button)
    {
        var remove = _bindings.Where(b => b.TargetKind == PadTargetKind.Button && b.TargetButton == button).ToList();
        foreach (var b in remove)
        {
            _bindings.Remove(b);
            if (_bySource.TryGetValue(b.SourceId, out var list))
            {
                list.Remove(b);
                if (list.Count == 0) _bySource.Remove(b.SourceId);
            }
        }
    }

    public void UnbindTarget(PadAxis axis)
    {
        var remove = _bindings.Where(b => b.TargetKind == PadTargetKind.Axis && b.TargetAxis == axis).ToList();
        foreach (var b in remove)
        {
            _bindings.Remove(b);
            if (_bySource.TryGetValue(b.SourceId, out var list))
            {
                list.Remove(b);
                if (list.Count == 0) _bySource.Remove(b.SourceId);
            }
        }
    }

    public bool TryMapSource(string sourceId, out InputBinding? binding)
    {
        binding = null;
        if (!_bySource.TryGetValue(sourceId, out var list) || list.Count == 0)
            return false;
        binding = list[0];
        return true;
    }

    /// <summary>Keyboard helper: <paramref name="keyName"/> without <c>kb:</c> prefix (e.g. <c>Z</c>).</summary>
    public bool TryMapKey(string keyName, out PadInput.Button button)
    {
        button = PadInput.Button.None;
        if (string.IsNullOrEmpty(keyName)) return false;
        string id = keyName.StartsWith("kb:", StringComparison.OrdinalIgnoreCase)
            ? keyName
            : HostSources.Keyboard(keyName);
        if (!_bySource.TryGetValue(id, out var list)) return false;
        foreach (var b in list)
        {
            if (b.TargetKind == PadTargetKind.Button && b.TargetButton != PadInput.Button.None)
            {
                button = b.TargetButton;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Apply host state through this table into digital buttons + stick bytes.
    /// Pure function-style: does not read hardware.
    /// </summary>
    public void Apply(HostInputState host, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry)
    {
        buttons = 0;
        lx = ly = rx = ry = 0x80;
        if (host == null) return;

        // Accumulate stick from last matching binding (typically one per axis).
        float? flx = null, fly = null, frx = null, fry = null;

        foreach (var b in _bindings)
        {
            if (b.TargetKind == PadTargetKind.Button)
            {
                if (b.TargetButton == PadInput.Button.None) continue;
                float thr = b.AxisToButtonThreshold > 0f
                    ? b.AxisToButtonThreshold
                    : DefaultAxisButtonThreshold(b.SourceId);
                if (host.IsSourceActive(b.SourceId, thr, b.Invert))
                    buttons |= (uint)b.TargetButton;
            }
            else
            {
                if (!host.TryGetAxis(b.SourceId, out float v) && !host.IsDown(b.SourceId))
                    continue;
                if (!host.TryGetAxis(b.SourceId, out v))
                    v = host.IsDown(b.SourceId) ? (b.Invert ? -1f : 1f) : 0f;
                if (b.Invert) v = -v;
                switch (b.TargetAxis)
                {
                    case PadAxis.LeftX: flx = v; break;
                    case PadAxis.LeftY: fly = v; break;
                    case PadAxis.RightX: frx = v; break;
                    case PadAxis.RightY: fry = v; break;
                }
            }
        }

        if (flx.HasValue) lx = StickFloatToByte(flx.Value);
        if (fly.HasValue) ly = StickFloatToByte(fly.Value);
        if (frx.HasValue) rx = StickFloatToByte(frx.Value);
        if (fry.HasValue) ry = StickFloatToByte(fry.Value);
    }

    public void ApplyTo(PadInput pad, HostInputState host)
    {
        Apply(host, out uint buttons, out byte lx, out byte ly, out byte rx, out byte ry);
        pad.SetButtons(buttons);
        pad.SetLeftStick(lx, ly);
        pad.SetRightStick(rx, ry);
    }

    /// <summary>Clone table (deep copy of bindings).</summary>
    public InputBindingTable Clone()
    {
        var t = new InputBindingTable(Name, Profile);
        foreach (var b in _bindings)
            t.Add(b.Clone());
        return t;
    }

    public List<InputBindingEntry> ToEntries()
    {
        var list = new List<InputBindingEntry>(_bindings.Count);
        foreach (var b in _bindings)
            list.Add(InputBindingEntry.FromBinding(b));
        return list;
    }

    public static InputBindingTable FromEntries(
        IEnumerable<InputBindingEntry>? entries,
        string name = "Custom",
        ControllerProfile profile = ControllerProfile.Standard)
    {
        var t = new InputBindingTable(name, profile);
        if (entries == null) return t;
        foreach (var e in entries)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Source)) continue;
            t.Add(e.ToBinding());
        }
        return t;
    }

    /// <summary>
    /// Merge non-empty custom entries over a base table (custom sources override base sources).
    /// </summary>
    public static InputBindingTable MergeOver(InputBindingTable basemap, IEnumerable<InputBindingEntry>? custom)
    {
        var t = basemap.Clone();
        if (custom == null) return t;
        foreach (var e in custom)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Source)) continue;
            t.RemoveBySource(e.Source);
            t.Add(e.ToBinding());
        }
        return t;
    }

    public static byte StickFloatToByte(float v)
    {
        // Deadzone near center
        if (v > -0.12f && v < 0.12f) return 0x80;
        int n = (int)Math.Round((v + 1f) * 0.5f * 255f);
        return (byte)Math.Clamp(n, 0, 255);
    }

    public static float ShortToStickFloat(short v)
    {
        if (v > -4000 && v < 4000) return 0f;
        return Math.Clamp(v / 32768f, -1f, 1f);
    }

    public static float ByteAxisToFloat(byte b)
    {
        if (b > 0x70 && b < 0x90) return 0f;
        return Math.Clamp((b / 255f) * 2f - 1f, -1f, 1f);
    }

    private static float DefaultAxisButtonThreshold(string sourceId)
    {
        // Triggers use 0..1; match HostGamepad L2/R2 threshold (~30/255)
        if (sourceId.EndsWith(":LT", StringComparison.OrdinalIgnoreCase) ||
            sourceId.EndsWith(":RT", StringComparison.OrdinalIgnoreCase) ||
            sourceId.EndsWith(":L2", StringComparison.OrdinalIgnoreCase) ||
            sourceId.EndsWith(":R2", StringComparison.OrdinalIgnoreCase))
            return 30f / 255f;
        return 0.55f;
    }

    private void Index(InputBinding binding)
    {
        if (!_bySource.TryGetValue(binding.SourceId, out var list))
        {
            list = new List<InputBinding>(2);
            _bySource[binding.SourceId] = list;
        }
        list.Add(binding);
    }
}
