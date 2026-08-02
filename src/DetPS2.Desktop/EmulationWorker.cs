using System;
using System.Diagnostics;
using System.Threading;
using DetPS2.Core;

namespace DetPS2.Desktop;

/// <summary>
/// Runs EE <see cref="Ps2System.RunFor"/> on a background thread so the Avalonia UI
/// thread never blocks (Windows "Not Responding"). UI only snapshots Soft-GS for present.
///
/// Thread model (solo play):
/// - Worker: RunFor → ActiveQuirk.OnHostPresent → PresentFrame → copy GetPresentSpan
/// - UI: TryGetPresent / GameDisplayWindow.PresentSnapshot only
/// - Pad poll stays on UI (1-frame tear possible — see MainWindow.PollGamepads)
/// - Optional host <see cref="FrameLimiter"/> WaitFrame runs here, never on UI
///
/// Adaptive quantum targets ~0.5–4M cycles/slice for playable present rate (not 50M UI freeze).
/// </summary>
public sealed class EmulationWorker : IDisposable
{
    /// <summary>Playable present cadence bounds (requirement: ~0.5–4M, not 50M).</summary>
    public const ulong MinQuantum = 500_000UL;
    public const ulong MaxQuantum = 4_000_000UL;

    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _running;
    private volatile bool _inSlice;
    private Ps2System? _system;
    private ulong _quantum = 2_000_000;
    private ulong _quantumMin = MinQuantum;
    private ulong _quantumMax = MaxQuantum;
    private Action? _onAfterSlice;
    private FrameLimiter? _frameLimiter;
    private volatile bool _paceFrames;

    // Double-buffer Soft-GS present for the UI thread (no lock held during Avalonia blit).
    private uint[] _snapA = new uint[640 * 448];
    private uint[] _snapB = new uint[640 * 448];
    private int _front; // index of buffer latest for UI (published via Volatile)
    private int _snapW = 640, _snapH = 448;
    private long _px;
    private long _lit;
    private ulong _cycles;
    private ulong _eePc;
    private int _gifP3;
    private string _status = "idle";

    // Present cadence (wall) — rolling estimate for HUD / tuning.
    private long _presentCount;
    private long _presentWindowStartMs;
    private double _presentHz;

    /// <summary>Wall-ms target band for one RunFor+present slice (~40–60 presents/sec center).</summary>
    private const int SliceMsGrowBelow = 14;   // slice too fast → grow quantum
    private const int SliceMsShrinkAbove = 24; // slice too slow → shrink quantum
    private const int SliceMsHardCap = 36;     // below ~28 presents/sec → shrink harder

    public bool IsWorkerAlive => _thread is { IsAlive: true };
    public bool IsInSlice => _inSlice;

    public bool IsRunning
    {
        get => _running;
        set => _running = value;
    }

    /// <summary>Current adaptive quantum (clamped to policy bounds).</summary>
    public ulong Quantum
    {
        get => _quantum;
        set => _quantum = Math.Clamp(value, _quantumMin, _quantumMax);
    }

    public ulong QuantumMin => _quantumMin;
    public ulong QuantumMax => _quantumMax;

    /// <summary>Estimated Soft-GS present updates per second (worker wall).</summary>
    public double PresentHz => _presentHz;

    public long PresentCount => Interlocked.Read(ref _presentCount);

    /// <summary>
    /// Set speed-mode bounds without stomping adaptive quantum every UI tick.
    /// <paramref name="seedQuantum"/> only applied when non-null (speed change / boot race).
    /// </summary>
    public void SetQuantumPolicy(ulong min, ulong max, ulong? seedQuantum = null)
    {
        min = Math.Clamp(min, 100_000UL, 50_000_000UL);
        max = Math.Clamp(max, min, 50_000_000UL);
        _quantumMin = min;
        _quantumMax = max;
        if (seedQuantum.HasValue)
            _quantum = Math.Clamp(seedQuantum.Value, min, max);
        else
            _quantum = Math.Clamp(_quantum, min, max);
    }

    /// <summary>
    /// Host frame pacing. WaitFrame runs on the EE worker thread only when
    /// <paramref name="enabled"/> is true (never blocks Avalonia).
    /// </summary>
    public void SetFramePacing(FrameLimiter? limiter, bool enabled)
    {
        lock (_gate)
        {
            _frameLimiter = limiter;
            _paceFrames = enabled && limiter != null;
        }
    }

    public void Attach(Ps2System system, Action? onAfterSlice = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        lock (_gate)
        {
            _system = system;
            _onAfterSlice = onAfterSlice;
        }
        EnsureThread();
        _status = "attached";
    }

    /// <summary>
    /// Stop slices and drop the system reference. Waits briefly for any in-flight RunFor
    /// so callers can Reset / replace Ps2System without racing the worker.
    /// </summary>
    public void Detach()
    {
        _running = false;
        WaitIdle(milliseconds: 250);
        lock (_gate)
        {
            _system = null;
            _onAfterSlice = null;
        }
        _status = "detached";
    }

    /// <summary>Pause slices and wait for the current quantum to finish (if any).</summary>
    public void PauseAndWait(int milliseconds = 250)
    {
        _running = false;
        WaitIdle(milliseconds);
    }

    private void WaitIdle(int milliseconds)
    {
        int budget = Math.Clamp(milliseconds, 0, 5_000);
        var sw = Stopwatch.StartNew();
        while (_inSlice && sw.ElapsedMilliseconds < budget)
            Thread.Sleep(1);
    }

