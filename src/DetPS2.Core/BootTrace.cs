using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DetPS2.Core;

/// <summary>
/// BIOS/boot progress tracer (Phase 9 + Phase 21 v2 JSON dump).
/// </summary>
public sealed class BootTrace
{
    public readonly struct Sample
    {
        public ulong Cycle { get; init; }
        public ulong Pc { get; init; }
    }

    private readonly List<Sample> _samples = new();
    public int MaxSamples { get; set; } = 256;
    public IReadOnlyList<Sample> Samples => _samples;
    public bool Crashed { get; private set; }
    public string? CrashReason { get; private set; }

    public void Reset()
    {
        _samples.Clear();
        Crashed = false;
        CrashReason = null;
    }

    public void Record(ulong cycle, ulong pc)
    {
        if (_samples.Count >= MaxSamples)
            _samples.RemoveAt(0);
        _samples.Add(new Sample { Cycle = cycle, Pc = pc });
    }

    public void MarkCrash(string reason)
    {
        Crashed = true;
        CrashReason = reason;
    }

    public void RunWithTrace(Ps2System system, ulong totalCycles, ulong sampleEvery = 10_000)
    {
        Reset();
        ulong left = totalCycles;
        ulong lastPc = system.EE.PC;
        int stuck = 0;

        while (left > 0)
        {
            ulong chunk = Math.Min(left, sampleEvery);
            system.RunFor(chunk);
            left -= chunk;
            Record(system.MasterCycles, system.EE.PC);

            if (system.EE.PC == lastPc)
            {
                stuck++;
                if (stuck > 32 && system.Memory.Read32(system.EE.PC) == 0)
                {
                    MarkCrash($"PC stuck at 0x{system.EE.PC:X8} reading NOP/zero for extended run");
                    break;
                }
            }
            else stuck = 0;
            lastPc = system.EE.PC;

            if (system.Hle.ExitRequested)
                break;
        }
    }

    public string FormatReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BootTrace samples={_samples.Count} crashed={Crashed} reason={CrashReason ?? "none"}");
        int n = Math.Min(16, _samples.Count);
        for (int i = _samples.Count - n; i < _samples.Count; i++)
        {
            if (i < 0) continue;
            var s = _samples[i];
            sb.AppendLine($"  cyc={s.Cycle,12} PC=0x{s.Pc:X8}");
        }
        return sb.ToString();
    }

    /// <summary>Phase 21: JSON dump including telemetry top blockers.</summary>
    public string ToJson(Ps2System? system = null)
    {
        var samples = new List<object>();
        foreach (var s in _samples)
        {
            samples.Add(new { cycle = s.Cycle, pc = $"0x{s.Pc:X8}" });
        }

        object? telemetry = null;
        if (system != null)
        {
            telemetry = new
            {
                totalHits = system.Telemetry.TotalHits,
                uniqueKeys = system.Telemetry.UniqueKeys,
                report = system.Telemetry.FormatReport(32),
                top = system.Telemetry.TopBlockers(32).Select(t => new
                {
                    kind = t.kind.ToString(),
                    key = $"0x{t.key:X8}",
                    count = t.count
                })
            };
        }

        var payload = new
        {
            version = 2,
            crashed = Crashed,
            crashReason = CrashReason,
            sampleCount = _samples.Count,
            samples,
            telemetry
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
