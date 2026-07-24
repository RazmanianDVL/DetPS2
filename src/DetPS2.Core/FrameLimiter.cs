using System;
using System.Diagnostics;

namespace DetPS2.Core;

/// <summary>
/// Host-side frame limiter (Phase 37). Uses Stopwatch only outside the core RunFor path.
/// Never call from SaveState / Step / Scheduler.
/// </summary>
public sealed class FrameLimiter
{
    private readonly Stopwatch _sw = new();
    private long _nextFrameTicks;
    private int _targetFps = 60;

    public bool Enabled { get; set; } = true;
    public int TargetFps
    {
        get => _targetFps;
        set => _targetFps = Math.Clamp(value, 1, 240);
    }

    public ulong FramesLimited { get; private set; }

    public void Reset()
    {
        _sw.Restart();
        _nextFrameTicks = 0;
        FramesLimited = 0;
    }

    /// <summary>Block until next frame boundary (host sleep). No-op if disabled.</summary>
    public void WaitFrame()
    {
        if (!Enabled) return;
        if (!_sw.IsRunning) _sw.Start();

        long period = Stopwatch.Frequency / TargetFps;
        long now = _sw.ElapsedTicks;
        if (_nextFrameTicks == 0)
            _nextFrameTicks = now + period;

        long wait = _nextFrameTicks - now;
        if (wait > 0)
        {
            // Coarse sleep then spin for determinism of host pacing only
            int ms = (int)(wait * 1000 / Stopwatch.Frequency);
            if (ms > 1)
                System.Threading.Thread.Sleep(ms - 1);
            while (_sw.ElapsedTicks < _nextFrameTicks) { /* spin */ }
            FramesLimited++;
        }
        _nextFrameTicks += period;
        // Resync if far behind
        if (_sw.ElapsedTicks - _nextFrameTicks > period * 3)
            _nextFrameTicks = _sw.ElapsedTicks + period;
    }
}

/// <summary>
/// Solo run-ahead (Phase 37). Uses SnapshotEngine; disabled for netplay/Det sessions.
/// </summary>
public sealed class RunAhead
{
    public int Frames { get; set; }
    public bool Enabled => Frames > 0;
    public ulong Applied { get; private set; }

    private readonly SnapshotEngine _snap = new();

    public void Reset()
    {
        Frames = 0;
        Applied = 0;
        _snap.Reset();
    }

    /// <summary>
    /// Classic run-ahead: save → simulate 1+N frames → present → restore → simulate 1 real frame.
    /// Disabled when Frames==0. Never use with netplay.
    /// </summary>
    public void Apply(Ps2System system, ulong frameQuantum, Action present)
    {
        if (Frames <= 0)
        {
            system.RunFor(frameQuantum);
            present();
            return;
        }

        _snap.BeginSession(system);
        var baseSnap = _snap.SaveFull(system);
        for (int i = 0; i <= Frames; i++)
            system.RunFor(frameQuantum);
        present();
        _snap.LoadFrame(system, baseSnap.FrameIndex);
        system.RunFor(frameQuantum);
        Applied++;
    }
}
