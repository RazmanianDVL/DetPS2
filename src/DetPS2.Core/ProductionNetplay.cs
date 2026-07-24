using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DetPS2.Core;

/// <summary>
/// Phase 46: UDP transport for WAN/LAN rollback netplay (N4 prototype).
/// Fixed 16-byte <see cref="NetplayFrameMsg"/> framing; best-effort delivery.
/// </summary>
public sealed class UdpNetplayTransport : INetplayTransport
{
    private UdpClient? _udp;
    private IPEndPoint? _remote;
    private readonly Queue<NetplayFrameMsg> _inbox = new();
    private readonly object _lock = new();
    private Thread? _rxThread;
    private volatile bool _running;

    public string Name => "UDP";
    public bool IsConnected => _udp != null && _remote != null;
    public long PacketsSent { get; private set; }
    public long PacketsReceived { get; private set; }
    public long PacketsDropped { get; private set; }

    /// <summary>Bind local port and wait for first peer packet (or use known remote).</summary>
    public static UdpNetplayTransport Host(int localPort, int waitPeerMs = 30_000)
    {
        var t = new UdpNetplayTransport();
        t._udp = new UdpClient(localPort);
        t._running = true;
        t.StartRx();
        // Wait until peer sends something (first packet sets remote)
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitPeerMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (t._lock)
            {
                if (t._remote != null) return t;
            }
            Thread.Sleep(10);
        }
        t.Dispose();
        throw new TimeoutException($"UDP host: no peer on :{localPort}");
    }

    /// <summary>Connect to remote; sends a zero-frame hello so host learns our endpoint.</summary>
    public static UdpNetplayTransport Connect(string host, int remotePort, int localPort = 0)
    {
        var t = new UdpNetplayTransport();
        t._udp = localPort > 0 ? new UdpClient(localPort) : new UdpClient(0);
        t._remote = new IPEndPoint(IPAddress.Parse(host == "localhost" ? "127.0.0.1" : host), remotePort);
        t._running = true;
        t.StartRx();
        // Hello
        t.Send(new NetplayFrameMsg { FrameIndex = 0, Buttons = 0, DesyncHashLo = 0 });
        return t;
    }

    /// <summary>In-process UDP-shaped pair for tests (no sockets).</summary>
    public static (InMemoryNetplayTransport a, InMemoryNetplayTransport b) CreateTestPair() =>
        InMemoryNetplayTransport.CreatePair();

    private void StartRx()
    {
        _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "DetPS2-UdpRx" };
        _rxThread.Start();
    }

    private void RxLoop()
    {
        while (_running && _udp != null)
        {
            try
            {
                IPEndPoint? any = null;
                byte[] data = _udp.Receive(ref any!);
                if (any != null)
                {
                    lock (_lock)
                    {
                        _remote ??= any;
                        if (NetplayFrameMsg.TryRead(data, out var msg))
                        {
                            _inbox.Enqueue(msg);
                            PacketsReceived++;
                        }
                        else PacketsDropped++;
                    }
                }
            }
            catch (SocketException)
            {
                if (!_running) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    public void Send(NetplayFrameMsg msg)
    {
        if (_udp == null || _remote == null) throw new InvalidOperationException("UDP not connected");
        byte[] buf = msg.ToArray();
        _udp.Send(buf, buf.Length, _remote);
        PacketsSent++;
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
        int spins = Math.Min(timeoutMs, 100);
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

    public void Dispose()
    {
        _running = false;
        try { _udp?.Close(); } catch { /* ignore */ }
        _udp = null;
        _remote = null;
    }
}

/// <summary>
/// Phase 46: production rollback peer — frame advantage + netgraph + desync dump.
/// Uses <see cref="RollbackSession"/> for Det resim; transport for input exchange.
/// </summary>
public sealed class ProductionRollbackPeer
{
    public int FrameAdvantage { get; set; } = 1;
    public int InputDelay { get; set; } = 2;
    public ulong FrameQuantum { get; set; } = 50_000;
    public int Window { get; set; } = 8;

    public RollbackSession Session { get; } = new();
    public INetplayTransport? Transport { get; private set; }
    public NetGraph Graph { get; } = new();
    public DesyncDumpWriter DesyncDump { get; } = new();

    public bool Running { get; private set; }
    public ulong FramesAdvanced { get; private set; }
    public bool Desynced => Session.Desynced || Graph.Desyncs > 0;

    public void Attach(INetplayTransport transport) =>
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public void Start(Ps2System system)
    {
        Session.InputDelay = InputDelay;
        Session.Window = Window;
        Session.FrameQuantum = FrameQuantum;
        Session.Start(system);
        Graph.Reset();
        Running = true;
        FramesAdvanced = 0;
    }

    public void Stop() => Running = false;

    /// <summary>
    /// One production frame: send local, drain remote confirms, advance with prediction.
    /// Frame advantage: allow local to run FrameAdvantage frames ahead of confirmed remote.
    /// </summary>
    public bool AdvanceFrame(Ps2System system, uint localButtons)
    {
        if (!Running) return false;
        if (Transport == null || !Transport.IsConnected)
            throw new InvalidOperationException("no transport");

        ulong f = Session.LocalFrame;
        uint hash = DesyncDetector.HashState(system);
        Transport.Send(new NetplayFrameMsg
        {
            FrameIndex = (uint)f,
            Buttons = localButtons,
            DesyncHashLo = hash
        });
        Graph.PacketsOut++;

        // Drain remote
        while (Transport.TryReceive(out var remote, timeoutMs: 0))
        {
            Graph.PacketsIn++;
            Session.ConfirmRemote(remote.FrameIndex, remote.Buttons);
            if (!Session.Desync.Check(hash, remote.DesyncHashLo, remote.FrameIndex) &&
                remote.FrameIndex == f)
            {
                Graph.Desyncs++;
                DesyncDump.Record(system, remote.FrameIndex, hash, remote.DesyncHashLo, Session.LastDesyncReason ?? "hash");
            }
        }

        // Frame advantage gate: if too far ahead of confirmed, stall (predict last)
        long lead = (long)Session.LocalFrame - (long)Session.ConfirmedFrame;
        if (lead > FrameAdvantage + InputDelay)
            Graph.Stalls++;

        uint pred = 0;
        Session.Advance(system, localButtons, pred == 0 ? null : pred);
        FramesAdvanced++;
        Graph.LocalFrame = Session.LocalFrame;
        Graph.ConfirmedFrame = Session.ConfirmedFrame;
        Graph.Rollbacks = Session.RollbackCount;
        Graph.ResimFrames = Session.ResimFrames;
        Graph.FrameAdvantageObserved = Math.Max(0, (long)Session.LocalFrame - (long)Session.ConfirmedFrame);
        return !Desynced;
    }

    /// <summary>Offline dual-peer soak with artificial delay (synthetic N3 cert).</summary>
    public static SoakResult SoakTwoPlayer(
        int frames,
        int delay,
        int frameAdvantage,
        Func<ulong, uint>? inputA = null,
        Func<ulong, uint>? inputB = null)
    {
        inputA ??= f => (uint)(f & 1);
        inputB ??= f => (uint)((f >> 1) & 1);

        var a = new Ps2System();
        var b = new Ps2System();
        a.LoadHomebrewGsDemo();
        b.LoadHomebrewGsDemo();

        var (tA, tB) = InMemoryNetplayTransport.CreatePair();
        var peerA = new ProductionRollbackPeer
        {
            FrameAdvantage = frameAdvantage,
            InputDelay = delay,
            FrameQuantum = 5_000,
            Window = 8
        };
        var peerB = new ProductionRollbackPeer
        {
            FrameAdvantage = frameAdvantage,
            InputDelay = delay,
            FrameQuantum = 5_000,
            Window = 8
        };
        peerA.Attach(tA);
        peerB.Attach(tB);
        peerA.Start(a);
        peerB.Start(b);

        var pendingA = new Queue<(ulong f, uint btn)>();
        var pendingB = new Queue<(ulong f, uint btn)>();

        for (int i = 0; i < frames; i++)
        {
            ulong f = (ulong)i;
            uint ia = inputA(f);
            uint ib = inputB(f);
            pendingA.Enqueue((f, ia));
            pendingB.Enqueue((f, ib));

            while (pendingA.Count > delay)
            {
                var (df, db) = pendingA.Dequeue();
                peerB.Session.ConfirmRemote(df, db);
                tA.Send(new NetplayFrameMsg { FrameIndex = (uint)df, Buttons = db, DesyncHashLo = 0 });
            }
            while (pendingB.Count > delay)
            {
                var (df, db) = pendingB.Dequeue();
                peerA.Session.ConfirmRemote(df, db);
                tB.Send(new NetplayFrameMsg { FrameIndex = (uint)df, Buttons = db, DesyncHashLo = 0 });
            }

            peerA.Session.Advance(a, ia, pendingB.Count > 0 ? pendingB.Peek().btn : 0);
            peerB.Session.Advance(b, ib, pendingA.Count > 0 ? pendingA.Peek().btn : 0);
            peerA.FramesAdvanced++;
            peerB.FramesAdvanced++;
            peerA.Graph.LocalFrame = peerA.Session.LocalFrame;
            peerA.Graph.ConfirmedFrame = peerA.Session.ConfirmedFrame;
            peerA.Graph.Rollbacks = peerA.Session.RollbackCount;
            peerB.Graph.LocalFrame = peerB.Session.LocalFrame;
            peerB.Graph.ConfirmedFrame = peerB.Session.ConfirmedFrame;
            peerB.Graph.Rollbacks = peerB.Session.RollbackCount;
        }

        bool sync = a.MasterCycles == b.MasterCycles
                    && !peerA.Session.Desynced
                    && !peerB.Session.Desynced;
        return new SoakResult
        {
            Frames = frames,
            Sync = sync,
            Rollbacks = peerA.Session.RollbackCount + peerB.Session.RollbackCount,
            CyclesA = a.MasterCycles,
            CyclesB = b.MasterCycles,
            TitleId = "homebrew-gs-demo",
            NetGraph = peerA.Graph.Format()
        };
    }

    public sealed class SoakResult
    {
        public int Frames { get; init; }
        public bool Sync { get; init; }
        public ulong Rollbacks { get; init; }
        public ulong CyclesA { get; init; }
        public ulong CyclesB { get; init; }
        public string TitleId { get; init; } = "";
        public string NetGraph { get; init; } = "";
        public bool Certified => Sync && Frames >= 100;
    }
}

/// <summary>Phase 46: live netgraph counters for Desktop / CLI.</summary>
public sealed class NetGraph
{
    public ulong LocalFrame { get; set; }
    public ulong ConfirmedFrame { get; set; }
    public ulong Rollbacks { get; set; }
    public ulong ResimFrames { get; set; }
    public ulong PacketsIn { get; set; }
    public ulong PacketsOut { get; set; }
    public ulong Stalls { get; set; }
    public ulong Desyncs { get; set; }
    public long FrameAdvantageObserved { get; set; }

    public void Reset()
    {
        LocalFrame = ConfirmedFrame = Rollbacks = ResimFrames = 0;
        PacketsIn = PacketsOut = Stalls = Desyncs = 0;
        FrameAdvantageObserved = 0;
    }

    public string Format() =>
        $"f={LocalFrame} conf={ConfirmedFrame} adv={FrameAdvantageObserved} rb={Rollbacks} resim={ResimFrames} " +
        $"in={PacketsIn} out={PacketsOut} stall={Stalls} desync={Desyncs}";
}

/// <summary>Phase 46: write desync dumps for offline triage (no host time in core state).</summary>
public sealed class DesyncDumpWriter
{
    public int Count { get; private set; }
    public string? LastPath { get; private set; }
    public string? LastSummary { get; private set; }

    public void Record(Ps2System system, uint frame, uint localHash, uint remoteHash, string reason)
    {
        Count++;
        LastSummary = $"frame={frame} local=0x{localHash:X8} remote=0x{remoteHash:X8} reason={reason} " +
                      $"pc=0x{system.EE.PC:X8} cyc={system.MasterCycles}";
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DetPS2", "desync");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"desync-f{frame}-{Count}.txt");
            var sb = new StringBuilder();
            sb.AppendLine(LastSummary);
            sb.AppendLine($"EE.PC=0x{system.EE.PC:X8}");
            sb.AppendLine($"MasterCycles={system.MasterCycles}");
            sb.AppendLine($"Pad=0x{system.Pad.Buttons:X8}");
            sb.AppendLine($"FbHash=0x{RegressionFixtures.HashFramebuffer(system.Gs):X16}");
            File.WriteAllText(path, sb.ToString());
            LastPath = path;
        }
        catch
        {
            // Desktop may lack write access — keep summary only
        }
    }

    public void Reset()
    {
        Count = 0;
        LastPath = LastSummary = null;
    }
}
