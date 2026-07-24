using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DetPS2.Core;

/// <summary>
/// Emulator telemetry (Phase 21): unknown opcode / MMIO / syscall hits with PC + MasterCycles.
/// No host clocks — cycle stamps come from the core.
/// </summary>
public sealed class Telemetry
{
    public enum Kind
    {
        UnknownOpcode = 1,
        UnknownSpecial = 2,
        UnknownMmioRead = 3,
        UnknownMmioWrite = 4,
        UnknownSyscall = 5,
        Other = 9
    }

    public readonly struct Event
    {
        public Kind Kind { get; init; }
        public ulong Cycle { get; init; }
        public ulong Pc { get; init; }
        public uint Key { get; init; } // opcode, address, or syscall number
        public string Detail { get; init; }
    }

    private readonly object _lock = new();
    private readonly List<Event> _events = new();
    private readonly Dictionary<(Kind kind, uint key), int> _counts = new();

    public bool Enabled { get; set; } = true;
    public int MaxEvents { get; set; } = 4096;
    public ulong TotalHits { get; private set; }
    public int UniqueKeys
    {
        get { lock (_lock) return _counts.Count; }
    }

    public int EventCount
    {
        get { lock (_lock) return _events.Count; }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _events.Clear();
            _counts.Clear();
            TotalHits = 0;
        }
    }

    public void Record(Kind kind, ulong cycle, ulong pc, uint key, string detail = "")
    {
        if (!Enabled) return;
        lock (_lock)
        {
            TotalHits++;
            var ck = (kind, key);
            _counts.TryGetValue(ck, out int c);
            _counts[ck] = c + 1;

            if (_events.Count >= MaxEvents)
                _events.RemoveAt(0);
            _events.Add(new Event
            {
                Kind = kind,
                Cycle = cycle,
                Pc = pc,
                Key = key,
                Detail = detail ?? ""
            });
        }
    }

    public void UnknownOpcode(ulong cycle, ulong pc, uint opcode) =>
        Record(Kind.UnknownOpcode, cycle, pc, opcode, $"primary=0x{(opcode >> 26) & 0x3F:X2}");

    public void UnknownSpecial(ulong cycle, ulong pc, uint opcode) =>
        Record(Kind.UnknownSpecial, cycle, pc, opcode & 0x3F, $"funct=0x{opcode & 0x3F:X2}");

    public void UnknownMmioRead(ulong cycle, ulong pc, uint address) =>
        Record(Kind.UnknownMmioRead, cycle, pc, address, "R");

    public void UnknownMmioWrite(ulong cycle, ulong pc, uint address) =>
        Record(Kind.UnknownMmioWrite, cycle, pc, address, "W");

    public void UnknownSyscall(ulong cycle, ulong pc, uint number) =>
        Record(Kind.UnknownSyscall, cycle, pc, number, $"sys=0x{number:X}");

    public IReadOnlyList<Event> SnapshotEvents()
    {
        lock (_lock)
            return _events.ToArray();
    }

    public IReadOnlyList<(Kind kind, uint key, int count)> TopBlockers(int n = 20)
    {
        lock (_lock)
        {
            return _counts
                .OrderByDescending(kv => kv.Value)
                .Take(n)
                .Select(kv => (kv.Key.kind, kv.Key.key, kv.Value))
                .ToList();
        }
    }

    public int CountOf(Kind kind)
    {
        lock (_lock)
        {
            int sum = 0;
            foreach (var kv in _counts)
                if (kv.Key.kind == kind) sum += kv.Value;
            return sum;
        }
    }

    public string FormatReport(int top = 20)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Telemetry hits={TotalHits} unique={UniqueKeys} events={EventCount} enabled={Enabled}");
        foreach (var (kind, key, count) in TopBlockers(top))
            sb.AppendLine($"  {count,8}  {kind,-18} key=0x{key:X8}");
        return sb.ToString();
    }

    /// <summary>JSON dump of top blockers + recent events (BootTrace v2 companion).</summary>
    public string ToJson(int top = 50, int recent = 64)
    {
        var topList = TopBlockers(top).Select(t => new
        {
            kind = t.kind.ToString(),
            key = $"0x{t.key:X8}",
            keyU32 = t.key,
            count = t.count
        }).ToList();

        var recentList = SnapshotEvents().TakeLast(recent).Select(e => new
        {
            kind = e.Kind.ToString(),
            cycle = e.Cycle,
            pc = $"0x{e.Pc:X8}",
            key = $"0x{e.Key:X8}",
            detail = e.Detail
        }).ToList();

        var payload = new
        {
            totalHits = TotalHits,
            uniqueKeys = UniqueKeys,
            topBlockers = topList,
            recentEvents = recentList
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// Compatibility row schema (Phase 21). Serialized for tools / COMPAT tooling.
/// </summary>
public sealed class CompatEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Region { get; set; } = "";
    public string Serial { get; set; } = "";
    /// <summary>P0, P1, P2, P3, P4, DX, Untested</summary>
    public string Tier { get; set; } = "Untested";
    public string Notes { get; set; } = "";
    public string LastPc { get; set; } = "";
    public string BlockerTags { get; set; } = ""; // EE_OP,GS_FMT,...
    public ulong MasterCycles { get; set; }

    public static bool IsValidTier(string tier) =>
        tier is "P0" or "P1" or "P2" or "P3" or "P4" or "DX" or "Untested";

    public static CompatEntry ParseLine(string line)
    {
        // CSV: id,title,region,serial,tier,notes,lastPc,blockerTags
        var parts = SplitCsv(line);
        return new CompatEntry
        {
            Id = Get(parts, 0),
            Title = Get(parts, 1),
            Region = Get(parts, 2),
            Serial = Get(parts, 3),
            Tier = string.IsNullOrEmpty(Get(parts, 4)) ? "Untested" : Get(parts, 4),
            Notes = Get(parts, 5),
            LastPc = Get(parts, 6),
            BlockerTags = Get(parts, 7)
        };
    }

    public string ToCsvLine() =>
        $"{Esc(Id)},{Esc(Title)},{Esc(Region)},{Esc(Serial)},{Esc(Tier)},{Esc(Notes)},{Esc(LastPc)},{Esc(BlockerTags)}";

    private static string Get(string[] p, int i) => i < p.Length ? p[i].Trim() : "";
    private static string Esc(string s) =>
        s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    private static string[] SplitCsv(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        foreach (char c in line)
        {
            if (c == '"') { q = !q; continue; }
            if (c == ',' && !q) { list.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        list.Add(sb.ToString());
        return list.ToArray();
    }
}

/// <summary>Load / query target catalog markdown or CSV tables.</summary>
public static class TargetCatalog
{
    public const int MinimumTitleCount = 200;

    /// <summary>
    /// Parse titles from TARGET_CATALOG.md style lines:
    /// | id | Title | Region | Serial |
    /// or plain: Title (REGION)
    /// </summary>
    public static List<CompatEntry> ParseMarkdownTable(string markdown)
    {
        var list = new List<CompatEntry>();
        foreach (var raw in markdown.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith('|')) continue;
            if (line.Contains("---")) continue;
            if (line.Contains("Title", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Region", StringComparison.OrdinalIgnoreCase))
                continue;

            var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cells.Length < 2) continue;
            // id | title | region | serial
            string id = cells.Length >= 1 ? cells[0] : "";
            string title = cells.Length >= 2 ? cells[1] : id;
            if (string.IsNullOrWhiteSpace(title) || title.Equals("id", StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add(new CompatEntry
            {
                Id = string.IsNullOrWhiteSpace(id) ? SanitizeId(title) : id,
                Title = title,
                Region = cells.Length >= 3 ? cells[2] : "",
                Serial = cells.Length >= 4 ? cells[3] : "",
                Tier = "Untested"
            });
        }
        return list;
    }

    public static string SanitizeId(string title)
    {
        var sb = new StringBuilder();
        foreach (char c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_') sb.Append('-');
        }
        string s = sb.ToString().Trim('-');
        while (s.Contains("--", StringComparison.Ordinal))
            s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Length > 48 ? s[..48] : s;
    }

    public static double MajorityPercent(IReadOnlyList<CompatEntry> entries)
    {
        int nonDx = 0, p2plus = 0;
        foreach (var e in entries)
        {
            if (e.Tier == "DX" || e.Tier == "Untested") continue;
            // Untested not counted in denominator for majority-of-tested;
            // plan: majority of (catalog − DX). Untested counts as not-P2.
            if (e.Tier == "DX") continue;
        }
        // Recalc per plan: denominator = catalog − DX (includes Untested as fail)
        nonDx = 0;
        p2plus = 0;
        foreach (var e in entries)
        {
            if (e.Tier == "DX") continue;
            nonDx++;
            if (e.Tier is "P2" or "P3" or "P4") p2plus++;
        }
        return nonDx == 0 ? 0 : (double)p2plus / nonDx;
    }
}
