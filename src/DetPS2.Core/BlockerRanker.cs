using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DetPS2.Core;

/// <summary>
/// Phase 41: aggregate telemetry / boot-report blockers and rank for global fixes.
/// </summary>
public sealed class BlockerRanker
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public int UniqueKeys => _counts.Count;
    public int TotalHits => _counts.Values.Sum();

    public void Add(string key, int count = 1)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _counts.TryGetValue(key, out int c);
        _counts[key] = c + count;
    }

    public void IngestTelemetry(Telemetry t)
    {
        foreach (var (kind, key, count) in t.TopBlockers(1000))
            Add($"{kind}:0x{key:X8}", count);
    }

    public void IngestBootResult(CommercialBootRunner.BootResult r)
    {
        foreach (string b in r.TopBlockers)
            Add(b, 1);
        if (r.Crashed && !string.IsNullOrEmpty(r.CrashReason))
            Add("CRASH:" + r.CrashReason, 1);
    }

    public void IngestReport(CommercialBootRunner.RunReport report)
    {
        foreach (var r in report.Results)
            IngestBootResult(r);
    }

    public IReadOnlyList<(string key, int count)> Rank(int top = 32) =>
        _counts.OrderByDescending(kv => kv.Value).Take(top).Select(kv => (kv.Key, kv.Value)).ToList();

    public string FormatReport(int top = 20)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BlockerRanker totalHits={TotalHits} unique={UniqueKeys}");
        foreach (var (key, count) in Rank(top))
            sb.AppendLine($"  {count,8}  {key}");
        return sb.ToString();
    }

    public void Reset() => _counts.Clear();
}

/// <summary>
/// Phase 41: known high-frequency BIOS/game stubs that return safe Det values.
/// </summary>
public static class BootBlockerFixes
{
    // Expanded syscall numbers often hit during commercial boots (HLE-friendly)
    public const uint SysGetCop0 = 0x07;
    public const uint SysSetCop0 = 0x08;
    public const uint SysRFU060 = 0x3C; // SetupThread (Sony)
    public const uint SysRFU061 = 0x3D; // SetupHeap (Sony)
    public const uint SysEndOfHeap = 0x3E;
    public const uint SysGsPutDrawEnv = 0x25;
    public const uint SysGsPutDisplayEnv = 0x26;
    public const uint SysSifLoadModuleBuffer = 0x84;
    public const uint SysSifCheckStatModule = 0x85;
    public const uint SysDeci2Call = 0x7C;
    public const uint SysKSeg0 = 0x63;
    /// <summary>Sony EE kernel: FindAddress (psdevwiki).</summary>
    public const uint SysFindAddress = 0x83;
    public const uint SysSetMemoryMode = 0x85;
    // Sony thread/sema block (subset) — commercial titles
    public const uint SonyCreateThread = 0x20;
    public const uint SonyDeleteThread = 0x21;
    public const uint SonyStartThread = 0x22;
    public const uint SonyExitThread = 0x23;
    public const uint SonySleepThread = 0x32;
    public const uint SonyWakeupThread = 0x33;
    public const uint SonyCreateSema = 0x40;
    public const uint SonyDeleteSema = 0x41;
    public const uint SonySignalSema = 0x42;
    public const uint SonyWaitSema = 0x44;
    public const uint SonyPollSema = 0x46;
    public const uint SonyFlushCache = 0x64;
    public const uint SonyGsSetCrt = 0x02;
    public const uint SonySifSetDma = 0x76;
    public const uint SonySifDmaStat = 0x77;
    public const uint SonySifSetReg = 0x78;
    public const uint SonySifGetReg = 0x79;
    public const uint SonySifLoadModule = 0x7A;

    public static readonly HashSet<uint> KnownSafeSyscalls = new()
    {
        SysGetCop0, SysSetCop0, SysRFU060, SysRFU061, SysEndOfHeap,
        SysGsPutDrawEnv, SysGsPutDisplayEnv,
        SysSifLoadModuleBuffer, SysSifCheckStatModule,
        SysDeci2Call, SysKSeg0, SysFindAddress, SysSetMemoryMode,
        SonyFlushCache, SonyGsSetCrt, SonySifDmaStat, SonySifSetReg, SonySifGetReg,
        SonyExitThread, SonySleepThread
    };
}
