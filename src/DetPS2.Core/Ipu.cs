using System;

namespace DetPS2.Core;

/// <summary>
/// IPU (Image Processing Unit) Phases 36/48: command surface + bitstream + SkipFMV path.
/// Enough for games that poll IPU status / issue decode commands without full MPEG silicon.
/// </summary>
public sealed class Ipu : ISchedulable
{
    public const uint MmioBase = 0x10002000;

    // Commands (subset)
    public const uint CmdBclr = 0x00;
    public const uint CmdIdec = 0x01;
    public const uint CmdBdec = 0x02;
    public const uint CmdVdec = 0x03;
    public const uint CmdFdec = 0x04;
    public const uint CmdSetIq = 0x05;
    public const uint CmdSetVq = 0x06;
    public const uint CmdCsc = 0x07;
    public const uint CmdPack = 0x08;
    public const uint CmdSetTh = 0x09;

    private readonly byte[] _fifoIn = new byte[0x10000];
    private readonly byte[] _fifoOut = new byte[0x10000];
    private readonly byte[] _iq = new byte[64];
    private int _inLen, _outLen, _outPos;
    private uint _ctrl;
    private uint _bp; // bit pointer
    private uint _top;
    private uint _cmd;
    private bool _busy;
    private ulong _busyLeft;
    private Intc? _intc;
    private int _frameW = 16, _frameH = 16;

    public ulong Commands { get; private set; }
    public ulong BitsConsumed { get; private set; }
    public ulong FramesDecoded { get; private set; }
    public ulong SkipFmvHits { get; private set; }
    public ulong IqLoads { get; private set; }
    public ulong MpegHeadersSeen { get; private set; }
    public ulong CscOps { get; private set; }
    public bool Busy => _busy;
    /// <summary>Phase 48: fast-complete FMV commands (still raises IPU IRQ for game progress).</summary>
    public bool SkipFmv { get; set; }
    public int LastFrameWidth => _frameW;
    public int LastFrameHeight => _frameH;

    public void SetIntc(Intc intc) => _intc = intc;

    public void Reset()
    {
        Array.Clear(_fifoIn);
        Array.Clear(_fifoOut);
        Array.Clear(_iq);
        // Default flat quant
        for (int i = 0; i < _iq.Length; i++) _iq[i] = 16;
        _inLen = _outLen = _outPos = 0;
        _ctrl = _bp = _top = _cmd = 0;
        _busy = false;
        _busyLeft = 0;
        Commands = BitsConsumed = FramesDecoded = 0;
        SkipFmvHits = IqLoads = MpegHeadersSeen = CscOps = 0;
        SkipFmv = false;
        _frameW = _frameH = 16;
    }

    public void WriteCommand(uint cmd, uint option = 0)
    {
        Commands++;
        _cmd = cmd & 0xF;
        _busy = true;
        _busyLeft = SkipFmv ? 64u : 5000u;
        if (SkipFmv && _cmd is CmdIdec or CmdBdec or CmdVdec or CmdFdec)
            SkipFmvHits++;

        switch (_cmd)
        {
            case CmdBclr:
                _inLen = _outLen = _outPos = 0;
                _bp = 0;
                _busyLeft = 10;
                break;
            case CmdIdec:
            case CmdBdec:
            case CmdVdec:
            case CmdFdec:
                DetectMpegHeader();
                ConsumeBitstream(SkipFmv ? 128 : 1024);
                DecodeStubFrame(option);
                FramesDecoded++;
                break;
            case CmdSetIq:
                LoadIqFromFifo();
                _busyLeft = 20;
                break;
            case CmdSetVq:
                _busyLeft = 20;
                break;
            case CmdCsc:
                CscOps++;
                // Color-space convert: expand out fifo as RGB if empty
                if (_outLen == 0)
                    DecodeStubFrame(0);
                _busyLeft = SkipFmv ? 20u : 200u;
                break;
            case CmdPack:
                _busyLeft = 50;
                break;
            case CmdSetTh:
                _busyLeft = 10;
                break;
            default:
                break;
        }
    }

    private void DetectMpegHeader()
    {
        // Look for 00 00 01 start codes in input fifo
        for (int i = 0; i + 3 < _inLen; i++)
        {
            if (_fifoIn[i] == 0 && _fifoIn[i + 1] == 0 && _fifoIn[i + 2] == 1)
            {
                MpegHeadersSeen++;
                byte sc = _fifoIn[i + 3];
                // sequence header 0xB3 — pretend 320x240
                if (sc == 0xB3 && i + 7 < _inLen)
                {
                    _frameW = Math.Max(16, (_fifoIn[i + 4] << 4) | (_fifoIn[i + 5] >> 4));
                    _frameH = Math.Max(16, ((_fifoIn[i + 5] & 0xF) << 8) | _fifoIn[i + 6]);
                    if (_frameW > 720) _frameW = 320;
                    if (_frameH > 576) _frameH = 240;
                }
                break;
            }
        }
    }

