using System;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Simple execution tracer for debugging and determinism verification.
/// Can be enabled to log EE instructions + cycles.
/// Phase 4 direction but useful early.
/// </summary>
public sealed class Tracer
{
    private StreamWriter? _writer;
    private bool _enabled = false;

    public void Enable(string filename = "detps2_trace.log")
    {
        _writer = new StreamWriter(filename);
        _enabled = true;
        Console.WriteLine($"[Tracer] Enabled. Writing to {filename}");
    }

    public void Disable()
    {
        if (_writer != null)
        {
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
        }
        _enabled = false;
        Console.WriteLine("[Tracer] Disabled.");
    }

    public void LogInstruction(ulong pc, uint opcode, int cycles)
    {
        if (!_enabled || _writer == null) return;

        _writer.WriteLine($"PC=0x{pc:X8}  OP=0x{opcode:X8}  Cycles={cycles}");
    }

    public void Log(string message)
    {
        if (!_enabled || _writer == null) return;
        _writer.WriteLine(message);
    }
}
