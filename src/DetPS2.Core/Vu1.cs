using System;
using System.Collections.Generic;
using System.IO;

namespace DetPS2.Core;

/// <summary>
/// VU1 (Phases 15/26): VIF FIFO, micro run via MSCAL, XGKICK → GIF Path1.
/// </summary>
public sealed class Vu1 : VectorUnit, ISchedulable
{
    private readonly Queue<uint> _incomingData = new();
    private uint _currentQuadwordWordCount;
    private Gif? _gif;
    private uint _xgkickAddr;
    private uint _xgkickQwc;

    public ulong XgKicks { get; private set; }
    public ulong MscalRuns { get; private set; }

    public Vu1(SystemMemory memory) : base(memory) { }

    public void SetGif(Gif gif) => _gif = gif;

    public override void Reset()
    {
        base.Reset();
        _incomingData.Clear();
        _currentQuadwordWordCount = 0;
        _xgkickAddr = 0;
        _xgkickQwc = 0;
        XgKicks = 0;
        MscalRuns = 0;
    }

    public override int Step(ulong maxCycles)
    {
        int processed = 0;
        while (_incomingData.Count > 0)
        {
            _ = _incomingData.Dequeue();
            _currentQuadwordWordCount++;
            processed++;
            if (_currentQuadwordWordCount >= 4)
                _currentQuadwordWordCount = 0;
        }

        if (_xgkickQwc > 0 && _gif != null)
        {
            _gif.ReceivePath1Data(_xgkickAddr, _xgkickQwc);
            XgKicks++;
            _xgkickQwc = 0;
            processed += 4;
        }

        return base.Step(maxCycles) + processed;
    }

    public override void WriteState(BinaryWriter w)
    {
        base.WriteState(w);
        w.Write(_incomingData.Count);
        foreach (var v in _incomingData) w.Write(v);
        w.Write(_currentQuadwordWordCount);
        w.Write(_xgkickAddr);
        w.Write(_xgkickQwc);
        w.Write(XgKicks);
        w.Write(MscalRuns);
    }

    public override void ReadState(BinaryReader r)
    {
        base.ReadState(r);
        _incomingData.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) _incomingData.Enqueue(r.ReadUInt32());
        _currentQuadwordWordCount = r.ReadUInt32();
        _xgkickAddr = r.ReadUInt32();
        _xgkickQwc = r.ReadUInt32();
        XgKicks = r.ReadUInt64();
        MscalRuns = r.ReadUInt64();
    }

    public void ReceiveFromVif1(uint data) => _incomingData.Enqueue(data);

    public uint GetNextIncomingData() =>
        _incomingData.Count > 0 ? _incomingData.Dequeue() : 0u;

    /// <summary>MSCAL: start microprogram at imm (VU instruction address).</summary>
    public void Mscal(uint imm)
    {
        MscalRuns++;
        // imm is typically address/8; convert to byte PC used by base Step
        uint entry = imm * 8;
        StartMicro(entry);
    }

    /// <summary>MSCNT: continue from current PC.</summary>
    public void Mscnt()
    {
        MscalRuns++;
        RunningMicro = true;
    }

    /// <summary>XGKICK: send EE/VU mem as GIF Path1 packet.</summary>
    public void XgKick(uint eeOrVuAddr, uint qwc)
    {
        _xgkickAddr = eeOrVuAddr;
        _xgkickQwc = qwc == 0 ? 1 : qwc;
    }
}
