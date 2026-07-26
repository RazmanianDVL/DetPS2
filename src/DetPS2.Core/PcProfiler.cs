using System;
using System.Collections.Generic;
using System.Linq;

namespace DetPS2.Core;

/// <summary>
/// Opt-in PC-visit histogram (--profile-pc). Cheap enough to run continuously: a single
/// dictionary bump per instruction. Built to answer "where is the CPU actually spending
/// its time" when other counters (syscalls, GIF transfers, telemetry) go flat for tens of
/// millions of cycles but PC keeps moving — a tight, hot loop shows up as a handful of
/// addresses with counts orders of magnitude above everything else; genuinely varied
/// forward progress shows a flat, broad distribution instead.
/// </summary>
public static class PcProfiler
{
    public static bool Enabled = Environment.GetEnvironmentVariable("DETPS2_PROFILE_PC") == "1";
    private static readonly Dictionary<uint, ulong> _counts = new();

    public static void Reset() => _counts.Clear();

    public static void Sample(ulong pc)
    {
        uint key = (uint)(pc & 0xFFFFFFFFUL);
        _counts.TryGetValue(key, out ulong c);
        _counts[key] = c + 1;
    }

    public static IReadOnlyList<(uint pc, ulong count)> Top(int n) =>
        _counts.OrderByDescending(kv => kv.Value).Take(n).Select(kv => (kv.Key, kv.Value)).ToList();

    public static int UniqueCount => _counts.Count;
    public static ulong TotalSamples => (ulong)_counts.Values.Aggregate(0UL, (a, b) => a + b);
}
