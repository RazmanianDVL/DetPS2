using System;

namespace DetPS2.Core;

/// <summary>
/// SIO2 controller (Phase 31): pad/memcard command FIFO stub.
/// MMIO base 0x1000F200 is SIF on some maps — use 0x1000F600 for SIO2 alias.
/// </summary>
public sealed class Sio2
{
    public const uint MmioBase = 0x1000F600;

    private readonly byte[] _inFifo = new byte[256];
    private readonly byte[] _outFifo = new byte[256];
    private int _inLen, _outLen, _outPos;
    private PadInput? _pad;
    private PadInput[]? _multitapPads;
    private MemoryCard? _memcard;
    private int _port; // 0 or 1
    private int _slot; // multitap slot 0-3

    public ulong Transfers { get; private set; }
    public ulong Commands { get; private set; }
    public bool MultitapEnabled { get; set; }

    public void Attach(PadInput pad, MemoryCard? memcard = null)
    {
        _pad = pad;
        _memcard = memcard;
    }

    public void AttachMultitap(PadInput[] pads)
    {
        _multitapPads = pads;
        // Do not auto-enable: games opt in; tests set MultitapEnabled explicitly
    }

    public void Reset()
    {
        _inLen = _outLen = _outPos = 0;
        Transfers = Commands = 0;
        _port = _slot = 0;
        Array.Clear(_inFifo);
        Array.Clear(_outFifo);
    }

    public uint ReadRegister(uint address)
    {
        uint off = address - MmioBase;
        return off switch
        {
            0x00 => _outPos < _outLen ? _outFifo[_outPos++] : 0u, // DATA
            0x04 => (uint)((_outLen - _outPos) > 0 ? 0x2000 : 0) | 0x1000, // STAT ready
            0x08 => MultitapEnabled ? 1u : 0u,
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        uint off = address - MmioBase;
        if (off == 0x00) // DATA in
        {
            if (_inLen < _inFifo.Length)
                _inFifo[_inLen++] = (byte)value;
        }
        else if (off == 0x04) // CTRL / start transfer
        {
            if ((value & 1) != 0)
                ProcessTransfer();
        }
        else if (off == 0x08)
        {
            _port = (int)(value & 1);
            _slot = (int)((value >> 1) & 3);
        }
    }

    /// <summary>Run pad/memcard command in FIFO (high-level).</summary>
    public void ProcessTransfer()
    {
        Transfers++;
        if (_inLen == 0) return;
        Commands++;
        _outLen = 0;
        _outPos = 0;

        byte cmd = _inFifo[0];
        var pad = SelectPad();

        switch (cmd)
        {
            case 0x01: // pad poll
            case 0x42: // standard pad
                EmitPad(pad);
                break;
            case 0x81: // memcard
                EmitMemcard();
                break;
            case 0x21: // multitap configure
                _outFifo[_outLen++] = 0x00;
                _outFifo[_outLen++] = MultitapEnabled ? (byte)0x80 : (byte)0x00;
                break;
            default:
                _outFifo[_outLen++] = 0xFF;
                break;
        }
        _inLen = 0;
    }

    private PadInput? SelectPad()
    {
        if (MultitapEnabled && _multitapPads != null && _slot < _multitapPads.Length)
            return _multitapPads[_slot];
        return _pad;
    }

    private void EmitPad(PadInput? pad)
    {
        if (pad == null)
        {
            _outFifo[_outLen++] = 0xFF;
            return;
        }
        // High-nibble response header + digital/analog
        _outFifo[_outLen++] = 0x00; // hi-z
        _outFifo[_outLen++] = pad.AnalogMode ? (byte)0x79 : (byte)0x41;
        _outFifo[_outLen++] = 0x5A;
        _outFifo[_outLen++] = (byte)(~pad.Buttons & 0xFF);
        _outFifo[_outLen++] = (byte)((~pad.Buttons >> 8) & 0xFF);
        if (pad.AnalogMode)
        {
            _outFifo[_outLen++] = pad.Rx;
            _outFifo[_outLen++] = pad.Ry;
            _outFifo[_outLen++] = pad.Lx;
            _outFifo[_outLen++] = pad.Ly;
        }
    }

    private void EmitMemcard()
    {
        if (_memcard == null)
        {
            _outFifo[_outLen++] = 0xFF;
            return;
        }
        _outFifo[_outLen++] = 0x00;
        _outFifo[_outLen++] = 0x5A;
        _outFifo[_outLen++] = _memcard.Formatted ? (byte)0x5D : (byte)0x00;
        _outFifo[_outLen++] = (byte)_memcard.FileCount;
    }

    /// <summary>Test helper: queue command bytes and process.</summary>
    public byte[] Transact(ReadOnlySpan<byte> cmd)
    {
        _inLen = 0;
        foreach (byte b in cmd)
        {
            if (_inLen < _inFifo.Length) _inFifo[_inLen++] = b;
        }
        ProcessTransfer();
        byte[] r = new byte[_outLen];
        Buffer.BlockCopy(_outFifo, 0, r, 0, _outLen);
        return r;
    }
}

/// <summary>Four-port multitap holding pads.</summary>
public sealed class Multitap
{
    public PadInput[] Ports { get; } = new PadInput[4];

    public Multitap()
    {
        for (int i = 0; i < 4; i++)
            Ports[i] = new PadInput();
    }

    public void Reset()
    {
        // Reset pad state in place (do not replace instances — port 0 may share system Pad)
        for (int i = 0; i < Ports.Length; i++)
            Ports[i]?.Reset();
    }

    public PadInput this[int i] => Ports[i & 3];
}
