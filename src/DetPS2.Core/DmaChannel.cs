using System;

namespace DetPS2.Core;

/// <summary>
/// DmaChannel - Foundational class for a single DMA channel.
/// 
/// This provides a clean, reusable structure for individual DMA channels
/// (VIF0, VIF1, GIF, SIF, etc.).
/// 
/// Design goals:
/// - Clear state (MADR, QWC, CHCR, TADR)
/// - Easy to extend with mode-specific logic (normal vs chain)
/// - Foundation for accurate DMA behavior and notifications
/// </summary>
public sealed class DmaChannel
{
    public uint MADR { get; set; }
    public uint QWC { get; set; }
    public uint CHCR { get; set; }
    public uint TADR { get; set; }

    public bool Active { get; set; }
    public uint OriginalQWC { get; set; }

    public void Reset()
    {
        MADR = 0;
        QWC = 0;
        CHCR = 0;
        TADR = 0;
        Active = false;
        OriginalQWC = 0;
    }
}