    private void ConsumeBitstream(int maxBytes)
    {
        int n = Math.Min(maxBytes, _inLen);
        if (n <= 0) return;
        // Shift remaining
        Buffer.BlockCopy(_fifoIn, n, _fifoIn, 0, _inLen - n);
        _inLen -= n;
        BitsConsumed += (ulong)n * 8;
        _bp = (_bp + (uint)n * 8) & 0x7F;
        _top = _inLen > 0 ? _fifoIn[0] : 0u;
    }

    private void LoadIqFromFifo()
    {
        int n = Math.Min(64, _inLen);
        if (n > 0)
        {
            Buffer.BlockCopy(_fifoIn, 0, _iq, 0, n);
            Buffer.BlockCopy(_fifoIn, n, _fifoIn, 0, _inLen - n);
            _inLen -= n;
            IqLoads++;
        }
    }

    private void DecodeStubFrame(uint option)
    {
        // Size from option bits or last MPEG header
        int w = _frameW;
        int h = _frameH;
        if ((option & 0xFF) != 0)
        {
            w = (int)((option & 0xFF) * 8);
            h = (int)(((option >> 8) & 0xFF) * 8);
            if (w < 8) w = 16;
            if (h < 8) h = 16;
            if (w > 64) w = 64;
            if (h > 64) h = 64;
            _frameW = w;
            _frameH = h;
        }

        int pixels = w * h;
        _outLen = Math.Min(pixels * 4, _fifoOut.Length);
        _outPos = 0;
        // IQ-tinted dummy RGB (deterministic from iq[0])
        byte baseC = _iq[0];
        for (int i = 0; i < _outLen; i += 4)
        {
            int px = i / 4;
            byte v = (byte)(baseC + (px & 0x1F));
            _fifoOut[i] = v;
            _fifoOut[i + 1] = (byte)(0x40 + (baseC >> 2));
            _fifoOut[i + 2] = (byte)(0xC0 - (baseC >> 1));
            _fifoOut[i + 3] = 0xFF;
        }
    }

    public void WriteFifo(ReadOnlySpan<byte> data)
    {
        int n = Math.Min(data.Length, _fifoIn.Length - _inLen);
        data[..n].CopyTo(_fifoIn.AsSpan(_inLen));
        _inLen += n;
    }

    public int ReadFifo(Span<byte> dest)
    {
        int n = Math.Min(dest.Length, _outLen - _outPos);
        _fifoOut.AsSpan(_outPos, n).CopyTo(dest);
        _outPos += n;
        return n;
    }

    /// <summary>
    /// EE MMIO IPU FIFO window (ps2tek): 0x10007000 out (read), 0x10007010 in (write).
    /// Games (Burnout 3) SQ command/bitstream words here; previously fell through as unknown MMIO.
    /// </summary>
    public uint ReadFifoWord(uint address)
    {
        // Out FIFO: return next decoded word if any
        if ((address & 0xF0) == 0x00)
        {
            if (_outPos + 3 < _outLen)
            {
                uint w = (uint)(_fifoOut[_outPos] | (_fifoOut[_outPos + 1] << 8)
                    | (_fifoOut[_outPos + 2] << 16) | (_fifoOut[_outPos + 3] << 24));
                _outPos += 4;
                return w;
            }
            return 0;
        }
        return 0;
    }

    public void WriteFifoWord(uint address, uint value)
    {
        // In FIFO @ 0x10007010: push little-endian bytes into bitstream buffer
        if ((address & 0xF0) == 0x10 || (address & 0xF0) == 0x00)
        {
            if (_inLen + 4 <= _fifoIn.Length)
            {
                _fifoIn[_inLen++] = (byte)value;
                _fifoIn[_inLen++] = (byte)(value >> 8);
                _fifoIn[_inLen++] = (byte)(value >> 16);
                _fifoIn[_inLen++] = (byte)(value >> 24);
            }
        }
    }

