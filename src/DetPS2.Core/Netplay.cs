using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DetPS2.Core;

/// <summary>
/// Netplay frame wire message (Phase 18).
/// Layout (16 bytes fixed):
///   u32 magic 'NPF1'
///   u32 frameIndex
///   u32 buttons
///   u32 desyncHashLo  (low 32 of FNV over MasterCycles^PC)
/// </summary>
public readonly struct NetplayFrameMsg
{
    public const uint Magic = 0x3146504E; // 'NPF1' LE
    public const int Size = 16;

    public uint FrameIndex { get; init; }
    public uint Buttons { get; init; }
    public uint DesyncHashLo { get; init; }

    public void Write(Span<byte> dest)
    {
        if (dest.Length < Size) throw new ArgumentException("buffer too small");
        BitConverter.TryWriteBytes(dest[0..4], Magic);
        BitConverter.TryWriteBytes(dest[4..8], FrameIndex);
        BitConverter.TryWriteBytes(dest[8..12], Buttons);
        BitConverter.TryWriteBytes(dest[12..16], DesyncHashLo);
    }

    public byte[] ToArray()
    {
        byte[] b = new byte[Size];
        Write(b);
        return b;
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out NetplayFrameMsg msg)
    {
        msg = default;
        if (src.Length < Size) return false;
        uint magic = BitConverter.ToUInt32(src[0..4]);
        if (magic != Magic) return false;
        msg = new NetplayFrameMsg
        {
            FrameIndex = BitConverter.ToUInt32(src[4..8]),
            Buttons = BitConverter.ToUInt32(src[8..12]),
            DesyncHashLo = BitConverter.ToUInt32(src[12..16])
        };
        return true;
    }
}

/// <summary>Transport abstraction: tests use in-memory; LAN uses TCP.</summary>
public interface INetplayTransport : IDisposable
{
    string Name { get; }
    bool IsConnected { get; }
    void Send(NetplayFrameMsg msg);
    bool TryReceive(out NetplayFrameMsg msg, int timeoutMs = 0);
}

/// <summary>Paired ring for host↔client unit tests (no sockets).</summary>
public sealed class InMemoryNetplayTransport : INetplayTransport
{
    private readonly Queue<NetplayFrameMsg> _inbox = new();
    private InMemoryNetplayTransport? _peer;
    private readonly object _lock = new();

    public string Name => "InMemory";
    public bool IsConnected => _peer != null;

    public static (InMemoryNetplayTransport host, InMemoryNetplayTransport client) CreatePair()
    {
        var a = new InMemoryNetplayTransport();
        var b = new InMemoryNetplayTransport();
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public void Send(NetplayFrameMsg msg)
    {
        if (_peer == null) throw new InvalidOperationException("not paired");
        lock (_peer._lock)
            _peer._inbox.Enqueue(msg);
    }

    public bool TryReceive(out NetplayFrameMsg msg, int timeoutMs = 0)
    {
        lock (_lock)
        {
            if (_inbox.Count > 0)
            {
                msg = _inbox.Dequeue();
                return true;
            }
        }
        msg = default;
        if (timeoutMs <= 0) return false;
        // Spin briefly for tests (no host wall-clock in core paths — only transport wait)
        int spins = Math.Min(timeoutMs, 50);
        for (int i = 0; i < spins; i++)
        {
            Thread.Sleep(1);
            lock (_lock)
            {
                if (_inbox.Count > 0)
                {
                    msg = _inbox.Dequeue();
                    return true;
                }
            }
        }
        return false;
    }

    public void Dispose() => _peer = null;
}

/// <summary>
/// Simple TCP LAN transport (Phase 18). Framing = fixed 16-byte messages.
/// Host listens; client connects. Core determinism is unaffected — only I/O timing of messages.
/// </summary>
public sealed class TcpNetplayTransport : INetplayTransport
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private TcpListener? _listener;
    private readonly byte[] _rx = new byte[NetplayFrameMsg.Size];
    private int _rxPos;

    public string Name => "TCP";
    public bool IsConnected => _client != null && _client.Connected && _stream != null;

    public static TcpNetplayTransport Host(int port, int acceptTimeoutMs = 30_000)
    {
        var t = new TcpNetplayTransport();
        t._listener = new TcpListener(IPAddress.Any, port);
        t._listener.Start();
        using var cts = new CancellationTokenSource(acceptTimeoutMs);
        // Accept with timeout via polling
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(acceptTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (t._listener.Pending())
            {
                t._client = t._listener.AcceptTcpClient();
                t._stream = t._client.GetStream();
                t._stream.ReadTimeout = 5000;
                t._stream.WriteTimeout = 5000;
                return t;
            }
            Thread.Sleep(10);
        }
        t._listener.Stop();
        throw new TimeoutException($"No client connected on port {port}");
    }