    private void EnsureThread()
    {
        if (_thread is { IsAlive: true }) return;
        _stop = false;
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "DetPS2-EE",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    private void WorkerLoop()
    {
        var sw = Stopwatch.StartNew();
        _presentWindowStartMs = sw.ElapsedMilliseconds;
        long presentsInWindow = 0;

        while (!_stop)
        {
            Ps2System? sys;
            ulong q;
            Action? after;
            FrameLimiter? limiter;
            bool pace;
            lock (_gate)
            {
                sys = _system;
                q = _quantum;
                after = _onAfterSlice;
                limiter = _frameLimiter;
                pace = _paceFrames;
            }

            if (sys == null || !_running || sys.Debugger.Halted)
            {
                _status = sys == null ? "no-system" : (sys.Debugger.Halted ? "halted" : "paused");
                _inSlice = false;
                Thread.Sleep(4);
                continue;
            }

            try
            {
                _inSlice = true;
                // Cap slice so UI gets ~30–60 present updates/sec when EE is slow.
                // Adaptive quantum (below) keeps wall time in the playable band — not max EE dump.
                long t0 = sw.ElapsedMilliseconds;
                sys.RunFor(q);
                sys.ActiveQuirk?.OnHostPresent(sys);
                sys.PresentFrame();
                after?.Invoke();

                // Snapshot Soft-GS present (after composite) for UI-only blit.
                var fb = sys.Gs.GetPresentSpan();
                int w = sys.Gs.FramebufferWidth;
                int h = sys.Gs.FramebufferHeight;
                if (w <= 0) w = 640;
                if (h <= 0) h = 448;
                int n = Math.Min(fb.Length, w * h);
                int back = 1 - Volatile.Read(ref _front);
                uint[] dest = back == 0 ? _snapA : _snapB;
                if (dest.Length < n)
                    dest = new uint[n];
                long lit = 0;
                for (int i = 0; i < n; i++)
                {
                    uint p = fb[i] | 0xFF000000u;
                    dest[i] = p;
                    if ((p & 0x00FFFFFFu) != 0) lit++;
                }
                if (back == 0) _snapA = dest; else _snapB = dest;
                _snapW = w;
                _snapH = h;
                _px = sys.Gs.PixelsWritten;
                _lit = lit;
                _cycles = sys.MasterCycles;
                _eePc = sys.EE.PC;
                _gifP3 = (int)sys.Gif.Path3Transfers;
                // Publish buffer index last so UI never sees a half-updated snap.
                Volatile.Write(ref _front, back);
                _status = "run";

                Interlocked.Increment(ref _presentCount);
                presentsInWindow++;
                long nowMs = sw.ElapsedMilliseconds;
                long windowMs = nowMs - _presentWindowStartMs;
                if (windowMs >= 500)
                {
                    _presentHz = presentsInWindow * 1000.0 / Math.Max(1, windowMs);
                    presentsInWindow = 0;
                    _presentWindowStartMs = nowMs;
                }

                long dt = nowMs - t0;
                AdaptQuantum(dt);

                // Optional 60fps host pacing — worker thread only (UI stays free).
                if (pace && limiter != null)
                    limiter.WaitFrame();
            }
            catch (Exception ex)
            {
                _status = "err:" + ex.GetType().Name;
                Thread.Sleep(16);
            }
            finally
            {
                _inSlice = false;
            }
        }

        _status = "stopped";
        _inSlice = false;
    }

    /// <summary>
    /// Keep wall time per present in ~14–24 ms (≈40–70 Hz), hard-shrink below ~28 Hz.
    /// Playable cadence: smooth UI + steady EE, not maximum cycles per second.
    /// Bound by policy (default 0.5–4M).
    /// </summary>
    private void AdaptQuantum(long dtMs)
    {
        ulong q = _quantum;
        ulong min = _quantumMin;
        ulong max = _quantumMax;

        if (dtMs > SliceMsHardCap && q > min)
        {
            // Far too slow for playable present rate — drop quantum aggressively.
            q = Math.Max(min, q * 2 / 3);
        }
        else if (dtMs > SliceMsShrinkAbove && q > min)
        {
            q = Math.Max(min, q * 3 / 4);
        }
        else if (dtMs < SliceMsGrowBelow && q < max)
        {
            // Headroom: grow toward speed-mode ceiling in modest steps.
            ulong step = Math.Max(100_000UL, q / 16);
            q = Math.Min(max, q + step);
        }

        _quantum = Math.Clamp(q, min, max);
    }

    /// <summary>Latest Soft-GS snapshot for the UI thread (no touch of live Ps2System GS).</summary>
    public bool TryGetPresent(out ReadOnlyMemory<uint> pixels, out int w, out int h,
        out long px, out long lit, out ulong cycles, out ulong pc, out int gifP3)
    {
        int f = Volatile.Read(ref _front);
        uint[] src = f == 0 ? _snapA : _snapB;
        w = _snapW;
        h = _snapH;
        px = _px;
        lit = _lit;
        cycles = _cycles;
        pc = _eePc;
        gifP3 = _gifP3;
        int n = Math.Min(src.Length, w * h);
        if (n <= 0)
        {
            pixels = ReadOnlyMemory<uint>.Empty;
            return false;
        }
        pixels = src.AsMemory(0, n);
        return true;
    }

    /// <summary>Worker-side lit pixels (for boot-race end without touching mid-RunFor GS).</summary>
    public long LastLit => _lit;

    public string Status => _status;

    public void Dispose()
    {
        _stop = true;
        _running = false;
        try { _thread?.Join(750); } catch { /* ignore */ }
        _thread = null;
        _inSlice = false;
    }
}
