using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// PL-002: cycle-scheduled pad events for interactive (T2) claim runs.
/// File / inline script — never invents game state; only drives <see cref="PadInput"/>.
/// </summary>
public sealed class PadScript
{
    public readonly struct Event
    {
        public ulong PressAt { get; init; }
        public ulong ReleaseAt { get; init; }
        public PadInput.Button Button { get; init; }
        public string Name { get; init; }
        public ulong Hold => ReleaseAt > PressAt ? ReleaseAt - PressAt : 0;
    }

    public IReadOnlyList<Event> Events { get; }
    public string Source { get; }

    public PadScript(IReadOnlyList<Event> events, string source = "")
    {
        Events = events ?? Array.Empty<Event>();
        Source = source ?? "";
    }

    public static PadScript Empty { get; } = new(Array.Empty<Event>(), "");

    /// <summary>Default hold when a line omits the hold field (50k cycles ≈ 1 frame-ish at 50M/s).</summary>
    public const ulong DefaultHoldCycles = 50_000;

    /// <summary>
    /// Parse a pad-script file.
    /// <para>Line forms (blank / <c>#</c> comments ignored):</para>
    /// <list type="bullet">
    /// <item><c>@CYCLE BUTTON [HOLD]</c></item>
    /// <item><c>CYCLE BUTTON [HOLD]</c></item>
    /// <item><c>press CYCLE BUTTON [HOLD]</c></item>
    /// </list>
    /// BUTTON names match <see cref="PadInput.Button"/> (Start, Cross, …). HOLD defaults to 50000.
    /// </summary>
    public static PadScript LoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("pad-script path required", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException($"pad-script not found: {path}", path);
        string text = File.ReadAllText(path);
        return Parse(text, path);
    }

    public static PadScript Parse(string text, string source = "<inline>")
    {
        var list = new List<Event>();
        if (string.IsNullOrWhiteSpace(text))
            return new PadScript(list, source);

        int lineNo = 0;
        foreach (string rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
        {
            lineNo++;
            string line = rawLine.Trim();
            if (line.Length == 0) continue;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash).Trim();
            if (line.Length == 0) continue;

            if (!TryParseLine(line, out Event ev, out string? err))
                throw new FormatException($"pad-script {source}:{lineNo}: {err} (line: {rawLine.Trim()})");
            list.Add(ev);
        }

        list.Sort((a, b) => a.PressAt.CompareTo(b.PressAt));
        return new PadScript(list, source);
    }

    /// <summary>
    /// Merge CLI <c>--press=BUTTON:CYCLE[:HOLD]</c> args with optional script events.
    /// </summary>
    public static PadScript Merge(PadScript? script, IEnumerable<Event>? extra)
    {
        var list = new List<Event>();
        if (script != null)
            list.AddRange(script.Events);
        if (extra != null)
            list.AddRange(extra);
        list.Sort((a, b) => a.PressAt.CompareTo(b.PressAt));
        string src = script?.Source ?? "";
        if (extra != null)
            src = string.IsNullOrEmpty(src) ? "<cli>" : src + "+cli";
        return new PadScript(list, src);
    }

    public static bool TryParsePressArg(string arg, out Event ev, out string? error)
    {
        ev = default;
        error = null;
        // --press=BUTTON:CYCLE[:HOLD]  or bare BUTTON:CYCLE[:HOLD]
        string body = arg.StartsWith("--press=", StringComparison.OrdinalIgnoreCase)
            ? arg.Substring("--press=".Length)
            : arg;
        var parts = body.Split(':');
        if (parts.Length < 2)
        {
            error = "need BUTTON:CYCLE[:HOLD]";
            return false;
        }
        if (!Enum.TryParse(parts[0], ignoreCase: true, out PadInput.Button btn) || btn == PadInput.Button.None)
        {
            error = $"unknown button '{parts[0]}'";
            return false;
        }
        if (!ulong.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong pressAt))
        {
            error = $"bad cycle '{parts[1]}'";
            return false;
        }
        ulong hold = DefaultHoldCycles;
        if (parts.Length > 2 && !ulong.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out hold))
        {
            error = $"bad hold '{parts[2]}'";
            return false;
        }
        if (hold == 0) hold = DefaultHoldCycles;
        ev = new Event
        {
            PressAt = pressAt,
            ReleaseAt = pressAt + hold,
            Button = btn,
            Name = parts[0],
        };
        return true;
    }

    private static bool TryParseLine(string line, out Event ev, out string? error)
    {
        ev = default;
        error = null;
        // Normalize: optional leading "press", optional leading '@'
        string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            error = "expected CYCLE BUTTON [HOLD]";
            return false;
        }

        int i = 0;
        if (tokens[0].Equals("press", StringComparison.OrdinalIgnoreCase) ||
            tokens[0].Equals("tap", StringComparison.OrdinalIgnoreCase))
        {
            i = 1;
            if (tokens.Length < 3)
            {
                error = "expected press CYCLE BUTTON [HOLD]";
                return false;
            }
        }

        string cycleTok = tokens[i];
        if (cycleTok.StartsWith("@", StringComparison.Ordinal))
            cycleTok = cycleTok.Substring(1);
        if (!ulong.TryParse(cycleTok, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong pressAt))
        {
            error = $"bad cycle '{tokens[i]}'";
            return false;
        }

        string btnTok = tokens[i + 1];
        if (!Enum.TryParse(btnTok, ignoreCase: true, out PadInput.Button btn) || btn == PadInput.Button.None)
        {
            error = $"unknown button '{btnTok}' — valid: {string.Join(",", Enum.GetNames(typeof(PadInput.Button)))}";
            return false;
        }

        ulong hold = DefaultHoldCycles;
        if (tokens.Length > i + 2)
        {
            string holdTok = tokens[i + 2];
            if (holdTok.StartsWith("hold=", StringComparison.OrdinalIgnoreCase))
                holdTok = holdTok.Substring(5);
            if (!ulong.TryParse(holdTok, NumberStyles.Integer, CultureInfo.InvariantCulture, out hold))
            {
                error = $"bad hold '{tokens[i + 2]}'";
                return false;
            }
        }
        if (hold == 0) hold = DefaultHoldCycles;

        ev = new Event
        {
            PressAt = pressAt,
            ReleaseAt = pressAt + hold,
            Button = btn,
            Name = btnTok,
        };
        return true;
    }

    /// <summary>
    /// Apply press/release at <paramref name="cycle"/> against <paramref name="pad"/>.
    /// Call once per step after advancing to <paramref name="cycle"/> (or ≥).
    /// </summary>
    public int ApplyAt(PadInput pad, ulong cycle, ref int nextEventIndex, List<(ulong releaseAt, PadInput.Button button, string name)> pending)
    {
        if (pad == null) throw new ArgumentNullException(nameof(pad));
        if (pending == null) throw new ArgumentNullException(nameof(pending));
        int fired = 0;
        while (nextEventIndex < Events.Count && Events[nextEventIndex].PressAt <= cycle)
        {
            var e = Events[nextEventIndex++];
            pad.Press(e.Button);
            pending.Add((e.ReleaseAt, e.Button, e.Name));
            fired++;
        }
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (pending[i].releaseAt > cycle) continue;
            pad.Release(pending[i].button);
            pending.RemoveAt(i);
            fired++;
        }
        return fired;
    }
}