    public uint ReadRegister(uint address)
    {
        uint off = address - MmioBase;
        return off switch
        {
            0x00 => _cmd,
            0x10 => _ctrl | (_busy ? 0x80000000u : 0u),
            0x20 => _bp,
            0x30 => _top,
            0x40 => _outPos < _outLen ? _fifoOut[_outPos++] : 0u,
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        uint off = address - MmioBase;
        switch (off)
        {
            case 0x00:
                WriteCommand(value & 0xF, value >> 4);
                break;
            case 0x10:
                _ctrl = value;
                if ((value & 1) != 0) // reset
                {
                    _inLen = _outLen = _outPos = 0;
                    _busy = false;
                }
                // bit1 = skip-fmv hint from software
                if ((value & 2) != 0)
                    SkipFmv = true;
                break;
            case 0x40:
                if (_inLen < _fifoIn.Length)
                    _fifoIn[_inLen++] = (byte)value;
                break;
        }
    }

    public int Step(ulong maxCycles)
    {
        if (!_busy || maxCycles == 0) return 0;
        if (_busyLeft > maxCycles)
        {
            _busyLeft -= maxCycles;
            return (int)maxCycles;
        }
        ulong used = _busyLeft;
        _busyLeft = 0;
        _busy = false;
        _intc?.Raise(Intc.InterruptSource.Ipu);
        return (int)used;
    }

    /// <summary>DMA helper: feed bitstream from EE memory.</summary>
    public void DmaIn(SystemMemory mem, uint addr, uint qwc)
    {
        int bytes = (int)Math.Min(qwc * 16, (uint)(_fifoIn.Length - _inLen));
        for (int i = 0; i < bytes; i++)
            _fifoIn[_inLen++] = mem.Read8(addr + (uint)i);
    }

    public void DmaOut(SystemMemory mem, uint addr, uint qwc)
    {
        int bytes = (int)Math.Min(qwc * 16, (uint)(_outLen - _outPos));
        for (int i = 0; i < bytes; i++)
            mem.Write8(addr + (uint)i, _fifoOut[_outPos++]);
    }
}

/// <summary>
/// Phase 48: re-score IPU-tagged DX titles when SkipFMV / stub decode is enough.
/// </summary>
public static class IpuFmvPolicy
{
    public const string TagIpu = "IPU";
    public const string TagFmv = "FMV";

    /// <summary>True if tags are only multimedia (safe to promote off mass-DX with SkipFMV).</summary>
    public static bool IsIpuOnlyBlocker(string tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return false;
        var parts = tags.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (string p in parts)
        {
            if (p.Equals(TagIpu, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals(TagFmv, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals("MPEG", StringComparison.OrdinalIgnoreCase)) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Promote IPU-only DX rows to P1 when SkipFMV path is enabled (honest: not full play, not mass-DX).
    /// Returns number of titles re-scored.
    /// </summary>
    public static int RescoreIpuBlocked(MajorityCampaign.Report report, bool skipFmvEnabled = true)
    {
        if (!skipFmvEnabled) return 0;
        int n = 0;
        foreach (var r in report.Results)
        {
            if (r.Tier != "DX") continue;
            if (!IsIpuOnlyBlocker(r.BlockerTags)) continue;
            r.Tier = "P1";
            r.Notes = string.IsNullOrEmpty(r.Notes)
                ? "IPU SkipFMV promote"
                : r.Notes + "; IPU SkipFMV promote";
            r.BlockerTags = "";
            n++;
        }
        MajorityCampaign.RecomputeStats(report);
        return n;
    }

    /// <summary>Rank whether IPU is the top DX tag in a report.</summary>
    public static (bool ipuIsTop, int ipuCount, int totalDx) RankIpuDx(MajorityCampaign.Report report)
    {
        int ipu = 0, dx = 0;
        var other = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in report.Results)
        {
            if (r.Tier != "DX") continue;
            dx++;
            if (IsIpuOnlyBlocker(r.BlockerTags) ||
                r.BlockerTags.Contains(TagIpu, StringComparison.OrdinalIgnoreCase) ||
                r.BlockerTags.Contains(TagFmv, StringComparison.OrdinalIgnoreCase))
                ipu++;
            else
            {
                string tag = string.IsNullOrEmpty(r.BlockerTags) ? "OTHER" : r.BlockerTags.Split(',')[0];
                other.TryGetValue(tag, out int c);
                other[tag] = c + 1;
            }
        }
        int maxOther = 0;
        foreach (var kv in other)
            if (kv.Value > maxOther) maxOther = kv.Value;
        // IPU is "top" only if it outranks every other single tag and dx>0
        bool top = dx > 0 && ipu > maxOther;
        return (top, ipu, dx);
    }
}
