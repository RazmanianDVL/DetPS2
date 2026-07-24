using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// GS command buffer (Phase 29): EE/GIF pushes draw commands; GPU presenter drains for display.
/// Software GS remains the determinism source — this is the Perf present path.
/// </summary>
public sealed class GsCommandBuffer
{
    public enum Op : byte
    {
        Clear = 1,
        Present = 2,
        UploadFb = 3,
        SetScale = 4,
    }

    public readonly struct Cmd
    {
        public Op Opcode { get; init; }
        public uint Arg0 { get; init; }
        public uint Arg1 { get; init; }
        public float ScaleX { get; init; }
        public float ScaleY { get; init; }
    }

    private readonly Queue<Cmd> _q = new();
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _q.Count; } }
    public ulong Enqueued { get; private set; }
    public ulong Drained { get; private set; }
    public float DisplayScaleX { get; private set; } = 1f;
    public float DisplayScaleY { get; private set; } = 1f;
    public int DisplayAspectNum { get; private set; } = 4;
    public int DisplayAspectDen { get; private set; } = 3;

    public void Reset()
    {
        lock (_lock) _q.Clear();
        Enqueued = Drained = 0;
        DisplayScaleX = DisplayScaleY = 1f;
        DisplayAspectNum = 4;
        DisplayAspectDen = 3;
    }

    public void Enqueue(Cmd cmd)
    {
        lock (_lock)
        {
            _q.Enqueue(cmd);
            Enqueued++;
        }
    }

    public void EnqueuePresent() => Enqueue(new Cmd { Opcode = Op.Present });
    public void EnqueueClear(uint color) => Enqueue(new Cmd { Opcode = Op.Clear, Arg0 = color });

    public void SetScale(float sx, float sy)
    {
        DisplayScaleX = sx;
        DisplayScaleY = sy;
        Enqueue(new Cmd { Opcode = Op.SetScale, ScaleX = sx, ScaleY = sy });
    }

    public void SetAspect(int num, int den)
    {
        DisplayAspectNum = Math.Max(1, num);
        DisplayAspectDen = Math.Max(1, den);
    }

    public bool TryDequeue(out Cmd cmd)
    {
        lock (_lock)
        {
            if (_q.Count == 0) { cmd = default; return false; }
            cmd = _q.Dequeue();
            Drained++;
            return true;
        }
    }

    /// <summary>Drain all commands applying scale; returns present count.</summary>
    public int Drain(Action<Cmd>? onCmd = null)
    {
        int n = 0;
        while (TryDequeue(out var c))
        {
            if (c.Opcode == Op.SetScale)
            {
                DisplayScaleX = c.ScaleX;
                DisplayScaleY = c.ScaleY;
            }
            onCmd?.Invoke(c);
            n++;
        }
        return n;
    }
}
