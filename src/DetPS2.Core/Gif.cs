using System;

namespace DetPS2.Core;

/// <summary>
/// Graphics Interface (GIF) - Basic GIFtag parsing added.
/// Receives data from DMAC (PATH3) and will eventually forward to GS.
/// </summary>
public sealed class Gif
{
    private readonly Gs _gs;

    public Gif(Gs gs)
    {
        _gs = gs ?? throw new ArgumentNullException(nameof(gs));
    }

    public void Reset() { }

    /// <summary>
    /// Called by DMAC when PATH3 data is ready.
    /// This version does very basic GIFtag parsing.
    /// </summary>
    public void ReceivePath3Data(uint address, uint qwc)
    {
        Console.WriteLine($"[GIF] PATH3 data received: {qwc} quadwords from 0x{address:X8}");

        // Very basic loop over possible GIFtags (each tag is 1 quadword = 16 bytes)
        for (uint i = 0; i < qwc; i++)
        {
            uint tagAddr = address + (i * 16);

            // Read first 64 bits of GIFtag (simplified)
            uint nloop = _gs.Memory?.Read32(tagAddr) ?? 0; // placeholder
            uint eop = (nloop >> 15) & 1;

            Console.WriteLine($"[GIF] GIFtag: NLOOP={nloop & 0x7FFF}, EOP={eop}");

            if (eop != 0)
                break;
        }

        // TODO: Actually parse and send primitives to GS
        _gs.ReceiveGifData(address, qwc);
    }

    public void Step(ulong cycles) { }
}
