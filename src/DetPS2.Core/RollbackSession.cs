using System;
using System.Collections.Generic;

namespace DetPS2.Core;

/// <summary>
/// GGPO-style rollback session (Phase 34). Det mode only.
/// Predict remote input, confirm, roll back R frames, resim.
/// </summary>
public sealed class RollbackSession
{
    public int InputDelay { get; set; } = 2;
    public int Window { get; set; } = 8;
    public ulong FrameQuantum { get; set; } = 50_000;

    public ulong LocalFrame { get; private set; }
    public ulong ConfirmedFrame { get; private set; }
    public ulong RollbackCount { get; private set; }
    public ulong ResimFrames { get; private set; }
    public ulong Desyncs { get; private set; }
    public bool Desynced => Desyncs > 0;
    public string? LastDesyncReason { get; private set; }

    public SnapshotEngine Snapshots { get; } = new();
    public DesyncDetector Desync { get; } = new();

    private readonly Dictionary<ulong, uint> _localInputs = new();
    private readonly Dictionary<ulong, uint> _remoteInputs = new();
    private readonly Dictionary<ulong, uint> _predictedRemote = new();
    private uint _lastRemote;

    public void Reset()
    {
        LocalFrame = ConfirmedFrame = 0;
        RollbackCount = ResimFrames = Desyncs = 0;
        LastDesyncReason = null;
        Snapshots.Reset();
        Snapshots.Window = Window;
        Desync.Reset();
        _localInputs.Clear();
        _remoteInputs.Clear();
        _predictedRemote.Clear();
        _lastRemote = 0;
    }

    public void Start(Ps2System system)
    {
        Reset();
        Snapshots.BeginSession(system);
        Snapshots.Window = Window;
    }

    /// <summary>Queue confirmed remote input for frame f.</summary>
    public void ConfirmRemote(ulong frame, uint buttons)
    {
        _remoteInputs[frame] = buttons;
        _lastRemote = buttons;
    }

    /// <summary>
    /// Advance one local frame: save snapshot, apply inputs, RunFor quantum.
    /// If a past remote input mismatches prediction, rollback + resim.
    /// </summary>
    public void Advance(Ps2System system, uint localButtons, uint? remotePredicted = null)
    {
        ulong f = LocalFrame;

        // Check for confirmed remote that differs from prediction
        for (ulong check = ConfirmedFrame; check < f; check++)
        {
            if (!_remoteInputs.TryGetValue(check, out uint confirmed))
                continue;
            if (_predictedRemote.TryGetValue(check, out uint pred) && pred != confirmed)
            {
                RollbackAndResim(system, check);
                break;
            }
            if (check >= ConfirmedFrame)
                ConfirmedFrame = check + 1;
        }

        uint remote = remotePredicted ?? (_remoteInputs.TryGetValue(f, out var r) ? r : _lastRemote);
        _localInputs[f] = localButtons;
        _predictedRemote[f] = remote;

        // Snapshot before sim
        Snapshots.SaveDelta(system);

        uint merged = localButtons | remote;
        system.Pad.SetButtons(merged);
        system.RunFor(FrameQuantum);

        // Desync hash for netplay wire
        uint hash = DesyncDetector.HashState(system);
        _ = hash;

        LocalFrame++;
    }

    private void RollbackAndResim(Ps2System system, ulong fromFrame)
    {
        RollbackCount++;
        if (!Snapshots.LoadFrame(system, fromFrame))
        {
            // Fall back: cannot load — mark desync
            Desyncs++;
            LastDesyncReason = $"missing snapshot frame {fromFrame}";
            return;
        }

        LocalFrame = fromFrame;
        for (ulong f = fromFrame; f < LocalFrame + (ulong)Window && _localInputs.ContainsKey(f); f++)
        {
            uint loc = _localInputs.TryGetValue(f, out var lb) ? lb : 0;
            uint rem = _remoteInputs.TryGetValue(f, out var rb)
                ? rb
                : (_predictedRemote.TryGetValue(f, out var pr) ? pr : _lastRemote);
            _predictedRemote[f] = rem;

            Snapshots.SaveDelta(system);
            system.Pad.SetButtons(loc | rem);
            system.RunFor(FrameQuantum);
            ResimFrames++;
            LocalFrame = f + 1;
        }
    }

    /// <summary>
    /// Offline test: inject artificial latency by delaying remote confirms.
    /// </summary>
    public static (ulong rollbacks, bool ok) SimulateTwoPlayer(
        Ps2System a,
        Ps2System b,
        int frames,
        int delay,
        Func<ulong, uint> inputA,
        Func<ulong, uint> inputB)
    {
        var ra = new RollbackSession { InputDelay = delay, Window = 8, FrameQuantum = 10_000 };
        var rb = new RollbackSession { InputDelay = delay, Window = 8, FrameQuantum = 10_000 };
        ra.Start(a);
        rb.Start(b);

        var pendingA = new Queue<(ulong f, uint btn)>();
        var pendingB = new Queue<(ulong f, uint btn)>();

        for (int i = 0; i < frames; i++)
        {
            ulong f = (ulong)i;
            uint ia = inputA(f);
            uint ib = inputB(f);
            pendingA.Enqueue((f, ia));
            pendingB.Enqueue((f, ib));

            // deliver after delay
            while (pendingA.Count > delay)
            {
                var (df, db) = pendingA.Dequeue();
                rb.ConfirmRemote(df, db);
            }
            while (pendingB.Count > delay)
            {
                var (df, db) = pendingB.Dequeue();
                ra.ConfirmRemote(df, db);
            }

            uint predB = pendingB.Count > 0 ? pendingB.Peek().btn : 0;
            uint predA = pendingA.Count > 0 ? pendingA.Peek().btn : 0;
            ra.Advance(a, ia, predB);
            rb.Advance(b, ib, predA);
        }

        // flush remaining confirms
        while (pendingA.Count > 0)
        {
            var (df, db) = pendingA.Dequeue();
            rb.ConfirmRemote(df, db);
        }
        while (pendingB.Count > 0)
        {
            var (df, db) = pendingB.Dequeue();
            ra.ConfirmRemote(df, db);
        }

        bool ok = a.MasterCycles == b.MasterCycles
                  && RegressionFixtures.HashFramebuffer(a.Gs) == RegressionFixtures.HashFramebuffer(b.Gs);
        return (ra.RollbackCount + rb.RollbackCount, ok);
    }

    public string FormatNetGraph() =>
        $"frame={LocalFrame} conf={ConfirmedFrame} rollbacks={RollbackCount} resim={ResimFrames} desync={Desyncs}";
}
