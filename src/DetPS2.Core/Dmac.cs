using System;

namespace DetPS2.Core;

/// <summary>
/// DMA Controller (DMAC) for the Emotion Engine.
/// This is the start of Phase 2.
/// 
/// The DMAC is critical because almost all large data transfers on the PS2
/// (to GS, VIF, IPU, etc.) go through DMA channels.
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
        // TODO: Reset all channel state
    }

    // ============================================================
    // DMA Channel Definitions
    // ============================================================

    public enum Channel
    {
        VIF0 = 0,
        VIF1 = 1,
        GIF  = 2,
        IPU_FROM = 3,
        IPU_TO   = 4,
        SIF0 = 5,   // EE -> IOP
        SIF1 = 6,   // IOP -> EE
        SIF2 = 7,
        SPR_FROM = 8,
        SPR_TO   = 9
    }

    // Basic channel state (we'll expand this)
    private class ChannelState
    {
        public uint MADR;   // Memory Address
        public uint QWC;    // QuadWord Count
        public uint CHCR;   // Channel Control
        public uint TADR;   // Tag Address (for chain mode)
        public bool Active;
    }

    private readonly ChannelState[] _channels = new ChannelState[10];

    public Dmac()
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i] = new ChannelState();
    }

    // ============================================================
    // Public API
    // ============================================================

    /// <summary>
    /// Starts a DMA transfer on the given channel.
    /// For now this is a stub that just marks the channel active.
    /// Real implementation will read CHCR, MADR, QWC, etc.
    /// </summary>
    public void StartTransfer(Channel channel)
    {
        var ch = _channels[(int)channel];
        ch.Active = true;

        // TODO: Read CHCR bits to determine mode (Normal / Chain / Interleave)
        // TODO: Perform actual data transfer based on mode

        Console.WriteLine($"[DMAC] Started transfer on channel {channel}");
    }

    /// <summary>
    /// Steps the DMAC forward by some number of cycles.
    /// This is where actual data movement should happen.
    /// </summary>
    public void Step(ulong cycles)
    {
        for (int i = 0; i < _channels.Length; i++)
        {
            var ch = _channels[i];
            if (!ch.Active) continue;

            // TODO: Perform actual DMA work here
            // - Read from MADR / TADR
            // - Write to destination
            // - Handle chain tags
            // - Update QWC, MADR, etc.

            // For now we just deactivate after one "step"
            ch.Active = false;
            Console.WriteLine($"[DMAC] Channel { (Channel)i } transfer completed (stub)");
        }
    }

    // ============================================================
    // Register Access (to be expanded)
    // ============================================================

    public uint ReadRegister(uint address)
    {
        // TODO: Implement proper register reads for MADR, QWC, CHCR, etc.
        return 0;
    }

    public void WriteRegister(uint address, uint value)
    {
        // TODO: Decode address and update the correct channel register
        // Example: CHCR, MADR, QWC, TADR, etc.
    }

    // ============================================================
    // Future: Chain Mode Support
    // ============================================================
    // We will add proper DMA tag parsing here later:
    // - REFE, CNT, NEXT, REF, CALL, RET, etc.
}
