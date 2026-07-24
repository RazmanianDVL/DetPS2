using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Execution tracer with cycle stamps (Phase 11).
/// In-memory ring + optional file; diff-friendly line format.
/// </summary>
public sealed class Tracer
{
    public readonly struct Entry
    {
        public ulong Cycle { get; init; }
        public ulong Pc { get; init; }
        public uint Opcode { get; init; }
        public string? Note { get; init; }
    }

    private readonly List<Entry> _entries = new();
    private StreamWriter? _writer;
    private bool _enabled;
    private int _maxEntries = 100_000;

    public bool Enabled => _enabled;
    public int Count => _entries.Count;
    public int MaxEntries
    {
        get => _maxEntries;
        set => _maxEntries = Math.Max(16, value);
    }

    public IReadOnlyList<Entry> Entries => _entries;

    public void Enable(string? filename = null)
    {
        _enabled = true;
        if (filename != null)
        {
            _writer?.Dispose();
            _writer = new StreamWriter(filename, append: false, Encoding.UTF8);
        }
    }

    public void Disable()
    {
        _enabled = false;
        if (_writer != null)
        {
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
        }
    }

    public void Clear() => _entries.Clear();

    public void LogInstruction(ulong cycle, ulong pc, uint opcode)
    {
        if (!_enabled) return;
        var e = new Entry { Cycle = cycle, Pc = pc, Opcode = opcode };
        Append(e);
        _writer?.WriteLine(Format(e));
    }

    public void Log(ulong cycle, string message)
    {
        if (!_enabled) return;
        var e = new Entry { Cycle = cycle, Pc = 0, Opcode = 0, Note = message };
        Append(e);
        _writer?.WriteLine($"C={cycle} {message}");
    }

    // Back-compat
    public void LogInstruction(ulong pc, uint opcode, int cycles) =>
        LogInstruction((ulong)cycles, pc, opcode);

    public void Log(string message) => Log(0, message);

    private void Append(Entry e)
    {
        if (_entries.Count >= _maxEntries)
            _entries.RemoveAt(0);
        _entries.Add(e);
    }

    public static string Format(Entry e)
    {
        if (e.Note != null)
            return $"C={e.Cycle} NOTE {e.Note}";
        return $"C={e.Cycle} PC=0x{e.Pc:X8} OP=0x{e.Opcode:X8}";
    }

    /// <summary>
    /// Diff two traces; returns lines present only in a or b (simple set diff by formatted line).
    /// See docs/TRACE_DIFF.md.
    /// </summary>
    public static string Diff(IReadOnlyList<Entry> a, IReadOnlyList<Entry> b, int maxLines = 50)
    {
        var sa = new HashSet<string>();
        var sb = new HashSet<string>();
        foreach (var e in a) sa.Add(Format(e));
        foreach (var e in b) sb.Add(Format(e));

        var sbOut = new StringBuilder();
        int n = 0;
        foreach (var line in sa)
        {
            if (!sb.Contains(line))
            {
                sbOut.AppendLine("- " + line);
                if (++n >= maxLines) break;
            }
        }
        foreach (var line in sb)
        {
            if (!sa.Contains(line))
            {
                sbOut.AppendLine("+ " + line);
                if (++n >= maxLines) break;
            }
        }
        if (n == 0) sbOut.AppendLine("(identical)");
        return sbOut.ToString();
    }

    public string ExportText()
    {
        var sb = new StringBuilder();
        foreach (var e in _entries)
            sb.AppendLine(Format(e));
        return sb.ToString();
    }
}
