using System;

namespace DetPS2.Core;

/// <summary>
/// DMA Controller (DMAC) - Phase 2
/// Now notifies GIF when PATH3 (GIF channel) transfer completes.
/// </summary>
public sealed class Dmac
{
    private readonly SystemMemory _memory;
    private Gif _gif; // Set later via dependency injection or setter

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

    public Dmac()
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i] = new ChannelState();
    }

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

            if (ch.Mode == 0) // Normal
                DoNormalTransfer((Channel)i, ch);
            else if (ch.Mode == 1) // Chain
                DoChainTransfer((Channel)i, ch);

            if (ch.QWC == 0)
            {
                ch.Active = false;

                // Notify GIF if this was the GIF channel (PATH3)
                if ((Channel)i == Channel.GIF && _gif != null)
                {
                    _gif.ReceivePath3Data(ch.MADR, ch.QWC); // QWC should be 0 here, but we pass original if needed
                }

                Console.WriteLine($"[DMAC] { (Channel)i } transfer finished");
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

    public uint ReadRegister(uint address) => 0;
    public void WriteRegister(uint address, uint value) { }
}
