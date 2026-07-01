using System;

namespace DetPS2.Core;

/// <summary>
/// DMA Controller (DMAC) - Phase 2/3
/// Supports major channels + chain mode.
/// Now has a basic but usable register interface (CHCR, MADR, QWC, TADR per channel).
/// This removes the need for reflection in tests and moves us toward accurate hardware modeling.
/// </summary>
public sealed class Dmac
{
    private readonly SystemMemory _memory;
    private Gif _gif;

    public Dmac(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void SetGif(Gif gif)
    {
        _gif = gif;
    }

    public void Reset()
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i] = new ChannelState();
    }

    public enum Channel
    {
        VIF0 = 0, VIF1 = 1, GIF = 2,
        IPU_FROM = 3, IPU_TO = 4,
        SIF0 = 5, SIF1 = 6, SIF2 = 7,
        SPR_FROM = 8, SPR_TO = 9
    }

    private class ChannelState
    {
        public uint MADR;
        public uint QWC;
        public uint CHCR;
        public uint TADR;
        public bool Active;
        public int Mode;
    }

    private readonly ChannelState[] _channels = new ChannelState[10];

    public void StartTransfer(Channel channel)
    {
        var ch = _channels[(int)channel];
        ch.Active = true;
        ch.Mode = (int)((ch.CHCR >> 2) & 0x3);

        Console.WriteLine($"[DMAC] Starting transfer on {channel} (Mode={ch.Mode})");
    }

    public void Step(ulong cycles)
    {
        for (int i = 0; i < _channels.Length; i++)
        {
            var ch = _channels[i];
            if (!ch.Active || ch.QWC == 0) continue;

            if (ch.Mode == 0)
                DoNormalTransfer((Channel)i, ch);
            else if (ch.Mode == 1)
                DoChainTransfer((Channel)i, ch);

            if (ch.QWC == 0)
            {
                ch.Active = false;

                if ((Channel)i == Channel.GIF && _gif != null)
                {
                    _gif.ReceivePath3Data(ch.MADR, ch.QWC);
                }

                Console.WriteLine($"[DMAC] {(Channel)i} transfer finished");
            }
        }
    }

    private void DoNormalTransfer(Channel channel, ChannelState ch)
    {
        uint qwToTransfer = Math.Min(ch.QWC, 8);
        ch.MADR += qwToTransfer * 16;
        ch.QWC -= qwToTransfer;
    }

    private void DoChainTransfer(Channel channel, ChannelState ch)
    {
        if (ch.QWC == 0 && ch.TADR != 0)
        {
            uint tagLow = _memory.Read32(ch.TADR);
            uint tagHigh = _memory.Read32(ch.TADR + 4);

            ch.QWC = tagLow & 0xFFFF;
            uint tagId = (tagLow >> 28) & 0x7;
            ch.MADR = tagHigh & 0x7FFFFFFF;

            Console.WriteLine($"[DMAC] Chain tag ID={tagId}, QWC={ch.QWC}");
            ch.TADR += 16;
        }

        if (ch.QWC > 0)
            DoNormalTransfer(channel, ch);
    }

    // ==================== Register Interface (new for Phase 2/3) ====================

    public uint ReadRegister(uint address)
    {
        // Simplified mapping for the most important per-channel registers
        int channel = GetChannelFromAddress(address);
        if (channel < 0) return 0;

        var ch = _channels[channel];

        uint reg = (address >> 4) & 0xF; // rough
        return reg switch
        {
            0x0 => ch.MADR,
            0x1 => ch.QWC,
            0x2 => ch.CHCR,
            0x3 => ch.TADR,
            _ => 0
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        int channel = GetChannelFromAddress(address);
        if (channel < 0) return;

        var ch = _channels[channel];

        uint reg = (address >> 4) & 0xF;

        switch (reg)
        {
            case 0x0: ch.MADR = value; break;
            case 0x1: ch.QWC = value; break;
            case 0x2:
                ch.CHCR = value;
                if ((value & 0x100) != 0) // start bit set
                {
                    StartTransfer((Channel)channel);
                }
                break;
            case 0x3: ch.TADR = value; break;
        }
    }

    private int GetChannelFromAddress(uint address)
    {
        // Very simplified address decoding for GIF channel (0x1000_8000 area is common)
        // Real hardware has specific base addresses per channel.
        if (address >= 0x10008000 && address < 0x10009000)
            return (int)Channel.GIF;

        // Fallback: treat low bits as channel index for test flexibility
        return (int)((address >> 8) & 0xF) % 10;
    }
}