    public static TcpNetplayTransport Connect(string host, int port, int timeoutMs = 10_000)
    {
        var t = new TcpNetplayTransport();
        t._client = new TcpClient();
        var ar = t._client.BeginConnect(host, port, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
        {
            t._client.Close();
            throw new TimeoutException($"Connect to {host}:{port} timed out");
        }
        t._client.EndConnect(ar);
        t._stream = t._client.GetStream();
        t._stream.ReadTimeout = 5000;
        t._stream.WriteTimeout = 5000;
        return t;
    }

    public void Send(NetplayFrameMsg msg)
    {
        if (_stream == null) throw new InvalidOperationException("not connected");
        byte[] buf = msg.ToArray();
        _stream.Write(buf, 0, buf.Length);
    }

    public bool TryReceive(out NetplayFrameMsg msg, int timeoutMs = 0)
    {
        msg = default;
        if (_stream == null) return false;
        try
        {
            if (timeoutMs > 0)
                _stream.ReadTimeout = timeoutMs;
            while (_rxPos < NetplayFrameMsg.Size)
            {
                if (!_stream.DataAvailable && timeoutMs <= 0 && _rxPos == 0)
                    return false;
                int n = _stream.Read(_rx, _rxPos, NetplayFrameMsg.Size - _rxPos);
                if (n <= 0) return false;
                _rxPos += n;
            }
            _rxPos = 0;
            return NetplayFrameMsg.TryRead(_rx, out msg);
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _client?.Close(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _stream = null;
        _client = null;
        _listener = null;
    }
}

/// <summary>Desync detector: compare low hash of MasterCycles ^ PC every N frames.</summary>
public sealed class DesyncDetector
{
    public int CheckEveryNFrames { get; set; } = 1;
    public ulong DesyncCount { get; private set; }
    public ulong Checks { get; private set; }
    public bool Desynced => DesyncCount > 0;
    public string? LastReason { get; private set; }

    public void Reset()
    {
        DesyncCount = 0;
        Checks = 0;
        LastReason = null;
    }

    public static uint HashState(Ps2System system)
    {
        ulong h = system.MasterCycles ^ system.EE.PC;
        // mix in pad for extra sensitivity
        h ^= system.Pad.Buttons;
        h *= 0x9E3779B97F4A7C15UL;
        h ^= h >> 32;
        return (uint)h;
    }

    public void Mark(string reason)
    {
        DesyncCount++;
        LastReason = reason;
    }

    /// <summary>Returns false if desync detected.</summary>
    public bool Check(uint localHash, uint remoteHash, uint frameIndex)
    {
        if (CheckEveryNFrames > 1 && (frameIndex % (uint)CheckEveryNFrames) != 0)
            return true;
        Checks++;
        if (localHash != remoteHash)
        {
            Mark($"frame {frameIndex}: local=0x{localHash:X8} remote=0x{remoteHash:X8}");
            return false;
        }
        return true;
    }
}

/// <summary>
/// Netplay session (Phases 11/18): lockstep quanta + optional transport + desync checks.
/// </summary>
public sealed class NetplaySession
{
    public const ulong DefaultFrameQuantum = 50_000;

    public enum Role { Host, Client }

    public Role SessionRole { get; }
    public ulong FrameQuantum { get; set; } = DefaultFrameQuantum;
    public ulong FrameIndex { get; private set; }
    public bool Running { get; private set; }
    public INetplayTransport? Transport { get; private set; }
    public DesyncDetector Desync { get; } = new();
    public InputRecording SharedInputs { get; } = new();
    public ulong LocalAdvances { get; private set; }
    public ulong RemoteFramesReceived { get; private set; }

    /// <summary>Local pad for this peer (host or client controller).</summary>
    public uint LocalButtons { get; set; }

    public NetplaySession(Role role) => SessionRole = role;

    public void AttachTransport(INetplayTransport transport) =>
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public void Reset()
    {
        FrameIndex = 0;
        Running = false;
        SharedInputs.Reset();
        Desync.Reset();
        LocalAdvances = 0;
        RemoteFramesReceived = 0;
        LocalButtons = 0;
    }

    public void Start()
    {
        Running = true;
        FrameIndex = 0;
        Desync.Reset();
    }

    public void Stop() => Running = false;

    /// <summary>One lockstep frame without network: set pad, RunFor quantum, optionally record.</summary>
    public void Advance(Ps2System system, uint buttons, bool record = true)
    {
        if (record)
        {
            if (!SharedInputs.IsRecording)
                SharedInputs.StartRecording();
            SharedInputs.Record(system.MasterCycles, buttons);
        }
        system.Pad.SetButtons(buttons);
        system.RunFor(FrameQuantum);
        FrameIndex++;
        LocalAdvances++;
    }

    /// <summary>
    /// Exchange inputs with peer then advance (send then receive).
    /// Safe when peer runs concurrently; for sequential unit tests prefer
    /// <see cref="ExchangeLockstep"/>.
    /// </summary>
    public bool AdvanceNetworked(Ps2System system, uint localButtons, int recvTimeoutMs = 5000)
    {
        if (Transport == null || !Transport.IsConnected)
            throw new InvalidOperationException("No connected transport");

        LocalButtons = localButtons;
        uint localHash = DesyncDetector.HashState(system);
        Transport.Send(new NetplayFrameMsg
        {
            FrameIndex = (uint)FrameIndex,
            Buttons = localButtons,
            DesyncHashLo = localHash
        });

        if (!Transport.TryReceive(out NetplayFrameMsg remote, timeoutMs: recvTimeoutMs))
            return false;

        return ApplyRemoteAndAdvance(system, localButtons, localHash, remote);
    }

    private bool ApplyRemoteAndAdvance(Ps2System system, uint localButtons, uint localHash, NetplayFrameMsg remote)
    {
        RemoteFramesReceived++;
        if (remote.FrameIndex != FrameIndex)
        {
            Desync.Mark($"frame index mismatch local={FrameIndex} remote={remote.FrameIndex}");
            return false;
        }

        // Both peers hash pre-advance state; identical starts → identical hashes.
        Desync.Check(localHash, remote.DesyncHashLo, (uint)FrameIndex);

        uint merged = localButtons | remote.Buttons;
        Advance(system, merged, record: true);
        return !Desync.Desynced;
    }

    /// <summary>
    /// Deterministic dual-peer lockstep (both send, then both receive, then both advance).
    /// Avoids send/recv deadlock on half-duplex test paths.
    /// </summary>
    public static bool ExchangeLockstep(
        NetplaySession a, Ps2System aSys, uint aPad,
        NetplaySession b, Ps2System bSys, uint bPad)
    {
        if (a.Transport == null || b.Transport == null)
            throw new InvalidOperationException("Both sessions need transport");
        if (a.FrameIndex != b.FrameIndex)
        {
            a.Desync.Mark("session frame index diverge before exchange");
            return false;
        }

        uint ha = DesyncDetector.HashState(aSys);
        uint hb = DesyncDetector.HashState(bSys);
        uint fi = (uint)a.FrameIndex;

        a.Transport.Send(new NetplayFrameMsg { FrameIndex = fi, Buttons = aPad, DesyncHashLo = ha });
        b.Transport.Send(new NetplayFrameMsg { FrameIndex = fi, Buttons = bPad, DesyncHashLo = hb });

        if (!a.Transport.TryReceive(out NetplayFrameMsg aRemote) ||
            !b.Transport.TryReceive(out NetplayFrameMsg bRemote))
            return false;

        bool okA = a.ApplyRemoteAndAdvance(aSys, aPad, ha, aRemote);
        bool okB = b.ApplyRemoteAndAdvance(bSys, bPad, hb, bRemote);
        return okA && okB;
    }

    /// <summary>
    /// Run N lockstep frames over transport; returns (frames, desynced).
    /// </summary>
    public (int frames, bool desynced) RunLockstep(
        Ps2System system,
        Func<uint> localPadProvider,
        int frameCount)
    {
        if (!Running) Start();
        int done = 0;
        for (int i = 0; i < frameCount; i++)
        {
            uint pad = localPadProvider();
            if (Transport != null && Transport.IsConnected)
            {
                if (!AdvanceNetworked(system, pad))
                    break;
            }
            else
            {
                Advance(system, pad);
            }
            done++;
            if (Desync.Desynced && FrameIndex > 1)
                break;
        }
        return (done, Desync.Desynced);
    }

    /// <summary>
    /// Replay a tape: for each recorded frame, set buttons at cycle and run to next (or quantum).
    /// Deterministic if systems start identical.
    /// </summary>
    public static (ulong cycles, ulong fbHash) ReplayTape(
        Ps2System system,
        byte[] tape,
        ulong quantum = DefaultFrameQuantum,
        Action? betweenFrames = null)
    {
        var rec = new InputRecording();
        if (!rec.Deserialize(tape))
            throw new InvalidOperationException("Invalid input tape");

        if (rec.FrameCount == 0)
        {
            system.RunFor(quantum);
            return (system.MasterCycles, RegressionFixtures.HashFramebuffer(system.Gs));
        }

        rec.StartPlayback();
        ulong end = rec.Frames[^1].Cycle + quantum;
        while (system.MasterCycles < end)
        {
            uint? b = rec.PollPlayback(system.MasterCycles);
            if (b.HasValue)
                system.Pad.SetButtons(b.Value);
            system.RunFor(Math.Min(quantum, end - system.MasterCycles));
            betweenFrames?.Invoke();
        }

        return (system.MasterCycles, RegressionFixtures.HashFramebuffer(system.Gs));
    }

    /// <summary>Load INPR tape from path into SharedInputs for playback.</summary>
    public bool LoadTapeFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return SharedInputs.Deserialize(data);
    }

    public void SaveTapeFile(string path)
    {
        File.WriteAllBytes(path, SharedInputs.Serialize());
    }
}
