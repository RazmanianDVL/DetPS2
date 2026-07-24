using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// Frame-indexed snapshot engine (Phase 33) for savestates + rollback.
/// Full blob via SaveState; delta ring uses CoW RDRAM pages + core key state.
/// </summary>
public interface ISnapshottable
{
    void CaptureTo(BinaryWriter w);
    void RestoreFrom(BinaryReader r);
}

public sealed class SnapshotEngine
{
    public const int DefaultWindow = 8;
    public const int PageSize = 4096;

    public sealed class FrameSnap
    {
        public ulong FrameIndex;
        public ulong MasterCycles;
        public ulong EePc;
        public uint PadButtons;
        public byte[]? FullState;      // optional full
        public byte[]? CoreDelta;      // EE+IOP+pad+intc compact
        public Dictionary<int, byte[]>? DirtyPages; // page index → 4K
        public ulong FbHash;
    }

    private readonly List<FrameSnap> _ring = new();
    private int _window = DefaultWindow;
    private ulong _frameIndex;
    private byte[]? _baseRdram;
    private readonly HashSet<int> _dirty = new();

    public int Window
    {
        get => _window;
        set => _window = Math.Clamp(value, 2, 32);
    }

    public ulong FrameIndex => _frameIndex;
    public int RingCount => _ring.Count;
    public ulong FullSnapshots { get; private set; }
    public ulong DeltaSnapshots { get; private set; }
    public double LastLoadMs { get; private set; }
    public double LastSaveMs { get; private set; }
    /// <summary>Phase 45: skip FB hash on delta for faster rollback ring (S3).</summary>
    public bool FastDelta { get; set; } = true;

    public void Reset()
    {
        _ring.Clear();
        _frameIndex = 0;
        _baseRdram = null;
        _dirty.Clear();
        FullSnapshots = DeltaSnapshots = 0;
        LastLoadMs = LastSaveMs = 0;
    }

    public void BeginSession(Ps2System system)
    {
        Reset();
        _baseRdram = system.Memory.GetRawData();
    }

    public void MarkRdramDirty(uint address, int length)
    {
        int start = (int)(address / PageSize);
        int end = (int)((address + (uint)Math.Max(0, length - 1)) / PageSize);
        for (int p = start; p <= end; p++)
            _dirty.Add(p);
    }

    /// <summary>Capture full compressed state at current frame.</summary>
    public FrameSnap SaveFull(Ps2System system)
    {
        var sw = Stopwatch.StartNew();
        byte[] blob = SaveState.Save(system, compress: true);
        sw.Stop();
        LastSaveMs = sw.Elapsed.TotalMilliseconds;
        FullSnapshots++;

        var snap = new FrameSnap
        {
            FrameIndex = _frameIndex,
            MasterCycles = system.MasterCycles,
            EePc = system.EE.PC,
            PadButtons = system.Pad.Buttons,
            FullState = blob,
            FbHash = RegressionFixtures.HashFramebuffer(system.Gs)
        };
        Push(snap);
        _frameIndex++;
        _dirty.Clear();
        return snap;
    }

    /// <summary>Capture delta (core + dirty RDRAM pages). Faster for rollback ring.</summary>
    public FrameSnap SaveDelta(Ps2System system)
    {
        var sw = Stopwatch.StartNew();
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(system.MasterCycles);
            w.Write(system.EE.PC);
            for (int i = 0; i < 32; i++)
            {
                var g = system.EE.GetGpr(i);
                w.Write(g.Lo);
                w.Write(g.Hi);
            }
            w.Write(system.EE.LO);
            w.Write(system.EE.HI);
            w.Write(system.EE.COP0_Status);
            w.Write(system.EE.COP0_Cause);
            w.Write(system.EE.COP0_EPC);
            w.Write(system.Iop.PC);
            for (int i = 0; i < 32; i++)
                w.Write(system.Iop.GetGpr(i));
            w.Write(system.Pad.Buttons);
            w.Write(system.Intc.Stat);
            w.Write(system.Intc.Mask);
        }

        var pages = new Dictionary<int, byte[]>();
        if (_baseRdram != null)
        {
            // Always snapshot a small working set around EE PC if no dirty marks
            if (_dirty.Count == 0)
            {
                int p = (int)((system.EE.PC & 0x1FFFFFFFu) / PageSize);
                CapturePage(system, p, pages);
                CapturePage(system, p + 1, pages);
            }
            else
            {
                foreach (int p in _dirty)
                    CapturePage(system, p, pages);
            }
        }

