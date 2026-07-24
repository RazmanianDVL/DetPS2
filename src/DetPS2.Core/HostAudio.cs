using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DetPS2.Core;

/// <summary>
/// Phase 43/50: host-facing audio device interface. Core never opens devices.
/// Desktop prefers <see cref="WinMmHostAudioDevice"/> on Windows; tests use meter/null.
/// </summary>
public interface IHostAudioDevice : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    int SampleRate { get; }
    /// <summary>True when Pump writes to an OS audio device (not meter-only).</summary>
    bool HasOsOutput { get; }
    /// <summary>Peak sample magnitude from last pump (0 if unknown).</summary>
    short LastPeak { get; }
    /// <summary>Start pumping from a ring sink (host thread).</summary>
    void Open(int sampleRate = 48000);
    void Close();
    /// <summary>Pull samples from sink and queue to OS (non-blocking as possible).</summary>
    int Pump(RingBufferAudioSink sink, int maxFrames = 1024);
}

public sealed class NullHostAudioDevice : IHostAudioDevice
{
    public string Name => "Null";
    public bool IsOpen { get; private set; }
    public int SampleRate { get; private set; } = 48000;
    public bool HasOsOutput => false;
    public short LastPeak { get; private set; }
    public long FramesPumped { get; private set; }

    public void Open(int sampleRate = 48000)
    {
        SampleRate = sampleRate;
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public int Pump(RingBufferAudioSink sink, int maxFrames = 1024)
    {
        if (!IsOpen || sink == null) return 0;
        Span<short> buf = stackalloc short[Math.Min(maxFrames * 2, 4096)];
        int n = sink.Drain(buf);
        FramesPumped += n / 2;
        return n / 2;
    }

    public void Dispose() => Close();
}

/// <summary>
/// Meter-only device: drains the ring and tracks peak (no OS output).
/// Use for tests and headless; Desktop prefers <see cref="WinMmHostAudioDevice"/>.
/// </summary>
public sealed class MeterHostAudioDevice : IHostAudioDevice
{
    public string Name => "Meter";
    public bool IsOpen { get; private set; }
    public int SampleRate { get; private set; } = 48000;
    public bool HasOsOutput => false;
    public long FramesPumped { get; private set; }
    public short LastPeak { get; private set; }

    public void Open(int sampleRate = 48000)
    {
        SampleRate = sampleRate;
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public int Pump(RingBufferAudioSink sink, int maxFrames = 1024)
    {
        if (!IsOpen) return 0;
        short[] buf = new short[Math.Min(maxFrames * 2, 4096)];
        int n = sink.Drain(buf);
        for (int i = 0; i < n; i++)
        {
            short a = Math.Abs(buf[i]);
            if (a > LastPeak) LastPeak = a;
        }
        FramesPumped += n / 2;
        return n / 2;
    }

    public void Dispose() => Close();
}

/// <summary>
/// Phase 50: Windows waveOut (WinMM) host audio — real OS output without NuGet deps.
/// Falls back gracefully if waveOutOpen fails (HasOsOutput stays false).
/// </summary>
public sealed class WinMmHostAudioDevice : IHostAudioDevice
{
    private IntPtr _waveOut = IntPtr.Zero;
    private readonly object _lock = new();
    private GCHandle _bufHandle;
    private byte[]? _pinnedBytes;
    private WAVEHDR _hdr;
    private bool _hdrPrepared;

    public string Name => HasOsOutput ? "WinMM" : "WinMM(unavailable)";
    public bool IsOpen { get; private set; }
    public int SampleRate { get; private set; } = 48000;
    public bool HasOsOutput { get; private set; }
    public long FramesPumped { get; private set; }
    public short LastPeak { get; private set; }
    public long WaveWrites { get; private set; }

    public void Open(int sampleRate = 48000)
    {
        Close();
        SampleRate = sampleRate;
        IsOpen = true;
        if (!OperatingSystem.IsWindows())
        {
            HasOsOutput = false;
            return;
        }

        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, // PCM
            nChannels = 2,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 4,
            nAvgBytesPerSec = (uint)(sampleRate * 4),
            cbSize = 0
        };

        int rc = waveOutOpen(out _waveOut, 0xFFFFFFFF /* WAVE_MAPPER */, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        HasOsOutput = rc == 0 && _waveOut != IntPtr.Zero;
        if (!HasOsOutput)
            _waveOut = IntPtr.Zero;
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_waveOut != IntPtr.Zero)
            {
                try
                {
                    waveOutReset(_waveOut);
                    if (_hdrPrepared)
                    {
                        waveOutUnprepareHeader(_waveOut, ref _hdr, Marshal.SizeOf<WAVEHDR>());
                        _hdrPrepared = false;
                    }
                    waveOutClose(_waveOut);
                }
                catch { /* ignore */ }
                _waveOut = IntPtr.Zero;
            }
            if (_bufHandle.IsAllocated)
                _bufHandle.Free();
            _pinnedBytes = null;
            HasOsOutput = false;
            IsOpen = false;
        }
    }

