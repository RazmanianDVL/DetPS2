using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// M4-S4: after a real UDNL/IOPRP ASCII tag is known, mirror those 4 digits into
/// registered EE RDRAM cells (class-B consumers that memcmp BSS, not GetVersion RPC).
/// Flag-gated: <c>DETPS2_MIRROR_IOPRP_CELLS=1</c> enables writes. Default off.
/// v1: cells only (no pointer cells); registry populated by title assist Register calls.
/// </summary>
public static class IopRpEeVersionMirror
{
    private static readonly List<uint> Cells = new(4);
    private static readonly object Gate = new();

    /// <summary>True when mirror writes are enabled (env =1 / true).</summary>
    public static bool Enabled
    {
        get
        {
            if (RealSifRpc.GetVersionClassicOverride) return false;
            string? v = Environment.GetEnvironmentVariable("DETPS2_MIRROR_IOPRP_CELLS");
            return v is "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Clear()
    {
        lock (Gate) Cells.Clear();
    }

    /// <summary>Register an EE RDRAM address of a 4-byte version placeholder (e.g. GoW 0x2C6D30).</summary>
    public static void RegisterCell(uint eeAddr)
    {
        if (eeAddr == 0 || eeAddr + 4 > (uint)SystemMemory.RDRAM_SIZE) return;
        lock (Gate)
        {
            if (!Cells.Contains(eeAddr))
                Cells.Add(eeAddr);
        }
    }

    /// <summary>
    /// Write the 4-char ASCII tag into all registered cells when enabled and tag non-empty.
    /// Overwrites placeholder ("...."/0) or any content that is not already the tag (re-scrub).
    /// </summary>
    public static int TryApply(SystemMemory mem, string? tagAscii)
    {
        if (!Enabled || mem == null) return 0;
        if (string.IsNullOrEmpty(tagAscii) || tagAscii.Length < 4) return 0;
        string tag = tagAscii.Length > 4 ? tagAscii[..4] : tagAscii;
        int wrote = 0;
        uint[] snapshot;
        lock (Gate) snapshot = Cells.ToArray();
        foreach (uint addr in snapshot)
        {
            if (addr + 4 > (uint)SystemMemory.RDRAM_SIZE) continue;
            // Skip if already exact tag (avoid thrash).
            bool same = true;
            for (int i = 0; i < 4; i++)
            {
                if (mem.Read8(addr + (uint)i) != (byte)tag[i]) { same = false; break; }
            }
            if (same) continue;
            for (int i = 0; i < 4; i++)
                mem.Write8(addr + (uint)i, (byte)tag[i]);
            wrote++;
        }
        if (wrote > 0 && Environment.GetEnvironmentVariable("DETPS2_TRACE_RPC") == "1")
            Console.Error.WriteLine($"[S4-MIRROR] wrote tag=\"{tag}\" cells={wrote}/{snapshot.Length}");
        return wrote;
    }
}
