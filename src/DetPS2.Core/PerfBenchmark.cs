using System;
using System.Diagnostics;

namespace DetPS2.Core;

/// <summary>
/// Phase 45: host-side performance benchmarks (never inside core Step).
/// </summary>
public static class PerfBenchmark
{
    public sealed class Result
    {
        public double InterpMs { get; init; }
        public double JitMs { get; init; }
        public double Speedup => JitMs > 0 ? InterpMs / JitMs : 0;
        /// <summary>S1 gate: ≥10× on synthetic ADDIU loop (Phase 51).</summary>
        public bool S1Met => Speedup >= 10.0;
        public ulong Cycles { get; init; }
        public string Notes { get; init; } = "";
        public bool ParityOk { get; init; }
    }

    /// <summary>
    /// Compare EE.Step vs EeJit.Execute on a tight self-loop that never exits
    /// (so the timed region stays in the hot ALU block, not zero-filled memory).
    ///   addiu t0, t0, 1
    ///   beq   zero, zero, -2   ; always taken → back to addiu
    ///   nop
    /// </summary>
    public static Result MeasureEeJit(ulong cycles = 200_000)
    {
        uint[] prog =
        {
            (0x09u << 26) | (8 << 21) | (8 << 16) | 1, // addiu t0,t0,1
            (0x04u << 26) | (0 << 21) | (0 << 16) | unchecked((ushort)-2), // beq r0,r0,-2
            0, // nop delay
        };

        var a = new Ps2System();
        var b = new Ps2System();
        for (int i = 0; i < prog.Length; i++)
        {
            a.Memory.Write32(0x00100000 + (uint)(i * 4), prog[i]);
            b.Memory.Write32(0x00100000 + (uint)(i * 4), prog[i]);
        }
        a.EE.PC = 0x00100000;
        b.EE.PC = 0x00100000;

        // Warmup JIT compile on a throwaway system (excluded from timing)
        {
            var w = new Ps2System();
            for (int i = 0; i < prog.Length; i++)
                w.Memory.Write32(0x00100000 + (uint)(i * 4), prog[i]);
            w.EE.PC = 0x00100000;
            w.EeJit.Enabled = true;
            w.RunEeJit(20_000);
        }

        var sw = Stopwatch.StartNew();
        a.EE.Step(cycles);
        sw.Stop();
        double interpMs = sw.Elapsed.TotalMilliseconds;

        b.EeJit.Enabled = true;
        sw.Restart();
        b.RunEeJit(cycles);
        sw.Stop();
        double jitMs = sw.Elapsed.TotalMilliseconds;

        bool parity = a.EE.GetGpr(8).Lo == b.EE.GetGpr(8).Lo && a.EE.PC == b.EE.PC;
        string notes = parity ? "parity OK" : "parity FAIL";
        if (parity && jitMs > 0 && interpMs / jitMs >= 10.0)
            notes += "; S1 met";
        else if (parity)
            notes += $"; S1 open (speedup={interpMs / Math.Max(jitMs, 1e-9):F2}x)";

        return new Result
        {
            InterpMs = interpMs,
            JitMs = jitMs,
            Cycles = cycles,
            Notes = notes,
            ParityOk = parity
        };
    }

    public static double MeasureSnapshotDeltaMs(Ps2System system, int iterations = 20)
    {
        system.Snapshots.BeginSession(system);
        system.Snapshots.MarkRdramDirty(0x1000, 64);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            system.Snapshots.SaveDelta(system);
            system.Snapshots.LoadFrame(system, system.Snapshots.FrameIndex - 1);
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iterations;
    }
}