    public int Pump(RingBufferAudioSink sink, int maxFrames = 1024)
    {
        if (!IsOpen) return 0;
        short[] samples = new short[Math.Min(maxFrames * 2, 4096)];
        int n = sink.Drain(samples);
        if (n <= 0) return 0;

        for (int i = 0; i < n; i++)
        {
            short a = Math.Abs(samples[i]);
            if (a > LastPeak) LastPeak = a;
        }

        int frames = n / 2;
        FramesPumped += frames;

        if (!HasOsOutput || _waveOut == IntPtr.Zero)
            return frames;

        lock (_lock)
        {
            try
            {
                int bytes = n * 2;
                if (_pinnedBytes == null || _pinnedBytes.Length < bytes)
                {
                    if (_bufHandle.IsAllocated) _bufHandle.Free();
                    _pinnedBytes = new byte[Math.Max(bytes, 4096)];
                    _bufHandle = GCHandle.Alloc(_pinnedBytes, GCHandleType.Pinned);
                }
                Buffer.BlockCopy(samples, 0, _pinnedBytes, 0, bytes);

                if (_hdrPrepared)
                {
                    waveOutUnprepareHeader(_waveOut, ref _hdr, Marshal.SizeOf<WAVEHDR>());
                    _hdrPrepared = false;
                }

                _hdr = new WAVEHDR
                {
                    lpData = _bufHandle.AddrOfPinnedObject(),
                    dwBufferLength = (uint)bytes,
                    dwFlags = 0
                };
                if (waveOutPrepareHeader(_waveOut, ref _hdr, Marshal.SizeOf<WAVEHDR>()) == 0)
                {
                    _hdrPrepared = true;
                    if (waveOutWrite(_waveOut, ref _hdr, Marshal.SizeOf<WAVEHDR>()) == 0)
                        WaveWrites++;
                }
            }
            catch
            {
                HasOsOutput = false;
            }
        }
        return frames;
    }

    public void Dispose() => Close();

    #region WinMM P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat,
        IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutClose(IntPtr hWaveOut);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutWrite(IntPtr hWaveOut, ref WAVEHDR lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutReset(IntPtr hWaveOut);

    #endregion
}

/// <summary>Pick best host device for this OS (WinMM on Windows, meter elsewhere).</summary>
public static class HostAudioFactory
{
    public static IHostAudioDevice CreateDefault()
    {
        if (OperatingSystem.IsWindows())
            return new WinMmHostAudioDevice();
        return new MeterHostAudioDevice();
    }
}

/// <summary>Phase 43: simple keyboard/gamepad binding table (host applies).</summary>
public sealed class InputMapper
{
    private readonly Dictionary<string, PadInput.Button> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = PadInput.Button.Start,
        ["Shift"] = PadInput.Button.Select,
        ["Up"] = PadInput.Button.Up,
        ["Down"] = PadInput.Button.Down,
        ["Left"] = PadInput.Button.Left,
        ["Right"] = PadInput.Button.Right,
        ["Z"] = PadInput.Button.Cross,
        ["X"] = PadInput.Button.Circle,
        ["C"] = PadInput.Button.Triangle,
        ["J"] = PadInput.Button.Square,
        ["Q"] = PadInput.Button.L1,
        ["E"] = PadInput.Button.R1,
    };

    public int BindingCount => _map.Count;

    public void Bind(string key, PadInput.Button button) => _map[key] = button;

    public bool TryMap(string key, out PadInput.Button button) =>
        _map.TryGetValue(key, out button);

    public void ResetDefaults()
    {
        _map.Clear();
        Bind("Enter", PadInput.Button.Start);
        Bind("Shift", PadInput.Button.Select);
        Bind("Up", PadInput.Button.Up);
        Bind("Down", PadInput.Button.Down);
        Bind("Left", PadInput.Button.Left);
        Bind("Right", PadInput.Button.Right);
        Bind("Z", PadInput.Button.Cross);
        Bind("X", PadInput.Button.Circle);
        Bind("C", PadInput.Button.Triangle);
        Bind("J", PadInput.Button.Square);
        Bind("Q", PadInput.Button.L1);
        Bind("E", PadInput.Button.R1);
    }
}