        sw.Stop();
        LastSaveMs = sw.Elapsed.TotalMilliseconds;
        DeltaSnapshots++;

        var snap = new FrameSnap
        {
            FrameIndex = _frameIndex,
            MasterCycles = system.MasterCycles,
            EePc = system.EE.PC,
            PadButtons = system.Pad.Buttons,
            CoreDelta = ms.ToArray(),
            DirtyPages = pages,
            FbHash = FastDelta ? 0 : RegressionFixtures.HashFramebuffer(system.Gs)
        };
        Push(snap);
        _frameIndex++;
        _dirty.Clear();
        return snap;
    }

    private void CapturePage(Ps2System system, int page, Dictionary<int, byte[]> pages)
    {
        if (page < 0) return;
        long off = (long)page * PageSize;
        if (off >= SystemMemory.RDRAM_SIZE) return;
        byte[] buf = new byte[PageSize];
        int len = (int)Math.Min(PageSize, SystemMemory.RDRAM_SIZE - off);
        for (int i = 0; i < len; i++)
            buf[i] = system.Memory.Read8((ulong)(off + i));
        pages[page] = buf;
    }

    private void Push(FrameSnap snap)
    {
        _ring.Add(snap);
        while (_ring.Count > _window)
            _ring.RemoveAt(0);
    }

    public bool TryGet(ulong frame, out FrameSnap snap)
    {
        for (int i = _ring.Count - 1; i >= 0; i--)
        {
            if (_ring[i].FrameIndex == frame)
            {
                snap = _ring[i];
                return true;
            }
        }
        snap = null!;
        return false;
    }

    public bool LoadFrame(Ps2System system, ulong frame)
    {
        if (!TryGet(frame, out var snap))
            return false;

        var sw = Stopwatch.StartNew();
        if (snap.FullState != null)
        {
            if (!SaveState.Load(system, snap.FullState))
                return false;
        }
        else if (snap.CoreDelta != null)
        {
            using var ms = new MemoryStream(snap.CoreDelta);
            using var r = new BinaryReader(ms);
            ulong cycles = r.ReadUInt64();
            system.Scheduler.SetMasterCycles(cycles);
            system.EE.PC = r.ReadUInt64();
            for (int i = 0; i < 32; i++)
            {
                ulong lo = r.ReadUInt64();
                ulong hi = r.ReadUInt64();
                system.EE.SetGpr(i, new EmotionEngine.Gpr128 { Lo = lo, Hi = hi });
            }
            system.EE.LO = r.ReadUInt64();
            system.EE.HI = r.ReadUInt64();
            system.EE.COP0_Status = r.ReadUInt32();
            system.EE.COP0_Cause = r.ReadUInt32();
            system.EE.COP0_EPC = r.ReadUInt64();
            system.Iop.PC = r.ReadUInt32();
            for (int i = 0; i < 32; i++)
                system.Iop.SetGpr(i, r.ReadUInt32());
            system.Pad.SetButtons(r.ReadUInt32());
            system.Intc.RestoreState(r.ReadUInt32(), r.ReadUInt32());

            if (snap.DirtyPages != null)
            {
                foreach (var kv in snap.DirtyPages)
                {
                    long off = (long)kv.Key * PageSize;
                    for (int i = 0; i < kv.Value.Length && off + i < SystemMemory.RDRAM_SIZE; i++)
                        system.Memory.Write8((ulong)(off + i), kv.Value[i]);
                }
            }
        }
        else return false;

        sw.Stop();
        LastLoadMs = sw.Elapsed.TotalMilliseconds;
        _frameIndex = frame;
        return true;
    }

    /// <summary>Fuzz helper: save full, mutate, load, compare cycles.</summary>
    public static bool FuzzRoundTrip(Ps2System system, ulong runCycles = 10_000)
    {
        system.RunFor(runCycles);
        ulong c0 = system.MasterCycles;
        ulong pc0 = system.EE.PC;
        byte[] st = SaveState.Save(system, compress: true);
        system.RunFor(runCycles);
        if (!SaveState.Load(system, st)) return false;
        return system.MasterCycles == c0 && system.EE.PC == pc0;
    }
}
