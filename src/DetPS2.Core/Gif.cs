using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Interface (GIF) - Phase 2 skeleton.
/// 
/// The GIF receives data from three paths:
/// - PATH1: From VU1 microprogram
/// - PATH2: From VIF1
/// - PATH3: From DMAC (most common for drawing lists)
/// 
/// This class will eventually feed commands to the GS.
/// </summary>
public sealed class Gif
{
    private readonly Dmac _dmac; // For PATH3

    public Gif(Dmac dmac)
    {
        _dmac = dmac ?? throw new ArgumentNullException(nameof(dmac));
    }

    public void Reset()
    {
        // TODO
    }

    /// <summary>
    /// Called when DMAC sends data via PATH3.
    /// This is where we will parse GIFtags and forward primitives to GS.
    /// </summary>
    public void ReceivePath3Data(uint address, uint qwc)
    {
        Console.WriteLine($"[GIF] Received PATH3 data: QWC={qwc} from 0x{address:X8}");

        // TODO:
        // - Read data from memory
        // - Parse GIFtags (NLOOP, EOP, PRIM, etc.)
        // - Handle PACKED, REGLIST, IMAGE formats
        // - Forward to GS
    }

    public void Step(ulong cycles)
    {
        // TODO: Process incoming data from all paths
    }
}
