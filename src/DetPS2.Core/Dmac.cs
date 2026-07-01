using System;

namespace DetPS2.Core;

/// <summary>
/// DMA Controller (DMAC) - Improved implementation for Phase 2.
/// Supports Normal and basic Chain mode.
/// </summary>
public sealed class Dmac
{
    private readonly SystemMemory _memory;

    public Dmac(SystemMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public void Reset()
    {
        for (int i = 0; i < _channels.Length; i++)
        {
            _channels[i] = new ChannelState();
        }
    }

    public enum Channel
    {
        VIF0 = 0,
        VIF1 = 1,
        GIF = 2,
        IPU_FROM = 3,
        IPU_TO = 4,
        SIF0 = 5,
        SIF1 = 6,
        SIF2 = 7,
        SPR_FROM = 8,
        SPR_TO = 9
    }

    private class ChannelState
    {
        public uint MADR;
        public uint QWC;
        public uint CHCR;
        public uint TADR;
        public bool Active;
        public int Mode; // 0 = Normal, 1 = Chain, 2 = Interleave
    }

    private readonly ChannelState[] _channels = new ChannelState[10];

    public Dmac()
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i] = new ChannelState();
    }

    // Start a DMA transfer (called when CHCR STR bit is set)
    public void StartTransfer(Channel channel)
    {
        var ch = _channels[(int)channel];
        ch.Active = true;

        // Decode CHCR for mode
        ch.Mode = (int)((ch.CHCR >> 2) & 0x3); // bits 2-3 usually

        Console.WriteLine($"[DMAC] Starting {channel} (Mode={(DmaMode)ch.Mode})");
    }

    public enum DmaMode
    {
        Normal = 0,
        Chain = 1,
        Interleave = 2
    }

    public void Step(ulong cycles)
    {
        for (int i = 0; i < _channels.Length; i++)
        {
            var ch = _channels[i];
            if (!ch.Active || ch.QWC == 0) continue;

            if (ch.Mode == (int)DmaMode.Normal)
            {
                DoNormalTransfer((Channel)i, ch);
            }
            else if (ch.Mode == (int)DmaMode.Chain)
            {
                DoChainTransfer((Channel)i, ch);
            }

            if (ch.QWC == 0)
            {
                ch.Active = false;
                Console.WriteLine($"[DMAC] { (Channel)i } transfer finished");
            }
        }
    }

    private void DoNormalTransfer(Channel channel, ChannelState ch)
    {
        // Simple normal mode: transfer QWC quadwords from MADR
        uint qwToTransfer = Math.Min(ch.QWC, 16); // limit per step for simulation

        for (uint i = 0; i < qwToTransfer; i++)
        {
            // In real hardware this would be 128-bit transfers
            // For now we just advance pointers
            ch.MADR += 16;
            ch.QWC--;
        }

        // TODO: Actually read/write data using _memory
    }

    private void DoChainTransfer(Channel channel, ChannelState ch)
    {
        if (ch.QWC == 0 && ch.TADR != 0)
        {
            // Read DMA tag from TADR
            uint tagLow = _memory.Read32(ch.TADR);
            uint tagHigh = _memory.Read32(ch.TADR + 4);

            uint qwc = tagLow & 0xFFFF;
            uint tagId = (tagLow >> 28) & 0x7;
            uint addr = tagHigh & 0x7FFFFFFF;

            ch.QWC = qwc;
            ch.MADR = addr;

            Console.WriteLine($"[DMAC] Chain tag: ID={tagId}, QWC={qwc}, ADDR=0x{addr:X8}");

            // Advance TADR for next tag (simplified)
            ch.TADR += 16;

            // TODO: Handle different tag types (REFE, CNT, NEXT, REF, CALL, RET, etc.)
        }

        if (ch.QWC > 0)
        {
            DoNormalTransfer(channel, ch);
        }
    }

    // Register access stubs
    public uint ReadRegister(uint address) => 0;
    public void WriteRegister(uint address, uint value)
    {
        // TODO: Proper decoding of channel registers
    }
}
